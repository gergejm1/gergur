using System.Text;

namespace Gergur.App;

/// <summary>Who made an agent request, as far as this browser can tell.</summary>
/// <param name="Key">What the agent is told apart by: its MCP session, or the name it gave.</param>
/// <param name="Name">What the agents panel calls it.</param>
/// <param name="Kind">"MCP" or "API".</param>
public sealed record AgentCaller(string Key, string Name, string Kind);

/// <summary>One thing an agent did, in words for the panel. Never what it typed or the script it ran.</summary>
public sealed record AgentAction(DateTime AtUtc, string Text, string? TabId);

/// <summary>An agent as the panel shows it, copied out so the panel never holds the live record.</summary>
public sealed record AgentSnapshot(
    string Key, string Name, string Kind, DateTime FirstSeenUtc, DateTime LastSeenUtc,
    int ActionCount, IReadOnlyList<AgentAction> Recent, IReadOnlyList<string> TabIds)
{
    public bool IsActive(DateTime nowUtc) => nowUtc - LastSeenUtc < AgentActivity.ActiveFor;
}

/// <summary>
/// What agents have done in this browser since it started, for the agents panel: which
/// agent, its last few actions, and the tabs it has worked in.
///
/// Agents are told apart the way each one can be. An MCP client gives its name once, in
/// <c>initialize</c>, and every request after that comes on a fresh connection, so the
/// server hands out an <c>Mcp-Session-Id</c> there (the MCP spec's own mechanism) and the
/// client sends it back on every call. A plain API script may name itself with an
/// <c>X-Gergur-Agent</c> header; one that does not is "Script".
///
/// Kept in memory only and bounded, and it records what kind of thing was done and to
/// which tab, never the text typed, the script run or a page's contents: the panel is
/// for seeing who is here, not a second copy of everything they read.
/// </summary>
public sealed class AgentActivity
{
    /// <summary>How long after its last request an agent still counts as active.</summary>
    public static readonly TimeSpan ActiveFor = TimeSpan.FromSeconds(30);

    internal const int MaxAgents = 20;
    internal const int MaxRecent = 12;
    internal const int MaxTabs = 8;
    internal const int MaxSessions = 64;
    internal const int MaxNameLength = 40;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<string> _sessionOrder = new();

    private sealed class Entry
    {
        public required AgentCaller Caller;
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
        public int Count;
        public readonly List<AgentAction> Recent = new();
        public readonly List<string> Tabs = new();
    }

