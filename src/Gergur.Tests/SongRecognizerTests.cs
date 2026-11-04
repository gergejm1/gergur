using System.Text.Json;
using Gergur.Music;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The song recognizer away from any page: names from video titles, which pages are songs,
/// reading the page's answer, the library on disk, matching synthetic songs under noise, and
/// the listener learning and answering with stand-in tabs. Real songs and real YouTube are the
/// live run in the scratchpad.
/// </summary>
public sealed class SongRecognizerTests : IDisposable
{
    private const int Rate = AudioFingerprint.SampleRate;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gergur-songs-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    // ---- names ----

    [Theory]
    [InlineData("Luis Fonsi - Despacito ft. Daddy Yankee", "LuisFonsiVEVO", "Despacito ft. Daddy Yankee", "Luis Fonsi")]
    [InlineData("Ed Sheeran - Shape of You (Official Music Video)", "Ed Sheeran", "Shape of You", "Ed Sheeran")]
    [InlineData("Shape of You", "Ed Sheeran - Topic", "Shape of You", "Ed Sheeran")]
    [InlineData("Some - Title", "Band - Topic", "Some - Title", "Band")]
    [InlineData("PSY - GANGNAM STYLE M/V", "officialpsy", "GANGNAM STYLE", "PSY")]
    [InlineData("Song [Lyrics]", "Singer", "Song", "Singer")]
    [InlineData("Bohemian Rhapsody (Remastered 2011)", "Queen Official", "Bohemian Rhapsody", "Queen Official")]
    [InlineData("Live at the Hall (Part 2)", "Band", "Live at the Hall (Part 2)", "Band")]
    [InlineData("Song", "VEVO", "Song", "VEVO")]
    public void NamesComeFromTheVideoTitleAndChannel(string title, string channel, string song, string artist)
    {
        var (t, a) = SongNames.FromVideo(title, channel);
        Assert.Equal(song, t);
        Assert.Equal(artist, a);
    }

    // ---- which pages are songs ----

    private static PageHearing Page(string host = "www.youtube.com", string id = "kJQP7kiw5Fk", string? aboutId = null,
        string category = "Music", int seconds = 282, bool live = false, string sessionTitle = "", string sessionArtist = "", bool noAbout = false)
        => new(host, id, noAbout ? null : new PlayerAbout(aboutId ?? id, "Luis Fonsi - Despacito ft. Daddy Yankee", "LuisFonsiVEVO", seconds, live, category, ""),
            sessionTitle, sessionArtist, false, "", []);

    [Fact]
    public void AYouTubeVideoFiledUnderMusicIsASong()
    {
        var info = SongPage.Judge(Page());
        Assert.NotNull(info);
        Assert.Equal("youtube:kJQP7kiw5Fk", info.Key);
        Assert.Equal("Despacito ft. Daddy Yankee", info.Title);
        Assert.Equal("Luis Fonsi", info.Artist);
        Assert.Equal("youtube.com", info.Site);
        Assert.Equal(282, info.Seconds);
        Assert.NotNull(SongPage.Judge(Page(host: "m.youtube.com")));
        Assert.NotNull(SongPage.Judge(Page(host: "YouTube.com")));
    }

    [Fact]
    public void WhatIsNotASongIsNotLearned()
    {
        Assert.Null(SongPage.Judge(Page(category: "Education")));          // a talk
        Assert.Null(SongPage.Judge(Page(live: true)));                     // a live stream
        Assert.Null(SongPage.Judge(Page(seconds: SongPage.LongestSong + 1))); // a mix or an album
        Assert.Null(SongPage.Judge(Page(seconds: 0)));                     // length not known on youtube.com
        Assert.Null(SongPage.Judge(Page(aboutId: "dQw4w9WgXcQ")));         // the player still on the last song
        Assert.Null(SongPage.Judge(Page(id: "")));                         // no video
        Assert.Null(SongPage.Judge(Page(id: "short")));                    // not an id
        Assert.Null(SongPage.Judge(Page(noAbout: true)));                  // no player
        Assert.Null(SongPage.Judge(Page(host: "www.youtube.com.evil.example")));
        Assert.Null(SongPage.Judge(Page(host: "soundcloud.com")));
        Assert.NotNull(SongPage.Judge(Page(seconds: SongPage.LongestSong)));
    }

    [Fact]
    public void YouTubeMusicIsASongWithTheNamesItGivesWindows()
    {
        var info = SongPage.Judge(Page(host: "music.youtube.com", category: "", sessionTitle: "Despacito", sessionArtist: "Luis Fonsi & Daddy Yankee"));
        Assert.NotNull(info);
        Assert.Equal("Despacito", info.Title);
        Assert.Equal("Luis Fonsi & Daddy Yankee", info.Artist);
        Assert.Equal("music.youtube.com", info.Site);
        // The same video on either site is one song.
        Assert.Equal(SongPage.Judge(Page())!.Key, info.Key);
        // Without names from the page, the video's own.
        Assert.Equal("Despacito ft. Daddy Yankee", SongPage.Judge(Page(host: "music.youtube.com", category: ""))!.Title);
        Assert.Null(SongPage.Judge(Page(host: "music.youtube.com", live: true)));
    }

    // ---- reading the page's answer ----

    private static string Engine(object answer) => JsonSerializer.Serialize(JsonSerializer.Serialize(answer));

