using System.Net;
using System.Net.Sockets;
using System.Text;
using Gergur.App;
using Gergur.Data;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Drives a real DropServer over a real socket.
///
/// The unit tests cover the head parser and the body reader separately, and a shipped
/// bug lived in the seam between them: reading the head in chunks swallowed the body, so
/// every POST hung while the whole suite stayed green. Routing, the key gate and the
/// timeouts had no coverage at all. These exercise the server end to end instead.
/// </summary>
public sealed class DropServerLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-live-{Guid.NewGuid():N}");
    private readonly DropStore _store;
    private readonly DropServer _server;
    private readonly Settings _settings;
    private readonly int _port;

    public DropServerLiveTests()
    {
        _port = FreePort();
        _store = new DropStore(_root);
        // A key of the right length already, so EnsureKey never calls Settings.Save and
        // a test can never write to the real profile.
        _settings = new Settings { DropPort = _port, DropKey = "0123456789ABCDEF01234567" };
        _server = new DropServer(_store, _settings);
        _server.Start();
    }

    public void Dispose()
    {
        _server.Stop();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    internal static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Sends a raw request, optionally holding the body back to split the writes.</summary>
    private async Task<string> SendAsync(
        string head, byte[]? body = null, TimeSpan? pauseBeforeBody = null, bool halfCloseAfterBody = false)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        using var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
        if (body is not null)
        {
            if (pauseBeforeBody is { } pause)
                await Task.Delay(pause);
            await stream.WriteAsync(body);
        }
        // A phone that walks out of range closes its side. Without this the server is
        // right to keep waiting for the bytes that were promised.
        if (halfCloseAfterBody)
            client.Client.Shutdown(SocketShutdown.Send);

        using var reader = new MemoryStream();
        try
        {
            await stream.CopyToAsync(reader);
        }
        catch (IOException)
        {
            // The peer reset instead of answering; return whatever arrived so the
            // assertion reports "" rather than an exception from the helper.
        }
        return Encoding.UTF8.GetString(reader.ToArray());
    }

    /// <summary>
    /// Sends a request and reads to the end, reporting whether the connection was reset
    /// rather than closed cleanly. <see cref="SendAsync"/> hides that difference on
    /// purpose so an assertion can report an empty string; here it is the thing measured.
    /// </summary>
    private async Task<(string Response, bool Reset)> ReadFullyAsync(string head)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));

        using var reader = new MemoryStream();
        bool reset = false;
        try
        {
            await stream.CopyToAsync(reader);
        }
        catch (IOException)
        {
            reset = true;
        }
        return (Encoding.UTF8.GetString(reader.ToArray()), reset);
    }

    private string Key => _settings.DropKey;

    [Fact]
    public async Task ThePageIsServedToAPairedRequest()
    {
        string response = await SendAsync($"GET /?k={Key} HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Gergur Drop", response);
        Assert.Contains("Referrer-Policy: no-referrer", response);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?k=wrongkeywrongkeywrongkey")]
    public async Task AnUnpairedRequestIsRefused(string query)
    {
        string response = await SendAsync($"GET /items{query} HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", response);
    }

    [Fact]
    public async Task ABodySentAfterTheHeadStillArrives()
    {
        // The regression that shipped: the body was consumed with the head, so the server
        // waited for bytes it already had and the request hung. Splitting the writes and
        // pausing is the case a single-buffer test never reaches.
        var body = Encoding.UTF8.GetBytes("""{"text":"https://example.com/split"}""");
        string response = await SendAsync(
            $"POST /send?k={Key} HTTP/1.1\r\nHost: x\r\nContent-Length: {body.Length}\r\n\r\n",
            body, pauseBeforeBody: TimeSpan.FromMilliseconds(150));

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains(_store.Items, i => i.Text == "https://example.com/split" && i.Kind == "link");
    }

    [Fact]
    public async Task AnUploadRoundTripsByteForByte()
    {
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);

        string response = await SendAsync(
            $"POST /upload?k={Key}&name=snap.jpg HTTP/1.1\r\nHost: x\r\nContent-Length: {payload.Length}\r\n\r\n",
            payload);
        Assert.StartsWith("HTTP/1.1 200 OK", response);

        var item = Assert.Single(_store.Items, i => i.IsFile);
        Assert.Equal("snap.jpg", item.Text);
        Assert.Equal(payload, File.ReadAllBytes(_store.PathFor(item)!));
    }

    [Fact]
    public async Task AnUploadThatStopsEarlyIsRefusedAndStoresNothing()
    {
        // Declares 4096, sends 10, then goes away. The phone leaving Wi-Fi mid-transfer.
        string response = await SendAsync(
            $"POST /upload?k={Key}&name=cut.bin HTTP/1.1\r\nHost: x\r\nContent-Length: 4096\r\n\r\n",
            Encoding.UTF8.GetBytes("truncated!"), halfCloseAfterBody: true);

        Assert.StartsWith("HTTP/1.1 400", response);
        Assert.Empty(_store.Items);
        Assert.Empty(Directory.GetFiles(_store.FilesDir));
    }

    [Fact]
    public async Task AnEmptyFileIsRefusedAsEmptyAndNotAsTooLarge()
    {
        // Folded into the size check, a zero byte file came back as "too large", which
        // is not something anyone can act on.
        string response = await SendAsync(
            $"POST /upload?k={Key}&name=nothing.txt HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400 Bad Request", response);
        Assert.Contains("that file is empty", response);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task ARefusedUploadIsAnAnswerAndNotAReset()
    {
        // The refusal is written while the body it refused is still arriving, and closing
        // both directions there makes Windows reset the connection, throwing the answer
        // away. The phone then shows a network error instead of the reason.
        //
        // The body has to actually be sent. Declaring a huge Content-Length and then
        // sending nothing leaves the receive queue empty, so no reset happens either way
        // and the first version of this test passed with the fix reverted.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(
            $"POST /upload?k={Key}&name=huge.bin HTTP/1.1\r\nHost: x\r\nContent-Length: 999999999\r\n\r\n"));

        // Keep pushing bytes at it, so there is unread data in the queue when it closes.
        using var pumping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pump = Task.Run(async () =>
        {
            var block = new byte[256 * 1024];
            try
            {
                while (!pumping.IsCancellationRequested)
                    await stream.WriteAsync(block, pumping.Token);
            }
            catch
            {
                // The server closes on us: that is the point of the test.
            }
        });

        var reader = new MemoryStream();
        bool reset = false;
        try
        {
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
                reader.Write(buffer, 0, read);
        }
        catch (IOException)
        {
            reset = true;
        }
        await pumping.CancelAsync();
        await pump;

        string response = Encoding.UTF8.GetString(reader.ToArray());
        Assert.StartsWith("HTTP/1.1 413 Payload Too Large", response);
        Assert.False(reset, "the refusal was written and then thrown away by a reset");
    }

    [Fact]
    public async Task EveryResponseCarriesTheHeadersThatKeepTheKeyOnThisNetwork()
    {
        // The pairing key lives in this page's url, so another page must not be able to
        // frame it and read that url, and the page must not be able to fetch anything
        // off the machine.
        string response = await SendAsync($"GET /?k={Key} HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.Contains("X-Frame-Options: DENY", response);
        Assert.Contains("Content-Security-Policy:", response);
        Assert.Contains("default-src 'none'", response);
        Assert.Contains("frame-ancestors 'none'", response);
        Assert.Contains("connect-src 'self'", response);
        Assert.Contains("Referrer-Policy: no-referrer", response);
    }

    [Fact]
    public async Task AnOrdinaryRequestIsClosedAsSoonAsItIsAnswered()
    {
        // The drain that stops a refusal being reset used to run after every response,
        // so a connection lingered for the full drain deadline even when there was
        // nothing left to read. The page polls /items every four seconds, so that is a
        // socket and a task held open for a quarter of a second, repeatedly, for nothing.
        //
        // Measured as the time to end of stream, with a client that does not close first:
        // the server's close is what ends the read.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"GET /items?k={Key} HTTP/1.1\r\nHost: x\r\n\r\n"));

        using var reader = new MemoryStream();
        await stream.CopyToAsync(reader);
        Assert.StartsWith("HTTP/1.1 200", Encoding.UTF8.GetString(reader.ToArray()));

        // Measured on the server, not the wire: the close of the send side reaches the
        // client before the drain begins, so from out here the two are identical.
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (_server.InFlight > 0 && start.Elapsed < TimeSpan.FromMilliseconds(150))
            await Task.Delay(5);

        Assert.True(_server.InFlight == 0,
            $"the handler was still running {start.ElapsedMilliseconds}ms after the answer, which is the drain running with nothing to drain");
    }

    [Fact]
    public async Task AnExecutableIsStoredWhereAClickCannotRunIt()
    {
        // Refusing only at click time left the deny list load-bearing. The bytes are kept,
        // the name is shown, but what lands on disk is not runnable.
        string response = await SendAsync(
            $"POST /upload?k={Key}&name=setup.exe HTTP/1.1\r\nHost: x\r\nContent-Length: 4\r\n\r\n",
            [1, 2, 3, 4]);

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        var item = Assert.Single(_store.Items);
        Assert.Equal("setup.exe", item.Text);                       // still shown honestly
        Assert.EndsWith(".bin", item.StoredName);                   // but not executable
        Assert.DoesNotContain(".exe", item.StoredName);
    }

    [Fact]
    public async Task AStoredFileIsDownloadableByItsId()
    {
        var item = _store.AddFile("notes.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello")), "pc");

        string response = await SendAsync($"GET /file/{item.Id}?k={Key} HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Length: 5", response);
        Assert.EndsWith("hello", response);
    }

    [Fact]
    public async Task AnUnknownPathIsNotFound()
        => Assert.StartsWith("HTTP/1.1 404",
            await SendAsync($"GET /nope?k={Key} HTTP/1.1\r\nHost: x\r\n\r\n"));

    [Fact]
    public async Task IdleConnectionsDoNotLockOutAPairedPhone()
    {
        // The connection cap made denial cheaper than the bug it replaced: sixteen sockets
        // that sent nothing kept a correctly keyed request out entirely. The head now has
        // its own short deadline, so squatters release their slots quickly.
        var squatters = new List<TcpClient>();
        try
        {
            for (int i = 0; i < 16; i++)
            {
                var idle = new TcpClient();
                await idle.ConnectAsync(IPAddress.Loopback, _port);
                squatters.Add(idle);
            }

            // With every slot held, the answer is "busy", delivered as an answer. This
            // used to accept 503 or 200 and to swallow the IOException from a reset,
            // which made it a test that could not fail: the server was resetting the
            // connection every time and the assertion passed anyway. Exactly 503, read
            // to a clean end of stream, is the thing worth pinning.
            var (busy, reset) = await ReadFullyAsync($"GET /items?k={Key} HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 503 Service Unavailable", busy);
            Assert.False(reset, "the busy answer was written and then thrown away by a reset");
            Assert.Contains("Busy, try again.", busy);

            // And squatters cannot hold the bridge: the head deadline drops them, after
            // which a paired phone gets served without anyone closing anything.
            await Task.Delay(TimeSpan.FromSeconds(8));
            string served = await SendAsync($"GET /items?k={Key} HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 200 OK", served);
        }
        finally
        {
            foreach (var idle in squatters)
                idle.Dispose();
        }
    }
}

