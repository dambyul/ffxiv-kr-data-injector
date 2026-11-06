using FFXIVInjector.Core;

internal static class ResourceCacheCleanupTests
{
    public static void Run(string root)
    {
        string cache = Path.Combine(root, "cleanup-cache");
        string current = Package(cache, 'a'), previous = Package(cache, 'b');
        string versions = Path.Combine(cache, "versions"), runtime = Path.Combine(cache, "runtime");
        Directory.CreateDirectory(versions); Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(versions, "release.json"), "keep");
        string stage = Path.Combine(cache, ".stage-test"); Directory.CreateDirectory(stage);
        string unrelated = Path.Combine(cache, "user-files"); Directory.CreateDirectory(unrelated);
        var logs = new List<string>();
        ResourceCacheCleanup.KeepCurrent(cache, current, logs.Add);
        if (Directory.Exists(previous) || !File.Exists(Path.Combine(current, "assets", "test.scd"))
            || !File.Exists(Path.Combine(versions, "release.json")) || !Directory.Exists(runtime)
            || !Directory.Exists(stage) || !Directory.Exists(unrelated) || logs.Count != 1)
            throw new Exception("Cleanup must remove only old package directories.");
        ResourceCacheCleanup.KeepCurrent(cache, current);
        string outside = Package(Path.Combine(root, "outside-cache"), 'c');
        previous = Package(cache, 'd');
        try { ResourceCacheCleanup.KeepCurrent(cache, outside); throw new Exception("Outside current package accepted"); }
        catch (ArgumentException) { }
        if (!Directory.Exists(previous) || !Directory.Exists(outside)) throw new Exception("Invalid cleanup modified files");
        string incomplete = Path.Combine(cache, new string('e', 64)); Directory.CreateDirectory(incomplete);
        try { ResourceCacheCleanup.KeepCurrent(cache, incomplete); throw new Exception("Incomplete package accepted"); }
        catch (ArgumentException) { }
        if (!Directory.Exists(previous)) throw new Exception("Incomplete package must not trigger cleanup");
        string channel = Path.Combine(cache, "versions", "stable");
        string currentVersion = Path.Combine(channel, "2026.11.06.0000.0000");
        string oldVersion = Path.Combine(channel, "2026.10.06.0000.0000");
        string stagingVersion = Path.Combine(cache, "versions", "staging", "2026.10.06.0000.0000");
        Directory.CreateDirectory(currentVersion); Directory.CreateDirectory(oldVersion); Directory.CreateDirectory(stagingVersion);
        File.WriteAllText(Path.Combine(currentVersion, "version.txt"), "current");
        ResourceCacheCleanup.KeepVersionMetadata(cache, currentVersion);
        if (Directory.Exists(oldVersion) || !Directory.Exists(currentVersion) || !Directory.Exists(stagingVersion)) throw new Exception("Version cleanup must preserve current and other channel metadata");
        string abandoned = Path.Combine(cache, ".stage-" + new string('a',32) + ".zip");
        string recent = Path.Combine(cache, ".stage-" + new string('b',32) + ".zip");
        File.WriteAllText(abandoned, "old"); File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(recent, "active");
        ResourceCacheCleanup.CleanAbandoned(cache);
        if (File.Exists(abandoned) || !File.Exists(recent)) throw new Exception("Staging cleanup must preserve recent downloads");
        string game = Path.Combine(root,"runtime-game"); Directory.CreateDirectory(game);
        string gameId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(game))));
        string gameRuntime = Path.Combine(runtime,gameId); Directory.CreateDirectory(gameRuntime);
        string currentMap = Path.Combine(gameRuntime,"mappings-"+new string('a',32)+".tsv");
        string oldMap = Path.Combine(gameRuntime,"mappings-"+new string('b',32)+".tsv");
        string nativeLog = Path.Combine(gameRuntime,"native-loader.log");
        File.WriteAllText(currentMap,"active");File.WriteAllText(oldMap,"stale");File.WriteAllText(nativeLog,"log");
        File.WriteAllText(Path.Combine(game,"resource-swap.ini"),currentMap+"\nexe-hash\n");
        using (var locked = new FileStream(nativeLog,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            ResourceCacheCleanup.CleanRuntime(cache,game);
        if (!File.Exists(currentMap) || File.Exists(oldMap) || !File.Exists(nativeLog)) throw new Exception("Runtime cleanup must keep active mapping and defer locked log");
        ResourceCacheCleanup.CleanRuntime(cache,game);
        if (File.Exists(nativeLog) || !File.Exists(currentMap)) throw new Exception("Deferred log must be removed on retry");
        Console.WriteLine("PASS: old cache removal, current assets/metadata/runtime preservation, repeat cleanup, outside/incomplete package rejection.");
        Console.WriteLine("PASS: old version metadata, abandoned staging, active mapping preservation, locked-log retry.");
    }

    private static string Package(string cache, char id)
    {
        string path = Path.Combine(cache, new string(id, 64));
        Directory.CreateDirectory(Path.Combine(path, "assets"));
        File.WriteAllText(Path.Combine(path, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(path, "assets", "test.scd"), "payload");
        return path;
    }
}
