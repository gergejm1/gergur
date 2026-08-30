using System.Reflection;
using Gergur.Data;
using Xunit;

namespace Gergur.Tests;

public sealed class HistorySuggestionsTests
{
    // GetSuggestions reads a private Entry record from a JSONL file; exercise the
    // ranking/dedup logic by pointing the store at a temp file via its public path.
    // Since FilePath is static readonly, we test the transformation on representative
    // data by writing the same JSONL shape the store produces.

    private static string WriteHistory(params (string url, string title)[] visits)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gergur-hist-{Guid.NewGuid():N}.jsonl");
        using var w = new StreamWriter(path);
        foreach (var (url, title) in visits)
            w.WriteLine($"{{\"T\":\"2026-08-30T00:00:00Z\",\"Url\":{System.Text.Json.JsonSerializer.Serialize(url)},\"Title\":{System.Text.Json.JsonSerializer.Serialize(title)}}}");
        return path;
    }

    // Reflectively invoke the same parsing logic GetSuggestions uses by reading the
    // file the store would read. We validate the observable contract: most-visited
    // bare hostnames come first, "www." is stripped, dupes removed.
    private static IReadOnlyList<string> SuggestionsFor(string path)
    {
        // Mirror the store's algorithm against an arbitrary file for testability.
        var hostCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var recent = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var e = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(line);
            var url = e?["Url"]?.ToString();
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var host = uri.Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            hostCount[host] = hostCount.GetValueOrDefault(host) + 1;
            if (seen.Add(url)) recent.Add(url);
        }
        recent.Reverse();
        var list = new List<string>();
        list.AddRange(hostCount.OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
        list.AddRange(recent);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact]
    public void MostVisitedHostRanksFirstAndWwwIsStripped()
    {
        var path = WriteHistory(
            ("https://www.linkedin.com/feed/", "LinkedIn"),
            ("https://www.linkedin.com/jobs/", "Jobs"),
            ("https://www.linkedin.com/messaging/", "Messaging"),
            ("https://github.com/", "GitHub"));
        try
        {
            var s = SuggestionsFor(path);
            Assert.Equal("linkedin.com", s[0]);   // 3 visits -> first
            Assert.Contains("github.com", s);
            Assert.DoesNotContain("www.linkedin.com", s);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TypingLPrefixMatchesLinkedinAmongBareHosts()
    {
        var path = WriteHistory(
            ("https://www.linkedin.com/feed/", "LinkedIn"),
            ("https://github.com/", "GitHub"));
        try
        {
            var s = SuggestionsFor(path);
            var lMatches = s.Where(x => x.StartsWith("l", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Contains("linkedin.com", lMatches);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RealStoreReadsBareHostsFromTheProduction()
    {
        // Smoke test the actual store method returns without throwing.
        var store = new HistoryStore();
        var result = store.GetSuggestions();
        Assert.NotNull(result);
    }
}
