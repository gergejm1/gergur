using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Gergur.UI;

/// <summary>
/// The rounded frame around the address bar, with the connection glyph on its left. A
/// TextBox cannot round its own corners, so it sits borderless inside this, which draws
/// the pill and an accent ring while it has the keyboard.
/// </summary>
public sealed class AddressPill : Control
{
    private readonly AddressBar _input;
    private string _glyph = Glyphs.Globe;

    public AddressPill(AddressBar input)
    {
        _input = input;
        // Not a stop of its own: tabbing lands in the text box inside it, not on an invisible frame.
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.ToolbarBg;
        Font = Theme.IconFont(9f);
        _input.BorderStyle = BorderStyle.None;
        _input.BackColor = Theme.InputBg;
        _input.GotFocus += (_, _) => Invalidate();
        _input.LostFocus += (_, _) => Invalidate();
        Controls.Add(_input);
        // Clicking the pill anywhere, the glyph or the padding, means the address bar.
        Click += (_, _) => _input.Focus();
    }

    /// <summary>
    /// Whether the page shown arrived over https. Drawn as a lock; anything else gets a
    /// plain globe, never a warning sign, since most of what is not https here is the
    /// browser's own pages and local files.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Secure
    {
        get => _glyph == Glyphs.Lock;
        set
        {
            string glyph = value ? Glyphs.Lock : Glyphs.Globe;
            if (glyph == _glyph)
                return;
            _glyph = glyph;
            Invalidate();
        }
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private int GlyphWidth => S(34);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int left = GlyphWidth;
        int right = S(14);
        _input.Location = new Point(left, (Height - _input.Height) / 2);
        _input.Width = Math.Max(S(40), Width - left - right);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = ClientRectangle;
        r.Width -= 1;
        r.Height -= 1;
        using var path = ChromeRenderer.Rounded(r, r.Height / 2);
        using (var fill = new SolidBrush(Theme.InputBg))
            g.FillPath(fill, path);
        bool typing = _input.Focused;
        using (var pen = new Pen(typing ? Theme.Accent : Theme.Border, typing ? S(2) / 1.5f : 1f))
            g.DrawPath(pen, path);

        var glyphBox = new Rectangle(S(4), 0, GlyphWidth - S(6), Height);
        TextRenderer.DrawText(g, _glyph, Font, glyphBox, Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        // The icon font was made for this control, so it goes with it.
        if (disposing)
            Font.Dispose();
        base.Dispose(disposing);
    }
}
