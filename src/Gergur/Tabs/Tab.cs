using System.Text.Json;
using Gergur.App;
using Gergur.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Gergur.Tabs;

/// <summary>
/// One tab. Owns either a live WebView2 control or, when discarded, only its
/// url/title/favicon. All WebView2 access funnels through this class: raw API
/// calls silently resume suspended tabs and would defeat the lifecycle policy.
/// </summary>
public sealed class Tab : ITabHandle, IDisposable
{
    private TabManager _owner; // reassigned when the tab is torn off into another window
    private WebView2? _webView;
    private Task? _ensureLiveTask;
    private bool _lowMemoryApplied;
    private bool _disposed;

    private static int _nextId;

    /// <summary>
    /// A name for this tab that does not move. An index is a position, and positions
    /// shift the moment a tab opens, closes or is torn into another window, so an agent
    /// holding one ends up acting on whatever slid into that slot: the wrong page
    /// navigated away from, typed into, or closed. This is handed out once and stays
    /// with the tab wherever it goes, for as long as the browser is running.
    /// </summary>
    public string Id { get; } = "t" + Interlocked.Increment(ref _nextId);

    public TabState State
    {
        get => _state;
        private set
        {
            var was = _state;
            _state = value;
            // Waking up: settings that changed while it slept were held back, and land
            // now. Not when it went from asleep to discarded: a rebuilt view reads the
            // current settings anyway.
            if (was == TabState.Suspended && value != TabState.Suspended && _settingsWhileAsleep is { } held)
            {
                _settingsWhileAsleep = null;
                if (value != TabState.Discarded)
                    _ = ApplySettingsLiveAsync(held);
            }
        }
    }

    private TabState _state = TabState.Discarded;

    /// <summary>
    /// Whether this tab has ever been on screen. A view created hidden has painted
    /// nothing, so capturing it returns a blank image rather than failing, which is a
    /// blank png served as a perfectly good 200. State alone does not answer this: a tab
    /// opened in the background is Hidden and has never rendered, and so is a tab that was
    /// on screen a moment ago and has plenty to show.
    /// </summary>
    public bool HasRendered { get; private set; }
    public DateTime LastActiveUtc { get; private set; } = DateTime.UtcNow;
    public string Url { get; private set; } = "about:blank";
    public string Title { get; private set; } = "New tab";
    public Image? Favicon { get; private set; }

    private readonly List<string> _recentErrors = new();
    /// <summary>Page problems (JS errors, failed loads) surfaced on this tab's current page.</summary>
    public IReadOnlyList<string> RecentErrors => _recentErrors;
    public int ErrorCount => _recentErrors.Count;

    // Injected at document creation: forwards JS errors and console.error to the host,
    // so page problems show in the chrome instead of hiding in the console.
    private const string ErrorReporterScript = """
        (function () {
            function send(prefix, m) {
                try { if (window.chrome && window.chrome.webview)
                    window.chrome.webview.postMessage(prefix + String(m).slice(0, 300)); } catch (e) {}
            }
            function post(m) { send("GERGUR_ERR:", m); }
            window.addEventListener("error", function (e) {
                var el = e.target;
                // A failed subresource surfaces here with an empty message. Hand the
                // host the url instead: most of these are requests our own blocker
                // killed, and it is the only side that can tell.
                if (el && el !== window && (el.src || el.href)) {
                    send("GERGUR_RES:", (el.tagName || "resource").toLowerCase() + " " + (el.src || el.href));
                    return;
                }
                // A cross-origin script served without CORS headers is reported
                // opaquely by the browser: no message, no file, no line. Say that,
                // rather than the bare "Script error" that explains nothing.
                post(e.message
                    ? e.message + (e.filename ? " @ " + e.filename : "")
                    : "Cross-origin script error (the browser hides the details)"); }, true);
            window.addEventListener("unhandledrejection", function (e) {
                post("Unhandled promise rejection: " + ((e.reason && e.reason.message) || e.reason || "")); });
            var oe = console.error;
            console.error = function () {
                post(Array.prototype.slice.call(arguments).map(String).join(" "));
                return oe.apply(this, arguments);
            };
        })();
        """;

    /// <summary>Raised when title/favicon/url/state changed - the strip repaints off this.</summary>
    public event EventHandler? Updated;
    /// <summary>Raised on successful navigation; used for the history log.</summary>
    public event EventHandler? PageLoaded;
    /// <summary>Accelerator keys pressed while the page has focus, forwarded for the shortcut router.</summary>
    public event KeyEventHandler? WebViewKeyDown;

    internal Tab(TabManager owner)
    {
        _owner = owner;
    }

    internal Tab(TabManager owner, TabSnapshot snapshot)
        : this(owner)
    {
        Url = snapshot.Url;
        _committedSource = snapshot.Url;
        Title = snapshot.Title;
    }

    /// <summary>
    /// The engine behind the view. UI thread only: WebView2 throws when CoreWebView2 is
    /// read from anywhere else, and the agent API's endpoints run on request threads.
    /// Use <see cref="HasView"/> there.
    /// </summary>
    internal CoreWebView2? Core => _webView?.CoreWebView2;

    /// <summary>
    /// Whether this tab has a page view, answered without touching WebView2, so it is safe
    /// from any thread.
    ///
    /// Added after the endpoints that report "that tab's page could not start" asked
    /// <see cref="Core"/> instead. That throws off the UI thread, so /open, /window and
    /// /navigate all created their tab and then answered 500. No test could see it, because
    /// a tab in a test never has a view and so never reaches WebView2; the live run did.
    /// </summary>
    public bool HasView => _webView is not null;

    public bool IsCurrent => _owner.ActiveTab == this;

    public bool IsPlayingAudio
    {
        get
        {
            try { return Core?.IsDocumentPlayingAudio ?? false; }
            catch { return false; }
        }
    }

    public bool CanGoBack
    {
        get { try { return Core?.CanGoBack ?? false; } catch { return false; } }
    }

    public bool CanGoForward
    {
        get { try { return Core?.CanGoForward ?? false; } catch { return false; } }
    }

    /// <summary>
    /// Whether a cached build should be thrown away and tried again.
    ///
    /// Caching a faulted build poisons the tab: every later read rethrows the same failure
    /// for the life of the process. Retrying it without limit is worse, because each
    /// attempt costs another engine start, so a browser whose engine cannot start at all
    /// would spend the rest of the session starting it once per request.
    ///
    /// So: retry a fault, but not more than <see cref="FailedBuildsBeforeGivingUp"/> times
    /// in a row, and not more often than <see cref="BetweenFailedBuilds"/>.
    /// </summary>
    internal static bool ShouldRebuild(int consecutiveFailures, TimeSpan sinceLastFailure)
    {
        // Only asked once there is no view and no build already on its way; those two
        // are EnsureLiveAsync's to check, and they used to be parameters here that its
        // only caller passed as constants, so the tests for them tested nothing real.
        if (consecutiveFailures == 0)
            return true;            // never tried, or the last one worked and the view went

        // Backed off rather than stopped. A runtime update or a briefly locked user data
        // folder is transient and multi-minute, and a tab that gave up for good would stay
        // dead until the browser was restarted.
        var wait = consecutiveFailures >= FailedBuildsBeforeGivingUp
            ? AfterGivingUp
            : BetweenFailedBuilds;
        return sinceLastFailure >= wait;
    }

    /// <summary>How many failed view builds in a row before a tab stops trying.</summary>
    internal const int FailedBuildsBeforeGivingUp = 3;

    /// <summary>The least time between two attempts to build a view that keeps failing.</summary>
    internal static readonly TimeSpan BetweenFailedBuilds = TimeSpan.FromSeconds(5);

    /// <summary>And once it has failed that many times, how long before trying again.</summary>
    internal static readonly TimeSpan AfterGivingUp = TimeSpan.FromMinutes(5);

    private int _failedBuilds;
    private DateTime _lastFailedBuildUtc = DateTime.MinValue;

    /// <summary>Creates and wires the WebView2 if this tab is discarded. Idempotent.</summary>
    private Task EnsureLiveAsync(bool navigateToStoredUrl = true)
    {
        // A closed tab builds nothing. A request still holding a reference to one would
        // otherwise start a whole engine view on every read, only for it to be thrown away.
        if (_disposed)
            return Task.CompletedTask;

        if (_webView is not null)
            return _ensureLiveTask ?? Task.CompletedTask;

        // One already on its way is joined, never raced. Two builds at once each assign
        // the view, the second overwrites the first, and the first is left parented and
        // running with nothing able to reach it: the leak this whole path exists to stop.
        if (_ensureLiveTask is { IsCompleted: false })
            return _ensureLiveTask;

        if (!ShouldRebuild(_failedBuilds, DateTime.UtcNow - _lastFailedBuildUtc))
        {
            // Backing off after repeated failures. The caller finds Core null and reports
            // that, rather than being handed a failure to rethrow.
            return Task.CompletedTask;
        }

        _ensureLiveTask = BuildAndCountAsync(navigateToStoredUrl, ++_buildGeneration);
        return _ensureLiveTask;
    }

    /// <summary>
    /// Which build is current. A failed build clears up after itself, and without this it
    /// could clear up after a newer one instead: take away the view a later build had just
    /// assigned, or forget that build was in flight.
    /// </summary>
    private int _buildGeneration;

    /// <summary>
    /// Builds the view, counts a failure, and never rethrows.
    ///
    /// Never, on purpose. This is awaited from ActivateAsync, which is reached from a tab
    /// click, which is an async void handler with nothing above it to catch, in an app
    /// with no unhandled exception handler at all. A view build failing while somebody
    /// clicked that tab took the whole browser down, session and all. The failure lives in
    /// the log and in a null Core, which is what every caller already checks for.
    /// </summary>
    private async Task BuildAndCountAsync(bool navigateToStoredUrl, int generation)
    {
        try
        {
            await CreateWebViewAsync(navigateToStoredUrl);
            _failedBuilds = 0;
            LastBuildFailure = null;
        }
        catch (Exception ex)
        {
            // Always written, since this is the only place the cause lands: the message the
            // person sees says the page could not start, not why. Without the url, which is
            // their browsing and only belongs in the log when they turned tracing on. The
            // back-off bounds how often this can run.
            DebugLog.WriteAlways($"view build failed: {ex.Message}");
            DebugLog.Write($"view build failed url={Url}");

            // Closed while it was building: nobody to tell, and "could not start" about a
            // tab the person has just closed would be nonsense.
            if (_disposed)
                return;

            // Only this build's mess. If a newer one has started since, the view and the
            // in-flight task belong to it.
            if (generation != _buildGeneration)
                return;

            // Whatever the failed build left goes now, not on the next attempt. A view
            // assigned before the fault is live, parented and backed by a renderer while
            // the tab still calls itself Discarded, so the lifecycle manager can never
            // reclaim it, and for a background tab nobody reads again there is no next
            // attempt to clear it up.
            DropView();
            _failedBuilds++;
            _lastFailedBuildUtc = DateTime.UtcNow;
            LastBuildFailure = ex.Message;
            RaiseBuildFailed();
        }
    }