/// <summary>
/// The share-sheet endpoint. It is a GET that changes state, which is normally poor
/// form, so the key gate and the input handling are worth pinning down.
/// </summary>
public sealed class DropShareEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-share-{Guid.NewGuid():N}");
    private readonly DropStore _store;
    private readonly DropServer _server;
    private readonly Settings _settings;
    private readonly int _port;

    public DropShareEndpointTests()
    {
        _port = DropServerLiveTests.FreePort();
        _store = new DropStore(_root);
        _settings = new Settings { DropPort = _port, DropKey = "ABCDEF0123456789ABCDEF01" };
        _server = new DropServer(_store, _settings);
        _server.Start();
    }

    public void Dispose()
    {
        _server.Stop();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<string> GetAsync(string target)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"GET {target} HTTP/1.1\r\nHost: x\r\n\r\n"));
        using var reader = new MemoryStream();
        try { await stream.CopyToAsync(reader); } catch (IOException) { }
        return Encoding.UTF8.GetString(reader.ToArray());
    }

    private string Key => _settings.DropKey;

    [Fact]
    public async Task SharingALinkFromAnotherAppStoresIt()
    {
        string shared = Uri.EscapeDataString("https://example.com/from-safari");

        string response = await GetAsync($"/share?k={Key}&text={shared}");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Link sent", response);
        var item = Assert.Single(_store.Items);
        Assert.Equal("https://example.com/from-safari", item.Text);
        Assert.Equal("link", item.Kind);
        Assert.Equal("phone", item.From);
    }

    [Fact]
    public async Task SharingPlainTextStoresItAsAMessage()
    {
        string response = await GetAsync($"/share?k={Key}&text={Uri.EscapeDataString("remember the milk")}");

        Assert.Contains("Message sent", response);
        Assert.Equal("text", Assert.Single(_store.Items).Kind);
    }

    [Fact]
    public async Task TheUrlParameterWorksTooSinceShortcutsSendsEither()
    {
        await GetAsync($"/share?k={Key}&url={Uri.EscapeDataString("https://example.com/x")}");
        Assert.Equal("https://example.com/x", Assert.Single(_store.Items).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("&text=")]
    [InlineData("&text=%20%20")]
    public async Task AnEmptyShareIsRefusedAndStoresNothing(string tail)
    {
        string response = await GetAsync($"/share?k={Key}{tail}");

        Assert.StartsWith("HTTP/1.1 400", response);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task SharingWithoutTheKeyIsRefused()
    {
        string response = await GetAsync($"/share?text={Uri.EscapeDataString("https://example.com")}");

        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task TheSetupPageCarriesTheAddressToPasteIntoShortcuts()
    {
        string response = await GetAsync($"/setup?k={Key}");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Show in Share Sheet", response);
        Assert.Contains($"/share?k={Key}&amp;text=", response);
    }

    [Fact]
    public async Task TheSetupPageIsNotServedUnpaired()
        => Assert.StartsWith("HTTP/1.1 403", await GetAsync("/setup"));

    [Fact]
    public async Task TheConfirmationSaysWhatWasSent()
    {
        string response = await GetAsync($"/share?k={Key}&text={Uri.EscapeDataString("remember the milk")}");
        Assert.Contains("remember the milk", response);
    }

    [Fact]
    public async Task SharedTextIsEscapedIntoTheConfirmation()
    {
        // The confirmation echoes the shared text, and the shared text came off the
        // network, so it has to arrive as characters rather than as markup.
        string response = await GetAsync(
            $"/share?k={Key}&text={Uri.EscapeDataString("<script>alert(1)</script>")}");

        Assert.DoesNotContain("<script>alert(1)</script>", response);
        Assert.Contains("&lt;script&gt;", response);
    }

    [Fact]
    public void ALongShareIsCutDownToAOneLineReceipt()
    {
        string preview = DropServer.Preview(new string('a', 400));

        Assert.True(preview.Length <= 96, $"preview was {preview.Length} characters");
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void AShortShareIsShownWhole()
        => Assert.Equal("remember the milk", DropServer.Preview("  remember the milk  "));

    [Fact]
    public void CuttingALongShareNeverSplitsACharacterInHalf()
    {
        // The cut counts UTF-16 units, so an emoji sitting across it used to be sliced
        // between its two halves and rendered as a replacement box.
        string preview = DropServer.Preview(new string('a', 94) + "\U0001F600" + new string('b', 40));

        // A lone half of a pair is what encodes as U+FFFD on the way to the page.
        Assert.DoesNotContain("�", preview);
        Assert.All(preview.Select((c, i) => (c, i)), pair =>
            Assert.False(
                char.IsHighSurrogate(pair.c) && (pair.i + 1 >= preview.Length || !char.IsLowSurrogate(preview[pair.i + 1])),
                "the preview ends on half a character"));
    }

    [Fact]
    public async Task ALinkCarryingItsOwnAmpersandArrivesWhole()
    {
        // Shortcuts appends the shared item raw, so this is what a YouTube link looks
        // like on the wire. Parsed as ordinary parameters it would arrive cut at "&t=".
        string response = await GetAsync(
            $"/share?k={Key}&text=https://www.youtube.com/watch?v=abc123&t=90s&list=PL1");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Equal(
            "https://www.youtube.com/watch?v=abc123&t=90s&list=PL1",
            Assert.Single(_store.Items).Text);
    }

    [Fact]
    public async Task ANoteWithAPercentSignArrivesWithThePercentSign()
    {
        // This was written as "a bare % is not refused", which could not fail:
        // Uri.UnescapeDataString does not throw on a malformed escape, so the guard it
        // tested was dead code. What is worth pinning is what the note actually says.
        string response = await GetAsync($"/share?k={Key}&text=100%25%20done%2C%20nearly%");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Equal("100% done, nearly%", Assert.Single(_store.Items).Text);
    }

    [Fact]
    public async Task AParameterEndingInTextIsNotMistakenForTheSharedItem()
    {
        // "context=" ends in "text=" without being it. The first version of this stopped
        // at that match and answered 400, so the assertion below (that the real share
        // still lands) is the half that matters.
        string response = await GetAsync($"/share?k={Key}&context=hi&text=the-real-share");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Equal("the-real-share", Assert.Single(_store.Items).Text);
    }

    [Fact]
    public async Task AQueryWithNoSharedItemAtAllIsRefused()
        => Assert.StartsWith("HTTP/1.1 400", await GetAsync($"/share?k={Key}&context=hi"));

    [Fact]
    public async Task TheKeyIsNeverStoredWhenTheShortcutPutsItLast()
    {
        // Reading to the end of the query takes whatever follows the share, and if the
        // Shortcut was assembled with the input before the key, that is the key. Stored
        // and rendered, it would parse as a link whose href carries the key offsite.
        string response = await GetAsync($"/share?text=https://news.example.com/article&k={Key}");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        var item = Assert.Single(_store.Items);
        Assert.Equal("https://news.example.com/article", item.Text);
        Assert.DoesNotContain(Key, item.Text);
        Assert.DoesNotContain(Key, response[response.IndexOf("\r\n\r\n", StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task ASharedLinkCarryingItsOwnTextParameterIsNotCutAtIt()
    {
        // Built on "url=" instead, the shared item can itself contain "&text=". Preferring
        // "text" over "url" would store "hi" and throw the link away.
        string shared = "https://example.com/doc?a=1&text=hi";
        string response = await GetAsync($"/share?k={Key}&url={shared}");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Equal(shared, Assert.Single(_store.Items).Text);
    }
}
