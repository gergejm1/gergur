using System.Text.Json;
using Gergur.App;
using Gergur.Tabs;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// A tab taken off the strip to listen to from the now-playing panel, and the script that stops
/// a playlist song starting at its very end. The real thing is the live run.
/// </summary>
public sealed class TuckedAwayTabTests
{
    private static TabManager Manager() => new(null!, new Control(), null!);

    private static Tab Add(TabManager m, string url) => m.AddSnapshotTab(new TabSnapshot(url, url));

    [Fact]
    public async Task ItLeavesTheStripAndTheTabBesideItComesForward()
    {
        var m = Manager();
        var a = Add(m, "https://a.example/");
        var song = Add(m, "https://www.youtube.com/watch?v=x");
        var c = Add(m, "https://c.example/");
        await m.ActivateAsync(song);

        await m.TuckAwayAsync(song);

        Assert.Equal([a, c], m.Tabs);
        Assert.Equal([song], m.TuckedAway);
        Assert.Equal([a, c, song], m.AllTabs);
        Assert.True(song.TuckedAway);
        Assert.Same(c, m.ActiveTab);
    }

    [Fact]
    public async Task ItComesBackLastOnTheStripAndInFront()
    {
        var m = Manager();
        var a = Add(m, "https://a.example/");
        var song = Add(m, "https://www.youtube.com/watch?v=x");
        var c = Add(m, "https://c.example/");
        await m.TuckAwayAsync(song);

        await m.BringBackAsync(song);

        Assert.Equal([a, c, song], m.Tabs);
        Assert.Empty(m.TuckedAway);
        Assert.False(song.TuckedAway);
        Assert.Same(song, m.ActiveTab);
    }

    [Fact]
    public async Task HidingTheOnlyTabLeavesANewTabOnTheStrip()
    {
        var m = Manager();
        var song = Add(m, "https://www.youtube.com/watch?v=x");
        bool closed = false;
        m.LastTabClosed += (_, _) => closed = true;
        await m.ActivateAsync(song);

        await m.TuckAwayAsync(song);

        var fresh = Assert.Single(m.Tabs);
        Assert.NotSame(song, fresh);
        Assert.Same(fresh, m.ActiveTab);
        Assert.False(closed);
    }

    [Fact]
    public async Task ClosingTheLastTabOnTheStripDoesNotCloseTheMusic()
    {
        var m = Manager();
        var a = Add(m, "https://a.example/");
        var song = Add(m, "https://www.youtube.com/watch?v=x");
        bool closed = false;
        m.LastTabClosed += (_, _) => closed = true;
        await m.TuckAwayAsync(song);

        await m.CloseTabAsync(a);

        Assert.False(closed);
        Assert.Single(m.Tabs);
        Assert.Equal([song], m.TuckedAway);
    }

    [Fact]
    public async Task ClosingItFromThePanelClosesItAndCanBeUndone()
    {
        var m = Manager();
        var a = Add(m, "https://a.example/");
        var song = Add(m, "https://www.youtube.com/watch?v=x");
        await m.TuckAwayAsync(song);
        int before = m.RecentlyClosedCount;

        await m.CloseTabAsync(song);

        Assert.Empty(m.TuckedAway);
        Assert.Equal([a], m.Tabs);
        Assert.Equal(before + 1, m.RecentlyClosedCount);
    }

    [Fact]
    public async Task ASecondHideOrABringBackOfATabNotHiddenDoesNothing()
    {
        var m = Manager();
        var a = Add(m, "https://a.example/");
        var song = Add(m, "https://www.youtube.com/watch?v=x");
        await m.BringBackAsync(a);
        Assert.Equal([a, song], m.Tabs);
        await m.TuckAwayAsync(song);
        await m.TuckAwayAsync(song);
        Assert.Equal([song], m.TuckedAway);
    }

