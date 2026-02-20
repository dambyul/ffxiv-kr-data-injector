using System.Diagnostics;
using System.Linq;

namespace FFXIVInjector.Core;

public static class GameProcess
{
    private static readonly string[] ProcessNames = { "ffxiv_dx11", "ffxiv" };

    public static bool IsRunning()
    {
        return ProcessNames.Any(name => Process.GetProcessesByName(name).Any());
    }

    public static string GetActiveProcessName()
    {
        foreach (var name in ProcessNames)
        {
            if (Process.GetProcessesByName(name).Any()) return name;
        }
        return string.Empty;
    }
}
