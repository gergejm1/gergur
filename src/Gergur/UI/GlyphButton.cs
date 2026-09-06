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

    public GlyphButton(string glyph)
    {
        _glyph = glyph;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(32, 30);
        Font = Theme.IconFont(9.75f);
        BackColor = Theme.ToolbarBg;
        Cursor = Cursors.Hand;
        TabStop = false;
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
            int d = 10;
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
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}
