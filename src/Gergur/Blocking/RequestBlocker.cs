using Gergur.App;
using Microsoft.Web.WebView2.Core;

namespace Gergur.Blocking;

/// <summary>
/// Layer-2 blocking (layer 1 is the engine's Strict tracking prevention):
/// kills requests to blocklisted hosts before they leave the machine.
/// </summary>
public sealed class RequestBlocker
{
    private const string StevenBlackHostsUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";
    private const string EasyListUrl = "https://easylist.to/easylist/easylist.txt";

    private HostBlocklist _list;
    private long _blockedCount;

    public bool Enabled { get; set; }
    public string BlocklistPath { get; } = Path.Combine(Settings.DataDir, "blocklist.txt");
    public string EasyListHostsPath { get; } = Path.Combine(Settings.DataDir, "easylist-hosts.txt");
    public long BlockedCount => Interlocked.Read(ref _blockedCount);
    /// <summary>Everything blocking, for the menu. The path rules are built in and
    /// stand on their own, so this is never zero while blocking is on.</summary>
    public int RuleCount => _list.Count + AdPaths.Length;

    /// <summary>Whether the downloaded host list is here yet. Separate from
    /// <see cref="RuleCount"/> because it is what decides whether to go and fetch one,
    /// and the built-in path rules must not make an empty list look like a full one.
    /// </summary>
    public bool HasHostList => _list.Count > 0;

    public RequestBlocker(bool enabled)
    {
        Enabled = enabled;
        _list = LoadLists();
    }

    /// <summary>Over a list handed in rather than whatever is on this machine's disk,
    /// so a test can say what the blocklist is instead of inheriting the developer's.
    /// </summary>
    internal RequestBlocker(bool enabled, HostBlocklist list)
    {
        ArgumentNullException.ThrowIfNull(list);
        Enabled = enabled;
        _list = list;
    }

    private HostBlocklist LoadLists()
    {
        var lines = Enumerable.Empty<string>();
        if (File.Exists(BlocklistPath))
            lines = lines.Concat(File.ReadLines(BlocklistPath));
        if (File.Exists(EasyListHostsPath))
            lines = lines.Concat(File.ReadLines(EasyListHostsPath));
        return new HostBlocklist(HostBlocklist.ParseLines(lines));
    }

