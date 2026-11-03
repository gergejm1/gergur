using System.Runtime.InteropServices;

namespace Gergur.UI;

/// <summary>
/// The window frame once the system title bar is gone.
///
/// The grey bar above the tabs carried the page title and the three window buttons, and
/// the tab strip under it was a second bar that did nothing about either. The system
/// title bar is now removed by giving its height back to the client area, and the tab
/// strip draws the buttons and answers for the dragging, so one bar does both jobs.
///
/// What is kept is the rest of the frame: left, right and bottom borders, the shadow,
/// snapping to the screen edges and Win+Arrow. Those come with the window style, and
/// replacing the style to remove one bar would have lost them.
/// </summary>
public static class WindowFrame
{
    public const int WM_WINDOWPOSCHANGED = 0x0047;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCHITTEST = 0x0084;

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;

    /// <summary>Answered by a child that wants the window under it to decide instead.</summary>
    public const int HTTRANSPARENT = -1;

    /// <summary>
    /// Where the client area starts after the system has worked out the frame.
    ///
    /// <paramref name="proposedTop"/> is the top edge the window was given before the title
    /// bar was taken off. Normally the client area runs from there. A maximised window is
    /// the exception: the system lays it out a frame's thickness beyond the screen on every
    /// side, so starting the client area at that edge would put the top of the tab strip
    /// off the monitor, the tabs half cut away and the window buttons out of reach.
    /// </summary>
    public static int ClientTop(int proposedTop, bool maximized, int frameThickness)
        => maximized ? proposedTop + frameThickness : proposedTop;

    /// <summary>
    /// What the top edge of the window answers to a hit test.
    ///
    /// With the title bar gone nothing is left at the top to grab for resizing, because the
    /// system drew that grip as part of the bar. A few pixels of the strip answer for it,
    /// the corners diagonally, and the side borders are widened up to the same height so a
    /// corner resizes both ways rather than one.
    /// </summary>
    /// <param name="defaultHit">What the system said for this point.</param>
    /// <param name="client">The point in the window's client area.</param>
    /// <param name="width">The client area's width.</param>
    /// <param name="border">How many pixels down from the top count as the edge.</param>
    /// <param name="corner">How far along the top, from either end, counts as the corner.</param>
    /// <param name="canResize">False when maximised, where the edge is not a grip.</param>
    public static int TopEdgeHit(int defaultHit, Point client, int width, int border, int corner, bool canResize)
    {
        if (!canResize)
            return defaultHit;

        // The side borders reach the top as they are, so a point on one near the top is a
        // corner and not a plain side.
        if (defaultHit == HTLEFT && client.Y < corner)
            return HTTOPLEFT;
        if (defaultHit == HTRIGHT && client.Y < corner)
            return HTTOPRIGHT;

        if (defaultHit != HTCLIENT || client.Y < 0 || client.Y >= border)
            return defaultHit;
        if (client.X < corner)
            return HTTOPLEFT;
        if (client.X >= width - corner)
            return HTTOPRIGHT;
        return HTTOP;
    }

    /// <summary>The point packed into a mouse message's lParam, in screen coordinates.</summary>
    public static Point PointFrom(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        return new Point(unchecked((short)(value & 0xFFFF)), unchecked((short)((value >> 16) & 0xFFFF)));
    }

    /// <summary>
    /// How thick the sizing frame is at this dpi: what a maximised window overhangs the
    /// screen by on each side.
    /// </summary>
    public static int FrameThickness(int dpi)
    {
        try
        {
            return GetSystemMetricsForDpi(SM_CYSIZEFRAME, (uint)dpi)
                + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)dpi);
        }
        catch (EntryPointNotFoundException)
        {
            // An older Windows 10 without the per-monitor call: the system-wide numbers.
            return SystemInformation.VerticalResizeBorderThickness + SystemInformation.Border3DSize.Width;
        }
    }

    /// <summary>
    /// Whether a position change is the system putting the window back to its ordinary size
    /// from maximised or minimised: it was one of those at the last change, and is neither now.
    ///
    /// Tracked here and not read off the form, because a form that is told to restore itself
    /// has already changed what it says its state is by the time the system answers.
    ///
    /// This is the one moment to act, because the form is about to move the window again.
    /// WinForms remembers the size a window had as the size of its client area, and puts
    /// it back by adding the frame the system would give a window with a title bar. This
    /// window's client area runs from the very top, so that sum is one title bar's height too
    /// much, and every maximise and restore (or fullscreen and back) grew the window by it:
    /// 31 pixels at 96 dpi, measured, accumulating. The system has already put the window
    /// where it was, so the form is made to leave it there.
    /// </summary>
    public static bool IsComingBack(bool wasMaximizedOrMinimized, bool zoomed, bool iconic)
        => wasMaximizedOrMinimized && !zoomed && !iconic;

    /// <summary>The window's outer rectangle in screen coordinates.</summary>
    internal static Rectangle WindowRect(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr window);
}
