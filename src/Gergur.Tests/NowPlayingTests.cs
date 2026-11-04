using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Tabs;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The toolbar's now-playing control: which tab it drives, what a page's answer is taken to
/// mean, what the panel shows, and the wiring that can be pinned without an engine. The
/// engine half (the site's own buttons pressed, a frame from another site, YouTube's
/// playlist) is the live harness in the scratchpad, run on a hidden desktop.
/// </summary>
public sealed class NowPlayingTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static string Source(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gergur", relative));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gergur.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Gergur.slnx not found above the test binaries");
    }

    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { work(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }

    private static SoundCandidate Tab(object key, bool audible, int startedAgoMin, int heardAgoMin, string host = "www.youtube.com", string? soundHost = "www.youtube.com")
        => new(key, audible, Now.AddMinutes(-startedAgoMin), Now.AddMinutes(-heardAgoMin), host, soundHost);

    // ------------------------------------------------------------------ which tab

    [Fact]
    public void NothingIsChosenWhenNothingHasPlayed()
    {
        var a = new object();
        Assert.Null(NowPlaying.Pick([new SoundCandidate(a, false, null, null, "example.com", null)], null, Now));
        Assert.Null(NowPlaying.Pick([], null, Now));
    }

    [Fact]
    public void OfTwoTabsMakingSoundTheOneThatBeganLastIsChosen()
    {
        object older = new(), newer = new();
        var tabs = new[] { Tab(older, true, 10, 0), Tab(newer, true, 1, 0) };
        Assert.Same(newer, NowPlaying.Pick(tabs, null, Now));
        // Whichever was chosen before: what was just put on is what the person means.
        Assert.Same(newer, NowPlaying.Pick(tabs, older, Now));
    }

    [Fact]
    public void ATabMakingSoundBeatsTheOnePausedEarlier()
    {
        object paused = new(), playing = new();
        Assert.Same(playing, NowPlaying.Pick([Tab(paused, false, 5, 1), Tab(playing, true, 30, 0)], paused, Now));
    }

    [Fact]
    public void APausedTabStaysChosenWhileItIsOnTheSiteItPlayedOnAndNotLongQuiet()
    {
        var song = new object();
        Assert.Same(song, NowPlaying.Pick([Tab(song, false, 20, 29)], song, Now));
        // Another song on the same site is the same "now playing".
        Assert.Same(song, NowPlaying.Pick([Tab(song, false, 20, 1, host: "WWW.YOUTUBE.COM")], song, Now));
    }

    [Fact]
    public void APausedTabIsLetGoOnceItIsLongQuietOrElsewhere()
    {
        var song = new object();
        Assert.Null(NowPlaying.Pick([Tab(song, false, 40, 31)], song, Now));
        Assert.Null(NowPlaying.Pick([Tab(song, false, 2, 1, host: "www.google.com")], song, Now));
        // Never heard, so never chosen: only a tab that played can be kept.
        Assert.Null(NowPlaying.Pick([new SoundCandidate(song, false, null, null, "www.youtube.com", null)], song, Now));
    }

    [Fact]
    public void AClosedTabIsLetGo()
    {
        object closed = new(), other = new();
        Assert.Null(NowPlaying.Pick([Tab(other, false, 5, 5)], closed, Now));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=x", "www.youtube.com", "youtube.com")]
    [InlineData("http://127.0.0.1:41801/", "127.0.0.1", "127.0.0.1")]
    [InlineData("https://music.youtube.com/", "music.youtube.com", "music.youtube.com")]
    [InlineData("about:blank", "", "")]
    [InlineData("file:///C:/x.mp3", "", "")]
    [InlineData("not a url", "", "")]
    [InlineData(null, "", "")]
    public void TheSiteIsTheHostOfAWebAddress(string? url, string host, string site)
    {
        Assert.Equal(host, NowPlaying.HostOf(url));
        Assert.Equal(site, NowPlaying.SiteOf(url));
    }

    // ------------------------------------------------------------------ what the page says

    [Fact]
    public void APagesAnswerIsReadAsItsMedia()
    {
        var state = MediaControl.Parse("""{"title":"Song","artist":"Band","media":true,"playing":true,"paused":false,"next":true,"previous":false}""");
        Assert.Equal(new MediaState("Song", "Band", true, true, false, true, false), state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{not json")]
    public void AnythingButThatObjectIsNoAnswer(string? json)
        => Assert.Null(MediaControl.Parse(json));

    [Fact]
    public void AnOversizedAnswerIsNotRead()
        => Assert.Null(MediaControl.Parse("{\"title\":\"" + new string('a', 70_000) + "\"}"));

    [Fact]
    public void OnlyTrueIsYes()
    {
        // The page writes this: "true" as text, 1, or a missing field is not a yes.
        var state = MediaControl.Parse("""{"title":5,"media":"true","playing":1,"next":true}""");
        Assert.Equal(new MediaState("", "", false, false, false, true, false), state);
    }

    [Fact]
    public void APagesTextIsMadeFitForOneLine()
    {
        Assert.Equal("a b c", MediaControl.Clean("  a\r\n\tb \u0000 c  "));
        // Direction overrides and other invisible marks cannot reorder or hide what is shown.
        Assert.Equal("evil txt.exe", MediaControl.Clean("evil \u202Etxt.exe\u202C\u200B"));
        Assert.Equal(MediaControl.MaxText, MediaControl.Clean(new string('x', 500)).Length);
        Assert.Equal("", MediaControl.Clean(null));
        var state = MediaControl.Parse("{\"title\":\"one\\ntwo\",\"artist\":\"\\u202Eme\"}")!;
        Assert.Equal("one two", state.Title);
        Assert.Equal("me", state.Artist);
    }

    [Theory]
    [InlineData("\"pause\"", "pause")]
    [InlineData("\"next\"", "next")]
    [InlineData("\"none\"", "none")]
    [InlineData("null", "none")]
    [InlineData("42", "none")]
    [InlineData("{oops", "none")]
    [InlineData(null, "none")]
    public void WhatACommandDidIsReadFromItsAnswer(string? json, string done)
        => Assert.Equal(done, MediaControl.Done(json));

    [Theory]
    [InlineData((int)MediaAction.Toggle, "toggle")]
    [InlineData((int)MediaAction.Play, "play")]
    [InlineData((int)MediaAction.Pause, "pause")]
    [InlineData((int)MediaAction.Next, "next")]
    [InlineData((int)MediaAction.Previous, "previous")]
    public void EachCommandNamesItsOwnFixedWord(int action, string word)
    {
        string js = MediaControl.CommandScript((MediaAction)action);
        Assert.EndsWith($"('{word}')", js.TrimEnd());
        Assert.DoesNotContain("ACTION", js, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommandIsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => MediaControl.CommandScript((MediaAction)99));

    [Fact]
    public void TheSitesOwnButtonsComeFirstAndYouTubesPlayerButtonsOnlyOnYouTube()
    {
        string js = MediaControl.CommandScript(MediaAction.Next);
        int handler = js.IndexOf("api.run(handler)", StringComparison.Ordinal);
        int button = js.IndexOf("button.click()", StringComparison.Ordinal);
        Assert.True(handler > 0 && button > handler, "the site's handler is tried before its player's button");
        Assert.Contains(@"/(^|\.)youtube\.com$/.test(location.hostname)", js, StringComparison.Ordinal);
        Assert.Contains("aria-disabled", js, StringComparison.Ordinal);
        Assert.Contains("ytButton('.ytp-next-button')", MediaControl.ReadScript, StringComparison.Ordinal);
        Assert.Contains("ytButton('.ytp-prev-button')", MediaControl.ReadScript, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHookCallsTheEnginesOwnFirstSoARefusedActionIsNeverNoted()
    {
        string hook = MediaControl.HookScript;
        int original = hook.IndexOf("original.call(this, action, handler)", StringComparison.Ordinal);
        int noted = hook.IndexOf("handlers.set(", StringComparison.Ordinal);
        Assert.True(original > 0 && noted > original);
        // Once per document, and only where the engine has media sessions at all.
        Assert.Contains("if (window[key] || typeof MediaSession !== 'function') return;", hook, StringComparison.Ordinal);
        Assert.Contains("} catch (e) { }", hook, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ what the panel shows

    [Fact]
    public void ThePanelShowsTheSongsOwnNameAndElseTheTabsTitleAndElseTheSite()
    {
        var song = new MediaState("Despacito", "Luis Fonsi", true, true, false, true, true);
        var view = NowPlayingView.Of(song, "Luis Fonsi - Despacito - YouTube", "https://www.youtube.com/watch?v=x", audible: true);
        Assert.Equal(new NowPlayingView("Despacito", "Luis Fonsi", "youtube.com", true, true, true, true), view);

        var untitled = NowPlayingView.Of(song with { Title = "", Artist = "" }, "Some \u202Epage\n", "https://example.com/", audible: true);
        Assert.Equal("Some page", untitled.Title);
        Assert.Equal("", untitled.Artist);

        Assert.Equal("example.com", NowPlayingView.Of(null, "  ", "https://www.example.com/", audible: false).Title);
    }

    [Fact]
    public void PlayingIsWhatThePageSaysElseWhatTheEngineHears()
    {
        var paused = new MediaState("", "", true, false, true, false, false);
        Assert.False(NowPlayingView.Of(paused, "t", "https://a.com/", audible: true).Playing);
        var silent = new MediaState("", "", true, true, false, false, false);
        Assert.True(NowPlayingView.Of(silent, "t", "https://a.com/", audible: false).Playing);
        Assert.True(NowPlayingView.Of(null, "t", "https://a.com/", audible: true).Playing);
        Assert.False(NowPlayingView.Of(null, "t", "https://a.com/", audible: false).Playing);
    }

    [Fact]
    public void ButtonsAreOnlyOfferedForMediaThePageHas()
    {
        // Before the page answers, nothing can be pressed.
        var first = NowPlayingView.Of(null, "t", "https://a.com/", audible: true);
        Assert.False(first.Controllable || first.CanNext || first.CanPrevious);
        // A page that says next but has no media is not believed.
        var claims = NowPlayingView.Of(new MediaState("", "", false, false, false, true, true), "t", "https://a.com/", audible: true);
        Assert.False(claims.Controllable || claims.CanNext || claims.CanPrevious);
        var plain = NowPlayingView.Of(new MediaState("", "", true, true, false, false, false), "t", "https://a.com/", audible: true);
        Assert.True(plain.Controllable);
        Assert.False(plain.CanNext || plain.CanPrevious);
    }

    [Fact]
    public void ASlowReadKeepsThePanelsButtonsForAFewSecondsAndOnlyForTheSameTab()
    {
        object tab = new(), other = new();
        var good = new MediaState("Song", "Band", true, true, false, true, true);
        var fresh = new MediaState("Next song", "", true, true, false, true, false);
        // A fresh answer is always the one used.
        Assert.Same(fresh, MainForm.KeptState(fresh, tab, (tab, good, Now), Now));
        // None in time: the last good one, with its buttons, but not its word on playing,
        // which is left to the engine.
        var kept = MainForm.KeptState(null, tab, (tab, good, Now.AddSeconds(-4)), Now);
        Assert.Equal(good with { Playing = false, Paused = false }, kept);
        Assert.True(NowPlayingView.Of(kept, "t", "https://a.com/", audible: false).CanNext);
        Assert.False(NowPlayingView.Of(kept, "t", "https://a.com/", audible: false).Playing);
        // Too old, another tab's, or none at all: nothing.
        Assert.Null(MainForm.KeptState(null, tab, (tab, good, Now - MainForm.MediaAnswerKept - TimeSpan.FromSeconds(1)), Now));
        Assert.Null(MainForm.KeptState(null, tab, (other, good, Now), Now));
        Assert.Null(MainForm.KeptState(null, tab, null, Now));
    }

    // ------------------------------------------------------------------ the toolbar

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheNoteJoinsTheToolbarNearestTheAddressBarOnlyWhileSomethingPlays(bool agentApi)
    {
        OnSta(() =>
        {
            using var form = MainForm.ForLayoutTest(new Settings { AgentServerEnabled = agentApi });
            int without = form.ToolbarButtons.Length;
            Assert.DoesNotContain(form.ToolbarButtons, b => b.AccessibleName?.StartsWith("Now playing", StringComparison.Ordinal) == true);

            form.SetMediaShown(true);
            var buttons = form.ToolbarButtons;
            Assert.Equal(without + 1, buttons.Length);
            Assert.Equal("Now playing", buttons[^1].AccessibleName);
            for (int i = 1; i < buttons.Length; i++)
            {
                Assert.True(buttons[i].Right <= buttons[i - 1].Left, $"button {i} at {buttons[i].Bounds} runs into {buttons[i - 1].Bounds}");
                Assert.True(buttons[i - 1].Left - buttons[i].Right <= 12, $"a gap between {buttons[i].Bounds} and {buttons[i - 1].Bounds}");
            }

            form.SetMediaShown(false);
            Assert.Equal(without, form.ToolbarButtons.Length);
        });
    }

    [Fact]
    public void EveryPageGetsTheHookAndEveryTabReportsItsSound()
    {
        string tab = Source(Path.Combine("Tabs", "Tab.cs"));
        Assert.Contains("core.IsDocumentPlayingAudioChanged += OnPlayingAudioChanged;", tab, StringComparison.Ordinal);
        Assert.Contains("await core.AddScriptToExecuteOnDocumentCreatedAsync(MediaControl.HookScript);", tab, StringComparison.Ordinal);
        // Reading what a tab plays never wakes a parked tab and never asks the browser's own pages.
        var read = Regex.Match(tab, @"ReadMediaAsync\(TimeSpan budget\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(read.Success);
        Assert.Contains("RefusalFor(Url, _navigationUnderWay?.Uri, CommittedUrl) is not null", read.Groups["body"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("WakeAsync", read.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentsWindowsAreNeverAskedAndNeverShowTheNote()
    {
        string form = Source(Path.Combine("UI", "MainForm.cs"));
        var tabs = Regex.Match(form, @"IEnumerable<Tab> PersonTabs\(AppSession session\)(?<body>.*?);", RegexOptions.Singleline);
        Assert.True(tabs.Success);
        Assert.Contains("!w.OpenedByAgent", tabs.Groups["body"].Value, StringComparison.Ordinal);
        var update = Regex.Match(form, @"private void UpdateMediaButton\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(update.Success);
        Assert.Contains("|| _openedByAgent)", update.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("SetMediaShown(false);", update.Groups["body"].Value, StringComparison.Ordinal);
    }
}
