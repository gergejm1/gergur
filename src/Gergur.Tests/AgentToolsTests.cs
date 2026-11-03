using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The agent tools built on trusted input: clicks, typing, keys, hovering, scrolling,
/// choosing options, waiting, uploads, page dialogs and downloads. What can be pinned
/// without an engine; the rest is the live harness in the scratchpad.
/// </summary>
public sealed class AgentToolsTests
{
    private static string Source(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gergur", relative));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gergur.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Gergur.slnx not found above the test binaries");
    }

    private static readonly string[] NewTools =
    [
        "gergur_press_key", "gergur_hover", "gergur_scroll", "gergur_select_option",
        "gergur_wait_for", "gergur_upload", "gergur_dialogs", "gergur_downloads",
    ];

    // ---------------------------------------------------------------- dialogs

    [Theory]
    [InlineData("alert", true, null, true, null)]
    [InlineData("alert", false, null, true, null)]          // an alert has only OK
    [InlineData("beforeunload", false, null, true, null)]   // an agent navigating meant to leave
    [InlineData("confirm", true, null, true, null)]
    [InlineData("confirm", false, null, false, null)]
    [InlineData("prompt", true, null, true, "the default")]
    [InlineData("prompt", true, "chosen", true, "chosen")]
    [InlineData("prompt", false, "chosen", false, null)]
    public void EachDialogIsAnsweredByItsKindAndThePolicy(string kind, bool accept, string? promptAnswer, bool expectAccept, string? expectText)
    {
        var (yes, text) = Tab.AnswerFor(kind, "the default", accept, promptAnswer);
        Assert.Equal(expectAccept, yes);
        Assert.Equal(expectText, text);
    }

    [Fact]
    public void DialogsSinceAMarkAreTheOnesAfterIt()
    {
        var tab = new Tab(null!);
        tab.RecordDialog("alert", "first", true, null);
        int mark = tab.DialogCount;
        Assert.Empty(tab.DialogsSince(mark));
        tab.RecordDialog("confirm", "second", false, null);
        tab.RecordDialog("prompt", "third", true, "yes");
        var since = tab.DialogsSince(mark);
        Assert.Equal(["second", "third"], since.Select(d => d.Message));
        Assert.Equal(3, tab.DialogCount);
    }

    [Fact]
    public void OnlyTheLastFewDialogsAreKeptAndEachMessageIsCut()
    {
        var tab = new Tab(null!);
        for (int i = 0; i < Tab.MaxDialogs + 5; i++)
            tab.RecordDialog("alert", $"m{i}", true, null);
        tab.RecordDialog("alert", new string('x', 2000), true, null);
        Assert.Equal(Tab.MaxDialogs, tab.RecentDialogs.Count);
        Assert.Equal(500, tab.RecentDialogs[^1].Message.Length);
        // A mark older than what is kept gives what is kept, not an exception.
        Assert.Equal(Tab.MaxDialogs, tab.DialogsSince(0).Count);
    }

    [Fact]
    public void TheDialogPolicyStartsAtOkAndCanBeChanged()
    {
        var tab = new Tab(null!);
        Assert.Equal((true, (string?)null), tab.DialogPolicy);
        tab.DialogPolicy = (false, "typed");
        Assert.Equal((false, (string?)"typed"), tab.DialogPolicy);
    }

    [Fact]
    public void DialogsAreAnsweredOnlyInAnAgentsWindowAndFollowTheTabWhenItMoves()
    {
        string tab = Source("Tabs/Tab.cs");
        // Off only for an agent's window, before the first navigation reads it.
        Assert.Matches(@"if \(_owner\.IsAgentWindow\(\)\)\s+core\.Settings\.AreDefaultScriptDialogsEnabled = false;", tab);
        // A tab moved into the person's window has the engine's own dialogs back.
        Assert.Matches(@"void TransferTo\(TabManager newOwner\)\s*\{[^}]*RefreshDialogMode\(\);", tab);
        // And until the engine reads that, the person is asked rather than the tab answering for them.
        Assert.Matches(@"if \(!_owner\.IsAgentWindow\(\)\)\s*\{\s*AskPersonInstead\(e\);", tab);

        string form = Source("UI/MainForm.cs");
        Assert.Matches(@"_tabStrip\.AgentWindow = value;[\s\S]{0,300}tab\.RefreshDialogMode\(\);", form);
    }

    // ---------------------------------------------------------------- locating an element

    [Fact]
    public void AMeasuredElementIsReadBack()
    {
        var spot = AgentServer.ParseLocated("""{"found":true,"x":10.5,"y":20,"w":30,"h":40,"tag":"button","type":"submit","fileInput":false,"inView":true,"covered":false,"coveredBy":null}""");
        Assert.NotNull(spot);
        Assert.Equal(10.5, spot.X);
        Assert.Equal(20, spot.Y);
        Assert.Equal("button", spot.Tag);
        Assert.Equal("submit", spot.Type);
        Assert.True(spot.CanClickAt);
    }

    [Theory]
    [InlineData("""{"found":false}""")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void NothingFoundOrNoAnswerIsNull(string raw)
        => Assert.Null(AgentServer.ParseLocated(raw));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]   // covered: a real click would reach the cover, a cookie banner's "Accept" say
    [InlineData(false, false, false)] // off screen
    public void ARealClickIsSentOnlyWhereItWouldReachTheElement(bool inView, bool covered, bool expected)
    {
        var spot = new AgentServer.Located(1, 1, 1, 1, "a", null, false, inView, covered, covered ? "div#cover" : null);
        Assert.Equal(expected, spot.CanClickAt);
    }

    [Fact]
    public void ASelectorGoesIntoThePageAsAStringAndNeverAsScript()
    {
        string hostile = "a'); alert(1); ('\"</script>";
        string literal = JsonSerializer.Serialize(hostile);
        foreach (string js in new[]
        {
            AgentServer.LocateJs(hostile, animate: true),
            AgentServer.LocateJs(hostile, animate: false),
            AgentServer.SyntheticClickJs(hostile),
            AgentServer.FocusJs(hostile, selectAll: true),
            AgentServer.SetValueJs(hostile, hostile, replace: true),
            AgentServer.ValueJs(hostile),
            AgentServer.SelectJs(hostile, hostile, hostile),
            AgentServer.WaitJs(hostile, hostile),
            AgentServer.FileCountJs(hostile),
        })
        {
            Assert.Contains(literal, js);
            Assert.DoesNotContain("alert(1); ('\"", js.Replace(literal, ""));
        }
    }

    [Fact]
    public void OnlyAnAnimatedLocateMovesTheCursor()
    {
        Assert.Contains("__gergurCursorTo(x, y", AgentServer.LocateJs("#a", animate: true));
        Assert.DoesNotContain("__gergurCursorTo(x, y", AgentServer.LocateJs("#a", animate: false));
    }

    // ---------------------------------------------------------------- typing

    [Theory]
    [InlineData("hello", "hello", true, true)]
    [InlineData("hello\n", "hello", true, true)]      // an editable element's trailing line
    [InlineData("hello world", "hello", true, false)]
    [InlineData("say hello", "hello", false, true)]    // added to the end
    [InlineData("hello say", "hello", false, false)]
    [InlineData("", "", true, true)]                   // emptied
    public void TypingIsCheckedAgainstWhatTheFieldNowSays(string now, string typed, bool replaced, bool expected)
        => Assert.Equal(expected, AgentServer.Matches(now, typed, replaced));

    [Fact]
    public void AFieldThatCannotBeReadIsNeitherAMatchNorAMismatch()
        => Assert.Null(AgentServer.Matches(null, "x", true));

    [Fact]
    public void AnUnknownKeyIsRefusedWithExamplesAndTheKeyCut()
    {
        string said = AgentServer.UnknownKey(new string('q', 200));
        Assert.Contains("Enter", said);
        Assert.Contains("Ctrl+A", said);
        Assert.True(said.Length < 300, said);
    }

    [Fact]
    public void AfterAClickThatMayHaveLandedNothingIsTriedAgain()
    {
        // A second click on "Pay" is worse than none: the fallback is only for a click that never went out.
        string server = Source("App/AgentServer.cs");
        Assert.Matches(@"catch \(TabInput\.InputFailedException failed\) when \(failed\.MayHaveLanded\)\s*\{[^}]*trusted = true;", server);
        string input = Source("Tabs/TabInput.cs");
        string code = Regex.Replace(input, @"//[^\n]*", "");
        Assert.Matches(@"catch \(InputFailedException failed\) when \(i > 0\)\s*\{\s*ReleaseQuietly\(core, events\[\^1\]\);\s*throw new InputFailedException\(failed\.Message, mayHaveLanded: true", code);
        // A typing call that timed out may have typed: passed on as possibly landed, and /type does not set it again.
        Assert.Matches(@"catch \(InputFailedException failed\) when \(failed\.TimedOut\)\s*\{\s*throw new InputFailedException\(failed\.Message, mayHaveLanded: true", code);
    }

    // ---------------------------------------------------------------- uploads and scrolling

    [Fact]
    public void FilesAreReadAsAnArrayOrOneString()
    {
        using var many = JsonDocument.Parse("""{"files":["C:/a.png","C:/b.png"]}""");
        Assert.Equal(["C:/a.png", "C:/b.png"], AgentServer.FilesIn(many));
        using var one = JsonDocument.Parse("""{"files":"C:/a.png"}""");
        Assert.Equal(["C:/a.png"], AgentServer.FilesIn(one));
        using var none = JsonDocument.Parse("""{"selector":"#f"}""");
        Assert.Empty(AgentServer.FilesIn(none));
        Assert.Empty(AgentServer.FilesIn(null));
        // A number in the list becomes an empty path, which CheckFiles refuses by name.
        using var odd = JsonDocument.Parse("""{"files":["C:/a.png",5]}""");
        Assert.Equal(["C:/a.png", ""], AgentServer.FilesIn(odd));
        Assert.NotNull(TrustedInput.CheckFiles(AgentServer.FilesIn(odd)));
    }

    [Theory]
    [InlineData("[640,360]", 640.0, 360.0)]
    [InlineData("[640.5, 0]", 640.5, 0.0)]
    public void ThePagesMiddleIsRead(string raw, double x, double y)
        => Assert.Equal((x, y), AgentServer.ParsePoint(raw));

    [Theory]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("[\"a\",2]")]
    [InlineData("{}")]
    public void AnythingElseIsNotAPoint(string raw)
        => Assert.Null(AgentServer.ParsePoint(raw));

    // ---------------------------------------------------------------- the tool surface

    [Fact]
    public void EveryNewToolIsAdvertisedAndRouted()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(AgentServer.McpTools()));
        var advertised = doc.RootElement.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        foreach (string tool in NewTools)
        {
            Assert.Contains(tool, advertised);
            Assert.NotEqual("", AgentServer.RouteForTool(tool).Path);
            Assert.True(AgentServer.NeedsTask(tool), $"{tool} should wait for the agent to say what it is doing");
        }
    }

    [Fact]
    public void EveryToolsEndpointIsServed()
    {
        // A route with no case answers "unknown endpoint" from a tool the agent was offered.
        string server = Source("App/AgentServer.cs");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(AgentServer.McpTools()));
        foreach (var tool in doc.RootElement.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            var (method, path) = AgentServer.RouteForTool(name);
            Assert.True(server.Contains($"case (\"{method}\", \"{path}\"):"), $"{name} routes to {method} {path}, which is not served");
        }
    }

    [Fact]
    public void EveryToolSaysWhatItDidInThePersonsLog()
    {
        // The fallback is "POST /key", which is the server talking to itself.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(AgentServer.McpTools()));
        foreach (var tool in doc.RootElement.EnumerateArray())
        {
            var (method, path) = AgentServer.RouteForTool(tool.GetProperty("name").GetString()!);
            string said = AgentActivity.Describe(method, path, "t1", null);
            Assert.NotEqual($"{method} {path}", said);
            Assert.DoesNotContain("/", said);
        }
    }

    [Theory]
    [InlineData("gergur_select_option", """{"selector":"#s"}""", "value or label")]
    [InlineData("gergur_select_option", """{"value":"x"}""", "selector")]
    [InlineData("gergur_wait_for", """{"gone":true}""", "selector or text")]
    [InlineData("gergur_press_key", """{"key":""}""", "key")]
    [InlineData("gergur_upload", """{"selector":"#f"}""", "files")]
    [InlineData("gergur_hover", """{}""", "selector")]
    public void ANewToolMissingWhatItNeedsIsRefusedBeforeItRuns(string tool, string args, string missing)
    {
        using var doc = JsonDocument.Parse(args);
        Assert.Equal(missing, AgentServer.MissingRequiredArg(tool, doc.RootElement));
    }

    [Theory]
    [InlineData("gergur_select_option", """{"selector":"#s","value":""}""")]  // an empty value is a real option
    [InlineData("gergur_wait_for", """{"text":"Saved"}""")]
    [InlineData("gergur_scroll", """{}""")]
    [InlineData("gergur_dialogs", """{}""")]
    [InlineData("gergur_downloads", """{}""")]
    public void ANewToolWithWhatItNeedsGoesAhead(string tool, string args)
    {
        using var doc = JsonDocument.Parse(args);
        Assert.Null(AgentServer.MissingRequiredArg(tool, doc.RootElement));
    }

    [Fact]
    public void ThePersonsLogNeverCarriesWhatWasTypedPressedOrUploaded()
    {
        // Describe is handed the method, path, tab and url only; this pins that /type, /key
        // and /upload say what kind of thing happened and nothing of its content.
        Assert.Equal("Typed in t1", AgentActivity.Describe("POST", "/type", "t1", null));
        Assert.Equal("Pressed a key in t1", AgentActivity.Describe("POST", "/key", "t1", null));
        Assert.Equal("Uploaded files in t1", AgentActivity.Describe("POST", "/upload", "t1", null));
        Assert.Equal("Checked its downloads", AgentActivity.Describe("GET", "/downloads", null, null));
    }

    [Theory]
    [InlineData(true, "mcp:a", null, "mcp:a")]        // started in its own window
    [InlineData(true, "mcp:a", "mcp:b", "mcp:a")]     // the window decides over a passer-by
    [InlineData(false, null, "mcp:b", "mcp:b")]       // its tab in the person's window, while it acted
    [InlineData(false, null, null, null)]             // the person's own, in a tab an agent once touched
    [InlineData(true, null, null, null)]
    public void ADownloadIsAnAgentsOnlyByItsWindowOrWhileItWasActing(bool inAgentWindow, string? owner, string? driver, string? expected)
        => Assert.Equal(expected, AgentServer.DownloadOwner(inAgentWindow, owner, driver));

    [Fact]
    public void WhoDroveATabIsForgottenAfterTheWindow()
    {
        var tab = new Tab(null!);
        Assert.Null(tab.DrivenWithin(AgentServer.DownloadDriverWindow, DateTime.UtcNow));
        tab.NoteDrivenBy("mcp:a");
        Assert.Equal("mcp:a", tab.DrivenWithin(AgentServer.DownloadDriverWindow, DateTime.UtcNow));
        Assert.Null(tab.DrivenWithin(AgentServer.DownloadDriverWindow, DateTime.UtcNow + AgentServer.DownloadDriverWindow + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AnAgentIsOnlyShownDownloadsPutDownToIt()
    {
        string server = Source("App/AgentServer.cs");
        var body = Regex.Match(server, @"private \(int Running, object\[\] Listed\) DownloadsOf\(string agentKey\)[\s\S]*?\n    \}").Value;
        Assert.NotEmpty(body);
        Assert.Contains("Where(d => d.AgentKey == agentKey)", body);
        Assert.DoesNotContain("TabId", body);
        string form = Source("UI/MainForm.cs");
        Assert.Contains("item.AgentKey = AgentServer.DownloadOwner(_openedByAgent, AgentOwnerKey, tab.DrivenWithin(", form);
    }

    [Fact]
    public void EachCallGetsItsBudgetOrWhatIsLeftOfTheActions()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(TabInput.CallBudget, TabInput.BudgetFor(null, now));
        Assert.Equal(TabInput.CallBudget, TabInput.BudgetFor(now + TimeSpan.FromSeconds(30), now));
        Assert.Equal(TimeSpan.FromSeconds(2), TabInput.BudgetFor(now + TimeSpan.FromSeconds(2), now));
        Assert.Equal(TimeSpan.Zero, TabInput.BudgetFor(now - TimeSpan.FromSeconds(1), now));
        // A whole action fits inside an MCP tool call with room to find the element first.
        Assert.True(TabInput.ActionBudget < TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void TheOwnPageCheckIsRepeatedRightBeforeWhatActs()
    {
        // The chooser watch awaits two protocol calls; the tab may reach gergur://settings meanwhile.
        string input = Regex.Replace(Source("Tabs/TabInput.cs"), @"//[^\n]*", "");
        Assert.Matches(@"await ChooserWatch\.StartAsync\(core\);\s*tab\.RefuseOwnPage\(\);\s*await SendClickAsync", input);
        Assert.Matches(@"tab\.RefuseOwnPage\(\);\s*await CallAsync\(core, ""DOM\.setFileInputFiles"", new \{ files, nodeId = node \}\)", input);
        Assert.Matches(@"tab\.RefuseOwnPage\(\);\s*await CallAsync\(core, ""DOM\.setFileInputFiles"", new \{ files, backendNodeId \}\)", input);
        // And clicks and uploads in one tab take turns, so one ending cannot switch interception off under another.
        Assert.Equal(3, Regex.Matches(input, @"using var turn = await TakeTurnAsync\(tab\);").Count);
    }

    [Fact]
    public void AnInvalidSelectorInAWaitIsSaidSoAndNeverReadAsGone()
    {
        Assert.Contains("return 'invalid selector'", AgentServer.WaitJs("#a", null));
        Assert.Contains("that is not a valid CSS selector", Source("App/AgentServer.cs"));
    }

    [Fact]
    public void FilesOnAnotherComputerAreRefusedEitherWaySpelled()
    {
        Assert.NotNull(TrustedInput.CheckFiles(["//server/share/a.txt"]));
        Assert.NotNull(TrustedInput.CheckFiles([new string((char)92, 2) + "server" + (char)92 + "a.txt"]));
    }

    [Fact]
    public void OnlyActingOnATabPutsItsDownloadsDownToTheAgent()
    {
        string server = Regex.Replace(Source("App/AgentServer.cs"), @"//[^\n]*", "");
        // Not stamped from the request id before routing, which counted reads and missed index.
        Assert.DoesNotMatch(@"NoteDriver\(StringValue\(query, body, ""id""\)\);\s*var result = await RouteRequestAsync", server);
        // Acting itself does the window's part and never calls itself.
        Assert.Matches(@"private static void Acting\(Tab tab\)\s*\{\s*tab\.NoteAgentAction\(\);", server);
        foreach (string acting in new[] { "/click", "/type", "/key", "/hover", "/scroll", "/select", "/upload", "/eval" })
            Assert.Matches(@"case \(""POST"", """ + Regex.Escape(acting) + @"""\):[\s\S]{0,900}?Acting\(tab\);", server);
        foreach (string read in new[] { "/page", "/html", "/screenshot", "/console" })
        {
            var block = Regex.Match(server, @"case \(""GET"", """ + Regex.Escape(read) + @"""\):[\s\S]*?\n            case ").Value;
            Assert.NotEmpty(block);
            Assert.DoesNotContain("Acting(", block);
            Assert.DoesNotContain("NoteDrivenBy", block);
        }
    }
}
