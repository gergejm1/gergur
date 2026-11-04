using System.Text.Json;
using Gergur.App;
using Gergur.Diagnostics;

namespace Gergur.Music;

/// <summary>A song Gergur has learned: its names, and how much of it was heard.</summary>
/// <param name="Id">Its number here, which names its fingerprint file.</param>
/// <param name="Key">Which song, "youtube:" and the video id.</param>
/// <param name="Seconds">How much of it was heard, so a longer hearing replaces a shorter one.</param>
internal sealed record LearnedSong(int Id, string Key, string Title, string Artist, string Site, DateTime LearnedUtc, double Seconds);

/// <summary>The answer to "what song is this?".</summary>
/// <param name="Song">The song, or null when none matched well enough.</param>
/// <param name="Score">How many of the clip's landmarks agreed with it at one offset.</param>
/// <param name="RunnerUp">The next best song's count.</param>
/// <param name="Known">How many songs were asked.</param>
internal sealed record Identification(LearnedSong? Song, int Score, int RunnerUp, int Known);

/// <summary>
/// The songs learned, as fingerprints, in <c>songs</c> in the data folder: <c>songs.json</c>
/// lists them, and each one's landmarks are a file of its own (<c>{id}.fp</c>). Never audio.
/// Held to the rule the other stores keep: a list that is there and cannot be read is never
/// written over, so nothing is learned that run.
///
/// Thread safe. Learning and asking run off the UI thread; the UI thread only reads the list,
/// under a lock no file work is ever done in. The search index is built from the files when a
/// question needs it and let go a few minutes after the last one (<see cref="ReleaseIdle"/>,
/// called by <see cref="SongListener"/>), since it can be large and questions are rare: one
/// array of every landmark, 8 bytes each, about 37 KB a minute of song heard.
/// </summary>
internal sealed partial class SongLibrary
{
    /// <summary>
    /// At most this many songs; past it the one learned longest ago goes. Full of four minute
    /// songs, the index for a question is about 75 MB, held for <see cref="IndexKept"/>.
    /// </summary>
    public const int MaxSongs = 500;

    /// <summary>A match needs this many landmarks agreeing (wrong songs measured at 3 at most)...</summary>
    public const int MinScore = 10;

    /// <summary>...and this many times the next best song's count.</summary>
    public const int MinLead = 3;

    /// <summary>How long the index is kept after the last question.</summary>
    public static readonly TimeSpan IndexKept = TimeSpan.FromMinutes(3);

    private const uint Magic = 0x31504647;   // "GFP1"
    private const int MostLandmarks = 400_000; // far past a 10 minute song's

    public static string DefaultFolder => Path.Combine(Settings.DataDir, "songs");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly int _maxSongs;
    private readonly object _gate = new();
    private readonly object _writeGate = new();   // one Put at a time; never held by the UI thread
    private readonly List<LearnedSong> _songs;
    private readonly bool _unreadable;
    private FingerprintIndex? _index;
    private int _indexVersion = -1;
    private int _version;
    private DateTime _indexUsedUtc;

    /// <param name="folder">Where the songs live. Defaults to the profile; a test passes its own.</param>
    /// <param name="maxSongs">How many are kept; a test passes fewer.</param>
    public SongLibrary(string? folder = null, int maxSongs = MaxSongs)
    {
        _folder = folder ?? DefaultFolder;
        _maxSongs = maxSongs;
        (_songs, _unreadable) = Load(ListPath);
        if (!_unreadable)
            SweepUnlisted();
    }

