using Avalonia;
using System;

namespace FFXIVInjector.UI;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            foreach (string cache in new[] { Core.PathConstants.IconPackagePath, Core.PathConstants.VoicePackagePath, Core.PathConstants.OriginalPackagePath })
            {
                try { Core.ResourceCacheCleanup.CleanAbandoned(cache); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Console.WriteLine(ex.Message); }
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("CRITICAL ERROR: " + ex.ToString());
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
