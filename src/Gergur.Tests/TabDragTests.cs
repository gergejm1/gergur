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

    private static readonly Size Strip = new(1200, 46);

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
        Assert.Equal(expected, TabDrag.IsClearOfStrip(new Point(300, y), Strip, margin: 28));
    }

    [Theory]
    [InlineData(-400, true)]    // off the left edge, onto a monitor to the left
    [InlineData(-29, true)]
    [InlineData(-28, false)]    // inside the margin
    [InlineData(600, false)]    // along the strip, which is a reorder
    [InlineData(1228, false)]
    [InlineData(1229, true)]    // off the right edge
    [InlineData(3000, true)]    // well onto a monitor to the right
    public void LeavingTheStripSidewaysCountsToo(int x, bool expected)
    {
        // The bug this was: with monitors side by side, you move a tab to the other screen
        // by dragging it sideways, level with the strip. Height alone never saw it leave,
        // so the gesture was a reorder and pulling left dropped the tab at slot zero: it
        // jumped to the front of its own strip instead of going to the other monitor.
        Assert.Equal(expected, TabDrag.IsClearOfStrip(new Point(x, 20), Strip, margin: 28));
    }

    [Fact]
    public void LettingGoOnAnotherWindowsStripInsideTheMarginStillLeaves()
    {
        // Just across the monitor boundary, still within this strip's margin, is where
        // the other maximised window's strip begins. Height and distance alone call that
        // "still here", so the tab was reordered in its own window, to the front when the
        // other monitor is on the left. Being over another strip has to win.
        var justAcross = new Point(-10, 20);   // 10px left of the edge, inside a 28px margin

        Assert.False(TabDrag.IsClearOfStrip(justAcross, Strip, margin: 28));
        Assert.True(TabDrag.IsOutside(justAcross, Strip, margin: 28, overAnotherStrip: true));
    }

    [Fact]
    public void AnOrdinaryReorderIsStillAReorder()
    {
        // The other half: along its own strip, over nobody else's, a tab stays put.
        Assert.False(TabDrag.IsOutside(new Point(600, 20), Strip, margin: 28, overAnotherStrip: false));
    }

    [Fact]
    public void AnotherMaximisedWindowsStripIsReachable()
    {
        // Two maximised windows on side by side monitors have their strips at exactly the
        // same height, so the only way from one to the other is sideways. That has to count
        // as leaving, or merging a tab into the other window cannot be done at all.
        Assert.True(TabDrag.IsClearOfStrip(new Point(1920 + 200, 20), new Size(1920, 46), margin: 28));
    }

    private static readonly Size Window = new(1200, 800);
    private static readonly Size Grab = new(160, 24);
    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    private static readonly Rectangle LeftMonitor = new(-2560, 0, 2560, 1400);
    private static readonly Rectangle AboveMonitor = new(0, -1080, 1920, 1040);

    [Fact]
    public void ATornOffWindowSitsUnderThePointer()
    {
        var bounds = TabDrag.TearOffBounds(new Point(700, 200), Window, Grab, Primary);

        Assert.Equal(new Point(540, 176), bounds.Location);
        Assert.Equal(Window, bounds.Size);
    }

    [Fact]
    public void ATabDroppedOnAMonitorToTheLeftStaysThere()
    {
        // Every coordinate on a screen left of the primary is negative. Clamping the
        // position at zero, which the old code did, put the new window back on the
        // primary monitor.
        var bounds = TabDrag.TearOffBounds(new Point(-1500, 400), Window, Grab, LeftMonitor);

        Assert.True(LeftMonitor.Contains(bounds), $"{bounds} is not on {LeftMonitor}");
        Assert.True(bounds.X < 0);
    }

    [Fact]
    public void ATabDroppedOnAMonitorAboveStaysThere()
    {
        var bounds = TabDrag.TearOffBounds(new Point(900, -600), Window, Grab, AboveMonitor);

        Assert.True(AboveMonitor.Contains(bounds), $"{bounds} is not on {AboveMonitor}");
        Assert.True(bounds.Y < 0);
    }

    [Fact]
    public void ATabDroppedNearAnEdgeIsKeptOnScreen()
    {
        var bounds = TabDrag.TearOffBounds(new Point(1910, 1030), Window, Grab, Primary);

        Assert.True(Primary.Contains(bounds), $"{bounds} runs off {Primary}");
    }

    [Fact]
    public void AWindowTooBigForTheScreenItLandsOnIsShrunkToFit()
    {
        // A big window torn onto a smaller second monitor is the ordinary case.
        var laptop = new Rectangle(1920, 0, 1366, 728);

        var bounds = TabDrag.TearOffBounds(new Point(2500, 300), new Size(1920, 1040), Grab, laptop);

        Assert.True(laptop.Contains(bounds), $"{bounds} runs off {laptop}");
        Assert.Equal(laptop.Size, bounds.Size);
    }
}
