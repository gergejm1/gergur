using System.Drawing.Drawing2D;
using Gergur.Tabs;

namespace Gergur.UI;

/// <summary>
/// Owner-drawn tab strip: favicons, ellipsized titles, close buttons, a "+" button.
/// Sleeping (suspended/discarded) tabs draw dimmed with a moon glyph - the memory
/// policy is visible at a glance.
/// </summary>
public sealed class TabStripControl : Control
{
    private const int MaxTabWidth = 220;
    private const int MinTabWidth = 56;

    private TabManager? _tabs;
    private readonly List<Rectangle> _tabRects = new();
    private readonly List<Rectangle> _closeRects = new();
    private Rectangle _newTabRect;
    private int _hoverIndex = -1;
    private bool _hoverClose;
    private bool _hoverNewTab;

    // Drag-to-reorder state.
    private int _pressIndex = -1;     // tab the left button went down on
    private int _pressX;              // where it went down (to detect drag threshold)
    private bool _dragging;
    private int _dragX;               // current cursor x while dragging
    private int _dropIndex = -1;      // where the dragged tab would land

    public event EventHandler<Tab>? TabClicked;
    public event EventHandler<Tab>? TabCloseClicked;
    public event EventHandler? NewTabClicked;
    /// <summary>Raised when a tab is dragged to a new position (fromIndex, toIndex).</summary>
    public event EventHandler<(int From, int To)>? TabReordered;

