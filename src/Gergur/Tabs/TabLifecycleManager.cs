namespace Gergur.Tabs;

public enum TabState
{
    Active,    // visible, focused-ish, fully alive
    Hidden,    // background, still live and consuming renderer memory
    Suspended, // frozen via TrySuspendAsync; renderer memory largely released
    Discarded, // no WebView2 at all; only a snapshot (url/title) remains
}

/// <summary>
/// The surface the lifecycle policy needs from a tab. Tab implements this over
/// WebView2; tests implement it with fakes. Contract: TrySuspendAsync must
/// transition the tab to Suspended when it returns true.
/// </summary>
public interface ITabHandle
{
    TabState State { get; }
    DateTime LastActiveUtc { get; }
    bool IsPlayingAudio { get; }
    /// <summary>True for the selected tab, even while it is hidden because the
    /// window is minimized or the session is locked. It may suspend (freeze in
    /// place) but must never be discarded, so its exact state survives.</summary>
    bool IsCurrent { get; }
    Task<bool> TrySuspendAsync();
    void Discard();
    void SetLowMemoryTarget(bool low);
}

/// <summary>
/// The policy engine: decides which background tabs to suspend or discard.
/// Driven by an external timer; owns no timer, no UI, no WebView2 reference.
/// </summary>
public sealed class TabLifecycleManager
{
    private readonly Func<DateTime> _utcNow;

    public TimeSpan SuspendAfter { get; }
    public TimeSpan DiscardAfter { get; }

    public TabLifecycleManager(TimeSpan suspendAfter, TimeSpan discardAfter, Func<DateTime>? utcNow = null)
    {
        SuspendAfter = suspendAfter;
        DiscardAfter = discardAfter;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// One policy pass. <paramref name="forceSuspend"/> ignores the suspend timer
    /// ("sleep everything now") but never force-discards - discard loses page state.
    /// </summary>
    public async Task TickAsync(IReadOnlyList<ITabHandle> tabs, bool forceSuspend = false)
    {
        var now = _utcNow();
        foreach (var tab in tabs)
        {
            var idle = now - tab.LastActiveUtc;
            switch (tab.State)
            {
                case TabState.Hidden when forceSuspend || idle >= SuspendAfter:
                    if (tab.IsPlayingAudio)
                        tab.SetLowMemoryTarget(true); // audible tabs can't freeze; at least shrink them
                    else
                        await tab.TrySuspendAsync();  // false (DevTools open, etc.) → retry next tick
                    break;

                case TabState.Suspended when idle >= DiscardAfter && !tab.IsPlayingAudio && !tab.IsCurrent:
                    tab.Discard();
                    break;
            }
        }
    }
}
