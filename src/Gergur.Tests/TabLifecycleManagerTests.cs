using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

public sealed class TabLifecycleManagerTests
{
    private sealed class FakeTab : ITabHandle
    {
        public TabState State { get; set; } = TabState.Hidden;
        public DateTime LastActiveUtc { get; set; }
        public bool IsPlayingAudio { get; set; }
        public bool IsCurrent { get; set; }
        public bool SuspendResult { get; set; } = true;
        public int SuspendCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public bool? LowTarget { get; private set; }

        public Task<bool> TrySuspendAsync()
        {
            SuspendCalls++;
            if (SuspendResult)
                State = TabState.Suspended;
            return Task.FromResult(SuspendResult);
        }

        public void Discard()
        {
            DiscardCalls++;
            State = TabState.Discarded;
        }

        public void SetLowMemoryTarget(bool low) => LowTarget = low;
    }

    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    private static TabLifecycleManager Manager()
        => new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), () => Now);

    private static FakeTab TabIdleFor(TimeSpan idle, TabState state = TabState.Hidden)
        => new() { State = state, LastActiveUtc = Now - idle };

    [Fact]
    public async Task RecentHiddenTabIsLeftAlone()
    {
        var tab = TabIdleFor(TimeSpan.FromMinutes(2));
        await Manager().TickAsync([tab]);
        Assert.Equal(0, tab.SuspendCalls);
        Assert.Equal(TabState.Hidden, tab.State);
    }

    [Fact]
    public async Task IdleHiddenTabIsSuspended()
    {
        var tab = TabIdleFor(TimeSpan.FromMinutes(6));
        await Manager().TickAsync([tab]);
        Assert.Equal(1, tab.SuspendCalls);
        Assert.Equal(TabState.Suspended, tab.State);
    }

    [Fact]
    public async Task ActiveTabIsNeverTouched()
    {
        var tab = TabIdleFor(TimeSpan.FromHours(2), TabState.Active);
        await Manager().TickAsync([tab]);
        Assert.Equal(0, tab.SuspendCalls);
        Assert.Equal(0, tab.DiscardCalls);
    }

    [Fact]
    public async Task AudioTabGetsLowMemoryTargetInsteadOfSuspend()
    {
        var tab = TabIdleFor(TimeSpan.FromMinutes(10));
        tab.IsPlayingAudio = true;
        await Manager().TickAsync([tab]);
        Assert.Equal(0, tab.SuspendCalls);
        Assert.True(tab.LowTarget);
        Assert.Equal(TabState.Hidden, tab.State);
    }

    [Fact]
    public async Task LongSuspendedTabIsDiscarded()
    {
        var tab = TabIdleFor(TimeSpan.FromMinutes(31), TabState.Suspended);
        await Manager().TickAsync([tab]);
        Assert.Equal(1, tab.DiscardCalls);
        Assert.Equal(TabState.Discarded, tab.State);
    }

    [Fact]
    public async Task SuspendedTabBelowDiscardThresholdIsKept()
    {
        var tab = TabIdleFor(TimeSpan.FromMinutes(20), TabState.Suspended);
        await Manager().TickAsync([tab]);
        Assert.Equal(0, tab.DiscardCalls);
    }

    [Fact]
    public async Task SuspendedAudioTabIsNotDiscarded()
    {
        var tab = TabIdleFor(TimeSpan.FromHours(2), TabState.Suspended);
        tab.IsPlayingAudio = true;
        await Manager().TickAsync([tab]);
        Assert.Equal(0, tab.DiscardCalls);
    }

    [Fact]
    public async Task FailedSuspendLeavesTabHiddenForRetry()
    {
        var tab = TabIdleFor(TimeSpan.FromMinutes(10));
        tab.SuspendResult = false; // e.g. DevTools open
        await Manager().TickAsync([tab]);
        Assert.Equal(TabState.Hidden, tab.State);

        await Manager().TickAsync([tab]);
        Assert.Equal(2, tab.SuspendCalls); // retried on the next tick
    }

    [Fact]
    public async Task CurrentTabMaySuspendButIsNeverDiscarded()
    {
        // The selected tab while the window is minimized/locked: freezes, keeps state.
        var tab = TabIdleFor(TimeSpan.FromHours(3));
        tab.IsCurrent = true;
        await Manager().TickAsync([tab], forceSuspend: true);
        Assert.Equal(TabState.Suspended, tab.State);

        await Manager().TickAsync([tab]);
        Assert.Equal(0, tab.DiscardCalls);
        Assert.Equal(TabState.Suspended, tab.State);
    }

    [Fact]
    public async Task ForceSuspendIgnoresIdleTimerButNeverDiscards()
    {
        var fresh = TabIdleFor(TimeSpan.Zero);
        var suspended = TabIdleFor(TimeSpan.FromMinutes(1), TabState.Suspended);
        await Manager().TickAsync([fresh, suspended], forceSuspend: true);
        Assert.Equal(1, fresh.SuspendCalls);
        Assert.Equal(0, suspended.DiscardCalls);
    }
}