    private static string Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(2 * i), samples[i]);
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public void ThePagesAnswerIsRead()
    {
        var page = SongPage.Parse(Engine(new
        {
            host = "www.youtube.com",
            id = "kJQP7kiw5Fk",
            about = new { id = "kJQP7kiw5Fk", title = "A\u0007 song\n", author = "Band", seconds = 200, live = false, category = "Music", type = "" },
            session = new { title = "Song", artist = "Band" },
            ad = false,
            heard = new { state = "running", parts = new object[] { new { id = "old", pcm = Pcm(1, -2) }, new { id = "kJQP7kiw5Fk", pcm = Pcm(short.MaxValue, short.MinValue, 0) }, new { id = "x", pcm = "" } } },
        }));
        Assert.NotNull(page);
        Assert.Equal("kJQP7kiw5Fk", page.VideoId);
        Assert.Equal("A song", page.About!.Title);   // one clean line
        Assert.Equal(200, page.About.Seconds);
        Assert.Equal("Song", page.SessionTitle);
        Assert.True(page.Listening);
        Assert.Equal("running", page.AudioState);
        Assert.Equal(2, page.Parts.Count);           // the empty one left out
        Assert.Equal([1, -2], page.Parts[0].Samples);
        Assert.Equal([short.MaxValue, short.MinValue, 0], page.Parts[1].Samples);
    }

    [Fact]
    public void AnAnswerThatIsNotOneIsRefused()
    {
        Assert.Null(SongPage.Parse(null));
        Assert.Null(SongPage.Parse("null"));
        Assert.Null(SongPage.Parse("{}"));                        // not a string holding JSON
        Assert.Null(SongPage.Parse(JsonSerializer.Serialize("[1]")));
        Assert.Null(SongPage.Parse(JsonSerializer.Serialize("not json")));
        Assert.Null(SongPage.Parse(Engine(new { heard = new { parts = new object[] { new { id = "a", pcm = "@@@" } } } })));
        Assert.Null(SongPage.Parse(Engine(new { heard = new { parts = new object[] { new { id = "a", pcm = Convert.ToBase64String(new byte[3]) } } } })));
        Assert.Null(SongPage.Parse(Engine(new { heard = new { parts = new object[] { 5 } } })));
        // More audio than the page can hold.
        var tooMuch = new short[(SongPage.KeptSeconds + 2) * Rate];
        Assert.Null(SongPage.Parse(Engine(new { heard = new { parts = new object[] { new { id = "a", pcm = Pcm(tooMuch) } } } })));
        // Fields of the wrong kind read as missing rather than as values.
        var odd = SongPage.Parse(Engine(new { host = 5, about = new { id = 7, seconds = "12", live = "true" } }));
        Assert.NotNull(odd);
        Assert.Equal("", odd.Host);
        Assert.Equal(0, odd.About!.Seconds);
        Assert.False(odd.About.Live);
        Assert.False(odd.Listening);
    }

    [Fact]
    public void TheScriptSaysWhetherToStartListening()
    {
        Assert.Contains("if (!ear && true)", SongPage.TakeScript(true));
        Assert.Contains("if (!ear && false)", SongPage.TakeScript(false));
        Assert.DoesNotContain("__", SongPage.TakeScript(true));
        Assert.Contains("RATE = 11025, KEEP = 20 * RATE, IDLE = 15000", SongPage.TakeScript(true));
    }

    // ---- synthetic songs ----

    /// <summary>A made-up song: a few notes at a time, each a fraction of a second, from a seed.</summary>
    internal static float[] Song(int seed, double seconds)
    {
        var rng = new Random(seed);
        var x = new float[(int)(seconds * Rate)];
        for (int voice = 0; voice < 3; voice++)
        {
            int at = 0;
            while (at < x.Length)
            {
                int len = (int)(Rate * (0.15 + rng.NextDouble() * 0.25));
                double f = 150 + rng.NextDouble() * 2500;
                double amp = 0.1 + rng.NextDouble() * 0.2;
                for (int i = 0; i < len && at + i < x.Length; i++)
                {
                    double env = Math.Min(1, Math.Min(i, len - i) / (Rate * 0.01));
                    x[at + i] += (float)(amp * env * Math.Sin(2 * Math.PI * f * i / Rate));
                }
                at += len;
            }
        }
        return x;
    }

    private static float[] WithNoise(ReadOnlySpan<float> clip, int seed, double noiseOverSong)
    {
        var rng = new Random(seed);
        double rms = Math.Sqrt(clip.ToArray().Average(v => (double)v * v));
        var noisy = new float[clip.Length];
        // Talk-like noise: bursts of a few random tones, changing quickly.
        double f = 300;
        for (int i = 0; i < clip.Length; i++)
        {
            if (i % 600 == 0)
                f = 120 + rng.NextDouble() * 900;
            noisy[i] = clip[i] + (float)(rms * noiseOverSong * (Math.Sin(2 * Math.PI * f * i / Rate) + 0.5 * (rng.NextDouble() * 2 - 1)));
        }
        return noisy;
    }

    private static short[] ToShorts(ReadOnlySpan<float> x)
    {
        var s = new short[x.Length];
        for (int i = 0; i < x.Length; i++)
            s[i] = (short)Math.Clamp(Math.Round(x[i] * 32767), short.MinValue, short.MaxValue);
        return s;
    }

    private static SongInfo Info(string id, string title = "Song", int seconds = 0) => new("youtube:" + id, title, "Band", "youtube.com", seconds);

    // ---- the library ----

    [Fact]
    public void ASongIsFoundUnderNoiseAndAnUnknownOneIsNot()
    {
        var library = new SongLibrary(_folder);
        var a = Song(1, 40);
        var b = Song(2, 40);
        Assert.True(library.Put(Info("aaaaaaaaaaa", "A"), 40, AudioFingerprint.Landmarks(a), DateTime.UtcNow));
        Assert.True(library.Put(Info("bbbbbbbbbbb", "B"), 40, AudioFingerprint.Landmarks(b), DateTime.UtcNow));

        // This jammer is one loud tone at a time, harsher on peaks than a voice; at the same
        // level as the song it took most of them (score 9). Real talk at the same level is the
        // live run, and the offline measurements in CLAUDE.md.
        var found = library.Identify(WithNoise(a.AsSpan(15 * Rate + 1234, 10 * Rate), 9, 0.5), DateTime.UtcNow);
        Assert.True(found.Song?.Title == "A", $"score {found.Score}, next best {found.RunnerUp}");
        Assert.True(found.Score >= SongLibrary.MinScore);
        Assert.Equal(2, found.Known);
        Assert.Equal("B", library.Identify(b.AsSpan(3 * Rate, 10 * Rate), DateTime.UtcNow).Song?.Title);

        var unknown = library.Identify(WithNoise(Song(3, 12).AsSpan(Rate, 10 * Rate), 4, 1.0), DateTime.UtcNow);
        Assert.Null(unknown.Song);
        Assert.Equal(2, unknown.Known);
        Assert.Null(library.Identify(new float[10 * Rate], DateTime.UtcNow).Song);   // silence
    }

    [Fact]
    public void TheRuleNeedsEnoughAgreementAndALead()
    {
        Assert.False(SongLibrary.Matches(9, 0));
        Assert.True(SongLibrary.Matches(10, 0));
        Assert.True(SongLibrary.Matches(30, 10));
        Assert.False(SongLibrary.Matches(29, 10));
    }

    [Fact]
    public void ALongerHearingReplacesAShorterOneAndTheLibraryReadsBack()
    {
        var library = new SongLibrary(_folder);
        var marks = AudioFingerprint.Landmarks(Song(1, 35));
        Assert.True(library.Put(Info("aaaaaaaaaaa", "First"), 35, marks, DateTime.UtcNow));
        Assert.False(library.Put(Info("aaaaaaaaaaa", "Shorter"), 30, marks, DateTime.UtcNow));
        Assert.False(library.Put(Info("aaaaaaaaaaa", "Same"), 35, marks, DateTime.UtcNow));
        Assert.True(library.Put(Info("aaaaaaaaaaa", "Longer"), 90, marks, DateTime.UtcNow));
        Assert.False(library.Put(Info("bbbbbbbbbbb"), 30, [], DateTime.UtcNow));   // nothing heard

        var again = new SongLibrary(_folder);
        var song = Assert.Single(again.Songs);
        Assert.Equal("Longer", song.Title);
        Assert.Equal(90, again.SecondsOf("youtube:aaaaaaaaaaa"));
        Assert.Equal(0, again.SecondsOf("youtube:bbbbbbbbbbb"));
        Assert.Equal(marks, SongLibrary.ReadLandmarks(Path.Combine(_folder, song.Id + ".fp")));
        Assert.Equal([song.Id + ".fp", "songs.json"], Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public void PastTheLimitTheSongLearnedLongestAgoGoes()
    {
        var library = new SongLibrary(_folder, maxSongs: 2);
        var marks = AudioFingerprint.Landmarks(Song(1, 5));
        var t = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        library.Put(Info("aaaaaaaaaaa", "A"), 30, marks, t);
        library.Put(Info("bbbbbbbbbbb", "B"), 30, marks, t.AddMinutes(1));
        int idOfA = library.Songs.Single(s => s.Title == "A").Id;
        library.Put(Info("ccccccccccc", "C"), 30, marks, t.AddMinutes(2));
        Assert.Equal(["B", "C"], library.Songs.Select(s => s.Title).Order());
        Assert.False(File.Exists(Path.Combine(_folder, idOfA + ".fp")));
        Assert.Equal(2, Directory.GetFiles(_folder, "*.fp").Length);
    }

    [Fact]
    public void AListThatCannotBeReadIsNeverWrittenOver()
    {
        Directory.CreateDirectory(_folder);
        string list = Path.Combine(_folder, "songs.json");
        File.WriteAllText(list, "{ not a list");
        var library = new SongLibrary(_folder);
        Assert.True(library.Unreadable);
        Assert.False(library.Put(Info("aaaaaaaaaaa"), 30, AudioFingerprint.Landmarks(Song(1, 5)), DateTime.UtcNow));
        Assert.Equal("{ not a list", File.ReadAllText(list));
        Assert.Empty(Directory.GetFiles(_folder, "*.fp"));

        File.WriteAllText(list, """[{"Id":1,"Key":"youtube:a","Title":"A","Artist":"","Site":"","LearnedUtc":"2026-11-04T00:00:00Z","Seconds":30},{"Id":1,"Key":"youtube:b","Title":"B","Artist":"","Site":"","LearnedUtc":"2026-11-04T00:00:00Z","Seconds":30}]""");
        Assert.True(new SongLibrary(_folder).Unreadable);   // two songs with one file
    }

    [Fact]
    public void AFingerprintFileThatIsNotOneIsSkipped()
    {
        var library = new SongLibrary(_folder);
        library.Put(Info("aaaaaaaaaaa", "A"), 40, AudioFingerprint.Landmarks(Song(1, 40)), DateTime.UtcNow);
        string file = Directory.GetFiles(_folder, "*.fp").Single();
        File.WriteAllBytes(file, File.ReadAllBytes(file)[..100]);
        Assert.Null(SongLibrary.ReadLandmarks(file));
        Assert.Null(SongLibrary.ReadLandmarks(Path.Combine(_folder, "missing.fp")));
        var answer = new SongLibrary(_folder).Identify(Song(1, 40).AsSpan(Rate, 10 * Rate), DateTime.UtcNow);
        Assert.Null(answer.Song);
        Assert.Equal(0, answer.Known);
    }

    [Fact]
    public void TheIndexIsLetGoWhenNoQuestionNeedsIt()
    {
        var library = new SongLibrary(_folder);
        var t = DateTime.UtcNow;
        library.Put(Info("aaaaaaaaaaa"), 30, AudioFingerprint.Landmarks(Song(1, 30)), t);
        library.Prepare(t);
        Assert.True(library.HoldsIndex);
        library.ReleaseIdle(t + SongLibrary.IndexKept);
        Assert.True(library.HoldsIndex);
        library.ReleaseIdle(t + SongLibrary.IndexKept + TimeSpan.FromSeconds(1));
        Assert.False(library.HoldsIndex);
    }

    [Fact]
    public void ASongLearnedAfterTheIndexWasBuiltIsFound()
    {
        var library = new SongLibrary(_folder);
        var a = Song(1, 30);
        library.Put(Info("aaaaaaaaaaa", "A"), 30, AudioFingerprint.Landmarks(a), DateTime.UtcNow);
        library.Prepare(DateTime.UtcNow);
        var b = Song(2, 30);
        library.Put(Info("bbbbbbbbbbb", "B"), 30, AudioFingerprint.Landmarks(b), DateTime.UtcNow);
        Assert.Equal("B", library.Identify(b.AsSpan(5 * Rate, 10 * Rate), DateTime.UtcNow).Song?.Title);
    }

    // ---- the listener ----

    /// <summary>A tab that plays a made-up song, and a page that answers as the take script does.</summary>
    private sealed class FakeTab(string host, string videoId, float[] audio, string category = "Music", int seconds = 200) : IHearingTab
    {
        public string Url { get; set; } = "https://" + host + "/watch?v=" + videoId;
        public float[] Audio { get; set; } = audio;
        public string EarName { get; set; } = "ear1";
        public string? PageHost { get; set; }
        public Action? OnHear { get; set; }
        public bool IsPlayingAudio { get; set; } = true;
        public DateTime? SoundStartedUtc { get; set; } = DateTime.UtcNow;
        public bool Ear { get; private set; }
        public int Reads { get; private set; }
        public int Stops { get; private set; }
        public int At { get; set; }
        private int _delivered;
        public string VideoId { get; set; } = videoId;

        public Task<string?> HearAsync(string js, TimeSpan budget)
        {
            if (js == SongPage.StopScript)
            {
                Ear = false;
                Stops++;
                return Task.FromResult<string?>("true");
            }
            Reads++;
            OnHear?.Invoke();
            if (js.Contains("if (!ear && true)", StringComparison.Ordinal))
                Ear = true;
            object? heard = null;
            if (Ear)
            {
                var parts = new List<object>();
                if (IsPlayingAudio && At < Audio.Length)
                {
                    // 2.5 s a read, to the sample: 27562 and 27563 in turn.
                    int n = Math.Min((int)Math.Round((_delivered + 1) * 2.5 * Rate) - (int)Math.Round(_delivered * 2.5 * Rate), Audio.Length - At);
                    _delivered++;
                    var pcm = ToShorts(Audio.AsSpan(At, n));
                    At += n;
                    var bytes = new byte[pcm.Length * 2];
                    Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                    parts.Add(new { id = VideoId, pcm = Convert.ToBase64String(bytes) });
                }
                heard = new { state = "running", ear = EarName, parts };
            }
            var about = new { id = VideoId, title = "Band - Song " + VideoId, author = "BandVEVO", seconds, live = false, category, type = "" };
            return Task.FromResult<string?>(Engine(new { host = PageHost ?? host, id = VideoId, about, session = (object?)null, ad = false, heard }));
        }
    }

    private static Func<Action, Task> Inline => a => { a(); return Task.CompletedTask; };

    private static async Task<DateTime> TickFor(SongListener ear, IHearingTab[] tabs, DateTime t, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            t += SongListener.Every;
            await ear.Tick(tabs, t);
        }
        return t;
    }

    [Fact]
    public async Task ASongPlayedIsLearnedAndKeptAsItGoes()
    {
        var library = new SongLibrary(_folder);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120), seconds: 200);
        var ear = new SongListener(() => library, () => true, Inline);
        var t = DateTime.UtcNow;

        // The first read only looks: the page is judged before it is listened to.
        t = await TickFor(ear, [tab], t, 1);
        Assert.False(tab.Ear);
        t = await TickFor(ear, [tab], t, 1);
        Assert.True(tab.Ear);

        // Kept once 30 s are heard (12 reads of 2.5 s), and again 60 s later.
        t = await TickFor(ear, [tab], t, 10);
        Assert.Equal(0, library.Count);
        t = await TickFor(ear, [tab], t, 1);
        await ear.Work;
        var song = Assert.Single(library.Songs);
        Assert.Equal("youtube:aaaaaaaaaaa", song.Key);
        Assert.Equal("Song aaaaaaaaaaa", song.Title);
        Assert.Equal("Band", song.Artist);
        Assert.Equal(30, song.Seconds);
        t = await TickFor(ear, [tab], t, 24);
        await ear.Work;
        Assert.Equal(90, library.SecondsOf("youtube:aaaaaaaaaaa"));

        // The audio ends at 120 s and nothing more is heard: a minute later the song is done
        // with, and what was heard after the last keep is kept.
        t = await TickFor(ear, [tab], t, 12);
        await ear.Work;
        Assert.Equal(90, library.SecondsOf("youtube:aaaaaaaaaaa"));
        Assert.NotNull(ear.Learning);
        t = await TickFor(ear, [tab], t, 26);
        await ear.Work;
        Assert.Equal(120, library.SecondsOf("youtube:aaaaaaaaaaa"));
        Assert.Null(ear.Learning);
    }

    [Fact]
    public async Task ASongHeardInFullIsNotListenedToAgain()
    {
        var library = new SongLibrary(_folder);
        library.Put(Info("aaaaaaaaaaa", seconds: 100), 90, AudioFingerprint.Landmarks(Song(1, 10)), DateTime.UtcNow);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120), seconds: 100);
        var ear = new SongListener(() => library, () => true, Inline);
        await TickFor(ear, [tab], DateTime.UtcNow, 6);
        Assert.False(tab.Ear);
        Assert.Equal(6, tab.Reads);
    }

    [Fact]
    public async Task ASongSeenThroughEndsAndIsNotLearnedTwiceThisRun()
    {
        var library = new SongLibrary(_folder);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 60), seconds: 50);
        var ear = new SongListener(() => library, () => true, Inline);
        var t = await TickFor(ear, [tab], DateTime.UtcNow, 20);
        await ear.Work;
        // 35 s of a 50 s song is all of it, give or take its last moments.
        Assert.InRange(library.SecondsOf("youtube:aaaaaaaaaaa"), SongListener.Target(Info("x", seconds: 50)) - SongListener.WholeSongSlack, 40);
        Assert.Null(ear.Learning);
        int reads = tab.Reads;
        tab.At = 0;
        await TickFor(ear, [tab], t, 4);
        Assert.Equal(reads + 4, tab.Reads);
        Assert.Null(ear.Learning);
    }

    [Fact]
    public async Task NothingIsLearnedWithLearningOffOrFromATalk()
    {
        var library = new SongLibrary(_folder);
        var music = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120));
        var off = new SongListener(() => library, () => false, Inline);
        await TickFor(off, [music], DateTime.UtcNow, 20);
        Assert.Equal(0, music.Reads);   // not even asked

        var talk = new FakeTab("www.youtube.com", "bbbbbbbbbbb", Song(2, 120), category: "Education");
        var on = new SongListener(() => library, () => true, Inline);
        await TickFor(on, [talk], DateTime.UtcNow, 20);
        Assert.False(talk.Ear);
        await on.Work;
        Assert.Equal(0, library.Count);
    }

    [Fact]
    public async Task TheSongChangingEndsTheOneBeingLearned()
    {
        var library = new SongLibrary(_folder);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 200));
        var ear = new SongListener(() => library, () => true, Inline);
        var t = await TickFor(ear, [tab], DateTime.UtcNow, 16);   // 37.5 s heard, 30 kept
        tab.VideoId = "ccccccccccc";
        t = await TickFor(ear, [tab], t, 1);
        await ear.Work;
        Assert.Equal(37.5, library.SecondsOf("youtube:aaaaaaaaaaa"));
        Assert.Equal("youtube:ccccccccccc", ear.Learning?.Info.Key);
    }

    [Fact]
    public async Task TheLoudestNewestTabIsTheOneListenedTo()
    {
        var library = new SongLibrary(_folder);
        var older = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120)) { SoundStartedUtc = DateTime.UtcNow.AddMinutes(-5) };
        var newer = new FakeTab("www.youtube.com", "bbbbbbbbbbb", Song(2, 120)) { SoundStartedUtc = DateTime.UtcNow };
        var silent = new FakeTab("www.youtube.com", "ccccccccccc", Song(3, 120)) { IsPlayingAudio = false, SoundStartedUtc = DateTime.UtcNow.AddMinutes(1) };
        var ear = new SongListener(() => library, () => true, Inline);
        var t = await TickFor(ear, [older, newer, silent], DateTime.UtcNow, 3);
        Assert.Equal(0, older.Reads);
        Assert.Equal(0, silent.Reads);
        Assert.True(newer.Ear);
        // It stops: the older one is listened to, and the newer's listener is stopped.
        newer.IsPlayingAudio = false;
        await TickFor(ear, [older, newer, silent], t, 2);
        Assert.Equal(1, newer.Stops);
        Assert.Equal(2, older.Reads);
    }

    [Fact]
    public async Task ATickWaitsItsTurn()
    {
        var library = new SongLibrary(_folder);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120));
        var ear = new SongListener(() => library, () => true, Inline);
        var t = DateTime.UtcNow;
        await ear.Tick([tab], t);
        await ear.Tick([tab], t + SongListener.Every - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, tab.Reads);
        await ear.Tick([tab], t + TimeSpan.FromMilliseconds(1), force: true);
        Assert.Equal(2, tab.Reads);
    }

    [Fact]
    public async Task WhatSongIsPlayingUnderTalkIsNamed()
    {
        var library = new SongLibrary(_folder);
        var songA = Song(1, 90);
        var songB = Song(2, 90);
        library.Put(Info("aaaaaaaaaaa", "A"), 90, AudioFingerprint.Landmarks(songA), DateTime.UtcNow);
        library.Put(Info("bbbbbbbbbbb", "B"), 90, AudioFingerprint.Landmarks(songB), DateTime.UtcNow);

        // A video that is not music, with song B under its talk, never listened to until asked.
        var video = new FakeTab("www.youtube.com", "vvvvvvvvvvv", WithNoise(songB.AsSpan(20 * Rate, 40 * Rate), 3, 1.0), category: "Entertainment");
        var ear = new SongListener(() => library, () => true, Inline);
        var t = await TickFor(ear, [video], DateTime.UtcNow, 2);
        Assert.False(video.Ear);

        var asking = ear.IdentifyAsync(video, t);
        for (int i = 0; i < 10 && !asking.IsCompleted; i++)
            t = await TickFor(ear, [video], t, 1);
        var found = await asking;
        Assert.NotNull(found);
        Assert.Equal("B", found.Song?.Title);
        Assert.Equal(2, found.Known);
        Assert.Equal(SongAnswer.Of(found, learning: true), new SongAnswer("B", "Band", false));
    }

    [Fact]
    public async Task AsTheSongBeingLearnedPlaysTheAnswerComesAtOnce()
    {
        var library = new SongLibrary(_folder);
        var song = Song(1, 120);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", song);
        var ear = new SongListener(() => library, () => true, Inline);
        var t = await TickFor(ear, [tab], DateTime.UtcNow, 14);   // 32.5 s heard and kept
        await ear.Work;
        int reads = tab.Reads;
        var found = await ear.IdentifyAsync(tab, t + TimeSpan.FromSeconds(1));
        Assert.Equal(reads, tab.Reads);   // nothing more was asked of the page
        Assert.Equal("Song aaaaaaaaaaa", found?.Song?.Title);
    }

    [Fact]
    public async Task AudioHeardOnThePageBeforeIsNotTheAnswer()
    {
        // Measured live: asked right after the tab went from a song to a talk, the answer was
        // the song, from audio heard on the page before.
        var library = new SongLibrary(_folder);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120));
        var ear = new SongListener(() => library, () => true, Inline);
        var t = await TickFor(ear, [tab], DateTime.UtcNow, 14);
        await ear.Work;
        Assert.Equal(1, library.Count);

        tab.Url = "http://127.0.0.1/talk";
        tab.Audio = Song(7, 60);
        tab.At = 0;
        int reads = tab.Reads;
        var asking = ear.IdentifyAsync(tab, t + TimeSpan.FromSeconds(1));
        for (int i = 0; i < 10 && !asking.IsCompleted; i++)
            t = await TickFor(ear, [tab], t, 1);
        var found = await asking;
        Assert.True(tab.Reads > reads, "listened to the page as it is now");
        Assert.NotNull(found);
        Assert.Null(found.Song);

        // A new listener on the same address (the page reloaded) is a new page too.
        var again = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120));
        var ear2 = new SongListener(() => library, () => true, Inline);
        t = await TickFor(ear2, [again], t, 6);
        again.EarName = "ear2";
        again.Audio = Song(8, 60);
        again.At = 0;
        t = await TickFor(ear2, [again], t, 1);
        var second = ear2.IdentifyAsync(again, t + TimeSpan.FromSeconds(1));
        for (int i = 0; i < 10 && !second.IsCompleted; i++)
            t = await TickFor(ear2, [again], t, 1);
        Assert.Null((await second)?.Song);
    }

    [Fact]
    public async Task OnlyYouTubesOwnSitesAreReadUnasked()
    {
        var library = new SongLibrary(_folder);
        var elsewhere = new FakeTab("open.spotify.com", "aaaaaaaaaaa", Song(1, 120));
        var ear = new SongListener(() => library, () => true, Inline);
        await TickFor(ear, [elsewhere], DateTime.UtcNow, 6);
        Assert.Equal(0, elsewhere.Reads);

        // A page that says it is YouTube from an address that is not: read, never learned.
        var pretending = new FakeTab("www.youtube.com", "bbbbbbbbbbb", Song(2, 120)) { PageHost = "music.youtube.com" };
        var ear2 = new SongListener(() => library, () => true, Inline);
        await TickFor(ear2, [pretending], DateTime.UtcNow, 20);
        Assert.False(pretending.Ear);
        await ear2.Work;
        Assert.Equal(0, library.Count);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=kJQP7kiw5Fk", "www.youtube.com")]
    [InlineData("https://music.youtube.com/watch?v=kJQP7kiw5Fk", "music.youtube.com")]
    [InlineData("https://M.YouTube.com/watch?v=x", "m.youtube.com")]
    [InlineData("https://youtube.com/", "youtube.com")]
    [InlineData("http://www.youtube.com/watch?v=x", null)]
    [InlineData("https://www.youtube.com.evil.example/watch?v=x", null)]
    [InlineData("https://evil.example/www.youtube.com", null)]
    [InlineData("https://gaming.youtube.com/", null)]
    [InlineData("about:blank", null)]
    [InlineData("not a url", null)]
    public void TheSongSitesAreReadFromTheTabsOwnAddress(string url, string? site)
        => Assert.Equal(site, SongPage.SongSiteOf(url));

    [Fact]
    public async Task AudioTakenWhileThePageMovedOnIsDropped()
    {
        var library = new SongLibrary(_folder);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120));
        var ear = new SongListener(() => library, () => true, Inline);
        var t = await TickFor(ear, [tab], DateTime.UtcNow, 6);
        double before = ear.Learning!.Seconds;
        // The address changes while the page is being asked.
        tab.OnHear = () => { tab.Url = "https://www.youtube.com/watch?v=ccccccccccc"; tab.OnHear = null; };
        t = await TickFor(ear, [tab], t, 1);
        Assert.Equal(before, ear.Learning!.Seconds);
        t = await TickFor(ear, [tab], t, 1);
        Assert.True(ear.Learning!.Seconds > before);
    }

    [Fact]
    public async Task TheIndexIsLetGoAFewMinutesAfterAQuestion()
    {
        var library = new SongLibrary(_folder);
        var tab = new FakeTab("www.youtube.com", "aaaaaaaaaaa", Song(1, 120));
        var ear = new SongListener(() => library, () => true, Inline);
        var t = await TickFor(ear, [tab], DateTime.UtcNow, 14);
        await ear.Work;
        Assert.NotNull(await ear.IdentifyAsync(tab, t));
        Assert.True(library.HoldsIndex);
        await ear.Tick([tab], t + SongLibrary.IndexKept);
        Assert.True(library.HoldsIndex);
        await ear.Tick([tab], t + SongLibrary.IndexKept + SongListener.Every + TimeSpan.FromSeconds(1));
        Assert.False(library.HoldsIndex);
    }

    [Fact]
    public async Task ASecondQuestionAboutAnotherTabEndsTheFirstAndOneAboutTheSameTabSharesIt()
    {
        var library = new SongLibrary(_folder);
        var songB = Song(2, 90);
        library.Put(Info("bbbbbbbbbbb", "B"), 90, AudioFingerprint.Landmarks(songB), DateTime.UtcNow);
        var first = new FakeTab("example.com", "", Song(3, 60));
        var second = new FakeTab("example.org", "", songB.AsSpan(10 * Rate).ToArray()) { SoundStartedUtc = DateTime.UtcNow.AddMinutes(1) };
        var ear = new SongListener(() => library, () => true, Inline);
        var t = DateTime.UtcNow;
        var one = ear.IdentifyAsync(first, t);
        var two = ear.IdentifyAsync(second, t);
        var three = ear.IdentifyAsync(second, t);
        Assert.Null(await one);
        for (int i = 0; i < 10 && !(two.IsCompleted && three.IsCompleted); i++)
            t = await TickFor(ear, [first, second], t, 1);
        Assert.Equal("B", (await two)?.Song?.Title);
        Assert.Equal("B", (await three)?.Song?.Title);
    }

    [Fact]
    public void ASongBeingReadForAQuestionCanStillBeLearnedOver()
    {
        var library = new SongLibrary(_folder);
        library.Put(Info("aaaaaaaaaaa", "A"), 30, AudioFingerprint.Landmarks(Song(1, 30)), DateTime.UtcNow);
        string file = Directory.GetFiles(_folder, "*.fp").Single();
        // As the index build holds it.
        using (var reading = SongLibrary.OpenShared(file))
        {
            Assert.True(library.Put(Info("aaaaaaaaaaa", "A"), 60, AudioFingerprint.Landmarks(Song(1, 60)), DateTime.UtcNow));
        }
        Assert.Equal(60, new SongLibrary(_folder).SecondsOf("youtube:aaaaaaaaaaa"));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
        Assert.Single(Directory.GetFiles(_folder, "*.fp"));   // the old one went once it was let go
    }

    [Fact]
    public void AListThatCannotBeWrittenLeavesEverythingAsItWas()
    {
        var library = new SongLibrary(_folder);
        var a = Song(1, 40);
        library.Put(Info("aaaaaaaaaaa", "A"), 30, AudioFingerprint.Landmarks(a), DateTime.UtcNow);
        // Settings.WriteAtomically may leave its own staging file; the songs' files are what count.
        var filesBefore = Directory.GetFiles(_folder, "*.fp").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
        string list = Path.Combine(_folder, "songs.json");
        string listBefore = File.ReadAllText(list);
        using (new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => library.Put(Info("aaaaaaaaaaa", "A"), 40, AudioFingerprint.Landmarks(a), DateTime.UtcNow));
            Assert.ThrowsAny<IOException>(() => library.Put(Info("bbbbbbbbbbb", "B"), 30, AudioFingerprint.Landmarks(Song(2, 30)), DateTime.UtcNow));
        }
        Assert.Equal(30, library.SecondsOf("youtube:aaaaaaaaaaa"));
        Assert.Equal(0, library.SecondsOf("youtube:bbbbbbbbbbb"));
        Assert.Equal(listBefore, File.ReadAllText(list));
        Assert.Equal(filesBefore, Directory.GetFiles(_folder, "*.fp").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(Directory.GetFiles(_folder, "*.fp.tmp"));
        // And it still answers, and learns once the list can be written.
        Assert.Equal("A", library.Identify(a.AsSpan(5 * Rate, 10 * Rate), DateTime.UtcNow).Song?.Title);
        Assert.True(library.Put(Info("aaaaaaaaaaa", "A"), 40, AudioFingerprint.Landmarks(a), DateTime.UtcNow));
    }

    [Fact]
    public void ASongWhoseFileGoesWhileTheIndexIsBuiltIsLeftOutAndTheRestAnswer()
    {
        var library = new SongLibrary(_folder);
        var a = Song(1, 30);
        var b = Song(2, 30);
        library.Put(Info("aaaaaaaaaaa", "A"), 30, AudioFingerprint.Landmarks(a), DateTime.UtcNow);
        library.Put(Info("bbbbbbbbbbb", "B"), 30, AudioFingerprint.Landmarks(b), DateTime.UtcNow);
        int idOfA = library.Songs.Single(s => s.Title == "A").Id;
        library.AfterSizing = () => File.Delete(Path.Combine(_folder, idOfA + ".fp"));
        var answer = library.Identify(b.AsSpan(5 * Rate, 10 * Rate), DateTime.UtcNow);
        Assert.Equal("B", answer.Song?.Title);
        Assert.Equal(1, answer.Known);
        Assert.Null(library.Identify(a.AsSpan(5 * Rate, 10 * Rate), DateTime.UtcNow).Song);
    }

    [Fact]
    public void FilesTheListDoesNotNameAreTidiedAtStartAndNothingElse()
    {
        var library = new SongLibrary(_folder);
        library.Put(Info("aaaaaaaaaaa", "A"), 30, AudioFingerprint.Landmarks(Song(1, 30)), DateTime.UtcNow);
        string kept = Path.GetFileName(Directory.GetFiles(_folder, "*.fp").Single());
        File.WriteAllText(Path.Combine(_folder, "77.fp"), "orphan");
        File.WriteAllText(Path.Combine(_folder, "5.fp.tmp"), "half written");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "the person's");
        File.WriteAllText(Path.Combine(_folder, "x77.fp"), "not one of ours");
        _ = new SongLibrary(_folder);
        Assert.Equal([kept, "notes.txt", "songs.json", "x77.fp"], Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());

        // Nothing is tidied when the list could not be read: it may name them all.
        File.WriteAllText(Path.Combine(_folder, "songs.json"), "{ broken");
        File.WriteAllText(Path.Combine(_folder, "78.fp"), "orphan?");
        _ = new SongLibrary(_folder);
        Assert.True(File.Exists(Path.Combine(_folder, "78.fp")));
        Assert.True(File.Exists(Path.Combine(_folder, kept)));
    }

    [Fact]
    public async Task ATabThatCannotBeHeardIsSaidSo()
    {
        var library = new SongLibrary(_folder);
        var paused = new FakeTab("example.com", "", Song(1, 60)) { IsPlayingAudio = false };
        var ear = new SongListener(() => library, () => true, Inline);
        var t = DateTime.UtcNow;
        var asking = ear.IdentifyAsync(paused, t);
        while (!asking.IsCompleted && t < DateTime.UtcNow + SongListener.ListenAtMost + TimeSpan.FromSeconds(10))
            t = await TickFor(ear, [paused], t, 1);
        Assert.Null(await asking);
        Assert.Equal(new SongAnswer("Could not hear that tab", "It needs to be playing, with sound.", false), SongAnswer.Of(null, true));
    }

    [Fact]
    public void TheAnswerSaysWhatGergurKnows()
    {
        var song = new LearnedSong(1, "youtube:a", "Song", "", "youtube.com", DateTime.UtcNow, 30);
        Assert.Equal(new SongAnswer("Song", "youtube.com", false), SongAnswer.Of(new Identification(song, 40, 2, 3), true));
        Assert.Equal("It learns songs as you play them on YouTube.", SongAnswer.Of(new Identification(null, 0, 0, 0), true).Detail);
        Assert.Equal("Learning songs is off in the settings.", SongAnswer.Of(new Identification(null, 0, 0, 0), false).Detail);
        Assert.Equal("It knows 1 song, learned as you play them.", SongAnswer.Of(new Identification(null, 4, 2, 1), true).Detail);
        Assert.Equal("It knows 12 songs, learned as you play them; learning is off.", SongAnswer.Of(new Identification(null, 4, 2, 12), false).Detail);
        Assert.Equal("Not a song Gergur knows yet", SongAnswer.Of(new Identification(null, 4, 2, 12), false).Headline);
        Assert.True(SongAnswer.Listening.Busy);
    }

    [Fact]
    public void TheSongButtonListensToWhateverPlaysInThePersonsWindows()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gergur", "UI", "MainForm.cs"));
        // A button of its own on the toolbar, beside the phone drop, in every window.
        Assert.Contains("[_menuButton, _agentsButton, _phoneButton, _songButton, _bookmarkButton, _downloadsButton]", source);
        Assert.Contains("[_menuButton, _phoneButton, _songButton, _bookmarkButton, _downloadsButton]", source);
        Assert.Contains("_songButton.Click += (_, _) => ToggleSongPanel();", source);
        // Opening the panel listens; the tab is whichever of the person's tabs makes sound, any site.
        int toggle = source.IndexOf("private void ToggleSongPanel()", StringComparison.Ordinal);
        int listens = source.IndexOf("_ = ListenForSongAsync();", toggle, StringComparison.Ordinal);
        Assert.True(toggle > 0 && listens > toggle && listens < source.IndexOf("private static Tab? PlayingTab", StringComparison.Ordinal));
        Assert.Contains("=> PersonTabs(session).Where(t => t.IsPlayingAudio).MaxBy(t => t.SoundStartedUtc ?? DateTime.MinValue);", source);
        Assert.Contains("panel.SetAnswer(SongAnswer.NothingPlaying, \"\");", source);
        // Every other panel closes it, as it closes them, and a download does not open over it.
        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(source, @"_songPanel\?\.Dismiss\(\);").Count);
        Assert.Contains("|| _songPanel is { Visible: true })", source);
        // The busy flag is set and cleared around everything that can throw, so a failure never
        // leaves the button doing nothing for good.
        int listen = source.IndexOf("private async Task ListenForSongAsync()", StringComparison.Ordinal);
        int set = source.IndexOf("_identifying = true;", listen, StringComparison.Ordinal);
        int open = source.IndexOf("try", set, StringComparison.Ordinal);
        int playing = source.IndexOf("PlayingTab(session)", listen, StringComparison.Ordinal);
        int reset = source.IndexOf("_identifying = false;", listen, StringComparison.Ordinal);
        int fin = source.IndexOf("finally", listen, StringComparison.Ordinal);
        Assert.True(set > listen && open > set && playing > open && fin > playing && reset > fin,
            "_identifying is set first, then the try, and reset in its finally");
    }

    [Fact]
    public void TheSongPanelShowsWhatItIsDoing()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int asked = 0;
                using var panel = new SongFlyout(() => asked++);
                Assert.Equal(SongAnswer.Listening, panel.Answer);
                panel.SetAnswer(SongAnswer.NothingPlaying, "");
                Assert.Equal("Nothing is playing in Gergur", panel.Answer.Headline);
                panel.SetAnswer(new SongAnswer("Despacito", "Luis Fonsi", false), "Heard on tiktok.com");
                Assert.Equal("Heard on tiktok.com", panel.Where);
                Assert.Equal("What song is this?", panel.AccessibleName);

                // Listen again waits while it is listening, and asks once it has answered.
                panel.SetAnswer(SongAnswer.Listening, "To the tab playing on youtube.com");
                panel.Relayout(keepPlace: false);
                var busy = Assert.Single(panel.Spots);
                Assert.Equal("Listen again", busy.Spoken);
                Assert.False(busy.Enabled);
                panel.RunSpot(busy);
                Assert.Equal(0, asked);
                panel.SetAnswer(SongAnswer.NothingPlaying, "");
                panel.Relayout(keepPlace: false);
                var ready = Assert.Single(panel.Spots);
                Assert.True(ready.Enabled);
                Assert.True(ready.KeepOpen);
                panel.RunSpot(ready);
                Assert.Equal(1, asked);
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
    public void TheWindowsTickTheListenerWhetherOrNotTheyAreSeen()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gergur", "UI", "MainForm.cs"));
        int tick = source.IndexOf("session.Songs.Tick(PersonTabs(session)", StringComparison.Ordinal);
        int seen = source.IndexOf("if (!Visible || WindowState == FormWindowState.Minimized)", StringComparison.Ordinal);
        int agent = source.IndexOf("if (_session is not { } session || _closing || IsDisposed || _openedByAgent)", StringComparison.Ordinal);
        Assert.True(agent >= 0 && tick > agent && seen > tick, "the listener is ticked after the agent-window check and before the visibility one");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
