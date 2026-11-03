using Gergur.Blocking;
using Gergur.Data;
using Gergur.Tabs;
using Gergur.UI;
using Microsoft.Web.WebView2.Core;

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

    // The list belongs to the UI thread. WindowInUse is asked from the agent server's
    // request threads and the pipe thread a second launch forwards links on, where copying
    // the list while a window opens or closes throws or copies a torn list, so it copies
    // under this lock. Not every reader takes it: AgentServer.AllTabs reads unlocked and
    // retries, and most other reads happen on the UI thread inside OnUiAsync.
    private readonly object _windowsLock = new();

    public Settings Settings { get; }

    /// <summary>The settings this engine was started with: what a change to a restart setting is measured against.</summary>
    public Settings StartedWith { get; }
    public BrowserEnvironment Env { get; }
    public RequestBlocker Blocker { get; }
    public HistoryStore History { get; } = new();

    /// <summary>What agents have done since the browser started, for the agents panel.</summary>
    public AgentActivity Agents { get; } = new();

    /// <summary>Questions agents are waiting on the person for, shown in a bar across their windows.</summary>
    public AgentAsks Asks { get; } = new();
    public BookmarkStore Bookmarks { get; } = new();

    /// <summary>The Google account the person signs in to each site with, learned from their own sign-ins.</summary>
    public GoogleAccountStore GoogleAccounts { get; } = new();
    public DownloadManager Downloads { get; } = new();
    public DropStore Drop { get; } = new();

    /// <summary>When the phone panel was last opened in any window: what came from the phone after it is new, and dots the phone button.</summary>
    public DateTime DropSeenUtc { get; set; } = DateTime.UtcNow;
    public DropServer? PhoneBridge { get; private set; }
    public VpnTunnel Vpn { get; }
    public AgentServer? Agent { get; private set; }

    /// <summary>Windows in creation order; the first is the one restored at startup.</summary>
    public IReadOnlyList<MainForm> Windows => _windows;

    /// <summary>
    /// The window the person last brought to the front. Kept here because the agent API
    /// needs it from its own threads, and asking a form whether it has focus from anywhere
    /// but the UI thread always says no: GetFocus only sees the calling thread's queue. So
    /// "the focused window" silently meant "the first window", and a request naming no tab
    /// navigated or typed into window 0's page while the person was working in another.
    /// </summary>
    public MainForm? LastActiveWindow { get; private set; }

    /// <summary>
    /// The person's own window they were in last, never an agent's: where the screen goes
    /// back to when they hide an agent's window they brought forward. LastActiveWindow will
    /// not do, because bringing the agent's window forward made it that.
    /// </summary>
    public MainForm? LastPersonsWindow { get; private set; }

    public void NoteActive(MainForm window)
    {
        LastActiveWindow = window;
        if (!window.OpenedByAgent)
            LastPersonsWindow = window;
    }

    /// <summary>
    /// Which window to put in front when an agent's window is hidden again: the person's one
    /// used last if it can take the front, otherwise the first that can, otherwise none (the
    /// agent's window still goes to the back). Apart from any window, so a test can pin it.
    /// </summary>
    internal static T? WindowToReturnTo<T>(IReadOnlyList<T> windows, T? lastPersons, T leaving, Func<T, bool> canTakeTheFront) where T : class
        => lastPersons is not null && !ReferenceEquals(lastPersons, leaving) && windows.Contains(lastPersons) && canTakeTheFront(lastPersons)
            ? lastPersons
            : windows.FirstOrDefault(w => !ReferenceEquals(w, leaving) && canTakeTheFront(w));

    /// <summary>
    /// The window a request that names none means: the one last brought to the front, or
    /// the first when that one has gone or nothing has been brought forward yet. Also
    /// where a link opened from another app lands, which had the same always-window-0 bug.
    /// </summary>
    public MainForm? WindowInUse()
    {
        MainForm[] windows;
        lock (_windowsLock)
            windows = _windows.ToArray();
        return PickWindow(windows, LastActiveWindow);
    }

    /// <summary>
    /// Where a link from another app opens: the person's own window used last, so having
    /// just looked at an agent's window does not send their link into it (and make it
    /// theirs). The window in use when none of theirs is left.
    /// </summary>
    public MainForm? WindowForPersonsLink()
    {
        MainForm[] windows;
        lock (_windowsLock)
            windows = _windows.ToArray();
        return PickPersonsWindow(windows, LastPersonsWindow, LastActiveWindow, w => !w.OpenedByAgent);
    }

    /// <summary>
    /// The choice for <see cref="WindowForPersonsLink"/>, apart from any window, so a test can
    /// pin it: their last window, else any of theirs, else the window in use.
    /// </summary>
    internal static T? PickPersonsWindow<T>(IReadOnlyList<T> windows, T? lastPersons, T? lastActive, Func<T, bool> isTheirs) where T : class
        => lastPersons is not null && windows.Contains(lastPersons) ? lastPersons
            : windows.FirstOrDefault(isTheirs) ?? PickWindow(windows, lastActive);

    /// <summary>The choice itself, apart from any window, so a test can pin it.</summary>
    internal static T? PickWindow<T>(IReadOnlyList<T> windows, T? last) where T : class
        => last is not null && windows.Contains(last) ? last : windows.FirstOrDefault();

    /// <summary>
    /// The one live session. How a url forwarded from a second launch finds a window
    /// to open in, without the entry point having to hold a form reference that a
    /// tear-off could outlive.
    /// </summary>
    public static AppSession? Current { get; private set; }

    private AppSession(Settings settings, BrowserEnvironment env, RequestBlocker blocker, VpnTunnel vpn)
    {
        Settings = settings;
        StartedWith = settings.Clone();
        Env = env;
        Blocker = blocker;
        Vpn = vpn;
    }

    /// <summary>The tunnel must already be listening: the engine reads its proxy flag at startup.</summary>
    public static async Task<AppSession> CreateAsync(Settings settings, VpnTunnel vpn)
    {
        var blocker = new RequestBlocker(settings.BlocklistEnabled) { Relaxed = RelaxedSites.Parse(settings.RelaxedSites) };
        // A downloaded uBlock Origin Lite update takes over here, while no engine has its files open.
        if (settings.UBlockLite)
            Blocking.UBlockLite.PromotePending();
        var env = await BrowserEnvironment.CreateAsync(settings);
        return Current = new AppSession(settings, env, blocker, vpn);
    }

    private CancellationTokenSource? _uBlockStop;

    /// <summary>
    /// Keeps uBlock Origin Lite current for the life of the app (Blocking/UBlockLite.cs). UI
    /// thread; the checks run off it and hand back through <paramref name="onUi"/>: a first
    /// download is loaded into the profile at once, and what happened is said in the status
    /// line of the window in use.
    /// </summary>
    public void StartUBlockLite(Action<Action> onUi)
    {
        if (!Settings.UBlockLite || _uBlockStop is not null)
            return;
        _uBlockStop = new CancellationTokenSource();
        var stop = _uBlockStop.Token;
        UBlockLite.Updated += text => onUi(() => WindowInUse()?.ShowStatus(text));
        UBlockLite.LoadedLate += () => onUi(HandOver);
        // Soon when there is none yet, so a first run is not long without it.
        var first = UBlockLite.VersionIn(UBlockLite.CurrentFolder) is null ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(1);
        _ = Task.Run(() => UBlockLite.KeepCurrentAsync(() => onUi(LoadUBlockNow), first, stop));
    }

    /// <summary>
    /// UI thread: loads a first download into the profile through any live tab. With none
    /// (every tab asleep), the next view built loads it (UBlockLite.EnsureLoadedOnceAsync).
    /// </summary>
    private void LoadUBlockNow()
    {
        MainForm[] windows;
        lock (_windowsLock)
            windows = _windows.ToArray();
        foreach (var window in windows)
        {
            if (window.IsDisposed || window.Tabs is not { } tabs)
                continue;
            foreach (var tab in tabs.Tabs)
            {
                CoreWebView2Profile? profile = null;
                try { profile = tab.Core?.Profile; }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
                if (profile is not null)
                {
                    _ = LoadThenHandOverAsync(profile);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Loads it, then hands YouTube over to it in the tabs already open: their page cleanup
    /// script was registered before it ran, with Gergur's own YouTube code switched on, and
    /// from their next page it is the version that leaves YouTube alone. UI thread.
    /// </summary>
    private async Task LoadThenHandOverAsync(CoreWebView2Profile profile)
    {
        await UBlockLite.LoadNowAsync(profile, Settings);
        if (UBlockLite.LoadedId is not null)
            HandOver();
    }

    /// <summary>UI thread: YouTube handed over in the tabs already open, and the person told it runs.</summary>
    private void HandOver()
    {
        MainForm.ApplyLiveSettings(this);
        WindowInUse()?.ShowStatus($"uBlock Origin Lite {UBlockLite.LoadedVersion} is running; pages loaded from now on use it.");
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

    /// <summary>
    /// Why the bridge is not running, or null when it is. Kept so the window can say what
    /// went wrong: "Phone drop is not running" on its own sends you looking through
    /// settings for a problem that is one port already in use.
    /// </summary>
    public string? PhoneBridgeError { get; private set; }

    /// <summary>
    /// Brings up the phone bridge when it is switched on. Separate from StartAgent
    /// because this one listens beyond loopback: it stays off unless asked for.
    /// </summary>
    public void StartPhoneBridge()
    {
        if (PhoneBridge is not null || !Settings.DropEnabled)
            return;
        try
        {
            PhoneBridge = new DropServer(Drop, Settings);
            PhoneBridge.Start();
            PhoneBridgeError = null;
        }
        catch (Exception ex)
        {
            PhoneBridge = null;
            PhoneBridgeError = BridgeErrorFor(ex, Settings.DropPort);
            Diagnostics.DebugLog.WriteAlways($"phone bridge did not start: {ex.Message}");
        }
    }

    /// <summary>
    /// What to tell the user when the bridge did not come up. Only an actual conflict is
    /// worth sending them to the port setting: access denied and address not available
    /// are SocketExceptions too, and changing a port that is not the problem wastes an
    /// evening. Everything else says what happened and what it means here.
    /// </summary>
    internal static string BridgeErrorFor(Exception ex, int port) => ex switch
    {
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
            => $"Port {port} is already in use. Change it in Settings.",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied }
            => $"Windows would not let Gergur listen on port {port}. Something may have "
             + "reserved it, or a policy blocks it. Try a different port in Settings.",
        System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressNotAvailable }
            => "This PC has no network address to listen on. Connect to Wi-Fi and turn the phone drop on again.",
        _ => ex.Message,
    };

    public void StopPhoneBridge()
    {
        PhoneBridge?.Stop();
        PhoneBridge = null;
        // Cleared with it: otherwise turning the drop off leaves the last failure on
        // record, and the window reports a port conflict that no longer exists.
        PhoneBridgeError = null;
    }

    public void AddWindow(MainForm window)
    {
        lock (_windowsLock)
            _windows.Add(window);
    }

    /// <summary>Drops a closed window and, when it was the last one, tears the shared services down.</summary>
    public void RemoveWindow(MainForm window)
    {
        lock (_windowsLock)
            _windows.Remove(window);
        if (LastActiveWindow == window)
            LastActiveWindow = null;
        if (LastPersonsWindow == window)
            LastPersonsWindow = null;
        if (_windows.Count > 0)
            return;
        Agent?.Stop();
        Agent = null;
        StopPhoneBridge();
        Vpn.Stop();
    }

    /// <summary>
    /// Writes every open window to the session file. Called before a window drops
    /// itself from the list, so closing the last one still records what it held.
    /// </summary>
    public void SaveSession()
    {
        var session = SessionOf(_windows
            .Where(w => w.Tabs is not null)
            .Select(w => (w.OpenedByAgent, w.Tabs!.Tabs, w.Tabs.ActiveTab)));
        if (session is not null)
            SessionStore.Save(session);
    }

    /// <summary>
    /// What the session file should hold, or null to leave it as it is.
    ///
    /// An agent's own window is its workspace, not the person's browsing, so it is never
    /// part of the session. And when only agent windows are left there is nothing of the
    /// person's to write: their session was saved when their own last window closed.
    /// Writing anyway is how it was lost once. The person closed their window, an agent's
    /// window was the last one open, the agent closed its own last tab, the app exited,
    /// and the exit saved "no windows" over the tabs they had.
    /// </summary>
    internal static List<SessionWindow>? SessionOf(IEnumerable<(bool OpenedByAgent, IReadOnlyList<Tab> Tabs, Tab? Active)> windows)
    {
        var saved = new List<SessionWindow>();
        bool anyOfTheirs = false;
        foreach (var (openedByAgent, tabs, active) in windows)
        {
            if (openedByAgent)
                continue;
            anyOfTheirs = true;
            var kept = tabs.Where(t => !HomePage.IsHome(t.Url)).ToList();
            if (kept.Count == 0)
                continue;
            int activeIndex = active is null ? 0 : Math.Max(0, kept.IndexOf(active));
            // The browser's own pages by their gergur:// name, not their path into this
            // install, which a moved or rebuilt Gergur no longer has.
            saved.Add(new SessionWindow(kept.Select(t => new SessionTab(
                InternalPages.Identify(t.Url) is { } page ? InternalPages.AddressOf(page) : t.Url, t.Title)).ToList(), activeIndex));
        }
        return anyOfTheirs ? saved : null;
    }
}
