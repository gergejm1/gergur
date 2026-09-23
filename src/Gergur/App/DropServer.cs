using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
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

    /// <summary>
    /// The outside edge for one request, once its head has been read. Generous, because
    /// the cap on a single item is 100 MB and a phone across the house does not move that
    /// in thirty seconds. What actually ends a dead transfer is <see cref="StallTimeout"/>,
    /// applied per chunk; this only stops one that trickles forever.
    /// </summary>
    internal TimeSpan TransferTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A peer gets far less time to send its request line and headers than to finish a
    /// transfer. Without this split, sockets that connect and say nothing hold their slot
    /// for the full request timeout, which made the connection cap a cheaper denial than
    /// having no cap at all: sixteen idle sockets locked out a correctly paired phone.
    /// </summary>
    internal TimeSpan HeadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Uploads stream to disk in chunks this size, never sized from a declared length.</summary>
    private const int CopyChunkBytes = 64 * 1024;

    /// <summary>
    /// How long a transfer may make no progress at all. Applies per chunk rather than to
    /// the whole body, so a slow phone finishes a large photo and a stalled one does not
    /// hold its connection for the full request budget.
    /// </summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The default, for the static copy helpers that take it as a parameter.</summary>
    private static readonly TimeSpan DefaultStall = TimeSpan.FromSeconds(20);

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
    public static string EnsureKey(Settings settings) => EnsureKey(settings, settings.Save);

    /// <summary>
    /// The same, saving through <paramref name="save"/>, so a test can point it at a file of
    /// its own. Settings.Save() writes the real one, and the pairing key in it has been lost
    /// to a test once already.
    /// </summary>
    internal static string EnsureKey(Settings settings, Func<bool> save)
    {
        if (settings.DropKey.Length < 16)
        {
            string previous = settings.DropKey;
            settings.DropKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            bool saved;
            try
            {
                saved = save();
            }
            catch
            {
                // A write that failed leaves the same unkept key as a refusal.
                settings.DropKey = previous;
                throw;
            }
            if (!saved)
            {
                // A key that is not written down is gone at the next start, and a phone
                // paired with it is then refused with no explanation. Refused here instead,
                // which StartPhoneBridge reports as why the drop is not running.
                settings.DropKey = previous;
                throw new InvalidOperationException(
                    "The pairing key could not be saved, because the settings file could not be read when Gergur started. Restart Gergur and try again.");
            }
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
                await WriteRawAsync(busy, 503, "text/plain", "Busy, try again."u8.ToArray());
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
        var exchange = new Exchange();
        try
        {
            timeout = LinkedTimeout(_cts);
            // These bound blocking socket calls only, and every path here is async, so
            // they are a backstop for nothing in practice. Left set because they cost
            // nothing and a future synchronous call would otherwise have no bound at all.
            client.ReceiveTimeout = (int)TransferTimeout.TotalMilliseconds;
            client.SendTimeout = (int)TransferTimeout.TotalMilliseconds;

            stream = client.GetStream();

            // The head gets a short deadline of its own. A peer holding a slot without
            // sending anything is released in seconds rather than half a minute.
            timeout.CancelAfter(HeadTimeout);
            var (parsed, error, buffered) = await ReadHeadAsync(stream, timeout.Token);
            request = parsed;
            timeout.CancelAfter(TransferTimeout); // a real transfer gets the full budget
            if (request is null)
            {
                int status = error switch
                {
                    HeadError.Unsupported => 501,
                    HeadError.Incomplete => 431,
                    _ => 400,
                };
                await WriteAsync(stream, exchange, status, "text/plain", Encoding.UTF8.GetBytes(error.ToString()));
                return;
            }

            // One gate for everything. No key, no access, whatever the path.
            //
            // Read out of the raw query rather than the parsed dictionary, which is
            // last-wins: the share sheet appends a link to the end of the address, and a
            // link with its own "k=" (a maps or search url) then replaced the pairing key
            // with the site's parameter and the share came back "Not paired".
            var expected = Encoding.UTF8.GetBytes(_settings.DropKey);
            if (!KeysFrom(request.RawQuery).Any(k => Matches(k, expected)))
            {
                await WriteAsync(stream, exchange, 403, "text/plain", "Not paired."u8.ToArray());
                return;
            }

            await RouteAsync(stream, request, buffered, exchange, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Ran out of time. Say so rather than closing silently: an empty read is
            // indistinguishable from a crash, and the phone has no way to tell why.
            await TryFailAsync(stream, exchange, 408, "text/plain", "Request timed out."u8.ToArray());
        }
        catch (Exception ex)
        {
            DebugLog.Write($"drop request failed: {ex.Message}");
            // Say something. A full disk or a permission error inside the store used to
            // close the connection with no answer at all, which the phone shows as a
            // network failure: the same "an empty read is indistinguishable from a crash"
            // the 408 above exists for.
            await TryFailAsync(stream, exchange, 500, "application/json",
                """{"error":"the PC could not finish that"}"""u8.ToArray());
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
                if (request is null || (request.ContentLength > 0 && !exchange.BodyRead))
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
    /// A name for something that arrived without one, from its first bytes and the time.
    /// The alternative is a drop full of items called "file", which is what a photo
    /// shared from the iPhone share sheet gives you: the Shortcut has no filename to put
    /// in the url, so the only thing that knows what this is, is the content.
    /// </summary>
    internal static string NameFromContent(string path)
    {
        string extension = ".bin";
        string kind = "File";
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            int read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            (extension, kind) = Sniff(head[..read]);
        }
        catch
        {
            // Unreadable is not worth failing the upload over; it keeps the plain name.
        }
        // Invariant, because this is meant to be a date: the Thai and Umm al-Qura
        // calendars render the same instant as 2569 and 1448.
        return $"{kind} {DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture)}{extension}";
    }

    /// <summary>
    /// The eight bytes every PNG starts with, written as bytes on purpose. As a string
    /// escape this is silently wrong: "\x89" is the character U+0089, which UTF-8 encodes
    /// as two bytes, so the literal never matched a real PNG and every iPhone screenshot
    /// arrived as an unopenable ".bin". The test that covered it fed the same broken
    /// literal back in and agreed with the bug.
    /// </summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>ISO base media brands that mean a still picture, and ones that mean video.</summary>
    private static readonly HashSet<string> PhotoBrands =
        new(StringComparer.Ordinal) { "heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1", "avif", "avis" };

    /// <summary>
    /// The video brands. Long, because the fallback is a file that opens nothing: the
    /// short version turned an ordinary web MP4 into ".bin", which is a worse answer than
    /// the guess it replaced.
    /// </summary>
    private static readonly HashSet<string> VideoBrands = new(StringComparer.Ordinal)
    {
        "qt  ", "isom", "iso2", "iso4", "iso5", "iso6", "mp41", "mp42", "mp4v", "avc1",
        "M4V ", "M4VP", "mmp4", "dash", "3gp4", "3gp5", "3gp6", "3g2a", "3g2b",
    };

    /// <summary>The same container carrying sound. A voice memo is one of these.</summary>
    private static readonly HashSet<string> AudioBrands =
        new(StringComparer.Ordinal) { "M4A ", "M4B ", "M4P " };

    /// <summary>
    /// What a file starts with, for the handful of things a phone actually sends. Only
    /// signatures that are unambiguous: anything else keeps the neutral name, because a
    /// wrong extension is worse than no extension.
    /// </summary>
    internal static (string Extension, string Kind) Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return (".jpg", "Photo");
        if (head.StartsWith(PngSignature))
            return (".png", "Photo");
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return (".gif", "Photo");
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return (".webp", "Photo");
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            // One container, many things inside it, and only the brand says which. Listed
            // rather than assumed: treating everything that was not a known photo brand
            // as video called an AVIF picture and a voice memo ".mov", and neither opens.
            string brand = Encoding.ASCII.GetString(head[8..12]);
            if (PhotoBrands.Contains(brand))
                return (".heic", "Photo");
            if (VideoBrands.Contains(brand))
                return (".mov", "Video");
            if (AudioBrands.Contains(brand))
                return (".m4a", "Audio");
            return (".bin", "File");
        }
        if (head.StartsWith("%PDF-"u8))
            return (".pdf", "Document");
        return (".bin", "File");
    }

    /// <summary>
    /// Fixed-time comparison against the key, encoded once by the caller: a query can
    /// carry thousands of "k=" values and each one used to encode the key again.
    /// Length mismatch returns false by design; the key is a fixed length, so that leaks
    /// nothing an attacker did not already send.
    /// </summary>
    private static bool Matches(string supplied, byte[] expected)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), expected);

    /// <summary>What happened to one request, for the paths that run after it.</summary>
    internal sealed class Exchange
    {
        /// <summary>
        /// Whether the route read the body the request declared. A body left unread is
        /// what turns a close into a reset, and draining when there is nothing to drain
        /// costs a connection slot the full drain deadline for no reason.
        /// </summary>
        public bool BodyRead;

        /// <summary>
        /// Whether any bytes of a response have gone out. Once they have, this connection
        /// can no longer carry an error: a download that fails halfway is already a
        /// stream of file bytes, and writing "HTTP/1.1 408" onto the end of it puts a
        /// response header inside the photo the phone is saving. A truncated file is
        /// honest; a corrupted one is not.
        /// </summary>
        public bool Responded;
    }

    /// <summary>
    /// Reports a failure, but only while the connection can still carry one.
    ///
    /// Once a response has started there is no way to say "actually that failed": the
    /// bytes already sent are a file, and appending a status line writes an HTTP header
    /// into the middle of it. Closing on the phone mid-download leaves a short file,
    /// which every client can tell is short. That is the better of the two.
    ///
    /// The write gets a deadline of its own. The failure this most often follows is a
    /// peer that stopped reading, and writing to one of those blocks until the operating
    /// system gives up, which is minutes with a connection held the whole time.
    /// </summary>
    internal static async Task TryFailAsync(
        Stream? stream, Exchange exchange, int status, string contentType, byte[] body)
    {
        if (stream is null || exchange.Responded)
            return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await WriteRawAsync(stream, status, contentType, body, deadline.Token);
        }
        catch
        {
            // The peer is gone or will not read. There is nothing left to tell it.
        }
    }

    /// <summary>
    /// Internal so a test can drive it over a stream that fails partway through a body,
    /// which is the one thing a socket test cannot arrange: by the time a real download
    /// fails, the peer is usually gone and cannot observe what is written after it.
    /// </summary>
    internal async Task RouteAsync(
        Stream stream, DropRequest request, byte[] buffered, Exchange exchange, CancellationToken ct)
    {
        switch (request.Method, request.Path)
        {
            case ("GET", "/"):
                await Respond(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(DropPage.Html));
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
                    await Respond(400, "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes(DropPage.Result("Nothing to send", "The share arrived empty.")));
                    return;
                }
                var item = _store.AddText(shared, from: "phone");
                await Respond(200, "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(DropPage.Result(
                        item.Kind == "link" ? "Link sent" : "Message sent", Preview(item.Text))));
                return;
            }

            case ("GET", "/setup"):
                await Respond(200, "text/html; charset=utf-8",
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
                await Respond(200, "application/json", JsonSerializer.SerializeToUtf8Bytes(items));
                return;
            }

            case ("POST", "/send"):
            {
                var body = await ReadBodyAsync(stream, request.ContentLength, 64 * 1024, buffered, ct, StallTimeout);
                exchange.BodyRead = body is not null;
                if (body is null)
                {
                    await Respond(400, "application/json", """{"error":"incomplete request"}"""u8.ToArray());
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
                    await Respond(400, "application/json", """{"error":"empty"}"""u8.ToArray());
                    return;
                }
                _store.AddText(text, from: "phone");
                await Respond(200, "application/json", """{"ok":true}"""u8.ToArray());
                return;
            }

            case ("POST", "/upload"):
            {
                if (request.ContentLength <= 0)
                {
                    // Folding this into the size check told the phone a zero byte file
                    // was too large, which is not a thing anyone can act on.
                    await Respond(400, "application/json",
                        """{"error":"that file is empty"}"""u8.ToArray());
                    return;
                }
                if (request.ContentLength > MaxUploadBytes)
                {
                    await Respond(413, "application/json", """{"error":"too large"}"""u8.ToArray());
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
                        stream, request.ContentLength, MaxUploadBytes, buffered, staging, ct, StallTimeout);
                    exchange.BodyRead = complete;
                    if (!complete)
                    {
                        // A phone that walked out of Wi-Fi range mid-transfer used to leave
                        // a truncated file recorded as a complete one, with a 200 to match.
                        await Respond(400, "application/json",
                            """{"error":"transfer did not complete; nothing was saved"}"""u8.ToArray());
                        return;
                    }
                    // A share-sheet Shortcut posting a photo has no filename to send:
                    // Photos does not give one to the shortcut, and a url can only carry
                    // what the Shortcut can put in it. Everything would land as "file",
                    // so name it from what it turns out to be.
                    _store.AddFileFromPath(
                        string.IsNullOrWhiteSpace(name) ? NameFromContent(staging) : name,
                        staging,
                        from: "phone");
                }
                finally
                {
                    try { File.Delete(staging); } catch { }
                }
                await Respond(200, "application/json", """{"ok":true}"""u8.ToArray());
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
                        await SendFile(path, item.Text);
                        return;
                    }
                }
                await Respond(404, "text/plain", "Not found."u8.ToArray());
                return;
        }

        // Every write in this method goes through one of these two, so that the handler
        // above knows whether this connection still has room for an error response. The
        // two replies it writes before reaching here set the flag themselves.
        // With the token: without it every response wrote under no deadline at all, so a
        // keyed peer that stopped reading a large /items answer wedged a connection slot.
        Task Respond(int status, string contentType, byte[] body)
            => WriteAsync(stream, exchange, status, contentType, body, ct, StallTimeout);

        async Task SendFile(string path, string downloadName)
        {
            // Opened before the flag is set, because opening is what fails when a scanner
            // or a backup is holding the file, and nothing has gone out at that point:
            // this request can still be answered with an error.
            using var file = File.OpenRead(path);
            exchange.Responded = true;
            await WriteFileAsync(stream, file, downloadName, StallTimeout, ct);
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

        // Back into brackets if it needs them: an IPv6 address written bare turns
        // "http://{host}:{port}/" into something no browser can parse.
        // A zone id ("fe80::1%12") needs percent escaping to survive in a url, and the
        // phone is not on this machine's link-local scope anyway, so it is dropped.
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return host;
        string bare6 = host.Trim('[', ']');
        int zone = bare6.IndexOf('%');
        return $"[{(zone < 0 ? bare6 : bare6[..zone])}]";
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
    internal static int MarkerIndex(string rawQuery, string name, int start = 0)
    {
        string marker = name + "=";
        for (int from = start; from + marker.Length <= rawQuery.Length; )
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
    /// Every "k=" that starts a parameter, in order.
    ///
    /// All of them, because there is no position that is reliably ours. The share sheet
    /// appends the shared item to the address raw, and a shared link can carry a "k=" of
    /// its own: an Amazon search url does. Whether that one lands before or after the
    /// pairing key depends only on which end of the address the Shortcut puts the input,
    /// so taking the last matched the link and taking the first matched the link in the
    /// other ordering. Both were wrong; only comparing them all is not.
    ///
    /// It does not weaken the gate. A caller still has to send the real key somewhere,
    /// and extra values that are not it buy nothing. It does turn one request into as
    /// many guesses as fit in a 16 KB head, on the order of five thousand, where before
    /// it was one. Against 96 bits of key that is not a number that matters.
    /// </summary>
    internal static IEnumerable<string> KeysFrom(string rawQuery)
    {
        for (int from = 0; from < rawQuery.Length; )
        {
            int mark = MarkerIndex(rawQuery, "k", from);
            if (mark < 0)
                yield break;

            int start = mark + 2;
            int end = rawQuery.IndexOf('&', start);
            yield return Decode(end < 0 ? rawQuery[start..] : rawQuery[start..end]);
            if (end < 0)
                yield break;
            from = end + 1;
        }
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
        Stream stream, long length, long cap, byte[] alreadyRead, string path,
        CancellationToken ct = default, TimeSpan? stallAfter = null)
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
            // Per chunk, not per transfer: a large photo from a slow phone is allowed to
            // take its time, and one that has stopped moving is not.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(stallAfter ?? DefaultStall);
            int read = await stream.ReadAsync(chunk.AsMemory(0, want), stall.Token);
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
        Stream stream, long length, long cap, byte[]? alreadyRead = null,
        CancellationToken ct = default, TimeSpan? stallAfter = null)
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
            // The same per-read bound the file paths have. Without it this loop was held
            // open by the whole transfer budget, which is ten minutes and a connection
            // slot, for a peer that sent a Content-Length and then went quiet.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(stallAfter ?? DefaultStall);
            int read = await stream.ReadAsync(buffer.AsMemory(filled), stall.Token);
            if (read == 0)
                return null; // peer went away before sending what it promised
            filled += read;
        }
        return buffer;
    }

    /// <summary>
    /// Writes a response and records that one has begun.
    ///
    /// The recording happens here rather than at the call sites because three of them
    /// forgot: the flag is what stops an error being appended to a reply already in
    /// flight, and a reply written without setting it is exactly the case that puts an
    /// HTTP header inside the photo the phone is saving. There is one way to write, and
    /// it cannot be used without saying so.
    /// </summary>
    internal static async Task WriteAsync(
        Stream stream, Exchange exchange, int status, string contentType, byte[] body,
        CancellationToken ct = default, TimeSpan? stallAfter = null)
    {
        exchange.Responded = true;
        await WriteRawAsync(stream, status, contentType, body, ct, stallAfter);
    }

    /// <summary>
    /// The bytes only, for the one caller that has already decided whether writing is
    /// allowed. Everything else goes through <see cref="WriteAsync"/>.
    /// </summary>
    private static async Task WriteRawAsync(
        Stream stream, int status, string contentType, byte[] body, CancellationToken ct = default,
        TimeSpan? stallAfter = null)
    {
        string reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden",
            404 => "Not Found", 408 => "Request Timeout", 413 => "Payload Too Large",
            431 => "Request Header Fields Too Large", 500 => "Internal Server Error",
            501 => "Not Implemented",
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
        // Stall bounded like a file body: /items can be megabytes, and a peer that stops
        // reading one used to hold its connection for the whole transfer budget.
        //
        // Small replies, which is nearly all of them, go out in one write. Sending a
        // twelve byte {"ok":true} through the chunked copy cost two streams and two
        // 64 KB buffers, on the path the phone takes for every item it sends.
        TimeSpan stall = stallAfter ?? DefaultStall;
        await WriteBoundedAsync(stream, head, stall, ct);
        if (body.Length > 0)
            await WriteBoundedAsync(stream, body, stall, ct);
    }

    /// <summary>One write for anything that fits a chunk, the chunked copy above that.</summary>
    private static async Task WriteBoundedAsync(
        Stream stream, byte[] bytes, TimeSpan stallAfter, CancellationToken ct)
    {
        if (bytes.Length > CopyChunkBytes)
        {
            using var source = new MemoryStream(bytes);
            await CopyWithStallTimeoutAsync(source, stream, stallAfter, ct);
            return;
        }
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(stallAfter);
        await stream.WriteAsync(bytes, stall.Token);
    }

    /// <summary>
    /// Sends a file, giving up only when it stops making progress.
    ///
    /// The request deadline is a total, and a total is the wrong shape for a body: the
    /// cap on one item is 100 MB, which needs a sustained 27 Mbps to finish inside thirty
    /// seconds, and a phone at the far end of the house does not have that. What deserves
    /// to be cut off is a transfer that has stalled, not one that is merely slow, so each
    /// chunk gets its own window and finishing one earns the next.
    /// </summary>
    private static async Task CopyWithStallTimeoutAsync(
        Stream file, Stream stream, TimeSpan stallAfter, CancellationToken ct)
    {
        var chunk = new byte[CopyChunkBytes];
        while (true)
        {
            int read = await file.ReadAsync(chunk, ct);
            if (read == 0)
                return;

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(stallAfter);
            await stream.WriteAsync(chunk.AsMemory(0, read), stall.Token);
        }
    }

    /// <param name="file">
    /// Already open, and measured from the handle. Taking the length from a FileInfo and
    /// then opening leaves a window where eviction deletes the file after the phone has
    /// been promised that many bytes. Opening it is also the step that fails when the
    /// file is locked, and the caller needs that to happen before it commits to a reply.
    /// </param>
    private static async Task WriteFileAsync(
        Stream stream, FileStream file, string downloadName, TimeSpan stallAfter, CancellationToken ct)
    {
        // Strip anything that could break out of the header value or forge a new one.
        string safe = new string(downloadName
            .Where(c => c is not ('"' or '\r' or '\n' or '\0') && !char.IsControl(c))
            .ToArray());
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n"
            + $"Content-Disposition: attachment; filename=\"{safe}\"\r\n"
            + $"Content-Length: {file.Length}\r\nReferrer-Policy: no-referrer\r\n"
            + "X-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await WriteBoundedAsync(stream, head, stallAfter, ct);
        await CopyWithStallTimeoutAsync(file, stream, stallAfter, ct);
    }
}
