namespace Gergur.Tabs;

/// <summary>
/// What a window has decided about one page's request for the whole screen.
///
/// Taken once, when the page asks, and kept until the page has stopped being fullscreen. It
/// used to be worked out again every time anything redrew the window, so a refusal that
/// rested on "an agent clicked a moment ago" lapsed ten seconds later and the window took
/// the screen then, late and from nobody: the same report, delayed.
/// </summary>
public enum FullScreenVerdict
{
    /// <summary>Not asked, or the page has left. The next request is judged afresh.</summary>
    None,

    /// <summary>The window may take the screen for this page, and keeps it until the page leaves.</summary>
    Allowed,

    /// <summary>
    /// The window may not, and does not change its mind for this request. The page still
    /// believes it is fullscreen, inside the window's own area, and leaves the usual way.
    /// </summary>
    Refused,

    /// <summary>
    /// The person ended it. The window stays as it is until the page has really left, however
    /// long that takes or whether it ever agrees.
    /// </summary>
    Dismissed,
}

/// <summary>When a window takes the screen for a page, as a function of its facts alone.</summary>
internal static class FullScreenPolicy
{
    /// <summary>How long after an agent ran something in a page its fullscreen request is not the person's.</summary>
    internal static readonly TimeSpan AgentQuiet = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The verdict for a page, given the one it already has.
    ///
    /// A page that is not fullscreen has none. One that has a verdict keeps it: that is the
    /// point of it. Only a request not yet judged is looked at, and it is refused when:
    ///
    /// - the window is an agent's: nobody is in front of it, and a window that took the
    ///   whole monitor from behind the one in use was the report this exists for;
    /// - an agent has just run script in the page: script run through the engine counts as
    ///   the person's click for the page's purposes, so a "video fullscreen" asked for by an
    ///   agent's click would take the screen of someone who did not ask;
    /// - the window is not the one being used.
    /// </summary>
    internal static FullScreenVerdict Judge(
        FullScreenVerdict current, bool pageIsFullScreen, bool openedByAgent,
        bool agentActedRecently, bool windowIsActive)
    {
        if (!pageIsFullScreen)
            return FullScreenVerdict.None;
        if (current != FullScreenVerdict.None)
            return current;
        return openedByAgent || agentActedRecently || !windowIsActive
            ? FullScreenVerdict.Refused
            : FullScreenVerdict.Allowed;
    }

    /// <summary>
    /// Whether the window should be filling the screen for the page in front. Only an allowed
    /// request does, and a window already filling it stays so when it loses focus: alt-tabbing
    /// out of a film does not shrink it, and nor does an agent running something in the page
    /// afterwards, because the person started it.
    /// </summary>
    internal static bool Fills(FullScreenVerdict verdict, bool pageIsFullScreen)
        => pageIsFullScreen && verdict == FullScreenVerdict.Allowed;

    /// <summary>
    /// Whether to tell the person a request was just refused: once, when it is refused, and
    /// only in their own window. Without it a refusal is a video button that does nothing.
    /// </summary>
    internal static bool ShouldSayRefused(FullScreenVerdict before, FullScreenVerdict after, bool openedByAgent)
        => !openedByAgent && before == FullScreenVerdict.None && after == FullScreenVerdict.Refused;
}