    /// <summary>Raised after every recorded action, on the thread that recorded it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts an MCP session for a client that named itself <paramref name="clientName"/>
    /// in <c>initialize</c>, and answers the id to send back in <c>Mcp-Session-Id</c>.
    /// </summary>
    public string BeginSession(string? clientName)
    {
        string id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _sessions[id] = Pretty(clientName, "MCP client");
            _sessionOrder.Enqueue(id);
            while (_sessionOrder.Count > MaxSessions)
                _sessions.Remove(_sessionOrder.Dequeue());
        }
        return id;
    }

    /// <summary>
    /// Who a request is from: the MCP session it carries if this browser started it, else
    /// the name in its <c>X-Gergur-Agent</c> header, else an MCP client or a script with no
    /// name. A session id this browser does not know (it restarted since) is not refused,
    /// only unnamed: refusing would break a client that was working a moment ago.
    /// </summary>
    public AgentCaller Identify(string? sessionId, string? agentHeader, bool viaMcp)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(sessionId, out var name))
                    return new AgentCaller("mcp:" + sessionId, name, "MCP");
            }
        }
        if (!string.IsNullOrWhiteSpace(agentHeader))
        {
            string name = Pretty(agentHeader, "Script");
            return new AgentCaller("name:" + name.ToLowerInvariant(), name, viaMcp ? "MCP" : "API");
        }
        return viaMcp ? new AgentCaller("mcp:unnamed", "MCP client", "MCP") : new AgentCaller("api:unnamed", "Script", "API");
    }

    /// <summary>Records one action by <paramref name="caller"/>, on <paramref name="tabId"/> when it was about a tab.</summary>
    public void Record(AgentCaller caller, string action, string? tabId, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(caller.Key, out var entry))
            {
                if (_agents.Count >= MaxAgents)
                {
                    // The one gone quiet longest makes room.
                    var oldest = _agents.MinBy(a => a.Value.LastSeenUtc).Key;
                    _agents.Remove(oldest);
                }
                entry = new Entry { Caller = caller, FirstSeenUtc = nowUtc };
                _agents[caller.Key] = entry;
            }
            entry.LastSeenUtc = nowUtc;
            entry.Count++;
            entry.Recent.Insert(0, new AgentAction(nowUtc, action, tabId));
            if (entry.Recent.Count > MaxRecent)
                entry.Recent.RemoveAt(entry.Recent.Count - 1);
            if (!string.IsNullOrEmpty(tabId))
            {
                entry.Tabs.Remove(tabId);
                entry.Tabs.Insert(0, tabId);
                if (entry.Tabs.Count > MaxTabs)
                    entry.Tabs.RemoveAt(entry.Tabs.Count - 1);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Every agent seen, the most recently active first.</summary>
    public IReadOnlyList<AgentSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _agents.Values
                .OrderByDescending(e => e.LastSeenUtc)
                .Select(e => new AgentSnapshot(e.Caller.Key, e.Caller.Name, e.Caller.Kind, e.FirstSeenUtc, e.LastSeenUtc,
                    e.Count, e.Recent.ToArray(), e.Tabs.ToArray()))
                .ToArray();
        }
    }

    /// <summary>Whether any agent has made a request in the last <see cref="ActiveFor"/>.</summary>
    public bool AnyActive(DateTime nowUtc)
    {
        lock (_gate)
            return _agents.Values.Any(e => nowUtc - e.LastSeenUtc < ActiveFor);
    }

    /// <summary>
    /// A name fit to show: printable characters only, at most <see cref="MaxNameLength"/>,
    /// and a client id written as one ("claude-code" reads "Claude Code"). It came over the
    /// API, so it is the caller's to choose, but not to fill the panel with.
    /// </summary>
    internal static string Pretty(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        var sb = new StringBuilder();
        foreach (char c in raw.Trim())
        {
            if (sb.Length >= MaxNameLength)
                break;
            if (!char.IsControl(c) && c is not ('\u2028' or '\u2029'))
                sb.Append(c);
        }
        string name = sb.ToString().Trim();
        if (name.Length == 0)
            return fallback;
        // An identifier, all lower case with dashes or underscores: spell it as words.
        if (name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
        {
            var words = name.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
            name = string.Join(" ", words);
        }
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>
    /// What a request did, in a few words, from its route and the address it named. The
    /// address is shortened to its site; what was typed or run is never part of it.
    /// </summary>
    internal static bool IsTabId(string? id)
        => id is { Length: >= 2 and <= 10 } && id[0] == 't' && id.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    internal static string Describe(string method, string path, string? tabId, string? url)
    {
        string on = string.IsNullOrEmpty(tabId) ? "" : $" in {tabId}";
        string site = SiteOf(url);
        return (method, path) switch
        {
            ("GET", "/tabs") => "Listed the tabs",
            ("GET", "/settings") => "Read the settings",
            ("POST", "/settings") => "Changed settings",
            ("POST", "/window") => site.Length > 0 ? $"Opened a window on {site}" : "Opened a window",
            ("POST", "/open") => site.Length > 0 ? $"Opened {site}" : "Opened a tab",
            ("POST", "/navigate") => site.Length > 0 ? $"Went to {site}{on}" : $"Navigated{on}",
            ("POST", "/activate") => $"Switched to {tabId ?? "a tab"}",
            ("POST", "/close") => $"Closed {tabId ?? "a tab"}",
            ("GET", "/page") => $"Read the page{on}",
            ("GET", "/html") => $"Read the page's HTML{on}",
            ("GET", "/screenshot") => $"Took a screenshot{on}",
            ("GET", "/console") => $"Read page errors{on}",
            ("POST", "/eval") => $"Ran a script{on}",
            ("POST", "/click") => $"Clicked{on}",
            ("POST", "/type") => $"Typed{on}",
            _ => $"{method} {path}",
        };
    }

    private static string SiteOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "";
        string host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }
}
