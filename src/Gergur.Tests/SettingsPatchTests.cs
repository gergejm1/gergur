using System.Text.Json;
using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Changing a setting used to mean closing the browser, editing a json file and starting
/// it again, which is why these exist: the patch has to be safe enough to use while the
/// browser is running and somebody is in it.
/// </summary>
public sealed class SettingsPatchTests
{
    // Kept rather than leaked: JsonDocument is pooled, and the element is read after this
    // returns, so a using at the call site would hand back a disposed one.
    private static readonly List<JsonDocument> Parsed = [];

    private static JsonElement Patch(string json)
    {
        var document = JsonDocument.Parse(json);
        lock (Parsed)
            Parsed.Add(document);
        return document.RootElement;
    }

    private static (string? Error, List<string> Applied, List<string> Restart, List<string> Unknown)
        Apply(Settings settings, string json)
    {
        List<string> applied = [], restart = [], unknown = [];
        string? error = SettingsPatch.Apply(settings, Patch(json), applied, restart, unknown);
        return (error, applied, restart, unknown);
    }

    [Fact]
    public void ASnapshotCarriesTheSettingsThatCanBeChanged()
    {
        var snapshot = SettingsPatch.Snapshot(new Settings());

        Assert.True(snapshot.ContainsKey(nameof(Settings.BlocklistEnabled)));
        Assert.True(snapshot.ContainsKey(nameof(Settings.VpnEnabled)));
        Assert.True(snapshot.ContainsKey(nameof(Settings.SearchUrlTemplate)));
        // Not the computed paths: those are not settings, and offering them would invite
        // an attempt to change one.
        Assert.False(snapshot.ContainsKey("SettingsPath"));
    }

    [Fact]
    public void AppliedValuesLandOnTheSettings()
    {
        var settings = new Settings { BlocklistEnabled = true, SuspendAfterMinutes = 3 };

        var result = Apply(settings, """{ "BlocklistEnabled": false, "SuspendAfterMinutes": 9 }""");

        Assert.Null(result.Error);
        Assert.False(settings.BlocklistEnabled);
        Assert.Equal(9, settings.SuspendAfterMinutes);
        Assert.Contains(nameof(Settings.BlocklistEnabled), result.Applied);
        Assert.Contains(nameof(Settings.SuspendAfterMinutes), result.Applied);
    }

    [Fact]
    public void SettingsTheEngineOnlyReadsAtStartupAreNamed()
    {
        // Applied, because the file is the record and the next start will honour it, but
        // said out loud: a setting that changed nothing and reported nothing is worse
        // than one that refused.
        var settings = new Settings();

        var result = Apply(settings, """{ "ProcessPerSite": false, "BlocklistEnabled": false }""");

        Assert.Contains(nameof(Settings.ProcessPerSite), result.Restart);
        Assert.DoesNotContain(nameof(Settings.BlocklistEnabled), result.Restart);
    }

    [Fact]
    public void AnUnknownNameIsReportedRatherThanSwallowed()
    {
        var settings = new Settings();

        var result = Apply(settings, """{ "NoSuchSetting": 1, "BlocklistEnabled": false }""");

        Assert.Null(result.Error);
        Assert.Contains("NoSuchSetting", result.Unknown);
        Assert.False(settings.BlocklistEnabled);   // the rest still applied
    }

    [Fact]
    public void AWrongTypeChangesNothingAtAll()
    {
        // Half a settings change is harder to reason about than none, so the caller gets
        // the request back rather than a browser in a state neither side expected.
        var settings = new Settings { BlocklistEnabled = true, SuspendAfterMinutes = 3 };

        var result = Apply(settings, """{ "BlocklistEnabled": false, "SuspendAfterMinutes": "soon" }""");

        Assert.NotNull(result.Error);
        Assert.Contains("SuspendAfterMinutes", result.Error);
        Assert.True(settings.BlocklistEnabled);
        Assert.Equal(3, settings.SuspendAfterMinutes);
        Assert.Empty(result.Applied);
    }

