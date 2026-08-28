using System.Text.Json;

namespace Gergur.App;

public sealed class Settings
{
    public int SuspendAfterMinutes { get; set; } = 3;
    public int DiscardAfterMinutes { get; set; } = 15;
    /// <summary>Minutes the window sits minimized (or the PC locked) before every tab sleeps.</summary>
    public int SleepAllWhenBackgroundedMinutes { get; set; } = 1;

    // Browser-process flags. Undocumented for WebView2, so each one is a toggle;
    // changes apply only after the app (and the engine processes) fully exit.
    public bool ProcessPerSite { get; set; } = true;
    public bool DisableSiteIsolation { get; set; } = false;
    public bool DisableSpareRenderer { get; set; } = true;   // no warm standby renderer process
    public bool InactiveMemoryPressure { get; set; } = true; // hidden views act memory-pressured
    public int V8ScavengerMaxMb { get; set; } = 0; // 0 = engine default
    public string ExtraBrowserArguments { get; set; } = "";

    public string SearchUrlTemplate { get; set; } = "https://duckduckgo.com/?q={0}";
    public string TrackingPrevention { get; set; } = "Strict"; // None | Basic | Balanced | Strict
    public bool BlocklistEnabled { get; set; } = true;
    public bool SavePasswords { get; set; } = true;   // engine-level autosave, DPAPI-encrypted in the profile
    public bool FormAutofill { get; set; } = true;

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gergur");

    public static string SettingsPath { get; } = Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new Settings();
        }
        catch
        {
            // Corrupt settings file: fall back to defaults rather than refuse to start.
        }
        var settings = new Settings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
