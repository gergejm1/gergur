using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Gergur.UI;

/// <summary>
/// "Agent watching": a soft edge in the agents' blue around the page of one of the person's own
/// windows, with a pill naming the agent, while an agent reads or acts on a tab there (asked
/// to look at what the person has open, say). Up for <see cref="Hold"/> after the agent's last
/// touch, then it fades. Agents' own windows have their band instead and never show it.
///
/// A window of its own over the page, owned by the person's window so it stays with it and
/// goes when it is minimised: the page is another process's window, which nothing of the form
/// can draw over, and taking a few pixels from it would reflow the page each time it shows.
/// It is never activated and takes no input: layered and transparent to the mouse
/// (WS_EX_LAYERED, WS_EX_TRANSPARENT, HTTRANSPARENT), never activated (WS_EX_NOACTIVATE,
/// ShowWithoutActivation, MA_NOACTIVATE), off the taskbar and Alt+Tab (WS_EX_TOOLWINDOW).
/// </summary>
internal sealed class AgentWatchGlow : Form
{
    /// <summary>How long it stays after the agent's last touch, before it fades.</summary>
    internal static readonly TimeSpan Hold = TimeSpan.FromSeconds(4);

    private const byte FadeStep = 40;   // per 50 ms tick: about 0.3 s from full to gone

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private DateTime _untilUtc;
    private byte _alpha;
    private string _label = "";
    private Rectangle _page;
    private Bitmap? _art;

    public AgentWatchGlow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Agent watching";
        AccessibleName = "Agent watching";
        _timer.Tick += (_, _) => Tick(DateTime.UtcNow);
    }

    /// <summary>What the pill says: who is watching, and whether it is the page shown or another tab of the window.</summary>
    internal static string Label(string agent, bool shownTab)
        => $"Agent watching{(shownTab ? "" : " another tab")} · {(string.IsNullOrWhiteSpace(agent) ? "an agent" : agent.Trim())}";

    /// <summary>What it shows now, for tests.</summary>
    internal string Caption => _label;

    /// <summary>How opaque it is now, 0 to 255, for tests.</summary>
    internal byte Opacity255 => _alpha;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = HTTRANSPARENT;
            return;
        }
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Shows it over <paramref name="page"/> (screen pixels) on <paramref name="owner"/>, or keeps
    /// it up another <see cref="Hold"/> with what it says now. UI thread.
    /// </summary>
    internal void ShowOver(Form owner, Rectangle page, string label, DateTime nowUtc)
    {
        _untilUtc = nowUtc + Hold;
        // Already up as it should be: only held longer. An agent polling a tab touches this on
        // every request, and a push copies the whole page-sized picture.
        if (Visible && _alpha == 255 && page == _page && label == _label && _art is not null)
            return;
        bool redraw = _art is null || page.Size != _page.Size || label != _label;
        _page = page;
        _label = label;
        if (redraw)
        {
            _art?.Dispose();
            _art = Render(page.Size, label, owner.DeviceDpi);
        }
        _alpha = 255;
        Bounds = page;
        Push();
        if (!Visible)
            Show(owner);
        _timer.Start();
    }

    /// <summary>Keeps it over the page as the window moves or resizes; hidden when there is no page to show it over. UI thread.</summary>
    internal void Follow(Rectangle page, bool pageShown)
    {
        if (!Visible)
            return;
        if (!pageShown || page.Width <= 0 || page.Height <= 0)
        {
            HideNow();
            return;
        }
        if (page == _page)
            return;
        if (page.Size != _page.Size)
        {
            _art?.Dispose();
            _art = Render(page.Size, _label, DeviceDpi);
        }
        _page = page;
        Bounds = page;
        Push();
    }

    /// <summary>Gone at once. UI thread.</summary>
    internal void HideNow()
    {
        _timer.Stop();
        _alpha = 0;
        if (Visible)
            Hide();
    }

    /// <summary>One tick of the timer: held, fading, or gone. For tests, with the time given.</summary>
    internal void Tick(DateTime nowUtc)
    {
        if (nowUtc < _untilUtc)
            return;
        _alpha = Faded(_alpha, _untilUtc, nowUtc);
        if (_alpha == 0)
        {
            HideNow();
            return;
        }
        Push();
    }

    /// <summary>The opacity after one tick: held until <paramref name="untilUtc"/>, then a step less each tick down to 0 (gone).</summary>
    internal static byte Faded(byte alpha, DateTime untilUtc, DateTime nowUtc)
        => nowUtc < untilUtc ? alpha : (byte)Math.Max(0, alpha - FadeStep);

    /// <summary>
    /// The picture: the edge, strongest at the border and gone a few pixels in, and the pill at
    /// the top middle. Transparent everywhere else, so the page shows through untouched.
    /// </summary>
    internal static Bitmap Render(Size size, string label, int dpi)
    {
        int S(int v) => (int)Math.Round(v * dpi / 96.0);
        var art = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(art);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int edge = S(10);
        for (int i = 0; i < edge; i++)
        {
            double fall = 1 - i / (double)edge;
            using var pen = new Pen(Color.FromArgb((int)(190 * fall * fall), Theme.Agent), 1);
            g.DrawRectangle(pen, i, i, size.Width - 1 - 2 * i, size.Height - 1 - 2 * i);
        }

        using var font = new Font("Segoe UI Semibold", 9f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Point);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var text = g.MeasureString(label, font);
        int dot = S(8), padX = S(12), gap = S(7);
        int w = (int)Math.Ceiling(text.Width) + padX * 2 + dot + gap, h = Math.Max(S(26), (int)Math.Ceiling(text.Height) + S(8));
        w = Math.Min(w, Math.Max(S(60), size.Width - edge * 2));
        var pill = new Rectangle((size.Width - w) / 2, S(10), w, h);
        if (pill.Bottom < size.Height - edge)
        {
            using var path = Rounded(pill, h / 2);
            using var fill = new SolidBrush(Color.FromArgb(235, Theme.AgentBg));
            using var rim = new Pen(Color.FromArgb(230, Theme.Agent), Math.Max(1, S(1)));
            g.FillPath(fill, path);
            g.DrawPath(rim, path);
            using var dotBrush = new SolidBrush(Theme.Agent);
            g.FillEllipse(dotBrush, pill.X + padX, pill.Y + (h - dot) / 2, dot, dot);
            using var ink = new SolidBrush(Color.White);
            var room = new RectangleF(pill.X + padX + dot + gap, pill.Y, pill.Right - padX - (pill.X + padX + dot + gap), h);
            using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(label, font, ink, room, format);
        }
        return art;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The picture onto the layered window at the page's place, at the current opacity.</summary>
    private void Push()
    {
        if (_art is null || IsDisposed)
            return;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            bitmap = _art.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, bitmap);
            var size = new SIZE { cx = _art.Width, cy = _art.Height };
            var at = new POINT { x = _page.X, y = _page.Y };
            var from = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = _alpha, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref from, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero)
                SelectObject(mem, old);
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _art?.Dispose();
            _art = null;
        }
        base.Dispose(disposing);
    }

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private static readonly IntPtr HTTRANSPARENT = new(-1);
    private static readonly IntPtr MA_NOACTIVATE = new(3);
    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const int ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
