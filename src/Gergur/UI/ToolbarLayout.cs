namespace Gergur.UI;

/// <summary>
/// Where the right-hand toolbar buttons go.
///
/// They used to sit at fixed offsets from the toolbar's right edge, 40, 76 and 112,
/// spaced for a 32px button. The buttons themselves are scaled by AutoScaleMode.Dpi, so
/// at 150% they are 48 wide and still 36 apart: they overlap, and the last one hangs off
/// the end. Nobody noticed with two of them, because two overlapping buttons still look
/// like two buttons. Working the positions out from the widths the buttons actually have
/// is what makes that impossible rather than unlikely.
/// </summary>
public static class ToolbarLayout
{
    /// <summary>
    /// The left edge of each button, in the order given, packed against the right edge
    /// of a toolbar <paramref name="toolbarWidth"/> wide. The first entry is the
    /// rightmost button.
    /// </summary>
    public static int[] RightToLeft(int toolbarWidth, IReadOnlyList<int> widths, int gap, int margin)
    {
        ArgumentNullException.ThrowIfNull(widths);

        var lefts = new int[widths.Count];
        int right = toolbarWidth - margin;
        for (int i = 0; i < widths.Count; i++)
        {
            lefts[i] = right - widths[i];
            right = lefts[i] - gap;
        }
        return lefts;
    }
}
