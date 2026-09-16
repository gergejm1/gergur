using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The cases come from a desktop with three monitors side by side, which is where the
/// menu was landing on the wrong screen: DISPLAY3 at x -1920, the primary at 0, and
/// DISPLAY2 at 1920. Each one below was measured against the real drop-down first, so
/// these are not invented shapes.
/// </summary>
public sealed class MenuPlacementTests
{
    private static readonly Rectangle Left = new(-1920, 0, 1920, 1040);
    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    private static readonly Rectangle Right = new(1920, 0, 1920, 1040);
    private static readonly Size Menu = new(282, 400);

    private static Rectangle Button(int x, int y) => new(x, y, 32, 30);

    private static bool Inside(Rectangle screen, Point at, Size size)
        => at.X >= screen.Left && at.X + size.Width <= screen.Right
        && at.Y >= screen.Top && at.Y + size.Height <= screen.Bottom;

    [Fact]
    public void AMenuNearAMonitorsRightEdgeStaysOnThatMonitor()
    {
        // The case that started this. The button is 48px from the boundary, so a menu
        // opening rightward crosses onto the primary screen, and Windows Forms put it
        // there rather than opening it the other way.
        var button = Button(-48, 236);

        Point at = MenuPlacement.For(button, Menu, Left);

        Assert.True(Inside(Left, at, Menu),
            $"the menu landed at {at}, outside the monitor its button is on ({Left}).");
        Assert.Equal(button.Right - Menu.Width, at.X);
    }

    [Fact]
    public void AMenuNearAMonitorsLeftEdgeStaysOnThatMonitor()
    {
        // And the mirror of it, which is what asking for the opposite direction gave
        // instead of a fix: a button just inside the left edge, hanging leftward.
        var button = Button(1930, 236);

        Point at = MenuPlacement.For(button, Menu, Right);

        Assert.True(Inside(Right, at, Menu),
            $"the menu landed at {at}, outside the monitor its button is on ({Right}).");
        Assert.Equal(Right.Left, at.X);
    }

    [Theory]
    [InlineData(-1198, 236)]   // comfortably inside the left monitor
    [InlineData(802, 236)]     // the primary
    [InlineData(3002, 236)]    // the right monitor
    public void AMenuWithRoomHangsFromTheButtonsRightEdge(int x, int y)
    {
        var screen = x < 0 ? Left : x < 1920 ? Primary : Right;
        var button = Button(x, y);

        Point at = MenuPlacement.For(button, Menu, screen);

        Assert.True(Inside(screen, at, Menu), $"the menu landed at {at}, outside {screen}.");
        Assert.Equal(button.Right - Menu.Width, at.X);
        Assert.Equal(button.Bottom, at.Y);
    }

    [Fact]
    public void AMenuWithNoRoomBelowOpensAbove()
    {
        // A short window near the foot of the screen. Dropping down would run the menu
        // off the bottom, and a menu you cannot reach the end of is no menu.
        var button = Button(1200, 900);

        Point at = MenuPlacement.For(button, Menu, Primary);

        Assert.True(Inside(Primary, at, Menu), $"the menu landed at {at}, outside {Primary}.");
        Assert.Equal(button.Top - Menu.Height, at.Y);
    }

    [Fact]
    public void AMenuTallerThanTheScreenIsPinnedRatherThanPushedOff()
    {
        // Nothing fits. Better the top of a too-long menu than the middle of it.
        var button = Button(1200, 500);
        var tall = new Size(282, 2000);

        Point at = MenuPlacement.For(button, tall, Primary);

        Assert.Equal(Primary.Top, at.Y);
        Assert.True(at.X >= Primary.Left);
    }

    [Fact]
    public void AMenuWiderThanTheScreenStartsAtItsLeftEdge()
    {
        var button = Button(1900, 100);
        var wide = new Size(3000, 400);

        Point at = MenuPlacement.For(button, wide, Primary);

        Assert.Equal(Primary.Left, at.X);
    }
}
