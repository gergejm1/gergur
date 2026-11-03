using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The agents panel and what feeds it: telling agents apart, what is recorded of what they
/// do (and what never is), and the panel listing them with their tabs.
/// </summary>
public sealed class AgentsPanelTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 22, 0, 0, DateTimeKind.Utc);

    private static string Code(params string[] parts)
        => Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource(parts)), @"//[^\n]*", "");

    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { work(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }

    [Fact]
    public void AnMcpClientIsKnownByTheNameItGaveForItsWholeSession()
    {
        var activity = new AgentActivity();
        string session = activity.BeginSession("claude-code");
        Assert.Matches("^[0-9a-f]{32}$", session);

        var caller = activity.Identify(session, null, viaMcp: true);
        Assert.Equal(("Claude Code", "MCP"), (caller.Name, caller.Kind));
        // Two sessions of the same client are two agents.
        var other = activity.Identify(activity.BeginSession("claude-code"), null, viaMcp: true);
        Assert.NotEqual(caller.Key, other.Key);
        // A session this browser never started (it restarted since) is not refused, only unnamed.
        Assert.Equal("MCP client", activity.Identify("feedfacefeedfacefeedfacefeedface", null, viaMcp: true).Name);
    }

    [Fact]
    public void AScriptIsKnownByItsHeaderOrAsAScript()
    {
        var activity = new AgentActivity();
        var named = activity.Identify(null, "nightly-checker", viaMcp: false);
        Assert.Equal(("Nightly Checker", "API"), (named.Name, named.Kind));
        Assert.Equal(named.Key, activity.Identify(null, "Nightly Checker", viaMcp: false).Key);
        Assert.Equal("Script", activity.Identify(null, null, viaMcp: false).Name);
        Assert.Equal("Script", activity.Identify(null, "   ", viaMcp: false).Name);
    }

    [Theory]
    [InlineData("claude-code", "Claude Code")]
    [InlineData("Cursor", "Cursor")]
    [InlineData("my_agent2", "My Agent2")]
    [InlineData("evil\u0007\nname\u2028", "Evilname")]
    [InlineData("", "Script")]
    public void ANameIsMadeFitToShow(string raw, string expected)
        => Assert.Equal(expected, AgentActivity.Pretty(raw, "Script"));

    [Fact]
    public void ANameCannotFillThePanel()
        => Assert.Equal(AgentActivity.MaxNameLength, AgentActivity.Pretty(new string('A', 500), "Script").Length);

    [Theory]
    [InlineData("POST", "/navigate", "t5", "https://www.youtube.com/watch?v=x", "Went to youtube.com in t5")]
    [InlineData("POST", "/open", "t9", "https://example.com/a?b=c", "Opened example.com")]
    [InlineData("POST", "/window", "t9", null, "Opened a window")]
    [InlineData("GET", "/screenshot", "t5", null, "Took a screenshot in t5")]
    [InlineData("POST", "/eval", "t5", null, "Ran a script in t5")]
    [InlineData("POST", "/type", "t5", null, "Typed in t5")]
    [InlineData("GET", "/page", null, null, "Read the page")]
    [InlineData("POST", "/close", "t3", null, "Closed t3")]
    public void WhatAnAgentDidIsSaidInAFewWords(string method, string path, string? tab, string? url, string expected)
        => Assert.Equal(expected, AgentActivity.Describe(method, path, tab, url));

    [Fact]
    public void WhatWasTypedOrRunIsNeverRecorded()
    {
        // Describe is only ever handed the route, the tab and the url: the text and the
        // script are not among its inputs, so they cannot reach the panel.
        string server = Code("Gergur", "App", "AgentServer.cs");
        Assert.Matches(@"AgentActivity\.Describe\(method, path, tabId, StringValue\(query, body, ""url""\)\)", server);
        Assert.DoesNotMatch(@"Describe\([^)]*""(text|js|script)""", server);
    }

    [Theory]
    [InlineData("t5", true)]
    [InlineData("t123456789", true)]
    [InlineData("t", false)]
    [InlineData("5", false)]
    [InlineData("t5 and deleted everything", false)]
    [InlineData("t1234567890", false)]
    [InlineData(null, false)]
    public void OnlyATabIdIsRecordedAsOne(string? id, bool recorded)
        => Assert.Equal(recorded, AgentActivity.IsTabId(id));

    [Fact]
    public void TheServerChecksTheIdBeforeItReachesThePanel()
        => Assert.Matches(@"if \(!AgentActivity\.IsTabId\(tabId\)\)\s*tabId = null;\s*string text = AgentActivity\.Describe", Code("Gergur", "App", "AgentServer.cs"));

    [Fact]
    public void TheLogKeepsTheLatestAndStaysSmall()
    {
        var activity = new AgentActivity();
        var caller = activity.Identify(null, "busy", viaMcp: false);
        int changed = 0;
        activity.Changed += () => changed++;
        for (int i = 0; i < 40; i++)
            activity.Record(caller, $"action {i}", $"t{i % 10}", Now.AddSeconds(i));

        var agent = Assert.Single(activity.Snapshot());
        Assert.Equal(40, agent.ActionCount);
        Assert.Equal(AgentActivity.MaxRecent, agent.Recent.Count);
        Assert.Equal("action 39", agent.Recent[0].Text);
        Assert.Equal(AgentActivity.MaxTabs, agent.TabIds.Count);
        Assert.Equal("t9", agent.TabIds[0]);   // the tab worked in last comes first
        Assert.Equal(40, changed);

        for (int i = 0; i < AgentActivity.MaxAgents + 5; i++)
            activity.Record(activity.Identify(null, $"agent {i}", viaMcp: false), "x", null, Now.AddMinutes(1 + i));
        Assert.Equal(AgentActivity.MaxAgents, activity.Snapshot().Count);
    }

    [Fact]
    public void AnAgentIsActiveForHalfAMinuteAfterItsLastRequest()
    {
        var activity = new AgentActivity();
        activity.Record(activity.Identify(null, "a", viaMcp: false), "x", null, Now);
        Assert.True(activity.AnyActive(Now.AddSeconds(29)));
        Assert.False(activity.AnyActive(Now.AddSeconds(31)));
        Assert.True(activity.Snapshot()[0].IsActive(Now.AddSeconds(10)));
    }

    [Fact]
    public async Task InitializeHandsTheClientsNameOnAndToolCallsDoNot()
    {
        var names = new List<string?>();
        using var init = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"claude-code","version":"2.1"}}}""");
        var (status, _, _) = await AgentServer.HandleJsonRpcAsync(init, _ => Task.FromResult<object>(new { }), names.Add);
        Assert.Equal(200, status);
        Assert.Equal(["claude-code"], names);

        using var bare = JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{}}""");
        await AgentServer.HandleJsonRpcAsync(bare, _ => Task.FromResult<object>(new { }), names.Add);
        Assert.Equal(["claude-code", null], names);

        using var ping = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        await AgentServer.HandleJsonRpcAsync(ping, _ => Task.FromResult<object>(new { }), names.Add);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void TheSessionGoesBackInTheResponseHeaderAndEveryRequestIsAttributed()
    {
        string server = Code("Gergur", "App", "AgentServer.cs");
        Assert.Contains("Mcp-Session-Id: {mcpSessionId}", server);
        Assert.Matches(@"_session\.Agents\.Identify\(sessionId, agentName, viaMcp: path == ""/mcp""\)", server);
        Assert.Matches(@"var result = await RouteRequestAsync\(method, path, query, body\);\s*NoteForAgentsPanel\(", server);
        Assert.Equal("t12", AgentServer.IdIn("""{"ok":true,"id":"t12"}"""u8.ToArray()));
        Assert.Null(AgentServer.IdIn("not json"u8.ToArray()));
        Assert.Null(AgentServer.IdIn("""{"id":12}"""u8.ToArray()));
    }

    [Fact]
    public void ThePanelListsEachAgentWithItsLiveTabsAndEachTabCanBeShown()
    {
        OnSta(() =>
        {
            var now = DateTime.UtcNow;
            var agents = new[]
            {
                new AgentSnapshot("mcp:a", "Claude Code", "MCP", now.AddMinutes(-5), now, 14,
                    [new AgentAction(now, "Went to youtube.com in t7", "t7")], ["t7", "t8", "t2", "t3", "t4"]),
                new AgentSnapshot("api:unnamed", "Script", "API", now.AddHours(-1), now.AddMinutes(-20), 2,
                    [new AgentAction(now.AddMinutes(-20), "Listed the tabs", null)], ["t7"]),
            };
            var tabs = new Dictionary<string, AgentTabInfo>
            {
                ["t7"] = new("t7", "A song", "https://www.youtube.com/watch?v=x", Hidden: true),
                ["t2"] = new("t2", "", "https://example.com/", Hidden: false),
                ["t3"] = new("t3", "Three", "https://three.example/", Hidden: false),
                ["t4"] = new("t4", "Four", "https://four.example/", Hidden: false),
                // t8 has closed: not listed.
            };
            var shown = new List<string>();
            int pageOpened = 0;
            using var panel = new AgentsFlyout(() => agents, id => tabs.GetValueOrDefault(id), shown.Add, () => "Agent API on", () => pageOpened++);
            panel.Relayout(keepPlace: false);

            // Three tabs at most for the first agent, the closed one skipped, and t7 not
            // listed again under the second; then the way through to the full page.
            Assert.Equal(new[] { "Show A song", "Show https://example.com/", "Show Three", "Open the agents page" }, panel.Spots.Select(s => s.Spoken));
            Assert.Equal("Show its hidden window", panel.Spots[0].Tip);
            Assert.Equal("Go to this tab", panel.Spots[1].Tip);
            panel.RunSpot(panel.Spots[0]);
            Assert.Equal(["t7"], shown);
            panel.RunSpot(panel.Spots[^1]);
            Assert.Equal(1, pageOpened);
        });
    }

    /// <summary>
    /// Builds the real main window, unshown, as startup does. The first version of the hidden
    /// button rule asked Control.Visible, which is false for everything before a window is
    /// shown, and threw here: the browser did not start. A source check had passed.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void TheMainWindowBuildsAndItsToolbarButtonsSitInARowWithNoGap(bool agentApi, bool agentsWindow)
    {
        OnSta(() =>
        {
            using var form = MainForm.ForLayoutTest(new Settings { AgentServerEnabled = agentApi }, agentsWindow);
            var buttons = form.ToolbarButtons;
            Assert.Equal((agentApi ? 5 : 4) + (agentsWindow ? 1 : 0), buttons.Length);
            Assert.Equal(agentsWindow, buttons[^1].AccessibleName == "Hide this agent window");
            for (int i = 1; i < buttons.Length; i++)
            {
                // Right to left, each just left of the one before, none on top of another.
                Assert.True(buttons[i].Right <= buttons[i - 1].Left, $"button {i} at {buttons[i].Bounds} runs into {buttons[i - 1].Bounds}");
                Assert.True(buttons[i - 1].Left - buttons[i].Right <= 12, $"a gap between {buttons[i].Bounds} and {buttons[i - 1].Bounds}");
            }
        });
    }

    /// <summary>
    /// The person brings an agent's window forward to look and hits X: it goes back behind
    /// their windows, the agent's tabs still open. Once they have taken the window over it is
    /// theirs, and X closes it like any other.
    /// </summary>
    [Fact]
    public void XHidesAnAgentsWindowUntilThePersonTakesItOver()
    {
        OnSta(() =>
        {
            using var form = MainForm.ForLayoutTest(new Settings { AgentServerEnabled = true }, agentsWindow: true);
            Assert.True(form.CloseHides);
            Assert.Contains(form.ToolbarButtons, b => b.AccessibleName == "Hide this agent window");

            form.ClaimForPerson();
            Assert.False(form.CloseHides);
            Assert.DoesNotContain(form.ToolbarButtons, b => b.AccessibleName == "Hide this agent window");
            Assert.Equal(5, form.ToolbarButtons.Length);
        });

        OnSta(() =>
        {
            using var mine = MainForm.ForLayoutTest(new Settings());
            Assert.False(mine.CloseHides);
        });
    }

    [Fact]
    public void XAltF4AndTheTaskbarAllHideItAndNothingElseDoes()
    {
        string form = Code("Gergur", "UI", "MainForm.cs");
        Assert.Contains("CloseWindowClicked += (_, _) => BeginInvoke(new Action(CloseOrSendBack));", form);
        // Hiding neither closes the window nor claims it.
        string hide = Regex.Match(form, @"internal void SendBackToAgent\(\)[\s\S]*?\n    \}").Value;
        Assert.Contains("WindowStacking.SendBack(Handle, theirs)", hide);
        Assert.DoesNotContain("Close()", hide);
        Assert.DoesNotContain("ClaimForPerson", hide);
        // Closing is still Close: a quit, the agent's own close and a closed last tab all close it.
        Assert.Contains("Tabs.LastTabClosed += (_, _) => Close();", form);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The system's Close (Alt+F4, the taskbar) sent to a real window handle: an agent's
    /// window stays, a person's closes. Through MainForm.WndProc itself, not a reading of it.
    /// </summary>
    [Fact]
    public void TheSystemsCloseHidesAnAgentsWindowAndClosesAPersons()
    {
        OnSta(() =>
        {
            using var agents = MainForm.ForLayoutTest(new Settings(), agentsWindow: true);
            SendMessage(agents.Handle, WindowStacking.WM_SYSCOMMAND, new IntPtr(WindowStacking.SC_CLOSE), IntPtr.Zero);
            Application.DoEvents();
            Assert.False(agents.IsDisposed);
            Assert.True(agents.IsHandleCreated);
            Assert.True(agents.CloseHides);

            using var mine = MainForm.ForLayoutTest(new Settings());
            SendMessage(mine.Handle, WindowStacking.WM_SYSCOMMAND, new IntPtr(WindowStacking.SC_CLOSE), IntPtr.Zero);
            Application.DoEvents();
            Assert.True(mine.IsDisposed || !mine.IsHandleCreated, "the person's window did not close");
        });
    }

    [Theory]
    [InlineData(0x0112, 0xF060, true)]    // the system menu's Close
    [InlineData(0x0112, 0xF062, true)]    // Close with the system's low bits set, as from the keyboard
    [InlineData(0x0112, 0xF020, false)]   // Minimise
    [InlineData(0x0112, 0xF030, false)]   // Maximise
    [InlineData(0x0010, 0, false)]        // WM_CLOSE: a quit, or a close from code, still closes
    public void OnlyTheSystemsCloseIsTakenForHiding(int msg, long wParam, bool hides)
        => Assert.Equal(hides, WindowStacking.IsSystemClose(msg, new IntPtr(wParam)));

    [Fact]
    public void HidingHandsTheScreenBackToThePersonsWindowUsedLast()
    {
        string agents = "agents", mine = "mine", other = "other", minimised = "minimised";
        bool CanTakeFront(string w) => w is "mine" or "other";
        string[] windows = [other, agents, mine, minimised];

        Assert.Same(mine, AppSession.WindowToReturnTo(windows, mine, agents, CanTakeFront));
        // Their last one has gone or cannot take the front: the first of theirs that can.
        Assert.Same(other, AppSession.WindowToReturnTo(windows, null, agents, CanTakeFront));
        Assert.Same(other, AppSession.WindowToReturnTo(windows, minimised, agents, CanTakeFront));
        Assert.Same(other, AppSession.WindowToReturnTo(windows, "closed", agents, CanTakeFront));
        // Never the window being hidden, and none at all when nothing of theirs is up.
        Assert.Same(mine, AppSession.WindowToReturnTo(windows, agents, agents, w => w != agents && CanTakeFront(w) && w != other));
        Assert.Null(AppSession.WindowToReturnTo([agents, minimised], null, agents, CanTakeFront));
    }

    [Fact]
    public void ALinkFromAnotherAppGoesToThePersonsWindowNotTheAgentsTheyLookedAt()
    {
        string agents = "agents", mine = "mine";
        string[] windows = [agents, mine];
        bool IsTheirs(string w) => w != agents;
        // They brought the agent's window forward last, but their own is where links go.
        Assert.Same(mine, AppSession.PickPersonsWindow(windows, mine, agents, IsTheirs));
        // Their last one not recorded or closed: still one of theirs over the agent's.
        Assert.Same(mine, AppSession.PickPersonsWindow(windows, null, agents, IsTheirs));
        Assert.Same(mine, AppSession.PickPersonsWindow(windows, "closed", agents, IsTheirs));
        // None of theirs open at all: the window in use, as before.
        Assert.Same(agents, AppSession.PickPersonsWindow([agents], null, agents, IsTheirs));
    }

    [Fact]
    public void AnEmptyPanelSaysNoAgentHasBeenHere()
    {
        OnSta(() =>
        {
            using var panel = new AgentsFlyout(() => [], _ => null, _ => { }, () => "Agent API on", () => { });
            panel.Relayout(keepPlace: false);
            Assert.Equal(["Open the agents page"], panel.Spots.Select(s => s.Spoken));
            Assert.True(panel.Height > 0);
        });
    }

    [Fact]
    public void TheAgentsButtonSitsBesideTheMenuAndAHiddenOneLeavesNoGap()
    {
        string form = Code("Gergur", "UI", "MainForm.cs");
        Assert.Contains("_agentsButton.Visible = _settings.AgentServerEnabled;", form);
        // A tab brought forward comes with its window, out of hiding and restored if minimised.
        Assert.Matches(@"await window\.Tabs\.ActivateAsync\(tab\);\s*window\.ShowToPerson\(\);", form);
        Assert.Matches(@"if \(WindowState == FormWindowState\.Minimized\)\s*WindowState = FormWindowState\.Normal;\s*Activate\(\);", form);
    }
}
