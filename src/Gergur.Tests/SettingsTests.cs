using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The settings window edits a clone and copies back on Save, so a bug in the clone or
/// the diff either loses edits silently or fails to prompt for a needed restart.
/// </summary>
public sealed class SettingsTests
{
    [Fact]
    public void CloneIsDetachedFromTheOriginal()
    {
        var live = new Settings { SuspendAfterMinutes = 3, SearchUrlTemplate = "https://a/{0}" };
        var working = live.Clone();

        working.SuspendAfterMinutes = 99;
        working.SearchUrlTemplate = "https://b/{0}";

        Assert.Equal(3, live.SuspendAfterMinutes);
        Assert.Equal("https://a/{0}", live.SearchUrlTemplate);
    }

    [Fact]
    public void CopyFromBringsEveryEditAcross()
    {
        var live = new Settings();
        var working = live.Clone();
        working.SuspendAfterMinutes = 42;
        working.BlocklistEnabled = !live.BlocklistEnabled;
        working.VpnProfile = "Windscribe US";

        live.CopyFrom(working);

        Assert.Equal(42, live.SuspendAfterMinutes);
        Assert.Equal(working.BlocklistEnabled, live.BlocklistEnabled);
        Assert.Equal("Windscribe US", live.VpnProfile);
    }

    [Fact]
    public void DifferencesFromNamesOnlyWhatChanged()
    {
        var live = new Settings();
        var working = live.Clone();
        working.DiscardAfterMinutes = 30;

        var changed = live.DifferencesFrom(working).ToList();

        Assert.Equal([nameof(Settings.DiscardAfterMinutes)], changed);
    }

    [Fact]
    public void AnIdenticalCloneReportsNoDifferences()
        => Assert.Empty(new Settings().DifferencesFrom(new Settings().Clone()));

    [Theory]
    [InlineData(nameof(Settings.VpnEnabled))]
    [InlineData(nameof(Settings.ProcessPerSite))]
    [InlineData(nameof(Settings.ExtraBrowserArguments))]
    [InlineData(nameof(Settings.AgentServerPort))]
    public void EngineLevelSettingsAreFlaggedAsNeedingARestart(string name)
        => Assert.Contains(name, Settings.RestartRequired);

    [Theory]
    [InlineData(nameof(Settings.SuspendAfterMinutes))]
    [InlineData(nameof(Settings.BlocklistEnabled))]
    [InlineData(nameof(Settings.SearchUrlTemplate))]
    public void SettingsThatApplyWithoutARestartAreNot(string name)
        => Assert.DoesNotContain(name, Settings.RestartRequired);

    [Fact]
    public void RestartRequiredOnlyNamesRealProperties()
    {
        var real = typeof(Settings).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.All(Settings.RestartRequired, name => Assert.Contains(name, real));
    }
}
