using Gergur.Data;
using Xunit;

namespace Gergur.Tests;

/// <summary>Read/search/forget paths of the history log, against a temp file.</summary>
public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gergur-hist-{Guid.NewGuid():N}.jsonl");
    private readonly HistoryStore _store;

    public HistoryStoreTests()
    {
        _store = new HistoryStore(_path);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void ReadReturnsNewestFirst()
    {
        _store.Append("https://one.example/", "One");
        _store.Append("https://two.example/", "Two");
        _store.Append("https://three.example/", "Three");

        var visits = _store.Read();

        Assert.Equal(3, visits.Count);
        Assert.Equal("https://three.example/", visits[0].Url);
        Assert.Equal("https://one.example/", visits[2].Url);
    }

    [Fact]
    public void ConsecutiveVisitsToTheSameUrlCollapse()
    {
        _store.Append("https://a.example/", "A");
        _store.Append("https://a.example/", "A"); // reload
        _store.Append("https://b.example/", "B");
        _store.Append("https://a.example/", "A"); // back again later: a separate row

        var visits = _store.Read();

        Assert.Equal(3, visits.Count);
        Assert.Equal(["https://a.example/", "https://b.example/", "https://a.example/"],
            visits.Select(v => v.Url));
    }

    [Fact]
    public void SearchMatchesTitleAndUrlCaseInsensitively()
    {
        _store.Append("https://github.com/anthropics", "Anthropic on GitHub");
        _store.Append("https://news.ycombinator.com/", "Hacker News");

        Assert.Single(_store.Read("GITHUB"));
        Assert.Single(_store.Read("hacker"));
        Assert.Empty(_store.Read("nothing here"));
    }

    [Fact]
    public void MaxCapsTheNumberOfRowsReturned()
    {
        for (int i = 0; i < 10; i++)
            _store.Append($"https://example.com/{i}", $"Page {i}");

        var visits = _store.Read(max: 4);

        Assert.Equal(4, visits.Count);
        Assert.Equal("https://example.com/9", visits[0].Url); // newest kept
    }

    [Fact]
    public void RemoveDropsEveryVisitToTheGivenUrls()
    {
        _store.Append("https://keep.example/", "Keep");
        _store.Append("https://drop.example/", "Drop");
        _store.Append("https://keep.example/", "Keep");
        _store.Append("https://drop.example/", "Drop again");

        int removed = _store.Remove(["https://drop.example/"]);

        Assert.Equal(2, removed);
        Assert.All(_store.Read(), v => Assert.Equal("https://keep.example/", v.Url));
    }

    [Fact]
    public void ClearEmptiesTheLog()
    {
        _store.Append("https://example.com/", "Example");
        _store.Clear();

        Assert.Empty(_store.Read());
        Assert.Empty(_store.GetSuggestions());
    }

    [Fact]
    public void HomePageVisitsAreNeverLogged()
    {
        _store.Append("about:blank", "New tab");
        _store.Append("file:///C:/app/Assets/home.html", "New tab");

        Assert.Empty(_store.Read());
    }

    [Fact]
    public void MalformedLinesAreSkippedRatherThanThrowing()
    {
        _store.Append("https://good.example/", "Good");
        File.AppendAllText(_path, "not json at all" + Environment.NewLine);
        _store.Append("https://also-good.example/", "Also good");

        var visits = _store.Read();

        Assert.Equal(2, visits.Count);
    }

    [Fact]
    public void SuggestionsRankMostVisitedHostFirstAndStripWww()
    {
        _store.Append("https://www.linkedin.com/feed/", "LinkedIn");
        _store.Append("https://www.linkedin.com/jobs/", "Jobs");
        _store.Append("https://www.linkedin.com/messaging/", "Messaging");
        _store.Append("https://github.com/", "GitHub");

        var suggestions = _store.GetSuggestions();

        Assert.Equal("linkedin.com", suggestions[0]);
        Assert.Contains("github.com", suggestions);
        Assert.DoesNotContain("www.linkedin.com", suggestions);
    }
}
