namespace Gergur.Blocking;

/// <summary>
/// Sites the person has relaxed blocking on: the request blocker lets every request a page
/// on one of them makes go through, and the page script stops taking ads out of what the
/// site sends, while still muting, speeding up and skipping any video ad that plays.
///
/// Made for YouTube, which refuses to play anything for a session it has decided is
/// blocking ads (playabilityStatus ERROR, with the "Ad blockers violate YouTube's Terms of
/// Service" screen in place of the video). Nothing on the page can undo that verdict; a
/// browser that lets the ads arrive and races through them might, over time.
///
/// Stored in <see cref="App.Settings.RelaxedSites"/> as names separated by semicolons. A
/// name covers itself and everything under it, so "youtube.com" covers www.youtube.com
/// and music.youtube.com.
/// </summary>
public static class RelaxedSites
{
    /// <summary>The names in a stored list, cleaned: lower case, no "www.", no trailing dot, each once.</summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var result = new List<string>();
        foreach (string raw in text.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string site = Clean(raw);
            if (site.Length > 0 && !result.Contains(site))
                result.Add(site);
        }
        return result;
    }

    /// <summary>
    /// The site name for a host or a single entry: lower case, without a leading "www." or
    /// a trailing dot. Empty for anything that is not a plain DNS name with a dot in it, so
    /// a wildcard or an address cannot relax more than the one site it reads as.
    /// </summary>
    public static string Clean(string host)
    {
        string h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal))
            h = h[4..];
        return h.Contains('.') && Uri.CheckHostName(h) == UriHostNameType.Dns ? h : "";
    }

    /// <summary>The site a page address belongs to, for the menu; empty for anything but a web page.</summary>
    public static string SiteOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? Clean(uri.Host)
            : "";

    /// <summary>Whether <paramref name="host"/> is one of the sites or under one. "notyoutube.com" is not youtube.com.</summary>
    public static bool Covers(IReadOnlyList<string> sites, string? host)
    {
        if (sites.Count == 0 || string.IsNullOrEmpty(host))
            return false;
        string h = host.TrimEnd('.');
        foreach (string site in sites)
        {
            if (h.Equals(site, StringComparison.OrdinalIgnoreCase))
                return true;
            if (h.Length > site.Length
                && h[h.Length - site.Length - 1] == '.'
                && h.EndsWith(site, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The stored list with <paramref name="site"/> added or taken out.</summary>
    public static string With(string? text, string site, bool relaxed)
    {
        var sites = Parse(text).ToList();
        string clean = Clean(site);
        if (clean.Length == 0)
            return string.Join(";", sites);
        sites.Remove(clean);
        if (relaxed)
            sites.Add(clean);
        return string.Join(";", sites);
    }
}
