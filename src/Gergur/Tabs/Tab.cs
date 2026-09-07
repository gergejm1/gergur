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

    public TabState State { get; private set; } = TabState.Discarded;
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
        Title = snapshot.Title;
    }

    internal CoreWebView2? Core => _webView?.CoreWebView2;

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

    /// <summary>Creates and wires the WebView2 if this tab is discarded. Idempotent.</summary>
    private Task EnsureLiveAsync(bool navigateToStoredUrl = true)
    {
        if (_webView is not null)
            return _ensureLiveTask ?? Task.CompletedTask;
        _ensureLiveTask ??= CreateWebViewAsync(navigateToStoredUrl);
        return _ensureLiveTask;
    }

    private async Task CreateWebViewAsync(bool navigateToStoredUrl)
    {
        DebugLog.Write($"CreateWebView url={Url} navigate={navigateToStoredUrl}");
        var webView = await _owner.Env.CreateWebViewAsync(_owner.Host, visible: false);
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
            await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ErrorReporterScript);

        State = TabState.Hidden;
        if (navigateToStoredUrl && Url is not ("" or "about:blank"))
            TryNavigateCore(Url);
        RaiseUpdated();
    }

    public async Task ActivateAsync()
    {
        if (_disposed)
            return;
        await EnsureLiveAsync();
        if (_webView is null)
            return;
        _webView.Visible = true; // auto-resumes a suspended page
        _webView.BringToFront();
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
            e.Handled = true;
            DownloadStarted?.Invoke(this, e.DownloadOperation);
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
    }

    public async Task NavigateAsync(string url)
    {
        await EnsureLiveAsync(navigateToStoredUrl: false);
        Url = url;
        TryNavigateCore(url);
        if (State == TabState.Suspended)
            State = _webView?.Visible == true ? TabState.Active : TabState.Hidden;
        RaiseUpdated();
    }

    private void TryNavigateCore(string url)
    {
        try { Core?.Navigate(url); }
        catch (ArgumentException) { /* malformed URL - leave the page as-is */ }
    }

    /// <summary>Runs JS in the page (agent API). Wakes the tab if it was parked.</summary>
    public async Task<string> ExecuteScriptAsync(string js)
    {
        await EnsureLiveAsync();
        var core = Core;
        if (core is null)
            return "null";
        return await core.ExecuteScriptAsync(js);
    }

    /// <summary>PNG screenshot of the rendered page (agent API). Wakes the tab if parked.</summary>
    public async Task<byte[]> CaptureScreenshotAsync()
    {
        await EnsureLiveAsync();
        var core = Core;
        if (core is null)
            return [];
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
        try
        {
            if (_lowMemoryApplied)
                SetLowMemoryTarget(false); // docs: don't mix manual Low with suspension
            bool suspended = await core.TrySuspendAsync();
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

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.TryGetWebMessageAsString();
            if (message is null)
                return;
            if (message.StartsWith("GERGUR_ERR:", StringComparison.Ordinal))
                AddError(message["GERGUR_ERR:".Length..]);
            else if (message.StartsWith("GERGUR_RES:", StringComparison.Ordinal))
                AddResourceFailure(message["GERGUR_RES:".Length..]);
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
            var tab = await _owner.CreatePopupTabAsync();
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
        var webView = _webView;
        _webView = null;
        _ensureLiveTask = null;
        _lowMemoryApplied = false;
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
        DetachAndDisposeWebView();
        State = TabState.Discarded;
        Favicon?.Dispose();
        Favicon = null;
    }
}
