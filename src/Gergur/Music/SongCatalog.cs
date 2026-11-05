using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Blocking;
using Gergur.Diagnostics;
using Gergur.Tabs;

namespace Gergur.Music;

/// <summary>A song found on Deezer or Apple, with where its 30 s preview is.</summary>
internal sealed record CatalogTrack(string Key, string Title, string Artist, string Site, string PreviewUrl);

/// <summary>An artist found on Deezer.</summary>
internal sealed record CatalogArtist(long Id, string Name);

/// <summary>What the catalog needs of the internet; a test passes a stand-in.</summary>
internal interface ICatalogWeb
{
    /// <summary>A page of text, or null when it could not be had.</summary>
    Task<string?> TextAsync(string url, CancellationToken stop);

    /// <summary>A preview's bytes, or null when they could not be had.</summary>
    Task<byte[]?> BytesAsync(string url, CancellationToken stop);
}

/// <summary>
/// The internet as the catalog sees it: its own client, direct like the blocklist download, no
/// redirects followed, nothing bigger than a preview taken, and previews only from the two
/// places they come from.
/// </summary>
internal sealed class CatalogWeb : ICatalogWeb, IDisposable
{
    internal const long MostText = 1024 * 1024;
    internal const long MostPreview = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _wholeRequest;

    /// <param name="wholeRequest">How long one request may take, body included: the client's own timeout ends at the headers. A minute by default.</param>
    public CatalogWeb(TimeSpan? wholeRequest = null)
    {
        _wholeRequest = wholeRequest ?? TimeSpan.FromMinutes(1);
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Gergur-browser");
    }

    public async Task<string?> TextAsync(string url, CancellationToken stop)
    {
        var bytes = await GetAsync(url, MostText, preview: false, stop);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    public Task<byte[]?> BytesAsync(string url, CancellationToken stop) => GetAsync(url, MostPreview, preview: true, stop);

    private async Task<byte[]?> GetAsync(string url, long limit, bool preview, CancellationToken stop)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timed.CancelAfter(_wholeRequest);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timed.Token);
        if (AskedToWait(response.StatusCode, preview))
            throw new HttpRequestException($"the site answered {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
            return null;
        await using var body = await response.Content.ReadAsStreamAsync(timed.Token);
        using var kept = new MemoryStream();
        try
        {
            await UBlockLite.CopyBoundedAsync(body, kept, limit, timed.Token);
        }
        catch (InvalidDataException)
        {
            return null;   // bigger than anything it could be: this one is passed over
        }
        return kept.ToArray();
    }

    /// <summary>
    /// Answers that mean back off rather than move on. Apple says 403 when asked too often, but a
    /// preview's 403 is an address that has expired, which says nothing about the next one.
    /// </summary>
    internal static bool AskedToWait(System.Net.HttpStatusCode status, bool preview)
        => status == System.Net.HttpStatusCode.TooManyRequests || (int)status >= 500 || (status == System.Net.HttpStatusCode.Forbidden && !preview);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Fills the song library by itself, so "What song is this?" can name songs the person has
/// never played here, with the search itself staying on this computer.
///
/// Where the songs come from: Deezer's and Apple's public catalogs, which need no account and
/// hand out a 30 second preview of each song. Which ones: the artists the person has played
/// first (<see cref="SongLibrary.LearnedArtists"/>), then what is in the charts, then the top
/// songs of those artists and of artists like them, breadth first. Each song found on Deezer
/// is looked for on Apple too, since the two previews are often different parts of it and
/// sometimes different recordings: both are kept, as one song to the search.
///
/// A preview is held in memory only while it is fingerprinted. Nothing but fingerprints and
/// names is written. A preview is only some of a song, so a clip from elsewhere in it does not
/// match: the listener goes on listening until the part it has comes around
/// (<see cref="SongListener.KeepListening"/>).
///
/// What leaves this computer: requests to api.deezer.com and itunes.apple.com, with the names
/// of artists the person has played and of songs found, from the person's own address, direct
/// and not through the vpn, like the blocklist download. <see cref="Settings.BuildSongLibrary"/>
/// turns it off.
///
/// Runs off the UI thread, one song at a time and slowly (<see cref="BetweenSongs"/>: Apple
/// allows about 20 searches a minute), and stops at <see cref="SongLibrary.MaxCatalog"/>.
/// </summary>
internal sealed partial class SongCatalog
{
    public static readonly TimeSpan BetweenSongs = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan WhenIdle = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ChartsEvery = TimeSpan.FromDays(7);
    private const int SaveEvery = 10;
    private const int MostPendingArtists = 5000;
    private const int LeastLandmarks = 200;   // a preview with fewer is silence or noise
    private const int TopOfPlayedArtist = 50;
    private const int TopOfOtherArtist = 15;
    private const int RelatedArtists = 10;
    private const string Deezer = "https://api.deezer.com";
    private const string ChartsTried = "\0charts";
    private const int MostTriesOfASong = 3;
    private static readonly TimeSpan TryAgainAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// What it has done, kept in the songs folder so a restart goes on from there. Something counts
    /// as done only once every song it led to has been taken: the songs waiting are in memory (their
    /// preview addresses expire, so keeping them would not help), and whatever led to them is asked
    /// again after a restart, at the cost of a few requests, with the songs already here passed over.
    /// </summary>
    internal sealed class Progress
    {
        public DateTime ChartsUtc { get; set; }
        public List<string> SearchedNames { get; set; } = [];
        public List<long> DoneArtists { get; set; } = [];
        public List<CatalogArtist> Pending { get; set; } = [];
    }

