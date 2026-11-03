using Gergur.App;

namespace Gergur.UI;

/// <summary>A tab an agent has worked in, as the agents panel shows it.</summary>
/// <param name="InAgentWindow">In a window an agent opened, behind the person's: hidden unless brought forward.</param>
internal sealed record AgentTabInfo(string Id, string Title, string Url, bool InAgentWindow);

/// <summary>
/// The panel the agents button drops: every agent that has used this browser since it
/// started, whether it is active now, what it did last, and the tabs it has been working
/// in, each of which can be brought forward with its window. It refreshes itself as
/// agents act, so it can be left open to watch one work.
/// </summary>
internal sealed class AgentsFlyout : FlyoutPanel
{
    /// <summary>How many agents and how many tabs of each the panel lists.</summary>
    internal const int MaxAgents = 4;
    internal const int MaxTabsEach = 3;

    private readonly Func<IReadOnlyList<AgentSnapshot>> _snapshot;
    private readonly Func<string, AgentTabInfo?> _tab;
    private readonly Action<string> _showTab;
    private readonly Func<string> _footer;
    private Font _bold = null!;
    private Font _small = null!;
    private Font _icons = null!;
    private Rectangle _header;
    private Rectangle _message;
    private Rectangle _footerBox;
    private int _activeCount;
    private DateTime _lastRefreshUtc;
    private readonly List<(AgentSnapshot Agent, Rectangle Bounds)> _agentRows = new();
    private readonly List<(AgentTabInfo Tab, HotSpot Spot)> _tabRows = new();

    /// <param name="snapshot">The agents seen, most recent first.</param>
    /// <param name="tab">A tab by id, or null once it has closed.</param>
    /// <param name="showTab">Brings a tab and its window forward.</param>
    /// <param name="footer">The line at the bottom: whether the agent API is on, and where.</param>
    public AgentsFlyout(Func<IReadOnlyList<AgentSnapshot>> snapshot, Func<string, AgentTabInfo?> tab, Action<string> showTab, Func<string> footer)
    {
        _snapshot = snapshot;
        _tab = tab;
        _showTab = showTab;
        _footer = footer;
        EnsureFonts();
        AccessibleName = "Agents";
    }

    protected override bool TakesFocus => true;

    protected override void MakeFonts()
    {
        base.MakeFonts();
        _bold?.Dispose();
        _small?.Dispose();
        _icons?.Dispose();
        _bold = MakeFont("Segoe UI Semibold", 10f);
        _small = MakeFont("Segoe UI", 8.5f);
        _icons = MakeIconFont(10f);
    }

