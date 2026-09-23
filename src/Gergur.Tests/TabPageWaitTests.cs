using System.Diagnostics;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Waiting for a tab's page to arrive.
///
/// This wait has produced a defect in three consecutive reviews, and every one of them
/// was a wrong answer rather than a crash: "the page is here" for a page that was not,
/// which downstream becomes a screenshot of a blank frame served at 200, or a /page that
/// reports the right url and title over empty text. Nothing about any of those looks
/// wrong from the outside, which is exactly why it needs pinning here.
///
/// AwaitPageAsync touches no WebView2, so a bare Tab is enough to drive every branch.
/// </summary>
public sealed class TabPageWaitTests
{
    private static Tab Bare() => new(null!);

    private static DateTime In(int ms) => DateTime.UtcNow + TimeSpan.FromMilliseconds(ms);

    [Fact]
    public async Task ATabWithNothingLoadingIsAlreadyReady()
    {
        var tab = Bare();

        Assert.True(await tab.AwaitPageAsync(In(50)));
    }

    [Fact]
    public async Task APageThatArrivesIsReported()
    {
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        var waiting = tab.AwaitPageAsync(In(2000));

        tab.NoteNavigationFinished(1, true);

        Assert.True(await waiting);
    }

    [Fact]
    public async Task APageThatFailsStillCountsAsArrived()
    {
        // The engine paints an error page, and that is a real frame: refusing to
        // photograph or read a 404 would be its own kind of wrong answer.
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        var waiting = tab.AwaitPageAsync(In(2000));

        tab.NoteNavigationFinished(1, false);

        Assert.True(await waiting);
    }

    [Fact]
    public async Task APageThatNeverArrivesRunsOutRatherThanClaimingToBeThere()
    {
        var tab = Bare();
        tab.NoteNavigationStarted(1);

        var clock = Stopwatch.StartNew();
        bool ready = await tab.AwaitPageAsync(In(200));
        clock.Stop();

        Assert.False(ready);
        Assert.True(clock.ElapsedMilliseconds >= 150, $"gave up after only {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AViewTornDownMidWaitIsNotAPageThatArrived()
    {
        // The one measured against the code and found wrong: abandoning the load and
        // finishing it both complete the same task with false. A renderer going
        // unresponsive is precisely what a screenshot refuses for, and the tab is
        // discarded when that happens, so reading it as "ready" put the blank png back.
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        var waiting = tab.AwaitPageAsync(In(2000));

        tab.AbandonPendingLoad();

        Assert.False(await waiting);
    }

    [Fact]
    public async Task ANavigationThatSupersedesAnotherIsWaitedForToo()
    {
        // Superseding releases the old wait immediately with false. Ending there would
        // refuse a tab that was a moment away from being perfectly fine.
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        var waiting = tab.AwaitPageAsync(In(2000));

        tab.NoteNavigationStarted(2);          // the first is released with false
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);     // still waiting, for the new one

        tab.NoteNavigationFinished(2, true);

        Assert.True(await waiting);
    }

    [Fact]
    public async Task ASupersedingNavigationThatAlsoNeverArrivesRunsOut()
    {
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        var waiting = tab.AwaitPageAsync(In(250));

        tab.NoteNavigationStarted(2);

        Assert.False(await waiting);
    }

    [Fact]
    public async Task ADeadlineAlreadyPastDoesNotWait()
    {
        var tab = Bare();
        tab.NoteNavigationStarted(1);

        var clock = Stopwatch.StartNew();
        bool ready = await tab.AwaitPageAsync(DateTime.UtcNow - TimeSpan.FromSeconds(1));
        clock.Stop();

        Assert.False(ready);
        Assert.True(clock.ElapsedMilliseconds < 500, $"waited {clock.ElapsedMilliseconds}ms on a past deadline");
    }

    [Fact]
    public async Task ANavigationLoopDoesNotHoldTheWaitPastItsDeadline()
    {
        // A page that navigates in a tight loop supersedes its own load over and over,
        // which is the shape that could turn the loop above into a spin.
        var tab = Bare();
        tab.NoteNavigationStarted(1);

        ulong next = 2;
        using var stop = new CancellationTokenSource();
        var churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                tab.NoteNavigationStarted(next++);
        });

