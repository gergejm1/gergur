using System.Text.Json;
using Gergur.Music;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The song recognizer's page script (<see cref="SongPage.TakeScript"/>) run for real under
/// node, against a stand-in page: YouTube's player, a video whose <c>captureStream</c> hands
/// out tracks, and an audio context whose processor the test feeds by hand, with the clock in
/// the test's hands too. Its answers go through <see cref="SongPage.Parse"/>, so the whole
/// chain from the page's samples to the browser's is checked. Real YouTube is the live run.
/// </summary>
public sealed class SongScriptTests
{
    private const string Harness = """
        const vm = require('vm');
        const LISTEN = __LISTEN__, LOOK = __LOOK__, STOP = __STOP__;
        const out = {};

        function page() {
          const ctx = vm.createContext({ btoa, URLSearchParams, Float32Array, Int16Array, Uint8Array, out });
          vm.runInContext(`
            var window = globalThis;
            var now = 1000;
            Date.now = () => now;
            var location = { hostname: 'www.youtube.com', search: '?v=aaaaaaaaaaa' };
            var ad = false;
            var player = {
              classList: { contains: c => c === 'ad-showing' && ad },
              getPlayerResponse: () => ({ videoDetails: { videoId: 'aaaaaaaaaaa', title: 'Band - Song', author: 'BandVEVO', lengthSeconds: '200', isLiveContent: false },
                microformat: { playerMicroformatRenderer: { category: 'Music' } } })
            };
            var navigator = { mediaSession: { metadata: { title: 'Song', artist: 'Band' } } };
            var procs = [], contexts = [], stopped = 0, listeners = {};
            class Track { stop() { stopped++; } }
            var tracks = [new Track()];
            var stream = { getAudioTracks: () => tracks, getTracks: () => tracks, addEventListener: (n, f) => { listeners[n] = f; } };
            var video = { paused: false, ended: false, captureStream: () => stream };
            var document = { getElementById: id => id === 'movie_player' ? player : null, querySelectorAll: () => [video] };
            class MediaStream { constructor(t) { this.t = t; } }
            class AudioContext {
              constructor(o) { this.rate = o.sampleRate; this.state = 'running'; this.destination = {}; this.sources = 0; contexts.push(this); }
              createScriptProcessor() { const p = { connect() {}, disconnect() {}, onaudioprocess: null }; procs.push(p); return p; }
              createGain() { return { gain: { value: 1 }, connect() {}, disconnect() {} }; }
              createMediaStreamSource() { this.sources++; return { connect() {}, disconnect() {} }; }
              resume() { return Promise.resolve(); }
              close() { this.state = 'closed'; }
            }
            function feed(values) {
              const p = procs[procs.length - 1];
              if (!p.onaudioprocess) return false;
              const d = Float32Array.from(values);
              p.onaudioprocess({ inputBuffer: { getChannelData: () => d } });
              return true;
            }
          `, ctx);
          return c => vm.runInContext(c, ctx);
        }

        // Looking only: what the player says, and no listener.
        {
          const run = page();
          out.look = run(LOOK);
          out.lookContexts = run('contexts.length');
        }

        // Listening: samples taken in order, by video; silence and ads not heard; drained once taken.
        {
          const run = page();
          out.started = run(LISTEN);
          out.rate = run('contexts[0].rate');
          run('feed([0.5, -0.5, 0, 1.0])');
          run('feed([0, 0, 0, 0])');
          run('ad = true; feed([0.9, 0.9])');
          run('ad = false; feed([-1.0])');
          run("location.search = '?v=bbbbbbbbbbb'; feed([0.25])");
          out.taken = run(LOOK);
          out.drained = run(LOOK);
          out.contexts = run('contexts.length');
          run("listeners.addtrack()");
          out.sourcesAfterNextSong = run('contexts[0].sources');
        }

        // Holds at most KEEP seconds when nobody takes it: the oldest goes.
        {
          const run = page();
          run(LISTEN);
          run('for (let i = 0; i < 60; i++) feed(new Array(4096).fill(0.1))');
          out.kept = run(LOOK);
        }

        // Stops itself when not taken from for the idle time: closed, tracks stopped, gone.
        {
          const run = page();
          run(LISTEN);
          run('now += 15001; feed([0.5])');
          out.idle = { closed: run('contexts[0].state'), stopped: run('stopped'), hook: run("typeof window[Symbol.for('gergur.music')]"), after: run(LOOK) };
        }

        // Stopped when asked.
        {
          const run = page();
          run(LISTEN);
          run(STOP);
          out.stop = { closed: run('contexts[0].state'), stopped: run('stopped'), hook: run("typeof window[Symbol.for('gergur.music')]") };
        }

        // Nothing playing: nothing to listen to.
        {
          const run = page();
          run('video.paused = true');
          out.paused = run(LISTEN);
          out.pausedContexts = run('contexts.length');
        }

        // A page that put something of its own where the listener goes: no throw, no listening.
        {
          const run = page();
          run("Object.defineProperty(window, Symbol.for('gergur.music'), { value: 5, configurable: false })");
          out.squatted = run(LISTEN);
          const run2 = page();
          run2("Object.defineProperty(window, Symbol.for('gergur.music'), { get() { throw new Error('no'); } })");
          out.throwing = run2(LISTEN);
        }

        console.log(JSON.stringify(out));
        """;

