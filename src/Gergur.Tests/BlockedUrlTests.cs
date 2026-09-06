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
