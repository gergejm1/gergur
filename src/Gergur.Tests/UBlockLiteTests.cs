using System.IO.Compression;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Blocking;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// uBlock Origin Lite run inside the engine and kept current (Blocking/UBlockLite.cs). That the
/// engine loads it and that its YouTube rules run there was measured live in a test instance;
/// these hold what Gergur decides: which release, from where, what it unpacks and swaps in, and
/// that Gergur's own YouTube code stands down while it runs.
/// </summary>
public sealed class UBlockLiteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gergur-tests", "ubol-" + Guid.NewGuid().ToString("N"));

    public UBlockLiteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Code(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gergur.slnx")))
            dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("Gergur.slnx not found above the test binaries");
        return File.ReadAllText(Path.Combine(root, "src", "Gergur", relative));
    }

    private static string Manifest(string version, string shortName = "uBO Lite", int mv = 3, string author = "Raymond Hill")
        => $$"""{ "manifest_version": {{mv}}, "name": "__MSG_extName__", "short_name": "{{shortName}}", "author": "{{author}}", "version": "{{version}}" }""";

    /// <summary>A folder holding a whole uBO Lite of that version.</summary>
    private string Whole(string name, string version)
    {
        string folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), Manifest(version));
        File.WriteAllText(Path.Combine(folder, UBlockLite.CompleteMarker), "x");
        return folder;
    }

    // ------------------------------------------------------------------ which release

    [Theory]
    [InlineData("2026.1103.1743", true)]
    [InlineData("2025.1.2", true)]
    [InlineData("v2026.1103.1743", false)]
    [InlineData("2026.1103", false)]
    [InlineData("2026.1103.1743/../../x", false)]
    [InlineData("2026.1103.1743 ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAReleaseTagIsATag(string? tag, bool ok) => Assert.Equal(ok, UBlockLite.IsTag(tag));

    [Theory]
    [InlineData("2026.1103.1743", "2026.1102.900", 1)]
    [InlineData("2026.1103.900", "2026.1103.1743", -1)]
    [InlineData("2026.1103.1743", "2026.1103.1743", 0)]
    [InlineData("2027.1.1", "2026.1231.2359", 1)]
    [InlineData("2026.1103.1743", null, 1)]
    [InlineData(null, "2026.1103.1743", -1)]
    [InlineData("junk", null, 0)]
    public void TagsCompareAsNumbers(string? a, string? b, int sign) => Assert.Equal(sign, Math.Sign(UBlockLite.CompareTags(a, b)));

    [Fact]
    public void TheDownloadIsAlwaysUBlocksOwnReleaseBuiltFromTheTag()
    {
        Assert.Equal("https://github.com/uBlockOrigin/uBOL-home/releases/download/2026.1103.1743/uBOLite_2026.1103.1743.chromium.zip",
            UBlockLite.DownloadUrl("2026.1103.1743"));
        Assert.Throws<ArgumentException>(() => UBlockLite.DownloadUrl("2026.1103.1743/../../../evil"));
        // The address in GitHub's answer is never followed: only its tag is read.
        string code = Code("Blocking/UBlockLite.cs");
        Assert.DoesNotContain("browser_download_url", code);
        Assert.Contains("http.GetAsync(DownloadUrl(latest)", code);
    }

    [Fact]
    public void TheTagIsReadFromGitHubsAnswerAndNothingElse()
    {
        Assert.Equal("2026.1103.1743", UBlockLite.TagFrom("""{ "tag_name": "2026.1103.1743", "assets": [] }"""));
        Assert.Null(UBlockLite.TagFrom("""{ "tag_name": "../../evil" }"""));
        Assert.Null(UBlockLite.TagFrom("""{ "message": "API rate limit exceeded" }"""));
        Assert.Null(UBlockLite.TagFrom("""[]"""));
        Assert.Null(UBlockLite.TagFrom("not json"));
        // A tag dated years ahead would outrank every real release for good.
        Assert.Null(UBlockLite.TagFrom($$"""{ "tag_name": "{{DateTime.UtcNow.Year + 5}}.101.1" }"""));
        Assert.NotNull(UBlockLite.TagFrom($$"""{ "tag_name": "{{DateTime.UtcNow.Year + 1}}.101.1" }"""));
    }

    [Fact]
    public void GitHubIsToldAPlainClientNameAndNothingAboutThePerson()
    {
        string code = Code("Blocking/UBlockLite.cs");
        Assert.Contains("UserAgent.ParseAdd(\"Gergur-browser\")", code);
        Assert.DoesNotContain("@", Regex.Match(code, @"ParseAdd\(""[^""]*""\)").Value);
    }

    // ------------------------------------------------------------------ what is unpacked

    [Fact]
    public void OnlyUBlockLitesManifestCounts()
    {
        Assert.Equal("2026.1103.1743", UBlockLite.VersionFromManifest(Manifest("2026.1103.1743")));
        Assert.Null(UBlockLite.VersionFromManifest(Manifest("2026.1103.1743", shortName: "Something else")));
        Assert.Null(UBlockLite.VersionFromManifest(Manifest("2026.1103.1743", mv: 2)));
        Assert.Null(UBlockLite.VersionFromManifest(Manifest("2026.1103.1743", author: "Someone")));
        Assert.Null(UBlockLite.VersionFromManifest(Manifest("1.0")));
        Assert.Null(UBlockLite.VersionFromManifest("{"));
        // Never one that updates itself from elsewhere, listens to web pages, or pins its id.
        foreach (var extra in new[] { "\"update_url\": \"https://example.com/u\"", "\"externally_connectable\": {}", "\"key\": \"abc\"" })
            Assert.Null(UBlockLite.VersionFromManifest(Manifest("2026.1103.1743").TrimEnd('}', ' ') + ", " + extra + " }"));
    }

    [Fact]
    public void AFolderWithoutItsMarkerIsHalfUnpackedAndNotUsed()
    {
        string folder = Whole("current", "2026.1103.1743");
        Assert.Equal("2026.1103.1743", UBlockLite.VersionIn(folder));
        File.Delete(Path.Combine(folder, UBlockLite.CompleteMarker));
        Assert.Null(UBlockLite.VersionIn(folder));
        Assert.Null(UBlockLite.VersionIn(Path.Combine(_dir, "nowhere")));
    }

    private string Zip(params (string Name, string Text)[] entries)
    {
        string zip = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var w = new StreamWriter(archive.CreateEntry(name).Open());
            w.Write(text);
        }
        return zip;
    }

    [Fact]
    public void ARealArchiveUnpacksWholeAndMarked()
    {
        string into = Path.Combine(_dir, "pending");
        UBlockLite.Unpack(Zip(("manifest.json", Manifest("2026.1103.1743")), ("js/background.js", "//"), ("css/", "")), into);
        Assert.Equal("2026.1103.1743", UBlockLite.VersionIn(into));
        Assert.True(File.Exists(Path.Combine(into, "js", "background.js")));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("js/../../escaped.txt")]
    public void AnEntryOutsideItsFolderStopsEverythingAndWritesNothingOutside(string name)
    {
        string into = Path.Combine(_dir, "box", "pending");
        Directory.CreateDirectory(Path.Combine(_dir, "box"));
        Assert.Throws<InvalidDataException>(() => UBlockLite.Unpack(Zip(("manifest.json", Manifest("2026.1103.1743")), (name, "x")), into));
        Assert.False(File.Exists(Path.Combine(_dir, "box", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        Assert.Null(UBlockLite.VersionIn(into));
    }

    [Fact]
    public void AnArchivePastItsLimitsIsRefusedHowMuchItSaysItHolds()
    {
        string into = Path.Combine(_dir, "limits");
        string big = Zip(("manifest.json", Manifest("2026.1103.1743")), ("a.txt", new string('x', 5000)));
        Assert.Throws<InvalidDataException>(() => UBlockLite.Unpack(big, into, maxBytes: 4000));
        Assert.Null(UBlockLite.VersionIn(into));
        Assert.Throws<InvalidDataException>(() => UBlockLite.Unpack(big, into, maxEntries: 1));
        Assert.Null(UBlockLite.VersionIn(into));
        // Counted as written, so the bytes inflated are what is held to the limit.
        string code = Code("Blocking/UBlockLite.cs");
        Assert.Matches(@"total \+= read;\s*if \(total > maxBytes\)", code);
        Assert.DoesNotContain("total += entry.Length", code);
    }

    [Fact]
    public void AFolderEntryForTheRootItselfIsFine()
    {
        string into = Path.Combine(_dir, "dot");
        UBlockLite.Unpack(Zip(("./", ""), ("manifest.json", Manifest("2026.1103.1743"))), into);
        Assert.Equal("2026.1103.1743", UBlockLite.VersionIn(into));
    }

    [Fact]
    public async Task ADownloadLargerThanAnyReleaseIsRefused()
    {
        using var big = new MemoryStream(new byte[1000]);
        using var to = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => UBlockLite.CopyBoundedAsync(big, to, 999, CancellationToken.None));
        big.Position = 0;
        await UBlockLite.CopyBoundedAsync(big, new MemoryStream(), 1000, CancellationToken.None);
    }

    // ------------------------------------------------------------------ swapping in at start

    [Fact]
    public void ANewerWholeUpdateTakesOverAtStart()
    {
        string current = Whole("current", "2026.1102.100");
        string pending = Whole("pending", "2026.1103.1743");
        UBlockLite.PromotePending(current, pending);
        Assert.Equal("2026.1103.1743", UBlockLite.VersionIn(current));
        Assert.False(Directory.Exists(pending));
        Assert.False(Directory.Exists(current + ".old"));
    }

    [Fact]
    public void AFirstUpdateWithNothingCurrentTakesOverToo()
    {
        string current = Path.Combine(_dir, "current");
        string pending = Whole("pending", "2026.1103.1743");
        UBlockLite.PromotePending(current, pending);
        Assert.Equal("2026.1103.1743", UBlockLite.VersionIn(current));
    }

    [Theory]
    [InlineData("2026.1102.100")]
    [InlineData("2026.1103.1743")]
    public void AnUpdateNoNewerIsClearedAndCurrentKept(string waiting)
    {
        string current = Whole("current", "2026.1103.1743");
        string pending = Whole("pending", waiting);
        UBlockLite.PromotePending(current, pending);
        Assert.Equal("2026.1103.1743", UBlockLite.VersionIn(current));
        Assert.False(Directory.Exists(pending));
    }

    [Fact]
    public void ASwapThatFailsPutsTheWorkingCopyBack()
    {
        string current = Whole("current", "2026.1102.100");
        string pending = Whole("pending", "2026.1103.1743");
        // A file held open inside pending, as an antivirus scan would: the move fails.
        using (new FileStream(Path.Combine(pending, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            UBlockLite.PromotePending(current, pending);
        Assert.Equal("2026.1102.100", UBlockLite.VersionIn(current));
        Assert.False(Directory.Exists(current + ".old"));
    }

    [Fact]
    public void AHalfUnpackedUpdateIsClearedAndCurrentKept()
    {
        string current = Whole("current", "2026.1102.100");
        string pending = Whole("pending", "2026.1103.1743");
        File.Delete(Path.Combine(pending, UBlockLite.CompleteMarker));
        UBlockLite.PromotePending(current, pending);
        Assert.Equal("2026.1102.100", UBlockLite.VersionIn(current));
        Assert.False(Directory.Exists(pending));
    }

    // ------------------------------------------------------------------ YouTube is left to it

    [Fact]
    public void WhileItRunsGergursOwnYouTubeCodeStandsDown()
    {
        string script = Code("Assets/adblock.js");
        Assert.Contains(PageCleanup.UBlockPlaceholder, script);
        string on = PageCleanup.Compose(script, [], uBlockActive: true);
        Assert.Contains("var UBOL_ACTIVE = true;", on);
        Assert.DoesNotContain(PageCleanup.UBlockPlaceholder, on);
        Assert.Equal(script, PageCleanup.Compose(script, [], uBlockActive: false));
        // Both lines written when both apply.
        string both = PageCleanup.Compose(script, ["youtube.com"], uBlockActive: true);
        Assert.Contains("var UBOL_ACTIVE = true;", both);
        Assert.Contains("var RELAXED_SITES = [\"youtube.com\"];", both);
        // It stands down before anything on YouTube: the hiding, the pruning hooks and the tick.
        int down = script.IndexOf("if (UBOL_ACTIVE && /(^|\\.)youtube(-nocookie)?\\.com$/.test(location.hostname)) return;", StringComparison.Ordinal);
        Assert.True(down > 0);
        Assert.True(down < script.IndexOf("var RELAXED = typeof location", StringComparison.Ordinal));
        Assert.True(down < script.IndexOf("ytInitialPlayerResponse", down, StringComparison.Ordinal));
        Assert.True(down > script.IndexOf("module.exports", StringComparison.Ordinal), "after the node export, so the tests of the pruning still reach it");
        // A tab's script says whether it is loaded.
        Assert.Contains("Blocking.PageCleanup.ScriptFor(Blocking.RelaxedSites.Parse(settings.RelaxedSites), Blocking.UBlockLite.LoadedId is not null)", Code("Tabs/Tab.cs"));
    }

    // ------------------------------------------------------------------ the setting and the wiring

    [Fact]
    public void ItIsOnByDefaultNeedsARestartAndIsThePersonsToTurnOff()
    {
        Assert.True(new Settings().UBlockLite);
        Assert.Contains(nameof(Settings.UBlockLite), Settings.RestartRequired);
        Assert.Contains(nameof(Settings.UBlockLite), SettingsPatch.DialogOnly);
    }

    [Fact]
    public void ItIsLoadedBeforeTheFirstPageAndUpdatesTakeOverBeforeTheEngine()
    {
        string env = Code("App/BrowserEnvironment.cs");
        Assert.Contains("AreBrowserExtensionsEnabled = settings.UBlockLite,", env);
        Assert.Matches(@"await SiteWorkarounds\.ResetServiceWorkersOnceAsync\(webView\.CoreWebView2\);\s*(//[^\n]*\s*)*await Blocking\.UBlockLite\.EnsureLoadedOnceAsync\(webView\.CoreWebView2\.Profile, Settings\);\s*return webView;", env);
        string session = Code("App/AppSession.cs");
        Assert.Matches(@"if \(settings\.UBlockLite\)\s*Blocking\.UBlockLite\.PromotePending\(\);\s*var env = await BrowserEnvironment\.CreateAsync\(settings\);", session);
        // Checked off the UI thread; engine calls come back to it.
        Assert.Contains("_ = Task.Run(() => UBlockLite.KeepCurrentAsync(() => onUi(LoadUBlockNow), first, stop));", session);
        Assert.Contains("_session.StartUBlockLite(work => ui.Post(_ => work(), null));", Code("UI/MainForm.cs"));
        // Tabs already open hand YouTube over once a first download is loaded.
        Assert.Matches(@"await UBlockLite\.LoadNowAsync\(profile, Settings\);\s*if \(UBlockLite\.LoadedId is not null\)\s*HandOver\(\);", session);
        Assert.Matches(@"private void HandOver\(\)\s*\{\s*MainForm\.ApplyLiveSettings\(this\);", session);
        // And after a load that finished late, when tabs built meanwhile kept Gergur's own code.
        Assert.Contains("UBlockLite.LoadedLate += () => onUi(HandOver);", session);
    }
}
