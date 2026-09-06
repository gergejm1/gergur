using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gergur.Data;
using Gergur.Diagnostics;

namespace Gergur.App;

/// <summary>
/// The phone bridge: a small server on the local network that passes links, messages
/// and files between this PC and a paired phone, in the spirit of a private chat used
/// to send things to yourself.
///
/// Deliberately a separate listener from <see cref="AgentServer"/>, which stays bound to
/// loopback. That one can run arbitrary script in logged-in sessions, so it must never
/// be reachable from the network. This one can only add to and read a list of items, and
/// every request has to carry the pairing key.
/// </summary>
public sealed class DropServer
{
    /// <summary>Refused above this, so a phone cannot fill the disk in one request.</summary>
    private const long MaxUploadBytes = 100L * 1024 * 1024;

    /// <summary>A single request may not hold a connection longer than this.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly DropStore _store;
    private readonly Settings _settings;
    /// <summary>Bounds concurrent handlers, so opening sockets cannot exhaust the process.</summary>
    private readonly SemaphoreSlim _slots = new(16, 16);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public DropServer(DropStore store, Settings settings)
    {
        _store = store;
        _settings = settings;
    }

    public bool IsRunning => _listener is not null;

    /// <summary>The address to open on the phone, or null when the server is not up.</summary>
    public string? PairingUrl => IsRunning && LocalAddress() is { } ip
        ? $"http://{ip}:{_settings.DropPort}/?k={_settings.DropKey}"
        : null;

    /// <summary>Mints the pairing key if there is not one yet. Returns it either way.</summary>
    public static string EnsureKey(Settings settings)
    {
        if (settings.DropKey.Length < 16)
        {
            settings.DropKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            settings.Save();
        }
        return settings.DropKey;
    }

