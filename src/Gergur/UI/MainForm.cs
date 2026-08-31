using Gergur.App;
using Gergur.Blocking;
using Gergur.Data;
using Gergur.Diagnostics;
using Gergur.Tabs;
using Microsoft.Web.WebView2.Core;
// (DebugLog trace calls are dev-only; enabled via GERGUR_DEBUG=1)

namespace Gergur.UI;

public sealed class MainForm : Form
{
    private readonly string? _startupUrl;
    private readonly Settings _settings;
    private readonly ShortcutRouter _shortcuts;
    private readonly BookmarkStore _bookmarks = new();
    private readonly HistoryStore _history = new();
    private readonly VpnTunnel _vpn = new();

    private BrowserEnvironment? _env;
    private RequestBlocker? _blocker;
    private TabLifecycleManager? _lifecycle;
    private AgentServer? _agent;
    private bool _closing;
    private bool _lifecycleTickRunning;
    private bool _isMinimized;
    private bool _isLocked;
    private DateTime? _backgroundSinceUtc;

    public TabManager? Tabs { get; private set; }

    private TabStripControl _tabStrip = null!;
    private Panel _toolbar = null!;
    private GlyphButton _backButton = null!;
    private GlyphButton _forwardButton = null!;
    private GlyphButton _reloadButton = null!;
    private GlyphButton _bookmarkButton = null!;
    private GlyphButton _menuButton = null!;
    private AddressBar _addressBar = null!;
    private Panel _hostPanel = null!;
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _messageLabel = null!;
    private ToolStripStatusLabel _errorLabel = null!;
    private ToolStripStatusLabel _vpnLabel = null!;
    private ToolStripStatusLabel _memoryLabel = null!;
    private ToolStripStatusLabel _sleepLabel = null!;
    private ToolStripStatusLabel _blockedLabel = null!;
    private ContextMenuStrip _menu = null!;
    private System.Windows.Forms.Timer _lifecycleTimer = null!;
    private System.Windows.Forms.Timer _statusTimer = null!;

    public MainForm(string? startupUrl)
    {
        _startupUrl = startupUrl;
        _settings = Settings.Load();
        _shortcuts = new ShortcutRouter(this);
        BuildUi();
    }

    // ------------------------------------------------------------------ UI setup

