using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace FFXIVInjector.Core;

public enum ResourceType { CSV, Font }
public enum ResourceStatus { Checking, Downloading, Extracting, Ready, Error }

public class S3ResourceManager
{
    private static string BaseUrl => Config.ResourceBaseUrl;
    
    public event Action<string>? OnLog;
    public event Action<ResourceStatus>? OnStatusChanged;
    public event Action<double>? OnProgressChanged;

    private class UpdateTask
    {
        public ResourceType Type { get; set; }
        public string? RemoteVersion { get; set; }
        public string? LocalInfoPath { get; set; }
        public string? ZipFileName { get; set; }
        public string? VersionFile { get; set; }
        public string? ExtractionPath { get; set; }
        public bool IsCsv { get; set; }
    }

    public async Task<bool> CheckAllResourcesAsync(string baseResourcePath, ResourceBranch branch, Func<Task<bool>>? onConfirmUpdate = null)
    {
        try
        {
            PathConstants.EnsureDirectories();
            var updateTasks = new List<UpdateTask>();
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            bool anyCheckFailed = false;

            // Check CSV
            try {
                var csvTask = await GetUpdateTaskAsync(client, branch, ResourceType.CSV);
                if (csvTask != null) updateTasks.Add(csvTask);
            } catch (Exception ex) {
                OnLog?.Invoke($"Failed to check CSV version: {ex.Message}");
                anyCheckFailed = true;
            }

            // Check Font
            try {
                var fontTask = await GetUpdateTaskAsync(client, branch, ResourceType.Font);
                if (fontTask != null) updateTasks.Add(fontTask);
            } catch (Exception ex) {
                OnLog?.Invoke($"Failed to check Font version: {ex.Message}");
                anyCheckFailed = true;
            }

            if (anyCheckFailed)
            {
                OnStatusChanged?.Invoke(ResourceStatus.Error);
                return true; 
            }

            if (updateTasks.Count == 0)
            {
                OnLog?.Invoke("All resources are up to date.");
                OnStatusChanged?.Invoke(ResourceStatus.Ready);
                return true;
            }

            // Prompt for update if callback provided
            {
                bool confirmed = await onConfirmUpdate();
                if (!confirmed) return false;
            }

            // Execute Updates
            foreach (var task in updateTasks)
            {
                await ExecuteUpdateAsync(client, task, branch);
            }

            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Major error during resource check: {ex.Message}");
            OnStatusChanged?.Invoke(ResourceStatus.Error);
            return true;
        }
    }

    private async Task<UpdateTask?> GetUpdateTaskAsync(HttpClient client, ResourceBranch branch, ResourceType type)
    {
        OnStatusChanged?.Invoke(ResourceStatus.Checking);
        OnLog?.Invoke($"Checking for {type} updates ({branch})...");
        
        string versionFile = type switch {
            ResourceType.CSV => branch == ResourceBranch.Stable ? "version.txt" : "dev-version.txt",
            ResourceType.Font  => branch == ResourceBranch.Stable ? "font-version.txt" : "dev-font-version.txt",
            _ => throw new ArgumentException()
        };

        string remoteVersionUrl = $"{BaseUrl}{versionFile}";
        string remoteRaw = (await client.GetStringAsync(remoteVersionUrl)).Trim();
        string remoteVersionStr = "0.0.0.0.0";

        if (!string.IsNullOrEmpty(remoteRaw))
        {
            string targetBranchStr = branch.ToString().ToLower();
            var lines = remoteRaw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(l => l.Trim())
                                 .Where(l => !string.IsNullOrEmpty(l));
            
            string searchContent = remoteRaw;
            foreach (var line in lines)
            {
                if (line.ToLower().Contains(targetBranchStr))
                {
                    searchContent = line;
                    break;
                }
            }

            var match = System.Text.RegularExpressions.Regex.Match(searchContent, @"[\d.]+");
            if (match.Success)
            {
                remoteVersionStr = match.Value.Trim('.');
            }
        }

        string infoFileName = type == ResourceType.CSV ? "version.txt" : "font-version.txt";
        string localInfoPath = Path.Combine(PathConstants.ResourcesRoot, infoFileName);
        string localVersionStr = "0.0.0.0.0";

        if (File.Exists(localInfoPath))
        {
            var contentLines = await File.ReadAllLinesAsync(localInfoPath);
            foreach (var line in contentLines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                
                if (trimmed.StartsWith("Version="))
                {
                    localVersionStr = trimmed.Substring(8).Trim();
                    break;
                }
                else if (char.IsDigit(trimmed[0]))
                {
                    localVersionStr = trimmed;
                    break;
                }
            }
        }

        var remoteVersion = new AppVersion(remoteVersionStr);
        var localVersion = new AppVersion(localVersionStr);

        OnLog?.Invoke($"  - Local: {localVersionStr} | Remote: {remoteVersionStr}");

        if (remoteVersion > localVersion)
        {
            OnLog?.Invoke($"New {type} version available: {remoteVersionStr} (Branch: {branch})");
            string extractionPath = type == ResourceType.CSV ? PathConstants.CsvPath : PathConstants.FontPath;

            return new UpdateTask
            {
                Type = type,
                RemoteVersion = remoteVersionStr,
                LocalInfoPath = localInfoPath,
                VersionFile = versionFile,
                ExtractionPath = extractionPath,
                ZipFileName = type switch {
                    ResourceType.CSV => branch == ResourceBranch.Stable ? "rawexd.zip" : "dev-rawexd.zip",
                    ResourceType.Font => branch == ResourceBranch.Stable ? "font.zip" : "dev-font.zip",
                    _ => throw new ArgumentException()
                },
                IsCsv = type == ResourceType.CSV
            };
        }
        
        return null;
    }