    [Fact]
    public async Task HidingATabCanNeverLoseIt_ItIsSavedWithTheSessionAsAnOrdinaryTab()
    {
        var m = Manager();
        var a = Add(m, "https://a.example/");
        var song = Add(m, "https://www.youtube.com/watch?v=x");
        await m.TuckAwayAsync(song);

        var saved = AppSession.SessionOf([(false, m.AllTabs, a)]);

        var window = Assert.Single(saved!);
        Assert.Equal(["https://a.example/", "https://www.youtube.com/watch?v=x"], window.Tabs.Select(t => t.Url));
        Assert.Equal(0, window.ActiveIndex);
    }

    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static SoundCandidate Quiet(string key, bool tucked, TimeSpan ago, string host = "www.youtube.com", string? soundHost = "www.youtube.com")
        => new(key, false, Now - ago, Now - ago, host, soundHost, tucked);

    [Fact]
    public void AHiddenTabStaysChosenHoweverLongItIsQuietAndWhereverItWent()
    {
        var hidden = Quiet("hidden", true, TimeSpan.FromHours(5), host: "elsewhere.example");
        Assert.Equal("hidden", NowPlaying.Pick([hidden], "hidden", Now));
        // Never chosen yet, as after the panel's tab was closed: the control finds it.
        Assert.Equal("hidden", NowPlaying.Pick([hidden], null, Now));
        Assert.Equal("hidden", NowPlaying.Pick([hidden, Quiet("gone", false, TimeSpan.FromHours(2))], "gone", Now));
        // A song just paused from the panel can be played again from it, but not for long: that
        // tab can be reached on the strip, and this one cannot.
        Assert.Equal("paused", NowPlaying.Pick([Quiet("paused", false, TimeSpan.FromMinutes(4)), hidden], "paused", Now));
        Assert.Equal("hidden", NowPlaying.Pick([Quiet("paused", false, TimeSpan.FromMinutes(6)), hidden], "paused", Now));
        // One that has gone to another site is not "now playing" any more, however recent.
        Assert.Equal("hidden", NowPlaying.Pick([Quiet("left", false, TimeSpan.FromMinutes(1), host: "other.example"), hidden], "left", Now));
        // Of two, the one chosen stays, and when it goes the other is reachable.
        var other = Quiet("other", true, TimeSpan.FromHours(1));
        Assert.Equal("other", NowPlaying.Pick([hidden, other], "other", Now));
        Assert.Equal("hidden", NowPlaying.Pick([hidden, Quiet("other", false, TimeSpan.FromHours(1))], "other", Now));
        // Shown again after being heard just now: it keeps the control for those five minutes.
        Assert.Equal("other", NowPlaying.Pick([hidden, Quiet("other", false, TimeSpan.FromMinutes(2))], "other", Now));
        Assert.Equal("hidden", NowPlaying.Pick([hidden, Quiet("other", false, TimeSpan.FromMinutes(6))], "other", Now));
        // Something else making sound still wins, as it does anywhere.
        var loud = new SoundCandidate("loud", true, Now, Now, "x.example", "x.example");
        Assert.Equal("loud", NowPlaying.Pick([hidden, loud], "hidden", Now));
    }

    [Fact]
    public void AnOrdinaryTabIsChosenAsBefore()
    {
        Assert.Equal("a", NowPlaying.Pick([Quiet("a", false, TimeSpan.FromMinutes(29))], "a", Now));
        Assert.Null(NowPlaying.Pick([Quiet("a", false, TimeSpan.FromMinutes(31))], "a", Now));
        Assert.Null(NowPlaying.Pick([Quiet("a", false, TimeSpan.FromMinutes(1), host: "other.example")], "a", Now));
        Assert.Null(NowPlaying.Pick([Quiet("a", false, TimeSpan.FromMinutes(1), soundHost: null)], "a", Now));
        Assert.Null(NowPlaying.Pick([Quiet("a", false, TimeSpan.FromMinutes(1))], null, Now));
    }

    [Fact]
    public void ThePanelOffersHideOrElseShowAndClose()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var did = new List<string>();
                using var panel = new NowPlayingFlyout(_ => did.Add("button"), () => did.Add("go"), () => did.Add("hide"), () => did.Add("close"));
                panel.SetView(new NowPlayingView("Song", "Band", "youtube.com", true, true, true, true));
                panel.Relayout(keepPlace: false);
                var hide = panel.Spots.Single(s => s.Spoken == "Hide tab");
                Assert.True(hide.KeepOpen);
                Assert.Contains(panel.Spots, s => s.Spoken == "Go to tab");
                Assert.DoesNotContain(panel.Spots, s => s.Spoken == "Close tab");
                panel.RunSpot(hide);
                Assert.Equal(["hide"], did);

