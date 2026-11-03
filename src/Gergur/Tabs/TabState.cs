namespace Gergur.Tabs;

public enum TabState
{
    Active,    // visible, focused-ish, fully alive
    Hidden,    // background, still live and consuming renderer memory
    Suspended, // frozen via TrySuspendAsync; renderer memory largely released
    Discarded, // no WebView2 at all; only a snapshot (url/title) remains
}

/// <summary>
/// What a tab offers for being frozen or discarded. Nothing sleeps a tab on a timer any
/// more; only a page that stopped answering is discarded, and the suspend half is kept
/// inert rather than deleted.
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
