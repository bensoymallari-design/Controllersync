using Avalonia;
using ControllerSync.App.Services;

namespace ControllerSync.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var settings = args.FirstOrDefault(arg => arg.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase));
        if (settings != null)
        {
            Directory.CreateDirectory(settings.Split('=', 2)[1]);
            SettingsStore.OverrideDirectory = Path.GetFullPath(settings.Split('=', 2)[1]);
            args = args.Where(arg => !arg.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
