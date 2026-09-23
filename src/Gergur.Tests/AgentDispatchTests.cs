using System.Text.Json;
using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Where a tool call is aimed, and whether it is complete enough to run at all.
/// Both of these were sent back by review: an index that could not be parsed used to
/// collapse into "the active tab", and a required index that was simply absent still
/// did, which on gergur_close_tab closed the page the user was reading.
/// </summary>
public sealed class AgentDispatchTests
{
    private static readonly Dictionary<string, string> NoQuery = new();

    private static (bool Supplied, int? Index) Resolve(string? bodyJson, Dictionary<string, string>? query = null)
    {
        using var body = bodyJson is null ? null : JsonDocument.Parse(bodyJson);
        return AgentServer.ResolveIndex(query ?? NoQuery, body);
    }

    // ---------------------------------------------------------------- index resolution

    [Fact]
    public void NoIndexAtAllMeansTheActiveTab()
        => Assert.Equal((false, (int?)null), Resolve("""{"url":"https://example.com"}"""));

    [Theory]
    [InlineData("""{"index":3}""", 3)]
    [InlineData("""{"index":0}""", 0)]
    [InlineData("""{"index":"3"}""", 3)]   // models send it as a string constantly
    [InlineData("""{"index":"-1"}""", -1)]
    public void AnIndexIsAcceptedAsANumberOrAString(string body, int expected)
        => Assert.Equal((true, (int?)expected), Resolve(body));

    [Theory]
    [InlineData("""{"index":"abc"}""")]
    [InlineData("""{"index":true}""")]
    [InlineData("""{"index":[1]}""")]
    [InlineData("""{"index":{}}""")]
    public void AnUnusableIndexIsSuppliedButUnresolved(string body)
    {
        // Supplied stays true so the caller errors instead of retargeting the user's tab.
        var (supplied, index) = Resolve(body);
        Assert.True(supplied);
        Assert.Null(index);
    }

    [Fact]
    public void AnExplicitNullIndexCountsAsOmitted()
    {
        // Models routinely send null for an argument they are choosing not to use.
        Assert.Equal((false, (int?)null), Resolve("""{"index":null}"""));
        Assert.Equal((false, (int?)null), Resolve(null, new Dictionary<string, string> { ["index"] = "null" }));
    }

    [Theory]
    [InlineData("2", true, 2)]
    [InlineData("abc", true, null)]
    [InlineData("", false, null)]
    public void TheQueryStringFollowsTheSameRules(string raw, bool supplied, int? index)
        => Assert.Equal((supplied, index), Resolve(null, new Dictionary<string, string> { ["index"] = raw }));

    [Fact]
    public void ANonObjectBodyDoesNotThrow()
        => Assert.Equal((false, (int?)null), Resolve("[1,2,3]"));

    // ---------------------------------------------------------------- required arguments

