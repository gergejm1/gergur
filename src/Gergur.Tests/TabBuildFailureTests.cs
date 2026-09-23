using Gergur.Tabs;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The pieces added in the fifteenth review round, each of which a mutation removed with
/// every test still green. Every one of these is reachable without a browser.
/// </summary>
public sealed class TabBuildFailureTests
{
    private static void Set(Tab tab, string field, object value)
        => typeof(Tab)
            .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(tab, value);

    private static object? Get(Tab tab, string field)
        => typeof(Tab)
            .GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(tab);

    [Fact]
    public void ATabWithNoViewSaysSoWithoutAskingTheEngine()
    {
        // HasView is what the agent API asks from its request threads. It must answer from
        // the tab's own field: the engine's CoreWebView2 throws off the UI thread, which is
        // how /open, /window and /navigate once created their tab and then answered 500.
        Assert.False(new Tab(null!).HasView);
    }

    [Fact]
    public void TheAgentServerNeverAsksTheEngineFromARequestThread()
    {
        // The only kind of test that can see that regression. A tab in a test never has a
        // view, so nothing that reads CoreWebView2 ever reaches WebView2 here and the
        // wrong thread goes unnoticed; the live run is what caught it. So the file itself
        // is checked for the members that go to the engine.
        string source = File.ReadAllText(FindSource(Path.Combine("src", "Gergur", "App", "AgentServer.cs")));
        string[] engineBacked = [".Core", ".IsPlayingAudio", ".CanGoBack", ".CanGoForward", ".IsMuted", ".ZoomFactor"];

        foreach (string member in engineBacked)
        {
            int at = 0;
            while ((at = source.IndexOf(member, at, StringComparison.Ordinal)) >= 0)
            {
                // A whole word, so ".CoreSomething" does not count.
                int end = at + member.Length;
                bool wholeWord = end >= source.Length || !char.IsLetterOrDigit(source[end]);
                Assert.False(wholeWord,
                    $"AgentServer.cs reads {member} (offset {at}); use Tab.HasView, or a Tab method called through OnUiAsync");
                at = end;
            }
        }
    }

    private static string FindSource(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("could not find the repository from " + AppContext.BaseDirectory, relative);
    }

    [Fact]
    public async Task AFailedBuildDoesNotClearUpAfterANewerOne()
    {
        // A failed build tidies away its own half-built view. Without the generation check
        // it could tidy away a newer build's instead: forget that build was in flight, and
        // let a third one start and leak.
        var tab = new Tab(null!);
        var newer = new TaskCompletionSource();
        Set(tab, "_buildGeneration", 2);
        Set(tab, "_ensureLiveTask", newer.Task);
        int told = 0;
        tab.ViewBuildFailed += (_, _) => told++;

        var older = (Task)typeof(Tab)
            .GetMethod("BuildAndCountAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(tab, [false, 1])!;
        await older;

        Assert.Same(newer.Task, Get(tab, "_ensureLiveTask"));
        Assert.Equal(0, (int)Get(tab, "_failedBuilds")!);
        Assert.Equal(0, told);
    }

    [Fact]
    public async Task ATabThatMovesWindowStopsTellingTheOldOne()
    {
        // A torn-off tab's handlers are dropped so the window it came from stops hearing
        // about it; the new window wires its own. The old window saying "could not start"
        // about a tab that now lives somewhere else would be wrong in the wrong place.
        var tab = new Tab(null!);
        int oldWindowTold = 0;
        tab.ViewBuildFailed += (_, _) => oldWindowTold++;

        tab.DetachOwnerHandlers();
        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200));   // fails: no owner

