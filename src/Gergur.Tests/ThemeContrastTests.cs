using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Contrast of the chrome, measured rather than eyeballed.
///
/// The palette was recoloured from blue to crimson by rotating hue and keeping lightness,
/// which holds contrast by construction. One value was picked by hand instead, and it
/// took the active-tab underline to 2.66:1 and the VPN label below AA without anything
/// noticing. These are the numbers that would have noticed.
/// </summary>
public sealed class ThemeContrastTests
{
    /// <summary>WCAG relative luminance.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    internal static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // 4.5:1 is the AA floor for body text; 3:1 is the floor for a UI component or a
    // large glyph. Every number below is the one the blue palette met or beat.

    [Theory]
    // The tab strip carries the accent as the active-tab underline, and it is the only
    // strong signal of which tab you are on: the fills either side differ by 1.33:1.
    [InlineData("accent on the tab strip", 3.0)]
    public void TheAccentIsVisibleWhereItIsDrawn(string what, double floor)
    {
        double measured = Contrast(Theme.Accent, Theme.TabStripBg);
        Assert.True(measured >= floor, $"{what}: {measured:0.00}:1, needs {floor}:1");
    }

    [Theory]
    [InlineData(4.5)]
    public void TheAccentWorksAsTextOnEveryChromeSurface(double floor)
    {
        // The VPN label, the settings category headers and the bookmark glyph all draw
        // the accent as text on these.
        foreach (var (name, background) in new[]
        {
            ("toolbar", Theme.ToolbarBg),
            ("window", Theme.WindowBg),
            ("tab strip", Theme.TabStripBg),
        })
        {
            double measured = Contrast(Theme.Accent, background);
            Assert.True(measured >= floor, $"accent on the {name}: {measured:0.00}:1, needs {floor}:1");
        }
    }

    [Theory]
    [InlineData(4.5)]
    public void OrdinaryTextIsReadableOnEveryChromeSurface(double floor)
    {
        foreach (var (name, background) in new[]
        {
            ("window", Theme.WindowBg),
            ("toolbar", Theme.ToolbarBg),
            ("tab strip", Theme.TabStripBg),
            ("an idle tab", Theme.TabBg),
            ("a hovered tab", Theme.TabHover),
            ("the active tab", Theme.TabActive),
            ("an input", Theme.InputBg),
        })
        {
            double measured = Contrast(Theme.Text, background);
            Assert.True(measured >= floor, $"text on {name}: {measured:0.00}:1, needs {floor}:1");
        }
    }

    [Theory]
    [InlineData(4.5)]
    public void DimTextIsStillText(double floor)
    {
        foreach (var (name, background) in new[]
        {
            ("the tab strip", Theme.TabStripBg),
            ("the toolbar", Theme.ToolbarBg),
            ("an input", Theme.InputBg),
        })
        {
            double measured = Contrast(Theme.TextDim, background);
            Assert.True(measured >= floor, $"dim text on {name}: {measured:0.00}:1, needs {floor}:1");
        }
    }

    [Fact]
    public void TheCloseCrossIsVisibleOnItsHoverDisc()
    {
        // Asks the strip which colour it actually draws, rather than checking two
        // constants: the first version of this passed while the drawing still used the
        // near-white cross the change was made to get rid of.
        double cross = Contrast(TabStripControl.CloseGlyphColor(hovered: true, sleeping: false), Theme.CloseHover);
        double disc = Contrast(Theme.CloseHover, Theme.TabHover);

        Assert.True(cross >= 3.0, $"the cross on its disc: {cross:0.00}:1");
        Assert.True(disc >= 3.0, $"the disc against a hovered tab: {disc:0.00}:1");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void TheCloseCrossIsVisibleWhenItIsNotHoveredEither(bool hovered, bool sleeping)
    {
        double measured = Contrast(TabStripControl.CloseGlyphColor(hovered, sleeping), Theme.TabActive);
        Assert.True(measured >= 3.0, $"the resting cross: {measured:0.00}:1");
    }

    [Fact]
    public void TheActiveTabIsDistinguishableFromAnIdleOne()
    {
        // Not by much on its own, which is exactly why the underline has to carry it.
        Assert.True(Contrast(Theme.TabActive, Theme.TabBg) > 1.0);
        Assert.True(
            Contrast(Theme.Accent, Theme.TabStripBg) > Contrast(Theme.TabActive, Theme.TabBg),
            "the underline must be the stronger signal, since the fills barely differ");
    }

    [Fact]
    public void TheReadingSurfacesStayReadable()
    {
        Assert.True(Contrast(ListTheme.Text, ListTheme.RowBg) >= 4.5);
        Assert.True(Contrast(ListTheme.Text, ListTheme.RowAlt) >= 4.5);
        Assert.True(Contrast(ListTheme.TextSelected, ListTheme.RowSelected) >= 4.5);
        Assert.True(Contrast(ListTheme.TextDim, ListTheme.RowBg) >= 4.5);
        Assert.True(Contrast(ListTheme.HeaderText, ListTheme.Surface) >= 4.5);
    }
}