    /// <summary>
    /// Whether a failure to start is told to the person, through the window's status bar.
    /// Off while an agent's tab is on trial: if its page cannot start it is taken away
    /// again (<see cref="TabManager.OpenOrDiscardAsync"/>), and "could not start, click it
    /// to try again" about a tab that is no longer there sent the person looking for it.
    /// </summary>
    internal bool ReportsBuildFailure { get; set; } = true;

    private void RaiseBuildFailed()
    {
        if (ReportsBuildFailure)
            ViewBuildFailed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The engine failed to build this tab's view and nothing is building one now. Not the
    /// same as having no view: a retry after an earlier failure that is still under way has
    /// no view and a failure on record, and reporting that as "could not start" had the
    /// caller start another build while this one was about to land.
    ///
    /// Plain field reads, so the agent server can ask from its own threads.
    /// </summary>
    internal bool CouldNotStart
        => _webView is null && LastBuildFailure is not null && _ensureLiveTask is not { IsCompleted: false };

    private async Task CreateWebViewAsync(bool navigateToStoredUrl)
    {
        DebugLog.Write($"CreateWebView url={Url} navigate={navigateToStoredUrl}");
        var webView = await _owner.Env.CreateWebViewAsync(_owner.Host, visible: false);

        // Closed while the engine was starting, which on a cold start is seconds. Without
        // this the view arrives after nobody is left to own it and stays parented and
        // running for the life of the app.
        if (_disposed)
        {
            try { webView.Parent?.Controls.Remove(webView); } catch { }
            webView.Dispose();
            return;
        }

        // Torn off into another window while the engine was starting. The view was
        // parented into the window this tab belonged to when the build began, and the
        // move could not take it along because it did not exist yet, so the page would
        // have been drawn in the window the tab had just left. Sideways dragging makes
        // tearing a tab off quick enough to hit this.
        if (webView.Parent != _owner.Host)
            _owner.Host.Controls.Add(webView);

        _webView = webView;
        var core = webView.CoreWebView2;

        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.SourceChanged += OnSourceChanged;
        core.HistoryChanged += OnHistoryChanged;
        core.FaviconChanged += OnFaviconChanged;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;
        core.WindowCloseRequested += OnWindowCloseRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
        core.Find.MatchCountChanged += OnFindStatusChanged;
        core.Find.ActiveMatchIndexChanged += OnFindStatusChanged;
        webView.KeyDown += OnWebViewKeyDown;

        _owner.Blocker.Attach(core);
        if (_owner.Env.Settings.PageAdCleanup && Blocking.PageCleanup.Script is { } script)
        {
            // Recorded before the await, so a settings change arriving during it sees this
            // registration and can take it away again.
            var adding = core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            _pageCleanupScript = (core, adding);
            await adding;
        }
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ErrorReporterScript);

        State = TabState.Hidden;
        if (navigateToStoredUrl && Url is not ("" or "about:blank"))
            TryNavigateCore(Url);
        RaiseUpdated();
    }

