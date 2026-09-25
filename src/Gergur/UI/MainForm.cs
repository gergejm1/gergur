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
    private TabLifecycleManager? _lifecycle;
    // Coalesces live updates to the browser's own pages. A download reports progress many
    // times a second, and each report would otherwise post the whole list to every
    // Downloads tab.
    private readonly System.Windows.Forms.Timer _pagePushTimer = new() { Interval = 250 };
    private bool _downloadsPushPending;
    private bool _bookmarksPushPending;
    private DropForm? _dropWindow;
    private NotifyIcon? _tray;
    private bool _closing;
    private bool _lifecycleTickRunning;
    private bool _isMinimized;
    private bool _isLocked;
    private DateTime? _backgroundSinceUtc;
    private bool _isPageFullScreen;
    private FormWindowState _preFullScreenState;
    private FormBorderStyle _preFullScreenBorder;

    public TabManager? Tabs { get; private set; }

    /// <summary>
    /// Opened by an agent through /window. Its tabs are the agent's work, so they are never
    /// saved as the person's session: see <see cref="AppSession.SessionOf"/>.
    /// </summary>
    internal bool OpenedByAgent { get; private set; }

    /// <summary>
    /// The person has used this window, so it is theirs from now on and its tabs are their
    /// session. Called from what only a person does: typing in the address bar, clicking
    /// the tab strip, a keyboard shortcut, a link from another app, or dragging in a tab
    /// from a window of their own. Without it, dragging every tab into an agent's window
    /// emptied their own, which then saved as "no tabs" while the tabs lived on unsaved.
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
        _tabStrip.TabClicked += async (_, tab) => { ClaimForPerson(); await ActivateTabAsync(tab); };
        _tabStrip.TabCloseClicked += async (_, tab) => { await CloseTabAsync(tab); };
        _tabStrip.NewTabClicked += (_, _) => { ClaimForPerson(); _ = NewTabAsync(); };
        _tabStrip.TabReordered += (_, move) => Tabs?.MoveTab(move.From, move.To);
        _tabStrip.TabTornOff += (_, drop) => _ = DropTabAsync(drop.Tab, drop.ScreenLocation);
        _tabStrip.TabDragMoved += (_, screen) => UpdateDropIndicators(screen);
        _tabStrip.TabDragEnded += (_, _) => ClearDropIndicators();
        _tabStrip.IsOverAnotherStrip = screen => DropTargetAt(screen) is not null;

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
            _addressBar.Text = AddressFor(Tabs?.ActiveTab);
            Tabs?.ActiveTab?.FocusPage();
        };

        _downloadsButton = MakeToolButton(Glyphs.Download, 0);
        _bookmarkButton = MakeToolButton(Glyphs.StarOutline, 0);
        _menuButton = MakeToolButton(Glyphs.Menu, 0);
        _downloadsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _bookmarkButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _menuButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _downloadsButton.Click += (_, _) => OpenDownloads();
        _bookmarkButton.Click += (_, _) => ToggleBookmark();
        _menuButton.Click += (_, _) => ShowMainMenu();

        _toolbar.Controls.AddRange(
            [_backButton, _forwardButton, _reloadButton, _addressPill,
             _downloadsButton, _bookmarkButton, _menuButton]);
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
        => new(glyph) { Location = new Point(x, 7) };

    private void OnDownloadsChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnDownloadsChanged(sender, e));
            return;
        }
        UpdateDownloadsButton();
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
            (url, newTab) => _ = newTab ? (Task)manager.CreateTabAsync(url, activate: false) : tab.NavigateAsync(url));
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

    /// <summary>Scales a design-time pixel count to this window's dpi.</summary>
    private int Scaled(int atNinetySix) => (int)Math.Round(atNinetySix * DeviceDpi / 96.0);

    private void LayoutToolbar()
    {
        // Right to left, off the widths the buttons actually have rather than the ones
        // they had at 96 dpi. See ToolbarLayout for what that cost.
        GlyphButton[] rightHand = [_menuButton, _bookmarkButton, _downloadsButton];
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
    /// Opens the main menu against the screen the button is actually on.
    ///
    /// Left to itself the drop-down slides onto the neighbouring monitor rather than
    /// opening the other way, and on a desktop with monitors either side it lands on a
    /// different screen from the window. Measured with three monitors side by side: a
    /// button 48px from a monitor's right edge put the menu at x=0, on the next screen
    /// along. Asking for the other direction only moves the problem to the left edge,
    /// so the position is worked out here and kept inside the button's own screen.
    ///
    /// The items have to exist before they can be measured, so they are built here, and
    /// only here. Building them again from an Opening handler was not free: each build
    /// walks the VPN profile directory several times over, so a second one put a dozen
    /// synchronous directory reads on the UI thread every time the menu was opened.
    /// </summary>
    private void ShowMainMenu()
    {
        RebuildMenuItems();
        var button = new Rectangle(_menuButton.PointToScreen(Point.Empty), _menuButton.Size);
        // FromRectangle, not FromControl: a window straddling two monitors is mostly on
        // one of them while this button is on the other, and the button is what matters.
        _menu.Show(MenuPlacement.For(
            button, _menu.GetPreferredSize(Size.Empty), Screen.FromRectangle(button).WorkingArea));
    }

    private void BuildMenu()
    {
        _menu = new ContextMenuStrip { Renderer = new ChromeRenderer() };
        RebuildMenuItems();
    }

    private void RebuildMenuItems()
    {
        // Snapshot, clear, then dispose. Disposing an item removes it from Items, so
        // disposing them in a loop over Items throws on the second one and leaves the
        // menu half torn down: MenuRebuildTests keeps that fact. Without this every
        // submenu the viewer has opened keeps its window handle for the life of the
        // process, and an undisposed control with a handle is never collected.
        var replaced = _menu.Items.Cast<ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var item in replaced)
            item.Dispose();
        _menu.Items.Add(new ToolStripMenuItem("New tab\tCtrl+T", null, (_, _) => _ = NewTabAsync()));
        _menu.Items.Add(new ToolStripMenuItem("Reopen closed tab\tCtrl+Shift+T", null, (_, _) => _ = ReopenClosedTabAsync()));
        _menu.Items.Add(new ToolStripSeparator());

        var bookmarks = new ToolStripMenuItem("Bookmarks");
        bookmarks.DropDownItems.Add(new ToolStripMenuItem("Bookmark this page\tCtrl+D", null, (_, _) => ToggleBookmark()));
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
        // "no host list" rather than a count, because the built-in path rules mean the
        // count is never zero now, and "5 rules" would read like a list had loaded.
        string rules = blocker?.HasHostList == true
            ? $"{blocker.RuleCount:N0} rules"
            : "no host list";
        var blocking = new ToolStripMenuItem($"Ad/tracker blocking ({(blocker?.Enabled == true ? "on" : "off")}, {rules})")
        {
            Checked = blocker?.Enabled == true,
        };
        blocking.Click += (_, _) =>
        {
            if (blocker is null)
                return;
            blocker.Enabled = !blocker.Enabled;
            _settings.BlocklistEnabled = blocker.Enabled;
            SaveSettingsOrSay(StillInForce);   // in force either way; this only says if it will not last
            UpdateStatus();
        };
        _menu.Items.Add(blocking);
        _menu.Items.Add(new ToolStripMenuItem("Update blocklist (StevenBlack hosts)", null, async (_, _) => await UpdateBlocklistAsync(silent: false)));

        _menu.Items.Add(BuildVpnMenu());
        _menu.Items.Add(BuildPhoneMenu());
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
            var session = _session!;
            session.AddWindow(this);

            // Here rather than in StartSharedServicesAsync, which only the first window
            // runs: the downloads are shared across windows, so the button that reports
            // them has to be live in every one of them.
            session.Downloads.Changed += OnDownloadsChanged;
            session.Bookmarks.Changed += OnBookmarksChanged;
            _bookmarksBar.SetBookmarks(session.Bookmarks.Items);
            UpdateDownloadsButton();
            _pagePushTimer.Tick += (_, _) => PushToOwnPages();

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
        _session.StartAgent();
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
        var window = session.WindowInUse();
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
        // Not a child of this form, so closing the window does not take it with it, and
        // it owns a window handle from the first time it is shown. Here rather than in
        // OnFormClosing, because that one can still be cancelled, and a live window with
        // a disposed menu would throw on the next click of the button.
        _menu.Dispose();
        _memoryLabel.Dispose();   // in no strip now, so nothing else disposes it
        base.OnFormClosed(e);
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

    private void UpdateChrome(bool forceAddressBar = false)
    {
        var active = Tabs?.ActiveTab;
        // Skip the URL refresh only when the user is actively editing the address bar,
        // never when a tab close incidentally parked focus here (forceAddressBar).
        if (forceAddressBar || !_addressBar.Focused)
            _addressBar.Text = AddressFor(active);
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
        foreach (var window in session.Windows)
        {
            window._addressBar.SearchUrlTemplate = session.Settings.SearchUrlTemplate;
            if (!window._isPageFullScreen)
                window._bookmarksBar.Visible = session.Settings.ShowBookmarksBar;
            if (window._lifecycle is { } lifecycle)
            {
                lifecycle.SuspendAfter = TimeSpan.FromMinutes(Math.Max(1, session.Settings.SuspendAfterMinutes));
                lifecycle.DiscardAfter = TimeSpan.FromMinutes(Math.Max(2, session.Settings.DiscardAfterMinutes));
            }
            // The page settings reach tabs already open, not only the next view built.
            // Each one catches its own failure, so nothing escapes the discard.
            foreach (var tab in window.Tabs?.Tabs ?? [])
                _ = tab.ApplySettingsLiveAsync(session.Settings);
            window.UpdateStatus();
        }
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

        ApplyLiveSettings(_session);

        if (!dialog.Saved)
        {
            // Not "Settings saved", and no offer to restart: a restart reads the file, and
            // the file has not got the change, so it would quietly undo what was just set.
            // A box, as TogglePhoneBridge does for answers to a dialog: the status label
            // clips, and the half that says what to do is the half that goes.
            // Worded per case. A refused save never writes, so a restart setting will not
            // happen; a failed write leaves the change in memory, where the next save that
            // does succeed writes it out with everything else.
            MessageBox.Show(this, dialog.WriteFailed
                    ? WriteFailedText + "\n\nWhat applies without a restart is in force now, and the next save that succeeds writes it out. What needs a restart has not happened, and will only once a later save succeeds and Gergur restarts."
                    : NotSavedText + "\n\nWhat applies without a restart is in force until Gergur closes. What needs a restart will not happen.",
                "Gergur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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
        _dropWindow = new DropForm(_session.Drop, url => _ = NewTabAsync(url));
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
                return true;
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