    private void BuildUi()
    {
        Text = "Gergur";
        BackColor = Theme.WindowBg;
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        try
        {
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "gergur.ico"));
        }
        catch
        {
            // Missing icon asset is cosmetic only.
        }

        _hostPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.WindowBg };

        _tabStrip = new TabStripControl { Dock = DockStyle.Top, Height = 46, Font = new Font("Segoe UI", 9.5f) };
        _tabStrip.TabClicked += async (_, tab) => { await ActivateTabAsync(tab); };
        _tabStrip.TabCloseClicked += async (_, tab) => { await CloseTabAsync(tab); };
        _tabStrip.NewTabClicked += (_, _) => _ = NewTabAsync();
        _tabStrip.TabReordered += (_, move) => Tabs?.MoveTab(move.From, move.To);

        _toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.ToolbarBg };
        _backButton = MakeToolButton(Glyphs.Back, 8);
        _forwardButton = MakeToolButton(Glyphs.Forward, 44);
        _reloadButton = MakeToolButton(Glyphs.Refresh, 80);
        _backButton.Click += (_, _) => BackActive();
        _forwardButton.Click += (_, _) => ForwardActive();
        _reloadButton.Click += (_, _) => ReloadActive();

        _addressBar = new AddressBar
        {
            Location = new Point(116, 7),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            SearchUrlTemplate = _settings.SearchUrlTemplate,
        };
        _addressBar.Width = _toolbar.Width; // corrected after toolbar is sized
        _addressBar.SuggestionProvider = () => _history.GetSuggestions();
        _addressBar.NavigationRequested += async (_, url) => await NavigateActiveAsync(url);
        _addressBar.Escaped += (_, _) =>
        {
            _addressBar.Text = Tabs?.ActiveTab?.Url ?? "";
            Tabs?.ActiveTab?.FocusPage();
        };

        _bookmarkButton = MakeToolButton(Glyphs.StarOutline, 0);
        _menuButton = MakeToolButton(Glyphs.Menu, 0);
        _bookmarkButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _menuButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _bookmarkButton.Click += (_, _) => ToggleBookmark();
        _menuButton.Click += (_, _) => _menu.Show(_menuButton, new Point(0, _menuButton.Height));

        _toolbar.Controls.AddRange([_backButton, _forwardButton, _reloadButton, _addressBar, _bookmarkButton, _menuButton]);
        _toolbar.Resize += (_, _) => LayoutToolbar();

        _statusStrip = new StatusStrip
        {
            BackColor = Theme.TabStripBg,
            ForeColor = Theme.TextDim,
            SizingGrip = false,
        };
        _messageLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextDim };
        _errorLabel = new ToolStripStatusLabel
        {
            ForeColor = Theme.CloseHover,
            IsLink = true,
            LinkColor = Theme.CloseHover,
            ToolTipText = "Click to open DevTools",
        };
        _errorLabel.Click += (_, _) => Tabs?.ActiveTab?.OpenDevTools();
        _vpnLabel = new ToolStripStatusLabel { ForeColor = Theme.Accent };
        _memoryLabel = new ToolStripStatusLabel { ForeColor = Theme.TextDim };
        _sleepLabel = new ToolStripStatusLabel { ForeColor = Theme.TextDim };
        _blockedLabel = new ToolStripStatusLabel { ForeColor = Theme.TextDim };
        _statusStrip.Items.AddRange([_messageLabel, _errorLabel, _vpnLabel, _sleepLabel, _blockedLabel, _memoryLabel]);

        Controls.Add(_hostPanel);
        Controls.Add(_statusStrip);
        Controls.Add(_toolbar);
        Controls.Add(_tabStrip);

        BuildMenu();
        LayoutToolbar();

        _lifecycleTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _lifecycleTimer.Tick += async (_, _) =>
        {
            // Minimized/locked long enough: force-sleep everything, active tab included.
            bool sleepAll = _backgroundSinceUtc is { } since
                && DateTime.UtcNow - since >= TimeSpan.FromMinutes(Math.Max(1, _settings.SleepAllWhenBackgroundedMinutes));
            await RunLifecycleTickAsync(forceSuspend: sleepAll);
        };
        _statusTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _statusTimer.Tick += (_, _) => UpdateStatus();
    }

    private GlyphButton MakeToolButton(string glyph, int x)
        => new(glyph) { Location = new Point(x, 5) };

    private void LayoutToolbar()
    {
        _menuButton.Location = new Point(_toolbar.Width - 40, 5);
        _bookmarkButton.Location = new Point(_toolbar.Width - 76, 5);
        _addressBar.Location = new Point(120, (_toolbar.Height - _addressBar.Height) / 2);
        _addressBar.Width = Math.Max(100, _bookmarkButton.Left - 8 - _addressBar.Left);
    }

    private void BuildMenu()
    {
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenuItems();
        RebuildMenuItems();
    }

    private void RebuildMenuItems()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("New tab\tCtrl+T", null, (_, _) => _ = NewTabAsync()));
        _menu.Items.Add(new ToolStripMenuItem("Reopen closed tab\tCtrl+Shift+T", null, (_, _) => _ = ReopenClosedTabAsync()));
        _menu.Items.Add(new ToolStripSeparator());

        var bookmarks = new ToolStripMenuItem("Bookmarks");
        bookmarks.DropDownItems.Add(new ToolStripMenuItem("Bookmark this page\tCtrl+D", null, (_, _) => ToggleBookmark()));
        if (_bookmarks.Items.Count > 0)
        {
            bookmarks.DropDownItems.Add(new ToolStripSeparator());
            foreach (var bookmark in _bookmarks.Items)
            {
                var item = new ToolStripMenuItem(Truncate(bookmark.Title, 60)) { ToolTipText = bookmark.Url };
                var url = bookmark.Url;
                item.Click += (_, _) => _ = NavigateActiveAsync(url);
                bookmarks.DropDownItems.Add(item);
            }
        }
        _menu.Items.Add(bookmarks);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem("Sleep background tabs now", null, async (_, _) =>
        {
            await RunLifecycleTickAsync(forceSuspend: true);
            UpdateStatus();
            ShowMessage("Background tabs put to sleep.");
        }));

        var blocking = new ToolStripMenuItem($"Ad/tracker blocking ({(_blocker?.Enabled == true ? "on" : "off")}, {_blocker?.RuleCount ?? 0:N0} rules)")
        {
            Checked = _blocker?.Enabled == true,
        };
        blocking.Click += (_, _) =>
        {
            if (_blocker is null)
                return;
            _blocker.Enabled = !_blocker.Enabled;
            _settings.BlocklistEnabled = _blocker.Enabled;
            _settings.Save();
            UpdateStatus();
        };
        _menu.Items.Add(blocking);
        _menu.Items.Add(new ToolStripMenuItem("Update blocklist (StevenBlack hosts)", null, async (_, _) => await UpdateBlocklistAsync(silent: false)));

        var vpn = new ToolStripMenuItem($"VPN through Cloudflare WARP ({(_settings.VpnEnabled ? "on" : "off")})")
        {
            Checked = _settings.VpnEnabled,
        };
        vpn.Click += (_, _) => ToggleVpn();
        _menu.Items.Add(vpn);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem("Browser task manager", null, (_, _) => Tabs?.ActiveTab?.OpenTaskManager()));
        _menu.Items.Add(new ToolStripMenuItem("DevTools\tF12", null, (_, _) => OpenDevTools()));
        _menu.Items.Add(new ToolStripMenuItem("Dump memory CSV", null, (_, _) => DumpMemoryCsv()));
        _menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Settings.DataDir);
            System.Diagnostics.Process.Start("explorer.exe", Settings.DataDir);
        }));
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ------------------------------------------------------------------ startup

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            // The tunnel must be listening before the engine starts, or every
            // request would hit a dead proxy. On failure, run without for this session.
            if (_settings.VpnEnabled)
            {
                bool up = await _vpn.StartAsync(_settings.VpnLocalPort, TimeSpan.FromSeconds(12));
                if (!up)
                {
                    _settings.VpnEnabled = false; // session-only; the saved intent stays
                    ShowMessage("VPN tunnel failed to start; browsing without it.");
                }
            }

            _blocker = new RequestBlocker(_settings.BlocklistEnabled);
            _env = await BrowserEnvironment.CreateAsync(_settings);
            _env.Core.BrowserProcessExited += OnBrowserProcessExited;

            Tabs = new TabManager(_env, _hostPanel, _blocker);
            Tabs.Changed += (_, _) => UpdateChrome();
            Tabs.TabCreated += (_, tab) => WireTab(tab);
            Tabs.LastTabClosed += (_, _) => Close();
            _tabStrip.Bind(Tabs);

            _lifecycle = new TabLifecycleManager(
                TimeSpan.FromMinutes(Math.Max(1, _settings.SuspendAfterMinutes)),
                TimeSpan.FromMinutes(Math.Max(2, _settings.DiscardAfterMinutes)));

            await RestoreSessionAsync();

            if (_settings.AgentServerEnabled)
            {
                try
                {
                    _agent = new AgentServer(this, Tabs, _settings.AgentServerPort);
                    _agent.Start();
                }
                catch
                {
                    _agent = null; // port taken etc.; browsing works without the agent API
                }
            }

            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            _lifecycleTimer.Start();
            _statusTimer.Start();
            UpdateStatus();
            DebugLog.Write($"layout client={ClientSize} strip={_tabStrip.Bounds} toolbar={_toolbar.Bounds} host={_hostPanel.Bounds} status={_statusStrip.Bounds} statusVisible={_statusStrip.Visible}");

            // First run with blocking on but no list yet: fetch one quietly.
            if (_blocker.Enabled && _blocker.RuleCount == 0)
                _ = UpdateBlocklistAsync(silent: true);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The WebView2 runtime is not installed. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and try again.",
                "Gergur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Gergur failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task RestoreSessionAsync()
    {
        if (Tabs is null)
            return;
        if (_startupUrl is not null)
        {
            await Tabs.CreateTabAsync(UrlHeuristics.ToNavigableUrl(_startupUrl, _settings.SearchUrlTemplate));
            return;
        }

        var session = SessionStore.Load();
        DebugLog.Write($"RestoreSession tabs={session?.Tabs.Count ?? -1} active={session?.ActiveIndex ?? -1}");
        if (session is null || session.Tabs.Count == 0)
        {
            await NewTabAsync();
            return;
        }

        // Background tabs come back as parked snapshots: zero engine processes until clicked.
        Tab? toActivate = null;
        for (int i = 0; i < session.Tabs.Count; i++)
        {
            var entry = session.Tabs[i];
            var tab = Tabs.AddSnapshotTab(new TabSnapshot(entry.Url, entry.Title));
            if (i == session.ActiveIndex)
                toActivate = tab;
        }
        await Tabs.ActivateAsync(toActivate ?? Tabs.Tabs[^1]);
        // Move initial focus off the address bar so it reflects the active tab's URL.
        Tabs.ActiveTab?.FocusPage();
        UpdateChrome();
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        if (_closing || e.BrowserProcessExitKind != CoreWebView2BrowserProcessExitKind.Failed)
            return;
        BeginInvoke(() =>
        {
            _closing = true;
            SaveSession();
            Application.Restart();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        SaveSession();
        _lifecycleTimer.Stop();
        _statusTimer.Stop();
        _agent?.Stop();
        Tabs?.DisposeAll(); // engine processes exit promptly once the last WebView is gone
        _vpn.Stop();
        base.OnFormClosing(e);
    }

    private void ToggleVpn()
    {
        if (!_settings.VpnEnabled && !VpnTunnel.IsProvisioned)
        {
            ShowMessage(@"VPN not set up yet: run scripts\setup-warp.ps1 from the repo first.");
            return;
        }
        _settings.VpnEnabled = !_settings.VpnEnabled;
        _settings.Save();
        RestartForNewEngineFlags();
    }

    private void RestartForNewEngineFlags()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(exe, $"--wait-restart {Environment.ProcessId}");
        }
        catch { }
        Close();
    }

    private void SaveSession()
    {
        if (Tabs is null)
            return;
        var kept = Tabs.Tabs.Where(t => !HomePage.IsHome(t.Url)).ToList();
        var open = kept.Select(t => new SessionTab(t.Url, t.Title)).ToList();
        int activeIndex = Tabs.ActiveTab is null ? 0 : Math.Max(0, kept.IndexOf(Tabs.ActiveTab));
        SessionStore.Save(new SessionData(open, activeIndex));
    }

    // ------------------------------------------------------------------ tab wiring

    private void WireTab(Tab tab)
    {
        tab.WebViewKeyDown += (_, e) =>
        {
            if (_shortcuts.Handle(e.KeyData))
                e.Handled = e.SuppressKeyPress = true;
        };
        tab.PageLoaded += (_, _) => _history.Append(tab.Url, tab.Title);
    }

    private async Task ActivateTabAsync(Tab tab)
    {
        if (Tabs is null)
            return;
        await Tabs.ActivateAsync(tab);
        tab.FocusPage();
    }

    private void UpdateChrome(bool forceAddressBar = false)
    {
        var active = Tabs?.ActiveTab;
        // Skip the URL refresh only when the user is actively editing the address bar,
        // never when a tab close incidentally parked focus here (forceAddressBar).
        if (forceAddressBar || !_addressBar.Focused)
            _addressBar.Text = active is null || HomePage.IsHome(active.Url) ? "" : active.Url;
        _backButton.Enabled = active?.CanGoBack ?? false;
        _forwardButton.Enabled = active?.CanGoForward ?? false;
        bool bookmarked = active is not null && _bookmarks.Contains(active.Url);
        _bookmarkButton.Glyph = bookmarked ? Glyphs.StarFilled : Glyphs.StarOutline;
        _bookmarkButton.GlyphColor = bookmarked ? Theme.Accent : Theme.Text;
        Text = active is null || string.IsNullOrWhiteSpace(active.Title) ? "Gergur" : $"{active.Title} - Gergur";

        int errors = active?.ErrorCount ?? 0;
        _errorLabel.Text = errors == 0 ? "" : $"⚠ {errors} issue{(errors == 1 ? "" : "s")}";
        _errorLabel.ToolTipText = errors == 0 ? "" : string.Join("\n", active!.RecentErrors.TakeLast(6)) + "\n\nClick to open DevTools";

        UpdateSleepLabel();
    }

    // ------------------------------------------------------------------ actions (shortcut router targets)

    public async Task NewTabAsync(string? url = null)
    {
        if (Tabs is null)
            return;
        await Tabs.CreateTabAsync(url ?? HomePage.Url);
        if (url is null)
            FocusAddressBar();
    }

    public async Task CloseActiveTabAsync()
    {
        if (Tabs?.ActiveTab is { } active)
            await CloseTabAsync(active);
    }

    /// <summary>
    /// Closes a tab, then focuses the newly-active page and re-syncs the chrome.
    /// Disposing the closed tab's WebView bounces WinForms focus to the address bar;
    /// without moving it back, UpdateChrome's "don't clobber what the user is typing"
    /// guard leaves the address bar showing the closed tab's URL.
    /// </summary>
    private async Task CloseTabAsync(Tab tab)
    {
        if (Tabs is null)
            return;
        await Tabs.CloseTabAsync(tab);
        if (Tabs.ActiveTab is { } now)
        {
            now.FocusPage();
            UpdateChrome(forceAddressBar: true);
        }
    }

    public async Task ReopenClosedTabAsync()
    {
        if (Tabs is not null)
            await Tabs.ReopenClosedAsync();
    }

    public async Task CycleTabAsync(int direction)
    {
        if (Tabs is not null)
        {
            await Tabs.ActivateNextAsync(direction);
            Tabs.ActiveTab?.FocusPage();
        }
    }

    public async Task ActivateTabIndexAsync(int index)
    {
        if (Tabs is not null)
        {
            await Tabs.ActivateIndexAsync(index);
            Tabs.ActiveTab?.FocusPage();
        }
    }

    private async Task NavigateActiveAsync(string url)
    {
        DebugLog.Write($"NavigateActive url={url}\n{Environment.StackTrace}");
        if (Tabs is null)
            return;
        if (Tabs.ActiveTab is { } active)
        {
            await active.NavigateAsync(url);
            active.FocusPage();
        }
        else
        {
            await Tabs.CreateTabAsync(url);
        }
    }

    public void FocusAddressBar()
    {
        _addressBar.Focus();
        _addressBar.SelectAll();
    }

    public void ReloadActive() => Tabs?.ActiveTab?.Reload();
    public void BackActive() => Tabs?.ActiveTab?.GoBack();
    public void ForwardActive() => Tabs?.ActiveTab?.GoForward();
    public void OpenDevTools() => Tabs?.ActiveTab?.OpenDevTools();

    public void ToggleBookmark()
    {
        if (Tabs?.ActiveTab is not { } active || HomePage.IsHome(active.Url))
            return;
        bool added = _bookmarks.Toggle(active.Url, active.Title);
        ShowMessage(added ? "Bookmarked." : "Bookmark removed.");
        UpdateChrome();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        => _shortcuts.Handle(keyData) || base.ProcessCmdKey(ref msg, keyData);

    // ------------------------------------------------------------------ minimize / lock

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool minimized = WindowState == FormWindowState.Minimized;
        if (minimized != _isMinimized)
        {
            _isMinimized = minimized;
            UpdateBackgroundState();
        }
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock)
            BeginInvoke(() => { _isLocked = true; UpdateBackgroundState(); });
        else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock)
            BeginInvoke(() => { _isLocked = false; UpdateBackgroundState(); });
    }

    private void UpdateBackgroundState()
    {
        if (_closing || Tabs is null)
            return;
        bool backgrounded = _isMinimized || _isLocked;
        if (backgrounded && _backgroundSinceUtc is null)
        {
            _backgroundSinceUtc = DateTime.UtcNow;
            // Hide the active tab so the policy may freeze it too; audio keeps playing
            // (audible tabs are exempt from suspension).
            Tabs.ActiveTab?.Deactivate();
        }
        else if (!backgrounded && _backgroundSinceUtc is not null)
        {
            _backgroundSinceUtc = null;
            if (Tabs.ActiveTab is { } active)
                _ = ActivateTabAsync(active); // resumes a frozen page instantly
        }
    }

    // ------------------------------------------------------------------ policy + status

    private async Task RunLifecycleTickAsync(bool forceSuspend)
    {
        if (_lifecycle is null || Tabs is null || _lifecycleTickRunning || _closing)
            return;
        _lifecycleTickRunning = true;
        try
        {
            await _lifecycle.TickAsync(Tabs.Tabs.Cast<ITabHandle>().ToList(), forceSuspend);
        }
        catch
        {
            // A failed pass is fine; the next timer tick tries again.
        }
        finally
        {
            _lifecycleTickRunning = false;
        }
    }

    private void UpdateStatus()
    {
        if (_env is null || _closing)
            return;
        var snapshot = MemoryMeter.Capture(_env.Core);
        _memoryLabel.Text = $"engine {MemorySnapshot.Format(snapshot.EnginePrivateBytes)} · {snapshot.RendererCount} renderers · shell {MemorySnapshot.Format(snapshot.ShellPrivateBytes)}";
        _blockedLabel.Text = _blocker is { Enabled: true } ? $"{_blocker.BlockedCount:N0} blocked" : "blocking off";
        _vpnLabel.Text = _settings.VpnEnabled && _vpn.IsRunning ? "WARP" : "";
        UpdateSleepLabel();
    }

    private void UpdateSleepLabel()
    {
        if (Tabs is null)
            return;
        int sleeping = Tabs.Tabs.Count(t => t.State is TabState.Suspended or TabState.Discarded);
        _sleepLabel.Text = $"{sleeping}/{Tabs.Tabs.Count} tabs asleep";
    }

    private void DumpMemoryCsv()
    {
        if (_env is null)
            return;
        var snapshot = MemoryMeter.Capture(_env.Core);
        Directory.CreateDirectory(Settings.DataDir);
        string path = Path.Combine(Settings.DataDir, $"memory-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(path, MemoryMeter.ToCsv(snapshot));
        ShowMessage($"Saved {path}");
    }

    private async Task UpdateBlocklistAsync(bool silent)
    {
        if (_blocker is null)
            return;
        try
        {
            ShowMessage("Updating blocklist…");
            int rules = await _blocker.UpdateBlocklistAsync();
            ShowMessage($"Blocklist updated: {rules:N0} hosts.");
        }
        catch (Exception ex)
        {
            ShowMessage(silent ? "" : $"Blocklist update failed: {ex.Message}");
        }
    }

    private void ShowMessage(string text) => _messageLabel.Text = text;
}
