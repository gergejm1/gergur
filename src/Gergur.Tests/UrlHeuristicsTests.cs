using Gergur.App;
using Xunit;

namespace Gergur.Tests;

public sealed class UrlHeuristicsTests
{
    private const string Search = "https://duckduckgo.com/?q={0}";

    [Theory]
    [InlineData("https://example.com/path?a=1")]
    [InlineData("http://example.com")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/temp/page.html")]
    public void FullUrlsPassThroughUntouched(string input)
        => Assert.Equal(input, UrlHeuristics.ToNavigableUrl(input, Search));

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("example.com/deep/path", "https://example.com/deep/path")]
    [InlineData("localhost:3000", "https://localhost:3000")]
    [InlineData("192.168.1.1", "https://192.168.1.1")]
    public void BareHostsGetHttps(string input, string expected)
        => Assert.Equal(expected, UrlHeuristics.ToNavigableUrl(input, Search));

    [Theory]
    [InlineData("what is rust")]
    [InlineData("3.14 pie recipe")] // has a dot but also a space → query
    [InlineData("singleword")]
    public void QueriesGoToSearch(string input)
    {
        var result = UrlHeuristics.ToNavigableUrl(input, Search);
        Assert.StartsWith("https://duckduckgo.com/?q=", result);
        Assert.DoesNotContain(" ", result);
    }
}
