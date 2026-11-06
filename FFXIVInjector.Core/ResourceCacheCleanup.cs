using System.Text.RegularExpressions;

namespace FFXIVInjector.Core;

public static class ResourceCacheCleanup
{
    public static void CleanAbandoned(string root, Action<string>? log = null)
    {
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(path);
            bool temporary = Regex.IsMatch(name, @"^\.stage-[0-9a-f]{32}(\.zip)?$")
                || Regex.IsMatch(name, @"^[0-9a-f]{64}\.damaged-[0-9a-f]{32}$")
                || Regex.IsMatch(name, @"^[0-9a-f-]{36}\.zip(\.files)?$");
            // A different injector process may still be downloading a large package.
            if (temporary && File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                TryDelete(root, path, log);
        }
    }

    public static void KeepVersionMetadata(string root, string currentVersionDirectory, Action<string>? log = null)
    {
        string versions = Path.Combine(Path.GetFullPath(root), "versions");
        string current = Path.GetFullPath(currentVersionDirectory);
        if (!IsInside(versions, current) || !Directory.Exists(current)) throw new ArgumentException("Invalid version directory");
        string channel = Path.GetDirectoryName(current)!;
        if (Directory.GetParent(channel)?.FullName != versions) throw new ArgumentException("Invalid version channel");
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(channel))
                if (!string.Equals(Path.GetFullPath(directory), current, StringComparison.OrdinalIgnoreCase)
                    && Regex.IsMatch(Path.GetFileName(directory), @"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$"))
                    TryDelete(root, directory, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { log?.Invoke("버전 메타데이터 정리 보류: " + ex.Message); }
    }

    public static void CleanRuntime(string root, string gamePath, Action<string>? log = null)
    {
        // The INI is the authority: keep the mapping still used after success or rollback.
        string game = Path.GetFullPath(gamePath);
        string id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(game)));
        string runtime = Path.Combine(Path.GetFullPath(root), "runtime", id);
        if (!Directory.Exists(runtime)) return;
        try
        {
            string ini = Path.Combine(game, "resource-swap.ini");
            string? current = File.Exists(ini) ? File.ReadLines(ini).FirstOrDefault() : null;
            if (current != null && (!Path.IsPathFullyQualified(current) || !IsInside(runtime, current))) return;
            foreach (string path in Directory.EnumerateFiles(runtime))
            {
                if (current != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(path);
                if (Regex.IsMatch(name, @"^mappings-[0-9a-f]{32}\.tsv$") || name == "native-loader.log" || name.EndsWith(".tmp", StringComparison.Ordinal))
                    TryDelete(root, path, log);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { log?.Invoke("로더 임시 파일 정리 보류: " + ex.Message); }
    }

    private static bool IsInside(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string root, string path, Action<string>? log)
    {
        if (!IsInside(root, path)) throw new ArgumentException("Cleanup target is outside cache root");
        try
        {
            // Inspect every ancestor before traversing the tree, including the cache root.
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); parent != null; parent = parent.Parent)
            {
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return;
                if (string.Equals(parent.FullName, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)) break;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            if (Directory.Exists(path))
            {
                if (ContainsLink(new DirectoryInfo(path))) return;
                Directory.Delete(path, true);
            }
            else File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { log?.Invoke("이전 리소스 임시 파일 정리 보류: " + ex.Message); }
    }

    // Only content-addressed package directories belong to this cleanup.
    // Never traverse runtime, versions, staging directories or filesystem links.
    public static void KeepCurrent(string root, string currentPackage, Action<string>? log = null)
    {
        root = Path.GetFullPath(root);
        currentPackage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPackage));
        if (!string.Equals(Path.GetDirectoryName(currentPackage), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(currentPackage), "^[0-9a-f]{64}$")
            || !File.Exists(Path.Combine(currentPackage, "manifest.json")))
            throw new ArgumentException("Current package must be a prepared package directly inside its cache root.");
        try
        {
            foreach (string candidate in Directory.EnumerateDirectories(root))
            {
                string target = Path.GetFullPath(candidate);
                if (string.Equals(target, currentPackage, StringComparison.OrdinalIgnoreCase)
                    || !Regex.IsMatch(Path.GetFileName(target), "^[0-9a-f]{64}$")) continue;
                try
                {
                    if (ContainsLink(new DirectoryInfo(target))) continue;
                    Directory.Delete(target, true);
                    log?.Invoke("이전 리소스 캐시 삭제: " + Path.GetFileName(target));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { log?.Invoke("이전 리소스 캐시 정리 보류: " + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { log?.Invoke("리소스 캐시 정리 보류: " + ex.Message); }
    }

    private static bool ContainsLink(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (entry is DirectoryInfo child && ContainsLink(child)) return true;
        }
        return false;
    }
}