    /// <summary>
    /// Removes fingerprint files the list does not name: left by a run that stopped between
    /// writing one and listing it, or by a replaced one that was being read. Only this
    /// folder's own names ({number}.fp and its .tmp) are ever touched.
    /// </summary>
    private void SweepUnlisted()
    {
        if (!Directory.Exists(_folder))
            return;
        var listed = _songs.Select(s => s.Id + ".fp").ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string file in Directory.EnumerateFiles(_folder))
            {
                string name = Path.GetFileName(file);
                if (OwnFileName().IsMatch(name) && !listed.Contains(name))
                    TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"the songs folder could not be tidied: {ex.Message}");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]{1,7}\.fp(\.tmp)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex OwnFileName();

    private string ListPath => Path.Combine(_folder, "songs.json");

    private string FileOf(int id) => Path.Combine(_folder, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".fp");

    /// <summary>Whether the list was there and could not be read, so nothing is learned this run.</summary>
    public bool Unreadable => _unreadable;

    public int Count
    {
        get { lock (_gate) return _songs.Count; }
    }

    public IReadOnlyList<LearnedSong> Songs
    {
        get { lock (_gate) return _songs.ToArray(); }
    }

    /// <summary>How much of a song has been heard, 0 for one never learned.</summary>
    public double SecondsOf(string key)
    {
        lock (_gate)
            return _songs.FirstOrDefault(s => s.Key == key)?.Seconds ?? 0;
    }

    /// <summary>
    /// Keeps a song's landmarks, from <paramref name="seconds"/> of it heard: added, or put in
    /// place of what was kept for it when this heard more. Whether it was kept. Throws
    /// (IOException) when the files cannot be written.
    /// </summary>
    public bool Put(SongInfo info, double seconds, IReadOnlyList<Landmark> landmarks, DateTime nowUtc)
    {
        if (landmarks.Count == 0 || landmarks.Count > MostLandmarks)
            return false;
        lock (_writeGate)
        {
            int at, id;
            lock (_gate)
            {
                if (_unreadable)
                    return false;
                at = _songs.FindIndex(s => s.Key == info.Key);
                if (at >= 0 && _songs[at].Seconds >= seconds)
                    return false;
                // A new file every time, never one written over: a question may be reading
                // the old one, and Windows will not replace a file that is open.
                id = NextId();
            }
            Directory.CreateDirectory(_folder);
            WriteLandmarks(FileOf(id), landmarks);
            var song = new LearnedSong(id, info.Key, info.Title, info.Artist, info.Site, nowUtc, Math.Round(seconds, 1));
            List<LearnedSong> before;
            var gone = new List<LearnedSong>();
            LearnedSong? replaced = null;
            string json;
            lock (_gate)
            {
                before = _songs.ToList();
                at = _songs.FindIndex(s => s.Key == info.Key);
                if (at >= 0)
                    replaced = _songs[at];
                if (at >= 0)
                    _songs[at] = song;
                else
                    _songs.Add(song);
                while (_songs.Count > _maxSongs)
                {
                    var oldest = _songs.MinBy(s => s.LearnedUtc)!;
                    _songs.Remove(oldest);
                    gone.Add(oldest);
                }
                json = JsonSerializer.Serialize(_songs, JsonOptions);
                // With the swap, so a cached index is never paired with the new list.
                _version++;
            }
            try
            {
                Settings.WriteAtomically(ListPath, json);
            }
            catch
            {
                lock (_gate)
                {
                    _songs.Clear();
                    _songs.AddRange(before);
                    _version++;
                }
                TryDelete(FileOf(id));   // nothing lists it
                throw;
            }
            if (replaced is not null)
                gone.Add(replaced);
            foreach (var old in gone)
                TryDelete(FileOf(old.Id));
            return true;
        }
    }

    /// <summary>An id no song has and no file is left under (one whose removal failed may still be open).</summary>
    private int NextId()
    {
        int id = _songs.Count == 0 ? 1 : _songs.Max(s => s.Id) + 1;
        while (id <= 0xFFFFF && File.Exists(FileOf(id)))
            id++;
        return id <= 0xFFFFF ? id : Enumerable.Range(1, 0xFFFFF).First(i => _songs.All(s => s.Id != i) && !File.Exists(FileOf(i)));
    }

    /// <summary>For a test: runs between measuring the files and reading them for an index.</summary>
    internal Action? AfterSizing { get; set; }

    /// <summary>
    /// Which song a clip is: the best match when it clears <see cref="MinScore"/> and leads the
    /// next best by <see cref="MinLead"/> times, otherwise none.
    /// </summary>
    public Identification Identify(ReadOnlySpan<float> clip, DateTime nowUtc)
    {
        var marks = AudioFingerprint.Landmarks(clip);
        var (index, songs) = IndexFor(nowUtc);
        var (id, score, second, _) = index.Best(marks);
        var song = songs.FirstOrDefault(s => s.Id == id);
        bool sure = song is not null && Matches(score, second);
        return new Identification(sure ? song : null, score, second, songs.Length);
    }

    /// <summary>The rule a match must clear.</summary>
    internal static bool Matches(int score, int runnerUp) => score >= MinScore && score >= MinLead * runnerUp;

    /// <summary>Builds the index now, so a question that comes later need not wait for it.</summary>
    public void Prepare(DateTime nowUtc) => IndexFor(nowUtc);

    private (FingerprintIndex Index, LearnedSong[] Songs) IndexFor(DateTime nowUtc)
    {
        LearnedSong[] songs;
        int version;
        lock (_gate)
        {
            _indexUsedUtc = nowUtc;
            if (_index is { } ready && _indexVersion == _version)
                return (ready, _songs.ToArray());
            songs = _songs.ToArray();
            version = _version;
        }
        // One array of the right size, filled from the files and sorted in place: no lists
        // of landmarks on the way, which would hold the library two or three times over.
        var sized = new List<(LearnedSong Song, int Count)>();
        long total = 0;
        foreach (var song in songs)
        {
            try
            {
                long length = new FileInfo(FileOf(song.Id)).Length;
                long count = (length - 8) / 8;
                if (length >= 8 && (length - 8) % 8 == 0 && count <= MostLandmarks)
                {
                    sized.Add((song, (int)count));
                    total += count;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or unreadable: not asked.
            }
        }
        AfterSizing?.Invoke();
        var entries = new ulong[total];
        int filled = 0;
        var found = new List<LearnedSong>();
        foreach (var (song, _) in sized)
        {
            int read = ReadInto(FileOf(song.Id), song.Id, entries, filled);
            if (read >= 0)
            {
                filled += read;
                found.Add(song);
            }
        }
        if (filled < entries.Length)
            Array.Resize(ref entries, filled);
        var index = FingerprintIndex.FromPacked(entries);
        lock (_gate)
        {
            if (_version == version)
            {
                _index = index;
                _indexVersion = version;
            }
        }
        return (index, found.ToArray());
    }

    /// <summary>Lets the index go when no question has needed it for <see cref="IndexKept"/>.</summary>
    public void ReleaseIdle(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_index is not null && nowUtc - _indexUsedUtc > IndexKept)
                _index = null;
        }
    }

    internal bool HoldsIndex
    {
        get { lock (_gate) return _index is not null; }
    }

    private static void WriteLandmarks(string path, IReadOnlyList<Landmark> landmarks)
    {
        string staging = path + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Magic);
                w.Write(landmarks.Count);
                foreach (var l in landmarks)
                {
                    w.Write(l.Hash);
                    w.Write(l.Time);
                }
            }
            File.Move(staging, path, overwrite: true);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Opens a fingerprint file so that learning can remove it while it is read.</summary>
    internal static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Packs a song's landmarks into <paramref name="entries"/> from <paramref name="at"/>, as
    /// the index keeps them: how many, or -1 for a file that is missing, not one of these, or
    /// longer than the room left (it changed since it was measured).
    /// </summary>
    private static int ReadInto(string path, int song, ulong[] entries, int at)
    {
        try
        {
            using var stream = OpenShared(path);
            using var r = new BinaryReader(stream);
            if (stream.Length < 8 || r.ReadUInt32() != Magic)
                return -1;
            int count = r.ReadInt32();
            if (count < 0 || count > MostLandmarks || stream.Length != 8 + 8L * count || at + (long)count > entries.Length)
                return -1;
            for (int i = 0; i < count; i++)
            {
                uint hash = r.ReadUInt32();
                int time = r.ReadInt32();
                if (hash > 0xFFFFFF || time < 0 || time > 0xFFFFF)
                    return -1;
                entries[at + i] = FingerprintIndex.Pack(hash, song, time);
            }
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>A song's landmarks, or null for a file that is missing or not one of these.</summary>
    internal static List<Landmark>? ReadLandmarks(string path)
    {
        try
        {
            using var stream = OpenShared(path);
            using var r = new BinaryReader(stream);
            if (stream.Length < 8 || r.ReadUInt32() != Magic)
                return null;
            int count = r.ReadInt32();
            if (count < 0 || count > MostLandmarks || stream.Length != 8 + 8L * count)
                return null;
            var marks = new List<Landmark>(count);
            for (int i = 0; i < count; i++)
            {
                uint hash = r.ReadUInt32();
                int time = r.ReadInt32();
                if (hash > 0xFFFFFF || time < 0 || time > 0xFFFFF)
                    return null;
                marks.Add(new Landmark(hash, time));
            }
            return marks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"a song fingerprint file could not be removed (it goes at the next start): {ex.Message}");
        }
    }

    private static (List<LearnedSong> Songs, bool Unreadable) Load(string path)
    {
        if (!File.Exists(path))
            return ([], false);
        try
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return ([], false);
            var songs = JsonSerializer.Deserialize<List<LearnedSong>>(text) ?? [];
            if (songs.Any(s => s is null || s.Id is <= 0 or > 0xFFFFF || string.IsNullOrEmpty(s.Key) || s.Title is null || s.Artist is null || s.Site is null)
                || songs.Select(s => s.Id).Distinct().Count() != songs.Count
                || songs.Select(s => s.Key).Distinct().Count() != songs.Count)
                throw new JsonException("an entry is not a song");
            return (songs, false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DebugLog.WriteAlways($"songs.json could not be read, so no songs are learned this run: {ex.Message}");
            return ([], true);
        }
    }
}
