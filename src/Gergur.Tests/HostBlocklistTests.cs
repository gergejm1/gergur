using Gergur.Blocking;
using Xunit;

namespace Gergur.Tests;

public sealed class HostBlocklistTests
{
    [Fact]
    public void ParsesHostsFormatSkippingCommentsAndLoopbackNoise()
    {
        string[] lines =
        [
            "# StevenBlack header comment",
            "127.0.0.1 localhost",
            "255.255.255.255 broadcasthost",
            "::1 ip6-localhost",
            "0.0.0.0 ads.example.com",
            "0.0.0.0 tracker.example.net # trailing comment",
            "bare-entry.example.org",
            "",
            "   ",
        ];
        var hosts = HostBlocklist.ParseLines(lines).ToList();
        Assert.Equal(["ads.example.com", "tracker.example.net", "bare-entry.example.org"], hosts);
    }

    [Fact]
    public void BlocksExactHost()
    {
        var list = new HostBlocklist(["ads.example.com"]);
        Assert.True(list.IsBlocked("ads.example.com"));
        Assert.True(list.IsBlocked("ADS.EXAMPLE.COM"));
    }

    [Fact]
    public void BlocksSubdomainsOfListedDomain()
    {
        var list = new HostBlocklist(["tracker.com"]);
        Assert.True(list.IsBlocked("cdn.eu.tracker.com"));
    }

    [Fact]
    public void DoesNotBlockUnrelatedOrParentHosts()
    {
        var list = new HostBlocklist(["ads.example.com"]);
        Assert.False(list.IsBlocked("example.com"));       // parent of a listed host
        Assert.False(list.IsBlocked("notads.example.org"));
        Assert.False(list.IsBlocked("com"));
    }

    [Fact]
    public void NeverMatchesBareTld()
    {
        var list = new HostBlocklist(["evil.com"]);
        Assert.False(list.IsBlocked("com"));
        Assert.True(list.IsBlocked("a.evil.com"));
    }

    [Fact]
    public void EmptyListBlocksNothing()
    {
        Assert.False(HostBlocklist.Empty.IsBlocked("anything.example.com"));
    }
}
