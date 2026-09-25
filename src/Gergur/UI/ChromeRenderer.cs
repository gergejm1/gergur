using System.Drawing.Drawing2D;

namespace Gergur.UI;

/// <summary>
/// Draws the browser's menus and status bar in its own colours. Left to Windows they came
/// out as light grey system menus with blue highlights under a dark crimson window, which
/// is most of why the chrome looked older than it is.
/// </summary>
public sealed class ChromeRenderer : ToolStripProfessionalRenderer
{
    public ChromeRenderer() : base(new ChromeColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(e.ToolStrip is StatusStrip ? Theme.TabStripBg : Theme.MenuBg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // A status strip gets no border at all; a drop-down gets one hairline, so it still
        // reads as lifted off the page behind it.
        if (e.ToolStrip is StatusStrip)
            return;
        using var pen = new Pen(Theme.Border);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Theme.MenuBg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;
        var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(r, 6);
        using var brush = new SolidBrush(Theme.TabHover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.TextDim
            : e.Item is ToolStripStatusLabel label ? label.ForeColor
            : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? Theme.TextDim : Theme.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // A tick in the accent colour, drawn rather than the system's blue-boxed image.
        var r = e.ImageRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = r.Left + r.Width * 0.22f, y = r.Top + r.Height * 0.52f;
        e.Graphics.DrawLines(pen, [
            new PointF(x, y),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f),
        ]);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class ChromeColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.MenuBg;
        public override Color ImageMarginGradientBegin => Theme.MenuBg;
        public override Color ImageMarginGradientMiddle => Theme.MenuBg;
        public override Color ImageMarginGradientEnd => Theme.MenuBg;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.TabHover;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color StatusStripGradientBegin => Theme.TabStripBg;
        public override Color StatusStripGradientEnd => Theme.TabStripBg;
    }
}
