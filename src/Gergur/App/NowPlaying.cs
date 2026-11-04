using Gergur.Tabs;

namespace Gergur.App;

/// <summary>A tab as <see cref="NowPlaying"/> weighs it.</summary>
/// <param name="Key">The tab itself.</param>
/// <param name="Audible">Making sound now, by the engine's own measure.</param>
/// <param name="StartedUtc">When it last began making sound.</param>
/// <param name="LastHeardUtc">When it was last heard.</param>
/// <param name="Host">The site it is on now.</param>
/// <param name="SoundHost">The site it was on when it began making sound.</param>
/// <param name="TuckedAway">Off the strip, so the control is the only way to it.</param>
internal sealed record SoundCandidate(object Key, bool Audible, DateTime? StartedUtc, DateTime? LastHeardUtc, string Host, string? SoundHost, bool TuckedAway = false);

/// <summary>
/// Which tab the toolbar's now-playing control drives, across every window the person has.
///
/// The tab making sound, and of two the one that began last: what was just put on is what
/// the person means. Once nothing is making sound, the last one chosen stays chosen, so a
/// song paused from the panel can be played again from it, but only while it is on the site
/// it played on and for <see cref="KeptSilent"/>; a tab gone elsewhere, or long quiet, is
/// not "now playing" any more. A tab the person tucked away is kept however long it is quiet
/// and wherever it has gone, and the control goes to one of those once a paused tab on the
/// strip has been quiet for <see cref="KeptBesideHidden"/>, since it is the only way back to
/// them: that tab can still be reached on the strip. Closing one brings up the next at once, and
/// showing one does once it has been quiet that long. An agent's windows are never asked.
/// </summary>
internal sealed class NowPlaying
{
    /// <summary>How long a paused tab stays the one the control drives.</summary>
    internal static readonly TimeSpan KeptSilent = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The same, with a hidden tab waiting for the control. Long enough to pause a song from the
    /// panel and play it again from there; short enough that the hidden one is not out of reach
    /// for long.
    /// </summary>
    internal static readonly TimeSpan KeptBesideHidden = TimeSpan.FromMinutes(5);

    private object? _last;

    /// <summary>The tab chosen now, remembered for the next call. UI thread.</summary>
    internal Tab? Current(IEnumerable<Tab> personTabs, DateTime now)
    {
        var candidates = personTabs.Select(CandidateOf).ToList();
        _last = Pick(candidates, _last, now);
        return _last as Tab;
    }

    internal static SoundCandidate CandidateOf(Tab tab)
        => new(tab, tab.IsPlayingAudio, tab.SoundStartedUtc, tab.SoundLastHeardUtc, HostOf(tab.CommittedUrl), tab.SoundHost, tab.TuckedAway);

    /// <summary>The choice itself, apart from the tabs. See the class summary.</summary>
    internal static object? Pick(IReadOnlyList<SoundCandidate> candidates, object? last, DateTime now)
    {
        SoundCandidate? loudest = null;
        foreach (var c in candidates)
        {
            if (c.Audible && (loudest is null || (c.StartedUtc ?? DateTime.MinValue) > (loudest.StartedUtc ?? DateTime.MinValue)))
                loudest = c;
        }
        if (loudest is not null)
            return loudest.Key;
        var kept = last is null ? null : candidates.FirstOrDefault(c => ReferenceEquals(c.Key, last));
        if (kept is { TuckedAway: true })
            return kept.Key;
        var tucked = candidates.FirstOrDefault(c => c.TuckedAway);
        if (kept is { LastHeardUtc: { } heard, SoundHost: { } host } && now - heard <= (tucked is null ? KeptSilent : KeptBesideHidden)
            && string.Equals(kept.Host, host, StringComparison.OrdinalIgnoreCase))
            return kept.Key;
        return tucked?.Key;
    }

    /// <summary>The site of an address, or "" when it has none.</summary>
    internal static string HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.Host
            : "";

    /// <summary>The site for the panel: its host without "www.".</summary>
    internal static string SiteOf(string? url)
    {
        string host = HostOf(url);
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }
}