    public void Attach(CoreWebView2 core)
    {
        // Document sources only: worker-sourced filters fan out to every WebView2
        // in the environment, so each worker request would be handled N times.
        core.AddWebResourceRequestedFilter(
            "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private static readonly string[] YouTubeHosts = ["youtube.com", "youtube-nocookie.com", "googlevideo.com"];

    /// <summary>
    /// Ad and ad-telemetry endpoints that sit on hosts we have to allow, so a host list
    /// cannot express them. YouTube serves its ad requests from youtube.com and its ad
    /// media from the same googlevideo.com hosts as the video itself, which is why
    /// blocking by host does nothing there and the page script does the real work.
    ///
    /// Every entry is a path, matched against the path only, never the query, and each
    /// one is an endpoint whose entire purpose is advertising. Nothing here is on the
    /// playback path: /youtubei/v1/player and the media hosts are deliberately absent,
    /// because blocking those does not remove an ad, it removes the video.
    ///
    /// Each is paired with the hosts it belongs to, because a path is not a name that
    /// only one site can have. "/api/stats/ads" reads like an ad endpoint and is one on
    /// youtube.com, but it is also a perfectly ordinary route for a self-hosted
    /// dashboard, and killing that on every host on the strength of the path alone is
    /// how a blocker breaks a site it has no business touching.
    ///
    /// Checked from a watch page: all five answer 403 in about 9ms, which is this
    /// handler rather than the network, while /api/stats/watchtime and
    /// /youtubei/v1/player both reach YouTube. Worth knowing that with the payload
    /// pruning working the player never asks for any of the five, so these earn their
    /// place only when that stops working, which is the day it will matter. Checked for
    /// that day too, by turning the pruning off and leaving these on: two of two loads
    /// that were served an ad still played the video, so a killed ad request is not one
    /// of the things this player waits forever for.
    /// </summary>
    private static readonly (string Path, string[] Hosts)[] AdPaths =
    [
        // Google's ad delivery, on the hosts that serve it.
        ("/pagead", ["doubleclick.net", "googlesyndication.com", "googleadservices.com",
                     "google.com", "youtube.com"]),
        // YouTube's own ad plumbing.
        ("/ptracking", YouTubeHosts),
        ("/api/stats/ads", YouTubeHosts),
        ("/get_midroll_info", YouTubeHosts),
        ("/youtubei/v1/ads", YouTubeHosts),
    ];

    /// <summary>Every host any rule could apply to, for the one check that decides
    /// whether a request is worth looking at further.</summary>
    private static readonly string[] AllAdHosts =
        AdPaths.SelectMany(rule => rule.Hosts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Whether this url is one the blocklist would kill. Lets a failed subresource be
    /// told apart from a page defect: we stopped it on purpose, so it is not an issue.
    /// </summary>
    public bool IsBlockedUrl(string url)
        => Enabled && IsBlockedUrl(_list, url);

    internal static bool IsBlockedUrl(HostBlocklist list, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return list.IsBlocked(uri.Host) || IsAdPath(uri);
    }

    /// <summary>
    /// Whether the path is one of <see cref="AdPaths"/>. Path only: a video id or a
    /// search term that happens to contain "/pagead" is not a reason to kill a request.
    ///
    /// Every entry is matched from the root of the path, only at a segment boundary,
    /// and only on the hosts it belongs to, because a substring test anywhere in the
    /// path reaches far past the endpoints it was meant for. "/ptracking" alone matched
    /// github.com/someone/ptracking and wikipedia.org/wiki/Ptracking, and
    /// "/api/stats/ads" matched an "adsense-report" on any CDN. Those arrive as a blank
    /// 403 the issue counter deliberately hides, so a page killed this way looks broken
    /// with nothing on screen to say why.
    /// </summary>
    internal static bool IsAdPath(Uri uri)
    {
        // Host first, against one flat list. AbsolutePath allocates, and this runs for
        // every request of every tab, so the overwhelming majority of traffic should
        // not pay for a string that was never going to match anything.
        string host = uri.Host;
        if (!MatchesHost(host, AllAdHosts))
            return false;

        string path = uri.AbsolutePath;
        foreach (var (ad, hosts) in AdPaths)
        {
            if (!path.StartsWith(ad, StringComparison.OrdinalIgnoreCase))
                continue;
            if (path.Length != ad.Length && path[ad.Length] is not ('/' or '.'))
                continue;
            if (MatchesHost(host, hosts))
                return true;
        }
        return false;
    }

    /// <summary>The host, or a subdomain of it. "notyoutube.com" is not youtube.com.</summary>
    private static bool MatchesHost(string host, string[] hosts)
    {
        // A fully qualified name may carry a trailing dot, and "www.youtube.com." is
        // the same host as "www.youtube.com". Without this these rules fail open on it.
        if (host.EndsWith('.'))
            host = host[..^1];

        foreach (string allowed in hosts)
        {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.Length > allowed.Length
                && host[host.Length - allowed.Length - 1] == '.'
                && host.EndsWith(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        // Hot path - fires synchronously for every request of every tab. Stay cheap.
        if (!Enabled)
            return;
        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            && (_list.IsBlocked(uri.Host) || IsAdPath(uri)))
        {
            var core = (CoreWebView2)sender!;
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            Interlocked.Increment(ref _blockedCount);
        }
    }

    /// <summary>Downloads/refreshes the StevenBlack hosts list plus EasyList's
    /// simple domain rules. Returns the combined rule count.</summary>
    public async Task<int> UpdateBlocklistAsync()
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        Directory.CreateDirectory(Settings.DataDir);

        string hosts = await http.GetStringAsync(StevenBlackHostsUrl);
        await File.WriteAllTextAsync(BlocklistPath, hosts);
        try
        {
            string easyList = await http.GetStringAsync(EasyListUrl);
            var easyHosts = ExtractSimpleHostRules(easyList.Split('\n'));
            await File.WriteAllLinesAsync(EasyListHostsPath, easyHosts);
        }
        catch
        {
            // EasyList is a bonus layer; the hosts list alone is still a valid update.
        }
        _list = LoadLists();
        return _list.Count;
    }

    /// <summary>
    /// Takes only EasyList rules of the form "||domain.tld^" with no options,
    /// paths, wildcards, or exceptions - the subset that maps safely onto
    /// host-level blocking without needing the full ABP rule engine.
    /// </summary>
    internal static IEnumerable<string> ExtractSimpleHostRules(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length < 6 || !line.StartsWith("||", StringComparison.Ordinal) || !line.EndsWith("^", StringComparison.Ordinal))
                continue;
            var host = line[2..^1];
            if (host.Contains('/') || host.Contains('*') || host.Contains('$') || host.Contains('^') || !host.Contains('.'))
                continue;
            if (Uri.CheckHostName(host) == UriHostNameType.Dns)
                yield return host;
        }
    }
}