    public void Start()
    {
        if (IsRunning)
            return;
        EnsureKey(_settings);
        _cts = new CancellationTokenSource();
        // Any interface: the phone reaches this over Wi-Fi, not loopback.
        _listener = new TcpListener(IPAddress.Any, _settings.DropPort);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch { }
        // Dispose as well as cancel: every on/off toggle used to leak a source and its
        // registrations.
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    /// <summary>
    /// The address to put in the pairing link: this machine on the network the phone is
    /// actually on.
    ///
    /// Taking the first private address found is wrong. VirtualBox, Hyper-V and WSL all
    /// add host-only adapters that are up and carry an RFC1918 address the phone cannot
    /// route to, and they frequently enumerate first. Having a default gateway is what
    /// separates the interface that reaches the rest of the network from one that does
    /// not, so rank on that, then prefer real Wi-Fi and Ethernet.
    /// </summary>
    internal static string? LocalAddress()
    {
        try
        {
            return RankInterfaces(NetworkInterface.GetAllNetworkInterfaces())
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                .Select(a => a.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Usable interfaces, best candidate first. Split out so it can be tested.</summary>
    internal static IEnumerable<NetworkInterface> RankInterfaces(IEnumerable<NetworkInterface> all)
        => all.Where(n => n.OperationalStatus == OperationalStatus.Up
                       && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
              .OrderByDescending(HasDefaultGateway)
              .ThenByDescending(n => n.NetworkInterfaceType
                  is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet);

    internal static bool HasDefaultGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses
                .Any(g => g.Address is { AddressFamily: AddressFamily.InterNetwork } address
                       && !address.Equals(IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>RFC1918 only: a public address here would mean the wrong interface.</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        while (!ct.IsCancellationRequested && listener is not null)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return; // Stop() was called; this is the only reason to leave the loop
            }
            catch (SocketException ex)
            {
                // A peer that resets between SYN and accept is routine. Exiting here left
                // the bridge dead for the rest of the session while the menu still said
                // it was on, with no way to tell.
                DebugLog.WriteAlways($"drop accept failed, continuing: {ex.SocketErrorCode}");
                continue;
            }
            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        // Cap concurrent handlers. Without it, opening sockets and never sending is
        // enough to exhaust the process, all before any key is checked.
        if (!await _slots.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            client.Dispose();
            return;
        }

        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? default);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            // Belt and braces with the token: these bound a blocking socket too.
            client.ReceiveTimeout = (int)RequestTimeout.TotalMilliseconds;
            client.SendTimeout = (int)RequestTimeout.TotalMilliseconds;

            using var stream = client.GetStream();
            var (request, error, buffered) = await ReadHeadAsync(stream, timeout.Token);
            if (request is null)
            {
                int status = error switch
                {
                    HeadError.Unsupported => 501,
                    HeadError.Incomplete => 431,
                    _ => 400,
                };
                await WriteAsync(stream, status, "text/plain", Encoding.UTF8.GetBytes(error.ToString()));
                return;
            }

            // One gate for everything. No key, no access, whatever the path.
            if (!request.Query.TryGetValue("k", out var key) || !KeyMatches(key))
            {
                await WriteAsync(stream, 403, "text/plain", "Not paired."u8.ToArray());
                return;
            }

            await RouteAsync(stream, request, buffered, timeout.Token);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"drop request failed: {ex.Message}");
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Fixed-time comparison of the pairing key. Length mismatch returns false by design;
    /// the key is a fixed length, so that leaks nothing an attacker did not already send.
    /// </summary>
    private bool KeyMatches(string supplied)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_settings.DropKey));

    private async Task RouteAsync(NetworkStream stream, DropRequest request, byte[] buffered, CancellationToken ct)
    {
        switch (request.Method, request.Path)
        {
            case ("GET", "/"):
                await WriteAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(DropPage.Html));
                return;

            case ("GET", "/items"):
            {
                var items = _store.Items.Select(i => new
                {
                    id = i.Id,
                    kind = i.Kind,
                    text = i.Text,
                    size = i.Size,
                    from = i.From,
                    at = i.AddedUtc,
                }).ToArray();
                await WriteAsync(stream, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(items));
                return;
            }

            case ("POST", "/send"):
            {
                var body = await ReadBodyAsync(stream, request.ContentLength, 64 * 1024, buffered, ct);
                if (body is null)
                {
                    await WriteAsync(stream, 400, "application/json", """{"error":"incomplete request"}"""u8.ToArray());
                    return;
                }
                string text = "";
                try
                {
                    using var json = JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        text = t.GetString() ?? "";
                }
                catch { }

                if (text.Trim().Length == 0)
                {
                    await WriteAsync(stream, 400, "application/json", """{"error":"empty"}"""u8.ToArray());
                    return;
                }
                _store.AddText(text, from: "phone");
                await WriteAsync(stream, 200, "application/json", """{"ok":true}"""u8.ToArray());
                return;
            }

            case ("POST", "/upload"):
            {
                if (request.ContentLength <= 0 || request.ContentLength > MaxUploadBytes)
                {
                    await WriteAsync(stream, 413, "application/json", """{"error":"too large"}"""u8.ToArray());
                    return;
                }
                // The body is the raw file. The page posts it directly rather than as
                // multipart, which keeps the parsing here trivial and the name out of band.
                request.Query.TryGetValue("name", out var name);
                var bytes = await ReadBodyAsync(stream, request.ContentLength, MaxUploadBytes, buffered, ct);
                if (bytes is null)
                {
                    // A phone that walked out of Wi-Fi range mid-transfer used to leave a
                    // truncated file recorded as a complete one, with a 200 to match.
                    await WriteAsync(stream, 400, "application/json",
                        """{"error":"transfer did not complete; nothing was saved"}"""u8.ToArray());
                    return;
                }
                using (var content = new MemoryStream(bytes))
                {
                    _store.AddFile(name ?? "file", content, from: "phone");
                }
                await WriteAsync(stream, 200, "application/json", """{"ok":true}"""u8.ToArray());
                return;
            }

            default:
                // A file download: /file/<id>. The id is looked up, never used as a path.
                if (request.Method == "GET" && request.Path.StartsWith("/file/", StringComparison.Ordinal))
                {
                    string id = request.Path["/file/".Length..];
                    if (_store.Find(id) is { IsFile: true } item
                        && _store.PathFor(item) is { } path && File.Exists(path))
                    {
                        await WriteFileAsync(stream, path, item.Text);
                        return;
                    }
                }
                await WriteAsync(stream, 404, "text/plain", "Not found."u8.ToArray());
                return;
        }
    }

    // ------------------------------------------------------------------ tiny HTTP

    internal sealed record DropRequest(
        string Method, string Path, Dictionary<string, string> Query, long ContentLength);

    /// <summary>Why a request was rejected before it reached a route.</summary>
    internal enum HeadError { None, Malformed, Incomplete, BadLength, Unsupported }

    private const int MaxHeadBytes = 16 * 1024;

    /// <summary>
    /// Parses the request line and headers. Takes a plain Stream and a token so it can be
    /// tested over a MemoryStream, and so a peer that connects and dribbles bytes cannot
    /// hold the connection open forever.
    /// </summary>
    internal static async Task<(DropRequest? Request, HeadError Error, byte[] Body)> ReadHeadAsync(
        Stream stream, CancellationToken ct = default)
    {
        var head = new MemoryStream();
        var chunk = new byte[1024];
        int terminator = -1;

        while (head.Length < MaxHeadBytes)
        {
            int read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
                break;
            head.Write(chunk, 0, read);
            terminator = head.GetBuffer().AsSpan(0, (int)head.Length).IndexOf("\r\n\r\n"u8);
            if (terminator >= 0)
                break;
        }

        // A head that never terminated is a truncated or oversized request. Parsing it
        // anyway would treat a half-received request as a complete one.
        if (terminator < 0)
            return (null, HeadError.Incomplete, []);

        // Reading in chunks overshoots the head, so any body bytes that came with it are
        // sitting in this buffer, not in the socket. Hand them back: waiting to read them
        // again is a hang, which is exactly what a POST did.
        byte[] buffered = head.GetBuffer();
        int bodyStart = terminator + 4;
        byte[] body = buffered[bodyStart..(int)head.Length];

        var lines = Encoding.UTF8.GetString(buffered, 0, terminator)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return (null, HeadError.Malformed, body);

        var start = lines[0].Split(' ');
        if (start.Length < 2 || start[1].Length == 0)
            return (null, HeadError.Malformed, body);

        string target = start[1];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int mark = target.IndexOf('?');
        string path = mark < 0 ? target : target[..mark];
        if (mark >= 0)
        {
            foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                try
                {
                    string name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
                    query[name] = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    return (null, HeadError.Malformed, body); // a bad percent escape
                }
            }
        }

        long length = 0;
        int seenLength = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
            {
                // Not implemented, and a request carrying both this and Content-Length is
                // the classic smuggling primitive. Refuse rather than guess.
                string encoding = line["Transfer-Encoding:".Length..].Trim();
                if (!encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    return (null, HeadError.Unsupported, body);
            }
            else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                seenLength++;
                // NumberStyles.None: no sign, no whitespace, no thousands separators.
                if (!long.TryParse(line["Content-Length:".Length..].Trim(),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out length))
                    return (null, HeadError.BadLength, body);
            }
        }
        if (seenLength > 1)
            return (null, HeadError.BadLength, body); // conflicting lengths: refuse

