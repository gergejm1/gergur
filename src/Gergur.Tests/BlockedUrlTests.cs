using Gergur.Blocking;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Telling "we blocked this on purpose" apart from "this page is broken": the page
/// error indicator drops the first kind so it does not fire on every ad-heavy site.
/// </summary>
public sealed class BlockedUrlTests
{
    private static readonly HostBlocklist List = new(["t.indeed.com", "ads.indeed.com"]);

    [Theory]
    [InlineData("https://t.indeed.com/signals/gnav/log")]
    [InlineData("https://ads.indeed.com/pixel?x=1")]
    [InlineData("http://t.indeed.com/")]
    // A fully qualified name is the same host, and a list of the plain form does not
    // contain the dotted one. Without that, every entry on both downloaded lists was
    // reachable by appending a single character to the name.
    [InlineData("https://t.indeed.com./signals/gnav/log")]
    [InlineData("https://sub.ads.indeed.com./pixel")]
    public void BlocklistedHostsAreRecognisedFromTheirUrl(string url)
        => Assert.True(RequestBlocker.IsBlockedUrl(List, url));

    [Theory]
    [InlineData("https://www.indeed.com/jobs")]
    [InlineData("https://d3hbwax96mbv6t.cloudfront.net/app.js")]
    public void UnblockedHostsAreNot(string url)
        => Assert.False(RequestBlocker.IsBlockedUrl(List, url));

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("/relative/path.js")]
    public void UnparseableUrlsCountAsNotBlocked(string url)
        => Assert.False(RequestBlocker.IsBlockedUrl(List, url));
}

/// <summary>
/// The ad endpoints that a host list cannot express. YouTube serves its ad requests
/// from youtube.com and its ad media from the same googlevideo.com hosts as the video,
/// so blocking by host either does nothing or takes the video with it.
/// </summary>
public sealed class AdPathTests
{
    private static bool Blocked(string url)
        => Gergur.Blocking.RequestBlocker.IsAdPath(new Uri(url));

    [Theory]
    // The playback path. Blocking any of these does not remove an ad, it removes the
    // video, which is why none of them are on the list.
    [InlineData("https://www.youtube.com/youtubei/v1/player?key=x")]
    [InlineData("https://www.youtube.com/youtubei/v1/next?key=x")]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://rr3---sn-abc.googlevideo.com/videoplayback?expire=1&itag=299")]
    [InlineData("https://www.youtube.com/api/stats/watchtime?ns=yt")]
    [InlineData("https://www.youtube.com/api/stats/qoe?ns=yt")]
    [InlineData("https://i.ytimg.com/vi/abc/maxresdefault.jpg")]
    public void ThePlaybackPathIsLeftAlone(string url) => Assert.False(Blocked(url));