    private readonly SongLibrary _library;
    private readonly Func<bool> _enabled;
    private readonly ICatalogWeb _web;
    private readonly Func<byte[], float[]?> _decode;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly string? _progressPath;
    private readonly Queue<CatalogTrack> _tracks = new();
    private readonly List<(SongInfo Info, double Seconds, List<Landmark> Marks)> _batch = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _failedUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _triesOf = new(StringComparer.Ordinal);
    private string? _feedingName;
    private CatalogArtist? _feedingArtist;
    private bool _feedingCharts;
    private Progress _progress;
    private bool _loaded;

    /// <param name="library">Where the songs go.</param>
    /// <param name="enabled">Whether it should run, read before each step.</param>
    /// <param name="web">The internet, or a stand-in.</param>
    /// <param name="decode">Compressed audio to mono samples at the fingerprint rate; null when it cannot be read.</param>
    /// <param name="wait">How it waits; a test goes straight on.</param>
    /// <param name="progressPath">Where it keeps its place; null keeps it in memory.</param>
    public SongCatalog(SongLibrary library, Func<bool> enabled, ICatalogWeb web, Func<byte[], float[]?> decode, Func<TimeSpan, CancellationToken, Task>? wait = null, string? progressPath = null)
    {
        _library = library;
        _enabled = enabled;
        _web = web;
        _decode = decode;
        _wait = wait ?? Task.Delay;
        _progressPath = progressPath;
        _progress = new Progress();
    }

