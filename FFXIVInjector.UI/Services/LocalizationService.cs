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
    private string _currentLanguage = "ko";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value)
            {
                _currentLanguage = value;
                LoadLanguage(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            }
        }
    }

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
        LoadLanguage(_currentLanguage);
    }

    public void LoadLanguage(string langCode)
    {
        try
        {
            var newStrings = new Dictionary<string, string>();
            var assembly = Assembly.GetExecutingAssembly();

            // Load English base, then overlay target language
            string enResource = "FFXIVInjector.UI.Assets.Languages.en.json";
            LoadFromResource(assembly, enResource, newStrings);
            if (langCode != "en")
            {
                string targetResource = $"FFXIVInjector.UI.Assets.Languages.{langCode}.json";
                LoadFromResource(assembly, targetResource, newStrings);
            }

            _currentStrings = newStrings;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading language {langCode}: {ex.Message}");
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
