using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Gergur.UI;

/// <summary>A place on a flyout that does something when clicked.</summary>
/// <param name="Bounds">Where it is, in the flyout's own coordinates.</param>
/// <param name="Run">What clicking it does. Null for something that only shows.</param>
/// <param name="Tip">A tooltip, for a control that is only an icon.</param>
/// <param name="KeepOpen">The flyout stays up after it runs: zoom steps, a toggle.</param>
/// <param name="Spoken">What a screen reader and the keyboard focus ring call it.</param>
internal sealed record HotSpot(Rectangle Bounds, Action? Run, string Spoken, string? Tip = null, bool KeepOpen = false)
{
    public bool Enabled => Run is not null;
}

/// <summary>
/// A panel dropped from a toolbar button over the page: the main menu and the downloads
/// panel. Drawn by hand in the chrome's own colours, as the tab strip is, rather than
/// built out of a ContextMenuStrip, whose rows cannot hold a zoom stepper, a progress bar or
/// a row of tiles.
///
/// Two ways of being shown, and they differ in the one thing that matters to the person:
///
/// - <see cref="TakesFocus"/>: the main menu. It is activated, so the keyboard drives it
///   (arrows, Enter, Esc) and it closes the moment anything else is used, the way a menu
///   does.
/// - Not taking focus: the downloads panel. It opens by itself when a download starts,
///   which can be in the middle of typing, so it must never take the keyboard. It is never
///   activated (WS_EX_NOACTIVATE), clicks on it still work, and it closes on a click
///   anywhere outside it, which has to be watched for because a window that is never
///   active is never deactivated either. The page is another process's window, so the
///   watch reads the mouse buttons globally rather than from this thread's queue.
/// </summary>
internal abstract class FlyoutPanel : Form
{
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 50 };
    private readonly ToolTip _tips = new() { InitialDelay = 400, ShowAlways = true };
    private readonly List<HotSpot> _spots = new();
    private Form? _owner;
    private Control? _button;
    private Rectangle _anchor;
    private HotSpot? _hot;
    private HotSpot? _pressed;
    private int _focusIndex = -1;
    private string? _tipShown;
    private bool _wasDown;
    private int _fontDpi;

    protected FlyoutPanel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        // Scaled here, by hand, rather than by WinForms: every length is S() of the window's
        // own DPI and every font is made in pixels for it (MakeFont), so nothing is scaled
        // twice when the panel moves to a monitor with another scale.
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.MenuBg;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _watch.Tick += (_, _) => Watch();
    }

    /// <summary>Whether showing this takes the keyboard. See the class summary.</summary>
    protected abstract bool TakesFocus { get; }

    /// <summary>
    /// Lays the panel out for the current state: adds its hot spots with
    /// <see cref="AddSpot"/> and answers the size it needs, in pixels at this DPI.
    /// </summary>
    protected abstract Size LayOut();

    /// <summary>Draws the panel. The background and border are already drawn.</summary>
    protected abstract void PaintContent(Graphics g);

    /// <summary>Raised after the panel has hidden itself, whatever the reason.</summary>
    public event EventHandler? Dismissed;

    /// <summary>When it last went away, so the button it hangs from can tell a second click from a first.</summary>
    public DateTime HiddenAtUtc { get; private set; }

    /// <summary>The panel's hot spots, in layout order. For tests and for keyboard focus.</summary>
    internal IReadOnlyList<HotSpot> Spots => _spots;

    protected override bool ShowWithoutActivation => !TakesFocus;

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            if (!TakesFocus)
                cp.ExStyle |= WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Pixels at this panel's DPI for a length designed at 96.</summary>
    protected int S(int atNinetySix) => (int)Math.Round(atNinetySix * DeviceDpi / 96.0);

    /// <summary>
    /// A font of <paramref name="points"/> at this panel's DPI, sized in pixels. A font made in
    /// points is measured against the system DPI, which is the wrong size on a monitor scaled
    /// differently from the main one, and WinForms only rescales the form's own Font.
    /// </summary>
    protected Font MakeFont(string family, float points, FontStyle style = FontStyle.Regular)
        => new(family, points * DeviceDpi / 72f, style, GraphicsUnit.Pixel);

    /// <summary>The icon font at <paramref name="points"/>, in pixels for this DPI. See <see cref="MakeFont"/>.</summary>
    protected Font MakeIconFont(float points) => MakeFont(Theme.IconFontName, points);

    /// <summary>
    /// Makes the panel's fonts for its current DPI, disposing any it replaces. Called through
    /// <see cref="EnsureFonts"/>: from the panel's constructor, and again before a layout
    /// whenever the DPI has changed since. The base sets <see cref="Control.Font"/>, the
    /// panel's ordinary text.
    /// </summary>
    protected virtual void MakeFonts()
    {
        var old = Font;
        Font = MakeFont("Segoe UI", 9f);
        if (!ReferenceEquals(old, DefaultFont))
            old.Dispose();
    }

    /// <summary>Makes the fonts if there are none yet or they were made for another DPI.</summary>
    protected void EnsureFonts()
    {
        if (_fontDpi == DeviceDpi)
            return;
        MakeFonts();
        _fontDpi = DeviceDpi;
    }

    protected void AddSpot(HotSpot spot) => _spots.Add(spot);

    protected bool IsHot(HotSpot spot) => ReferenceEquals(_hot, spot) || (_focusIndex >= 0 && _focusIndex < _spots.Count && ReferenceEquals(_spots[_focusIndex], spot));

    protected bool IsPressed(HotSpot spot) => ReferenceEquals(_pressed, spot);

    /// <summary>
    /// Shows the panel hanging from <paramref name="button"/>, on that button's own monitor
    /// (<see cref="MenuPlacement"/>), owned by <paramref name="owner"/> so it stays above it
    /// and goes with it.
    /// </summary>
    public void ShowFrom(Form owner, Control button)
    {
        _owner = owner;
        _button = button;
        _anchor = ButtonOnScreen(button);
        _wasDown = AnyMouseButtonDown();
        // The handle is made on the button's monitor before anything is measured: a window's
        // DPI is the monitor it is created on, and laid out before that it would use the
        // system's, which is the wrong scale on a monitor set differently.
        if (!IsHandleCreated)
        {
            Location = _anchor.Location;
            CreateHandle();
        }
        Relayout(keepPlace: false);
        if (!Visible)
        {
            if (Owner != owner)
                Owner = owner;
            Show(owner);
        }
        if (TakesFocus)
        {
            Activate();
            Focus();
        }
        _watch.Start();
    }

    /// <summary>
    /// Lays out again for a change in what it shows (a download's progress), keeping the
    /// right edge under the button so a panel that grows does not walk across the screen.
    /// </summary>
    public void Relayout(bool keepPlace = true)
    {
        EnsureFonts();
        // What the pointer is on and what it is holding down are carried across by place in
        // the list. A running download relays the panel out several times a second, and
        // dropping these each time lost the press between mouse down and up, so Cancel
        // could not be clicked while it was most needed.
        int hot = _hot is null ? -1 : _spots.IndexOf(_hot);
        int pressed = _pressed is null ? -1 : _spots.IndexOf(_pressed);
        var before = _spots.Select(s => s.Spoken).ToList();
        _spots.Clear();
        _hot = null;
        _pressed = null;
        var size = LayOut();
        _hot = Carried(before, hot);
        _pressed = Carried(before, pressed);
        if (_focusIndex >= _spots.Count)
            _focusIndex = _spots.Count - 1;
        var screen = Screen.FromRectangle(_anchor).WorkingArea;
        var at = MenuPlacement.For(_anchor, size, screen);
        if (!keepPlace || Size != size || Location != at)
            Bounds = new Rectangle(at, size);
        Invalidate();
    }

    /// <summary>
    /// The spot now standing where <paramref name="index"/> stood before a relayout, if it is
    /// still the same thing (same name in the same place); otherwise the first spot of that
    /// name, which is the same download after one ahead of it finished; otherwise none.
    /// </summary>
    private HotSpot? Carried(List<string> before, int index)
    {
        if (index < 0 || index >= before.Count)
            return null;
        string name = before[index];
        if (index < _spots.Count && _spots[index].Spoken == name)
            return _spots[index];
        return _spots.FirstOrDefault(s => s.Spoken == name);
    }

    /// <summary>Where a control is on the screen.</summary>
    internal static Rectangle ButtonOnScreen(Control button) => new(button.PointToScreen(Point.Empty), button.Size);

    /// <summary>A move to a monitor with another scale: lay out again for it.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (Visible)
            Relayout(keepPlace: false);
    }

    /// <summary>Takes the panel down. Safe to call when it is not up.</summary>
    public void Dismiss()
    {
        _watch.Stop();
        _tips.Hide(this);
        _tipShown = null;
        _focusIndex = -1;
        _hot = null;
        _pressed = null;
        if (!Visible)
            return;
        HiddenAtUtc = DateTime.UtcNow;
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether a click on the button this hangs from should open it, or is the click that just closed it.</summary>
    public bool JustDismissed => ClosedByThisClick(HiddenAtUtc, (_button as GlyphButton)?.PressOfThisClickUtc, DateTime.UtcNow);

    /// <summary>
    /// Whether the panel went away during the click now landing on its button. A panel that
    /// takes the keyboard goes as the press activates the window behind it, so by the
    /// release that clicks the button it has already gone, and opening it again would undo
    /// the click. Decided by when the press began, not by a fixed time before the release:
    /// that was 300 ms, and a press held longer than that closed the panel and opened it
    /// straight back up. A press that began after the panel went is always a fresh click.
    /// The margin covers the press being recorded a moment after the deactivation it causes.
    /// With no press to go by (not a GlyphButton's click) it keeps the old 300 ms rule.
    /// </summary>
    internal static bool ClosedByThisClick(DateTime hiddenAt, DateTime? pressedAt, DateTime now)
        => pressedAt is { } press
            ? hiddenAt >= press - TimeSpan.FromMilliseconds(50)
            : (now - hiddenAt).TotalMilliseconds < 300;

    // ---------------------------------------------------------------- closing on its own

    private void Watch()
    {
        if (!Visible || _owner is null)
            return;
        // The window it belongs to went away or was minimised.
        if (_owner.IsDisposed || !_owner.Visible || _owner.WindowState == FormWindowState.Minimized)
        {
            Dismiss();
            return;
        }
        // Or its button moved: the window was moved, resized, snapped, or maximised, by the
        // mouse or the keyboard. A panel left where the button used to be hangs from nothing.
        if (_button is { IsDisposed: false } button && ButtonOnScreen(button) != _anchor)
        {
            Dismiss();
            return;
        }
        bool down = AnyMouseButtonDown(out bool clickedSince);
        // A press that began since the last tick, including a click quick enough to have
        // gone down and up between two ticks, which the held state alone never shows.
        if ((down && !_wasDown) || (!down && clickedSince))
        {
            var at = Cursor.Position;
            // Its own button is left to its own click, which closes it: closing it here as
            // well would have the click reopen what it just closed.
            if (!Bounds.Contains(at) && !_anchor.Contains(at) && !OverOwnedPopup(at))
            {
                Dismiss();
                return;
            }
        }
        _wasDown = down;

        // Another application came to the front. The menu learns that by losing focus; the
        // downloads panel never had it to lose.
        if (!TakesFocus)
        {
            IntPtr front = GetForegroundWindow();
            if (front != IntPtr.Zero && front != _owner.Handle && front != Handle && !IsOwnedByOwner(front))
                Dismiss();
        }
        if (Visible)
            OnWatch();
    }

    /// <summary>Called on every tick of the watch while the panel is up, for a panel with its own reasons to go.</summary>
    protected virtual void OnWatch() { }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!TakesFocus)
            return;
        // A submenu opened from here is a popup that never takes activation, so losing it
        // means something else really was used. The exception is the button this hangs
        // from: its own click closes the panel, and closing here first would let that
        // click open it straight back up.
        // Control.MouseButtons, this thread's own key state, rather than GetAsyncKeyState:
        // any call to that clears its pressed-since bit, which Watch needs for quick clicks.
        // The anchor is a button in the owner window, on this thread, so its own state sees it.
        if (Control.MouseButtons != MouseButtons.None && _anchor.Contains(Cursor.Position))
            return;
        if (OverOwnedPopup(Cursor.Position))
            return;
        BeginInvoke(Dismiss);
    }

    /// <summary>A submenu this panel opened (a ToolStripDropDown) is under the point.</summary>
    protected virtual bool OverOwnedPopup(Point screen) => false;

    private bool IsOwnedByOwner(IntPtr window)
    {
        IntPtr root = GetAncestor(window, GA_ROOTOWNER);
        return root == _owner?.Handle || root == Handle;
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var spot = SpotAt(e.Location);
        if (!ReferenceEquals(spot, _hot))
        {
            _hot = spot;
            _focusIndex = -1;
            Cursor = spot?.Enabled == true ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        string? tip = spot?.Tip;
        if (tip != _tipShown)
        {
            _tipShown = tip;
            if (tip is null)
                _tips.Hide(this);
            else
                _tips.Show(tip, this, e.X + S(12), e.Y + S(18), 3000);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot is not null)
        {
            _hot = null;
            Invalidate();
        }
        _tips.Hide(this);
        _tipShown = null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        _pressed = SpotAt(e.Location) is { Enabled: true } spot ? spot : null;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;
        var pressed = _pressed;
        _pressed = null;
        Invalidate();
        if (pressed is not null && ReferenceEquals(SpotAt(e.Location), pressed))
            RunSpot(pressed);
    }

    /// <summary>Runs a hot spot: the panel goes first unless the spot keeps it open.</summary>
    internal void RunSpot(HotSpot spot)
    {
        if (spot.Run is not { } run)
            return;
        if (spot.KeepOpen)
        {
            run();
            if (Visible)
                Relayout();
            return;
        }
        Dismiss();
        // After the panel has gone and the click has finished: an action that opens a
        // dialog would otherwise put it up under a panel still on screen.
        var owner = _owner;
        if (owner is { IsDisposed: false })
            owner.BeginInvoke(run);
        else
            run();
    }

    private HotSpot? SpotAt(Point p)
    {
        for (int i = _spots.Count - 1; i >= 0; i--)
        {
            if (_spots[i].Bounds.Contains(p))
                return _spots[i];
        }
        return null;
    }

    // ---------------------------------------------------------------- keyboard

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                Dismiss();
                return true;
            case Keys.Down:
            case Keys.Tab:
                MoveFocus(+1);
                return true;
            case Keys.Up:
            case Keys.Shift | Keys.Tab:
                MoveFocus(-1);
                return true;
            case Keys.Enter:
            case Keys.Space:
                if (_focusIndex >= 0 && _focusIndex < _spots.Count)
                    RunSpot(_spots[_focusIndex]);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Moves the keyboard focus to the next spot that does something, wrapping.</summary>
    internal void MoveFocus(int step)
    {
        int next = NextFocus(_spots, _focusIndex, step);
        if (next == _focusIndex)
            return;
        _focusIndex = next;
        _hot = null;
        Invalidate();
    }

    /// <summary>
    /// The index keyboard focus moves to from <paramref name="from"/> (-1: nowhere yet),
    /// skipping spots that do nothing, wrapping at either end. Stays put when there is
    /// nothing to move to.
    /// </summary>
    internal static int NextFocus(IReadOnlyList<HotSpot> spots, int from, int step)
    {
        if (spots.Count == 0 || step == 0)
            return from;
        int start = from < 0 ? (step > 0 ? -1 : spots.Count) : from;
        for (int n = 1; n <= spots.Count; n++)
        {
            int i = ((start + step * n) % spots.Count + spots.Count) % spots.Count;
            if (spots[i].Enabled)
                return i;
        }
        return from;
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.MenuBg);
        using (var pen = new Pen(Theme.Border))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PaintContent(g);
        // The keyboard's place, drawn last so nothing covers it.
        if (_focusIndex >= 0 && _focusIndex < _spots.Count)
        {
            var r = _spots[_focusIndex].Bounds;
            r.Inflate(-1, -1);
            using var path = Rounded(r, S(6));
            using var pen = new Pen(Theme.Accent, Math.Max(1f, S(2) / 1.5f));
            g.DrawPath(pen, path);
        }
    }

    /// <summary>The hover fill the menus use, rounded as theirs are.</summary>
    protected void PaintHover(Graphics g, HotSpot spot, Rectangle? area = null)
    {
        if (!spot.Enabled || !(IsHot(spot) || IsPressed(spot)))
            return;
        var r = area ?? spot.Bounds;
        using var path = Rounded(r, S(6));
        using var brush = new SolidBrush(IsPressed(spot) ? Theme.TabActive : Theme.TabHover);
        g.FillPath(brush, path);
    }

    protected static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Text in one line, cut with an ellipsis, vertically centred in <paramref name="r"/>.</summary>
    protected static void DrawLabel(Graphics g, string text, Font font, Rectangle r, Color color, bool right = false, bool center = false)
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        if (right)
            flags |= TextFormatFlags.Right;
        if (center)
            flags |= TextFormatFlags.HorizontalCenter;
        TextRenderer.DrawText(g, text, font, r, color, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watch.Dispose();
            _tips.Dispose();
        }
        // The panel's own font, made by MakeFonts; the subclasses have finished with theirs.
        var font = disposing && !ReferenceEquals(Font, DefaultFont) ? Font : null;
        base.Dispose(disposing);
        font?.Dispose();
    }

    // ---------------------------------------------------------------- native

    private static bool AnyMouseButtonDown() => AnyMouseButtonDown(out _);


    /// <summary>
    /// Whether a mouse button is held now, and in <paramref name="pressedSince"/> whether one
    /// was pressed since the last time this asked. That low bit is shared by every caller in
    /// the session, so it can miss a click another program asked about first; the held
    /// state is the reliable half and this only adds the clicks that fall between ticks.
    /// </summary>
    private static bool AnyMouseButtonDown(out bool pressedSince)
    {
        bool down = false;
        pressedSince = false;
        foreach (int key in (ReadOnlySpan<int>)[VK_LBUTTON, VK_RBUTTON, VK_MBUTTON])
        {
            short state = GetAsyncKeyState(key);
            down |= (state & 0x8000) != 0;
            pressedSince |= (state & 1) != 0;
        }
        return down;
    }

    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;
    private const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
