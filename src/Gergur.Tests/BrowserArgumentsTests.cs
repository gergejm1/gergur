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
        Assert.Contains("--disable-features=SpareRendererForSitePerProcess", args);
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
