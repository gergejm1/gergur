using System.Text;
using Gergur.App;
using Gergur.Data;
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
    // a Shortcut set to accept both Text and URLs sends both and leaves one empty
    [InlineData("k=KEY&text=&url=https://example.com/x", "https://example.com/x")]
    [InlineData("k=KEY&url=&text=my note", "my note")]
    [InlineData("k=KEY&url=&text=https://x/?a=1&b=2", "https://x/?a=1&b=2")]
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
    private static IEnumerable<string> Mine() => ["192.168.0.20", "10.1.2.3", "fe80::1", "fe80::1%12"];

    [Theory]
    [InlineData("192.168.0.20:24003", "192.168.0.20")]
    [InlineData("192.168.0.20", "192.168.0.20")]
    [InlineData("10.1.2.3:24003", "10.1.2.3")]
    [InlineData("[fe80::1]:24003", "[fe80::1]")]
    [InlineData("[fe80::1]", "[fe80::1]")]
    [InlineData("fe80::1", "[fe80::1]")]   // bracketless in, bracketed out, or no url parses
    [InlineData("[fe80::1%12]:24003", "[fe80::1]")]   // the zone id would need escaping
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
    // The share is appended raw, so a shared link brings its own parameters with it, and
    // an Amazon search url really does carry "&k=". Whether it lands before or after the
    // pairing key depends only on which end the Shortcut puts the input, so neither the
    // first nor the last is reliably ours: all of them are compared.
    [InlineData("k=KEY&text=https://maps.example.com/?q=cafe&k=abc123", "KEY")]
    [InlineData("k=KEY&url=https://x/?k=other&z=1", "KEY")]
    [InlineData("text=https://www.amazon.com/s?i=electronics&k=laptop&k=KEY", "KEY")]
    [InlineData("text=https://x/?k=a&k=b&k=KEY", "KEY")]
    // a name merely ending in "k" is not the key
    [InlineData("bk=no&k=KEY", "KEY")]
    [InlineData("k=KEY%20spaced", "KEY spaced")]
    public void TheRealKeyIsFoundWhereverTheLinkPutItsOwn(string rawQuery, string expected)
        => Assert.Contains(expected, DropServer.KeysFrom(rawQuery));

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("text=hello", new string[0])]
    [InlineData("bk=no", new string[0])]
    // Asserting the whole sequence, because a KeysFrom that returned nothing at all
    // would satisfy "does not contain the key" while refusing every real request.
    //
    // The link's own "k=" here follows a "?", so it does not start a parameter and is
    // never even a candidate. Only one that follows an "&" is.
    [InlineData("k=wrong&text=https://x/?k=alsowrong", new[] { "wrong" })]
    [InlineData("k=one&k=two", new[] { "one", "two" })]
    public void EveryCandidateIsOfferedAndNoneOfTheseIsTheKey(string rawQuery, string[] expected)
    {
        Assert.Equal(expected, DropServer.KeysFrom(rawQuery));
        Assert.DoesNotContain("KEY", DropServer.KeysFrom(rawQuery));
    }

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

/// <summary>
/// Whether a failure can still be reported. The end of a download cannot be tested
/// through a socket, because by then the peer has usually gone and cannot observe what
/// is written after it: the guard has to be checked where it is made.
/// </summary>
public sealed class DropServerFailureReportingTests
{
    [Fact]
    public async Task NothingIsWrittenOnceAResponseHasStarted()
    {
        // The defect this exists for: a download that fails halfway is already a stream
        // of file bytes, so "HTTP/1.1 408 Request Timeout" written after it lands inside
        // the photo the phone is saving. Measured on a real transfer as 2,883,584 bytes
        // of image followed by a response header.
        var sink = new MemoryStream();

        await DropServer.TryFailAsync(
            sink, new DropServer.Exchange { Responded = true }, 500, "text/plain", "boom"u8.ToArray());

        Assert.Empty(sink.ToArray());
    }

