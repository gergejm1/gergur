using System.ComponentModel;

namespace Gergur.UI;

/// <summary>
/// Flat button that paints a single icon-font glyph dead-centered. Button's own
/// text layout drifts by a few pixels with symbol fonts; this owns the paint.
/// </summary>
public sealed class GlyphButton : Control
{
    private bool _hover;
    private bool _pressed;
    private string _glyph = "";
    private Color _glyphColor = Theme.Text;
    private Color? _dot;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph
    {
        get => _glyph;
        set { _glyph = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GlyphColor
    {
        get => _glyphColor;
        set { _glyphColor = value; Invalidate(); }
    }

    /// <summary>A small dot in the top right corner, for "something is happening here"; null for none.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Dot
    {
        get => _dot;
        set
        {
            if (_dot == value)
                return;
            _dot = value;
            Invalidate();
        }
    }

    public GlyphButton(string glyph)
    {
        _glyph = glyph;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        // Every click is a click. A plain Control raises DoubleClick instead of Click for the
        // second of two quick clicks, so closing a panel straight after opening it, or a second
        // press of Back, did nothing and had to be clicked a third time. Button turns this off
        // for the same reason.
        SetStyle(ControlStyles.StandardDoubleClick, false);
        Size = new Size(34, 32);
        Font = Theme.IconFont(9.75f);
        BackColor = Theme.ToolbarBg;
        Cursor = Cursors.Hand;
        TabStop = false;
        // Painted by hand, so nothing else tells a screen reader what it is.
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Enabled && (_hover || _pressed))
        {
            using var brush = new SolidBrush(_pressed ? Theme.TabActive : Theme.TabHover);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            var r = ClientRectangle;
            r.Inflate(-1, -1);
            // Fully round, matching the pill address bar beside it.
            int d = Math.Min(r.Width, r.Height);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
        TextRenderer.DrawText(g, _glyph, Font, ClientRectangle,
            Enabled ? _glyphColor : Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (_dot is { } dot)
        {
            int size = Math.Max(6, Width / 5);
            var r = new Rectangle(Width - size - Width / 6, Height / 6, size, size);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var ring = new SolidBrush(BackColor))
                g.FillEllipse(ring, Rectangle.Inflate(r, 2, 2));
            using var fill = new SolidBrush(dot);
            g.FillEllipse(fill, r);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    private DateTime _pressedAtUtc;
    private bool _clicking;

    /// <summary>
    /// While this button's Click is being raised, when the press behind it began; otherwise
    /// null, so a panel opened any other way is never judged by an old press. See
    /// <see cref="FlyoutPanel.JustDismissed"/>.
    /// </summary>
    internal DateTime? PressOfThisClickUtc => _clicking && _pressedAtUtc != default ? _pressedAtUtc : null;

    protected override void OnClick(EventArgs e)
    {
        _clicking = true;
        try { base.OnClick(e); }
        finally
        {
            _clicking = false;
            // Used up: a later click with no press of its own (from code, or accessibility)
            // must not be judged by this one.
            _pressedAtUtc = default;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e) { _pressedAtUtc = DateTime.UtcNow; _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}
