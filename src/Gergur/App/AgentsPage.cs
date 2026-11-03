using Gergur.UI;

namespace Gergur.App;

/// <summary>
/// What the agents page acts through: the window it is open in. An interface so the page's
/// requests can be answered in a test. Everything that shows or hides a window, or opens
/// DevTools, is done after the request has been answered: from inside the engine's message
/// handler, showing a window is the kind of re-entry it throws on.
/// </summary>
internal interface IAgentsHost
{
    /// <summary>Every agent seen since start, most recently active first.</summary>
    IReadOnlyList<AgentSnapshot> Agents { get; }

    /// <summary>A tab by id, in any window, or null once it has closed.</summary>
    AgentTabInfo? Tab(string id);

    /// <summary>Whether the agent API is on, and where: the panel's last line.</summary>
    string ApiLine { get; }

    /// <summary>Brings the tab and its window forward, out of hiding if it was hidden.</summary>
    void Show(string tabId);

    /// <summary>Hides the agent's window the tab is in.</summary>
    void Hide(string tabId);

    /// <summary>Opens DevTools on the tab, in its own window, without showing the tab's.</summary>
    void DevTools(string tabId);

    /// <summary>Takes a small picture of the tab and pushes it to the page as a "snapshot" event.</summary>
    void Snapshot(string tabId);
}

/// <summary>
/// The agents page (gergur://agents): the agents panel at full size. Every agent seen since
/// start, what it says it is doing, its last actions with times, and its tabs, each with a
/// live picture and Show, Hide and DevTools. It records only what <see cref="AgentActivity"/>
/// does: the kind of thing done and to which tab, never what was typed or run.
/// </summary>
internal static class AgentsPage
{
    /// <summary>How wide a tab's picture is taken, in pixels: a card on the page, not a screenshot.</summary>
    internal const int SnapshotWidth = 560;

    /// <summary>Everything the page shows, as one answer to "agents.list".</summary>
    internal static object Describe(IAgentsHost host, DateTime nowUtc)
    {
        var agents = host.Agents;
        return new
        {
            apiLine = host.ApiLine,
            nowUtc = Iso(nowUtc),
            agents = agents.Select(a => new
            {
                key = a.Key,
                name = a.Name,
                kind = a.Kind,
                task = a.Task,
                active = a.IsActive(nowUtc),
                firstSeenUtc = Iso(a.FirstSeenUtc),
                lastSeenUtc = Iso(a.LastSeenUtc),
                actionCount = a.ActionCount,
                recent = a.Recent.Select(r => new { atUtc = Iso(r.AtUtc), text = r.Text, tabId = r.TabId }).ToArray(),
                // Only tabs still open, each listed once, under the agent that used it last.
                tabs = a.TabIds
                    .Where(id => FirstAgentWith(agents, id) == a)
                    .Select(host.Tab)
                    .OfType<AgentTabInfo>()
                    .Select(t => new
                    {
                        id = t.Id,
                        title = t.Title,
                        url = t.Url,
                        hidden = t.Hidden,
                        agentWindow = t.InAgentWindow,
                        asleep = t.Asleep,
                    })
                    .ToArray(),
            }).ToArray(),
        };
    }

    /// <summary>The first agent in the list (the most recently active) that has worked in a tab.</summary>
    private static AgentSnapshot? FirstAgentWith(IReadOnlyList<AgentSnapshot> agents, string tabId)
        => agents.FirstOrDefault(a => a.TabIds.Contains(tabId, StringComparer.Ordinal));

    /// <summary>The push that carries a tab's picture, or says there is none to take.</summary>
    internal static string SnapshotPush(string tabId, byte[]? jpeg)
        => System.Text.Json.JsonSerializer.Serialize(jpeg is null
            ? new { @event = "snapshot", tabId, image = (string?)null }
            : new { @event = "snapshot", tabId, image = (string?)("data:image/jpeg;base64," + Convert.ToBase64String(jpeg)) });

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O");
}
