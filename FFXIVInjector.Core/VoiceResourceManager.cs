using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFXIVInjector.Core;

/// <summary>Download and validate Korean SCD packages for German/French voice requests.</summary>
public sealed class VoiceResourceManager
{
    private readonly string cacheRoot;
    public VoiceResourceManager() => cacheRoot = Path.GetFullPath(PathConstants.VoicePackagePath);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly Regex VersionPattern = new(@"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$");
    private static readonly Regex DigestPattern = new("^[0-9a-f]{64}$");
    private static readonly Regex TargetPattern = new(@"^(cut/(?:ffxiv|ex[0-9]+)/sound/|sound/(?:voice/|battle/mon/))[a-z0-9_/]+_(de|fr)\.scd$");
    public event Action<string>? OnLog;
    public event Action<double>? OnProgress;
    public event Action<string>? OnStatus;
    public Func<string, string, Task<bool>>? OnClientVersionMismatch { get; set; }
    public sealed class Release
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = "";
        public string Channel { get; set; } = "";
        public string TextVersion { get; set; } = "";
        public string GlobalVersion { get; set; } = "";
        public string KoreanVersion { get; set; } = "";
        public string GlobalExecutableSha256 { get; set; } = "";
        public string ArchiveKey { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Bytes { get; set; }
        public long UnpackedBytes { get; set; }
        public int Mappings { get; set; }
        public int UniqueFiles { get; set; }
    }
    public sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = "";
        public string GlobalVersion { get; set; } = "";
        public string KoreanVersion { get; set; } = "";
        public string GlobalExecutableSha256 { get; set; } = "";
        public Entry[] Entries { get; set; } = [];
    }
    public sealed class Entry
    {
        public string Target { get; set; } = "";
        public string Source { get; set; } = "";
        public string Status { get; set; } = "";
        public string CacheFile { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
    private sealed record Installation(string GamePath, string DllSha256, string IniSha256);
    private static string StatePath(string game) => Path.Combine(game, "resource-swap.installation.json");
    public static bool HasInstallation(string gamePath) => File.Exists(StatePath(GameDirectory(gamePath)));
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty metadata");
    private static string GameDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(Path.Combine(path, "game", "ffxiv_dx11.exe"))) path = Path.Combine(path, "game");
        if (!File.Exists(Path.Combine(path, "ffxiv_dx11.exe"))) throw new FileNotFoundException("Global executable not found");
        return path;
    }
    private static void RequireClosed()
    {
        if (GameProcess.IsRunning()) throw new InvalidOperationException("게임을 종료한 뒤 리소스를 적용하거나 해제해 주세요.");
    }
    public static void ValidateRelease(Release release, string version, string channel, bool allowClientVersionMismatch = false)
    {
        if (release.SchemaVersion != 1 || release.Kind != "voices-de-fr" || (!allowClientVersionMismatch && release.GlobalVersion != version) ||
            release.Channel != channel || !VersionPattern.IsMatch(version) || !VersionPattern.IsMatch(release.KoreanVersion) ||
            !DigestPattern.IsMatch(release.Sha256) || !DigestPattern.IsMatch(release.GlobalExecutableSha256) ||
            release.Bytes is <= 0 or > 17179869184L || release.UnpackedBytes is <= 0 or > 34359738368L ||
            release.Mappings is <= 0 or > 100000 || release.UniqueFiles is <= 0 or > 50000)
            throw new InvalidDataException("Invalid or incompatible voice release");
        if (release.ArchiveKey != "voices.zip") throw new InvalidDataException("Invalid resource archive key");
    }
    public static Dictionary<string, string> ValidateManifest(Manifest manifest, Release release)
    {
        if (manifest.SchemaVersion != 1 || manifest.Kind != release.Kind || manifest.GlobalVersion != release.GlobalVersion ||
            manifest.KoreanVersion != release.KoreanVersion || manifest.GlobalExecutableSha256 != release.GlobalExecutableSha256 ||
            manifest.Entries.Length != release.Mappings) throw new InvalidDataException("Manifest/release mismatch");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            var match = TargetPattern.Match(entry.Target);
            string source = Regex.Replace(entry.Target, @"_(de|fr)\.scd$", "_ko.scd");
            if (!match.Success || entry.Source != source || entry.Status != "prepared" || !targets.Add(entry.Target) ||
                !DigestPattern.IsMatch(entry.Sha256) || entry.CacheFile != $"assets/{entry.Sha256}.scd")
                throw new InvalidDataException("Invalid voice mapping");
            files[entry.CacheFile] = entry.Sha256;
        }
        if (files.Count != release.UniqueFiles) throw new InvalidDataException("Voice file count mismatch");
        return files;
    }
    private static void VerifySound(string file)
    {
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[32]; stream.ReadExactly(header);
        if (!header[..8].SequenceEqual("SEDBSSCF"u8)) throw new InvalidDataException("Invalid SCD header");
    }
    private static Manifest VerifyPackage(string package, Release release, CancellationToken ct)
    {
        var manifest = Read<Manifest>(Path.Combine(package, "manifest.json"));
        ct.ThrowIfCancellationRequested();
        ValidateManifest(manifest, release);
        // Extraction checked the payload once. Reuse never opens individual assets.
        return manifest;
    }
    public static void ExtractPackage(string archive, string destination, Release release, CancellationToken ct = default)
    {
        if (Directory.Exists(destination)) throw new IOException("Extraction destination already exists");
        if (new FileInfo(archive).Length != release.Bytes)
            throw new InvalidDataException("Archive size mismatch");
        using var zip = ZipFile.OpenRead(archive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in zip.Entries)
            if (!names.Add(item.FullName)) throw new InvalidDataException("Duplicate ZIP entry");
        var metadata = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Manifest missing");
        if (metadata.Length > 32 * 1024 * 1024) throw new InvalidDataException("Oversized manifest");
        Manifest manifest;
        using (var stream = metadata.Open()) manifest = JsonSerializer.Deserialize<Manifest>(stream, Json) ?? throw new InvalidDataException("Empty manifest");
        var files = ValidateManifest(manifest, release);
        if (zip.Entries.Count != files.Count + 1) throw new InvalidDataException("Unexpected ZIP contents");
        long total = 0;
        foreach (var item in zip.Entries.Where(e => e.FullName != "manifest.json"))
        {
            if (!files.ContainsKey(item.FullName) || item.Length is < 32 or > 67108864)
                throw new InvalidDataException("Unexpected or oversized SCD");
            total += item.Length;
        }
        if (total != release.UnpackedBytes) throw new InvalidDataException("ZIP size mismatch");
        Directory.CreateDirectory(Path.Combine(destination, "assets"));
        foreach (var pair in files)
        {
            ct.ThrowIfCancellationRequested();
            string file = Path.Combine(destination, pair.Key);
            zip.GetEntry(pair.Key)!.ExtractToFile(file);
            VerifySound(file);
        }
        File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
    }
    public async Task<string> EnsurePackageAsync(string gamePath, ResourceBranch branch, CancellationToken ct = default)
    {
        string game = GameDirectory(gamePath);
        string version = File.ReadAllText(Path.Combine(game, "ffxivgame.ver")).Trim();
        if (!VersionPattern.IsMatch(version)) throw new InvalidDataException("Invalid global client version");
        string channel = branch == ResourceBranch.Stable ? "stable" : "staging";
        using var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVInjector/1.1");
        string baseUrl = Config.ResourceBaseUrl.TrimEnd('/') + "/";
        OnStatus?.Invoke("한국어 음성 버전 확인 중...");
        string versionFile = branch == ResourceBranch.Stable ? "version.txt" : "dev-version.txt";
        string remoteVersion = S3ResourceManager.ParseVersionText(
            await client.GetStringAsync($"{baseUrl}{versionFile}?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", ct), branch);
        if (!Regex.IsMatch(remoteVersion, @"^\d+(\.\d+){4,6}$")) throw new InvalidDataException("Invalid shared text/resource version");
        string versionRoot = Path.Combine(cacheRoot, "versions", channel, version);
        string localVersion = Path.Combine(versionRoot, "version.txt"), localRelease = Path.Combine(versionRoot, "release.json");
        string json = "";
        if (File.Exists(localVersion) && File.ReadAllText(localVersion).Trim() == remoteVersion && File.Exists(localRelease))
        {
            json = File.ReadAllText(localRelease);
            try { if (JsonSerializer.Deserialize<Release>(json, Json)?.TextVersion != remoteVersion) json = ""; }
            catch (JsonException) { json = ""; }
        }
        if (json.Length == 0)
        {
            using var metadata = JsonDocument.Parse(await client.GetStringAsync($"{baseUrl}data.json?t={remoteVersion}", ct));
            if (metadata.RootElement.TryGetProperty("voiceResources", out var voices)) json = voices.GetRawText();
            else
            {
                // Migration: existing releases have a separate descriptor without a shared version.
                string descriptorName = branch == ResourceBranch.Stable ? "voice-release.json" : "dev-voice-release.json";
                using var response = await client.GetAsync($"{baseUrl}{descriptorName}?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", ct);
                if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("한국어 음성 패키지가 아직 배포되지 않았습니다.");
                response.EnsureSuccessStatusCode();
                var legacy = JsonSerializer.Deserialize<Release>(await response.Content.ReadAsStringAsync(ct), Json)
                    ?? throw new InvalidDataException("Empty voice release");
                if (string.IsNullOrEmpty(legacy.TextVersion)) legacy.TextVersion = remoteVersion;
                json = JsonSerializer.Serialize(legacy, Json);
            }
        }
        if (json.Length > 65536) throw new InvalidDataException("Oversized release descriptor");
        var release = JsonSerializer.Deserialize<Release>(json, Json) ?? throw new InvalidDataException("Empty release");
        ValidateSharedVersion(release, remoteVersion);
        if (release.GlobalVersion != version)
        {
            bool proceed = OnClientVersionMismatch != null && await OnClientVersionMismatch(release.GlobalVersion, version);
            if (!proceed) throw new OperationCanceledException(ct);
        }
        ValidateRelease(release, version, channel, allowClientVersionMismatch: true);
        Directory.CreateDirectory(cacheRoot);
        string package = Path.Combine(cacheRoot, release.Sha256);
        if (Directory.Exists(package))
        {
            try { VerifyPackage(package, release, ct); SaveVersion(); OnLog?.Invoke("한국어 음성 최신 버전: 다운로드 없이 캐시 재사용"); return package; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            { OnLog?.Invoke("손상된 음성 캐시를 다시 다운로드합니다."); }
        }
        string stage = Path.Combine(cacheRoot, ".stage-" + Guid.NewGuid().ToString("N"));
        string archive = stage + ".zip";
        try
        {
            OnStatus?.Invoke("음성 리소스 다운로드 중...");
            using (var download = await client.GetAsync(baseUrl + release.ArchiveKey + "?v=" + release.Sha256, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                download.EnsureSuccessStatusCode();
                using var input = await download.Content.ReadAsStreamAsync(ct);
                using var output = File.Create(archive);
                byte[] buffer = new byte[131072];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    count += read;
                    if (count > release.Bytes) throw new InvalidDataException("Download exceeds expected size");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    OnProgress?.Invoke(100.0 * count / release.Bytes);
                }
                if (count != release.Bytes) throw new InvalidDataException("Incomplete resource download");
            }
            OnStatus?.Invoke("음성 리소스 압축 해제 중...");
            ExtractPackage(archive, stage, release, ct);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(package))
            {
                string damaged = package + ".damaged-" + Guid.NewGuid().ToString("N");
                Directory.Move(package, damaged);
                try { Directory.Move(stage, package); }
                catch { Directory.Move(damaged, package); throw; }
                Directory.Delete(damaged, true);
            }
            else Directory.Move(stage, package);
            SaveVersion();
            return package;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        void SaveVersion()
        {
            Directory.CreateDirectory(versionRoot);
            AtomicText(localRelease, json);
            AtomicText(localVersion, remoteVersion + "\n");
            ResourceCacheCleanup.KeepVersionMetadata(cacheRoot, versionRoot, OnLog);
        }
    }
    public static void ValidateSharedVersion(Release release, string sharedVersion)
    {
        if (!Regex.IsMatch(sharedVersion, @"^\d+(\.\d+){4,6}$") || release.TextVersion != sharedVersion)
            throw new InvalidDataException("Text/voice release versions differ; retry after publication completes");
    }
    public static string[] GetValidatedMappings(string package)
    {
        var manifest = Read<Manifest>(Path.Combine(package, "manifest.json"));
        var release = new Release { Kind = "voices-de-fr", GlobalVersion = manifest.GlobalVersion, KoreanVersion = manifest.KoreanVersion,
            GlobalExecutableSha256 = manifest.GlobalExecutableSha256, Mappings = manifest.Entries.Length,
            UniqueFiles = manifest.Entries.Select(e => e.CacheFile).Distinct().Count() };
        ValidateManifest(manifest, release);
        // Accept existing DE/FR packages without downloading again; install French targets only.
        var selected = manifest.Entries.Where(e => e.Target.EndsWith("_fr.scd", StringComparison.Ordinal)).ToArray();
        return selected.Select(e => {
            string path = Path.GetFullPath(Path.Combine(package, e.CacheFile));
            if (path.Length >= 260 || path.Any(char.IsControl)) throw new InvalidDataException("Voice path too long/invalid");
            return $"{e.Target}\t{path}\t{e.Sha256}";
        }).ToArray();
    }
    private static void AtomicText(string path, string text)
    {
        string temp = path + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