    [Theory]
    // The path only, never the query: a search for the word is not a request for an ad.
    [InlineData("https://www.youtube.com/results?search_query=/pagead/")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=/ptracking")]
    public void AQueryThatMentionsOneIsNotOne(string url) => Assert.False(Blocked(url));

    [Theory]
    // From the root of the path and at a segment boundary, never anywhere in it. These
    // are all real pages on sites with nothing to do with advertising, and a substring
    // test killed every one of them: the user gets a blank 403 that the issue counter
    // hides on purpose, so there is nothing on screen to say what happened.
    [InlineData("https://github.com/kmulvey/ptracking")]
    [InlineData("https://pypi.org/project/ptracking/")]
    [InlineData("https://en.wikipedia.org/wiki/Ptracking")]
    [InlineData("https://cdn.example.com/api/stats/adsense-report")]
    [InlineData("https://example.com/blog/pagead/how-ads-work")]
    [InlineData("https://example.com/downloads/get_midroll_info_guide.pdf")]
    public void AnAdWordInsideSomeOtherPathIsNotOne(string url) => Assert.False(Blocked(url));

    [Theory]
    // Every rule, in the shape it actually arrives in.
    [InlineData("https://www.youtube.com/pagead/viewthroughconversion/962985656/")]
    [InlineData("https://www.youtube.com/pagead/interaction/?ai=abc")]
    [InlineData("https://googleads.g.doubleclick.net/pagead/id")]
    [InlineData("https://www.youtube.com/ptracking?html5=1&video_id=abc")]
    [InlineData("https://www.youtube.com/api/stats/ads?ver=2&ns=yt")]
    [InlineData("https://www.youtube.com/get_midroll_info?video_id=abc")]
    [InlineData("https://www.youtube.com/youtubei/v1/ads?key=x")]
    [InlineData("https://rr3---sn-abc.googlevideo.com/ptracking?video_id=abc")]
    // A fully qualified name with its trailing dot is the same host.
    [InlineData("https://www.youtube.com./get_midroll_info?video_id=abc")]
    public void TheAdEndpointsGo(string url) => Assert.True(Blocked(url));

    [Theory]
    // A path is not a name only one site can have. "/api/stats/ads" is an ad endpoint
    // on youtube.com and an ordinary route on somebody's own dashboard, so each rule
    // is tied to the hosts that actually serve it.
    [InlineData("https://analytics.internal.corp/api/stats/ads")]
    [InlineData("https://metrics.example.com/api/stats/ads?range=7d")]
    [InlineData("https://example.com/ptracking")]
    [InlineData("https://example.com/pagead/banner")]
    [InlineData("https://example.com/get_midroll_info")]
    // And the suffix has to be a real domain boundary, not the end of a longer name.
    [InlineData("https://notyoutube.com/get_midroll_info")]
    [InlineData("https://evil-youtube.com/api/stats/ads")]
    // And on a host that is on the list for some other rule. Each rule carries its own
    // hosts, so google.com serving /pagead does not make it serve /get_midroll_info,
    // and googlevideo.com carrying /ptracking does not make it carry /pagead.
    [InlineData("https://www.google.com/get_midroll_info")]
    [InlineData("https://rr3---sn-abc.googlevideo.com/pagead/id")]
    public void TheSameEndpointOnSomebodyElsesSiteIsLeftAlone(string url) => Assert.False(Blocked(url));

    [Theory]
    // On youtube.com itself, where the host check cannot help and the path rules are on
    // their own. A rule has to be the whole of a segment, from the root: a channel or a
    // vanity url that merely begins with one of these words is a page, not an endpoint.
    [InlineData("https://www.youtube.com/ptrackingfan")]
    [InlineData("https://www.youtube.com/@ptracking")]
    [InlineData("https://www.youtube.com/c/pagead/videos")]
    // Deliberately sized so "/pagead" sits at a segment boundary further along the
    // path: this is the case that pins the match to the root rather than anywhere.
    [InlineData("https://www.youtube.com/abcdef/pagead")]
    [InlineData("https://www.youtube.com/watch?v=get_midroll_info")]
    [InlineData("https://www.youtube.com/youtubei/v1/adsomething")]
    public void AYouTubePageThatMerelyStartsLikeOneIsLeftAlone(string url) => Assert.False(Blocked(url));

    [Fact]
    public void TheBlockerAppliesThemEvenWithNoHostListLoaded()
    {
        // The host list is downloaded on demand and may never have been, and these
        // rules are the only thing standing between the player and its ad requests.
        // Deliberately not constructing a RequestBlocker: that reads whatever blocklist
        // happens to be on this machine, which makes the test slow and its result
        // depend on the developer running it.
        Assert.True(Gergur.Blocking.RequestBlocker.IsBlockedUrl(
            Gergur.Blocking.HostBlocklist.Empty, "https://www.youtube.com/pagead/interaction/"));
        Assert.False(Gergur.Blocking.RequestBlocker.IsBlockedUrl(
            Gergur.Blocking.HostBlocklist.Empty, "https://www.youtube.com/youtubei/v1/player"));
    }

    [Fact]
    public void TurningBlockingOffTurnsOffThePathRulesToo()
    {
        // They are built in rather than downloaded, so it would be easy for them to
        // keep firing after the user has switched blocking off, which is not what the
        // switch says it does. This pins the reporting side, IsBlockedUrl; the request
        // handler has its own "if (!Enabled) return" that only a WebView2 can reach.
        const string adUrl = "https://www.youtube.com/pagead/interaction/";
        var off = new Gergur.Blocking.RequestBlocker(enabled: false, Gergur.Blocking.HostBlocklist.Empty);
        var on = new Gergur.Blocking.RequestBlocker(enabled: true, Gergur.Blocking.HostBlocklist.Empty);

        Assert.False(off.IsBlockedUrl(adUrl));
        Assert.True(on.IsBlockedUrl(adUrl));
    }

    [Fact]
    public void AnEmptyHostListIsStillAnEmptyHostList()
    {
        // The built-in path rules count towards what the menu shows, and must not count
        // towards whether a host list has been downloaded: that decision is what makes
        // the browser fetch one on first run, and folding the two together would mean
        // it quietly never did.
        var noHosts = new Gergur.Blocking.RequestBlocker(enabled: true, Gergur.Blocking.HostBlocklist.Empty);
        var withHosts = new Gergur.Blocking.RequestBlocker(
            enabled: true, new Gergur.Blocking.HostBlocklist(["ads.example.com"]));

        Assert.False(noHosts.HasHostList);
        Assert.True(withHosts.HasHostList);
        // Relative, so adding a path rule is not a failing test with a number in it.
        Assert.True(noHosts.RuleCount > 0, "the built-in path rules are not counted");
        Assert.Equal(noHosts.RuleCount + 1, withHosts.RuleCount);
    }
}