    public TabStripControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.TabStripBg;
    }

    public void Bind(TabManager tabs)
    {
        _tabs = tabs;
        tabs.Changed += (_, _) => Invalidate();
        Invalidate();
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.TabStripBg);
        _tabRects.Clear();
        _closeRects.Clear();

        var tabs = _tabs?.Tabs;
        int newTabSize = S(26);
        int margin = S(6);
        int y = S(5);
        int tabHeight = Height - y - S(3);

        int count = tabs?.Count ?? 0;
        int available = Width - newTabSize - margin * 3;
        int tabWidth = count == 0 ? 0 : Math.Clamp(available / count, S(MinTabWidth), S(MaxTabWidth));

        int x = margin;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (tabs is not null)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var rect = new Rectangle(x, y, tabWidth - S(4), tabHeight);
                _tabRects.Add(rect);
                DrawTab(g, tabs[i], rect, i);
                x += tabWidth;
            }
        }

        _newTabRect = new Rectangle(x + S(2), y + (tabHeight - newTabSize) / 2, newTabSize, newTabSize);
        DrawNewTabButton(g);

        // Drop indicator: a vertical accent bar at the insertion point while dragging.
        if (_dragging && _dropIndex >= 0 && _dropIndex < _tabRects.Count)
        {
            var target = _tabRects[_dropIndex];
            int barX = _dropIndex <= _pressIndex ? target.Left - S(3) : target.Right + S(1);
            using var pen = new Pen(Theme.Accent, S(3));
            g.DrawLine(pen, barX, y, barX, y + tabHeight);
        }
    }

    private void DrawTab(Graphics g, Tab tab, Rectangle rect, int index)
    {
        bool isActive = _tabs?.ActiveTab == tab;
        bool isHover = index == _hoverIndex;
        bool isSleeping = tab.State is TabState.Suspended or TabState.Discarded;

        var fill = isActive ? Theme.TabActive : isHover ? Theme.TabHover : Theme.TabBg;
        using (var path = RoundedRect(rect, S(6)))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }
        if (isActive)
        {
            using var pen = new Pen(Theme.Accent, S(2));
            g.DrawLine(pen, rect.Left + S(6), rect.Bottom - 1, rect.Right - S(6), rect.Bottom - 1);
        }

        int pad = S(8);
        int iconSize = S(16);
        int textLeft = rect.Left + pad;

        if (tab.Favicon is { } favicon)
        {
            try
            {
                var iconRect = new Rectangle(rect.Left + pad, rect.Top + (rect.Height - iconSize) / 2, iconSize, iconSize);
                g.DrawImage(favicon, iconRect);
                textLeft = iconRect.Right + S(6);
            }
            catch { }
        }

        bool showClose = isActive || isHover || rect.Width > S(110);
        int closeSize = S(18);
        Rectangle closeRect = Rectangle.Empty;
        if (showClose && rect.Width >= S(70))
        {
            closeRect = new Rectangle(rect.Right - closeSize - S(6), rect.Top + (rect.Height - closeSize) / 2, closeSize, closeSize);
            bool closeHovered = isHover && _hoverClose;
            if (closeHovered)
            {
                using var brush = new SolidBrush(Theme.CloseHover);
                g.FillEllipse(brush, closeRect);
            }
            // Vector ×: text glyphs never center; two strokes always do.
            var glyphColor = closeHovered ? Theme.Text : isSleeping ? Theme.TextDim : Theme.Text;
            using var closePen = new Pen(glyphColor, Math.Max(1.4f, S(3) / 2f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            var cross = Rectangle.Inflate(closeRect, -S(6), -S(6));
            g.DrawLine(closePen, cross.Left, cross.Top, cross.Right, cross.Bottom);
            g.DrawLine(closePen, cross.Right, cross.Top, cross.Left, cross.Bottom);
        }
        while (_closeRects.Count < index)
            _closeRects.Add(Rectangle.Empty);
        _closeRects.Add(closeRect);

        string title = tab.Title;
        if (isSleeping)
            title = "☾ " + title; // ☾ sleeping/parked marker
        else if (tab.IsPlayingAudio)
            title = "♪ " + title; // ♪

        int textRight = closeRect.IsEmpty ? rect.Right - pad : closeRect.Left - S(4);
        var textRect = Rectangle.FromLTRB(textLeft, rect.Top, Math.Max(textLeft + 1, textRight), rect.Bottom);
        TextRenderer.DrawText(g, title, Font, textRect,
            isSleeping ? Theme.TextDim : Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    private void DrawNewTabButton(Graphics g)
    {
        if (_hoverNewTab)
        {
            using var brush = new SolidBrush(Theme.TabHover);
            g.FillEllipse(brush, _newTabRect);
        }
        using var pen = new Pen(Theme.Text, Math.Max(1.4f, S(3) / 2f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var inner = Rectangle.Inflate(_newTabRect, -S(8), -S(8));
        int cx = _newTabRect.Left + _newTabRect.Width / 2;
        int cy = _newTabRect.Top + _newTabRect.Height / 2;
        g.DrawLine(pen, inner.Left, cy, inner.Right, cy);
        g.DrawLine(pen, cx, inner.Top, cx, inner.Bottom);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Drag-to-reorder: once the press has moved past a small threshold, track it.
        if (e.Button == MouseButtons.Left && _pressIndex >= 0)
        {
            if (!_dragging && Math.Abs(e.X - _pressX) > S(6))
                _dragging = true;
            if (_dragging)
            {
                _dragX = e.X;
                _dropIndex = DropIndexFor(e.X);
                Cursor = Cursors.SizeAll;
                Invalidate();
                return;
            }
        }

        int newHover = HitTestTab(e.Location);
        bool newHoverClose = newHover >= 0 && newHover < _closeRects.Count && _closeRects[newHover].Contains(e.Location);
        bool newHoverNewTab = _newTabRect.Contains(e.Location);
        if (newHover != _hoverIndex || newHoverClose != _hoverClose || newHoverNewTab != _hoverNewTab)
        {
            _hoverIndex = newHover;
            _hoverClose = newHoverClose;
            _hoverNewTab = newHoverNewTab;
            Cursor = newHover >= 0 || newHoverNewTab ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        _hoverClose = _hoverNewTab = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var tabs = _tabs?.Tabs;
        if (tabs is null)
            return;

        if (e.Button == MouseButtons.Left && _newTabRect.Contains(e.Location))
        {
            NewTabClicked?.Invoke(this, EventArgs.Empty);
            return;
        }

        int index = HitTestTab(e.Location);
        if (index < 0 || index >= tabs.Count)
            return;
        var tab = tabs[index];

        if (e.Button == MouseButtons.Middle)
        {
            TabCloseClicked?.Invoke(this, tab);
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            // Close button wins immediately; otherwise arm a possible drag and
            // decide click-vs-drag on mouse up.
            if (index < _closeRects.Count && _closeRects[index].Contains(e.Location))
            {
                TabCloseClicked?.Invoke(this, tab);
                return;
            }
            _pressIndex = index;
            _pressX = e.X;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var tabs = _tabs?.Tabs;
        try
        {
            if (e.Button != MouseButtons.Left || tabs is null || _pressIndex < 0)
                return;

            if (_dragging && _dropIndex >= 0 && _dropIndex != _pressIndex)
                TabReordered?.Invoke(this, (_pressIndex, _dropIndex));
            else if (_pressIndex < tabs.Count)
                TabClicked?.Invoke(this, tabs[_pressIndex]); // it was a plain click
        }
        finally
        {
            _pressIndex = -1;
            _dragging = false;
            _dropIndex = -1;
            Invalidate();
        }
    }

    private int DropIndexFor(int x)
    {
        // Land before the first tab whose horizontal center is past the cursor.
        for (int i = 0; i < _tabRects.Count; i++)
        {
            if (x < _tabRects[i].Left + _tabRects[i].Width / 2)
                return i;
        }
        return _tabRects.Count - 1;
    }

    private int HitTestTab(Point location)
    {
        for (int i = 0; i < _tabRects.Count; i++)
        {
            if (_tabRects[i].Contains(location))
                return i;
        }
        return -1;
    }
}