    private static readonly Lazy<JsonElement> Run = new(() =>
    {
        string js = Harness
            .Replace("__LISTEN__", JsonSerializer.Serialize(SongPage.TakeScript(true)))
            .Replace("__LOOK__", JsonSerializer.Serialize(SongPage.TakeScript(false)))
            .Replace("__STOP__", JsonSerializer.Serialize(SongPage.StopScript));
        return JsonDocument.Parse(MediaScriptTests.RunNode(js)).RootElement.Clone();
    });

    private static JsonElement Of(string name) => Run.Value.GetProperty(name);

    /// <summary>A script's answer as the engine hands it over (JSON of the string), read the browser's way.</summary>
    private static PageHearing Read(string name)
    {
        var page = SongPage.Parse(JsonSerializer.Serialize(Of(name).GetString()));
        Assert.NotNull(page);
        return page;
    }

    [Fact]
    public void LookingReadsThePlayerAndStartsNothing()
    {
        var page = Read("look");
        Assert.Equal("www.youtube.com", page.Host);
        Assert.Equal("aaaaaaaaaaa", page.VideoId);
        Assert.Equal(new PlayerAbout("aaaaaaaaaaa", "Band - Song", "BandVEVO", 200, false, "Music", ""), page.About);
        Assert.Equal("Song", page.SessionTitle);
        Assert.Equal("Band", page.SessionArtist);
        Assert.False(page.Listening);
        Assert.Equal(0, Of("lookContexts").GetInt32());
        Assert.Equal("Song", SongPage.Judge(page)?.Title);
    }

    [Fact]
    public void WhatIsHeardComesOverInOrderByVideoWithoutSilenceOrAds()
    {
        var started = Read("started");
        Assert.True(started.Listening);
        Assert.Equal("running", started.AudioState);
        Assert.Empty(started.Parts);
        Assert.Matches("^[a-z0-9]{4,10}$", started.Ear);
        Assert.Equal(AudioFingerprint.SampleRate, Of("rate").GetInt32());

        var taken = Read("taken");
        Assert.Equal(started.Ear, taken.Ear);   // the same listener throughout
        Assert.Equal(2, taken.Parts.Count);
        Assert.Equal("aaaaaaaaaaa", taken.Parts[0].VideoId);
        // 0.5 and -0.5 round the way the page rounds; the silent block and the ad are not there.
        Assert.Equal([16384, -16383, 0, 32767, -32767], taken.Parts[0].Samples);
        Assert.Equal("bbbbbbbbbbb", taken.Parts[1].VideoId);
        Assert.Equal([8192], taken.Parts[1].Samples);

        Assert.Empty(Read("drained").Parts);
        Assert.Equal(1, Of("contexts").GetInt32());   // one listener, reused
        Assert.Equal(2, Of("sourcesAfterNextSong").GetInt32());   // a playlist's next track is listened to as well
    }

    [Fact]
    public void ThePageHoldsAtMostItsShareWhenNobodyTakesIt()
    {
        var kept = Read("kept");
        int total = kept.Parts.Sum(p => p.Samples.Length);
        int most = SongPage.KeptSeconds * AudioFingerprint.SampleRate;
        Assert.InRange(total, most - 4096, most);
        Assert.All(kept.Parts.SelectMany(p => p.Samples), s => Assert.Equal((short)3277, s));
    }

    [Fact]
    public void AListenerNobodyTakesFromStopsItself()
    {
        var idle = Of("idle");
        Assert.Equal("closed", idle.GetProperty("closed").GetString());
        Assert.Equal(1, idle.GetProperty("stopped").GetInt32());
        Assert.Equal("undefined", idle.GetProperty("hook").GetString());
        var after = SongPage.Parse(JsonSerializer.Serialize(idle.GetProperty("after").GetString()));
        Assert.False(after!.Listening);

        var stop = Of("stop");
        Assert.Equal("closed", stop.GetProperty("closed").GetString());
        Assert.Equal(1, stop.GetProperty("stopped").GetInt32());
        Assert.Equal("undefined", stop.GetProperty("hook").GetString());
    }

    [Fact]
    public void NothingPlayingOrAPageInTheWayMeansNoListening()
    {
        Assert.False(Read("paused").Listening);
        Assert.Equal(0, Of("pausedContexts").GetInt32());
        Assert.False(Read("squatted").Listening);
        Assert.False(Read("throwing").Listening);
    }
}
