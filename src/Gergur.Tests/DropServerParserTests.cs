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

/// <summary>
/// The share-sheet query reader, on its own. Every ordering case reached it only through
/// a socket before, and the orderings that matter (the key after the share, a marker
/// hiding inside an earlier parameter name, a shared link carrying "&amp;text=" of its
/// own) are the ones that were not covered at all.
/// </summary>
public sealed class SharedValueTests
{
    [Theory]
    // the shape the setup page tells you to build
    [InlineData("k=KEY&text=hello", "hello")]
    [InlineData("k=KEY&url=https://example.com", "https://example.com")]
    // a link carrying its own parameters: read to the end, not to the first "&"
    [InlineData("k=KEY&text=https://youtube.com/watch?v=abc&t=90s", "https://youtube.com/watch?v=abc&t=90s")]
    // a marker inside an earlier parameter name is not the marker
    [InlineData("k=KEY&context=hi&text=real", "real")]
    [InlineData("k=KEY&subtext=hi&text=real", "real")]
    [InlineData("k=KEY&curl=hi&url=real", "real")]
    // whichever marker starts first wins, so a shared link keeps its own "&text="
    [InlineData("k=KEY&url=https://x/?a=1&text=hi", "https://x/?a=1&text=hi")]
    [InlineData("k=KEY&text=https://x/?a=1&url=hi", "https://x/?a=1&url=hi")]
    // the share can come first
    [InlineData("text=hello&k=KEY", "hello&k=KEY")]
    // percent escapes decode; "+" is a space; a malformed escape stays as written
    [InlineData("k=KEY&text=my%20note", "my note")]
    [InlineData("k=KEY&text=a+b", "a b")]
    [InlineData("k=KEY&text=100%25%20done%", "100% done%")]
    public void TheSharedItemIsTakenWhole(string rawQuery, string expected)
        => Assert.Equal(expected, DropServer.SharedValue(rawQuery));

    [Theory]
    [InlineData("")]
    [InlineData("k=KEY")]
    [InlineData("k=KEY&context=hi")]      // ends in "text=" but is not it
    [InlineData("k=KEY&text=")]           // nothing was shared
    [InlineData("k=KEY&mytext=hi")]
    public void NothingSharedIsNull(string rawQuery)
        => Assert.Null(DropServer.SharedValue(rawQuery));

    [Fact]
    public void TheKeyIsCutOutOfTheSharedItem()
    {
        // Reading to the end takes the key with it when the Shortcut is built the other
        // way round, and the result would be stored and rendered as a link.
        Assert.Equal(
            "https://example.com/x",
            DropServer.StripKey("https://example.com/x&k=0123456789ABCDEF01234567", "0123456789ABCDEF01234567"));
    }

    [Fact]
    public void StrippingTheKeyLeavesAnUnrelatedParameterAlone()
        => Assert.Equal("a&k=other", DropServer.StripKey("a&k=other", "0123456789ABCDEF01234567"));

    /// <summary>The addresses this machine is pretending to have, for HostFor.</summary>
    private static IEnumerable<string> Mine() => ["192.168.0.20", "10.1.2.3", "fe80::1"];

    [Theory]
    [InlineData("192.168.0.20:24003", "192.168.0.20")]
    [InlineData("192.168.0.20", "192.168.0.20")]
    [InlineData("10.1.2.3:24003", "10.1.2.3")]
    [InlineData("[fe80::1]:24003", "[fe80::1]")]
    [InlineData("[fe80::1]", "[fe80::1]")]
    [InlineData("fe80::1", "fe80::1")]     // bracketless, and the colons are the address
    public void TheSetupPageUsesTheAddressThePhoneReachedUsOn(string header, string expected)
        => Assert.Equal(expected, DropServer.HostFor(header, () => "guessed", Mine));

    [Theory]
    [InlineData("")]                       // no Host header
    [InlineData("localhost:24003")]        // the PC talking to itself says nothing useful
    [InlineData("127.0.0.1:24003")]
    [InlineData("evil host with spaces")]
    [InlineData("<script>:1")]
    // A name cannot be checked against anything here, so it is not printed as the address.
    [InlineData("gergur-pc.local:24003")]
    // The address the setup page prints is the one you are told to bake into a Shortcut,
    // with the pairing key on the end. Anything that is not this machine goes nowhere
    // near it: one request from something holding the key would otherwise redirect every
    // future share, and the key with it, to somebody else's server.
    [InlineData("evil.example.com:24003")]
    [InlineData("203.0.113.7:24003")]
    [InlineData("192.168.0.99:24003")]     // this network, still not this machine
    public void AHostThatIsNotThisMachineFallsBackToTheGuess(string header)
        => Assert.Equal("guessed", DropServer.HostFor(header, () => "guessed", Mine));

    [Fact]
    public void WithNoHostAndNoGuessThereIsNoAddress()
        => Assert.Null(DropServer.HostFor("", () => null, Mine));

