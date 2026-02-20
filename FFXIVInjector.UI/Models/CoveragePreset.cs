using System.Collections.Generic;

namespace FFXIVInjector.UI.Models;

public class CoveragePreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PresetEntry> Entries { get; set; } = new();
}

public class PresetEntry
{
    public string Path { get; set; } = "";
    public EntryType Type { get; set; } = EntryType.File;
}

public enum EntryType
{
    File,
    Directory
}

public class PresetConfig
{
    public List<CoveragePreset> Presets { get; set; } = new();
    public Dictionary<string, int> RSV { get; set; } = new();
}