        Assert.Equal(0, oldWindowTold);
    }

    [Fact]
    public async Task WaitingForANavigationDoesNotWaitForeverOnTheBuild()
    {
        // The engine's own build has no timeout, so this used to hold a plain http request
        // for as long as that took, and the caller's timeout only started counting after.
        var tab = new Tab(null!);
        Set(tab, "_ensureLiveTask", new TaskCompletionSource().Task);   // a build that never finishes

        var navigate = tab.NavigateAndWaitAsync("https://example.com/", TimeSpan.FromMilliseconds(200));
        var finished = await Task.WhenAny(navigate, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(navigate, finished);
        Assert.False(await navigate);
    }

    [Fact]
    public async Task ClickingATabDuringTheBackOffStillSaysWhy()
    {
        // The back-off after a failed build means a click attempts nothing, so it raised
        // nothing, and the blank area went unexplained on the second click: the very
        // silence the event was added to end.
        var tab = new Tab(null!);
        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200));   // first failure
        int told = 0;
        tab.ViewBuildFailed += (_, _) => told++;

        await tab.ActivateAsync();   // inside the five second back-off

        Assert.Equal(1, told);
        Assert.NotNull(tab.NextBuildAttemptUtc);
    }

    [Fact]
    public void NoFailureMeansNoWait()
        => Assert.Null(new Tab(null!).NextBuildAttemptUtc);

    [Fact]
    public void TheMessageSaysHowLongTheWaitReallyIs()
    {
        var now = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

        // It said "a few seconds" whatever the wait was, which after three failures in a
        // row is five minutes.
        Assert.Contains("5 minutes", Tab.CouldNotStartMessage(now.AddMinutes(5), now));
        Assert.Contains("4 seconds", Tab.CouldNotStartMessage(now.AddSeconds(4), now));
        Assert.Contains("Click it to try again", Tab.CouldNotStartMessage(null, now));
        Assert.Contains("Click it to try again", Tab.CouldNotStartMessage(now.AddSeconds(-1), now));
    }

    [Fact]
    public void TheMessageDoesNotPromiseARetryThatNeverComes()
    {
        // Nothing retries on a timer; a click, a read or the window coming back does. "It
        // will try again in 5 minutes" had the person waiting for nothing.
        var now = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        foreach (var next in new DateTime?[] { null, now.AddSeconds(4), now.AddMinutes(5) })
        {
            string message = Tab.CouldNotStartMessage(next, now);
            Assert.DoesNotContain("will try again", message);
            Assert.Contains("Click it", message);
        }
    }

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(4, "4 seconds")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(61, "2 minutes")]
    [InlineData(300, "5 minutes")]
    public void WaitsAreWordedLikeSomebodyWouldSayThem(int seconds, string expected)
        => Assert.Equal(expected, Tab.DescribeWait(seconds));

    [Fact]
    public async Task AFreshFailureIsToldOnceNotTwice()
    {
        // The build says so when it fails, and a click that found no view said so again.
        var tab = new Tab(null!);
        int told = 0;
        tab.ViewBuildFailed += (_, _) => told++;

        await tab.ActivateAsync();

        Assert.Equal(1, told);
    }

    [Fact]
    public async Task ATabClosedDuringARetrySaysNothing()
    {
        // A failure on record, a retry under way, and the tab closed before it finishes.
        // The retry records nothing for a closed tab, so without a check after the wait
        // this looked exactly like a click during the back-off, and the window said a tab
        // the person had just closed could not start.
        var tab = new Tab(null!);
        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200));
        var retry = new TaskCompletionSource();
        Set(tab, "_ensureLiveTask", retry.Task);
        int told = 0;
        tab.ViewBuildFailed += (_, _) => told++;

        var clicked = tab.ActivateAsync();
        tab.Dispose();
        retry.SetResult();
        await clicked;

        Assert.Equal(0, told);
    }

    [Fact]
    public async Task ATabOnTrialFailsQuietly()
    {
        var tab = new Tab(null!) { ReportsBuildFailure = false };
        int told = 0;
        tab.ViewBuildFailed += (_, _) => told++;

        await tab.ActivateAsync();
        await tab.ActivateAsync();   // and during the back-off

        Assert.Equal(0, told);
        Assert.NotNull(tab.LastBuildFailure);
    }

    [Fact]
    public async Task ARetryStillUnderWayIsNotAFailureToStart()
    {
        // After one failure a retry that is merely slow has no view and a failure on
        // record. Calling that "could not start" had the caller start yet another build.
        var tab = new Tab(null!);
        Assert.False(tab.CouldNotStart);   // nothing has been tried

        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200));
        Assert.True(tab.CouldNotStart);    // tried, failed, nothing under way

        Set(tab, "_ensureLiveTask", new TaskCompletionSource().Task);
        Assert.False(tab.CouldNotStart);   // a retry is building
    }

    [Fact]
    public async Task ANavigationAskedForDuringASlowBuildIsKeptForWhenItLands()
    {
        var tab = new Tab(null!);
        var build = new TaskCompletionSource();
        Set(tab, "_ensureLiveTask", build.Task);

        Assert.False(await tab.NavigateAndWaitAsync("https://first.example/", TimeSpan.FromMilliseconds(100)));
        Assert.Equal("https://first.example/", tab.DeferredNavigation);

        // The latest ask wins. Each used to keep its own, the first to run moved the url,
        // and the later one, the one the caller actually wanted last, then stood down.
        Assert.False(await tab.NavigateAndWaitAsync("https://second.example/", TimeSpan.FromMilliseconds(100)));
        Assert.Equal("https://second.example/", tab.DeferredNavigation);

        // And it is used up when the build finishes, view or not.
        build.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (tab.DeferredNavigation is not null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Null(tab.DeferredNavigation);
    }

    [Fact]
    public async Task SettingsChangedWhileATabSleepsWaitForItToWake()
    {
        // Calls into a suspended view can wake it, so a settings change is held for a
        // sleeping tab and handed over when something else wakes it.
        var tab = new Tab(null!);
        var state = typeof(Tab).GetProperty(nameof(Tab.State))!;
        state.SetValue(tab, TabState.Suspended);
        var settings = new Gergur.App.Settings();

        await tab.ApplySettingsLiveAsync(settings);
        Assert.Same(settings, Get(tab, "_settingsWhileAsleep"));

        state.SetValue(tab, TabState.Hidden);   // woken
        Assert.Null(Get(tab, "_settingsWhileAsleep"));
    }

    [Fact]
    public async Task SettingsHeldForADiscardedTabAreDropped()
    {
        // A rebuilt view reads the current settings anyway.
        var tab = new Tab(null!);
        var state = typeof(Tab).GetProperty(nameof(Tab.State))!;
        state.SetValue(tab, TabState.Suspended);
        await tab.ApplySettingsLiveAsync(new Gergur.App.Settings());

        state.SetValue(tab, TabState.Discarded);

        Assert.Null(Get(tab, "_settingsWhileAsleep"));
    }

    [Fact]
    public void TheWindowInUseIsWhatEveryCrossThreadCallerAsks()
    {
        // Wiring that no unit test can drive, because it needs real windows and a real
        // session: focus asked from any thread but the UI one always says no, so each of
        // these once meant window 0 whatever the person was using. Checked in the source,
        // like the engine reads above, with comments stripped so an explanation that
        // names the old call does not count as using it.
        string agent = Code(File.ReadAllText(FindSource(Path.Combine("src", "Gergur", "App", "AgentServer.cs"))));
        string form = Code(File.ReadAllText(FindSource(Path.Combine("src", "Gergur", "UI", "MainForm.cs"))));
        string session = Code(File.ReadAllText(FindSource(Path.Combine("src", "Gergur", "App", "AppSession.cs"))));

        Assert.DoesNotContain("ContainsFocus", agent);
        Assert.Contains("_session.WindowInUse()", Body(agent, "? ActiveEntry()"));
        Assert.Contains("session.WindowInUse()", Body(form, "static void OpenExternalUrl("));
        Assert.DoesNotContain("ContainsFocus", Body(form, "static void OpenExternalUrl("));
        Assert.Contains("NoteActive(this)", Body(form, "override void OnActivated("));
        Assert.Contains("PickWindow(windows, LastActiveWindow)", Body(session, "public MainForm? WindowInUse()"));
        // And the live settings push, which has no other check short of a browser.
        Assert.Contains("ApplySettingsLiveAsync(session.Settings)", Body(form, "static void ApplyLiveSettings("));
        // Settings held for a sleeping tab are handed over when it wakes, not only let go.
        string tab = Code(File.ReadAllText(FindSource(Path.Combine("src", "Gergur", "Tabs", "Tab.cs"))));
        Assert.Contains("ApplySettingsLiveAsync(held)", Body(tab, "public TabState State"));
    }

    [Fact]
    public void ARunWithoutTheTunnelNeverWritesTheVpnOff()
    {
        // Startup marks the tunnel down for this run and leaves the saved choice alone,
        // and what decides whether traffic goes through the tunnel reads both.
        string form = Code(File.ReadAllText(FindSource(Path.Combine("src", "Gergur", "UI", "MainForm.cs"))));
        string startup = Body(form, "private async Task StartSharedServicesAsync(");
        Assert.Contains("VpnDownThisRun = true", startup);
        Assert.DoesNotContain("VpnEnabled = false", startup);

        string drop = Code(File.ReadAllText(FindSource(Path.Combine("src", "Gergur", "App", "DropServer.cs"))));
        // The real pairing key path saves through the real Save, not only the test seam.
        Assert.Contains("EnsureKey(settings, settings.Save)", drop);
    }

    [Fact]
    public async Task SwitchingAwayFromASuspendedTabLeavesItSuspended()
    {
        // Hiding a suspended view does not resume it. Calling it Hidden handed it any
        // settings held while it slept, into an engine that could wake for them.
        var tab = new Tab(null!);
        var state = typeof(Tab).GetProperty(nameof(Tab.State))!;
        state.SetValue(tab, TabState.Suspended);
        var settings = new Gergur.App.Settings();
        await tab.ApplySettingsLiveAsync(settings);

        tab.Deactivate();

        Assert.Equal(TabState.Suspended, tab.State);
        Assert.Same(settings, Get(tab, "_settingsWhileAsleep"));
    }

    private static string Code(string source)
        => System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*", "");

    /// <summary>The braces of the member whose declaration contains <paramref name="signature"/>.</summary>
    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"could not find {signature}");
        int open = source.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"unbalanced braces after {signature}");
    }

    [Fact]
    public void AHeldNavigationGoesAheadOnlyIntoAViewNobodyHasMovedOn()
    {
        Assert.True(Tab.ShouldNavigateWhenBuilt(disposed: false, hasView: true, "https://a/", "https://a/"));
        Assert.False(Tab.ShouldNavigateWhenBuilt(disposed: true, hasView: true, "https://a/", "https://a/"));
        Assert.False(Tab.ShouldNavigateWhenBuilt(disposed: false, hasView: false, "https://a/", "https://a/"));
        // Somebody sent the tab elsewhere meanwhile, and theirs stands.
        Assert.False(Tab.ShouldNavigateWhenBuilt(disposed: false, hasView: true, "https://b/", "https://a/"));
    }
}