                panel.SetView(new NowPlayingView("Song", "Band", "youtube.com", true, true, true, true, TuckedAway: true));
                panel.Relayout(keepPlace: false);
                Assert.DoesNotContain(panel.Spots, s => s.Spoken == "Hide tab");
                Assert.Contains(panel.Spots, s => s.Spoken == "Show tab");
                Assert.Contains(panel.Spots, s => s.Spoken == "Close tab");
                // The player's buttons are all still there: that is the point of it.
                foreach (string b in new[] { "Previous", "Pause", "Next" })
                    Assert.Contains(panel.Spots, s => s.Spoken == b);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    // ------------------------------------------------------------------ a song starting at its end

    private const string Harness = """
        const vm = require('vm');
        const SCRIPT = __SCRIPT__;
        // One page: a player, its video, and the clock in the test's hands. "load" is what the
        // player does for a new song: its handler moves the time after the capture listeners ran.
        function page(href, opts) {
          const url = new URL(href);
          const listeners = {};
          const timers = [];
          const seeks = [];
          const player = { id: 'movie_player', className: opts.className || '', video: opts.id || 'one',
            getVideoData() { return { video_id: this.video }; },
            getPlayerResponse() { return { microformat: { playerMicroformatRenderer: { category: opts.category || '' } } }; },
            seekTo(t) { seeks.push(t); video.currentTime = t; } };
          class HTMLVideoElement { }
          const video = new HTMLVideoElement();
          Object.assign(video, { currentTime: 0, duration: opts.duration || 200, closest: s => s === '#movie_player' ? player : null });
          const ctx = vm.createContext({ HTMLVideoElement, URL, Symbol, Object, isFinite, location: { href, protocol: url.protocol, hostname: url.hostname },
            performance: { getEntriesByType: () => [{ type: opts.navigation || 'navigate' }] },
            document: { addEventListener: (t, f) => { (listeners[t] = listeners[t] || []).push(f); } }, setTimeout: f => timers.push(f) });
          vm.runInContext('var window = globalThis;', ctx);
          if (opts.frame) vm.runInContext('window.top = {};', ctx); else vm.runInContext('window.top = window;', ctx);
          vm.runInContext(SCRIPT, ctx);
          const fire = t => (listeners[t] || []).forEach(f => f({ target: video }));
          const flush = () => { while (timers.length) timers.shift()(); };
          return { player, video, seeks, fire, flush,
            load(at, id) { if (id) player.video = id; video.currentTime = 0; fire('loadedmetadata'); video.currentTime = at; flush(); },
            hooked: Object.keys(listeners).length > 0 };
        }
        const WATCH = 'https://www.youtube.com/watch?v=one&list=RDone';
        const out = {};
        { const p = page(WATCH, {}); p.load(198); out.nearEnd = p.seeks; }
        { const p = page(WATCH, { duration: 3600 }); p.load(3300); out.lastTenth = p.seeks; }
        { const p = page(WATCH, { category: 'Music' }); p.load(60); out.musicPartWay = p.seeks; }
        { const p = page(WATCH, {}); p.load(60); out.otherPartWay = p.seeks; }
        { const p = page(WATCH, {}); p.load(0); out.fromTheStart = p.seeks; }
        { const p = page(WATCH + '&t=198', {}); p.load(198); out.linkNamedATime = p.seeks; }
        { const p = page(WATCH, {}); p.load(0); p.video.currentTime = 0; p.fire('playing'); p.flush(); p.video.currentTime = 195; p.fire('seeking'); p.flush(); out.personSeeks = p.seeks; }
        { const p = page(WATCH, {}); p.load(198); p.seeks.length = 0; p.video.currentTime = 198; p.fire('loadedmetadata'); p.flush(); out.sameSongReloads = p.seeks; }
        { const p = page(WATCH, {}); p.load(198); p.load(150, 'two'); p.load(199, 'three'); out.eachSong = p.seeks; }
        { const p = page(WATCH, { className: 'ad-showing' }); p.video.currentTime = 25; p.fire('loadedmetadata'); p.flush(); const inAd = p.seeks.slice(); p.player.className = ''; p.load(198); out.adThenSong = { inAd, song: p.seeks }; }
        { const p = page('https://www.youtube.com/watch?v=one', { category: 'Music' }); p.load(198); out.notInAPlaylist = p.seeks; }
        { const p = page(WATCH, { category: 'Music', navigation: 'reload' }); p.load(60); const back = p.seeks.slice(); p.load(198, 'two'); out.reload = { back, next: p.seeks }; }
        { const p = page(WATCH, { category: 'Music' }); p.fire('loadedmetadata'); p.player.className = 'ad-showing'; p.video.currentTime = 20; p.fire('seeking'); p.fire('playing'); p.flush(); const inAd = p.seeks.slice(); p.player.className = ''; p.video.currentTime = 150; p.fire('seeking'); out.adAfterItArmed = { inAd, song: p.seeks }; }
        // The player seeks by itself a moment later, no timer needed (a hidden tab's are slow).
        { const p = page(WATCH, {}); p.fire('loadedmetadata'); p.video.currentTime = 198; p.fire('seeking'); out.atItsFirstSeek = p.seeks; }
        out.otherSite = page('https://www.example.com/watch?v=one', {}).hooked;
        out.lookalike = page('https://notyoutube.com/watch?v=one', {}).hooked;
        out.plainHttp = page('http://www.youtube.com/watch?v=one', {}).hooked;
        out.frame = page(WATCH, { frame: true }).hooked;
        out.music = page('https://music.youtube.com/watch?v=one', {}).hooked;
        console.log(JSON.stringify(out));
        """;

