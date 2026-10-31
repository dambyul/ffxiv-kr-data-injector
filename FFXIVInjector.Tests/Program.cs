using System.Security.Cryptography;
using System.Text.Json;
using FFXIVInjector.Core;

// Run with: dotnet run --project FFXIVInjector.Tests
string root = Path.Combine(Path.GetTempPath(), "injector-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    const string version = "2026.10.06.0000.0000";
    const string missing = "0a0000.win32.dat1";
    string package = Path.Combine(root, "package");
    string game = Path.Combine(root, "game");
    string destination = Path.Combine(game, "sqpack", "ffxiv");
    Directory.CreateDirectory(package);
    Directory.CreateDirectory(destination);
    File.WriteAllText(Path.Combine(game, "ffxivgame.ver"), version);
    byte[] pristine = Enumerable.Repeat((byte)42, 1024).ToArray();
    byte[] previous = Enumerable.Repeat((byte)17, 1024).ToArray();
    foreach (string name in OriginalResourceManager.FileNames)
    {
        File.WriteAllBytes(Path.Combine(package, name), pristine);
        if (name != missing) File.WriteAllBytes(Path.Combine(destination, name), previous);
    }
    File.WriteAllText(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(new OriginalResourceManager.Manifest
    {
        SchemaVersion = 1,
        GlobalVersion = version,
        Files = OriginalResourceManager.FileNames.Select(name => new OriginalResourceManager.OriginalFile
        {
            Name = name, Bytes = pristine.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(pristine))
        }).ToArray()
    }));
    var manager = new OriginalResourceManager();
    manager.OnClientVersionMismatch = (_, _) => throw new Exception("Matching game versions must not prompt.");
    string workspace = await manager.PrepareWorkspaceAsync(game, package);
    try
    {
        manager.Commit(game, workspace, () => throw new InvalidOperationException("simulated commit failure"));
        throw new Exception("Expected commit failure.");
    }
    catch (InvalidOperationException ex) when (ex.Message == "simulated commit failure") { }
    Assert(!File.Exists(Path.Combine(destination, missing)), "Rollback must remove newly created dat1.");
    foreach (string name in OriginalResourceManager.FileNames.Where(name => name != missing))
        Assert(File.ReadAllBytes(Path.Combine(destination, name)).SequenceEqual(previous), "Rollback must restore " + name);
    OriginalResourceManager.CleanupWorkspace(workspace);

    workspace = await manager.PrepareWorkspaceAsync(game, package);
    manager.Commit(game, workspace);
    foreach (string name in OriginalResourceManager.FileNames)
        Assert(File.ReadAllBytes(Path.Combine(destination, name)).SequenceEqual(pristine), "Commit must restore " + name);
    OriginalResourceManager.CleanupWorkspace(workspace);

    // Reapplying text must start from the installed fonts and only reset 0a0000.
    foreach (string name in OriginalResourceManager.FileNames)
        File.WriteAllBytes(Path.Combine(destination, name), previous);
    workspace = await manager.PrepareWorkspaceAsync(game, package, preserveFonts: true);
    foreach (string name in OriginalResourceManager.FileNames)
        Assert(File.ReadAllBytes(Path.Combine(workspace, "sqpack", "ffxiv", name)).SequenceEqual(
            name.StartsWith("000000.") ? previous : pristine), "Reapply workspace must preserve fonts: " + name);
    manager.Commit(game, workspace, preserveFonts: true);
    foreach (string name in OriginalResourceManager.FileNames)
        Assert(File.ReadAllBytes(Path.Combine(destination, name)).SequenceEqual(
            name.StartsWith("000000.") ? previous : pristine), "Text reapply must not reset fonts: " + name);
    OriginalResourceManager.CleanupWorkspace(workspace);

    workspace = await manager.PrepareWorkspaceAsync(game, package);
    manager.Commit(game, workspace);
    foreach (string name in OriginalResourceManager.FileNames)
        Assert(File.ReadAllBytes(Path.Combine(destination, name)).SequenceEqual(pristine), "Explicit full restore must reset fonts: " + name);
    OriginalResourceManager.CleanupWorkspace(workspace);

    File.WriteAllText(Path.Combine(game, "ffxivgame.ver"), "2026.10.07.0000.0000");
    int prompts = 0;
    manager.OnClientVersionMismatch = (resource, client) =>
    {
        Assert(resource == version && client == "2026.10.07.0000.0000", "Compare against actual game version.");
        prompts++;
        return Task.FromResult(false);
    };
    try
    {
        await manager.PrepareWorkspaceAsync(game, package);
        throw new Exception("Declined version mismatch must cancel.");
    }
    catch (OperationCanceledException) { }
    Assert(prompts == 1, "Prompt once for actual mismatch.");
    manager.OnClientVersionMismatch = (_, _) => Task.FromResult(true);
    workspace = await manager.PrepareWorkspaceAsync(game, package);
    OriginalResourceManager.CleanupWorkspace(workspace);
    Console.WriteLine("PASS: missing dat1, rollback, matching versions, declined/accepted game version mismatch.");
}
finally
{
    Directory.Delete(root, true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