/// <summary>
/// Opening a tab for an agent and taking it away again when its page cannot start. A
/// TabManager with no engine behind it makes every build fail, which is the case here.
/// </summary>
public sealed class OpenOrDiscardTests
{
    private static async Task<(TabManager Manager, Tab Reading)> WindowWithOneTabAsync()
    {
        var manager = new TabManager(null!, new Control(), null!);
        var reading = manager.AddSnapshotTab(new TabSnapshot("https://reading.example/", "Reading"));
        await manager.ActivateAsync(reading);
        return (manager, reading);
    }

    [Fact]
    public async Task AFailedTabInFrontLeavesNothingBehind()
    {
        var (manager, reading) = await WindowWithOneTabAsync();
        var other = manager.AddSnapshotTab(new TabSnapshot("https://other.example/", "Other"));
        int told = 0;
        manager.TabCreated += (_, tab) => tab.ViewBuildFailed += (_, _) => told++;

        var opened = await manager.OpenOrDiscardAsync("https://agent.example/", activate: true);

        Assert.Null(opened);
        Assert.Equal([reading, other], manager.Tabs);
        // The tab the person was reading comes back, not whichever sat next to the one
        // taken away, which here would be "other".
        Assert.Same(reading, manager.ActiveTab);
        // Not on their Ctrl+Shift+T stack, and nothing in their status bar.
        Assert.Equal(0, manager.RecentlyClosedCount);
        Assert.Equal(0, told);
    }

