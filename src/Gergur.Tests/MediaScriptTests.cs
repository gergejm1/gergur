using System.Diagnostics;
using System.Text.Json;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The now-playing scripts (<see cref="MediaControl"/>) run for real, under node, against a
/// stand-in for the parts of a page they touch: the engine's MediaSession (which refuses an
/// action it does not know, as Chromium does), media elements, YouTube's player buttons and the
/// page's address. Each case gets a fresh realm (node's vm), since the hook installs once per
/// page and cannot be taken away. The real engine and real YouTube are the live harness in
/// the scratchpad; this is what keeps a broken script from passing the suite.
/// </summary>
public sealed class MediaScriptTests
{
    private const string Harness = """
        const vm = require('vm');
        const HOOK = __HOOK__;
        const READ = __READ__;
        const COMMAND = __COMMAND__;
        const results = {};

        // A page: host, media elements as [paused, readyState], YouTube buttons as {selector: 'shown'|'hidden'|'disabled'}.
        function page(host, elements, buttons) {
          const calls = [];
          const ctx = vm.createContext({ calls });
          vm.runInContext(`
            var window = globalThis;
            class MediaSession {
              constructor() { this.metadata = null; this.playbackState = 'none'; this.native = new Map(); }
              setActionHandler(action, handler) {
                if (!['play', 'pause', 'nexttrack', 'previoustrack', 'seekto', 'stop'].includes(action))
                  throw new TypeError('The provided value is not a valid enum value of type MediaSessionAction.');
                calls.push('native ' + action + ' ' + (handler ? 'set' : 'cleared'));
                if (handler) this.native.set(action, handler); else this.native.delete(action);
              }
            }
            var navigator = { mediaSession: new MediaSession() };
            var location = { hostname: ${JSON.stringify(host)} };
            class Media {
              constructor(paused, readyState) { this.paused = paused; this.ended = false; this.readyState = readyState; this.currentSrc = 'blob:x'; this.currentTime = 42; }
              play() { this.paused = false; calls.push('element play'); return Promise.resolve(); }
              pause() { this.paused = true; calls.push('element pause'); }
            }
            var media = ${JSON.stringify(elements)}.map(([p, r]) => new Media(p, r));
            var buttons = ${JSON.stringify(buttons)};
            var document = {
              querySelectorAll: () => media,
              querySelector: selector => buttons[selector] ? {
                offsetParent: buttons[selector] === 'hidden' ? null : {},
                getAttribute: name => name === 'aria-disabled' ? String(buttons[selector] === 'disabled') : null,
                click: () => calls.push('clicked ' + selector)
              } : null
            };
            class MediaMetadata { constructor(m) { Object.assign(this, m); } }
          `, ctx);
          return {
            calls,
            run: code => vm.runInContext(code, ctx),
            read: () => JSON.parse(JSON.stringify(vm.runInContext(READ, ctx))),
            command: action => vm.runInContext(COMMAND[action], ctx)
          };
        }

        // The site's handlers are noted as it hands them over, and pressed.
        {
          const p = page('example.com', [], {});
          p.run(HOOK);
          p.run(`
            var got = [];
            for (const a of ['play', 'pause', 'nexttrack']) navigator.mediaSession.setActionHandler(a, d => got.push(d.action));
            navigator.mediaSession.metadata = new MediaMetadata({ title: 'Song', artist: 'Band' });
          `);
          const before = p.read();
          const next = p.command('next');
          const previous = p.command('previous');
          results.handlers = { before, next, previous, got: p.run('got'), engineToldToo: p.calls.filter(c => c.startsWith('native')).length };
        }

        // An action the engine refuses throws for the page as before and is never noted; a null
        // handler takes one away; a second run of the hook changes nothing.
        {
          const p = page('example.com', [], {});
          p.run(HOOK);
          p.run(HOOK);
          let threw = '';
          try { p.run(`navigator.mediaSession.setActionHandler('skipthis', () => 0)`); } catch (e) { threw = e.name; }
          p.run(`navigator.mediaSession.setActionHandler('nexttrack', () => 0)`);
          const withNext = p.read().next;
          p.run(`navigator.mediaSession.setActionHandler('nexttrack', null)`);
          results.refusals = {
            threw, withNext, afterNull: p.read().next,
            noted: p.run(`window[Symbol.for('gergur.media')].has('skipthis')`),
            nativeCalls: p.calls.length,
            frozen: p.run(`(() => { try { window[Symbol.for('gergur.media')] = null; } catch (e) { } return !!window[Symbol.for('gergur.media')]; })()`)
          };
        }

        // A handler that throws falls through to the page's own media; toggle picks by what plays.
        {
          const p = page('example.com', [[false, 4]], {});
          p.run(HOOK);
          p.run(`navigator.mediaSession.setActionHandler('pause', () => { throw new Error('site bug'); })`);
          const pause = p.command('toggle');
          const pausedAfter = p.run('media[0].paused');
          const play = p.command('toggle');
          results.fallback = { pause, pausedAfter, play, playingAfter: !p.run('media[0].paused'), calls: p.calls.filter(c => c.startsWith('element')) };
        }

        // No media at all, or media not loaded: nothing to drive. Previous with no handler starts the song again.
        {
          const empty = page('example.com', [[true, 0]], {});
          empty.run(HOOK);
          const restart = page('example.com', [[false, 4]], {});
          restart.run(HOOK);
          const done = restart.command('previous');
          results.plain = { unloaded: empty.read(), unloadedPlay: empty.command('play'), previous: done, at: restart.run('media[0].currentTime') };
        }

        // The page says it is playing through the session alone (an Audio never put in the page).
        {
          const p = page('example.com', [], {});
          p.run(HOOK);
          p.run(`navigator.mediaSession.setActionHandler('pause', () => calls.push('site pause')); navigator.mediaSession.playbackState = 'playing'`);
          const playing = p.read().playing;
          results.session = { playing, toggle: p.command('toggle'), calls: p.calls.filter(c => c.startsWith('site')) };
        }

        // YouTube's own Next and Previous when no handler was handed over: only on YouTube, only shown and enabled.
        {
          const yt = page('www.youtube.com', [[false, 4]], { '.ytp-next-button': 'shown', '.ytp-prev-button': 'hidden' });
          yt.run(HOOK);
          const read = yt.read();
          results.youtube = { next: read.next, previous: read.previous, pressNext: yt.command('next'), clicked: yt.calls.filter(c => c.startsWith('clicked')) };
          const music = page('music.youtube.com', [[false, 4]], { '.ytp-next-button': 'shown' });
          music.run(HOOK);
          results.youtubeMusic = music.read().next;
          const fake = page('notyoutube.com', [[false, 4]], { '.ytp-next-button': 'shown' });
          fake.run(HOOK);
          const fakeRead = fake.read();
          results.notYoutube = { next: fakeRead.next, press: fake.command('next'), clicked: fake.calls.filter(c => c.startsWith('clicked')).length };
          const fakeSub = page('youtube.com.evil.example', [[false, 4]], { '.ytp-next-button': 'shown' });
          fakeSub.run(HOOK);
          results.lookalike = fakeSub.read().next;
          const off = page('www.youtube.com', [[false, 4]], { '.ytp-next-button': 'disabled' });
          off.run(HOOK);
          results.disabled = { next: off.read().next, press: off.command('next') };
        }

        // Without the engine's MediaSession the hook stands aside and the scripts still answer.
        {
          const ctx = vm.createContext({});
          vm.runInContext(`var window = globalThis; var navigator = {}; var location = { hostname: 'x' }; var document = { querySelectorAll: () => [], querySelector: () => null };`, ctx);
          vm.runInContext(HOOK, ctx);
          results.noSession = { hooked: vm.runInContext(`!!window[Symbol.for('gergur.media')]`, ctx), read: JSON.parse(JSON.stringify(vm.runInContext(READ, ctx))), press: vm.runInContext(COMMAND.next, ctx) };
        }

        console.log(JSON.stringify(results));
        """;

