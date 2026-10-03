namespace Gergur.Diagnostics;

/// <summary>
/// The last few things the browser did to a tab, kept in memory and written out only when
/// the window stops responding (<see cref="UiWatchdog"/>). A freeze leaves nothing behind
/// by itself: the process has to be killed, and the log said nothing of what it was doing.
///
/// What was done and to which tab, never an address: that is the person's browsing.
/// </summary>
public static class Breadcrumbs
{
    internal const int Capacity = 40;

    private static readonly string[] Entries = new string[Capacity];
    private static int _next;
    private static int _count;
    private static readonly object Gate = new();

    public static void Note(string what)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {what}";
        lock (Gate)
        {
            Entries[_next] = line;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }
    }

    /// <summary>The most recent entries, oldest first.</summary>
    public static IReadOnlyList<string> Recent(int max)
    {
        lock (Gate)
        {
            int take = Math.Min(Math.Max(max, 0), _count);
            var list = new List<string>(take);
            for (int i = take; i > 0; i--)
                list.Add(Entries[(_next - i + Capacity) % Capacity]);
            return list;
        }
    }

    /// <summary>Forgets everything. For tests, which share the one list.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Array.Clear(Entries);
            _next = _count = 0;
        }
    }
}