    /// <summary>
    /// Sends the tab to <paramref name="url"/> once a build that is still running finishes,
    /// on the thread that asked. Nothing happens if the build fails, the tab closes, or the
    /// tab has been pointed somewhere else by then.
    /// </summary>
    private void NavigateWhenBuilt(Task building, string url)
    {
        if (building.IsCompleted)
            return;

        // One slot, and the latest ask takes it. Each ask used to keep a continuation of its
        // own that checked the url had not moved since it was made, so the first to run
        // navigated and moved it, and every later ask then stood down: two navigations
        // during one slow build ended on the first, and the caller who asked last and was
        // told "not loaded yet" never got their page.
        bool alreadyWaiting = DeferredNavigation is not null;
        DeferredNavigation = url;
        _deferredFromUrl = Url;
        if (alreadyWaiting)
            return;

        var here = SynchronizationContext.Current is null
            ? TaskScheduler.Default
            : TaskScheduler.FromCurrentSynchronizationContext();
        _ = building.ContinueWith(
            _ =>
            {
                string? wanted = DeferredNavigation;
                DeferredNavigation = null;
                if (wanted is null || !ShouldNavigateWhenBuilt(_disposed, _webView is not null, Url, _deferredFromUrl))
                    return;
                if (TryNavigateCore(wanted))
                {
                    Url = wanted;
                    RaiseUpdated();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            here);
    }

    /// <summary>
    /// A navigation asked for while the view was still being built, which happens when the
    /// view arrives, or null when there is none waiting.
    /// </summary>
    internal string? DeferredNavigation { get; private set; }

    // What the tab's url was when that navigation was asked for. If it has moved since,
    // somebody navigated the tab themselves in the meantime, and theirs stands.
    private string _deferredFromUrl = "";

    // The page cleanup script and the engine it was registered with, so turning the
    // setting off can take it away again. Keyed by engine because a rebuilt view starts
    // with nothing registered, whatever an old one had. The id is held as the task that
    // produces it and recorded before that task is awaited: recorded after, two changes
    // inside one round trip both saw nothing registered, the script went in twice and ran
    // twice on every page, and turning it off later took away only one of the two.
    private (CoreWebView2 Core, Task<string> Id)? _pageCleanupScript;

    // Settings that changed while this tab was suspended, applied when it wakes. Nothing is
    // pushed into a suspended view, because calls into one can wake it, and a tab woken
    // for a settings change it cannot show is memory the sleep timer already won back.
    private Settings? _settingsWhileAsleep;

    /// <summary>
    /// Brings this tab's page settings in line with <paramref name="settings"/> without a
    /// rebuild. The colour scheme, tracking prevention and the autofill switches take
    /// effect at once; the page cleanup script from the next page this tab loads, since it
    /// runs as a document is created. A suspended tab takes them when it wakes.
    /// </summary>
    internal async Task ApplySettingsLiveAsync(Settings settings)
    {
        if (State == TabState.Suspended)
        {
            _settingsWhileAsleep = settings;
            return;
        }
        try
        {
            // Inside the try: the getter itself throws once the browser process is gone,
            // and this task is discarded by its caller, so nothing else would see it.
            if (Core is not { } core)
                return;
            BrowserEnvironment.ApplyViewSettings(core, settings);
            bool registered = _pageCleanupScript is { } current && current.Core == core;
            if (settings.PageAdCleanup && !registered && Blocking.PageCleanup.Script is { } script)
            {
                var adding = core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                _pageCleanupScript = (core, adding);
                await adding;
            }
            else if (!settings.PageAdCleanup && registered)
            {
                var added = _pageCleanupScript!.Value.Id;
                _pageCleanupScript = null;
                core.RemoveScriptToExecuteOnDocumentCreated(await added);
            }
        }
        catch (Exception ex)
        {
            // A renderer that died a moment ago. The next build takes the settings anyway.
            DebugLog.Write($"live settings not applied url={Url}: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether a navigation held for a slow build should go ahead now it has finished: only
    /// into a view that exists, for a tab that is still open, that nobody has since sent
    /// somewhere else.
    /// </summary>
    internal static bool ShouldNavigateWhenBuilt(bool disposed, bool hasView, string urlNow, string urlWhenAsked)
        => !disposed && hasView && urlNow == urlWhenAsked;

    /// <summary>
    /// What to tell a person whose tab's page could not start, worded from how long they
    /// actually have to wait. It said "in a few seconds" whatever the back-off was, which
    /// after three failures in a row is five minutes.
    /// </summary>
    internal static string CouldNotStartMessage(DateTime? nextAttemptUtc, DateTime nowUtc)
    {
        // Nothing retries on a timer. A retry happens when the tab is clicked, read, or its
        // window comes back, so "it will try again in" had the person waiting for
        // something that was never going to happen by itself.
        const string lead = "That tab's page could not start.";
        int seconds = SecondsUntil(nextAttemptUtc, nowUtc);
        if (seconds == 0)
            return lead + " Click it to try again.";
        if (seconds < 60)
            return $"{lead} Click it to try again after {DescribeWait(seconds)}.";
        return $"{lead} Click it to try again after {DescribeWait(seconds)}, or restart Gergur.";
    }

    /// <summary>Whole seconds until <paramref name="nextAttemptUtc"/>, and 0 when that is now or past.</summary>
    internal static int SecondsUntil(DateTime? nextAttemptUtc, DateTime nowUtc)
        => nextAttemptUtc is { } next && next > nowUtc ? (int)Math.Ceiling((next - nowUtc).TotalSeconds) : 0;

    /// <summary>
    /// "4 seconds", "1 second", "5 minutes". One wording for the status bar and the agent
    /// API alike; they were two copies and only one of them was tested.
    /// </summary>
    internal static string DescribeWait(int seconds)
    {
        if (seconds < 60)
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        int minutes = (seconds + 59) / 60;
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    /// <summary>
    /// When the next attempt to start this tab's page will be allowed, or null when one
    /// can be made now. For telling a person how long to wait, which used to say "a few
    /// seconds" even during the five minute back-off.
    /// </summary>
    public DateTime? NextBuildAttemptUtc
    {
        get
        {
            if (_failedBuilds == 0)
                return null;
            var wait = _failedBuilds >= FailedBuildsBeforeGivingUp ? AfterGivingUp : BetweenFailedBuilds;
            var next = _lastFailedBuildUtc + wait;
            return next > DateTime.UtcNow ? next : null;
        }
    }

    public async Task ActivateAsync()
    {
        if (_disposed)
            return;
        int failedBefore = _failedBuilds;
        await EnsureLiveAsync();
        if (_disposed)
            return;
        if (_webView is null)
        {
            // Clicked during the back-off after a failed build: nothing was attempted, so
            // nothing would have been said, and the blank area went unexplained. That is
            // the silent state this event was added to end, reached on the second click.
            // Only then, though: a build that failed just now, this one or one it joined,
            // has already said so, and saying it twice is noise.
            if (LastBuildFailure is not null && _failedBuilds == failedBefore)
                RaiseBuildFailed();
            return;
        }
        _webView.Visible = true; // auto-resumes a suspended page
        _webView.BringToFront();
        HasRendered = true;
        if (_lowMemoryApplied)
            SetLowMemoryTarget(false);
        State = TabState.Active;
        LastActiveUtc = DateTime.UtcNow;
        RaiseUpdated();
    }

    public void Deactivate()
    {
        if (State is TabState.Discarded)
            return;
        if (_webView is not null)
            _webView.Visible = false;
        // Hiding a suspended view does not resume it, so it stays Suspended. Calling it
        // Hidden lied about a frozen page, and now also let settings held for it while it
        // slept be pushed into the suspended engine, which can wake it for nothing: the
        // active tab of a minimised window, switched away from by an agent.
        if (State != TabState.Suspended)
            State = TabState.Hidden;
        LastActiveUtc = DateTime.UtcNow;
        RaiseUpdated();
    }

    public void FocusPage() => _webView?.Focus();

    // ------------------------------------------------------------------ zoom, audio, find

    /// <summary>Page zoom for this tab, 25% to 500%. Per tab, as in every real browser.</summary>
    public double ZoomFactor
    {
        get => _webView?.ZoomFactor ?? 1.0;
        set
        {
            if (_webView is not null)
                _webView.ZoomFactor = Math.Clamp(value, 0.25, 5.0);
        }
    }

    /// <summary>Silences a tab without pausing it. Note an audible tab is exempt from
    /// suspension whether or not it is muted: it is still doing work.</summary>
    public bool IsMuted
    {
        get { try { return Core?.IsMuted ?? false; } catch { return false; } }
        set
        {
            try
            {
                if (Core is { } core)
                {
                    core.IsMuted = value;
                    RaiseUpdated(); // the strip marks muted tabs
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// The page called window.close(). Chromium only honours that for windows opened by
    /// script, which is exactly what an OAuth sign-in popup is: the provider closes it
    /// once the handshake lands, and the tab should go with it.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>A download started on this tab; the window hands it to the shared list.</summary>
    public event EventHandler<CoreWebView2DownloadOperation>? DownloadStarted;

    /// <summary>Raised when a find pass reports new counts, so the find bar can redraw.</summary>
    public event EventHandler? FindStatusChanged;
    /// <summary>Raised when the page enters or leaves its own fullscreen, which for a
    /// video means the chrome has to get out of the way.</summary>
    public event EventHandler<bool>? FullScreenChanged;

    public int FindMatchCount { get; private set; }
    public int FindActiveMatch { get; private set; }

    /// <summary>Starts or updates a find. An empty term clears the highlights.</summary>
    public async Task FindAsync(string term, bool caseSensitive = false)
    {
        if (Core?.Find is not { } find)
            return;
        try
        {
            if (string.IsNullOrEmpty(term))
            {
                find.Stop();
                FindMatchCount = FindActiveMatch = 0;
                FindStatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            var options = _owner.Env.Core.CreateFindOptions();
            options.FindTerm = term;
            options.IsCaseSensitive = caseSensitive;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true; // our own bar draws the counts
            await find.StartAsync(options);
        }
        catch
        {
            // Find is a convenience; a page that refuses it must not break the tab.
        }
    }

    public void FindNext() { try { Core?.Find?.FindNext(); } catch { } }

    public void FindPrevious() { try { Core?.Find?.FindPrevious(); } catch { } }

    public void StopFind()
    {
        try { Core?.Find?.Stop(); } catch { }
        FindMatchCount = FindActiveMatch = 0;
    }

    /// <summary>Opens the engine's print dialog for this page.</summary>
    public void ShowPrintUi()
    {
        try { Core?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); } catch { }
    }

    /// <summary>Renders the page straight to a PDF file. True when it was written.</summary>
    public async Task<bool> PrintToPdfAsync(string path)
    {
        try { return Core is { } core && await core.PrintToPdfAsync(path, null); }
        catch { return false; }
    }

    private void OnWindowCloseRequested(object? sender, object e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Keeps the engine's own save location and behaviour, but hides its download
    /// flyout: our downloads window replaces it, and two competing lists is worse
    /// than either alone.
    /// </summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            // Handled only when something is listening to track it; otherwise the engine's
            // own flyout shows it, rather than the file downloading with nothing saying so.
            if (DownloadStarted is null)
                return;
            e.Handled = true;
            DownloadStarted.Invoke(this, e.DownloadOperation);
        }
        catch
        {
            e.Handled = false; // fall back to the engine's flyout rather than losing the file
        }
    }

    private void OnContainsFullScreenElementChanged(object? sender, object e)
        => FullScreenChanged?.Invoke(this, Core?.ContainsFullScreenElement ?? false);

    private void OnFindStatusChanged(object? sender, object e)
    {
        try
        {
            if (Core?.Find is { } find)
            {
                FindMatchCount = find.MatchCount;
                FindActiveMatch = find.ActiveMatchIndex;
            }
        }
        catch { }
        FindStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Hands this tab to another window's manager. The live WebView is reparented
    /// rather than recreated, so the page is not reloaded: scroll position, script
    /// state and half-filled forms all survive the move. Adding the control to the
    /// new host removes it from the old one.
    /// </summary>
    internal void TransferTo(TabManager newOwner)
    {
        _owner = newOwner;
        if (_webView is not null)
            newOwner.Host.Controls.Add(_webView);
    }

    /// <summary>
    /// Drops the handlers the previous window attached, so a transferred tab stops
    /// feeding the window it came from. The new owner re-wires them on adoption.
    /// </summary>
    internal void DetachOwnerHandlers()
    {
        Updated = null;
        PageLoaded = null;
        WebViewKeyDown = null;
        CloseRequested = null;
        DownloadStarted = null;
        FindStatusChanged = null;
        FullScreenChanged = null;
        ViewBuildFailed = null;
        PageRequest = null;
    }

    /// <summary>
    /// Raised when the engine could not start this tab's page.
    ///
    /// A failed build no longer throws, because the throw reached a tab click's async void
    /// handler and took the whole browser down. But swallowing it completely went too far
    /// the other way: the window was left blank with nothing anywhere saying why, and the
    /// "Gergur failed to start" dialog that used to catch this at launch could no longer
    /// see it. This is how the window finds out and says so.
    /// </summary>
    public event EventHandler? ViewBuildFailed;

    /// <summary>Why the last attempt to start this tab's page failed, or null if it did not.</summary>
    public string? LastBuildFailure { get; private set; }

    /// <summary>
    /// Points the tab at a url. False when the engine would not take it: a malformed url,
    /// or a tab with no view to navigate. Callers that only wanted the page changed can
    /// ignore that; the one that waits for the load needs it, because a navigation that
    /// never started never finishes either.
    /// </summary>
    public async Task<bool> NavigateAsync(string url)
    {
        await EnsureLiveAsync(navigateToStoredUrl: false);
        bool went = TryNavigateCore(url);
        // Only once the navigation actually went out. Recording it either way had /tabs
        // reporting a url the
        // tab was not on, and the session restore then opening that url on the next start.
        if (went)
            Url = url;
        if (State == TabState.Suspended)
            State = _webView?.Visible == true ? TabState.Active : TabState.Hidden;
        RaiseUpdated();
        return went;
    }

    /// <summary>
    /// Somebody waiting for a navigation to finish, and which navigation they meant.
    ///
    /// The id starts null and means "the next one to start", because the wait is set up
    /// before Navigate is called and the engine hands out the id after. Once stamped, only
    /// the navigation carrying that id can answer: the id survives redirects, so it stays
    /// the right one all the way to the page that actually loads.
    /// </summary>
    private sealed class LoadWaiter
    {
        public TaskCompletionSource<bool> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ulong? NavigationId { get; set; }

        /// <summary>The url this call asked for, which is how its navigation is known.</summary>
        public string Url { get; init; } = "";
    }

    private readonly List<LoadWaiter> _awaitingLoad = new();

    /// <summary>
    /// The load this tab is in the middle of, if any. Guarded by the same lock as
    /// <see cref="_awaitingLoad"/>.
    ///
    /// A fact about the tab rather than about the caller, which is what the first two
    /// versions of this got wrong. Asking "did I have to build the view?" answers a
    /// different question from "has the page arrived?", and they come apart the moment
    /// anything else builds it first: activating a parked tab, or a click in the tab
    /// strip, issues the navigation and returns, and the next read saw a live view with
    /// nothing in it yet and answered 200 with the real url, the real title and an empty
    /// page. Anyone who wants the page waits on this, whoever started it.
    /// </summary>
    private TaskCompletionSource<bool>? _pendingLoad;

    /// <summary>
    /// Which navigation the pending load belongs to, or null for one that has been issued
    /// and not yet announced by the engine.
    ///
    /// Without this the pending load was navigation blind: the first NavigationCompleted
    /// to arrive cleared it, whichever navigation it belonged to. A tab still loading A,
    /// pointed at B, answers A's abort within milliseconds, so a read that was waiting for
    /// the page got A's text with B's url beside it. That is the worst shape this can take,
    /// because nothing about the answer says it is wrong.
    /// </summary>
    private ulong? _pendingLoadNavigationId;

    /// <summary>When it started, so a load that never completes stops counting as one.</summary>
    private DateTime _pendingLoadStartedUtc;

    /// <summary>
    /// Bumped every time a load is abandoned, so a wait can tell "the view went away while
    /// I was waiting" from "it went away and a new page has since started arriving".
    ///
    /// The flag alone made that a race: whether AwaitPageAsync saw an abandonment depended
    /// on whether the rebuilt tab had already called NoteNavigationStarted, which clears
    /// it. Both answers were defensible and which one you got was down to scheduling,
    /// which is not something to leave in a wait that has been wrong four passes running.
    /// </summary>
    private int _loadGeneration;

    /// <summary>
    /// The outstanding load to wait on, or null when the page is settled.
    ///
    /// A navigation that starts and never completes, which is what a link that turns into
    /// a download looks like, would otherwise leave the tab loading for the life of the
    /// view: every later read would find something to wait for and pay for it.
    ///
    /// The cutoff is deliberately far longer than any single wait. It used to be the same
    /// fifteen seconds a read waits, so a wait that ran its course found the property had
    /// gone null in the meantime and read that as "settled": the screenshot refusal for a
    /// page that never loaded turned back into a blank png at 200, which is the thing that
    /// endpoint exists to prevent. Nothing decides whether a page arrived by asking this
    /// afterwards; the wait reports what it saw.
    /// </summary>
    internal Task<bool>? PendingLoad
    {
        get
        {
            lock (_awaitingLoad)
            {
                if (_pendingLoad is null)
                    return null;
                if (DateTime.UtcNow - _pendingLoadStartedUtc > LongestRead)
                    return null;
                return _pendingLoad.Task;
            }
        }
    }

    /// <summary>
    /// A navigation has begun. <paramref name="navigationId"/> is null when it has been
    /// issued but not yet announced, which is the gap this used to answer reads in.
    /// </summary>
    internal void NoteNavigationStarted(ulong? navigationId)
    {
        TaskCompletionSource<bool>? superseded = null;
        lock (_awaitingLoad)
        {
            if (_pendingLoad is not null && _pendingLoadNavigationId is null && navigationId is not null)
            {
                // The one we issued, now announced. Same load, and it keeps its start time
                // so a slow announcement does not buy it a fresh deadline.
                _pendingLoadNavigationId = navigationId;
                return;
            }

            if (_pendingLoad is not null && navigationId is not null
                && _pendingLoadNavigationId is { } current && current != navigationId)
            {
                // A different navigation took over. Whoever was waiting for the old one is
                // told it did not finish rather than left to time out.
                superseded = _pendingLoad;
                _pendingLoad = null;
            }

            _pendingLoad ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingLoadNavigationId = navigationId;
            _pendingLoadStartedUtc = DateTime.UtcNow;
        }
        superseded?.TrySetResult(false);
    }

    /// <summary>A navigation has finished. Only the one the tab is waiting on counts.</summary>
    internal void NoteNavigationFinished(ulong navigationId, bool success)
    {
        TaskCompletionSource<bool>? finished = null;
        lock (_awaitingLoad)
        {
            if (_pendingLoad is not null && _pendingLoadNavigationId == navigationId)
            {
                finished = _pendingLoad;
                _pendingLoad = null;
                _pendingLoadNavigationId = null;
            }
        }
        finished?.TrySetResult(success);
    }

    /// <summary>Nothing is loading any more, because there is nothing to load it.</summary>
    internal void AbandonPendingLoad()
    {
        TaskCompletionSource<bool>? finished;
        lock (_awaitingLoad)
        {
            finished = _pendingLoad;
            _pendingLoad = null;
            _pendingLoadNavigationId = null;
            _loadGeneration++;
        }
        finished?.TrySetResult(false);
    }

    /// <summary>How many times a load has been abandoned. Read either side of a wait.</summary>
    internal int LoadGeneration
    {
        get { lock (_awaitingLoad) return _loadGeneration; }
    }

    /// <summary>True while the tab has a load outstanding. For tests.</summary>
    internal bool IsLoading => PendingLoad is not null;

    /// <summary>
    /// An engine call with a deadline on it.
    ///
    /// None of WebView2's calls time out by themselves, and a page whose main thread is
    /// blocked never answers any of them. Without this a single "while (true)" in a page
    /// held an http request, its stream and its task for the life of the process, and the
    /// next read of that tab joined it.
    /// </summary>
    internal static async Task<(T Value, bool InTime)> WithBudget<T>(
        Task<T> work, TimeSpan budget, T whenLate)
    {
        if (budget <= TimeSpan.Zero)
        {
            Ignore(work);
            return (whenLate, false);
        }

        using var giveUp = new CancellationTokenSource();
        var finished = await Task.WhenAny(work, Task.Delay(budget, giveUp.Token));
        giveUp.Cancel();
        if (finished != work)
        {
            Ignore(work);
            return (whenLate, false);
        }

        // Observed either way: an engine call that faulted must not go unhandled.
        try
        {
            return (await work, true);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"engine call failed: {ex.Message}");
            return (whenLate, false);
        }
    }

    /// <summary>
    /// Watches a task nobody is waiting for any more, so a fault after the deadline does
    /// not surface as an UnobservedTaskException. The comment above claimed this was
    /// happening; it was not.
    /// </summary>
    private static void Ignore(Task work)
        => _ = work.ContinueWith(finished => { _ = finished.Exception; }, TaskScheduler.Default);

    /// <summary>What is left of a budget, never negative.</summary>
    internal static TimeSpan Remaining(DateTime deadlineUtc)
    {
        var left = deadlineUtc - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// Navigates and comes back when that page has loaded, or when the wait runs out.
    ///
    /// Without this the only way to know a page was ready was to sleep and hope, and a
    /// sleep that is too short reads as a broken page: a run here reported a video player
    /// wedged when it had simply not started yet, and that nearly went in as a finding.
    /// Returns whether the load actually completed, so "it timed out" and "it loaded" are
    /// not the same answer.
    ///
    /// It answers about the navigation it asked for and no other, and it knows which one
    /// that is by the url it asked for. Two earlier versions of this got it wrong in ways
    /// that never look like an error. Releasing on any NavigationCompleted meant that, for
    /// a tab still loading page A, asking for B aborted A, the abort completed first, and
    /// the call reported "did not load" in milliseconds while B loaded perfectly; the
    /// other way round, A finishing just after B was asked for reported "loaded", and the
    /// agent read page A convinced it was B. Claiming "the next navigation to start" then
    /// went wrong the same way: right after /open the engine has queued A but not yet
    /// raised NavigationStarting for it, so the wait for B claimed A's navigation and
    /// answered about A's abort.
    ///
    /// Matching on the url means a navigation this call did not ask for cannot answer it.
    /// The cost is that an unmatched navigation leaves the waiter to time out, which is
    /// slow but honest; a wrong answer is neither.
    /// </summary>
    public async Task<bool?> NavigateAndWaitAsync(string url, TimeSpan timeout)
    {
        // Started before the navigation, because building a view for a discarded tab can
        // take seconds and used to come out of nobody's budget: over MCP the timeout is
        // capped on the assumption that nothing precedes the wait, so a cold engine start
        // pushed the whole call past it and the answer arrived as "abandoned".
        var deadline = DateTime.UtcNow + timeout;

        // Before the waiter is registered, not after. NavigateAsync builds the view when
        // there is none, and clearing up after a failed build answers everyone waiting on
        // the tab: this call's own waiter was completed with false, the rebuild then
        // succeeded, the page arrived, and the endpoint reported loaded:false for a page
        // that was sitting right there.
        //
        // Bounded by the same deadline as everything else here. EnsureCoreWebView2Async
        // has no timeout of its own, so an unbounded wait held a plain http request for
        // good, and the caller's timeout only started counting after the build.
        var building = EnsureLiveAsync(navigateToStoredUrl: false);
        var (_, built) = await WithBudget(
            building.ContinueWith(finished => finished.IsCompleted, TaskScheduler.Default),
            Remaining(deadline),
            false);
        // Not when the build has landed since the wait gave up. Both finish on the UI
        // queue, so a build that completes after the budget's timer fires but before this
        // runs looked unfinished here, and the held navigation then saw a finished build
        // and dropped itself: the view came up blank under the old url and title.
        if (!built && !building.IsCompleted)
        {
            // Slow rather than failed: the build carries on after this call gives up. It was
            // started with nothing to navigate to, so without this the view came up on
            // about:blank while the tab still claimed its old url and title, and a later
            // /page read that as an empty page: the wrong answer this file works hardest
            // to avoid. So the navigation is kept for when the view arrives, unless
            // somebody has navigated the tab elsewhere in the meantime.
            NavigateWhenBuilt(building, url);
            return false;
        }
        if (_webView is null)
            return false;

        var waiter = new LoadWaiter { Url = url };
        lock (_awaitingLoad)
            _awaitingLoad.Add(waiter);

        try
        {
            // A url the engine will not take never raises NavigationStarting, so without
            // this the call would sit out its whole timeout, up to two minutes of a held
            // connection, to report a load that was never attempted. Null rather than
            // false, because "that url is no good" and "that page was slow" are different
            // answers and the caller can only act on the first.
            if (!await NavigateAsync(url))
                return null;

            // Cancelled as soon as the real answer arrives: an uncancelled timer and its
            // continuation stay alive for the whole timeout, which is up to two minutes
            // per call on a browser that is meant to be frugal.
            var left = Remaining(deadline);
            if (left <= TimeSpan.Zero)
                return false;

            using var giveUp = new CancellationTokenSource();
            var finished = await Task.WhenAny(waiter.Source.Task, Task.Delay(left, giveUp.Token));
            giveUp.Cancel();
            return finished == waiter.Source.Task && waiter.Source.Task.Result;
        }
        finally
        {
            lock (_awaitingLoad)
                _awaitingLoad.Remove(waiter);
        }
    }

    /// <summary>
    /// Hands the navigation that just started to the one call that asked for that url.
    ///
    /// The first unclaimed match only, so two waits on the same tab do not both claim the
    /// first navigation and then both answer about it. Oldest first, which pairs them in
    /// the order they were asked for when two calls want the same page.
    /// </summary>
    private void StampLoadWaiters(ulong navigationId, string uri)
    {
        lock (_awaitingLoad)
        {
            int at = ClaimIndex(_awaitingLoad.Select(w => (w.NavigationId, w.Url)).ToList(), uri);
            if (at >= 0)
                _awaitingLoad[at].NavigationId = navigationId;
        }
    }

    /// <summary>
    /// Which waiter a starting navigation belongs to, or -1 for nobody.
    ///
    /// Pulled out as a function over plain values because this is where both earlier
    /// versions of this bug lived, and neither could be tested while it was tangled up in
    /// a live WebView2. The rule: the first waiter, in the order the calls arrived, that
    /// asked for this url and has not already been given a navigation.
    /// </summary>
    internal static int ClaimIndex(IReadOnlyList<(ulong? NavigationId, string Url)> waiters, string startedUri)
    {
        for (int i = 0; i < waiters.Count; i++)
        {
            if (waiters[i].NavigationId is null && SameTarget(waiters[i].Url, startedUri))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Whether a navigation that started is the one somebody asked for. The engine hands
    /// back a normalised url, so "https://example.com" comes back as "https://example.com/"
    /// and a plain string comparison would miss its own navigation on nearly every call.
    /// </summary>
    internal static bool SameTarget(string wanted, string started)
    {
        if (string.Equals(wanted, started, StringComparison.Ordinal))
            return true;
        if (!Uri.TryCreate(wanted, UriKind.Absolute, out var a)
            || !Uri.TryCreate(started, UriKind.Absolute, out var b))
            return false;

        // Compared piece by piece rather than through AbsoluteUri, which keeps the unicode
        // host while the engine always reports punycode: navigating to "https://bucher.de"
        // with an umlaut announced itself as xn--, matched nothing, and the wait sat out
        // its whole timeout to report a page that had loaded perfectly.
        //
        // Scheme and host are case insensitive; the path is not, on most servers.
        // A file url has no host, and Windows paths are not case sensitive, so the
        // drive letter alone would otherwise decide that a page is a different page.
        var comparison = a.IsFile ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port
            && string.Equals(a.PathAndQuery, b.PathAndQuery, comparison)
            // A trailing "#" comes back as the fragment "#" from one side and "" from
            // the other, and they are the same page.
            && string.Equals(a.Fragment.TrimEnd('#'), b.Fragment.TrimEnd('#'), StringComparison.Ordinal);
    }

    /// <summary>Answers only the people who were waiting for this navigation.</summary>
    private void ReleaseLoadWaiters(ulong navigationId, bool success)
    {
        LoadWaiter[] theirs;
        lock (_awaitingLoad)
        {
            theirs = _awaitingLoad.Where(w => w.NavigationId == navigationId).ToArray();
            foreach (var waiter in theirs)
                _awaitingLoad.Remove(waiter);
        }
        foreach (var waiter in theirs)
            waiter.Source.TrySetResult(success);
    }

    /// <summary>
    /// Nothing is going to load now, so say so rather than leaving every waiter to burn
    /// its full timeout. For a tab being discarded, closed, or whose renderer died.
    /// </summary>
    private void AbandonLoadWaiters()
    {
        LoadWaiter[] waiting;
        lock (_awaitingLoad)
        {
            waiting = _awaitingLoad.ToArray();
            _awaitingLoad.Clear();
        }
        foreach (var waiter in waiting)
            waiter.Source.TrySetResult(false);
    }

    /// <summary>Whether the navigation actually went out to the engine.</summary>
    private bool TryNavigateCore(string url)
    {
        var core = Core;
        if (core is null)
            return false;
        try
        {
            core.Navigate(url);
            NoteNavigationStarted(null);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // malformed url: leave the page as it is
        }
    }

    /// <summary>The least time a round of the page wait can take, so it cannot spin.</summary>
    internal static readonly TimeSpan RoundFloor = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// How many rounds the last page wait took. Counted because wall time alone does not
    /// pin the floor: a wait that spins 1.6 million times still finishes inside its
    /// deadline, and the 227 MB of timers it allocates on the way is what the floor is
    /// there to prevent.
    /// </summary>
    internal int LastWaitRounds { get; private set; }

    /// <summary>The longest a read will wait for an outstanding page load.</summary>
    internal static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The longest a single read can hold a tab awake.
    ///
    /// The engine calls a read makes have no timeout of their own: a page whose main
    /// thread is wedged never resolves ExecuteScriptAsync, so without this the counter
    /// below would never come back down and that tab would be pinned awake for the life
    /// of the process. In a browser whose whole point is being frugal, a fix for a flaky
    /// evaluation must not cost a permanently live renderer.
    /// </summary>
    private static readonly TimeSpan LongestRead = TimeSpan.FromMinutes(2);

    private int _reading;
    private long _readingSinceTicks;

    internal void BeginRead()
    {
        Interlocked.Increment(ref _reading);
        // Every read, not just the first. Stamping only on the transition meant that once
        // one read had aged out, every later read on that tab was unprotected: a wedged
        // /page holds the counter above zero for good, so a long /eval started afterwards
        // was frozen underneath itself, which is the exact failure the counter is for.
        Interlocked.Exchange(ref _readingSinceTicks, DateTime.UtcNow.Ticks);
    }

    internal void EndRead()
    {
        if (Interlocked.Decrement(ref _reading) <= 0)
            Interlocked.Exchange(ref _readingSinceTicks, 0);
        // Stamped at the end as well as the start: the tab was in use for the whole call,
        // and a long evaluation must not look idle from the moment it began.
        LastActiveUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// True while something is reading this tab and it must not be frozen. Ages out, so a
    /// read that can never finish stops holding the tab open.
    /// </summary>
    internal bool IsBeingRead
    {
        get
        {
            if (Volatile.Read(ref _reading) <= 0)
                return false;
            long since = Interlocked.Read(ref _readingSinceTicks);
            return since != 0 && DateTime.UtcNow.Ticks - since < LongestRead.Ticks;
        }
    }

    /// <summary>
    /// Waits for whatever page is on its way, touching no view at all.
    ///
    /// Split out from the wake so it can be exercised on a bare Tab. This is the third
    /// consecutive review to find a defect in this wait, every one of them a wrong answer
    /// rather than a crash, and the project's own precedent for that is ClaimIndex: pull
    /// the decision out to where a test can reach it.
    ///
    /// True means a page settled. False means the wait ran out, or the view went away
    /// underneath it. A navigation that failed still counts as settled, because the engine
    /// paints an error page and that is a real frame to read or photograph.
    /// </summary>
    internal async Task<bool> AwaitPageAsync(DateTime deadlineUtc)
    {
        // Sampled before the loop even looks for a load. Reading it after meant an
        // abandonment landing between the two reads bumped the counter before it was
        // taken, so the comparison saw no change, the loop rounded, found nothing pending
        // and reported a page for a view that had gone: the original bug with a narrower
        // window. Both fields are lock guarded because cross thread use is expected here.
        int generation = LoadGeneration;
        LastWaitRounds = 0;

        while (PendingLoad is { } loading)
        {
            LastWaitRounds++;
            var left = Remaining(deadlineUtc);
            if (left <= TimeSpan.Zero)
                return false;

            using var giveUp = new CancellationTokenSource();
            var finished = await Task.WhenAny(loading, Task.Delay(left, giveUp.Token));
            giveUp.Cancel();
            if (finished != loading)
                return false;

            // The task completes for three different reasons and only one of them is
            // "no page is coming": the view went away underneath this wait.
            if (LoadGeneration != generation)
                return false;

            // Otherwise it settled. Round again, because a navigation that superseded
            // this one will have installed a new load, and ending here would refuse a
            // tab that was a moment from being perfectly fine.
            //
            // With a floor under each round, because a page can supersede its own
            // navigation as fast as the engine will take them: measured at 1.6 million
            // rounds and 227 MB of timers in 300ms against a synthetic churn. Real pages
            // cannot go that fast, but a loop whose cost depends on how fast something
            // else misbehaves is not one to leave unbounded.
            var settle = Remaining(deadlineUtc);
            if (settle > TimeSpan.Zero)
                await Task.Delay(settle < RoundFloor ? settle : RoundFloor);
        }
        return true;
    }

    /// <summary>
    /// Wakes the tab and waits for whatever page is on its way, without reading anything.
    ///
    /// For a caller that needs the page there before it does its own thing, which for a
    /// screenshot means before the engine is asked to photograph it. False when the wait
    /// ran out and the page is still not settled, so an empty frame is reported rather
    /// than served.
    /// </summary>
    public async Task<bool> WaitForPageAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        BeginRead();
        try
        {
            // What the wait saw, not what a property says afterwards.
            return await WakeAsync(deadline);
        }
        finally
        {
            EndRead();
        }
    }

    /// <summary>
    /// Brings a parked tab back far enough to be read from, and waits for its page, all
    /// inside <paramref name="deadlineUtc"/>.
    ///
    /// The deadline is the caller's whole budget, not another one on top of it. Waiting
    /// up to fifteen seconds here and then letting the caller wait its own fifteen meant
    /// a screenshot of a wedged page took thirty, which is past the twenty an MCP tool
    /// call is given, so the honest refusal turned into "the tool call was abandoned".
    /// A wake has to come out of the budget rather than be added to it.
    ///
    /// Two different sleeps. A suspended tab still has its page and only needs resuming.
    /// A discarded tab has no view at all, and building one issues the navigation without
    /// waiting for it. Either way what a reader needs is the page, so this waits on the
    /// tab's outstanding load rather than on anything about how it got there.
    /// </summary>
    private async Task<bool> WakeAsync(DateTime deadlineUtc)
    {
        try
        {
            // Bounded like every other engine call. EnsureCoreWebView2Async has no timeout
            // of its own, so a hang there held a plain http request, its stream and its
            // task for the life of the process, and the deadline this method was given
            // meant nothing.
            var (_, inTime) = await WithBudget(
                EnsureLiveAsync().ContinueWith(finished => finished.IsCompleted, TaskScheduler.Default),
                Remaining(deadlineUtc),
                false);
            // The build no longer reports failure by faulting, because that task is
            // awaited from a tab click and there is nothing above it to catch. A view or
            // no view is the answer.
            if (!inTime || _webView is null)
                return false;

            if (State == TabState.Suspended)
            {
                try
                {
                    Core?.Resume();
                    State = _webView?.Visible == true ? TabState.Active : TabState.Hidden;
                    RaiseUpdated();
                }
                catch (Exception ex)
                {
                    // Logged rather than swallowed: a view that will not resume hands the
                    // caller a frozen frame, and a stale frame is not noticed anywhere.
                    DebugLog.Write($"Resume failed url={Url}: {ex.Message}");
                }
            }

            // Whoever started it. Nothing to wait for when the page is settled, or when
            // the navigation was never issued because the engine would not take the url.
            return await AwaitPageAsync(deadlineUtc);
        }
        finally
        {
            LastActiveUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Runs JS in the page (agent API). Wakes the tab if it was parked.</summary>
    public async Task<string> ExecuteScriptAsync(string js)
        => (await ReadScriptAsync(js, WakeTimeout)).Result;

    /// <summary>
    /// Whether there is anything worth reading: a view to ask, and a page that arrived.
    ///
    /// The same two terms as <see cref="WorthCapturing"/> and pulled out for the same
    /// reason: the second one cannot be reached from a test through the method itself,
    /// because a tab with no view answers at the first, and a mutation that dropped it
    /// left every test green.
    /// </summary>
    internal static bool Readable(bool hasView, bool pageArrived) => hasView && pageArrived;

    /// <summary>
    /// Runs script and says whether the tab actually had a page to run it against.
    ///
    /// Ready comes back rather than being dropped, which is what /page and /html did with
    /// it: a tab still loading after fifteen seconds answered 200 with the real url, the
    /// real title and empty text, and nothing in that says it is wrong. CLAUDE.md points
    /// at /page as the read that disturbs nothing, so it is the one most likely to be
    /// believed.
    /// </summary>
    public async Task<(string Result, bool Ready)> ReadScriptAsync(string js, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        BeginRead();
        try
        {
            bool ready = await WakeAsync(deadline);
            var core = Core;
            if (!Readable(core is not null, ready))
                return ("null", false);
            RefuseOwnPage();

            var (result, inTime) = await WithBudget(core!.ExecuteScriptAsync(js), Remaining(deadline), "null");
            return (result ?? "null", inTime);
        }
        finally
        {
            EndRead();
        }
    }

    /// <summary>
    /// Answers a waiting evaluation with a failure, if it is still waiting.
    ///
    /// For the fire and forget call that starts a script: when that fails asynchronously
    /// the caller used to sit out the whole timeout and then be told the script "did not
    /// settle", rather than what actually went wrong.
    /// </summary>
    internal bool FailAwaited(string token, string why)
    {
        TaskCompletionSource<string>? pending;
        lock (_awaitedScripts)
        {
            if (!_awaitedScripts.Remove(token, out pending))
                return false;
        }
        return pending.TrySetResult(Failed(why));
    }

    /// <summary>Registers a waiting evaluation. For tests; the real one is made inline.</summary>
    internal void AwaitScript(string token, TaskCompletionSource<string> waiting)
    {
        lock (_awaitedScripts)
            _awaitedScripts[token] = waiting;
    }

    /// <summary>Scripts whose result is still being waited for, by the token handed to the page.</summary>
    private readonly Dictionary<string, TaskCompletionSource<string>> _awaitedScripts = new();

    /// <summary>
    /// What the probe answers when the source parsed as an expression. One constant, so
    /// that changing the spelling in the script and forgetting the comparison cannot
    /// leave every evaluation quietly taking the non-awaiting path with every test green.
    /// </summary>
    internal const string ExpressionMarker = "GERGUR_EXPR";

    /// <summary>The marker as ExecuteScriptAsync hands it back: JSON, so quoted.</summary>
    internal const string ExpressionMarkerJson = "\"" + ExpressionMarker + "\"";

    private void CompleteAwaitedScript(string payload)
    {
        int split = payload.IndexOf(':');
        if (split <= 0)
            return;
        string token = payload[..split];
        string json = payload[(split + 1)..];
        TaskCompletionSource<string>? waiting;
        lock (_awaitedScripts)
        {
            if (!_awaitedScripts.Remove(token, out waiting))
                return;
        }

        // This string goes straight out as an application/json body, and it came from the
        // page. The wrapper builds it with JSON.stringify, but a page can replace
        // JSON.stringify, and a page that hooks postMessage can read the token out of the
        // wrapper's own call and answer in its place. Parsing it here is what keeps a
        // 200 application/json from carrying something that is not json.
        if (!IsEnvelope(json))
        {
            waiting.TrySetResult(Failed("the page returned an unreadable result"));
            return;
        }
        waiting.TrySetResult(json);
    }

    /// <summary>
    /// Whether the page's reply is the shape this promises its callers, rather than just
    /// valid json. A page that hooks postMessage can answer anything at all, and "123" is
    /// perfectly good json that no caller reading {ok, result} can use.
    /// </summary>
    internal static bool IsEnvelope(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tells everyone still waiting on a script that no answer is coming. For a view that
    /// is going away: without it each one sits out its whole timeout for nothing.
    /// </summary>
    private void AbandonAwaitedScripts(string why)
    {
        TaskCompletionSource<string>[] waiting;
        lock (_awaitedScripts)
        {
            waiting = _awaitedScripts.Values.ToArray();
            _awaitedScripts.Clear();
        }
        foreach (var one in waiting)
            one.TrySetResult(Failed(why));
    }

    /// <summary>
    /// A script that parses <paramref name="js"/> as an expression without running it:
    /// the source goes inside a function nothing calls, so either the whole script fails
    /// to parse or it did not need the other path. Answers <see cref="ExpressionMarker"/>
    /// when it parsed.
    ///
    /// The source gets a line to itself here and in the wrapper, because an expression
    /// ending in a // comment would otherwise swallow the tokens that close the function
    /// it was pasted into, and a perfectly good expression would look like statements.
    ///
    /// A well formed expression cannot run here. A deliberately unbalanced one can: close
    /// the brackets yourself and the rest of the source lands in the probe's own body,
    /// where it runs, and then runs again in the wrapper. That is not an escalation, since
    /// anyone calling this already has arbitrary JavaScript in the page, but it is why
    /// this says "a well formed expression" rather than "nothing".
    /// </summary>
    internal static string ExpressionProbe(string js)
        => "(function () { function unused() { return (\n"
            + js
            + "\n); } return " + ExpressionMarkerJson + "; })()";

    /// <summary>
    /// The expression, awaited in the page, posting its result back on the channel the
    /// error reporter already uses. Shaped so a rejected promise and a thrown error read
    /// the same way rather than one of them disappearing.
    /// </summary>
    internal static string AwaitingWrapper(string js, string token) => $$"""
        (function () {
            var token = {{JsonSerializer.Serialize(token)}};
            function reply(payload) {
                try {
                    window.chrome.webview.postMessage("GERGUR_EVAL:" + token + ":" + JSON.stringify(payload));
                } catch (e) {
                    try {
                        window.chrome.webview.postMessage(
                            "GERGUR_EVAL:" + token + ":{\"ok\":false,\"error\":\"the result could not be serialised\"}");
                    } catch (ignored) { }
                }
            }
            try {
                Promise.resolve((function () { return (
        {{js}}
                ); })()).then(
                    function (value) { reply({ ok: true, result: value === undefined ? null : value }); },
                    function (error) { reply({ ok: false, error: String(error) }); });
            } catch (error) {
                reply({ ok: false, error: String(error) });
            }
        })()
        """;

    /// <summary>
    /// Runs script and waits for its result, a promise included.
    ///
    /// ExecuteScriptAsync hands back the JSON of the expression, and the JSON of a promise
    /// is "{}". Anything asynchronous therefore came back as an empty object, and the way
    /// round it was to park the answer on a global and poll for it from outside, which is
    /// a lot of scaffolding to write again every time. So the script is wrapped, awaited
    /// in the page, and the result posted back on the channel the error reporter already
    /// uses.
    ///
    /// Only an expression can be wrapped that way. A script of several statements is a
    /// syntax error inside "return (...)", and ExecuteScriptAsync has always accepted one
    /// and answered with its completion value, so those are run as before rather than
    /// broken in the name of awaiting them. They get the same shape and the same error
    /// channel, which they did not have at first: the plain ExecuteScriptAsync reports a
    /// thrown error and the value null identically, so a TypeError came back as
    /// {"ok": true, "result": null} and read as a page that simply had nothing to say.
    ///
    /// Which of the two a script is gets decided by parsing it and not running it, so a
    /// script that turns out to need the other path has had no chance to do anything
    /// twice. Guessing from the text would be quicker and would eventually run somebody's
    /// fetch twice.
    ///
    /// The shape is always {"ok": true, "result": ...} or {"ok": false, "error": "..."},
    /// so a rejected promise and a thrown error read the same way rather than one of them
    /// disappearing, and "result" is still the name it had.
    /// </summary>
    public async Task<string> ExecuteScriptAwaitingAsync(string js, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        BeginRead();
        try
        {
            await WakeAsync(deadline);

            // What is left of the caller's budget, not a second one: the wake and the
            // settle used to be a timeout each, so /eval could take 33 seconds against
            // an 18 second cap. And if the wake spent all of it, say so rather than
            // running the script and then reporting that it "did not settle within 0s",
            // which is both untrue and hides that the side effects already happened.
            var left = Remaining(deadline);
            if (left <= TimeSpan.Zero)
                return Failed("the tab did not finish waking within the time allowed");
            return await EvaluateAsync(js, left);
        }
        finally
        {
            EndRead();
        }
    }

    private async Task<string> EvaluateAsync(string js, TimeSpan timeout)
    {
        if (Core is null)
            return Failed("the tab has no view");
        // Before the probe, which runs the agent's source too. Outside every try below, so the
        // refusal reaches the agent server as a 403 rather than a quiet "could not run that".
        RefuseOwnPage();

        var deadline = DateTime.UtcNow + timeout;

        string parsed;
        try
        {
            // Bounded: a page whose main thread is blocked never answers this, and the
            // timeout further down was never reached because this is awaited first. One
            // "while (true)" held the request for the life of the process, and so did
            // every later read of that tab.
            var (probed, inTime) = await WithBudget(
                Core.ExecuteScriptAsync(ExpressionProbe(js)), Remaining(deadline), "");
            if (!inTime)
                return Failed($"the page did not answer within {timeout.TotalSeconds:0.#}s");
            parsed = probed;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"eval probe failed url={Url}: {ex}");
            return Failed("the page could not be asked to run that");
        }

        if (parsed != ExpressionMarkerJson)
        {
            // Statements, or something the page would not parse either way. Run it as it
            // has always been run, but with the result form that can tell a thrown error
            // from the value null: the plain ExecuteScriptAsync reports both as "null",
            // so a TypeError came back as {"ok": true, "result": null} and an agent read
            // a broken script as a page that answered nothing.
            var core = Core;
            if (core is null)
                return Failed("the tab was closed while the script was running");
            // Again: the probe's await was long enough for the tab to arrive at one.
            RefuseOwnPage();
            try
            {
                var (outcome, inTime) = await WithBudget(
                    core.ExecuteScriptWithResultAsync(js), Remaining(deadline), null!);
                if (!inTime || outcome is null)
                    return Failed($"the page did not answer within {timeout.TotalSeconds:0.#}s");
                if (!outcome.Succeeded)
                    // The one exception message worth returning: it is the page's own
                    // error text, which is the point of asking.
                    return Failed(outcome.Exception?.Message ?? "the script did not run");
                return Succeeded(outcome.ResultAsJson);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"eval failed url={Url}: {ex}");
                return Failed("the page could not be asked to run that");
            }
        }

        // And here for the expression path, after the probe's await and before anything is
        // registered, so a refusal leaves nothing waiting behind it.
        RefuseOwnPage();
        string token = Guid.NewGuid().ToString("n")[..12];
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_awaitedScripts)
            _awaitedScripts[token] = waiting;

        try
        {
            var live = Core;
            if (live is null)
                throw new InvalidOperationException("the tab was closed while the script was running");

            // Deliberately not awaited. The wrapper runs the expression synchronously, so
            // a script that blocks the renderer never returns from this call and the
            // timeout below was never reached: over plain http the request was held open
            // for good. The answer comes back by postMessage, so the wait is the only
            // thing that needs to happen here.
            _ = live.ExecuteScriptAsync(AwaitingWrapper(js, token)).ContinueWith(
                run =>
                {
                    if (run.Exception is not { } failed)
                        return;
                    DebugLog.Write($"eval wrapper failed url={Url}: {failed.GetBaseException().Message}");
                    // Answered rather than left to time out: without this a wrapper that
                    // failed asynchronously reported "did not settle" fifteen seconds
                    // later instead of saying what went wrong.
                    FailAwaited(token, "the page could not be asked to run that");
                },
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Abandon(token);
            DebugLog.Write($"eval could not start url={Url}: {ex}");
            return Failed("the page could not be asked to run that");
        }

        // What is left of the budget, not the whole of it again. The probe above can spend
        // most of it on a slow page, and handing the settle a fresh full timeout meant a
        // call capped at eighteen seconds could run for thirty five, then report a number
        // that understated the wait by half.
        var settleIn = Remaining(deadline);

        // Cancelled the moment the page answers, so the timer and its continuation do not
        // outlive the call by up to two minutes.
        using var giveUp = new CancellationTokenSource();
        var finished = await Task.WhenAny(waiting.Task, Task.Delay(settleIn, giveUp.Token));
        giveUp.Cancel();
        if (finished != waiting.Task)
        {
            Abandon(token);
            return Failed($"the script did not settle within {timeout.TotalSeconds:0.#}s");
        }
        return waiting.Task.Result;

        void Abandon(string abandoned)
        {
            lock (_awaitedScripts)
                _awaitedScripts.Remove(abandoned);
        }
    }

    /// <summary>
    /// The one shape /eval answers in. Built in four places before this, which is four
    /// chances for one of them to drift into a different set of keys.
    /// </summary>
    internal static string Succeeded(string resultAsJson)
        => "{\"ok\":true,\"result\":" + (string.IsNullOrEmpty(resultAsJson) ? "null" : resultAsJson) + "}";

    /// <summary>The same shape, for everything that went wrong.</summary>
    internal static string Failed(string why)
        => JsonSerializer.Serialize(new { ok = false, error = why });

    /// <summary>
    /// Whether there is anything worth photographing: a view to ask, and a page that
    /// actually arrived.
    ///
    /// Two terms and an and, pulled out because the second one cannot be reached from a
    /// test otherwise: a tab with no view answers at the first term, and giving one a view
    /// needs a real engine. It was dropped once already, and a mutation that dropped it
    /// again left every test green, which is the whole reason this is a function.
    /// </summary>
    internal static bool WorthCapturing(bool hasView, bool pageArrived) => hasView && pageArrived;

    /// <summary>
    /// PNG of the rendered page, within a budget shared with whatever waited before it,
    /// and whether there was a page to photograph.
    ///
    /// Ready comes back rather than being thrown away, because the wake in here is the
    /// last thing between the caller and the shutter: a renderer that died during the
    /// preceding wait gets its view rebuilt here, the rebuild does not finish inside the
    /// budget, and photographing anyway produced a blank png served as a perfectly good
    /// 200, which is the one outcome this whole path exists to avoid.
    /// </summary>
    public async Task<(byte[] Png, bool Ready)> CaptureScreenshotAsync(TimeSpan budget)
    {
        if (budget < TimeSpan.Zero)
            budget = TimeSpan.Zero;

        // Once, at the top. Recomputing it after the wake gave the capture a fresh full
        // budget instead of the remainder, because Remaining(UtcNow + budget) is just
        // budget again: the wake could burn fifteen seconds rebuilding a view and the
        // capture would then be handed fifteen more, which is the "the tool call was
        // abandoned" regression this same request already has on record as fixed.
        var deadline = DateTime.UtcNow + budget;

        BeginRead();
        try
        {
            bool ready = await WakeAsync(deadline);
            var core = Core;
            if (!WorthCapturing(core is not null, ready))
                return ([], false);
            RefuseOwnPage();

            var (png, inTime) = await WithBudget(
                CaptureAsync(core!), Remaining(deadline), Array.Empty<byte>());
            return (png, inTime);
        }
        finally
        {
            EndRead();
        }
    }

    private static async Task<byte[]> CaptureAsync(CoreWebView2 core)
    {
        using var stream = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        return stream.ToArray();
    }

    public void GoBack() { try { Core?.GoBack(); } catch { } }
    public void GoForward() { try { Core?.GoForward(); } catch { } }
    public void Reload() { try { Core?.Reload(); } catch { } }
    public void Stop() { try { Core?.Stop(); } catch { } }
    public void OpenDevTools() { try { Core?.OpenDevToolsWindow(); } catch { } }
    public void OpenTaskManager() { try { Core?.OpenTaskManagerWindow(); } catch { } }

    // --- ITabHandle (called only by TabLifecycleManager) ---

    public async Task<bool> TrySuspendAsync()
    {
        var core = Core;
        if (State != TabState.Hidden || core is null || _webView is null || _webView.Visible)
            return false;
        // Not while something is reading it. Freezing a renderer underneath a running
        // evaluation is how a script that was about to settle never settles.
        if (IsBeingRead)
            return false;
        try
        {
            if (_lowMemoryApplied)
                SetLowMemoryTarget(false); // docs: don't mix manual Low with suspension
            bool suspended = await core.TrySuspendAsync();
            if (suspended && IsBeingRead)
            {
                // Checked again: a read that arrived during the await would otherwise
                // have its renderer frozen underneath it, which is the intermittent
                // failure the counter exists to stop.
                try { core.Resume(); } catch { }
                return false;
            }
            if (suspended && State == TabState.Hidden) // state may have changed across the await
            {
                State = TabState.Suspended;
                RaiseUpdated();
            }
            return suspended;
        }
        catch
        {
            return false;
        }
    }

    public void Discard()
    {
        if (_webView is null)
            return;
        bool wasActive = State == TabState.Active;
        DetachAndDisposeWebView();
        State = TabState.Discarded;
        RaiseUpdated();
        if (wasActive)
            _ = _owner.ReactivateAsync(this); // crash path: bring it straight back
    }

    public void SetLowMemoryTarget(bool low)
    {
        try
        {
            var core = Core;
            if (core is null)
                return;
            core.MemoryUsageTargetLevel = low
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
            _lowMemoryApplied = low;
        }
        catch { }
    }

    // --- WebView2 event handlers ---

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        var title = Core?.DocumentTitle;
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title;
            RaiseUpdated();
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        Url = Core?.Source ?? Url;
        _committedSource = Url;
        RaiseUpdated();
    }

    private void OnHistoryChanged(object? sender, object e) => RaiseUpdated();

    private async void OnFaviconChanged(object? sender, object e)
    {
        try
        {
            var core = Core;
            if (core is null)
                return;
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null || stream.Length == 0)
                return;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;
            using var image = Image.FromStream(buffer);
            var copy = new Bitmap(image); // detach from the stream's lifetime
            Favicon?.Dispose();
            Favicon = copy;
            RaiseUpdated();
        }
        catch
        {
            // Favicons are cosmetic; never let them take a tab down.
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.IsUserInitiated || !e.IsRedirected)
            _recentErrors.Clear(); // fresh page, fresh slate for the error indicator
        // Redirects included: the same navigation, now heading somewhere else.
        NoteNavigationUnderWay(e.NavigationId, e.Uri);

        // Hand this navigation to whoever asked for this url. Redirects are skipped: the
        // engine keeps the same NavigationId across one, so re-stamping would be a no-op
        // anyway, and skipping stops a wait registered midway through a redirect chain
        // from claiming a navigation it did not ask for.
        if (!e.IsRedirected)
        {
            NoteNavigationStarted(e.NavigationId);
            StampLoadWaiters(e.NavigationId, e.Uri);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            PageLoaded?.Invoke(this, EventArgs.Empty);
        }
        else if (IsFinishedOAuthCallback(Url, e.WebErrorStatus))
        {
            ShowSignInComplete();
        }
        else if (e.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled
                 or CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired))
        {
            AddError($"Page failed to load: {e.WebErrorStatus}");
        }
        // Whatever the outcome, this navigation is over: anyone who was waiting for this
        // one gets the answer rather than the full timeout, and gets to know which answer
        // it was. Anyone waiting for a different navigation is left waiting for it.
        ReleaseLoadWaiters(e.NavigationId, e.IsSuccess);
        NoteNavigationFinished(e.NavigationId, e.IsSuccess);
        NoteNavigationOver(e.NavigationId);
        RaiseUpdated();
    }

    /// <summary>
    /// A sign-in that already succeeded, not a failure. Tools like "gh auth login" run a
    /// one-shot listener on a loopback port, take the code the provider redirects back
    /// with, and shut down immediately. The browser's follow-up request then finds
    /// nothing, which surfaces as ConnectionReset on a blank page and reads like the
    /// login broke when it did not.
    /// </summary>
    internal static bool IsFinishedOAuthCallback(string url, CoreWebView2WebErrorStatus status)
    {
        if (status is not (CoreWebView2WebErrorStatus.ConnectionReset
            or CoreWebView2WebErrorStatus.ConnectionAborted
            or CoreWebView2WebErrorStatus.CannotConnect
            or CoreWebView2WebErrorStatus.ServerUnreachable))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            return false;
        return HasAuthorizationResult(uri.Query);
    }

    /// <summary>
    /// True only for a query carrying an actual authorization result: an exact "code" or
    /// "access_token" parameter with a value, and no "error".
    ///
    /// Substring matching was wrong and dangerous. A denial redirect carries
    /// "error=access_denied&amp;error_code=200051", and "error_code=" contains "code=", so a
    /// refused sign-in rendered as "Sign-in complete" and hid the real failure.
    /// </summary>
    internal static bool HasAuthorizationResult(string query)
    {
        bool found = false;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            string value = equals < 0 ? "" : pair[(equals + 1)..];

            // An explicit error wins outright, wherever it appears in the query.
            if (key.Equals("error", StringComparison.OrdinalIgnoreCase))
                return false;
            if (value.Length > 0
                && (key.Equals("code", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("access_token", StringComparison.OrdinalIgnoreCase)))
                found = true;
        }
        return found;
    }

    private const string SignInCompleteHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Sign-in complete</title>
        <style>
          html,body{margin:0;height:100%;background:#0a0406;color:#f8eced;
            font:400 16px/1.6 "Segoe UI",system-ui,sans-serif}
          body{display:grid;place-items:center;text-align:center}
          .card{max-width:30rem;padding:2rem}
          .tick{width:64px;height:64px;border-radius:50%;background:#fa3d4d;margin:0 auto 1.5rem;
            display:grid;place-items:center;font-size:32px;color:#fff}
          h1{font-size:1.5rem;margin:0 0 .5rem;font-weight:600}
          p{margin:0;color:#ac8c94}
        </style></head>
        <body><div class="card">
          <div class="tick">&#10003;</div>
          <h1>Sign-in complete</h1>
          <p>The app that asked for this has your authorization. You can close this tab.</p>
        </div></body></html>
        """;

    private void ShowSignInComplete()
    {
        // Navigating from inside NavigationCompleted is asking for re-entrancy, so hop
        // to the message loop first.
        _webView?.BeginInvoke(() =>
        {
            try { Core?.NavigateToString(SignInCompleteHtml); }
            catch { /* The tab is going away; a nicer page is not worth a crash. */ }
        });
    }

    /// <summary>
    /// A request from one of the browser's own pages (History, Downloads, Bookmarks, the new
    /// tab page), with which page asked. Raised only for those files, by exact path, so a
    /// site posting the same message is ignored: see <see cref="InternalPages"/>.
    /// </summary>
    public event EventHandler<(InternalPages.Page Page, string Json)>? PageRequest;

    /// <summary>The tab whose page opened this one as a new window, or null.</summary>
    internal Tab? Opener { get; set; }

    /// <summary>
    /// Whether a tab that just started a download exists only for it: opened by a page as a
    /// new window, and never shown a page of its own. That is what a download link with
    /// target=_blank makes, and it stayed behind as an empty tab. A new window that showed
    /// a page first, a download page that starts the file after a moment, is kept. So is one
    /// its opener wrote a report or a print view into: that stays at about:blank too, but
    /// with a title of its own.
    /// </summary>
    internal static bool OnlyOpenedForADownload(bool openedByAPage, string url, string title)
        => openedByAPage && url == "about:blank" && title is "" or "New tab" or "about:blank";

    /// <summary>
    /// Posts json to the browser's own page in this tab, but only when the document the
    /// tab is actually showing is that page. Not what Url says: Url moves to a new address
    /// the moment a navigation starts, so a reply or a downloads push could otherwise land
    /// in the site that tab was leaving, or stay with it for good when the site keeps the
    /// person with a "leave this page?" prompt.
    /// </summary>
    public void PostToPage(string json, InternalPages.Page expected)
    {
        if (ShownPage != expected)
            return;
        try { Core?.PostWebMessageAsJson(json); }
        catch { }
    }

    /// <summary>
    /// The address of the document this tab has committed to showing, or Url when it has
    /// no view. What the lock and the page routing go by, rather than an address still loading.
    /// </summary>
    internal string CommittedUrl
    {
        get
        {
            try { return Core?.Source ?? Url; }
            catch { return Url; }
        }
    }

    /// <summary>Which of the browser's own pages this tab is showing, or null.</summary>
    internal InternalPages.Page? ShownPage => InternalPages.Identify(CommittedUrl);

    // The committed address, kept in a plain field so the agent server can read it from its
    // own threads, where Core must not be touched. Url alone is not enough there: it moves
    // to a new address when a navigation starts and stays there if it never commits, with
    // the old page, one of the browser's own perhaps, still on screen.
    private volatile string _committedSource = "about:blank";

    /// <summary>The committed address, for other threads. See <see cref="ShownPage"/> on the UI thread.</summary>
    internal string CommittedSourceForOtherThreads => _committedSource;

    // The main-frame navigation the engine has announced and not yet finished. The engine
    // cannot commit a navigation before this thread has handled its NavigationStarting, but
    // it can commit one before this thread has handled the SourceChanged after it, so for a
    // moment the document on screen is newer than CommittedUrl. A Back click to one of the
    // browser's own pages is like that and never moves Url either. UI thread only.
    private (ulong Id, string Uri)? _navigationUnderWay;

    internal void NoteNavigationUnderWay(ulong navigationId, string uri) => _navigationUnderWay = (navigationId, uri);

    internal void NoteNavigationOver(ulong navigationId)
    {
        if (_navigationUnderWay?.Id == navigationId)
            _navigationUnderWay = null;
    }

    /// <summary>
    /// Throws when one of the browser's own pages is on screen, or on its way: by the address
    /// the host sent the tab to, or by a navigation the engine has started. Called by the
    /// agent API's script and capture paths at the moment they would run, after the wait for
    /// the page, which is long enough for the tab to arrive at one of those pages. A tab on
    /// its way from one of them to anywhere else is "leaving", which the agent server answers
    /// as not ready rather than forbidden.
    /// </summary>
    internal void RefuseOwnPage()
    {
        if (RefusalFor(Url, _navigationUnderWay?.Uri, CommittedUrl) is { } refused)
            throw refused;
    }

    /// <summary>
    /// The refusal for agent code or a capture, or null when it may go ahead. The decision
    /// <see cref="RefuseOwnPage"/> makes, apart from the tab, so each address can be tested:
    /// with no view the committed page is Url, and no test could tell the two apart.
    /// </summary>
    /// <param name="url">Where the host last sent the tab.</param>
    /// <param name="underWay">Where a navigation the engine has announced is heading, if one is.</param>
    /// <param name="committed">The document on screen.</param>
    internal static InternalPages.OwnPageRefusedException? RefusalFor(string url, string? underWay, string committed)
    {
        bool heading = InternalPages.Identify(url) is not null || InternalPages.Identify(underWay) is not null;
        if (!heading && InternalPages.Identify(committed) is null)
            return null;
        return new InternalPages.OwnPageRefusedException(leaving: !heading);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Our own pages post objects; everything else here is a string.
            if (InternalPages.Identify(e.Source) is { } page)
            {
                string json = e.WebMessageAsJson;
                if (json.StartsWith('{'))
                {
                    PageRequest?.Invoke(this, (page, json));
                    return;
                }
            }
            var message = e.TryGetWebMessageAsString();
            if (message is null)
                return;
            if (message.StartsWith("GERGUR_ERR:", StringComparison.Ordinal))
                AddError(message["GERGUR_ERR:".Length..]);
            else if (message.StartsWith("GERGUR_RES:", StringComparison.Ordinal))
                AddResourceFailure(message["GERGUR_RES:".Length..]);
            else if (message.StartsWith("GERGUR_EVAL:", StringComparison.Ordinal))
                CompleteAwaitedScript(message["GERGUR_EVAL:".Length..]);
        }
        catch
        {
            // Non-string web messages from the page: ignore.
        }
    }

    /// <summary>
    /// A subresource failed to load. Requests our own blocklist killed are not page
    /// defects, and counting them would light up the indicator on every ad-heavy site
    /// purely because blocking works, so those are dropped. Anything else is a real
    /// broken asset and worth naming.
    /// </summary>
    private void AddResourceFailure(string payload)
    {
        int split = payload.IndexOf(' ');
        if (split <= 0)
            return;
        string tag = payload[..split];
        string url = payload[(split + 1)..];
        if (_owner.Blocker.IsBlockedUrl(url))
            return;
        AddError($"Failed to load {tag}: {url}");
    }

    private void AddError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        if (_recentErrors.Count > 0 && _recentErrors[^1] == message)
            return; // collapse immediate repeats
        _recentErrors.Add(message);
        if (_recentErrors.Count > 20)
            _recentErrors.RemoveAt(0);
        RaiseUpdated();
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        DebugLog.Write($"NewWindowRequested from={Url} target={e.Uri}");
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            var tab = await _owner.CreatePopupTabAsync(opener: this);
            if (tab?.Core is not null)
                e.NewWindow = tab.Core; // the opener drives navigation; preserves window.open semantics
            else
                e.Handled = false;
        }
        catch
        {
            e.Handled = false;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        DebugLog.Write($"ProcessFailed url={Url} kind={e.ProcessFailedKind}");
        // GPU/utility failures recover on their own; only a dead/hung renderer needs us.
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            Discard();
        }
    }

    private void OnWebViewKeyDown(object? sender, KeyEventArgs e) => WebViewKeyDown?.Invoke(this, e);

    private void RaiseUpdated() => Updated?.Invoke(this, EventArgs.Empty);

    private void DetachAndDisposeWebView()
    {
        // The one place every way of losing the view for good goes through: discarded to
        // save memory, closed, or a renderer that died. Nothing is going to load or reply
        // now, so say so rather than leaving every caller to sit out its full timeout,
        // which for an evaluation is up to two minutes of a request thread going nowhere.
        AbandonLoadWaiters();
        AbandonPendingLoad();
        AbandonAwaitedScripts("the tab went to sleep while the script was running");
        DropView();
    }

    /// <summary>
    /// Takes the view away without answering anyone waiting on the tab.
    ///
    /// For clearing up after a build that failed, where somebody is about to try again and
    /// still wants their answer. Tearing the whole thing down there told a /navigate?wait
    /// caller its page had not loaded, the rebuild then succeeded, the page arrived, and
    /// the endpoint reported loaded:false for a page sitting right there: the wrong answer
    /// that looks fine, which is the shape this file works hardest to avoid.
    /// </summary>
    private void DropView()
    {
        var webView = _webView;
        _webView = null;
        _committedSource = Url;   // no document now; the address is what a rebuild shows
        _navigationUnderWay = null;
        _ensureLiveTask = null;
        _lowMemoryApplied = false;
        // Belongs to the engine going away; holding it kept that engine's wrapper alive
        // until the next build.
        _pageCleanupScript = null;

        // The pixels went with the view. Leaving this latched meant a tab discarded after
        // fifteen minutes still claimed to have something to photograph, so a screenshot
        // built a fresh hidden view and captured it before the page had loaded: a blank
        // png at 200, and an engine process started to produce it.
        HasRendered = false;

        if (webView is null)
            return;
        try
        {
            webView.KeyDown -= OnWebViewKeyDown;
            _owner.Host.Controls.Remove(webView);
            webView.Dispose(); // takes CoreWebView2 (and our handlers on it) down with it
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Opener = null;   // a closed popup lets go of its opener; TabManager clears the other direction
        DetachAndDisposeWebView();
        State = TabState.Discarded;
        Favicon?.Dispose();
        Favicon = null;
    }
}
