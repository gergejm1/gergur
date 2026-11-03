namespace Gergur.UI;

/// <summary>Which of the window's own buttons a point is over.</summary>
public enum WindowButton
{
    None,
    Minimize,
    Maximize,
    Close,
}

/// <summary>
/// Where the minimise, maximise and close buttons sit in the tab strip.
///
/// Worked out from the strip's width and the size the buttons have at this display scale,
/// for the reason the toolbar's buttons are: numbers written for 96 dpi and left alone put
/// the buttons on top of each other at 150%. The strip also needs to know how much room
/// they take, so the tabs stop short of them instead of running underneath.
/// </summary>
public static class CaptionButtons
{
    /// <summary>The width and height of one button at 96 dpi: the size Windows draws its own.</summary>
    public const int WidthAt96 = 46;
    public const int HeightAt96 = 32;

    /// <summary>
    /// Always left empty between the last tab and the buttons. With a lot of tabs open
    /// there would otherwise be no strip left to take hold of, and nothing to drag the
    /// window by.
    /// </summary>
    public const int GrabGapAt96 = 24;

    /// <summary>Minimise, maximise, close: the first is leftmost, the last is against the edge.</summary>
    public static Rectangle[] Layout(int stripWidth, int buttonWidth, int buttonHeight)
    {
        var close = new Rectangle(stripWidth - buttonWidth, 0, buttonWidth, buttonHeight);
        var maximize = new Rectangle(close.Left - buttonWidth, 0, buttonWidth, buttonHeight);
        var minimize = new Rectangle(maximize.Left - buttonWidth, 0, buttonWidth, buttonHeight);
        return [minimize, maximize, close];
    }

    /// <summary>The width the three buttons and the gap before them take from the right edge.</summary>
    public static int Reserved(int buttonWidth, int grabGap) => buttonWidth * 3 + grabGap;

    public static WindowButton HitTest(IReadOnlyList<Rectangle> layout, Point point)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Count != 3)
            return WindowButton.None;
        if (layout[0].Contains(point))
            return WindowButton.Minimize;
        if (layout[1].Contains(point))
            return WindowButton.Maximize;
        if (layout[2].Contains(point))
            return WindowButton.Close;
        return WindowButton.None;
    }
}
