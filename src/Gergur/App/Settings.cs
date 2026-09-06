using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gergur.App;

public sealed class Settings
{
    // Categories are ordered by the leading digit, which the settings window strips.
    private const string Memory = "1 Memory policy";
    private const string Engine = "2 Engine (restart required)";
    private const string Browsing = "3 Browsing";
    private const string Privacy = "4 Privacy and blocking";
    private const string Vpn = "5 VPN";
    private const string Agent = "6 Agent API";
    private const string Phone = "7 Phone drop";

    [Category(Memory), DisplayName("Suspend after (minutes)")]
    [Description("Idle minutes before a background tab is frozen and its renderer memory trimmed. Tabs playing audio are exempt.")]
    public int SuspendAfterMinutes { get; set; } = 3;

    [Category(Memory), DisplayName("Discard after (minutes)")]
    [Description("Idle minutes before a background tab's WebView is destroyed entirely. It keeps its url, title and favicon and holds zero engine processes until clicked. The selected tab is never discarded.")]
    public int DiscardAfterMinutes { get; set; } = 15;

    [Category(Memory), DisplayName("Sleep everything after (minutes backgrounded)")]
    [Description("Minutes the window sits minimized, or the PC locked, before every tab sleeps including the active one.")]
    public int SleepAllWhenBackgroundedMinutes { get; set; } = 1;

    // Browser-process flags. Undocumented for WebView2, so each one is a toggle;
    // changes apply only after the app (and the engine processes) fully exit.
    [Category(Engine), DisplayName("Process per site")]
    [Description("Share one renderer process across all tabs of the same site.")]
    public bool ProcessPerSite { get; set; } = true;

    [Category(Engine), DisplayName("Disable site isolation")]
    [Description("Trades process isolation for fewer processes. Off by default: it weakens a real security boundary.")]
    public bool DisableSiteIsolation { get; set; } = false;

    [Category(Engine), DisplayName("Disable spare renderer")]
    [Description("Stops the engine keeping a warm standby renderer process around.")]
    public bool DisableSpareRenderer { get; set; } = true;

    [Category(Engine), DisplayName("Inactive memory pressure")]
    [Description("Makes hidden views behave as though the system were low on memory, so they release more.")]
    public bool InactiveMemoryPressure { get; set; } = true;

    // Leave false. Google made FedCM mandatory for Sign in with Google in Aug 2025,
    // and WebView2 does render the FedCM account chooser correctly (verified), so
    // disabling it removes the API entirely and breaks Google login with no fallback.
    [Category(Engine), DisplayName("Disable FedCM")]
    [Description("Leave off. Sign in with Google requires FedCM, and WebView2 renders its account chooser correctly, so turning this on breaks Google login with no fallback.")]
    public bool DisableFedCm { get; set; } = false;

    [Category(Engine), DisplayName("V8 scavenger max new space (MB)")]
    [Description("Caps V8's young-generation heap per renderer. 0 leaves the engine default.")]
    public int V8ScavengerMaxMb { get; set; } = 0;

    [Category(Engine), DisplayName("Extra browser arguments")]
    [Description("Appended verbatim to the engine command line. Wrong values here can stop the browser starting.")]
    public string ExtraBrowserArguments { get; set; } = "";

    [Category(Browsing), DisplayName("Search URL template")]
    [Description("Where a non-url address-bar entry goes. {0} is replaced with the query.")]
    public string SearchUrlTemplate { get; set; } = "https://www.google.com/search?q={0}";

    [Category(Privacy), DisplayName("Tracking prevention")]
    [Description("Engine-level tracking prevention: None, Basic, Balanced or Strict. Applies to new tabs.")]
    public string TrackingPrevention { get; set; } = "Strict"; // None | Basic | Balanced | Strict

    [Category(Privacy), DisplayName("Ad and tracker blocking")]
    [Description("The hosts-file blocklist enforced on the request hot path. Applies immediately.")]
    public bool BlocklistEnabled { get; set; } = true;

    [Category(Privacy), DisplayName("Page-level ad cleanup")]
    [Description("Cosmetic filtering and the YouTube ad neutralizer injected into every page. Applies to new tabs.")]
    public bool PageAdCleanup { get; set; } = true;

    [Category(Privacy), DisplayName("Save passwords")]
    [Description("Engine-level password autosave, DPAPI-encrypted inside the profile. Applies to new tabs.")]
    public bool SavePasswords { get; set; } = true;