    private async Task ExecuteUpdateAsync(HttpClient client, UpdateTask task, ResourceBranch branch)
    {
        try
        {
            if (string.IsNullOrEmpty(task.ZipFileName) || string.IsNullOrEmpty(task.ExtractionPath)) return;

            string zipUrl = $"{BaseUrl}{task.ZipFileName}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            string zipPath = Path.Combine(PathConstants.ResourcesRoot, task.ZipFileName);

            OnStatusChanged?.Invoke(ResourceStatus.Downloading);
            OnLog?.Invoke($"Downloading {task.ZipFileName}...");
            
            await DownloadWithProgressAsync(client, zipUrl, zipPath);

            OnStatusChanged?.Invoke(ResourceStatus.Extracting);
            OnProgressChanged?.Invoke(-1); // Indeterminate
            OnLog?.Invoke($"Extracting {task.Type} resources...");
            if (!Directory.Exists(task.ExtractionPath)) Directory.CreateDirectory(task.ExtractionPath);
            
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, task.ExtractionPath, true));
            
            if (task.IsCsv)
            {
                string dataJsonUrl = $"{BaseUrl}data.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                try 
                {
                    byte[] jsonData = await client.GetByteArrayAsync(dataJsonUrl);
                    await File.WriteAllBytesAsync(PathConstants.DataJsonPath, jsonData);
                }
                catch {}
            }

            if (!string.IsNullOrEmpty(task.LocalInfoPath))
            {
                await File.WriteAllTextAsync(task.LocalInfoPath, task.RemoteVersion ?? "");
            }
            File.Delete(zipPath);
            OnProgressChanged?.Invoke(100);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Error during {task.Type} update: {ex.Message}");
            OnStatusChanged?.Invoke(ResourceStatus.Error);
        }
    }

    private async Task DownloadWithProgressAsync(HttpClient client, string url, string destinationPath)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        var totalRead = 0L;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
        {
            await fileStream.WriteAsync(buffer, 0, read);
            totalRead += read;

            if (totalBytes.HasValue)
            {
                var progress = (double)totalRead / totalBytes.Value * 100;
                OnProgressChanged?.Invoke(progress);
            }
        }
    }

    private struct AppVersion : IComparable<AppVersion>
    {
        public long[] Parts { get; }
        public AppVersion(string version)
        {
            Parts = version.Split('.')
                           .Select(s => long.TryParse(s, out var v) ? v : 0)
                           .ToArray();
        }
        public int CompareTo(AppVersion other)
        {
            int maxLen = Math.Max(Parts.Length, other.Parts.Length);
            for (int i = 0; i < maxLen; i++)
            {
                long v1 = i < Parts.Length ? Parts[i] : 0;
                long v2 = i < other.Parts.Length ? other.Parts[i] : 0;
                if (v1 != v2) return v1.CompareTo(v2);
            }
            return 0;
        }
        public static bool operator >(AppVersion v1, AppVersion v2) => v1.CompareTo(v2) > 0;
        public static bool operator <(AppVersion v1, AppVersion v2) => v1.CompareTo(v2) < 0;
    }
}