        var clock = Stopwatch.StartNew();
        bool ready = await tab.AwaitPageAsync(In(300));
        clock.Stop();
        stop.Cancel();
        await churn;

        Assert.False(ready);
        Assert.True(clock.ElapsedMilliseconds < 3000, $"took {clock.ElapsedMilliseconds}ms for a 300ms budget");

        // The count, not the clock. A wait that spins 1.6 million times still finishes
        // inside its deadline, so wall time alone passes with or without the floor, and
        // the 227 MB of timers such a spin allocates is what the floor is there to stop.
        int allowed = (int)(TimeSpan.FromMilliseconds(300).Ticks / Tab.RoundFloor.Ticks) + 5;
        Assert.True(tab.LastWaitRounds <= allowed,
            $"{tab.LastWaitRounds} rounds for a 300ms budget with a {Tab.RoundFloor.TotalMilliseconds}ms floor");
    }

    [Fact]
    public async Task ALoadOlderThanTheCutoffStopsCountingAsOne()
    {
        // A navigation that starts and never completes, which is what a link that turns
        // into a download looks like, must not make every later read pay a full wait.
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        Age(tab, TimeSpan.FromMinutes(5));

        Assert.False(tab.IsLoading);
        Assert.True(await tab.AwaitPageAsync(In(50)));
    }

    /// <summary>Winds the pending load's start back, so the cutoff can be exercised.</summary>
    private static void Age(Tab tab, TimeSpan by)
    {
        var field = typeof(Tab).GetField(
            "_pendingLoadStartedUtc",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var started = (DateTime)field!.GetValue(tab)!;
        field.SetValue(tab, started - by);
    }
}

/// <summary>
/// The parts of the read path that mutation testing found were pinned by nothing.
///
/// Eight guards added over two review passes survived being deleted with all 790 tests
/// still green, including one line of the abandonment latch whose removal would have made
/// every screenshot of a once-discarded tab fail forever. A fix nothing would notice the
/// loss of is not a fix yet.
/// </summary>
public sealed class TabReadVerdictTests
{
    /// <summary>
    /// A Tab with no view that will not try to build one. It is put in the back-off after
    /// repeated failures, so EnsureLiveAsync answers straight away and leaves Core null,
    /// which is the path these are about. (Planting a completed build task used to be
    /// enough; since a finished build with no view now means "try again", it is not.)
    /// </summary>
    private static Tab Bare()
    {
        var tab = new Tab(null!);
        Set(tab, "_failedBuilds", Tab.FailedBuildsBeforeGivingUp);
        Set(tab, "_lastFailedBuildUtc", DateTime.UtcNow);
        return tab;
    }

    private static void Set(Tab tab, string field, object value)
        => typeof(Tab)
            .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(tab, value);

    private static object? Get(Tab tab, string field)
        => typeof(Tab)
            .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(tab);

    [Fact]
    public async Task ATabWithNoViewIsNotReadable()
    {
        // The Core is null path, which is the only one of these reachable without an
        // engine, and it is what every other Ready answer funnels through.
        var tab = Bare();

        var (result, ready) = await tab.ReadScriptAsync("document.title", TimeSpan.FromMilliseconds(50));

        Assert.False(ready);
        Assert.Equal("null", result);
    }

    [Fact]
    public async Task ATabWithNoViewHasNoScreenshotEither()
    {
        var tab = Bare();

        var (png, ready) = await tab.CaptureScreenshotAsync(TimeSpan.FromMilliseconds(50));

        Assert.False(ready);
        Assert.Empty(png);
    }

