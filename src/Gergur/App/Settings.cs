using Gergur.Diagnostics;
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
    public string SearchUrlTemplate { get; set; } = UrlHeuristics.DefaultSearchUrlTemplate;

    [Category(Privacy), DisplayName("Tracking prevention")]
    [Description("Engine-level tracking prevention: None, Basic, Balanced or Strict. Applies at once, open tabs included.")]
    public string TrackingPrevention { get; set; } = "Strict"; // None | Basic | Balanced | Strict

    [Category(Privacy), DisplayName("Ad and tracker blocking")]
    [Description("The hosts-file blocklist enforced on the request hot path. Applies immediately.")]
    public bool BlocklistEnabled { get; set; } = true;

    [Category(Privacy), DisplayName("Page-level ad cleanup")]
    [Description("Cosmetic filtering and the YouTube ad neutralizer injected into every page. Applies from the next page each tab loads.")]
    public bool PageAdCleanup { get; set; } = true;

    [Category(Privacy), DisplayName("Save passwords")]
    [Description("Engine-level password autosave, DPAPI-encrypted inside the profile. Applies at once, open tabs included.")]
    public bool SavePasswords { get; set; } = true;

    [Category(Privacy), DisplayName("Form autofill")]
    [Description("Engine-level form autofill. Applies at once, open tabs included.")]
    public bool FormAutofill { get; set; } = true;

    [Category(Privacy), DisplayName("Page theme")]
    [Description("What sites see for prefers-color-scheme: Auto, Light or Dark. Applies at once, open tabs included.")]
    public string PageTheme { get; set; } = "Auto";   // Auto | Light | Dark

    [Category(Vpn), DisplayName("VPN enabled")]
    [Description("Routes engine traffic and DNS through the local tunnel. The proxy is a browser-process flag, so this needs a restart.")]
    public bool VpnEnabled { get; set; } = false;

    /// <summary>
    /// The tunnel was asked for and did not come up, so this run browses without it. Kept
    /// apart from <see cref="VpnEnabled"/>, which is what the person chose and what gets
    /// saved. Startup used to switch VpnEnabled itself off "for this session", and the next
    /// save of anything, a menu toggle, the settings window or an agent's /settings patch
    /// of an unrelated setting, wrote that off to disk: the vpn stayed off at the next
    /// start with nobody having turned it off, and an agent had got round the rule that it
    /// may not change the vpn at all.
    /// </summary>
    [JsonIgnore]
    internal bool VpnDownThisRun { get; set; }

    /// <summary>
    /// Whether an engine started now should point at the tunnel: chosen, and it came up.
    /// Only for starting one. What the running engine was actually given is
    /// <see cref="BrowserEnvironment.ProxyInForce"/>, which VpnEnabled changing later does
    /// not move.
    /// </summary>
    [JsonIgnore]
    internal bool StartWithProxy => VpnEnabled && !VpnDownThisRun;

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
        // The sleep timers are deliberately absent, and for a while they should have been
        // here instead: a window read them once when it opened, so changing one did
        // nothing until the next window. They are pushed into the live lifecycle manager
        // now (MainForm.ApplyLiveSettings), which is what the dialog always implied by
        // not listing them.
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load()
    {
        return Load(SettingsPath);
    }

    /// <summary>
    /// The settings in a given file, or defaults when it cannot be used.
    ///
    /// Takes a path so the decisions below can be exercised somewhere other than the
    /// user's real file. That file is the only home of the phone drop pairing key, which
    /// is recoverable from nothing in this build, and it has already been destroyed once.
    /// </summary>
    internal static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();
        }
        catch (JsonException)
        {
            // Genuinely not json. Start on defaults rather than refuse to start, but keep
            // the file: writing defaults straight over it is what turns "truncated by a
            // crash mid-write" into "the pairing key is gone". A person can read the copy.
            //
            // And if it could not be set aside, do not write at all. Overwriting the file
            // we were unable to protect is the loss this whole mechanism exists to
            // prevent, and a file that will not move is exactly when it would happen.
            if (SetAside(path) is null)
                return new Settings { NotSavingBecause = "the settings file could not be read, and could not be moved aside" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient: a second instance part way through a save, an antivirus lock, a
            // permissions blip. The file is probably fine, so run on defaults in memory
            // and touch nothing. Saving here would replace settings we merely could not
            // read this once.
            DebugLog.WriteAlways($"Settings unreadable this run, leaving the file alone: {ex.Message}");
            return new Settings { NotSavingBecause = "the settings file could not be read when Gergur started" };
        }
        catch (Exception ex)
        {
            // Anything else at all. This runs at startup and nothing above it handles an
            // exception, so narrowing the catches above must not turn an odd failure into
            // a browser that will not open.
            DebugLog.WriteAlways($"Settings could not be read: {ex.Message}");
            return new Settings { NotSavingBecause = "the settings file could not be read when Gergur started" };
        }

        var settings = new Settings();
        try
        {
            settings.Save(path);
        }
        catch (Exception ex)
        {
            // Outside the try above, and so the one line in here that could still stop
            // the browser opening: the same shape as the fresh-install crash, one line
            // lower. Running on defaults held in memory beats not starting.
            DebugLog.Write($"Settings could not be written on first use: {ex.Message}");
        }
        return settings;
    }

    /// <summary>
    /// Moves a file that could not be parsed out of the way, keeping every byte.
    ///
    /// Separate from the path it normally runs on so it can be exercised against a temp
    /// directory. Nothing here may be first run against the real file: it is the only
    /// home of the phone drop's pairing key, which is recoverable from nothing in this
    /// build, and this project has already lost it once.
    /// </summary>
    internal static string? SetAside(string path, Func<DateTime>? now = null)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            string directory = Path.GetDirectoryName(path)!;
            string stem = Path.GetFileNameWithoutExtension(path);
            string stamp = (now ?? (() => DateTime.Now))().ToString("yyyyMMdd-HHmmss");

            // The stamp is only good to the second, and two failures inside one second
            // are exactly the case where the original must not be written over.
            for (int attempt = 0; attempt < 100; attempt++)
            {
                string suffix = attempt == 0 ? "" : "-" + attempt;
                string kept = Path.Combine(directory, $"{stem}.unreadable-{stamp}{suffix}.json");
                if (File.Exists(kept))
                    continue;
                File.Move(path, kept, overwrite: false);
                return kept;
            }
            return null;
        }
        catch
        {
            return null; // Nothing here is worth failing to start over.
        }
    }

    /// <summary>
    /// Writes a file in a way that cannot leave half of one behind.
    ///
    /// A bare WriteAllText truncates first and fills in after, so a crash, a power cut or
    /// a full disk in between leaves something that will not parse. Writing beside it and
    /// replacing in one step means the worst case is the previous contents, whole.
    ///
    /// The temp write is deliberately outside the try. A full disk is the canonical reason
    /// to write a temp file at all, and folding it into the catch sent it straight to the
    /// direct write it was avoiding, which truncates the live file and then fails to fill
    /// it: the one case the docstring promised was safe was the one it broke.
    /// </summary>
    internal static void WriteAtomically(string path, string contents)
    {
        // The public Save() used to do this and the path seam dropped it, which meant a
        // machine with no %LOCALAPPDATA%\\Gergur yet died in Settings.Load before the first
        // window existed: DirectoryNotFoundException on settings.json.tmp, no handler above
        // it, no window. A fresh install did not start.
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        string beside = path + ".tmp";
        File.WriteAllText(beside, contents);

        try
        {
            if (File.Exists(path))
                File.Replace(beside, path, null);
            else
                File.Move(beside, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Replace can fail on some filesystems and under some antivirus hooks. The
            // temp file is known good by now, so a copy is the weaker guarantee rather
            // than none, and losing the change the user just made would be worse.
            //
            // A copy truncates first, though, and the canonical reason to be here is a
            // full disk, where it will then fail to fill it back in. So keep what was
            // there and put it back if the copy does not finish: the temp file is still
            // on disk either way, and the path is logged so it can be recovered by hand.
            DebugLog.Write($"Atomic replace failed for {path}, copying instead: {ex.Message}");

            bool existed = File.Exists(path);
            byte[]? previous = null;
            if (existed)
            {
                try
                {
                    previous = File.ReadAllBytes(path);
                }
                catch (Exception readFailed)
                {
                    // Without a copy of what is there, the truncating copy below could
                    // destroy it with nothing to put back. A file that was not written is
                    // recoverable; a half written one is not, and this one holds a key
                    // that exists nowhere else.
                    DebugLog.Write(
                        $"Settings not written: {path} could not be read to protect it "
                        + $"({readFailed.Message}). The new values are at {beside}.");
                    throw;
                }
            }

            try
            {
                File.Copy(beside, path, overwrite: true);
                try { File.Delete(beside); } catch { }
            }
            catch
            {
                if (previous is not null)
                {
                    try
                    {
                        File.WriteAllBytes(path, previous);
                        DebugLog.Write($"Settings write failed; the previous values were put back. New values at {beside}.");
                    }
                    catch (Exception restoreFailed)
                    {
                        DebugLog.Write(
                            $"Settings write failed AND could not be put back ({restoreFailed.Message}). "
                            + $"The previous values are lost; the new ones are at {beside}.");
                    }
                }
                else
                {
                    DebugLog.Write($"Settings write failed; the new values are at {beside}.");
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Writes the settings out, and says whether it did: false when this run must not touch
    /// the file (<see cref="NotSavingBecause"/>). See <see cref="WriteAtomically"/> for why
    /// not directly.
    /// </summary>
    public bool Save()
    {
        Directory.CreateDirectory(DataDir);
        return Save(SettingsPath);
    }

    /// <summary>Writes to a given path, so the round trip can be exercised off the real one.</summary>
    internal bool Save(string path)
    {
        if (NotSavingBecause is { } why)
        {
            DebugLog.WriteAlways($"Settings not saved: {why}");
            return false;
        }
        WriteAtomically(path, JsonSerializer.Serialize(this, JsonOptions));
        return true;
    }

    /// <summary>
    /// Why this run must not write the settings file, or null when it may. Set when the
    /// file was there but could not be used and was left where it is, so what is in memory
    /// is defaults standing in for it. Writing those out replaces the real settings, the
    /// phone drop pairing key and the vpn profile with them, from the next toggle, the
    /// settings window or an agent's /settings. The file stays as it is for the rest of the
    /// run; the next start reads it again.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? NotSavingBecause { get; private init; }

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
