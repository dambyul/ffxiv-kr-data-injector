using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using FFXIVInjector.Core;

namespace FFXIVInjector.UI.Services;

public class ConfigurationService
{
    private static ConfigurationService? _instance;
    public static ConfigurationService Instance => _instance ??= new ConfigurationService();

    private ConfigurationService() { }

    public void LoadConfiguration()
    {
        // Configuration is now loaded from Secrets.cs via Config class static initialization.
        // No external file loading is required for now.
    }

    private string EnsureTrailingSlash(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        return url.EndsWith('/') ? url : url + "/";
    }
}
