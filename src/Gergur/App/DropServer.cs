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

    /// <summary>
    /// A peer gets far less time to send its request line and headers than to finish a
    /// transfer. Without this split, sockets that connect and say nothing hold their slot
    /// for the full request timeout, which made the connection cap a cheaper denial than
    /// having no cap at all: sixteen idle sockets locked out a correctly paired phone.
    /// </summary>
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Uploads stream to disk in chunks this size, never sized from a declared length.</summary>
    private const int CopyChunkBytes = 64 * 1024;

    private readonly DropStore _store;
    private readonly Settings _settings;
    /// <summary>Bounds concurrent handlers, so opening sockets cannot exhaust the process.</summary>
    private readonly SemaphoreSlim _slots = new(16, 16);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _inFlight;

    /// <summary>
    /// Handlers that have not finished. Not the same as slots in use: the slot is given
    /// back before the connection is closed, so this is the only way to see a handler
    /// still holding a socket after its answer went out.
    /// </summary>
    internal int InFlight => Volatile.Read(ref _inFlight);

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

    /// <exception cref="SocketException">
    /// The port is already taken. Left to the caller to report: silently ending up with a
    /// bridge that is off while the menu says it is on is how this becomes a mystery.
    /// </exception>
    public void Start()
    {
        if (IsRunning)
            return;
        EnsureKey(_settings);
        var cts = new CancellationTokenSource();
        // Any interface: the phone reaches this over Wi-Fi, not loopback.
        var listener = new TcpListener(IPAddress.Any, _settings.DropPort);
        try
        {
            listener.Start();
        }
        catch
        {
            // Nothing is published unless the listen succeeded, so a failed Start leaves
            // IsRunning false rather than claiming a bridge that is not there.
            cts.Dispose();
            throw;
        }
        _cts = cts;
        _listener = listener;
        _ = AcceptLoopAsync(cts.Token);
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

    /// <summary>
    /// Ends a connection so the peer reads the answer instead of a reset.
    ///
    /// Shutting down both directions while the peer's request bytes are still sitting
    /// unread in the receive queue makes Windows send RST on close, and the reply we
    /// just wrote is discarded with it. That is what a phone hitting the busy cap saw:
    /// "connection forcibly closed" rather than "busy, try again". Closing only the send
    /// side and then draining to EOF leaves nothing unread, so the close is a clean FIN.
    /// </summary>
    private static async Task CloseGracefullyAsync(TcpClient client, NetworkStream stream)
    {
        try { client.Client.Shutdown(SocketShutdown.Send); } catch { return; }

        // Bounded: a peer that keeps talking must not hold the socket open. Whatever is
        // still coming is a request we have already refused, so it is read and dropped.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var sink = new byte[4096];
        try
        {
            while (await stream.ReadAsync(sink, deadline.Token) > 0)
            {
            }
        }
        catch
        {
            // Timed out or the peer reset first. Either way there is nothing left to do.
        }
    }

    /// <summary>
    /// A cancellation source that also trips when <see cref="Stop"/> runs.
    ///
    /// Reading .Token off the field directly is a race: Stop() can dispose the source
    /// between the two reads, and the throw used to escape before the slot was taken,
    /// so every occurrence leaked a permit off the connection cap permanently. A
    /// disposed source means we are shutting down, and a plain source is the right
    /// answer for the request already in hand.
    /// </summary>
    internal static CancellationTokenSource LinkedTimeout(CancellationTokenSource? lifetime)
    {
        if (lifetime is null)
            return new CancellationTokenSource();
        try
        {
            return CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }
        catch (ObjectDisposedException)
        {
            return new CancellationTokenSource();
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            await ServeAsync(client);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        // Cap concurrent handlers, but say so rather than closing silently: a phone that
        // arrives while the cap is full should be told to retry, not left guessing.
        if (!await _slots.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            try
            {
                using var busy = client.GetStream();
                await WriteAsync(busy, 503, "text/plain", "Busy, try again."u8.ToArray());
                await busy.FlushAsync();
                await CloseGracefullyAsync(client, busy);
            }
            catch { }
            client.Dispose();
            return;
        }

        using var _ = client;
        // Both held outside the try so the exit path can still answer and still release
        // the slot. Nothing between the slot being taken and this try may throw.
        NetworkStream? stream = null;
        CancellationTokenSource? timeout = null;
        DropRequest? request = null;
        var bodyRead = new BodyRead();
        try
        {
            timeout = LinkedTimeout(_cts);
            // Belt and braces with the token: these bound a blocking socket too.
            client.ReceiveTimeout = (int)RequestTimeout.TotalMilliseconds;
            client.SendTimeout = (int)RequestTimeout.TotalMilliseconds;

            stream = client.GetStream();

            // The head gets a short deadline of its own. A peer holding a slot without
            // sending anything is released in seconds rather than half a minute.
            timeout.CancelAfter(HeadTimeout);
            var (parsed, error, buffered) = await ReadHeadAsync(stream, timeout.Token);
            request = parsed;
            timeout.CancelAfter(RequestTimeout); // a real transfer gets the full budget
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
            //
            // Read out of the raw query rather than the parsed dictionary, which is
            // last-wins: the share sheet appends a link to the end of the address, and a
            // link with its own "k=" (a maps or search url) then replaced the pairing key
            // with the site's parameter and the share came back "Not paired".
            if (KeyFrom(request.RawQuery) is not { } key || !KeyMatches(key))
            {
                await WriteAsync(stream, 403, "text/plain", "Not paired."u8.ToArray());
                return;
            }

            await RouteAsync(stream, request, buffered, bodyRead, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Ran out of time. Say so rather than closing silently: an empty read is
            // indistinguishable from a crash, and the phone has no way to tell why.
            try
            {
                if (stream is not null)
                    await WriteAsync(stream, 408, "text/plain", "Request timed out."u8.ToArray());
            }
            catch { }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"drop request failed: {ex.Message}");
        }
        finally
        {
            // Released first: nothing below may cost a permit, and the drain can take
            // a quarter of a second.
            _slots.Release();

            // A body we never read is what turns the close into a reset, which throws
            // away the answer we just wrote: a 413 on an oversized upload arrives at the
            // phone as a network error rather than a reason. Drain only in that case.
            // Draining unconditionally held the connection for the full deadline after
            // every ordinary request, which is a slot off the cap for nothing.
            if (stream is not null)
            {
                if (request is null || (request.ContentLength > 0 && !bodyRead.Done))
                    await CloseGracefullyAsync(client, stream);
                stream.Dispose();
            }
            timeout?.Dispose();
        }
    }

    /// <summary>
    /// A glance at what was just sent, so the page the share sheet flashes says which
    /// thing landed rather than only that something did. Long text is cut: this is a one
    /// line receipt, not the item itself, which is on the PC by then.
    /// </summary>
    internal static string Preview(string text)
    {
        text = text.Trim();
        if (text.Length <= 96)
            return text;

        // Back off a cut that lands between the halves of a surrogate pair, or the
        // emoji you shared renders as a replacement box in the confirmation.
        int cut = 95;
        if (char.IsLowSurrogate(text[cut]))
            cut--;
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// Fixed-time comparison of the pairing key. Length mismatch returns false by design;
    /// the key is a fixed length, so that leaks nothing an attacker did not already send.
    /// </summary>
    private bool KeyMatches(string supplied)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_settings.DropKey));

    /// <summary>
    /// Whether the route read the body the request declared. The close path needs to know:
    /// a body left unread is what turns a close into a reset, and draining when there is
    /// nothing to drain costs a connection slot the full drain deadline for no reason.
    /// </summary>
    private sealed class BodyRead
    {
        public bool Done;
    }

    private async Task RouteAsync(
        NetworkStream stream, DropRequest request, byte[] buffered, BodyRead bodyRead, CancellationToken ct)
    {
        switch (request.Method, request.Path)
        {
            case ("GET", "/"):
                await WriteAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(DropPage.Html));
                return;

            case ("GET", "/share"):
            {
                // Deliberately a GET that changes state, which is normally poor form.
                // The reason: an iOS share-sheet Shortcut built by hand is one action
                // for a GET and half a dozen fiddly ones for a POST, and there is no
                // ambient authority here to forge. Nothing is a cookie, nothing is a
                // session; the pairing key is the only credential and a page that does
                // not have it cannot make this request either way.
                string? shared = SharedValue(request.RawQuery);
                if (shared is not null)
                    shared = StripKey(shared, _settings.DropKey);

                if (string.IsNullOrWhiteSpace(shared))
                {
                    await WriteAsync(stream, 400, "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes(DropPage.Result("Nothing to send", "The share arrived empty.")));
                    return;
                }
                var item = _store.AddText(shared, from: "phone");
                await WriteAsync(stream, 200, "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(DropPage.Result(
                        item.Kind == "link" ? "Link sent" : "Message sent", Preview(item.Text))));
                return;
            }

            case ("GET", "/setup"):
                await WriteAsync(stream, 200, "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(DropPage.Setup(
                        _settings.DropKey, HostFor(request.Host, LocalAddress), _settings.DropPort)));
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
                bodyRead.Done = body is not null;
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
                if (request.ContentLength <= 0)
                {
                    // Folding this into the size check told the phone a zero byte file
                    // was too large, which is not a thing anyone can act on.
                    await WriteAsync(stream, 400, "application/json",
                        """{"error":"that file is empty"}"""u8.ToArray());
                    return;
                }
                if (request.ContentLength > MaxUploadBytes)
                {
                    await WriteAsync(stream, 413, "application/json", """{"error":"too large"}"""u8.ToArray());
                    return;
                }
                // The body is the raw file. The page posts it directly rather than as
                // multipart, which keeps the parsing here trivial and the name out of band.
                request.Query.TryGetValue("name", out var name);

                // Straight to a temp file in chunks, so nothing is sized from a declared
                // length and a failed transfer leaves nothing behind.
                string staging = Path.Combine(Path.GetTempPath(), $"gergur-drop-{Guid.NewGuid():N}");
                try
                {
                    bool complete = await CopyBodyToFileAsync(
                        stream, request.ContentLength, MaxUploadBytes, buffered, staging, ct);
                    bodyRead.Done = complete;
                    if (!complete)
                    {
                        // A phone that walked out of Wi-Fi range mid-transfer used to leave
                        // a truncated file recorded as a complete one, with a 200 to match.
                        await WriteAsync(stream, 400, "application/json",
                            """{"error":"transfer did not complete; nothing was saved"}"""u8.ToArray());
                        return;
                    }
                    _store.AddFileFromPath(name ?? "file", staging, from: "phone");
                }
                finally
                {
                    try { File.Delete(staging); } catch { }
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

    /// <param name="RawQuery">
    /// The query exactly as it arrived, undecoded and unsplit. The share sheet needs it:
    /// see <see cref="SharedValue"/>.
    /// </param>
    /// <param name="Host">The Host header as sent, port and all, or empty.</param>
    internal sealed record DropRequest(
        string Method, string Path, Dictionary<string, string> Query, long ContentLength,
        string RawQuery, string Host);

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
        string rawQuery = mark < 0 ? "" : target[(mark + 1)..];
        foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            query[Decode(equals < 0 ? pair : pair[..equals])] =
                equals < 0 ? "" : Decode(pair[(equals + 1)..]);
        }

        long length = 0;
        int seenLength = 0;
        string host = "";
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                host = line["Host:".Length..].Trim();
            }
            else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
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

        return (new DropRequest(start[0].ToUpperInvariant(), path, query, length, rawQuery, host),
                HeadError.None, body);
    }

    /// <summary>
    /// The address to print on the setup page: the one the phone actually reached us on,
    /// which the request itself proves works. Guessing from the machine's own adapters
    /// can name an interface the phone cannot route to, and on a PC with more than one
    /// gatewayed adapter that guess is a coin toss. Falls back to the guess, and to null
    /// when there is nothing to offer, which the page says out loud rather than printing
    /// a placeholder that looks like an address.
    /// </summary>
    internal static string? HostFor(string hostHeader, Func<string?> fallback)
        => HostFor(hostHeader, fallback, OwnAddresses);

    /// <param name="own">
    /// This machine's addresses. The header is only accepted when it names one of them.
    /// Taking it on trust prints whatever the peer sent into an address the setup page
    /// tells you to bake into a Shortcut permanently, with the pairing key attached, so
    /// one request from anything holding the key redirects every future share to a
    /// stranger's server. Past the key gate, but the key is the only thing being guarded.
    /// </param>
    internal static string? HostFor(string hostHeader, Func<string?> fallback, Func<IEnumerable<string>> own)
    {
        string host = hostHeader.Trim();

        // Strip the port. The last colon separates one only after the closing bracket of
        // an IPv6 literal, or, when there is no bracket, when it is the only colon: a
        // bare "fe80::1" is an address, not a host and a port.
        int bracket = host.LastIndexOf(']');
        int colon = host.LastIndexOf(':');
        if (colon > bracket && (bracket >= 0 || colon == host.IndexOf(':')))
            host = host[..colon];

        if (host.Length == 0)
            return fallback();

        string bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (!IPAddress.TryParse(bare, out var address))
            return fallback();   // a name, not an address: nothing here can confirm it
        if (IPAddress.IsLoopback(address))
            return fallback();   // the PC talking to itself says nothing about the phone

        try
        {
            if (!own().Contains(address.ToString(), StringComparer.OrdinalIgnoreCase))
                return fallback();
        }
        catch
        {
            return fallback();
        }
        return host;
    }

    /// <summary>Every unicast address on this machine, as text.</summary>
    private static IEnumerable<string> OwnAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address.ToString())
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Decodes a query value. Percent escapes that are not valid escapes are left as the
    /// characters they are: <see cref="Uri.UnescapeDataString"/> is lenient about them,
    /// which is what a note reading "100% done" needs.
    /// </summary>
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    /// <summary>
    /// Where a named parameter starts in the raw query, or -1.
    ///
    /// The name has to actually start a parameter, and the scan continues past matches
    /// that do not: "context=" ends in "text=" without being it, and stopping at the
    /// first match threw away the real share that followed it.
    /// </summary>
    internal static int MarkerIndex(string rawQuery, string name)
    {
        string marker = name + "=";
        for (int from = 0; from + marker.Length <= rawQuery.Length; )
        {
            int mark = rawQuery.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);
            if (mark < 0)
                return -1;
            if (mark == 0 || rawQuery[mark - 1] == '&')
                return mark;
            from = mark + 1;
        }
        return -1;
    }

    /// <summary>
    /// The pairing key as sent: the first "k=" that starts a parameter, up to the next
    /// "&amp;". Not from the parsed query, because that is last-wins and the shared item
    /// is appended raw, so a shared link carrying its own "k=" would decide who is paired.
    /// </summary>
    internal static string? KeyFrom(string rawQuery)
    {
        int mark = MarkerIndex(rawQuery, "k");
        if (mark < 0)
            return null;
        int start = mark + 2;
        int end = rawQuery.IndexOf('&', start);
        return Decode(end < 0 ? rawQuery[start..] : rawQuery[start..end]);
    }

    /// <summary>
    /// What the share sheet sent: everything from "text=" or "url=" to the end of the
    /// query, undivided.
    ///
    /// Shortcuts appends the shared item to the address as it is, so a link carrying its
    /// own "&amp;" (most YouTube and Maps links do) splits into extra parameters and would
    /// otherwise arrive cut off at the first one. Reading to the end takes the whole thing.
    ///
    /// Whichever marker comes first wins, rather than "text" being preferred. A shared
    /// link can contain "&amp;text=" of its own, so with a Shortcut built on "url=",
    /// preferring "text" would return the tail of the link instead of the link.
    /// </summary>
    internal static string? SharedValue(string rawQuery)
    {
        int text = MarkerIndex(rawQuery, "text");
        int link = MarkerIndex(rawQuery, "url");
        int first = text < 0 ? link : link < 0 ? text : Math.Min(text, link);
        int second = text < 0 || link < 0 ? -1 : Math.Max(text, link);

        // A Shortcut set to accept both Text and URLs, which is what step 6 of the setup
        // page tells you to do, sends both parameters and leaves one of them empty. An
        // empty first marker is not the share; the other one is.
        return At(first) ?? At(second);

        string? At(int mark)
        {
            if (mark < 0)
                return null;
            int start = rawQuery.IndexOf('=', mark) + 1;

            // An empty parameter. Reading to the end would otherwise hand back the rest
            // of the query ("&text=my note") and store it verbatim.
            if (start >= rawQuery.Length || rawQuery[start] == '&')
                return null;

            string value = Decode(rawQuery[start..]);
            return value.Length == 0 ? null : value;
        }
    }

    /// <summary>
    /// Removes the pairing key from something read to the end of the query.
    ///
    /// Reading to the end takes whatever follows, and if the Shortcut was built with the
    /// shared item before the key rather than after it, what follows is the key. It would
    /// then be stored in the drop, shown on screen, and (since the result still parses as
    /// a url) rendered as a link whose href carries the key to whatever site it points
    /// at, putting it in a stranger's access log. Nobody hostile is needed for that, only
    /// a Shortcut assembled in the other order, so the key is cut wherever it appears
    /// rather than only at the end.
    /// </summary>
    internal static string StripKey(string shared, string key)
        => key.Length == 0 ? shared : shared.Replace("&k=" + key, "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies exactly the declared number of body bytes into a file, in fixed chunks.
    ///
    /// Never sizes a buffer from what the peer claims. Allocating the declared length up
    /// front meant sixteen sockets promising 100 MB and sending nothing took the process
    /// from 7 MB to 1.6 GB, in a browser whose whole point is its memory behaviour.
    /// Returns false when the transfer did not complete, and the caller keeps nothing.
    /// </summary>
    internal static async Task<bool> CopyBodyToFileAsync(
        Stream stream, long length, long cap, byte[] alreadyRead, string path, CancellationToken ct = default)
    {
        if (length <= 0 || length > cap)
            return false;

        using var file = File.Create(path);
        long written = 0;

        if (alreadyRead.Length > 0)
        {
            int take = (int)Math.Min(alreadyRead.Length, length);
            await file.WriteAsync(alreadyRead.AsMemory(0, take), ct);
            written = take;
        }

        var chunk = new byte[CopyChunkBytes];
        while (written < length)
        {
            int want = (int)Math.Min(chunk.Length, length - written);
            int read = await stream.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read == 0)
                return false; // peer stopped before sending what it promised
            await file.WriteAsync(chunk.AsMemory(0, read), ct);
            written += read;
        }
        return true;
    }

    /// <summary>
    /// Reads exactly the declared number of bytes into memory, for bodies small enough to
    /// hold. Returns null on a short read, so a transfer cut off midway is refused rather
    /// than stored as a complete one and answered with success.
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
            404 => "Not Found", 408 => "Request Timeout", 413 => "Payload Too Large",
            431 => "Request Header Fields Too Large", 501 => "Not Implemented",
            503 => "Service Unavailable", _ => "Error",
        };
        // no-referrer keeps the pairing key out of any Referer a future link might send.
        // The pages are self-contained, so the policy can say so: nothing loads from
        // anywhere, the only requests go back here, and no other page may frame this one
        // and read the key out of its url.
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\n"
            + $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\n"
            + "Referrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n"
            + "X-Frame-Options: DENY\r\n"
            + "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; "
            + "style-src 'unsafe-inline'; connect-src 'self'; form-action 'none'; "
            + "frame-ancestors 'none'\r\n"
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
