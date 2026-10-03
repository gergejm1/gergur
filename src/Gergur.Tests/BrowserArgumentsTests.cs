using Gergur.App;
using Xunit;

namespace Gergur.Tests;

public sealed class BrowserArgumentsTests
{
    [Fact]
    public void DefaultsIncludeProcessPerSiteAndMemoryFeatures()
    {
        var args = BrowserEnvironment.BuildBrowserArguments(new Settings());
        Assert.Contains("--process-per-site", args);
        Assert.Contains("--enable-features=msWebView2SimulateMemoryPressureWhenInactive", args);
        Assert.Contains("SpareRendererForSitePerProcess", args);
        // FedCM must stay enabled: Google requires it for Sign in with Google, and
        // disabling it removes the API with no fallback.
        Assert.DoesNotContain("FedCm", args);
        Assert.DoesNotContain("--disable-site-isolation-trials", args);
    }

    [Fact]
    public void EachFeatureSwitchAppearsAtMostOnce()
    {
        var args = BrowserEnvironment.BuildBrowserArguments(new Settings());
        Assert.Single(args.Split("--enable-features=")[1..]);
        Assert.Single(args.Split("--disable-features=")[1..]);
    }

    [Fact]
    public void VpnAddsProxyAndDnsLeakProtection()
    {
        var args = BrowserEnvironment.BuildBrowserArguments(new Settings { VpnEnabled = true, VpnLocalPort = 24001 });
        Assert.Contains("--proxy-server=socks5://127.0.0.1:24001", args);
        Assert.Contains("--host-resolver-rules=", args);
        Assert.Contains("EXCLUDE 127.0.0.1", args);
    }

    [Fact]
    public void ATunnelThatDidNotComeUpIsNotAProxy()
    {
        // Chosen but down this run: pointing the engine at a dead proxy would fail every
        // request, which is what browsing without it avoids.
        var args = BrowserEnvironment.BuildBrowserArguments(
            new Settings { VpnEnabled = true, VpnLocalPort = 24001, VpnDownThisRun = true });
        Assert.DoesNotContain("--proxy-server", args);
    }

    [Fact]
    public void VpnOffAddsNoProxyFlags()
    {
        var args = BrowserEnvironment.BuildBrowserArguments(new Settings());
        Assert.DoesNotContain("--proxy-server", args);
    }

    [Fact]
    public void VpnBypassHostsSkipTunnelAndResolveLocally()
    {
        var args = BrowserEnvironment.BuildBrowserArguments(new Settings { VpnEnabled = true });
        // Google is in the default bypass list...
        Assert.Contains("--proxy-bypass-list=", args);
        Assert.Contains("*.google.com", args);
        // ...and excluded from the DNS-blackhole rule so it resolves locally.
        Assert.Contains("EXCLUDE google.com", args);
        Assert.Contains("MAP * ~NOTFOUND", args);
    }

    [Fact]
    public void BypassWildcardsSurviveIntoTheDnsExcludeRule()
    {
        // Without the wildcard, www.google.com skips the tunnel (proxy bypass matches
        // *.google.com) but still hits MAP * ~NOTFOUND, and fails HostNameNotResolved.
        var args = BrowserEnvironment.BuildBrowserArguments(new Settings { VpnEnabled = true });
        Assert.Contains("EXCLUDE *.google.com", args);
        Assert.Contains("EXCLUDE google.com", args);
        Assert.Contains("EXCLUDE *.gstatic.com", args);
    }

    [Fact]
    public void BypassHostsWithoutAWildcardAreExcludedVerbatim()
    {
        var args = BrowserEnvironment.BuildBrowserArguments(
            new Settings { VpnEnabled = true, VpnBypassHosts = "accounts.youtube.com" });
        Assert.Contains("EXCLUDE accounts.youtube.com", args);
        Assert.DoesNotContain("EXCLUDE *.accounts.youtube.com", args);
    }

    [Fact]
    public void TheHomeNetworkNeverGoesThroughTheTunnel()
    {
        // The streaming server on 192.168.0.29 timed out through the tunnel while the PC's own
        // 192.168.0.20 was the only address on the list. A tunnel cannot reach the LAN.
        foreach (string bypass in new[] { "", "192.168.0.20", "*.google.com" })
        {
            var args = BrowserEnvironment.BuildBrowserArguments(new Settings { VpnEnabled = true, VpnBypassHosts = bypass });
            string list = System.Text.RegularExpressions.Regex.Match(args, "--proxy-bypass-list=\"([^\"]*)\"").Groups[1].Value;
            foreach (string range in new[] { "192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12" })
                Assert.Contains(range, list.Split(';'));
            // The blackhole rule would fail a bypassed address before it could bypass.
            foreach (string pattern in new[] { "192.168.*", "10.*", "172.16.*", "172.20.*", "172.31.*" })
                Assert.Contains("EXCLUDE " + pattern, args);
            Assert.DoesNotContain("EXCLUDE 192.168.0.0/16", args);
        }
        // The ranges are the private ones and no others: 172.32 and 11.x are on the internet.
        var all = BrowserEnvironment.BuildBrowserArguments(new Settings { VpnEnabled = true });
        Assert.DoesNotContain("EXCLUDE 172.32.*", all);
        Assert.DoesNotContain("EXCLUDE 172.15.*", all);
        Assert.DoesNotContain("EXCLUDE 11.*", all);
        // And nothing is added when there is no tunnel.
        Assert.DoesNotContain("192.168", BrowserEnvironment.BuildBrowserArguments(new Settings()));
    }

    [Fact]
    public void TogglesRemoveTheirFlags()
    {
        var settings = new Settings
        {
            ProcessPerSite = false,
            DisableSpareRenderer = false,
            InactiveMemoryPressure = false,
        };
        var args = BrowserEnvironment.BuildBrowserArguments(settings);
        Assert.Equal("", args);
    }
}
