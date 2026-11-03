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

    /// <summary>
    /// The request being answered on this flow: who sent it, and an MCP session id to hand
    /// back when the request was an initialize. An object rather than values, because the
    /// session is started deep inside the JSON-RPC handler and an AsyncLocal set there would
    /// not flow back out to the code that writes the response.
    /// </summary>
    private sealed class RequestContext
    {
        public required AgentCaller Caller;
        public string? SessionToSend;
    }

    private static readonly AsyncLocal<RequestContext?> CurrentRequest = new();
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

            headers.TryGetValue("mcp-session-id", out var sessionId);
            headers.TryGetValue("x-gergur-agent", out var agentName);
            var context = new RequestContext { Caller = _session.Agents.Identify(sessionId, agentName, viaMcp: path == "/mcp") };
            CurrentRequest.Value = context;
            // A plain script can say what it is doing on any request; an MCP client says it
            // with gergur_set_task, which comes back through here as /task.
            if (headers.TryGetValue("x-gergur-task", out var taskHeader))
                _session.Agents.SetTask(context.Caller, taskHeader, DateTime.UtcNow);

            var (status, contentType, payload) = await RouteAsync(method, path, query, body);
            await WriteAsync(stream, status, contentType, payload, context.SessionToSend);
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
            var result = await RouteRequestAsync(method, path, query, body);
            NoteForAgentsPanel(method, path, query, body, result);
            return result;
        }
        catch (InternalPages.OwnPageRefusedException refused)
        {
            return Refusal(refused);
        }
        catch (TabInput.InputFailedException failed)
        {
            return (503, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = failed.Message }));
        }
    }

    /// <summary>
    /// Records a request that did something in the agents panel's log. /mcp itself is not
    /// an action: each tool call inside it comes back through here as the route it maps to.
    /// Only what was done and to which tab: never what was typed, run or read.
    /// </summary>
    private void NoteForAgentsPanel(string method, string path, Dictionary<string, string> query, JsonDocument? body, (int, string, byte[]) result)
    {
        try
        {
            var (status, _, payload) = result;
            if (path == "/mcp" || status is < 200 or >= 300)
                return;
            var caller = CurrentRequest.Value?.Caller ?? _session.Agents.Identify(null, null, viaMcp: false);
            string? tabId = StringValue(query, body, "id");
            if (path is "/open" or "/window")
            {
                tabId = IdIn(payload) ?? tabId;
                NoteDriver(tabId);
            }
            // Waiting again for an answer is not something new to show in the person's log.
            if (path == "/ask" && StringValue(query, body, "id") is { Length: > 0 })
                return;
            // The caller's own text otherwise, shown in the person's panel: only a tab id goes in.
            if (!AgentActivity.IsTabId(tabId))
                tabId = null;
            string text = AgentActivity.Describe(method, path, tabId, StringValue(query, body, "url"));
            _session.Agents.Record(caller, text, tabId, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // The log is a convenience; the request it describes has already succeeded.
            DebugLog.Write($"agents panel note failed: {ex.Message}");
        }
    }

    /// <summary>
    /// An endpoint that acts on a tab is about to: the window is told it was an agent (see
    /// <see cref="Tab.NoteAgentAction"/>), and the tab is put down as driven by this caller, so
    /// a download it sets off is listed to that agent. Only acting endpoints, on the tab they
    /// resolved, so naming it by index or not at all counts, and a read never does: a download
    /// the person starts after an agent merely read their tab stays theirs.
    /// </summary>
    private static void Acting(Tab tab)
    {
        tab.NoteAgentAction();
        if (CurrentRequest.Value?.Caller is { } caller)
            tab.NoteDrivenBy(caller.Key);
    }

    /// <summary>Marks a tab an /open or /window just made as driven by its caller.</summary>
    private void NoteDriver(string? tabId)
    {
        if (!AgentActivity.IsTabId(tabId) || CurrentRequest.Value?.Caller is not { } caller)
            return;
        try
        {
            AllTabs().FirstOrDefault(e => e.Tab.Id == tabId).Tab?.NoteDrivenBy(caller.Key);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"noting who drives a tab failed: {ex.Message}");
        }
    }

    /// <summary>How long after an agent's request a download in that tab is still put down to it.</summary>
    internal static readonly TimeSpan DownloadDriverWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whose download it is: the agent whose window it started in, else the agent that acted
    /// in its tab within <see cref="DownloadDriverWindow"/>, else the person's (null), and an
    /// agent is never shown one of the person's.
    /// </summary>
    internal static string? DownloadOwner(bool inAgentWindow, string? windowOwner, string? recentDriver)
        => inAgentWindow && windowOwner is not null ? windowOwner : recentDriver;

    /// <summary>The "id" a response carries, for the tab an /open or /window just made.</summary>
    internal static string? IdIn(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
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
            if (at < 0 || at >= all.Count)
                return null;
            // Every read of a tab and every act on one goes through here: in one of the
            // person's own windows, it says "Agent watching" over the page (AgentWatchGlow).
            var (window, tab) = all[at];
            if (!window.OpenedByAgent)
                window.AgentLookedIn(tab, CurrentRequest.Value?.Caller?.Name ?? "An agent");
            return all[at];
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
            => body is not null && body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

        byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);

        // Signs in with what the engine filled in from the person's saved passwords. Nothing
        // here reads the password: the page is asked whether its field holds something, and
        // the form's own button (or Enter in the field) sends it. See "Saved passwords".
        async Task<(int, string, byte[])> LoginAsync()
        {
            if (TargetEntry() is not { } entry)
                return (404, "application/json", Json(new { error = "no such tab" }));
            var tab = entry.Tab;
            if (OwnPageRefused(tab) is { } refused)
                return refused;
            string? asked = null;
            if (AccountNotText(body) || BodyString("account") is { } rawAccount && (asked = CleanAccount(rawAccount)) is null)
                return (400, "application/json", Json(new { error = "account is the saved username to sign in as: one printable line of at most 200 characters" }));
            Acting(tab);
            // Google's chooser drawn by the browser over the page (FedCM), in an agent's window,
            // when it is the sign-in meant: One Tap can sit over a password form, and then the
            // password is what is wanted.
            if (tab.FedCm is { } fedCm && fedCm.Current is { } shown
                && FedCmWatch.Applies(shown, GoogleSignIn.CleanEmail(asked)
                    ?? (GoogleSignIn.SiteOfPage(tab.CommittedSourceForOtherThreads) is { Length: > 0 } pageSite ? _session.GoogleAccounts.For(pageSite) : null), GoogleSignIn.Origin))
                return await FedCmLoginAsync(tab, fedCm, shown, asked);
            if (GoogleSignIn.IsGooglePage(tab.CommittedSourceForOtherThreads))
                return await GoogleLoginAsync(tab, asked);
            string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var (raw, ready) = await OnUiAsync(() => tab.ReadScriptAsync(LoginStateJs(nonce), Tab.WakeTimeout));
            if (!ready)
                return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time" }));
            var state = ParseLoginState(raw);
            // No password in the page itself: a sign-in form inside one of its frames, often a
            // sign-in provider's from another site.
            if (state is { Fields: 0 } && await FrameLoginAsync(entry.Window, tab, nonce, asked) is { } framed)
                return framed;
            // No password here, and Google's chooser up over the page: what it offers is said
            // (no account known for the site, the one named not among them) rather than that
            // there is no password field.
            if (state is { Fields: 0, Elsewhere: false } && tab.FedCm is { } watching && watching.Current is { } up)
                return await FedCmLoginAsync(tab, watching, up, asked);
            string? wanted = state is null ? null : WantedAccount(state, asked);
            string? chosen = null;
            bool picked = false;
            if (state is not null && PlanPick(state, wanted) is var plan && plan != PickPlan.None)
            {
                // Filling another account is acting on the page, so not on one an agent scripted.
                if (tab.AgentScriptedThisDocument)
                    return (409, "application/json", Json(new { error = ScriptedLoginPage }));
                var pick = await PickAccountAsync(entry.Window, tab, nonce, plan, wanted);
                if (pick.Refused is { } stopped)
                    return stopped;
                chosen = pick.User;
                picked = pick.Borrowed;
                if (wanted is not null && chosen != wanted)
                    return (200, "application/json", Quiet(new { ok = false, filled = false, error = AccountNotSaved }));
                (raw, ready) = await OnUiAsync(() => tab.ReadScriptAsync(LoginStateJs(nonce), Tab.WakeTimeout));
                if (!ready)
                    return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time" }));
                state = ParseLoginState(raw);
                if (chosen is not null && state is not null)
                    state = state with { User = state.User ?? chosen };
            }
            if (LoginStop(state, submit: BoolFrom("submit") != false, tab.AgentScriptedThisDocument) is { } stop)
                return (stop.Status, "application/json", Quiet(stop.Answer));
            bool hasButton = state!.Button;   // LoginStop stops on no state

            int dialogMark = tab.DialogCount;
            string? via = null, note = null;
            if (hasButton)
            {
                string button = $"[data-gergur-login=\"go-{nonce}\"]";
                var (first, _) = await LocateAsync(tab, button, animate: true);
                if (first is not null)
                {
                    await Task.Delay(CursorGlide);
                    var (spot, _) = await LocateAsync(tab, button, animate: false);
                    if (spot is { CanClickAt: true })
                    {
                        // Looked at again directly before acting: a script run since the
                        // first look could have pointed the form somewhere else.
                        if (tab.AgentScriptedThisDocument)
                            return (409, "application/json", Json(new { error = ScriptedLoginPage }));
                        try
                        {
                            await OnUiAsync(() => TabInput.ClickAsync(tab, spot.X, spot.Y));
                            via = "button";
                        }
                        catch (TabInput.InputFailedException failed) when (failed.Busy)
                        {
                            return (503, "application/json", Json(new { error = failed.Message }));
                        }
                        catch (TabInput.InputFailedException failed) when (failed.MayHaveLanded)
                        {
                            // Never pressed again with Enter: that would send it twice.
                            via = "button";
                            note = $"{failed.Message}. The click may still have landed, so it was not tried again.";
                        }
                        catch (TabInput.InputFailedException)
                        {
                            // Nothing went down, so Enter in the field is still the one try.
                        }
                    }
                }
            }
            if (via is null)
            {
                var (focused, _) = await OnUiAsync(() => tab.ReadScriptAsync(FocusJs($"[data-gergur-login=\"pass-{nonce}\"]", selectAll: false), Tab.WakeTimeout));
                if (focused != "true")
                    return (200, "application/json", Json(new { ok = false, filled = true, error = "the password field would not take the keyboard focus to press Enter in, and there is no sign-in button to click" }));
                if (tab.AgentScriptedThisDocument)
                    return (409, "application/json", Json(new { error = ScriptedLoginPage }));
                await OnUiAsync(async () => { await TabInput.PressAsync(tab, TrustedInput.ParseChord("Enter")!); return true; });
                via = "enter";
            }
            await Task.Delay(1500); // for the page to move on, or say the password was wrong
            var (stillRaw, _) = await OnUiAsync(() => tab.ReadScriptAsync(StillAskingJs(nonce), TimeSpan.FromSeconds(3)));
            return (200, "application/json", Quiet(new
            {
                ok = true,
                filled = true,
                user = state.User,
                // Chosen from the person's saved accounts, with the keyboard borrowed for it.
                picked = picked ? true : (bool?)null,
                submitted = true,
                via,
                // The same password field still on screen: refused (a wrong password, a captcha
                // or a code to enter), or a page that signs in without leaving. Read the page.
                stillAsking = stillRaw == "true",
                note,
                url = tab.CommittedSourceForOtherThreads,
                dialogs = DialogsOut(tab.DialogsSince(dialogMark)),
            }));
        }

        // A sign-in form inside a frame the page holds (not a frame inside that). The engine
        // never fills another site's frame by itself (measured: the same saved login filled the
        // provider's page on its own and stayed out of it framed), only from its list once the
        // field is clicked, so the account is chosen from that list, as on a page
        // (AccountPick, the keyboard borrowed for about a second, in the agent's own window):
        // the one named, else the list's first. Then the frame's button, or Enter in its
        // password field, with real clicks placed through the frame. Null when no frame asks
        // for a password.
        async Task<(int, string, byte[])?> FrameLoginAsync(MainForm window, Tab tab, string nonce, string? asked)
        {
            var answers = await OnUiAsync(() => tab.ReadFramesAsync(FrameLoginStateJs(nonce), TimeSpan.FromSeconds(3)));
            var asking = answers.Select(a => (a.Frame, Read: ParseFrameLogin(a.Result)))
                .Where(a => a.Read is { State.Fields: > 0 }).ToList();
            if (asking.Count == 0)
                return null;
            if (asking.Count > 1)
                return (409, "application/json", Json(new { error = SeveralFramesAsk }));
            var frame = asking[0].Frame;
            var read = asking[0].Read!;
            var state = read.State;
            string url = tab.CommittedSourceForOtherThreads;
            Task<(string Result, bool Ready)> InFrame(string js, TimeSpan budget) => tab.ReadFrameScriptAsync(frame, js, budget);

            // Where an element of the frame is in the page's view, the frame brought into view
            // and the element into the frame's: the frame's own box in the page, the element's
            // centre in the frame.
            async Task<(FrameClick Why, (double X, double Y) Point)> PlaceAsync(string selector)
            {
                var (boxRaw, boxReady) = await OnUiAsync(() => tab.ReadScriptAsync(FrameBoxJs(read.Origin, null), Tab.WakeTimeout));
                if (!boxReady || ParseFrameBox(boxRaw) is not { } box || !SameFrame(box, read))
                    return (FrameClick.NotPlaced, default);
                var (spotRaw, inTime) = await OnUiAsync(() => InFrame(LocateJs(selector, animate: false), Tab.WakeTimeout));
                if (!inTime || ParseLocated(spotRaw) is not { } spot || PointInFrame(box, spot) is not { } point)
                    return (FrameClick.NotReachable, default);
                return (FrameClick.Clicked, point);
            }

            // Looked at directly before acting on the frame, on the UI thread: the page's hit
            // test says the point is the frame's (nothing of the page over it), the page is not
            // scripted, and neither it nor the frame moved on.
            async Task<FrameClick> LastLookAsync((double X, double Y) point)
            {
                var (hitRaw, _) = await tab.ReadScriptAsync(FrameBoxJs(read.Origin, point), TimeSpan.FromSeconds(2));
                if (ParseFrameBox(hitRaw) is not { Hit: true })
                    return FrameClick.NotReachable;
                if (tab.AgentScriptedThisDocument)
                    return FrameClick.Scripted;
                if (tab.CommittedSourceForOtherThreads != url || Tab.FrameGone(frame))
                    return FrameClick.MovedOn;
                return FrameClick.Clicked;
            }

            (int, string, byte[]) Refusal(FrameClick why) => why switch
            {
                FrameClick.Scripted => (409, "application/json", Json(new { error = ScriptedLoginPage })),
                FrameClick.MovedOn => (200, "application/json", Quiet(new { ok = false, filled = state.Filled, user = state.User, frame = read.Origin, error = FrameMovedOn })),
                FrameClick.NotPlaced => (200, "application/json", Quiet(new { ok = false, filled = state.Filled, user = state.User, frame = read.Origin, error = FrameNotPlaced })),
                _ => (200, "application/json", Quiet(new { ok = false, filled = state.Filled, user = state.User, frame = read.Origin, error = FrameNotReachable })),
            };

            // Only looking: a frame's form is empty until an account is chosen from the list, so
            // empty here says nothing about whether one is saved.
            if (BoolFrom("submit") == false && state is { Fields: 1, Filled: false })
                return (200, "application/json", Quiet(new { ok = true, filled = false, frame = read.Origin, submitted = false, note = FrameFillsOnChoice }));
            bool picked = false;
            if (state.Fields == 1 && (!state.Filled || asked is not null && state.User != asked) && BoolFrom("submit") != false)
            {
                // Choosing from the list is acting on the page, so not on one an agent scripted.
                if (tab.AgentScriptedThisDocument)
                    return (409, "application/json", Json(new { error = ScriptedLoginPage }));
                if (!window.OpenedByAgent)
                    return (409, "application/json", Json(new { error = PickNeedsOwnWindow }));
                string field = state.UserField ? $"[data-gergur-login=\"user-{nonce}\"]" : $"[data-gergur-login=\"pass-{nonce}\"]";
                bool keep = false;
                if (!await AccountPick.Turn.WaitAsync(TimeSpan.FromSeconds(10)))
                    return (503, "application/json", Json(new { error = PickBusy }));
                try
                {
                    await OnUiAsync(async () =>
                    {
                        if (window.Tabs is { } tabs && tabs.ActiveTab != tab)
                            await tabs.ActivateAsync(tab);
                        return true;
                    });
                    if (state.UserField)
                        await OnUiAsync(() => InFrame(ClearForPickJs(nonce, field), Tab.WakeTimeout));
                    var (placed, point) = await PlaceAsync(field);
                    if (placed != FrameClick.Clicked)
                        return Refusal(placed);
                    // Read on the UI thread, inside the pick, while the focus is still held.
                    async Task<bool> Chosen()
                        => ParseLoginState((await InFrame(LoginStateJs(nonce), TimeSpan.FromSeconds(1))).Result)
                            is { Filled: true } now && (asked is null || now.User == asked);
                    var outcome = await OnUiAsync(async () =>
                    {
                        var look = await LastLookAsync(point);
                        if (look != FrameClick.Clicked)
                            return (Look: look, Pick: AccountPick.Outcome.NoList);
                        return (Look: look, Pick: await AccountPick.RunAsync(window, tab, point.X, point.Y, asked ?? "", Chosen, InFrame));
                    }).WaitAsync(PickBudget);
                    if (outcome.Look != FrameClick.Clicked)
                        return Refusal(outcome.Look);
                    if (outcome.Pick == AccountPick.Outcome.Interrupted)
                        return (503, "application/json", Json(new { error = PickInterrupted }));
                    var (afterRaw, _) = await OnUiAsync(() => InFrame(LoginStateJs(nonce), Tab.WakeTimeout));
                    if (ParseLoginState(afterRaw) is { Filled: true } after && (asked is null || after.User == asked))
                    {
                        keep = true;
                        picked = true;
                        state = after;
                    }
                    else
                    {
                        return (200, "application/json", Quiet(new { ok = false, filled = false, frame = read.Origin, error = asked is null ? NoSavedLogin : AccountNotSaved }));
                    }
                }
                catch (TabInput.InputFailedException failed)
                {
                    return (503, "application/json", Json(new { error = failed.Message }));
                }
                catch (TimeoutException)
                {
                    return (503, "application/json", Json(new { error = PickTimedOut }));
                }
                finally
                {
                    AccountPick.Turn.Release();
                    // Another account's password is never left in the frame to be sent.
                    if (!keep)
                    {
                        try
                        {
                            await OnUiAsync(() => InFrame(EmptyPasswordJs(nonce), Tab.WakeTimeout)).WaitAsync(PickBudget);
                        }
                        catch (Exception ex)
                        {
                            DebugLog.WriteAlways($"after choosing a saved account in a frame, emptying the password failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            if (LoginStop(state, submit: BoolFrom("submit") != false, tab.AgentScriptedThisDocument) is { } stop)
                return (stop.Status, "application/json", Quiet(WithFrame(stop.Answer, read.Origin)));

            int dialogMark = tab.DialogCount;
            string? via = null, note = null;

            // One real click at an element of the frame, placed through it, with the last look
            // and the click on the UI thread and nothing awaited after the look's checks.
            async Task<FrameClick> ClickInFrameAsync(string selector)
            {
                var (placed, point) = await PlaceAsync(selector);
                if (placed != FrameClick.Clicked)
                    return placed;
                return await OnUiAsync(async () =>
                {
                    var look = await LastLookAsync(point);
                    if (look != FrameClick.Clicked)
                        return look;
                    await TabInput.ClickAsync(tab, point.X, point.Y);
                    return FrameClick.Clicked;
                });
            }

            async Task<(int, string, byte[])?> ClickedOrRefused(string selector, string how)
            {
                FrameClick done;
                try
                {
                    done = await ClickInFrameAsync(selector);
                }
                catch (TabInput.InputFailedException failed) when (failed.Busy)
                {
                    return (503, "application/json", Json(new { error = failed.Message }));
                }
                catch (TabInput.InputFailedException failed) when (failed.MayHaveLanded)
                {
                    // Never tried again: that could send it twice.
                    via = how;
                    note = $"{failed.Message}. The click may still have landed, so it was not tried again.";
                    return null;
                }
                catch (TabInput.InputFailedException failed)
                {
                    return (503, "application/json", Json(new { error = failed.Message }));
                }
                return done == FrameClick.Clicked ? null : Refusal(done);
            }

            if (state.Button)
            {
                if (await ClickedOrRefused($"[data-gergur-login=\"go-{nonce}\"]", "button") is { } refused)
                    return refused;
                via ??= "button";
            }
            else
            {
                // Into the password field (a script cannot move the keyboard into another
                // site's frame), the engine's list it may open closed, then Enter.
                if (await ClickedOrRefused($"[data-gergur-login=\"pass-{nonce}\"]", "enter") is { } refused)
                    return refused;
                if (via is null)
                {
                    await Task.Delay(250);
                    // Where the keys go is wherever the keyboard is, so each key waits on a last
                    // look that the field clicked still has it: not scripted, page and frame
                    // unchanged, the frame focused with that field in it. A page that moved the
                    // focus or went elsewhere in between gets no Enter.
                    async Task<FrameClick> KeyboardStillThere()
                    {
                        var (pageRaw, _) = await tab.ReadScriptAsync(FrameFocusedJs(read.Origin), TimeSpan.FromSeconds(1));
                        var (focusRaw, _) = await InFrame(FocusedJs($"[data-gergur-login=\"pass-{nonce}\"]"), TimeSpan.FromSeconds(1));
                        if (tab.AgentScriptedThisDocument)
                            return FrameClick.Scripted;
                        if (pageRaw != "true" || focusRaw != "true" || tab.CommittedSourceForOtherThreads != url || Tab.FrameGone(frame))
                            return FrameClick.MovedOn;
                        return FrameClick.Clicked;
                    }
                    FrameClick keys;
                    try
                    {
                        keys = await OnUiAsync(async () =>
                        {
                            var look = await KeyboardStillThere();
                            if (look != FrameClick.Clicked)
                                return look;
                            await TabInput.PressAsync(tab, TrustedInput.ParseChord("Escape")!);
                            look = await KeyboardStillThere();
                            if (look != FrameClick.Clicked)
                                return look;
                            await TabInput.PressAsync(tab, TrustedInput.ParseChord("Enter")!);
                            return FrameClick.Clicked;
                        });
                    }
                    catch (TabInput.InputFailedException failed)
                    {
                        return (503, "application/json", Json(new { error = failed.Message }));
                    }
                    if (keys != FrameClick.Clicked)
                        return Refusal(keys);
                    via = "enter";
                }
            }
            await Task.Delay(1500); // for the frame to move on, or say the password was wrong
            var (stillRaw, _) = await OnUiAsync(() => InFrame(StillAskingJs(nonce), TimeSpan.FromSeconds(3)));
            return (200, "application/json", Quiet(new
            {
                ok = true,
                filled = true,
                user = state.User,
                // Chosen from the person's saved accounts, with the keyboard borrowed for it.
                picked = picked ? true : (bool?)null,
                submitted = true,
                via,
                // The site the form came from: its saved password is the one that was sent.
                frame = read.Origin,
                stillAsking = stillRaw == "true",
                note,
                url = tab.CommittedSourceForOtherThreads,
                dialogs = DialogsOut(tab.DialogsSince(dialogMark)),
            }));
        }

        // "Sign in with Google": clicks the person's Google account for the site the sign-in is
        // for, the one they chose there themselves (or the one asked for), the way they would.
        // Never Google's consent screen or anything after it: what comes next is read and said.
        async Task<(int, string, byte[])> GoogleLoginAsync(Tab tab, string? asked)
        {
            string url = tab.CommittedSourceForOtherThreads;
            string site = GoogleSignIn.SiteOf(url);
            string? email = null;
            if (asked is not null && (email = GoogleSignIn.CleanEmail(asked)) is null)
                return (400, "application/json", Json(new { error = GoogleAccountNotAnAddress }));
            bool remembered = false;
            if (email is null && site.Length > 0 && _session.GoogleAccounts.For(site) is { } kept)
                (email, remembered) = (kept, true);
            string? siteOut = site.Length > 0 ? site : null;
            if (email is null)
                return (200, "application/json", Quiet(new { ok = false, google = true, site = siteOut, error = siteOut is null ? GoogleSiteUnknown : NoGoogleAccountRemembered }));
            if (BoolFrom("submit") == false)
                return (200, "application/json", Quiet(new { ok = true, google = true, site = siteOut, user = email, remembered, submitted = false }));
            // Google's consent screen is the person's: nothing on it is clicked, an account chip
            // that happens to carry the address included.
            if (GoogleSignIn.IsConsentPage(url))
                return (200, "application/json", Quiet(new { ok = false, google = true, site = siteOut, user = email, remembered, stillOnGoogle = true, error = GoogleStillAsking }));
            if (tab.AgentScriptedThisDocument)
                return (409, "application/json", Json(new { error = ScriptedLoginPage }));
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var (raw, ready) = await OnUiAsync(() => tab.ReadScriptAsync(GoogleSignIn.MarkJs(nonce, email), Tab.WakeTimeout));
            if (!ready)
                return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time" }));
            var marked = GoogleSignIn.ParseMarked(raw);
            if (marked is not { Found: true })
                return (200, "application/json", Quiet(new { ok = false, google = true, site = siteOut, user = email, remembered, accounts = marked?.Accounts ?? 0, error = GoogleAccountNotListed }));

            string entry = $"[data-gergur-google=\"{nonce}\"]";
            int dialogMark = tab.DialogCount;
            string? note = null;
            var (first, _) = await LocateAsync(tab, entry, animate: true);
            if (first is not null)
                await Task.Delay(CursorGlide);
            var (spot, _) = await LocateAsync(tab, entry, animate: false);
            if (spot is not { CanClickAt: true })
                return (200, "application/json", Quiet(new { ok = false, google = true, site = siteOut, user = email, remembered, error = GoogleAccountUnreachable }));
            // Looked at again directly before acting, as for a password: a page that moved on
            // meanwhile (to a consent screen, say) would take the click at the same point.
            if (tab.AgentScriptedThisDocument)
                return (409, "application/json", Json(new { error = ScriptedLoginPage }));
            if (tab.CommittedSourceForOtherThreads != url)
                return (200, "application/json", Quiet(new { ok = false, google = true, site = siteOut, user = email, remembered, stillOnGoogle = GoogleSignIn.IsGooglePage(tab.CommittedSourceForOtherThreads), error = GoogleMovedOn }));
            try
            {
                await OnUiAsync(() => TabInput.ClickAsync(tab, spot.X, spot.Y));
            }
            catch (TabInput.InputFailedException failed) when (failed.MayHaveLanded)
            {
                note = $"{failed.Message}. The click may still have landed, so it was not tried again.";
            }
            catch (TabInput.InputFailedException failed)
            {
                return (503, "application/json", Json(new { error = failed.Message }));
            }
            // For Google to move on: to the site, or to a page of its own (a consent screen, a
            // password, a check), or for a popup to close itself.
            for (int i = 0; i < 24 && !tab.Closed && tab.CommittedSourceForOtherThreads == url; i++)
                await Task.Delay(250);
            string now = tab.CommittedSourceForOtherThreads;
            bool still = !tab.Closed && GoogleSignIn.IsGooglePage(now);
            // An account the agent was told and signed in with, past Google: remembered for the
            // site, marked as named to this agent. Not while Google is still asking.
            bool learned = asked is not null && !still && siteOut is not null && await RememberNamedAsync(siteOut, email);
            return (200, "application/json", Quiet(new
            {
                ok = true,
                google = true,
                site = siteOut,
                user = email,
                // Chosen by the person for this site before, rather than named by the agent.
                remembered,
                // Named by the agent and now kept for this site, for the next sign-in.
                rememberedNow = learned ? true : (bool?)null,
                submitted = true,
                via = "click",
                closed = tab.Closed ? true : (bool?)null,
                stillOnGoogle = still,
                note = note ?? (still ? GoogleStillAsking : null),
                url = tab.Closed ? null : now,
                dialogs = DialogsOut(tab.DialogsSince(dialogMark)),
            }));
        }

        // "Sign in with Google" by FedCM: the chooser the browser draws itself, reported and
        // answered through the DevTools protocol (FedCmWatch), so there is nothing to click. Only
        // Google's accounts, only the one the person uses for the site (or the one named), and
        // only one already used with this site: a first sign-in shares their details with it,
        // which is theirs to agree to.
        async Task<(int, string, byte[])> FedCmLoginAsync(Tab tab, FedCmWatch watch, FedCmWatch.Dialog dialog, string? asked)
        {
            string url = tab.CommittedSourceForOtherThreads;
            string site = GoogleSignIn.SiteOfPage(url);
            string? siteOut = site.Length > 0 ? site : null;
            string? email = null;
            if (asked is not null && (email = GoogleSignIn.CleanEmail(asked)) is null)
                return (400, "application/json", Json(new { error = GoogleAccountNotAnAddress }));
            if (FedCmWatch.IsQuietReturn(dialog, GoogleSignIn.Origin))
            {
                // The browser is signing the person back in by itself, with the one Google
                // account this site used last, as it would in their own window: nothing to
                // choose. Waited for and said, never remembered.
                for (int i = 0; i < 24 && !tab.Closed && watch.Current?.Id == dialog.Id; i++)
                    await Task.Delay(250);
                string? used = GoogleSignIn.CleanEmail(dialog.Accounts[0].Email);
                bool other = email is not null && used != email;
                return (200, "application/json", Quiet(new
                {
                    ok = !other,
                    google = true,
                    fedCm = true,
                    site = siteOut,
                    user = used,
                    submitted = false,
                    via = "browser",
                    stillOnGoogle = !tab.Closed && watch.Current is not null,
                    error = other ? FedCmQuietReturnOther : null,
                    note = other ? null : FedCmQuietReturn,
                    url = tab.Closed ? null : tab.CommittedSourceForOtherThreads,
                }));
            }
            bool remembered = false;
            if (email is null && siteOut is not null && _session.GoogleAccounts.For(siteOut) is { } kept)
                (email, remembered) = (kept, true);
            if (email is null)
                return (200, "application/json", Quiet(new { ok = false, google = true, fedCm = true, site = siteOut, error = siteOut is null ? GoogleSiteUnknown : NoGoogleAccountRemembered }));
            var (index, why) = FedCmWatch.Choose(dialog, email, GoogleSignIn.Origin);
            if (why != FedCmWatch.Refusal.None)
            {
                string error = why switch
                {
                    FedCmWatch.Refusal.NotAChooser => FedCmNotAChooser,
                    FedCmWatch.Refusal.NotGoogle => FedCmNotGoogle,
                    FedCmWatch.Refusal.FirstSignIn => FedCmFirstSignIn,
                    _ => GoogleAccountNotListed,
                };
                return (200, "application/json", Quiet(new { ok = false, google = true, fedCm = true, site = siteOut, user = email, remembered, accounts = dialog.Accounts.Count, error }));
            }
            if (BoolFrom("submit") == false)
                return (200, "application/json", Quiet(new { ok = true, google = true, fedCm = true, site = siteOut, user = email, remembered, submitted = false }));
            int dialogMark = tab.DialogCount;
            FedCmLook look;
            try
            {
                // Looked at again on the UI thread, directly before choosing and with nothing
                // awaited between: still this tab's watch (not moved to the person's window),
                // the page not scripted (a page an agent scripted could have asked for the
                // chooser on terms of its own), the same chooser over the same page.
                look = await OnUiAsync(async () =>
                {
                    if (tab.FedCm != watch)
                        return FedCmLook.MovedOn;
                    if (tab.AgentScriptedThisDocument)
                        return FedCmLook.Scripted;
                    if (watch.Current?.Id != dialog.Id || url != tab.CommittedSourceForOtherThreads)
                        return FedCmLook.MovedOn;
                    await watch.SelectAsync(dialog.Id, index);
                    return FedCmLook.Chosen;
                });
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
            {
                // The chooser was gone (closed by the page, or timed out) or would not take it.
                return (200, "application/json", Quiet(new { ok = false, google = true, fedCm = true, site = siteOut, user = email, remembered, error = FedCmMovedOn, detail = ex.Message }));
            }
            if (look == FedCmLook.Scripted)
                return (409, "application/json", Json(new { error = ScriptedLoginPage }));
            if (look == FedCmLook.MovedOn)
                return (200, "application/json", Quiet(new { ok = false, google = true, fedCm = true, site = siteOut, user = email, remembered, error = FedCmMovedOn }));
            // For the chooser to close, then for the site to take the sign-in: a page it loads,
            // or (at most two seconds more) the same page signed in.
            for (int i = 0; i < 24 && !tab.Closed && watch.Current?.Id == dialog.Id; i++)
                await Task.Delay(250);
            for (int i = 0; i < 8 && !tab.Closed && watch.Current is null && tab.CommittedSourceForOtherThreads == url; i++)
                await Task.Delay(250);
            var after = watch.Current;
            bool still = !tab.Closed && after is not null;
            bool learned = asked is not null && !still && siteOut is not null && await RememberNamedAsync(siteOut, email);
            return (200, "application/json", Quiet(new
            {
                ok = true,
                google = true,
                fedCm = true,
                site = siteOut,
                user = email,
                remembered,
                rememberedNow = learned ? true : (bool?)null,
                submitted = true,
                via = "chooser",
                stillOnGoogle = still,
                // Another of the browser's Google dialogs up after the choice (an error, or one
                // asking to sign in to Google): said, never answered.
                waiting = still ? after!.Type : null,
                note = still ? GoogleStillAsking : null,
                url = tab.Closed ? null : tab.CommittedSourceForOtherThreads,
                dialogs = DialogsOut(tab.DialogsSince(dialogMark)),
            }));
        }

        // Keeps an account the agent named and signed in with for the site, marked as named to
        // it, says so in the person's log, and redraws any settings page. False when nothing
        // new was kept (already the one, or the list could not be read at startup).
        async Task<bool> RememberNamedAsync(string site, string email)
        {
            var caller = CurrentRequest.Value?.Caller ?? _session.Agents.Identify(null, null, viaMcp: false);
            try
            {
                if (_session.GoogleAccounts.For(site) == email
                    || !_session.GoogleAccounts.Remember(site, email, DateTime.UtcNow, caller.Name))
                    return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.WriteAlways($"a Google account named to an agent could not be remembered: {ex.Message}");
                return false;
            }
            _session.Agents.Record(caller, $"Remembered which Google account to use on {site}", null, DateTime.UtcNow);
            await OnUiAsync(() =>
            {
                _session.Windows.FirstOrDefault()?.PushSettingsChanged();
                return Task.FromResult(true);
            });
            return true;
        }

        // Gets the wanted saved account into the sign-in form: first by adding a username field
        // the engine fills by itself (a password-only page), then, when that is not the one, by
        // choosing it from the engine's list with the keyboard borrowed (AccountPick). User is
        // the account the form now holds, or null; the password is emptied when it is not the
        // wanted one, so another account's is never sent.
        async Task<(string? User, bool Borrowed, (int, string, byte[])? Refused)> PickAccountAsync(
            MainForm window, Tab tab, string nonce, PickPlan plan, string? wanted)
        {
            string field = plan == PickPlan.UserField ? $"[data-gergur-login=\"user-{nonce}\"]" : $"[data-gergur-login=\"pick-{nonce}\"]";
            bool added = false, keep = false;
            // Best effort, after the fact: the answer already says what happened, so a page that
            // went away meanwhile is written down rather than thrown over it.
            async Task QuietlyAsync(string js, string what)
            {
                try
                {
                    await OnUiAsync(() => tab.ReadScriptAsync(js, Tab.WakeTimeout)).WaitAsync(PickBudget);
                }
                catch (Exception ex)
                {
                    DebugLog.WriteAlways($"after choosing a saved account, {what} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            try
            {
                if (plan == PickPlan.AddedField)
                {
                    var (addRaw, _) = await OnUiAsync(() => tab.ReadScriptAsync(AddPickFieldJs(nonce), Tab.WakeTimeout));
                    if (addRaw != "true")
                        return (null, false, null);
                    added = true;
                    await Task.Delay(700);   // the engine fills the most recent account into it by itself
                    var (firstRaw, _) = await OnUiAsync(() => tab.ReadScriptAsync(PickStateJs(nonce, field), Tab.WakeTimeout));
                    var first = ParsePickState(firstRaw);
                    if (first is { Filled: true } && (wanted is null || first.User == wanted))
                    {
                        keep = true;
                        return (first.User, false, null);
                    }
                    if (wanted is null)
                        return (null, false, null);
                }
                if (!window.OpenedByAgent)
                    return (null, false, (409, "application/json", Json(new { error = PickNeedsOwnWindow })));
                if (!await AccountPick.Turn.WaitAsync(TimeSpan.FromSeconds(10)))
                    return (null, false, (503, "application/json", Json(new { error = PickBusy })));
                try
                {
                    // Which tab is in front is the UI thread's to read.
                    await OnUiAsync(async () =>
                    {
                        if (window.Tabs is { } tabs && tabs.ActiveTab != tab)
                            await tabs.ActivateAsync(tab);
                        return true;
                    });
                    await OnUiAsync(() => tab.ReadScriptAsync(ClearForPickJs(nonce, field), Tab.WakeTimeout));
                    var (spot, _) = await LocateAsync(tab, field, animate: false);
                    if (spot is not { CanClickAt: true })
                        return (null, false, (200, "application/json", Quiet(new { ok = false, filled = false, error = PickFieldUnreachable })));
                    // Read on the UI thread, inside the pick, while the focus is still held.
                    async Task<bool> Chosen()
                        => ParsePickState((await tab.ReadScriptAsync(PickStateJs(nonce, field), TimeSpan.FromSeconds(1))).Result)
                            is { Filled: true } now && now.User == wanted;
                    // Again directly before the keyboard is borrowed: a script since the first look
                    // could have pointed the form somewhere else.
                    if (tab.AgentScriptedThisDocument)
                        return (null, false, (409, "application/json", Json(new { error = ScriptedLoginPage })));
                    // Bounded: a window closing before the UI thread runs the pick would leave this
                    // waiting for good, holding the turn. Every step inside has a budget far under it.
                    var outcome = await OnUiAsync(() => AccountPick.RunAsync(window, tab, spot.X, spot.Y, wanted!, Chosen)).WaitAsync(PickBudget);
                    if (outcome == AccountPick.Outcome.Interrupted)
                        return (null, false, (503, "application/json", Json(new { error = PickInterrupted })));
                }
                catch (TabInput.InputFailedException failed)
                {
                    return (null, true, (503, "application/json", Json(new { error = failed.Message })));
                }
                catch (TimeoutException)
                {
                    return (null, true, (503, "application/json", Json(new { error = PickTimedOut })));
                }
                finally
                {
                    AccountPick.Turn.Release();
                }
                var (afterRaw, _) = await OnUiAsync(() => tab.ReadScriptAsync(PickStateJs(nonce, field), Tab.WakeTimeout));
                var after = ParsePickState(afterRaw);
                // Not chosen: the field may well hold the account, as typed, with no password or
                // another account's behind it. That is never taken for the account.
                if (after is { Filled: true } && after.User == wanted)
                {
                    keep = true;
                    return (wanted, true, null);
                }
                return (null, true, null);
            }
            finally
            {
                // Whatever came of it, refused, failed or thrown: another account's password is
                // never left in the page to be sent, and Gergur's own field goes.
                if (!keep)
                    await QuietlyAsync(EmptyPasswordJs(nonce), "emptying the password field");
                if (added)
                    await QuietlyAsync(RemovePickFieldJs(nonce), "removing the added username field");
            }
        }

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
                bool muted = BoolFrom("muted") ?? false;
                // Whose window it is, for the banner across it and the agents panel.
                var owner = CurrentRequest.Value?.Caller;
                var opened = await OnUiAsync(() => MainForm.OpenWindowAsync(_session, focus, owner));
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
                    UrlHeuristics.ToNavigableUrl(url, _session.Settings.SearchUrlTemplate), activate: true, muted));
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
                bool mutedTab = BoolFrom("muted") ?? false;

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
                    activate: !background, muted: mutedTab));
                if (tab is null)
                    return PageCouldNotStart(null, DateTime.UtcNow);
                // Badged as an agent's on the tab strip, even in the person's own window.
                var opener = CurrentRequest.Value?.Caller;
                await OnUiAsync(() => { window.MarkOpenedByAgent(tab, opener?.Name ?? "An agent"); return Task.FromResult(true); });
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
                    // Its window forward too, unless it is an agent's hidden one: only the
                    // person shows that, from the agents panel. The tab is still switched to.
                    if (!entry.Window.HiddenForAgent)
                        entry.Window.Activate();
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

            case ("POST", "/task"):
            {
                // Always set by the time a request is routed; see HandleClientAsync.
                if (CurrentRequest.Value?.Caller is not { } who)
                    return (500, "application/json", Json(new { error = "the caller could not be told apart" }));
                if (!_session.Agents.SetTask(who, BodyString("task"), DateTime.UtcNow))
                    return (400, "application/json", Json(new { error = "task required: a few words on what you are doing, such as \"Researching flights to Tokyo\"" }));
                return (200, "application/json", Json(new { ok = true, task = _session.Agents.TaskOf(who.Key) }));
            }

            case ("POST", "/navigate"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (CurrentRequest.Value?.Caller is { } navigator)
                    tab.NoteDrivenBy(navigator.Key);
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
                // A password shown as plain text (a "show password" eye pressed) would be in
                // the picture; dots are fine.
                var (shownPassword, _) = await OnUiAsync(() => shot.Tab.ReadScriptAsync(PasswordStateJs, TimeSpan.FromSeconds(3)));
                if (ShowsPasswordAsText(shownPassword))
                    return (409, "application/json", Json(new { error = PasswordInPlainText }));
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
                // Script the engine runs counts as the person's own click for what a page may
                // ask of the window, so the window is told who was acting.
                Acting(tab);

                // A password in a field here (filled in from the person's saved ones, most
                // likely) is not read out by an agent's script: the person asked that agents
                // can sign in with what they saved and never see it. /login signs in.
                var (passwordBefore, checkable) = await OnUiAsync(() => tab.ReadScriptAsync(PasswordStateJs, Tab.WakeTimeout));
                if (!checkable)
                    return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time to run a script in" }));
                if (!HoldsNoPassword(passwordBefore))
                    return (409, "application/json", Json(new { error = PasswordOnPage }));
                tab.AgentScriptStarting();
                try
                {
                    var answer = await EvaluateAsync();
                    // A refusal or a failure carries no value from the page: passed on as it is.
                    // EvaluateAsync's only one is a fixed 503; a script that threw answers 200
                    // (Evaluated, ok false, with its message), so that goes through the look below.
                    if (answer.Item1 != 200)
                        return answer;
                    // Looked at again once it has run: the page can move on, or draw a sign-in
                    // form the engine then fills, between the look above and the script. Its
                    // answer is then kept back, since it may hold what was filled in.
                    var (passwordAfter, _) = await OnUiAsync(() => tab.ReadScriptAsync(PasswordStateJs, TimeSpan.FromSeconds(3)));
                    return HoldsNoPassword(passwordAfter) ? answer : (409, "application/json", Json(new { error = PasswordDuringScript }));
                }
                finally
                {
                    tab.AgentScriptOver();
                }

                async Task<(int, string, byte[])> EvaluateAsync()
                {
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
            }

            case ("POST", "/login"):
                return await LoginAsync();

            case ("POST", "/click"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string selector = BodyString("selector") ?? "";
                if (selector.Length == 0)
                    return (400, "application/json", Json(new { error = "selector required" }));
                Acting(tab);
                var (first, clickable) = await LocateAsync(tab, selector, animate: true);
                if (!clickable)
                    return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time to click" }));
                if (first is null)
                    return (200, "application/json", Json(new { ok = false, error = NothingMatches }));
                await Task.Delay(CursorGlide);
                // Measured again: the page may have moved it while the cursor was on its way.
                var (spot, _) = await LocateAsync(tab, selector, animate: false);
                if (spot is null)
                    return (200, "application/json", Json(new { ok = false, error = WentAway }));

                int dialogMark = tab.DialogCount;
                var openedBefore = await OpenedFromAsync(tab);
                bool trusted = false, chooser = false;
                string? note = null;
                if (spot.CanClickAt)
                {
                    try
                    {
                        chooser = await OnUiAsync(() => TabInput.ClickAsync(tab, spot.X, spot.Y));
                        trusted = true;
                    }
                    catch (TabInput.InputFailedException failed) when (failed.Busy)
                    {
                        return (503, "application/json", Json(new { error = failed.Message }));
                    }
                    catch (TabInput.InputFailedException failed) when (failed.MayHaveLanded)
                    {
                        // Not tried again another way: a second click on a "Pay" or "Delete" is the one thing worse than none.
                        trusted = true;
                        note = $"{failed.Message}. The click may still have landed, so it was not tried again; a dialog in the person's window can hold a page like this.";
                    }
                    catch (TabInput.InputFailedException failed)
                    {
                        note = $"a real click could not be sent ({failed.Message}), so the element's click() was called instead";
                    }
                }
                else
                {
                    note = spot.Covered
                        ? $"something covers it ({spot.CoveredBy}), so rather than click that, the element's click() was called. Close whatever is over it for a real click."
                        : "it is not visible on screen, so the element's click() was called instead of a real click";
                }
                if (!trusted)
                {
                    var (clicked, _) = await ActOnPageAsync(tab, SyntheticClickJs(selector));
                    if (!clicked)
                        return (200, "application/json", Json(new { ok = false, error = WentAway }));
                    await Task.Delay(400); // for a dialog or a tab it opens to show up in the answer
                }
                return (200, "application/json", Quiet(new
                {
                    ok = true,
                    trusted,
                    note,
                    covered = spot.Covered ? spot.CoveredBy : null,
                    fileChooser = chooser
                        ? "the click opened a file chooser, which was closed unseen; use gergur_upload with this selector to choose files"
                        : null,
                    opened = await NewlyOpenedAsync(tab, openedBefore),
                    dialogs = DialogsOut(tab.DialogsSince(dialogMark)),
                }));
            }

            case ("POST", "/type"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string selector = BodyString("selector") ?? "";
                string text = BodyString("text") ?? "";
                if (selector.Length == 0)
                    return (400, "application/json", Json(new { error = "selector required" }));
                bool clear = BoolFrom("clear") ?? true;
                Acting(tab);
                var (spot, typeable) = await LocateAsync(tab, selector, animate: true);
                if (!typeable)
                    return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time to type into" }));
                if (spot is null)
                    return (200, "application/json", Json(new { ok = false, error = NothingMatches }));
                if (spot.Tag == "select")
                    return (400, "application/json", Json(new { error = "that is a drop-down list; use gergur_select_option" }));
                if (spot.FileInput)
                    return (400, "application/json", Json(new { error = "that is a file input; use gergur_upload" }));
                await Task.Delay(CursorGlide);

                var (focused, _) = await OnUiAsync(() => tab.ReadScriptAsync(FocusJs(selector, selectAll: clear), Tab.WakeTimeout));
                bool trusted = false;
                string? note = null;
                if (focused == "true")
                {
                    try
                    {
                        await OnUiAsync(async () =>
                        {
                            // Typed over what is selected, which with clear is everything in it.
                            if (text.Length > 0)
                                await TabInput.InsertTextAsync(tab, text);
                            else if (clear)
                                await TabInput.PressAsync(tab, TrustedInput.ParseChord("Backspace")!);
                            return true;
                        });
                        trusted = true;
                    }
                    catch (TabInput.InputFailedException failed) when (failed.MayHaveLanded)
                    {
                        // Not set again another way, which with clear false would add it twice.
                        trusted = true;
                        note = $"{failed.Message}. The text may still have gone in, so it was not set again; check the field.";
                    }
                    catch (TabInput.InputFailedException failed)
                    {
                        note = $"real typing could not be sent ({failed.Message}), so the value was set directly";
                    }
                }
                else
                {
                    note = "it would not take the keyboard focus, so the value was set directly";
                }
                if (!trusted)
                {
                    var (set, _) = await ActOnPageAsync(tab, SetValueJs(selector, text, clear));
                    if (!set)
                        return (200, "application/json", Json(new { ok = false, error = WentAway }));
                }
                if (BoolFrom("submit") == true)
                {
                    try
                    {
                        await OnUiAsync(async () => { await TabInput.PressAsync(tab, TrustedInput.ParseChord("Enter")!); return true; });
                    }
                    catch (TabInput.InputFailedException failed)
                    {
                        note = (note is null ? "" : note + "; ") + $"Enter could not be pressed: {failed.Message}";
                    }
                }
                // Whether the field now says what was meant, without the text itself coming back:
                // it may be a password, and the answer goes into the agent's transcript.
                var (valueRaw, _) = await OnUiAsync(() => tab.ReadScriptAsync(ValueJs(selector), Tab.WakeTimeout));
                string? now = valueRaw == "null" ? null : AsString(valueRaw);
                return (200, "application/json", Quiet(new { ok = true, trusted, note, matches = Matches(now, text, clear) }));
            }

            case ("POST", "/key"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string key = BodyString("key") ?? "";
                if (TrustedInput.ParseChord(key) is not { } chord)
                    return (400, "application/json", Json(new { error = UnknownKey(key) }));
                int times = (int)Math.Clamp(NumberFrom("times") ?? 1, 1, 50);
                Acting(tab);
                string? selector = BodyString("selector");
                if (!string.IsNullOrEmpty(selector))
                {
                    var (spot, ready) = await LocateAsync(tab, selector, animate: true);
                    if (!ready)
                        return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time" }));
                    if (spot is null)
                        return (200, "application/json", Json(new { ok = false, error = NothingMatches }));
                    await Task.Delay(CursorGlide);
                    var (focused, _) = await OnUiAsync(() => tab.ReadScriptAsync(FocusJs(selector, selectAll: false), Tab.WakeTimeout));
                    if (focused != "true")
                        return (200, "application/json", Json(new { ok = false, error = "it would not take the keyboard focus" }));
                }
                else
                {
                    // Woken and loaded first: a key sent to a page still on its way goes nowhere.
                    var (_, ready) = await OnUiAsync(() => tab.ReadScriptAsync("true", Tab.WakeTimeout));
                    if (!ready)
                        return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time" }));
                }
                int dialogMark = tab.DialogCount;
                var openedBefore = await OpenedFromAsync(tab);
                await OnUiAsync(async () => { await TabInput.PressAsync(tab, chord, times); return true; });
                await Task.Delay(300);
                return (200, "application/json", Quiet(new
                {
                    ok = true,
                    opened = await NewlyOpenedAsync(tab, openedBefore),
                    dialogs = DialogsOut(tab.DialogsSince(dialogMark)),
                }));
            }

            case ("POST", "/hover"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string selector = BodyString("selector") ?? "";
                if (selector.Length == 0)
                    return (400, "application/json", Json(new { error = "selector required" }));
                Acting(tab);
                var (first, ready) = await LocateAsync(tab, selector, animate: true);
                if (!ready)
                    return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time" }));
                if (first is null)
                    return (200, "application/json", Json(new { ok = false, error = NothingMatches }));
                await Task.Delay(CursorGlide);
                var (spot, _) = await LocateAsync(tab, selector, animate: false);
                if (spot is null)
                    return (200, "application/json", Json(new { ok = false, error = WentAway }));
                if (!spot.InView)
                    return (200, "application/json", Json(new { ok = false, error = "it is not visible on screen, so there is nothing to point at" }));
                await OnUiAsync(async () => { await TabInput.HoverAsync(tab, spot.X, spot.Y); return true; });
                await Task.Delay(300); // for a menu that opens on hover to be there when the agent looks
                return (200, "application/json", Quiet(new { ok = true, covered = spot.Covered ? spot.CoveredBy : null }));
            }

            case ("POST", "/scroll"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string? selector = BodyString("selector") is { Length: > 0 } named ? named : null;
                string? to = BodyString("to");
                double? dx = NumberFrom("dx"), dy = NumberFrom("dy");
                if (to is not null and not ("top" or "bottom"))
                    return (400, "application/json", Json(new { error = "to is \"top\" or \"bottom\"" }));
                Acting(tab);
                const string notReady = "that tab's page did not finish loading in time";
                if (to is not null)
                {
                    string js = to == "top" ? "(window.scrollTo(0, 0), true)" : "(window.scrollTo(0, document.documentElement.scrollHeight), true)";
                    var (_, ready) = await OnUiAsync(() => tab.ReadScriptAsync(js, Tab.WakeTimeout));
                    if (!ready)
                        return (503, "application/json", Json(new { error = notReady }));
                }
                else if (selector is not null && dx is null && dy is null)
                {
                    // Finding it scrolls it to the middle of the view.
                    var (spot, ready) = await LocateAsync(tab, selector, animate: false);
                    if (!ready)
                        return (503, "application/json", Json(new { error = notReady }));
                    if (spot is null)
                        return (200, "application/json", Json(new { ok = false, error = NothingMatches }));
                }
                else
                {
                    double x, y;
                    if (selector is not null)
                    {
                        // The wheel over an element scrolls what it is in: a list or panel of its own.
                        var (spot, ready) = await LocateAsync(tab, selector, animate: false);
                        if (!ready)
                            return (503, "application/json", Json(new { error = notReady }));
                        if (spot is null)
                            return (200, "application/json", Json(new { ok = false, error = NothingMatches }));
                        (x, y) = (spot.X, spot.Y);
                    }
                    else
                    {
                        var (middle, ready) = await OnUiAsync(() => tab.ReadScriptAsync("[innerWidth / 2, innerHeight / 2]", Tab.WakeTimeout));
                        if (!ready)
                            return (503, "application/json", Json(new { error = notReady }));
                        (x, y) = ParsePoint(middle) ?? (400, 300);
                    }
                    double byX = dx ?? 0, byY = dy ?? (dx is null ? 600 : 0);
                    try
                    {
                        await OnUiAsync(async () => { await TabInput.WheelAsync(tab, x, y, byX, byY); return true; });
                    }
                    catch (TabInput.InputFailedException)
                    {
                        await ActOnPageAsync(tab, string.Create(CultureInfo.InvariantCulture, $"(window.scrollBy({byX}, {byY}), true)"));
                    }
                }
                await Task.Delay(350); // smooth scrolling takes a moment to arrive
                var (where, _) = await OnUiAsync(() => tab.ReadScriptAsync(ScrollStateJs, Tab.WakeTimeout));
                return (200, "application/json", where == "null" ? Json(new { ok = true }) : Encoding.UTF8.GetBytes(where));
            }

            case ("POST", "/select"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string selector = BodyString("selector") ?? "";
                string? value = BodyString("value"), label = BodyString("label");
                if (selector.Length == 0)
                    return (400, "application/json", Json(new { error = "selector required" }));
                if (value is null && label is null)
                    return (400, "application/json", Json(new { error = "value or label required" }));
                Acting(tab);
                var (spot, ready) = await LocateAsync(tab, selector, animate: true);
                if (!ready)
                    return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time" }));
                if (spot is null)
                    return (200, "application/json", Json(new { ok = false, error = NothingMatches }));
                if (spot.Tag != "select")
                    return (400, "application/json", Json(new
                    {
                        error = "that is not a <select>. A drop-down a site builds itself is ordinary elements: gergur_click it, then gergur_click the option.",
                    }));
                await Task.Delay(CursorGlide);
                var (chosen, _) = await OnUiAsync(() => tab.ReadScriptAsync(SelectJs(selector, value, label), Tab.WakeTimeout));
                return (200, "application/json", chosen == "null" ? Json(new { ok = false, error = WentAway }) : Encoding.UTF8.GetBytes(chosen));
            }

            case ("POST", "/wait"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string? selector = BodyString("selector") is { Length: > 0 } named ? named : null;
                string? text = BodyString("text") is { Length: > 0 } words ? words : null;
                if (selector is null && text is null)
                    return (400, "application/json", Json(new { error = "selector or text required" }));
                bool gone = BoolFrom("gone") ?? false;
                double seconds = Math.Clamp(NumberFrom("timeout") ?? 10, 0.5, 60);
                string js = WaitJs(selector, text);
                var started = DateTime.UtcNow;
                var deadline = started + TimeSpan.FromSeconds(seconds);
                bool met = false, everReady = false;
                while (true)
                {
                    var left = Tab.Remaining(deadline);
                    var (seen, ready) = await OnUiAsync(() => tab.ReadScriptAsync(js, left > MinRead ? left : MinRead));
                    everReady |= ready;
                    if (ready && seen == "\"invalid selector\"")
                        return (400, "application/json", Json(new { error = "that is not a valid CSS selector" }));
                    // A page between loads says nothing either way, "gone" included.
                    if (ready && (seen == "true") != gone)
                    {
                        met = true;
                        break;
                    }
                    if (DateTime.UtcNow >= deadline)
                        break;
                    await Task.Delay(250);
                }
                return (200, "application/json", Quiet(new
                {
                    ok = true,
                    met,
                    waitedSeconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
                    note = everReady ? null : "the page never finished loading while this waited",
                }));
            }

            case ("POST", "/upload"):
            {
                if (Target() is not { } tab)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                string selector = BodyString("selector") ?? "";
                if (selector.Length == 0)
                    return (400, "application/json", Json(new { error = "selector required" }));
                var files = FilesIn(body);
                if (TrustedInput.CheckFiles(files) is { } badFiles)
                    return (400, "application/json", Json(new { error = badFiles }));
                Acting(tab);
                var (spot, ready) = await LocateAsync(tab, selector, animate: true);
                if (!ready)
                    return (503, "application/json", Json(new { error = "that tab's page did not finish loading in time" }));
                if (spot is null)
                    return (200, "application/json", Json(new { ok = false, error = NothingMatches }));
                if (spot.FileInput)
                {
                    // Works hidden or not, which is how most sites keep theirs.
                    if (!await OnUiAsync(() => TabInput.SetFilesAsync(tab, selector, files)))
                        return (200, "application/json", Json(new { ok = false, error = WentAway }));
                    var (count, _) = await OnUiAsync(() => tab.ReadScriptAsync(FileCountJs(selector), Tab.WakeTimeout));
                    return (200, "application/json", Quiet(new
                    {
                        ok = true,
                        via = "the file input",
                        files = files.Count,
                        inputHas = int.TryParse(count, out int n) ? n : (int?)null,
                    }));
                }
                await Task.Delay(CursorGlide);
                var (button, _) = await LocateAsync(tab, selector, animate: false);
                if (button is null)
                    return (200, "application/json", Json(new { ok = false, error = WentAway }));
                if (!button.CanClickAt)
                    return (200, "application/json", Json(new
                    {
                        ok = false,
                        error = (button.Covered ? $"something covers it ({button.CoveredBy}); close that first" : "it is not visible on screen")
                            + ". Or name the <input type=file> itself, which works even when the page hides it.",
                    }));
                bool chose = await OnUiAsync(() => TabInput.UploadThroughChooserAsync(tab, button.X, button.Y, files, TimeSpan.FromSeconds(3)));
                return chose
                    ? (200, "application/json", Json(new { ok = true, via = "the file chooser that clicking it opened", files = files.Count }))
                    : (200, "application/json", Json(new
                    {
                        ok = false,
                        error = "clicking it opened no file chooser. Name the <input type=file> itself; it often sits hidden next to the button, and gergur_read_html shows it.",
                    }));
            }

            case ("GET", "/dialogs"):
            case ("POST", "/dialogs"):
            {
                if (TargetEntry() is not { } entry)
                    return (404, "application/json", Json(new { error = "no such tab" }));
                var tab = entry.Tab;
                if (OwnPageRefused(tab) is { } refused)
                    return refused;
                if (method == "POST")
                {
                    var (accept, answer) = tab.DialogPolicy;
                    if (BoolFrom("accept") is { } yes)
                        accept = yes;
                    // Present and null clears it back to each prompt's own default.
                    if (body is { RootElement.ValueKind: JsonValueKind.Object } && body.RootElement.TryGetProperty("text", out var said))
                        answer = said.ValueKind == JsonValueKind.String ? said.GetString() : null;
                    tab.DialogPolicy = (accept, answer);
                }
                var policy = tab.DialogPolicy;
                bool answeredHere = await OnUiAsync(() => Task.FromResult(entry.Window.OpenedByAgent));
                return (200, "application/json", Quiet(new
                {
                    answeredForYou = answeredHere,
                    note = answeredHere ? null : "this tab is in the person's window, so its dialogs are shown to them rather than answered here",
                    confirm = policy.Accept ? "OK" : "Cancel",
                    promptAnswer = policy.PromptAnswer,
                    recent = DialogsOut(tab.RecentDialogs) ?? [],
                }));
            }

            case ("POST", "/ask"):
            {
                var caller = CurrentRequest.Value?.Caller ?? _session.Agents.Identify(null, null, viaMcp: false);
                string? id = BodyString("id") is { Length: > 0 } given ? given : null;
                var wait = TimeSpan.FromSeconds(Math.Clamp(NumberFrom("timeout") ?? 18, 0, 600));
                if (id is null)
                {
                    if (AgentAsks.Clean(BodyString("question")) is not { } question)
                        return (400, "application/json", Json(new { error = "question required" }));
                    if (_session.Asks.Ask(caller, question, DateTime.UtcNow) is not { } asked)
                        return (429, "application/json", Json(new
                        {
                            error = $"{AgentAsks.MaxPending} questions are already waiting for the person; wait for those first",
                        }));
                    id = asked.Id;
                }
                var (known, reply) = await _session.Asks.WaitAsync(id, caller.Key, wait);
                if (!known)
                    return (404, "application/json", Json(new
                    {
                        error = "no question of yours with that id; answers are kept for an hour, and each is handed over once",
                    }));
                return (200, "application/json", reply is null
                    ? Json(new
                    {
                        id,
                        answered = false,
                        note = "The person has not answered yet; the question is in a bar across their window. Call gergur_ask_person with this id to keep waiting.",
                    })
                    : Json(new { id, answered = true, done = reply.Done, reply = reply.Text }));
            }

            case ("GET", "/downloads"):
            {
                var caller = CurrentRequest.Value?.Caller ?? _session.Agents.Identify(null, null, viaMcp: false);
                bool wait = BoolFrom("wait") ?? false;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(NumberFrom("timeout") ?? 15, 0, 60));
                while (true)
                {
                    var (running, listed) = await OnUiAsync(() => Task.FromResult(DownloadsOf(caller.Key)));
                    if (!wait || running == 0 || DateTime.UtcNow >= deadline)
                        return (200, "application/json", Json(new { running, downloads = listed }));
                    await Task.Delay(500);
                }
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

    private static object Muted() => new { type = "boolean", description = "Open the tab muted, silent from its first frame. Use it for anything that plays sound, so testing a video does not play it at whoever is sitting at the computer." };

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
        "gergur_set_task" => ["task"],
        "gergur_press_key" => ["key"],
        "gergur_hover" => ["selector"],
        "gergur_select_option" => ["selector", "value|label"],
        "gergur_wait_for" => ["selector|text"],
        "gergur_upload" => ["selector", "files"],
        "gergur_ask_person" => ["question|id"],
        _ => [],
    };

    /// <summary>
    /// Arguments where an empty string cannot mean anything. Deliberately excludes
    /// "text": clearing a field by typing nothing into it is a real action.
    /// </summary>
    private static bool EmptyIsMeaningless(string key) => key is "url" or "js" or "selector" or "task" or "key" or "question";

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
        "gergur_set_task" => ("POST", "/task"),
        "gergur_press_key" => ("POST", "/key"),
        "gergur_hover" => ("POST", "/hover"),
        "gergur_scroll" => ("POST", "/scroll"),
        "gergur_select_option" => ("POST", "/select"),
        "gergur_wait_for" => ("POST", "/wait"),
        "gergur_upload" => ("POST", "/upload"),
        "gergur_dialogs" => ("POST", "/dialogs"),
        "gergur_downloads" => ("GET", "/downloads"),
        "gergur_ask_person" => ("POST", "/ask"),
        "gergur_login" => ("POST", "/login"),
        _ => ("", ""),
    };

    /// <summary>
    /// The tools an MCP agent may use before it has said what it is doing: saying it, and
    /// looking around first. Everything else is refused until then, so the person watching
    /// always sees "Researching flights to Tokyo" beside an agent's name and on its window,
    /// not only which site it is on.
    /// </summary>
    internal static bool NeedsTask(string tool) => tool is not ("gergur_set_task" or "gergur_list_tabs" or "gergur_read_settings");

    internal const string TaskFirstText =
        "Say what you are doing first: call gergur_set_task with a few words, such as \"Researching flights to Tokyo\" "
        + "or \"Uploading screenshots to App Store Connect\". The person using Gergur sees it beside your name and across "
        + "your window. Call it again whenever the task changes. If gergur_set_task is not among your tools, your tool "
        + "list is from an older Gergur: ask the person to reconnect the gergur MCP server (in Claude Code: /mcp, choose "
        + "gergur, Reconnect) or to restart your session, which loads the current tools.";

    /// <summary>What an MCP client is told in initialize, for clients that show server instructions to the model.</summary>
    internal const string McpInstructions =
        "Gergur is the user's own browser, logged in as them. Before acting, call gergur_set_task with a few words on what "
        + "you are doing; the user sees it. Work in your own window (gergur_open_window): it stays hidden from the user "
        + "unless they choose to look. To sign in to a site, load its sign-in page and call gergur_login: the person's "
        + "saved passwords fill in by themselves and you never see them. For Sign in with Google, click the site's Google "
        + "button and call gergur_login: it chooses the Google account the person uses there, or the one you name in "
        + "account, which is then remembered for the site.";

    /// <summary>The refusal for an MCP agent that has not said what it is doing, or null when it may go ahead.</summary>
    internal static object? TaskRefusal(string tool, AgentCaller? caller, Func<string, string?> taskOf)
        => caller is { Kind: "MCP" } && NeedsTask(tool) && taskOf(caller.Key) is null ? McpError(TaskFirstText) : null;

    /// <summary>Every tool maps onto an endpoint this server already serves.</summary>
    internal static object[] McpTools() =>
    [
        Tool("gergur_set_task",
            "Say what you are doing, in a few words the user can read, such as \"Researching flights to Tokyo\". Required before the other tools work (listing tabs and reading settings aside); call it again whenever the task changes.",
            new { task = Text("What you are doing, in a few words.") },
            ["task"]),
        Tool("gergur_list_tabs",
            "List every open tab across all Gergur windows: flat index, which window, url, title, sleep state, and any errors the page reported.",
            new { }, []),
        Tool("gergur_open_tab",
            "Open a url in a new tab. Bare terms are treated as a search. Set background to leave the user on the tab they are reading.",
            new
            {
                url = Text("The url or search terms to open."),
                background = new { type = "boolean", description = "Open without switching to it. Defaults to false." },
                muted = Muted(),
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
            "Evaluate JavaScript in a page and return the result. A promise is awaited, so fetch and the like come back with their value rather than an empty object. Refused on a page where a password is filled in: use gergur_login there.",
            new { js = Text("The expression to evaluate."), id = Id(), index = Index() }, ["js"]),
        Tool("gergur_click",
            "Click the first element matching a CSS selector with a real mouse click, the kind sites accept as a person's. Scrolls it into view and animates a visible cursor there first. Reports anything covering it (then it calls the element's click() instead of clicking the cover), a file chooser the click opened (closed unseen: use gergur_upload), tabs it opened, and page dialogs it answered.",
            new { selector = Text("A CSS selector."), id = Id(), index = Index() }, ["selector"]),
        Tool("gergur_type_text",
            "Type into an input, textarea or editable element as the keyboard does, so every framework sees it. Replaces what is there unless clear is false. Reports whether the field then holds the text, without echoing it back.",
            new
            {
                selector = Text("A CSS selector for the field."),
                text = Text("The text to enter. Empty with clear empties the field."),
                clear = new { type = "boolean", description = "Replace what is in the field. Defaults to true; false adds to the end." },
                submit = new { type = "boolean", description = "Press Enter afterwards." },
                id = Id(),
                index = Index(),
            },
            ["selector", "text"]),
        Tool("gergur_press_key",
            "Press a key or chord, such as Enter, Escape, Tab, ArrowDown, PageDown, F5, a single character, Ctrl+A or Shift+Tab. Goes to the element a selector names, or to whatever has the focus.",
            new
            {
                key = Text("The key or chord."),
                selector = Text("Optional: focus this element first."),
                times = new { type = "integer", description = "Press it this many times, up to 50. Defaults to 1." },
                id = Id(),
                index = Index(),
            },
            ["key"]),
        Tool("gergur_hover",
            "Move the mouse over an element and leave it there, for menus and tooltips that open on hover.",
            new { selector = Text("A CSS selector."), id = Id(), index = Index() }, ["selector"]),
        Tool("gergur_scroll",
            "Scroll the page or something in it. With only a selector, brings that element to the middle of the view. With dx/dy, turns the mouse wheel by that many pixels, over the selector's element (to scroll a list or panel) or the middle of the page. With to, goes to the top or bottom. With nothing, scrolls down 600 pixels. Answers where the page is now.",
            new
            {
                selector = Text("Optional element to bring into view, or to scroll inside."),
                dx = new { type = "number", description = "Pixels right (negative: left)." },
                dy = new { type = "number", description = "Pixels down (negative: up)." },
                to = new { type = "string", @enum = new[] { "top", "bottom" }, description = "Go to the top or bottom of the page." },
                id = Id(),
                index = Index(),
            },
            []),
        Tool("gergur_select_option",
            "Choose an option in a <select> drop-down, by its value or by the words shown on it. When none fits, the options there are come back. Drop-downs a site builds from ordinary elements are clicked instead.",
            new
            {
                selector = Text("A CSS selector for the <select>."),
                value = Text("The option's value attribute."),
                label = Text("The words shown on the option."),
                id = Id(),
                index = Index(),
            },
            ["selector"]),
        Tool("gergur_wait_for",
            "Wait until an element is on the page and visible, or some words are in its text, or with gone until they are not. Answers met:false at the timeout rather than failing.",
            new
            {
                selector = Text("A CSS selector to wait for."),
                text = Text("Words to wait for in the page's visible text."),
                gone = new { type = "boolean", description = "Wait for it to go away instead." },
                timeout = new { type = "number", description = "Seconds to wait, up to 18. Defaults to 10." },
                id = Id(),
                index = Index(),
            },
            []),
        Tool("gergur_upload",
            "Upload files from this computer: give the <input type=file> (it works even when the page hides it), or the button that opens the file chooser, and the files' absolute paths. The chooser never appears on screen.",
            new
            {
                selector = Text("A CSS selector for the file input, or for the button that opens the chooser."),
                files = new { type = "array", items = new { type = "string" }, description = "Absolute paths to the files." },
                id = Id(),
                index = Index(),
            },
            ["selector", "files"]),
        Tool("gergur_dialogs",
            "In your own window, page dialogs (alert, confirm, prompt, \"leave this page?\") are answered for you, never shown. This reads the recent ones and sets how the next are answered: accept for confirm and prompt (default true), and text for prompts.",
            new
            {
                accept = new { type = "boolean", description = "OK (true) or Cancel (false) for the next confirm and prompt dialogs." },
                text = Text("What the next prompt dialogs are answered with. null goes back to each prompt's default."),
                id = Id(),
                index = Index(),
            },
            []),
        Tool("gergur_login",
            "Sign in with a password the person saved in Gergur. When the person logs in to a site in their own window, Gergur asks whether to save it; a saved login then fills in by itself as that site's sign-in page loads, in your windows too. This checks that it did and presses the form's sign-in button (or Enter in the password field). The password never comes back to you. Call it on a freshly loaded sign-in page, before typing in it or running scripts there. With several saved accounts the most recent fills in; name another with account and it is chosen from the person's saved ones (in your own window; it borrows the keyboard for about a second). A sign-in form inside a frame the page holds (a sign-in provider's, say) is signed in to the same way, with the password saved for the frame's site; the browser never fills another site's frame by itself, so there the account is chosen from the person's saved ones in your own window (the one named, else the first the browser lists), borrowing the keyboard for about a second. For a site that asks for the username on one page and the password on the next, type the username and continue yourself, then call this on the password page: it fills the password for the username that page carries. If nothing is filled, no login is saved for the site: ask the person with gergur_ask_person. Reports the account it signed in as and where the page went. On Google's account chooser (after Sign in with Google, as a page or as the browser's own chooser in your window), it chooses the Google account the person uses for that site, remembered from their own sign-ins, or the one named in account, which is then remembered for the site; it never clicks Google's consent screen or signs an account up to a site for the first time, which are the person's to decide.",
            new
            {
                submit = new { type = "boolean", description = "Press sign in (true, the default), or only fill in and check (false)." },
                account = Text("The saved username to sign in as, when the person has saved more than one for this site. Leave it out for the one the browser fills in. On Google's account chooser, the Google address to choose; leave it out for the one the person uses for that site."),
                id = Id(),
                index = Index(),
            },
            []),
        Tool("gergur_ask_person",
            "Ask the person at the computer for something only they can do or decide: a code from their phone, a captcha, a login, a choice. It appears in a bar across their window with a reply box, Done and Can't, and a button to show your window. Waits up to the timeout; if they have not answered by then, call again with the id it gave to keep waiting.",
            new
            {
                question = Text("What you need from them, in a sentence. Say where to look, such as \"in the agent window\"."),
                id = Text("The id of a question already asked, to keep waiting for its answer instead of asking again."),
                timeout = new { type = "number", description = "Seconds to wait this time, up to 18. Defaults to 18." },
            },
            []),
        Tool("gergur_downloads",
            "List the downloads that came from your own windows and tabs, with where each was saved and how far along it is. Set wait to come back once none is still running.",
            new
            {
                wait = new { type = "boolean", description = "Wait until none is running, up to the timeout." },
                timeout = new { type = "number", description = "Seconds to wait, up to 18. Defaults to 15." },
            },
            []),
        Tool("gergur_open_window",
            "Open another browser window on the same session. Leave focus false to work in it without taking the screen from whoever is using the browser: it stays hidden, and silent (pages still play, the person just does not hear them) until the person shows it.",
            new
            {
                url = Text("Optional url to open in the new window's first tab."),
                focus = new { type = "boolean", description = "Bring the new window to the front. Defaults to false." },
                muted = Muted(),
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
        => HandleJsonRpcAsync(body, CallToolAsync, clientName =>
        {
            // The client named itself; give it a session to carry that name on every call.
            string session = _session.Agents.BeginSession(clientName);
            if (CurrentRequest.Value is { } context)
                context.SessionToSend = session;
        });

    /// <summary>
    /// The JSON-RPC envelope, split from the browser so it can be tested without an
    /// engine: every malformed-input path below is reachable with no session at all.
    /// </summary>
    internal static async Task<(int, string, byte[])> HandleJsonRpcAsync(
        JsonDocument? body, Func<JsonElement, Task<object>> callTool, Action<string?>? initialized = null)
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
                initialized?.Invoke(ClientNameIn(root));
                return (200, "application/json", Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        protocolVersion = McpProtocolVersion,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "gergur", version = "1.0.0" },
                        instructions = McpInstructions,
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
        if (TaskRefusal(name, CurrentRequest.Value?.Caller, _session.Agents.TaskOf) is { } sayFirst)
            return sayFirst;
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

    private const string NothingMatches = "nothing on the page matches that selector (or it is not a valid CSS selector)";
    private const string WentAway = "the element went away before it could be reached";

    internal const string PasswordOnPage =
        "a password is filled in on this page, so scripts are not run here: an agent signs in with the person's saved "
        + "passwords without ever reading them. Use gergur_login to sign in, or the other tools to work on the page.";

    internal const string NoPasswordField =
        "there is no password field on this page, nor in a frame it holds. If it asks for the username first, type it and "
        + "continue, then call gergur_login on the page that asks for the password. A sign-in form in a frame inside "
        + "another frame cannot be reached: then ask the person with gergur_ask_person.";

    internal const string PasswordElsewhere =
        "the password field on this page is inside a component, or a frame inside a frame, that gergur_login cannot reach, "
        + "so it cannot sign in here. Ask the person with gergur_ask_person to sign in.";

    internal const string SeveralFramesAsk =
        "more than one frame on this page asks for a password, so none is signed in to. Ask the person with gergur_ask_person.";

    internal const string FrameFillsOnChoice =
        "the sign-in form is inside a frame from another site, which the browser fills only once an account is chosen from "
        + "its list; gergur_login without submit false chooses one (the account named, else the first the browser lists) and signs in.";

    internal const string FrameNotPlaced =
        "the sign-in form's frame could not be told apart on the page to click into (another frame comes from the same "
        + "site, it is inside another frame, or the page draws it scaled or turned). Nothing was sent; ask the person with "
        + "gergur_ask_person.";

    internal const string FrameNotReachable =
        "the sign-in form's button or password field in its frame could not be clicked (covered, or out of view). Nothing "
        + "was sent; read the page, or ask the person.";

    internal const string FrameMovedOn =
        "the page or its sign-in frame moved on before the form could be sent, so nothing was. Read the page; call "
        + "gergur_login again if it still asks.";

    internal const string SeveralPasswordFields =
        "this page shows more than one password field (a sign-up or change-password form, most likely), so a saved "
        + "password is not sent from it. Check with gergur_login and submit false, or ask the person.";

    internal const string NoSavedLogin =
        "the password field is empty, so the person has no saved login for this site (or the page cleared it). "
        + "Saved logins fill in as the sign-in page loads, so load it again (gergur_navigate) and call gergur_login "
        + "before typing in it. Still empty: ask the person with gergur_ask_person to sign in, or to sign in once in "
        + "their own window and choose Save.";

    internal const string AccountNotSaved =
        "the person has no saved login for that account on this site (or saved it under another username), so nothing "
        + "was sent and the password field was left empty. Call gergur_login again without account for the one the browser "
        + "fills in, or ask the person with gergur_ask_person.";

    internal const string PickNeedsOwnWindow =
        "choosing one of the person's saved accounts borrows the keyboard for about a second, so it is only done in your "
        + "own window, never the person's. Open the sign-in page there (gergur_open_window) and call gergur_login again.";

    internal const string PickInterrupted =
        "the person is using another app, and choosing a saved account would have taken their keyboard from it, so it "
        + "was not done. Try again in a little while.";

    internal const string GoogleAccountNotAnAddress = "account on a Google sign-in is the Google address to choose, such as name@gmail.com";

    internal const string GoogleSiteUnknown =
        "this Google sign-in does not say which site it is for, so no remembered account applies. Pass account with the Google address to choose, or ask the person with gergur_ask_person.";

    internal const string NoGoogleAccountRemembered =
        "no Google account is remembered for this site: the person has not signed in to it with Google in one of their own windows yet. Ask the person which account (gergur_ask_person) and pass it as account; nothing was clicked.";

    internal const string GoogleAccountNotListed =
        "that Google account is not among the accounts this sign-in offers (it may not be signed in to Google in this browser, or the page is not an account chooser). Nothing was clicked; read the page or ask the person.";

    internal const string GoogleAccountUnreachable =
        "that Google account is listed but could not be clicked (covered or off screen). Nothing was clicked.";

    internal const string GoogleMovedOn =
        "the Google page moved on before the account could be clicked, so nothing was clicked. Read the page; call gergur_login again if it is the account chooser.";

    internal const string GoogleStillAsking =
        "Google is still asking something after the account was chosen: a consent screen, a password or a check. Read the page; whether to share their details with a site is the person's to decide, so ask them (gergur_ask_person) rather than clicking Continue or Allow.";

    /// <summary>What the last look before choosing in the browser's chooser found.</summary>
    private enum FedCmLook { Chosen, Scripted, MovedOn }

    internal const string FedCmNotAChooser =
        "the browser's Google sign-in dialog up now is not an account chooser (it reports an error or asks to sign in to Google), so nothing was chosen. Ask the person (gergur_ask_person).";

    internal const string FedCmNotGoogle =
        "the browser's sign-in chooser up now offers no Google accounts, so nothing was chosen. Ask the person (gergur_ask_person).";

    internal const string FedCmFirstSignIn =
        "that Google account has not signed in to this site before: choosing it would share the person's name and email with the site, which is theirs to agree to. Nothing was chosen; ask the person (gergur_ask_person).";

    internal const string FedCmQuietReturn =
        "the browser signed back in to this site by itself, with the Google account it used last there, as it does for the person; nothing was chosen. Read the page to see where it went.";

    internal const string FedCmQuietReturnOther =
        "the browser signed back in to this site by itself, with the Google account it used last there, not the one asked for; nothing was chosen. Read the page; to use the other account, sign out of the site and sign in again, or ask the person.";

    internal const string FedCmMovedOn =
        "the browser's Google chooser closed or changed before an account could be chosen, so nothing was chosen. Read the page; call gergur_login again if the chooser is back.";

    internal const string PickTimedOut = "choosing the saved account did not finish in time; nothing was sent. Try again.";

    /// <summary>The longest a pick, or the tidying after it, is waited for.</summary>
    internal static readonly TimeSpan PickBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether the body names an account that is not text (a number, a list): refused rather
    /// than ignored, since ignored it would sign in as the default account while the agent
    /// believes it chose another. Null counts as not named.
    /// </summary>
    internal static bool AccountNotText(JsonDocument? body)
        => body is not null && body.RootElement.ValueKind == JsonValueKind.Object
            && body.RootElement.TryGetProperty("account", out var account)
            && account.ValueKind is not (JsonValueKind.String or JsonValueKind.Null);

    internal const string PickBusy ="another agent is choosing a saved account at this moment; try again in a few seconds.";

    internal const string PickFieldUnreachable =
        "the username field could not be clicked (something covers it, or it is off the page), so no saved account was chosen.";

    internal const string ScriptedLoginPage =
        "a script was run in this page (gergur_run_javascript) since it loaded, or the tab went back or forward since "
        + "one was, so a saved password is not sent from it: the script could have changed where the form goes. Load "
        + "the page again (gergur_navigate) and call gergur_login before running scripts in it.";

    internal const string PasswordDuringScript =
        "a password was filled in on this page by the time the script finished, so its answer is kept back: it could "
        + "hold the password. Nothing was undone; the script did run. (Also said when the page could not be looked at "
        + "afterwards, because it was still loading.)";

    internal const string PasswordInPlainText =
        "a password is shown as plain text on this page (a \"show password\" switch is on), so it is not photographed. "
        + "Switch it back, or read the page with gergur_read_page.";

    /// <summary>
    /// Whether the page holds a password, as a number (the engine hands a string back quoted):
    /// 0 none, 1 one in a password field (shown as dots), 2 one shown as plain text. A field counts as a password field when it is one, or was one
    /// and a "show password" switch made it text: its autocomplete says current-password or
    /// new-password, its name or id says password, or /login marked it. Searched in the
    /// document, open shadow roots, and frames this page can reach, since a script here could
    /// read those as well. Anything that throws counts as a password, not as none.
    /// </summary>
    internal const string PasswordStateJs = """
        (() => {
            try {
                // A "show password" or "remember password" checkbox is named like one and
                // holds "on"; buttons and files hold no typed secret either.
                const notTyped = ['checkbox', 'radio', 'submit', 'button', 'image', 'reset', 'file'];
                const secret = i => i.type === 'password'
                    || (!notTyped.includes(i.type) && /\b(current|new)-password\b/i.test(i.getAttribute('autocomplete') || ''))
                    || (!notTyped.includes(i.type) && [i.name, i.id].some(n => /password|passwd|pwd|^pass$/i.test(n || '')))
                    || (i.getAttribute('data-gergur-login') || '').startsWith('pass-');
                let state = 0;
                const look = root => {
                    for (const i of root.querySelectorAll('input'))
                        if (i.value && secret(i)) state = Math.max(state, i.type === 'password' || i.type === 'hidden' ? 1 : 2);
                    for (const el of root.querySelectorAll('*')) if (el.shadowRoot) look(el.shadowRoot);
                    for (const f of root.querySelectorAll('iframe, frame')) {
                        let d = null;
                        try { d = f.contentDocument; } catch (e) { }
                        if (d) look(d);
                    }
                };
                look(document);
                return state;
            } catch (e) {
                return 2;
            }
        })()
        """;

    /// <summary>
    /// <see cref="PasswordStateJs"/> as the engine hands it back, raw JSON: a number. Anything
    /// else (a page that could not be read is "null"; a string would come back quoted) is not
    /// taken for none.
    /// </summary>
    internal static bool HoldsNoPassword(string raw) => raw == "0";

    /// <summary>Whether <see cref="PasswordStateJs"/> answered that a password shows as plain text.</summary>
    internal static bool ShowsPasswordAsText(string raw) => raw == "2";

    /// <summary>
    /// What <see cref="LoginStateJs"/> found: password fields shown in the document, whether the
    /// first holds something, the username beside it, a button that sends its form, and whether
    /// there are password fields it cannot reach (in a frame or a shadow root).
    /// </summary>
    /// <param name="UserField">The username before the password is a field that can be typed in (marked user-nonce).</param>
    /// <param name="PageUser">With no such field, the username a two-step password page carries, in a hidden or read-only field.</param>
    internal sealed record LoginState(int Fields, bool Filled, string? User, bool Button, bool Elsewhere = false,
        bool UserField = false, string? PageUser = null);

    /// <summary>How /login gets the wanted saved account into the form, if it has to.</summary>
    internal enum PickPlan
    {
        /// <summary>What is filled is what is wanted, or there is nothing to choose by.</summary>
        None,
        /// <summary>Choose it in the page's own username field.</summary>
        UserField,
        /// <summary>Add a username field before the password (a password-only page) and fill through that.</summary>
        AddedField,
    }

    /// <summary>
    /// The account to sign in as: the one asked for, or on a two-step password page the engine
    /// did not fill, the username that page carries. Never the page's choice over what the engine
    /// already filled when nothing was asked for, so a page with a stray hidden field signs in as
    /// it always did.
    /// </summary>
    internal static string? WantedAccount(LoginState state, string? asked)
        => asked ?? (!state.Filled && !state.UserField ? state.User ?? state.PageUser : null);

    /// <summary>What has to happen for <paramref name="wanted"/> to be in the form, on a page with one password field.</summary>
    internal static PickPlan PlanPick(LoginState state, string? wanted)
    {
        if (state.Fields != 1)
            return PickPlan.None;
        if (wanted is null)
            // Nothing to choose by. A password-only page the engine did not fill may still be
            // filled through an added username field, which takes no keyboard.
            return !state.Filled && !state.UserField ? PickPlan.AddedField : PickPlan.None;
        if (state.Filled && state.User == wanted)
            return PickPlan.None;
        return state.UserField ? PickPlan.UserField : PickPlan.AddedField;
    }

    /// <summary>The account as asked for: one printable line of at most 200 characters, trimmed; null when it is not one.</summary>
    internal static string? CleanAccount(string raw)
    {
        string account = raw.Trim();
        return account.Length is > 0 and <= 200 && !account.Any(char.IsControl) ? account : null;
    }

    /// <summary>What <see cref="PickStateJs"/> read: the username in the field chosen through, and whether the password field holds something.</summary>
    internal sealed record PickState(string? User, bool Filled);

    internal static PickState? ParsePickState(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object)
                return null;
            string? user = e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            return new PickState(user, e.TryGetProperty("filled", out var f) && f.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a username field of Gergur's own (marked pick-nonce) just before the password field
    /// /login marked: on a page that asks for the password alone, the engine then fills a saved
    /// account into both by itself, and its list can be opened from it. Removed again by
    /// <see cref="RemovePickFieldJs"/> before anything is sent.
    /// </summary>
    internal static string AddPickFieldJs(string nonce) => $$"""
        (() => {
            const p = document.querySelector('[data-gergur-login="pass-{{nonce}}"]');
            if (!p) return false;
            const f = document.createElement('input');
            f.type = 'text';
            f.autocomplete = 'username';
            f.setAttribute('data-gergur-login', 'pick-{{nonce}}');
            f.style.cssText = 'display:block;width:260px;height:30px;margin:4px 0';
            p.before(f);
            return true;
        })()
        """;

    /// <summary>The username in the field chosen through, and whether the password field holds something: never the password.</summary>
    internal static string PickStateJs(string nonce, string field) => $$"""
        (() => {
            const p = document.querySelector('[data-gergur-login="pass-{{nonce}}"]');
            const f = document.querySelector({{JsonSerializer.Serialize(field)}});
            return { user: f && f.value ? f.value.slice(0, 200) : null, filled: !!p && p.value.length > 0 };
        })()
        """;

    /// <summary>Empties the field chosen through and the password field, so typing the account narrows the engine's list to it.</summary>
    internal static string ClearForPickJs(string nonce, string field) => $$"""
        (() => {
            const p = document.querySelector('[data-gergur-login="pass-{{nonce}}"]');
            const f = document.querySelector({{JsonSerializer.Serialize(field)}});
            if (f) f.value = '';
            if (p) p.value = '';
            return true;
        })()
        """;

    /// <summary>Empties the password field: what the engine filled is not the account wanted.</summary>
    internal static string EmptyPasswordJs(string nonce) => $$"""
        (() => {
            const p = document.querySelector('[data-gergur-login="pass-{{nonce}}"]');
            if (p) p.value = '';
            return true;
        })()
        """;

    /// <summary>Takes away the username field <see cref="AddPickFieldJs"/> put in.</summary>
    internal static string RemovePickFieldJs(string nonce) => $$"""
        (() => {
            const f = document.querySelector('[data-gergur-login="pick-{{nonce}}"]');
            if (f) f.remove();
            return true;
        })()
        """;

    /// <summary>
    /// Looks at the sign-in form without reading the password: how many password fields are
    /// shown, whether the first one holds something, the username typed or filled before it,
    /// and the form's submit button. The field and the button are marked with the nonce so
    /// the next steps reach the same two elements.
    /// </summary>
    internal static string LoginStateJs(string nonce) => $$"""
        (() => {
            const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
            const fields = [...document.querySelectorAll('input')].filter(i => i.type === 'password' && shown(i));
            if (fields.length === 0) {
                const inside = root => {
                    for (const el of root.querySelectorAll('*'))
                        if (el.shadowRoot && (el.shadowRoot.querySelector('input[type=password]') || inside(el.shadowRoot))) return true;
                    for (const f of root.querySelectorAll('iframe, frame')) {
                        let d = null;
                        try { d = f.contentDocument; } catch (e) { }
                        if (d && (d.querySelector('input[type=password]') || inside(d))) return true;
                    }
                    return false;
                };
                return { fields: 0, elsewhere: inside(document) };
            }
            const p = fields[0];
            p.setAttribute('data-gergur-login', 'pass-{{nonce}}');
            const scope = p.form ? [...p.form.elements] : [...document.querySelectorAll('input')];
            const users = scope.filter(i => i instanceof HTMLInputElement && ['text', 'email', 'tel'].includes(i.type)
                && shown(i) && (p.compareDocumentPosition(i) & Node.DOCUMENT_POSITION_PRECEDING));
            const u = users.length ? users[users.length - 1] : null;
            const userField = !!u && !u.readOnly && !u.disabled;
            if (userField) u.setAttribute('data-gergur-login', 'user-{{nonce}}');
            // A two-step password page often carries the username it asked for on the page
            // before, in a hidden or read-only field. Only one that says it is a username.
            let pageUser = null;
            if (!userField && !(u && u.value)) {
                const says = i => /\b(username|email)\b/i.test(i.getAttribute('autocomplete') || '') || i.type === 'email'
                    || /^(user(_?name)?|e?-?mail(_?address)?|identifier|login(_?name)?|account(_?name)?)$/i.test(i.name || '')
                    || /^(user(_?name)?|e?-?mail(_?address)?|identifier|login(_?name)?|account(_?name)?)$/i.test(i.id || '');
                const known = scope.filter(i => i instanceof HTMLInputElement && i !== p && ['hidden', 'text', 'email', 'tel'].includes(i.type)
                    && i.value && i.value.length <= 200 && !/\s/.test(i.value) && says(i)
                    && (p.compareDocumentPosition(i) & Node.DOCUMENT_POSITION_PRECEDING));
                if (known.length) pageUser = known[known.length - 1].value;
            }
            let button = null;
            if (p.form)
                button = [...p.form.elements].find(b => shown(b) && !b.disabled
                    && ((b instanceof HTMLButtonElement && b.type === 'submit')
                        || (b instanceof HTMLInputElement && (b.type === 'submit' || b.type === 'image'))));
            if (button) button.setAttribute('data-gergur-login', 'go-{{nonce}}');
            return { fields: fields.length, filled: p.value.length > 0, user: u && u.value ? u.value.slice(0, 200) : null, button: !!button,
                userField, pageUser };
        })()
        """;

    /// <summary><see cref="LoginStateJs"/> in a frame, with where the frame is: its address and its site's origin.</summary>
    internal static string FrameLoginStateJs(string nonce)
        => $"(() => ({{ state: {LoginStateJs(nonce).Trim()}, href: location.href, origin: location.origin, vw: innerWidth, vh: innerHeight }}))()";

    /// <summary>What <see cref="FrameLoginStateJs"/> found in a frame, with the frame's own view size.</summary>
    internal sealed record FrameLogin(LoginState State, string Href, string Origin, double ViewW = double.NaN, double ViewH = double.NaN);

    internal static FrameLogin? ParseFrameLogin(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("state", out var state)
                || !e.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String
                || !e.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.String
                || ParseLoginState(state.GetRawText()) is not { } parsed)
                return null;
            double N(string name) => e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : double.NaN;
            return new FrameLogin(parsed, href.GetString()!, origin.GetString()!, N("vw"), N("vh"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An answer with the frame it came from added.</summary>
    internal static System.Text.Json.Nodes.JsonNode WithFrame(object answer, string origin)
    {
        var node = JsonSerializer.SerializeToNode(answer)!;
        node["frame"] = origin;
        return node;
    }

    internal enum FrameClick { Clicked, Scripted, MovedOn, NotPlaced, NotReachable }

    /// <summary>
    /// A frame's content box in the page's view, in the page's CSS pixels, and with a point
    /// asked about, whether the page's own hit test there finds the frame (nothing of the page
    /// over it).
    /// </summary>
    internal sealed record FrameBox(double X, double Y, double W, double H, double ViewW, double ViewH, bool? Hit);

    /// <summary>
    /// Run in the page: its one frame whose address is on <paramref name="origin"/> (the frame's
    /// own site), brought into view when no point is asked about, its content box measured, and
    /// the point, when asked about, hit tested. The frame's element is the page's; the frame may
    /// be another site's, in another process, which the DevTools frame tree of the page leaves
    /// out (measured), so the page is asked. Count is how many frames are on that site, or -1
    /// when the frame is drawn scaled, turned or skewed (by itself or anything around it): its
    /// laid-out size and drawn size then differ, and a point in the frame would not be the same
    /// point on the page, so a click could land on another control of the frame.
    /// </summary>
    internal static string FrameBoxJs(string origin, (double X, double Y)? point) => $$"""
        (() => {
            const want = {{JsonSerializer.Serialize(origin)}};
            const all = [...document.querySelectorAll('iframe, frame')].filter(f => {
                try { return new URL(f.getAttribute('src') || '', location.href).origin === want; } catch (e) { return false; }
            });
            if (all.length !== 1) return { count: all.length };
            const f = all[0];
            const asked = {{(point is { } p ? $"[{p.X.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}]" : "null")}};
            if (!asked) f.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
            const r = f.getBoundingClientRect(), s = getComputedStyle(f);
            if (Math.abs(r.width - f.offsetWidth) > 1 || Math.abs(r.height - f.offsetHeight) > 1) return { count: -1 };
            const n = v => parseFloat(v) || 0;
            const x = r.left + f.clientLeft + n(s.paddingLeft), y = r.top + f.clientTop + n(s.paddingTop);
            const w = f.clientWidth - n(s.paddingLeft) - n(s.paddingRight);
            const h = f.clientHeight - n(s.paddingTop) - n(s.paddingBottom);
            return { count: 1, x, y, w, h, vw: innerWidth, vh: innerHeight, hit: asked ? document.elementFromPoint(asked[0], asked[1]) === f : null };
        })()
        """;

    /// <summary>
    /// Run in a frame: whether the element <paramref name="selector"/> names is the one focused
    /// in it. True or false, nothing else of the page. (Not <c>document.hasFocus()</c>, which
    /// asks about the window, and an agent's hidden window does not have the system's focus.)
    /// </summary>
    internal static string FocusedJs(string selector) => $$"""
        (() => {
            const a = document.activeElement;
            return !!a && a.matches({{JsonSerializer.Serialize(selector)}});
        })()
        """;

    /// <summary>
    /// Run in the page: whether the element focused in it is a frame on <paramref name="origin"/>,
    /// so keys pressed now go into that frame. True or false.
    /// </summary>
    internal static string FrameFocusedJs(string origin) => $$"""
        (() => {
            const a = document.activeElement;
            if (!a || (a.tagName !== 'IFRAME' && a.tagName !== 'FRAME')) return false;
            try { return new URL(a.getAttribute('src') || '', location.href).origin === {{JsonSerializer.Serialize(origin)}}; } catch (e) { return false; }
        })()
        """;

    /// <summary>What <see cref="FrameBoxJs"/> answered: null unless exactly one frame is on the site.</summary>
    internal static FrameBox? ParseFrameBox(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var v = doc.RootElement;
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("count", out var count)
                || count.ValueKind != JsonValueKind.Number || count.GetDouble() != 1)
                return null;
            double N(string name) => v.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : double.NaN;
            bool? hit = v.TryGetProperty("hit", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False ? h.GetBoolean() : null;
            return new FrameBox(N("x"), N("y"), N("w"), N("h"), N("vw"), N("vh"), hit);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the frame's element the page has is the frame that was read: its content box the
    /// size of the frame's own view, to a pixel or two. Another frame of the same site that the
    /// page shows at another size is not taken for it.
    /// </summary>
    internal static bool SameFrame(FrameBox box, FrameLogin frame)
        => double.IsFinite(frame.ViewW) && double.IsFinite(frame.ViewH)
            && Math.Abs(box.W - frame.ViewW) <= 2 && Math.Abs(box.H - frame.ViewH) <= 2;

    /// <summary>
    /// The point in the page's view at an element's centre in a frame, or null when it is not
    /// one to click: the element covered or out of the frame's view, the point outside the
    /// frame's box, or outside the page's view.
    /// </summary>
    internal static (double X, double Y)? PointInFrame(FrameBox box, Located spot)
    {
        double[] all = [box.X, box.Y, box.W, box.H, box.ViewW, box.ViewH, spot.X, spot.Y];
        if (!spot.CanClickAt || all.Any(v => !double.IsFinite(v)))
            return null;
        if (spot.X < 0 || spot.Y < 0 || spot.X >= box.W || spot.Y >= box.H)
            return null;
        double x = box.X + spot.X, y = box.Y + spot.Y;
        return x >= 0 && y >= 0 && x < box.ViewW && y < box.ViewH ? (x, y) : null;
    }

    /// <summary>Whether the password field /login used is still on the page and shown: the sign-in did not go through, or asks again.</summary>
    internal static string StillAskingJs(string nonce) => $$"""
        (() => {
            const p = document.querySelector('[data-gergur-login="pass-{{nonce}}"]');
            if (!p) return false;
            const r = p.getBoundingClientRect();
            return r.width > 0 && r.height > 0;
        })()
        """;

    /// <summary>
    /// Where /login stops before sending anything, with its answer, or null to go on and sign in.
    /// The submit false answer is the one that works on a page an agent scripted: it sends
    /// nothing, and the username it reports is on the page for anyone to read.
    /// </summary>
    internal static (int Status, object Answer)? LoginStop(LoginState? state, bool submit, bool scripted)
    {
        if (state is null || state.Fields == 0)
            return (200, new { ok = false, filled = false, error = state?.Elsewhere == true ? PasswordElsewhere : NoPasswordField });
        if (!state.Filled)
            return (200, new { ok = false, filled = false, error = NoSavedLogin });
        if (!submit)
            return (200, new { ok = true, filled = true, user = state.User, submitted = false });
        if (state.Fields > 1)
            return (409, new { error = SeveralPasswordFields });
        if (scripted)
            return (409, new { error = ScriptedLoginPage });
        return null;
    }

    /// <summary>What <see cref="LoginStateJs"/> answered, or null when the answer is not one.</summary>
    internal static LoginState? ParseLoginState(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Number)
                return null;
            bool B(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            string? S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new LoginState(f.GetInt32(), B("filled"), S("user"), B("button"), B("elsewhere"), B("userField"), S("pageUser"));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>How long the cursor takes to glide to an element, so the act lands when it arrives.</summary>
    private static readonly TimeSpan CursorGlide = TimeSpan.FromMilliseconds(600);

    /// <summary>The least a single read in a wait is given, so the last one is not handed nothing.</summary>
    private static readonly TimeSpan MinRead = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions QuietJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>An answer with its empty parts left out, so an agent reads what happened and not a list of what did not.</summary>
    private static byte[] Quiet(object o) => JsonSerializer.SerializeToUtf8Bytes(o, QuietJson);

    /// <summary>Where an element is in the view, as the page measured it, in the page's own pixels.</summary>
    /// <param name="Covered">Something else is on top at its centre, so a click there would reach that instead.</param>
    /// <param name="CoveredBy">What is on top, as tag#id.class, for the agent to close.</param>
    internal sealed record Located(double X, double Y, double W, double H, string Tag, string? Type,
        bool FileInput, bool InView, bool Covered, string? CoveredBy)
    {
        /// <summary>A click at its centre would reach it and nothing else.</summary>
        public bool CanClickAt => InView && !Covered;
    }

    /// <summary>What <see cref="LocateJs"/> answered, or null when nothing matched or the answer is not one.</summary>
    internal static Located? ParseLocated(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True)
                return null;
            double N(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            string? S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool B(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            return new Located(N("x"), N("y"), N("w"), N("h"), S("tag") ?? "", S("type"), B("fileInput"), B("inView"), B("covered"), S("coveredBy"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds an element, scrolls it to the middle of the view and measures it: its centre,
    /// whether that is on screen, and whether something else is on top there. A label for
    /// the element counts as the element, since clicking it is clicking the field.
    /// With animate, the visible cursor sets off towards it.
    /// </summary>
    internal static string LocateJs(string selector, bool animate) => $$"""
        (() => {
            let el;
            try { el = document.querySelector({{JsonSerializer.Serialize(selector)}}); } catch (e) { return { found: false }; }
            if (!el) return { found: false };
            el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
            const r = el.getBoundingClientRect();
            const x = r.left + r.width / 2, y = r.top + r.height / 2;
            const inView = r.width > 0 && r.height > 0 && x >= 0 && y >= 0 && x < innerWidth && y < innerHeight;
            let covered = false, coveredBy = null;
            if (inView) {
                const top = document.elementFromPoint(x, y);
                const label = top && top.closest ? top.closest('label') : null;
                if (top && top !== el && !el.contains(top) && !(label && label.control === el)) {
                    covered = true;
                    const cls = typeof top.className === 'string' ? top.className.split(' ').filter(Boolean).slice(0, 2) : [];
                    coveredBy = (top.tagName.toLowerCase() + (top.id ? '#' + top.id : '') + cls.map(c => '.' + c).join('')).slice(0, 80);
                }
            }
            {{(animate ? CursorJs + AnimateJs : "")}}
            return { found: true, x, y, w: r.width, h: r.height, tag: el.tagName.toLowerCase(),
                type: typeof el.type === 'string' ? el.type : null,
                fileInput: el instanceof HTMLInputElement && el.type === 'file', inView, covered, coveredBy };
        })()
        """;

    private const string AnimateJs = """
        if (inView) __gergurCursorTo(x, y, () => {
            const prev = el.style.outline;
            el.style.outline = '2px solid #3D7BFA';
            setTimeout(() => { el.style.outline = prev; }, 600);
        });
        """;

    private async Task<(Located? Spot, bool Ready)> LocateAsync(Tab tab, string selector, bool animate)
    {
        var (raw, ready) = await OnUiAsync(() => tab.ReadScriptAsync(LocateJs(selector, animate), Tab.WakeTimeout));
        return (ready ? ParseLocated(raw) : null, ready);
    }

    /// <summary>The element's own click(), for when a real one cannot reach it.</summary>
    internal static string SyntheticClickJs(string selector) => $$"""
        (() => {
            let el;
            try { el = document.querySelector({{JsonSerializer.Serialize(selector)}}); } catch (e) { return false; }
            if (!el) return false;
            el.click();
            return true;
        })()
        """;

    /// <summary>
    /// Gives an element the keyboard focus, and either selects what is in it, for typing
    /// over, or puts the caret at the end, for adding to it. True when it took the focus.
    /// </summary>
    internal static string FocusJs(string selector, bool selectAll) => $$"""
        (() => {
            let el;
            try { el = document.querySelector({{JsonSerializer.Serialize(selector)}}); } catch (e) { return false; }
            if (!el) return false;
            el.focus({ preventScroll: true });
            if ({{(selectAll ? "true" : "false")}}) {
                if (typeof el.select === 'function') el.select();
                else if (el.isContentEditable) {
                    const range = document.createRange();
                    range.selectNodeContents(el);
                    const chosen = getSelection();
                    chosen.removeAllRanges();
                    chosen.addRange(range);
                }
            } else if (typeof el.setSelectionRange === 'function' && typeof el.value === 'string') {
                try { el.setSelectionRange(el.value.length, el.value.length); } catch (e) { }
            }
            const active = document.activeElement;
            return active === el || el.contains(active);
        })()
        """;

    /// <summary>
    /// Sets a field's value the way React and its kind notice, for when real typing cannot
    /// be sent: the prototype's setter, then input and change.
    /// </summary>
    internal static string SetValueJs(string selector, string text, bool replace) => $$"""
        (() => {
            let el;
            try { el = document.querySelector({{JsonSerializer.Serialize(selector)}}); } catch (e) { return false; }
            if (!el) return false;
            el.focus();
            const text = {{JsonSerializer.Serialize(text)}};
            if (el.isContentEditable && !(el instanceof HTMLInputElement) && !(el instanceof HTMLTextAreaElement)) {
                el.textContent = {{(replace ? "text" : "el.textContent + text")}};
            } else {
                const next = {{(replace ? "text" : "(el.value || '') + text")}};
                const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                const desc = Object.getOwnPropertyDescriptor(proto, 'value');
                if (desc && desc.set) desc.set.call(el, next); else el.value = next;
            }
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            return true;
        })()
        """;

    /// <summary>What a field holds now, as a string, or null when it is gone.</summary>
    internal static string ValueJs(string selector) => $$"""
        (() => {
            let el;
            try { el = document.querySelector({{JsonSerializer.Serialize(selector)}}); } catch (e) { return null; }
            if (!el) return null;
            return typeof el.value === 'string' ? el.value : (el.innerText || '');
        })()
        """;

    /// <summary>
    /// Whether a field says what was typed: all of it when it was typed over, its end when
    /// added to. Null when the field could not be read, which after Enter is often because
    /// the page went on to the next one.
    /// </summary>
    internal static bool? Matches(string? now, string text, bool replaced)
    {
        if (now is null)
            return null;
        string trimmed = now.TrimEnd();
        return replaced
            ? now == text || trimmed == text
            : now.EndsWith(text, StringComparison.Ordinal) || trimmed.EndsWith(text, StringComparison.Ordinal);
    }

    internal static string UnknownKey(string key)
        => $"not a key Gergur knows: \"{Truncate(key, 40)}\". Use a name such as Enter, Tab, Escape, Backspace, ArrowDown, PageDown or F5, "
            + "a single character, or a chord such as Ctrl+A or Shift+Tab.";

    private const string ScrollStateJs = """
        ({ ok: true, x: Math.round(scrollX), y: Math.round(scrollY),
           height: document.documentElement.scrollHeight, viewport: innerHeight,
           atTop: scrollY <= 0,
           atBottom: Math.ceil(scrollY + innerHeight) >= document.documentElement.scrollHeight })
        """;

    /// <summary>Two numbers from a page's [x, y], or null.</summary>
    internal static (double X, double Y)? ParsePoint(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var e = doc.RootElement;
            return e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 2
                && e[0].ValueKind == JsonValueKind.Number && e[1].ValueKind == JsonValueKind.Number
                ? (e[0].GetDouble(), e[1].GetDouble())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Chooses an option in a select, by its value or else by the words on it (spacing and
    /// then case ignored), with input and change fired as a person's choice fires them.
    /// When none fits, the options there are come back so the agent can pick one.
    /// </summary>
    internal static string SelectJs(string selector, string? value, string? label) => $$"""
        (() => {
            let el;
            try { el = document.querySelector({{JsonSerializer.Serialize(selector)}}); } catch (e) { return null; }
            if (!(el instanceof HTMLSelectElement)) return null;
            const want = {{JsonSerializer.Serialize(value)}}, words = {{JsonSerializer.Serialize(label)}};
            const norm = s => (s || '').trim().split(' ').filter(Boolean).join(' ');
            const opts = Array.from(el.options);
            let found = want !== null ? opts.find(o => o.value === want) : undefined;
            if (!found && words !== null) {
                found = opts.find(o => norm(o.text) === norm(words))
                    || opts.find(o => norm(o.text).toLowerCase() === norm(words).toLowerCase());
            }
            if (!found) return { ok: false, error: 'no option like that', options: opts.slice(0, 50).map(o => ({ value: o.value, label: norm(o.text) })) };
            if (found.disabled) return { ok: false, error: 'that option is disabled' };
            el.focus();
            found.selected = true;
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            return { ok: true, value: found.value, label: norm(found.text) };
        })()
        """;

    /// <summary>
    /// Whether what a wait is for is there: an element that is laid out and not hidden,
    /// and words in the page's visible text. Both when both are named.
    /// </summary>
    internal static string WaitJs(string? selector, string? text) => $$"""
        (() => {
            try {
                const sel = {{JsonSerializer.Serialize(selector)}}, words = {{JsonSerializer.Serialize(text)}};
                let here = true;
                if (sel !== null) {
                    let el;
                    try { el = document.querySelector(sel); } catch (e) { return 'invalid selector'; }
                    here = !!el && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
                }
                if (here && words !== null) here = !!document.body && document.body.innerText.includes(words);
                return here;
            } catch (e) { return false; }
        })()
        """;

    /// <summary>How many files a file input holds, or null.</summary>
    internal static string FileCountJs(string selector) => $$"""
        (() => {
            let el;
            try { el = document.querySelector({{JsonSerializer.Serialize(selector)}}); } catch (e) { return null; }
            return el && el.files ? el.files.length : null;
        })()
        """;

    /// <summary>The paths an upload names: an array of strings, or one string.</summary>
    internal static IReadOnlyList<string> FilesIn(JsonDocument? body)
    {
        if (body is not { RootElement.ValueKind: JsonValueKind.Object } || !body.RootElement.TryGetProperty("files", out var files))
            return [];
        return files.ValueKind switch
        {
            JsonValueKind.String => [files.GetString() ?? ""],
            JsonValueKind.Array => files.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "").ToList(),
            _ => [],
        };
    }

    /// <summary>The page dialogs a tab answered, as an agent reads them; null when there were none.</summary>
    internal static object[]? DialogsOut(IReadOnlyList<Tab.DialogSeen> dialogs)
        => dialogs.Count == 0
            ? null
            : dialogs.Select(d => (object)new { kind = d.Kind, message = d.Message, accepted = d.Accepted, answer = d.Answer }).ToArray();

    /// <summary>The tabs a tab has opened that are still open, by id. UI thread work, so it is sent there.</summary>
    private Task<HashSet<string>> OpenedFromAsync(Tab tab)
        => OnUiAsync(() => Task.FromResult(AllTabs().Where(e => e.Tab.Opener == tab).Select(e => e.Tab.Id).ToHashSet()));

    /// <summary>Tabs the tab opened since <paramref name="before"/>: a popup or target=_blank link. Null when none.</summary>
    private async Task<object[]?> NewlyOpenedAsync(Tab tab, HashSet<string> before)
    {
        await Task.Delay(150); // the engine's NewWindowRequested comes a moment after the click
        var opened = await OnUiAsync(() => Task.FromResult(AllTabs()
            .Where(e => e.Tab.Opener == tab && !before.Contains(e.Tab.Id))
            .Select(e => (object)new { id = e.Tab.Id, url = InternalPages.NameOf(e.Tab.Url) })
            .ToArray()));
        return opened.Length == 0 ? null : opened;
    }

    /// <summary>
    /// An agent's downloads: from a window of its own, or from a tab it was acting in when the
    /// download started (<see cref="DownloadOwner"/>). The person's own downloads are theirs
    /// and never listed, in a tab an agent once touched included. UI thread.
    /// </summary>
    private (int Running, object[] Listed) DownloadsOf(string agentKey)
    {
        var mine = _session.Downloads.Items.Where(d => d.AgentKey == agentKey).ToList();
        return (mine.Count(d => d.IsRunning), mine.Select(d => (object)new
        {
            id = d.Id,
            file = d.FileName,
            path = d.FilePath,
            state = d.State.ToString(),
            received = d.BytesReceived,
            total = d.TotalBytes,
            from = Truncate(d.Uri, 300),
        }).ToArray());
    }

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
        // A click or typing from here is the person's, as far as a fullscreen request can
        // tell. The window is told it was an agent, so it does not take the screen for it.
        Acting(tab);
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

    /// <summary>params.clientInfo.name of an initialize, if it has one.</summary>
    internal static string? ClientNameIn(JsonElement request)
        => request.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("clientInfo", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body, string? mcpSessionId = null)
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
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n"
            // A hex guid this server made, so nothing in it can end the header early.
            + (mcpSessionId is null ? "" : $"Mcp-Session-Id: {mcpSessionId}\r\n")
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