    [Theory]
    [InlineData(true, true, true)]      // a view, and a page: read it
    [InlineData(true, false, false)]    // a view, but the page never arrived
    [InlineData(false, true, false)]    // no view at all
    [InlineData(false, false, false)]
    public void AReadNeedsBothAViewAndAPage(bool hasView, bool arrived, bool expected)
    {
        // Same two terms as the photograph, same reason for being a function: a tab with
        // no view answers at the first term, so the second cannot be reached through
        // ReadScriptAsync from a test, and a mutation that dropped it left every test
        // green. /page is the read CLAUDE.md recommends, which makes a wrong answer from
        // it the most likely one to be believed.
        Assert.Equal(expected, Tab.Readable(hasView, arrived));
    }

    [Theory]
    // nothing tried yet, or the last one worked and the view went: build one
    [InlineData(0, 0, true)]
    // one failure, long enough ago: try again
    [InlineData(1, 60, true)]
    // the same failure a moment ago: do not, or a browser whose engine cannot start at
    // all spends the rest of the session starting it once per request
    [InlineData(1, 0, false)]
    // after enough in a row, wait a lot longer rather than stop: a runtime update or a
    // briefly locked profile folder is transient, and a permanently dead tab is worse
    [InlineData(3, 60, false)]
    [InlineData(4, 600, true)]
    public void AFailedViewBuildIsRetriedButNotWithoutLimit(int failures, int secondsSince, bool expected)
    {
        // Caching a failed build poisons the tab: every later read gets the same failure
        // for good. Retrying without limit is worse, because each attempt is another
        // engine start, and the browser this lives in exists to hold few of those.
        Assert.Equal(expected, Tab.ShouldRebuild(failures, TimeSpan.FromSeconds(secondsSince)));
    }

    [Fact]
    public async Task ABuildAlreadyOnItsWayIsJoinedRatherThanRaced()
    {
        // The guard that stops a view leaking. Two builds at once each assign the view,
        // the second overwrites the first, and the first stays parented and running with
        // nothing able to reach it. A mutation that removed this guard left every test
        // green: the rows that claimed to cover it tested parameters production passed as
        // constants.
        var tab = new Tab(null!);
        var inFlight = new TaskCompletionSource();
        Set(tab, "_ensureLiveTask", inFlight.Task);

        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(50));

