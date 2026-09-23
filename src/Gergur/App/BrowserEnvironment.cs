using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Gergur.App;

/// <summary>
/// Owns the single CoreWebView2Environment shared by every tab, so all tabs share
/// one browser/GPU process group and renderer processes are pooled per-site.
/// </summary>
public sealed class BrowserEnvironment
{
    public CoreWebView2Environment Core { get; }
    public Settings Settings { get; }

    /// <summary>
    /// Whether this engine was started pointing at the tunnel. Fixed for its life, because the
    /// proxy is a browser-process flag: VpnEnabled can change underneath it (the settings
    /// window with its restart declined, or its save refused or failed), and anything
    /// that worked "is the vpn on" out from the setting then said off with every request
    /// still going through the tunnel, or on with none of them doing so. The menu, the
    /// status bar, GET /settings and whether a profile can be switched live all read this.
    /// </summary>
    public bool ProxyInForce { get; }

    private BrowserEnvironment(CoreWebView2Environment core, Settings settings, bool proxyInForce)
    {
        Core = core;
        Settings = settings;
        ProxyInForce = proxyInForce;
    }

    public static async Task<BrowserEnvironment> CreateAsync(Settings settings)
    {
        string userDataFolder = Path.Combine(Settings.DataDir, "Profile");
        Directory.CreateDirectory(userDataFolder);

        // Read beside the arguments, which read the same property, so what is recorded is
        // what the engine was given.
        bool proxied = settings.StartWithProxy;
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = BuildBrowserArguments(settings),
        };
        var core = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder, options);
        return new BrowserEnvironment(core, settings, proxied);
    }

    internal static string BuildBrowserArguments(Settings settings)
    {
        var flags = new List<string>();
        var enableFeatures = new List<string>();
        var disableFeatures = new List<string>();
        if (settings.ProcessPerSite)
            flags.Add("--process-per-site");
        if (settings.DisableSiteIsolation)
            flags.Add("--disable-site-isolation-trials");
        if (settings.DisableSpareRenderer)
            disableFeatures.Add("SpareRendererForSitePerProcess");
        if (settings.DisableFedCm)
            disableFeatures.Add("FedCm");
        if (settings.InactiveMemoryPressure)
            enableFeatures.Add("msWebView2SimulateMemoryPressureWhenInactive");
        if (settings.V8ScavengerMaxMb > 0)
            flags.Add($"--js-flags=--scavenger_max_new_space_capacity_mb={settings.V8ScavengerMaxMb}");
        if (settings.StartWithProxy)
        {
            flags.Add($"--proxy-server=socks5://127.0.0.1:{settings.VpnLocalPort}");

            // Hosts that skip the tunnel and connect directly.
            var bypassHosts = new List<string> { "localhost", "127.0.0.1" };
            if (!string.IsNullOrWhiteSpace(settings.VpnBypassHosts))
                bypassHosts.AddRange(settings.VpnBypassHosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            flags.Add($"--proxy-bypass-list=\"{string.Join(';', bypassHosts)}\"");

            // Force DNS through the proxy (no leak) for tunneled hosts, but EXCLUDE the
            // bypass hosts so they resolve locally and reach the direct connection.
            //
            // The wildcard has to survive into the EXCLUDE rule. Chromium matches these
            // patterns literally, so "EXCLUDE google.com" covers google.com and nothing
            // else, while the proxy bypass "*.google.com" still sends www.google.com
            // direct: the request skips the tunnel and then has no DNS, which fails as
            // HostNameNotResolved. Emit the wildcard AND the bare domain, since
            // "*.google.com" does not match "google.com" either.
            var resolver = new List<string> { "MAP * ~NOTFOUND" };
            foreach (var host in bypassHosts)
            {
                resolver.Add("EXCLUDE " + host);
                if (host.StartsWith("*.", StringComparison.Ordinal))
                    resolver.Add("EXCLUDE " + host[2..]);
            }
            flags.Add($"--host-resolver-rules=\"{string.Join(" , ", resolver)}\"");
        }
        // Chromium honors only one instance of each feature switch, so join lists.
        if (enableFeatures.Count > 0)
            flags.Add("--enable-features=" + string.Join(',', enableFeatures));
        if (disableFeatures.Count > 0)
            flags.Add("--disable-features=" + string.Join(',', disableFeatures));
        if (!string.IsNullOrWhiteSpace(settings.ExtraBrowserArguments))
            flags.Add(settings.ExtraBrowserArguments.Trim());
        return string.Join(' ', flags);
    }

    /// <summary>
    /// Creates a WebView2 control parented into <paramref name="host"/> and initialized
    /// on the shared environment. WebView2.Source must never be assigned before this
    /// completes - that triggers implicit init against a default, non-shared environment.
    /// </summary>
    public async Task<WebView2> CreateWebViewAsync(Control host, bool visible)
    {
        var webView = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = visible,
            // White, like every real browser: sites with transparent regions assume a
            // white canvas; a dark one bleeds through and fakes broken dark mode.
            DefaultBackgroundColor = Color.White,
        };
        host.Controls.Add(webView);
        try
        {
            _ = webView.Handle; // force HWND creation; init needs it even while the control is hidden
            await webView.EnsureCoreWebView2Async(Core);
            ApplyViewSettings(webView.CoreWebView2, Settings);
            return webView;
        }
        catch
        {
            // Everything after Controls.Add, not just the init. The profile and settings
            // calls below it can throw too, on a runtime older than the SDK or a browser
            // process that died a moment after starting, and the control is parented and
            // owns an HWND by then. The caller never learns about it, because it only
            // takes ownership of what is returned, so nothing can ever reach it to dispose
            // it: one orphan per failed build, and the tab retries every five seconds.
            try { host.Controls.Remove(webView); } catch { }
            try { webView.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// The page settings a view takes from <see cref="Settings"/>. Used when a view is made
    /// and again when the settings change, so a change reaches the tabs already open. It
    /// used to run only on the first path: an open page kept its old colour scheme and
    /// password saving until the tab was rebuilt, while the settings window and the agent
    /// API both said the change was applied.
    /// </summary>
    internal static void ApplyViewSettings(CoreWebView2 core, Settings settings)
    {
        core.Profile.PreferredTrackingPreventionLevel = settings.TrackingPrevention switch
        {
            "None" => CoreWebView2TrackingPreventionLevel.None,
            "Basic" => CoreWebView2TrackingPreventionLevel.Basic,
            "Balanced" => CoreWebView2TrackingPreventionLevel.Balanced,
            _ => CoreWebView2TrackingPreventionLevel.Strict,
        };
        // What sites see for prefers-color-scheme: Auto follows Windows, or pin it.
        core.Profile.PreferredColorScheme = settings.PageTheme switch
        {
            "Light" => CoreWebView2PreferredColorScheme.Light,
            "Dark" => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
        core.Settings.IsPasswordAutosaveEnabled = settings.SavePasswords;
        core.Settings.IsGeneralAutofillEnabled = settings.FormAutofill;
    }
}
