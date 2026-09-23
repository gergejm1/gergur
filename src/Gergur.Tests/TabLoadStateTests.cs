using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The two pieces of bookkeeping that decide whether a read of a tab gets the page or an
/// empty one, and whether the memory policy is allowed to freeze a tab mid-read.
///
/// These have been wrong in three consecutive reviews, every time in a different way, and
/// every time the symptom was a 200 that looked perfectly fine: the real url, the real
/// title, and the wrong page underneath. They touch no WebView2, so there was never a
/// reason for them to be untested except that nobody had written it down.
///
/// A Tab with no manager is enough: nothing here reaches the owner.
/// </summary>
public sealed class TabLoadStateTests
{
    private static Tab Bare() => new(null!);

    [Fact]
    public void ATabWithNothingHappeningIsNotLoading()
        => Assert.False(Bare().IsLoading);

    [Fact]
    public void ANavigationThatWasIssuedCountsAsLoading()
    {
        // Issued but not yet announced by the engine. This is the gap reads used to be
        // answered in: the view exists, so it looked settled, and the page was about:blank.
        var tab = Bare();

        tab.NoteNavigationStarted(null);

        Assert.True(tab.IsLoading);
    }

    [Fact]
    public void TheAnnouncementOfTheSameNavigationDoesNotStartASecondOne()
    {
        var tab = Bare();
        tab.NoteNavigationStarted(null);   // issued
        tab.NoteNavigationStarted(7);      // the engine announcing that same one

        tab.NoteNavigationFinished(7, true);

        Assert.False(tab.IsLoading);
    }

    [Fact]
    public void FinishingTheNavigationBeingWaitedOnEndsTheLoad()
    {
        var tab = Bare();
        tab.NoteNavigationStarted(3);

        tab.NoteNavigationFinished(3, true);

        Assert.False(tab.IsLoading);
    }

    [Fact]
    public void FinishingSomeOtherNavigationDoesNotEndIt()
    {
        // The failure this exists to stop. A tab still loading A is pointed at B; A's
        // abort arrives within milliseconds, and if it cleared the load then a read that
        // was waiting for B returned immediately with A's page and B's url beside it.
        var tab = Bare();
        tab.NoteNavigationStarted(1);      // A, announced
        tab.NoteNavigationStarted(2);      // B supersedes it

        tab.NoteNavigationFinished(1, false);   // A's abort lands

        Assert.True(tab.IsLoading);

        tab.NoteNavigationFinished(2, true);    // B really arrives
        Assert.False(tab.IsLoading);
    }

    [Fact]
    public void AFinishForANavigationNobodyIsWaitingOnIsHarmless()
    {
        var tab = Bare();

        tab.NoteNavigationFinished(9, true);

        Assert.False(tab.IsLoading);
    }

    [Fact]
    public async Task ASupersededNavigationReleasesWhoeverWasWaitingOnIt()
    {
        // Rather than leaving them to sit out the whole timeout for a page that was
        // abandoned before it arrived.
        var tab = Bare();
        tab.NoteNavigationStarted(1);
        var waiting = Loading(tab);

        tab.NoteNavigationStarted(2);

        Assert.True(waiting.IsCompleted);
        Assert.False(await waiting);
    }

    [Fact]
    public async Task AWaiterIsReleasedWithWhetherThePageArrived()
    {
        var tab = Bare();
        tab.NoteNavigationStarted(4);
        var waiting = Loading(tab);

        tab.NoteNavigationFinished(4, true);

        Assert.True(waiting.IsCompleted);
        Assert.True(await waiting);
    }

    /// <summary>The task a reader would wait on, which is the property they use.</summary>
    private static Task<bool> Loading(Tab tab)
    {
        var pending = tab.PendingLoad;
        Assert.NotNull(pending);
        return pending;
    }
}

/// <summary>
/// Whether the lifecycle policy is allowed to freeze this tab. A read in flight must not
/// be, or a script that was about to settle simply never settles, intermittently, and
/// reads as a broken page. But a read that can never finish must not pin a renderer awake
/// for the life of the process either, in a browser whose whole point is frugality.
/// </summary>
public sealed class TabReadCounterTests
{
    private static Tab Bare() => new(null!);

    private static void Begin(Tab tab) => tab.BeginRead();
    private static void End(Tab tab) => tab.EndRead();

    [Fact]
    public void ATabNobodyIsReadingMaySleep()
        => Assert.False(Bare().IsBeingRead);

    [Fact]
    public void ATabBeingReadMayNot()
    {
        var tab = Bare();

        Begin(tab);

        Assert.True(tab.IsBeingRead);
    }

    [Fact]
    public void AFinishedReadReleasesIt()
    {
        var tab = Bare();
        Begin(tab);

        End(tab);

        Assert.False(tab.IsBeingRead);
    }

    [Fact]
    public void NestedReadsHoldItUntilTheOuterOneFinishes()
    {
        // Two requests reading the same tab at once. Releasing on the first to finish
        // would leave the second unprotected, which is the hole the counter exists to
        // close. (The screenshot path does not actually nest: WaitForPageAsync releases
        // in its finally before the capture takes its own.)
        var tab = Bare();
        Begin(tab);
        Begin(tab);

        End(tab);
        Assert.True(tab.IsBeingRead);

        End(tab);
        Assert.False(tab.IsBeingRead);
    }

    [Fact]
    public void AReadThatStartsAfterAnotherHasAgedOutIsStillProtected()
    {
        // A read that can never finish, which is a page whose main thread is wedged,
        // holds the counter above zero for good. Ageing it out is right, but the stamp
        // used to be taken only when the counter went from nothing to something, so from
        // then on every later read on that tab was unprotected: a long evaluation started
        // afterwards got its renderer frozen underneath it, which is the intermittent
        // failure the counter was added to stop.
        var tab = Bare();
        Begin(tab);                                  // the one that never returns
        Age(tab, TimeSpan.FromMinutes(5));
        Assert.False(tab.IsBeingRead);               // aged out, as intended

        Begin(tab);                                  // a new read arrives

        Assert.True(tab.IsBeingRead);
    }

    [Fact]
    public void AReadThatCannotFinishStopsHoldingTheTabOpen()
    {
        // The other half: in a browser whose whole point is being frugal, one wedged
        // renderer must not be pinned awake for the life of the process.
        var tab = Bare();
        Begin(tab);
        Assert.True(tab.IsBeingRead);

        Age(tab, TimeSpan.FromMinutes(5));

        Assert.False(tab.IsBeingRead);
    }

    /// <summary>Winds this tab's read stamp back, so the ageing can be exercised.</summary>
    private static void Age(Tab tab, TimeSpan by)
    {
        var field = typeof(Tab).GetField(
            "_readingSinceTicks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        long now = (long)field!.GetValue(tab)!;
        field.SetValue(tab, now - by.Ticks);
    }

    [Fact]
    public void AReadMarksTheTabAsRecentlyUsed()
    {
        // Otherwise the lifecycle tick sees a tab whose last activity is however long ago
        // it was parked and freezes it again underneath the request that just woke it.
        var tab = Bare();
        var before = tab.LastActiveUtc;
        Thread.Sleep(30);   // DateTime.UtcNow moves in ~15.6ms steps by default

        Begin(tab);
        End(tab);

        Assert.True(tab.LastActiveUtc > before);
    }
}