        Assert.Same(inFlight.Task, Get(tab, "_ensureLiveTask"));
        Assert.Equal(0, (int)Get(tab, "_failedBuilds")!);   // no second build was started
    }

    [Fact]
    public async Task AClosedTabBuildsNothing()
    {
        // A request still holding a reference to a closed tab used to start a whole
        // engine view on every read, only for it to be thrown away.
        var tab = new Tab(null!);
        tab.Dispose();

        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(50));

        Assert.Equal(0, (int)Get(tab, "_failedBuilds")!);
        Assert.Null(tab.LastBuildFailure);
    }

    [Fact]
    public async Task APageThatCouldNotStartSaysSo()
    {
        // The build no longer throws into the click that asked for it, and swallowing it
        // completely left a blank window with no reason anywhere. The window listens for
        // this and tells the person; at launch it brings back the "failed to start" dialog.
        var tab = new Tab(null!);   // no owner, so the build fails
        int told = 0;
        tab.ViewBuildFailed += (_, _) => told++;

        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, told);
        Assert.False(string.IsNullOrEmpty(tab.LastBuildFailure));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(-3600, 0)]
    public void ABudgetAlreadyPastIsZeroRatherThanNegative(int secondsFromNow, int expected)
    {
        // A negative remainder handed to Task.Delay throws, and handed to a comparison
        // reads as "plenty of time left".
        var left = Tab.Remaining(DateTime.UtcNow + TimeSpan.FromSeconds(secondsFromNow));

        Assert.True(left >= TimeSpan.Zero, $"got {left}");
        Assert.True(left.TotalSeconds <= expected + 1, $"got {left}");
    }

    [Fact]
    public void EveryTabGetsItsOwnId()
    {
        // The headline of this whole change, and a mutation that made Id a constant left
        // every test green twice: once because there was no test, and once because a
        // slice-and-replace in this file silently cut the one that had been added. With
        // every tab named the same thing, TargetIndex resolves any id to the first tab, so
        // /close closes the tab the user is reading, /type types into it and /navigate
        // takes it away.
        var ids = Enumerable.Range(0, 50).Select(_ => new Tab(null!).Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public void ATabKeepsItsIdForLife()
    {
        // It is the name an agent holds across everything the tab does, including being
        // torn into another window, which is the case an index could not survive.
        var tab = new Tab(null!);
        string given = tab.Id;

        tab.Dispose();

        Assert.Equal(given, tab.Id);
    }

    [Fact]
    public async Task ATabCountsItsFailedBuildsSoTheRetryLimitMeansSomething()
    {
        // The counter that feeds ShouldRebuild. Mutating away the two lines that keep it
        // left every test green, and without them the limit and the five second floor
        // never engage: back to one engine start per request, which is the leak the whole
        // retry was added to bound.
        var tab = new Tab(null!);   // no owner, so every build fails

        await Record.ExceptionAsync(() => tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200)));

        Assert.Equal(1, FailedBuilds(tab));

        // The floor holds the next one off, so the count does not move.
        await Record.ExceptionAsync(() => tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, FailedBuilds(tab));

        // Wound back past the floor, it tries again and counts again.
        AgeLastBuild(tab, Tab.BetweenFailedBuilds + TimeSpan.FromSeconds(1));
        await Record.ExceptionAsync(() => tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200)));

        Assert.Equal(2, FailedBuilds(tab));
    }

    private static int FailedBuilds(Tab tab)
        => (int)typeof(Tab)
            .GetField("_failedBuilds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(tab)!;

    private static void AgeLastBuild(Tab tab, TimeSpan by)
    {
        var field = typeof(Tab).GetField(
            "_lastFailedBuildUtc",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(tab, (DateTime)field.GetValue(tab)! - by);
    }

    [Fact]
    public async Task ATabThatCouldNotBuildAViewTriesAgainRatherThanRethrowingForever()
    {
        // Driven through the real path: a cached faulted build used to come straight back
        // out of every later read, so one bad engine start meant a permanent 500 on that
        // tab for the life of the process.
        var tab = new Tab(null!);
        var first = new InvalidOperationException("the first build failed");
        typeof(Tab)
            .GetField("_ensureLiveTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(tab, Task.FromException(first));

        // It has no owner, so the rebuild fails too. What matters is that it tried at all
        // rather than handing back the failure it cached, and that nothing was thrown at
        // the caller. (This used to assert the two failures differed, which could not fail
        // once nothing was thrown: the second one was always null.)
        var thrown = await Record.ExceptionAsync(
            () => tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200)));

        Assert.Null(thrown);
        Assert.Equal(1, (int)Get(tab, "_failedBuilds")!);
    }

    [Fact]
    public async Task AUrlTheEngineWouldNotTakeIsNotRecordedAsWhereTheTabIs()
    {
        // Otherwise /tabs reports a url the tab is not on, and the session restore opens
        // that url on the next start.
        //
        // Through the seam, so the navigate really runs and finds no view to take it. An
        // earlier version of this let the build throw first, so it passed without ever
        // reaching the line it names.
        var tab = Bare();
        string before = tab.Url;

        bool went = await tab.NavigateAsync("https://example.com/");

        Assert.False(went);
        Assert.Equal(before, tab.Url);
    }

    [Fact]
    public async Task AScriptWhoseStartFailsIsAnsweredRatherThanLeftToTimeOut()
    {
        // The call that starts a script is deliberately not awaited, because the wrapper
        // runs the expression synchronously and a page that blocks its main thread would
        // never return from it. When that call fails asynchronously, the failure has to
        // reach the caller: without it they waited out the whole timeout and were told
        // the script "did not settle" rather than what went wrong.
        var tab = Bare();
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.AwaitScript("tok", waiting);

        Assert.True(tab.FailAwaited("tok", "the page could not be asked to run that"));

        string answer = await waiting.Task;
        Assert.Contains("\"ok\":false", answer);
        Assert.Contains("could not be asked", answer);
    }

    [Fact]
    public void AnsweringAScriptNobodyIsWaitingOnIsHarmless()
    {
        Assert.False(Bare().FailAwaited("no-such-token", "whatever"));
    }

    [Fact]
    public async Task AScriptIsOnlyAnsweredOnce()
    {
        var tab = Bare();
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.AwaitScript("tok", waiting);

        Assert.True(tab.FailAwaited("tok", "first"));
        Assert.False(tab.FailAwaited("tok", "second"));

        Assert.Contains("first", await waiting.Task);
    }

    [Theory]
    [InlineData(true, true, true)]      // a view, and a page: photograph it
    [InlineData(true, false, false)]    // a view, but the page never arrived
    [InlineData(false, true, false)]    // no view at all
    [InlineData(false, false, false)]
    public void APhotographNeedsBothAViewAndAPage(bool hasView, bool arrived, bool expected)
    {
        // The second term is the one that keeps coming back: the capture used to run
        // whatever the wait had said, so a tab whose page never loaded was photographed
        // blank and served at 200. It cannot be reached through CaptureScreenshotAsync in
        // a test, because a tab with no view answers at the first term and giving one a
        // view needs a real engine, so the decision lives here where it can be pinned.
        Assert.Equal(expected, Tab.WorthCapturing(hasView, arrived));
    }

    [Fact]
    public async Task APageThatArrivesAfterAnAbandonmentIsReadyAgain()
    {
        // The line mutation testing showed nothing was watching: NoteNavigationStarted
        // clears the abandonment latch. Without it the latch stays set for the life of the
        // tab, so every later load reports "the view went away" on its first completion
        // and /screenshot 503s forever for any tab that was ever discarded.
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        tab.AbandonPendingLoad();

        // A wait that starts after the abandonment is not affected by it: the generation
        // it samples is the one that is already current.
        tab.NoteNavigationStarted(2);
        var waiting = tab.AwaitPageAsync(DateTime.UtcNow + TimeSpan.FromSeconds(2));
        tab.NoteNavigationFinished(2, true);

        Assert.True(await waiting);
    }

    [Fact]
    public async Task AnAbandonmentDuringAWaitIsSeenEvenIfANewPageStartsImmediately()
    {
        // The ordering that used to be a coin flip. Abandoning and then starting a new
        // navigation before the waiter's continuation runs cleared the latch first, so
        // whether the wait noticed came down to scheduling. The generation counter is read
        // before the await and compared after, so it cannot be cleared out from under it.
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        var waiting = tab.AwaitPageAsync(DateTime.UtcNow + TimeSpan.FromSeconds(2));
        await Task.Delay(20);

        tab.AbandonPendingLoad();
        tab.NoteNavigationStarted(2);       // the rebuilt tab, straight away

        // Quickly, which is the whole point. The version this replaced also answered
        // false, but by sitting out the full two seconds: the flag had been cleared
        // before the waiter looked at it, so it never saw the abandonment and only gave
        // up when the deadline arrived. A test that accepts either is pinning nothing.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool ready = await waiting;
        clock.Stop();

        Assert.False(ready);
        Assert.True(clock.ElapsedMilliseconds < 500,
            $"took {clock.ElapsedMilliseconds}ms, so it timed out rather than noticing");
    }

    [Fact]
    public void AbandoningMovesTheGenerationOn()
    {
        var tab = Bare();
        int before = tab.LoadGeneration;

        tab.NoteNavigationStarted(1);
        Assert.Equal(before, tab.LoadGeneration);   // starting one is not abandoning one

        tab.AbandonPendingLoad();

        Assert.NotEqual(before, tab.LoadGeneration);
    }

    [Fact]
    public async Task AnEngineCallThatNeverAnswersIsGivenUpOn()
    {
        // A page whose main thread is blocked never answers any WebView2 call, and none of
        // them time out by themselves. One "while (true)" used to hold an http request,
        // its stream and its task for the life of the process, and the next read of that
        // tab joined it.
        var never = new TaskCompletionSource<string>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (value, inTime) = await Budget(never.Task, TimeSpan.FromMilliseconds(200), "gave up");
        clock.Stop();

        Assert.False(inTime);
        Assert.Equal("gave up", value);
        Assert.True(clock.ElapsedMilliseconds >= 150, $"gave up after only {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AnEngineCallThatAnswersInTimeIsUsed()
    {
        var (value, inTime) = await Budget(Task.FromResult("the answer"), TimeSpan.FromSeconds(2), "gave up");

        Assert.True(inTime);
        Assert.Equal("the answer", value);
    }

    [Fact]
    public async Task AnEngineCallThatThrowsIsNotAnUnhandledFault()
    {
        var failed = Task.FromException<string>(new InvalidOperationException("engine said no"));

        var (value, inTime) = await Budget(failed, TimeSpan.FromSeconds(2), "gave up");

        Assert.False(inTime);
        Assert.Equal("gave up", value);
    }

    [Fact]
    public async Task ABudgetAlreadySpentGivesUpAtOnce()
    {
        var never = new TaskCompletionSource<string>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (_, inTime) = await Budget(never.Task, TimeSpan.Zero, "gave up");
        clock.Stop();

        Assert.False(inTime);
        Assert.True(clock.ElapsedMilliseconds < 500, $"waited {clock.ElapsedMilliseconds}ms on a spent budget");
    }

    [Fact]
    public async Task ASpentBudgetIsSpentEvenIfTheAnswerIsAlreadyThere()
    {
        // I recorded the spent-budget guard as unobservable, on the grounds that
        // Task.Delay(Zero) wins the race anyway. That is only true when the work never
        // completes. WhenAny prefers an already-completed work task, so without the guard
        // this returns the answer and claims it was in time, and a caller that asked for
        // zero budget gets told its deadline was met.
        var (value, inTime) = await Budget(Task.FromResult("done"), TimeSpan.Zero, "gave up");

        Assert.False(inTime);
        Assert.Equal("gave up", value);
    }

    private static Task<(string Value, bool InTime)> Budget(Task<string> work, TimeSpan budget, string late)
        => Tab.WithBudget(work, budget, late);
}

/*
 * What is not covered here, measured rather than assumed. Each was mutated and no test
 * noticed. This list has been wrong before, in both directions, so it is a record of what
 * was measured and nothing more.
 *
 * Needs a real CoreWebView2, because each sits behind a "this tab has a view" gate that
 * nothing here can satisfy:
 *   - that the retry disposes the half built view before building another (the leak this
 *     retry exists to bound, one layer up)
 *   - the screenshot capture spending Remaining(deadline) rather than a fresh budget
 *   - the /eval settle spending Remaining(deadline)
 *   - ReadScriptAsync spending Remaining(deadline)
 *   - that WithBudget watches the work it abandoned, so a late fault is not unobserved
 *
 * Needs a live AppSession:
 *   - that ReadStringAsync hands its Ready flag back unchanged to /page and /html
 *   - that /click, /type and /eval?await=0 answer 503 rather than ok:false
 *   - that the 500 body and the MCP error carry no exception text
 *   - that Settings.WriteAtomically's copy fallback restores the previous bytes (the test
 *     that names it fails the temp write, so the fallback is never entered)
 *
 * All of those are wiring or call-site arithmetic rather than decisions, which is why
 * there is nothing to pull out the way Readable, WorthCapturing and ShouldRebuild were.
 * They need the browser.
 */
