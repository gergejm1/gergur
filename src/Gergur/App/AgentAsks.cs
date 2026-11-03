namespace Gergur.App;

/// <summary>A question an agent put to the person, as the bar across their window shows it.</summary>
public sealed record AgentQuestion(string Id, string AgentKey, string AgentName, string Question, DateTime AskedUtc);

/// <summary>The person's answer: Done (they did it, or answered) or Can't, with anything they typed.</summary>
public sealed record AgentReply(bool Done, string Text);

/// <summary>
/// Questions agents are waiting on the person for: a code from their phone, a captcha, a
/// choice only they can make. Asked through /ask, shown in a bar across the person's
/// windows, answered there. An agent waits a while for the answer and, when it is not
/// there yet, asks again with the question's id to keep waiting, so no call is held open
/// for longer than a tool call may last.
///
/// Thread-safe: asked and waited on from the agent server's threads, answered on the UI thread.
/// </summary>
public sealed class AgentAsks
{
    internal const int MaxPending = 10;
    internal const int MaxQuestionLength = 300;
    internal const int MaxReplyLength = 2000;

    /// <summary>How long a question, or an answer not yet collected, is kept.</summary>
    internal static readonly TimeSpan KeptFor = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        public required AgentQuestion Question;
        public readonly TaskCompletionSource<AgentReply> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime? AnsweredUtc;
    }

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private int _next;

    /// <summary>A question was asked or answered. Raised outside the lock, on whichever thread did it.</summary>
    public event Action? Changed;

    /// <summary>
    /// A question as the bar shows it: trimmed, its lines and runs of spaces joined, and cut
    /// to <see cref="MaxQuestionLength"/>. Null when nothing is left.
    /// </summary>
    internal static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string joined = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // Control and format characters out: a right-to-left override would show the
        // question reversed, or make it read as something it does not say.
        joined = new string(joined.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
        if (joined.Length == 0)
            return null;
        if (joined.Length <= MaxQuestionLength)
            return joined;
        int cut = MaxQuestionLength - 1;
        if (char.IsHighSurrogate(joined[cut - 1]))
            cut--;   // never half a character
        return joined[..cut] + "…";
    }

    /// <summary>
    /// Puts a question to the person. Asking the same thing again while it waits gives the
    /// same question back, so an agent that lost the id cannot stack copies in the bar.
    /// Null when <see cref="MaxPending"/> are already waiting.
    /// </summary>
    public AgentQuestion? Ask(AgentCaller caller, string question, DateTime nowUtc)
    {
        AgentQuestion asked;
        lock (_gate)
        {
            Forget(nowUtc);
            var waiting = _entries.Where(e => e.AnsweredUtc is null).ToList();
            if (waiting.FirstOrDefault(e => e.Question.AgentKey == caller.Key && e.Question.Question == question) is { } same)
                return same.Question;
            if (waiting.Count >= MaxPending)
                return null;
            asked = new AgentQuestion($"ask-{++_next}", caller.Key, caller.Name, question, nowUtc);
            _entries.Add(new Entry { Question = asked });
        }
        Changed?.Invoke();
        return asked;
    }

    /// <summary>The questions still waiting, the oldest first.</summary>
    public IReadOnlyList<AgentQuestion> Pending(DateTime nowUtc)
    {
        lock (_gate)
        {
            Forget(nowUtc);
            return _entries.Where(e => e.AnsweredUtc is null).Select(e => e.Question).ToArray();
        }
    }

    /// <summary>The person answered. False when the question is no longer waiting.</summary>
    public bool Answer(string id, bool done, string? text, DateTime nowUtc)
    {
        string reply = (text ?? "").Trim();
        if (reply.Length > MaxReplyLength)
            reply = reply[..MaxReplyLength];
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Question.Id == id && e.AnsweredUtc is null) is not { } entry)
                return false;
            entry.AnsweredUtc = nowUtc;
            entry.Answer.TrySetResult(new AgentReply(done, reply));
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the answer to one of this agent's questions.
    /// Known is false for an id that is not this agent's or has been forgotten; Reply is null
    /// while the person has not answered. An answer collected is forgotten.
    /// </summary>
    public async Task<(bool Known, AgentReply? Reply)> WaitAsync(string id, string agentKey, TimeSpan wait)
    {
        Task<AgentReply> answer;
        lock (_gate)
        {
            Forget(DateTime.UtcNow);
            if (_entries.FirstOrDefault(e => e.Question.Id == id && e.Question.AgentKey == agentKey) is not { } entry)
                return (false, null);
            answer = entry.Answer.Task;
        }
        if (!answer.IsCompleted && wait > TimeSpan.Zero)
        {
            using var giveUp = new CancellationTokenSource();
            await Task.WhenAny(answer, Task.Delay(wait, giveUp.Token));
            giveUp.Cancel();
        }
        if (!answer.IsCompleted)
            return (true, null);
        lock (_gate)
            _entries.RemoveAll(e => e.Question.Id == id);
        return (true, answer.Result);
    }

    /// <summary>Drops what is older than <see cref="KeptFor"/>. Under the lock.</summary>
    private void Forget(DateTime nowUtc)
        => _entries.RemoveAll(e => nowUtc - (e.AnsweredUtc ?? e.Question.AskedUtc) > KeptFor);
}
