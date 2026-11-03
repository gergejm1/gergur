using System.Text.RegularExpressions;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// A page that goes unresponsive on a busy machine is given time before it is thrown away, and
/// the person is told; a new tab shows its own address, and takes the keyboard, while its view
/// is still being built, without a typed address being overwritten when it arrives.
/// </summary>
public sealed class HungPageAndNewTabTests
{
    private static string Code(params string[] parts)
        => Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource(parts)), @"//[^\n]*", "");

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"could not find {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced");
    }

    // ------------------------------------------------------------------ judging a hung page

    [Fact]
    public async Task APageThatStillDoesNotAnswerIsJudgedHung()
    {
        var verdict = await Tab.JudgeHungPageAsync(() => Task.CompletedTask, () => true, () => Task.FromResult(false));
        Assert.Equal(Tab.HungVerdict.Hung, verdict);
    }

    [Fact]
    public async Task APageThatAnswersAfterAllIsKept()
    {
        var verdict = await Tab.JudgeHungPageAsync(() => Task.CompletedTask, () => true, () => Task.FromResult(true));
        Assert.Equal(Tab.HungVerdict.Recovered, verdict);
    }

    [Fact]
    public async Task AProbeThatCannotBeIssuedCountsAsNoAnswer()
    {
        // Thrown outright, and as a faulted task: the page is no better off either way.
        Assert.Equal(Tab.HungVerdict.Hung,
            await Tab.JudgeHungPageAsync(() => Task.CompletedTask, () => true, () => throw new InvalidOperationException("no view")));
        Assert.Equal(Tab.HungVerdict.Hung,
            await Tab.JudgeHungPageAsync(() => Task.CompletedTask, () => true, () => Task.FromException<bool>(new InvalidOperationException("gone"))));
    }

    [Fact]
    public async Task AViewReplacedBeforeOrDuringTheWaitIsLeftAlone()
    {
        // Gone before the question is asked: it is not asked at all.
        bool asked = false;
        var before = await Tab.JudgeHungPageAsync(() => Task.CompletedTask, () => false, () => { asked = true; return Task.FromResult(false); });
        Assert.Equal(Tab.HungVerdict.Gone, before);
        Assert.False(asked);

        // Replaced while the question was out: its silence says nothing about the new view.
        int checks = 0;
        var during = await Tab.JudgeHungPageAsync(() => Task.CompletedTask, () => ++checks == 1, () => Task.FromResult(false));
        Assert.Equal(Tab.HungVerdict.Gone, during);
    }

    [Fact]
    public async Task NothingIsAskedUntilTheGraceIsOver()
    {
        var grace = new TaskCompletionSource();
        bool asked = false;
        var judging = Tab.JudgeHungPageAsync(() => grace.Task, () => true, () => { asked = true; return Task.FromResult(false); });

        await Task.Delay(50);
        Assert.False(asked);
        Assert.False(judging.IsCompleted);

        grace.SetResult();
        Assert.Equal(Tab.HungVerdict.Hung, await judging);
        Assert.True(asked);
    }

    // ------------------------------------------------------------------ the wiring around it

    [Fact]
    public void ADeadPageIsDiscardedAtOnceAndAnUnresponsiveOneIsWatched()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        string failed = Body(tab, "private void OnProcessFailed(");
        Assert.Matches(@"if \(e\.ProcessFailedKind is CoreWebView2ProcessFailedKind\.RenderProcessExited\)\s*\{[\s\S]*?Discard\(\);", failed);
        Assert.DoesNotMatch(@"RenderProcessExited\s*or\s*CoreWebView2ProcessFailedKind\.RenderProcessUnresponsive", failed);
        Assert.Matches(@"RenderProcessUnresponsive\s*&&\s*sender is CoreWebView2 hung\)\s*\{[\s\S]*?AfterCallback\(\(\) => _ = DiscardIfStillHungAsync\(hung\)\);", failed);
        Assert.Single(Regex.Matches(failed, @"\bDiscard\(\)"));
        // An unresponsive report with no view to wait on is noted rather than dropped silently.
        Assert.Matches(@"else if \(e\.ProcessFailedKind is CoreWebView2ProcessFailedKind\.RenderProcessUnresponsive\)\s*\{\s*Breadcrumbs\.Note\(", failed);
    }

    [Fact]
    public void TheWatchAsksTheRealPageAndDiscardsOnlyAHungOne()
    {
        string watch = Body(Code("src", "Gergur", "Tabs", "Tab.cs"), "private async Task DiscardIfStillHungAsync(");
        // What is handed to the judgement is the real wait, the real view check and the real probe.
        Assert.Contains("Task.Delay(HungGrace)", watch);
        Assert.Contains("ReferenceEquals(Core, hung)", watch);
        Assert.Contains("hung.ExecuteScriptAsync(\"1\")", watch);
        Assert.Contains("HungProbe", watch);
        // Discarded only for the one verdict, after the judgement.
        int judged = watch.IndexOf("await JudgeHungPageAsync(", StringComparison.Ordinal);
        int kept = watch.IndexOf("if (verdict != HungVerdict.Hung)", StringComparison.Ordinal);
        int discarded = watch.IndexOf("Discard();", StringComparison.Ordinal);
        Assert.True(judged >= 0 && kept > judged && discarded > kept);
        // One watch per view: a second report for the view already being waited on starts nothing.
        Assert.Matches(@"if \(ReferenceEquals\(_watchedHung, hung\)\)\s*\{\s*Breadcrumbs\.Note\([^;]*\);\s*return;\s*\}", watch);
        // Cleared however it ends, and the person told on both sides.
        Assert.Contains("ReferenceEquals(_watchedHung, hung)", watch);
        Assert.Matches(@"finally\s*\{\s*if \(ReferenceEquals\(_watchedHung, hung\)\)\s*_watchedHung = null;", watch);
        int told = watch.IndexOf("NotRespondingChanged?.Invoke(this, true);", StringComparison.Ordinal);
        Assert.True(told >= 0 && told < judged);
        Assert.Contains("NotRespondingChanged?.Invoke(this, false);", watch);
    }

    [Fact]
    public void OnlyTheNotRespondingMessageIsEverTakenDown()
    {
        // Still holding the message: cleared.
        Assert.Equal("", Gergur.UI.MainForm.StatusAfterNotResponding(Gergur.UI.MainForm.NotRespondingText));
        // Something else arrived since (a download, a saved bookmark): not ours to wipe.
        Assert.Null(Gergur.UI.MainForm.StatusAfterNotResponding("Downloading a.zip..."));
        Assert.Null(Gergur.UI.MainForm.StatusAfterNotResponding("Bookmarked."));
        Assert.Null(Gergur.UI.MainForm.StatusAfterNotResponding(""));
        // And it says what is happening and what comes next.
        Assert.Contains("not responding", Gergur.UI.MainForm.NotRespondingText);
        Assert.Contains("reload", Gergur.UI.MainForm.NotRespondingText);
    }

    [Fact]
    public void TheNotRespondingMessageFollowsTheTabItIsFor()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        string wiring = Body(form, "tab.NotRespondingChanged += (_, notResponding) =>");
        // Put up only for the tab being looked at, and remembered.
        Assert.Matches(@"if \(notResponding\)\s*\{\s*if \(Tabs\?\.ActiveTab != tab\)\s*return;\s*_notRespondingTab = tab;\s*ShowMessage\(NotRespondingText\);", wiring);
        // Taken down when it ends, wherever the person is: not gated on the active tab.
        Assert.Matches(@"else\s*\{[\s\S]*?ClearNotResponding\(\);", wiring);
        Assert.DoesNotMatch(@"else\s*\{[^}]*ActiveTab", wiring);
        // And when the person goes to another tab before it ends.
        string chrome = Body(form, "private void UpdateChrome(");
        Assert.Matches(@"if \(_notRespondingTab is not null && _notRespondingTab != active\)\s*\{\s*_notRespondingTab = null;\s*ClearNotResponding\(\);", chrome);
        // Only ever through the helper that leaves other messages alone.
        Assert.Contains("StatusAfterNotResponding(_messageLabel.Text)", Body(form, "private void ClearNotResponding("));
    }

    [Fact]
    public void AWaitedOnPageIsShownAgainWhereverItNextComesToFront()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        string chrome = Body(form, "private void UpdateChrome(");
        // Switched back to, or torn off into this window while it was being waited on.
        Assert.Matches(@"if \(_notRespondingTab is null && active is \{ IsWaitingOnPage: true \} waiting\)\s*\{\s*_notRespondingTab = waiting;\s*ShowMessage\(NotRespondingText\);", chrome);
        // After the take-down for a tab that was left, so leaving and returning in one update works.
        Assert.True(chrome.IndexOf("ClearNotResponding();", StringComparison.Ordinal) < chrome.IndexOf("IsWaitingOnPage: true", StringComparison.Ordinal));
        Assert.Contains("internal bool IsWaitingOnPage => _watchedHung is not null;", Code("src", "Gergur", "Tabs", "Tab.cs"));
    }

    [Fact]
    public void ThePersonIsToldWhenAHungPageWasReloaded()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        string watch = Body(tab, "private async Task DiscardIfStillHungAsync(");
        // Said just before the discard, which is what loses what was typed into it.
        int told = watch.IndexOf("DiscardedAfterHang?.Invoke(this, EventArgs.Empty);", StringComparison.Ordinal);
        int discarded = watch.IndexOf("Discard();", StringComparison.Ordinal);
        Assert.True(told >= 0 && discarded > told);
        Assert.Contains("DiscardedAfterHang = null;", Body(tab, "internal void DetachOwnerHandlers("));
        string wiring = Body(Code("src", "Gergur", "UI", "MainForm.cs"), "tab.DiscardedAfterHang += (_, _) =>");
        Assert.Contains("Tabs?.ActiveTab == tab", wiring);
        Assert.Contains("was reloaded", wiring);
    }

    [Fact]
    public void ADiscardedOrMovedTabLetsGoOfTheViewItWasWatching()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        Assert.Contains("_watchedHung = null;", Body(tab, "private void DropView("));
        Assert.Contains("NotRespondingChanged = null;", Body(tab, "internal void DetachOwnerHandlers("));
        string watch = Body(tab, "private async Task DiscardIfStillHungAsync(");
        // A report for a view already gone says nothing and starts nothing.
        int gone = watch.IndexOf("!ReferenceEquals(Core, hung)", StringComparison.Ordinal);
        int watching = watch.IndexOf("_watchedHung = hung;", StringComparison.Ordinal);
        int said = watch.IndexOf("NotRespondingChanged?.Invoke(this, true);", StringComparison.Ordinal);
        Assert.True(gone >= 0 && watching > gone && said > watching);
        // The end is always said, whatever became of the tab, so the message does not outlive it.
        Assert.Matches(@"if \(said\)\s*NotRespondingChanged\?\.Invoke\(this, false\);", watch);
        Assert.DoesNotMatch(@"if \(said && !_disposed\)", watch);
        // A sleeping view is not asked, which would wake it.
        Assert.Contains("State != TabState.Suspended", watch);
    }
    // ------------------------------------------------------------------ a new tab

    [Fact]
    public void ATypedAddressIsNotOverwrittenByTheHomePageThatFollowsTheBuild()
    {
        // The new tab's address bar takes the keyboard at once, so an address can be typed and
        // sent while the view is still being built. The home page then went to the tab last.
        Assert.True(TabManager.ShouldOpenInitialUrl("about:blank", "about:blank", null));
        // Navigated during the build and applied since.
        Assert.False(TabManager.ShouldOpenInitialUrl("about:blank", "https://typed.example/", null));
        // Asked for during the build and not applied yet.
        Assert.False(TabManager.ShouldOpenInitialUrl("about:blank", "about:blank", "https://typed.example/"));

        string create = Body(Code("src", "Gergur", "Tabs", "TabManager.cs"), "public async Task<Tab> CreateTabAsync(");
        int start = create.IndexOf("string urlAtStart = tab.Url;", StringComparison.Ordinal);
        int waited = create.IndexOf("await ActivateAsync(tab);", StringComparison.Ordinal);
        int decided = create.IndexOf("ShouldOpenInitialUrl(urlAtStart, tab.Url, tab.DeferredNavigation)", StringComparison.Ordinal);
        int navigated = create.IndexOf("await tab.NavigateAsync(url);", StringComparison.Ordinal);
        Assert.True(start >= 0 && waited > start && decided > waited && navigated > decided);
    }

    [Fact]
    public void ANewTabShowsItsOwnAddressAndTakesTheKeyboardBeforeItsViewIsBuilt()
    {
        string activate = Body(Code("src", "Gergur", "Tabs", "TabManager.cs"), "public async Task ActivateAsync(");
        int active = activate.IndexOf("ActiveTab = tab;", StringComparison.Ordinal);
        int said = activate.IndexOf("RaiseChanged();", StringComparison.Ordinal);
        int built = activate.IndexOf("await tab.ActivateAsync();", StringComparison.Ordinal);
        Assert.True(active >= 0 && said > active && built > said,
            "Changed must be raised after the tab becomes active and before its view is awaited");

        string newTab = Body(Code("src", "Gergur", "UI", "MainForm.cs"), "public async Task NewTabAsync(");
        int creating = newTab.IndexOf("var creating = Tabs.CreateTabAsync(", StringComparison.Ordinal);
        int focused = newTab.IndexOf("FocusAddressBar();", StringComparison.Ordinal);
        int awaited = newTab.IndexOf("await creating;", StringComparison.Ordinal);
        Assert.True(creating >= 0 && focused > creating && awaited > focused,
            "the address bar takes focus after the tab is started and before its view is awaited");
        Assert.Single(Regex.Matches(newTab, @"FocusAddressBar\(\);"));
    }
}
