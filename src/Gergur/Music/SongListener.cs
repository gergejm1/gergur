using Gergur.Diagnostics;

namespace Gergur.Music;

/// <summary>What the listener needs of a tab: whether it is heard, since when, its page, and running a script in it.</summary>
internal interface IHearingTab
{
    string Url { get; }
    bool IsPlayingAudio { get; }
    DateTime? SoundStartedUtc { get; }
    Task<string?> HearAsync(string js, TimeSpan budget);
}

/// <summary>The last few seconds heard from one tab, for a question about what plays now.</summary>
internal sealed class RecentAudio
{
    public const int Capacity = 15 * AudioFingerprint.SampleRate;
    private readonly List<short> _samples = new(Capacity);

    public int Count => _samples.Count;

    /// <summary>When audio last came in.</summary>
    public DateTime LastUtc { get; private set; }

    public void Add(short[] samples, DateTime nowUtc)
    {
        _samples.AddRange(samples);
        if (_samples.Count > Capacity)
            _samples.RemoveRange(0, _samples.Count - Capacity);
        LastUtc = nowUtc;
    }

    /// <summary>The newest <paramref name="count"/> samples, or all there are.</summary>
    public short[] Last(int count) => _samples.Skip(Math.Max(0, _samples.Count - count)).ToArray();

    public void Clear()
    {
        _samples.Clear();
        LastUtc = default;
    }
}

/// <summary>A song being learned: what has been heard of it so far, and how much of that is kept already.</summary>
internal sealed class LearnSession(SongInfo info, DateTime startedUtc)
{
    private readonly List<short> _samples = [];

    public SongInfo Info { get; } = info;
    public DateTime LastHeardUtc { get; private set; } = startedUtc;
    public double CommittedSeconds { get; set; }
    public double Seconds => _samples.Count / (double)AudioFingerprint.SampleRate;

    public void Add(short[] samples, DateTime nowUtc)
    {
        int room = (int)(SongListener.LearnAtMost * AudioFingerprint.SampleRate) - _samples.Count;
        if (room > 0)
            _samples.AddRange(room >= samples.Length ? samples : samples[..room]);
        LastHeardUtc = nowUtc;
    }

    public short[] Samples() => _samples.ToArray();
}