    /// <summary>Goes on finding songs until <paramref name="stop"/>. Never throws, but for that.</summary>
    public async Task RunAsync(TimeSpan firstAfter, CancellationToken stop)
    {
        int failures = 0;
        try
        {
            await _wait(firstAfter, stop);
            while (!stop.IsCancellationRequested)
            {
                if (!_enabled() || _library.Unreadable || _library.CatalogCount >= _library.CatalogLimit)
                {
                    SaveQuietly();
                    await _wait(WhenIdle, stop);
                    continue;
                }
                TimeSpan next;
                try
                {
                    next = await StepAsync(stop) ? BetweenSongs : WhenIdle;
                    failures = 0;
                }
                catch (Exception ex) when (Passing(ex, stop))
                {
                    failures++;
                    next = Backoff(failures);
                    DebugLog.Write($"the song catalog stopped for {next.TotalMinutes:F0} min: {ex.Message}");
                }
                await _wait(next, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Closing.
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"the song catalog failed: {ex.Message}");
        }
        finally
        {
            SaveQuietly();
        }
    }

    /// <summary>A failure that is the network's, a site's or the disk's, and so worth trying again; not closing.</summary>
    private static bool Passing(Exception ex, CancellationToken stop)
        => !stop.IsCancellationRequested
            && ex is HttpRequestException or IOException or JsonException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException;

    /// <summary>A minute, doubling, up to an hour: the network is down, or a site asked for less.</summary>
    internal static TimeSpan Backoff(int failures) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(failures - 1, 0, 6))));

    /// <summary>Does the next thing: one song, or finding more. False when there is nothing left to do for now.</summary>
    internal async Task<bool> StepAsync(CancellationToken stop)
    {
        Load();
        // Stops at a song it has already got without waiting, so a list mostly known is passed quickly.
        while (_tracks.TryDequeue(out var next))
        {
            string key = SeenKey(next);
            if (!_seen.Add(key))
                continue;
            try
            {
                await TakeAsync(next, stop);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !stop.IsCancellationRequested)
            {
                // Probably the network, not the song: it waits its turn again. Not for ever, since
                // what never answers would keep the list from ever being finished.
                int tries = _triesOf.GetValueOrDefault(key) + 1;
                _triesOf[key] = tries;
                if (tries < MostTriesOfASong)
                {
                    _seen.Remove(key);
                    _tracks.Enqueue(next);
                }
                throw;
            }
            if (_tracks.Count == 0)
                FeedingDone();
            if (_batch.Count >= SaveEvery || _tracks.Count == 0)
                Save();
            return true;
        }
        FeedingDone();
        Save();
        if (await PlayedArtistAsync(stop))
            return true;
        if (await ChartsAsync(stop))
            return true;
        return await NextArtistAsync(stop);
    }

    /// <summary>Every song it led to has been taken, so what led to them can be written off.</summary>
    private void FeedingDone()
    {
        if (_feedingName is { } name)
            _progress.SearchedNames.Add(name);
        if (_feedingArtist is { } artist)
        {
            _progress.Pending.RemoveAll(a => a.Id == artist.Id);
            _progress.DoneArtists.Add(artist.Id);
        }
        if (_feedingCharts)
            _progress.ChartsUtc = DateTime.UtcNow;
        _feedingName = null;
        _feedingArtist = null;
        _feedingCharts = false;
    }

    /// <summary>Failed within the hour, so the next thing is tried instead of stopping at this one for good.</summary>
    private bool FailedLately(string what) => _failedUtc.TryGetValue(what, out var at) && DateTime.UtcNow - at < TryAgainAfter;

    /// <summary>A song by name, whatever its id; by its id if its name has nothing to go by.</summary>
    private static string SeenKey(CatalogTrack track) => Normal(track.Title).Length == 0 ? track.Key : NameKey(track.Title, track.Artist);

    private void Load()
    {
        if (_loaded)
            return;
        _loaded = true;
        foreach (string name in _library.CatalogNames())
            _seen.Add(name);
        if (_progressPath is null || !File.Exists(_progressPath))
            return;
        try
        {
            _progress = JsonSerializer.Deserialize<Progress>(File.ReadAllText(_progressPath)) ?? new Progress();
            _progress.SearchedNames ??= [];
            _progress.DoneArtists ??= [];
            _progress.Pending ??= [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Only its place: the songs themselves are in the library, and are not fetched again.
            DebugLog.Write($"the song catalog's place could not be read, so it starts over: {ex.Message}");
            _progress = new Progress();
        }
    }

    /// <summary>Takes a song's previews: Deezer's or Apple's as found, and the other's if it has one.</summary>
    private async Task TakeAsync(CatalogTrack track, CancellationToken stop)
    {
        await TakePreviewAsync(track, stop);
        if (track.Key.StartsWith("deezer:", StringComparison.Ordinal))
        {
            string term = Uri.EscapeDataString(track.Artist + " " + track.Title);
            string? answer = await _web.TextAsync($"https://itunes.apple.com/search?entity=song&limit=5&term={term}", stop);
            if (answer is not null && AppleMatch(ParseApple(answer), track) is { } apple)
                await TakePreviewAsync(apple, stop);
        }
    }

    private async Task TakePreviewAsync(CatalogTrack track, CancellationToken stop)
    {
        if (!IsPreviewAddress(track.PreviewUrl))
            return;
        var bytes = await _web.BytesAsync(track.PreviewUrl, stop);
        if (bytes is null)
            return;
        var samples = _decode(bytes);
        if (samples is null)
            return;
        var marks = AudioFingerprint.Landmarks(samples);
        if (marks.Count < LeastLandmarks)
            return;
        double seconds = samples.Length / (double)AudioFingerprint.SampleRate;
        _batch.Add((new SongInfo(track.Key, track.Title, track.Artist, track.Site, (int)seconds), seconds, marks));
    }

    private async Task<bool> PlayedArtistAsync(CancellationToken stop)
    {
        var searched = _progress.SearchedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? name = _library.LearnedArtists().FirstOrDefault(a => !searched.Contains(a) && !FailedLately(a));
        if (name is null)
            return false;
        try
        {
            string? answer = await _web.TextAsync($"{Deezer}/search/artist?limit=1&q={Uri.EscapeDataString(name)}", stop);
            if (answer is not null && ParseArtists(answer).FirstOrDefault() is { } artist && Normal(artist.Name) == Normal(name))
                await ArtistAsync(artist, TopOfPlayedArtist, played: true, stop);
        }
        catch (Exception ex) when (Passing(ex, stop))
        {
            _failedUtc[name] = DateTime.UtcNow;
            throw;
        }
        _feedingName = name;
        Save();
        return true;
    }

    private async Task<bool> ChartsAsync(CancellationToken stop)
    {
        if (DateTime.UtcNow - _progress.ChartsUtc < ChartsEvery || FailedLately(ChartsTried))
            return false;
        try
        {
            string? genres = await _web.TextAsync($"{Deezer}/genre", stop);
            foreach (long genre in genres is null ? [0] : ParseGenres(genres).Prepend(0).Distinct())
            {
                string chart = genre.ToString(CultureInfo.InvariantCulture);
                if (await _web.TextAsync($"{Deezer}/chart/{chart}/tracks?limit=100", stop) is { } tracks)
                    Enqueue(ParseDeezer(tracks));
                if (await _web.TextAsync($"{Deezer}/chart/{chart}/artists?limit=100", stop) is { } artists)
                    AddPending(ParseArtists(artists), first: false);
                await _wait(TimeSpan.FromMilliseconds(250), stop);
            }
        }
        catch (Exception ex) when (Passing(ex, stop))
        {
            _failedUtc[ChartsTried] = DateTime.UtcNow;
            throw;
        }
        _feedingCharts = true;
        Save();
        return true;
    }

    private async Task<bool> NextArtistAsync(CancellationToken stop)
    {
        if (_progress.Pending.Count == 0)
            return false;
        // To the back until its songs are in, so one that keeps failing does not hold up the rest.
        var artist = _progress.Pending[0];
        _progress.Pending.RemoveAt(0);
        _progress.Pending.Add(artist);
        await ArtistAsync(artist, TopOfOtherArtist, played: false, stop);
        Save();
        return true;
    }

    private async Task ArtistAsync(CatalogArtist artist, int top, bool played, CancellationToken stop)
    {
        if (_progress.DoneArtists.Contains(artist.Id))
        {
            _progress.Pending.RemoveAll(a => a.Id == artist.Id);
            return;
        }
        string id = artist.Id.ToString(CultureInfo.InvariantCulture);
        string count = top.ToString(CultureInfo.InvariantCulture);
        if (await _web.TextAsync($"{Deezer}/artist/{id}/top?limit={count}", stop) is { } tracks)
            Enqueue(ParseDeezer(tracks));
        if (await _web.TextAsync($"{Deezer}/artist/{id}/related?limit={RelatedArtists}", stop) is { } related)
            AddPending(ParseArtists(related), first: played);
        _feedingArtist = artist;
    }

    private void Enqueue(IEnumerable<CatalogTrack> tracks)
    {
        foreach (var track in tracks)
            _tracks.Enqueue(track);
    }

    private void AddPending(IEnumerable<CatalogArtist> artists, bool first)
    {
        var known = _progress.DoneArtists.Concat(_progress.Pending.Select(a => a.Id)).ToHashSet();
        var fresh = artists.Where(a => known.Add(a.Id)).ToList();
        if (first)
            _progress.Pending.InsertRange(0, fresh);
        else
            _progress.Pending.AddRange(fresh);
        if (_progress.Pending.Count > MostPendingArtists)
            _progress.Pending.RemoveRange(MostPendingArtists, _progress.Pending.Count - MostPendingArtists);
    }

    /// <summary>Keeps what was fingerprinted and where it has got to.</summary>
    private void Save()
    {
        if (_batch.Count > 0)
        {
            _library.PutCatalog(_batch, DateTime.UtcNow);
            _batch.Clear();
        }
        if (_progressPath is null || !_loaded)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_progressPath)!);
        Settings.WriteAtomically(_progressPath, JsonSerializer.Serialize(_progress));
    }

    private void SaveQuietly()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DebugLog.Write($"the song catalog's place could not be kept: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- what the sites answer

    /// <summary>Only https, and only the two places previews really come from.</summary>
    internal static bool IsPreviewAddress(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && (uri.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".itunes.apple.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Songs from a Deezer answer. Anything not shaped like a song with a preview is left out.</summary>
    internal static List<CatalogTrack> ParseDeezer(string json)
    {
        var found = new List<CatalogTrack>();
        foreach (var t in Items(json, "data"))
        {
            if (Id(t, "id") is not { } id || Str(t, "preview") is not { Length: > 0 } preview || !IsPreviewAddress(preview))
                continue;
            string title = MediaControl.Clean(Str(t, "title"));
            string artist = t.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object ? MediaControl.Clean(Str(a, "name")) : "";
            if (title.Length > 0 && artist.Length > 0)
                found.Add(new CatalogTrack("deezer:" + id.ToString(CultureInfo.InvariantCulture), title, artist, "Deezer", preview));
        }
        return found;
    }

    /// <summary>Songs from an Apple search answer.</summary>
    internal static List<CatalogTrack> ParseApple(string json)
    {
        var found = new List<CatalogTrack>();
        foreach (var t in Items(json, "results"))
        {
            if (Id(t, "trackId") is not { } id || Str(t, "previewUrl") is not { Length: > 0 } preview || !IsPreviewAddress(preview))
                continue;
            string title = MediaControl.Clean(Str(t, "trackName"));
            string artist = MediaControl.Clean(Str(t, "artistName"));
            if (title.Length > 0 && artist.Length > 0)
                found.Add(new CatalogTrack("apple:" + id.ToString(CultureInfo.InvariantCulture), title, artist, "Apple", preview));
        }
        return found;
    }

    /// <summary>The same song on Apple, by its title and its first artist. Null when it has none: a near name is another song.</summary>
    internal static CatalogTrack? AppleMatch(IEnumerable<CatalogTrack> apple, CatalogTrack song)
        => apple.FirstOrDefault(a => SameSong(a.Title, a.Artist, song.Title, song.Artist));

    internal static List<CatalogArtist> ParseArtists(string json)
    {
        var found = new List<CatalogArtist>();
        foreach (var a in Items(json, "data"))
        {
            if (Id(a, "id") is { } id && MediaControl.Clean(Str(a, "name")) is { Length: > 0 } name)
                found.Add(new CatalogArtist(id, name));
        }
        return found;
    }

    internal static List<long> ParseGenres(string json) => Items(json, "data").Select(g => Id(g, "id")).OfType<long>().Where(i => i > 0).ToList();

    private const int NoData = 800;

    private static List<JsonElement> Items(string json, string list)
    {
        using var doc = JsonDocument.Parse(json);
        // Deezer says "too many requests" in a 200: thrown, so the run backs off. Its 800 is only "no data".
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int number) && number == NoData)
                return [];
            throw new InvalidOperationException("the site refused: " + MediaControl.Clean(error.ToString()));
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => i.Clone()).ToList();
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long? Id(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long id) && id > 0 ? id : null;

    // ---------------------------------------------------------------- names

    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();

    // "Song ft. Someone", "Song - Radio Edit", "Song feat Someone".
    [GeneratedRegex(@"(\s(ft\.?|feat\.?|featuring)\s.*|\s[-\u2013\u2014]\s.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tail();

    /// <summary>A name with case, brackets, guests, versions and punctuation taken out, for telling whether two names are one song.</summary>
    internal static string Normal(string name)
    {
        string bare = Tail().Replace(Brackets().Replace(name, " "), "");
        return new string(bare.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// Whether two entries are one song: the same title and first artist, whatever release or
    /// version. So a live cut and the studio one count as one, which is what is wanted when a
    /// match is judged. Two songs with one title by different artists are two songs; a title with
    /// nothing in it to compare is never the same as anything.
    /// </summary>
    internal static bool SameSong(string titleA, string artistA, string titleB, string artistB)
        => Normal(titleA).Length > 0 && NameKey(titleA, artistA) == NameKey(titleB, artistB);

    /// <summary>The song by its title and first artist, whatever release it is on.</summary>
    internal static string NameKey(string title, string artist)
    {
        int cut = artist.IndexOfAny(['&', ',', ';']);
        return Normal(title) + "|" + Normal(cut > 0 ? artist[..cut] : artist);
    }
}
