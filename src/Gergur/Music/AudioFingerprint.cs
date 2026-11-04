namespace Gergur.Music;

/// <summary>One fingerprint point: a hash of two spectral peaks and when the first one was.</summary>
/// <param name="Hash">The two peaks' frequencies and the time between them, 24 bits.</param>
/// <param name="Time">The first peak's frame (<see cref="AudioFingerprint.Hop"/> samples each).</param>
internal readonly record struct Landmark(uint Hash, int Time);

/// <summary>
/// Audio fingerprints in the way Shazam published (Wang, 2003): the strongest peaks of a
/// spectrogram, paired with peaks a little later, each pair hashed from the two frequencies and
/// the time between them. A song shares many such hashes with any clip of itself, and they line
/// up at one offset in time, which noise laid over it (someone talking) does not destroy: it adds
/// peaks of its own but leaves most of the song's in place.
///
/// Mono 16-bit-equivalent floats at <see cref="SampleRate"/>; nothing here keeps the audio.
/// </summary>
internal static class AudioFingerprint
{
    public const int SampleRate = 11025;
    public const int WindowSize = 1024;            // 93 ms
    public const int Hop = 256;                    // 23 ms per frame
    private const int Bins = WindowSize / 2;
    private const int MinBin = 8;                  // about 86 Hz
    private const int MaxBin = 480;                // about 5.2 kHz
    private const int TimeRadius = 8;              // a peak is the loudest within this many frames
    private const int FreqRadius = 10;             // and this many bins
    private const int Bands = 4;                   // peaks are chosen per band, so a voice cannot take them all
    private const int PeaksPerBandPerSecond = 5;
    private const int FanOut = 4;                  // each peak is paired with this many later ones
    private const int MinDt = 1, MaxDt = 63;       // 6 bits of time between the two
    private const int MaxDf = 63;                  // and the second within this many bins of the first

    private static readonly float[] Window = MakeWindow();

    private static float[] MakeWindow()
    {
        var w = new float[WindowSize];
        for (int i = 0; i < WindowSize; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1)));
        return w;
    }

    /// <summary>The landmarks of some audio, in time order.</summary>
    public static List<Landmark> Landmarks(ReadOnlySpan<float> samples)
    {
        var spectrogram = Spectrogram(samples);
        var peaks = Peaks(spectrogram);
        return Pair(peaks);
    }

    /// <summary>Log magnitude, frame by frame, bins <see cref="MinBin"/> to <see cref="MaxBin"/>.</summary>
    internal static float[][] Spectrogram(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length < WindowSize ? 0 : 1 + (samples.Length - WindowSize) / Hop;
        var result = new float[frames][];
        var re = new double[WindowSize];
        var im = new double[WindowSize];
        for (int f = 0; f < frames; f++)
        {
            int start = f * Hop;
            for (int i = 0; i < WindowSize; i++)
            {
                re[i] = samples[start + i] * Window[i];
                im[i] = 0;
            }
            Fft(re, im);
            var row = new float[MaxBin - MinBin];
            for (int b = MinBin; b < MaxBin; b++)
                row[b - MinBin] = (float)Math.Log(re[b] * re[b] + im[b] * im[b] + 1e-10);
            result[f] = row;
        }
        return result;
    }

    /// <summary>In-place radix-2 FFT of <see cref="WindowSize"/> points.</summary>
    internal static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    double next = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = next;
                }
            }
        }
    }

    /// <summary>
    /// The peaks: points louder than everything within <see cref="TimeRadius"/> frames and
    /// <see cref="FreqRadius"/> bins, of which the loudest few per band per second are kept.
    /// </summary>
    internal static List<(int Time, int Bin)> Peaks(float[][] s)
    {
        int frames = s.Length, bins = MaxBin - MinBin;
        if (frames == 0)
            return [];
        // Separable maximum: across frequency, then across time.
        var freqMax = new float[frames][];
        for (int t = 0; t < frames; t++)
        {
            var row = new float[bins];
            for (int b = 0; b < bins; b++)
            {
                float m = float.MinValue;
                for (int k = Math.Max(0, b - FreqRadius); k <= Math.Min(bins - 1, b + FreqRadius); k++)
                    m = Math.Max(m, s[t][k]);
                row[b] = m;
            }
            freqMax[t] = row;
        }
        var candidates = new List<(int Time, int Bin, float Level)>();
        for (int t = 0; t < frames; t++)
        {
            for (int b = 0; b < bins; b++)
            {
                float v = s[t][b];
                if (v < freqMax[t][b])
                    continue;
                bool top = true;
                for (int u = Math.Max(0, t - TimeRadius); u <= Math.Min(frames - 1, t + TimeRadius) && top; u++)
                    top = freqMax[u][b] <= v;
                if (top && v > -10)   // not silence
                    candidates.Add((t, b, v));
            }
        }
        // The loudest few per band per second.
        int perBlock = PeaksPerBandPerSecond;
        int block = SampleRate / Hop;   // frames in about a second
        int bandWidth = (bins + Bands - 1) / Bands;
        var kept = new List<(int Time, int Bin)>();
        foreach (var group in candidates.GroupBy(c => (c.Time / block, c.Bin / bandWidth)))
        {
            foreach (var c in group.OrderByDescending(c => c.Level).Take(perBlock))
                kept.Add((c.Time, c.Bin + MinBin));
        }
        kept.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Bin.CompareTo(b.Bin));
        return kept;
    }

    /// <summary>Each peak with the next <see cref="FanOut"/> peaks in its target zone.</summary>
    internal static List<Landmark> Pair(List<(int Time, int Bin)> peaks)
    {
        var result = new List<Landmark>(peaks.Count * FanOut);
        for (int i = 0; i < peaks.Count; i++)
        {
            var (t1, f1) = peaks[i];
            int made = 0;
            for (int j = i + 1; j < peaks.Count && made < FanOut; j++)
            {
                var (t2, f2) = peaks[j];
                int dt = t2 - t1;
                if (dt < MinDt)
                    continue;
                if (dt > MaxDt)
                    break;
                if (Math.Abs(f2 - f1) > MaxDf)
                    continue;
                result.Add(new Landmark(HashOf(f1, f2, dt), t1));
                made++;
            }
        }
        return result;
    }

    /// <summary>9 bits each frequency, 6 bits the time between: 24 bits.</summary>
    internal static uint HashOf(int f1, int f2, int dt) => ((uint)f1 & 0x1FF) << 15 | ((uint)f2 & 0x1FF) << 6 | ((uint)dt & 0x3F);
}