    private static readonly Lazy<JsonElement> Run = new(() => JsonDocument.Parse(MediaScriptTests.RunNode(
        Harness.Replace("__SCRIPT__", JsonSerializer.Serialize(MediaControl.SongStartScript), StringComparison.Ordinal))).RootElement.Clone());

    private static int[] Seeks(string name) => Run.Value.GetProperty(name).EnumerateArray().Select(e => e.GetInt32()).ToArray();

    [Theory]
    [InlineData("nearEnd")]        // what skipped: 154 of 155, measured
    [InlineData("lastTenth")]
    [InlineData("musicPartWay")]   // a song from half way is not the song
    [InlineData("atItsFirstSeek")]
    public void ASongStartedWhereItWouldBeLostStartsFromTheBeginning(string name)
        => Assert.Equal([0], Seeks(name));

    [Theory]
    [InlineData("otherPartWay")]    // a film picks up where it was left
    [InlineData("fromTheStart")]
    [InlineData("linkNamedATime")]
    [InlineData("personSeeks")]     // their own seek to near the end stays
    [InlineData("sameSongReloads")] // a stream reloaded part way is not a new song
    [InlineData("notInAPlaylist")]  // what was measured is playlists; elsewhere it goes on as it has
    public void EverythingElseIsLeftWhereItIs(string name)
        => Assert.Empty(Seeks(name));

    [Fact]
    public void EachNewSongIsLookedAtOnItsOwn()
        => Assert.Equal([0, 0], Seeks("eachSong"));   // not the 150 of 200, which is a film part way

