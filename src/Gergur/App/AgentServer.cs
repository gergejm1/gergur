using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Gergur.Diagnostics;
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

    private readonly AppSession _session;
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";

    public AgentServer(AppSession session, int port)
    {
        _session = session;
        _port = port;
    }

    /// <summary>
    /// Every tab of every window, in window order. This flat sequence is the index
    /// space the API exposes, so tearing a tab off renumbers what follows it.
    /// </summary>
    private List<(MainForm Window, Tab Tab)> AllTabs()
    {
        // These lists belong to the UI thread and this runs on a request thread, so a
        // tab opening or being torn off mid-enumeration throws "Collection was modified"
        // and would surface as an opaque 500. Retrying rides out that momentary race.
        // The complete fix is to resolve the target inside OnUiAsync; this bounds the
        // damage without restructuring every endpoint.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return _session.Windows
                    .Where(w => w.Tabs is not null)
                    .SelectMany(w => w.Tabs!.Tabs.Select(t => (Window: w, Tab: t)))
                    .ToList();
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                // The window list changed underneath us; read it again.
            }
        }
    }

    /// <summary>The active tab of the focused window, falling back to the first window.</summary>
    private (MainForm Window, Tab Tab)? ActiveEntry()
    {
        var windows = _session.Windows;
        var window = windows.FirstOrDefault(w => w.ContainsFocus) ?? windows.FirstOrDefault();
        return window?.Tabs?.ActiveTab is { } tab ? (window, tab) : null;
    }

    public void Start()
    {
        _token = LoadOrCreateToken();

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
            // The token file deliberately survives: see LoadOrCreateToken.
        }
        catch { }
    }

    /// <summary>
    /// A stable per-install token, reused across launches so the browser can be used as
    /// an MCP server: that configuration carries the token in a static header, and a
    /// rotating secret would break on every restart.
    ///
    /// Be honest about the trade, because it is not free. Loopback binding and the Origin
    /// check stop remote attackers and web pages. The token was the only control against
    /// other processes running as this user, and rotation bounded a stolen one to a
    /// single browser session. That bound is gone and nothing here replaces it: any
    /// process running as this user can read this file, by design, since the MCP workflow
    /// requires the user to read it too, and it could equally create the file itself with
    /// a 48-hex value of its choosing and pass every check below.
    ///
    /// So the shape and ownership checks are not a fix for that. What they do is stop a
    /// *different* account planting a token, and the ACL raises the bar against other
    /// users on a shared machine. Against code already running as this user, treat the
    /// agent API as fully exposed.
    /// </summary>
    internal static string LoadOrCreateToken(string? path = null)
    {
        string file = path ?? TokenPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        if (TryReadTrustedToken(file) is { } existing)
        {
            // A token written by an older build carries inherited permissions, so
            // adopting one has to re-apply the lockdown or the guarantee is not true.
            RestrictToCurrentUser(file);
            return existing;
        }

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); // 48 hex chars
        WriteTokenForCurrentUserOnly(file, token);
        return token;
    }

    /// <summary>The stored token, but only if this code could have written it.</summary>
    internal static string? TryReadTrustedToken(string? path = null)
    {
        string file = path ?? TokenPath;
        try
        {
            if (!File.Exists(file))
                return null;
            string value = File.ReadAllText(file).Trim();
            if (!IsMintedTokenShape(value))
                return null;

            // Fail closed. An owner we cannot establish is an owner we do not trust:
            // treating "unknown" as "mine" would adopt exactly the planted file the
            // check exists to reject.
            var current = WindowsIdentity.GetCurrent().User;
            var owner = new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (current is null || owner is not SecurityIdentifier sid || !sid.Equals(current))
                return null;
            return value;
        }
        catch
        {
            return null; // Unreadable or un-inspectable: mint a fresh one rather than trust it.
        }
    }

    /// <summary>Exactly what <see cref="LoadOrCreateToken"/> mints: 24 random bytes as hex.</summary>
    internal static bool IsMintedTokenShape(string value)
        => value.Length == 48 && value.All(Uri.IsHexDigit);

    private static void WriteTokenForCurrentUserOnly(string file, string token)
    {
        // Delete before creating. Overwriting a file somebody else created leaves them
        // as its owner, holding WRITE_DAC, so they would keep read access to the secret
        // we just minted and every restart would quietly re-leak a fresh one.
        try { File.Delete(file); } catch { }
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(token);
        }
        RestrictToCurrentUser(file);
    }

    /// <summary>Strips inherited permissions so only the current user can read the token.</summary>
    private static void RestrictToCurrentUser(string file)
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null)
                return;
            var info = new FileInfo(file);
            var security = info.GetAccessControl();
            var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
            // "Protected with one rule" is not enough: that single rule could grant
            // Everyone. Only skip the rewrite when the lone rule is this user's.
            if (security.AreAccessRulesProtected && rules.Count == 1
                && rules.Cast<FileSystemAccessRule>().Single() is
                   { AccessControlType: AccessControlType.Allow } only
                && only.IdentityReference.Equals(user)
                && only.FileSystemRights.HasFlag(FileSystemRights.FullControl))
                return; // already locked down to us; nothing to do

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in rules)
                security.RemoveAccessRule(rule);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // Say so rather than silently leaving the token world-readable.
            DebugLog.WriteAlways($"agent token ACL not applied to {file}: {ex.Message}");
        }
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

            // Distinguish the two rejections. MCP clients vary in whether they attach an
            // Origin header, and an identical 403 for both left no way to tell a
            // misconfigured token from a client this server will never accept.
            // Neither message tells an attacker anything it did not already send.
            if (headers.ContainsKey("origin"))
            {
                await WriteAsync(stream, 403, "application/json",
                    """{"error":"requests carrying an Origin header are rejected; this API is not reachable from web content"}"""u8.ToArray());
                return;
            }
            if (!headers.TryGetValue("x-gergur-token", out var token) || token != _token)
            {
                await WriteAsync(stream, 403, "application/json",
                    """{"error":"bad or missing X-Gergur-Token header"}"""u8.ToArray());
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
        // An index that was supplied but cannot be used must never fall back to the
        // active tab. An agent that believes it is acting on tab 3 would otherwise
        // navigate away from, type into, or run script against whatever the user is
        // looking at right now. Only an absent index means "the active tab".
        (MainForm Window, Tab Tab)? TargetEntry()
        {
            var (supplied, index) = ResolveIndex(query, body);
            if (!supplied)
                return ActiveEntry();
            if (index is not { } i)
                return null; // supplied but unusable: that is an error, not the active tab
            var all = AllTabs();
            return i >= 0 && i < all.Count ? all[i] : null;
        }

        Tab? Target() => TargetEntry()?.Tab;

        string? BodyString(string name)
            => body is not null && body.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

        byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);

        switch ((method, path))
        {
            case ("POST", "/mcp"):
                return await HandleMcpAsync(body);

            case ("GET", "/tabs"):
            {
                var list = await OnUiAsync(() =>
                {
                    var windows = _session.Windows.ToList();
                    return Task.FromResult(AllTabs().Select((e, i) => new
                    {
                        index = i,
                        window = windows.IndexOf(e.Window), // which window this tab lives in
                        url = e.Tab.Url,
                        title = e.Tab.Title,
                        state = e.Tab.State.ToString(),
                        active = e.Window.Tabs?.ActiveTab == e.Tab, // active within its own window
                        errors = e.Tab.RecentErrors.ToArray(), // what the status bar's "N issues" is counting
                    }).ToArray());
                });
                return (200, "application/json", Json(list));
            }

            case ("POST", "/open"):
            {
                string url = BodyString("url") ?? HomePage.Url;
                var window = ActiveEntry()?.Window ?? _session.Windows.FirstOrDefault();
                if (window?.Tabs is not { } manager)
                    return (503, "application/json", Json(new { error = "no window is ready" }));
                var tab = await OnUiAsync(() => manager.CreateTabAsync(UrlHeuristics.ToNavigableUrl(url, _session.Settings.SearchUrlTemplate)));
                return (200, "application/json", Json(new { index = AllTabs().FindIndex(e => e.Tab == tab) }));
            }

            case ("POST", "/activate"):
            {
                // Documented as taking an index. Defaulting to the active tab would make
                // "activate" a no-op and "close" destructive, so require it explicitly.
                if (!ResolveIndex(query, body).Supplied)
                    return (400, "application/json", Json(new { error = "index required" }));
                if (TargetEntry() is not { } entry || entry.Window.Tabs is null)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                await OnUiAsync(async () =>
                {
                    await entry.Window.Tabs.ActivateAsync(entry.Tab);
                    entry.Window.Activate(); // bring its window forward too
                    return true;
                });
                return (200, "application/json", Json(new { ok = true }));
            }

            case ("POST", "/close"):
            {
                // Closing is the one destructive action here; it must never guess.
                if (!ResolveIndex(query, body).Supplied)
                    return (400, "application/json", Json(new { error = "index required" }));
                if (TargetEntry() is not { } entry || entry.Window.Tabs is null)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                await OnUiAsync(async () => { await entry.Window.Tabs.CloseTabAsync(entry.Tab); return true; });
                return (200, "application/json", Json(new { ok = true }));
            }

            case ("POST", "/navigate"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                string url = BodyString("url") ?? "";
                if (url.Length == 0)
                    return (400, "application/json", Json(new { error = "url required" }));
                await OnUiAsync(async () => { await tab.NavigateAsync(UrlHeuristics.ToNavigableUrl(url, _session.Settings.SearchUrlTemplate)); return true; });
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
                if (TargetEntry() is not { } shot || shot.Window.Tabs is null)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                var png = await OnUiAsync(async () =>
                {
                    if (shot.Window.Tabs.ActiveTab != shot.Tab)
                    {
                        await shot.Window.Tabs.ActivateAsync(shot.Tab); // capture needs a rendered, visible view
                        await Task.Delay(400);
                    }
                    return await shot.Tab.CaptureScreenshotAsync();
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

    // ------------------------------------------------------------------ MCP

    // Speaking MCP here rather than from a wrapper process means any Claude Code
    // session, in any project, gets these as native tools once the server is
    // registered - no per-project subagent, no second codebase to keep in step.
    private const string McpProtocolVersion = "2024-11-05";

    /// <summary>How long a single tool call may run before the connection is released.</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Past this, a screenshot is more likely to be rejected than read.</summary>
    private const int MaxScreenshotBytes = 5 * 1024 * 1024;

    private static object Text(string description) => new { type = "string", description };
    private static object Index() => new { type = "integer", description = "Tab index from gergur_list_tabs. Omit for the active tab of the focused window." };
    /// <summary>For the tools where the index is required, so the schema and the prose agree.</summary>
    private static object NamedIndex() => new { type = "integer", description = "Tab index from gergur_list_tabs." };

    private static object Tool(string name, string description, object properties, string[] required)
        => new { name, description, inputSchema = new { type = "object", properties, required } };

    /// <summary>
    /// Where a request says to act, split out so it can be tested directly.
    ///
    /// Supplied-but-unusable must never collapse into "the active tab": an agent that
    /// believes it is closing tab 3 would otherwise close whatever the user is looking
    /// at. An explicit JSON null counts as not supplied, because models routinely send
    /// null for an argument they are choosing to omit.
    /// </summary>
    internal static (bool Supplied, int? Index) ResolveIndex(
        IReadOnlyDictionary<string, string> query, JsonDocument? body)
    {
        if (query.TryGetValue("index", out var fromQuery))
        {
            if (fromQuery.Length == 0 || fromQuery == "null")
                return (false, null);
            return (true, int.TryParse(fromQuery, out int parsed) ? parsed : null);
        }

        if (body is { RootElement.ValueKind: JsonValueKind.Object }
            && body.RootElement.TryGetProperty("index", out var fromBody))
        {
            // Models send an index as a number or as a string; accept both, nothing else.
            return fromBody.ValueKind switch
            {
                JsonValueKind.Null => (false, null),
                JsonValueKind.Number => (true, fromBody.TryGetInt32(out int number) ? number : null),
                JsonValueKind.String => (true, int.TryParse(fromBody.GetString(), out int text) ? text : null),
                _ => (true, null),
            };
        }
        return (false, null);
    }

    /// <summary>
    /// Arguments a tool cannot run without. The schema declares these, but a schema is
    /// only a hint to the caller: nothing stops one being omitted, and for
    /// gergur_close_tab that meant falling through to "the active tab" and closing the
    /// page the user was reading.
    /// </summary>
    internal static string[] RequiredArgsForTool(string name) => name switch
    {
        "gergur_open_tab" => ["url"],
        "gergur_navigate" => ["url"],
        "gergur_activate_tab" => ["index"],
        "gergur_close_tab" => ["index"],
        "gergur_run_javascript" => ["js"],
        "gergur_click" => ["selector"],
        "gergur_type_text" => ["selector", "text"],
        _ => [],
    };

    /// <summary>
    /// Arguments where an empty string cannot mean anything. Deliberately excludes
    /// "text": clearing a field by typing nothing into it is a real action.
    /// </summary>
    private static bool EmptyIsMeaningless(string key) => key is "url" or "js" or "selector";

    /// <summary>The first required argument this call is missing, or null when complete.</summary>
    internal static string? MissingRequiredArg(string name, JsonElement? args)
    {
        foreach (string key in RequiredArgsForTool(name))
        {
            if (args is not { ValueKind: JsonValueKind.Object } supplied
                || !supplied.TryGetProperty(key, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (EmptyIsMeaningless(key)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString()?.Length is null or 0))
                return key;
        }
        return null;
    }

    /// <summary>
    /// The endpoint behind each tool. A closed map with a rejecting default: the path is
    /// never built from input, so no tool name can reach an endpoint not listed here.
    /// An empty path means the name is not a tool.
    /// </summary>
    internal static (string Method, string Path) RouteForTool(string name) => name switch
    {
        "gergur_list_tabs" => ("GET", "/tabs"),
        "gergur_open_tab" => ("POST", "/open"),
        "gergur_navigate" => ("POST", "/navigate"),
        "gergur_activate_tab" => ("POST", "/activate"),
        "gergur_close_tab" => ("POST", "/close"),
        "gergur_read_page" => ("GET", "/page"),
        "gergur_read_html" => ("GET", "/html"),
        "gergur_screenshot" => ("GET", "/screenshot"),
        "gergur_run_javascript" => ("POST", "/eval"),
        "gergur_click" => ("POST", "/click"),
        "gergur_type_text" => ("POST", "/type"),
        _ => ("", ""),
    };

    /// <summary>Every tool maps onto an endpoint this server already serves.</summary>
    internal static object[] McpTools() =>
    [
        Tool("gergur_list_tabs",
            "List every open tab across all Gergur windows: flat index, which window, url, title, sleep state, and any errors the page reported.",
            new { }, []),
        Tool("gergur_open_tab", "Open a url in a new tab and focus it. Bare terms are treated as a search.",
            new { url = Text("The url or search terms to open.") }, ["url"]),
        Tool("gergur_navigate", "Point an existing tab at a url.",
            new { url = Text("The url to go to."), index = Index() }, ["url"]),
        Tool("gergur_activate_tab", "Bring a tab to the front, and its window with it.",
            new { index = NamedIndex() }, ["index"]),
        Tool("gergur_close_tab", "Close a tab.", new { index = NamedIndex() }, ["index"]),
        Tool("gergur_read_page", "Read a page as rendered text. Prefer this over a screenshot for reading: it does not disturb which tab the user is looking at.",
            new { index = Index() }, []),
        Tool("gergur_read_html", "Read a page's full HTML.", new { index = Index() }, []),
        Tool("gergur_screenshot", "Capture a tab as a PNG. This activates the tab first, so it changes what the user sees.",
            new { index = Index() }, []),
        Tool("gergur_run_javascript", "Evaluate JavaScript in a page and return the result.",
            new { js = Text("The expression to evaluate."), index = Index() }, ["js"]),
        Tool("gergur_click", "Click the first element matching a CSS selector. Animates a visible cursor to it first.",
            new { selector = Text("A CSS selector."), index = Index() }, ["selector"]),
        Tool("gergur_type_text", "Fill an input or textarea, in a way React and similar frameworks notice.",
            new { selector = Text("A CSS selector for the field."), text = Text("The text to enter."), index = Index() },
            ["selector", "text"]),
    ];

    private Task<(int, string, byte[])> HandleMcpAsync(JsonDocument? body)
        => HandleJsonRpcAsync(body, CallToolAsync);

    /// <summary>
    /// The JSON-RPC envelope, split from the browser so it can be tested without an
    /// engine: every malformed-input path below is reachable with no session at all.
    /// </summary>
    internal static async Task<(int, string, byte[])> HandleJsonRpcAsync(
        JsonDocument? body, Func<JsonElement, Task<object>> callTool)
    {
        byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);
        (int, string, byte[]) Invalid(string message) => (200, "application/json",
            Json(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32600, message } }));

        if (body is null)
            return (400, "application/json",
                Json(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "parse error" } }));

        // Anything that is not a JSON object gets a JSON-RPC error, not an exception.
        // A batch (array root) is legal JSON-RPC that this server does not implement,
        // and used to escape as an HTTP 500 that clients read as a transport failure.
        var root = body.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return Invalid(root.ValueKind == JsonValueKind.Array
                ? "batched requests are not supported; send one request per call"
                : "request must be a JSON object");

        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            return Invalid("missing or non-string \"method\"");
        string method = methodElement.GetString() ?? "";

        // A JSON-RPC notification carries no id and must get no response body.
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind == JsonValueKind.Null)
            return (202, "application/json", Array.Empty<byte>());

        // Only a string or a number is a legal id.
        if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            return Invalid("\"id\" must be a string or a number");

        // Echo it back verbatim. Converting a number to a string here meant a client
        // matching replies by strict equality would not recognise its own request, and
        // ids beyond Int64 or with a fraction changed type silently.
        object id = idElement.Clone();

        switch (method)
        {
            case "initialize":
                return (200, "application/json", Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        protocolVersion = McpProtocolVersion,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "gergur", version = "1.0.0" },
                    },
                }));

            case "ping":
                return (200, "application/json", Json(new { jsonrpc = "2.0", id, result = new { } }));

            case "tools/list":
                return (200, "application/json", Json(new { jsonrpc = "2.0", id, result = new { tools = McpTools() } }));

            case "tools/call":
                return (200, "application/json", Json(new { jsonrpc = "2.0", id, result = await callTool(root) }));

            default:
                return (200, "application/json", Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = $"unknown method: {method}" },
                }));
        }
    }

    private async Task<object> CallToolAsync(JsonElement request)
    {
        if (RejectBadToolCall(request, out string name) is { } rejection)
            return rejection;
        var (httpMethod, path) = RouteForTool(name);

        JsonDocument? args = null;
        bool abandoned = false;
        try
        {
            // Safe: RejectBadToolCall has already established that params is an object.
            var parameters = request.GetProperty("params");
            if (parameters.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object)
                args = JsonDocument.Parse(argsElement.GetRawText());

            // Refuse before dispatch. Without this, gergur_close_tab with no index
            // reaches /close with nothing to target and closes the user's active tab.
            if (MissingRequiredArg(name, args?.RootElement) is { } missing)
                return McpError($"{name} requires \"{missing}\", which was not supplied.");

            // The GET endpoints take the tab index from the query string. Pass through
            // whatever was supplied, valid or not, so TargetEntry can reject a bad index
            // rather than quietly retargeting the user's active tab.
            var query = new Dictionary<string, string>();
            if (httpMethod == "GET" && args is { RootElement.ValueKind: JsonValueKind.Object }
                && args.RootElement.TryGetProperty("index", out var index))
            {
                query["index"] = index.ValueKind switch
                {
                    JsonValueKind.Number => index.TryGetInt32(out int n) ? n.ToString() : index.GetRawText(),
                    JsonValueKind.String => index.GetString() ?? "",
                    _ => index.GetRawText(),
                };
            }

            // A page running a script that never returns would otherwise hold this
            // connection, its stream and its task for the life of the process.
            var dispatch = RouteAsync(httpMethod, path, query, args);
            if (await Task.WhenAny(dispatch, Task.Delay(ToolTimeout)) != dispatch)
            {
                // The abandoned call still holds args, so disposing here would be a
                // use-after-dispose on a live task. Hand disposal to that task instead,
                // and observe its fault so it does not go unhandled.
                abandoned = true;
                _ = dispatch.ContinueWith(
                    finished => { _ = finished.Exception; args?.Dispose(); }, TaskScheduler.Default);

                // Say it may still land: giving up waiting is not the same as cancelling,
                // and for a close the tab really does disappear afterwards.
                return McpError(
                    $"{name} did not finish within {ToolTimeout.TotalSeconds:0} seconds and was abandoned. "
                    + "It may still complete. If it was a read, the page may be running a script that never returns.");
            }

            var (status, contentType, payload) = await dispatch;
            if (contentType == "image/png")
            {
                // /page and /html both bound their output; this did not. A 4K capture is
                // megabytes of base64 that most clients reject with an opaque error.
                if (payload.Length > MaxScreenshotBytes)
                {
                    return McpError(
                        $"the screenshot is {payload.Length / 1024.0 / 1024.0:0.0} MB, over the {MaxScreenshotBytes / 1024 / 1024} MB limit. "
                        + "Use gergur_read_page to read the content instead.");
                }
                return new
                {
                    content = new object[]
                    {
                        new { type = "image", data = Convert.ToBase64String(payload), mimeType = "image/png" },
                    },
                };
            }
            string text = Encoding.UTF8.GetString(payload);
            return status >= 400 ? McpError(text) : McpText(text);
        }
        catch (Exception ex)
        {
            // A failed tool call is reported to the caller, never thrown at the transport.
            return McpError($"{name} failed: {ex.Message}");
        }
        finally
        {
            if (!abandoned)
                args?.Dispose();
        }
    }

    /// <summary>
    /// Shape and name checks for a tools/call, split out so they can be exercised
    /// without a browser. Everything here must run before any dispatch: these are
    /// exactly the paths that used to throw out of the handler as an HTTP 500.
    /// Returns an error result to send back, or null when the call is well formed.
    /// </summary>
    internal static object? RejectBadToolCall(JsonElement request, out string name)
    {
        name = "";
        if (!request.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            return McpError("\"params\" must be an object naming a tool");
        if (!parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            return McpError("the call named no tool");

        name = nameElement.GetString() ?? "";
        return RouteForTool(name).Path.Length == 0 ? McpError($"unknown tool: {name}") : null;
    }

    private static object McpText(string text)
        => new { content = new object[] { new { type = "text", text } } };

    private static object McpError(string message)
        => new { content = new object[] { new { type = "text", text = message } }, isError = true };

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
        // Any window will do: they all run on the one UI thread.
        if (_session.Windows.FirstOrDefault() is not { } window)
        {
            tcs.TrySetException(new InvalidOperationException("no window is open"));
            return tcs.Task;
        }
        window.BeginInvoke(async () =>
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
        string reason = status switch
        {
            200 => "OK",
            202 => "Accepted", // JSON-RPC notifications answer with this and no body
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Error",
        };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
