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

    private static int FreePort()
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

            // With every slot held, the answer is "busy" rather than a silent reset. The
            // original bug closed the connection with no response at all after 2 seconds.
            string busy = await SendAsync($"GET /items?k={Key} HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.True(busy.StartsWith("HTTP/1.1 503") || busy.StartsWith("HTTP/1.1 200"),
                $"expected an HTTP answer while slots were held, got: {(busy.Length == 0 ? "<nothing>" : busy[..Math.Min(40, busy.Length)])}");

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