/// <summary>
/// Songs as fingerprints, and the search: every landmark of a clip looked up, and the song whose
/// matches agree on one offset in time the most is the answer, when they agree enough.
/// </summary>
internal sealed class FingerprintIndex
{
    // hash (24) | song (20) | time (20), sorted: a hash's entries are one run, found by binary search.
    private ulong[] _entries = [];
    private readonly List<ulong> _pending = [];
    private bool _sorted = true;

    public int Count => _entries.Length + _pending.Count;

    public void Add(int song, IEnumerable<Landmark> landmarks)
    {
        foreach (var l in landmarks)
            _pending.Add(Pack(l.Hash, song, l.Time));
        _sorted = false;
    }

    /// <summary>An index of entries already packed (<see cref="Pack"/>), sorted here in place.</summary>
    internal static FingerprintIndex FromPacked(ulong[] entries)
    {
        Array.Sort(entries);
        return new FingerprintIndex { _entries = entries };
    }

    internal static ulong Pack(uint hash, int song, int time) => (ulong)hash << 40 | (ulong)((uint)song & 0xFFFFF) << 20 | (ulong)((uint)time & 0xFFFFF);

    /// <summary>Sorts what was added, so it can be searched. Done by the first search otherwise.</summary>
    public void Seal()
    {
        if (_sorted)
            return;
        var all = new ulong[_entries.Length + _pending.Count];
        _entries.CopyTo(all, 0);
        _pending.CopyTo(all, _entries.Length);
        Array.Sort(all);
        _entries = all;
        _pending.Clear();
        _sorted = true;
    }

    /// <summary>The best song for a clip, with how many landmarks agreed, and the runner-up's count.</summary>
    public (int Song, int Score, int Second, double OffsetSeconds) Best(IReadOnlyList<Landmark> clip)
    {
        Seal();
        var votes = new Dictionary<(int Song, int Offset), int>();
        foreach (var l in clip)
        {
            ulong low = (ulong)l.Hash << 40;
            int i = LowerBound(low);
            for (; i < _entries.Length && (_entries[i] >> 40) == l.Hash; i++)
            {
                int song = (int)(_entries[i] >> 20 & 0xFFFFF);
                int time = (int)(_entries[i] & 0xFFFFF);
                var key = (song, time - l.Time);
                votes[key] = votes.GetValueOrDefault(key) + 1;
            }
        }
        // A clip's frames do not line up with the song's, so a landmark can land one frame off:
        // each offset counts its neighbours too.
        var perSong = new Dictionary<int, (int Score, int Offset)>();
        foreach (var ((song, offset), n) in votes)
        {
            int score = n + votes.GetValueOrDefault((song, offset - 1)) + votes.GetValueOrDefault((song, offset + 1));
            if (!perSong.TryGetValue(song, out var best) || score > best.Score)
                perSong[song] = (score, offset);
        }
        var ranked = perSong.OrderByDescending(p => p.Value.Score).Take(2).ToList();
        if (ranked.Count == 0)
            return (-1, 0, 0, 0);
        return (ranked[0].Key, ranked[0].Value.Score, ranked.Count > 1 ? ranked[1].Value.Score : 0,
            ranked[0].Value.Offset * (double)AudioFingerprint.Hop / AudioFingerprint.SampleRate);
    }

    private int LowerBound(ulong value)
    {
        int lo = 0, hi = _entries.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_entries[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
