using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Data;
using Gergur.Tabs;
using Microsoft.Web.WebView2.Core;
using Xunit;
using Page = Gergur.App.InternalPages.Page;

namespace Gergur.Tests;

/// <summary>
/// What the first review of the in-tab pages found: an Undo the host could not do, failures
/// answered as success, the agent API reaching the pages, and a session lost another way.
/// </summary>
public sealed class OwnPagesHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gergur-hardening-{Guid.NewGuid():N}");

    public OwnPagesHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var file in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : [])
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private InternalPages.Services Services(DownloadManager? downloads = null, string history = "history.jsonl")
        => new(
            new HistoryStore(Path.Combine(_dir, history)),
            new BookmarkStore(Path.Combine(_dir, "bookmarks.json")),
            downloads ?? new DownloadManager(),
            (_, _) => { });

    private static JsonElement Ask(Page page, object request, InternalPages.Services services)
        => JsonDocument.Parse(InternalPages.Answer(page, JsonSerializer.Serialize(request), services)).RootElement;

    internal static string FindSource(params string[] parts)
    {
        string relative = Path.Combine(parts);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("could not find the repository", relative);
    }

    [Fact]
    public void EveryOpThePagesAskForIsOneTheHostAnswers()
    {
        // How the bookmark Undo was missed: the demo backend answered bookmarks.add, the
        // host did not, and every page test was green against something that did not exist.
        var pages = new Dictionary<string, Page>
        {
            ["home.html"] = Page.Home,
            ["history.html"] = Page.History,
            ["downloads.html"] = Page.Downloads,
            ["bookmarks.html"] = Page.Bookmarks,
        };
        // Every op-shaped string literal, not only the ones written straight into a call: the
        // downloads page passes open, showInFolder and cancel through a variable. And "open",
        // which every page reaches through bridge.js.
        var ops = new List<(Page Page, string Op)>();
        foreach (var (file, page) in pages)
        {
            string source = File.ReadAllText(FindSource("src", "Gergur", "Assets", file));
            foreach (Match literal in Regex.Matches(source, @"[""'`]((?:history|bookmarks|downloads)\.(?!html\b|css\b|js\b)[A-Za-z]+)[""'`]"))
                ops.Add((page, literal.Groups[1].Value));
            ops.Add((page, "open"));
        }
        Assert.Contains("\"open\"", File.ReadAllText(FindSource("src", "Gergur", "Assets", "bridge.js")));
        foreach (var (page, op) in ops.Distinct())
        {
            string error = Ask(page, new { id = 1, op, args = new { } }, Services()).TryGetProperty("error", out var e) ? e.GetString()! : "";
            Assert.False(error.Contains("there is no") || error.Contains("cannot ask for"), $"{page} asks for {op}: {error}");
        }
        Assert.Contains((Page.Downloads, "downloads.showInFolder"), ops);   // the ones only a variable carries
        Assert.True(ops.Distinct().Count() >= 16, $"only {ops.Distinct().Count()} ops found in the pages; did the pattern change?");
    }

    [Fact]
    public void ADeletedBookmarkCanBePutBackWhereItWas()
    {
        var services = Services();
        services.Bookmarks.Toggle("https://a.example/", "A");
        services.Bookmarks.Toggle("https://b.example/", "B");
        services.Bookmarks.Toggle("https://c.example/", "C");

        Ask(Page.Bookmarks, new { id = 1, op = "bookmarks.remove", args = new { url = "https://b.example/" } }, services);
        var undo = Ask(Page.Bookmarks, new { id = 2, op = "bookmarks.add", args = new { url = "https://b.example/", title = "B", index = 1 } }, services);

        Assert.True(undo.GetProperty("ok").GetBoolean());
        Assert.Equal(["A", "B", "C"], services.Bookmarks.Items.Select(b => b.Title));
        // And not twice.
        Assert.False(Ask(Page.Bookmarks, new { id = 3, op = "bookmarks.add", args = new { url = "https://b.example/", title = "B", index = 0 } }, services).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ABookmarkChangeThatCannotBeWrittenIsUndoneAndSaidSo()
    {
        string path = Path.Combine(_dir, "bookmarks.json");
        var store = new BookmarkStore(path);
        store.Toggle("https://a.example/", "A");
        int changes = 0;
        store.Changed += (_, _) => changes++;
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var answer = Ask(Page.Bookmarks, new { id = 1, op = "bookmarks.remove", args = new { url = "https://a.example/" } },
            new InternalPages.Services(new HistoryStore(Path.Combine(_dir, "h.jsonl")), store, new DownloadManager(), (_, _) => { }));

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("could not save", answer.GetProperty("error").GetString());
        Assert.Single(store.Items);    // still there in memory, as it still is on disk
        Assert.Equal(0, changes);
    }

    [Fact]
    public void HistoryThatCouldNotBeClearedIsNotReportedCleared()
    {
        var services = Services();
        services.History.Append("https://a.example/", "A");
        string path = Path.Combine(_dir, "history.jsonl");

        JsonElement answer;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            answer = Ask(Page.History, new { id = 1, op = "history.clear", args = new { } }, services);

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Single(services.History.Read());
    }

    [Fact]
    public void OpeningADownloadWhoseFileHasGoneSaysSo()
    {
        var downloads = new DownloadManager();
        var item = downloads.Add(new DownloadItem("https://x.example/a.zip", Path.Combine(_dir, "gone.zip"), 1, 1, CoreWebView2DownloadState.Completed));

        var answer = Ask(Page.Downloads, new { id = 1, op = "downloads.open", args = new { id = item.Id } }, Services(downloads));

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("no longer", answer.GetProperty("error").GetString());
    }

    [Fact]
    public void AFileOnAnotherMachineIsNotOpened()
        => Assert.False(InternalPages.Openable("file://server/share/page.html"));

    [Fact]
    public void HistoryListLeavesOutTheBrowsersOwnPagesEvenWhenTheyAreInTheFile()
    {
        // Append already keeps them out, so the file is written directly: this is the second
        // guard, for a history written before the first existed.
        string path = Path.Combine(_dir, "own.jsonl");
        File.WriteAllLines(path,
        [
            JsonSerializer.Serialize(new { T = DateTime.UtcNow, Url = "https://site.example/", Title = "Site" }),
            JsonSerializer.Serialize(new { T = DateTime.UtcNow, Url = InternalPages.UrlOf(Page.History), Title = "History" }),
        ]);

        var list = Ask(Page.History, new { id = 1, op = "history.list", args = new { } }, Services(history: "own.jsonl")).GetProperty("result");

        Assert.Equal(["https://site.example/"], list.EnumerateArray().Select(v => v.GetProperty("url").GetString()));
    }

    [Fact]
    public void TheSessionNamesOwnPagesRatherThanTheirPathIntoThisInstall()
    {
        var manager = new TabManager(null!, new Control(), null!);
        manager.AddSnapshotTab(new TabSnapshot(InternalPages.UrlOf(Page.History), "History"));

        var window = Assert.Single(AppSession.SessionOf([(false, manager.Tabs, null)])!);

        Assert.Equal("gergur://history", Assert.Single(window.Tabs).Url);
        Assert.Equal(InternalPages.UrlOf(Page.History), InternalPages.Resolve("gergur://history"));
    }

    [Fact]
    public void ATabIsTheBrowsersOwnPageByEitherAddress()
    {
        // A navigation that never commits moves Url and leaves the page on screen: the agent
        // check has to look at the committed one too.
        var tab = new Tab(null!);
        Assert.Null(AgentServer.OwnPageRefusal(tab));

        typeof(Tab).GetField("_committedSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(tab, InternalPages.UrlOf(Page.Downloads));

        Assert.StartsWith("about:", tab.Url);
        // On one only by the page still on screen: leaving it, which is not ready yet.
        var leaving = AgentServer.OwnPageRefusal(tab);
        Assert.NotNull(leaving);
        Assert.True(leaving.Leaving);
        Assert.Equal(503, AgentServer.Refusal(leaving).Item1);
        Assert.Contains("still leaving", leaving.Message);

        // Heading to one, or on one and staying: refused outright.
        var staying = AgentServer.OwnPageRefusal(new Tab(null!, new TabSnapshot(InternalPages.UrlOf(Page.History), "History")));
        Assert.NotNull(staying);
        Assert.False(staying.Leaving);
        Assert.Equal(403, AgentServer.Refusal(staying).Item1);
        Assert.Equal(InternalPages.OwnPageRefusedException.Text, staying.Message);
    }

    [Fact]
    public void TheTabRefusesItsOwnPagesAndNothingElse()
    {
        // The check the script and capture paths make at the moment they run. With no view,
        // the committed page is the address, so no engine is needed to see it throw.
        var own = new Tab(null!, new TabSnapshot(InternalPages.UrlOf(Page.History), "History"));
        var refused = Assert.Throws<InternalPages.OwnPageRefusedException>(own.RefuseOwnPage);
        Assert.False(refused.Leaving);

        new Tab(null!, new TabSnapshot("https://example.com/", "Example")).RefuseOwnPage();
        new Tab(null!, new TabSnapshot(InternalPages.UrlOf(Page.History).Replace("history.html", "elsewhere.html"), "Elsewhere")).RefuseOwnPage();
    }

    [Fact]
    public void TheRefusalLooksAtEveryAddressTheTabHas()
    {
        string own = InternalPages.UrlOf(Page.History), site = "https://example.com/";

        // On one and staying, or on its way to one by either route: never.
        Assert.False(Tab.RefusalFor(own, null, own)!.Leaving);
        Assert.False(Tab.RefusalFor(site, own, site)!.Leaving);
        Assert.False(Tab.RefusalFor(own, null, site)!.Leaving);
        // Still showing one with both addresses elsewhere: the second navigation of a race,
        // or an ordinary leave. Refused, as not yet.
        var leaving = Tab.RefusalFor(site, site, own);
        Assert.NotNull(leaving);
        Assert.True(leaving.Leaving);
        Assert.Equal(503, AgentServer.Refusal(leaving).Item1);
        // Nothing of theirs anywhere.
        Assert.Null(Tab.RefusalFor(site, null, site));
        Assert.Null(Tab.RefusalFor(site, "https://example.com/next", site));
    }

    [Fact]
    public void ATabThatLosesItsViewNoLongerCountsAsLeavingAPageItWasOn()
    {
        // With no document behind it, a leftover committed address would answer "still
        // leaving" before the read that wakes the tab, so it never woke and never changed.
        var tab = new Tab(null!, new TabSnapshot("https://example.com/", "Example"));
        typeof(Tab).GetField("_committedSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(tab, InternalPages.UrlOf(Page.Home));
        Assert.NotNull(AgentServer.OwnPageRefusal(tab));

        typeof(Tab).GetMethod("DropView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(tab, null);

        Assert.Null(AgentServer.OwnPageRefusal(tab));
    }

    [Fact]
    public void TheAgentApiNamesOwnPagesRatherThanTheirPath()
    {
        Assert.Equal("gergur://history", InternalPages.NameOf(InternalPages.UrlOf(Page.History)));
        Assert.Equal("gergur://newtab", InternalPages.NameOf(InternalPages.UrlOf(Page.Home)));
        Assert.Equal("https://example.com/", InternalPages.NameOf("https://example.com/"));
        Assert.Equal(InternalPages.UrlOf(Page.Home), InternalPages.Resolve(InternalPages.NameOf(InternalPages.UrlOf(Page.Home))));
    }

    [Fact]
    public void ANavigationOnItsWayToAnOwnPageIsRefusedUntilItIsOver()
    {
        // A Back click to the history page never moves Url, and the engine can commit it
        // before this thread hears so. The navigation it announced is what shows it coming.
        var tab = new Tab(null!, new TabSnapshot("https://example.com/", "Example"));

        tab.NoteNavigationUnderWay(7, InternalPages.UrlOf(Page.History));
        Assert.False(Assert.Throws<InternalPages.OwnPageRefusedException>(tab.RefuseOwnPage).Leaving);

        tab.NoteNavigationOver(6);   // a different, older one finishing changes nothing
        Assert.Throws<InternalPages.OwnPageRefusedException>(tab.RefuseOwnPage);

        tab.NoteNavigationOver(7);
        tab.RefuseOwnPage();

        tab.NoteNavigationUnderWay(8, "https://example.com/next");
        tab.RefuseOwnPage();
    }

    [Fact]
    public void BookmarksThatCouldNotBeReadAreNotSavedOver()
    {
        // Loaded as an empty list, the next change used to write that list over the file.
        string path = Path.Combine(_dir, "bookmarks.json");
        const string theirs = """[{"Url":"https://kept.example/","Title":"Kept","AddedUtc":"2026-01-01T00:00:00Z"}]""";
        File.WriteAllText(path, theirs);

        BookmarkStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            store = new BookmarkStore(path);   // held by something else at startup

        Assert.Empty(store.Items);
        Assert.Throws<BookmarkStore.UnreadableException>(() => store.Toggle("https://new.example/", "New"));
        Assert.Empty(store.Items);
        var answer = Ask(Page.Bookmarks, new { id = 1, op = "bookmarks.add", args = new { url = "https://new.example/", title = "New", index = 0 } },
            new InternalPages.Services(new HistoryStore(Path.Combine(_dir, "h.jsonl")), store, new DownloadManager(), (_, _) => { }));
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("could not be read when Gergur started", answer.GetProperty("error").GetString());
        Assert.Equal(theirs, File.ReadAllText(path));
        // And the pages do not show it as empty, which would read "No bookmarks yet".
        var list = Ask(Page.Bookmarks, new { id = 2, op = "bookmarks.list", args = new { } },
            new InternalPages.Services(new HistoryStore(Path.Combine(_dir, "h.jsonl")), store, new DownloadManager(), (_, _) => { }));
        Assert.False(list.GetProperty("ok").GetBoolean());
        Assert.Contains(BookmarkStore.WhatToDo, list.GetProperty("error").GetString());

        // The same file, readable at the next start, loads and takes changes as it always did.
        var next = new BookmarkStore(path);
        Assert.False(next.Unreadable);
        Assert.Equal("Kept", Assert.Single(next.Items).Title);
        Assert.True(next.Toggle("https://new.example/", "New"));
        Assert.Equal(2, new BookmarkStore(path).Items.Count);

        // Nor one that is not a list at all, which is refused like the locked one.
        File.WriteAllText(path, "{ not json");
        Assert.Throws<BookmarkStore.UnreadableException>(() => new BookmarkStore(path).Toggle("https://new.example/", "New"));
        Assert.Equal("{ not json", File.ReadAllText(path));
        // Or one that parses into something that is not bookmarks, which the bar would throw on.
        foreach (string odd in new[] { "[null]", "[{}]" })
        {
            File.WriteAllText(path, odd);
            Assert.True(new BookmarkStore(path).Unreadable, odd);
        }

        // A file that is not there yet is not unreadable: the first bookmark creates it. Nor is
        // an empty one, which has nothing in it to lose.
        string fresh = Path.Combine(_dir, "fresh.json");
        Assert.True(new BookmarkStore(fresh).Toggle("https://new.example/", "New"));
        Assert.True(File.Exists(fresh));
        File.WriteAllText(path, "  \r\n");
        Assert.True(new BookmarkStore(path).Toggle("https://new.example/", "New"));
    }

    [Fact]
    public void AHistoryThatCannotBeReadIsNotReportedEmpty()
    {
        var services = Services();
        services.History.Append("https://a.example/", "A");

        JsonElement answer;
        using (new FileStream(Path.Combine(_dir, "history.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
            answer = Ask(Page.History, new { id = 1, op = "history.list", args = new { } }, services);

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("could not be read", answer.GetProperty("error").GetString());
    }

    [Fact]
    public void AnUnexpectedFailureIsStillAnswered()
    {
        var services = new InternalPages.Services(
            new HistoryStore(Path.Combine(_dir, "h.jsonl")), new BookmarkStore(Path.Combine(_dir, "b.json")), new DownloadManager(),
            (_, _) => throw new NotSupportedException("boom"));

        var answer = Ask(Page.History, new { id = 5, op = "open", args = new { url = "https://a.example/" } }, services);

        Assert.Equal(5, answer.GetProperty("id").GetInt32());
        Assert.False(answer.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task AClosedTabIsNotKeptReachableByItsPopups()
    {
        var manager = new TabManager(null!, new Control(), null!);
        var opener = manager.AddSnapshotTab(new TabSnapshot("https://files.example/", "Files"));
        manager.AddSnapshotTab(new TabSnapshot("https://other.example/", "Other"));
        var popup = await manager.CreatePopupTabAsync(opener);

        await manager.CloseTabAsync(opener);

        Assert.Null(popup.Opener);
    }

    [Fact]
    public async Task ASecondDownloadFromASetAsideTabKeepsItsViewUntilBothEnd()
    {
        var manager = new TabManager(null!, new Control(), null!);
        var opener = manager.AddSnapshotTab(new TabSnapshot("https://files.example/", "Files"));
        var popup = await manager.CreatePopupTabAsync(opener);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();

        await manager.SetAsideAsync(popup, first.Task);
        Assert.True(manager.IsSetAside(popup));
        await manager.SetAsideAsync(popup, second.Task);

        first.SetResult();
        await Task.Delay(100);
        Assert.True(manager.IsSetAside(popup));    // the second is still running

        second.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (manager.IsSetAside(popup) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.False(manager.IsSetAside(popup));
    }
}

/// <summary>
/// Wiring no unit test can drive, since it needs windows, a session or an engine: checked in
/// the source, comments stripped, like the rest of these source checks.
/// </summary>
public sealed class OwnPagesWiringTests
{
    private static string Code(params string[] parts)
        => Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource(parts)), @"//[^\n]*", "");

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"could not find {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced");
    }

    [Theory]
    [InlineData("\"GET\", \"/page\"")]
    [InlineData("\"GET\", \"/html\"")]
    [InlineData("\"GET\", \"/screenshot\"")]
    [InlineData("\"GET\", \"/console\"")]
    [InlineData("\"POST\", \"/eval\"")]
    [InlineData("\"POST\", \"/click\"")]
    [InlineData("\"POST\", \"/type\"")]
    public void TheAgentApiCannotReadOrScriptTheBrowsersOwnPages(string endpoint)
    {
        // Through the downloads page an agent could launch any downloaded file, and through
        // the history page clear history without the page's own confirm.
        string agent = Code("src", "Gergur", "App", "AgentServer.cs");
        Assert.Contains("OwnPageRefused(", Body(agent, $"case ({endpoint})"));
    }

    [Fact]
    public void AnAgentsWindowIsMarkedAndAPersonsUseClaimsIt()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        Assert.Contains("OpenedByAgent = true", Body(form, "internal static async Task<MainForm> OpenWindowAsync("));
        Assert.Contains("ClaimForPerson()", Body(form, "internal static void OpenExternalUrl("));
        Assert.Contains("ClaimForPerson()", Body(form, "internal async Task AdoptTabAsync("));
        Assert.Matches(@"TabClicked \+= async \(_, tab\) => \{ ClaimForPerson\(\);", form);
        Assert.Matches(@"NewTabClicked \+= \(_, _\) => \{ ClaimForPerson\(\);", form);
        Assert.Matches(@"NavigationRequested \+= async \(_, url\) => \{ ClaimForPerson\(\);", form);
        Assert.Contains("_form.ClaimForPerson()", Body(Code("src", "Gergur", "UI", "ShortcutRouter.cs"), "public bool Handle(Keys keyData)"));
    }

    [Fact]
    public void RepliesAndPushesReachOnlyTheRightDocumentAndNoSleepingTab()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        Assert.Contains("request.Page)", form);   // a reply names the page that asked
        string push = Body(form, "private void PushToOwnPages(");
        Assert.Contains("TabState.Suspended", push);
        Assert.Contains("tab.ShownPage", push);
        Assert.DoesNotContain("Identify(tab.Url)", push);

        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        Assert.Contains("ShownPage != expected", Body(tab, "public void PostToPage("));
    }

    [Fact]
    public void ADownloadOnlyTabIsSetAsideAndLaterOnesExtendIt()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        string wiring = Body(form, "tab.DownloadStarted += (_, operation) =>");
        Assert.Contains("SetAsideAsync(tab, finished.Task)", wiring);
        Assert.Contains("manager.IsSetAside(tab)", wiring);
    }

    /// <summary>
    /// The own-page check comes before this run, with nothing awaited between them: an await
    /// hands the thread back, and the tab can arrive at one of those pages meanwhile. The
    /// await on the run's own statement is fine, since the run has started by then.
    /// </summary>
    private static void CheckedRightBefore(string body, string run, string where)
    {
        int at = body.IndexOf(run, StringComparison.Ordinal);
        Assert.True(at > 0, $"{run} not found in {where}");
        int check = body.LastIndexOf("RefuseOwnPage();", at, StringComparison.Ordinal);
        Assert.True(check >= 0, $"no own-page check before {run} in {where}");
        int statement = body.LastIndexOfAny([';', '{', '}'], at);
        string between = body[(check + "RefuseOwnPage();".Length)..(statement + 1)];
        Assert.False(Regex.IsMatch(between, @"\bawait\b"), $"something is awaited between the own-page check and {run} in {where}");
    }

    [Fact]
    public void TheTabRefusesItsOwnPagesWhereScriptsAndCapturesRun()
    {
        // Checked at the moment of running, after the wait for the page, which is long
        // enough for the tab to arrive at one of the browser's own pages. Present but after
        // the run passed an earlier version of this check, so it is the order that is held.
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        string read = Body(tab, "public async Task<(string Result, bool Ready)> ReadScriptAsync(");
        string capture = Body(tab, "public async Task<(byte[] Png, bool Ready)> CaptureScreenshotAsync(");
        CheckedRightBefore(read, "core!.ExecuteScriptAsync(js)", "ReadScriptAsync");
        CheckedRightBefore(capture, "CaptureAsync(core!)", "CaptureScreenshotAsync");
        // Neither catches anything, so the refusal cannot be swallowed on its way out.
        Assert.DoesNotContain("catch", read);
        Assert.DoesNotContain("catch", capture);
        // The check is the tested decision, given every address the tab has.
        Assert.Contains("RefusalFor(Url, _navigationUnderWay?.Uri, CommittedUrl)", Body(tab, "internal void RefuseOwnPage("));
        // In the eval path, before each place the agent's source runs.
        string eval = Body(tab, "private async Task<string> EvaluateAsync(");
        foreach (string run in new[] { "ExpressionProbe(js)", "ExecuteScriptWithResultAsync(js)", "AwaitingWrapper(js, token)" })
            CheckedRightBefore(eval, run, "EvaluateAsync");
        // And outside any catch that would turn the refusal into "could not run that".
        Assert.DoesNotMatch(@"try\s*\{[^}]*RefuseOwnPage\(\);", eval);

        string agent = Code("src", "Gergur", "App", "AgentServer.cs");
        Assert.Contains("catch (InternalPages.OwnPageRefusedException", Body(agent, "private async Task<(int, string, byte[])> RouteAsync("));
        Assert.Contains("Refusal(refused)", Body(agent, "private async Task<(int, string, byte[])> RouteAsync("));
        Assert.Contains("OwnPageRefusal(t)", agent);
        Assert.Contains("Tab.RefusalFor(tab.Url, null, tab.CommittedSourceForOtherThreads)", agent);
        // The whole-window capture checks again in the step that takes the picture: that the
        // window still shows this tab, and that the tab is not on one of the pages.
        string chrome = Body(agent, "if (wantsChrome)");
        int shot = chrome.IndexOf("WindowCapture.Of", StringComparison.Ordinal);
        int step = chrome.IndexOf("OnUiAsync(", StringComparison.Ordinal);
        foreach (string recheck in new[] { "ActiveTab != shot.Tab", "shot.Tab.RefuseOwnPage();" })
        {
            int at = chrome.IndexOf(recheck, StringComparison.Ordinal);
            Assert.True(at > step && at < shot, $"the chrome capture does not check {recheck} in the step before WindowCapture.Of");
        }
        // A window that switched tabs is told apart from a capture that failed, and says what to do.
        Assert.Matches(@"if \(switchedAway\)\s*return \(400,[^}]*activate=1", chrome);
        // /console the same, in the step that copies the list.
        string console = Body(agent, "case (\"GET\", \"/console\")");
        int copy = console.IndexOf("RecentErrors.ToArray()", StringComparison.Ordinal);
        int consoleCheck = console.IndexOf("consoleTab.RefuseOwnPage();", StringComparison.Ordinal);
        Assert.True(consoleCheck > console.IndexOf("OnUiAsync(", StringComparison.Ordinal) && consoleCheck < copy,
            "/console does not check the page again in the step that copies its errors");
        // /tabs, which carries the same list, leaves it out for those pages, and names them
        // rather than giving their path into the install.
        string tabs = Body(agent, "case (\"GET\", \"/tabs\")");
        Assert.Contains("ShownPage is null ? e.Tab.RecentErrors.ToArray()", tabs);
        Assert.Contains("url = InternalPages.NameOf(e.Tab.Url)", tabs);
    }

    [Fact]
    public void AnUnreadableBookmarksFileIsExplainedWhereverAChangeIsRefused()
    {
        // The status label clips the half that says what to do, so the first refusal of a
        // run is a box with the whole message.
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        Assert.Contains("SayBookmarkNotSaved(ex, ", Body(form, "public void ToggleBookmark("));
        Assert.Contains("SayBookmarkNotSaved(ex, ", Body(form, "_bookmarksBar.RemoveRequested += (_, url) =>"));
        string say = Body(form, "private void SayBookmarkNotSaved(");
        Assert.Contains("ex is not BookmarkStore.UnreadableException", say);
        Assert.Contains("MessageBox.Show(this, ex.Message", say);
        // Once a run: the flag is checked, then set, before the box.
        Assert.Matches(@"if \(_toldBookmarksUnreadable\)\s*return;\s*_toldBookmarksUnreadable = true;\s*MessageBox\.Show", say);
    }

    [Fact]
    public void WhatTheRefusalReadsIsTheCommittedPageAndItsNavigations()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        // The document the engine has committed, not the address a navigation is heading to.
        Assert.Contains("ShownPage => InternalPages.Identify(CommittedUrl)", tab);
        Assert.Contains("Core?.Source", Body(tab, "internal string CommittedUrl"));
        // Kept up to date for the agent server's own threads as pages commit.
        Assert.Contains("_committedSource = Url", Body(tab, "private void OnSourceChanged("));
        // A navigation under way is recorded as the engine announces it, redirects too, so
        // not inside the branch that skips them, and forgotten when it is over.
        string starting = Body(tab, "private void OnNavigationStarting(");
        Assert.Contains("NoteNavigationUnderWay(e.NavigationId, e.Uri);", starting);
        Assert.DoesNotContain("NoteNavigationUnderWay", Body(starting, "if (!e.IsRedirected)"));
        Assert.Contains("NoteNavigationOver(e.NavigationId);", Body(tab, "private void OnNavigationCompleted("));
        Assert.Contains("_navigationUnderWay = null;", Body(tab, "private void DropView("));
    }

    [Fact]
    public void TheAddressBarKeepsOnlyWhatIsBeingTypedForTheTabItShows()
    {
        // Typing in progress for this tab: kept.
        Assert.False(Gergur.UI.MainForm.ShouldRefreshAddress(force: false, focused: true, sameTab: true, edited: true));
        // A switch to another tab while the bar has focus, which a new tab leaves it with:
        // that tab's address, not the last one's, and not blank.
        Assert.True(Gergur.UI.MainForm.ShouldRefreshAddress(force: false, focused: true, sameTab: false, edited: true));
        Assert.True(Gergur.UI.MainForm.ShouldRefreshAddress(force: false, focused: true, sameTab: false, edited: false));
        // Focused but untouched, as a new tab's placeholder address is: updated as it loads.
        Assert.True(Gergur.UI.MainForm.ShouldRefreshAddress(force: false, focused: true, sameTab: true, edited: false));
        Assert.True(Gergur.UI.MainForm.ShouldRefreshAddress(force: false, focused: false, sameTab: true, edited: true));
        Assert.True(Gergur.UI.MainForm.ShouldRefreshAddress(force: true, focused: true, sameTab: true, edited: true));

        // And every address the bar is given goes through the one place that records it.
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        Assert.Single(Regex.Matches(form, @"_addressBar\.Text\s*=(?!=)"));
        string show = Body(form, "private void ShowAddress(");
        Assert.Contains("_addressBar.Text = _addressShownText;", show);
        // In this order: record which tab the text belongs to (what keeps typing: without it
        // every update from any tab counts as a switch and wipes it), leave an unchanged bar
        // alone (so a placed caret stays), and after a rewrite under focus select it all, so
        // typing replaces the url rather than landing in front of it. Recording after the
        // early return would bring back a new tab showing the old address.
        Assert.Matches(@"(?:_addressShownText = AddressFor\(tab\);\s*_addressShownFor = tab;|_addressShownFor = tab;\s*_addressShownText = AddressFor\(tab\);)\s*"
            + @"if \(_addressBar\.Text == _addressShownText\)\s*return;\s*"
            + @"_addressBar\.Text = _addressShownText;\s*if \(_addressBar\.Focused\)\s*_addressBar\.SelectAll\(\);", show);
        // Escape puts the tab's address back.
        Assert.Matches(@"Escaped \+= \(_, _\) =>\s*\{[^}]*ShowAddress\(Tabs\?\.ActiveTab\);", form);
        // And what a refresh shows is the active tab: showing the one it already showed would
        // leave sameTab false for good and the bar stuck on the first tab's address.
        Assert.Matches(@"ShouldRefreshAddress\(forceAddressBar, _addressBar\.Focused,\s*sameTab: active == _addressShownFor, edited: _addressBar\.Text != _addressShownText\)\)\s*ShowAddress\(active\);",
            Body(form, "private void UpdateChrome("));
    }

    [Fact]
    public void TheStatusBarShowsItsTooltips()
    {
        // Off by default on a StatusStrip, so the memory figures moved into tooltips showed nowhere.
        Assert.Contains("ShowItemToolTips = true", Code("src", "Gergur", "UI", "MainForm.cs"));
    }
}