    private static string? Missing(string tool, string? argsJson)
    {
        using var args = argsJson is null ? null : JsonDocument.Parse(argsJson);
        return AgentServer.MissingRequiredArg(tool, args?.RootElement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"index":null}""")]
    public void ClosingATabWithoutSayingWhichIsRefused(string? args)
    {
        // The regression guard for the worst case found in review: this used to reach
        // /close with nothing to target and close the user's active tab.
        Assert.Equal("id or index", Missing("gergur_close_tab", args));
        Assert.Equal("id or index", Missing("gergur_activate_tab", args));
    }

    [Fact]
    public void ClosingATabWithAnIndexIsAllowedThrough()
        => Assert.Null(Missing("gergur_close_tab", """{"index":2}"""));

    [Fact]
    public void ClosingATabByIdAloneIsAllowedThrough()
    {
        // The id is the better of the two and has to be enough on its own. Demanding the
        // index as well would push callers back onto the number that goes stale the
        // moment a tab opens or is torn off.
        Assert.Null(Missing("gergur_close_tab", """{"id":"t4"}"""));
        Assert.Null(Missing("gergur_activate_tab", """{"id":"t4"}"""));
    }

    [Fact]
    public void AToolThatNamesNeitherSaysSoInWordsSomebodyCanActOn()
    {
        // "id|index" is the internal spelling, and is the name of nothing.
        Assert.DoesNotContain("|", Missing("gergur_close_tab", "{}"));
    }

    [Theory]
    [InlineData("gergur_navigate", """{"index":1}""", "url")]
    [InlineData("gergur_open_tab", "{}", "url")]
    [InlineData("gergur_run_javascript", "{}", "js")]
    [InlineData("gergur_click", "{}", "selector")]
    [InlineData("gergur_type_text", """{"selector":"#a"}""", "text")]
    public void EachToolNamesTheArgumentItIsMissing(string tool, string args, string expected)
        => Assert.Equal(expected, Missing(tool, args));

    [Fact]
    public void ClearingAFieldWithEmptyTextIsAllowed()
        => Assert.Null(Missing("gergur_type_text", """{"selector":"#a","text":""}"""));

    [Fact]
    public void AnEmptyUrlIsStillMissing()
        => Assert.Equal("url", Missing("gergur_navigate", """{"url":""}"""));

    [Theory]
    [InlineData("gergur_read_page")]
    [InlineData("gergur_list_tabs")]
    [InlineData("gergur_screenshot")]
    public void ToolsWithNoRequiredArgumentsRunWithNone(string tool)
        => Assert.Null(Missing(tool, null));

    [Fact]
    public void EveryRequiredArgumentIsDeclaredInTheToolSchema()
    {
        // The enforced list and the advertised schema must not drift apart.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(AgentServer.McpTools()));
        foreach (var tool in doc.RootElement.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            var schema = tool.GetProperty("inputSchema");
            var declared = schema.GetProperty("required")
                .EnumerateArray().Select(r => r.GetString()).ToHashSet();
            var enforced = AgentServer.RequiredArgsForTool(name);

            // A json schema cannot say "one of these two", so a requirement satisfied by
            // either argument is deliberately not in the schema's required list: putting
            // it there would have every caller supply both, including the stale one.
            var alternatives = enforced.Where(r => r.Contains('|')).ToArray();
            var plain = enforced.Where(r => !r.Contains('|')).ToArray();

            // Set equality, not subset: a schema declaring a required argument the
            // enforcement forgets is exactly how the close-tab bug got through.
            Assert.Equal(declared.Order(), plain.Order());

            // Whichever way it is spelled, every name the enforcement will look for has
            // to be one the schema told the caller about.
            var properties = schema.GetProperty("properties").EnumerateObject()
                .Select(p => p.Name).ToHashSet();
            foreach (string key in enforced.SelectMany(r => r.Split('|')))
                Assert.Contains(key, properties);
        }
    }

    [Fact]
    public void EveryToolThatTakesAnIndexAlsoTakesAnId()
    {
        // The id is the one that survives a tab opening or being torn into another
        // window, so any tool offering only the position is offering only the trap.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(AgentServer.McpTools()));
        foreach (var tool in doc.RootElement.EnumerateArray())
        {
            var properties = tool.GetProperty("inputSchema").GetProperty("properties");
            if (properties.TryGetProperty("index", out _))
                Assert.True(
                    properties.TryGetProperty("id", out _),
                    $"{tool.GetProperty("name").GetString()} offers an index but no id");
        }
    }

    [Fact]
    public void EveryToolRoutesSomewhereThisServerServes()
    {
        // A tool advertised with no route answers "not a tool" when it is called, which
        // is a worse first impression than never offering it.
        foreach (var tool in AgentServer.McpTools())
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(tool));
            string name = doc.RootElement.GetProperty("name").GetString()!;
            var (method, path) = AgentServer.RouteForTool(name);
            Assert.False(string.IsNullOrEmpty(path), $"{name} is advertised but routes nowhere");
            Assert.Contains(method, new[] { "GET", "POST" });
        }
    }
}
