using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// How settings.json is written and what happens when it cannot be read.
///
/// This file is the only home of the phone drop's pairing key, which is recoverable from
/// nothing in this build, and it has already been destroyed once here. The write was a
/// bare WriteAllText until recently, which truncates first and fills in after, so a crash
/// or a full disk in between left something that would not parse; Load then fell back to
/// defaults and wrote them straight over it.
///
/// So these run against a temp directory. The previous version of this code went in with
/// no test and would have had its first execution against the real file.
/// </summary>
public sealed class SettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "gergur-settings-" + Guid.NewGuid().ToString("n"));

    public SettingsFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void AFirstEverLaunchCreatesItsDirectory()
    {
        // The whole point, and the case every other test here hid: they make the
        // directory in the constructor. On a machine with no %LOCALAPPDATA%Gergur yet,
        // Settings.Load is the first thing the main window does, nothing has created that
        // folder, and there is no unhandled exception handler above it. A path seam that
        // dropped the CreateDirectory the old Save did meant a fresh install threw
        // DirectoryNotFoundException and never opened a window.
        string fresh = Path.Combine(_dir, "never-made-yet", "settings.json");
        Assert.False(Directory.Exists(Path.GetDirectoryName(fresh)));

        var settings = Settings.Load(fresh);

        Assert.True(settings.BlocklistEnabled);
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void WritingIntoADirectoryThatIsNotThereYetMakesIt()
    {
        string fresh = Path.Combine(_dir, "also-not-made", "settings.json");

        Settings.WriteAtomically(fresh, """{"a":1}""");

        Assert.Equal("""{"a":1}""", File.ReadAllText(fresh));
    }

    [Fact]
    public void WritingWhereNothingExistsLeavesTheFile()
    {
        string path = Path_("settings.json");

        Settings.WriteAtomically(path, """{"a":1}""");

        Assert.Equal("""{"a":1}""", File.ReadAllText(path));
    }

    [Fact]
    public void WritingOverSomethingReplacesIt()
    {
        string path = Path_("settings.json");
        File.WriteAllText(path, """{"old":true}""");

        Settings.WriteAtomically(path, """{"new":true}""");

        Assert.Equal("""{"new":true}""", File.ReadAllText(path));
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehind()
    {
        // It sits next to the real one in the user's data directory, so leaving it is
        // both litter and a second copy of everything in settings.json.
        string path = Path_("settings.json");
        Settings.WriteAtomically(path, """{"a":1}""");
        Settings.WriteAtomically(path, """{"a":2}""");

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AFailedWriteLeavesTheExistingFileWhole()
    {
        // The case the temp file exists for, and it has to fail on the very path whose
        // file must survive: an earlier version of this test failed a write to a
        // different path entirely, so it passed just as happily against a version that
        // truncated the target first, which is the thing being guarded against.
        //
        // A directory sitting where the temp file goes makes the temp write fail while
        // the real file is right there to be damaged.
        string path = Path_("settings.json");
        File.WriteAllText(path, """{"dropKey":"B6717A67D71A9FFBF9D2E284"}""");
        string before = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsAny<Exception>(() => Settings.WriteAtomically(path, """{"new":true}"""));

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void AnUnreadableFileIsKeptRatherThanOverwritten()
    {
        string path = Path_("settings.json");
        File.WriteAllText(path, "this is not json, but it is somebody's settings");

        string? kept = Settings.SetAside(path);

        Assert.NotNull(kept);
        Assert.False(File.Exists(path));
        Assert.Equal("this is not json, but it is somebody's settings", File.ReadAllText(kept));
    }

    [Fact]
    public void TwoFailuresInTheSameSecondBothKeepTheirFile()
    {
        // The stamp is only good to the second, and File.Move refuses to overwrite, so
        // without a numbered fallback the second attempt threw, was swallowed, and the
        // original was then written over: the exact loss this is here to prevent.
        var frozen = new DateTime(2026, 9, 16, 13, 5, 59);
        string path = Path_("settings.json");

        File.WriteAllText(path, "first");
        string? one = Settings.SetAside(path, () => frozen);
        File.WriteAllText(path, "second");
        string? two = Settings.SetAside(path, () => frozen);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one, two);
        Assert.Equal("first", File.ReadAllText(one));
        Assert.Equal("second", File.ReadAllText(two));
    }

    [Fact]
    public void SettingAsideNothingIsNotAnError()
        => Assert.Null(Settings.SetAside(Path_("never-existed.json")));

    [Fact]
    public void WhatIsSavedIsWhatLoadsBack()
    {
        // Through Save and Load, not through JsonSerializer beside them: the earlier
        // version of this serialised and deserialised by hand, so it would have passed
        // just as happily with Settings.JsonOptions broken and WriteAtomically reduced to
        // a bare WriteAllText.
        string path = Path_("settings.json");
        var original = new Settings
        {
            DropKey = "B6717A67D71A9FFBF9D2E284",
            VpnProfile = "Cloudflare WARP",
            BlocklistEnabled = true,
            SuspendAfterMinutes = 3,
        };

        original.Save(path);
        var back = Settings.Load(path);

        Assert.Equal("B6717A67D71A9FFBF9D2E284", back.DropKey);
        Assert.Equal("Cloudflare WARP", back.VpnProfile);
        Assert.True(back.BlocklistEnabled);
        Assert.Equal(3, back.SuspendAfterMinutes);
    }

    [Fact]
    public void LoadingNothingGivesDefaultsAndWritesThem()
    {
        string path = Path_("settings.json");

        var settings = Settings.Load(path);

        Assert.True(settings.BlocklistEnabled);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AFileThatIsNotJsonIsKeptAndDefaultsAreWritten()
    {
        string path = Path_("settings.json");
        File.WriteAllText(path, "{ truncated by a crash");

        var settings = Settings.Load(path);

        Assert.True(settings.BlocklistEnabled);              // running on defaults
        var kept = Directory.GetFiles(_dir, "*.unreadable-*.json");
        Assert.Single(kept);
        Assert.Equal("{ truncated by a crash", File.ReadAllText(kept[0]));
    }

    [Fact]
    public void AFileThatCannotBeSetAsideIsNotOverwritten()
    {
        // The loss this whole mechanism exists to prevent, and the case that was still
        // reachable: the file will not parse AND will not move, so defaults were written
        // straight over it and the pairing key went with them. A file that cannot be
        // protected must be left alone.
        string path = Path_("settings.json");
        File.WriteAllText(path, "{ not json, but it is the only copy");
        string before = File.ReadAllText(path);

        // FileShare.Read, so the file can still be READ and therefore still fails to
        // PARSE, which is what puts Load on the quarantine branch. It then cannot be
        // moved, because the handle denies that. An earlier version of this used
        // FileShare.None, which made the read itself throw, so Load took the transient
        // branch instead and SetAside was never called at all: the test passed just as
        // happily with the guard it was named for deleted.
        Settings settings;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            settings = Settings.Load(path);
            Assert.True(settings.BlocklistEnabled);          // running on defaults
        }

        // And not later either. Loading left it alone, and then the first toggle of the
        // run saved the defaults over it anyway, once the handle had gone.
        Assert.NotNull(settings.NotSavingBecause);
        Assert.False(settings.Save(path));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.unreadable-*.json"));
    }

    [Fact]
    public void AFileThatCannotBeReadAtAllLeavesItAlone()
    {
        // Transient rather than corrupt: a sharing violation is not a reason to move
        // somebody's settings out of the way.
        string path = Path_("settings.json");
        new Settings { DropKey = "keepme" }.Save(path);
        string before = File.ReadAllText(path);

        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var settings = Settings.Load(path);
            Assert.Equal("", settings.DropKey);              // defaults, in memory only
        }

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.unreadable-*.json"));
    }

    [Fact]
    public void ARunThatCouldNotReadTheFileNeverWritesOverIt()
    {
        // Held open by something else at startup: the run goes on defaults in memory and
        // leaves the file alone. Then the next toggle, the settings window or an agent's
        // /settings saved those defaults over it, and the pairing key and vpn profile with
        // them. Save now refuses for the rest of that run and says it did.
        string path = Path_("settings.json");
        File.WriteAllText(path, """{ "DropKey": "0123456789ABCDEF01234567" }""");

        Settings loaded;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            loaded = Settings.Load(path);

        Assert.NotNull(loaded.NotSavingBecause);
        Assert.False(loaded.Save(path));
        Assert.Contains("0123456789ABCDEF01234567", File.ReadAllText(path));
    }

    [Fact]
    public void APairingKeyThatCannotBeSavedIsNotHandedOut()
    {
        // Minted now and gone at the next start, so a phone paired with it would be locked
        // out tomorrow with no explanation. Saved through the path seam: EnsureKey's own
        // Save() writes the real settings file, and a regression in the guard would then
        // write it from a test.
        string path = Path_("settings.json");
        File.WriteAllText(path, """{ "DropEnabled": false }""");
        Settings loaded;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            loaded = Settings.Load(path);

        Assert.Throws<InvalidOperationException>(() => DropServer.EnsureKey(loaded, () => loaded.Save(path)));
        Assert.Equal("", loaded.DropKey);
    }

    [Fact]
    public void APairingKeyWhoseWriteFailsIsNotHandedOutEither()
    {
        // A full disk rather than a refusal: the same unkept key, so the same answer.
        var settings = new Settings();

        Assert.Throws<IOException>(() => DropServer.EnsureKey(settings, () => throw new IOException("disk full")));
        Assert.Equal("", settings.DropKey);
    }

    [Fact]
    public void ATunnelThatFailedThisRunIsNotSavedAsTurnedOff()
    {
        // Startup used to switch VpnEnabled off when the tunnel would not come up, "for
        // this session", and the next save of anything wrote that to disk. An agent's
        // /settings patch of an unrelated setting was enough, so the vpn was off at the
        // next start and an agent had turned it off without ever naming it.
        string path = Path_("settings.json");
        File.WriteAllText(path, """{ "VpnEnabled": true }""");
        var settings = Settings.Load(path);

        settings.VpnDownThisRun = true;   // what startup does when the tunnel fails
        Assert.False(settings.VpnInForce);

        using var patch = System.Text.Json.JsonDocument.Parse("""{ "PageTheme": "Dark" }""");
        Assert.Null(SettingsPatch.Apply(settings, patch.RootElement, [], [], []));
        Assert.True(settings.Save(path));

        var reloaded = Settings.Load(path);
        Assert.True(reloaded.VpnEnabled);
        Assert.True(reloaded.VpnInForce);   // and the next start tries the tunnel again
    }

    [Fact]
    public void ARunThatReadTheFileSavesAsUsual()
    {
        // The other half: the guard must not stop an ordinary run saving.
        string path = Path_("settings.json");
        File.WriteAllText(path, """{ "DropKey": "0123456789ABCDEF01234567" }""");

        var loaded = Settings.Load(path);
        loaded.DropKey = "FEDCBA9876543210FEDCBA98";

        Assert.Null(loaded.NotSavingBecause);
        Assert.True(loaded.Save(path));
        Assert.Contains("FEDCBA9876543210FEDCBA98", File.ReadAllText(path));
    }
}
