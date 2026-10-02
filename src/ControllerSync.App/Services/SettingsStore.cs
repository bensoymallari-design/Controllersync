using System.Text.Json;

namespace ControllerSync.App.Services;

public sealed class AppSettings
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 24710;
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; } = 24710;
    public string Channel { get; set; } = "show";
    public string Role { get; set; } = "Primary";
    public bool SendKeyboard { get; set; } = true;
    public bool SendAllKeys { get; set; }
    public bool SendMouseClicks { get; set; } = true;
    public bool SendMouseMove { get; set; }
    public bool PublishSlides { get; set; } = true;
    public bool FollowSlides { get; set; }
    public bool AcceptKeyboard { get; set; }
    public bool AcceptMouse { get; set; }
    public int ResolumePort { get; set; } = 8080;
    public int Layer { get; set; } = 1;
    public int Clip { get; set; } = 1;
    public bool PlayAfterLoad { get; set; } = true;
    public bool LoadOnThisLaptop { get; set; } = true;
    public string ExtraLaptops { get; set; } = "";
    public string ContentPath { get; set; } = "";
    public string ContentWidth { get; set; } = "";
    public string ContentHeight { get; set; } = "";
    public string ContentX { get; set; } = "";
    public string ContentY { get; set; } = "";
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string? OverrideDirectory { get; set; }

    public static string ChooseDirectory()
    {
        if (!string.IsNullOrWhiteSpace(OverrideDirectory))
        {
            Directory.CreateDirectory(OverrideDirectory);
            return OverrideDirectory;
        }

        var nextToExe = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(nextToExe, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return nextToExe;
        }
        catch
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ControllerSync");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static AppSettings Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "settings.json");
            if (!File.Exists(path))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(string directory, AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
    }
}
