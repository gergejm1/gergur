namespace Gergur.UI;

/// <summary>
/// Where a drop-down hanging off a toolbar button belongs.
///
/// Windows Forms works this out itself and gets it wrong on a multi-monitor desktop: a
/// menu that will not fit to the right of its button is slid sideways until it fits the
/// nearest space, and when the next monitor along is that space, the menu opens there,
/// on a different screen from the window it belongs to. Measured with three monitors
/// side by side, a button 48px from a monitor's right edge put a 282px menu at x=0, on
/// the screen next door. Asking for the opposite direction only moves the problem to the
/// left edge, where the same thing happens in reverse.
///
/// So the position is worked out here instead, and stays on the button's own screen.
/// </summary>
public static class MenuPlacement
{
    /// <summary>
    /// The top-left corner for a menu of <paramref name="menu"/> size dropped from
    /// <paramref name="button"/>, kept inside <paramref name="screen"/>.
    ///
    /// The menu hangs from the button's right edge, because the button it hangs from
    /// lives at the right end of a toolbar and the room is to its left. It goes above
    /// the button when there is not enough room below, and if it fits nowhere it is
    /// pinned to the top-left of the screen rather than pushed off it.
    /// </summary>
    public static Point For(Rectangle button, Size menu, Rectangle screen)
    {
        int x = button.Right - menu.Width;
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - menu.Width));

        int y = button.Bottom;
        if (y + menu.Height > screen.Bottom)
            y = button.Top - menu.Height;
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - menu.Height));

        return new Point(x, y);
    }
}
