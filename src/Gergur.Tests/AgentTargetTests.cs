using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Which tab a request acts on. This is the sharp edge of the whole agent API: the one
/// bug this project has already shipped here closed the tab the user was reading, because
/// an index that could not be used fell through to "the active tab" instead of failing.
///
/// Tab ids are the new way to name a tab and are meant to be the safe one, so the same
/// guarantee has to hold for them, and has to be pinned rather than assumed.
/// </summary>
public sealed class AgentTargetTests
{
    private static readonly string[] Tabs = ["t1", "t7", "t9"];
    private const int Active = 1;   // t7, the one the user is looking at

    private static int Target(string? id = null, bool suppliedIndex = false, int? index = null)
        => AgentServer.TargetIndex(Tabs, id, (suppliedIndex, index), Active);

    [Fact]
    public void NothingSuppliedMeansTheActiveTab()
        => Assert.Equal(Active, Target());

    [Fact]
    public void AnIdNamesItsTab()
    {
        Assert.Equal(0, Target(id: "t1"));
        Assert.Equal(2, Target(id: "t9"));
    }

    [Fact]
    public void AnIdThatNamesNothingIsNotTheActiveTab()
    {
        // The whole reason this function exists. An agent holding an id for a tab that
        // has since been closed must be told so, not handed the page the user moved on
        // to and left to navigate it away or close it.
        Assert.Equal(-1, Target(id: "t99"));
    }

    [Fact]
    public void AnEmptyIdIsAnErrorRatherThanAnInvitation()
        => Assert.Equal(-1, Target(id: ""));

    [Fact]
    public void AnIdIsMatchedWithoutRegardToCase()
        => Assert.Equal(2, Target(id: "T9"));

    [Fact]
    public void AnIdWinsOverAnIndex()
    {
        // They should not disagree, but when they do the id is the one that survived a
        // tab opening or being torn off, and the index is the one that went stale.
        Assert.Equal(0, Target(id: "t1", suppliedIndex: true, index: 2));
    }

    [Fact]
    public void AnIdThatNamesNothingStillLosesToNobody()
    {
        // Specifically: a bad id does not quietly fall back to the index beside it.
        Assert.Equal(-1, Target(id: "t99", suppliedIndex: true, index: 2));
    }

    [Fact]
    public void AnIndexNamesItsPosition()
        => Assert.Equal(2, Target(suppliedIndex: true, index: 2));

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    public void AnIndexOutsideTheListIsNotTheActiveTab(int index)
        => Assert.Equal(-1, Target(suppliedIndex: true, index: index));

    [Fact]
    public void AnIndexThatCouldNotBeReadIsNotTheActiveTab()
    {
        // "index": "third". Supplied, unusable, and never the user's tab.
        Assert.Equal(-1, Target(suppliedIndex: true, index: null));
    }

    [Fact]
    public void WithNoTabsAtAllNothingIsTargeted()
        => Assert.Equal(-1, AgentServer.TargetIndex([], null, (false, null), -1));
}

/// <summary>
/// The query string, parsed. Every new option on this API is a flag or a number read out
/// of here, and a flag the parser drops is not an error anybody sees: the endpoint just
/// quietly does the other thing.
/// </summary>
public sealed class AgentQueryTests
{
    private static Dictionary<string, string> Parse(string rawPath)
        => AgentServer.ParseQuery(rawPath, out _);

    private static string PathOf(string rawPath)
    {
        AgentServer.ParseQuery(rawPath, out string path);
        return path;
    }

    [Fact]
    public void APathWithNoQueryIsItself()
    {
        Assert.Equal("/tabs", PathOf("/tabs"));
        Assert.Empty(Parse("/tabs"));
    }

    [Fact]
    public void ThePathStopsAtTheQuestionMark()
        => Assert.Equal("/screenshot", PathOf("/screenshot?id=t3&chrome=1"));

    [Fact]
    public void PairsAreRead()
    {
        var query = Parse("/screenshot?id=t3&chrome=1");
        Assert.Equal("t3", query["id"]);
        Assert.Equal("1", query["chrome"]);
    }

    [Fact]
    public void ABareFlagIsPresentRatherThanDropped()
    {
        // "?chrome" is how a url writes a flag, and BoolValue reads an empty value as
        // true. The parser used to drop the key entirely, so ?chrome silently captured
        // the page instead of the window, and the test that covered it passed because it
        // built its dictionary by hand and never came through here.
        var query = Parse("/screenshot?chrome");
        Assert.True(query.ContainsKey("chrome"));
        Assert.True(AgentServer.BoolValue(query, null, "chrome"));
    }

    [Fact]
    public void ABareFlagAmongOthersIsStillPresent()
    {
        var query = Parse("/screenshot?id=t3&chrome&activate=0");
        Assert.True(AgentServer.BoolValue(query, null, "chrome"));
        Assert.False(AgentServer.BoolValue(query, null, "activate"));
        Assert.Equal("t3", query["id"]);
    }

    [Fact]
    public void AnEscapedValueIsUnescaped()
        => Assert.Equal("a b", Parse("/page?title=a%20b")["title"]);

    [Fact]
    public void APairWithNoNameIsDropped()
    {
        // "=value" names nothing, so nothing can ask for it.
        Assert.Empty(Parse("/tabs?=orphan"));
    }

    [Fact]
    public void AnEmptyValueIsAnEmptyString()
        => Assert.Equal("", Parse("/tabs?index=")["index"]);
}
