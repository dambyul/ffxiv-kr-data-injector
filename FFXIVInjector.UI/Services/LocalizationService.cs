using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Data;
using Avalonia.Threading;

namespace FFXIVInjector.UI.Services;

public class LocalizationService : INotifyPropertyChanged
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    private Dictionary<string, string> _currentStrings = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentLanguage => "ko";

    [System.Runtime.CompilerServices.IndexerName("Item")]
    public string this[string key]
    {
        get
        {
            if (_currentStrings.TryGetValue(key, out var value))
            {
                return value;
            }
            return key;
        }
    }

    public string GetString(string key, params object[] args)
    {
        string val = this[key];
        try
        {
            if (args.Length > 0) return string.Format(val, args);
            return val;
        }
        catch
        {
            return val;
        }
    }

    private LocalizationService()
    {
        LoadLanguage();
    }

    private void LoadLanguage()
    {
        try
        {
            var newStrings = new Dictionary<string, string>();
            var assembly = Assembly.GetExecutingAssembly();

            LoadFromResource(assembly, "FFXIVInjector.UI.Assets.Languages.ko.json", newStrings);

            _currentStrings = newStrings;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading Korean UI: {ex.Message}");
        }
    }

    private void LoadFromResource(Assembly assembly, string resourceName, Dictionary<string, string> target)
    {
        using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
        {
            if (stream == null) return;
            using (StreamReader reader = new StreamReader(stream))
            {
                string json = reader.ReadToEnd();
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    foreach (var kv in dict) target[kv.Key] = kv.Value;
                }
            }
        }
    }
}