    [Fact]
    public async Task AFailedTabBehindLeavesNothingBehind()
    {
        var (manager, reading) = await WindowWithOneTabAsync();

        Assert.Null(await manager.OpenOrDiscardAsync("https://agent.example/", activate: false));

        Assert.Equal([reading], manager.Tabs);
        Assert.Same(reading, manager.ActiveTab);
        Assert.Equal(0, manager.RecentlyClosedCount);
    }

    [Fact]
    public async Task AWindowWhoseOnlyTabFailsIsToldItIsEmpty()
    {
        // How the agent's own window closes itself when its first page cannot start.
        var manager = new TabManager(null!, new Control(), null!);
        bool emptied = false;
        manager.LastTabClosed += (_, _) => emptied = true;

        Assert.Null(await manager.OpenOrDiscardAsync("https://agent.example/", activate: true));

        Assert.True(emptied);
        Assert.Empty(manager.Tabs);
    }

    [Fact]
    public async Task TheTrialQuietIsLiftedWhateverHappens()
    {
        // A kept tab that stayed quiet would go blank without a word the next time its
        // view could not be rebuilt. The flag comes off in a finally, so it comes off on
        // every path; this is the path a test can reach.
        var (manager, _) = await WindowWithOneTabAsync();
        Tab? trial = null;
        manager.TabCreated += (_, tab) => trial = tab;

        await manager.OpenOrDiscardAsync("https://agent.example/", activate: true);

        Assert.NotNull(trial);
        Assert.True(trial!.ReportsBuildFailure);
    }

