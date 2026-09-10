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
            // A fully qualified line. Stored with its dot it would match nothing, since
            // the lookup strips the dot off the host it is asked about.
            "0.0.0.0 fqdn.example.com.",
            "",
            "   ",
        ];
        var hosts = HostBlocklist.ParseLines(lines).ToList();
        Assert.Equal(
            ["ads.example.com", "tracker.example.net", "bare-entry.example.org", "fqdn.example.com"],
            hosts);
    }

    [Fact]
    public void AFullyQualifiedNameMatchesEitherWayRound()
    {
        // The name and the list can each carry the trailing dot or not, and all four
        // pairings are the same host. Normalising only one side left half of them open.
        var plain = new HostBlocklist(["ads.example.com"]);
        var dotted = new HostBlocklist(HostBlocklist.ParseLines(["ads.example.com."]));

        Assert.True(plain.IsBlocked("ads.example.com"));
        Assert.True(plain.IsBlocked("ads.example.com."));
        Assert.True(dotted.IsBlocked("ads.example.com"));
        Assert.True(dotted.IsBlocked("ads.example.com."));
        Assert.False(plain.IsBlocked("notads.example.com"));
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

    [Fact]
    public void EasyListExtractionTakesOnlySimpleHostRules()
    {
        string[] lines =
        [
            "[Adblock Plus 2.0]",
            "! comment",
            "||ads.example.com^",                    // keep
            "||tracker.example.net^$third-party",    // option -> skip
            "||example.org/banners/*",               // path -> skip
            "@@||goodsite.com^",                     // exception -> skip
            "||sub.domain.co.uk^",                   // keep
            "##.ad-container",                       // cosmetic -> skip
            "||no-tld^",                             // not a domain -> skip
        ];
        var hosts = Blocking.RequestBlocker.ExtractSimpleHostRules(lines).ToList();
        Assert.Equal(["ads.example.com", "sub.domain.co.uk"], hosts);
    }
}
