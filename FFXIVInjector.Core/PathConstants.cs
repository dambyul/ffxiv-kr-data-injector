using System;
using System.IO;

namespace FFXIVInjector.Core;

public static class PathConstants
{
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FFXIVInjector"
    );

    public static string ResourcesRoot => Path.Combine(AppDataPath, "resources");
    public static string CsvPath => Path.Combine(ResourcesRoot, "rawexd");
    public static string FontPath => Path.Combine(ResourcesRoot, "font");
    public static string DataJsonPath => Path.Combine(ResourcesRoot, "data.json");
    public static string BackupPath => Path.Combine(ResourcesRoot, "backup");

    public static void EnsureDirectories()
    {
        if (!Directory.Exists(ResourcesRoot)) Directory.CreateDirectory(ResourcesRoot);
        if (!Directory.Exists(CsvPath)) Directory.CreateDirectory(CsvPath);
        if (!Directory.Exists(FontPath)) Directory.CreateDirectory(FontPath);
        if (!Directory.Exists(BackupPath)) Directory.CreateDirectory(BackupPath);
    }
}
