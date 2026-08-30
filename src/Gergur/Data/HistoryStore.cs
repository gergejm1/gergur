using System.Text.Json;
using Gergur.App;

namespace Gergur.Data;

/// <summary>Append-only JSONL log - greppable, no database, no UI needed for v1.</summary>
public sealed class HistoryStore
{
    public static readonly string FilePath = Path.Combine(Settings.DataDir, "history.jsonl");

    private sealed record Entry(DateTime T, string Url, string Title);

    public void Append(string url, string title)
    {
        if (HomePage.IsHome(url))
            return;
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(new Entry(DateTime.UtcNow, url, title)) + Environment.NewLine);
        }
        catch
        {
            // History is best-effort; never block browsing over it.
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
            if (!File.Exists(FilePath))
                return Array.Empty<string>();

            var hostCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var recentUrls = new List<string>();
            var urlSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in File.ReadLines(FilePath))
            {
                Entry? entry = null;
                try { entry = JsonSerializer.Deserialize<Entry>(line); } catch { }
                if (entry is null || string.IsNullOrEmpty(entry.Url)
                    || !Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
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
}