    [Fact]
    public async Task TheRawQueryAndHostSurviveParsing()
    {
        var (request, error, _) = await DropServer.ReadHeadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(
                "GET /share?k=K&text=a&b HTTP/1.1\r\nHost: 192.168.0.20:24003\r\n\r\n")));

        Assert.Equal(DropServer.HeadError.None, error);
        Assert.Equal("k=K&text=a&b", request!.RawQuery);
        Assert.Equal("192.168.0.20:24003", request.Host);
    }

    [Fact]
    public async Task AMalformedEscapeIsKeptRatherThanRefused()
    {
        // Not a design choice so much as what UnescapeDataString does; asserted here so
        // the next person does not add a guard for a case that cannot happen.
        var (request, error, _) = await DropServer.ReadHeadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("GET /?k=K&n=100%25%20done% HTTP/1.1\r\n\r\n")));

        Assert.Equal(DropServer.HeadError.None, error);
        Assert.Equal("100% done%", request!.Query["n"]);
    }
}

/// <summary>
/// Streaming an upload to disk. This replaced the in-memory read on the upload path and
/// inherited none of its boundary tests, though it is the method that decides whether a
/// half-received photo is kept.
/// </summary>
public sealed class CopyBodyToFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gergur-copy-{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static Stream Wire(string raw) => new MemoryStream(Encoding.UTF8.GetBytes(raw));

    [Fact]
    public async Task ABodyOfTheDeclaredLengthIsWritten()
    {
        Assert.True(await DropServer.CopyBodyToFileAsync(Wire("hello"), 5, 1024, [], _path));
        Assert.Equal("hello", await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task ATruncatedUploadIsRefused()
    {
        // The phone walking out of Wi-Fi range mid-photo. What is bought here is the
        // false, not the state of the disk: the partial bytes are still in the staging
        // file, and the caller deletes it. Storing it and answering 200 is the bug.
        Assert.False(await DropServer.CopyBodyToFileAsync(Wire("hel"), 5, 1024, [], _path));
        Assert.Equal("hel", await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task BytesThatArrivedWithTheHeadAreWrittenFirst()
    {
        Assert.True(await DropServer.CopyBodyToFileAsync(
            Wire("world"), 10, 1024, Encoding.UTF8.GetBytes("hello"), _path));
        Assert.Equal("helloworld", await File.ReadAllTextAsync(_path));
    }

    [Fact]
    public async Task MoreBytesThanPromisedAreNotWritten()
    {
        // The declared length is the contract; the rest belongs to no request.
        Assert.True(await DropServer.CopyBodyToFileAsync(Wire("hello there"), 5, 1024, [], _path));
        Assert.Equal(5, new FileInfo(_path).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2048)]  // over the cap
    public async Task ARefusedLengthWritesNothing(long declared)
    {
        Assert.False(await DropServer.CopyBodyToFileAsync(Wire("data"), declared, 1024, [], _path));
        Assert.False(File.Exists(_path), "a refused upload must not leave a file behind");
    }

    [Fact]
    public async Task ALargeBodyIsReassembledAcrossReads()
    {
        string payload = new string('z', 300_000);   // several chunks
        Assert.True(await DropServer.CopyBodyToFileAsync(Wire(payload), payload.Length, 1 << 20, [], _path));
        Assert.Equal(payload.Length, new FileInfo(_path).Length);
    }
}

/// <summary>
/// The pairing key gate and the cancellation source behind the connection cap. Both are
/// small, and both had a failure that only showed up under a race or an unusual url.
/// </summary>
public sealed class DropServerGateTests
{
    [Theory]
    [InlineData("k=KEY", "KEY")]
    [InlineData("k=KEY&text=hello", "KEY")]
    // the share is appended raw, so a shared link brings its own parameters with it
    [InlineData("k=KEY&text=https://maps.example.com/?q=cafe&k=abc123", "KEY")]
    [InlineData("k=KEY&url=https://x/?k=other&z=1", "KEY")]
    // a name merely ending in "k" is not the key
    [InlineData("bk=no&k=KEY", "KEY")]
    [InlineData("k=KEY%20spaced", "KEY spaced")]
    public void TheKeyIsTheOneWeWereSentAndNotTheOneTheLinkCarries(string rawQuery, string expected)
        => Assert.Equal(expected, DropServer.KeyFrom(rawQuery));

    [Theory]
    [InlineData("")]
    [InlineData("text=hello")]
    [InlineData("bk=no")]
    public void NoKeyMeansNoKey(string rawQuery)
        => Assert.Null(DropServer.KeyFrom(rawQuery));

    [Fact]
    public void ARequestArrivingAsTheServerStopsStillGetsAWorkingDeadline()
    {
        // Stop() disposes the source, and reading .Token on a disposed one throws. That
        // throw used to escape before the connection slot was released, so every
        // occurrence cost a permit off the cap of 16, permanently.
        var disposed = new CancellationTokenSource();
        disposed.Dispose();

        using var timeout = DropServer.LinkedTimeout(disposed);

        Assert.False(timeout.IsCancellationRequested);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1));   // still a usable source
    }

    [Fact]
    public void ALiveSourceStillCancelsTheRequestsLinkedToIt()
    {
        using var lifetime = new CancellationTokenSource();
        using var timeout = DropServer.LinkedTimeout(lifetime);

        lifetime.Cancel();

        Assert.True(timeout.IsCancellationRequested);
    }

    [Fact]
    public void NoSourceAtAllIsStillASource()
    {
        using var timeout = DropServer.LinkedTimeout(null);
        Assert.False(timeout.IsCancellationRequested);
    }
}
