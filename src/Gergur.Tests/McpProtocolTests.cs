using System.Text.Json;
using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The JSON-RPC envelope Gergur exposes as an MCP server. Every case below used to be
/// either untested or an HTTP 500 escaping the handler, which a conforming client reads
/// as a transport failure rather than a protocol error.
/// </summary>
public sealed class McpProtocolTests
{
    private static Task<object> NoTool(JsonElement _)
        => Task.FromResult<object>(new { content = Array.Empty<object>() });

    private static async Task<(int Status, JsonElement Body)> SendAsync(
        string json, Func<JsonElement, Task<object>>? tool = null)
    {
        JsonDocument? doc = json.Length == 0 ? null : JsonDocument.Parse(json);
        try
        {
            var (status, _, payload) = await AgentServer.HandleJsonRpcAsync(doc, tool ?? NoTool);
            return payload.Length == 0
                ? (status, default)
                : (status, JsonDocument.Parse(payload).RootElement.Clone());
        }
        finally { doc?.Dispose(); }
    }

    private static int ErrorCode(JsonElement body) => body.GetProperty("error").GetProperty("code").GetInt32();

    // ---------------------------------------------------------------- envelope

    [Fact]
    public async Task NotificationGetsAcceptedWithNoBody()
    {
        var (status, _) = await SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Equal(202, status);
    }

    [Fact]
    public async Task ExplicitNullIdIsAlsoANotification()
    {
        var (status, _) = await SendAsync("""{"jsonrpc":"2.0","id":null,"method":"ping"}""");
        Assert.Equal(202, status);
    }

    [Fact]
    public async Task UnknownMethodReturnsMethodNotFoundAndEchoesTheId()
    {
        var (_, body) = await SendAsync("""{"jsonrpc":"2.0","id":7,"method":"nope"}""");
        Assert.Equal(-32601, ErrorCode(body));
        Assert.Equal(7, body.GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""")] // a legal JSON-RPC batch
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task NonObjectRootsAreInvalidRequestsRatherThanExceptions(string json)
    {
        var (status, body) = await SendAsync(json);
        Assert.Equal(200, status);
        Assert.Equal(-32600, ErrorCode(body));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":[],"method":"ping"}""")]
    public async Task NonScalarIdsAreRejectedRatherThanThrowing(string json)
        => Assert.Equal(-32600, ErrorCode((await SendAsync(json)).Body));

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1.5,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":99999999999999999999,"method":"ping"}""")]
    public async Task IdsOutsideInt64SurviveAsRawTextInsteadOfThrowing(string json)
    {
        var (status, body) = await SendAsync(json);
        Assert.Equal(200, status);
        Assert.False(body.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""")]
    public async Task MissingOrNonStringMethodIsAnInvalidRequest(string json)
        => Assert.Equal(-32600, ErrorCode((await SendAsync(json)).Body));

    [Fact]
    public async Task InitializeAdvertisesToolsAndNamesTheServer()
    {
        var (_, body) = await SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        var result = body.GetProperty("result");
        Assert.False(string.IsNullOrEmpty(result.GetProperty("protocolVersion").GetString()));
        Assert.Equal("gergur", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    // ---------------------------------------------------------------- tools/call

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":[1,2]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":7}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""")]
    public async Task MalformedToolCallsReportIsErrorRatherThanThrowing(string json)
    {
        // The real dispatcher must run here: these used to throw before reaching it.
        var (status, body) = await SendAsync(json, Dispatch);
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task UnknownToolNameIsReportedNotThrown()
    {
        var (_, body) = await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gergur_delete_everything"}}""",
            Dispatch);
        Assert.True(body.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    // ---------------------------------------------------------------- tool surface

    [Fact]
    public void EveryAdvertisedToolMapsToARealEndpoint()
    {
        foreach (string name in AdvertisedToolNames())
        {
            var (method, path) = AgentServer.RouteForTool(name);
            Assert.False(path.Length == 0, $"{name} is advertised but maps to no endpoint");
            Assert.Contains(method, new[] { "GET", "POST" });
        }
    }

    [Fact]
    public void ToolsListIsNotEmptyAndNamesAreUnique()
    {
        var names = AdvertisedToolNames().ToList();
        Assert.NotEmpty(names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void NoToolCanReachTheMcpEndpointItself()
    {
        // Recursion here would be an uncatchable stack overflow, so keep it unreachable.
        foreach (string name in AdvertisedToolNames())
            Assert.NotEqual("/mcp", AgentServer.RouteForTool(name).Path);
        Assert.Equal("", AgentServer.RouteForTool("mcp").Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gergur_")]
    [InlineData("../tabs")]
    public void UnknownToolNamesMapNowhere(string name)
        => Assert.Equal("", AgentServer.RouteForTool(name).Path);

    /// <summary>The real validator, then a stub for a well-formed call.</summary>
    private static Task<object> Dispatch(JsonElement request)
        => Task.FromResult(AgentServer.RejectBadToolCall(request, out _)
            ?? (object)new { content = Array.Empty<object>() });

    private static IEnumerable<string> AdvertisedToolNames()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(AgentServer.McpTools()));
        foreach (var tool in doc.RootElement.EnumerateArray())
            yield return tool.GetProperty("name").GetString() ?? "";
    }
}
