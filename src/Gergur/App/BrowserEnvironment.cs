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

    private BrowserEnvironment(CoreWebView2Environment core, Settings settings)
    {
        Core = core;
        Settings = settings;
    }

    public static async Task<BrowserEnvironment> CreateAsync(Settings settings)
    {
        string userDataFolder = Path.Combine(Settings.DataDir, "Profile");
        Directory.CreateDirectory(userDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = BuildBrowserArguments(settings),
        };
        var core = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder, options);
        return new BrowserEnvironment(core, settings);
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
        if (settings.InactiveMemoryPressure)
            enableFeatures.Add("msWebView2SimulateMemoryPressureWhenInactive");
        if (settings.V8ScavengerMaxMb > 0)
            flags.Add($"--js-flags=--scavenger_max_new_space_capacity_mb={settings.V8ScavengerMaxMb}");
        if (settings.VpnEnabled)
        {
            // Route everything through the local WARP tunnel; the resolver rule
            // forces DNS through the proxy too (no DNS leak), except localhost.
            flags.Add($"--proxy-server=socks5://127.0.0.1:{settings.VpnLocalPort}");
            flags.Add("--host-resolver-rules=\"MAP * ~NOTFOUND , EXCLUDE 127.0.0.1\"");
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
        _ = webView.Handle; // force HWND creation; init needs it even while the control is hidden
        await webView.EnsureCoreWebView2Async(Core);

        webView.CoreWebView2.Profile.PreferredTrackingPreventionLevel = Settings.TrackingPrevention switch
        {
            "None" => CoreWebView2TrackingPreventionLevel.None,
            "Basic" => CoreWebView2TrackingPreventionLevel.Basic,
            "Balanced" => CoreWebView2TrackingPreventionLevel.Balanced,
            _ => CoreWebView2TrackingPreventionLevel.Strict,
        };
        // What sites see for prefers-color-scheme: Auto follows Windows, or pin it.
        webView.CoreWebView2.Profile.PreferredColorScheme = Settings.PageTheme switch
        {
            "Light" => CoreWebView2PreferredColorScheme.Light,
            "Dark" => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
        var webSettings = webView.CoreWebView2.Settings;
        webSettings.IsPasswordAutosaveEnabled = Settings.SavePasswords;
        webSettings.IsGeneralAutofillEnabled = Settings.FormAutofill;
        return webView;
    }
}