    [Fact]
    public void AnAdIsNotASong()
    {
        var r = Run.Value.GetProperty("adThenSong");
        Assert.Empty(r.GetProperty("inAd").EnumerateArray());
        Assert.Equal([0], r.GetProperty("song").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void AnAdThatComesAfterTheSongLoadedIsNotSeekedAndTheSongStillIs()
    {
        var r = Run.Value.GetProperty("adAfterItArmed");
        Assert.Empty(r.GetProperty("inAd").EnumerateArray());
        Assert.Equal([0], r.GetProperty("song").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void AReloadComesBackToTheSameSongAndOnlyThatOne()
    {
        var r = Run.Value.GetProperty("reload");
        Assert.Empty(r.GetProperty("back").EnumerateArray());
        Assert.Equal([0], r.GetProperty("next").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void ItOnlyRunsOnYouTubesOwnTopPage()
    {
        foreach (string other in new[] { "otherSite", "lookalike", "plainHttp", "frame" })
            Assert.False(Run.Value.GetProperty(other).GetBoolean(), other);
        Assert.True(Run.Value.GetProperty("music").GetBoolean());
    }

    [Fact]
    public void EveryPageGetsTheScript()
    {
        string tab = File.ReadAllText(OwnPagesHardeningTests.FindSource("Gergur", "Tabs", "Tab.cs"));
        Assert.Contains("await core.AddScriptToExecuteOnDocumentCreatedAsync(MediaControl.SongStartScript);", tab, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowHandsTuckedAwayTabsToEverythingThatMustStillReachThem()
    {
        string form = File.ReadAllText(OwnPagesHardeningTests.FindSource("Gergur", "UI", "MainForm.cs"));
        Assert.Contains(".SelectMany(w => w.Tabs!.AllTabs.ToArray());", form, StringComparison.Ordinal);
        Assert.Contains("foreach (var tab in Tabs.AllTabs)\r\n                    tab.RefreshDialogMode();".Replace("\r\n", form.Contains("\r\n") ? "\r\n" : "\n"), form, StringComparison.Ordinal);
        Assert.Contains("foreach (var tab in window.Tabs?.AllTabs ?? [])", form, StringComparison.Ordinal);
        string session = File.ReadAllText(OwnPagesHardeningTests.FindSource("Gergur", "App", "AppSession.cs"));
        Assert.Contains("w.Tabs!.AllTabs, w.Tabs.ActiveTab", session, StringComparison.Ordinal);
    }

    private static readonly DateTime At = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false, true, -1, true)]     // heard
    [InlineData(false, false, 1, true)]     // between two songs, or just paused
    [InlineData(false, false, -1, false)]   // silent past the grace: hidden like any other tab
    [InlineData(true, false, -1, true)]     // hidden on purpose: its controls are all there is
    public void WhenATabNotInFrontKeepsItsPageUp(bool tuckedAway, bool playing, int upUntilSeconds, bool up)
    {
        Assert.Equal(up, Tab.StaysUpBehind(tuckedAway, playing, At, At.AddSeconds(upUntilSeconds)));
    }

    [Fact]
    public void TheGraceOutlastsTheSilenceBetweenTwoSongs()
    {
        Assert.True(Tab.PlayingBehindGrace >= TimeSpan.FromSeconds(30));
    }

    private static string TabBody(string startsAt, string endsAt)
    {
        string tab = File.ReadAllText(OwnPagesHardeningTests.FindSource("Gergur", "Tabs", "Tab.cs"));
        int from = tab.IndexOf(startsAt, StringComparison.Ordinal);
        Assert.True(from >= 0, startsAt);
        int to = tab.IndexOf(endsAt, from, StringComparison.Ordinal);
        Assert.True(to > from, endsAt);
        return tab[from..to];
    }

    [Fact]
    public void LeavingTheFrontHidesThePageBeforePuttingItUpAgain()
    {
        // Hiding is what takes the keyboard away from it: the person's keys must not go on to
        // a tab out of sight.
        string leave = TabBody("public void Deactivate()", "public void FocusPage()");
        int hide = leave.IndexOf("_webView.Visible = false;", StringComparison.Ordinal);
        int up = leave.IndexOf("ConsiderPlayingBehind();", StringComparison.Ordinal);
        Assert.True(hide >= 0 && up > hide);
        Assert.Contains("_playsBehind = false;", leave[hide..up], StringComparison.Ordinal);
    }

    [Fact]
    public void ComingToTheFrontPutsTheViewBackInThePageAreaBeforeShowingIt()
    {
        string come = TabBody("public async Task ActivateAsync()", "public void Deactivate()");
        int dock = come.IndexOf("PlaceInPageArea(_webView);", StringComparison.Ordinal);
        int show = come.IndexOf("_webView.Visible = true;", StringComparison.Ordinal);
        Assert.True(dock >= 0 && show > dock);
        Assert.Contains("_playsBehind = false;", come[..dock], StringComparison.Ordinal);
    }

    [Fact]
    public void APageUpBehindIsMovedOutOfThePageAreaFirstAndNeverSuspended()
    {
        string consider = TabBody("private void ConsiderPlayingBehind()", "private void StayUpForAWhile()");
        Assert.Contains("State != TabState.Hidden", consider, StringComparison.Ordinal);
        Assert.Contains("PlaceBehind(view);", consider, StringComparison.Ordinal);
        string suspend = TabBody("public async Task<bool> TrySuspendAsync()", "public void Discard()");
        Assert.Contains("_webView.Visible", suspend, StringComparison.Ordinal);
    }

    [Fact]
    public void SoundAndThePanelsButtonsKeepItUp()
    {
        string heard = TabBody("private void OnPlayingAudioChanged(", "internal static readonly TimeSpan PlayingBehindGrace");
        Assert.Contains("ConsiderPlayingBehind();", heard, StringComparison.Ordinal);
        Assert.Contains("StayUpForAWhile();", heard, StringComparison.Ordinal);
        string press = TabBody("internal async Task<string> RunMediaAsync(", "public bool CanGoBack");
        int up = press.IndexOf("StayUpForAWhile();", StringComparison.Ordinal);
        int run = press.IndexOf("MediaControl.CommandScript(action)", StringComparison.Ordinal);
        Assert.True(up >= 0 && run > up, "the song must not load in a hidden page");
    }

    [Fact]
    public void BehindIsOutOfThePageAreaAtFullSizeUpAndOffTheTabKeysRound()
    {
        using var host = new Panel { ClientSize = new Size(800, 600) };
        using var view = new Control { Parent = host, Dock = DockStyle.Fill };

        Tab.PlaceBehind(view);

        Assert.Equal(DockStyle.None, view.Dock);
        Assert.True(view.Right <= 0, $"it must be clipped: {view.Bounds}");
        Assert.Equal(new Size(800, 600), view.Size);
        Assert.False(view.TabStop);
        Assert.True(view.Visible || !host.Visible);

        Tab.PlaceInPageArea(view);

        Assert.Equal(DockStyle.Fill, view.Dock);
        Assert.True(view.TabStop);
        host.PerformLayout();
        Assert.Equal(new Rectangle(0, 0, 800, 600), view.Bounds);
    }

    [Fact]
    public void ANeverShownViewStillGetsASizeBehind()
    {
        using var host = new Panel { ClientSize = new Size(0, 0) };
        using var view = new Control { Parent = host, Size = new Size(300, 200) };
        Tab.PlaceBehind(view);
        Assert.Equal(new Size(300, 200), view.Size);
        Assert.True(view.Right <= 0);
    }

    [Fact]
    public async Task AWindowClosingTakesItsHiddenTabsWithIt()
    {
        var m = Manager();
        Add(m, "https://a.example/");
        var song = Add(m, "https://www.youtube.com/watch?v=x");
        await m.TuckAwayAsync(song);

        m.DisposeAll();

        Assert.Empty(m.AllTabs);
    }

    [Fact]
    public void ThePanelsHideAndCloseActOnTheTabItDescribes()
    {
        string form = File.ReadAllText(OwnPagesHardeningTests.FindSource("Gergur", "UI", "MainForm.cs"));
        int close = form.IndexOf("private async Task CloseMediaTabAsync()", StringComparison.Ordinal);
        int hide = form.IndexOf("private async Task HideMediaTabAsync()", StringComparison.Ordinal);
        Assert.True(close >= 0 && hide >= 0);
        int closes = form.IndexOf("CloseTabAsync(tab)", close, StringComparison.Ordinal);
        int hides = form.IndexOf("TuckAwayAsync(tab)", hide, StringComparison.Ordinal);
        Assert.Contains("!tab.TuckedAway || !ReferenceEquals(_mediaPanelTab, tab)", form[close..closes], StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(_mediaPanelTab, tab)", form[hide..hides], StringComparison.Ordinal);
    }

    [Fact]
    public void ANewViewStartsInThePageArea()
    {
        string tab = File.ReadAllText(OwnPagesHardeningTests.FindSource("Gergur", "Tabs", "Tab.cs"));
        int gone = tab.IndexOf("private void DetachAndDisposeWebView", StringComparison.Ordinal);
        Assert.True(gone >= 0);
        Assert.Contains("_playsBehind = false;", tab.Substring(gone, 120), StringComparison.Ordinal);
    }
}
