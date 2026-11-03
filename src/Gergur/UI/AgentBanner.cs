using System.Drawing.Drawing2D;

namespace Gergur.UI;

/// <summary>
/// The band across the top of an agent's window, above the page: a robot, an AGENT pill,
/// which agent it is and what it says it is doing, and a Hide button. In the agent's own
/// blue rather than the browser's crimson, so a window shown from the agents panel is
/// never mistaken for one of the person's, at a glance and from across the room.
/// </summary>
public sealed class AgentBanner : Control
{
    private string _agent = "An agent";
    private string? _task;
    private Rectangle _hideRect;
    private bool _hoverHide;
    private bool _pressHide;
    private Font? _boldFont;
    private Font? _iconFont;

    /// <summary>The Hide button: the window goes back into hiding, as the toolbar's Hide does.</summary>
    public event EventHandler? HideClicked;

    public AgentBanner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // Every click is a click: see GlyphButton.
        SetStyle(ControlStyles.StandardDoubleClick, false);
        BackColor = Theme.AgentBg;
        Font = new Font("Segoe UI", 9f);   // its own, so disposed with it below
        Height = 30;                       // in 96 dpi units: the form's Dpi autoscale scales it
        // Reachable from the keyboard, where Enter or Space hides the window, and announced
        // with Hide as its action: the band is the prominent control, so not mouse only.
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleDefaultActionDescription = "Hide this agent window";
        UpdateAccessibleName();
    }

    /// <summary>Which agent this window is, and what it last said it is doing (null: it has not said).</summary>
    public void SetAgent(string agent, string? task)
    {
        if (_agent == agent && _task == task)
            return;
        _agent = agent;
        _task = task;
        UpdateAccessibleName();
        Invalidate();
    }

    /// <summary>What the band reads, apart from the pill and the button: for tests and screen readers alike.</summary>
    internal string Caption => Describe(_agent, _task);

    internal static string Describe(string agent, string? task)
        => string.IsNullOrWhiteSpace(task) ? $"{agent} has not said what it is doing" : $"{agent} · {task}";

    private void UpdateAccessibleName() => AccessibleName = "Agent window: " + Caption;

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _boldFont ??= new Font(Font, FontStyle.Bold);
        _iconFont ??= Theme.IconFont(10f);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        // A line of the agent's blue along the bottom, where the band meets the page.
        using (var line = new SolidBrush(Theme.Agent))
            g.FillRectangle(line, 0, Height - S(2), Width, S(2));
        int bandHeight = Height - S(2);

        int x = S(10);
        var robot = new Rectangle(x, 0, S(18), bandHeight);
        TextRenderer.DrawText(g, AgentGlyphs.Robot, _iconFont, robot, Theme.Text,
            flags | TextFormatFlags.HorizontalCenter);
        x = robot.Right + S(6);

        // The pill: white capitals on the agent's blue.
        const string pill = "AGENT";
        var pillText = TextRenderer.MeasureText(g, pill, _boldFont, Size.Empty, TextFormatFlags.NoPadding);
        int pillHeight = Math.Min(bandHeight - S(8), pillText.Height + S(4));
        var pillRect = new Rectangle(x, (bandHeight - pillHeight) / 2, pillText.Width + S(14), pillHeight);
        using (var fill = new SolidBrush(Theme.Agent))
        using (var path = Pill(pillRect))
            g.FillPath(fill, path);
        TextRenderer.DrawText(g, pill, _boldFont, pillRect, Color.White,
            flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        x = pillRect.Right + S(10);

        // The Hide button, on the right.
        const string hide = "Hide";
        var hideText = TextRenderer.MeasureText(g, hide, Font, Size.Empty, TextFormatFlags.NoPadding);
        int hideWidth = S(14) + S(16) + S(6) + hideText.Width + S(4);
        _hideRect = new Rectangle(Width - S(8) - hideWidth, S(3), hideWidth, bandHeight - S(6));
        if (_hoverHide || _pressHide || (Focused && ShowFocusCues))
        {
            using var brush = new SolidBrush(_pressHide ? Theme.AgentPressed : Theme.AgentHover);
            using var path = Pill(_hideRect);
            g.FillPath(brush, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Text, Math.Max(1, S(2) / 2f));
            using var ringPath = Pill(Rectangle.Inflate(_hideRect, -1, -1));
            g.DrawPath(ring, ringPath);
        }
        var eye = new Rectangle(_hideRect.Left + S(8), _hideRect.Top, S(16), _hideRect.Height);
        TextRenderer.DrawText(g, AgentGlyphs.SendBack, _iconFont, eye, Theme.Text, flags | TextFormatFlags.HorizontalCenter);
        if (AgentGlyphs.SendBackNeedsStrike)
            AgentGlyphs.DrawStrike(g, eye, Theme.Text, Math.Max(1.4f, S(3) / 2f));
        var hideLabel = Rectangle.FromLTRB(eye.Right + S(6), _hideRect.Top, _hideRect.Right - S(4), _hideRect.Bottom);
        TextRenderer.DrawText(g, hide, Font, hideLabel, Theme.Text, flags | TextFormatFlags.NoPadding);

        // Name, then what it is doing, cut short with an ellipsis rather than run under Hide.
        var caption = Rectangle.FromLTRB(x, 0, Math.Max(x + 1, _hideRect.Left - S(10)), bandHeight);
        if (string.IsNullOrWhiteSpace(_task))
        {
            TextRenderer.DrawText(g, Caption, Font, caption, Theme.AgentTextDim, flags | TextFormatFlags.EndEllipsis);
        }
        else
        {
            string name = _agent + "  ·  ";
            int nameWidth = Math.Min(caption.Width, TextRenderer.MeasureText(g, name, _boldFont, Size.Empty, TextFormatFlags.NoPadding).Width);
            var nameRect = new Rectangle(caption.Left, 0, nameWidth, bandHeight);
            TextRenderer.DrawText(g, name, _boldFont, nameRect, Theme.Text, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var taskRect = Rectangle.FromLTRB(nameRect.Right, 0, Math.Max(nameRect.Right + 1, caption.Right), bandHeight);
            TextRenderer.DrawText(g, _task, Font, taskRect, Theme.Text, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = _hideRect.Contains(e.Location);
        Cursor = over ? Cursors.Hand : Cursors.Default;
        if (over != _hoverHide)
        {
            _hoverHide = over;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverHide || _pressHide)
        {
            _hoverHide = _pressHide = false;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && _hideRect.Contains(e.Location))
        {
            _pressHide = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool wasPressed = _pressHide;
        _pressHide = false;
        Invalidate();
        if (wasPressed && e.Button == MouseButtons.Left && _hideRect.Contains(e.Location))
            HideClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyData is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            HideClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    /// <summary>For a screen reader's default action, as Enter does.</summary>
    internal void PerformHide() => HideClicked?.Invoke(this, EventArgs.Empty);

    protected override AccessibleObject CreateAccessibilityInstance() => new BannerAccessible(this);

    private sealed class BannerAccessible(AgentBanner owner) : ControlAccessibleObject(owner)
    {
        public override string DefaultAction => "Hide";
        public override void DoDefaultAction() => owner.PerformHide();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _boldFont?.Dispose();
        _boldFont = null;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _iconFont?.Dispose();
        _iconFont = null;   // made again at the new scale on the next paint
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _boldFont?.Dispose();
            _boldFont = null;
            _iconFont?.Dispose();
            _iconFont = null;
            Font.Dispose();
        }
        base.Dispose(disposing);
    }
}
