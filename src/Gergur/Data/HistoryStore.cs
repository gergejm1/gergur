using System.Text.Json;
using Gergur.App;

namespace Gergur.Data;

/// <summary>One visited page, newest-first when handed out by <see cref="HistoryStore.Read"/>.</summary>
public sealed record HistoryVisit(DateTime VisitedUtc, string Url, string Title);

/// <summary>Append-only JSONL log - greppable, no database. The history window
/// reads it back; the address bar ranks it into autocomplete suggestions.</summary>
public sealed class HistoryStore
{
    public static readonly string DefaultPath = Path.Combine(Settings.DataDir, "history.jsonl");

    private sealed record Entry(DateTime T, string Url, string Title);

    private readonly string _path;

    /// <param name="path">Override the log location; defaults to the profile's history.jsonl.</param>
    public HistoryStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public string FilePath => _path;

    public void Append(string url, string title)
    {
        if (HomePage.IsHome(url))
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(new Entry(DateTime.UtcNow, url, title)) + Environment.NewLine);
        }
        catch
        {
            // History is best-effort; never block browsing over it.
        }
    }

    /// <summary>
    /// Newest-first visits, with runs of the same URL collapsed to their most recent
    /// visit so a reload or an in-page navigation loop does not flood the list.
    /// </summary>
    public IReadOnlyList<HistoryVisit> Read(string? search = null, int max = 5000)
    {
        var visits = new List<HistoryVisit>();
        try
        {
            foreach (var entry in ReadEntries())
            {
                if (!Matches(entry, search))
                    continue;
                visits.Add(new HistoryVisit(entry.T, entry.Url, entry.Title));
            }
        }
        catch
        {
            return Array.Empty<HistoryVisit>();
        }

        visits.Reverse(); // the log is append-order; the window wants newest first
        var result = new List<HistoryVisit>(Math.Min(visits.Count, max));
        string? lastUrl = null;
        foreach (var visit in visits)
        {
            if (string.Equals(visit.Url, lastUrl, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(visit);
            lastUrl = visit.Url;
            if (result.Count >= max)
                break;
        }
        return result;
    }

    /// <summary>Drops every visit to any of these URLs. Returns the number of lines removed.</summary>
    public int Remove(IEnumerable<string> urls)
    {
        var drop = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);
        if (drop.Count == 0 || !File.Exists(_path))
            return 0;
        try
        {
            var kept = new List<string>();
            int removed = 0;
            foreach (var line in File.ReadLines(_path))
            {
                var entry = TryParse(line);
                if (entry is not null && drop.Contains(entry.Url))
                    removed++;
                else
                    kept.Add(line);
            }
            if (removed > 0)
                File.WriteAllLines(_path, kept);
            return removed;
        }
        catch
        {
            return 0;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            // Nothing useful to do; the window reports what it can still read.
        }
    }

    /// <summary>
    /// Address-bar autocomplete entries: bare hostnames first (most-visited first, so
    /// typing "l" surfaces linkedin.com), then recently-visited full URLs. Prefix match
    /// against the full URL never fires on a lone letter, so the bare hosts are what
    /// makes single-letter suggestions work.
    /// </summary>
    public IReadOnlyList<string> GetSuggestions(int max = 400)
    {
        try
        {
            var hostCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var recentUrls = new List<string>();
            var urlSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in ReadEntries())
            {
                if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
                    continue;

                var host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    host = host[4..];
                if (host.Length > 0)
                    hostCount[host] = hostCount.GetValueOrDefault(host) + 1;
                if (urlSeen.Add(entry.Url))
                    recentUrls.Add(entry.Url);
            }

            recentUrls.Reverse(); // most recent first
            var suggestions = new List<string>();
            suggestions.AddRange(hostCount.OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
            suggestions.AddRange(recentUrls);
            return suggestions.Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private IEnumerable<Entry> ReadEntries()
    {
        if (!File.Exists(_path))
            yield break;
        foreach (var line in File.ReadLines(_path))
        {
            var entry = TryParse(line);
            if (entry is not null && !string.IsNullOrEmpty(entry.Url))
                yield return entry;
        }
    }

    private static Entry? TryParse(string line)
    {
        try { return JsonSerializer.Deserialize<Entry>(line); }
        catch { return null; }
    }

    private static bool Matches(Entry entry, string? search)
        => string.IsNullOrWhiteSpace(search)
        || entry.Url.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Title.Contains(search, StringComparison.OrdinalIgnoreCase);
}
