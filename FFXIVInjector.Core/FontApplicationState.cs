using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FFXIVInjector.Core;

public static class FontApplicationState
{
    private sealed class State
    {
        public string GamePath { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string StatePath(string gamePath)
    {
        return Path.Combine(PathConstants.ResourcesRoot, "font-applied-" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gamePath)))) + ".json");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static Dictionary<string, string>? CurrentFiles(IEnumerable<string> relativeFiles)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in relativeFiles.Where(p => p.StartsWith("font/", StringComparison.OrdinalIgnoreCase)))
        {
            string path = Path.GetFullPath(Path.Combine(PathConstants.ResourcesRoot, relative));
            if (!File.Exists(path)) return null;
            files[relative.Replace('\\', '/')] = Hash(path);
        }
        return files.Count == 0 ? null : files;
    }

    public static bool IsAlreadyApplied(string gamePath, IEnumerable<string> relativeFiles)
    {
        var files = CurrentFiles(relativeFiles);
        if (files == null || !File.Exists(StatePath(gamePath))) return false;
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath(gamePath)));
            return state != null && state.GamePath == Path.GetFullPath(gamePath) &&
                state.Files.Count == files.Count && state.Files.All(pair => files.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    public static void MarkApplied(string gamePath, IEnumerable<string> relativeFiles)
    {
        var files = CurrentFiles(relativeFiles);
        if (files == null) return;
        Directory.CreateDirectory(PathConstants.ResourcesRoot);
        var state = new State { GamePath = Path.GetFullPath(gamePath), Files = files };
        File.WriteAllText(StatePath(gamePath), JsonSerializer.Serialize(state));
    }

    public static void Clear(string gamePath) => File.Delete(StatePath(gamePath));
}
