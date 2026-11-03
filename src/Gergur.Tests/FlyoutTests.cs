using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Blocking;
using Gergur.Data;
using Gergur.UI;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The main menu and the downloads panel, both drawn by hand over the page. What they look
/// like is only ever seen by running them; these hold the parts that can be held without
/// a screen: the words a download is described with, where things are laid out and what
/// each place does, where the keyboard goes, and that every command the old menu had is
/// still reachable from the new one.
/// </summary>
public sealed class FlyoutTests
{
    private static string Code(params string[] parts)
        => Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource(parts)), @"//[^\n]*", "");

    private static readonly DateTime T0 = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Is this click on a panel's button the one that just closed it? Decided by when the
    /// press began. The old fixed 300 ms before the release reopened a panel on any press
    /// held longer than that.
    /// </summary>
    [Theory]
    [InlineData(10, 0, 100, true)]       // closed as the press went down: the click closed it
    [InlineData(10, 0, 900, true)]       // the same with the button held most of a second
    [InlineData(-30, 0, 100, true)]      // closed a moment before the press was recorded
    [InlineData(-200, 0, 100, false)]    // closed before this press began: a fresh click opens it
    [InlineData(-5000, 0, 100, false)]   // closed long ago
    public void APanelsButtonTellsTheClickThatClosedItFromAFreshOne(int hiddenMs, int pressMs, int nowMs, bool closedByIt)
        => Assert.Equal(closedByIt, FlyoutPanel.ClosedByThisClick(T0.AddMilliseconds(hiddenMs), T0.AddMilliseconds(pressMs), T0.AddMilliseconds(nowMs)));

    [Fact]
    public void WithNoPressToGoByTheOldWindowStands()
    {
        Assert.True(FlyoutPanel.ClosedByThisClick(T0, null, T0.AddMilliseconds(200)));
        Assert.False(FlyoutPanel.ClosedByThisClick(T0, null, T0.AddMilliseconds(400)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int msg, IntPtr wParam, IntPtr lParam);

    private static T CallProtected<T>(Control control, string name, params object?[] args)
        => (T)typeof(Control).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            args.Select(a => a?.GetType() ?? typeof(Control)).ToArray())!.Invoke(control, args)!;

    /// <summary>
    /// The real button: every click is a click (no second click turned into a DoubleClick),
    /// and a click reports the press behind it while it runs, and never again after. Whether
    /// the panel then opens or stays shut is ClosedByThisClick, above; the whole toggle on
    /// real windows needs a desktop to click on, and was run on a hidden one.
    /// </summary>
    [Fact]
    public void AToolbarButtonCountsEveryClickAndReportsOnlyThePressBehindIt()
    {
        OnSta(() =>
        {
            using var b = new GlyphButton("x");
            Assert.False(CallProtected<bool>(b, "GetStyle", ControlStyles.StandardDoubleClick));
            Assert.True(CallProtected<bool>(b, "GetStyle", ControlStyles.StandardClick));

            var seen = new List<DateTime?>();
            b.Click += (_, _) => seen.Add(b.PressOfThisClickUtc);
            Assert.Null(b.PressOfThisClickUtc);   // outside a click there is no press to judge by

            var before = DateTime.UtcNow;
            SendMessage(b.Handle, 0x0201, (IntPtr)1, IntPtr.Zero);   // WM_LBUTTONDOWN: the press
            b.Capture = false;                                     // let go without a release here
            CallProtected<object?>(b, "InvokeOnClick", b, EventArgs.Empty);
            // Then a click with no press of its own, as from code or accessibility.
            CallProtected<object?>(b, "InvokeOnClick", b, EventArgs.Empty);

            Assert.Equal(2, seen.Count);
            Assert.True(seen[0] >= before, "the click did not report the press behind it");
            Assert.Null(seen[1]);                 // the press was used up by the first click
            Assert.Null(b.PressOfThisClickUtc);
        });
    }

    /// <summary>WinForms wants an STA thread; no message loop runs, so this cannot hang.</summary>
    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }

    // ------------------------------------------------------------------ download words

    [Theory]
    [InlineData("report.final.PDF", "PDF")]
    [InlineData("photos.zip", "ZIP")]
    [InlineData(@"C:\Users\x\Downloads\movie.mp4", "MP4")]
    [InlineData(".bashrc", "")]          // a dot at the start is a hidden file, not an extension
    [InlineData("archive.toolong", "")]  // more than four is not a badge
    [InlineData("noext", "")]
    [InlineData("trailing.", "")]
    [InlineData("weird.p-f", "")]
    [InlineData("", "")]
    public void TheBadgeShowsTheExtensionTheWayTheDownloadsPageDoes(string name, string expected)
        => Assert.Equal(expected, DownloadText.Extension(name));

    [Theory]
    // Taken from bridge.js's hueOf, run under node: the panel and the page must colour a
    // file the same, or the same download looks like two different things.
    [InlineData("PDF", 205)]
    [InlineData("ZIP", 260)]
    [InlineData("file", 243)]
    [InlineData("EXE", 207)]
    [InlineData("MP4", 188)]
    [InlineData("\u00e9", 100)]
    public void TheBadgeColourIsTheSameHueThePageGivesIt(string text, int hue)
        => Assert.Equal(hue, DownloadText.Hue(text));

    [Fact]
    public void HslReadsAsCssReadsIt()
    {
        Assert.Equal(Color.FromArgb(255, 0, 0), DownloadText.FromHsl(0, 1, 0.5));
        Assert.Equal(Color.FromArgb(0, 0, 255), DownloadText.FromHsl(240, 1, 0.5));
        Assert.Equal(Color.FromArgb(128, 128, 128), DownloadText.FromHsl(120, 0, 0.5));
        // The badge's own pair is a dark fill under light text, at every hue.
        for (int hue = 0; hue < 360; hue += 15)
        {
            var fill = DownloadText.FromHsl(hue, 0.34, 0.23);
            var text = DownloadText.FromHsl(hue, 0.75, 0.88);
            Assert.True(text.GetBrightness() - fill.GetBrightness() > 0.5f, $"hue {hue}: the badge text does not stand off its fill");
        }
    }

    [Theory]
    [InlineData("https://www.example.com/file.zip", "example.com")]
    [InlineData("https://cdn.example.com/a", "cdn.example.com")]
    [InlineData("http://localhost:4321/x", "localhost")]
    [InlineData("data:text/plain;base64,aGk=", "")]
    [InlineData("blob:https://example.com/123", "")]
    [InlineData("not a url", "")]
    public void TheSiteIsTheHostWithoutWww(string url, string expected)
        => Assert.Equal(expected, DownloadText.Site(url));

    [Fact]
    public void ARunningDownloadSaysHowMuchOfHowMuchAndFromWhere()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2.4 MB of 11.8 MB \u00b7 example.com", DownloadText.Status(
            CoreWebView2DownloadState.InProgress, (long)(2.4 * 1048576), (long)(11.8 * 1048576),
            "https://www.example.com/a.zip", now, now, missing: false));
        // Size not known yet: what has arrived, and nothing invented.
        Assert.Equal("512 KB", DownloadText.Status(
            CoreWebView2DownloadState.InProgress, 512 * 1024, 0, "data:x", now, now, missing: false));
    }

    [Fact]
    public void AFinishedDownloadSaysItsSizeWhereFromAndWhen()
    {
        var started = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("3 MB \u00b7 example.com \u00b7 5 min ago", DownloadText.Status(
            CoreWebView2DownloadState.Completed, 3 * 1048576, 3 * 1048576,
            "https://example.com/f.pdf", started, started.AddMinutes(5), missing: false));
    }

    [Fact]
    public void AStoppedOrMissingDownloadSaysSoFirst()
    {
        var now = DateTime.UtcNow;
        Assert.StartsWith("Failed or cancelled", DownloadText.Status(
            CoreWebView2DownloadState.Interrupted, 10, 100, "https://example.com/f", now, now, missing: false));
        Assert.StartsWith("Deleted or moved", DownloadText.Status(
            CoreWebView2DownloadState.Completed, 100, 100, "https://example.com/f", now, now, missing: true));
    }

    [Fact]
    public void AgoUsesTheFewestWords()
    {
        var t = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Just now", DownloadText.Ago(t, t.AddSeconds(20)));
        Assert.Equal("5 min ago", DownloadText.Ago(t, t.AddMinutes(5)));
        Assert.Equal("3 h ago", DownloadText.Ago(t, t.AddHours(3)));
        Assert.Equal("Yesterday", DownloadText.Ago(t, t.AddHours(30)));
    }

    [Theory]
    [InlineData(50, 100, 0.5)]
    [InlineData(150, 100, 1.0)]   // the engine can over-report; the bar never runs past its end
    public void ProgressIsAFractionOfTheKnownSize(long received, long total, double expected)
        => Assert.Equal(expected, DownloadText.Fraction(received, total));

    [Fact]
    public void ProgressOfAnUnknownSizeIsNotMadeUp()
        => Assert.Null(DownloadText.Fraction(500, 0));

    // ------------------------------------------------------------------ the downloads panel

    private static DownloadItem Item(string name, CoreWebView2DownloadState state, long received = 100, long total = 100)
        => new("https://example.com/" + name, Path.Combine(Path.GetTempPath(), name), received, total, state);

    [Fact]
    public void EachDownloadGetsTheActionsItsStateAllows()
    {
        OnSta(() =>
        {
            var downloads = new DownloadManager();
            downloads.Add(Item("stopped.zip", CoreWebView2DownloadState.Interrupted, 10));
            downloads.Add(Item("done.pdf", CoreWebView2DownloadState.Completed));
            downloads.Add(Item("running.iso", CoreWebView2DownloadState.InProgress, 30));
            int shown = 0;
            using var panel = new DownloadsFlyout(downloads, () => shown++, _ => { });
            panel.Relayout(keepPlace: false);

            var spots = panel.Spots;
            // Newest first: running (its line does nothing, Cancel does), done (open, folder),
            // stopped (nothing to do), then the way to the page.
            Assert.Collection(spots,
                s => { Assert.Equal("running.iso", s.Spoken); Assert.False(s.Enabled); },
                s => { Assert.Equal("Cancel running.iso", s.Spoken); Assert.True(s.KeepOpen); },
                s => Assert.Equal("Open done.pdf", s.Spoken),
                s => { Assert.Equal("Show done.pdf in folder", s.Spoken); Assert.Equal("Show in folder", s.Tip); },
                s => { Assert.Equal("stopped.zip", s.Spoken); Assert.False(s.Enabled); },
                s => Assert.Equal("Show all downloads", s.Spoken));

            panel.RunSpot(spots[^1]);
            Assert.Equal(1, shown);
        });
    }

    [Fact]
    public void ThePanelListsTheLatestFewAndCountsTheRestOnItsWayThrough()
    {
        OnSta(() =>
        {
            var downloads = new DownloadManager();
            for (int i = 0; i < DownloadsFlyout.MaxRows + 3; i++)
                downloads.Add(Item($"f{i}.txt", CoreWebView2DownloadState.Interrupted));
            using var panel = new DownloadsFlyout(downloads, () => { }, _ => { });
            panel.Relayout(keepPlace: false);

            Assert.Equal(DownloadsFlyout.MaxRows + 1, panel.Spots.Count);
            Assert.Equal($"Show all downloads ({DownloadsFlyout.MaxRows + 3})", panel.Spots[^1].Spoken);
        });
    }

    [Fact]
    public void NothingOnThePanelOverlapsOrRunsOffIt()
    {
        OnSta(() =>
        {
            var downloads = new DownloadManager();
            downloads.Add(Item("a.zip", CoreWebView2DownloadState.Completed));
            downloads.Add(Item("b.zip", CoreWebView2DownloadState.InProgress, 1, 0));
            using var panel = new DownloadsFlyout(downloads, () => { }, _ => { });
            panel.Relayout(keepPlace: false);
            AssertLaidOutInside(panel.Spots, panel.ClientSize);
        });
    }

    [Fact]
    public void AnEmptyPanelStillLeadsToThePage()
    {
        OnSta(() =>
        {
            using var panel = new DownloadsFlyout(new DownloadManager(), () => { }, _ => { });
            panel.Relayout(keepPlace: false);
            Assert.Equal("Show all downloads", Assert.Single(panel.Spots).Spoken);
        });
    }

    private static void Mouse(Control control, string handler, Point at)
        => typeof(Control).GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                [typeof(MouseEventArgs)])!
            .Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0)]);

    [Fact]
    public void AClickSurvivesThePanelBeingLaidOutAgainBetweenPressAndRelease()
    {
        // A running download lays the panel out again several times a second. A press
        // that was forgotten each time could not be finished, so Cancel could not be
        // clicked while a download was running, which is the only time it is there.
        OnSta(() =>
        {
            var downloads = new DownloadManager();
            downloads.Add(Item("running.iso", CoreWebView2DownloadState.InProgress, 30));
            int shown = 0;
            using var panel = new DownloadsFlyout(downloads, () => shown++, _ => { });
            panel.Relayout(keepPlace: false);
            var centre = Centre(panel.Spots[^1].Bounds);

            Mouse(panel, "OnMouseDown", centre);
            panel.Relayout();   // progress came in
            panel.Relayout();
            Mouse(panel, "OnMouseUp", centre);

            Assert.Equal(1, shown);
        });
    }

    [Fact]
    public void AReleaseOverSomethingElseAfterARelayoutDoesNothing()
    {
        OnSta(() =>
        {
            var downloads = new DownloadManager();
            downloads.Add(Item("first.iso", CoreWebView2DownloadState.InProgress, 30));
            int shown = 0;
            using var panel = new DownloadsFlyout(downloads, () => shown++, _ => { });
            panel.Relayout(keepPlace: false);
            var centre = Centre(panel.Spots[^1].Bounds);

            Mouse(panel, "OnMouseDown", centre);
            // A new download pushes "Show all" down a row; the pointer is now over a row.
            downloads.Add(Item("second.iso", CoreWebView2DownloadState.InProgress, 30));
            panel.Relayout();
            Mouse(panel, "OnMouseUp", centre);

            Assert.Equal(0, shown);
        });
    }

    private static Point Centre(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [Fact]
    public void WhetherAFileIsGoneIsAskedOncePerOpeningNotOnEveryPaint()
    {
        OnSta(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"gergur-missing-{Guid.NewGuid():N}.txt");
            var downloads = new DownloadManager();
            var item = new DownloadItem("https://example.com/x.txt", path, 1, 1, CoreWebView2DownloadState.Completed);
            downloads.Add(item);
            using var panel = new DownloadsFlyout(downloads, () => { }, _ => { });
            try
            {
                Assert.True(panel.IsMissing(item));
                File.WriteAllText(path, "x");
                // Still the answer from when it opened: the disk is not asked again per paint.
                Assert.True(panel.IsMissing(item));
            }
            finally
            {
                File.Delete(path);
            }
            // And only a finished download can be missing.
            Assert.False(panel.IsMissing(Item("running.iso", CoreWebView2DownloadState.InProgress, 1)));
            string code = Code("Gergur", "UI", "DownloadsFlyout.cs");
            Assert.DoesNotContain("File.Exists", code[code.IndexOf("private void PaintRow(", StringComparison.Ordinal)..code.IndexOf("internal bool IsMissing(", StringComparison.Ordinal)]);
        });
    }

    [Fact]
    public void ThePanelsFontsAreMadeInPixelsForTheirOwnDpi()
    {
        // In points they are measured against the system DPI, the wrong size on a second
        // monitor scaled differently, and WinForms only rescales the form's own Font.
        OnSta(() =>
        {
            using var panel = new DownloadsFlyout(new DownloadManager(), () => { }, _ => { });
            Assert.Equal(GraphicsUnit.Pixel, panel.Font.Unit);
            Assert.Equal(9f * panel.DeviceDpi / 72f, panel.Font.Size, 3);
            using var menu = new MainMenuFlyout();
            Assert.Equal(GraphicsUnit.Pixel, menu.Font.Unit);
            Assert.Equal(AutoScaleMode.None, menu.AutoScaleMode);
        });
        string flyout = Code("Gergur", "UI", "FlyoutPanel.cs");
        // Made on the button's monitor before anything is measured, and again on a move.
        Assert.Matches(@"if \(!IsHandleCreated\)\s*\{\s*Location = _anchor\.Location;\s*CreateHandle\(\);\s*\}\s*Relayout\(keepPlace: false\);", flyout);
        Assert.Matches(@"protected override void OnDpiChanged\(DpiChangedEventArgs e\)", flyout);
        foreach (string panelFile in new[] { "DownloadsFlyout.cs", "MainMenuFlyout.cs" })
            Assert.DoesNotContain("new Font(", Code("Gergur", "UI", panelFile));
    }

    [Fact]
    public void APanelGoesWhenTheButtonItHangsFromMoves()
    {
        string flyout = Code("Gergur", "UI", "FlyoutPanel.cs");
        Assert.Matches(@"_button is \{ IsDisposed: false \} button && ButtonOnScreen\(button\) != _anchor\)\s*\{\s*Dismiss\(\);", flyout);
        // A quick click, down and up between two ticks, is caught by the pressed-since bit.
        Assert.Matches(@"\(down && !_wasDown\) \|\| \(!down && clickedSince\)", flyout);
    }

    private static HistoryVisit Visit(string url, string title, int minutesAgo)
        => new(DateTime.UtcNow.AddMinutes(-minutesAgo), url, title);

    [Fact]
    public void TheHistoryPanelListsRecentWebPagesOnceEach()
    {
        var visits = new[]
        {
            Visit("https://www.youtube.com/watch?v=a", "A song", 1),
            Visit("gergur://history", "History", 2),            // the browser's own page
            Visit("https://example.com/", "Example", 3),
            Visit("https://www.youtube.com/watch?v=a", "A song", 4),   // seen already
            Visit("file:///C:/secret.txt", "secret.txt", 5),     // not a web page
            Visit("https://news.example.org/", "News", 6),
        };
        var recent = HistoryFlyout.Recent(visits, 8);
        Assert.Equal(new[] { "https://www.youtube.com/watch?v=a", "https://example.com/", "https://news.example.org/" },
            recent.Select(v => v.Url));
        Assert.Equal(2, HistoryFlyout.Recent(visits, 2).Count);
    }

    [Fact]
    public void EachPlaceOnTheHistoryPanelOpensItAndTheLastLineIsThePage()
    {
        OnSta(() =>
        {
            var opened = new List<string>();
            int page = 0;
            using var panel = new HistoryFlyout(() => null, opened.Add, () => page++);
            panel.Fill([Visit("https://example.com/a", "", 1), Visit("https://example.com/b", "Bee", 2)]);
            panel.Relayout(keepPlace: false);
            Assert.Collection(panel.Spots,
                s => Assert.Equal("Open https://example.com/a", s.Spoken),   // no title: the address
                s => Assert.Equal("Open Bee", s.Spoken),
                s => Assert.Equal("Show all history", s.Spoken));
            AssertLaidOutInside(panel.Spots, panel.ClientSize);
            panel.RunSpot(panel.Spots[1]);
            panel.RunSpot(panel.Spots[^1]);
            Assert.Equal(["https://example.com/b"], opened);
            Assert.Equal(1, page);
        });
    }

    [Fact]
    public void AHistoryPanelThatCannotReadSaysSoAndStillLeadsToThePage()
    {
        OnSta(() =>
        {
            using var panel = new HistoryFlyout(() => null, _ => { }, () => { });
            panel.Fill(null);
            panel.Relayout(keepPlace: false);
            Assert.Equal("Show all history", Assert.Single(panel.Spots).Spoken);
        });
        // And the read is off the UI thread: the log is read whole every time.
        Assert.Contains("Task.Run(_read)", Code("Gergur", "UI", "HistoryFlyout.cs"));
    }

    [Fact]
    public void TheDownloadsPanelNeverTakesTheKeyboard()
    {
        // It opens by itself when a download starts, which can be in the middle of typing an
        // address or a message. Taking focus then would send the rest of the words nowhere.
        string panel = Code("Gergur", "UI", "DownloadsFlyout.cs");
        Assert.Matches(@"protected override bool TakesFocus => false;", panel);
        string flyout = Code("Gergur", "UI", "FlyoutPanel.cs");
        Assert.Matches(@"protected override bool ShowWithoutActivation => !TakesFocus;", flyout);
        Assert.Matches(@"if \(!TakesFocus\)\s*cp\.ExStyle \|= WS_EX_NOACTIVATE;", flyout);
    }

    [Fact]
    public void ADownloadOpensThePanelOnlyForThePersonInFront()
    {
        string form = Code("Gergur", "UI", "MainForm.cs");
        int at = form.IndexOf("private void ShowNewDownload()", StringComparison.Ordinal);
        Assert.True(at >= 0, "ShowNewDownload is gone");
        string body = form[at..form.IndexOf("private DownloadsFlyout DownloadsPanel(", at, StringComparison.Ordinal)];
        foreach (string condition in new[] { "OpenedByAgent", "IsPageFullScreen", "GetForegroundWindow() != Handle", "FormWindowState.Minimized" })
            Assert.Contains(condition, body);
        // And it is asked for after the engine's event, not inside it.
        Assert.Contains("BeginInvoke(ShowNewDownload);", form);
    }

    // ------------------------------------------------------------------ the main menu

    private static IReadOnlyList<MenuEntry> SampleMenu(List<string> ran) =>
    [
        new MenuAction("New tab", "Ctrl+T", () => ran.Add("new tab"), MenuGlyphs.NewTab),
        new MenuSeparator(),
        new MenuTiles(
        [
            new MenuTile(MenuGlyphs.History, "History", () => ran.Add("history")),
            new MenuTile(Glyphs.Download, "Downloads", () => ran.Add("downloads"), () => "2"),
            new MenuTile(MenuGlyphs.Bookmarks, "Bookmarks", () => ran.Add("bookmarks")),
        ]),
        new MenuZoom(() => 100, () => ran.Add("out"), () => ran.Add("in"), () => ran.Add("reset")),
        new MenuToggle("Ad blocking", () => "5 rules", () => true, () => ran.Add("toggle")),
        new MenuToggle("Mute tab", () => null, () => false, null),
        new MenuSubmenu("VPN", () => "off", () => new ToolStripMenuItem("VPN")),
        new MenuAction("Disabled", null, null),
        new MenuSeparator(),
        new MenuFooter(
        [
            new MenuAction("Settings", null, () => ran.Add("settings")),
            new MenuAction("DevTools", "F12", () => ran.Add("devtools")),
            new MenuSubmenu("More", () => null, () => new ToolStripMenuItem("More")),
        ]),
    ];

    [Fact]
    public void EveryPlaceOnTheMenuIsWhereItSaysAndNothingOverlaps()
    {
        OnSta(() =>
        {
            var ran = new List<string>();
            using var menu = new MainMenuFlyout();
            menu.LayOutForTest(SampleMenu(ran));

            // Two separators make no spots; every other line or button makes its own.
            Assert.Equal(1 + 3 + 3 + 1 + 1 + 1 + 1 + 3, menu.Spots.Count);
            AssertLaidOutInside(menu.Spots, menu.ClientSize);

            // Laid out top to bottom in the order given, with no gaps between lines.
            var laid = menu.Laid;
            for (int i = 1; i < laid.Count; i++)
                Assert.Equal(laid[i - 1].Bounds.Bottom, laid[i].Bounds.Top);
        });
    }

    [Fact]
    public void TheZoomStepperAndTogglesKeepTheMenuOpenAndCommandsDoNot()
    {
        OnSta(() =>
        {
            var ran = new List<string>();
            using var menu = new MainMenuFlyout();
            menu.LayOutForTest(SampleMenu(ran));
            var byName = menu.Spots.ToDictionary(s => s.Spoken);

            Assert.True(byName["Zoom out"].KeepOpen);
            Assert.True(byName["Reset zoom"].KeepOpen);
            Assert.True(byName["Zoom in"].KeepOpen);
            Assert.True(byName["Ad blocking"].KeepOpen);
            Assert.True(byName["VPN"].KeepOpen, "opening a side list closed the menu it hangs from");
            Assert.False(byName["New tab"].KeepOpen);
            Assert.False(byName["History"].KeepOpen);
            Assert.False(byName["Settings"].KeepOpen);

            // Something with nothing to do does nothing, and says so by being disabled.
            Assert.False(byName["Disabled"].Enabled);
            Assert.False(byName["Mute tab"].Enabled);

            // The stepper runs what it says, in place.
            menu.RunSpot(byName["Zoom in"]);
            menu.RunSpot(byName["Zoom out"]);
            menu.RunSpot(byName["Reset zoom"]);
            menu.RunSpot(byName["Ad blocking"]);
            Assert.Equal(["in", "out", "reset", "toggle"], ran);
        });
    }

    [Fact]
    public void TheKeyboardSkipsWhatDoesNothingAndWraps()
    {
        HotSpot Live(string name) => new(Rectangle.Empty, () => { }, name);
        HotSpot Dead(string name) => new(Rectangle.Empty, null, name);
        var spots = new[] { Dead("a"), Live("b"), Dead("c"), Live("d") };

        Assert.Equal(1, FlyoutPanel.NextFocus(spots, -1, +1));   // first live one
        Assert.Equal(3, FlyoutPanel.NextFocus(spots, 1, +1));    // over the dead one
        Assert.Equal(1, FlyoutPanel.NextFocus(spots, 3, +1));    // wraps past the end
        Assert.Equal(3, FlyoutPanel.NextFocus(spots, -1, -1));   // up from nowhere is the last
        Assert.Equal(3, FlyoutPanel.NextFocus(spots, 1, -1));    // wraps past the start
        Assert.Equal(-1, FlyoutPanel.NextFocus([Dead("x")], -1, +1));   // nothing to go to
    }

    [Fact]
    public void OpeningASideListByKeyboardLandsOnItsFirstUsableLine()
    {
        OnSta(() =>
        {
            using var list = new ToolStripDropDown();
            list.Items.Add(new ToolStripSeparator());
            list.Items.Add(new ToolStripMenuItem("Greyed") { Enabled = false });
            list.Items.Add(new ToolStripMenuItem("Hidden") { Available = false });
            var first = new ToolStripMenuItem("Sweden");
            list.Items.Add(first);
            list.Items.Add(new ToolStripMenuItem("Norway"));
            MainMenuFlyout.SelectFirst(list);
            Assert.True(first.Selected, "the highlight did not land on the first line that can be used");
        });

        // Opening a list by keyboard is never taken for the click that closes it.
        string menu = Code("Gergur", "UI", "MainMenuFlyout.cs");
        Assert.Matches(@"_closedName = null;\s*RunSpot\(spot\);", menu);
    }

    [Fact]
    public void ANewWindowOpensSteppedFromThisOneAndSaysSoWhenItCannot()
    {
        var screen = new Rectangle(0, 0, 1920, 1040);
        Assert.Equal(new Rectangle(132, 132, 1200, 800), MainForm.CascadedBounds(new Rectangle(100, 100, 1200, 800), 32, screen));
        // Off the bottom right: back to the top left of the screen, not off it.
        Assert.Equal(new Rectangle(0, 0, 1200, 800), MainForm.CascadedBounds(new Rectangle(700, 230, 1200, 800), 32, screen));
        // On a monitor to the left of the primary, whose coordinates are negative.
        var left = new Rectangle(-1920, 0, 1920, 1040);
        Assert.Equal(new Rectangle(-1900, 52, 1200, 800), MainForm.CascadedBounds(new Rectangle(-1932, 20, 1200, 800), 32, left));

        string form = Code("Gergur", "UI", "MainForm.cs");
        string body = form[form.IndexOf("public async Task NewWindowAsync()", StringComparison.Ordinal)..form.IndexOf("internal static Rectangle CascadedBounds(", StringComparison.Ordinal)];
        Assert.Contains("catch (Exception ex)", body);
        Assert.Contains("ShowMessage(\"Could not open a new window.\")", body);
    }

    [Fact]
    public void ASideListIsCutLooseFromItsItemBeforeItIsShown()
    {
        // Still owned by its item, WinForms places the list beside that item whatever point
        // it is given, and the item is on no strip: every side list came up at the top left
        // of the primary monitor. Measured on all three monitors by the hidden-desktop
        // harness; this holds the line that fixed it.
        string menu = Code("Gergur", "UI", "MainMenuFlyout.cs");
        int open = menu.IndexOf("private void OpenSubmenu(", StringComparison.Ordinal);
        string body = menu[open..menu.IndexOf("internal static Point SubmenuPlacement(", open, StringComparison.Ordinal)];
        int cut = body.IndexOf("drop.OwnerItem = null;", StringComparison.Ordinal);
        int show = body.IndexOf("drop.Show(", StringComparison.Ordinal);
        Assert.True(cut >= 0 && show > cut, "the list is shown while it still belongs to its item");
    }

    [Fact]
    public void ASideListOpensToTheLeftAndToTheRightOnlyWhenTheLeftHasNoRoom()
    {
        var screen = new Rectangle(0, 0, 1920, 1040);
        var panel = new Rectangle(1500, 80, 320, 600);
        var row = new Rectangle(1506, 400, 308, 32);

        var left = MainMenuFlyout.SubmenuPlacement(panel, row, new Size(240, 200), screen, 2);
        Assert.Equal(new Point(1500 - 240 + 2, 400), left);

        // A panel at the left edge of the screen has no room to its left.
        var atLeft = new Rectangle(0, 80, 320, 600);
        var right = MainMenuFlyout.SubmenuPlacement(atLeft, row with { X = 6 }, new Size(240, 200), screen, 2);
        Assert.Equal(320 - 2, right.X);

        // A list taller than the room under its line moves up to stay on the screen.
        var low = MainMenuFlyout.SubmenuPlacement(panel, row with { Y = 1000 }, new Size(240, 200), screen, 2);
        Assert.Equal(1040 - 200, low.Y);
    }

    [Fact]
    public void EveryCommandTheOldMenuHadIsStillOnTheNewOne()
    {
        // The old list was replaced wholesale, so this is the check that nothing fell out of
        // it on the way: each call the old list made, made somewhere in the new menu.
        string form = Code("Gergur", "UI", "MainForm.cs");
        int at = form.IndexOf("private IReadOnlyList<MenuEntry> MainMenuEntries()", StringComparison.Ordinal);
        Assert.True(at >= 0, "MainMenuEntries is gone");
        string menu = form[at..form.IndexOf("public async Task NewWindowAsync()", at, StringComparison.Ordinal)];
        foreach (string call in new[]
        {
            "NewTabAsync()", "ReopenClosedTabAsync()", "ShowHistoryPanel", "ShowDownloadsPanel", "OpenBookmarks",
            "ToggleBookmark", "ToggleBookmarksBar()", "NavigateActiveAsync(url)", "ZoomBy(-0.1)", "ZoomBy(0.1)",
            "ZoomReset", "OpenFind", "ToggleMuteActiveTab", "PrintActive", "SaveAsPdfAsync()",
            "blocker.Enabled = !blocker.Enabled", "UpdateBlocklistAsync(silent: false)", "BuildVpnMenu", "BuildPhoneMenu",
            "OpenTaskManager()", "OpenDevTools", "DumpMemoryCsv()", "OpenSettings", "Settings.DataDir",
        })
            Assert.Contains(call, menu);
        // History and downloads open as panels now; each panel's last line is the full page.
        Assert.Matches(@"new HistoryFlyout\(\s*\(\) => session\.History\.TryRead\(max: 200\),\s*url => \{ ClaimForPerson\(\); _ = NavigateActiveAsync\(url\); \},\s*OpenHistory\)", form);
        Assert.Contains("new DownloadsFlyout(session.Downloads, OpenDownloads, ShowMessage)", form);
        // And the old list is gone rather than left alongside it.
        Assert.DoesNotContain("ContextMenuStrip _menu", form);
        Assert.DoesNotContain("RebuildMenuItems", form);
    }

    [Fact]
    public void ASecondClickOnTheButtonClosesTheMenuRatherThanReopeningIt()
    {
        // The click that lands on the button has already taken the menu down (it lost focus,
        // or the panel saw the press), so a toggle that only looked at Visible reopened it.
        string form = Code("Gergur", "UI", "MainForm.cs");
        Assert.Matches(@"if \(_mainMenu\.JustDismissed\)\s*return;", form);
        Assert.Matches(@"if \(panel\.JustDismissed\)\s*return;", form);
    }

    private static void AssertLaidOutInside(IReadOnlyList<HotSpot> spots, Size size)
    {
        var area = new Rectangle(Point.Empty, size);
        for (int i = 0; i < spots.Count; i++)
        {
            Assert.True(area.Contains(spots[i].Bounds), $"'{spots[i].Spoken}' at {spots[i].Bounds} runs off a panel of {size}");
            for (int j = i + 1; j < spots.Count; j++)
                Assert.False(spots[i].Bounds.IntersectsWith(spots[j].Bounds),
                    $"'{spots[i].Spoken}' at {spots[i].Bounds} overlaps '{spots[j].Spoken}' at {spots[j].Bounds}");
        }
    }

    // ------------------------------------------------------------------ relaxed sites

    [Theory]
    [InlineData("youtube.com", "youtube.com")]
    [InlineData("  WWW.YouTube.com. ", "youtube.com")]
    [InlineData("music.youtube.com", "music.youtube.com")]
    [InlineData("*.youtube.com", "")]          // a wildcard is not one site
    [InlineData("localhost", "")]              // no dot: not a site name
    [InlineData("192.168.0.1", "")]            // an address, not a name
    [InlineData("you tube.com", "")]
    public void ARelaxedSiteIsAPlainNameCleaned(string entry, string expected)
        => Assert.Equal(expected, RelaxedSites.Clean(entry));

    [Fact]
    public void ARelaxedSiteCoversItselfAndWhatIsUnderItAndNothingElse()
    {
        var sites = RelaxedSites.Parse("youtube.com; example.org,,");
        Assert.Equal(new[] { "youtube.com", "example.org" }, sites);
        Assert.True(RelaxedSites.Covers(sites, "www.youtube.com"));
        Assert.True(RelaxedSites.Covers(sites, "music.youtube.com."));
        Assert.True(RelaxedSites.Covers(sites, "YOUTUBE.COM"));
        Assert.False(RelaxedSites.Covers(sites, "notyoutube.com"));
        Assert.False(RelaxedSites.Covers(sites, "youtube.com.evil.net"));
        Assert.False(RelaxedSites.Covers([], "youtube.com"));
    }

    [Fact]
    public void TheMenuSwitchAddsAndTakesOutTheSiteInFront()
    {
        Assert.Equal("youtube.com", RelaxedSites.SiteOf("https://www.youtube.com/watch?v=a"));
        Assert.Equal("", RelaxedSites.SiteOf("file:///C:/x.html"));
        Assert.Equal("", RelaxedSites.SiteOf(null));
        string on = RelaxedSites.With("example.org", "youtube.com", relaxed: true);
        Assert.Equal("example.org;youtube.com", on);
        Assert.Equal("example.org;youtube.com", RelaxedSites.With(on, "youtube.com", relaxed: true));   // once only
        Assert.Equal("example.org", RelaxedSites.With(on, "www.youtube.com", relaxed: false));
    }

    [Fact]
    public void TheRelaxedSitesAreWrittenIntoTheScriptAndNowhereAPageCanSee()
    {
        string script = "(function () {\n    var RELAXED_SITES = [];\n    run();\n})();";
        Assert.Equal(script, PageCleanup.Compose(script, []));
        string composed = PageCleanup.Compose(script, ["youtube.com", "example.org"]);
        Assert.Contains("var RELAXED_SITES = [\"youtube.com\",\"example.org\"];", composed);
        Assert.DoesNotContain("var RELAXED_SITES = [];", composed);
        // An older script without the line relaxes nothing rather than breaking.
        Assert.Equal("run();", PageCleanup.Compose("run();", ["youtube.com"]));
        // And the shipped script has the line, inside its closure, exactly once.
        string shipped = File.ReadAllText(OwnPagesHardeningTests.FindSource("Gergur", "Assets", "adblock.js"));
        int at = shipped.IndexOf(PageCleanup.RelaxedPlaceholder, StringComparison.Ordinal);
        Assert.True(at > shipped.IndexOf("(function () {", StringComparison.Ordinal), "the placeholder is outside the closure");
        Assert.Equal(at, shipped.LastIndexOf(PageCleanup.RelaxedPlaceholder, StringComparison.Ordinal));
    }

    [Fact]
    public void ARequestFromAPageOnARelaxedSiteIsLetThrough()
    {
        string[] relaxed = ["youtube.com"];
        Assert.True(RequestBlocker.IsRelaxedPage(relaxed, "https://www.youtube.com/watch?v=a"));
        Assert.False(RequestBlocker.IsRelaxedPage(relaxed, "https://example.com/"));
        Assert.False(RequestBlocker.IsRelaxedPage(relaxed, null));
        Assert.False(RequestBlocker.IsRelaxedPage([], "https://www.youtube.com/"));
        // Only a request that would be blocked asks the engine for the page address.
        string code = Code("Gergur", "Blocking", "RequestBlocker.cs");
        Assert.Matches(@"\(_list\.IsBlocked\(uri\.Host\) \|\| IsAdPath\(uri\)\)\s*&& !OnRelaxedPage\(", code);
    }

    [Fact]
    public void ChangingTheRelaxedSitesSwapsTheScriptAndReloadsThePageInFront()
    {
        string tab = Code("Gergur", "Tabs", "Tab.cs");
        Assert.Matches(@"if \(registered && wanted != _pageCleanupScript!\.Value\.Text\)", tab);
        string form = Code("Gergur", "UI", "MainForm.cs");
        Assert.Contains("session.Blocker.Relaxed = Blocking.RelaxedSites.Parse(session.Settings.RelaxedSites);", form);
        Assert.Matches(@"RelaxedSites\.SiteOf\(tab\.Url\) == site\)\s*tab\.Reload\(\);", form);
    }

    // ------------------------------------------------------------------ the cleanup script and the agent mute

    [Fact]
    public void TheCleanupScriptIsReadAgainWhenItsFileChanges()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gergur-tests", "cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "adblock.js");
        try
        {
            var none = PageCleanup.Read(path, default);
            Assert.Null(none.Text);

            File.WriteAllText(path, "first();");
            var first = PageCleanup.Read(path, none);
            Assert.Equal("first();", first.Text);
            Assert.Equal(first, PageCleanup.Read(path, first));   // unchanged: the same snapshot back

            File.WriteAllText(path, "second(); longer");
            File.SetLastWriteTimeUtc(path, first.StampUtc.AddSeconds(5));
            Assert.Equal("second(); longer", PageCleanup.Read(path, first).Text);

            // Same stamp, different length: two saves inside the timestamp's resolution.
            var stamped = PageCleanup.Read(path, first);
            File.WriteAllText(path, "third");
            File.SetLastWriteTimeUtc(path, stamped.StampUtc);
            Assert.Equal("third", PageCleanup.Read(path, stamped).Text);

            // Gone: the last good script stays rather than the cleanup vanishing.
            var last = PageCleanup.Read(path, stamped);
            File.Delete(path);
            Assert.Equal("third", PageCleanup.Read(path, last).Text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AnAgentCanOpenATabOrWindowMuted()
    {
        // So testing a video in an agent's window does not play it out loud at whoever is
        // sitting at the computer, which until now was the only way to test one.
        var tools = AgentServer.McpTools();
        string json = System.Text.Json.JsonSerializer.Serialize(tools);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (string name in new[] { "gergur_open_tab", "gergur_open_window" })
        {
            var tool = doc.RootElement.EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);
            Assert.True(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("muted", out _),
                $"{name} has no muted argument");
        }

        string server = Code("Gergur", "App", "AgentServer.cs");
        Assert.Matches(@"bool muted = BoolFrom\(""muted""\) \?\? false;", server);
        Assert.Matches(@"activate: true, muted\)\)", server);
        Assert.Matches(@"activate: !background, muted: mutedTab\)\)", server);

        // Muted before the view exists, and every view the tab builds starts muted.
        string manager = Code("Gergur", "Tabs", "TabManager.cs");
        Assert.Matches(@"if \(muted\)\s*tab\.IsMuted = true;\s*bool kept", manager.Replace("\r", ""));
        string tab = Code("Gergur", "Tabs", "Tab.cs");
        Assert.Matches(@"var core = webView\.CoreWebView2;\s*if \(_muted\)\s*core\.IsMuted = true;", tab);
    }
}