    private static JsonElement Results()
    {
        string commands = "{" + string.Join(",", Enum.GetValues<MediaAction>().Select(a =>
            $"{JsonSerializer.Serialize(a.ToString().ToLowerInvariant())}:{JsonSerializer.Serialize(MediaControl.CommandScript(a))}")) + "}";
        string js = Harness
            .Replace("__HOOK__", JsonSerializer.Serialize(MediaControl.HookScript), StringComparison.Ordinal)
            .Replace("__READ__", JsonSerializer.Serialize(MediaControl.ReadScript), StringComparison.Ordinal)
            .Replace("__COMMAND__", commands, StringComparison.Ordinal);
        return JsonDocument.Parse(RunNode(js)).RootElement.Clone();
    }

    private static readonly Lazy<JsonElement> Run = new(Results);

    private static JsonElement Of(string name) => Run.Value.GetProperty(name);

    [Fact]
    public void TheSitesHandlersAreNotedAsItHandsThemOverAndPressed()
    {
        var r = Of("handlers");
        var before = r.GetProperty("before");
        Assert.Equal("Song", before.GetProperty("title").GetString());
        Assert.Equal("Band", before.GetProperty("artist").GetString());
        Assert.True(before.GetProperty("media").GetBoolean());   // handlers alone are media
        Assert.True(before.GetProperty("next").GetBoolean());
        Assert.False(before.GetProperty("previous").GetBoolean());
        Assert.Equal("next", r.GetProperty("next").GetString());
        // No "previous" handler and nothing to start again: nothing done, nothing claimed.
        Assert.Equal("none", r.GetProperty("previous").GetString());
        Assert.Equal(["nexttrack"], r.GetProperty("got").EnumerateArray().Select(e => e.GetString()));
        // The engine is still told, every time: the media keys keep working.
        Assert.Equal(3, r.GetProperty("engineToldToo").GetInt32());
    }