        return (new DropRequest(start[0].ToUpperInvariant(), path, query, length), HeadError.None, body);
    }

    /// <summary>
    /// Reads exactly the declared number of bytes. Returns null on a short read, so a
    /// transfer cut off midway is refused rather than stored as a complete file and
    /// answered with success.
    /// </summary>
    internal static async Task<byte[]?> ReadBodyAsync(
        Stream stream, long length, long cap, byte[]? alreadyRead = null, CancellationToken ct = default)
    {
        if (length <= 0 || length > cap)
            return null;
        var buffer = new byte[length];
        int filled = 0;

        // Whatever came in with the head belongs to the body. Skipping this and reading
        // from the socket again waits for bytes that have already been consumed.
        if (alreadyRead is { Length: > 0 })
        {
            int take = (int)Math.Min(alreadyRead.Length, length);
            alreadyRead.AsSpan(0, take).CopyTo(buffer);
            filled = take;
        }

        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
                return null; // peer went away before sending what it promised
            filled += read;
        }
        return buffer;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        string reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden",
            404 => "Not Found", 413 => "Payload Too Large", _ => "Error",
        };
        // no-referrer keeps the pairing key out of any Referer a future link might send.
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\n"
            + $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\n"
            + "Referrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    private static async Task WriteFileAsync(NetworkStream stream, string path, string downloadName)
    {
        // Open before measuring and before writing headers. Taking the length from a
        // FileInfo and then opening leaves a window where eviction deletes the file, and
        // the phone has already been promised that many bytes.
        using var file = File.OpenRead(path);

        // Strip anything that could break out of the header value or forge a new one.
        string safe = new string(downloadName
            .Where(c => c is not ('"' or '\r' or '\n' or '\0') && !char.IsControl(c))
            .ToArray());
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n"
            + $"Content-Disposition: attachment; filename=\"{safe}\"\r\n"
            + $"Content-Length: {file.Length}\r\nReferrer-Policy: no-referrer\r\n"
            + "X-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await file.CopyToAsync(stream);
    }
}