    [Fact]
    public async Task ClosingATabTheOrdinaryWayStillRemembersIt()
    {
        // The other half of the flag: Ctrl+Shift+T must keep working for the person's own tabs.
        var (manager, _) = await WindowWithOneTabAsync();
        var other = manager.AddSnapshotTab(new TabSnapshot("https://other.example/", "Other"));

        await manager.CloseTabAsync(other);

        Assert.Equal(1, manager.RecentlyClosedCount);
    }
}

/// <summary>What the agent API answers when a page cannot start, and which window a request with no tab means.</summary>
public sealed class AgentCouldNotStartTests
{
    private static System.Text.Json.JsonElement Answer((int Status, string Type, byte[] Body) response)
    {
        Assert.Equal(503, response.Status);
        return System.Text.Json.JsonDocument.Parse(response.Body).RootElement;
    }

    [Fact]
    public void NothingKeptMeansNoWaitToQuote()
    {
        // The back-off belongs to a tab, and a discarded tab's wait says nothing about the
        // next /open, which makes a new tab that tries straight away.
        var answer = Answer(Gergur.App.AgentServer.PageCouldNotStart(null, DateTime.UtcNow));

        Assert.Contains("nothing was opened", answer.GetProperty("error").GetString());
        Assert.False(answer.TryGetProperty("retryInSeconds", out _));
        Assert.False(answer.TryGetProperty("id", out _));
    }

