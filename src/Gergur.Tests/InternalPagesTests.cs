using System.Text.Json;
using Gergur.App;
using Gergur.Data;
using Gergur.Tabs;
using Microsoft.Web.WebView2.Core;
using Xunit;
using Page = Gergur.App.InternalPages.Page;

namespace Gergur.Tests;

/// <summary>
/// The browser's own pages and what they may ask for. Every page in a WebView2 can post a
/// web message, including any site, so which page asked decides everything here.
/// </summary>
public sealed class InternalPagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gergur-pages-{Guid.NewGuid():N}");
    private readonly List<(string Url, bool NewTab)> _opened = new();

    public InternalPagesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private InternalPages.Services Services(DownloadManager? downloads = null)
        => new(
            new HistoryStore(Path.Combine(_dir, "history.jsonl")),
            new BookmarkStore(Path.Combine(_dir, "bookmarks.json")),
            downloads ?? new DownloadManager(),
            (url, newTab) => _opened.Add((url, newTab)));

    private static JsonElement Ask(Page page, object request, InternalPages.Services services)
        => JsonDocument.Parse(InternalPages.Answer(page, JsonSerializer.Serialize(request), services)).RootElement;

    [Fact]
    public void OnlyThisInstallsFilesAreTheBrowsersPages()
    {
        Assert.Equal(Page.History, InternalPages.Identify(InternalPages.UrlOf(Page.History)));
        // The query and fragment do not change which page it is.
        Assert.Equal(Page.History, InternalPages.Identify(InternalPages.UrlOf(Page.History) + "?demo=1#top"));
        Assert.Equal(Page.Home, InternalPages.Identify(InternalPages.UrlOf(Page.Home)));

        // A file of the same name elsewhere on disk is just a file, and a site is a site.
        Assert.Null(InternalPages.Identify(new Uri(Path.Combine(_dir, "history.html")).AbsoluteUri));
        Assert.Null(InternalPages.Identify("https://example.com/history.html"));
        Assert.Null(InternalPages.Identify("about:blank"));
        Assert.Null(InternalPages.Identify(null));
    }

    [Fact]
    public void TheAddressBarNamesAreTwoWay()
    {
        foreach (var page in new[] { Page.History, Page.Downloads, Page.Bookmarks })
        {
            string address = InternalPages.AddressOf(page);
            Assert.StartsWith("gergur://", address);
            Assert.Equal(InternalPages.UrlOf(page), InternalPages.Resolve(address));
            Assert.Equal(InternalPages.UrlOf(page), UrlHeuristics.ToNavigableUrl(address.ToUpperInvariant() + "/", null));
        }
        // The new tab page shows an empty address bar, ready to type in.
        Assert.Equal("", InternalPages.AddressOf(Page.Home));
        Assert.Null(InternalPages.Resolve("gergur://nothing"));
    }

    [Theory]
    [InlineData(Page.Home, "bookmarks.list", true)]
    [InlineData(Page.Home, "history.list", false)]      // the new tab page shows bookmarks, not history
    [InlineData(Page.Home, "downloads.open", false)]
    [InlineData(Page.History, "history.clear", true)]
    [InlineData(Page.History, "bookmarks.remove", false)]
    [InlineData(Page.Downloads, "downloads.open", true)]
    [InlineData(Page.Downloads, "history.list", false)]
    [InlineData(Page.Bookmarks, "bookmarks.rename", true)]
    [InlineData(Page.Bookmarks, "downloads.list", false)]
    [InlineData(Page.History, "open", true)]
    public void EachPageMayOnlyAskForWhatItShows(Page page, string op, bool allowed)
        => Assert.Equal(allowed, InternalPages.Allowed(page, op));

    [Fact]
    public void ARefusedRequestSaysSoAndDoesNothing()
    {
        var services = Services();
        services.History.Append("https://example.com/", "Example");

        var answer = Ask(Page.Home, new { gergur = 1, id = 7, op = "history.clear", args = new { } }, services);

        Assert.Equal(7, answer.GetProperty("id").GetInt32());
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Single(services.History.Read());
    }

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("file:///C:/notes.html", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,<script>1</script>", false)]
    [InlineData("not a url", false)]
    [InlineData("file://server/share/page.html", false)]
    public void OnlyOrdinaryAddressesCanBeOpened(string url, bool openable)
    {
        var services = Services();

        var answer = Ask(Page.History, new { id = 1, op = "open", args = new { url, newTab = true } }, services);

        Assert.Equal(openable, answer.GetProperty("ok").GetBoolean());
        Assert.Equal(openable ? 1 : 0, _opened.Count);
        if (openable)
            Assert.Equal((url, true), _opened[0]);
    }

    [Fact]
    public void HistoryListsNewestFirstAndLeavesOutTheBrowsersOwnPages()
    {
        var services = Services();
        services.History.Append("https://first.example/", "First");
        services.History.Append("https://second.example/", "Second");

        var list = Ask(Page.History, new { id = 1, op = "history.list", args = new { max = 10 } }, services).GetProperty("result");

        Assert.Equal(["https://second.example/", "https://first.example/"], list.EnumerateArray().Select(v => v.GetProperty("url").GetString()));
        Assert.EndsWith("Z", list[0].GetProperty("visitedUtc").GetString());
    }

    [Fact]
    public void HistorySearchesAndRemoves()
    {
        var services = Services();
        services.History.Append("https://keep.example/", "Keep");
        services.History.Append("https://drop.example/", "Drop me");

        var found = Ask(Page.History, new { id = 1, op = "history.list", args = new { q = "drop" } }, services).GetProperty("result");
        Assert.Single(found.EnumerateArray());

        var removed = Ask(Page.History, new { id = 2, op = "history.remove", args = new { urls = new[] { "https://drop.example/" } } }, services);
        Assert.Equal(1, removed.GetProperty("result").GetProperty("removed").GetInt32());
        Assert.Equal("https://keep.example/", Assert.Single(services.History.Read()).Url);
    }

    [Fact]
    public void BookmarksAreListedRenamedAndRemoved()
    {
        var services = Services();
        services.Bookmarks.Toggle("https://a.example/", "A");
        services.Bookmarks.Toggle("https://b.example/", "B");

        Ask(Page.Bookmarks, new { id = 1, op = "bookmarks.rename", args = new { url = "https://a.example/", title = "  Alpha  " } }, services);
        Ask(Page.Bookmarks, new { id = 2, op = "bookmarks.remove", args = new { url = "https://b.example/" } }, services);
        var list = Ask(Page.Home, new { id = 3, op = "bookmarks.list", args = new { } }, services).GetProperty("result");

        var only = Assert.Single(list.EnumerateArray());
        Assert.Equal("Alpha", only.GetProperty("title").GetString());
    }

    [Fact]
    public void ABlankTitleIsRefusedRatherThanSaved()
    {
        var services = Services();
        services.Bookmarks.Toggle("https://a.example/", "A");

        var answer = Ask(Page.Bookmarks, new { id = 1, op = "bookmarks.rename", args = new { url = "https://a.example/", title = "   " } }, services);

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("A", services.Bookmarks.Items[0].Title);
    }

    [Fact]
    public void DownloadsAreNamedByIdNotPosition()
    {
        var downloads = new DownloadManager();
        var older = downloads.Add(new DownloadItem("https://x.example/a.zip", @"C:\d\a.zip", 10, 10, CoreWebView2DownloadState.Completed));
        var newer = downloads.Add(new DownloadItem("https://x.example/b.zip", @"C:\d\b.zip", 5, 20, CoreWebView2DownloadState.InProgress));

        var list = Ask(Page.Downloads, new { id = 1, op = "downloads.list", args = new { } }, Services(downloads)).GetProperty("result");

        Assert.NotEqual(older.Id, newer.Id);
        Assert.Equal(newer.Id, list[0].GetProperty("id").GetInt32());   // newest first
        Assert.Equal("inProgress", list[0].GetProperty("state").GetString());
        Assert.Equal("completed", list[1].GetProperty("state").GetString());
        Assert.Same(older, downloads.Find(older.Id));
    }

    [Fact]
    public void ADownloadThatHasBeenClearedSaysSo()
    {
        var answer = Ask(Page.Downloads, new { id = 1, op = "downloads.open", args = new { id = 99 } }, Services());

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("no longer", answer.GetProperty("error").GetString());
    }

    [Fact]
    public void GarbleIsAnsweredNotIgnored()
    {
        string answer = InternalPages.Answer(Page.History, "{not json", Services());
        Assert.False(JsonDocument.Parse(answer).RootElement.GetProperty("ok").GetBoolean());

        var missing = Ask(Page.Bookmarks, new { id = 4, op = "bookmarks.remove", args = new { } }, Services());
        Assert.Equal(4, missing.GetProperty("id").GetInt32());
        Assert.False(missing.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void VisitsToTheBrowsersOwnPagesAreNotHistory()
    {
        var history = new HistoryStore(Path.Combine(_dir, "own.jsonl"));

        history.Append(InternalPages.UrlOf(Page.History), "History");
        history.Append(InternalPages.UrlOf(Page.Downloads) + "?x", "Downloads");

        Assert.Empty(history.Read());
    }
}

/// <summary>The bookmark list's own rules, on a file of the test's own.</summary>
public sealed class BookmarkStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gergur-bookmarks-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void EveryChangeIsAnnouncedAndSaved()
    {
        var store = new BookmarkStore(_path);
        int changes = 0;
        store.Changed += (_, _) => changes++;

        store.Toggle("https://a.example/", "A");
        store.Rename("https://a.example/", "Renamed");
        store.Toggle("https://b.example/", "B");
        store.Remove("https://b.example/");

        Assert.Equal(4, changes);
        var reloaded = new BookmarkStore(_path);
        Assert.Equal("Renamed", Assert.Single(reloaded.Items).Title);
    }

    [Fact]
    public void RenamingKeepsTheBookmarksPlace()
    {
        var store = new BookmarkStore(_path);
        store.Toggle("https://a.example/", "A");
        store.Toggle("https://b.example/", "B");

        store.Rename("https://a.example/", "First");

        Assert.Equal(["First", "B"], store.Items.Select(b => b.Title));
    }

    [Fact]
    public void NothingToRemoveOrRenameChangesNothing()
    {
        var store = new BookmarkStore(_path);
        int changes = 0;
        store.Changed += (_, _) => changes++;

        Assert.False(store.Remove("https://none.example/"));
        Assert.False(store.Rename("https://none.example/", "X"));

        Assert.Equal(0, changes);
        Assert.False(File.Exists(_path));
    }
}

/// <summary>A link that opens a new window only to start a download.</summary>
public sealed class DownloadOnlyTabTests
{
    [Theory]
    [InlineData(true, "about:blank", true)]
    [InlineData(true, "https://example.com/download-page", false)]   // showed a page first: kept
    [InlineData(false, "about:blank", false)]                         // a tab the person opened
    public void OnlyAnEmptyNewWindowIsTakenAway(bool openedByAPage, string url, bool takenAway)
        => Assert.Equal(takenAway, Tab.OnlyOpenedForADownload(openedByAPage, url, "New tab"));

    [Fact]
    public void ANewWindowItsOpenerWroteIntoIsKept()
        => Assert.False(Tab.OnlyOpenedForADownload(openedByAPage: true, "about:blank", "Quarterly report"));

    [Fact]
    public async Task ItLeavesTheStripButItsViewLivesUntilTheDownloadIsDone()
    {
        var manager = new TabManager(null!, new Control(), null!);
        var opener = manager.AddSnapshotTab(new TabSnapshot("https://files.example/", "Files"));
        await manager.ActivateAsync(opener);
        var popup = await manager.CreatePopupTabAsync(opener);
        var download = new TaskCompletionSource();

        await manager.SetAsideAsync(popup, download.Task);

        Assert.Equal([opener], manager.Tabs);
        Assert.Same(opener, manager.ActiveTab);           // back to the page it came from
        Assert.Equal(1, manager.SetAsideCount);            // not closed yet
        Assert.False(Disposed(popup));

        download.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (manager.SetAsideCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(0, manager.SetAsideCount);
        Assert.True(Disposed(popup));                      // closed once the download ended
        Assert.Equal(0, manager.RecentlyClosedCount);       // and never offered for Ctrl+Shift+T
    }

    [Fact]
    public async Task TheWindowsOnlyTabIsLeftAlone()
    {
        var manager = new TabManager(null!, new Control(), null!);
        var popup = await manager.CreatePopupTabAsync(opener: null);

        await manager.SetAsideAsync(popup, Task.CompletedTask);

        Assert.Equal([popup], manager.Tabs);
        Assert.Equal(0, manager.SetAsideCount);
    }

    [Fact]
    public async Task ClosingTheWindowClosesWhatWasSetAside()
    {
        var manager = new TabManager(null!, new Control(), null!);
        var opener = manager.AddSnapshotTab(new TabSnapshot("https://files.example/", "Files"));
        var popup = await manager.CreatePopupTabAsync(opener);
        await manager.SetAsideAsync(popup, new TaskCompletionSource().Task);

        manager.DisposeAll();

        Assert.Equal(0, manager.SetAsideCount);
        Assert.True(Disposed(popup));
    }

    // A tab with no engine behind it reads Discarded from the start, so its state cannot
    // tell "closed" from "never built"; whether it was disposed can.
    private static bool Disposed(Tab tab)
        => (bool)typeof(Tab).GetField("_disposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tab)!;
}

/// <summary>What the session file keeps, and what an agent's own window must never do to it.</summary>
public sealed class SessionOfTests
{
    private static IReadOnlyList<Tab> Tabs(params string[] urls)
    {
        var manager = new TabManager(null!, new Control(), null!);
        foreach (var url in urls)
            manager.AddSnapshotTab(new TabSnapshot(url, url));
        return manager.Tabs;
    }

    [Fact]
    public void OnlyAgentWindowsLeftMeansLeaveTheFileAlone()
    {
        // The person closed their window; the agent's is last, and its last tab closes.
        var session = AppSession.SessionOf([(true, Tabs(), null)]);

        Assert.Null(session);
    }

    [Fact]
    public void AnAgentsWindowIsNeverPartOfTheSession()
    {
        var theirs = Tabs("https://mine.example/");
        var agents = Tabs("https://agent.example/");

        var session = AppSession.SessionOf([(false, theirs, theirs[0]), (true, agents, agents[0])]);

        var window = Assert.Single(session!);
        Assert.Equal("https://mine.example/", Assert.Single(window.Tabs).Url);
    }

    [Fact]
    public void ThePersonClosingTheirLastTabStillSavesAnEmptySession()
    {
        // Their own window with nothing left in it is a real answer: start fresh next time.
        var session = AppSession.SessionOf([(false, Tabs(), null)]);

        Assert.NotNull(session);
        Assert.Empty(session);
    }

    [Fact]
    public void TheActiveTabIsRememberedAmongTheOnesKept()
    {
        var tabs = Tabs(Gergur.App.HomePage.Url, "https://a.example/", "https://b.example/");

        var session = AppSession.SessionOf([(false, tabs, tabs[2])]);

        var window = Assert.Single(session!);
        Assert.Equal(2, window.Tabs.Count);      // the new tab page is not kept
        Assert.Equal(1, window.ActiveIndex);     // b is the second of the two kept
    }
}

/// <summary>Which bookmarks the bar shows and which go behind its overflow button.</summary>
public sealed class BookmarksBarFitTests
{
    [Fact]
    public void EverythingThatFitsIsShownWithNoOverflowButton()
        => Assert.Equal(3, Gergur.UI.BookmarksBar.FitCount([50, 50, 50], available: 200, gap: 2, overflowWidth: 32));

    [Fact]
    public void WhatDoesNotFitMakesRoomForTheOverflowButton()
    {
        // Two fit with room for the button beside them.
        Assert.Equal(2, Gergur.UI.BookmarksBar.FitCount([80, 80, 80], available: 200, gap: 2, overflowWidth: 32));
        // Two would fit, but not with the button too, so one goes behind it.
        Assert.Equal(1, Gergur.UI.BookmarksBar.FitCount([90, 90, 90], available: 200, gap: 2, overflowWidth: 32));
    }

    [Fact]
    public void OneTooWideForTheBarGoesBehindTheButton()
        => Assert.Equal(0, Gergur.UI.BookmarksBar.FitCount([300], available: 200, gap: 2, overflowWidth: 32));

    [Fact]
    public void EachSiteKeepsItsBadgeColour()
        => Assert.Equal(
            Gergur.UI.BookmarksBar.BadgeColour("https://www.github.com/a"),
            Gergur.UI.BookmarksBar.BadgeColour("https://github.com/b"));
}
