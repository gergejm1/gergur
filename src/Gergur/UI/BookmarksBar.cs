using System.Drawing.Drawing2D;
using Gergur.Data;

namespace Gergur.UI;

/// <summary>
/// The strip of bookmarks under the toolbar. Bookmarks used to live only in a submenu of
/// the main menu, where two saved pages were as good as lost. Owner drawn, so it costs a
/// window handle and nothing else; a browser page here would cost a renderer.
/// </summary>
public sealed class BookmarksBar : Control
{
    private readonly ToolTip _tip = new();
    private readonly List<(Rectangle Box, Bookmark Bookmark)> _placed = new();
    private IReadOnlyList<Bookmark> _bookmarks = [];
    // Measured once per change, not on every repaint: a hover repaints the whole bar.
    private int[]? _widths;
    private Font? _badgeFont;
    private Font? _glyphFont;
    private int _visible;
    private Rectangle _overflowBox;
    private int _hover = -1;
    private bool _hoverOverflow;
    private string? _tipShown;

    public BookmarksBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.ToolbarBg;
        Font = new Font("Segoe UI", 9f);   // its own, so disposed with it below
        AccessibleRole = AccessibleRole.ToolBar;
        AccessibleName = "Bookmarks";
    }

    /// <summary>Open a bookmark: in this tab, or a new one for Ctrl or middle click.</summary>
    public event EventHandler<(string Url, bool NewTab)>? OpenRequested;
    public event EventHandler<string>? RemoveRequested;
    public event EventHandler? ManageRequested;
    public event EventHandler? HideRequested;

    public void SetBookmarks(IReadOnlyList<Bookmark> bookmarks)
    {
        _bookmarks = bookmarks.ToArray();
        _widths = null;
        _hover = -1;
        Invalidate();
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    /// <summary>
    /// How many items fit, left to right, when the ones that do not go behind an overflow
    /// button of <paramref name="overflowWidth"/>. The button only takes room when it is
    /// needed: if everything fits without it, everything is shown.
    /// </summary>
    internal static int FitCount(IReadOnlyList<int> widths, int available, int gap, int overflowWidth)
    {
        int used = 0;
        for (int i = 0; i < widths.Count; i++)
        {
            int next = used + (i > 0 ? gap : 0) + widths[i];
            if (next > available)
            {
                // Did not fit: take items back off until the overflow button fits too.
                int count = i;
                while (count > 0 && used + gap + overflowWidth > available)
                {
                    count--;
                    used = 0;
                    for (int j = 0; j < count; j++)
                        used += (j > 0 ? gap : 0) + widths[j];
                }
                return count;
            }
            used = next;
        }
        return widths.Count;
    }

    private int ItemWidth(Bookmark bookmark)
    {
        int text = TextRenderer.MeasureText(Label(bookmark), Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + S(2);
        return S(10) + S(16) + S(7) + Math.Min(text, S(160)) + S(10);
    }

    private static string Label(Bookmark bookmark)
        => string.IsNullOrWhiteSpace(bookmark.Title) ? HostOf(bookmark.Url) : bookmark.Title.Trim();

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host)
            : url;

    /// <summary>A steady colour per site for its letter badge, from the crimson family.</summary>
    internal static Color BadgeColour(string url)
    {
        Color[] palette =
        [
            Color.FromArgb(184, 52, 72), Color.FromArgb(150, 62, 120), Color.FromArgb(196, 96, 60),
            Color.FromArgb(120, 70, 150), Color.FromArgb(170, 70, 90), Color.FromArgb(86, 96, 160),
        ];
        uint hash = 2166136261;
        foreach (char c in HostOf(url).ToLowerInvariant())
            hash = (hash ^ c) * 16777619;
        return palette[hash % (uint)palette.Length];
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        // A hairline between the chrome and the page.
        using (var edge = new Pen(Theme.TabStripBg))
            g.DrawLine(edge, 0, Height - 1, Width, Height - 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _placed.Clear();
        _overflowBox = Rectangle.Empty;

        int left = S(8);
        int height = Height - S(6);
        int top = S(2);
        if (_bookmarks.Count == 0)
        {
            TextRenderer.DrawText(g, "Bookmark a page with Ctrl+D and it shows up here.", Font,
                new Rectangle(left + S(4), 0, Width - left, Height - S(2)), Theme.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }

        var widths = _widths ??= _bookmarks.Select(ItemWidth).ToArray();
        int overflowWidth = S(30);
        int gap = S(2);
        _visible = FitCount(widths, Width - left * 2, gap, overflowWidth + gap);

        int x = left;
        for (int i = 0; i < _visible; i++)
        {
            var box = new Rectangle(x, top, widths[i], height);
            _placed.Add((box, _bookmarks[i]));
            DrawItem(g, _bookmarks[i], box, i == _hover);
            x += widths[i] + gap;
        }

        if (_visible < _bookmarks.Count)
        {
            _overflowBox = new Rectangle(Width - left - overflowWidth, top, overflowWidth, height);
            if (_hoverOverflow)
            {
                using var path = ChromeRenderer.Rounded(_overflowBox, S(6));
                using var hover = new SolidBrush(Theme.TabHover);
                g.FillPath(hover, path);
            }
            _glyphFont ??= Theme.IconFont(9f);
            TextRenderer.DrawText(g, Glyphs.ChevronDown, _glyphFont, _overflowBox, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private void DrawItem(Graphics g, Bookmark bookmark, Rectangle box, bool hovered)
    {
        if (hovered)
        {
            using var path = ChromeRenderer.Rounded(box, S(6));
            using var brush = new SolidBrush(Theme.TabHover);
            g.FillPath(brush, path);
        }
        int badge = S(16);
        var badgeBox = new Rectangle(box.Left + S(10), box.Top + (box.Height - badge) / 2, badge, badge);
        using (var fill = new SolidBrush(BadgeColour(bookmark.Url)))
            g.FillEllipse(fill, badgeBox);
        string label = Label(bookmark);
        string letter = label.Length > 0 ? char.ToUpperInvariant(label[0]).ToString() : "?";
        _badgeFont ??= new Font(Font.FontFamily, 7.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, letter, _badgeFont, badgeBox, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var textBox = Rectangle.FromLTRB(badgeBox.Right + S(7), box.Top, box.Right - S(8), box.Bottom);
        // The same flags the width was measured with, or the text never fits the box it was given.
        TextRenderer.DrawText(g, label, Font, textBox, Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private int HitIndex(Point p) => _placed.FindIndex(item => item.Box.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = HitIndex(e.Location);
        bool overflow = _overflowBox.Contains(e.Location);
        if (hover != _hover || overflow != _hoverOverflow)
        {
            _hover = hover;
            _hoverOverflow = overflow;
            Invalidate();
        }
        string? tip = hover >= 0 ? _placed[hover].Bookmark.Url : overflow ? "More bookmarks" : null;
        if (tip != _tipShown)
        {
            _tipShown = tip;
            _tip.SetToolTip(this, tip);
        }
        Cursor = hover >= 0 || overflow ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _hoverOverflow = false;
        _tipShown = null;
        _tip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_overflowBox.Contains(e.Location) && e.Button == MouseButtons.Left)
        {
            ShowOverflow();
            return;
        }
        int index = HitIndex(e.Location);
        if (index < 0)
        {
            if (e.Button == MouseButtons.Right)
                ShowMenu(null, e.Location);
            return;
        }
        var bookmark = _placed[index].Bookmark;
        switch (e.Button)
        {
            case MouseButtons.Left:
                OpenRequested?.Invoke(this, (bookmark.Url, (ModifierKeys & Keys.Control) != 0));
                break;
            case MouseButtons.Middle:
                OpenRequested?.Invoke(this, (bookmark.Url, true));
                break;
            case MouseButtons.Right:
                ShowMenu(bookmark, e.Location);
                break;
        }
    }

    private void ShowOverflow()
    {
        var menu = NewMenu();
        foreach (var bookmark in _bookmarks.Skip(_visible))
        {
            var url = bookmark.Url;
            var item = new ToolStripMenuItem(Label(bookmark)) { ToolTipText = url };
            item.MouseUp += (_, e) => OpenRequested?.Invoke(this, (url, e.Button == MouseButtons.Middle || (ModifierKeys & Keys.Control) != 0));
            menu.Items.Add(item);
        }
        menu.Show(this, new Point(_overflowBox.Right - menu.PreferredSize.Width, _overflowBox.Bottom));
    }

    private void ShowMenu(Bookmark? bookmark, Point at)
    {
        var menu = NewMenu();
        if (bookmark is not null)
        {
            var url = bookmark.Url;
            menu.Items.Add(new ToolStripMenuItem("Open", null, (_, _) => OpenRequested?.Invoke(this, (url, false))));
            menu.Items.Add(new ToolStripMenuItem("Open in new tab", null, (_, _) => OpenRequested?.Invoke(this, (url, true))));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Remove", null, (_, _) => RemoveRequested?.Invoke(this, url)));
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(new ToolStripMenuItem("Manage bookmarks\tCtrl+Shift+O", null, (_, _) => ManageRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripMenuItem("Hide bookmarks bar\tCtrl+Shift+B", null, (_, _) => HideRequested?.Invoke(this, EventArgs.Empty)));
        menu.Show(this, at);
    }

    /// <summary>A throwaway menu that frees itself once closed, so each right click does not keep a window handle.</summary>
    private static ContextMenuStrip NewMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new ChromeRenderer(), ShowImageMargin = false };
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        return menu;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _widths = null;
        _badgeFont?.Dispose();
        _badgeFont = null;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _widths = null;   // the padding around each title is scaled
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _badgeFont?.Dispose();
            _glyphFont?.Dispose();
            Font.Dispose();
        }
        base.Dispose(disposing);
    }
}
