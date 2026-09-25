using System.Globalization;
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

    /// <summary>
    /// The active tab of the window the person last brought to the front, falling back to
    /// the first window.
    ///
    /// Not ContainsFocus: this runs on a request thread, and focus is per thread, so that
    /// was false for every window every time and this always meant window 0. A request
    /// naming no tab then acted on window 0's page while the person was in another.
    /// </summary>
    private (MainForm Window, Tab Tab)? ActiveEntry()
    {
        var window = _session.WindowInUse();
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
                // Logged, not returned. A WebView2 failure carries the profile path,
                // account name and all, and this body goes into an agent transcript. The
                // same policy /eval and WindowCapture already follow.
                // Always written: pointing a caller at a log that only exists when
                // GERGUR_DEBUG is set points them at nothing.
                DebugLog.WriteAlways($"request failed: {ex}");
                var err = JsonSerializer.SerializeToUtf8Bytes(new { error = "that request failed inside the browser; the cause is in %LOCALAPPDATA%/Gergur/debug.log" });
                await WriteAsync(stream, 500, "application/json", err);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ routing

    /// <summary>
    /// The refusal for a tab that is, or may be, showing one of the browser's own pages,
    /// judged from this thread, or null when it is not. Either address counts: Url is where
    /// a navigation is heading, and the committed one is what is on screen, which a
    /// navigation that never commits leaves behind. On one only by the committed address,
    /// the tab is leaving it. The tab checks again, on its own thread, when the script
    /// actually runs.
    /// </summary>
    internal static InternalPages.OwnPageRefusedException? OwnPageRefusal(Tab tab)
        // The same rule the tab applies when the script runs, less the navigation record,
        // which only the UI thread may read.
        => Tab.RefusalFor(tab.Url, null, tab.CommittedSourceForOtherThreads);

    internal static (int, string, byte[]) Refusal(InternalPages.OwnPageRefusedException refused)
        => (refused.Status, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = refused.Message }));

    private async Task<(int, string, byte[])> RouteAsync(
        string method, string path, Dictionary<string, string> query, JsonDocument? body)
    {
        // The page reached one of the browser's own pages while the request was waiting for
        // it. Refused the same as when it was there from the start, for http and MCP alike.
        try
        {
            return await RouteRequestAsync(method, path, query, body);
        }
        catch (InternalPages.OwnPageRefusedException refused)
        {
            return Refusal(refused);
        }
    }

    private async Task<(int, string, byte[])> RouteRequestAsync(
        string method, string path, Dictionary<string, string> query, JsonDocument? body)
    {
        // An index that was supplied but cannot be used must never fall back to the
        // active tab. An agent that believes it is acting on tab 3 would otherwise
        // navigate away from, type into, or run script against whatever the user is
        // looking at right now. Only an absent index means "the active tab".
        (MainForm Window, Tab Tab)? TargetEntry()
        {
            var all = AllTabs();
            var active = ActiveEntry();
            int at = TargetIndex(
                all.Select(e => e.Tab.Id).ToList(),
                StringValue(query, body, "id"),
                ResolveIndex(query, body),
                active is null ? -1 : all.FindIndex(e => e.Tab == active.Value.Tab));
            return at >= 0 && at < all.Count ? all[at] : null;
        }

        string? StringFrom(string name) => StringValue(query, body, name);

        // The browser's own pages show history, downloads and bookmarks, and can open a
        // downloaded file or clear history. Reading or scripting them through this API
        // would reach all of that, and launching files is the kind of reach /settings
        // refuses. Both addresses are plain fields, safe to read here.
        (int, string, byte[])? OwnPageRefused(Tab t) => OwnPageRefusal(t) is { } refused ? Refusal(refused) : null;

        Tab? Target() => TargetEntry()?.Tab;

        bool? BoolFrom(string name) => BoolValue(query, body, name);

        double? NumberFrom(string name) => NumberValue(query, body, name);

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
                        id = e.Tab.Id,       // stable; prefer this to index
                        index = i,
                        window = windows.IndexOf(e.Window), // which window this tab lives in
                        // The browser's own pages by name, as the address bar and the session
                        // show them: their file url is a path into the install, account name
                        // and all, and it would go into the transcript.
                        url = InternalPages.NameOf(e.Tab.Url),
                        title = e.Tab.Title,
                        state = e.Tab.State.ToString(),
                        active = e.Window.Tabs?.ActiveTab == e.Tab, // active within its own window
                        // What the status bar's "N issues" is counting. Not for the browser's
                        // own pages, which /console refuses: one rule for both.
                        errors = e.Tab.ShownPage is null ? e.Tab.RecentErrors.ToArray() : [],
                    }).ToArray());
                });
                return (200, "application/json", Json(list));
            }

            case ("GET", "/settings"):
            {
                // Reading them needed a look at a json file on disk, and changing one
                // needed the browser closed, the file edited and the browser relaunched.
                // That is three restarts to try a thing twice, and it is how a session
                // got lost here.
                // Read together on the UI thread, which owns the tunnel's process, so the
                // answer is one snapshot rather than two reads a restart could fall between.
                var (current, tunnelUp) = await OnUiAsync(() =>
                    Task.FromResult((SettingsPatch.Snapshot(_session.Settings), _session.Vpn.IsRunning)));
                return (200, "application/json", Json(new
                {
                    settings = current,
                    restartRequired = Settings.RestartRequired.ToArray(),
                    // Outside "settings", so they read as facts about this run and cannot be
                    // sent back as a patch. VpnEnabled is the choice, and says on while a
                    // tunnel that failed at startup has the engine running without it.
                    // vpnInForce: this engine points at the tunnel. tunnelRunning: the tunnel's
                    // process is running. Traffic goes through the vpn only when both are true;
                    // in force with no tunnel running, requests fail rather than go around it.
                    vpnInForce = _session.Env.ProxyInForce,
                    tunnelRunning = tunnelUp,
                }));
            }

            case ("POST", "/settings"):
            {
                if (body is not { RootElement.ValueKind: JsonValueKind.Object } patch)
                    return (400, "application/json", Json(new { error = "a json object of settings is required" }));

                // Over http the settings are the body. As an MCP tool they arrive nested
                // under "settings", because a tool whose schema is an empty object tells
                // the model nothing about what it may send. Both are accepted rather than
                // making one of the two callers wrap or unwrap by hand.
                var wanted = patch.RootElement;
                if (wanted.TryGetProperty("settings", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    wanted = nested;

                var applied = new List<string>();
                var needsRestart = new List<string>();
                var unknown = new List<string>();
                bool persisted = true;
                string? whyNotPersisted = null;
                string? failure = await OnUiAsync(() =>
                {
                    string? error = SettingsPatch.Apply(
                        _session.Settings, wanted, applied, needsRestart, unknown);
                    if (error is null && applied.Count > 0)
                    {
                        // The same live-apply the settings dialog runs. Without it this
                        // answered "applied" for the blocklist and the blocker went on
                        // blocking: RequestBlocker.Enabled is its own field and nothing
                        // re-read it.
                        MainForm.ApplyLiveSettings(_session);
                        try
                        {
                            // The patch changes memory; writing it out is this endpoint's
                            // call. A failure here is not a failed request: the change is
                            // in force, it just will not survive a restart, and a bare 500
                            // would leave the caller thinking neither happened.
                            if (!_session.Settings.Save())
                            {
                                persisted = false;
                                whyNotPersisted = $"{_session.Settings.NotSavingBecause}, so it is left alone until the next start";
                            }
                        }
                        catch (Exception ex)
                        {
                            // Named, not quoted: the exception text carries the full
                            // profile path, account name included, and this answer goes
                            // into a transcript.
                            // The message for the expected write failures; everything for anything
                            // else, whose stack is the only clue. The log is local either way.
                            DebugLog.WriteAlways(ex is IOException or UnauthorizedAccessException
                                ? $"Settings not persisted: {ex.Message}"
                                : $"Settings not persisted: {ex}");
                            persisted = false;
                            whyNotPersisted = ex is UnauthorizedAccessException
                                ? "the settings file could not be written to"
                                : "the settings file could not be written";
                        }
                    }
                    return Task.FromResult(error);
                });
                if (failure is not null)
                    return (400, "application/json", Json(new { error = failure }));

                return (200, "application/json", Json(new
                {
                    ok = true,
                    applied,
                    // Named rather than silently ignored: a setting that did nothing and
                    // said nothing is worse than one that refused.
                    restartNeededFor = needsRestart,
                    unknown,
                    // In force but not written down, which is worth knowing before
                    // anybody restarts expecting it to stick.
                    persisted,
                    persistError = whyNotPersisted,
                }));
            }

            case ("POST", "/window"):
            {
                if (_session.Windows.Count == 0)
                    return (503, "application/json", Json(new { error = "no window is ready" }));
                bool focus = BoolFrom("focus") ?? false;
                var opened = await OnUiAsync(() => MainForm.OpenWindowAsync(_session, focus));
                if (opened.Tabs is not { } manager)
                {
                    // Closed here too, not only when its page fails: an empty window with
                    // no tab manager is nothing anybody can use, and it would sit behind
                    // the person's work until they found it.
                    await OnUiAsync(() => { CloseIfOpen(opened); return Task.FromResult(true); });
                    return (503, "application/json", Json(new { error = "the new window did not start" }));
                }

                // Always a tab, even with no url. A secondary window skips the startup
                // restore, so without this it came up empty and nothing could put a tab
                // in it: /open targets the focused window, which is the user's, which is
                // the one this exists to stay out of.
                string url = BodyString("url") ?? HomePage.Url;
                var first = await OnUiAsync(() => manager.OpenOrDiscardAsync(
                    UrlHeuristics.ToNavigableUrl(url, _session.Settings.SearchUrlTemplate), activate: true));
                if (first is null)
                {
                    // Taken away again rather than left behind. Every retry otherwise made
                    // another window, and a new tab starts with no failures on record, so
                    // the back-off never applied: five retries were five engine starts.
                    // Taking its only tab away closes a window by itself; this is for the
                    // case where that did not happen. Only while it is empty, though: a tab
                    // the person dragged into it during a slow build is theirs now.
                    await OnUiAsync(() =>
                    {
                        if (opened.Tabs?.Tabs.Count is null or 0)
                            CloseIfOpen(opened);
                        return Task.FromResult(true);
                    });
                    return PageCouldNotStart(null, DateTime.UtcNow);
                }
                // On the UI thread, which owns the window list: this is the same race
                // /open was moved off the request thread to avoid.
                int index = await OnUiAsync(() => Task.FromResult(_session.Windows.ToList().IndexOf(opened)));
                return (200, "application/json", Json(new { window = index, id = first.Id }));
            }

            case ("GET", "/console"):
            {
                if (Target() is not { } consoleTab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(consoleTab) is { } refusedConsole)
                    return refusedConsole;
                // On the UI thread, as /tabs does. RecentErrors is a plain List the UI
                // thread appends to and clears on every navigation, so copying it from a
                // request thread races a page that is logging errors in a loop: a torn
                // list on a good day, "Destination array was not long enough" on a bad one.
                var reported = await OnUiAsync(() =>
                {
                    // Again on this thread, as every other read here does.
                    consoleTab.RefuseOwnPage();
                    return Task.FromResult(new
                    {
                        url = consoleTab.Url,
                        // The same list the status bar counts, which until now could only be
                        // reached by injecting a second error reporter of one's own.
                        errors = consoleTab.RecentErrors.ToArray(),
                    });
                });
                return (200, "application/json", Json(reported));
            }

            case ("POST", "/open"):
            {
                string url = BodyString("url") ?? HomePage.Url;
                // Opening a tab normally means "look at this", so activating is the right
                // default. An agent working while someone else uses the browser wants the
                // opposite, and taking the screen away mid-sentence is how that goes wrong.
                bool background = BoolFrom("background") ?? false;

                // Which window. Without this, a tab always landed in the focused one, so
                // the window /window opens to keep out of the user's way could never be
                // filled: everything an agent opened went straight back into the window
                // it was trying to leave alone.
                // Resolved on the UI thread, which owns the window list: reading Count and
                // then indexing from a request thread lets a window open or close between
                // the two, and that surfaces as an opaque 500.
                MainForm? window = await OnUiAsync(() => Task.FromResult(
                    NumberFrom("window") is { } which
                        ? (which == (int)which && (int)which >= 0 && (int)which < _session.Windows.Count
                            ? _session.Windows[(int)which]
                            : null)
                        : ActiveEntry()?.Window ?? _session.Windows.FirstOrDefault()));
                if (window is null && NumberFrom("window") is not null)
                    return (404, "application/json", Json(new { error = "no such window" }));

                if (window?.Tabs is not { } manager)
                    return (503, "application/json", Json(new { error = "no window is ready" }));
                // A 200 here used to hand out a tab whose page never started, and a /page on
                // it then read about:blank. Now such a tab is taken away again rather than
                // left in the user's window, for the same reason as /window: each retry made
                // another blank tab, with a clean record.
                var tab = await OnUiAsync(() => manager.OpenOrDiscardAsync(
                    UrlHeuristics.ToNavigableUrl(url, _session.Settings.SearchUrlTemplate),
                    activate: !background));
                if (tab is null)
                    return PageCouldNotStart(null, DateTime.UtcNow);
                return (200, "application/json", Json(new
                {
                    id = tab.Id,
                    index = AllTabs().FindIndex(e => e.Tab == tab),
                }));
            }

            case ("POST", "/activate"):
            {
                // Documented as taking an index. Defaulting to the active tab would make
                // "activate" a no-op and "close" destructive, so require it explicitly.
                if (StringFrom("id") is null && !ResolveIndex(query, body).Supplied)
                    return (400, "application/json", Json(new { error = "id or index required" }));
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
                if (StringFrom("id") is null && !ResolveIndex(query, body).Supplied)
                    return (400, "application/json", Json(new { error = "id or index required" }));
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
                string target = UrlHeuristics.ToNavigableUrl(url, _session.Settings.SearchUrlTemplate);

                // Sleeping and hoping is the alternative, and a sleep that is too short
                // reads as a broken page rather than a slow one.
                if (BoolFrom("wait") == true)
                {
                    var allowed = TimeSpan.FromSeconds(Math.Clamp(NumberFrom("timeout") ?? 30, 1, 120));
                    bool? loaded = await OnUiAsync(() => tab.NavigateAndWaitAsync(target, allowed));
                    return NavigateAnswer(waited: true, loaded, tab, DateTime.UtcNow);
                }

                // Answered honestly even without a wait: the engine refusing the url is
                // the one thing this can know straight away, and reporting ok for it would
                // leave the caller reading the old page as the new one.
                bool went = await OnUiAsync(() => tab.NavigateAsync(target));
                return NavigateAnswer(waited: false, went ? true : null, tab, DateTime.UtcNow);
            }

            case ("GET", "/page"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                var (text, readable) = await ReadStringAsync(tab, "document.body ? document.body.innerText : ''");
                if (!readable)
                    return (503, "application/json", Json(new
                    {
                        error = "that tab's page did not finish loading in time to read",
                    }));
                return (200, "application/json", Json(new { url = tab.Url, title = tab.Title, text = Truncate(text, 200_000) }));
            }

            case ("GET", "/html"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                var (html, readable) = await ReadStringAsync(tab, "document.documentElement.outerHTML");
                if (!readable)
                    return (503, "application/json", Json(new
                    {
                        error = "that tab's page did not finish loading in time to read",
                    }));
                return (200, "application/json", Json(new { url = tab.Url, html = Truncate(html, 400_000) }));
            }

            case ("GET", "/screenshot"):
            {
                if (TargetEntry() is not { } shot || shot.Window.Tabs is null)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(shot.Tab) is { } refusedShot)
                    return refusedShot;

                // Activating switches the tab's window to it, which changes what the person
                // looking at that window sees, so it is asked for rather than assumed. It does
                // not raise the window over other apps: /activate does that, this does not.
                bool activate = BoolFrom("activate") ?? false;
                bool wantsChrome = BoolFrom("chrome") == true;
                bool isOnScreen = shot.Window.Tabs.ActiveTab == shot.Tab;

                // Checked before either branch, because a blank photograph of the window
                // is no better than a blank photograph of the page, and the chrome branch
                // used to return above this. A tab nobody has looked at has painted
                // nothing, and capturing it anyway produces a blank png served as a
                // perfectly good 200 with nothing to say it is wrong.
                if (!isOnScreen && !activate && !shot.Tab.HasRendered)
                {
                    // Worded from the state rather than from the flag: a tab discarded
                    // after fifteen minutes has rendered plenty, it just has no frame now.
                    string state = shot.Tab.State == TabState.Discarded
                        ? "that tab is asleep and has no rendered frame"
                        : "that tab has not been on screen yet";
                    return (503, "application/json", Json(new
                    {
                        error = $"{state}; read it with /page, or pass activate=1 to switch its window to it first",
                    }));
                }

                // A window shows one tab at a time, so asking for the chrome around a tab
                // that is not the one on screen has no answer. Returning the window anyway
                // would hand back a different page inside the right frame, at 200, with
                // nothing to say it was the wrong one.
                if (wantsChrome && !isOnScreen && !activate)
                    return (400, "application/json", Json(new
                    {
                        error = "that tab is not the one its window is showing; "
                            + "pass activate=1 to switch its window to it, or omit the tab to photograph what is on screen",
                    }));

                // Bring it forward and let its page arrive. The fixed 400ms this replaced
                // was enough for a switch between two loaded tabs and nowhere near a tab
                // whose view had to be rebuilt, so activate=1 traded the honest refusal
                // above for the blank png it exists to prevent.
                // Started before anything that can take time, including the view build
                // inside ActivateAsync, which for a discarded tab can be a cold engine
                // start. Computing it afterwards left that outside every budget, which is
                // the defect this same request had for /navigate one pass ago.
                var shotDeadline = DateTime.UtcNow + Tab.WakeTimeout;
                if (activate && !isOnScreen)
                {
                    await OnUiAsync(async () =>
                    {
                        await shot.Window.Tabs.ActivateAsync(shot.Tab);
                        return true;
                    });
                }
                bool ready = await OnUiAsync(() => shot.Tab.WaitForPageAsync(shotDeadline - DateTime.UtcNow));
                if (!ready)
                    return (503, "application/json", Json(new
                    {
                        error = "that tab's page did not finish loading in time to photograph",
                    }));
                if (activate && !isOnScreen)
                    await OnUiAsync(async () => { await Task.Delay(250); return true; }); // let it paint

                if (wantsChrome)
                {
                    // Checked again in the same step as the capture, after the wait above:
                    // long enough for the window to switch tabs, or for this tab to arrive at
                    // one of the browser's own pages, and the photograph includes the page.
                    bool switchedAway = false;
                    var (captured, why) = await OnUiAsync(() =>
                    {
                        if (shot.Window.Tabs?.ActiveTab != shot.Tab)
                        {
                            switchedAway = true;
                            return Task.FromResult<(byte[], string?)>(([], null));
                        }
                        shot.Tab.RefuseOwnPage();
                        return Task.FromResult(WindowCapture.Of(shot.Window));
                    });
                    if (switchedAway)   // the same answer as on entry
                        return (400, "application/json", Json(new
                        {
                            error = "that tab is no longer the one its window is showing; ask again, with activate=1 to switch its window back to it",
                        }));
                    return captured.Length == 0
                        ? (503, "application/json", Json(new { error = why ?? "the window could not be captured" }))
                        : (200, "image/png", captured);
                }

                var (png, hadPage) = await OnUiAsync(
                    () => shot.Tab.CaptureScreenshotAsync(shotDeadline - DateTime.UtcNow));
                if (!hadPage)
                    return (503, "application/json", Json(new
                    {
                        error = "that tab had no rendered page to photograph when the wait ran out",
                    }));
                return png.Length == 0
                    ? (503, "application/json", Json(new { error = "the page could not be captured" }))
                    : (200, "image/png", png);
            }

            case ("POST", "/eval"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string js = BodyString("js") ?? "";
                if (js.Length == 0)
                    return (400, "application/json", Json(new { error = "js required" }));

                // The result of a promise used to come back as {}, so anything async had
                // to park its answer on a global and be polled for from outside. Awaited
                // by default now; await=0 keeps the old immediate read for a caller who
                // wants the expression and not what it settles to.
                if (BoolFrom("await") == false)
                {
                    var (immediate, readable) = await OnUiAsync(
                        () => tab.ReadScriptAsync(js, Tab.WakeTimeout));
                    // Ready is not dropped here either. It was: a tab whose page never
                    // arrived answered {"ok": true, "result": null}, which is exactly what
                    // a script returning null looks like, on the one endpoint that is
                    // supposed to tell those apart.
                    if (!readable)
                        return (503, "application/json", Json(new
                        {
                            error = "that tab's page did not finish loading in time to read",
                        }));
                    // Same shape either way, and built in one place: this was a fifth
                    // hand-rolled copy of the envelope in the change that added the helper
                    // because four copies were four chances to drift.
                    return Evaluated(Tab.Succeeded(immediate));
                }

                var settled = TimeSpan.FromSeconds(Math.Clamp(NumberFrom("timeout") ?? 15, 1, 120));
                string outcome = await OnUiAsync(() => tab.ExecuteScriptAwaitingAsync(js, settled));
                return Evaluated(outcome);
            }

            case ("POST", "/click"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
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
                var (clicked, clickable) = await ActOnPageAsync(tab, js);
                if (!clickable)
                    return (503, "application/json", Json(new
                    {
                        error = "that tab's page did not finish loading in time to click",
                    }));
                await Task.Delay(900); // let the cursor glide and the click land before responding
                return (200, "application/json", Json(new { ok = clicked }));
            }

            case ("POST", "/type"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
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
                var (typed, typeable) = await ActOnPageAsync(tab, js);
                if (!typeable)
                    return (503, "application/json", Json(new
                    {
                        error = "that tab's page did not finish loading in time to type into",
                    }));
                await Task.Delay(900);
                return (200, "application/json", Json(new { ok = typed }));
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

    /// <summary>
    /// What a page returns is the page's decision, and "return document.body.innerHTML"
    /// on a big site is megabytes. /page and /html have always bounded their output; this
    /// did not, so a single expression could hand a client more than it can take and fail
    /// it with an opaque error rather than a readable one. Applied after the result is
    /// already in memory, so it bounds what the client receives, not what the page built.
    /// </summary>
    private const int MaxEvalBytes = 1024 * 1024;

    private static (int, string, byte[]) Evaluated(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return body.Length <= MaxEvalBytes
            ? (200, "application/json", body)
            : (413, "application/json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = false,
                error = $"the result is {body.Length / 1024.0 / 1024.0:0.0} MB, over the "
                    + $"{MaxEvalBytes / 1024 / 1024} MB limit. Return less of it, or read the page with /page.",
            }));
    }

    /// <summary>Caps what a screenshot may weigh before a client is asked to swallow it
    /// as base64. A 4K capture is megabytes, and most clients reject one opaquely.</summary>
    private const int MaxScreenshotBytes = 5 * 1024 * 1024;

    private static object Text(string description) => new { type = "string", description };
    /// <summary>
    /// Preferred over the index everywhere both are offered: an index is a position, and
    /// opening, closing or tearing off a tab renumbers every position after it, so an
    /// agent holding one acts on whatever slid into that slot.
    /// </summary>
    private static object Id() => new { type = "string", description = "Tab id from gergur_list_tabs. Stable for the life of the tab; prefer this to index." };

    private static object Index() => new { type = "integer", description = "Tab index from gergur_list_tabs. Omit for the active tab of the focused window." };
    /// <summary>For the tools where the index is required, so the schema and the prose agree.</summary>
    private static object NamedIndex() => new { type = "integer", description = "Tab index from gergur_list_tabs." };

    private static object Tool(string name, string description, object properties, string[] required)
        => new { name, description, inputSchema = new { type = "object", properties, required } };

    /// <summary>
    /// Splits a request target into its path and its query.
    ///
    /// A pair with no "=" is a flag that is present, which is how a url writes one and how
    /// BoolValue reads one. It used to be dropped entirely, so <c>?chrome</c> silently
    /// captured the page instead of the window, and the test that said otherwise passed
    /// because it built its dictionary by hand and never came through here.
    /// </summary>
    internal static Dictionary<string, string> ParseQuery(string rawPath, out string path)
    {
        var query = new Dictionary<string, string>();
        int qm = rawPath.IndexOf('?');
        if (qm < 0)
        {
            path = rawPath;
            return query;
        }

        path = rawPath[..qm];
        foreach (var pair in rawPath[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
                query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            else if (eq < 0)
                query[Uri.UnescapeDataString(pair)] = "";
            // eq == 0 is "=value", a pair with no name. Nothing can ask for it, so it goes.
        }
        return query;
    }

    /// <summary>
    /// Which tab a request means: its position in <paramref name="tabIds"/>, or -1 when
    /// the answer is "none", which every endpoint turns into a 404.
    ///
    /// The rule that matters is that -1 is never quietly replaced by the active tab. An
    /// agent that believes it is closing tab 3, or the tab it knows as t9, must not close
    /// the page the user is reading because its name turned out to be stale. This project
    /// shipped exactly that bug once with indexes, which is why the index path has had a
    /// test since; the id path is newer and is the one an agent will actually hold onto
    /// across a tab opening or being torn off.
    ///
    /// An id beats an index when both arrive, because it is the one that survives.
    /// </summary>
    internal static int TargetIndex(
        IReadOnlyList<string> tabIds,
        string? id,
        (bool Supplied, int? Index) byIndex,
        int activeIndex)
    {
        if (id is not null)
        {
            // Supplied and empty is still supplied: it names nothing, so it is an error,
            // not an invitation to pick the tab somebody is looking at.
            if (id.Length == 0)
                return -1;
            for (int i = 0; i < tabIds.Count; i++)
            {
                if (string.Equals(tabIds[i], id, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        if (!byIndex.Supplied)
            return activeIndex;
        if (byIndex.Index is not { } at)
            return -1; // supplied but unreadable: an error, not the active tab
        return at >= 0 && at < tabIds.Count ? at : -1;
    }

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
    /// A string named in the query or in the json body, whichever carries it. The query
    /// wins, because a caller who put it in the url meant this request specifically.
    /// </summary>
    internal static string? StringValue(
        IReadOnlyDictionary<string, string> query, JsonDocument? body, string name)
    {
        if (query.TryGetValue(name, out var fromQuery))
            return fromQuery;
        if (body is { RootElement.ValueKind: JsonValueKind.Object }
            && body.RootElement.TryGetProperty(name, out var fromBody)
            && fromBody.ValueKind == JsonValueKind.String)
            return fromBody.GetString();
        return null;
    }

    /// <summary>
    /// A flag named in the query or the body. Null means it was not asked for at all,
    /// which is not the same as false: every caller of this decides its own default, and
    /// "chrome=0" has to be able to mean something different from leaving it out.
    ///
    /// A bare "?chrome" is true, because that is what a flag in a url looks like, and an
    /// unparseable value is null rather than false: silently reading "yes" as "no" is how
    /// a request does the opposite of what it said.
    /// </summary>
    internal static bool? BoolValue(
        IReadOnlyDictionary<string, string> query, JsonDocument? body, string name)
    {
        if (query.TryGetValue(name, out var fromQuery))
        {
            if (fromQuery.Length == 0 || fromQuery == "1")
                return true;
            if (fromQuery == "0")
                return false;
            return bool.TryParse(fromQuery, out bool parsed) ? parsed : null;
        }
        if (body is { RootElement.ValueKind: JsonValueKind.Object }
            && body.RootElement.TryGetProperty(name, out var fromBody))
        {
            return fromBody.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(fromBody.GetString(), out bool parsed) ? parsed : null,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// A number named in the query or the body. Null when it is missing or unreadable,
    /// so a timeout of "soon" falls back to the default rather than to zero.
    /// </summary>
    internal static double? NumberValue(
        IReadOnlyDictionary<string, string> query, JsonDocument? body, string name)
    {
        if (query.TryGetValue(name, out var fromQuery))
            return double.TryParse(fromQuery, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : null;
        if (body is { RootElement.ValueKind: JsonValueKind.Object }
            && body.RootElement.TryGetProperty(name, out var fromBody)
            && fromBody.ValueKind == JsonValueKind.Number
            && fromBody.TryGetDouble(out double number))
            return number;
        return null;
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
        // Either names the tab. Requiring the index outright would make a caller that
        // correctly used the stable id supply a position as well, and the position is
        // the thing that goes stale.
        "gergur_activate_tab" => ["id|index"],
        "gergur_close_tab" => ["id|index"],
        "gergur_run_javascript" => ["js"],
        "gergur_change_settings" => ["settings"],
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
        foreach (string requirement in RequiredArgsForTool(name))
        {
            // "a|b" is satisfied by either. Reported as "a or b", because a caller told
            // it needs "id|index" has been told the name of nothing.
            string[] alternatives = requirement.Split('|');
            if (!alternatives.Any(key => Supplied(args, key)))
                return string.Join(" or ", alternatives);
        }
        return null;

        static bool Supplied(JsonElement? args, string key)
            => args is { ValueKind: JsonValueKind.Object } supplied
                && supplied.TryGetProperty(key, out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !(EmptyIsMeaningless(key)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString()?.Length is null or 0);
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
        "gergur_open_window" => ("POST", "/window"),
        "gergur_read_settings" => ("GET", "/settings"),
        "gergur_change_settings" => ("POST", "/settings"),
        "gergur_page_errors" => ("GET", "/console"),
        _ => ("", ""),
    };

    /// <summary>Every tool maps onto an endpoint this server already serves.</summary>
    internal static object[] McpTools() =>
    [
        Tool("gergur_list_tabs",
            "List every open tab across all Gergur windows: flat index, which window, url, title, sleep state, and any errors the page reported.",
            new { }, []),
        Tool("gergur_open_tab",
            "Open a url in a new tab. Bare terms are treated as a search. Set background to leave the user on the tab they are reading.",
            new
            {
                url = Text("The url or search terms to open."),
                background = new { type = "boolean", description = "Open without switching to it. Defaults to false." },
                window = new
                {
                    type = "integer",
                    description = "Which window to open it in, as returned by gergur_open_window. Omit for the focused window.",
                },
            },
            ["url"]),
        Tool("gergur_navigate", "Point an existing tab at a url. Set wait to come back only once the page has loaded.",
            new
            {
                url = Text("The url to go to."),
                id = Id(),
                index = Index(),
                wait = new { type = "boolean", description = "Wait for the load to finish and report whether it did." },
            },
            ["url"]),
        Tool("gergur_activate_tab", "Bring a tab to the front, and its window with it. Supply id or index; this changes what the user is looking at.",
            new { id = Id(), index = NamedIndex() }, []),
        Tool("gergur_close_tab", "Close a tab. Supply id or index; it will not guess.",
            new { id = Id(), index = NamedIndex() }, []),
        Tool("gergur_read_page", "Read a page as rendered text. Prefer this over a screenshot for reading: it does not disturb which tab the user is looking at.",
            new { id = Id(), index = Index() }, []),
        Tool("gergur_read_html", "Read a page's full HTML.", new { id = Id(), index = Index() }, []),
        Tool("gergur_screenshot",
            "Capture a tab as a PNG. Reads what the tab last rendered without disturbing the user; set activate to switch its window to it first, or chrome to photograph the whole window including the tab strip and toolbar.",
            new
            {
                id = Id(),
                index = Index(),
                activate = new { type = "boolean", description = "Switch its window to this tab first. Changes what that window shows, but does not raise it over other apps." },
                chrome = new { type = "boolean", description = "Photograph the window rather than the page." },
            },
            []),
        Tool("gergur_run_javascript",
            "Evaluate JavaScript in a page and return the result. A promise is awaited, so fetch and the like come back with their value rather than an empty object.",
            new { js = Text("The expression to evaluate."), id = Id(), index = Index() }, ["js"]),
        Tool("gergur_click", "Click the first element matching a CSS selector. Animates a visible cursor to it first.",
            new { selector = Text("A CSS selector."), id = Id(), index = Index() }, ["selector"]),
        Tool("gergur_type_text", "Fill an input or textarea, in a way React and similar frameworks notice.",
            new { selector = Text("A CSS selector for the field."), text = Text("The text to enter."), id = Id(), index = Index() },
            ["selector", "text"]),
        Tool("gergur_open_window",
            "Open another browser window on the same session. Leave focus false to work in it without taking the screen from whoever is using the browser.",
            new
            {
                url = Text("Optional url to open in the new window's first tab."),
                focus = new { type = "boolean", description = "Bring the new window to the front. Defaults to false." },
            },
            []),
        Tool("gergur_read_settings",
            "Read every Gergur setting and its current value, which ones only take effect after a restart, and two facts about this run: vpnInForce (the engine points at the vpn tunnel) and tunnelRunning (the tunnel's process is running). Traffic goes through the vpn only when both are true; VpnEnabled alone does not say.",
            new { }, []),
        Tool("gergur_change_settings",
            "Change Gergur settings. Names come from gergur_read_settings; anything unrecognised is reported back rather than ignored, and a value of the wrong type changes nothing at all.",
            new
            {
                settings = new
                {
                    type = "object",
                    description = "Setting names mapped to their new values, for example {\"BlocklistEnabled\": true}.",
                },
            },
            ["settings"]),
        Tool("gergur_page_errors",
            "Read the JavaScript errors, unhandled rejections and failed subresource loads a page has reported. This is what the status bar's issue count counts, and it clears on every navigation.",
            new { id = Id(), index = Index() }, []),
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

            // Anything this call waits on has to finish inside the tool's own budget.
            // /navigate's wait defaults to 30 seconds and the budget is 20, so every page
            // slower than 20 seconds came back as "abandoned" rather than as the honest
            // {"ok": true, "loaded": false} the wait exists to give. The query wins over
            // the body, so writing it here caps whatever the caller asked for.
            double ceiling = ToolTimeout.TotalSeconds - 2;
            double asked = NumberValue(query, args, "timeout") ?? ceiling;
            query["timeout"] = Math.Min(asked, ceiling)
                .ToString("0.###", CultureInfo.InvariantCulture);

            // A page running a script that never returns would otherwise hold this
            // connection, its stream and its task for the life of the process.
            var dispatch = RouteAsync(httpMethod, path, query, args);
            using var budget = new CancellationTokenSource();
            var spent = Task.Delay(ToolTimeout, budget.Token);
            if (await Task.WhenAny(dispatch, spent) != dispatch)
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

            // The dispatch won, so the budget timer and its continuation are dead weight
            // for the rest of the 20 seconds.
            budget.Cancel();

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
            DebugLog.WriteAlways($"{name} failed: {ex}");
            return McpError($"{name} failed inside the browser; the cause is in %LOCALAPPDATA%/Gergur/debug.log");
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

    /// <summary>
    /// What an endpoint says when the tab's page could not be started at all, as opposed
    /// to a url the engine refused. Told apart because the fix is different: a caller told
    /// its url was bad goes and changes a url that was fine.
    /// </summary>
    /// <param name="tab">The tab that is still there, or null when what was made for the
    /// request has been taken away again.</param>
    /// <param name="nowUtc">The clock, passed in so the wording can be pinned by a test.</param>
    internal static (int, string, byte[]) PageCouldNotStart(Tab? tab, DateTime nowUtc)
    {
        if (tab is null)
        {
            // No retryInSeconds. The back-off belongs to a tab, and a discarded one's wait
            // says nothing about the next /open, which makes a new tab that tries at once.
            return (503, "application/json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                error = "the page could not start, so nothing was opened",
            }));
        }

        // Worded from the real wait, which after three failures in a row is five minutes,
        // not "a few seconds". Nothing retries by itself: the next read or navigate is the
        // retry, and before this many seconds it is refused without trying.
        int seconds = Tab.SecondsUntil(tab.NextBuildAttemptUtc, nowUtc);
        return (503, "application/json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            error = seconds == 0
                ? "that tab's page could not start; it can be tried again now"
                : $"that tab's page could not start; it can be tried again after {Tab.DescribeWait(seconds)}",
            id = tab.Id,
            retryInSeconds = seconds,
        }));
    }

    /// <summary>
    /// What /navigate answers, apart from the endpoint so a test can pin it.
    /// </summary>
    /// <param name="waited">Whether the caller asked to wait for the page.</param>
    /// <param name="loaded">For a wait, whether that page loaded, or null when the
    /// navigation never went out. Without a wait, true when it went out and null when not.</param>
    internal static (int, string, byte[]) NavigateAnswer(bool waited, bool? loaded, Tab tab, DateTime nowUtc)
    {
        // Only a build that actually failed. One that is just slow, a retry after an
        // earlier failure included, carries on and navigates when it arrives, so the honest
        // answer then is "not loaded yet", not "could not start; try again", which would
        // have the caller start another.
        if (loaded != true && tab.CouldNotStart)
            return PageCouldNotStart(tab, nowUtc);
        // Null means the navigation never went out. Folding it into loaded:false would
        // read as a slow page and have the caller wait and retry.
        if (loaded is null)
            return (503, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = "the engine would not take that url" }));
        return waited
            ? (200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, loaded = loaded.Value }))
            : (200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { ok = true }));
    }

    /// <summary>Closes a window that has not already closed itself. Closing a disposed form throws.</summary>
    private static void CloseIfOpen(Form window)
    {
        if (!window.IsDisposed && !window.Disposing)
            window.Close();
    }

    /// <summary>
    /// Runs a script for an endpoint that changes something, and says whether the page was
    /// there to change. /click and /type used to drop that, so a tab whose page had not
    /// settled answered ok:false, which reads exactly like "no element matched" and has an
    /// agent retrying selectors against a page that was never there.
    /// </summary>
    private async Task<(bool Worked, bool Ready)> ActOnPageAsync(Tab tab, string js)
    {
        var (raw, ready) = await OnUiAsync(() => tab.ReadScriptAsync(js, Tab.WakeTimeout));
        return (raw == "true", ready);
    }

    /// <summary>
    /// A string out of a page, and whether the page was there to be read.
    ///
    /// Ready used to be dropped here, so /page on a tab still loading after fifteen
    /// seconds answered 200 with the real url, the real title and empty text. Nothing in
    /// that says it is wrong, and CLAUDE.md points at /page as the read that disturbs
    /// nothing, which makes it the one most likely to be believed.
    /// </summary>
    private async Task<(string Text, bool Ready)> ReadStringAsync(Tab tab, string js)
    {
        var (raw, ready) = await OnUiAsync(() => tab.ReadScriptAsync(js, Tab.WakeTimeout));
        return (AsString(raw), ready);
    }

    private static string AsString(string raw)
    {
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
        var query = ParseQuery(rawPath, out path);

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
            413 => "Payload Too Large",
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