    [Fact]
    public void AKeptTabQuotesItsRealWait()
    {
        var tab = new Tab(null!);
        var now = DateTime.UtcNow;
        typeof(Tab).GetField("_failedBuilds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(tab, Tab.FailedBuildsBeforeGivingUp);
        typeof(Tab).GetField("_lastFailedBuildUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(tab, now);

        var answer = Answer(Gergur.App.AgentServer.PageCouldNotStart(tab, now));

        Assert.Equal(300, answer.GetProperty("retryInSeconds").GetInt32());
        Assert.Contains("5 minutes", answer.GetProperty("error").GetString());
        Assert.Equal(tab.Id, answer.GetProperty("id").GetString());
    }

    [Fact]
    public void ATabThatCanBeTriedNowSaysSo()
    {
        var answer = Answer(Gergur.App.AgentServer.PageCouldNotStart(new Tab(null!), DateTime.UtcNow));

        Assert.Equal(0, answer.GetProperty("retryInSeconds").GetInt32());
        Assert.Contains("tried again now", answer.GetProperty("error").GetString());
    }

    private static void SetField(Tab tab, string field, object value)
        => typeof(Tab).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(tab, value);

    [Fact]
    public async Task ANavigateWhosePageCouldNotStartSaysSo()
    {
        var tab = new Tab(null!);
        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200));   // failed, nothing building

        foreach (bool waited in new[] { true, false })
        {
            var answer = Answer(Gergur.App.AgentServer.NavigateAnswer(waited, waited ? false : null, tab, DateTime.UtcNow));
            Assert.Contains("could not start", answer.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task ARetryStillBuildingIsNotLoadedYetRatherThanAFailure()
    {
        // The slow-versus-failed line. Reporting a retry in flight as "could not start"
        // had the caller start another build while this one was about to land.
        var tab = new Tab(null!);
        await tab.ReadScriptAsync("1", TimeSpan.FromMilliseconds(200));
        SetField(tab, "_ensureLiveTask", new TaskCompletionSource().Task);

        var (status, _, body) = Gergur.App.AgentServer.NavigateAnswer(waited: true, false, tab, DateTime.UtcNow);

        Assert.Equal(200, status);
        Assert.False(System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("loaded").GetBoolean());
    }

    [Fact]
    public void AUrlTheEngineRefusedIsNotASlowPage()
    {
        var answer = Answer(Gergur.App.AgentServer.NavigateAnswer(waited: true, null, new Tab(null!), DateTime.UtcNow));
        Assert.Contains("would not take that url", answer.GetProperty("error").GetString());
    }

    [Fact]
    public void WithoutAWaitThereIsNoLoadedToReport()
    {
        var (status, _, body) = Gergur.App.AgentServer.NavigateAnswer(waited: false, true, new Tab(null!), DateTime.UtcNow);

        Assert.Equal(200, status);
        var root = System.Text.Json.JsonDocument.Parse(body).RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(root.TryGetProperty("loaded", out _));
    }

    [Fact]
    public void ARequestNamingNoTabMeansTheWindowLastInUse()
    {
        string[] windows = ["first", "second", "third"];

        Assert.Equal("second", Gergur.App.AppSession.PickWindow(windows, "second"));
        // Closed since, or never set: the first, rather than nothing.
        Assert.Equal("first", Gergur.App.AppSession.PickWindow(windows, "gone"));
        Assert.Equal("first", Gergur.App.AppSession.PickWindow(windows, null));
        Assert.Null(Gergur.App.AppSession.PickWindow(Array.Empty<string>(), null));
    }
}

/// <summary>Where an agent's own window goes, so it cannot end up on top of the person's work.</summary>
public sealed class AgentWindowPlacementTests
{
    private static readonly IntPtr Theirs = new(0x1234);
    private static readonly IntPtr Ours = new(0x5678);

    [Fact]
    public void ItGoesDirectlyBehindWhatIsInUse()
        => Assert.Equal(Theirs, MainForm.BehindWhat(Theirs, inUseIsTopmost: false, Ours));

    [Fact]
    public void NotBehindAnAlwaysOnTopWindow()
    {
        // The taskbar, Start, a picture in picture video: linking in after a topmost window
        // can make the new one topmost too, and then the agent's window sits over the
        // person's work for good.
        Assert.Equal(MainForm.HWND_BOTTOM, MainForm.BehindWhat(Theirs, inUseIsTopmost: true, Ours));
    }

    [Fact]
    public void WithNothingInUseItGoesToTheBottom()
        => Assert.Equal(MainForm.HWND_BOTTOM, MainForm.BehindWhat(IntPtr.Zero, false, Ours));

    [Fact]
    public void NeverBehindItself()
        => Assert.Equal(MainForm.HWND_BOTTOM, MainForm.BehindWhat(Ours, false, Ours));
}