    /// <summary>An agent acted: show it, if the panel is up. Safe from any thread.</summary>
    public void AgentsChanged()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(() =>
            {
                if (Visible)
                    Relayout();
            });
        }
        catch (InvalidOperationException) { }   // the handle went between the check and the call
    }

    protected override void OnWatch()
    {
        // "Active now" turns into "1 min ago" with nobody acting, so the times are redrawn
        // every few seconds while it is open.
        if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds >= 5)
            Relayout();
    }

    protected override Size LayOut()
    {
        _agentRows.Clear();
        _tabRows.Clear();
        var now = DateTime.UtcNow;
        _lastRefreshUtc = now;
        var agents = _snapshot();
        _activeCount = agents.Count(a => a.IsActive(now));

        int width = S(400);
        int pad = S(8);
        int y = pad;
        _header = new Rectangle(S(16), y, width - S(32), S(36));
        y += _header.Height;

        if (agents.Count == 0)
        {
            _message = new Rectangle(S(16), y, width - S(32), S(64));
            y += _message.Height;
        }
        else
        {
            _message = Rectangle.Empty;
            foreach (var agent in agents.Take(MaxAgents))
            {
                var row = new Rectangle(pad, y, width - pad * 2, S(52));
                _agentRows.Add((agent, row));
                y += row.Height;
                int shown = 0;
                foreach (string id in agent.TabIds)
                {
                    if (shown == MaxTabsEach)
                        break;
                    // Closed since, or already listed under an agent above.
                    if (_tab(id) is not { } tab || _tabRows.Any(t => t.Tab.Id == tab.Id))
                        continue;
                    shown++;
                    string name = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title;
                    var spot = new HotSpot(new Rectangle(pad + S(40), y, width - pad * 2 - S(40), S(34)),
                        () => _showTab(tab.Id), $"Show {name}", Tip: tab.InAgentWindow ? "Bring its window forward" : "Go to this tab");
                    AddSpot(spot);
                    _tabRows.Add((tab, spot));
                    y += spot.Bounds.Height;
                }
                y += S(4);
            }
        }

        y += S(4);
        _footerBox = new Rectangle(S(16), y, width - S(32), S(30));
        y += _footerBox.Height + pad;
        return new Size(width, y);
    }

    protected override void PaintContent(Graphics g)
    {
        var now = DateTime.UtcNow;
        DrawLabel(g, "Agents", _bold, _header, Theme.Text);
        DrawLabel(g, _activeCount switch { 0 => "none active", 1 => "1 active", var n => $"{n} active" },
            _small, _header, _activeCount > 0 ? Theme.Live : Theme.TextDim, right: true);

        if (_message != Rectangle.Empty)
        {
            DrawLabel(g, "No agent has used Gergur since it started.", Font,
                new Rectangle(_message.X, _message.Y + S(8), _message.Width, S(22)), Theme.TextDim, center: true);
            DrawLabel(g, "Agents connect through the local agent API.", _small,
                new Rectangle(_message.X, _message.Y + S(32), _message.Width, S(20)), Theme.TextDim, center: true);
        }

        foreach (var (agent, row) in _agentRows)
            PaintAgent(g, agent, row, now);
        foreach (var (tab, spot) in _tabRows)
            PaintTab(g, tab, spot);

        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, S(12), _footerBox.Top - S(2), Width - S(12), _footerBox.Top - S(2));
        DrawLabel(g, _footer(), _small, _footerBox, Theme.TextDim);
    }

    private void PaintAgent(Graphics g, AgentSnapshot agent, Rectangle row, DateTime now)
    {
        bool active = agent.IsActive(now);
        // A round avatar with the agent's initial, coloured from its name so the same agent
        // keeps its colour, and a dot on it while it is working.
        var avatar = new Rectangle(row.X + S(8), row.Y + (row.Height - S(32)) / 2, S(32), S(32));
        int hue = DownloadText.Hue(agent.Name);
        using (var fill = new SolidBrush(DownloadText.FromHsl(hue, 0.38, 0.26)))
            g.FillEllipse(fill, avatar);
        DrawLabel(g, char.ToUpperInvariant(agent.Name[0]).ToString(), _bold, avatar, DownloadText.FromHsl(hue, 0.75, 0.88), center: true);
        if (active)
        {
            var dot = new Rectangle(avatar.Right - S(10), avatar.Bottom - S(10), S(11), S(11));
            using (var ring = new SolidBrush(Theme.MenuBg))
                g.FillEllipse(ring, Rectangle.Inflate(dot, S(2), S(2)));
            using var live = new SolidBrush(Theme.Live);
            g.FillEllipse(live, dot);
        }

        int left = avatar.Right + S(12);
        var nameBox = new Rectangle(left, row.Y + S(7), row.Right - S(8) - left, S(20));
        DrawLabel(g, agent.Name, _bold, nameBox, Theme.Text);
        string when = active ? "active now" : DownloadText.Ago(agent.LastSeenUtc, now);
        DrawLabel(g, $"{agent.Kind} \u00B7 {when}", _small, nameBox, active ? Theme.Live : Theme.TextDim, right: true);

        var lastBox = new Rectangle(left, row.Y + S(28), nameBox.Width, S(18));
        string last = agent.Recent.Count > 0 ? agent.Recent[0].Text : "";
        string count = agent.ActionCount == 1 ? "1 action" : $"{agent.ActionCount:N0} actions";
        DrawLabel(g, last.Length > 0 ? $"{last} \u00B7 {count}" : count, _small, lastBox, Theme.TextDim);
    }

    private void PaintTab(Graphics g, AgentTabInfo tab, HotSpot spot)
    {
        PaintHover(g, spot);
        var r = spot.Bounds;
        // The tree line from the agent above to its tab.
        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, r.X - S(16), r.Y, r.X - S(16), r.Y + r.Height / 2);
        string title = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title;
        string site = DownloadText.Site(tab.Url);
        if (tab.InAgentWindow)
            site = site.Length > 0 ? $"{site} \u00B7 hidden" : "hidden";
        var titleBox = new Rectangle(r.X + S(8), r.Y, r.Width - S(8) - S(36), r.Height);
        int siteWidth = site.Length == 0 ? 0 : Math.Min(TextRenderer.MeasureText(site, _small).Width + S(8), titleBox.Width / 2);
        DrawLabel(g, title, Font, new Rectangle(titleBox.X, titleBox.Y, titleBox.Width - siteWidth, titleBox.Height), Theme.Text);
        if (siteWidth > 0)
            DrawLabel(g, site, _small, titleBox, Theme.TextDim, right: true);
        // A hidden tab says so: it is in a window behind the person's.
        var icon = new Rectangle(r.Right - S(32), r.Y, S(28), r.Height);
        DrawLabel(g, tab.InAgentWindow ? AgentGlyphs.Hidden : AgentGlyphs.Show, _icons, icon, Theme.TextDim, center: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold.Dispose();
            _small.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Segoe MDL2 / Fluent codepoints for the agents button and panel.</summary>
internal static class AgentGlyphs
{
    public const string Robot = "\uE99A";
    public const string Show = "\uE8A7";      // OpenInNewWindow
    public const string Hidden = "\uE7B3";    // an eye: look at a tab in a window behind yours
    public const string SendBack = "\uED1A";  // Hide, an eye struck through: an agent's window back behind yours
}
