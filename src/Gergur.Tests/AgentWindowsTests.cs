using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Data;
using Gergur.UI;
using Xunit;
using Page = Gergur.App.InternalPages.Page;

namespace Gergur.Tests;

/// <summary>
/// Agents saying what they are doing, their windows hidden from the person until shown,
/// the band and the tab badge that mark what is theirs, and the agents page.
/// </summary>
public sealed class AgentWindowsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 1, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gergur-agentwin-{Guid.NewGuid():N}");

    public AgentWindowsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

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

    // ---------------------------------------------------------------- what it is doing

    [Fact]
    public void ATaskIsKeptAsOneShortPrintableLine()
    {
        Assert.Null(AgentActivity.CleanTask(null));
        Assert.Null(AgentActivity.CleanTask("   \r\n\t "));
        Assert.Null(AgentActivity.CleanTask("\u0007\u2028"));
        Assert.Equal("Researching flights to Tokyo", AgentActivity.CleanTask("  Researching\r\nflights \t to\u2028Tokyo  "));
        Assert.Equal(AgentActivity.MaxTaskLength, AgentActivity.CleanTask(new string('a', 500))!.Length);
    }

    [Fact]
    public void AnAgentsTaskIsShownAgainstItAndReplacedWhenItSaysAgain()
    {
        var activity = new AgentActivity();
        var caller = new AgentCaller("mcp:1", "Claude Code", "MCP");
        int changes = 0;
        activity.Changed += () => changes++;

        Assert.Null(activity.TaskOf(caller.Key));
        Assert.False(activity.SetTask(caller, "  ", Now));
        Assert.Equal(0, changes);

        Assert.True(activity.SetTask(caller, "Uploading the build", Now));
        Assert.Equal("Uploading the build", activity.TaskOf(caller.Key));
        Assert.Equal("Uploading the build", activity.Snapshot().Single().Task);

        Assert.True(activity.SetTask(caller, "Writing release notes", Now.AddMinutes(1)));
        Assert.Equal("Writing release notes", activity.TaskOf(caller.Key));
        Assert.Equal(Now.AddMinutes(1), activity.Snapshot().Single().LastSeenUtc);
        Assert.Equal(2, changes);
        // Saying what it does is not an action in the count.
        Assert.Equal(0, activity.Snapshot().Single().ActionCount);
        Assert.Equal("Said what it is doing", AgentActivity.Describe("POST", "/task", null, null));
    }

    [Fact]
    public void AnMcpAgentMustSayWhatItIsDoingBeforeItActs()
    {
        var mcp = new AgentCaller("mcp:1", "Claude Code", "MCP");
        string? task = null;
        string? TaskOf(string key) => key == mcp.Key ? task : null;

        // Before it has said: refused, and the refusal tells it how.
        var refusal = AgentServer.TaskRefusal("gergur_click", mcp, TaskOf);
        Assert.NotNull(refusal);
        string json = JsonSerializer.Serialize(refusal);
        Assert.Contains("\"isError\":true", json);
        Assert.Contains("gergur_set_task", json);
        // Saying it, finding a tab and reading settings never need one.
        Assert.Null(AgentServer.TaskRefusal("gergur_set_task", mcp, TaskOf));
        Assert.Null(AgentServer.TaskRefusal("gergur_list_tabs", mcp, TaskOf));
        Assert.Null(AgentServer.TaskRefusal("gergur_read_settings", mcp, TaskOf));
        // A plain script over HTTP is never held to it, nor is a request nobody could name.
        Assert.Null(AgentServer.TaskRefusal("gergur_click", new AgentCaller("api:unnamed", "Script", "API"), TaskOf));
        Assert.Null(AgentServer.TaskRefusal("gergur_click", null, TaskOf));

        task = "Uploading the build";
        foreach (var tool in AgentServer.McpTools().Select(t => JsonDocument.Parse(JsonSerializer.Serialize(t)).RootElement.GetProperty("name").GetString()!))
            Assert.Null(AgentServer.TaskRefusal(tool, mcp, TaskOf));
    }

    [Fact]
    public void SetTaskIsATool()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(AgentServer.McpTools()));
        var tool = doc.RootElement.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "gergur_set_task");
        Assert.Equal(["task"], tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(("POST", "/task"), AgentServer.RouteForTool("gergur_set_task"));
        Assert.Equal(["task"], AgentServer.RequiredArgsForTool("gergur_set_task"));
    }

    [Fact]
    public void TheRefusalTellsAClientWithAnOldToolListHowToGetTheTool()
    {
        // An MCP client keeps the tool list it fetched when it connected. One connected to a
        // Gergur from before gergur_set_task is refused everything and has no way to comply,
        // which is what an agent reported as "a set task step my tools lack".
        Assert.Contains("not among your tools", AgentServer.TaskFirstText);
        Assert.Contains("Reconnect", AgentServer.TaskFirstText);
    }

    [Fact]
    public async Task InitializeTellsTheClientToSayWhatItIsDoing()
    {
        using var doc = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        var (status, _, payload) = await AgentServer.HandleJsonRpcAsync(doc, _ => Task.FromResult<object>(new { }));
        Assert.Equal(200, status);
        string instructions = JsonDocument.Parse(payload).RootElement.GetProperty("result").GetProperty("instructions").GetString()!;
        Assert.Contains("gergur_set_task", instructions);
    }

    [Fact]
    public void TheTaskHeaderIsReadForEveryRequest()
    {
        string server = Code("src", "Gergur", "App", "AgentServer.cs");
        Assert.Matches(@"""x-gergur-task""", server);
        Assert.Matches(@"Agents\.SetTask\(context\.Caller, taskHeader", server);
        // Checked for every MCP tool call before it is dispatched.
        Assert.Matches(@"TaskRefusal\(name, CurrentRequest\.Value\?\.Caller, _session\.Agents\.TaskOf\)[\s\S]{0,80}RouteForTool\(name\)", server);
    }

    // ---------------------------------------------------------------- hidden windows

    [Fact]
    public void ThePersonClosingTheLastWindowTheyCanSeeTakesTheHiddenOnesWithIt()
    {
        // Their last visible window, with two hidden agent windows left: everything goes.
        Assert.True(MainForm.QuitsWithThisWindow(closingWasHidden: false, [true, true]));
        // One of theirs still open, or an agent's window they are looking at: nothing else closes.
        Assert.False(MainForm.QuitsWithThisWindow(false, [true, false]));
        // An agent closing its own hidden window never takes anything with it.
        Assert.False(MainForm.QuitsWithThisWindow(true, [true]));
        Assert.False(MainForm.QuitsWithThisWindow(true, [false]));
        // The last window of all: that is the ordinary quit, not this.
        Assert.False(MainForm.QuitsWithThisWindow(false, []));
    }

    [Fact]
    public void AnAgentsWindowIsHiddenFromItsFirstFrameAndOnlyShownToThePersonOnPurpose()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        // Cloaked before Show, so it never flashes up; hidden only if that worked, and only
        // then off the taskbar (a window left on screen with no button is one nobody finds).
        // What hiding, showing and taking over do is tested on a real window below.
        Assert.Matches(@"bool cloaked = WindowStacking\.Cloak\(window\.Handle, true\);[\s\S]{0,200}window\.HiddenForAgent = cloaked;[\s\S]{0,1200}window\.Show\(\);[\s\S]{0,900}if \(cloaked && !WindowStacking\.SetTaskbarButton\(window\.Handle, shown: false\)\)", form);
        // The agent API never shows a hidden window to the person.
        string server = Code("src", "Gergur", "App", "AgentServer.cs");
        Assert.Matches(@"if \(!entry\.Window\.HiddenForAgent\)\s*entry\.Window\.Activate\(\);", server);
    }

    [Fact]
    public void AnAgentsWindowWearsItsBandAndItsTabsTheirBadge()
    {
        OnSta(() =>
        {
            using var agents = MainForm.ForLayoutTest(new Settings { AgentServerEnabled = true }, agentsWindow: true);
            Assert.True(agents.TabStripForTest.AgentWindow);
            Assert.Equal("An agent has not said what it is doing", agents.AgentBannerForTest.Caption);

            agents.ClaimForPerson();
            Assert.False(agents.TabStripForTest.AgentWindow);

            using var mine = MainForm.ForLayoutTest(new Settings());
            Assert.False(mine.TabStripForTest.AgentWindow);
        });
        Assert.True(TabStripControl.ShowsAgentBadge(agentWindow: true, agentName: null));
        Assert.True(TabStripControl.ShowsAgentBadge(agentWindow: false, agentName: "Claude Code"));
        Assert.False(TabStripControl.ShowsAgentBadge(agentWindow: false, agentName: null));
        Assert.Equal("Claude Code · Uploading the build", AgentBanner.Describe("Claude Code", "Uploading the build"));
        Assert.Equal("Claude Code has not said what it is doing", AgentBanner.Describe("Claude Code", null));
    }

    [Fact]
    public void TheBandSaysWhoseWindowItIsAndItsHideButtonHides()
    {
        OnSta(() =>
        {
            using var form = new Form { Width = 800, Height = 200 };
            using var banner = new AgentBanner { Dock = DockStyle.Top };
            form.Controls.Add(banner);
            banner.SetAgent("Claude Code", "Uploading the build");
            Assert.Equal("Agent window: Claude Code · Uploading the build", banner.AccessibleName);
            int hidden = 0;
            banner.HideClicked += (_, _) => hidden++;
            // Paint once so the button has a place, then press and let go on it.
            using (var bitmap = new Bitmap(banner.Width, banner.Height))
                banner.DrawToBitmap(bitmap, banner.ClientRectangle);
            var on = new Point(banner.Width - 30, banner.Height / 2);
            var press = typeof(Control).GetMethod("OnMouseDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var release = typeof(Control).GetMethod("OnMouseUp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            press.Invoke(banner, [new MouseEventArgs(MouseButtons.Left, 1, on.X, on.Y, 0)]);
            release.Invoke(banner, [new MouseEventArgs(MouseButtons.Left, 1, on.X, on.Y, 0)]);
            Assert.Equal(1, hidden);
            // Pressed on the text and let go on the button: not a click.
            press.Invoke(banner, [new MouseEventArgs(MouseButtons.Left, 1, 60, on.Y, 0)]);
            release.Invoke(banner, [new MouseEventArgs(MouseButtons.Left, 1, on.X, on.Y, 0)]);
            Assert.Equal(1, hidden);
        });
    }

    [Fact]
    public void ATabTheAgentOpenedStopsBeingBadgedWhenThePersonGoesSomewhereInIt()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        Assert.Matches(@"NavigationRequested \+= async \(_, url\) =>\s*\{\s*ClaimForPerson\(\);\s*if \(Tabs\?\.ActiveTab is \{ AgentName: not null \} taken\)\s*\{\s*taken\.AgentName = null;", form);
        string server = Code("src", "Gergur", "App", "AgentServer.cs");
        Assert.Matches(@"window\.MarkOpenedByAgent\(tab, ", server);
    }

    // ---------------------------------------------------------------- hidden windows, for real

    /// <summary>
    /// The real main window, its real handle and the window manager's own answer: hiding
    /// cloaks it, showing uncloaks it, and the person taking it over shows it. Not shown on
    /// screen at any point, so the test disturbs nobody.
    /// </summary>
    [Fact]
    public void AnAgentsWindowIsCloakedWhenHiddenAndUncloakedWhenShownOrTakenOver()
    {
        OnSta(() =>
        {
            using var form = MainForm.ForLayoutTest(new Settings { AgentServerEnabled = true }, agentsWindow: true);
            IntPtr handle = form.Handle;
            Assert.False(form.HiddenForAgent);
            Assert.False(WindowStacking.IsCloaked(handle));

            form.SendBackToAgent();
            Assert.True(form.HiddenForAgent);
            Assert.True(WindowStacking.IsCloaked(handle));

            form.ShowToPerson();
            Assert.False(form.HiddenForAgent);
            Assert.False(WindowStacking.IsCloaked(handle));
            Assert.True(form.CloseHides);   // still the agent's: X hides it again

            form.SendBackToAgent();
            Assert.True(WindowStacking.IsCloaked(handle));
            form.ClaimForPerson();
            Assert.False(form.HiddenForAgent);
            Assert.False(WindowStacking.IsCloaked(handle));
            Assert.False(form.CloseHides);
        });
    }

    [Fact]
    public void CloakingRoundTripsThroughTheWindowManager()
    {
        OnSta(() =>
        {
            using var form = new Form();
            IntPtr handle = form.Handle;
            Assert.True(WindowStacking.Cloak(handle, true));
            Assert.True(WindowStacking.IsCloaked(handle));
            Assert.True(WindowStacking.Cloak(handle, false));
            Assert.False(WindowStacking.IsCloaked(handle));
        });
    }

    [Fact]
    public void HidingAnAgentsWindowBringsForwardOneOfTheirsPreferringOneNotMinimised()
    {
        string[] all = ["agent", "mine-min", "mine-open", "mine-last-min"];
        bool Theirs(string w) => w.StartsWith("mine", StringComparison.Ordinal);
        bool Minimised(string w) => w.EndsWith("min", StringComparison.Ordinal);
        // The one they used last is minimised, another of theirs is open: the open one.
        Assert.Equal("mine-open", MainForm.WindowToBringForward(all, "mine-last-min", "agent", Theirs, Minimised));
        // Every one of theirs minimised: the one they used last, to be restored.
        Assert.Equal("mine-last-min", MainForm.WindowToBringForward(["agent", "mine-min", "mine-last-min"], "mine-last-min", "agent", Theirs, Minimised));
        // None of theirs: nothing.
        Assert.Null(MainForm.WindowToBringForward(["agent"], null, "agent", Theirs, Minimised));
    }

    [Fact]
    public void TheBandsHideWorksFromTheKeyboardAndForAScreenReader()
    {
        OnSta(() =>
        {
            using var banner = new AgentBanner();
            int hidden = 0;
            banner.HideClicked += (_, _) => hidden++;
            Assert.True(banner.TabStop);
            var keyDown = typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            keyDown.Invoke(banner, [new KeyEventArgs(Keys.Enter)]);
            keyDown.Invoke(banner, [new KeyEventArgs(Keys.Space)]);
            keyDown.Invoke(banner, [new KeyEventArgs(Keys.A)]);
            Assert.Equal(2, hidden);
            Assert.Equal("Hide", banner.AccessibilityObject.DefaultAction);
            banner.AccessibilityObject.DoDefaultAction();
            Assert.Equal(3, hidden);
        });
    }

    // ---------------------------------------------------------------- the agents page

    private sealed class FakeAgentsHost : IAgentsHost
    {
        public List<AgentSnapshot> List = new();
        public Dictionary<string, AgentTabInfo> Tabs = new();
        public List<string> Calls = new();
        public IReadOnlyList<AgentSnapshot> Agents => List;
        public AgentTabInfo? Tab(string id) => Tabs.GetValueOrDefault(id);
        public string ApiLine => "Agent API on";
        public void Show(string tabId) => Calls.Add("show " + tabId);
        public void Hide(string tabId) => Calls.Add("hide " + tabId);
        public void DevTools(string tabId) => Calls.Add("devtools " + tabId);
        public void Snapshot(string tabId) => Calls.Add("snapshot " + tabId);
    }

    private InternalPages.Services Services(IAgentsHost host)
        => new(new HistoryStore(Path.Combine(_dir, "history.jsonl")), new BookmarkStore(Path.Combine(_dir, "bookmarks.json")),
            new DownloadManager(), (_, _) => { }, Agents: host);

    private static JsonElement Ask(Page page, object request, InternalPages.Services services)
        => JsonDocument.Parse(InternalPages.Answer(page, JsonSerializer.Serialize(request), services)).RootElement.Clone();

    private static FakeAgentsHost SampleHost()
    {
        var host = new FakeAgentsHost();
        host.List.Add(new AgentSnapshot("mcp:a", "Claude Code", "MCP", Now.AddMinutes(-10), Now, 5,
            [new AgentAction(Now, "Clicked in t7", "t7")], ["t7", "t9", "t3"], "Uploading the build"));
        host.List.Add(new AgentSnapshot("api:unnamed", "Script", "API", Now.AddHours(-1), Now.AddMinutes(-30), 1,
            [new AgentAction(Now.AddMinutes(-30), "Read the page in t7", "t7")], ["t7", "t4"]));
        host.Tabs["t7"] = new AgentTabInfo("t7", "App Store Connect", "https://appstoreconnect.apple.com/", Hidden: true, InAgentWindow: true);
        host.Tabs["t3"] = new AgentTabInfo("t3", "Notes", "https://notes.example/", Hidden: false, InAgentWindow: false, Asleep: true);
        host.Tabs["t4"] = new AgentTabInfo("t4", "Shop", "https://shop.example/", Hidden: false, InAgentWindow: false);
        host.Tabs["t5"] = new AgentTabInfo("t5", "The person's own", "https://bank.example/", Hidden: false);
        return host;
    }

    [Fact]
    public void TheAgentsPageListsEachAgentWithItsTaskTimelineAndOpenTabs()
    {
        var host = SampleHost();
        var result = Ask(Page.Agents, new { id = 1, op = "agents.list", args = new { } }, Services(host)).GetProperty("result");
        Assert.Equal("Agent API on", result.GetProperty("apiLine").GetString());
        var agents = result.GetProperty("agents").EnumerateArray().ToArray();
        Assert.Equal(2, agents.Length);
        Assert.Equal("Uploading the build", agents[0].GetProperty("task").GetString());
        Assert.Equal(JsonValueKind.Null, agents[1].GetProperty("task").ValueKind);
        Assert.Equal("Clicked in t7", agents[0].GetProperty("recent")[0].GetProperty("text").GetString());
        // Open tabs only (t9 has closed), each under the agent that used it last.
        Assert.Equal(["t7", "t3"], agents[0].GetProperty("tabs").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        Assert.Equal(["t4"], agents[1].GetProperty("tabs").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        var t7 = agents[0].GetProperty("tabs")[0];
        Assert.True(t7.GetProperty("hidden").GetBoolean());
        Assert.True(t7.GetProperty("agentWindow").GetBoolean());
        Assert.True(agents[0].GetProperty("tabs")[1].GetProperty("asleep").GetBoolean());
    }

    [Fact]
    public void TheAgentsPageActsOnlyOnTabsAnAgentHasWorkedIn()
    {
        var host = SampleHost();
        var services = Services(host);
        foreach (string op in new[] { "agents.show", "agents.devtools", "agents.snapshot" })
            Assert.True(Ask(Page.Agents, new { id = 1, op, args = new { tabId = "t7" } }, services).GetProperty("ok").GetBoolean());
        Assert.True(Ask(Page.Agents, new { id = 1, op = "agents.hide", args = new { tabId = "t7" } }, services).GetProperty("ok").GetBoolean());
        Assert.Equal(["show t7", "devtools t7", "snapshot t7", "hide t7"], host.Calls);

        host.Calls.Clear();
        // A tab of the person's no agent touched, one that closed, one with no id: refused.
        var mine = Ask(Page.Agents, new { id = 2, op = "agents.snapshot", args = new { tabId = "t5" } }, services);
        Assert.Equal("No agent has worked in that tab.", mine.GetProperty("error").GetString());
        var closed = Ask(Page.Agents, new { id = 3, op = "agents.show", args = new { tabId = "t9" } }, services);
        Assert.Equal("That tab has closed.", closed.GetProperty("error").GetString());
        Assert.False(Ask(Page.Agents, new { id = 4, op = "agents.show", args = new { } }, services).GetProperty("ok").GetBoolean());
        // An agent's tab in one of the person's windows is never hidden: their window would go.
        var theirs = Ask(Page.Agents, new { id = 5, op = "agents.hide", args = new { tabId = "t4" } }, services);
        Assert.Contains("your own windows", theirs.GetProperty("error").GetString());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void OnlyTheAgentsPageMayAskAboutAgents()
    {
        Assert.True(InternalPages.Allowed(Page.Agents, "agents.list"));
        Assert.True(InternalPages.Allowed(Page.Agents, "open"));
        Assert.False(InternalPages.Allowed(Page.Agents, "history.list"));
        Assert.False(InternalPages.Allowed(Page.Agents, "settings.set"));
        foreach (var other in Enum.GetValues<Page>().Where(p => p != Page.Agents))
            Assert.False(InternalPages.Allowed(other, "agents.snapshot"), other.ToString());
        Assert.Equal("gergur://agents", InternalPages.NameOf(InternalPages.UrlOf(Page.Agents)));
        Assert.Equal(InternalPages.UrlOf(Page.Agents), InternalPages.Resolve("gergur://agents"));
        // The agent API cannot read or script it, like every other page of the browser's.
        Assert.NotNull(Gergur.Tabs.Tab.RefusalFor(InternalPages.UrlOf(Page.Agents), null, InternalPages.UrlOf(Page.Agents)));
    }

    [Fact]
    public void TheAgentsPageAllowsPicturesAsDataAndNothingElseNew()
    {
        string page = File.ReadAllText(OwnPagesHardeningTests.FindSource("src", "Gergur", "Assets", "agents.html"));
        var csp = Regex.Match(page, @"Content-Security-Policy"" content=""([^""]+)""").Groups[1].Value;
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("img-src file: data:;", csp);
        Assert.DoesNotContain("connect-src", csp);
        // Titles and urls come from sites: never through innerHTML.
        Assert.DoesNotContain("innerHTML", page);
        // And a picture is only ever taken as one.
        Assert.Contains("message.image.indexOf(\"data:image/\") === 0", page);
    }

    [Fact]
    public void APictureIsPushedAsAJpegOrAsNone()
    {
        using var doc = JsonDocument.Parse(AgentsPage.SnapshotPush("t7", [1, 2, 3]));
        Assert.Equal("snapshot", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("t7", doc.RootElement.GetProperty("tabId").GetString());
        Assert.Equal("data:image/jpeg;base64,AQID", doc.RootElement.GetProperty("image").GetString());
        using var none = JsonDocument.Parse(AgentsPage.SnapshotPush("t7", null));
        Assert.Equal(JsonValueKind.Null, none.RootElement.GetProperty("image").ValueKind);
    }

    [Fact]
    public void ATabsPictureIsMadeSmall()
    {
        using var big = new Bitmap(1920, 1080);
        using (var g = Graphics.FromImage(big))
            g.Clear(Color.SteelBlue);
        using var png = new MemoryStream();
        big.Save(png, System.Drawing.Imaging.ImageFormat.Png);

        byte[]? small = Gergur.Tabs.Tab.Shrink(png.ToArray(), AgentsPage.SnapshotWidth);
        Assert.NotNull(small);
        using var read = Image.FromStream(new MemoryStream(small!));
        Assert.Equal(AgentsPage.SnapshotWidth, read.Width);
        Assert.Equal(315, read.Height);
        Assert.Equal(System.Drawing.Imaging.ImageFormat.Jpeg.Guid, read.RawFormat.Guid);
        // Never made bigger, and junk is no picture rather than a throw.
        using var tiny = new Bitmap(100, 50);
        using var tinyPng = new MemoryStream();
        tiny.Save(tinyPng, System.Drawing.Imaging.ImageFormat.Png);
        using (var read2 = Image.FromStream(new MemoryStream(Gergur.Tabs.Tab.Shrink(tinyPng.ToArray(), 560)!)))
            Assert.Equal(100, read2.Width);
        Assert.Null(Gergur.Tabs.Tab.Shrink([1, 2, 3], 560));
    }

    [Fact]
    public void TheAgentsPagePictureNeverWakesATab()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        var method = Regex.Match(tab, @"internal async Task<byte\[\]\?> CaptureThumbnailAsync[\s\S]*?\n    \}").Value;
        Assert.Contains("TabState.Suspended or TabState.Discarded", method);
        Assert.DoesNotContain("WakeAsync", method);
        Assert.DoesNotContain("BeginRead", method);
        Assert.Contains("RefusalFor(", method);
    }

    [Fact]
    public void ThePanelOpensTheAgentsPageInOneOfThePersonsWindows()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        Assert.Matches(@"new AgentsFlyout\([^;]*OpenAgentsPage\)", form);
        Assert.Matches(@"public void OpenAgentsPage\(\)[\s\S]{0,400}w => w is \{ OpenedByAgent: false", form);
    }
}
