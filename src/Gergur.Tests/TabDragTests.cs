using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Tearing a tab into its own window did not work, and the reason was here: the drag was
/// armed on horizontal movement alone, so pulling a tab straight down never started one.
/// The release was then read as a plain click and the tab was merely selected.
/// </summary>
public sealed class TabDragTests
{
    private const int Threshold = 6;
    private static readonly Point Press = new(300, 20);

    [Fact]
    public void PullingATabStraightDownStartsADrag()
    {
        // The gesture everyone reaches for, and the one that did nothing at all.
        Assert.True(TabDrag.HasBegun(Press, new Point(300, 60), Threshold));
        Assert.True(TabDrag.HasBegun(Press, new Point(301, 200), Threshold));
    }

    [Fact]
    public void PullingATabStraightUpStartsADrag()
    {
        Assert.True(TabDrag.HasBegun(Press, new Point(300, -40), Threshold));
    }

    [Fact]
    public void DraggingSidewaysStillStartsADrag()
    {
        // The reorder gesture, which did work, and has to keep working.
        Assert.True(TabDrag.HasBegun(Press, new Point(340, 20), Threshold));
        Assert.True(TabDrag.HasBegun(Press, new Point(260, 22), Threshold));
    }

    [Theory]
    [InlineData(300, 20)]   // dead still
    [InlineData(305, 24)]   // the wobble of a click
    [InlineData(306, 26)]   // exactly on the threshold, which is not past it
    [InlineData(294, 14)]
    public void ASteadyClickIsNotADrag(int x, int y)
    {
        Assert.False(TabDrag.HasBegun(Press, new Point(x, y), Threshold));
    }

    [Fact]
    public void ADriftDownInsideTheStripSelectsRatherThanReorders()
    {
        // Arming the drag vertically is what makes tear-off work, and it also means a
        // press that slips a few pixels down while still over the strip is now a drag.
        // The slot under the pointer is the next tab along whenever the press was in the
        // right half of one, so without this an unsteady click swaps two tabs.
        Assert.True(TabDrag.HasBegun(Press, new Point(301, 34), Threshold));
        Assert.False(TabDrag.ShouldReorder(Press, new Point(301, 34), Threshold));
    }

    [Fact]
    public void MovingSidewaysStillReorders()
    {
        Assert.True(TabDrag.ShouldReorder(Press, new Point(340, 22), Threshold));
        Assert.True(TabDrag.ShouldReorder(Press, new Point(255, 60), Threshold));
    }

    [Theory]
    [InlineData(-40, true)]     // above the strip
    [InlineData(-29, true)]
    [InlineData(-28, false)]    // inside the margin
    [InlineData(20, false)]     // on the strip
    [InlineData(74, false)]     // just inside the margin below
    [InlineData(75, true)]      // clear below
    [InlineData(400, true)]     // over the page
    public void ClearOfTheStripIsWhereReleasingTearsTheTabOut(int y, bool expected)
    {
        Assert.Equal(expected, TabDrag.IsClearOfStrip(y, stripHeight: 46, margin: 28));
    }
}