    [Theory]
    [InlineData("""{ "BlocklistEnabled": "yes" }""")]
    [InlineData("""{ "BlocklistEnabled": 1 }""")]
    [InlineData("""{ "SuspendAfterMinutes": 1.5 }""")]
    [InlineData("""{ "SearchUrlTemplate": 3 }""")]
    public void EachTypeIsCheckedRatherThanCoerced(string json)
    {
        var settings = new Settings();

        var result = Apply(settings, json);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void ApplyingChangesMemoryAndLeavesTheSettingsFileAlone()
    {
        // This test exists because the opposite happened. Apply used to end with
        // settings.Save(), Settings.Save writes to one fixed path under %LOCALAPPDATA%,
        // and so running this file replaced the real settings of whoever ran it: the vpn
        // profile, the blocklist and the phone drop's pairing key all went, and the key
        // is not recoverable from anywhere in the build. Persisting is the caller's now.
        string path = Settings.SettingsPath;
        bool existed = File.Exists(path);
        string? before = existed ? File.ReadAllText(path) : null;

        var settings = new Settings { BlocklistEnabled = true, SuspendAfterMinutes = 3 };
        var result = Apply(settings, """{ "BlocklistEnabled": false, "SuspendAfterMinutes": 9 }""");

        Assert.Null(result.Error);
        Assert.False(settings.BlocklistEnabled);          // applied, in memory
        Assert.Equal(9, settings.SuspendAfterMinutes);

        Assert.Equal(existed, File.Exists(path));
        if (existed)
            Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void ACredentialIsListedButNotGivenAway()
    {
        // GET /settings and the MCP tool behind it put their answer into a transcript.
        // DropKey is the pairing key for the phone drop, which is the only listener in
        // this browser that reaches past loopback, and CLAUDE.md records that losing it
        // already cost the user once. A caller needs to know it is set. Nobody needs to
        // be told what it is.
        var snapshot = SettingsPatch.Snapshot(new Settings { DropKey = "B6717A67D71A9FFBF9D2E284" });

        Assert.True(snapshot.ContainsKey(nameof(Settings.DropKey)));
        Assert.Equal(SettingsPatch.Hidden, snapshot[nameof(Settings.DropKey)]);
        Assert.DoesNotContain("B6717A67", string.Join(" ", snapshot.Values));
    }

    [Fact]
    public void AnUnsetCredentialSaysSoRatherThanLookingSet()
    {
        var snapshot = SettingsPatch.Snapshot(new Settings { DropKey = "" });

        Assert.Equal("", snapshot[nameof(Settings.DropKey)]);
    }

    [Fact]
    public void EverySettingOnTheRefuseListIsActuallyRefused()
    {
        // Over the whole set rather than a handful of names, because a hand-picked list
        // is one somebody can be dropped from silently: VpnBypassHosts went onto the
        // refuse list and every test stayed green with it removed again.
        foreach (string name in SettingsPatch.DialogOnly)
        {
            var settings = new Settings();
            var property = typeof(Settings).GetProperty(name);
            Assert.NotNull(property);

            string value = property.PropertyType == typeof(bool) ? "true"
                : property.PropertyType == typeof(int) ? "9999"
                : "\"anything\"";

            var result = Apply(settings, $$"""{ "{{name}}": {{value}} }""");

            Assert.NotNull(result.Error);
            Assert.Contains(name, result.Error);
            Assert.Empty(result.Applied);
        }
    }

    [Fact]
    public void TheRefuseListCoversEverythingThatOutlivesTheAgent()
    {
        // The rule the list is drawn on, written down where it can fail. Each of these
        // is persisted, in force at the next launch long after the session that asked is
        // gone, and invisible anywhere a person would look.
        string[] mustRefuse =
        [
            nameof(Settings.DropKey), nameof(Settings.DropEnabled), nameof(Settings.DropPort),
            nameof(Settings.AgentServerEnabled), nameof(Settings.AgentServerPort),
            nameof(Settings.ExtraBrowserArguments), nameof(Settings.DisableSiteIsolation),
            nameof(Settings.VpnEnabled), nameof(Settings.VpnProfile), nameof(Settings.VpnBypassHosts),
            // The port the engine's --proxy-server flag is built from: an agent that
            // points it at a port it holds receives every request this browser makes from
            // the next launch on, while the status bar still shows the vpn as on.
            nameof(Settings.VpnLocalPort),
            nameof(Settings.SearchUrlTemplate),
        ];

        foreach (string name in mustRefuse)
            Assert.Contains(name, SettingsPatch.DialogOnly);
    }

    [Theory]
    [InlineData(nameof(Settings.DropKey), "\"aaa\"")]
    [InlineData(nameof(Settings.DropEnabled), "true")]
    [InlineData(nameof(Settings.ExtraBrowserArguments), "\"--disable-web-security\"")]
    [InlineData(nameof(Settings.SearchUrlTemplate), "\"https://evil.example/?q={0}\"")]
    [InlineData(nameof(Settings.DisableSiteIsolation), "true")]
    public void SettingsThatMoveTheSecurityBoundaryAreRefused(string name, string value)
    {
        // Three of these decide who can reach this browser from off the machine, one is
        // the credential for doing so, and one passes arbitrary flags to the engine. An
        // agent that can change them can widen its own reach, so they belong to the
        // settings window, where a person is looking at it.
        var settings = new Settings();
        string before = settings.DropKey;

        var result = Apply(settings, $$"""{ "{{name}}": {{value}} }""");

        Assert.NotNull(result.Error);
        Assert.Contains(name, result.Error);
        Assert.Empty(result.Applied);
        Assert.Equal(before, settings.DropKey);
    }

    [Fact]
    public void ARefusedSettingTakesTheWholePatchWithIt()
    {
        // Half a change is harder to reason about than none, and a patch that quietly
        // dropped the refused half would report success for a request it did not carry out.
        var settings = new Settings { BlocklistEnabled = true };

        var result = Apply(settings, """{ "BlocklistEnabled": false, "DropKey": "aaa" }""");

        Assert.NotNull(result.Error);
        Assert.True(settings.BlocklistEnabled);
        Assert.Empty(result.Applied);
    }

    [Theory]
    [InlineData("""{ "SuspendAfterMinutes": -5 }""")]
    [InlineData("""{ "V8ScavengerMaxMb": -1 }""")]
    public void ANumberOutsideItsRangeIsRefused(string json)
    {
        // A type check alone let a port of 0 through, and it persisted: the agent API
        // locked itself out of its own listener until somebody hand-edited the json.
        var result = Apply(new Settings(), json);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Applied);
    }

    [Theory]
    [InlineData("""{ "TrackingPrevention": "Banana" }""")]
    [InlineData("""{ "PageTheme": "Puce" }""")]
    public void AStringOutsideItsAllowedSetIsRefused(string json)
    {
        var result = Apply(new Settings(), json);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Applied);
    }

