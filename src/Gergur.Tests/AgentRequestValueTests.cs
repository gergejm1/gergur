using System.Globalization;
using System.Text.Json;
using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Every new option on the agent API arrives as a flag or a number in a query string or
/// a json body, and the endpoints branch on them: whether a screenshot photographs the
/// window or the page, whether opening a tab takes the screen away from whoever is using
/// the browser, how long an evaluation is given. Reading one of those wrongly is not a
/// parse error anybody sees, it is the request quietly doing the other thing.
/// </summary>
public sealed class AgentRequestValueTests
{
    private static readonly Dictionary<string, string> NoQuery = new();

    private static Dictionary<string, string> Query(string name, string value)
        => new() { [name] = value };

    // Kept for the life of the class rather than leaked per call: JsonDocument is
    // poolable and these are read from after Body() returns, so a using at the call site
    // would hand back a disposed element.
    private static readonly List<JsonDocument> Parsed = [];

    private static JsonDocument Body(string json)
    {
        var document = JsonDocument.Parse(json);
        lock (Parsed)
            Parsed.Add(document);
        return document;
    }

    [Fact]
    public void AMissingFlagIsNeitherTrueNorFalse()
    {
        // The distinction the endpoints rest on: "background" left out means the caller
        // had no opinion and the endpoint's own default applies, and that default is not
        // the same for every endpoint.
        Assert.Null(AgentServer.BoolValue(NoQuery, null, "background"));
        Assert.Null(AgentServer.BoolValue(NoQuery, Body("{}"), "background"));
    }

    [Theory]
    [InlineData("", true)]        // a bare ?chrome, which is what a flag in a url looks like
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void AFlagInTheQueryReadsTheWayAUrlWritesOne(string value, bool expected)
        => Assert.Equal(expected, AgentServer.BoolValue(Query("chrome", value), null, "chrome"));

    [Fact]
    public void AFlagNobodyCanReadIsNotQuietlyFalse()
    {
        // "?await=yes" is a caller who meant true. Reading it as false would run the
        // opposite of the request; null hands it to the endpoint's default instead.
        Assert.Null(AgentServer.BoolValue(Query("await", "yes"), null, "await"));
    }

    [Theory]
    [InlineData("""{ "background": true }""", true)]
    [InlineData("""{ "background": false }""", false)]
    [InlineData("""{ "background": "true" }""", true)]
    public void AFlagInTheBodyIsReadAsJsonOrAsTheStringModelsSend(string json, bool expected)
        => Assert.Equal(expected, AgentServer.BoolValue(NoQuery, Body(json), "background"));

    [Fact]
    public void TheQueryWinsOverTheBody()
    {
        // Putting it in the url is aiming it at this request specifically.
        Assert.True(AgentServer.BoolValue(Query("chrome", "1"), Body("""{ "chrome": false }"""), "chrome"));
        Assert.Equal("t7", AgentServer.StringValue(Query("id", "t7"), Body("""{ "id": "t2" }"""), "id"));
    }

    [Fact]
    public void AnIdIsTakenFromEitherSide()
    {
        Assert.Equal("t3", AgentServer.StringValue(Query("id", "t3"), null, "id"));
        Assert.Equal("t3", AgentServer.StringValue(NoQuery, Body("""{ "id": "t3" }"""), "id"));
        Assert.Null(AgentServer.StringValue(NoQuery, Body("""{ "id": 3 }"""), "id"));
        Assert.Null(AgentServer.StringValue(NoQuery, null, "id"));
    }

    [Fact]
    public void AnEmptyIdIsSuppliedRatherThanAbsent()
    {
        // The endpoints treat a supplied id that matches nothing as an error rather than
        // falling back to the tab the user is looking at, so "" has to be distinguishable
        // from leaving it out.
        Assert.Equal("", AgentServer.StringValue(Query("id", ""), null, "id"));
    }

    [Theory]
    [InlineData("30", 30d)]
    [InlineData("2.5", 2.5d)]
    public void ATimeoutIsReadFromTheQuery(string value, double expected)
        => Assert.Equal(expected, AgentServer.NumberValue(Query("timeout", value), null, "timeout"));

    [Fact]
    public void ATimeoutNobodyCanReadFallsBackRatherThanToZero()
    {
        // Zero would be clamped to the shortest allowed wait and every call would report
        // a page that never settled.
        Assert.Null(AgentServer.NumberValue(Query("timeout", "soon"), null, "timeout"));
        Assert.Null(AgentServer.NumberValue(NoQuery, Body("""{ "timeout": "soon" }"""), "timeout"));
        Assert.Null(AgentServer.NumberValue(NoQuery, null, "timeout"));
    }

    [Fact]
    public void ATimeoutIsReadTheSameWayWhereverThisRuns()
    {
        // Under a culture whose decimal separator is a comma, a plain double.TryParse
        // reads "2.5" as 25, so a two and a half second wait silently becomes twenty
        // five. Asserting this on an en-US machine would pass either way and prove
        // nothing, so the culture is swapped for the length of the call.
        var restore = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(2.5d, AgentServer.NumberValue(Query("timeout", "2.5"), null, "timeout"));
        }
        finally
        {
            CultureInfo.CurrentCulture = restore;
        }
    }

    [Fact]
    public void ANumberInTheBodyIsReadAsANumber()
    {
        Assert.Equal(45d, AgentServer.NumberValue(NoQuery, Body("""{ "timeout": 45 }"""), "timeout"));
    }
}
