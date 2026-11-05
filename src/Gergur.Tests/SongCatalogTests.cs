using System.Text.Json;
using Gergur.App;
using Gergur.Music;
using NAudio.Wave;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Filling the song library (<see cref="SongCatalog"/>) against a stand-in internet, what makes a
/// match lead by song and not by entry, and asking while a song plays on. Real previews against
/// real songs are measured in the scratchpad's catalog harness; this keeps the rules.
/// </summary>
public sealed class SongCatalogTests : IDisposable
{
    private const int Rate = AudioFingerprint.SampleRate;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gergur-catalog-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder: left for the system.
        }
    }

    private static float[] Tune(int seed, int seconds)
    {
        var rng = new Random(seed);
        var samples = new float[seconds * Rate];
        for (int note = 0; note * Rate / 4 < samples.Length; note++)
        {
            double f1 = 200 + rng.NextDouble() * 1800, f2 = 200 + rng.NextDouble() * 3000;
            for (int i = note * Rate / 4; i < Math.Min(samples.Length, (note + 1) * Rate / 4); i++)
                samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * f1 * i / Rate) + 0.2 * Math.Sin(2 * Math.PI * f2 * i / Rate));
        }
        return samples;
    }

    private static List<Landmark> Marks(int seed, int seconds = 30) => AudioFingerprint.Landmarks(Tune(seed, seconds));

    private static SongInfo Info(string key, string title, string artist = "Band") => new(key, title, artist, "Deezer", 30);

    // ---------------------------------------------------------------- a stand-in internet

    /// <summary>Answers by address; a preview's bytes name the tune it decodes to.</summary>
    private sealed class FakeWeb : ICatalogWeb
    {
        public Dictionary<string, string> Pages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Tunes { get; } = new(StringComparer.Ordinal);
        public List<string> Asked { get; } = [];
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        public Task<string?> TextAsync(string url, CancellationToken stop)
        {
            Asked.Add(url);
            if (Failing.Contains(url))
                throw new HttpRequestException("the site answered 503");
            return Task.FromResult(Pages.GetValueOrDefault(url));
        }

        public Task<byte[]?> BytesAsync(string url, CancellationToken stop)
        {
            Asked.Add(url);
            return Task.FromResult(Tunes.TryGetValue(url, out int seed) ? BitConverter.GetBytes(seed) : null);
        }
    }

    private static float[]? DecodeFake(byte[] bytes) => bytes.Length == 4 ? Tune(BitConverter.ToInt32(bytes), 30) : null;

    private static string DeezerTrack(long id, string title, string artist, string preview)
        => JsonSerializer.Serialize(new { id, title, preview, artist = new { name = artist } });

    private static string Dz(int n) => $"https://cdnt-preview.dzcdn.net/p{n}.mp3";

    private static string Ap(int n) => $"https://audio-ssl.itunes.apple.com/p{n}.m4a";

    private static SongCatalog Catalog(SongLibrary library, FakeWeb web, Func<bool>? enabled = null, string? progress = null)
        => new(library, enabled ?? (() => true), web, DecodeFake, (_, _) => Task.CompletedTask, progress);

    private static async Task Run(SongCatalog catalog, int steps)
    {
        for (int i = 0; i < steps; i++)
            await catalog.StepAsync(CancellationToken.None);
    }

    private const string Genres = """{"data":[{"id":0,"name":"All"},{"id":132,"name":"Pop"}]}""";

    /// <summary>The charts name an artist (and whoever else), with no songs of their own; that artist has one song.</summary>
    private static FakeWeb OneArtistInTheCharts(string? also = null)
    {
        var web = new FakeWeb();
        web.Pages["https://api.deezer.com/genre"] = Genres;
        string extra = also is null ? "" : "," + also;
        web.Pages["https://api.deezer.com/chart/0/artists?limit=100"] = "{\"data\":[{\"id\":77,\"name\":\"Seven Band\"}" + extra + "]}";
        web.Pages["https://api.deezer.com/artist/77/top?limit=15"] = "{\"data\":[" + DeezerTrack(7, "Seven", "Seven Band", Dz(7)) + "]}";
        web.Tunes[Dz(7)] = 107;
        return web;
    }

    private static FakeWeb Charts()
    {
        var web = new FakeWeb();
        web.Pages["https://api.deezer.com/genre"] = Genres;
        web.Pages["https://api.deezer.com/chart/0/tracks?limit=100"] = "{\"data\":[" + DeezerTrack(1, "One", "Band", Dz(1)) + "," + DeezerTrack(2, "Two", "Other", Dz(2)) + "]}";
        web.Tunes[Dz(1)] = 101;
        web.Tunes[Dz(2)] = 102;
        return web;
    }

    // ---------------------------------------------------------------- what it fetches

    [Fact]
    public async Task ChartSongsEndUpAsFingerprintsAndNothingElse()
    {
        var library = new SongLibrary(_folder);
        var web = Charts();
        await Run(Catalog(library, web), 3);
        Assert.Equal(["deezer:1", "deezer:2"], library.Songs.Select(s => s.Key).Order());
        Assert.Equal("One", library.Songs.Single(s => s.Key == "deezer:1").Title);
        Assert.Equal(2, library.CatalogCount);
        Assert.Equal(2, Directory.GetFiles(_folder, "*.fp").Length);
        // A clip of the tune is named by it.
        var answer = library.Identify(Tune(102, 30).AsSpan(10 * Rate, 10 * Rate), DateTime.UtcNow);
        Assert.Equal("Two", answer.Song?.Title);
        // Nothing but the list and the fingerprints.
        Assert.All(Directory.GetFiles(_folder), f => Assert.True(f.EndsWith(".fp", StringComparison.Ordinal) || f.EndsWith("songs.json", StringComparison.Ordinal), f));
    }

    [Fact]
    public async Task EachSongIsAlsoLookedForOnAppleAndKeptWhenItIsTheSameSong()
    {
        var library = new SongLibrary(_folder);
        var web = Charts();
        web.Pages["https://itunes.apple.com/search?entity=song&limit=5&term=Band%20One"] = JsonSerializer.Serialize(new
        {
            results = new object[]
            {
                new { trackId = 70L, trackName = "One More Time", artistName = "Band", previewUrl = Ap(70) },
                new { trackId = 71L, trackName = "One (Radio Edit)", artistName = "Band & Someone", previewUrl = Ap(71) },
            },
        });
        web.Pages["https://itunes.apple.com/search?entity=song&limit=5&term=Other%20Two"] = JsonSerializer.Serialize(new
        {
            results = new object[] { new { trackId = 80L, trackName = "Two", artistName = "Not The Same Band", previewUrl = Ap(80) } },
        });
        web.Tunes[Ap(70)] = 170;
        web.Tunes[Ap(71)] = 171;
        web.Tunes[Ap(80)] = 180;
        await Run(Catalog(library, web), 3);
        Assert.Equal(["apple:71", "deezer:1", "deezer:2"], library.Songs.Select(s => s.Key).Order());
    }

    [Fact]
    public async Task TheSameSongOnAnotherReleaseIsNotFetchedAgain()
    {
        var library = new SongLibrary(_folder);
        var web = Charts();
        web.Pages["https://api.deezer.com/chart/0/tracks?limit=100"] = "{\"data\":["
            + DeezerTrack(1, "One", "Band", Dz(1)) + "," + DeezerTrack(3, "One (2012 Remaster)", "Band", Dz(3)) + "," + DeezerTrack(4, "ONE - Radio Edit", "Band feat. X", Dz(4)) + "]}";
        web.Tunes[Dz(3)] = 103;
        web.Tunes[Dz(4)] = 104;
        await Run(Catalog(library, web), 4);
        Assert.Equal(["deezer:1"], library.Songs.Select(s => s.Key));
        Assert.DoesNotContain(Dz(3), web.Asked);
    }

    [Fact]
    public async Task ArtistsThePersonPlaysComeFirstAndThoseLikeThemNext()
    {
        var library = new SongLibrary(_folder);
        library.Put(new SongInfo("youtube:aaaaaaaaaaa", "Song", "Played Band", "youtube.com", 200), 60, Marks(1, 60), DateTime.UtcNow);
        var web = Charts();
        web.Pages["https://api.deezer.com/search/artist?limit=1&q=Played%20Band"] = """{"data":[{"id":55,"name":"Played Band"}]}""";
        web.Pages["https://api.deezer.com/artist/55/top?limit=50"] = "{\"data\":[" + DeezerTrack(5, "Their Hit", "Played Band", Dz(5)) + "]}";
        web.Pages["https://api.deezer.com/artist/55/related?limit=10"] = """{"data":[{"id":66,"name":"Like Them"}]}""";
        web.Pages["https://api.deezer.com/artist/66/top?limit=15"] = "{\"data\":[" + DeezerTrack(6, "Their Song", "Like Them", Dz(6)) + "]}";
        web.Tunes[Dz(5)] = 105;
        web.Tunes[Dz(6)] = 106;
        var catalog = Catalog(library, web);
        await Run(catalog, 2);
        Assert.Contains("deezer:5", library.Songs.Select(s => s.Key));
        Assert.DoesNotContain("https://api.deezer.com/genre", web.Asked);
        // The charts, then what was queued behind them; the related artist is reached in time.
        await Run(catalog, 6);
        Assert.Contains("deezer:6", library.Songs.Select(s => s.Key));
        Assert.Contains("deezer:1", library.Songs.Select(s => s.Key));
    }

    [Fact]
    public async Task AnArtistSearchThatFindsSomeoneElseIsNotFollowed()
    {
        var library = new SongLibrary(_folder);
        library.Put(new SongInfo("youtube:aaaaaaaaaaa", "Song", "Rare Name", "youtube.com", 200), 60, Marks(1, 60), DateTime.UtcNow);
        var web = new FakeWeb();
        web.Pages["https://api.deezer.com/search/artist?limit=1&q=Rare%20Name"] = """{"data":[{"id":55,"name":"Somebody Else"}]}""";
        await Run(Catalog(library, web), 1);
        Assert.DoesNotContain(web.Asked, u => u.Contains("/artist/55/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItGoesOnFromWhereItWasAfterARestart()
    {
        string progress = Path.Combine(_folder, "catalog.json");
        var web = Charts();
        var first = new SongLibrary(_folder);
        await Run(Catalog(first, web, progress: progress), 3);
        int asked = web.Asked.Count;

        var again = new SongLibrary(_folder);
        var more = Catalog(again, web, progress: progress);
        await Run(more, 3);
        // The charts were read this week, and those songs are here already.
        Assert.Equal(asked, web.Asked.Count);
        Assert.Equal(2, again.CatalogCount);
    }

    [Fact]
    public async Task ARestartWithSongsStillWaitingStillGetsThem()
    {
        string progress = Path.Combine(_folder, "catalog.json");
        var web = Charts();
        var first = new SongLibrary(_folder);
        // The charts read and one song of two taken, and then the browser is closed.
        await Run(Catalog(first, web, progress: progress), 2);

        var again = new SongLibrary(_folder);
        await Run(Catalog(again, web, progress: progress), 3);
        Assert.Equal(2, again.CatalogCount);
        Assert.True(JsonSerializer.Deserialize<SongCatalog.Progress>(File.ReadAllText(progress))!.ChartsUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task AnArtistIsDoneOnlyOnceItsSongsAreIn()
    {
        string progress = Path.Combine(_folder, "catalog.json");
        var library = new SongLibrary(_folder);
        var web = OneArtistInTheCharts();
        var catalog = Catalog(library, web, progress: progress);
        await Run(catalog, 2);   // the charts, then its top songs read
        var place = JsonSerializer.Deserialize<SongCatalog.Progress>(File.ReadAllText(progress))!;
        Assert.DoesNotContain(77, place.DoneArtists);
        Assert.Contains(place.Pending, a => a.Id == 77);

        await Run(catalog, 1);   // its one song
        place = JsonSerializer.Deserialize<SongCatalog.Progress>(File.ReadAllText(progress))!;
        Assert.Contains(77, place.DoneArtists);
        Assert.DoesNotContain(place.Pending, a => a.Id == 77);
    }

    [Fact]
    public async Task AnArtistThatFailsDoesNotHoldUpTheRest()
    {
        var library = new SongLibrary(_folder);
        var web = OneArtistInTheCharts(also: """{"id":88,"name":"Other"}""");
        web.Failing.Add("https://api.deezer.com/artist/77/top?limit=15");
        var catalog = Catalog(library, web);
        await Run(catalog, 1);   // the charts
        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.StepAsync(CancellationToken.None));
        Assert.True(await catalog.StepAsync(CancellationToken.None));
        Assert.Contains("https://api.deezer.com/artist/88/top?limit=15", web.Asked);

        // Once it answers again, it gets its turn.
        web.Failing.Clear();
        await Run(catalog, 3);
        Assert.Contains(library.Songs, s => s.Title == "Seven");
    }

    [Fact]
    public async Task APlayedArtistThatFailsIsAskedAgainAndDoesNotHoldUpTheRest()
    {
        string progress = Path.Combine(_folder, "catalog.json");
        var library = new SongLibrary(_folder);
        library.Put(new SongInfo("youtube:aaaaaaaaaaa", "Song", "Rare Name", "youtube.com", 200), 60, Marks(1, 60), DateTime.UtcNow);
        var web = Charts();
        web.Failing.Add("https://api.deezer.com/search/artist?limit=1&q=Rare%20Name");
        var catalog = Catalog(library, web, progress: progress);
        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.StepAsync(CancellationToken.None));
        Assert.True(await catalog.StepAsync(CancellationToken.None));
        Assert.Contains("https://api.deezer.com/genre", web.Asked);
        Assert.DoesNotContain("Rare Name", JsonSerializer.Deserialize<SongCatalog.Progress>(File.ReadAllText(progress))!.SearchedNames);
    }

    [Fact]
    public async Task ASongTheNetworkLostComesRoundAgain()
    {
        var library = new SongLibrary(_folder);
        var web = Charts();
        web.Failing.Add("https://itunes.apple.com/search?entity=song&limit=5&term=Band%20One");
        var catalog = Catalog(library, web);
        await Run(catalog, 1);
        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.StepAsync(CancellationToken.None));
        web.Failing.Clear();
        await Run(catalog, 2);
        Assert.Contains(web.Asked, u => u.EndsWith("term=Other%20Two", StringComparison.Ordinal));
        Assert.Equal(2, web.Asked.Count(u => u.EndsWith("term=Band%20One", StringComparison.Ordinal)));
        Assert.Equal(2, library.CatalogCount);
    }

    [Fact]
    public async Task ASongThatNeverAnswersDoesNotHoldUpEverythingAfterIt()
    {
        var library = new SongLibrary(_folder);
        var web = OneArtistInTheCharts();
        web.Pages["https://api.deezer.com/chart/0/tracks?limit=100"] = "{\"data\":[" + DeezerTrack(1, "One", "Band", Dz(1)) + "]}";
        web.Tunes[Dz(1)] = 101;
        string search = "https://itunes.apple.com/search?entity=song&limit=5&term=Band%20One";
        web.Failing.Add(search);
        var catalog = Catalog(library, web);
        for (int i = 0; i < 12; i++)
        {
            try
            {
                await catalog.StepAsync(CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                // Backed off, in the real run.
            }
        }
        Assert.Equal(3, web.Asked.Count(u => u == search));
        Assert.Contains("https://api.deezer.com/artist/77/top?limit=15", web.Asked);
        Assert.Contains(library.Songs, s => s.Title == "Seven");
    }

    [Fact]
    public async Task APlaceFileWithNothingInItsListsIsNoTrouble()
    {
        string progress = Path.Combine(_folder, "catalog.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(progress, """{"Pending":null,"DoneArtists":null,"SearchedNames":null}""");
        var library = new SongLibrary(_folder);
        await Run(Catalog(library, Charts(), progress: progress), 3);
        Assert.Equal(2, library.CatalogCount);
    }

    [Fact]
    public async Task AUnreadablePlaceStartsOverAndFetchesNothingItHas()
    {
        string progress = Path.Combine(_folder, "catalog.json");
        var web = Charts();
        var library = new SongLibrary(_folder);
        await Run(Catalog(library, web, progress: progress), 3);
        File.WriteAllText(progress, "{ not json");
        int previews = web.Asked.Count(u => u.EndsWith(".mp3", StringComparison.Ordinal));
        await Run(Catalog(new SongLibrary(_folder), web, progress: progress), 3);
        Assert.Equal(previews, web.Asked.Count(u => u.EndsWith(".mp3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ItStopsAtTheLimitOfTheLibrary()
    {
        var library = new SongLibrary(_folder, maxCatalog: 1);
        await Run(Catalog(library, Charts()), 3);
        Assert.Equal(1, library.CatalogCount);
    }

    [Fact]
    public async Task TurnedOffItFetchesNothing()
    {
        var library = new SongLibrary(_folder);
        var web = Charts();
        Assert.Empty(await Asked(enabled: false));
        // The same run turned on does fetch, so the empty list above means something.
        Assert.NotEmpty(await Asked(enabled: true));

        async Task<List<string>> Asked(bool enabled)
        {
            int waits = 0;
            using var stop = new CancellationTokenSource();
            web.Asked.Clear();
            var catalog = new SongCatalog(library, () => enabled, web, DecodeFake, (_, _) =>
            {
                // The first wait is the one before it starts: let that one by.
                if (++waits >= 2)
                    stop.Cancel();
                return Task.CompletedTask;
            });
            await catalog.RunAsync(TimeSpan.Zero, stop.Token);
            return web.Asked;
        }
    }

    [Fact]
    public async Task ADiskThatWillNotWriteDoesNotEndTheRun()
    {
        // The place is a folder, so every save of it fails: while working, and while turned off.
        string notAFile = Path.Combine(_folder, "catalog.json");
        Directory.CreateDirectory(notAFile);
        var library = new SongLibrary(_folder);
        bool on = true;
        var waits = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();
        var catalog = new SongCatalog(library, () => on, Charts(), DecodeFake, (t, _) =>
        {
            waits.Add(t);
            on = waits.Count < 3;
            if (waits.Count >= 6)
                stop.Cancel();
            return Task.CompletedTask;
        }, notAFile);
        await catalog.RunAsync(TimeSpan.Zero, stop.Token);
        Assert.Equal(6, waits.Count);
        Assert.Contains(SongCatalog.WhenIdle, waits);
    }

    [Fact]
    public async Task TheRunBacksOffWhenASiteSaysNoAndEndsWhenToldTo()
    {
        var library = new SongLibrary(_folder);
        var web = new FakeWeb();
        web.Pages["https://api.deezer.com/genre"] = """{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""";
        var waits = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();
        var catalog = new SongCatalog(library, () => true, web, DecodeFake, (t, _) =>
        {
            waits.Add(t);
            if (waits.Count >= 2)
                stop.Cancel();
            return Task.CompletedTask;
        });
        await catalog.RunAsync(TimeSpan.Zero, stop.Token);
        Assert.Equal(SongCatalog.Backoff(1), waits[1]);
        Assert.Empty(library.Songs);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(40, 60)]
    public void BackingOffDoublesUpToAnHour(int failures, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), SongCatalog.Backoff(failures));

    // ---------------------------------------------------------------- what the sites say

    [Theory]
    [InlineData("https://cdnt-preview.dzcdn.net/api/a.mp3?hdnea=x", true)]
    [InlineData("https://audio-ssl.itunes.apple.com/a/b.m4a", true)]
    [InlineData("http://cdnt-preview.dzcdn.net/a.mp3", false)]
    [InlineData("https://dzcdn.net.evil.example/a.mp3", false)]
    [InlineData("https://evil.example/.dzcdn.net/a.mp3", false)]
    [InlineData("https://cdnt-preview.dzcdn.net:8443/a.mp3", false)]
    [InlineData("file:///c:/a.mp3", false)]
    [InlineData("", false)]
    public void PreviewsComeOnlyFromWhereTheyReallyCome(string url, bool allowed)
        => Assert.Equal(allowed, SongCatalog.IsPreviewAddress(url));

    [Fact]
    public void AnAnswerNotShapedLikeSongsGivesNone()
    {
        Assert.Empty(SongCatalog.ParseDeezer("""{"data":[null,3,{"id":"x"},{"id":1,"title":"","preview":"https://a.dzcdn.net/a"}]}"""));
        Assert.Empty(SongCatalog.ParseDeezer("""{"data":[{"id":1,"title":"A","artist":{"name":"B"},"preview":"https://evil.example/a"}]}"""));
        Assert.Empty(SongCatalog.ParseDeezer("""[]"""));
        Assert.Empty(SongCatalog.ParseApple("""{"results":[{"trackId":-5,"trackName":"A","artistName":"B","previewUrl":"https://a.itunes.apple.com/a"}]}"""));
        Assert.ThrowsAny<JsonException>(() => SongCatalog.ParseDeezer("not json"));
        Assert.Throws<InvalidOperationException>(() => SongCatalog.ParseDeezer("""{"error":{"code":4}}"""));
        var song = SongCatalog.ParseDeezer("""{"data":[{"id":7,"title":"A\u202Eb\nc","artist":{"name":"B"},"preview":"https://a.dzcdn.net/a"}]}""").Single();
        Assert.Equal("A b c", song.Title);
        Assert.Equal("deezer:7", song.Key);
    }

    [Theory]
    [InlineData("Despacito", "despacito")]
    [InlineData("Despacito ft. Daddy Yankee", "despacito")]
    [InlineData("Uptown Funk (feat. Bruno Mars)", "uptownfunk")]
    [InlineData("Smooth Criminal (2012 Remaster)", "smoothcriminal")]
    [InlineData("Dracula (with JENNIE)", "dracula")]
    [InlineData("Song - Radio Edit", "song")]
    [InlineData("bad guy", "badguy")]
    [InlineData("HUMBLE.", "humble")]
    public void ANameIsNormalWithoutItsVersionAndGuests(string name, string normal)
        => Assert.Equal(normal, SongCatalog.Normal(name));

    [Fact]
    public void ASongIsItsTitleAndFirstArtist()
    {
        Assert.Equal(SongCatalog.NameKey("Song", "Band"), SongCatalog.NameKey("SONG (Live Mix)", "Band & Someone"));
        Assert.NotEqual(SongCatalog.NameKey("Song", "Band"), SongCatalog.NameKey("Song", "Other Band"));
    }

    // ---------------------------------------------------------------- the library

    [Fact]
    public void TheSameSongTwiceIsNoRivalToItself()
    {
        var library = new SongLibrary(_folder);
        // The same audio under two entries, which split nothing: both agree in full.
        library.PutCatalog([(Info("deezer:1", "Song (2012 Remaster)"), 30, Marks(1)), (Info("apple:1", "Song"), 30, Marks(1)), (Info("deezer:2", "Other"), 30, Marks(2))], DateTime.UtcNow);
        var answer = library.Identify(Tune(1, 30).AsSpan(5 * Rate, 10 * Rate), DateTime.UtcNow);
        Assert.NotNull(answer.Song);
        Assert.Equal("song", SongCatalog.Normal(answer.Song.Title));
        Assert.True(answer.RunnerUp < answer.Score / 3);
    }

    [Fact]
    public void AnotherSongSoundingTheSameIsStillARival()
    {
        var library = new SongLibrary(_folder);
        library.PutCatalog([(Info("deezer:1", "Song"), 30, Marks(1)), (Info("deezer:2", "Not That Song"), 30, Marks(1))], DateTime.UtcNow);
        Assert.Null(library.Identify(Tune(1, 30).AsSpan(5 * Rate, 10 * Rate), DateTime.UtcNow).Song);
    }

    [Fact]
    public void DeezersNoDataIsAnEmptyAnswerAndNotARefusal()
    {
        Assert.Empty(SongCatalog.ParseArtists("""{"error":{"type":"DataException","message":"no data","code":800}}"""));
        Assert.Throws<InvalidOperationException>(() => SongCatalog.ParseArtists("""{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}"""));
        Assert.Throws<InvalidOperationException>(() => SongCatalog.ParseArtists("""{"error":"no"}"""));
    }

    [Fact]
    public void TheSameTitleByAnotherArtistIsARival()
    {
        LearnedSong Of(string title, string artist) => new(1, "deezer:1", title, artist, "Deezer", DateTime.UtcNow, 30);
        Assert.Equal(40, SongLibrary.RunnerUp(Of("Home", "One Band"), [(Of("Home", "Other Band"), 40)]));
        Assert.Equal(0, SongLibrary.RunnerUp(Of("Home", "One Band"), [(Of("Home (Live)", "One Band & Guest"), 40)]));
        Assert.True(SongCatalog.SameSong("Home", "One Band", "Home - Radio Edit", "One Band, Guest"));
        Assert.False(SongCatalog.SameSong("Home", "One Band", "Home", "Other Band"));
        Assert.False(SongCatalog.SameSong("...", "One Band", "...", "One Band"));
    }

    [Fact]
    public void TheRunnerUpIsTheFirstOfAnotherName()
    {
        LearnedSong Named(string title) => new(1, "deezer:1", title, "", "", DateTime.UtcNow, 30);
        Assert.Equal(0, SongLibrary.RunnerUp(Named("A"), []));
        Assert.Equal(0, SongLibrary.RunnerUp(Named("A"), [(Named("A (Live)"), 90)]));
        Assert.Equal(40, SongLibrary.RunnerUp(Named("A"), [(Named("A (Live)"), 90), (Named("B"), 40), (Named("C"), 30)]));
        // An entry with no name left, or no song at all, counts against it.
        Assert.Equal(50, SongLibrary.RunnerUp(Named("A"), [(null, 50)]));
        Assert.Equal(60, SongLibrary.RunnerUp(Named("(Live)"), [(Named("(Live)"), 60)]));
    }

    [Fact]
    public void ABatchIsKeptOnceAndSurvivesAReopen()
    {
        var library = new SongLibrary(_folder);
        int kept = library.PutCatalog([(Info("deezer:1", "A"), 30, Marks(1)), (Info("deezer:1", "A again"), 30, Marks(2)), (Info("deezer:2", "B"), 30, []), (Info("deezer:3", "C"), 30, Marks(3))], DateTime.UtcNow);
        Assert.Equal(2, kept);
        Assert.Equal(0, library.PutCatalog([(Info("deezer:1", "A"), 30, Marks(1))], DateTime.UtcNow));
        var again = new SongLibrary(_folder);
        Assert.Equal(["deezer:1", "deezer:3"], again.Songs.Select(s => s.Key).Order());
        Assert.Equal(2, Directory.GetFiles(_folder, "*.fp").Length);
    }

    [Fact]
    public void PreviewsDoNotPushOutSongsPlayedAndTheOtherWayRound()
    {
        var library = new SongLibrary(_folder, maxSongs: 1, maxCatalog: 2);
        library.Put(new SongInfo("youtube:aaaaaaaaaaa", "Played", "Band", "youtube.com", 60), 60, Marks(1), DateTime.UtcNow);
        library.PutCatalog([(Info("deezer:1", "A"), 30, Marks(2)), (Info("deezer:2", "B"), 30, Marks(3)), (Info("deezer:3", "C"), 30, Marks(4))], DateTime.UtcNow);
        Assert.Equal(2, library.CatalogCount);
        library.Put(new SongInfo("youtube:bbbbbbbbbbb", "Played Too", "Band", "youtube.com", 60), 60, Marks(5), DateTime.UtcNow);
        Assert.Equal(["deezer:1", "deezer:2", "youtube:bbbbbbbbbbb"], library.Songs.Select(s => s.Key).Order());
    }

    [Fact]
    public void AnUnreadableListIsNotWrittenOver()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "songs.json"), "{ not json");
        var library = new SongLibrary(_folder);
        Assert.Equal(0, library.PutCatalog([(Info("deezer:1", "A"), 30, Marks(1))], DateTime.UtcNow));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(_folder, "songs.json")));
    }

    [Fact]
    public void AQuestionUnderWayKeepsItsIndexWhilePreviewsArriveAndALearnedSongStillRenewsIt()
    {
        var library = new SongLibrary(_folder);
        library.PutCatalog([(Info("deezer:1", "A"), 30, Marks(1))], DateTime.UtcNow);
        var now = DateTime.UtcNow;
        Assert.Equal("A", library.Identify(Tune(1, 30).AsSpan(5 * Rate, 10 * Rate), now).Song?.Title);
        library.PutCatalog([(Info("deezer:2", "B"), 30, Marks(2))], now);
        // Not in the index yet, and nothing is rebuilt for it. (These made-up tunes change note with
        // a click on one beat, which is enough for one to look like another; real songs are measured
        // in the scratchpad's catalog harness.)
        Assert.NotEqual("B", library.Identify(Tune(2, 30).AsSpan(5 * Rate, 10 * Rate), now).Song?.Title);
        Assert.Equal(2, library.Identify(Tune(1, 30).AsSpan(5 * Rate, 10 * Rate), now).Known);
        // Let go after a question, it comes back with everything.
        library.ReleaseIdle(now + SongLibrary.IndexKept + TimeSpan.FromSeconds(1));
        Assert.Equal("B", library.Identify(Tune(2, 30).AsSpan(5 * Rate, 10 * Rate), now + SongLibrary.IndexKept + TimeSpan.FromSeconds(2)).Song?.Title);
    }

    // ---------------------------------------------------------------- asking, as a song plays on

    /// <summary>A tab playing audio on a site that is not a song page; 2.5 s heard a read.</summary>
    private sealed class PlayingTab(float[] audio, string host = "example.com", string videoId = "") : IHearingTab
    {
        private int _at;
        private bool _ear;
        public string Url { get; set; } = "https://" + host + "/watch?v=" + videoId;
        public bool IsPlayingAudio { get; set; } = true;
        public DateTime? SoundStartedUtc { get; set; } = DateTime.UtcNow;
        public object? About { get; set; }
        public int Reads { get; private set; }

        public Task<string?> HearAsync(string js, TimeSpan budget)
        {
            if (js == SongPage.StopScript)
            {
                _ear = false;
                return Task.FromResult<string?>("true");
            }
            Reads++;
            if (js.Contains("if (!ear && true)", StringComparison.Ordinal))
                _ear = true;
            object? heard = null;
            if (_ear)
            {
                var parts = new List<object>();
                if (IsPlayingAudio && _at < audio.Length)
                {
                    int n = Math.Min((int)(2.5 * Rate), audio.Length - _at);
                    var pcm = new short[n];
                    for (int i = 0; i < n; i++)
                        pcm[i] = (short)(audio[_at + i] * 32767);
                    _at += n;
                    var bytes = new byte[n * 2];
                    Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                    parts.Add(new { id = videoId, pcm = Convert.ToBase64String(bytes) });
                }
                heard = new { state = "running", ear = "ear1", parts };
            }
            return Task.FromResult<string?>(JsonSerializer.Serialize(JsonSerializer.Serialize(new { host, id = videoId, about = About, session = (object?)null, ad = false, heard })));
        }
    }

    private static Func<Action, Task> Inline => a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static async Task<(Identification? Answer, TimeSpan Took)> Ask(SongListener ear, IHearingTab tab, int mostTicks = 80)
    {
        var start = DateTime.UtcNow;
        var t = start;
        var asking = ear.IdentifyAsync(tab, t);
        for (int i = 0; i < mostTicks && !asking.IsCompleted; i++)
        {
            t += SongListener.Every;
            await ear.Tick([tab], t);
            // The question goes round off this thread: wait for it to be waiting again.
            for (int spin = 0; spin < 2000 && !asking.IsCompleted && !ear.Asking; spin++)
                await Task.Delay(1);
        }
        return (await asking, t - start);
    }

    [Fact]
    public async Task ItGoesOnListeningUntilThePartOfTheSongItHasComesAround()
    {
        var library = new SongLibrary(_folder);
        // The library has only 30 s, from 60 s into the song.
        var song = Tune(1, 120);
        library.PutCatalog([(Info("deezer:1", "Found Later"), 30, AudioFingerprint.Landmarks(song.AsSpan(60 * Rate, 30 * Rate)))], DateTime.UtcNow);
        var ear = new SongListener(() => library, () => true, Inline);
        var (answer, took) = await Ask(ear, new PlayingTab(song));
        Assert.Equal("Found Later", answer?.Song?.Title);
        Assert.True(took > TimeSpan.FromSeconds(50), $"took {took}");
    }

    [Fact]
    public async Task ItGivesUpInTheEndAndSaysSo()
    {
        var library = new SongLibrary(_folder);
        library.PutCatalog([(Info("deezer:1", "Other"), 30, Marks(2))], DateTime.UtcNow);
        var ear = new SongListener(() => library, () => true, Inline, keepListening: TimeSpan.FromSeconds(20));
        var (answer, took) = await Ask(ear, new PlayingTab(Tune(1, 300)));
        Assert.NotNull(answer);
        Assert.Null(answer.Song);
        Assert.InRange(took.TotalSeconds, 20, 45);
    }

    [Fact]
    public async Task ASongThatStopsPlayingEndsTheQuestionWithWhatItHas()
    {
        var library = new SongLibrary(_folder);
        library.PutCatalog([(Info("deezer:1", "Other"), 30, Marks(2))], DateTime.UtcNow);
        var ear = new SongListener(() => library, () => true, Inline);
        var (answer, _) = await Ask(ear, new PlayingTab(Tune(1, 15)), mostTicks: 40);
        Assert.NotNull(answer);
        Assert.Null(answer.Song);
    }

    [Fact]
    public async Task APageThatIsASongSaysWhichAtOnce()
    {
        var library = new SongLibrary(_folder);
        var tab = new PlayingTab(Tune(1, 120), "www.youtube.com", "aaaaaaaaaaa")
        {
            About = new { id = "aaaaaaaaaaa", title = "Band - The Song (Official Video)", author = "BandVEVO", seconds = 200, live = false, category = "Music", type = "" },
        };
        var ear = new SongListener(() => library, () => false, Inline);
        var (answer, took) = await Ask(ear, tab);
        Assert.Equal("The Song", answer?.Song?.Title);
        Assert.Equal("Band", answer?.Song?.Artist);
        Assert.True(took < TimeSpan.FromSeconds(6), $"took {took}");
        Assert.Equal(0, library.Count);   // said, not kept
    }

    [Fact]
    public async Task AWeakMatchMustBeTheSameSongInTheNextClip()
    {
        // Half the landmarks of one 10 s clip's worth: it clears the bar, weakly, once only.
        var library = new SongLibrary(_folder);
        var song = Tune(1, 60);
        var weak = AudioFingerprint.Landmarks(song.AsSpan(0, 10 * Rate)).Take(14).ToList();
        library.PutCatalog([(Info("deezer:1", "Once By Chance"), 30, weak)], DateTime.UtcNow);
        var first = library.Identify(song.AsSpan(0, 10 * Rate), DateTime.UtcNow);
        Assert.Equal("Once By Chance", first.Song?.Title);
        Assert.True(first.Score < SongListener.SureAtOnce, $"score {first.Score}");

        var ear = new SongListener(() => library, () => true, Inline, keepListening: TimeSpan.FromSeconds(30));
        var (answer, _) = await Ask(ear, new PlayingTab(song));
        Assert.NotNull(answer);
        Assert.Null(answer.Song);
    }

    // ---------------------------------------------------------------- reading a preview

    [Fact]
    public void WindowsDecodersTurnAudioIntoMonoAtTheFingerprintRate()
    {
        // Stereo at 44100, in a form every copy of Windows reads: what comes out is the right
        // length, and its landmarks match the tune it was made from.
        var tune = Tune(9, 20);
        using var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new NonClosing(wav), new WaveFormat(44100, 16, 2)))
        {
            for (int i = 0; i < 20 * 44100; i++)
            {
                short v = (short)(tune[Math.Min(tune.Length - 1, (int)(i * (long)Rate / 44100))] * 32000);
                writer.WriteSample(v / 32768f);
                writer.WriteSample(v / 32768f);
            }
        }
        var decoded = PreviewAudio.Decode(wav.ToArray());
        Assert.NotNull(decoded);
        Assert.InRange(decoded.Length, 19 * Rate, 21 * Rate);
        var library = new SongLibrary(_folder);
        library.PutCatalog([(Info("deezer:1", "Tune"), 20, AudioFingerprint.Landmarks(decoded))], DateTime.UtcNow);
        Assert.Equal("Tune", library.Identify(tune.AsSpan(5 * Rate, 10 * Rate), DateTime.UtcNow).Song?.Title);
    }

    [Fact]
    public void WhatIsNotAudioIsNoAudio()
    {
        Assert.Null(PreviewAudio.Decode([1, 2, 3, 4, 5]));
        Assert.Null(PreviewAudio.Decode([]));
    }

    private sealed class NonClosing(Stream inner) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Length => inner.Length;
        public override void Flush() => inner.Flush();
    }

    // ---------------------------------------------------------------- the real client, against a stand-in site

    /// <summary>A tiny site on this machine: answers every request with what <paramref name="respond"/> writes.</summary>
    private static async Task<T> AgainstASite<T>(Func<Stream, string, Task> respond, Func<string, Task<T>> ask)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var done = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(done.Token);
                    await using var stream = client.GetStream();
                    var reader = new StreamReader(stream);
                    string path = (await reader.ReadLineAsync(done.Token) ?? "").Split(' ').ElementAtOrDefault(1) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(done.Token)))
                    {
                    }
                    await respond(stream, path);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException)
            {
                // The test is over, or it hung up.
            }
        });
        try
        {
            return await ask($"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/x");
        }
        finally
        {
            done.Cancel();
            listener.Stop();
            await serving;
        }
    }

    private static Func<Stream, string, Task> Answering(string status, byte[]? body = null, string headers = "") => async (stream, _) =>
    {
        byte[] bytes = body ?? [];
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n{headers}\r\n"));
        await stream.WriteAsync(bytes);
    };

    [Theory]
    [InlineData("429 Too Many Requests")]
    [InlineData("403 Forbidden")]
    [InlineData("503 Service Unavailable")]
    public async Task ASiteSayingWaitMakesTheRunBackOff(string status)
    {
        using var web = new CatalogWeb();
        await Assert.ThrowsAsync<HttpRequestException>(() => AgainstASite(Answering(status), url => web.TextAsync(url, CancellationToken.None)));
    }

    [Theory]
    [InlineData("404 Not Found")]
    [InlineData("302 Found")]
    public async Task AnAnswerWithNothingInItIsJustNothing(string status)
    {
        using var web = new CatalogWeb();
        Assert.Null(await AgainstASite(Answering(status), url => web.TextAsync(url, CancellationToken.None)));
    }

    [Fact]
    public async Task AnExpiredPreviewIsPassedOverAndNotARefusal()
    {
        using var web = new CatalogWeb();
        Assert.Null(await AgainstASite(Answering("403 Forbidden"), url => web.BytesAsync(url, CancellationToken.None)));
        await Assert.ThrowsAsync<HttpRequestException>(() => AgainstASite(Answering("429 Too Many Requests"), url => web.BytesAsync(url, CancellationToken.None)));
    }

    [Fact]
    public async Task ARedirectIsNotFollowed()
    {
        using var web = new CatalogWeb();
        int asked = 0;
        var answer = Answering("302 Found", headers: "Location: /elsewhere\r\n");
        Assert.Null(await AgainstASite((stream, path) =>
        {
            asked++;
            return answer(stream, path);
        }, url => web.TextAsync(url, CancellationToken.None)));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task APreviewBiggerThanAnyIsPassedOver()
    {
        using var web = new CatalogWeb();
        Assert.Null(await AgainstASite(Answering("200 OK", new byte[CatalogWeb.MostPreview + 1]), url => web.BytesAsync(url, CancellationToken.None)));
        Assert.Equal(1000, (await AgainstASite(Answering("200 OK", new byte[1000]), url => web.BytesAsync(url, CancellationToken.None)))!.Length);
    }

    [Fact]
    public async Task ASiteThatDripsItsAnswerIsCutOff()
    {
        using var web = new CatalogWeb(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgainstASite(async (stream, _) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\n\r\n"u8.ToArray());
            for (int i = 0; i < 100; i++)
            {
                await stream.WriteAsync(new byte[10]);
                await Task.Delay(100);
            }
        }, url => web.BytesAsync(url, CancellationToken.None)));
    }

    // ---------------------------------------------------------------- the setting

    [Fact]
    public void AnAgentCannotTurnItOnOrOff()
        => Assert.Contains(nameof(Settings.BuildSongLibrary), SettingsPatch.DialogOnly);
}