    [Theory]
    [InlineData("""{ "TrackingPrevention": "Balanced" }""")]
    [InlineData("""{ "PageTheme": "Dark" }""")]
    [InlineData("""{ "SuspendAfterMinutes": 1 }""")]
    public void AValueInsideItsRangeStillApplies(string json)
    {
        // The refusals must not be so eager that the ordinary case stops working.
        var result = Apply(new Settings(), json);

        Assert.Null(result.Error);
        Assert.Single(result.Applied);
    }

    [Fact]
    public void ARefusedNameIsRefusedHoweverItIsSpelled()
    {
        // Names are matched without regard to case when they are looked up, so the refusal
        // has to be too, or "dropkey" walks straight past it.
        var result = Apply(new Settings(), """{ "dropkey": "aaa" }""");

        Assert.NotNull(result.Error);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void EveryCredentialShapedSettingIsHidden()
    {
        // The redaction list is hand written, so the next property called something Key
        // or Token would be handed out by GET /settings with every test still green.
        // This makes that a failing build instead of a leak into a transcript.
        var snapshot = SettingsPatch.Snapshot(new Settings());
        string[] credentialish = ["Key", "Token", "Password", "Secret"];

        foreach (var name in snapshot.Keys)
        {
            if (credentialish.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
            {
                Assert.True(
                    SettingsPatch.IsSecret(name),
                    $"{name} looks like a credential but GET /settings would hand it out");
            }
        }
    }

    [Fact]
    public void ATabCannotBeSetToDiscardBeforeItSleeps()
    {
        // Each value is fine on its own, which is why a per-field check cannot catch it.
        // Together they skip the cheap half of the memory policy entirely, and nothing
        // downstream would report it as anything but tabs behaving oddly.
        var settings = new Settings { SuspendAfterMinutes = 3, DiscardAfterMinutes = 15 };

        var result = Apply(settings, """{ "DiscardAfterMinutes": 2 }""");

        Assert.NotNull(result.Error);
        Assert.Contains(nameof(Settings.DiscardAfterMinutes), result.Error);
        Assert.Equal(15, settings.DiscardAfterMinutes);
    }

    [Fact]
    public void RaisingTheSleepTimerPastTheDiscardTimerIsRefusedToo()
    {
        // The same pair from the other side.
        var settings = new Settings { SuspendAfterMinutes = 3, DiscardAfterMinutes = 15 };

        var result = Apply(settings, """{ "SuspendAfterMinutes": 20 }""");

        Assert.NotNull(result.Error);
        Assert.Equal(3, settings.SuspendAfterMinutes);
    }

    [Fact]
    public void EqualTimersAreRefusedToo()
    {
        // The boundary the message names ("has to be more than"). With < instead of <=
        // this passes and a tab is discarded on the same tick it is suspended.
        var settings = new Settings { SuspendAfterMinutes = 3, DiscardAfterMinutes = 15 };

        var result = Apply(settings, """{ "SuspendAfterMinutes": 5, "DiscardAfterMinutes": 5 }""");

        Assert.NotNull(result.Error);
        Assert.Equal(3, settings.SuspendAfterMinutes);
    }

    [Fact]
    public void APatchAboutSomethingElseIsNotRefusedForAPairItDoesNotTouch()
    {
        // A settings file can already hold a bad pair, because the settings window does
        // not check it. Judging the pair on the whole resulting state made every unrelated
        // patch fail on it: turning the blocklist off came back as a complaint about
        // DiscardAfterMinutes.
        var settings = new Settings { SuspendAfterMinutes = 20, DiscardAfterMinutes = 5 };

        var result = Apply(settings, """{ "BlocklistEnabled": false }""");

        Assert.Null(result.Error);
        Assert.False(settings.BlocklistEnabled);
    }

    [Fact]
    public void AnUnknownNameIsStillReportedWhenTheTimersAreAlreadyWrong()
    {
        // The same state used to turn "that name means nothing" into a complaint about a
        // pair the caller never mentioned.
        var settings = new Settings { SuspendAfterMinutes = 20, DiscardAfterMinutes = 5 };

        var result = Apply(settings, """{ "NoSuchSetting": 1 }""");

        Assert.Null(result.Error);
        Assert.Contains("NoSuchSetting", result.Unknown);
    }

    [Fact]
    public void MovingBothTogetherIsFine()
    {
        // The pair has to be judged on what the patch would leave behind, not on what is
        // there now, or changing both at once would be refused for a state that never exists.
        var settings = new Settings { SuspendAfterMinutes = 3, DiscardAfterMinutes = 15 };

        var result = Apply(settings, """{ "SuspendAfterMinutes": 20, "DiscardAfterMinutes": 60 }""");

        Assert.Null(result.Error);
        Assert.Equal(20, settings.SuspendAfterMinutes);
        Assert.Equal(60, settings.DiscardAfterMinutes);
    }

    [Fact]
    public void AnEmptyPatchIsAcceptedAndChangesNothing()
    {
        var settings = new Settings { BlocklistEnabled = true };

        var result = Apply(settings, "{}");

        Assert.Null(result.Error);
        Assert.Empty(result.Applied);
        Assert.True(settings.BlocklistEnabled);
    }
}
