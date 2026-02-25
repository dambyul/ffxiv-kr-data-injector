using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Buffers.Binary;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIVInjector.Core;

public interface IPatcher
{
    event Action<string>? OnLog;
    void Inject(string gamePath, string csvResourcePath, GameLanguage language, HashSet<string>? fileFilter = null, Dictionary<string, string>? selectedFonts = null, IProgress<double>? progress = null, CancellationToken ct = default, bool useCompatibilityMode = false);
}

// Orchestrates the injection of CSV/EXD/TEX data into the game files.
public class CsvInjector : IPatcher
{
    public event Action<string>? OnLog;

    private class DataJson
    {
        [JsonPropertyName("third-party")]
        public List<ThirdPartyItem>? ThirdParty { get; set; }
    }

    private class ThirdPartyItem
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }
        
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("Key")]
        public List<int>? Keys { get; set; }

        [JsonPropertyName("Offset")]
        public List<int>? Offsets { get; set; }
    }

    public void Inject(string gamePath, string csvResourcePath, GameLanguage language, HashSet<string>? fileFilter = null, Dictionary<string, string>? selectedFonts = null, IProgress<double>? progress = null, CancellationToken ct = default, bool useCompatibilityMode = false)
    {
        if (!Directory.Exists(csvResourcePath))
        {
            OnLog?.Invoke($"Resource path not found: {csvResourcePath}");
            return;
        }

        if (GameProcess.IsRunning())
        {
            throw new InvalidOperationException("FFXIV is currently running. Please close the game before patching.");
        }

        ct.ThrowIfCancellationRequested();

        // Gather files from resource folder
        var allFiles = Directory.GetFiles(csvResourcePath, "*.*", SearchOption.AllDirectories)
                                .Where(f => {
                                    var ext = Path.GetExtension(f).ToLowerInvariant();
                                    return ext == ".csv" || ext == ".exd" || ext == ".fdt" || ext == ".tex";
                                })
                                .Where(f => {
                                    if (fileFilter == null) return true;
                                    var rel = Path.GetRelativePath(csvResourcePath, f).Replace('\\', '/').ToLowerInvariant();
                                    return fileFilter.Contains(rel);
                                })
                                .ToArray();

        OnLog?.Invoke($"Starting injection...");
        
        HashSet<string> fileExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<int>> keyExclusions = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<int>> offsetExclusions = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        if (useCompatibilityMode)
        {
            OnLog?.Invoke($"[Compatibility Mode] Active. Loading exclusion rules...");
            
            try 
            {
                if (File.Exists(PathConstants.DataJsonPath))
                {
                    var jsonContent = File.ReadAllText(PathConstants.DataJsonPath);
                    var data = JsonSerializer.Deserialize<DataJson>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (data?.ThirdParty != null)
                    {
                        foreach (var item in data.ThirdParty)
                        {
                            if (string.IsNullOrEmpty(item.Path)) continue;

                            // Normalize path to match relFile format (relative to rawexd, forward slashes)
                            string entry = item.Path.Replace('\\', '/').ToLowerInvariant();
                            if (entry.StartsWith("rawexd/"))
                            {
                                entry = entry.Substring(7);
                            }

                            if (string.Equals(item.Type, "Key", StringComparison.OrdinalIgnoreCase))
                            {
                                if (item.Keys != null && item.Keys.Any())
                                {
                                    if (!keyExclusions.ContainsKey(entry))
                                    {
                                        keyExclusions[entry] = new HashSet<int>();
                                    }
                                    foreach (var k in item.Keys)
                                    {
                                        keyExclusions[entry].Add(k);
                                    }
                                }
                            }
                            else if (string.Equals(item.Type, "Offset", StringComparison.OrdinalIgnoreCase))
                            {
                                if (item.Offsets != null && item.Offsets.Any())
                                {
                                    if (!offsetExclusions.ContainsKey(entry))
                                    {
                                        offsetExclusions[entry] = new HashSet<int>();
                                    }
                                    foreach (var off in item.Offsets)
                                    {
                                        offsetExclusions[entry].Add(off);
                                    }
                                }
                            }
                            else
                            {
                                // Default to File exclusion if Type is "File" or unspecified/unknown
                                fileExclusions.Add(entry);
                            }
                        }
                        OnLog?.Invoke($"  [Compatibility] Loaded additional rules from data.json ({data.ThirdParty.Count} items).");
                    }
                }
                else
                {
                    OnLog?.Invoke($"  [Info] data.json not found at {PathConstants.DataJsonPath}. Using only core compatibility rules.");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"  [Error] Failed to load additional compatibility rules: {ex.Message}");
            }
        }

        var indexBuffers = new Dictionary<string, byte[]>();
        var index2Buffers = new Dictionary<string, byte[]>();
        string sqpackPath = Path.Combine(gamePath, "sqpack");

        // Map existing assets to their repositories
        var repositoryMap = DiscoverRepositories(gamePath);
        OnLog?.Invoke($"  [Discovery] Mapped {repositoryMap.Count} assets.");

        string langSuffix = language switch {
            GameLanguage.English => "en",
            GameLanguage.Japanese => "ja",
            GameLanguage.French => "fr",
            GameLanguage.German => "de",
            _ => "en"
        };

        try
        {
            using (var lumina = new Lumina.GameData(sqpackPath))
            {
                var injectionQueue = new List<(string target, byte[] data, bool isTex, uint pHash, uint fHash, string domain, string repoFolder)>();

                OnLog?.Invoke($"  [Reconstruct] Preparing {allFiles.Length} assets for injection...");
                int reconstructedCount = 0;
                int skippedCount = 0;

                // Process resource files
                foreach (var file in allFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    var relFile = Path.GetRelativePath(csvResourcePath, file).Replace('\\', '/').ToLowerInvariant();

                    // Normalize: remove prefix to get internal path
                    string internalPath = relFile;
                    if (internalPath.StartsWith("rawexd/")) internalPath = internalPath.Substring(7);
                    
                    // Compatibility Mode Filter (File Level)
                    if (useCompatibilityMode && fileExclusions.Contains(internalPath))
                    {
                        OnLog?.Invoke($"    [Compatibility] Skipping file: {relFile}");
                        skippedCount++;
                        continue;
                    }
                    
                    if (extension == ".csv") 
                    {
                        // CSV sheets are merged with original .exd data
                        string sheetName = internalPath;
                        if (sheetName.StartsWith("exd/")) sheetName = sheetName.Substring(4);
                        if (sheetName.EndsWith(".csv")) sheetName = sheetName.Substring(0, sheetName.Length - 4);

                        try {
                            var patchData = CsvReader.Read(file);

                            // Compatibility: Filter rows by key
                            if (useCompatibilityMode && keyExclusions.TryGetValue(internalPath, out var keysToRemove))
                            {
                                int removedCount = 0;
                                foreach (var key in keysToRemove)
                                {
                                    if (patchData.Rows.Remove(key))
                                    {
                                        removedCount++;
                                    }
                                }
                                if (removedCount > 0)
                                {
                                    OnLog?.Invoke($"    [Compatibility] Removed {removedCount} rows from {relFile} (Key Filter)");
                                }
                            }
                            
                            var exhPath = $"exd/{sheetName}.exh";
                            var exhFile = lumina.GetFile(exhPath);
                            if (exhFile != null) {
                                var schema = ParseExhSchema(exhFile.Data);
                                foreach (var pageInfo in schema.PageTable) {
                                    // Match the EXD file for the requested language
                                    string[] probeSuffixes = { $"_{langSuffix}", "" };
                                    bool pageFound = false;
                                    foreach (var suffix in probeSuffixes)
                                    {
                                        string testFile = $"exd/{sheetName}_{pageInfo.StartId}{suffix}.exd";
                                        
                                        // Let Lumina find the file first - it's more reliable for expansion routing
                                        var exdFile = lumina.GetFile(testFile);
                                        if (exdFile != null)
                                        {
                                            string folder = Path.GetDirectoryName(testFile).Replace('\\', '/').ToLowerInvariant();
                                            uint ph = FFXIVHash.Calc(folder);
                                            uint fh = FFXIVHash.Calc(Path.GetFileName(testFile));
                                            ulong fullHash = (ulong)ph << 32 | fh;

                                            if (repositoryMap.TryGetValue(fullHash, out var info))
                                            {
                                                HashSet<int>? currentOffsetExclusions = null;
                                                offsetExclusions.TryGetValue(internalPath, out currentOffsetExclusions);

                                                byte[] injectionData = ExdReconstructor.Merge(exdFile.Data, patchData, schema, currentOffsetExclusions);
                                                injectionQueue.Add((testFile, injectionData, false, ph, fh, info.domain, info.folder));
                                                reconstructedCount++;
                                                pageFound = true;
                                                break;
                                            }
                                            else
                                            {
                                                // Fallback to ffxiv/0a0000 if discovery missed it (should not happen for basic EXDs)
                                                injectionQueue.Add((testFile, ExdReconstructor.Merge(exdFile.Data, patchData, schema), false, ph, fh, "0a0000", "ffxiv"));
                                                reconstructedCount++;
                                                pageFound = true;
                                                break;
                                            }
                                        }
                                    }
                                    if (!pageFound) {
                                        OnLog?.Invoke($"    [Debug] No page found for {sheetName}_{pageInfo.StartId} (EXD could not be found via Lumina)");
                                        skippedCount++;
                                    }
                                }
                            } else {
                                OnLog?.Invoke($"    [Warning] Could not find EXH definition for {sheetName}. Skipping.");
                                skippedCount++;
                            }
                        } catch (Exception ex) {
                            OnLog?.Invoke($"    [Error] Failed to reconstruct {sheetName}: {ex}");
                            skippedCount++;
                        }
                    }
                    else if (extension == ".fdt" || extension == ".tex" || extension == ".exd")
                    {
                        // Loose assets (Fonts, Pre-built EXDs) are injected directly
                        uint ph, fh;
                        string parent, filename;
                        
                        string normalizedAssetPath = internalPath;
                        if (normalizedAssetPath.StartsWith("font/")) normalizedAssetPath = "common/" + normalizedAssetPath;
                        
                        if (!normalizedAssetPath.Contains('/')) {
                            // If fonts/files are in the root of the resource folder, check if they are likely fonts
                            if (extension == ".tex" || extension == ".fdt") {
                                parent = "common/font";
                            } else {
                                parent = "";
                            }
                            filename = normalizedAssetPath;
                        } else {
                            parent = normalizedAssetPath.Substring(0, normalizedAssetPath.LastIndexOf('/'));
                            filename = normalizedAssetPath.Substring(normalizedAssetPath.LastIndexOf('/') + 1);
                        }

                        ph = FFXIVHash.Calc(parent);
                        fh = FFXIVHash.Calc(filename);
                        ulong fullHash = (ulong)ph << 32 | fh;
                        
                        if (repositoryMap.TryGetValue(fullHash, out var info))
                        {
                            byte[] assetData = File.ReadAllBytes(file);
                            if (extension == ".fdt")
                            {
                                try
                                {
                                    assetData = FontMerger.Repair(assetData);
                                    OnLog?.Invoke($"    [Font] Repaired KNHD search tree for {internalPath}");
                                }
                                catch (Exception ex)
                                {
                                    OnLog?.Invoke($"    [Warning] Failed to repair FDT {internalPath}: {ex.Message}");
                                }
                            }
                            
                            injectionQueue.Add((internalPath, assetData, extension == ".tex", ph, fh, info.domain, info.folder));
                            reconstructedCount++;
                        }
                        else
                        {
                            // Fallback for common/font if not discovered but explicitly in font folder
                            if (parent == "common/font") {
                                injectionQueue.Add((normalizedAssetPath, File.ReadAllBytes(file), extension == ".tex", ph, fh, "000000", "ffxiv")); // Assume ffxiv repo for common/font fallback
                                reconstructedCount++;
                            } else {
                                skippedCount++;
                                OnLog?.Invoke($"    [Warning] Could not find original asset for {internalPath} in any repository. Skipping.");
                            }
                        }
                    }
                }

                OnLog?.Invoke($"  [Reconstruct] Completed: {reconstructedCount} queued, {skippedCount} skipped.");

                lumina.Dispose();

                // Atomic Injection Stage
                using var writer = new SqPackWriter(gamePath, OnLog);
                int totalInjections = injectionQueue.Count;
                int currentInjection = 0;


                foreach (var iq in injectionQueue)
                {
                    ct.ThrowIfCancellationRequested();
                    try {
                        byte[] entry = iq.isTex ? SqPackBuilder.BuildTexture(iq.data) : SqPackBuilder.BuildBinary(iq.data);
                        long offset = writer.AppendRawBlock(iq.repoFolder, $"{iq.domain}.win32.dat0", entry);
                        
                        writer.UpdateIndexWithExpansion(indexBuffers, iq.repoFolder, iq.domain, iq.pHash, iq.fHash, offset, false);
                        if (iq.isTex) {
                            writer.UpdateIndexWithExpansion(index2Buffers, iq.repoFolder, iq.domain, iq.pHash, iq.fHash, offset, true);
                        }
                        
                        OnLog?.Invoke($"  [{ (iq.isTex ? "TEX" : "CSV") }] {iq.target} -> Success");
                    } catch (Exception ex) { 
                        OnLog?.Invoke($"  [Error] Failed to inject {iq.target}: {ex.Message}"); 
                    }
                    
                    currentInjection++;
                    progress?.Report((double)currentInjection / totalInjections * 100);
                }

                // Finalize index files
                foreach (var entry in indexBuffers) {
                    var parts = entry.Key.Split('/');
                    string folder = parts[0];
                    string domain = parts[1];
                    writer.WriteIndex(Path.Combine(sqpackPath, folder, $"{domain}.win32.index"), entry.Value);
                }
                foreach (var entry in index2Buffers) {
                    var parts = entry.Key.Split('/');
                    string folder = parts[0];
                    string domain = parts[1];
                    writer.WriteIndex(Path.Combine(sqpackPath, folder, $"{domain}.win32.index2"), entry.Value);
                }

                OnLog?.Invoke("Injection process completed successfully.");
            }
        }
        catch (Exception ex) { OnLog?.Invoke($"Fatal Error: {ex.Message}"); throw; }
    }

    private Lumina.Data.Category? GetCategory(Lumina.Data.Repository repo, byte catId)
    {
        return repo.Categories.Cast<object>().FirstOrDefault(o => {
            var p = o.GetType().GetProperty("CategoryId");
            return p != null && (byte)p.GetValue(o)! == catId;
        }) as Lumina.Data.Category;
    }

    // Maps asset hashes to their containing domain/folder by scanning .index files
    private Dictionary<ulong, (string domain, string folder)> DiscoverRepositories(string gamePath)
    {
        var map = new Dictionary<ulong, (string domain, string folder)>();
        var sqpackPath = Path.Combine(gamePath, "sqpack");
        if (!Directory.Exists(sqpackPath)) return map;

        // Scan repositories to prioritize newer expansion data.
        var folders = Directory.GetDirectories(sqpackPath).OrderByDescending(d => Path.GetFileName(d));
        
        foreach (var dir in folders)
        {
            string folderName = Path.GetFileName(dir);
            foreach (var indexFile in Directory.GetFiles(dir, "*.index"))
            {
                string domain = Path.GetFileNameWithoutExtension(indexFile);
                if (domain.EndsWith(".win32")) domain = domain.Substring(0, domain.Length - 6);
                
                try {
                    byte[] data = File.ReadAllBytes(indexFile);
                    int seg1Offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x408, 4));
                    int seg1Size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x40C, 4));
                    
                    for (int i = 0; i < seg1Size; i += 16) {
                        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(seg1Offset + i, 8));
                        // Always keep the LATEST expansion's hash mapping if multiples exist
                        if (!map.ContainsKey(hash)) map[hash] = (domain, folderName);
                    }
                } catch { }
            }
        }
        return map;
    }

    // Extract EXD page structure from EXH header
    private ExdReconstructor.SheetSchema ParseExhSchema(byte[] data)
    {
        var schema = new ExdReconstructor.SheetSchema();
        if (data.Length < 32 || Encoding.ASCII.GetString(data, 0, 4) != "EXHF") return schema;
        schema.FixedDataSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x06, 2));
        int colCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x08, 2));
        // EXHF structure: Header (32) -> Columns -> Pages -> Languages.
        
        // Offset mapping based on actual EXH column types
        for (int i = 0; i < colCount; i++) {
            int colPos = 0x20 + i * 4;
            if (colPos + 4 <= data.Length) {
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(colPos, 2));
                ushort offset = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(colPos + 2, 2));
                schema.Offsets.Add(offset);
                // Type 0 is String in FFXIV EXH
                schema.Types.Add(type == 0 ? "str" : "binary");
            }
        }
        
        // The Page Table follows the Column Table
        int ptPos = 0x20 + (colCount * 4);
        schema.PageCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x0A, 2));
        for (int i = 0; i < schema.PageCount; i++) {
            int entryPos = ptPos + i * 8;
            if (entryPos + 8 <= data.Length) {
                schema.PageTable.Add((BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entryPos, 4)), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entryPos + 4, 4))));
            }
        }
        return schema;
    }
}
