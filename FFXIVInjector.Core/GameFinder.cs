
namespace FFXIVInjector.Core;

public class GameFinder
{
    public string? FindGamePath()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows Registry Logic
            return FindWindowsGamePath();
        }
        else if (OperatingSystem.IsMacOS())
        {
            // MacOS Default Path Logic
            return FindMacGamePath();
        }
        
        return null;
    }

    private string? FindWindowsGamePath()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            // Try standard registry key
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SquareEnix\FINAL FANTASY XIV - A Realm Reborn");
            if (key != null)
            {
                var path = key.GetValue("install_location") as string;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    return path;
                }
            }
            
            // Try common default paths
            string[] commonPaths = {
                @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn",
                @"C:\Program Files\SquareEnix\FINAL FANTASY XIV - A Realm Reborn"
            };

            foreach (var path in commonPaths)
            {
                if (Directory.Exists(path)) return path;
            }
        }
        catch 
        {
            // Ignore registry errors
        }

        return null;
    }

    private string? FindMacGamePath()
    {
        string[] commonPaths = {
            "/Applications/FINAL FANTASY XIV ONLINE.app",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Applications/FINAL FANTASY XIV ONLINE.app")
        };

        foreach (var path in commonPaths)
        {
            if (Directory.Exists(path)) return path;
        }

        return null;
    }
}
