using Avalonia;
using Velopack;

namespace ScriptArt.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Handles the installer's install, update, and uninstall hooks (and exits for them) before any UI starts; a no-op otherwise.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
