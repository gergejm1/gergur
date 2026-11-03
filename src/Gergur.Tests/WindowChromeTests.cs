using System.Text.RegularExpressions;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The tab strip is the window's title bar: it draws the minimise, maximise and close
/// buttons, and the window asks it where the bare strip is so that dragging and double
/// clicking it work as they did on the system bar. These are the numbers behind that,
/// because the rest of it (what the strip looks like, whether the window drags) is only
/// ever seen by running it.
/// </summary>
public sealed class WindowChromeTests
{
    private static string Code(params string[] parts)
        => Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource(parts)), @"//[^\n]*", "");

    // ------------------------------------------------------------------ the client area

    [Fact]
    public void AWindowThatIsNotMaximisedStartsItsClientAreaAtItsTopEdge()
    {
        Assert.Equal(100, WindowFrame.ClientTop(100, maximized: false, frameThickness: 8));
    }

    [Fact]
    public void AMaximisedWindowStartsItsClientAreaOnTheScreenNotAFrameAboveIt()
    {
        // The system lays a maximised window out 8px past the screen on every side. Starting
        // the client area at that edge put the top of the strip off the monitor.
        Assert.Equal(0, WindowFrame.ClientTop(-8, maximized: true, frameThickness: 8));
        // And it is the thickness it was given, not 8 written in: it is 9 or more at 125%.
        Assert.Equal(1, WindowFrame.ClientTop(-8, maximized: true, frameThickness: 9));
        Assert.Equal(0, WindowFrame.ClientTop(-10, maximized: true, frameThickness: 10));
    }

    [Fact]
    public void TheFrameHasAThicknessAtEveryScaleAndGrowsWithIt()
    {
        int at96 = WindowFrame.FrameThickness(96);
        int at192 = WindowFrame.FrameThickness(192);
        Assert.True(at96 > 0, $"no frame at 96 dpi: {at96}");
        Assert.True(at192 >= at96, $"the frame shrank with the scale: {at96} then {at192}");
    }

    // ------------------------------------------------------------------ coming back from maximised

    [Theory]
    [InlineData(true, false, false, true)]    // was maximised or minimised, now neither: the system restored it
    [InlineData(false, false, false, false)]  // an ordinary move or resize
    [InlineData(true, true, false, false)]    // still maximised
    [InlineData(true, false, true, false)]    // still minimised
    [InlineData(false, true, false, false)]   // has just been maximised
    [InlineData(false, false, true, false)]   // has just been minimised
    public void OnlyAWindowThatWasMaximisedOrMinimisedAndIsNeitherNowHasComeBack(
        bool was, bool zoomed, bool iconic, bool expected)
    {
        Assert.Equal(expected, WindowFrame.IsComingBack(was, zoomed, iconic));
    }

    [Fact]
    public void ThePlaceTheSystemRestoredTheWindowToIsPutBackAfterTheFormHasHadItsSay()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");

        int hook = form.IndexOf("WindowFrame.WM_WINDOWPOSCHANGED", StringComparison.Ordinal);
        Assert.True(hook >= 0, "the form no longer corrects where it is restored to");
        int end = form.IndexOf("Bounds = placed", hook, StringComparison.Ordinal);
        Assert.True(end > hook, "the correction no longer puts the window back");
        string handler = form[hook..(end + "Bounds = placed".Length)];

        // Read before the form moves it, kept after, and put back only once it is Normal again.
        int placed = handler.IndexOf("WindowFrame.WindowRect(Handle)", StringComparison.Ordinal);
        int tracked = handler.IndexOf("_wasMaximizedOrMinimized = zoomed || iconic", StringComparison.Ordinal);
        int baseCall = handler.IndexOf("base.WndProc(ref m)", StringComparison.Ordinal);
        int putBack = handler.IndexOf("Bounds = placed", StringComparison.Ordinal);
        Assert.True(placed >= 0 && tracked >= 0 && baseCall >= 0 && putBack >= 0, "a step of the correction is missing");
        Assert.True(placed < baseCall && tracked < baseCall, "the position has to be read before the form moves the window");
        Assert.True(putBack > baseCall, "the position has to be put back after the form has moved the window");
        Assert.Contains("WindowState == FormWindowState.Normal", handler);
        // Not gated on the border style: the restore out of a fullscreen page has none.
        Assert.DoesNotContain("HasCustomTitleBar", handler);
    }

    [Fact]
    public void AFullscreenPageGivesBackTheBoundsItTookTheWindowFrom()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");

        int enter = form.IndexOf("private void SetPageFullScreen(bool on)", StringComparison.Ordinal);
        Assert.True(enter >= 0);
        string body = form[enter..Math.Min(form.Length, enter + 3000)];
        Assert.Contains("_preFullScreenBounds = Bounds;", body);
        Assert.Matches(@"_preFullScreenState == FormWindowState\.Normal\)\s*Bounds = _preFullScreenBounds;", body);
    }

    // ------------------------------------------------------------------ the top edge

    private const int Width = 1000;
    private const int Border = 5;
    private const int Corner = 16;

    private static int Hit(int defaultHit, int x, int y, bool canResize = true)
        => WindowFrame.TopEdgeHit(defaultHit, new Point(x, y), Width, Border, Corner, canResize);

    [Fact]
    public void TheTopFewPixelsOfTheStripResizeTheWindow()
    {
        Assert.Equal(WindowFrame.HTTOP, Hit(WindowFrame.HTCLIENT, 500, 0));
        Assert.Equal(WindowFrame.HTTOP, Hit(WindowFrame.HTCLIENT, 500, Border - 1));
    }

    [Fact]
    public void BelowTheEdgeIsTheStripNotTheEdge()
    {
        Assert.Equal(WindowFrame.HTCLIENT, Hit(WindowFrame.HTCLIENT, 500, Border));
        Assert.Equal(WindowFrame.HTCLIENT, Hit(WindowFrame.HTCLIENT, 500, 30));
    }

    [Fact]
    public void TheCornersResizeDiagonally()
    {
        Assert.Equal(WindowFrame.HTTOPLEFT, Hit(WindowFrame.HTCLIENT, 0, 0));
        Assert.Equal(WindowFrame.HTTOPLEFT, Hit(WindowFrame.HTCLIENT, Corner - 1, 2));
        Assert.Equal(WindowFrame.HTTOP, Hit(WindowFrame.HTCLIENT, Corner, 2));
        Assert.Equal(WindowFrame.HTTOPRIGHT, Hit(WindowFrame.HTCLIENT, Width - 1, 0));
        Assert.Equal(WindowFrame.HTTOPRIGHT, Hit(WindowFrame.HTCLIENT, Width - Corner, 2));
        Assert.Equal(WindowFrame.HTTOP, Hit(WindowFrame.HTCLIENT, Width - Corner - 1, 2));
    }

    [Fact]
    public void ASideBorderNearTheTopIsACornerSoItResizesBothWays()
    {
        // The side borders reach the top as they are; without this the last few pixels of
        // the corner would only ever drag the width.
        Assert.Equal(WindowFrame.HTTOPLEFT, Hit(WindowFrame.HTLEFT, -3, 4));
        Assert.Equal(WindowFrame.HTTOPRIGHT, Hit(WindowFrame.HTRIGHT, Width + 3, 4));
        Assert.Equal(WindowFrame.HTLEFT, Hit(WindowFrame.HTLEFT, -3, Corner));
        Assert.Equal(WindowFrame.HTRIGHT, Hit(WindowFrame.HTRIGHT, Width + 3, 100));
    }

    [Fact]
    public void AMaximisedWindowHasNoGripAtTheEdge()
    {
        foreach (int hit in new[] { WindowFrame.HTCLIENT, WindowFrame.HTLEFT, WindowFrame.HTRIGHT })
            Assert.Equal(hit, Hit(hit, 500, 0, canResize: false));
    }

    [Fact]
    public void AnythingElseTheSystemSaidIsLeftAlone()
    {
        Assert.Equal(WindowFrame.HTCAPTION, Hit(WindowFrame.HTCAPTION, 500, 2));
        Assert.Equal(WindowFrame.HTTRANSPARENT, Hit(WindowFrame.HTTRANSPARENT, 500, 2));
    }

    [Fact]
    public void ThePointInAMouseMessageKeepsItsSignOnAMonitorLeftOfTheMainOne()
    {
        // This machine has a screen at a negative x. Read as unsigned, a point there lands
        // near 65000 and every hit test on that monitor answers for the wrong place.
        static IntPtr Pack(int x, int y) => (IntPtr)(((long)(y & 0xFFFF) << 16) | (uint)(x & 0xFFFF));

        Assert.Equal(new Point(100, 200), WindowFrame.PointFrom(Pack(100, 200)));
        Assert.Equal(new Point(-1915, 30), WindowFrame.PointFrom(Pack(-1915, 30)));
        Assert.Equal(new Point(40, -8), WindowFrame.PointFrom(Pack(40, -8)));
    }

    // ------------------------------------------------------------------ the buttons

    /// <summary>Button width and height at 96, 125, 150 and 200 percent.</summary>
    public static TheoryData<int, int> Scales => new()
    {
        { 46, 32 },
        { 58, 40 },
        { 69, 48 },
        { 92, 64 },
    };

    [Theory]
    [MemberData(nameof(Scales))]
    public void TheThreeButtonsSitFlushAgainstTheRightEdgeAndNeverOverlap(int width, int height)
    {
        var rects = CaptionButtons.Layout(1280, width, height);

        Assert.Equal(3, rects.Length);
        Assert.Equal(1280, rects[2].Right);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(new Size(width, height), rects[i].Size);
            Assert.Equal(0, rects[i].Top);
        }
        // Minimise, maximise, close, left to right, touching as the system's own do.
        Assert.Equal(rects[0].Right, rects[1].Left);
        Assert.Equal(rects[1].Right, rects[2].Left);
        Assert.False(rects[0].IntersectsWith(rects[1]));
        Assert.False(rects[1].IntersectsWith(rects[2]));
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void TheRoomTheButtonsTakeIsTheirWidthAndTheGapBeforeThem(int width, int height)
    {
        _ = height;
        int gap = width / 2;
        Assert.Equal(width * 3 + gap, CaptionButtons.Reserved(width, gap));
        // The tabs stop where the buttons and the gap start.
        var rects = CaptionButtons.Layout(1280, width, 32);
        Assert.Equal(1280 - CaptionButtons.Reserved(width, gap), rects[0].Left - gap);
    }

    [Fact]
    public void EachButtonAnswersForItsOwnRectangleAndNothingElseDoes()
    {
        var rects = CaptionButtons.Layout(1280, 46, 32);

        Assert.Equal(WindowButton.Minimize, CaptionButtons.HitTest(rects, new Point(rects[0].Left + 5, 10)));
        Assert.Equal(WindowButton.Maximize, CaptionButtons.HitTest(rects, new Point(rects[1].Left + 5, 10)));
        Assert.Equal(WindowButton.Close, CaptionButtons.HitTest(rects, new Point(rects[2].Left + 5, 10)));
        Assert.Equal(WindowButton.Close, CaptionButtons.HitTest(rects, new Point(1279, 0)));
        Assert.Equal(WindowButton.None, CaptionButtons.HitTest(rects, new Point(rects[0].Left - 1, 10)));
        Assert.Equal(WindowButton.None, CaptionButtons.HitTest(rects, new Point(1279, 32)));
        Assert.Equal(WindowButton.None, CaptionButtons.HitTest([], new Point(10, 10)));
    }

    [Fact]
    public void TheCloseButtonsRedStaysReadableWithAWhiteCross()
    {
        // The one colour in the chrome that is not from the palette: it is red because that is
        // what a person looks for there. White on it has to hold up as a glyph does.
        double measured = ThemeContrastTests.Contrast(Color.White, TabStripControl.CaptionCloseHover);
        Assert.True(measured >= 3.0, $"white on the close button: {measured:0.00}:1, needs 3:1");
    }

    // ------------------------------------------------------------------ the strip

    [Fact]
    public void BareStripIsWhereTheWindowIsDraggedFromAndTheButtonsAreNot()
    {
        using var strip = new TabStripControl { Width = 1200, Height = 42 };

        Assert.True(strip.IsCaptionArea(new Point(600, 20)), "bare strip should drag the window");
        Assert.True(strip.IsCaptionArea(new Point(600, 2)), "the top of the strip is bare too");
        Assert.False(strip.IsCaptionArea(new Point(1190, 10)), "the close button is not a place to drag from");
        Assert.False(strip.IsCaptionArea(new Point(-1, 10)));
        Assert.False(strip.IsCaptionArea(new Point(600, 42)));
    }

    // ------------------------------------------------------------------ wiring

    [Fact]
    public void TheWindowTakesTheTitleBarOffAndGivesItsHeightToTheClientArea()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");

        int calc = form.IndexOf("WM_NCCALCSIZE", StringComparison.Ordinal);
        Assert.True(calc >= 0, "the form no longer handles WM_NCCALCSIZE");
        string handler = form[calc..Math.Min(form.Length, calc + 1400)];
        Assert.Contains("base.WndProc(ref m)", handler);
        Assert.Contains("WindowFrame.ClientTop(", handler);
        Assert.Contains("WindowFrame.IsZoomed(Handle)", handler);
        // Only with the ordinary frame: a page in fullscreen has none, and there is no bar to remove.
        Assert.Contains("HasCustomTitleBar", handler);
    }

    [Fact]
    public void TheStripAsksTheWindowBeforeAnsweringForItsOwnTopEdge()
    {
        string strip = Code("src", "Gergur", "UI", "TabStripControl.cs");

        Assert.Contains("WindowFrame.WM_NCHITTEST", strip);
        Assert.Contains("form.FrameHit(", strip);
        Assert.Contains("HTTRANSPARENT", strip);
    }

    [Fact]
    public void TheWindowButtonsDoWhatTheyAreLabelled()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");

        Assert.Matches(@"MinimizeClicked \+= .*WindowState = FormWindowState\.Minimized", form.Replace("\n", " "));
        Assert.Matches(@"CloseWindowClicked \+= .*BeginInvoke\(new Action\(Close\)\)", form.Replace("\n", " "));
        Assert.Contains("_tabStrip.WindowMaximized = WindowState == FormWindowState.Maximized", form);
    }

    [Fact]
    public void TheWindowButtonsDoNotClaimAnAgentsWindowForThePerson()
    {
        // The system buttons never did, and claiming is not harmless: a claimed window's tabs
        // are saved as the person's session, so claiming an agent's window and then closing it,
        // with none of the person's left, writes the agent's tabs over theirs.
        string form = Code("src", "Gergur", "UI", "MainForm.cs");

        int first = form.IndexOf("_tabStrip.MinimizeClicked +=", StringComparison.Ordinal);
        int last = form.IndexOf("_tabStrip.CloseWindowClicked +=", StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first, "the window buttons are no longer wired together");
        int end = form.IndexOf(';', last);
        Assert.DoesNotContain("ClaimForPerson", form[first..end]);
    }
}
