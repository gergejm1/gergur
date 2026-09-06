using Gergur.Data;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The session file gained a Windows list when tabs became tearable. Sessions written
/// by the single-window build must still restore, or an upgrade silently loses tabs.
/// </summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gergur-session-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void LegacySingleWindowFileRestoresAsOneWindow()
    {
        File.WriteAllText(_path, """
            {
              "Tabs": [
                { "Url": "https://a.example/", "Title": "A" },
                { "Url": "https://b.example/", "Title": "B" },
                { "Url": "https://c.example/", "Title": "C" }
              ],
              "ActiveIndex": 2
            }
            """);

        var windows = SessionStore.LoadFrom(_path);

        Assert.Single(windows);
        Assert.Equal(3, windows[0].Tabs.Count);
        Assert.Equal(2, windows[0].ActiveIndex);
        Assert.Equal("https://a.example/", windows[0].Tabs[0].Url);
    }

    [Fact]
    public void MultiWindowFileRestoresEveryWindowInOrder()
    {
        File.WriteAllText(_path, """
            {
              "Windows": [
                { "Tabs": [ { "Url": "https://one.example/", "Title": "One" } ], "ActiveIndex": 0 },
                { "Tabs": [ { "Url": "https://two.example/", "Title": "Two" },
                            { "Url": "https://three.example/", "Title": "Three" } ], "ActiveIndex": 1 }
              ]
            }
            """);

        var windows = SessionStore.LoadFrom(_path);

        Assert.Equal(2, windows.Count);
        Assert.Single(windows[0].Tabs);
        Assert.Equal(2, windows[1].Tabs.Count);
        Assert.Equal(1, windows[1].ActiveIndex);
    }

    [Fact]
    public void MissingFileRestoresNothing()
        => Assert.Empty(SessionStore.LoadFrom(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json")));

    [Fact]
    public void CorruptFileRestoresNothingRatherThanThrowing()
    {
        File.WriteAllText(_path, "{ this is not json");
        Assert.Empty(SessionStore.LoadFrom(_path));
    }

    [Fact]
    public void EmptyWindowsListFallsThroughRatherThanRestoringAnEmptyWindow()
    {
        File.WriteAllText(_path, """{ "Windows": [] }""");
        Assert.Empty(SessionStore.LoadFrom(_path));
    }
}
