using Gergur.Blocking;
using Gergur.Data;
using Gergur.UI;

namespace Gergur.App;

/// <summary>
/// Everything the windows share. One <see cref="BrowserEnvironment"/> above all:
/// every tab in every window then lives in a single browser process group, which is
/// the whole point of the memory policy. Also one blocklist, one history and bookmark
/// store, one tunnel, one agent server. Windows come and go around it.
/// </summary>
public sealed class AppSession
{
    private readonly List<MainForm> _windows = new();

    public Settings Settings { get; }
    public BrowserEnvironment Env { get; }
    public RequestBlocker Blocker { get; }
    public HistoryStore History { get; } = new();
    public BookmarkStore Bookmarks { get; } = new();
    public DownloadManager Downloads { get; } = new();
    public VpnTunnel Vpn { get; }
    public AgentServer? Agent { get; private set; }

    /// <summary>Windows in creation order; the first is the one restored at startup.</summary>
    public IReadOnlyList<MainForm> Windows => _windows;

    /// <summary>
    /// The one live session. How a url forwarded from a second launch finds a window
    /// to open in, without the entry point having to hold a form reference that a
    /// tear-off could outlive.
    /// </summary>
    public static AppSession? Current { get; private set; }

    private AppSession(Settings settings, BrowserEnvironment env, RequestBlocker blocker, VpnTunnel vpn)
    {
        Settings = settings;
        Env = env;
        Blocker = blocker;
        Vpn = vpn;
    }

    /// <summary>The tunnel must already be listening: the engine reads its proxy flag at startup.</summary>
    public static async Task<AppSession> CreateAsync(Settings settings, VpnTunnel vpn)
    {
        var blocker = new RequestBlocker(settings.BlocklistEnabled);
        var env = await BrowserEnvironment.CreateAsync(settings);
        return Current = new AppSession(settings, env, blocker, vpn);
    }

    /// <summary>One server for the whole app; it reaches every window's tabs.</summary>
    public void StartAgent()
    {
        if (Agent is not null || !Settings.AgentServerEnabled)
            return;
        try
        {
            Agent = new AgentServer(this, Settings.AgentServerPort);
            Agent.Start();
        }
        catch (Exception ex)
        {
            // Browsing works without it, but say why: a silent failure leaves an MCP
            // client reporting "connection refused" with nothing to diagnose.
            Agent = null;
            Diagnostics.DebugLog.WriteAlways($"agent server did not start: {ex.Message}");
        }
    }

    public void AddWindow(MainForm window) => _windows.Add(window);

    /// <summary>Drops a closed window and, when it was the last one, tears the shared services down.</summary>
    public void RemoveWindow(MainForm window)
    {
        _windows.Remove(window);
        if (_windows.Count > 0)
            return;
        Agent?.Stop();
        Agent = null;
        Vpn.Stop();
    }

    /// <summary>
    /// Writes every open window to the session file. Called before a window drops
    /// itself from the list, so closing the last one still records what it held.
    /// </summary>
    public void SaveSession()
    {
        var windows = new List<SessionWindow>();
        foreach (var window in _windows)
        {
            if (window.Tabs is not { } tabs)
                continue;
            var kept = tabs.Tabs.Where(t => !HomePage.IsHome(t.Url)).ToList();
            if (kept.Count == 0)
                continue;
            int activeIndex = tabs.ActiveTab is null ? 0 : Math.Max(0, kept.IndexOf(tabs.ActiveTab));
            windows.Add(new SessionWindow(kept.Select(t => new SessionTab(t.Url, t.Title)).ToList(), activeIndex));
        }
        SessionStore.Save(windows);
    }
}
