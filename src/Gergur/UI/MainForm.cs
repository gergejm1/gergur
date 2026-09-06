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
    /// <summary>Raised once the last window has closed; Program ends the message loop on it.</summary>
    public static event EventHandler? LastWindowClosed;

    private readonly string? _startupUrl;
    private readonly bool _isSecondaryWindow;
    private readonly SessionWindow? _restore;
    private readonly Settings _settings;
    private readonly ShortcutRouter _shortcuts;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AppSession? _session;
    private TabLifecycleManager? _lifecycle;
    private HistoryForm? _historyWindow;
    private DownloadsForm? _downloadsWindow;
    private bool _closing;
    private bool _lifecycleTickRunning;
    private bool _isMinimized;
    private bool _isLocked;
    private DateTime? _backgroundSinceUtc;
    private bool _isPageFullScreen;
    private FormWindowState _preFullScreenState;
    private FormBorderStyle _preFullScreenBorder;

    public TabManager? Tabs { get; private set; }

    private TabStripControl _tabStrip = null!;
    private Panel _toolbar = null!;
    private GlyphButton _backButton = null!;
    private GlyphButton _forwardButton = null!;
    private GlyphButton _reloadButton = null!;
    private GlyphButton _bookmarkButton = null!;
    private GlyphButton _menuButton = null!;
    private AddressBar _addressBar = null!;
    private FindBar _findBar = null!;
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

    /// <summary>The first window: loads settings, brings up the shared services, restores the session.</summary>
    public MainForm(string? startupUrl)
        : this(session: null, startupUrl, isSecondaryWindow: false, restore: null)
    {
    }

    private MainForm(AppSession? session, string? startupUrl, bool isSecondaryWindow, SessionWindow? restore)
    {
        _session = session;
        _settings = session?.Settings ?? Settings.Load();
        _startupUrl = startupUrl;
        _isSecondaryWindow = isSecondaryWindow;
        _restore = restore;
        _shortcuts = new ShortcutRouter(this);
        BuildUi();
    }

    /// <summary>The services every window shares. Null until the first window has started them.</summary>
    internal AppSession? Session => _session;

    /// <summary>Completes once OnShown has built this window's tab manager.</summary>
    internal Task Ready => _ready.Task;

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
        _tabStrip.TabTornOff += (_, drop) => _ = DropTabAsync(drop.Tab, drop.ScreenLocation);
        _tabStrip.TabDragMoved += (_, screen) => UpdateDropIndicators(screen);
        _tabStrip.TabDragEnded += (_, _) => ClearDropIndicators();

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
        _addressBar.SuggestionProvider = () => _session?.History.GetSuggestions() ?? Array.Empty<string>();
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

        _findBar = new FindBar();
        _findBar.TermChanged += (_, term) => _ = Tabs?.ActiveTab?.FindAsync(term) ?? Task.CompletedTask;
        _findBar.NextRequested += (_, _) => Tabs?.ActiveTab?.FindNext();
        _findBar.PreviousRequested += (_, _) => Tabs?.ActiveTab?.FindPrevious();
        _findBar.CloseRequested += (_, _) => CloseFind();

        // Docking is applied last-added-outermost, so the find bar goes in before the
        // toolbar to sit directly beneath it and above the page.
        Controls.Add(_hostPanel);
        Controls.Add(_statusStrip);
        Controls.Add(_findBar);
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
        if (_session is { Bookmarks.Items.Count: > 0 })
        {
            bookmarks.DropDownItems.Add(new ToolStripSeparator());
            foreach (var bookmark in _session.Bookmarks.Items)
            {
                var item = new ToolStripMenuItem(Truncate(bookmark.Title, 60)) { ToolTipText = bookmark.Url };
                var url = bookmark.Url;
                item.Click += (_, _) => _ = NavigateActiveAsync(url);
                bookmarks.DropDownItems.Add(item);
            }
        }
        _menu.Items.Add(bookmarks);
        _menu.Items.Add(new ToolStripMenuItem("History\tCtrl+H", null, (_, _) => OpenHistory()));
        int running = _session?.Downloads.RunningCount ?? 0;
        _menu.Items.Add(new ToolStripMenuItem(
            running > 0 ? $"Downloads ({running} running)\tCtrl+J" : "Downloads\tCtrl+J",
            null, (_, _) => OpenDownloads()));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem("Find in page\tCtrl+F", null, (_, _) => OpenFind()));

        var zoom = new ToolStripMenuItem($"Zoom ({(Tabs?.ActiveTab?.ZoomFactor ?? 1.0) * 100:0}%)");
        zoom.DropDownItems.Add(new ToolStripMenuItem("Zoom in\tCtrl++", null, (_, _) => ZoomBy(0.1)));
        zoom.DropDownItems.Add(new ToolStripMenuItem("Zoom out\tCtrl+-", null, (_, _) => ZoomBy(-0.1)));
        zoom.DropDownItems.Add(new ToolStripMenuItem("Reset\tCtrl+0", null, (_, _) => ZoomReset()));
        _menu.Items.Add(zoom);

        _menu.Items.Add(new ToolStripMenuItem("Mute tab\tCtrl+M", null, (_, _) => ToggleMuteActiveTab())
        {
            Checked = Tabs?.ActiveTab?.IsMuted == true,
        });
        _menu.Items.Add(new ToolStripMenuItem("Print…\tCtrl+P", null, (_, _) => PrintActive()));
        _menu.Items.Add(new ToolStripMenuItem("Save as PDF…", null, async (_, _) => await SaveAsPdfAsync()));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem("Sleep background tabs now", null, async (_, _) =>
        {
            await RunLifecycleTickAsync(forceSuspend: true);
            UpdateStatus();
            ShowMessage("Background tabs put to sleep.");
        }));

        var blocker = _session?.Blocker;
        var blocking = new ToolStripMenuItem($"Ad/tracker blocking ({(blocker?.Enabled == true ? "on" : "off")}, {blocker?.RuleCount ?? 0:N0} rules)")
        {
            Checked = blocker?.Enabled == true,
        };
        blocking.Click += (_, _) =>
        {
            if (blocker is null)
                return;
            blocker.Enabled = !blocker.Enabled;
            _settings.BlocklistEnabled = blocker.Enabled;
            _settings.Save();
            UpdateStatus();
        };
        _menu.Items.Add(blocking);
        _menu.Items.Add(new ToolStripMenuItem("Update blocklist (StevenBlack hosts)", null, async (_, _) => await UpdateBlocklistAsync(silent: false)));

        _menu.Items.Add(BuildVpnMenu());
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem("Browser task manager", null, (_, _) => Tabs?.ActiveTab?.OpenTaskManager()));
        _menu.Items.Add(new ToolStripMenuItem("DevTools\tF12", null, (_, _) => OpenDevTools()));
        _menu.Items.Add(new ToolStripMenuItem("Dump memory CSV", null, (_, _) => DumpMemoryCsv()));
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
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
            if (_session is null)
                await StartSharedServicesAsync();
            var session = _session!;
            session.AddWindow(this);

            Tabs = new TabManager(session.Env, _hostPanel, session.Blocker);
            Tabs.Changed += (_, _) => UpdateChrome();
            Tabs.TabCreated += (_, tab) => WireTab(tab);
            Tabs.LastTabClosed += (_, _) => Close();
            _tabStrip.Bind(Tabs);

            _lifecycle = new TabLifecycleManager(
                TimeSpan.FromMinutes(Math.Max(1, _settings.SuspendAfterMinutes)),
                TimeSpan.FromMinutes(Math.Max(2, _settings.DiscardAfterMinutes)));

            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            _lifecycleTimer.Start();
            _statusTimer.Start();
            _ready.TrySetResult(); // a torn-off tab can be handed over from here on

            if (_restore is not null)
                await RestoreWindowAsync(_restore);
            else if (!_isSecondaryWindow)
                await RestoreStartupAsync();

            UpdateStatus();
            DebugLog.Write($"layout client={ClientSize} strip={_tabStrip.Bounds} toolbar={_toolbar.Bounds} host={_hostPanel.Bounds} status={_statusStrip.Bounds} statusVisible={_statusStrip.Visible}");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _ready.TrySetResult();
            MessageBox.Show(this, "The WebView2 runtime is not installed. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and try again.",
                "Gergur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            _ready.TrySetResult();
            MessageBox.Show(this, ex.ToString(), "Gergur failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// <summary>
    /// First window only: brings up everything the windows share. The tunnel has to
    /// be listening before the engine starts, or every request hits a dead proxy;
    /// on failure we run without it for this session.
    /// </summary>
    private async Task StartSharedServicesAsync()
    {
        var vpn = new VpnTunnel();
        if (_settings.VpnEnabled)
        {
            bool up = await vpn.StartAsync(_settings.VpnLocalPort, _settings.VpnProfile, TimeSpan.FromSeconds(12));
            if (!up)
            {
                _settings.VpnEnabled = false; // session-only; the saved intent stays
                ShowMessage("VPN tunnel failed to start; browsing without it.");
            }
        }
        _session = await AppSession.CreateAsync(_settings, vpn);
        _session.Env.Core.BrowserProcessExited += OnBrowserProcessExited;
        _session.StartAgent();

        // First run with blocking on but no list yet: fetch one quietly.
        if (_session.Blocker.Enabled && _session.Blocker.RuleCount == 0)
            _ = UpdateBlocklistAsync(silent: true);
    }

    /// <summary>Last session's windows, then any url from the command line on top.</summary>
    private async Task RestoreStartupAsync()
    {
        if (Tabs is null || _session is null)
            return;

        var windows = SessionStore.Load();
        DebugLog.Write($"RestoreSession windows={windows.Count} startupUrl={_startupUrl is not null}");
        if (windows.Count > 0)
        {
            await RestoreWindowAsync(windows[0]);
            // Windows torn off during the last run come back as windows of their own.
            for (int i = 1; i < windows.Count; i++)
                new MainForm(_session, null, isSecondaryWindow: true, restore: windows[i]).Show();
        }

        // A url on the command line means Windows launched us to open a link. It opens
        // on top of the restored session, never instead of it: losing every tab because
        // something handed the default browser a sign-in page would be indefensible.
        if (_startupUrl is not null)
            await Tabs.CreateTabAsync(UrlHeuristics.ToNavigableUrl(_startupUrl, _settings.SearchUrlTemplate));
        else if (Tabs.Tabs.Count == 0)
            await NewTabAsync();
    }

    /// <summary>
    /// A url from outside: a default-browser launch, or a later launch that forwarded
    /// its command line here. Opens a tab in the focused window and surfaces it.
    /// Safe to call from the pipe listener's thread.
    /// </summary>
    internal static void OpenExternalUrl(string url)
    {
        if (AppSession.Current is not { } session)
            return;
        var window = session.Windows.FirstOrDefault(w => w.ContainsFocus) ?? session.Windows.FirstOrDefault();
        if (window is null || window.IsDisposed)
            return;
        window.BeginInvoke(() =>
        {
            if (window.WindowState == FormWindowState.Minimized)
                window.WindowState = FormWindowState.Normal;
            window.Activate();
            _ = window.NewTabAsync(UrlHeuristics.ToNavigableUrl(url, window._settings.SearchUrlTemplate));
        });
    }

    /// <summary>Background tabs come back as parked snapshots: zero engine processes until clicked.</summary>
    private async Task RestoreWindowAsync(SessionWindow window)
    {
        if (Tabs is null)
            return;
        Tab? toActivate = null;
        for (int i = 0; i < window.Tabs.Count; i++)
        {
            var tab = Tabs.AddSnapshotTab(new TabSnapshot(window.Tabs[i].Url, window.Tabs[i].Title));
            if (i == window.ActiveIndex)
                toActivate = tab;
        }
        if (Tabs.Tabs.Count == 0)
        {
            await NewTabAsync();
            return;
        }
        await Tabs.ActivateAsync(toActivate ?? Tabs.Tabs[^1]);
        // Move initial focus off the address bar so it reflects the active tab's URL.
        Tabs.ActiveTab?.FocusPage();
        UpdateChrome();
    }

    // ------------------------------------------------------------------ tear-off and merge

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private const uint GA_ROOT = 2;

    /// <summary>
    /// The other window whose tab strip is under this screen point, if any. Asks the
    /// desktop which window is actually on top there rather than just intersecting
    /// rectangles, so a strip hidden behind another window is not a drop target.
    /// </summary>
    private MainForm? DropTargetAt(Point screen)
    {
        if (_session is null)
            return null;
        IntPtr top = GetAncestor(WindowFromPoint(screen), GA_ROOT);
        foreach (var window in _session.Windows)
        {
            if (window == this || !window.Visible || window.WindowState == FormWindowState.Minimized)
                continue;
            if (window.Handle == top && window._tabStrip.ScreenBounds.Contains(screen))
                return window;
        }
        return null;
    }

    private IReadOnlyList<MainForm> AllWindows()
        => _session?.Windows ?? (IReadOnlyList<MainForm>)[this];

    /// <summary>Lights up the strip a drop would land in, and only that one.</summary>
    private void UpdateDropIndicators(Point screen)
    {
        var target = DropTargetAt(screen);
        foreach (var window in AllWindows())
        {
            if (window == target)
                window._tabStrip.ShowExternalDrop(window._tabStrip.DropIndexForScreen(screen));
            else
                window._tabStrip.ClearExternalDrop();
        }
    }

    private void ClearDropIndicators()
    {
        foreach (var window in AllWindows())
            window._tabStrip.ClearExternalDrop();
    }

    /// <summary>
    /// A tab let go outside its own strip: it joins whatever window's strip is under
    /// the cursor, and failing that becomes a window of its own.
    /// </summary>
    private async Task DropTabAsync(Tab tab, Point screenLocation)
    {
        ClearDropIndicators();
        if (DropTargetAt(screenLocation) is { } target)
        {
            await target.AdoptTabAsync(this, tab, target._tabStrip.DropIndexForScreen(screenLocation));
            target.Activate();
            return;
        }
        await TearOffTabAsync(tab, screenLocation);
    }

    /// <summary>
    /// Pulls a tab into a window of its own at the drop point. The live WebView is
    /// reparented rather than recreated, so the page is not reloaded and anything
    /// typed into it survives. A one-tab window is left alone: tearing its only tab
    /// off would just be moving the window.
    /// </summary>
    private async Task TearOffTabAsync(Tab tab, Point screenLocation)
    {
        if (Tabs is null || _session is null || Tabs.Tabs.Count < 2)
            return;

        var window = new MainForm(_session, null, isSecondaryWindow: true, restore: null)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(Math.Max(0, screenLocation.X - 160), Math.Max(0, screenLocation.Y - 24)),
            Size = Size,
        };
        window.Show();
        await window.Ready;
        await window.AdoptTabAsync(this, tab, insertIndex: -1);
    }

    /// <summary>
    /// Takes a tab released by another window and makes it this window's active tab,
    /// dropped at <paramref name="insertIndex"/> (-1 appends). When that empties the
    /// window it came from, that window closes: merging its last tab away is the same
    /// gesture as closing it.
    /// </summary>
    internal async Task AdoptTabAsync(MainForm from, Tab tab, int insertIndex = -1)
    {
        if (Tabs is null || from.Tabs is null)
            return;
        bool sourceEmpties = from.Tabs.Tabs.Count == 1;

        await from.Tabs.ReleaseAsync(tab);
        Tabs.Adopt(tab);
        if (insertIndex >= 0 && Tabs.Tabs.Count > 1)
            Tabs.MoveTab(Tabs.Tabs.Count - 1, Math.Min(insertIndex, Tabs.Tabs.Count - 1));
        await Tabs.ActivateAsync(tab);
        tab.FocusPage();
        UpdateChrome(forceAddressBar: true);

        if (sourceEmpties)
            from.Close();
        else
            from.UpdateChrome(forceAddressBar: true);
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        if (_closing || e.BrowserProcessExitKind != CoreWebView2BrowserProcessExitKind.Failed)
            return;
        BeginInvoke(() =>
        {
            _closing = true;
            _session?.SaveSession();
            Application.Restart();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        // Saved before this window drops out of the list, so closing the last one
        // still records what it was holding.
        _session?.SaveSession();
        _lifecycleTimer.Stop();
        _statusTimer.Stop();
        Tabs?.DisposeAll(); // engine processes exit promptly once the last WebView is gone
        _session?.RemoveWindow(this); // stops the agent and tunnel when this was the last
        if (_session is null || _session.Windows.Count == 0)
            LastWindowClosed?.Invoke(this, EventArgs.Empty);
        base.OnFormClosing(e);
    }

    /// <summary>
    /// VPN submenu: off, then one entry per WireGuard profile. WARP is whatever
    /// datacenter is nearest, so extra .conf files are how you choose an exit country.
    /// </summary>
    private ToolStripMenuItem BuildVpnMenu()
    {
        string active = _settings.VpnEnabled
            ? _session?.Vpn.ActiveProfileName ?? VpnTunnel.ResolveProfile(_settings.VpnProfile)?.Name ?? "on"
            : "off";
        var menu = new ToolStripMenuItem($"VPN ({active})") { Checked = _settings.VpnEnabled };

        var profiles = VpnTunnel.ListProfiles();
        if (!VpnTunnel.IsProvisioned)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("Not set up: run scripts\\setup-warp.ps1") { Enabled = false });
        }
        else
        {
            var off = new ToolStripMenuItem("Off", null, (_, _) => SetVpnEnabled(false)) { Checked = !_settings.VpnEnabled };
            menu.DropDownItems.Add(off);
            menu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var profile in profiles)
            {
                bool current = _settings.VpnEnabled
                    && string.Equals(profile.Name, VpnTunnel.ResolveProfile(_settings.VpnProfile)?.Name, StringComparison.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(profile.Name) { Checked = current };
                string name = profile.Name;
                item.Click += (_, _) => _ = UseVpnProfileAsync(name);
                menu.DropDownItems.Add(item);
            }
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Add profile from .conf file…", null, (_, _) => ImportVpnProfile()));
        return menu;
    }

    private void SetVpnEnabled(bool enabled)
    {
        if (_settings.VpnEnabled == enabled)
            return;
        _settings.VpnEnabled = enabled;
        _settings.Save();
        RestartForNewEngineFlags(); // the proxy is a browser-process flag
    }

    /// <summary>
    /// Switching between profiles while the VPN is already on only restarts wireproxy:
    /// the browser keeps pointing at the same local SOCKS5 address, so no restart.
    /// Turning the VPN on from off still needs one.
    /// </summary>
    private async Task UseVpnProfileAsync(string profileName)
    {
        bool wasOn = _settings.VpnEnabled;
        bool sameProfile = string.Equals(VpnTunnel.ResolveProfile(_settings.VpnProfile)?.Name, profileName,
            StringComparison.OrdinalIgnoreCase);
        _settings.VpnProfile = profileName;
        _settings.VpnEnabled = true;
        _settings.Save();

        if (!wasOn)
        {
            RestartForNewEngineFlags();
            return;
        }
        if (_session is null || (sameProfile && _session.Vpn.IsRunning))
            return;

        ShowMessage($"Switching VPN to {profileName}…");
        bool up = await _session.Vpn.SwitchProfileAsync(_settings.VpnLocalPort, profileName, TimeSpan.FromSeconds(12));
        ShowMessage(up ? $"VPN now exiting through {profileName}." : $"{profileName} failed to start; the tunnel is down.");
        UpdateStatus();
    }

    private void ImportVpnProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Add a WireGuard profile",
            Filter = "WireGuard config (*.conf)|*.conf|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            string name = VpnTunnel.ImportProfile(dialog.FileName);
            ShowMessage($"Added VPN profile \"{name}\". Pick it from the VPN menu.");
        }
        catch (Exception ex)
        {
            ShowMessage($"Could not add that profile: {ex.Message}");
        }
    }

    private void RestartForNewEngineFlags()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(exe, $"--wait-restart {Environment.ProcessId}");
        }
        catch { }
        // An engine-flag change applies to the whole app, so every window goes.
        foreach (var window in _session?.Windows.ToList() ?? [this])
            window.Close();
    }

    // ------------------------------------------------------------------ tab wiring

    private void WireTab(Tab tab)
    {
        tab.WebViewKeyDown += (_, e) =>
        {
            if (_shortcuts.Handle(e.KeyData))
                e.Handled = e.SuppressKeyPress = true;
        };
        tab.PageLoaded += (_, _) => _session?.History.Append(tab.Url, tab.Title);
        // A sign-in popup closing itself is the normal end of an OAuth handshake.
        // It still goes on the reopen stack, so a surprise close is one Ctrl+Shift+T away.
        tab.CloseRequested += (_, _) => _ = CloseTabAsync(tab);
        tab.DownloadStarted += (_, operation) =>
        {
            if (_session is null)
                return;
            var item = _session.Downloads.Track(operation);
            ShowMessage($"Downloading {item.FileName}…");
            item.Changed += (_, _) =>
            {
                if (!item.IsRunning && !_closing)
                    ShowMessage($"Downloaded {item.FileName}");
            };
        };
        tab.FindStatusChanged += (_, _) =>
        {
            if (tab.IsCurrent)
                _findBar.SetStatus(tab.FindActiveMatch, tab.FindMatchCount);
        };
        // A video going fullscreen has to take the whole window, chrome included.
        tab.FullScreenChanged += (_, on) =>
        {
            if (tab.IsCurrent)
                SetPageFullScreen(on);
        };
    }

    // ------------------------------------------------------------------ find, zoom, audio, fullscreen

    /// <summary>Ctrl+F. Re-running it on an open bar re-selects the term, as browsers do.</summary>
    public void OpenFind()
    {
        if (_isPageFullScreen)
            return;
        _findBar.Open();
        if (_findBar.Term.Length > 0)
            _ = Tabs?.ActiveTab?.FindAsync(_findBar.Term);
    }

    private void CloseFind()
    {
        _findBar.Visible = false;
        Tabs?.ActiveTab?.StopFind();
        Tabs?.ActiveTab?.FocusPage();
    }

    public void FindNextMatch()
    {
        if (_findBar.Visible)
            Tabs?.ActiveTab?.FindNext();
    }

    public void ZoomBy(double step)
    {
        if (Tabs?.ActiveTab is not { } active)
            return;
        active.ZoomFactor += step;
        ShowMessage($"Zoom {active.ZoomFactor * 100:0}%");
    }

    public void ZoomReset()
    {
        if (Tabs?.ActiveTab is not { } active)
            return;
        active.ZoomFactor = 1.0;
        ShowMessage("Zoom 100%");
    }

    /// <summary>Ctrl+P: the engine's own print dialog, which includes Save as PDF.</summary>
    public void PrintActive() => Tabs?.ActiveTab?.ShowPrintUi();

    /// <summary>Straight to a PDF file, skipping the print dialog entirely.</summary>
    private async Task SaveAsPdfAsync()
    {
        if (Tabs?.ActiveTab is not { } active)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save page as PDF",
            Filter = "PDF document (*.pdf)|*.pdf",
            FileName = SafeFileName(active.Title) + ".pdf",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        ShowMessage("Saving PDF…");
        bool saved = await active.PrintToPdfAsync(dialog.FileName);
        ShowMessage(saved ? $"Saved {dialog.FileName}" : "Could not save that page as a PDF.");
    }

    /// <summary>Page titles routinely contain characters a filename cannot.</summary>
    private static string SafeFileName(string title)
    {
        var cleaned = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "page" : Truncate(cleaned, 80);
    }

    public void ToggleMuteActiveTab()
    {
        if (Tabs?.ActiveTab is not { } active)
            return;
        active.IsMuted = !active.IsMuted;
        ShowMessage(active.IsMuted ? "Tab muted." : "Tab unmuted.");
    }

    /// <summary>
    /// Page-driven fullscreen (a video, almost always). The WebView only fills the host
    /// panel, so without stripping the chrome and the window border a "fullscreen" video
    /// would still sit inside the tab strip and toolbar.
    /// </summary>
    private void SetPageFullScreen(bool on)
    {
        if (_isPageFullScreen == on)
            return;
        _isPageFullScreen = on;
        if (on)
        {
            _preFullScreenState = WindowState;
            _preFullScreenBorder = FormBorderStyle;
            _tabStrip.Visible = _toolbar.Visible = _statusStrip.Visible = false;
            _findBar.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            // Maximized -> Maximized does not re-apply the new border style, so drop out first.
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _preFullScreenBorder;
            WindowState = _preFullScreenState;
            _tabStrip.Visible = _toolbar.Visible = _statusStrip.Visible = true;
        }
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
        bool bookmarked = active is not null && _session?.Bookmarks.Contains(active.Url) == true;
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

    /// <summary>Modeless so browsing continues behind it; one window, reused.</summary>
    public void OpenHistory()
    {
        if (_historyWindow is { IsDisposed: false })
        {
            _historyWindow.Activate();
            return;
        }
        if (_session is null)
            return;
        _historyWindow = new HistoryForm(_session.History, url => _ = NewTabAsync(url));
        _historyWindow.FormClosed += (_, _) => _historyWindow = null;
        _historyWindow.Show(this);
    }

    /// <summary>
    /// Modal, unlike history and downloads: settings are shared by every window, so
    /// letting two of these disagree with each other would be asking for trouble.
    /// What can be applied without a restart is applied here and now.
    /// </summary>
    public void OpenSettings()
    {
        if (_session is null)
            return;
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        // Applied live across every window; the rest waits for new tabs or a restart.
        _session.Blocker.Enabled = _settings.BlocklistEnabled;
        foreach (var window in _session.Windows)
            window._addressBar.SearchUrlTemplate = _settings.SearchUrlTemplate;
        UpdateStatus();

        if (dialog.ChangedRestartSettings.Count == 0)
        {
            ShowMessage("Settings saved.");
            return;
        }
        string names = string.Join(", ", dialog.ChangedRestartSettings);
        var answer = MessageBox.Show(this,
            $"These take effect only when the engine restarts:\n\n{names}\n\nRestart Gergur now? Your tabs come back.",
            "Gergur", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            RestartForNewEngineFlags();
        else
            ShowMessage("Settings saved; engine changes apply on the next restart.");
    }

    /// <summary>Modeless, one window, shared across every browser window's downloads.</summary>
    public void OpenDownloads()
    {
        if (_session is null)
            return;
        if (_downloadsWindow is { IsDisposed: false })
        {
            _downloadsWindow.Activate();
            return;
        }
        _downloadsWindow = new DownloadsForm(_session.Downloads);
        _downloadsWindow.FormClosed += (_, _) => _downloadsWindow = null;
        _downloadsWindow.Show(this);
    }

    public void ToggleBookmark()
    {
        if (_session is null || Tabs?.ActiveTab is not { } active || HomePage.IsHome(active.Url))
            return;
        bool added = _session.Bookmarks.Toggle(active.Url, active.Title);
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
        if (_session is null || _closing)
            return;
        var snapshot = MemoryMeter.Capture(_session.Env.Core);
        _memoryLabel.Text = $"engine {MemorySnapshot.Format(snapshot.EnginePrivateBytes)} · {snapshot.RendererCount} renderers · shell {MemorySnapshot.Format(snapshot.ShellPrivateBytes)}";
        _blockedLabel.Text = _session.Blocker is { Enabled: true } blocker ? $"{blocker.BlockedCount:N0} blocked" : "blocking off";
        // Name the exit, not just "on": with several profiles, which one matters.
        _vpnLabel.Text = _settings.VpnEnabled && _session.Vpn.IsRunning
            ? (_session.Vpn.ActiveProfileName == VpnTunnel.WarpName ? "WARP" : _session.Vpn.ActiveProfileName ?? "VPN")
            : "";
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
        if (_session is null)
            return;
        var snapshot = MemoryMeter.Capture(_session.Env.Core);
        Directory.CreateDirectory(Settings.DataDir);
        string path = Path.Combine(Settings.DataDir, $"memory-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(path, MemoryMeter.ToCsv(snapshot));
        ShowMessage($"Saved {path}");
    }

    private async Task UpdateBlocklistAsync(bool silent)
    {
        if (_session is null)
            return;
        try
        {
            ShowMessage("Updating blocklist…");
            int rules = await _session.Blocker.UpdateBlocklistAsync();
            ShowMessage($"Blocklist updated: {rules:N0} hosts.");
        }
        catch (Exception ex)
        {
            ShowMessage(silent ? "" : $"Blocklist update failed: {ex.Message}");
        }
    }

    private void ShowMessage(string text) => _messageLabel.Text = text;
}