    [Fact]
    public void ARefusedActionThrowsForThePageAsBeforeAndIsNeverNoted()
    {
        var r = Of("refusals");
        Assert.Equal("TypeError", r.GetProperty("threw").GetString());
        Assert.False(r.GetProperty("noted").GetBoolean());
        Assert.True(r.GetProperty("withNext").GetBoolean());
        Assert.False(r.GetProperty("afterNull").GetBoolean());
        // The hook run a second time on the same page changes nothing: each call still reaches
        // the engine exactly once, and the refused one never did.
        Assert.Equal(2, r.GetProperty("nativeCalls").GetInt32());
        Assert.True(r.GetProperty("frozen").GetBoolean());
    }

    [Fact]
    public void AHandlerThatThrowsFallsThroughToThePagesOwnMedia()
    {
        var r = Of("fallback");
        Assert.Equal("pause", r.GetProperty("pause").GetString());
        Assert.True(r.GetProperty("pausedAfter").GetBoolean());
        Assert.Equal("play", r.GetProperty("play").GetString());
        Assert.True(r.GetProperty("playingAfter").GetBoolean());
        Assert.Equal(["element pause", "element play"], r.GetProperty("calls").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void MediaNotLoadedIsNothingToDriveAndPreviousStartsASongAgain()
    {
        var r = Of("plain");
        Assert.False(r.GetProperty("unloaded").GetProperty("media").GetBoolean());
        Assert.Equal("none", r.GetProperty("unloadedPlay").GetString());
        Assert.Equal("previous", r.GetProperty("previous").GetString());
        Assert.Equal(0, r.GetProperty("at").GetInt32());
    }

    [Fact]
    public void APageThatPlaysThroughTheSessionAloneIsPausedThroughIt()
    {
        var r = Of("session");
        Assert.True(r.GetProperty("playing").GetBoolean());
        Assert.Equal("pause", r.GetProperty("toggle").GetString());
        Assert.Equal(["site pause"], r.GetProperty("calls").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void YouTubesOwnButtonsAreUsedOnlyOnYouTubeAndOnlyWhenShownAndEnabled()
    {
        var yt = Of("youtube");
        Assert.True(yt.GetProperty("next").GetBoolean());
        Assert.False(yt.GetProperty("previous").GetBoolean());   // hidden on the first song
        Assert.Equal("next", yt.GetProperty("pressNext").GetString());
        Assert.Equal(["clicked .ytp-next-button"], yt.GetProperty("clicked").EnumerateArray().Select(e => e.GetString()));
        Assert.True(Of("youtubeMusic").GetBoolean());

        var fake = Of("notYoutube");
        Assert.False(fake.GetProperty("next").GetBoolean());
        Assert.Equal("none", fake.GetProperty("press").GetString());
        Assert.Equal(0, fake.GetProperty("clicked").GetInt32());
        Assert.False(Of("lookalike").GetBoolean());

        var off = Of("disabled");
        Assert.False(off.GetProperty("next").GetBoolean());
        Assert.Equal("none", off.GetProperty("press").GetString());
    }

    [Fact]
    public void WithoutTheEnginesMediaSessionTheHookStandsAsideAndTheScriptsStillAnswer()
    {
        var r = Of("noSession");
        Assert.False(r.GetProperty("hooked").GetBoolean());
        Assert.False(r.GetProperty("read").GetProperty("media").GetBoolean());
        Assert.Equal("none", r.GetProperty("press").GetString());
    }

    /// <summary>
    /// Runs the text under node and returns what it wrote to standard output. Node is a hard
    /// requirement, as it is for checks.sh: passing without it would prove nothing.
    /// </summary>
    internal static string RunNode(string javascript)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gergur-media-{Guid.NewGuid():n}.js");
        File.WriteAllText(path, javascript);
        try
        {
            Process? node;
            try
            {
                node = Process.Start(new ProcessStartInfo("node", $"\"{path}\"")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Fail("node is needed to run the page scripts under test and was not found on PATH (checks.sh needs it too)");
                throw;
            }
            using (node)
            {
                Assert.NotNull(node);
                var printed = node.StandardOutput.ReadToEndAsync();
                var complaint = node.StandardError.ReadToEndAsync();
                Assert.True(node.WaitForExit(30_000), "node did not finish");
                Assert.True(Task.WaitAll([printed, complaint], 5_000), "node did not close its pipes");
                Assert.True(node.ExitCode == 0, complaint.Result);
                return printed.Result;
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* a temp file that outlives the run is not a failure */ }
        }
    }
}
