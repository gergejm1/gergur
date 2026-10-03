namespace Gergur.Diagnostics;

/// <summary>
/// Decides, from how long a ping to the window's thread has gone unanswered, whether the
/// window has stopped responding, and says so once per stall rather than at every check.
/// </summary>
public sealed class StallDetector
{
    /// <summary>Longer than anything the window does on purpose, shorter than a person waits.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(6);

    public enum Verdict { Nothing, Stalled, Recovered }

    private readonly TimeSpan _threshold;
    private bool _reported;

    public StallDetector(TimeSpan? threshold = null) => _threshold = threshold ?? DefaultThreshold;

    public Verdict Observe(bool pingPending, TimeSpan waiting)
    {
        if (pingPending && waiting >= _threshold && !_reported)
        {
            _reported = true;
            return Verdict.Stalled;
        }
        if (!pingPending && _reported)
        {
            _reported = false;
            return Verdict.Recovered;
        }
        return Verdict.Nothing;
    }

    /// <summary>Forgets a stall in progress, for when the clock jumped and it can no longer be measured.</summary>
    public void Reset() => _reported = false;
}

/// <summary>
/// Pings the window's thread from another one, and writes the log when it does not answer.
/// Two stalls reported by a person (the Google account chooser, and switching to a sleeping
/// tab) ended with the browser killed by hand and nothing recorded: the engine's calls into
/// its browser process are synchronous, so one that never returns freezes the window.
/// This cannot unfreeze it. It records what the browser had last been doing.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private static readonly object Gate = new();
    private static readonly List<Control> Windows = [];
    private static UiWatchdog? _running;

    private readonly Func<Control?> _pickWindow;
    private readonly System.Threading.Timer _timer;
    private readonly StallDetector _detector;
    private readonly object _sync = new();
    private bool _pingPending;
    private DateTime _pingSentUtc;
    private DateTime _stalledSinceUtc;
    private DateTime _lastCheckUtc = DateTime.UtcNow;

    /// <summary>A gap between checks this long means the whole machine was asleep, not the window.</summary>
    internal static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(30);

    /// <summary>Whether this long since the last check can only be the machine having slept.</summary>
    internal static bool WasAsleep(TimeSpan sinceLastCheck) => sinceLastCheck > SleepGap;

    /// <param name="pickWindow">The window to ping now, or null when none is left.</param>
    /// <param name="period">How often the window is pinged.</param>
    /// <param name="threshold">How long a ping may go unanswered before it is a stall.</param>
    internal UiWatchdog(Func<Control?> pickWindow, TimeSpan period, TimeSpan threshold)
    {
        _pickWindow = pickWindow;
        _detector = new StallDetector(threshold);
        _timer = new System.Threading.Timer(_ => Check(), null, period, period);
    }

    /// <summary>
    /// Watches this window, for the whole app. The first call starts the watching; every window
    /// is a candidate to ping, so closing the first does not end it while others stay open,
    /// and none is kept alive here once it is closed.
    /// </summary>
    public static void Start(Control window)
    {
        lock (Gate)
        {
            Windows.Add(window);
            window.Disposed += (_, _) =>
            {
                lock (Gate)
                    Windows.Remove(window);
            };
            _running ??= new UiWatchdog(PickLiveWindow, TimeSpan.FromSeconds(2), StallDetector.DefaultThreshold);
        }
    }

    private static Control? PickLiveWindow()
    {
        lock (Gate)
            return Windows.FirstOrDefault(w => !w.IsDisposed && w.IsHandleCreated);
    }

    private void Check()
    {
        try
        {
            var window = _pickWindow();
            if (window is null)
                return;
            bool post;
            StallDetector.Verdict verdict;
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                // Asleep with the lid shut, the ping sent before it looks hours old on waking
                // and is answered a moment later. Not a stall, and not one to report the end
                // of: start again.
                if (WasAsleep(now - _lastCheckUtc))
                {
                    _pingPending = false;
                    _detector.Reset();
                }
                _lastCheckUtc = now;
                verdict = _detector.Observe(_pingPending, now - _pingSentUtc);
                if (verdict == StallDetector.Verdict.Stalled)
                    _stalledSinceUtc = _pingSentUtc;
                post = !_pingPending;
                if (post)
                {
                    _pingPending = true;
                    _pingSentUtc = now;
                }
            }
            if (verdict == StallDetector.Verdict.Stalled)
                DebugLog.WriteAlways(Describe((int)(now - _stalledSinceUtc).TotalSeconds));
            if (post)
                window.BeginInvoke(new Action(Answered));
        }
        catch
        {
            // A window going away under the check. Nothing to report.
        }
    }

    private void Answered()
    {
        DateTime now = DateTime.UtcNow;
        bool recovered;
        lock (_sync)
        {
            _pingPending = false;
            // Seen here rather than at the next check, which is up to two seconds later.
            recovered = _detector.Observe(false, TimeSpan.Zero) == StallDetector.Verdict.Recovered;
        }
        if (recovered)
            DebugLog.WriteAlways($"the window responded again after {(int)(now - _stalledSinceUtc).TotalSeconds}s");
    }

    /// <summary>The log line for a stall: how long, and what was last done to which tab.</summary>
    internal static string Describe(int seconds)
        => $"the window has not responded for {seconds}s. Last things done: "
            + string.Join(" > ", Breadcrumbs.Recent(12).DefaultIfEmpty("(nothing recorded)"));

    public void Dispose() => _timer.Dispose();
}