    [Category(Privacy), DisplayName("Form autofill")]
    [Description("Engine-level form autofill. Applies to new tabs.")]
    public bool FormAutofill { get; set; } = true;

    [Category(Privacy), DisplayName("Page theme")]
    [Description("What sites see for prefers-color-scheme: Auto, Light or Dark. Applies to new tabs.")]
    public string PageTheme { get; set; } = "Auto";   // Auto | Light | Dark

    [Category(Vpn), DisplayName("VPN enabled")]
    [Description("Routes engine traffic and DNS through the local tunnel. The proxy is a browser-process flag, so this needs a restart.")]
    public bool VpnEnabled { get; set; } = false;

    [Category(Vpn), DisplayName("Local SOCKS5 port")]
    [Description("Port wireproxy listens on.")]
    public int VpnLocalPort { get; set; } = 24001;

    /// <summary>Which WireGuard profile the tunnel uses; empty picks the first available
    /// (Cloudflare WARP when it is set up). Drop extra .conf files in vpn\profiles\ to
    /// choose an exit country, since WARP always exits at the nearest datacenter.</summary>
    [Category(Vpn), DisplayName("Profile")]
    [Description("Which WireGuard profile the tunnel uses. Empty picks the first available. Extra .conf files in vpn\\profiles choose an exit country.")]
    public string VpnProfile { get; set; } = "";

    // Hosts that skip the tunnel and connect directly. Google flags VPN/datacenter
    // IPs, which breaks Gmail's realtime channel and the account switcher; bypass fixes it.
    [Category(Vpn), DisplayName("Bypass hosts")]
    [Description("Semicolon-separated hosts that skip the tunnel and connect directly. Google flags VPN and datacenter IPs, which breaks Gmail's realtime channel and the account switcher.")]
    public string VpnBypassHosts { get; set; } = "*.google.com;*.googleusercontent.com;*.gstatic.com;accounts.youtube.com";

    [Category(Agent), DisplayName("Agent API enabled")]
    [Description("Token-protected local API on 127.0.0.1 that lets an AI agent drive the browser.")]
    public bool AgentServerEnabled { get; set; } = true;

    [Category(Agent), DisplayName("Agent API port")]
    public int AgentServerPort { get; set; } = 24002;

    [Category(Phone), DisplayName("Phone drop enabled")]
    [Description("Serves a small page on your local network so a paired phone can send and receive links, messages and files. Off by default: unlike the agent API this listens beyond loopback. It can only pass items back and forth, never drive the browser.")]
    public bool DropEnabled { get; set; } = false;

    [Category(Phone), DisplayName("Phone drop port")]
    public int DropPort { get; set; } = 24003;

    [Category(Phone), DisplayName("Pairing key")]
    [Description("Every phone request must carry this. Generated on first use. Change it to revoke a phone that has the link.")]
    public string DropKey { get; set; } = "";

    [Browsable(false)]
    [JsonIgnore]
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gergur");

    [Browsable(false)]
    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(DataDir, "settings.json");

    /// <summary>Settings the engine only reads at startup: changing one needs a restart.</summary>
    public static readonly IReadOnlySet<string> RestartRequired = new HashSet<string>
    {
        nameof(ProcessPerSite), nameof(DisableSiteIsolation), nameof(DisableSpareRenderer),
        nameof(InactiveMemoryPressure), nameof(DisableFedCm), nameof(V8ScavengerMaxMb),
        nameof(ExtraBrowserArguments), nameof(VpnEnabled), nameof(VpnLocalPort),
        nameof(VpnBypassHosts), nameof(AgentServerEnabled), nameof(AgentServerPort),
        // DropPort only. The phone drop itself is turned on and off from the menu, which
        // starts and stops the listener there and then, so listing it here would tell you
        // to restart for something that already took effect.
        nameof(DropPort),
    };

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

    /// <summary>A detached copy, so a settings window can be cancelled without a trace.</summary>
    public Settings Clone()
        => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    /// <summary>Copies every stored value across, keeping the live instance's identity.</summary>
    public void CopyFrom(Settings other)
    {
        foreach (var property in Editable())
            property.SetValue(this, property.GetValue(other));
    }

    /// <summary>Names of the properties whose values differ between the two.</summary>
    public IEnumerable<string> DifferencesFrom(Settings other)
    {
        foreach (var property in Editable())
        {
            if (!Equals(property.GetValue(this), property.GetValue(other)))
                yield return property.Name;
        }
    }

    private static IEnumerable<System.Reflection.PropertyInfo> Editable()
        => typeof(Settings).GetProperties()
            .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0);
}
