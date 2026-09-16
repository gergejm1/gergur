using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The toolbar's right-hand buttons at the scales a real desktop uses. The offsets they
/// replaced were fixed at 40, 76 and 112, which is correct only while a button is 32px
/// wide; the buttons scale with the display and those numbers did not.
/// </summary>
public sealed class ToolbarLayoutTests
{
    /// <summary>Button width, gap and margin at 96, 125, 150 and 200 percent.</summary>
    public static TheoryData<int, int, int> Scales => new()
    {
        { 32, 4, 8 },
        { 40, 5, 10 },
        { 48, 6, 12 },
        { 64, 8, 16 },
    };

    [Theory]
    [MemberData(nameof(Scales))]
    public void ThreeButtonsNeverOverlapOrLeaveTheToolbar(int button, int gap, int margin)
    {
        const int toolbarWidth = 1280;
        int[] widths = [button, button, button];

        int[] lefts = ToolbarLayout.RightToLeft(toolbarWidth, widths, gap, margin);

        Assert.Equal(toolbarWidth - margin - button, lefts[0]);
        for (int i = 0; i < lefts.Length; i++)
        {
            Assert.True(lefts[i] >= 0, $"button {i} starts off the left of the toolbar at {lefts[i]}");
            Assert.True(lefts[i] + button <= toolbarWidth,
                $"button {i} runs past the toolbar's right edge: {lefts[i] + button} > {toolbarWidth}");
        }
        for (int i = 1; i < lefts.Length; i++)
        {
            Assert.True(lefts[i] + button <= lefts[i - 1],
                $"buttons {i - 1} and {i} overlap at this scale: {lefts[i] + button} > {lefts[i - 1]}");
            Assert.Equal(gap, lefts[i - 1] - (lefts[i] + button));
        }
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void TheRunOfButtonsGrowsWithTheScale(int button, int gap, int margin)
    {
        // The whole point: the span they take up is derived, not assumed. At 200% the
        // three buttons need 208px, where the old fixed offsets reserved 112.
        int[] lefts = ToolbarLayout.RightToLeft(1280, [button, button, button], gap, margin);

        int span = (1280 - margin) - lefts[^1];
        Assert.Equal(button * 3 + gap * 2, span);
    }

    [Fact]
    public void ANarrowToolbarStillReportsWhereTheButtonsWouldGo()
    {
        // Below the minimum window size the answer can be negative, and the caller
        // clamps the address bar rather than this pretending the room exists.
        int[] lefts = ToolbarLayout.RightToLeft(60, [32, 32, 32], 4, 8);

        Assert.True(lefts[^1] < 0);
        Assert.Equal(60 - 8 - 32, lefts[0]);
    }

    [Fact]
    public void OneButtonIsJustInsideTheMargin()
    {
        int[] lefts = ToolbarLayout.RightToLeft(500, [32], 4, 8);

        Assert.Single(lefts);
        Assert.Equal(500 - 8 - 32, lefts[0]);
    }
}
