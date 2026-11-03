using System.Text.RegularExpressions;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// A page can ask for the whole window, and the window used to give it without keeping a way
/// back: the tab strip and toolbar are hidden, Esc only works if the page has the keyboard,
/// and the "left fullscreen" news was thrown away when the page was not the tab in front.
/// Reproduced on 2026-11-02 in a window of an agent's own: one agent click on a page's
/// fullscreen button turned it borderless and maximised across the whole monitor, strip and
/// toolbar gone, in a window the person had not touched.
///
/// The decision is taken once per request and kept, so these run it through whole histories
/// and not single moments: the first review of this found a refusal that lapsed ten seconds
/// later, and the window then took the screen anyway.
/// </summary>
public sealed class PageFullScreenTests
{
    private static string Code(params string[] parts)
        => Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource(parts)), @"//[^\n]*", "");

    private static FullScreenVerdict Judge(
        FullScreenVerdict current = FullScreenVerdict.None, bool page = true, bool agentWindow = false,
        bool agentRecently = false, bool active = true)
        => FullScreenPolicy.Judge(current, page, agentWindow, agentRecently, active);

    // ------------------------------------------------------------------ the first look at a request

    [Fact]
    public void ThePersonsOwnVideoInTheirOwnActiveWindowIsAllowedAndFillsTheScreen()
    {
        var verdict = Judge();

        Assert.Equal(FullScreenVerdict.Allowed, verdict);
        Assert.True(FullScreenPolicy.Fills(verdict, pageIsFullScreen: true));
    }

    [Fact]
    public void AnAgentsWindowIsRefused()
    {
        Assert.Equal(FullScreenVerdict.Refused, Judge(agentWindow: true));
    }

    [Fact]
    public void AnAgentsClickInThePageDoesNotTakeTheScreenOfSomeoneWhoDidNotAskForIt()
    {
        Assert.Equal(FullScreenVerdict.Refused, Judge(agentRecently: true));
    }

    [Fact]
    public void AWindowThatIsNotBeingUsedIsRefused()
    {
        Assert.Equal(FullScreenVerdict.Refused, Judge(active: false));
    }

    [Theory]
    [InlineData(FullScreenVerdict.None)]
    [InlineData(FullScreenVerdict.Allowed)]
    [InlineData(FullScreenVerdict.Refused)]
    [InlineData(FullScreenVerdict.Dismissed)]
    public void APageThatIsNotFullScreenHasNoVerdictWhateverItHadBefore(FullScreenVerdict before)
    {
        var verdict = Judge(before, page: false);

        Assert.Equal(FullScreenVerdict.None, verdict);
        Assert.False(FullScreenPolicy.Fills(verdict, pageIsFullScreen: false));
    }

    [Theory]
    [InlineData(FullScreenVerdict.None, false)]
    [InlineData(FullScreenVerdict.Allowed, true)]
    [InlineData(FullScreenVerdict.Refused, false)]
    [InlineData(FullScreenVerdict.Dismissed, false)]
    public void OnlyAnAllowedRequestForAPageThatIsStillFullScreenFills(FullScreenVerdict verdict, bool fills)
    {
        Assert.Equal(fills, FullScreenPolicy.Fills(verdict, pageIsFullScreen: true));
        Assert.False(FullScreenPolicy.Fills(verdict, pageIsFullScreen: false));
    }

    // ------------------------------------------------------------------ a verdict, once taken, stays

    [Fact]
    public void ARefusalBecauseAnAgentJustClickedIsNotReconsideredWhenTheTenSecondsPass()
    {
        // The case the first review found. The page goes fullscreen on an agent's click, so it
        // is refused; the window redraws for a title change ten seconds on, the agent is long
        // quiet by then, and the window used to take the whole monitor there and then.
        var verdict = Judge(agentRecently: true);
        Assert.Equal(FullScreenVerdict.Refused, verdict);

        verdict = Judge(verdict, agentRecently: false);
        Assert.Equal(FullScreenVerdict.Refused, verdict);
        Assert.False(FullScreenPolicy.Fills(verdict, pageIsFullScreen: true));
    }

    [Fact]
    public void ARefusalBecauseTheWindowWasInactiveIsNotReconsideredWhenItIsActivated()
    {
        var verdict = Judge(active: false);

        verdict = Judge(verdict, active: true);

        Assert.Equal(FullScreenVerdict.Refused, verdict);
    }

    [Fact]
    public void AnAgentsWindowThatTheyThenUseKeepsRefusingThatRequest()
    {
        var verdict = Judge(agentWindow: true);

        verdict = Judge(verdict, agentWindow: false);

        Assert.Equal(FullScreenVerdict.Refused, verdict);
    }

    [Fact]
    public void AFilmThePersonStartedStaysFullScreenWhenFocusMovesAwayOrAnAgentActs()
    {
        // Alt-tabbing out of a film must not shrink it, and an agent running something in the
        // page afterwards does not end it either: the person started it.
        var verdict = Judge();

        verdict = Judge(verdict, active: false);
        Assert.True(FullScreenPolicy.Fills(verdict, pageIsFullScreen: true));

        verdict = Judge(verdict, active: false, agentRecently: true);
        Assert.True(FullScreenPolicy.Fills(verdict, pageIsFullScreen: true));
    }

    [Fact]
    public void WhenThePersonEndsItThePageCannotGetTheWindowBackByStayingFullScreen()
    {
        // Esc: the window comes back at once, and the page is asked to leave. A page that
        // ignores the request stays fullscreen for as long as it likes, and it is the page's
        // word against the person's. The window believes the person.
        var verdict = Judge();
        Assert.Equal(FullScreenVerdict.Allowed, verdict);
        verdict = FullScreenVerdict.Dismissed;   // what ExitPageFullScreen records

        for (int redraws = 0; redraws < 5; redraws++)
        {
            verdict = Judge(verdict);
            Assert.Equal(FullScreenVerdict.Dismissed, verdict);
            Assert.False(FullScreenPolicy.Fills(verdict, pageIsFullScreen: true));
        }
    }

    [Fact]
    public void ThePageFinallyLeavingEndsADismissalSoTheNextRequestIsJudgedAfresh()
    {
        var verdict = FullScreenVerdict.Dismissed;

        verdict = Judge(verdict, page: false);
        Assert.Equal(FullScreenVerdict.None, verdict);

        verdict = Judge(verdict);
        Assert.Equal(FullScreenVerdict.Allowed, verdict);
    }

    [Fact]
    public void ARefusedPageThatLeavesAndAsksAgainIsJudgedAgainOnTheNewRequest()
    {
        var verdict = Judge(agentRecently: true);
        verdict = Judge(verdict, page: false);

        // A minute later the person clicks the button themselves.
        verdict = Judge(verdict, agentRecently: false);

        Assert.Equal(FullScreenVerdict.Allowed, verdict);
    }

    [Fact]
    public void ARefusalInThePersonsWindowIsSaidOnceAndNeverInAnAgentsWindow()
    {
        Assert.True(FullScreenPolicy.ShouldSayRefused(FullScreenVerdict.None, FullScreenVerdict.Refused, openedByAgent: false));
        // Not again on every redraw while it stands.
        Assert.False(FullScreenPolicy.ShouldSayRefused(FullScreenVerdict.Refused, FullScreenVerdict.Refused, openedByAgent: false));
        // Nobody is watching an agent's window.
        Assert.False(FullScreenPolicy.ShouldSayRefused(FullScreenVerdict.None, FullScreenVerdict.Refused, openedByAgent: true));
        // Nothing to say about anything but a refusal.
        Assert.False(FullScreenPolicy.ShouldSayRefused(FullScreenVerdict.None, FullScreenVerdict.Allowed, openedByAgent: false));
        Assert.False(FullScreenPolicy.ShouldSayRefused(FullScreenVerdict.Allowed, FullScreenVerdict.Dismissed, openedByAgent: false));
    }

    // ------------------------------------------------------------------ remembering an agent acted

    [Fact]
    public void AnAgentActionIsRememberedForAWhileAndThenForgotten()
    {
        var tab = (Tab)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Tab));

        Assert.False(tab.AgentActedWithin(FullScreenPolicy.AgentQuiet), "nothing has acted yet");
        tab.NoteAgentAction();
        Assert.True(tab.AgentActedWithin(FullScreenPolicy.AgentQuiet));
        Assert.False(tab.AgentActedWithin(TimeSpan.Zero), "a span of nothing has always passed");
    }

    [Fact]
    public void TheAgentQuietPeriodCoversHowLongAClickKeepsAPagesPermissionToGoFullScreen()
    {
        // The engine lets a page act on a click for about five seconds. Less than that and an
        // agent's click could still be what a page is running on when the veto has lapsed.
        Assert.InRange(FullScreenPolicy.AgentQuiet, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(60));
    }

    // ------------------------------------------------------------------ wiring

    [Fact]
    public void EveryTabIsJudgedWhenItsPageAsksAndEveryChangeOfFrontTabChecksTheWindowAgain()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");

        // Told by any tab, not only the one in front, and judged there and then.
        Assert.Matches(@"tab\.FullScreenChanged \+= \(_, _\) =>\s*\{\s*JudgeFullScreen\(tab\);\s*SyncPageFullScreen\(\);\s*\};", form);
        // And asked again whenever the chrome is redrawn.
        int chrome = form.IndexOf("private void UpdateChrome(", StringComparison.Ordinal);
        Assert.True(chrome >= 0);
        Assert.Contains("SyncPageFullScreen();", form[chrome..Math.Min(form.Length, chrome + 3200)]);
        // The old rule: act on the event only when the tab is in front, which dropped a page
        // leaving fullscreen from behind and left the window without its strip.
        Assert.DoesNotMatch(@"FullScreenChanged \+= \(_, on\)", form);
    }

    [Fact]
    public void TheMomentsThatDecideAreReadOnlyWhereTheVerdictIsTaken()
    {
        // Whether an agent has just acted, and whether this is the window in use, are facts
        // about one moment. Read anywhere but at the judgement they would be read again on every
        // redraw, and a refusal would last only as long as the reason for it.
        string form = Code("src", "Gergur", "UI", "MainForm.cs");

        Assert.Single(Regex.Matches(form, @"\.AgentActedWithin\("));
        int judge = form.IndexOf("private void JudgeFullScreen(Tab tab)", StringComparison.Ordinal);
        Assert.True(judge >= 0);
        Assert.Contains(".AgentActedWithin(", form[judge..Math.Min(form.Length, judge + 500)]);

        int sync = form.IndexOf("private void SyncPageFullScreen()", StringComparison.Ordinal);
        Assert.True(sync >= 0);
        string syncBody = form[sync..Math.Min(form.Length, sync + 600)];
        Assert.DoesNotContain("AgentActedWithin", syncBody);
        Assert.DoesNotContain("ActiveForm", syncBody);
        Assert.DoesNotContain("OpenedByAgent", syncBody);
    }

    [Fact]
    public void ThePersonEndingItIsHeldOnTheTabUntilThePageHasLeft()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");

        int exit = form.IndexOf("internal void ExitPageFullScreen()", StringComparison.Ordinal);
        Assert.True(exit >= 0);
        string body = form[exit..Math.Min(form.Length, exit + 900)];
        Assert.Contains("tab.FullScreenVerdict = FullScreenVerdict.Dismissed;", body);
        Assert.Contains("tab.ExitFullScreen();", body);
        Assert.True(body.IndexOf("Dismissed", StringComparison.Ordinal) < body.IndexOf("SyncPageFullScreen();", StringComparison.Ordinal),
            "the dismissal has to be recorded before the window is brought back");

        // On the tab, so that it goes with the tab to another window and is gone with it.
        string tabCode = Code("src", "Gergur", "Tabs", "Tab.cs");
        Assert.Contains("internal FullScreenVerdict FullScreenVerdict { get; set; }", tabCode);
    }

    [Fact]
    public void EscAndF11LeaveItFromWhereverTheKeyboardIsAndOnlyThen()
    {
        string router = Code("src", "Gergur", "UI", "ShortcutRouter.cs");

        Assert.Contains("Keys.Escape or Keys.F11 when _form.IsPageFullScreen", router);
        Assert.Contains("_form.ExitPageFullScreen", router);
    }

    [Fact]
    public void AnAgentRunningScriptInAPageIsRecordedBeforeItRuns()
    {
        string agent = Code("src", "Gergur", "App", "AgentServer.cs");

        int eval = agent.IndexOf("case (\"POST\", \"/eval\")", StringComparison.Ordinal);
        Assert.True(eval >= 0);
        string evalBody = agent[eval..Math.Min(agent.Length, eval + 1800)];
        // Acting(tab) records it (and who): pinned to call NoteAgentAction first in AgentToolsTests.
        Assert.Contains("Acting(tab);", evalBody);
        Assert.True(evalBody.IndexOf("Acting(tab);", StringComparison.Ordinal)
            < evalBody.IndexOf("ReadScriptAsync", StringComparison.Ordinal),
            "the action must be recorded before the script is run");

        int act = agent.IndexOf("private async Task<(bool Worked, bool Ready)> ActOnPageAsync", StringComparison.Ordinal);
        Assert.True(act >= 0);
        string actBody = agent[act..Math.Min(agent.Length, act + 700)];
        Assert.Contains("Acting(tab);", actBody);
        Assert.True(actBody.IndexOf("Acting(tab);", StringComparison.Ordinal)
            < actBody.IndexOf("ReadScriptAsync", StringComparison.Ordinal),
            "/click and /type must record the action before the script is run");
    }
}
