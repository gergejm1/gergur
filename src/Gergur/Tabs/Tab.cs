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
    private readonly TabManager _owner;
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
            function post(m) {
                try { if (window.chrome && window.chrome.webview)
                    window.chrome.webview.postMessage("GERGUR_ERR:" + String(m).slice(0, 300)); } catch (e) {}
            }
            window.addEventListener("error", function (e) {
                post((e.message || "Script error") + (e.filename ? " @ " + e.filename : "")); }, true);
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
        else if (e.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled
                 or CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired))
        {
            AddError($"Page failed to load: {e.WebErrorStatus}");
        }
        RaiseUpdated();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.TryGetWebMessageAsString();
            if (message is not null && message.StartsWith("GERGUR_ERR:", StringComparison.Ordinal))
                AddError(message["GERGUR_ERR:".Length..]);
        }
        catch
        {
            // Non-string web messages from the page: ignore.
        }
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
