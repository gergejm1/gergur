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
    /// </summary>
    public static bool IsClearOfStrip(int y, int stripHeight, int margin)
        => y < -margin || y > stripHeight + margin;
}
