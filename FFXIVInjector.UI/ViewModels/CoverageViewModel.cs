using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFXIVInjector.UI.Models;

namespace FFXIVInjector.UI.ViewModels;

public partial class CoverageViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<PresetItemViewModel> _presets = new();

    private bool _isUpdatingSelection = false;

    private bool? _isAllSelected;
    public bool? IsAllSelected
    {
        get => _isAllSelected;
        set
        {
            if (SetProperty(ref _isAllSelected, value))
            {
                if (!_isUpdatingSelection && value.HasValue)
                {
                    ToggleAll(value.Value);
                }
            }
        }
    }

    private void ToggleAll(bool selected)
    {
        _isUpdatingSelection = true;
        foreach (var preset in Presets)
        {
            preset.IsChecked = selected;
        }
        _isUpdatingSelection = false;
        SaveSelection();
    }

    private void UpdateIsAllSelected()
    {
        if (_isUpdatingSelection) return;

        _isUpdatingSelection = true;
        if (Presets.All(p => p.IsChecked))
        {
            IsAllSelected = true;
        }
        else if (Presets.All(p => !p.IsChecked))
        {
            IsAllSelected = false;
        }
        else
        {
            IsAllSelected = null; // Indeterminate
        }
        _isUpdatingSelection = false;
    }

    private string _resourcePath = "";

    public CoverageViewModel(string resourcePath)
    {
        _resourcePath = resourcePath;
        LoadPresets();
    }

    private string _appDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FFXIVInjector", "coverage_selection.json");

    [ObservableProperty]
    private bool _isCompatibilityModeEnabled;

    partial void OnIsCompatibilityModeEnabledChanged(bool value)
    {
        SaveSelection();
    }

    private class SelectionData
    {
        public List<string> Presets { get; set; } = new();
        public bool CompatibilityMode { get; set; }
    }

    private void LoadSelection()
    {
        try
        {
            if (File.Exists(_appDataPath))
            {
                var json = File.ReadAllText(_appDataPath);
                try 
                {
                    // Try new format first
                    var data = JsonSerializer.Deserialize<SelectionData>(json);
                    if (data != null)
                    {
                        foreach (var preset in Presets)
                        {
                            preset.IsChecked = data.Presets.Contains(preset.Name);
                        }
                        IsCompatibilityModeEnabled = data.CompatibilityMode;
                        return;
                    }
                }
                catch
                {
                    // Fallback to old format (List<string>)
                    var selection = JsonSerializer.Deserialize<List<string>>(json);
                    if (selection != null)
                    {
                        foreach (var preset in Presets)
                        {
                            preset.IsChecked = selection.Contains(preset.Name);
                        }
                    }
                }
            }
        }
        catch (Exception) { /* Ignore load errors */ }
    }

    private void SaveSelection()
    {
        try
        {
            var data = new SelectionData
            {
                Presets = Presets.Where(p => p.IsChecked).Select(p => p.Name).ToList(),
                CompatibilityMode = IsCompatibilityModeEnabled
            };
            
            var json = JsonSerializer.Serialize(data);
            var dir = Path.GetDirectoryName(_appDataPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_appDataPath, json);
        }
        catch (Exception) { /* Ignore save errors */ }
    }

    private void LoadPresets()
    {
        Presets.Clear();

        // Load presets from JSON
        string[] searchPaths = {
            FFXIVInjector.Core.PathConstants.DataJsonPath
        };

        string? presetPath = searchPaths.FirstOrDefault(File.Exists);
        bool loaded = false;

        if (presetPath != null)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                options.Converters.Add(new JsonStringEnumConverter());

                var json = File.ReadAllText(presetPath);
                var config = JsonSerializer.Deserialize<PresetConfig>(json, options);
                if (config?.Presets != null)
                {
                    var rsvData = config.RSV ?? new Dictionary<string, int>();

                    foreach (var preset in config.Presets)
                    {
                        int rsvCount = CalculateRsvCount(preset, rsvData);
                        Presets.Add(new PresetItemViewModel
                        {
                            Name = preset.Name,
                            Description = preset.Description,
                            Preset = preset,
                            IsChecked = true,
                            UnresolvedRsvCount = rsvCount
                        });
                    }
                    loaded = true;
                }
            }
            catch (Exception)
            {
                // Fallback will handle it
            }
        }

        // Fallback if JSON is missing or empty
        if (!loaded)
        {
            var loc = Services.LocalizationService.Instance;
            Presets.Add(new PresetItemViewModel { 
                Name = loc.GetString("Coverage.DefaultFont.Name"), 
                Description = loc.GetString("Coverage.DefaultFont.Description"), 
                Preset = new CoveragePreset { Entries = new List<PresetEntry> { new PresetEntry { Path = "font", Type = EntryType.Directory } } } 
            });
            Presets.Add(new PresetItemViewModel { 
                Name = loc.GetString("Coverage.DefaultCsv.Name"), 
                Description = loc.GetString("Coverage.DefaultCsv.Description"), 
                Preset = new CoveragePreset { Entries = new List<PresetEntry> { new PresetEntry { Path = "rawexd", Type = EntryType.Directory } } } 
            });
        }

        // Attach property change handlers for auto-save
        foreach (var preset in Presets)
        {
            preset.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PresetItemViewModel.IsChecked))
                {
                    UpdateIsAllSelected();
                    SaveSelection();
                }
            };
        }

        // Load saved state
        LoadSelection();

        // Initial All-Selected state
        UpdateIsAllSelected();
    }

    public string[] GetSelectedFiles()
    {
        var result = new List<string>();
        foreach (var item in Presets.Where(p => p.IsChecked))
        {
            foreach (var entry in item.Preset.Entries)
            {
                string fullPath = Path.Combine(_resourcePath, entry.Path);
                if (entry.Type == EntryType.Directory)
                {
                    CollectAllFilesInDirectory(fullPath, result);
                }
                else if (File.Exists(fullPath))
                {
                    result.Add(Path.GetRelativePath(_resourcePath, fullPath).Replace('\\', '/'));
                }
            }
        }
        return result.Distinct().ToArray();
    }

    private void CollectAllFilesInDirectory(string dirPath, List<string> result)
    {
        if (!Directory.Exists(dirPath)) return;

        var files = Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories)
                             .Where(f => {
                                 var ext = Path.GetExtension(f).ToLowerInvariant();
                                 return ext == ".csv" || ext == ".exd" || ext == ".fdt" || ext == ".tex";
                             });

        foreach (var file in files)
        {
            result.Add(Path.GetRelativePath(_resourcePath, file).Replace('\\', '/'));
        }
    }

    private int CalculateRsvCount(CoveragePreset preset, Dictionary<string, int> rsvData)
    {
        var files = new List<string>();
        foreach (var entry in preset.Entries)
        {
            string fullPath = Path.Combine(_resourcePath, entry.Path);
            if (entry.Type == EntryType.Directory)
            {
                CollectAllFilesInDirectory(fullPath, files);
            }
            else if (File.Exists(fullPath))
            {
                files.Add(Path.GetRelativePath(_resourcePath, fullPath).Replace('\\', '/'));
            }
        }

        int count = 0;
        foreach (var file in files.Distinct())
        {
            if (rsvData.TryGetValue(file, out int fileRsvCount))
            {
                count += fileRsvCount;
            }
        }
        return count;
    }
}