    [Fact]
    public async Task AFailureBeforeAnythingWentOutIsStillReported()
    {
        // The other half: closing silently reaches the phone as a network error with
        // nothing to say what happened.
        var sink = new MemoryStream();

        await DropServer.TryFailAsync(
            sink, new DropServer.Exchange(), 500, "application/json", """{"error":"nope"}"""u8.ToArray());

        string written = Encoding.UTF8.GetString(sink.ToArray());
        Assert.StartsWith("HTTP/1.1 500 Internal Server Error", written);
        Assert.EndsWith("""{"error":"nope"}""", written);
    }

    [Fact]
    public async Task WritingAResponseRecordsThatOneHasBegun()
    {
        // The flag lives inside the writer rather than at the call sites because three
        // call sites forgot it. This is what makes that structural rather than a habit.
        var sink = new MemoryStream();
        var exchange = new DropServer.Exchange();

        await DropServer.WriteAsync(sink, exchange, 200, "text/plain", "ok"u8.ToArray());

        Assert.True(exchange.Responded, "a reply went out without recording that it had");
        Assert.StartsWith("HTTP/1.1 200 OK", Encoding.UTF8.GetString(sink.ToArray()));
    }

    [Fact]
    public async Task AMissingStreamIsNotAnError()
        => await DropServer.TryFailAsync(null, new DropServer.Exchange(), 500, "text/plain", "x"u8.ToArray());
}

