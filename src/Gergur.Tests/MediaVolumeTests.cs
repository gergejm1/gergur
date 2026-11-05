using System.Text.Json;
using Gergur.Tabs;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The now-playing panel's volume: the page's side under node against a stand-in page (YouTube's
/// player and plain media elements), and the slider's sums. The panel on a real window, against a
/// real YouTube player, is the live harness in the scratchpad.
/// </summary>
public sealed class MediaVolumeTests
{
    private const string Harness = """
        const vm = require('vm');
        const READ = __READ__;
        const SETS = __SET__;
        const SET = level => SETS[level];

        function page(host, elements, player) {
          const ctx = vm.createContext({});
          vm.runInContext(`
            var window = globalThis;
            var navigator = {};
            var location = { hostname: ${JSON.stringify(host)} };
            var media = ${JSON.stringify(elements)}.map(([volume, muted, src]) => ({ volume, muted, paused: false, ended: false, readyState: 4, currentSrc: src === false ? '' : 'blob:x' }));
            var said = [];
            var player = ${JSON.stringify(player)} && {
              level: ${JSON.stringify(player && player.level)}, muted: ${JSON.stringify(player && player.muted)},
              getVolume() { return this.level; },
              setVolume(v) { said.push('set ' + v); this.level = v; },
              isMuted() { return this.muted; },
              unMute() { said.push('unmute'); this.muted = false; }
            };
            var document = {
              querySelectorAll: () => media,
              querySelector: selector => selector === '#movie_player' ? player : null
            };
          `, ctx);
          return { read: () => JSON.parse(JSON.stringify(vm.runInContext(READ, ctx))), set: l => vm.runInContext(SET(l), ctx), run: c => vm.runInContext(c, ctx) };
        }
        const results = {};

        // A plain page: read from what plays, set on every element it has, its own mute taken off.
        {
          const p = page('example.com', [[0.4, false], [1, true]], null);
          const read = p.read().volume;
          const did = p.set('0.25');
          results.plain = { read, did, levels: p.run('media.map(m => m.volume)'), muted: p.run('media.map(m => m.muted)') };
        }

        // Muted by the page reads as nothing, and setting nothing leaves its mute alone.
        {
          const p = page('example.com', [[0.7, true]], null);
          const read = p.read().volume;
          p.set('0');
          results.mutedPage = { read, level: p.run('media[0].volume'), stillMuted: p.run('media[0].muted') };
        }

        // YouTube: its player's own volume, read and set, not the element under it.
        {
          const p = page('www.youtube.com', [[0.1, false]], { level: 30, muted: false });
          const read = p.read().volume;
          const did = p.set('0.5');
          results.youtube = { read, did, said: p.run('said'), element: p.run('media[0].volume') };
        }

        // The player's mute comes off with a level, and stays with none.
        {
          const p = page('music.youtube.com', [[1, false]], { level: 80, muted: true });
          const read = p.read().volume;
          p.set('0');
          const afterNothing = p.run('said.slice()');
          p.set('0.5');
          results.youtubeMuted = { read, afterNothing, said: p.run('said') };
        }

        // Another site's #movie_player is not YouTube's, and nothing to set says so.
        {
          const fake = page('notyoutube.com', [[0.6, false]], { level: 5, muted: false });
          const read = fake.read().volume;
          fake.set('0.5');
          const none = page('example.com', [], null);
          const unloaded = page('example.com', [[1, false, false]], null);
          results.others = { read, element: fake.run('media[0].volume'), said: fake.run('said'),
            noneRead: none.read().volume === undefined || none.read().volume === null, noneDid: none.set('0.5'), unloadedDid: unloaded.set('0.5') };
        }
        console.log(JSON.stringify(results));
        """;

    private static readonly Lazy<JsonElement> Run = new(() =>
    {
        string sets = "{" + string.Join(",", new[] { 0, 0.25, 0.5 }.Select(l =>
            $"{JsonSerializer.Serialize(l.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))}:{JsonSerializer.Serialize(MediaControl.VolumeScript(l))}")) + "}";
        string js = Harness
            .Replace("__READ__", JsonSerializer.Serialize(MediaControl.ReadScript), StringComparison.Ordinal)
            .Replace("__SET__", sets, StringComparison.Ordinal);
        return JsonDocument.Parse(MediaScriptTests.RunNode(js)).RootElement.Clone();
    });

    private static JsonElement Of(string name) => Run.Value.GetProperty(name);

    [Fact]
    public void APlainPageIsReadFromWhatPlaysAndSetOnEveryElement()
    {
        var r = Of("plain");
        Assert.Equal(0.4, r.GetProperty("read").GetDouble());
        Assert.Equal("volume", r.GetProperty("did").GetString());
        Assert.Equal([0.25, 0.25], r.GetProperty("levels").EnumerateArray().Select(e => e.GetDouble()));
        Assert.Equal([false, false], r.GetProperty("muted").EnumerateArray().Select(e => e.GetBoolean()));
    }

    [Fact]
    public void APageItselfMutedReadsAsNothingAndNothingLeavesItMuted()
    {
        var r = Of("mutedPage");
        Assert.Equal(0, r.GetProperty("read").GetDouble());
        Assert.Equal(0, r.GetProperty("level").GetDouble());
        Assert.True(r.GetProperty("stillMuted").GetBoolean());
    }

    [Fact]
    public void YouTubesPlayerKeepsTheVolumeNotTheElementUnderIt()
    {
        var r = Of("youtube");
        Assert.Equal(0.3, r.GetProperty("read").GetDouble());
        Assert.Equal("volume", r.GetProperty("did").GetString());
        Assert.Equal(["set 50"], r.GetProperty("said").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0.1, r.GetProperty("element").GetDouble());
    }

    [Fact]
    public void YouTubesMuteComesOffWithALevelAndStaysWithNone()
    {
        var r = Of("youtubeMuted");
        Assert.Equal(0, r.GetProperty("read").GetDouble());
        Assert.Equal(["set 0"], r.GetProperty("afterNothing").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["set 0", "set 50", "unmute"], r.GetProperty("said").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void AnotherSitesPlayerIsNotYouTubesAndNothingToSetSaysSo()
    {
        var r = Of("others");
        Assert.Equal(0.6, r.GetProperty("read").GetDouble());
        Assert.Equal(0.5, r.GetProperty("element").GetDouble());
        Assert.Empty(r.GetProperty("said").EnumerateArray());
        Assert.True(r.GetProperty("noneRead").GetBoolean());
        Assert.Equal("none", r.GetProperty("noneDid").GetString());
        Assert.Equal("none", r.GetProperty("unloadedDid").GetString());
    }

    [Theory]
    [InlineData(-1, "0")]
    [InlineData(0, "0")]
    [InlineData(0.5, "0.5")]
    [InlineData(2, "1")]
    [InlineData(double.NaN, "0")]
    public void TheLevelInTheScriptIsAlwaysANumberAVolumeCanBe(double level, string written)
        => Assert.EndsWith($"({written})", MediaControl.VolumeScript(level).TrimEnd(), StringComparison.Ordinal);

    [Fact]
    public void ThePagesVolumeIsReadOnlyWhenItIsOne()
    {
        static double? Read(string volume) => MediaControl.Parse($$"""{"media":true,"volume":{{volume}}}""")!.Volume;
        Assert.Equal(0.5, Read("0.5"));
        Assert.Equal(0, Read("0"));
        Assert.Equal(1, Read("1"));
        Assert.Null(Read("1.5"));
        Assert.Null(Read("-0.1"));
        Assert.Null(Read("\"loud\""));
        Assert.Null(Read("null"));
        Assert.Null(MediaControl.Parse("""{"media":true}""")!.Volume);
    }

    [Fact]
    public void ThePanelOffersAVolumeOnlyForMediaThePageHas()
    {
        var song = new MediaState("Song", "", true, true, false, false, false, 0.4);
        Assert.Equal(0.4, NowPlayingView.Of(song, "t", "https://a.com/", true).Volume);
        Assert.Null(NowPlayingView.Of(song with { HasMedia = false }, "t", "https://a.com/", true).Volume);
        Assert.Null(NowPlayingView.Of(null, "t", "https://a.com/", true).Volume);
        Assert.True(NowPlayingView.Of(null, "t", "https://a.com/", true, tabMuted: true).TabMuted);
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(50, 0)]
    [InlineData(200, 0.5)]
    [InlineData(300, 1)]
    [InlineData(900, 1)]
    public void TheSliderRunsFromNothingToFullAndHoldsAtItsEnds(int x, double level)
        => Assert.Equal(level, NowPlayingFlyout.LevelAt(x, new Rectangle(100, 0, 200, 4)));

    [Fact]
    public void ASliderWithNoWidthIsNothing()
        => Assert.Equal(0, NowPlayingFlyout.LevelAt(5, new Rectangle(0, 0, 0, 4)));

    [Fact]
    public void ALevelJustSetStandsUntilThePageCatchesUp()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var until = now.AddSeconds(1);
        Assert.Equal(0.2, NowPlayingFlyout.ShownLevel(0.9, dragging: false, held: 0.2, until, now));
        Assert.Equal(0.9, NowPlayingFlyout.ShownLevel(0.9, dragging: false, held: 0.2, until, now.AddSeconds(2)));
        // Held for as long as it is being dragged, however long that is.
        Assert.Equal(0.2, NowPlayingFlyout.ShownLevel(0.9, dragging: true, held: 0.2, until, now.AddSeconds(2)));
        // A page with no volume has nothing to show, whatever was held.
        Assert.Null(NowPlayingFlyout.ShownLevel(null, dragging: true, held: 0.2, until, now));
    }

    [Fact]
    public void ThePanelSetsTheLevelAndMutesThroughWhatItWasGiven()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var levels = new List<double>();
                int mutes = 0;
                using var panel = new NowPlayingFlyout(_ => { }, () => { }, () => { }, () => { }, levels.Add, () => mutes++);
                panel.SetView(new NowPlayingView("Song", "Band", "youtube.com", true, true, true, true, Volume: 0.5));
                panel.Relayout(keepPlace: false);
                var mute = panel.Spots.Single(s => s.Spoken == "Mute tab");
                Assert.True(mute.KeepOpen);
                panel.RunSpot(mute);
                Assert.Equal(1, mutes);
                panel.SetLevel(0.25);
                panel.SetLevel(3);
                panel.SetLevel(-1);
                Assert.Equal([0.25, 1.0, 0.0], levels);

                panel.SetView(new NowPlayingView("Song", "Band", "youtube.com", true, true, true, true, Volume: 0.5, TabMuted: true));
                panel.Relayout(keepPlace: false);
                Assert.Contains(panel.Spots, s => s.Spoken == "Unmute tab");
                Assert.DoesNotContain(panel.Spots, s => s.Spoken == "Mute tab");
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

    [Fact]
    public void TheSliderAndTheMuteActOnlyOnTheTabThePanelDescribes()
    {
        string form = File.ReadAllText(OwnPagesHardeningTests.FindSource("Gergur", "UI", "MainForm.cs"));
        int set = form.IndexOf("private async Task SetMediaVolumeAsync(double level)", StringComparison.Ordinal);
        int mute = form.IndexOf("private void ToggleMediaMute()", StringComparison.Ordinal);
        Assert.True(set >= 0 && mute >= 0);
        int sets = form.IndexOf("tab.SetMediaVolumeAsync(next", set, StringComparison.Ordinal);
        int mutes = form.IndexOf("tab.IsMuted = !tab.IsMuted", mute, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(_mediaPanelTab, tab)", form[set..sets], StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(_mediaPanelTab, tab)", form[mute..mutes], StringComparison.Ordinal);
    }
}
