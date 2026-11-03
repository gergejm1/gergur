using Gergur.App;
using Gergur.Blocking;
using Gergur.Data;
using Gergur.Diagnostics;
using Gergur.Tabs;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
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
    // Coalesces live updates to the browser's own pages. A download reports progress many
    // times a second, and each report would otherwise post the whole list to every
    // Downloads tab.
    private readonly System.Windows.Forms.Timer _pagePushTimer = new() { Interval = 250 };
    private bool _downloadsPushPending;
    private bool _bookmarksPushPending;
    private DropForm? _dropWindow;
    private NotifyIcon? _tray;
    private bool _closing;
    private bool _isPageFullScreen;
    private FormWindowState _preFullScreenState;
    private FormBorderStyle _preFullScreenBorder;
    private Rectangle _preFullScreenBounds;

    /// <summary>Whether a page has the window to itself, chrome hidden.</summary>
    internal bool IsPageFullScreen => _isPageFullScreen;

    public TabManager? Tabs { get; private set; }

    /// <summary>
    /// Opened by an agent through /window. Its tabs are the agent's work, so they are never
    /// saved as the person's session: see <see cref="AppSession.SessionOf"/>.
    /// </summary>
    internal bool OpenedByAgent
    {
        get => _openedByAgent;
        private set
        {
            if (_openedByAgent == value)
                return;
            _openedByAgent = value;
            // Only an agent's window can be hidden again; once it is the person's, X closes it.
            _hideButton.Visible = value;
            LayoutToolbar();
        }
    }

    private bool _openedByAgent;

    /// <summary>
    /// The person has used this window, so it is theirs from now on and its tabs are their
    /// session. Called from what only a person does: typing in the address bar, opening a
    /// new tab from the strip or the menu, reopening a closed tab, a bookmark, a history
    /// entry, a link from the phone panel or drop window, a keyboard shortcut, a link from
    /// another app, or dragging in a tab from a window of their own. Without it, dragging every tab into an agent's
    /// window emptied their own, which then saved as "no tabs" while the tabs lived on
    /// unsaved.
    ///
    /// Not clicking between its tabs: that is looking at what the agent is doing, which the
    /// agents panel brings the window forward for. Claiming on it made the window theirs
    /// behind their back, and X then closed it for good, agent's work and all, instead of
    /// hiding it again.
    /// </summary>
    internal void ClaimForPerson() => OpenedByAgent = false;

    private TabStripControl _tabStrip = null!;
    private Panel _toolbar = null!;
    private GlyphButton _backButton = null!;
    private GlyphButton _forwardButton = null!;
    private GlyphButton _reloadButton = null!;
    private GlyphButton _downloadsButton = null!;
    private GlyphButton _bookmarkButton = null!;
    private GlyphButton _menuButton = null!;
    private AddressBar _addressBar = null!;
    private FindBar _findBar = null!;
    private Panel _hostPanel = null!;
    private StatusStrip _statusStrip = null!;
    private AddressPill _addressPill = null!;
    private BookmarksBar _bookmarksBar = null!;
    private ToolStripStatusLabel _messageLabel = null!;
    private ToolStripStatusLabel _errorLabel = null!;
    private ToolStripStatusLabel _vpnLabel = null!;
    private ToolStripStatusLabel _memoryLabel = null!;
    private ToolStripStatusLabel _sleepLabel = null!;
    private ToolStripStatusLabel _blockedLabel = null!;
    // Made when first opened, and owned windows of this form: see OnFormClosed.
    private MainMenuFlyout? _mainMenu;
    private DownloadsFlyout? _downloadsPanel;
    private HistoryFlyout? _historyPanel;
    private AgentsFlyout? _agentsPanel;
    private PhoneFlyout? _phonePanel;
    private GlyphButton _phoneButton = null!;
    private GlyphButton _agentsButton = null!;
    private GlyphButton _hideButton = null!;
    private ToolTip _hideTip = null!;
    private AppSession? _agentsHookedTo;
    private System.Windows.Forms.Timer _statusTimer = null!;

    /// <summary>The first window: loads settings, brings up the shared services, restores the session.</summary>
    public MainForm(string? startupUrl)
        : this(session: null, startupUrl, isSecondaryWindow: false, restore: null)
    {
    }

    private MainForm(AppSession? session, string? startupUrl, bool isSecondaryWindow, SessionWindow? restore, Settings? settings = null)
    {
        _session = session;
        _settings = session?.Settings ?? settings ?? Settings.Load();
        _startupUrl = startupUrl;
        _isSecondaryWindow = isSecondaryWindow;
        _restore = restore;
        _shortcuts = new ShortcutRouter(this);
        BuildUi();
    }

    /// <summary>
    /// Opens another window on the same session, optionally without taking the screen.
    ///
    /// An agent driving this browser while somebody is using it has nowhere of its own to
    /// work: every tab it opens lands in the window being read, and every activation
    /// takes the view away mid-sentence. A window it can have to itself is the answer,
    /// and one that does not come to the front is what makes it bearable.
    /// </summary>
    internal static async Task<MainForm> OpenWindowAsync(AppSession session, bool focus)
    {
        var window = new MainForm(session, null, isSecondaryWindow: true, restore: null) { OpenedByAgent = true };
        if (focus)
        {
            window.Show();
        }
        else
        {
            // ShowWithoutActivation keeps the keyboard where it is. It does not decide the
            // stacking: a new top level window starts at the top, and whether Windows lets
            // it stay there depends on who has focus. Measured with a terminal in front it
            // came up well behind; with the person in Gergur itself it is the same process
            // asking, and nothing stopped it landing on top of the page they were reading.
            // So it is put directly behind whatever has focus, which is right either way.
            window._openWithoutStealingFocus = true;
            IntPtr inUse = GetForegroundWindow();
            window.Show();

            // Behind the top level window that owns whatever has focus, so a Gergur dialog
            // in front does not wedge this between it and the window the person is reading.
            IntPtr root = inUse == IntPtr.Zero ? IntPtr.Zero : GetAncestor(inUse, GA_ROOTOWNER);
            bool rootIsTopmost = root != IntPtr.Zero
                && (GetWindowLong(root, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            IntPtr after = BehindWhat(root, rootIsTopmost, window.Handle);
            if (!SetWindowPos(window.Handle, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
                DebugLog.WriteAlways("an agent window could not be placed behind the one in use");
        }
        await window.Ready;
        return window;
    }

    /// <summary>
    /// Which window a new agent window should sit directly behind.
    ///
    /// Normally the one in use. Not when that one is always on top, which is what the
    /// taskbar, Start, a picture in picture video or an always-on-top app are: linking a
    /// window in after a topmost one can make it topmost itself, and then the agent's
    /// window would sit on top of the person's work for good. Nor when there is nothing
    /// in use at all. The bottom of the stack is right in both.
    /// </summary>
    internal static IntPtr BehindWhat(IntPtr inUseRoot, bool inUseIsTopmost, IntPtr ours)
        => inUseRoot == IntPtr.Zero || inUseRoot == ours || inUseIsTopmost ? HWND_BOTTOM : inUseRoot;

    internal static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint GA_ROOTOWNER = 3;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private bool _openWithoutStealingFocus;

    /// <summary>
    /// True only for this window's first appearance, when an agent asked for it and the
    /// person at the keyboard did not. Cleared once it is up, because otherwise the
    /// window would refuse activation for the rest of its life: showing it again, or
    /// bringing it back from minimised, would leave it behind whatever was in front.
    /// </summary>
    protected override bool ShowWithoutActivation => _openWithoutStealingFocus;

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

        _tabStrip = new TabStripControl { Dock = DockStyle.Top, Height = 42, Font = new Font("Segoe UI", 9.5f) };
        _tabStrip.TabClicked += async (_, tab) => { await ActivateTabAsync(tab); };   // looking, not claiming: see ClaimForPerson
        _tabStrip.TabCloseClicked += async (_, tab) => { await CloseTabAsync(tab); };
        _tabStrip.NewTabClicked += (_, _) => { ClaimForPerson(); _ = NewTabAsync(); };
        _tabStrip.TabReordered += (_, move) => Tabs?.MoveTab(move.From, move.To);
        _tabStrip.TabTornOff += (_, drop) => _ = DropTabAsync(drop.Tab, drop.ScreenLocation);
        _tabStrip.TabDragMoved += (_, screen) => UpdateDropIndicators(screen);
        _tabStrip.TabDragEnded += (_, _) => ClearDropIndicators();
        _tabStrip.IsOverAnotherStrip = screen => DropTargetAt(screen) is not null;
        // The strip is the title bar now: it draws the window's buttons, and answers for
        // dragging and double clicking the window through FrameHit.
        //
        // None of these claim the window for the person, because the system buttons never did
        // and claiming is not harmless: a claimed window's tabs are saved as their session.
        // Close an agent's window after claiming it, with no window of the person's left, and
        // the agent's tabs would be written over theirs, the loss AppSession.SessionOf exists
        // to prevent. Closed afterwards from a posted call, not from inside the strip's own
        // mouse handler, which is about to be disposed with the window.
        _tabStrip.MinimizeClicked += (_, _) => WindowState = FormWindowState.Minimized;
        _tabStrip.MaximizeClicked += (_, _) =>
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        //
        // In an agent's window X hides it again rather than closing it: see SendBackToAgent.
        _tabStrip.CloseWindowClicked += (_, _) => BeginInvoke(new Action(CloseOrSendBack));

        _toolbar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.ToolbarBg };
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
        _addressPill = new AddressPill(_addressBar);
        _addressBar.SuggestionProvider = () => _session?.History.GetSuggestions() ?? Array.Empty<string>();
        _addressBar.NavigationRequested += async (_, url) => { ClaimForPerson(); await NavigateActiveAsync(url); };
        _addressBar.Escaped += (_, _) =>
        {
            ShowAddress(Tabs?.ActiveTab);
            Tabs?.ActiveTab?.FocusPage();
        };

        _downloadsButton = MakeToolButton(Glyphs.Download, 0);
        _bookmarkButton = MakeToolButton(Glyphs.StarOutline, 0);
        _menuButton = MakeToolButton(Glyphs.Menu, 0);
        // Beside the menu: who is driving the browser, there whenever agents may be.
        _agentsButton = MakeToolButton(AgentGlyphs.Robot, 0);
        _agentsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _agentsButton.AccessibleName = "Agents";
        _agentsButton.Visible = _settings.AgentServerEnabled;
        _agentsButton.Click += (_, _) => ToggleAgentsPanel();
        // In an agent's window only, nearest the address bar: back behind the person's
        // windows, the agent still working in it. Shown from OpenedByAgent.
        _hideButton = MakeToolButton(AgentGlyphs.SendBack, 0);
        _hideButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _hideButton.AccessibleName = "Hide this agent window";
        _hideButton.Visible = false;
        _hideButton.Click += (_, _) => SendBackToAgent();
        _hideTip = new ToolTip();
        _hideTip.SetToolTip(_hideButton, "Hide this agent window. The agent keeps working in it.");
        // The phone drop, a click away: and a file dragged onto it goes to the phone.
        _phoneButton = MakeToolButton(PhoneGlyphs.Phone, 0);
        _phoneButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _phoneButton.AccessibleName = "Phone drop";
        _phoneButton.Click += (_, _) => TogglePhonePanel();
        _phoneButton.AllowDrop = true;
        _phoneButton.DragEnter += OnPhoneButtonDragEnter;
        _phoneButton.DragDrop += (_, e) => _phonePanel?.OnDragDrop(_phoneButton, e);
        _phoneButton.DragLeave += (_, _) => _phonePanel?.SetDragging(false);
        _downloadsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _bookmarkButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _menuButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _downloadsButton.Click += (_, _) => ToggleDownloadsPanel();
        _bookmarkButton.Click += (_, _) => ToggleBookmark();
        _menuButton.Click += (_, _) => ToggleMainMenu();

        _toolbar.Controls.AddRange(
            [_backButton, _forwardButton, _reloadButton, _addressPill,
             _hideButton, _downloadsButton, _bookmarkButton, _phoneButton, _agentsButton, _menuButton]);
        _toolbar.Resize += (_, _) => LayoutToolbar();
        // The hairline between the chrome and the page, drawn here when the bookmarks bar is
        // hidden, since then the toolbar is the chrome's bottom edge.
        _toolbar.Paint += (_, e) =>
        {
            if (_bookmarksBar is { Visible: true })
                return;
            using var edge = new Pen(Theme.TabStripBg);
            e.Graphics.DrawLine(edge, 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
        };

        _bookmarksBar = new BookmarksBar { Dock = DockStyle.Top, Height = 32, Visible = _settings.ShowBookmarksBar };
        _bookmarksBar.OpenRequested += (_, open) => _ = OpenBookmarkAsync(open.Url, open.NewTab);
        _bookmarksBar.RemoveRequested += (_, url) =>
        {
            try { _session?.Bookmarks.Remove(url); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A menu click has nothing above it to catch this.
                SayBookmarkNotSaved(ex, "The bookmark could not be removed: something else has the bookmarks file open.");
            }
        };
        _bookmarksBar.ManageRequested += (_, _) => OpenBookmarks();
        _bookmarksBar.HideRequested += (_, _) => ToggleBookmarksBar();
        _bookmarksBar.VisibleChanged += (_, _) => _toolbar.Invalidate();   // it owns the bottom hairline now

        _statusStrip = new StatusStrip
        {
            BackColor = Theme.TabStripBg,
            ForeColor = Theme.TextDim,
            SizingGrip = false,
            Renderer = new ChromeRenderer(),
            Padding = new Padding(6, 0, 10, 0),
            // Off by default on a StatusStrip, unlike every other ToolStrip. Without it the
            // memory figures below, and the error list, were set as tooltips nobody could see.
            ShowItemToolTips = true,
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
        _vpnLabel = new ToolStripStatusLabel { ForeColor = Theme.Accent, Padding = new Padding(8, 0, 0, 0) };
        _memoryLabel = new ToolStripStatusLabel { ForeColor = Theme.TextDim };
        _sleepLabel = new ToolStripStatusLabel { ForeColor = Theme.TextDim };
        _blockedLabel = new ToolStripStatusLabel { ForeColor = Theme.TextDim };
        // The engine's memory and renderer count are for whoever wants them, not for every
        // glance at the window: they ride along as the tooltip of the items still shown.
        _statusStrip.Items.AddRange([_messageLabel, _errorLabel, _vpnLabel, _sleepLabel, _blockedLabel]);

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
        Controls.Add(_bookmarksBar);
        Controls.Add(_toolbar);
        Controls.Add(_tabStrip);

        LayoutToolbar();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _statusTimer.Tick += (_, _) => { UpdateStatus(); UpdateAgentsButton(); UpdatePhoneButton(); };
    }

    private GlyphButton MakeToolButton(string glyph, int x)
        => new(glyph) { Location = new Point(x, 7) };

    private void OnDownloadsChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnDownloadsChanged(sender, e));
            return;
        }
        UpdateDownloadsButton();
        if (_downloadsPanel is { Visible: true } panel)
            panel.Relayout();
        _downloadsPushPending = true;
        _pagePushTimer.Start();
    }

    private void OnBookmarksChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnBookmarksChanged(sender, e));
            return;
        }
        UpdateChrome();   // the star
        if (_session is not null)
            _bookmarksBar.SetBookmarks(_session.Bookmarks.Items);
        _bookmarksPushPending = true;
        _pagePushTimer.Start();
    }

    /// <summary>
    /// Tells this window's open Downloads, Bookmarks and new tab pages what changed. The
    /// Downloads page gets the list itself; the others are told to ask again.
    /// </summary>
    private void PushToOwnPages()
    {
        _pagePushTimer.Stop();
        if (_session is null || Tabs is null)
            return;
        bool downloadsDue = _downloadsPushPending, bookmarksDue = _bookmarksPushPending;
        _downloadsPushPending = _bookmarksPushPending = false;
        string? downloads = null;
        foreach (var tab in Tabs.Tabs)
        {
            // Not into a sleeping tab: calls into a suspended view can wake it, and a download
            // reports four times a second. The page asks again when it is shown.
            if (tab.State == TabState.Suspended)
                continue;
            switch (tab.ShownPage)
            {
                case InternalPages.Page.Downloads when downloadsDue:
                    // Built only once there is a Downloads page to send it to.
                    downloads ??= JsonSerializer.Serialize(new { @event = "downloads", items = _session.Downloads.Items.Select(InternalPages.Describe).ToArray() });
                    tab.PostToPage(downloads, InternalPages.Page.Downloads);
                    break;
                case InternalPages.Page.Bookmarks or InternalPages.Page.Home when bookmarksDue:
                    tab.PostToPage("""{"event":"bookmarks"}""", tab.ShownPage!.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// What the address bar shows for a tab: nothing for the new tab page, so it is ready to
    /// type in, gergur://history and the like for the browser's own pages rather than a
    /// file path into the build output, and the url for everything else.
    /// </summary>
    private static string AddressFor(Tab? tab)
    {
        if (tab is null || HomePage.IsHome(tab.Url))
            return "";
        return InternalPages.Identify(tab.Url) is { } page ? InternalPages.AddressOf(page) : tab.Url;
    }

    /// <summary>
    /// Shows one of the browser's own pages: the tab already showing it in this window if
    /// there is one, a new tab otherwise. Opening History twice used to give two windows'
    /// worth of the same list; now it is one tab, like any other browser.
    /// </summary>
    private async Task OpenOwnPageAsync(InternalPages.Page page)
    {
        if (Tabs is null)
            return;
        if (Tabs.Tabs.FirstOrDefault(t => t.ShownPage == page) is { } open)
        {
            await Tabs.ActivateAsync(open);
            return;
        }
        await Tabs.CreateTabAsync(InternalPages.UrlOf(page));
    }

    /// <summary>What the browser's own page in this tab may act on.</summary>
    private InternalPages.Services? PageServicesFor(Tab tab)
    {
        if (_session is null || Tabs is not { } manager)
            return null;
        return new InternalPages.Services(
            _session.History,
            _session.Bookmarks,
            _session.Downloads,
            // A plain click replaces the page in its own tab; Ctrl or middle click opens a
            // tab behind it, as a link does anywhere else.
            (url, newTab) => _ = newTab ? (Task)manager.CreateTabAsync(url, activate: false) : tab.NavigateAsync(url),
            new SettingsHost(this, _session));
    }

    /// <summary>
    /// What the settings page acts through. Anything that puts up a window (the phone
    /// drop's question, the pairing link) or closes this one (a restart) is done after the
    /// page's request has been answered: from inside the engine's message handler, showing
    /// a window is the kind of re-entry it throws on.
    /// </summary>
    private sealed class SettingsHost(MainForm window, AppSession session) : ISettingsHost
    {
        public Settings Settings => session.Settings;
        public Settings StartedWith => session.StartedWith;
        public bool PhoneRunning => session.PhoneBridge?.IsRunning == true;
        public IReadOnlyList<string> VpnProfiles => VpnTunnel.ListProfiles().Select(p => p.Name).ToArray();

        public string? Commit()
        {
            string? problem = null;
            try
            {
                if (!session.Settings.Save())
                    problem = "It was not saved: Gergur could not read its settings file when it started, so it is leaving that file alone. The change lasts until Gergur closes.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.WriteAlways($"settings not saved: {ex.Message}");
                problem = "It was not saved: the settings file could not be written (something may have it open, or the disk may be full). It is in force now, and the next save that succeeds writes it out.";
            }
            ApplyLiveSettings(session);
            window.PushSettingsChanged();
            return problem;
        }

        public void TogglePhone() => Later(() =>
        {
            window.TogglePhoneBridge();
            window.PushSettingsChanged();
        });

        public void ShowPairing() => Later(window.ShowPairingLink);

        public void Restart() => Later(window.RestartForNewEngineFlags);

        private void Later(Action action)
        {
            if (!window.IsDisposed && window.IsHandleCreated)
                window.BeginInvoke(action);
        }
    }

    /// <summary>Tells every open settings page, in every window, to read the settings again.</summary>
    private void PushSettingsChanged()
    {
        foreach (var window in _session?.Windows ?? [])
        {
            foreach (var tab in window.Tabs?.Tabs ?? [])
            {
                if (tab.State != TabState.Suspended && tab.ShownPage == InternalPages.Page.Settings)
                    tab.PostToPage("""{"event":"settings"}""", InternalPages.Page.Settings);
            }
        }
    }

    /// <summary>The running count the button is currently painted for.</summary>
    private int _downloadsShown = -1;

    /// <summary>
    /// The button carries the accent while something is downloading. A dedicated button
    /// is only worth the room it takes if it says something the menu could not, and what
    /// it says is that a download is running: the window that lists them is somewhere
    /// else, and a download you have forgotten about is the one you wanted to watch.
    /// </summary>
    private void UpdateDownloadsButton()
    {
        // Changed fires on every progress report of every download, so this runs many
        // times a second while one is running. Setting GlyphColor invalidates the button
        // whether or not the colour differs, and resetting AccessibleName re-announces it
        // to a screen reader, so the early exit is what keeps a download from repainting
        // the toolbar and interrupting a reader for the whole of its length.
        int running = _session?.Downloads.RunningCount ?? 0;
        if (running == _downloadsShown)
            return;
        _downloadsShown = running;

        _downloadsButton.GlyphColor = running > 0 ? Theme.Accent : Theme.Text;
        _downloadsButton.AccessibleName = running > 0
            ? $"Downloads, {running} in progress"
            : "Downloads";
    }

    /// <summary>
    /// Re-lay the toolbar when the window moves to a differently scaled monitor. The
    /// only other trigger is the toolbar's own Resize, which fires while the buttons are
    /// still at their old size, so the positions it works out then are for widths that
    /// are about to change.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutToolbar();
    }

    // ------------------------------------------------------------------ the frame

    /// <summary>
    /// Whether the system title bar has been taken away, which is every window with the
    /// ordinary frame. A page going fullscreen swaps the frame for none at all, and then
    /// there is nothing here to adjust.
    /// </summary>
    private bool HasCustomTitleBar => FormBorderStyle == FormBorderStyle.Sizable;

    // Whether the window was maximised or minimised at its last position change: see
    // WindowFrame.IsComingBack.
    private bool _wasMaximizedOrMinimized;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WindowFrame.WM_NCCALCSIZE && m.WParam != IntPtr.Zero && HasCustomTitleBar)
        {
            // lParam points at the window rectangle the system proposes and gets the client
            // area back, and the second int of it is the top edge. The system works out all
            // four sides, title bar included. The top it works out is thrown away and the
            // client area runs from the edge instead, so the tab strip is the first thing in
            // the window and takes the title bar's place.
            int proposedTop = System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 4);
            base.WndProc(ref m);
            System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 4,
                WindowFrame.ClientTop(proposedTop, WindowFrame.IsZoomed(Handle), WindowFrame.FrameThickness(DeviceDpi)));
            return;
        }
        if (m.Msg == WindowFrame.WM_NCHITTEST && HasCustomTitleBar)
        {
            base.WndProc(ref m);
            m.Result = (IntPtr)FrameHit((int)m.Result, PointToClient(WindowFrame.PointFrom(m.LParam)));
            return;
        }
        if (m.Msg == WindowFrame.WM_WINDOWPOSCHANGED)
        {
            // Whatever the border style: the system's own restore is right, and the form's
            // arithmetic after it is not once the title bar has gone. See IsComingBack.
            bool zoomed = WindowFrame.IsZoomed(Handle), iconic = WindowFrame.IsIconic(Handle);
            bool comingBack = WindowFrame.IsComingBack(_wasMaximizedOrMinimized, zoomed, iconic);
            var placed = comingBack ? WindowFrame.WindowRect(Handle) : Rectangle.Empty;
            _wasMaximizedOrMinimized = zoomed || iconic;
            base.WndProc(ref m);
            if (comingBack && WindowState == FormWindowState.Normal && Bounds != placed)
                Bounds = placed;
            return;
        }
        // Alt+F4 and the taskbar's Close, in an agent's window: hidden, as X does. The low four
        // bits of the command are the system's own. A quit or an agent's own close is WM_CLOSE.
        if (_openedByAgent && WindowStacking.IsSystemClose(m.Msg, m.WParam))
        {
            SendBackToAgent();
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// What a point in this window answers to a hit test, given what the system said.
    ///
    /// The top few pixels grab the window's edge for resizing, and bare strip grabs the
    /// window itself: the two things the system title bar used to answer for. The strip
    /// asks this too, because a child's own answer of "client" would stop the window ever
    /// being asked.
    /// </summary>
    internal int FrameHit(int defaultHit, Point client)
    {
        if (!HasCustomTitleBar)
            return defaultHit;

        int edge = WindowFrame.TopEdgeHit(defaultHit, client, ClientSize.Width,
            border: Scaled(5), corner: Scaled(16), canResize: WindowState != FormWindowState.Maximized);
        if (edge != defaultHit)
            return edge;

        if (defaultHit == WindowFrame.HTCLIENT && _tabStrip.Visible && _tabStrip.Bounds.Contains(client)
            && _tabStrip.IsCaptionArea(new Point(client.X - _tabStrip.Left, client.Y - _tabStrip.Top)))
            return WindowFrame.HTCAPTION;
        return defaultHit;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Null while the window is still being built, which resizes it before the strip exists.
        if (_tabStrip is not null)
            _tabStrip.WindowMaximized = WindowState == FormWindowState.Maximized;
    }

    /// <summary>Scales a design-time pixel count to this window's dpi.</summary>
    private int Scaled(int atNinetySix) => (int)Math.Round(atNinetySix * DeviceDpi / 96.0);

    /// <summary>
    /// The buttons right of the address bar, right to left, leaving out the agents button
    /// while the agent API is off: a hidden button holding its place leaves a gap.
    ///
    /// Decided from the setting, never from Control.Visible. That reads false for every
    /// control until its window is first shown, and the toolbar is laid out while the window
    /// is still being built: filtering on it left no buttons at all, and the first window
    /// threw at startup (IndexOutOfRange in this method) before it ever appeared.
    /// </summary>
    internal GlyphButton[] ToolbarButtons
    {
        get
        {
            GlyphButton[] always = _settings.AgentServerEnabled
                ? [_menuButton, _agentsButton, _phoneButton, _bookmarkButton, _downloadsButton]
                : [_menuButton, _phoneButton, _bookmarkButton, _downloadsButton];
            // The hide button, nearest the address bar, in an agent's window only.
            return _openedByAgent ? [.. always, _hideButton] : always;
        }
    }

    /// <summary>A window built on the given settings rather than the real file, for tests. Never shown, never given a session.</summary>
    internal static MainForm ForLayoutTest(Settings settings, bool agentsWindow = false)
        => new(session: null, startupUrl: null, isSecondaryWindow: false, restore: null, settings) { OpenedByAgent = agentsWindow };

    /// <summary>Test hook: what X in the window's corner does now.</summary>
    internal bool CloseHides => _openedByAgent;

    /// <summary>X in the window's corner: closes a window of the person's, hides an agent's.</summary>
    private void CloseOrSendBack()
    {
        if (_openedByAgent)
            SendBackToAgent();
        else
            Close();
    }

    /// <summary>
    /// Hides an agent's window again: back behind the person's own windows, as it was
    /// opened, with the one of theirs they used last in front. What X, Alt+F4, the taskbar's
    /// Close and the hide button do in a window an agent opened.
    ///
    /// Closing it would end the agent's work under it: every tab it had open, mid task, with
    /// nothing to tell it why. The person brought it forward to look, from the agents panel,
    /// and X is the reflex for "done looking". Behind rather than minimised, because behind
    /// is how the agent had it and what its screenshots are taken from; a minimised page
    /// counts as hidden to the engine and stops drawing.
    ///
    /// An agent closing its own window, Gergur quitting, and the person closing its tabs all
    /// still close it: those come through Close or WM_CLOSE, not through here.
    /// </summary>
    internal void SendBackToAgent()
    {
        if (_closing || IsDisposed || !IsHandleCreated)
            return;
        _mainMenu?.Dismiss();
        _downloadsPanel?.Dismiss();
        _historyPanel?.Dismiss();
        _agentsPanel?.Dismiss();
        _phonePanel?.Dismiss();
        MainForm? theirs = _session is { } session
            ? AppSession.WindowToReturnTo(session.Windows, session.LastPersonsWindow, this,
                w => w is { OpenedByAgent: false, IsDisposed: false, Visible: true } && w.WindowState != FormWindowState.Minimized)
            : null;
        if (!WindowStacking.SendBack(Handle, theirs))
            DebugLog.WriteAlways("an agent window could not be sent back");
    }

    private void LayoutToolbar()
    {
        // Right to left, off the widths the buttons actually have rather than the ones
        // they had at 96 dpi. See ToolbarLayout for what that cost.
        GlyphButton[] rightHand = ToolbarButtons;
        int gap = Scaled(4);
        int[] lefts = ToolbarLayout.RightToLeft(
            _toolbar.Width, rightHand.Select(b => b.Width).ToArray(), gap, Scaled(8));
        for (int i = 0; i < rightHand.Length; i++)
            rightHand[i].Location = new Point(lefts[i], (_toolbar.Height - rightHand[i].Height) / 2);

        _addressPill.Height = Scaled(34);
        _addressPill.Location = new Point(Scaled(124), (_toolbar.Height - _addressPill.Height) / 2);
        _addressPill.Width = Math.Max(Scaled(100), lefts[^1] - Scaled(8) - _addressPill.Left);
    }

    /// <summary>
    /// Opens the main menu, or closes it when it is open. It is placed on the screen the
    /// button is actually on (<see cref="MenuPlacement"/>): left to itself a drop-down slid
    /// onto the neighbouring monitor, measured with three side by side.
    ///
    /// The entries are built here, each time, so what the menu says is what is true when it
    /// opens: the zoom of the tab in front, whether the blocker is on, the vpn in force.
    /// </summary>
    private void ToggleMainMenu()
    {
        _downloadsPanel?.Dismiss();
        _historyPanel?.Dismiss();
        _phonePanel?.Dismiss();
        _mainMenu ??= new MainMenuFlyout();
        if (_mainMenu.Visible)
        {
            _mainMenu.Dismiss();
            return;
        }
        if (_mainMenu.JustDismissed)
            return;   // the click on the button that just closed it
        _mainMenu.Open(this, _menuButton, MainMenuEntries());
    }

    /// <summary>
    /// Opens the downloads panel from its button, or closes it. Ctrl+J still goes straight
    /// to the full page, and the panel's last line does too.
    /// </summary>
    private void ToggleDownloadsPanel()
    {
        if (_session is null)
        {
            OpenDownloads();
            return;
        }
        _mainMenu?.Dismiss();
        _historyPanel?.Dismiss();
        _phonePanel?.Dismiss();
        var panel = DownloadsPanel(_session);
        if (panel.Visible)
        {
            panel.Dismiss();
            return;
        }
        if (panel.JustDismissed)
            return;
        panel.ShowFromButton(this, _downloadsButton);
    }

    /// <summary>
    /// A download just started from a tab in this window: show it in the panel, which goes
    /// again by itself. Only for the person: an agent's window, a window in the background
    /// or one showing a film fullscreen gets nothing put over it.
    /// </summary>
    private void ShowNewDownload()
    {
        if (_session is null || _closing || OpenedByAgent || IsPageFullScreen || !Visible
            || WindowState == FormWindowState.Minimized || GetForegroundWindow() != Handle)
            return;
        if (_mainMenu is { Visible: true })
            return;
        DownloadsPanel(_session).ShowForNewDownload(this, _downloadsButton);
    }

    /// <summary>
    /// The menu's History tile: the last few places in a panel under the menu button, with
    /// the way through to the full page. Ctrl+H still goes straight to the page.
    /// </summary>
    private void ShowHistoryPanel()
    {
        if (_session is not { } session)
        {
            OpenHistory();
            return;
        }
        _mainMenu?.Dismiss();
        _downloadsPanel?.Dismiss();
        _phonePanel?.Dismiss();
        _historyPanel ??= new HistoryFlyout(
            () => session.History.TryRead(max: 200),
            url => { ClaimForPerson(); _ = NavigateActiveAsync(url); },
            OpenHistory);
        _historyPanel.ShowFromButton(this, _menuButton);
    }

    /// <summary>The menu's Downloads tile: the downloads panel, hanging from its own button.</summary>
    private void ShowDownloadsPanel()
    {
        if (_session is not { } session)
        {
            OpenDownloads();
            return;
        }
        _mainMenu?.Dismiss();
        _historyPanel?.Dismiss();
        DownloadsPanel(session).ShowFromButton(this, _downloadsButton);
    }

    /// <summary>
    /// Opens the agents panel from its button, or closes it: who has used the browser,
    /// what each did last, and their tabs, any of which can be brought forward.
    /// </summary>
    private void ToggleAgentsPanel()
    {
        if (_session is not { } session)
            return;
        HookAgents();
        _mainMenu?.Dismiss();
        _downloadsPanel?.Dismiss();
        _historyPanel?.Dismiss();
        _phonePanel?.Dismiss();
        if (_agentsPanel is null)
        {
            _agentsPanel = new AgentsFlyout(session.Agents.Snapshot, AgentTab, id => _ = ShowAgentTabAsync(id), AgentApiLine);
            session.Agents.Changed += _agentsPanel.AgentsChanged;
        }
        if (_agentsPanel.Visible)
        {
            _agentsPanel.Dismiss();
            return;
        }
        if (_agentsPanel.JustDismissed)
            return;
        _agentsPanel.ShowFrom(this, _agentsButton);
    }

    /// <summary>
    /// Opens the phone panel from its button, or closes it: send the page, a file or a
    /// message to the phone, see what came from it, pair one, or turn the drop on.
    /// </summary>
    private void TogglePhonePanel()
    {
        if (_session is not { } session)
            return;
        var panel = PhonePanel(session);
        if (panel.Visible)
        {
            panel.Dismiss();
            return;
        }
        if (panel.JustDismissed)
            return;
        ShowPhonePanel(session);
    }

    private PhoneFlyout PhonePanel(AppSession session)
    {
        if (_phonePanel is null)
        {
            _phonePanel = new PhoneFlyout(session.Drop, new PhoneActions(
                IsRunning: () => _session?.PhoneBridge?.IsRunning == true,
                CanSendPage: () => Tabs?.ActiveTab is { } tab && !HomePage.IsHome(tab.Url),
                SendPage: SendPageToPhone,
                Toggle: TogglePhoneBridge,
                Pair: ShowPairingLink,
                ShowAll: OpenDrop,
                OpenUrl: url => { ClaimForPerson(); _ = NewTabAsync(url); },
                Say: ShowMessage));
            session.Drop.Changed += _phonePanel.DropChanged;
            _phonePanel.Dismissed += (_, _) => MarkDropSeen();
        }
        return _phonePanel;
    }

    private void ShowPhonePanel(AppSession session)
    {
        _mainMenu?.Dismiss();
        _downloadsPanel?.Dismiss();
        _historyPanel?.Dismiss();
        _agentsPanel?.Dismiss();
        MarkDropSeen();
        PhonePanel(session).ShowFromButton(this, _phoneButton);
    }

    /// <summary>
    /// A file dragged over the phone button opens the panel under it, so it can be let go
    /// there or on the panel. Only while the drop is on: off, the drag is refused.
    /// </summary>
    private void OnPhoneButtonDragEnter(object? sender, DragEventArgs e)
    {
        if (_session is not { } session)
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        var panel = PhonePanel(session);
        if (!panel.Visible)
            ShowPhonePanel(session);
        panel.OnDragEnter(sender, e);
    }

    /// <summary>Everything from the phone up to now has been seen: the dot goes, in every window.</summary>
    private void MarkDropSeen()
    {
        if (_session is not { } session)
            return;
        session.DropSeenUtc = DateTime.UtcNow;
        foreach (var window in session.Windows)
            window.UpdatePhoneButton();
    }

    /// <summary>The drop changed, on whatever thread changed it.</summary>
    private void OnDropChanged(object? sender, EventArgs e)
    {
        if (_closing || IsDisposed || !IsHandleCreated)
            return;
        try { BeginInvoke(UpdatePhoneButton); }
        catch (InvalidOperationException) { }   // the handle went between the check and the call
    }

    /// <summary>A dot on the phone button for anything from the phone not seen yet; dim while the drop is off.</summary>
    private void UpdatePhoneButton()
    {
        if (_session is not { } session || _phoneButton.IsDisposed)
            return;
        // Open, the panel is showing it: seen.
        int unseen = _phonePanel is { Visible: true } ? 0 : UnseenFromPhone(session.Drop.Items, session.DropSeenUtc);
        bool running = session.PhoneBridge?.IsRunning == true;
        Color? dot = unseen > 0 ? Theme.Accent : null;
        if (_phoneButton.Dot != dot)
            _phoneButton.Dot = dot;
        var ink = running ? Theme.Text : Theme.TextDim;
        if (_phoneButton.GlyphColor != ink)
            _phoneButton.GlyphColor = ink;
        string name = unseen switch
        {
            0 => running ? "Phone drop" : "Phone drop (off)",
            1 => "Phone drop, 1 new from your phone",
            _ => $"Phone drop, {unseen} new from your phone",
        };
        if (_phoneButton.AccessibleName != name)
            _phoneButton.AccessibleName = name;
    }

    /// <summary>How many things came from the phone after <paramref name="seenUtc"/>.</summary>
    internal static int UnseenFromPhone(IReadOnlyList<DropItem> items, DateTime seenUtc)
        => items.Count(i => i.From == "phone" && i.AddedUtc > seenUtc);

    /// <summary>Listens for agents acting, once this window has a session to listen to.</summary>
    private void HookAgents()
    {
        if (_session is not { } session || ReferenceEquals(_agentsHookedTo, session))
            return;
        session.Agents.Changed += OnAgentsChanged;
        _agentsHookedTo = session;
    }

    /// <summary>An agent acted, on the agent server's thread.</summary>
    private void OnAgentsChanged()
    {
        if (_closing || IsDisposed || !IsHandleCreated)
            return;
        try { BeginInvoke(UpdateAgentsButton); }
        catch (InvalidOperationException) { }   // the handle went between the check and the call
    }

    /// <summary>The dot on the agents button while an agent is active, gone 30 seconds after.</summary>
    private void UpdateAgentsButton()
    {
        if (_session is not { } session)
            return;
        HookAgents();
        bool active = session.Agents.AnyActive(DateTime.UtcNow);
        _agentsButton.Dot = active ? Theme.Live : null;
        _agentsButton.AccessibleName = active ? "Agents (an agent is working)" : "Agents";
    }

    /// <summary>A tab by id, in any window, as the agents panel shows it.</summary>
    private AgentTabInfo? AgentTab(string id)
    {
        foreach (var window in _session?.Windows ?? [])
        {
            if (window.Tabs?.Tabs.FirstOrDefault(t => t.Id == id) is { } tab)
                return new AgentTabInfo(tab.Id, tab.Title, InternalPages.NameOf(tab.Url), window.OpenedByAgent);
        }
        return null;
    }

    /// <summary>
    /// Brings an agent's tab forward: its window to the front, restored if minimised, with
    /// the tab chosen. An agent's window is a window like any other, only opened behind,
    /// so this is all "opening their hidden tab" needs.
    /// </summary>
    private async Task ShowAgentTabAsync(string id)
    {
        foreach (var window in _session?.Windows ?? [])
        {
            if (window.Tabs?.Tabs.FirstOrDefault(t => t.Id == id) is not { } tab)
                continue;
            try
            {
                if (window.WindowState == FormWindowState.Minimized)
                    window.WindowState = FormWindowState.Normal;
                await window.Tabs.ActivateAsync(tab);
                window.Activate();
            }
            catch (Exception ex)
            {
                DebugLog.WriteAlways($"an agent's tab could not be shown: {ex.Message}");
                ShowMessage("That tab could not be shown.");
            }
            return;
        }
        ShowMessage("That tab has closed.");
    }

    /// <summary>The agents panel's last line: whether agents can connect, and where.</summary>
    private string AgentApiLine()
        => _session is { } s && s.Settings.AgentServerEnabled
            ? $"Agent API on 127.0.0.1:{s.Settings.AgentServerPort} \u00B7 token protected"
            : "Agent API off. Turn it on in Settings.";

    private DownloadsFlyout DownloadsPanel(AppSession session)
        => _downloadsPanel ??= new DownloadsFlyout(session.Downloads, OpenDownloads, ShowMessage);

    /// <summary>
    /// What the main menu holds, in order. Everything the old list had is here; the vpn,
    /// phone drop and bookmark lists open to the side exactly as they did, built by the same
    /// methods.
    /// </summary>
    private IReadOnlyList<MenuEntry> MainMenuEntries()
    {
        var blocker = _session?.Blocker;
        return
        [
            new MenuAction("New tab", "Ctrl+T", () => { ClaimForPerson(); _ = NewTabAsync(); }, MenuGlyphs.NewTab),
            new MenuAction("New window", "Ctrl+N", () => _ = NewWindowAsync(), MenuGlyphs.NewWindow),
            new MenuAction("Reopen closed tab", "Ctrl+Shift+T", () => { ClaimForPerson(); _ = ReopenClosedTabAsync(); }, MenuGlyphs.Reopen),
            new MenuSeparator(),
            new MenuTiles(
            [
                new MenuTile(MenuGlyphs.History, "History", ShowHistoryPanel),
                new MenuTile(Glyphs.Download, "Downloads", ShowDownloadsPanel,
                    () => (_session?.Downloads.RunningCount ?? 0) is > 0 and var n ? n.ToString(System.Globalization.CultureInfo.CurrentCulture) : null),
                new MenuTile(MenuGlyphs.Bookmarks, "Bookmarks", OpenBookmarks),
            ]),
            new MenuSeparator(),
            new MenuAction("Bookmark this page", "Ctrl+D", ToggleBookmark, Glyphs.StarOutline),
            new MenuSubmenu("Bookmarks", () => _session is { Bookmarks.Items.Count: > 0 and var count } ? count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : null,
                BuildBookmarksMenu, MenuGlyphs.Bookmarks),
            new MenuZoom(
                () => (int)Math.Round((Tabs?.ActiveTab?.ZoomFactor ?? 1.0) * 100),
                () => ZoomBy(-0.1), () => ZoomBy(0.1), ZoomReset),
            new MenuAction("Find in page", "Ctrl+F", OpenFind, MenuGlyphs.Find),
            new MenuToggle("Mute tab", () => "Ctrl+M", () => Tabs?.ActiveTab?.IsMuted == true,
                Tabs?.ActiveTab is null ? null : ToggleMuteActiveTab, MenuGlyphs.Mute),
            new MenuAction("Print", "Ctrl+P", PrintActive, MenuGlyphs.Print),
            new MenuAction("Save as PDF", null, () => _ = SaveAsPdfAsync(), MenuGlyphs.Save),
            new MenuSeparator(),
            new MenuToggle("Ad blocking",
                // "no host list" rather than a count, because the built-in path rules mean the
                // count is never zero, and "5 rules" would read like a list had loaded.
                () => blocker?.HasHostList == true ? $"{blocker.RuleCount:N0} rules" : "no host list",
                () => blocker?.Enabled == true,
                blocker is null ? null : ToggleBlocking, MenuGlyphs.Shield),
            .. RelaxEntry(),
            new MenuSubmenu("VPN", VpnMenuDetail, BuildVpnMenu, MenuGlyphs.Vpn),
            new MenuSubmenu("Phone drop", () => _session?.PhoneBridge?.IsRunning == true ? "on" : "off", BuildPhoneMenu, MenuGlyphs.Phone),
            new MenuSeparator(),
            new MenuFooter(
            [
                new MenuAction("Settings", null, OpenSettings, MenuGlyphs.Settings),
                new MenuAction("DevTools", "F12", OpenDevTools, MenuGlyphs.DevTools),
                new MenuSubmenu("More", () => null, BuildMoreToolsMenu, Glyphs.More),
            ]),
        ];
    }

    /// <summary>The vpn line's state, from what the engine runs with: see BuildVpnMenu.</summary>
    private string? VpnMenuDetail()
    {
        if (_session is null)
            return "not ready";
        if (!ProxyInForce)
            return "off";
        return _session.Vpn.ActiveProfileName ?? VpnTunnel.ResolveProfile(_settings.VpnProfile)?.Name ?? "on";
    }

    /// <summary>
    /// The menu's "Relax blocking on this site" switch, for the site in front; nothing on a
    /// page that is not a web page. See <see cref="Blocking.RelaxedSites"/>.
    /// </summary>
    private IEnumerable<MenuEntry> RelaxEntry()
    {
        string site = Blocking.RelaxedSites.SiteOf(Tabs?.ActiveTab?.Url);
        if (site.Length == 0)
            yield break;
        yield return new MenuToggle($"Relax blocking on {site}",
            () => IsRelaxed(site) ? "ads load, sped up" : null,
            () => IsRelaxed(site),
            () => SetRelaxed(site, !IsRelaxed(site)),
            MenuGlyphs.Relax);
    }

    private bool IsRelaxed(string site) => Blocking.RelaxedSites.Parse(_settings.RelaxedSites).Contains(site);

    /// <summary>
    /// Relaxes blocking on a site, or stops, and reloads the tab in front when it is on that
    /// site: the page script is given to a page as it is created, so the page showing has
    /// the old one until it loads again.
    /// </summary>
    private void SetRelaxed(string site, bool relaxed)
    {
        if (_session is not { } session)
            return;
        _settings.RelaxedSites = Blocking.RelaxedSites.With(_settings.RelaxedSites, site, relaxed);
        SaveSettingsOrSay(StillInForce);
        ApplyLiveSettings(session);
        if (Tabs?.ActiveTab is { } tab && Blocking.RelaxedSites.SiteOf(tab.Url) == site)
            tab.Reload();
        ShowMessage(relaxed ? $"Blocking relaxed on {site}: its ads load and video ads are muted and sped up." : $"Blocking back on for {site}.");
    }

    private void ToggleBlocking()
    {
        var blocker = _session?.Blocker;
        if (blocker is null)
            return;
        blocker.Enabled = !blocker.Enabled;
        _settings.BlocklistEnabled = blocker.Enabled;
        SaveSettingsOrSay(StillInForce);   // in force either way; this only says if it will not last
        UpdateStatus();
    }

    /// <summary>Bookmarks side list: the bar, the page, then every bookmark.</summary>
    private ToolStripMenuItem BuildBookmarksMenu()
    {
        var bookmarks = new ToolStripMenuItem("Bookmarks");
        bookmarks.DropDownItems.Add(new ToolStripMenuItem("Show bookmarks bar\tCtrl+Shift+B", null, (_, _) => ToggleBookmarksBar())
        {
            Checked = _settings.ShowBookmarksBar,
        });
        bookmarks.DropDownItems.Add(new ToolStripMenuItem("Manage bookmarks\tCtrl+Shift+O", null, (_, _) => OpenBookmarks()));
        if (_session is { Bookmarks.Items.Count: > 0 })
        {
            bookmarks.DropDownItems.Add(new ToolStripSeparator());
            foreach (var bookmark in _session.Bookmarks.Items)
            {
                var item = new ToolStripMenuItem(Truncate(bookmark.Title, 60)) { ToolTipText = bookmark.Url };
                var url = bookmark.Url;
                item.Click += (_, _) => { ClaimForPerson(); _ = NavigateActiveAsync(url); };
                bookmarks.DropDownItems.Add(item);
            }
        }
        return bookmarks;
    }

    /// <summary>The tools nobody needs every day.</summary>
    private ToolStripMenuItem BuildMoreToolsMenu()
    {
        var more = new ToolStripMenuItem("More");
        more.DropDownItems.Add(new ToolStripMenuItem("Update blocklist (StevenBlack hosts)", null, async (_, _) => await UpdateBlocklistAsync(silent: false)));
        more.DropDownItems.Add(new ToolStripMenuItem("Browser task manager", null, (_, _) => Tabs?.ActiveTab?.OpenTaskManager()));
        more.DropDownItems.Add(new ToolStripMenuItem("Dump memory CSV", null, (_, _) => DumpMemoryCsv()));
        more.DropDownItems.Add(new ToolStripMenuItem("Open data folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Settings.DataDir);
            System.Diagnostics.Process.Start("explorer.exe", Settings.DataDir);
        }));
        return more;
    }

    /// <summary>
    /// A window of the person's own, with a new tab in it. Not an agent's window: it is
    /// part of their session from the start.
    /// </summary>
    public async Task NewWindowAsync()
    {
        if (_session is null || _closing)
            return;
        MainForm? window = null;
        try
        {
            window = new MainForm(_session, null, isSecondaryWindow: true, restore: null);
            // Stepped down and right from this one, so the new window is seen to open
            // rather than landing exactly on top and looking like nothing happened.
            if (WindowState == FormWindowState.Normal)
            {
                window.StartPosition = FormStartPosition.Manual;
                window.Bounds = CascadedBounds(Bounds, Scaled(32), Screen.FromControl(this).WorkingArea);
            }
            window.Show();
            await window.Ready;
            await window.NewTabAsync();
        }
        catch (Exception ex)
        {
            // Called from the menu and a shortcut as fire and forget: a failure here would
            // otherwise vanish, and the person would only see that nothing opened.
            DebugLog.WriteAlways($"a new window could not be opened: {ex.GetType().Name}: {ex.Message}");
            // Half built, it would stay on screen empty and be saved into the session.
            if (window is { IsDisposed: false })
            {
                try { window.Close(); }
                catch (Exception closeError) { DebugLog.WriteAlways($"and could not be closed: {closeError.Message}"); }
            }
            if (!IsDisposed)
                ShowMessage("Could not open a new window.");
        }
    }

    /// <summary>
    /// <paramref name="from"/> moved down and right by <paramref name="step"/>, or back to
    /// the top left of <paramref name="screen"/> when that would run off its bottom or right.
    /// </summary>
    internal static Rectangle CascadedBounds(Rectangle from, int step, Rectangle screen)
    {
        var r = new Rectangle(from.X + step, from.Y + step, from.Width, from.Height);
        if (r.Right > screen.Right || r.Bottom > screen.Bottom)
            r.Location = screen.Location;
        return r;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ------------------------------------------------------------------ startup

    /// <summary>
    /// Recorded for the agent API, which cannot ask which window has focus from its own
    /// threads. An agent's own window opened in the background is not activated, so it
    /// does not take this over from the window the person is in.
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _session?.NoteActive(this);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // It has had its one unactivated appearance; from here it behaves like any other
        // window, so a later Show or a restore from minimised comes to the front.
        _openWithoutStealingFocus = false;
        try
        {
            if (_session is null)
                await StartSharedServicesAsync();
            // Closed during that wait: its unsubscribing has already run, so anything added
            // now would never be taken off and would keep the disposed window alive.
            if (_closing || IsDisposed)
                return;
            var session = _session!;
            session.AddWindow(this);

            // Here rather than in StartSharedServicesAsync, which only the first window
            // runs: the downloads are shared across windows, so the button that reports
            // them has to be live in every one of them.
            session.Downloads.Changed += OnDownloadsChanged;
            session.Bookmarks.Changed += OnBookmarksChanged;
            session.Drop.Changed += OnDropChanged;
            _bookmarksBar.SetBookmarks(session.Bookmarks.Items);
            UpdatePhoneButton();
            UpdateDownloadsButton();
            _pagePushTimer.Tick += (_, _) => PushToOwnPages();

            Tabs = new TabManager(session.Env, _hostPanel, session.Blocker);
            Tabs.Changed += (_, _) => UpdateChrome();
            Tabs.TabCreated += (_, tab) => WireTab(tab);
            Tabs.LastTabClosed += (_, _) => Close();
            _tabStrip.Bind(Tabs);

            _statusTimer.Start();
            // Once for the app. A freeze leaves nothing in the log by itself; this writes what
            // the browser had last been doing when the window stops answering.
            UiWatchdog.Start(this);
            _ready.TrySetResult(); // a torn-off tab can be handed over from here on

            if (_restore is not null)
                await RestoreWindowAsync(_restore);
            else if (!_isSecondaryWindow)
                await RestoreStartupAsync();

            // The first window's first page failing to start used to throw straight into
            // the catch below and say "Gergur failed to start". A failed build no longer
            // throws, so ask: an engine that cannot start a page at launch is almost
            // always the whole engine, not one tab, and a blank window with no reason is
            // the worst way to find that out.
            if (!_isSecondaryWindow && Tabs?.ActiveTab is { HasView: false, LastBuildFailure: { } why })
            {
                MessageBox.Show(this,
                    "Gergur could not start its page engine:" + Environment.NewLine + Environment.NewLine + why
                    + Environment.NewLine + Environment.NewLine
                    + "If you recently changed Extra browser arguments in Settings, that is the usual cause.",
                    "Gergur failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

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
                // For this run only, and kept out of VpnEnabled so no later save can write it.
                _settings.VpnDownThisRun = true;
                ShowMessage("VPN tunnel failed to start; browsing without it.");
            }
        }
        _session = await AppSession.CreateAsync(_settings, vpn);
        _session.Env.Core.BrowserProcessExited += OnBrowserProcessExited;
        _session.Env.Core.NewBrowserVersionAvailable += OnNewBrowserVersionAvailable;
        _session.StartAgent();
        HookAgents();
        _session.StartPhoneBridge();
        // Only the first window listens, or an arrival would notify once per window.
        _session.Drop.ItemAdded += OnDropItemArrived;

        // First run with blocking on but no list yet: fetch one quietly.
        if (_session.Blocker.Enabled && !_session.Blocker.HasHostList)
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
        // Not ContainsFocus: this runs on the pipe thread, where focus is always somebody
        // else's, so a link from another app always went to window 0.
        var window = session.WindowForPersonsLink();
        if (window is null || window.IsDisposed)
            return;
        window.BeginInvoke(() =>
        {
            if (window.WindowState == FormWindowState.Minimized)
                window.WindowState = FormWindowState.Normal;
            window.Activate();
            window.ClaimForPerson();
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
            // gergur:// names back into this install's own pages; anything else as it was.
            string url = InternalPages.Resolve(window.Tabs[i].Url) ?? window.Tabs[i].Url;
            var tab = Tabs.AddSnapshotTab(new TabSnapshot(url, window.Tabs[i].Title));
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

        // On the monitor it was dropped on, whichever that is. Clamping at zero, as this
        // used to, is only the edge of the primary monitor: a tab dropped on a screen to
        // the left of it or above it came back onto the primary instead.
        var bounds = TabDrag.TearOffBounds(
            screenLocation,
            Size,
            new Size(Scaled(160), Scaled(24)),
            Screen.FromPoint(screenLocation).WorkingArea);
        var window = new MainForm(_session, null, isSecondaryWindow: true, restore: null)
        {
            StartPosition = FormStartPosition.Manual,
            Bounds = bounds,
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
        if (!from.OpenedByAgent)
            ClaimForPerson();   // their tab, so their window now

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

    /// <summary>
    /// The browser engine updated itself while Gergur was running, which it does every few
    /// days. The running one keeps working and a restart picks up the new one, which is what
    /// Microsoft asks of an app here. Whether running on through an update is behind the
    /// "Class not registered" view build failure in the log is not established: it is one
    /// line from a day when this may have happened, and this at least puts the two side by
    /// side next time. Said in every window.
    /// </summary>
    private void OnNewBrowserVersionAvailable(object? sender, object e)
    {
        DebugLog.WriteAlways("the browser engine was updated while Gergur was running");
        Breadcrumbs.Note("browser engine updated underneath the running one");
        foreach (var window in _session?.Windows.ToList() ?? [])
        {
            if (!window.IsDisposed)
                window.ShowMessage("The browser engine was updated. Restart Gergur to use it.");
        }
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Not children of this form, so closing the window does not take them with it, and
        // each owns a window handle from the first time it is shown. Here rather than in
        // OnFormClosing, because that one can still be cancelled, and a live window with
        // a disposed menu would throw on the next click of the button.
        _mainMenu?.Dispose();
        _downloadsPanel?.Dispose();
        _historyPanel?.Dispose();
        if (_agentsPanel is not null && _agentsHookedTo is not null)
            _agentsHookedTo.Agents.Changed -= _agentsPanel.AgentsChanged;
        _agentsPanel?.Dispose();
        _phonePanel?.Dispose();
        _hideTip.Dispose();
        if (_agentsHookedTo is not null)
            _agentsHookedTo.Agents.Changed -= OnAgentsChanged;
        _memoryLabel.Dispose();   // in no strip now, so nothing else disposes it
        base.OnFormClosed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        // Saved before this window drops out of the list, so closing the last one
        // still records what it was holding.
        _session?.SaveSession();
        _statusTimer.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false; // or the icon lingers in the tray until hovered
            _tray.Dispose();
            _tray = null;
        }
        if (_session is not null)
        {
            _session.Drop.ItemAdded -= OnDropItemArrived;
            _session.Downloads.Changed -= OnDownloadsChanged;
            _session.Bookmarks.Changed -= OnBookmarksChanged;
            _session.Drop.Changed -= OnDropChanged;
            if (_phonePanel is not null)
                _session.Drop.Changed -= _phonePanel.DropChanged;
        }
        _pagePushTimer.Stop();
        _pagePushTimer.Dispose();

        Tabs?.DisposeAll(); // engine processes exit promptly once the last WebView is gone
        _session?.RemoveWindow(this); // stops the agent and tunnel when this was the last
        if (_session is null || _session.Windows.Count == 0)
            LastWindowClosed?.Invoke(this, EventArgs.Empty);
        base.OnFormClosing(e);
    }

    /// <summary>Whether this run's engine routes through the tunnel. See <see cref="BrowserEnvironment.ProxyInForce"/>.</summary>
    private bool ProxyInForce => _session?.Env.ProxyInForce ?? false;

    /// <summary>
    /// VPN submenu: off, then one entry per WireGuard profile. WARP is whatever
    /// datacenter is nearest, so extra .conf files are how you choose an exit country.
    /// </summary>
    private ToolStripMenuItem BuildVpnMenu()
    {
        // Before the session exists the engine has not started, so there is nothing to
        // report and nothing a choice could apply to. The tunnel may still be coming up
        // (it gets twelve seconds), and a restart then closed the only window and left
        // that tunnel running with nobody to stop it.
        if (_session is null)
            return new ToolStripMenuItem("VPN (not ready yet)") { Enabled = false };

        // What the engine was started with, not what the setting says now: the two part
        // whenever a change is waiting for a restart. Likewise the profile the tunnel is
        // actually running, since the settings window can name another without switching.
        bool proxied = ProxyInForce;
        string? running = proxied ? _session.Vpn.ActiveProfileName : null;
        string? chosen = VpnTunnel.ResolveProfile(_settings.VpnProfile)?.Name;
        string active = proxied ? running ?? chosen ?? "on" : "off";
        var menu = new ToolStripMenuItem($"VPN ({active})") { Checked = proxied };

        var profiles = VpnTunnel.ListProfiles();
        if (!VpnTunnel.IsProvisioned)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("Not set up: run scripts\\setup-warp.ps1") { Enabled = false });
        }
        else
        {
            var off = new ToolStripMenuItem("Off", null, (_, _) => TurnVpnOff()) { Checked = !proxied };
            menu.DropDownItems.Add(off);
            menu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var profile in profiles)
            {
                bool current = proxied
                    && string.Equals(profile.Name, running ?? chosen, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Phone submenu: on/off, the pairing link, and the two everyday actions.</summary>
    private ToolStripMenuItem BuildPhoneMenu()
    {
        bool running = _session?.PhoneBridge?.IsRunning == true;
        var menu = new ToolStripMenuItem($"Phone drop ({(running ? "on" : "off")})") { Checked = running };

        menu.DropDownItems.Add(new ToolStripMenuItem(running ? "Turn off" : "Turn on…", null,
            (_, _) => TogglePhoneBridge()));
        menu.DropDownItems.Add(new ToolStripSeparator());

        int waiting = _session?.Drop.Items.Count ?? 0;
        menu.DropDownItems.Add(new ToolStripMenuItem(
            waiting > 0 ? $"Open drop ({waiting})" : "Open drop", null, (_, _) => OpenDrop()));
        menu.DropDownItems.Add(new ToolStripMenuItem("Send this page to phone", null, (_, _) => SendPageToPhone())
        {
            Enabled = running && Tabs?.ActiveTab is { } tab && !HomePage.IsHome(tab.Url),
        });
        menu.DropDownItems.Add(new ToolStripMenuItem("Show pairing link…", null, (_, _) => ShowPairingLink())
        {
            Enabled = running,
        });
        return menu;
    }

    /// <summary>What the vpn menu's Off has to do.</summary>
    public enum VpnOff
    {
        /// <summary>Already off, and the engine already runs without the tunnel.</summary>
        Nothing,
        /// <summary>The engine already runs without it (the tunnel failed at startup); only the choice needs writing down.</summary>
        SaveOnly,
        /// <summary>The engine runs through the tunnel, and the proxy is a browser-process flag.</summary>
        SaveAndRestart,
    }

    /// <summary>
    /// The decision, apart from the window. It went wrong twice by being worked out from
    /// the setting alone: once restarting every window when the engine already ran without
    /// the tunnel, and once doing nothing at all when the setting already said off and the
    /// engine still used the tunnel, which is what the settings window leaves behind when
    /// its restart is declined. Off only: turning the vpn on is picking a profile, which
    /// UseVpnProfileAsync decides for itself.
    /// </summary>
    /// <param name="chosen">What the setting says now.</param>
    /// <param name="proxied">What the running engine was started with.</param>
    internal static VpnOff DecideVpnOff(bool chosen, bool proxied)
        => proxied ? VpnOff.SaveAndRestart   // saved even when chosen is off: the file may still say on
            : chosen ? VpnOff.SaveOnly
            : VpnOff.Nothing;

    /// <summary>
    /// Everything the menu's Off does, with the save, the restart and the message passed in
    /// so a test can run it with a save that fails. Deciding correctly was not enough on
    /// its own: what was done with the decision could put the vpn back on after a failed
    /// save, or restart every window for nothing, with every test still green.
    /// </summary>
    /// <param name="saveOrSay">Saves, or says why not with the given outcome, and returns whether it saved.</param>
    internal static VpnOff TurnVpnOff(Settings settings, bool proxied, Func<string, bool> saveOrSay, Action restart, Action<string> tell)
    {
        var change = DecideVpnOff(settings.VpnEnabled, proxied);
        if (change == VpnOff.Nothing)
            return change;
        bool previous = settings.VpnEnabled;
        settings.VpnEnabled = false;
        if (!saveOrSay(change == VpnOff.SaveAndRestart ? Undone : StillInForce))
        {
            // No restart: it would read the old value back from the file. Put back exactly
            // what was there. Kept off when no restart was needed, since then the engine
            // already runs the way it was just set.
            if (change == VpnOff.SaveAndRestart)
                settings.VpnEnabled = previous;
            return change;
        }
        if (change == VpnOff.SaveAndRestart)
            restart(); // the proxy is a browser-process flag
        else
            tell("VPN off. This run was already browsing without it.");
        return change;
    }

    private void TurnVpnOff()
    {
        var change = TurnVpnOff(_settings, ProxyInForce, SaveSettingsOrSay, RestartForNewEngineFlags, ShowMessage);
        if (change == VpnOff.SaveOnly)
        {
            // Nothing routes through it this run, so nothing should be left running for it.
            _session?.Vpn.Stop();
            UpdateStatus();
        }
    }

    // Profile picks across every window, so a pick can tell whether a later one has
    // superseded it. Every pick runs on the UI thread, so a plain counter is enough.
    private static int _vpnPicks;

    /// <summary>
    /// Switching between profiles while the VPN is already on only restarts wireproxy:
    /// the browser keeps pointing at the same local SOCKS5 address, so no restart.
    /// Turning the VPN on from off still needs one.
    /// </summary>
    private async Task UseVpnProfileAsync(string profileName)
    {
        // What the engine was started with, not merely chosen: with the tunnel down this run,
        // or a change waiting for a restart, switching profile live would route nothing.
        if (_session is null)
            return;   // the menu is disabled until then; this is belt and braces
        bool wasOn = ProxyInForce;
        // Against the profile the tunnel is running, not the setting: the settings window
        // can change the setting without switching the tunnel, and then picking the one
        // it named said nothing and switched nothing.
        bool sameProfile = _session.Vpn.IsRunning
            && string.Equals(_session.Vpn.ActiveProfileName, profileName, StringComparison.OrdinalIgnoreCase);
        string previousProfile = _settings.VpnProfile;
        bool previousEnabled = _settings.VpnEnabled;
        _settings.VpnProfile = profileName;
        _settings.VpnEnabled = true;
        bool saved = SaveSettingsOrSay(wasOn ? StillInForce : Undone);

        if (!wasOn)
        {
            if (!saved)
            {
                // Turning it on takes a restart, which would read the old values back. Put
                // back exactly what was there: forcing it off here turned a vpn that was
                // chosen but down this run into one chosen off, which the next save kept.
                _settings.VpnProfile = previousProfile;
                _settings.VpnEnabled = previousEnabled;
                return;
            }
            RestartForNewEngineFlags();
            return;
        }
        // Already on: switching profile happens live, so it works for this run even
        // unsaved, and the box has said it will not last.
        if (sameProfile)
            return;

        string switching = $"Switching VPN to {profileName}…";
        ShowMessage(switching);
        int pick = ++_vpnPicks;
        bool up = await _session.Vpn.SwitchProfileAsync(_settings.VpnLocalPort, profileName, TimeSpan.FromSeconds(12));
        // Picked again while this one was still starting: the later pick owns the tunnel
        // and will say how it went. This one saying "failed" or "now exiting through"
        // would describe a tunnel that is not this pick's. Counted, not compared by name:
        // A, then B, then A again left the setting naming A, so the first pick reported
        // "failed" while the third was still coming up.
        if (pick != _vpnPicks)
        {
            // Its own "switching" line goes, unless something has replaced it since.
            if (_messageLabel.Text == switching)
                ShowMessage("");
            return;
        }
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
        // Said out loud, because otherwise nothing is: the build no longer throws into the
        // click that asked for it, so without this a tab whose page could not start just
        // showed a blank area and did nothing when clicked.
        tab.ViewBuildFailed += (_, _) =>
        {
            if (!_closing)
                ShowMessage(Tab.CouldNotStartMessage(tab.NextBuildAttemptUtc, DateTime.UtcNow));
        };
        tab.PageLoaded += (_, _) => _session?.History.Append(tab.Url, tab.Title);
        tab.DiscardedAfterHang += (_, _) =>
        {
            // Only for the page being looked at. Replaces the waiting message, which then has
            // nothing of its own left to take down.
            if (!_closing && Tabs?.ActiveTab == tab)
                ShowMessage("The page stopped responding and was reloaded.");
        };
        // Only when it is the page being looked at: a background tab hanging is not news.
        tab.NotRespondingChanged += (_, notResponding) =>
        {
            if (_closing)
                return;
            if (notResponding)
            {
                if (Tabs?.ActiveTab != tab)
                    return;
                _notRespondingTab = tab;
                ShowMessage(NotRespondingText);
            }
            else
            {
                // Wherever the person has gone since: it is only ever our own words that are taken down.
                if (_notRespondingTab == tab)
                    _notRespondingTab = null;
                ClearNotResponding();
            }
        };
        // A sign-in popup closing itself is the normal end of an OAuth handshake.
        // It still goes on the reopen stack, so a surprise close is one Ctrl+Shift+T away.
        tab.CloseRequested += (_, _) => _ = CloseTabAsync(tab);
        tab.PageRequest += (_, request) =>
        {
            if (PageServicesFor(tab) is { } services)
                tab.PostToPage(InternalPages.Answer(request.Page, request.Json, services), request.Page);
        };
        tab.DownloadStarted += (_, operation) =>
        {
            if (_session is null)
                return;
            var item = _session.Downloads.Track(operation);
            ShowMessage($"Downloading {item.FileName}…");
            // After the engine's event has returned: the panel is a window, and showing one
            // from inside a WebView2 handler is the kind of re-entry it throws on.
            BeginInvoke(ShowNewDownload);
            if (Tabs is { } manager
                && (manager.IsSetAside(tab) || Tab.OnlyOpenedForADownload(tab.Opener is not null, tab.Url, tab.Title)))
            {
                // Its view stays alive, out of sight, until the download is done with.
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                item.Changed += (_, _) =>
                {
                    if (!item.IsRunning)
                        finished.TrySetResult();
                };
                if (!item.IsRunning)
                    finished.TrySetResult();
                // After the engine's event has returned, not inside it.
                BeginInvoke(() => _ = manager.SetAsideAsync(tab, finished.Task));
            }
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
        // A video going fullscreen has to take the whole window, chrome included. The verdict
        // is taken here, when this tab's page asks, whichever tab is in front; what the window
        // then does is worked out from the tab in front rather than from this one event. A
        // page that left fullscreen while another tab was in front used to be ignored, and the
        // window stayed without its tab strip and toolbar with nothing on screen to click to
        // get out.
        tab.FullScreenChanged += (_, _) =>
        {
            JudgeFullScreen(tab);
            SyncPageFullScreen();
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
    /// Takes the verdict on a page's fullscreen, if it has not got one. Whether the window
    /// is the one in use, and whether an agent has just acted in the page, are looked at
    /// here, at the moment the page asks, and never again for the same request: see
    /// <see cref="FullScreenPolicy"/>.
    /// </summary>
    private void JudgeFullScreen(Tab tab)
    {
        var before = tab.FullScreenVerdict;
        bool agentActed = tab.AgentActedWithin(FullScreenPolicy.AgentQuiet);
        tab.FullScreenVerdict = FullScreenPolicy.Judge(
            before, tab.IsFullScreen, OpenedByAgent, agentActed, ReferenceEquals(ActiveForm, this));
        // Said out loud in the person's own window: a refusal is otherwise a video button
        // that silently does nothing. Not in an agent's window, which nobody is watching.
        if (FullScreenPolicy.ShouldSayRefused(before, tab.FullScreenVerdict, OpenedByAgent) && tab.IsCurrent)
            ShowMessage(agentActed
                ? "Fullscreen held back: an agent is using this tab. Press the button again in a few seconds."
                : "Fullscreen held back because this window was not in use. Press the button again.");
    }

    /// <summary>
    /// Puts the window into or out of fullscreen to match the page in front. Called whenever
    /// anything that could change the answer has: a page entering or leaving fullscreen, the
    /// tab in front changing, a tab closing. Only for the page in front: another tab going
    /// fullscreen behind it, or the tab in front being changed to one that is not, ends it.
    /// </summary>
    private void SyncPageFullScreen()
    {
        if (_closing)
            return;
        var tab = Tabs?.ActiveTab;
        if (tab is not null)
            JudgeFullScreen(tab);
        SetPageFullScreen(tab is not null && FullScreenPolicy.Fills(tab.FullScreenVerdict, tab.IsFullScreen));
    }

    /// <summary>
    /// Esc or F11 while a page has the window: the chrome comes back at once, and the page is
    /// asked to leave its own fullscreen as well. The window does not wait for the page, which
    /// is the point: the way out used to be the page's own, and a page that does not have the
    /// keyboard, or does not answer, left nothing to click.
    /// </summary>
    internal void ExitPageFullScreen()
    {
        if (Tabs?.ActiveTab is { } tab)
        {
            // Held until the page has really left, so a page that ignores the request does
            // not get the window back.
            tab.FullScreenVerdict = FullScreenVerdict.Dismissed;
            tab.ExitFullScreen();
        }
        // Dismissed above, so this brings the window back whatever the page says next.
        SyncPageFullScreen();
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
            _preFullScreenBounds = Bounds;
            _tabStrip.Visible = _toolbar.Visible = _statusStrip.Visible = _bookmarksBar.Visible = false;
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
            // Changing the border style makes the form keep its client area's size, which for a
            // window that was not maximised changed the window itself, and the system then
            // remembered that size as the one to come back to. Measured: with the title bar
            // gone from the client area it is a different size each way round. Where a
            // maximised window goes when it is un-maximised is the system's, and is right
            // (see WM_WINDOWPOSCHANGED in WndProc). Going back to maximised needs no detour
            // through Normal: measured, its client area returns to exactly the work area.
            if (_preFullScreenState == FormWindowState.Normal)
                Bounds = _preFullScreenBounds;
            _tabStrip.Visible = _toolbar.Visible = _statusStrip.Visible = true;
            _bookmarksBar.Visible = _settings.ShowBookmarksBar;
        }
    }

    private async Task ActivateTabAsync(Tab tab)
    {
        if (Tabs is null)
            return;
        await Tabs.ActivateAsync(tab);
        tab.FocusPage();
    }

    internal const string NotRespondingText = "This page is not responding. Gergur will reload it if it does not come back.";

    // The tab the not-responding text was put up for, so it comes down when the person goes elsewhere.
    private Tab? _notRespondingTab;

    /// <summary>
    /// What the status line becomes when a not-responding message is to come down: empty when it
    /// still holds that message, and null (leave it) when anything else is there, a download or a
    /// saved bookmark that arrived since, which is not ours to wipe.
    /// </summary>
    internal static string? StatusAfterNotResponding(string? current)
        => current == NotRespondingText ? "" : null;

    private void ClearNotResponding()
    {
        if (StatusAfterNotResponding(_messageLabel.Text) is { } cleared)
            ShowMessage(cleared);
    }
    // The tab whose address the address bar was last given, and the text it was given, so
    // typing can be told from focus: it is only kept for that tab, and only once changed.
    private Tab? _addressShownFor;
    private string _addressShownText = "";

    /// <summary>
    /// Whether the address bar should be given the active tab's address now. Left alone only
    /// while the person is typing in it for the tab it already shows. A new tab focuses the
    /// address bar and clicking the tab strip does not take focus, so going by focus alone
    /// left a switched-to tab showing the previous tab's text, or nothing, and a new tab
    /// opened from a focused address bar showing the old tab's address.
    /// </summary>
    internal static bool ShouldRefreshAddress(bool force, bool focused, bool sameTab, bool edited)
        => force || !focused || !sameTab || !edited;

    private void ShowAddress(Tab? tab)
    {
        _addressShownText = AddressFor(tab);
        _addressShownFor = tab;
        if (_addressBar.Text == _addressShownText)
            return;   // leaves a caret or selection the person placed where it is
        _addressBar.Text = _addressShownText;
        // New text puts the caret at the start. Under a focused bar nobody has typed in yet
        // (Ctrl+L while a page loads, say), the next keystroke would land in front of the
        // url rather than replace it, so it is selected again, ready to be typed over.
        if (_addressBar.Focused)
            _addressBar.SelectAll();
    }

    private void UpdateChrome(bool forceAddressBar = false)
    {
        var active = Tabs?.ActiveTab;
        if (_notRespondingTab is not null && _notRespondingTab != active)
        {
            _notRespondingTab = null;
            ClearNotResponding();
        }
        // A page still being waited on, newly in front: switched back to, or torn off into this
        // window while it was. The event that said so went to the window it was in then.
        if (_notRespondingTab is null && active is { IsWaitingOnPage: true } waiting)
        {
            _notRespondingTab = waiting;
            ShowMessage(NotRespondingText);
        }
        // Skip the URL refresh only when the user is actively editing the address bar for
        // this tab, never when a tab close incidentally parked focus here (forceAddressBar).
        if (ShouldRefreshAddress(forceAddressBar, _addressBar.Focused,
                sameTab: active == _addressShownFor, edited: _addressBar.Text != _addressShownText))
            ShowAddress(active);
        // The committed page, not an address still loading: a typed https url would otherwise
        // show the lock over the http page still on screen, and keep it if the load stopped.
        _addressPill.Secure = active?.CommittedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
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
        // The tab in front may have changed, or closed, and with it whether a page owns the window.
        SyncPageFullScreen();
    }

    // ------------------------------------------------------------------ actions (shortcut router targets)

    public async Task NewTabAsync(string? url = null)
    {
        if (Tabs is null)
            return;
        var creating = Tabs.CreateTabAsync(url ?? HomePage.Url);
        // The new tab is the active one as soon as it exists, and its address bar is empty. Taking
        // focus only once its view had been built (seconds, on a cold engine) sent the first
        // keystrokes of a new tab nowhere.
        if (url is null)
            FocusAddressBar();
        await creating;
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
    public void OpenHistory() => _ = OpenOwnPageAsync(InternalPages.Page.History);

    public void OpenBookmarks() => _ = OpenOwnPageAsync(InternalPages.Page.Bookmarks);

    /// <summary>Shows or hides the bookmarks bar in every window, and remembers which.</summary>
    public void ToggleBookmarksBar()
    {
        _settings.ShowBookmarksBar = !_settings.ShowBookmarksBar;
        // Just the bars, not ApplyLiveSettings, which would push every page setting into
        // every tab of every window to show or hide one strip.
        foreach (var window in _session?.Windows ?? [this])
        {
            if (!window._isPageFullScreen)
                window._bookmarksBar.Visible = _settings.ShowBookmarksBar;
        }
        SaveSettingsOrSay(StillInForce);
    }

    private async Task OpenBookmarkAsync(string url, bool newTab)
    {
        if (Tabs is null)
            return;
        ClaimForPerson();   // only a person opens a bookmark
        if (newTab)
            await Tabs.CreateTabAsync(url, activate: false);
        else
            await NavigateActiveAsync(url);
    }

    /// <summary>
    /// Pushes the settings that can change without a restart into the things already
    /// holding a copy of them, across every window.
    ///
    /// Shared with the agent API rather than left in the dialog's hands, because it did
    /// not used to be: <c>POST /settings {"BlocklistEnabled": false}</c> answered
    /// "applied", and the blocker went on blocking for the rest of the session, because
    /// <see cref="Blocking.RequestBlocker.Enabled"/> is its own field and nothing re-read
    /// it. Being told a change took effect when it did not is worse than being told it
    /// needs a restart.
    ///
    /// The sleep timers are here too, which they were not: each window read them once
    /// when it opened, so changing one did nothing until you opened another window, and
    /// nothing said so because they are not engine flags and were never on the restart
    /// list. Now the window you changed them in honours them.
    /// </summary>
    internal static void ApplyLiveSettings(AppSession session)
    {
        session.Blocker.Enabled = session.Settings.BlocklistEnabled;
        session.Blocker.Relaxed = Blocking.RelaxedSites.Parse(session.Settings.RelaxedSites);
        foreach (var window in session.Windows)
        {
            window._addressBar.SearchUrlTemplate = session.Settings.SearchUrlTemplate;
            if (!window._isPageFullScreen)
                window._bookmarksBar.Visible = session.Settings.ShowBookmarksBar;
            // The page settings reach tabs already open, not only the next view built.
            // Each one catches its own failure, so nothing escapes the discard.
            foreach (var tab in window.Tabs?.Tabs ?? [])
                _ = tab.ApplySettingsLiveAsync(session.Settings);
            window.UpdateStatus();
        }
    }

    /// <summary>
    /// The settings page, gergur://settings: a page in a tab like History, rather than the
    /// modal property grid it replaced. Each change is saved as it is made, what can apply
    /// at once does, and the page says what is waiting for a restart and offers one.
    /// </summary>
    public void OpenSettings() => _ = OpenOwnPageAsync(InternalPages.Page.Settings);

    // ------------------------------------------------------------------ phone drop

    /// <summary>
    /// Something arrived from the phone. A tray balloon rather than a dialog: this is a
    /// notification, not a question, and it must not steal focus from what you are doing.
    /// </summary>
    private void OnDropItemArrived(object? sender, DropItem item)
    {
        if (item.From != "phone" || _closing || IsDisposed)
            return;
        BeginInvoke(() =>
        {
            string summary = DropForm.Summarize(item);
            ShowMessage($"From your phone: {summary}");
            EnsureTray()?.ShowBalloonTip(5000, "Gergur Drop", summary, ToolTipIcon.Info);
        });
    }

    /// <summary>The tray icon exists only once the phone bridge has something to say.</summary>
    private NotifyIcon? EnsureTray()
    {
        if (_tray is not null)
            return _tray;
        try
        {
            _tray = new NotifyIcon { Icon = Icon, Text = "Gergur Drop", Visible = true };
            _tray.BalloonTipClicked += (_, _) => OpenDrop();
            _tray.DoubleClick += (_, _) => OpenDrop();
        }
        catch
        {
            _tray = null; // No tray is survivable; the status bar still says so.
        }
        return _tray;
    }

    /// <summary>Modeless, one window, shared by every browser window.</summary>
    public void OpenDrop()
    {
        if (_session is null)
            return;
        if (_dropWindow is { IsDisposed: false })
        {
            _dropWindow.Activate();
            return;
        }
        _dropWindow = new DropForm(_session.Drop, url => { ClaimForPerson(); _ = NewTabAsync(url); });
        _dropWindow.FormClosed += (_, _) => _dropWindow = null;
        _dropWindow.Show(this);
    }

    /// <summary>Puts the current page in the drop so the phone can pick it up.</summary>
    private void SendPageToPhone()
    {
        if (_session is null || Tabs?.ActiveTab is not { } active || HomePage.IsHome(active.Url))
            return;
        _session.Drop.AddText(active.Url, from: "pc");
        ShowMessage("Sent to your phone's drop.");
    }

    /// <summary>
    /// Turning the bridge on is a real decision: it opens a port on your network, so it
    /// says what it does and what it cannot do before starting.
    /// </summary>
    private void TogglePhoneBridge()
    {
        if (_session is null)
            return;
        if (_settings.DropEnabled)
        {
            _settings.DropEnabled = false;
            SaveSettingsOrSay("The phone drop is off until Gergur closes, and may be back on at the next start.");
            _session.StopPhoneBridge();
            ShowMessage("Phone drop off.");
            return;
        }

        var answer = MessageBox.Show(this,
            "Start the phone drop?\n\n"
            + "This opens a port on your local network so a paired phone can send and receive "
            + "links, messages and files. Only devices with the pairing link can reach it, and it "
            + "cannot control the browser: the agent API stays on loopback.\n\n"
            + "Windows may ask you to allow it through the firewall.",
            "Gergur", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK)
            return;

        _settings.DropEnabled = true;
        if (!SaveSettingsOrSay("The phone drop has not been started, because its pairing key could not be kept."))
        {
            // Not started. Its pairing key would be minted now and lost at the next start,
            // so a phone paired today would be locked out tomorrow with no explanation.
            _settings.DropEnabled = false;
            return;
        }
        _session.StartPhoneBridge();

        // A failed start used to leave the setting on with nothing listening, so the next
        // launch would try again, fail again and say nothing, while the setting claimed
        // the feature was on. Turning it back off makes the state match what happened.
        if (_session.PhoneBridge is null)
        {
            _settings.DropEnabled = false;
            try
            {
                _settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The write most likely to fail next, since this branch is where a pairing
                // key that could not be saved lands, and a click handler has nothing above
                // it. The box below still says why the drop is not running.
                DebugLog.WriteAlways($"settings not saved: {ex.Message}");
            }
            // A box, not the status bar. This answers a question the user was just asked
            // in a dialog, and the status label shares one line with five others, so the
            // half that says what to do is the half that gets clipped.
            MessageBox.Show(
                this,
                $"Phone drop could not start.\n\n{_session.PhoneBridgeError}",
                "Gergur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ShowPairingLink();
    }

    /// <summary>The address to open on the phone, with a copy button, since you type it once.</summary>
    private void ShowPairingLink()
    {
        if (_session?.PhoneBridge?.PairingUrl is not { } url)
        {
            ShowMessage(_session?.PhoneBridgeError is { } why
                ? $"Phone drop is not running. {why}"
                : "Phone drop is not running.");
            return;
        }
        using var pairing = new PairingForm(url);
        pairing.ShowDialog(this);
    }

    /// <summary>Modeless, one window, shared across every browser window's downloads.</summary>
    public void OpenDownloads() => _ = OpenOwnPageAsync(InternalPages.Page.Downloads);

    public void ToggleBookmark()
    {
        if (_session is null || Tabs?.ActiveTab is not { } active || HomePage.IsHome(active.Url))
            return;
        if (InternalPages.Identify(active.Url) is not null)
        {
            // Its address is a path into this install, which a moved or rebuilt Gergur no
            // longer has; gergur:// names are typed, not bookmarked.
            ShowMessage("Gergur's own pages cannot be bookmarked. Type gergur://history and the like instead.");
            return;
        }
        try
        {
            bool added = _session.Bookmarks.Toggle(active.Url, active.Title);
            ShowMessage(added ? "Bookmarked." : "Bookmark removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SayBookmarkNotSaved(ex, "The bookmark could not be saved: something else has the bookmarks file open.");
        }
        UpdateChrome();
    }

    // Once a run, across every window: the box says why and what to do, and saying it again
    // at every click would be a box to dismiss each time.
    private static bool _toldBookmarksUnreadable;

    /// <summary>
    /// Says a bookmark change was not saved. An unreadable bookmarks file gets a box the
    /// first time, not only the status bar: its label shares one line with five others,
    /// and the half that gets clipped is the half that says what to do.
    /// </summary>
    private void SayBookmarkNotSaved(Exception ex, string whenLocked)
    {
        if (ex is not BookmarkStore.UnreadableException)
        {
            ShowMessage(whenLocked);
            return;
        }
        ShowMessage("Bookmarks are not being saved this run: bookmarks.json could not be read.");
        if (_toldBookmarksUnreadable)
            return;
        _toldBookmarksUnreadable = true;
        MessageBox.Show(this, ex.Message, "Gergur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        => _shortcuts.Handle(keyData) || base.ProcessCmdKey(ref msg, keyData);

    private void UpdateStatus()
    {
        if (_session is null || _closing)
            return;
        var snapshot = MemoryMeter.Capture(_session.Env.Core);
        _memoryLabel.Text = $"engine {MemorySnapshot.Format(snapshot.EnginePrivateBytes)} · {snapshot.RendererCount} renderers · shell {MemorySnapshot.Format(snapshot.ShellPrivateBytes)}";
        _blockedLabel.Text = _session.Blocker is { Enabled: true } blocker ? $"{blocker.BlockedCount:N0} blocked" : "blocking off";
        // Name the exit, not just "on": with several profiles, which one matters.
        _vpnLabel.Text = ProxyInForce && _session.Vpn.IsRunning
            ? (_session.Vpn.ActiveProfileName == VpnTunnel.WarpName ? "WARP" : _session.Vpn.ActiveProfileName ?? "VPN")
            : "";
        UpdateSleepLabel();
    }

    private void UpdateSleepLabel()
    {
        if (Tabs is null)
            return;
        int sleeping = Tabs.Tabs.Count(t => t.State is TabState.Suspended or TabState.Discarded);
        // Said only when there is something to say; "0/3 tabs asleep" was noise on every glance.
        _sleepLabel.Text = sleeping == 0 ? "" : $"{sleeping} asleep";
        string detail = $"{_memoryLabel.Text}\n{sleeping} of {Tabs.Tabs.Count} tabs asleep";
        _sleepLabel.ToolTipText = _blockedLabel.ToolTipText = _vpnLabel.ToolTipText = detail;
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

    /// <summary>
    /// Saves the settings, or says in a box why they were not. A run that could not read
    /// settings.json at startup leaves that file alone rather than risk overwriting it, so
    /// a change made now lasts until Gergur closes. Callers that restart to apply a change
    /// must not go ahead when this is false: the restart reads the file and quietly puts
    /// the old value back, so the vpn you just turned on comes back off.
    /// </summary>
    /// <param name="ifNotSaved">What happens to the change when it cannot be saved, said
    /// in the box: whether it is still in force for this run or was undone.</param>
    private bool SaveSettingsOrSay(string ifNotSaved)
    {
        string why;
        try
        {
            if (_settings.Save())
            {
                PushSettingsChanged();   // a settings page open beside the menu shows the change
                return true;
            }
            why = NotSavedText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A write that failed, which a click handler has nothing above it to catch. The
            // log gets the message; the box below names the file rather than quoting it.
            DebugLog.WriteAlways($"settings not saved: {ex.Message}");
            why = WriteFailedText;
            // A failed write sets no guard, so a change kept in memory is written out by the
            // next save that does succeed, an agent's /settings patch of something else
            // included. "Until Gergur closes" undersold how long it can last.
            if (ifNotSaved == StillInForce)
                ifNotSaved = "It is in force now, and the next save that succeeds writes it out.";
        }
        MessageBox.Show(this, why + "\n\n" + ifNotSaved, "Gergur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    internal const string NotSavedText =
        "This change was not saved.\n\n"
        + "Gergur could not read its settings file when it started, so it is leaving that file "
        + "alone rather than risk overwriting your settings with defaults. Close Gergur, check "
        + "nothing else has %LOCALAPPDATA%\\Gergur\\settings.json open, start Gergur again, then "
        + "make the change again.";

    internal const string WriteFailedText =
        "This change was not saved: the settings file could not be written. Something may "
        + "have %LOCALAPPDATA%\\Gergur\\settings.json open, or the disk may be full.";

    internal const string StillInForce = "It is in force until Gergur closes.";
    internal const string Undone = "It has been undone, because it needs a restart and a restart would read the old value back.";
}
