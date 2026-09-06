using Gergur.App;
using Xunit;

namespace Gergur.Tests;

public sealed class VpnTunnelTests
{
    [Fact]
    public void WireproxyConfigPointsAtTheProfileAndBindsLocally()
    {
        var config = VpnTunnel.BuildWireproxyConfig(@"C:\profiles\windscribe-us.conf", 24001);

        Assert.Contains(@"WGConfig = C:\profiles\windscribe-us.conf", config);
        Assert.Contains("[Socks5]", config);
        Assert.Contains("BindAddress = 127.0.0.1:24001", config);
    }

    [Fact]
    public void WireproxyConfigUsesTheConfiguredPort()
    {
        var config = VpnTunnel.BuildWireproxyConfig(@"C:\profiles\warp.conf", 25555);

        Assert.Contains("BindAddress = 127.0.0.1:25555", config);
        Assert.DoesNotContain("24001", config);
    }

    [Fact]
    public void ResolveProfileFallsBackWhenTheNameIsUnknown()
    {
        // With no profiles installed there is nothing to fall back to, which is the
        // signal StartAsync uses to refuse rather than launch a tunnel to nowhere.
        var profiles = VpnTunnel.ListProfiles();
        var resolved = VpnTunnel.ResolveProfile("no-such-profile");

        if (profiles.Count == 0)
            Assert.Null(resolved);
        else
            Assert.Equal(profiles[0].Name, resolved!.Name);
    }
}
