using System.Drawing;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Signing in to a form inside one of the page's frames (gergur_login), and "Agent watching"
/// over the person's window while an agent reads or acts on it. The engine's part (it never
/// fills another site's frame by itself, its list once the field is clicked) and the real
/// windows were measured live and on a hidden desktop; these hold what Gergur decides.
/// </summary>
public sealed class FrameLoginAndWatchingTests
{
    private static string Code(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gergur.slnx")))
            dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("Gergur.slnx not found above the test binaries");
        return Regex.Replace(File.ReadAllText(Path.Combine(root, "src", "Gergur", relative)), @"//[^\n]*", "");
    }

    private static string FrameLogin()
        => Regex.Match(Code("App/AgentServer.cs"), @"async Task<\(int, string, byte\[\]\)\?> FrameLoginAsync\([\s\S]*?\r?\n        \}\r?\n").Value;

    // ------------------------------------------------------------------ reading a frame

    [Fact]
    public void AFramesAnswerSaysItsFormAndWhereItIs()
    {
        var read = AgentServer.ParseFrameLogin("""{"state":{"fields":1,"filled":false,"user":null,"button":true,"userField":true},"href":"https://login.example.net/form#x","origin":"https://login.example.net","vw":420,"vh":300}""")!;
        Assert.Equal((1, false, true, true), (read.State.Fields, read.State.Filled, read.State.Button, read.State.UserField));
        Assert.Equal(("https://login.example.net/form#x", "https://login.example.net", 420d, 300d), (read.Href, read.Origin, read.ViewW, read.ViewH));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"state":{"fields":1},"href":"x"}""")]
    [InlineData("""{"state":{"fields":"1"},"href":"x","origin":"y"}""")]
    [InlineData("""{"state":null,"href":"x","origin":"y"}""")]
    [InlineData("not json")]
    public void AnAnswerThatIsNotOneIsNothing(string raw) => Assert.Null(AgentServer.ParseFrameLogin(raw));

    [Fact]
    public void TheFrameScriptIsThePageScriptWithWhereItIs()
    {
        string js = AgentServer.FrameLoginStateJs("abc123");
        Assert.Contains(AgentServer.LoginStateJs("abc123").Trim(), js);
        Assert.Contains("href: location.href, origin: location.origin, vw: innerWidth, vh: innerHeight", js);
    }

    // ------------------------------------------------------------------ placing a click in a frame

    private static AgentServer.Located Spot(double x, double y, bool inView = true, bool covered = false)
        => new(x, y, 40, 20, "button", "submit", false, inView, covered, null);

    private static readonly AgentServer.FrameBox Box = new(100, 200, 400, 300, 1200, 800, null);

    [Fact]
    public void ThePointIsTheFramesPlacePlusTheElementsInIt()
        => Assert.Equal((150d, 260d), AgentServer.PointInFrame(Box, Spot(50, 60)));

    [Theory]
    [InlineData(50, 60, false, false)]    // out of the frame's own view
    [InlineData(50, 60, true, true)]      // covered inside the frame
    [InlineData(-1, 60, true, false)]     // left of the frame's box
    [InlineData(400, 60, true, false)]    // right of it
    [InlineData(50, 300, true, false)]    // below it
    public void AnElementNotThereToClickIsNot(double x, double y, bool inView, bool covered)
        => Assert.Null(AgentServer.PointInFrame(Box, Spot(x, y, inView, covered)));

    [Fact]
    public void APointOutsideThePagesViewIsNot()
    {
        Assert.Null(AgentServer.PointInFrame(Box with { Y = 700 }, Spot(50, 150)));        // 850 below an 800 view
        Assert.Null(AgentServer.PointInFrame(Box with { X = double.NaN }, Spot(50, 60)));
    }

    [Fact]
    public void TheFramesBoxIsTakenOnlyWhenExactlyOneFrameIsOnItsSite()
    {
        Assert.Equal(new AgentServer.FrameBox(1, 2, 3, 4, 5, 6, true), AgentServer.ParseFrameBox("""{"count":1,"x":1,"y":2,"w":3,"h":4,"vw":5,"vh":6,"hit":true}"""));
        Assert.Null(AgentServer.ParseFrameBox("""{"count":2}"""));
        Assert.Null(AgentServer.ParseFrameBox("""{"count":0}"""));
        Assert.Null(AgentServer.ParseFrameBox("null"));
        Assert.Null(AgentServer.ParseFrameBox("""{"count":1,"x":1,"y":2,"w":3,"h":4,"vw":5,"vh":6,"hit":null}""")!.Hit);
    }

    [Fact]
    public void TheFrameElementIsTheFrameReadOnlyAtItsSize()
    {
        var read = new AgentServer.FrameLogin(new AgentServer.LoginState(1, false, null, true), "https://a.example/", "https://a.example", 420, 300);
        Assert.True(AgentServer.SameFrame(Box with { W = 421, H = 299 }, read));
        Assert.False(AgentServer.SameFrame(Box with { W = 600, H = 300 }, read));
        Assert.False(AgentServer.SameFrame(Box with { W = 420, H = 300 }, read with { ViewW = double.NaN }));
    }

    [Fact]
    public void ThePageIsAskedForTheFrameByItsSiteWithTheOriginAsALiteral()
    {
        string js = AgentServer.FrameBoxJs("https://a.example'); alert(1); ('", (12.5, 30));
        Assert.Contains("const want = \"https://a.example\\u0027); alert(1); (\\u0027\";", js);
        Assert.Contains("const asked = [12.5, 30];", js);
        Assert.Contains("if (all.length !== 1) return { count: all.length };", js);
        Assert.Contains("const asked = null;", AgentServer.FrameBoxJs("https://a.example", null));
    }

    [Fact]
    public void AFrameDrawnScaledOrTurnedIsNeverPlaced()
    {
        // Its drawn box and laid-out box differ, so a point in it is not the same point on the page.
        string js = AgentServer.FrameBoxJs("https://a.example", null);
        Assert.Contains("if (Math.abs(r.width - f.offsetWidth) > 1 || Math.abs(r.height - f.offsetHeight) > 1) return { count: -1 };", js);
        Assert.True(js.IndexOf("return { count: -1 }", StringComparison.Ordinal) < js.IndexOf("return { count: 1,", StringComparison.Ordinal));
        Assert.Null(AgentServer.ParseFrameBox("""{"count":-1}"""));
    }

    [Fact]
    public void WhereTheKeysGoIsAskedOfThePageAndTheFrameWithLiterals()
    {
        string page = AgentServer.FrameFocusedJs("https://a.example'); alert(1); ('");
        Assert.Contains("=== \"https://a.example\\u0027); alert(1); (\\u0027\"", page);
        Assert.Contains("a.tagName !== 'IFRAME' && a.tagName !== 'FRAME'", page);
        string frame = AgentServer.FocusedJs("[data-gergur-login=\"pass-ab\"]");
        Assert.Contains("a.matches(\"[data-gergur-login=\\u0022pass-ab\\u0022]\")", frame);
        // Not the window's focus, which an agent's hidden window never has.
        Assert.DoesNotContain("hasFocus", frame);
    }

    [Fact]
    public void EnterGoesOnlyWhileTheFieldClickedStillHasTheKeyboard()
    {
        string frame = FrameLogin();
        var still = Regex.Match(frame, @"async Task<FrameClick> KeyboardStillThere\(\)[\s\S]*?\r?\n                    \}").Value;
        Assert.NotEmpty(still);
        Assert.Contains("FrameFocusedJs(read.Origin)", still);
        Assert.Contains("FocusedJs($\"[data-gergur-login=\\\"pass-{nonce}\\\"]\")", still);
        Assert.Matches(@"if \(tab\.AgentScriptedThisDocument\)\s*return FrameClick\.Scripted;\s*if \(pageRaw != ""true"" \|\| focusRaw != ""true"" \|\| tab\.CommittedSourceForOtherThreads != url \|\| Tab\.FrameGone\(frame\)\)\s*return FrameClick\.MovedOn;", still);
        // Looked at before each key, in one go on the UI thread, and a refusal is answered.
        Assert.Matches(@"keys = await OnUiAsync\(async \(\) =>\s*\{\s*var look = await KeyboardStillThere\(\);\s*if \(look != FrameClick\.Clicked\)\s*return look;\s*await TabInput\.PressAsync\(tab, TrustedInput\.ParseChord\(""Escape""\)!\);\s*look = await KeyboardStillThere\(\);\s*if \(look != FrameClick\.Clicked\)\s*return look;\s*await TabInput\.PressAsync\(tab, TrustedInput\.ParseChord\(""Enter""\)!\);", frame);
        Assert.Matches(@"if \(keys != FrameClick\.Clicked\)\s*return Refusal\(keys\);", frame);
        Assert.Single(Regex.Matches(frame, @"ParseChord\(""Enter""\)"));
    }

    // ------------------------------------------------------------------ the wiring

    [Fact]
    public void FramesAreTriedOnlyWhenThePageItselfHasNoPassword()
    {
        string server = Code("App/AgentServer.cs");
        Assert.Matches(@"var state = ParseLoginState\(raw\);\s*if \(state is \{ Fields: 0 \} && await FrameLoginAsync\(entry\.Window, tab, nonce, asked\) is \{ \} framed\)\s*return framed;", server);
        string frame = FrameLogin();
        Assert.NotEmpty(frame);
        // One frame, or none: two asking is refused.
        Assert.Matches(@"if \(asking\.Count > 1\)\s*return \(409[^;]*SeveralFramesAsk", frame);
    }

    [Fact]
    public void AnAccountIsChosenInAFrameOnlyInTheAgentsOwnWindowAndNeverLeftWrong()
    {
        string frame = FrameLogin();
        Assert.Matches(@"if \(tab\.AgentScriptedThisDocument\)\s*return \(409[^;]*;\s*if \(!window\.OpenedByAgent\)\s*return \(409[^;]*PickNeedsOwnWindow", frame);
        // One pick at a time, always given back, and another account's password emptied.
        Assert.Contains("AccountPick.Turn.WaitAsync", frame);
        Assert.Matches(@"finally\s*\{\s*AccountPick\.Turn\.Release\(\);\s*if \(!keep\)[\s\S]*?EmptyPasswordJs\(nonce\)", frame);
        // Taken only when the frame now holds the account asked for (or any, with none asked).
        Assert.Contains("ParseLoginState(afterRaw) is { Filled: true } after && (asked is null || after.User == asked)", frame);
        // The guard and the list go into the frame's own document.
        Assert.Contains("AccountPick.RunAsync(window, tab, point.X, point.Y, asked ?? \"\", Chosen, InFrame)", frame);
    }

    [Fact]
    public void EveryClickIntoAFrameIsLookedAtLastOnTheUiThread()
    {
        string frame = FrameLogin();
        var last = Regex.Match(frame, @"async Task<FrameClick> LastLookAsync\([\s\S]*?\r?\n            \}").Value;
        Assert.Matches(@"is not \{ Hit: true \}\)\s*return FrameClick\.NotReachable;\s*if \(tab\.AgentScriptedThisDocument\)\s*return FrameClick\.Scripted;\s*if \(tab\.CommittedSourceForOtherThreads != url \|\| Tab\.FrameGone\(frame\)\)\s*return FrameClick\.MovedOn;", last);
        Assert.Matches(@"var look = await LastLookAsync\(point\);\s*if \(look != FrameClick\.Clicked\)\s*return look;\s*await TabInput\.ClickAsync\(tab, point\.X, point\.Y\);", frame);
        Assert.Matches(@"var look = await LastLookAsync\(point\);\s*if \(look != FrameClick\.Clicked\)\s*return \(Look: look, Pick: AccountPick\.Outcome\.NoList\);", frame);
        // Nothing in a frame is read for its password: only whether it holds one.
        Assert.DoesNotContain(".value", frame);
    }

    [Fact]
    public void ThePickTypesNothingForTheListsFirstAndGuardsTheDocumentItIsIn()
    {
        string pick = Code("Tabs/AccountPick.cs");
        Assert.Matches(@"if \(account\.Length > 0\)\s*await TabInput\.InsertTextAsync\(tab, account\);", pick);
        Assert.Contains("inDocument ??= tab.ReadScriptAsync;", pick);
        Assert.Contains("await inDocument(GuardJs, TimeSpan.FromSeconds(2));", pick);
        Assert.Contains("await inDocument(UnguardJs, TimeSpan.FromSeconds(2));", pick);
        Assert.DoesNotContain("tab.ReadScriptAsync(GuardJs", pick);
    }

    [Fact]
    public void FramesAreTheDocumentsOwnAndGoWithTheView()
    {
        string tab = Code("Tabs/Tab.cs");
        Assert.Contains("core.FrameCreated += OnFrameCreated;", tab);
        Assert.Contains("frame.Destroyed += (_, _) => _frames.Remove(frame);", tab);
        Assert.Matches(@"Interlocked\.Exchange\(ref _fedCm, null\)\?\.Dispose\(\);\s*_frames\.Clear\(\);", tab);
        Assert.Contains(".Take(20)", tab);
    }

    // ------------------------------------------------------------------ Agent watching

    [Theory]
    [InlineData("Claude Code", true, "Agent watching · Claude Code")]
    [InlineData("Claude Code", false, "Agent watching another tab · Claude Code")]
    [InlineData("  ", true, "Agent watching · an agent")]
    public void ThePillSaysWhoAndWhere(string agent, bool shown, string label) => Assert.Equal(label, AgentWatchGlow.Label(agent, shown));

    [Theory]
    [InlineData(false, false, true, false, true)]
    [InlineData(true, false, true, false, false)]    // an agent's window: its band says it
    [InlineData(false, true, true, false, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, true, true, false)]    // minimised: nothing to show it over
    public void OnlyThePersonsWindowOnScreenShowsIt(bool openedByAgent, bool hidden, bool visible, bool minimized, bool shows)
        => Assert.Equal(shows, MainForm.ShowsWatchGlow(openedByAgent, hidden, visible, minimized));

    [Fact]
    public void ThePictureIsAnEdgeAndAPillAndLeavesThePageAlone()
    {
        using var art = AgentWatchGlow.Render(new Size(900, 520), AgentWatchGlow.Label("Claude Code", true), 96);
        Assert.True(art.GetPixel(0, 260).A > 150);
        Assert.Equal(0, art.GetPixel(20, 260).A);
        Assert.Equal(0, art.GetPixel(450, 300).A);
        Assert.True(art.GetPixel(450, 23).A > 200);
        using var tiny = AgentWatchGlow.Render(new Size(30, 20), "x", 192);
        Assert.Equal(new Size(30, 20), tiny.Size);
    }

    [Fact]
    public void EveryTouchOfAPersonsTabSaysSoAndNoneOfAnAgentsWindow()
    {
        string server = Code("App/AgentServer.cs");
        Assert.Matches(@"var \(window, tab\) = all\[at\];\s*if \(!window\.OpenedByAgent\)\s*window\.AgentLookedIn\(tab, CurrentRequest\.Value\?\.Caller\?\.Name \?\? ""An agent""\);", server);
        Assert.Single(Regex.Matches(server, @"AgentLookedIn\("));
        string form = Code("UI/MainForm.cs");
        Assert.Contains("BeginInvoke(() => ShowWatchGlow(tab, agent, DateTime.UtcNow));", form);
        Assert.Contains("_watchGlow?.Dispose();", form);
    }

    [Fact]
    public void TheGlowTakesNoInputAndIsNeverActivated()
    {
        string glow = Code("UI/AgentWatchGlow.cs");
        Assert.Contains("cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;", glow);
        Assert.Contains("protected override bool ShowWithoutActivation => true;", glow);
        Assert.Matches(@"if \(m\.Msg == WM_NCHITTEST\)\s*\{\s*m\.Result = HTTRANSPARENT;", glow);
        Assert.Matches(@"if \(m\.Msg == WM_MOUSEACTIVATE\)\s*\{\s*m\.Result = MA_NOACTIVATE;", glow);
        Assert.DoesNotContain("Activate()", glow);
        Assert.DoesNotContain("Focus()", glow);
    }

    [Fact]
    public void ItIsHeldThenFadesAndGoes()
    {
        var until = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(255, AgentWatchGlow.Faded(255, until, until - TimeSpan.FromMilliseconds(1)));
        byte alpha = 255;
        int ticks = 0;
        while (alpha > 0 && ticks < 50)
            alpha = AgentWatchGlow.Faded(alpha, until, until + TimeSpan.FromMilliseconds(50 * ++ticks));
        Assert.Equal(0, alpha);
        Assert.InRange(ticks, 5, 8);   // about 0.3 s at 50 ms a tick
        Assert.Equal(0, AgentWatchGlow.Faded(10, until, until));
    }

    [Fact]
    public void ATouchWhileItIsUpOnlyHoldsItLonger()
    {
        // Every request of a polling agent comes here; the picture is pushed only when something changed.
        string glow = Code("UI/AgentWatchGlow.cs");
        Assert.Matches(@"_untilUtc = nowUtc \+ Hold;\s*if \(Visible && _alpha == 255 && page == _page && label == _label && _art is not null\)\s*return;", glow);
        Assert.Matches(@"HideNow\(\);\s*return;\s*\}\s*if \(page == _page\)\s*return;", glow);
        // And it follows the page when only the page moves.
        string form = Code("UI/MainForm.cs");
        Assert.Contains("_hostPanel.SizeChanged += (_, _) => FollowWatchGlow();", form);
        Assert.Contains("_hostPanel.LocationChanged += (_, _) => FollowWatchGlow();", form);
    }

    [Fact]
    public void AgentsAreToldAboutSignInWithGoogleWhenTheyConnect()
        => Assert.Contains("For Sign in with Google, click the site's Google button and call gergur_login", AgentServer.McpInstructions);
}