/// <summary>
/// The song recognizer's ear, on the UI thread, ticked by the windows' once-a-second timer
/// (<see cref="Tick"/>, at most every <see cref="Every"/>): it learns the songs the person
/// plays, and answers what song a tab is playing (<see cref="IdentifyAsync"/>).
///
/// Learning: while one of the person's tabs plays a song (<see cref="SongPage.Judge"/>: music
/// on YouTube or YouTube Music) and learning is on, the page is listened to and what it heard
/// is taken every few seconds. Once <see cref="LearnAtLeast"/> seconds of it are heard its
/// fingerprints are kept, and again every <see cref="CommitEvery"/> seconds after, up to
/// <see cref="LearnAtMost"/> or the whole song. A song learned in full is not listened to
/// again. Ads and silence are not heard (the page skips them).
///
/// Asking: a page that is itself a song says which at once. Otherwise the last
/// <see cref="ClipSeconds"/> of the tab, already heard if it was being listened to, else
/// listened for (up to <see cref="ListenAtMost"/>), looked up in the library off the UI thread,
/// and looked up again every <see cref="StepSeconds"/> as it plays on, up to
/// <see cref="KeepListening"/>: much of the library is 30 s previews, and the part of a song
/// the library has may be yet to come.
/// </summary>
internal sealed class SongListener
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);
    public const double LearnAtLeast = 30;
    public const double LearnAtMost = 360;
    public const double CommitEvery = 60;

    /// <summary>A song is counted as heard in full this close to its end.</summary>
    public const double WholeSongSlack = 15;

    /// <summary>A song nothing has been heard of for this long is done with (paused and left).</summary>
    public static readonly TimeSpan QuietEnds = TimeSpan.FromSeconds(60);

    public const double ClipSeconds = 10;
    public const double StepSeconds = 5;
    public static readonly TimeSpan ListenAtMost = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long a question goes on before it says the song was not found. Measured on twelve
    /// real songs against previews: when it came, the answer came 11 to 33 s after asking on average.
    /// </summary>
    public static readonly TimeSpan KeepListening = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A match this strong stands alone. A weaker one must come up for the same song again in the
    /// same question, since a question looks many times over and a large library gives chance
    /// more to work with. It need not be the very next clip: talk over a song comes and goes.
    /// On real songs a right answer's median was 32, and a wrong song scored under this.
    /// </summary>
    public const int SureAtOnce = 20;

    /// <summary>Audio already heard counts as "now" for a question when it is this fresh.</summary>
    public static readonly TimeSpan FreshEnough = TimeSpan.FromSeconds(4);

    private const int Rate = AudioFingerprint.SampleRate;

    private readonly Func<SongLibrary> _library;
    private readonly Func<bool> _learn;
    private readonly Func<Action, Task> _background;
    private readonly TimeSpan _keepListening;
    private readonly RecentAudio _recent = new();
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private IHearingTab? _subject;
    private string _ear = "";
    private string? _recentUrl;
    private SongInfo? _judged;
    private LearnSession? _learning;
    private Ask? _ask;
    private bool _asked;
    private bool _busy;
    private DateTime _lastTick;
    private Task _work = Task.CompletedTask;

    private sealed class Ask(IHearingTab tab, DateTime untilUtc)
    {
        public IHearingTab Tab { get; } = tab;
        public DateTime UntilUtc { get; } = untilUtc;
        public int Fresh { get; set; }
        public int Need { get; init; } = (int)(ClipSeconds * Rate);

        /// <summary>The page turned out to say itself which song it is.</summary>
        public SongInfo? Named { get; set; }

        public TaskCompletionSource<short[]?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <param name="library">The songs; asked for only when needed, so it is not read at startup.</param>
    /// <param name="learn">Whether learning is on, read on each tick.</param>
    /// <param name="background">Runs fingerprinting off the UI thread (Task.Run); a test runs it in place.</param>
    /// <param name="keepListening">How long a question goes on; null is <see cref="KeepListening"/>, a test passes less.</param>
    public SongListener(Func<SongLibrary> library, Func<bool> learn, Func<Action, Task>? background = null, TimeSpan? keepListening = null)
    {
        _keepListening = keepListening ?? KeepListening;
        _library = library;
        _learn = learn;
        _background = background ?? (a => Task.Run(a));
    }

    /// <summary>The song being learned now, for a test.</summary>
    internal LearnSession? Learning => _learning;

    /// <summary>Whether a question is waiting on what the tab plays, for a test to tick against.</summary>
    internal bool Asking => _ask is not null;

    /// <summary>Fingerprinting and saving under way, for a test to wait on.</summary>
    internal Task Work => _work;

    /// <summary>
    /// Listens once: picks the tab (the one asked about, else the one heard that began
    /// last), takes what its page heard, and learns from it. Does nothing while the last tick
    /// is still waiting on its page, nor sooner than <see cref="Every"/> after it unless
    /// <paramref name="force"/>.
    /// </summary>
    public async Task Tick(IEnumerable<IHearingTab> tabs, DateTime nowUtc, bool force = false)
    {
        if (_busy || (!force && nowUtc - _lastTick < Every))
            return;
        _lastTick = nowUtc;
        _busy = true;
        try
        {
            // The search index goes a few minutes after the last question; the library is not
            // read for it when no question was ever asked.
            if (_asked)
                _library().ReleaseIdle(nowUtc);
            await TickCore(tabs, nowUtc);
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"the song listener failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task TickCore(IEnumerable<IHearingTab> tabs, DateTime nowUtc)
    {
        if (_ask is { } expired && nowUtc > expired.UntilUtc)
        {
            expired.Done.TrySetResult(null);
            _ask = null;
        }
        var playing = tabs.Where(t => t.IsPlayingAudio).MaxBy(t => t.SoundStartedUtc ?? DateTime.MinValue);
        var subject = _ask?.Tab ?? playing;
        if (!ReferenceEquals(subject, _subject))
        {
            Finish();
            if (_subject is { } old)
                StopQuietly(old);
            ForgetRecent();
            _ear = "";
            _recentUrl = null;
            _judged = null;
            _subject = subject;
        }
        if (_learning is { } quiet && nowUtc - quiet.LastHeardUtc > QuietEnds)
            Finish();
        if (subject is null)
            return;

        bool learn = _learn();
        if (!learn)
            Finish();
        // Unasked, only a page on YouTube's own sites is read at all: those are the songs learned.
        string url = subject.Url;
        string? songSite = SongPage.SongSiteOf(url);
        if (_ask is null && (!learn || songSite is null))
            return;
        bool listen = _ask is not null || (_judged is { } judged && Wants(judged));
        string? json = await subject.HearAsync(SongPage.TakeScript(listen), ReadBudget);
        if (!ReferenceEquals(subject, _subject) || SongPage.Parse(json) is not { } page)
            return;
        // The page moved on while it was asked: what it heard may be the last page's.
        if (subject.Url != url)
            return;
        // Audio heard on another page, or by a listener since gone, is not what plays now.
        if (page.Ear != _ear || url != _recentUrl)
        {
            ForgetRecent();
            _ear = page.Ear;
            _recentUrl = url;
        }
        if (page.Listening && page.AudioState != "running")
            DebugLog.Write($"the page's song listener is {page.AudioState}, so it hears nothing");
        // A song only from a page whose own address is on the site it says it is.
        var info = songSite is not null && string.Equals(songSite, page.Host, StringComparison.OrdinalIgnoreCase) ? SongPage.Judge(page) : null;
        _judged = info;
        foreach (var part in page.Parts)
            Take(part, info, learn, nowUtc);

        if (_ask is { } named && ReferenceEquals(named.Tab, subject) && info is not null)
        {
            named.Named = info;
            named.Done.TrySetResult([]);
            _ask = null;
        }
        else if (_ask is { } ask && ReferenceEquals(ask.Tab, subject) && ask.Fresh >= ask.Need)
        {
            ask.Done.TrySetResult(_recent.Last((int)(ClipSeconds * Rate)));
            _ask = null;
        }
    }

    /// <summary>Drops the audio heard so far, and what an open question counted of it.</summary>
    private void ForgetRecent()
    {
        _recent.Clear();
        if (_ask is { } ask)
            ask.Fresh = 0;
    }

    private void Take(HeardPart part, SongInfo? info, bool learn, DateTime nowUtc)
    {
        _recent.Add(part.Samples, nowUtc);
        if (_ask is { } ask)
            ask.Fresh += part.Samples.Length;
        if (!learn)
            return;
        string key = "youtube:" + part.VideoId;
        if (_learning is { } other && other.Info.Key != key)
            Finish();
        if (_learning is null && info is not null && info.Key == key && Wants(info))
            _learning = new LearnSession(info, nowUtc);
        if (_learning is not { } session)
            return;
        session.Add(part.Samples, nowUtc);
        if (session.Seconds >= Target(session.Info) - WholeSongSlack || session.Seconds >= LearnAtMost)
            Finish();
        else if (session.Seconds >= Math.Max(LearnAtLeast, session.CommittedSeconds + (session.CommittedSeconds > 0 ? CommitEvery : 0)))
            Commit(session);
    }

    /// <summary>How much of a song there is to hear: all of it, up to <see cref="LearnAtMost"/>.</summary>
    internal static double Target(SongInfo info) => info.Seconds > 0 ? Math.Min(LearnAtMost, info.Seconds) : LearnAtMost;

    /// <summary>Whether a song is worth listening to: not heard in full yet, this run or before.</summary>
    private bool Wants(SongInfo info)
    {
        if (_done.Contains(info.Key))
            return false;
        var library = _library();
        return !library.Unreadable && library.SecondsOf(info.Key) < Target(info) - WholeSongSlack;
    }

    /// <summary>Done with the song being learned: what was heard of it is kept, when it is enough.</summary>
    private void Finish()
    {
        if (_learning is not { } session)
            return;
        _learning = null;
        if (session.Seconds >= LearnAtLeast && session.Seconds > session.CommittedSeconds)
            Commit(session);
        if (session.Seconds >= Target(session.Info) - WholeSongSlack)
            _done.Add(session.Info.Key);
    }

    /// <summary>Fingerprints what has been heard of a song and keeps it, off the UI thread, one at a time.</summary>
    private void Commit(LearnSession session)
    {
        session.CommittedSeconds = session.Seconds;
        var samples = session.Samples();
        var info = session.Info;
        double seconds = session.CommittedSeconds;
        var library = _library;
        var prior = _work;
        _work = Then(prior, () =>
        {
            try
            {
                var marks = AudioFingerprint.Landmarks(ToFloats(samples));
                if (library().Put(info, seconds, marks, DateTime.UtcNow))
                    DebugLog.Write($"learned {seconds:F0} s of a song, {marks.Count} landmarks: {info.Title} by {info.Artist}");
            }
            catch (Exception ex)
            {
                DebugLog.WriteAlways($"a song could not be kept: {ex.Message}");
            }
        });
    }

    private async Task Then(Task prior, Action work)
    {
        try
        {
            await prior;
        }
        catch
        {
            // Logged where it failed.
        }
        await _background(work);
    }

    /// <summary>
    /// What song <paramref name="tab"/> is playing: null when it could not be heard (silent,
    /// paused, a page that cannot be listened to), else the library's answer. UI thread.
    /// </summary>
    public async Task<Identification?> IdentifyAsync(IHearingTab tab, DateTime nowUtc)
    {
        int clipLength = (int)(ClipSeconds * Rate);
        var library = _library();
        _asked = true;
        // The page says itself which song this is: nothing to listen for.
        if (ReferenceEquals(_subject, tab) && tab.Url == _recentUrl && _judged is { } known)
            return Named(known, library, nowUtc);
        var prepared = _background(() => library.Prepare(nowUtc));
        Identification? answer = null;
        LearnedSong? tentative = null;
        var giveUp = nowUtc + _keepListening;
        for (bool first = true; ; first = false)
        {
            short[]? clip;
            if (first && ReferenceEquals(_subject, tab) && tab.Url == _recentUrl && _recent.Count >= clipLength && nowUtc - _recent.LastUtc <= FreshEnough)
            {
                clip = _recent.Last(clipLength);
            }
            else
            {
                var from = first ? nowUtc : _lastTick;
                var ask = _ask is { } waiting && ReferenceEquals(waiting.Tab, tab)
                    ? waiting
                    : new Ask(tab, from + ListenAtMost) { Need = first ? clipLength : (int)(StepSeconds * Rate) };
                if (!ReferenceEquals(_ask, ask))
                {
                    _ask?.Done.TrySetResult(null);
                    _ask = ask;
                }
                if (first)
                    _ = Tick([], nowUtc, force: true);
                var done = await Task.WhenAny(ask.Done.Task, Task.Delay(ListenAtMost + TimeSpan.FromSeconds(5)));
                clip = done == ask.Done.Task ? ask.Done.Task.Result : null;
                if (ReferenceEquals(_ask, ask))
                    _ask = null;
                if (ask.Named is { } named)
                    return Named(named, library, nowUtc);
            }
            await prepared;
            // Stopped being heard, or another question took over: what it found so far stands.
            if (clip is null)
                return answer;
            var floats = ToFloats(clip);
            Identification? found = null;
            await _background(() => found = library.Identify(floats, _lastTick > nowUtc ? _lastTick : nowUtc));
            bool last = _keepListening <= TimeSpan.Zero || _lastTick >= giveUp;
            if (found is { Song: { } song })
            {
                bool confirmed = found.Score >= SureAtOnce || _keepListening <= TimeSpan.Zero
                    || (tentative is not null && SongCatalog.SameSong(tentative.Title, tentative.Artist, song.Title, song.Artist));
                if (confirmed)
                    return found;
                tentative = song;
                answer = found with { Song = null };
            }
            else
            {
                answer = found;
            }
            if (last)
                return answer;
        }
    }

    private static Identification Named(SongInfo song, SongLibrary library, DateTime nowUtc)
        => new(new LearnedSong(0, song.Key, song.Title, song.Artist, song.Site, nowUtc, 0), 0, 0, library.Count);

    internal static float[] ToFloats(short[] samples)
    {
        var f = new float[samples.Length];
        for (int i = 0; i < f.Length; i++)
            f[i] = samples[i] / 32768f;
        return f;
    }

    private static async void StopQuietly(IHearingTab tab)
    {
        try
        {
            await tab.HearAsync(SongPage.StopScript, ReadBudget);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"a page's song listener could not be stopped (it stops itself): {ex.Message}");
        }
    }
}