/// <summary>
/// A download that fails after the body has started. Driven over a stream that breaks on
/// purpose, because a socket cannot arrange this: by the time a real transfer fails the
/// peer has usually gone, and what is written after it lands in nobody's file.
/// </summary>
public sealed class DownloadFailurePartwayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-partway-{Guid.NewGuid():N}");
    private readonly DropStore _store;
    private readonly DropServer _server;

    public DownloadFailurePartwayTests()
    {
        _store = new DropStore(_root);
        _server = new DropServer(_store, new Settings { DropPort = 24999, DropKey = "0123456789ABCDEF01234567" });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Accepts a fixed number of bytes and then behaves like a peer that vanished.</summary>
    private sealed class BreaksAfter(int limit) : Stream
    {
        private readonly MemoryStream _written = new();

        public byte[] Delivered => _written.ToArray();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_written.Length + buffer.Length > limit)
                throw new IOException("the peer went away");
            _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written.Length;
        public override long Position { get => _written.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private DropServer.DropRequest Get(string path)
        => new("GET", path, new Dictionary<string, string>(), 0, "k=KEY", "x");

    [Fact]
    public async Task AnOrdinaryReplyAlsoRecordsThatItHappened()
    {
        // Not only the download path: every route has to leave the exchange saying a
        // reply went out, or an error can still be appended to one.
        _store.AddText("something", from: "pc");
        var sink = new BreaksAfter(64 * 1024);
        var exchange = new DropServer.Exchange();

        await _server.RouteAsync(sink, Get("/items"), [], exchange, default);

        Assert.True(exchange.Responded);
        Assert.StartsWith("HTTP/1.1 200 OK", Encoding.UTF8.GetString(sink.Delivered));
    }

    [Fact]
    public async Task AFailedDownloadIsNotFollowedByAnErrorResponse()
    {
        // The defect: the bytes already sent are a photo, so a status line written after
        // them lands inside the file the phone is saving. Measured on a real transfer as
        // 2,753,104 bytes of image whose tail was "HTTP/1.1 408 Request Timeout".
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        var item = _store.AddFile("big.bin", new MemoryStream(payload), from: "pc");

        // Big enough that the head and at least one body chunk land before it breaks,
        // so the failure really is partway through a body.
        var sink = new BreaksAfter(96 * 1024);
        var exchange = new DropServer.Exchange();

        await Assert.ThrowsAsync<IOException>(
            () => _server.RouteAsync(sink, Get($"/file/{item.Id}"), [], exchange, default));

        // The handler's error paths both run through this, and both must now write nothing.
        Assert.True(exchange.Responded, "the route did not record that a reply had begun");
        await DropServer.TryFailAsync(sink, exchange, 408, "text/plain", "Request timed out."u8.ToArray());
        await DropServer.TryFailAsync(sink, exchange, 500, "text/plain", "boom"u8.ToArray());

        string delivered = Encoding.UTF8.GetString(sink.Delivered);
        Assert.Equal(0, delivered.IndexOf("HTTP/1.1", StringComparison.Ordinal));
        Assert.Equal(-1, delivered.IndexOf("HTTP/1.1", 1, StringComparison.Ordinal));
        Assert.DoesNotContain("Request timed out", delivered);
    }

    [Fact]
    public async Task AFailureBeforeTheBodyStartsIsStillReportable()
    {
        // Opening the file is what fails when something else is holding it, and nothing
        // has gone out at that point, so this one must still be answerable.
        var item = _store.AddFile("held.bin", new MemoryStream([1, 2, 3]), from: "pc");
        using var held = File.Open(_store.PathFor(item)!, FileMode.Open, FileAccess.Read, FileShare.None);

        var sink = new BreaksAfter(1024);
        var exchange = new DropServer.Exchange();

        await Assert.ThrowsAnyAsync<Exception>(
            () => _server.RouteAsync(sink, Get($"/file/{item.Id}"), [], exchange, default));

        Assert.False(exchange.Responded, "nothing was written, so the reply is still open");
        await DropServer.TryFailAsync(sink, exchange, 500, "text/plain", "boom"u8.ToArray());
        Assert.StartsWith("HTTP/1.1 500", Encoding.UTF8.GetString(sink.Delivered));
    }
}

/// <summary>
/// Naming something that arrived without a name. A share-sheet Shortcut posting a photo
/// has none to send: Photos does not hand the shortcut a filename, and a url can only
/// carry what the shortcut can put in it, so everything landed in the drop as "file".
/// </summary>
public sealed class UploadNamingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gergur-sniff-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static ReadOnlySpan<byte> Bytes(params byte[] head) => head;

    [Fact]
    public void AJpegIsRecognised()
        => Assert.Equal((".jpg", "Photo"), DropServer.Sniff(Bytes(0xFF, 0xD8, 0xFF, 0xE0, 0, 0)));

    [Fact]
    public void APngIsRecognised()
        => Assert.Equal((".png", "Photo"), DropServer.Sniff("\x89PNG\r\n\x1a\n"u8));

    [Fact]
    public void AGifIsRecognised()
        => Assert.Equal((".gif", "Photo"), DropServer.Sniff("GIF89a-and-more"u8));

    [Fact]
    public void AWebpIsRecognised()
        => Assert.Equal((".webp", "Photo"), DropServer.Sniff("RIFF....WEBP"u8));

    [Theory]
    [InlineData("heic")]
    [InlineData("mif1")]
    public void TheFormatAnIphoneActuallyShootsIsRecognised(string brand)
        => Assert.Equal((".heic", "Photo"), DropServer.Sniff(Encoding.UTF8.GetBytes("....ftyp" + brand)));

    [Fact]
    public void TheSameContainerCarryingVideoIsNotCalledAPhoto()
        => Assert.Equal((".mov", "Video"), DropServer.Sniff("....ftypqt  "u8));

    [Fact]
    public void APdfIsRecognised()
        => Assert.Equal((".pdf", "Document"), DropServer.Sniff("%PDF-1.7"u8));

    [Theory]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("\0\0\0\0\0\0\0\0\0\0\0\0")]
    public void AnythingUnrecognisedKeepsANeutralName(string content)
        => Assert.Equal((".bin", "File"), DropServer.Sniff(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void APhotoWithNoNameIsNamedForWhatItIsAndWhenItLanded()
    {
        File.WriteAllBytes(_path, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);

        string name = DropServer.NameFromContent(_path);

        Assert.StartsWith("Photo ", name);
        Assert.EndsWith(".jpg", name);
        // And it is a name the store will keep the extension of, not rewrite to .bin.
        Assert.Equal(".jpg", Gergur.Data.DropStore.StorableExtension(name));
    }

    [Fact]
    public void AFileTooShortToIdentifyStillGetsAName()
    {
        File.WriteAllBytes(_path, [0xFF]);

        string name = DropServer.NameFromContent(_path);

        Assert.StartsWith("File ", name);
        Assert.EndsWith(".bin", name);
    }
}
