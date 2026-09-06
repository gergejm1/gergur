using System.Text;
using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The hand-rolled HTTP parsing on the only listener that is reachable from the network.
/// Every case here was previously unguarded: a malformed request either parsed anyway or
/// held the connection open.
/// </summary>
public sealed class DropServerParserTests
{
    private static Stream Wire(string raw) => new MemoryStream(Encoding.UTF8.GetBytes(raw));

    private static async Task<(DropServer.DropRequest? Request, DropServer.HeadError Error)> Head(string raw)
    {
        var (request, error, _) = await DropServer.ReadHeadAsync(Wire(raw));
        return (request, error);
    }

    // ---------------------------------------------------------------- well formed

    [Fact]
    public async Task AWellFormedRequestIsParsed()
    {
        var (request, error) = await Head("GET /items?k=ABC123 HTTP/1.1\r\nHost: 192.168.0.20\r\n\r\n");

        Assert.Equal(DropServer.HeadError.None, error);
        Assert.Equal("GET", request!.Method);
        Assert.Equal("/items", request.Path);
        Assert.Equal("ABC123", request.Query["k"]);
        Assert.Equal(0, request.ContentLength);
    }

    [Fact]
    public async Task QueryValuesAreDecoded()
    {
        var (request, _) = await Head("POST /upload?k=K&name=my%20holiday%2Bsnap.jpg HTTP/1.1\r\n\r\n");
        Assert.Equal("my holiday+snap.jpg", request!.Query["name"]);
    }

    [Fact]
    public async Task ContentLengthIsRead()
    {
        var (request, error) = await Head("POST /send?k=K HTTP/1.1\r\nContent-Length: 42\r\n\r\n");
        Assert.Equal(DropServer.HeadError.None, error);
        Assert.Equal(42, request!.ContentLength);
    }

    // ---------------------------------------------------------------- refused

    [Fact]
    public async Task AHeadThatNeverTerminatesIsIncompleteNotParsedAnyway()
    {
        // A peer that opens a socket and dribbles bytes must not look like a request.
        var (request, error) = await Head("GET /items?k=K HTTP/1.1\r\nHost: x\r\n");
        Assert.Null(request);
        Assert.Equal(DropServer.HeadError.Incomplete, error);
    }

    [Fact]
    public async Task AnOversizedHeadIsRefused()
    {
        string flood = "GET /?k=K HTTP/1.1\r\n" + new string('x', 20 * 1024) + "\r\n\r\n";
        var (request, error) = await Head(flood);
        Assert.Null(request);
        Assert.Equal(DropServer.HeadError.Incomplete, error);
    }

    [Fact]
    public async Task DuplicateContentLengthIsRefusedRatherThanLastOneWinning()
    {
        // "100 then bogus" used to silently become 0.
        var (request, error) = await Head(
            "POST /send?k=K HTTP/1.1\r\nContent-Length: 100\r\nContent-Length: 5\r\n\r\n");
        Assert.Null(request);
        Assert.Equal(DropServer.HeadError.BadLength, error);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData(" -1 ")]
    [InlineData("+7")]
    [InlineData("1,000")]
    public async Task AContentLengthThatIsNotAPlainNumberIsRefused(string value)
    {
        var (request, error) = await Head($"POST /send?k=K HTTP/1.1\r\nContent-Length: {value}\r\n\r\n");
        Assert.Null(request);
        Assert.Equal(DropServer.HeadError.BadLength, error);
    }

    [Theory]
    [InlineData("chunked")]
    [InlineData("gzip, chunked")]
    public async Task TransferEncodingIsRefusedRatherThanIgnored(string encoding)
    {
        // Ignoring it while honouring Content-Length is the classic smuggling primitive.
        var (request, error) = await Head(
            $"POST /send?k=K HTTP/1.1\r\nTransfer-Encoding: {encoding}\r\nContent-Length: 5\r\n\r\n");
        Assert.Null(request);
        Assert.Equal(DropServer.HeadError.Unsupported, error);
    }

    [Theory]
    [InlineData("\r\n\r\n")]
    [InlineData("GARBAGE\r\n\r\n")]
    public async Task AMalformedRequestLineIsRefused(string raw)
    {
        var (request, error) = await Head(raw);
        Assert.Null(request);
        Assert.Equal(DropServer.HeadError.Malformed, error);
    }

    // ---------------------------------------------------------------- bodies

    [Fact]
    public async Task ABodyOfExactlyTheDeclaredLengthIsReturned()
    {
        var body = await DropServer.ReadBodyAsync(Wire("hello"), 5, 1024);
        Assert.Equal("hello", Encoding.UTF8.GetString(body!));
    }

    [Fact]
    public async Task ATruncatedBodyIsRejectedRatherThanStoredShort()
    {
        // The phone walking out of Wi-Fi range mid-upload used to save a partial file
        // and answer 200 OK.
        var body = await DropServer.ReadBodyAsync(Wire("hel"), 5, 1024);
        Assert.Null(body);
    }

    [Fact]
    public async Task ABodyArrivingAlongsideTheHeadIsNotLost()
    {
        // The head is read in chunks, so body bytes come with it. Waiting to read them
        // from the socket again is a hang, which is what every POST did.
        var stream = Wire("POST /send?k=K HTTP/1.1\r\nContent-Length: 5\r\n\r\nhello");
        var (request, error, buffered) = await DropServer.ReadHeadAsync(stream);

        Assert.Equal(DropServer.HeadError.None, error);
        var body = await DropServer.ReadBodyAsync(stream, request!.ContentLength, 1024, buffered);
        Assert.Equal("hello", Encoding.UTF8.GetString(body!));
    }

    [Fact]
    public async Task ABodyLargerThanOneReadIsReassembled()
    {
        // Part arrives with the head, the rest from the socket; both must land.
        string payload = new string('z', 4096);
        var stream = Wire($"POST /upload?k=K HTTP/1.1\r\nContent-Length: {payload.Length}\r\n\r\n{payload}");
        var (request, _, buffered) = await DropServer.ReadHeadAsync(stream);

        var body = await DropServer.ReadBodyAsync(stream, request!.ContentLength, 1 << 20, buffered);
        Assert.Equal(payload.Length, body!.Length);
        Assert.All(body, b => Assert.Equal((byte)'z', b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2048)]  // over the cap
    public async Task ARefusedLengthReadsNothing(long declared)
        => Assert.Null(await DropServer.ReadBodyAsync(Wire("data"), declared, 1024));
}
