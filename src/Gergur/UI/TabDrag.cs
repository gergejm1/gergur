namespace Gergur.UI;

/// <summary>
/// The two decisions a tab drag rests on, kept out of the control so they can be
/// exercised without a mouse.
/// </summary>
public static class TabDrag
{
    /// <summary>
    /// Whether a press has moved far enough to be a drag rather than a click.
    ///
    /// This used to look at horizontal movement alone, which quietly made tearing a tab
    /// off impossible: pulling a tab straight down out of the strip, which is the gesture
    /// everyone reaches for, barely moves x at all, so the drag never started, the
    /// pointer was never seen leaving the strip, and the release was treated as an
    /// ordinary click that just selected the tab. Nothing looked broken; it simply did
    /// nothing. Either axis starts a drag.
    /// </summary>
    public static bool HasBegun(Point press, Point now, int threshold)
        => Math.Abs(now.X - press.X) > threshold || Math.Abs(now.Y - press.Y) > threshold;

    /// <summary>
    /// Whether releasing here should reorder rather than count as a click.
    ///
    /// Only sideways movement reorders. Arming the drag vertically is what makes a
    /// tear-off possible, but it also means a press that drifts a few pixels down while
    /// still over the strip now counts as a drag, and the drop slot under the pointer is
    /// the neighbouring one whenever the press was in the right half of a tab. Without
    /// this, a slightly unsteady click swaps two tabs instead of selecting one.
    /// </summary>
    public static bool ShouldReorder(Point press, Point now, int threshold)
        => Math.Abs(now.X - press.X) > threshold;

    /// <summary>
    /// Whether the pointer has left the strip far enough that releasing should tear the
    /// tab out rather than drop it back between its neighbours. The margin keeps a shaky
    /// hand from throwing a tab into its own window.
    ///
    /// Either direction. This used to look at height alone, and with monitors side by
    /// side the natural way to move a tab to the other screen is sideways, level with the
    /// strip: the pointer never went above or below it, so the whole gesture counted as a
    /// reorder, and pulling off the left edge worked out a drop slot of zero. The tab
    /// jumped to the front of its own strip instead of going anywhere. It also made
    /// another window's strip unreachable when both windows were maximised, because the
    /// two strips sit at exactly the same height.
    /// </summary>
    public static bool IsClearOfStrip(Point pointer, Size strip, int margin)
        => pointer.Y < -margin || pointer.Y > strip.Height + margin
        || pointer.X < -margin || pointer.X > strip.Width + margin;

    /// <summary>
    /// Whether letting go here takes the tab out of its own strip: clear of it, or on top
    /// of another window's strip, whichever comes first.
    ///
    /// The second half matters within the margin. Two maximised windows on side by side
    /// monitors have their strips meeting at the monitor boundary, so the first pixels of
    /// the other window's strip are still inside this strip's margin. Letting go there,
    /// on a strip that was lit up to accept the tab, reordered it in its own window
    /// instead: to the front when the other monitor is on the left, which is exactly the
    /// bug as first reported.
    /// </summary>
    public static bool IsOutside(Point pointer, Size strip, int margin, bool overAnotherStrip)
        => overAnotherStrip || IsClearOfStrip(pointer, strip, margin);

    /// <summary>
    /// Where a torn off window goes: under the pointer, on the monitor it was dropped on.
    ///
    /// The window is placed so the pointer sits a little way in from its top left corner,
    /// in the title bar at ordinary scaling, and then
    /// kept inside the working area of the screen under the drop point. The previous
    /// version clamped the position at zero, which is only the edge of the primary
    /// monitor: on a screen to the left of it, or above it, every coordinate is negative,
    /// so a tab dropped there came back onto the primary monitor instead.
    /// </summary>
    /// <param name="drop">Where the pointer was released, in screen coordinates.</param>
    /// <param name="size">The size the new window would like to be.</param>
    /// <param name="grab">How far into the window the pointer should sit.</param>
    /// <param name="workingArea">The working area of the screen under <paramref name="drop"/>.</param>
    public static Rectangle TearOffBounds(Point drop, Size size, Size grab, Rectangle workingArea)
    {
        // A window bigger than the screen it lands on is shrunk to fit rather than left
        // hanging off it, which on a smaller second monitor is the ordinary case.
        int width = Math.Min(size.Width, workingArea.Width);
        int height = Math.Min(size.Height, workingArea.Height);

        int x = drop.X - grab.Width;
        int y = drop.Y - grab.Height;

        x = Math.Clamp(x, workingArea.Left, workingArea.Right - width);
        y = Math.Clamp(y, workingArea.Top, workingArea.Bottom - height);

        return new Rectangle(x, y, width, height);
    }
}
