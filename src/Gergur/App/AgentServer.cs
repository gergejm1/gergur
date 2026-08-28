using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gergur.Tabs;
using Gergur.UI;

namespace Gergur.App;

/// <summary>
/// Local API that lets an AI agent (e.g. a Claude Code session) drive the
/// browser: list/open/activate tabs, read pages, click, type, eval, screenshot.
///
/// Security model: binds 127.0.0.1 only; every request must present the random
/// per-install token from agent-token.txt (readable by local processes, never
/// by web content); any request carrying an Origin header is rejected, so a
/// web page cannot reach this even via fetch.
/// </summary>
public sealed class AgentServer
{
    public static readonly string TokenPath = Path.Combine(Settings.DataDir, "agent-token.txt");

    private readonly MainForm _form;
    private readonly TabManager _tabs;
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";

    public AgentServer(MainForm form, TabManager tabs, int port)
    {
        _form = form;
        _tabs = tabs;
        _port = port;
    }

    public void Start()
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        Directory.CreateDirectory(Settings.DataDir);
        File.WriteAllText(TokenPath, _token);

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            File.Delete(TokenPath);
        }
        catch { }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => HandleClientAsync(client), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        try
        {
            var (method, path, query, headers, body) = await ReadRequestAsync(stream);

            if (headers.ContainsKey("origin")
                || !headers.TryGetValue("x-gergur-token", out var token)
                || token != _token)
            {
                await WriteAsync(stream, 403, "application/json", """{"error":"forbidden"}"""u8.ToArray());
                return;
            }

            var (status, contentType, payload) = await RouteAsync(method, path, query, body);
            await WriteAsync(stream, status, contentType, payload);
        }
        catch (Exception ex)
        {
            try
            {
                var err = JsonSerializer.SerializeToUtf8Bytes(new { error = ex.Message });
                await WriteAsync(stream, 500, "application/json", err);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ routing

    private async Task<(int, string, byte[])> RouteAsync(
        string method, string path, Dictionary<string, string> query, JsonDocument? body)
    {
        Tab? Target()
        {
            int? index = null;
            if (query.TryGetValue("index", out var q) && int.TryParse(q, out var qi))
                index = qi;
            else if (body is not null && body.RootElement.TryGetProperty("index", out var b) && b.ValueKind == JsonValueKind.Number)
                index = b.GetInt32();
            if (index is { } i)
                return i >= 0 && i < _tabs.Tabs.Count ? _tabs.Tabs[i] : null;
            return _tabs.ActiveTab;
        }

        string? BodyString(string name)
            => body is not null && body.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

        byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);

        switch ((method, path))
        {
            case ("GET", "/tabs"):
            {
                var list = await OnUiAsync(() => Task.FromResult(_tabs.Tabs.Select((t, i) => new
                {
                    index = i,
                    url = t.Url,
                    title = t.Title,
                    state = t.State.ToString(),
                    active = _tabs.ActiveTab == t,
                }).ToArray()));
                return (200, "application/json", Json(list));
            }

            case ("POST", "/open"):
            {
                string url = BodyString("url") ?? HomePage.Url;
                var tab = await OnUiAsync(() => _tabs.CreateTabAsync(UrlHeuristics.ToNavigableUrl(url, "https://duckduckgo.com/?q={0}")));
                return (200, "application/json", Json(new { index = _tabs.Tabs.ToList().IndexOf(tab) }));
            }

            case ("POST", "/activate"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                await OnUiAsync(async () => { await _tabs.ActivateAsync(tab); return true; });
                return (200, "application/json", Json(new { ok = true }));
            }

            case ("POST", "/close"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                await OnUiAsync(async () => { await _tabs.CloseTabAsync(tab); return true; });
                return (200, "application/json", Json(new { ok = true }));
            }

            case ("POST", "/navigate"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                string url = BodyString("url") ?? "";
                if (url.Length == 0)
                    return (400, "application/json", Json(new { error = "url required" }));
                await OnUiAsync(async () => { await tab.NavigateAsync(UrlHeuristics.ToNavigableUrl(url, "https://duckduckgo.com/?q={0}")); return true; });
                return (200, "application/json", Json(new { ok = true }));
            }

            case ("GET", "/page"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                string text = await EvalStringAsync(tab, "document.body ? document.body.innerText : ''");
                return (200, "application/json", Json(new { url = tab.Url, title = tab.Title, text = Truncate(text, 200_000) }));
            }

            case ("GET", "/html"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                string html = await EvalStringAsync(tab, "document.documentElement.outerHTML");
                return (200, "application/json", Json(new { url = tab.Url, html = Truncate(html, 400_000) }));
            }

            case ("GET", "/screenshot"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                var png = await OnUiAsync(async () =>
                {
                    if (_tabs.ActiveTab != tab)
                    {
                        await _tabs.ActivateAsync(tab); // capture needs a rendered, visible view
                        await Task.Delay(400);
                    }
                    return await tab.CaptureScreenshotAsync();
                });
                return (200, "image/png", png);
            }

            case ("POST", "/eval"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                string js = BodyString("js") ?? "";
                if (js.Length == 0)
                    return (400, "application/json", Json(new { error = "js required" }));
                string result = await OnUiAsync(() => tab.ExecuteScriptAsync(js));
                return (200, "application/json", Encoding.UTF8.GetBytes($"{{\"result\":{result}}}"));
            }

            case ("POST", "/click"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                string selector = BodyString("selector") ?? "";
                string js = $$"""
                    (() => {
                        const el = document.querySelector({{JsonSerializer.Serialize(selector)}});
                        if (!el) return false;
                        el.scrollIntoView({ block: 'center', behavior: 'instant' });
                        {{CursorJs}}
                        const r = el.getBoundingClientRect();
                        __gergurCursorTo(r.left + r.width / 2, r.top + r.height / 2, () => {
                            const prev = el.style.outline;
                            el.style.outline = '2px solid #3D7BFA';
                            setTimeout(() => { el.style.outline = prev; }, 600);
                            el.click();
                        });
                        return true;
                    })()
                    """;
                string result = await OnUiAsync(() => tab.ExecuteScriptAsync(js));
                await Task.Delay(900); // let the cursor glide and the click land before responding
                return (200, "application/json", Json(new { ok = result == "true" }));
            }

            case ("POST", "/type"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                string selector = BodyString("selector") ?? "";
                string text = BodyString("text") ?? "";
                string js = $$"""
                    (() => {
                        const el = document.querySelector({{JsonSerializer.Serialize(selector)}});
                        if (!el) return false;
                        el.scrollIntoView({ block: 'center', behavior: 'instant' });
                        {{CursorJs}}
                        const r = el.getBoundingClientRect();
                        __gergurCursorTo(r.left + r.width / 2, r.top + r.height / 2, () => {
                            el.focus();
                            const prev = el.style.outline;
                            el.style.outline = '2px solid #3D7BFA';
                            setTimeout(() => { el.style.outline = prev; }, 600);
                            const proto = el instanceof HTMLTextAreaElement
                                ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                            const desc = Object.getOwnPropertyDescriptor(proto, 'value');
                            if (desc && desc.set) desc.set.call(el, {{JsonSerializer.Serialize(text)}});
                            else el.value = {{JsonSerializer.Serialize(text)}};
                            el.dispatchEvent(new Event('input', { bubbles: true }));
                            el.dispatchEvent(new Event('change', { bubbles: true }));
                        });
                        return true;
                    })()
                    """;
                string result = await OnUiAsync(() => tab.ExecuteScriptAsync(js));
                await Task.Delay(900);
                return (200, "application/json", Json(new { ok = result == "true" }));
            }

            default:
                return (404, "application/json", Json(new { error = "unknown endpoint" }));
        }
    }

    /// <summary>
    /// The visible agent cursor: a Gergur-blue dot that glides to the target,
    /// ripples on arrival, then runs the action, so the user can watch the
    /// agent work. Defines __gergurCursorTo(x, y, action) in the page.
    /// </summary>
    private const string CursorJs = """
        if (!window.__gergurCursorTo) {
            window.__gergurCursorTo = (x, y, action) => {
                let c = document.getElementById('__gergur_cursor');
                if (!c) {
                    c = document.createElement('div');
                    c.id = '__gergur_cursor';
                    c.style.cssText = 'position:fixed;left:50%;top:40%;width:18px;height:18px;'
                        + 'border-radius:50%;background:rgba(61,123,250,.85);border:2px solid #fff;'
                        + 'box-shadow:0 1px 8px rgba(0,0,0,.55);z-index:2147483647;pointer-events:none;'
                        + 'transition:left .5s cubic-bezier(.3,.7,.4,1),top .5s cubic-bezier(.3,.7,.4,1);';
                    document.documentElement.appendChild(c);
                }
                requestAnimationFrame(() => {
                    c.style.left = (x - 9) + 'px';
                    c.style.top = (y - 9) + 'px';
                });
                setTimeout(() => {
                    const rip = document.createElement('div');
                    rip.style.cssText = 'position:fixed;left:' + (x - 9) + 'px;top:' + (y - 9) + 'px;'
                        + 'width:18px;height:18px;border-radius:50%;border:3px solid rgba(61,123,250,.9);'
                        + 'z-index:2147483646;pointer-events:none;transition:transform .45s ease-out,opacity .45s ease-out;';
                    document.documentElement.appendChild(rip);
                    requestAnimationFrame(() => { rip.style.transform = 'scale(3)'; rip.style.opacity = '0'; });
                    setTimeout(() => rip.remove(), 500);
                    action();
                }, 550);
            };
        }
        """;

    private async Task<string> EvalStringAsync(Tab tab, string js)
    {
        string raw = await OnUiAsync(() => tab.ExecuteScriptAsync(js));
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? "" : raw;
        }
        catch
        {
            return raw;
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _form.BeginInvoke(async () =>
        {
            try { tcs.TrySetResult(await work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    // ------------------------------------------------------------------ tiny HTTP/1.1

    private static async Task<(string method, string path, Dictionary<string, string> query, Dictionary<string, string> headers, JsonDocument? body)>
        ReadRequestAsync(NetworkStream stream)
    {
        var header = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one);
            if (n == 0)
                break;
            header.WriteByte(one[0]);
            if (header.Length > 32_768)
                throw new InvalidOperationException("header too large");
            if (EndsWithCrlfCrlf(header))
                break;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            throw new InvalidOperationException("empty request");
        var parts = lines[0].Split(' ');
        string method = parts[0].ToUpperInvariant();
        string rawPath = parts.Length > 1 ? parts[1] : "/";

        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        string path = rawPath;
        var query = new Dictionary<string, string>();
        int qm = rawPath.IndexOf('?');
        if (qm >= 0)
        {
            path = rawPath[..qm];
            foreach (var pair in rawPath[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        JsonDocument? body = null;
        if (headers.TryGetValue("content-length", out var lenText)
            && int.TryParse(lenText, out int len) && len is > 0 and <= 4_000_000)
        {
            var buffer = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read));
                if (n == 0)
                    break;
                read += n;
            }
            try { body = JsonDocument.Parse(buffer.AsMemory(0, read)); } catch { }
        }
        return (method, path, query, headers, body);
    }

    private static bool EndsWithCrlfCrlf(MemoryStream ms)
    {
        if (ms.Length < 4)
            return false;
        var buf = ms.GetBuffer();
        long i = ms.Length;
        return buf[i - 4] == '\r' && buf[i - 3] == '\n' && buf[i - 2] == '\r' && buf[i - 1] == '\n';
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        string reason = status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
