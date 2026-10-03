using System.Text.RegularExpressions;
using Gergur.Diagnostics;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The browser freezing: the Google account chooser, and switching to a sleeping tab. Neither
/// has been reproduced, so these hold the fixes for what the code was found doing wrong, and
/// the watchdog that records the rest.
/// </summary>
public sealed class FreezeFixesTests
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

    // ------------------------------------------------------------------ the watchdog

    [Fact]
    public void AStallIsReportedOnceAndItsEndOnce()
    {
        var detector = new StallDetector(TimeSpan.FromSeconds(6));
        Assert.Equal(StallDetector.Verdict.Nothing, detector.Observe(false, TimeSpan.Zero));
        Assert.Equal(StallDetector.Verdict.Nothing, detector.Observe(true, TimeSpan.FromSeconds(5)));
        Assert.Equal(StallDetector.Verdict.Stalled, detector.Observe(true, TimeSpan.FromSeconds(6)));
        Assert.Equal(StallDetector.Verdict.Nothing, detector.Observe(true, TimeSpan.FromSeconds(8)));   // not again
        Assert.Equal(StallDetector.Verdict.Recovered, detector.Observe(false, TimeSpan.Zero));
        Assert.Equal(StallDetector.Verdict.Nothing, detector.Observe(false, TimeSpan.Zero));
        // A second stall is a new one.
        Assert.Equal(StallDetector.Verdict.Stalled, detector.Observe(true, TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void BreadcrumbsKeepTheNewestAndTheirOrder()
    {
        // Other tests write to the same list while this runs, so only this test's own entries
        // are held to an order, and "newest last" means last of those.
        string mine = Guid.NewGuid().ToString("n");
        Breadcrumbs.Note($"first {mine}");
        for (int i = 0; i < Breadcrumbs.Capacity; i++)
            Breadcrumbs.Note($"filler {mine} {i:000}");
        Breadcrumbs.Note($"last {mine}");

        var all = Breadcrumbs.Recent(Breadcrumbs.Capacity * 2);
        Assert.Equal(Breadcrumbs.Capacity, all.Count);
        var ours = all.Where(line => line.Contains(mine)).ToList();
        Assert.DoesNotContain(ours, line => line.Contains($"first {mine}"));   // the oldest went
        Assert.EndsWith($"last {mine}", ours[^1]);                               // newest last
        var fillers = ours.Where(line => line.Contains("filler")).Select(line => line[^3..]).ToList();
        Assert.Equal(fillers.OrderBy(n => n, StringComparer.Ordinal), fillers);   // oldest first
        Assert.Equal(2, Breadcrumbs.Recent(2).Count);
        Assert.Empty(Breadcrumbs.Recent(0));
    }

    [Fact]
    public void ADescriptionSaysHowLongAndWhatWasLastDone()
    {
        string marker = "marker-" + Guid.NewGuid().ToString("n");
        Breadcrumbs.Note(marker);

        string line = UiWatchdog.Describe(9);

        Assert.Contains("has not responded for 9s", line);
        Assert.Contains(marker, line);
    }

    [Fact]
    public void AWindowThatStopsAnsweringIsWrittenToTheLog()
    {
        // A real window on its own thread, made to stop answering for three and a half seconds.
        // Pings every 100 ms, a stall at a second: wide enough to hold on a busy machine. That a
        // stall is reported once is the detector test above, not this one.
        string path = DebugLog.FilePath;
        long start = File.Exists(path) ? new FileInfo(path).Length : 0;
        Form? form = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000), Size = new Size(50, 50) };
            form.Shown += (_, _) => ready.Set();
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(15)), "the window did not open");
        try
        {
            using var watchdog = new UiWatchdog(() => form, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            Thread.Sleep(600);   // answering, so far
            form!.Invoke(() => Thread.Sleep(3500));
            string written = "";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                written = ReadFrom(path, start);
                if (written.Contains("responded again after"))
                    break;
                Thread.Sleep(100);
            }
            Assert.Contains("the window has not responded for", written);
            Assert.Contains("responded again after", written);
        }
        finally
        {
            try { form!.Invoke(() => form.Close()); } catch { }
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static string ReadFrom(string path, long offset)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length <= offset)
                    return "";
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) { Thread.Sleep(50); }
        }
        return "";
    }

    [Fact]
    public void AMachineThatSleptIsNotAStall()
    {
        Assert.False(UiWatchdog.WasAsleep(TimeSpan.FromSeconds(2)));
        Assert.False(UiWatchdog.WasAsleep(UiWatchdog.SleepGap));
        Assert.True(UiWatchdog.WasAsleep(TimeSpan.FromHours(3)));

        // A stall in progress is forgotten with it, so its end is not reported afterwards.
        var detector = new StallDetector(TimeSpan.FromSeconds(1));
        Assert.Equal(StallDetector.Verdict.Stalled, detector.Observe(true, TimeSpan.FromSeconds(2)));
        detector.Reset();
        Assert.Equal(StallDetector.Verdict.Nothing, detector.Observe(false, TimeSpan.Zero));
    }

    [Fact]
    public void TheWatchdogFollowsAWindowThatIsStillOpen()
    {
        // Picked at every check, so closing the first window does not end the watching while
        // another is open, and nothing here holds a closed window.
        string source = Code("src", "Gergur", "Diagnostics", "UiWatchdog.cs");
        string check = Body(source, "private void Check(");
        Assert.Contains("_pickWindow()", check);
        Assert.Matches(@"if \(WasAsleep\(now - _lastCheckUtc\)\)\s*\{\s*_pingPending = false;\s*_detector\.Reset\(\);\s*\}", check);
        Assert.Contains("window.Disposed +=", Body(source, "public static void Start("));
        Assert.Contains("Windows.Remove(window)", Body(source, "public static void Start("));
    }

    // ------------------------------------------------------------------ which tab is hidden by an activation

    [Fact]
    public void OnlyTheNewestActivationHidesTheTabThatIsShowing()
    {
        var manager = new TabManager(null!, new Control(), null!);
        var a = manager.AddSnapshotTab(new TabSnapshot("https://a.example/", "A"));
        var b = manager.AddSnapshotTab(new TabSnapshot("https://b.example/", "B"));
        var c = manager.AddSnapshotTab(new TabSnapshot("https://c.example/", "C"));

        // A showing, B clicked and arriving while still the newest: A is hidden.
        Assert.Same(a, TabManager.TabToHide(active: b, activated: b, shown: a));
        // B was slow and A was clicked back: B arriving late hides nothing. A is the active tab.
        Assert.Null(TabManager.TabToHide(active: a, activated: b, shown: a));
        // A showing, B clicked (slow), C clicked and finishing first: C hides A, the tab that is
        // really on screen, not B, which is what C found in front and which has no view yet.
        Assert.Same(a, TabManager.TabToHide(active: c, activated: c, shown: a));
        // And B finishing after that hides nothing: C is the active tab and has taken A's place.
        Assert.Null(TabManager.TabToHide(active: c, activated: b, shown: c));
        // Nothing showing yet, or the tab showing is the one being shown again.
        Assert.Null(TabManager.TabToHide(active: b, activated: b, shown: null));
        Assert.Null(TabManager.TabToHide(active: b, activated: b, shown: b));
    }

    [Fact]
    public void ActivatingATabHidesWhatIsShowingAndRemembersWhatIsNow()
    {
        string source = Code("src", "Gergur", "Tabs", "TabManager.cs");
        string activate = Body(source, "public async Task ActivateAsync(");
        int waited = activate.IndexOf("await tab.ActivateAsync();", StringComparison.Ordinal);
        Assert.True(waited >= 0);
        // After the wait, only for the newest activation, in this order: decide, remember, hide.
        Assert.Matches(@"if \(ActiveTab == tab\)\s*\{\s*var toHide = TabToHide\(ActiveTab, tab, _shown\);\s*_shown = tab\.State == TabState\.Active \? tab : null;\s*toHide\?\.Deactivate\(\);\s*\}", activate[waited..]);
        Assert.DoesNotMatch(@"\bprevious\??\.Deactivate", activate);
        // A tab that leaves the strip or the window is no longer the one showing.
        Assert.Contains("_shown = null;", Body(source, "private async Task RemoveAndDisposeAsync("));
        Assert.Contains("_shown = null;", Body(source, "internal async Task ReleaseAsync("));
        Assert.Contains("_shown = null;", Body(source, "internal async Task SetAsideAsync("));
        Assert.Contains("_shown = null;", Body(source, "public void DisposeAll("));
    }
    // ------------------------------------------------------------------ a popup that closes itself

    [Fact]
    public async Task AClosingPopupGoesBackToThePageThatOpenedIt()
    {
        // The Google sign-in window closes itself once an account is chosen. It is the last tab,
        // so the tab "next to it" is another page entirely.
        var manager = new TabManager(null!, new Control(), null!);
        var opener = manager.AddSnapshotTab(new TabSnapshot("https://a.example/", "A"));
        var other = manager.AddSnapshotTab(new TabSnapshot("https://b.example/", "B"));
        var popup = await manager.CreatePopupTabAsync(opener);
        Assert.Same(popup, manager.ActiveTab);

        await manager.CloseTabAsync(popup);

        Assert.Same(opener, manager.ActiveTab);
        Assert.NotSame(other, manager.ActiveTab);
    }

    [Fact]
    public async Task AClosingPopupWhoseOpenerIsGoneFallsBackToTheTabBeside()
    {
        var manager = new TabManager(null!, new Control(), null!);
        var opener = manager.AddSnapshotTab(new TabSnapshot("https://a.example/", "A"));
        var other = manager.AddSnapshotTab(new TabSnapshot("https://b.example/", "B"));
        var popup = await manager.CreatePopupTabAsync(opener);
        await manager.CloseTabAsync(opener);   // not the active one: the popup is

        await manager.CloseTabAsync(popup);

        Assert.Same(other, manager.ActiveTab);
    }

    // ------------------------------------------------------------------ wiring no test can drive

    [Fact]
    public void AViewIsNeverDisposedFromInsideItsOwnEngineCallback()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");

        // The renderer dying: the tab is discarded after the callback returns, and only if it
        // is still the view that failed.
        string failed = Body(tab, "private void OnProcessFailed(");
        Assert.Matches(@"AfterCallback\(\(\) =>\s*\{\s*if \(_webView is not null && ReferenceEquals\(Core, failing\)\)\s*Discard\(\);\s*\}\);", failed);
        Assert.Single(Regex.Matches(failed, @"\bDiscard\(\)"));   // that one, nowhere else

        // The page closing itself: the tab closes after the callback returns.
        string closing = Body(tab, "private void OnWindowCloseRequested(");
        Assert.Matches(@"AfterCallback\(\(\) =>\s*\{\s*if \(!_disposed\)\s*CloseRequested\?\.Invoke\(this, EventArgs\.Empty\);\s*\}\);", closing);
        Assert.Single(Regex.Matches(closing, @"CloseRequested\?\.Invoke"));

        // And the deferral really is a post to the window's thread, not a call.
        Assert.Contains("host.BeginInvoke(action);", Body(tab, "private void AfterCallback("));
        // A callback with no window to act in is said, not dropped silently.
        Assert.Matches(@"if \(host\.IsDisposed \|\| !host\.IsHandleCreated\)\s*\{\s*Breadcrumbs\.Note\([^;]*\);\s*return;\s*\}", Body(tab, "private void AfterCallback("));
    }

    [Fact]
    public void ATabThatFinishesWakingAfterThePersonMovedOnStaysHidden()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        string activate = Body(tab, "public async Task ActivateAsync(");
        int built = activate.IndexOf("await EnsureLiveAsync();", StringComparison.Ordinal);
        int guard = activate.IndexOf("manager.ActiveTab != this", StringComparison.Ordinal);
        int shown = activate.IndexOf("_webView.Visible = true;", StringComparison.Ordinal);
        Assert.True(built >= 0 && guard > built && shown > guard,
            "the check that this is still the active tab must come after the wait and before the view is shown");
        Assert.Matches(@"manager\.ActiveTab != this\)\s*\{[\s\S]*?return;\s*\}", activate);
        // Built just now, so not due to sleep on the timestamp it had before it was discarded.
        Assert.Matches(@"manager\.ActiveTab != this\)\s*\{[\s\S]*?LastActiveUtc = DateTime\.UtcNow;[\s\S]*?return;", activate);
    }

    [Fact]
    public void TheWindowIsWatchedAndAnEngineUpdateIsSaid()
    {
        string form = Code("src", "Gergur", "UI", "MainForm.cs");
        Assert.Contains("UiWatchdog.Start(this);", form);
        Assert.Contains("NewBrowserVersionAvailable += OnNewBrowserVersionAvailable;", form);
        Assert.Contains("Restart Gergur to use it", Body(form, "private void OnNewBrowserVersionAvailable("));
        // An engine process failing is always in the log, without an address.
        string failed = Body(Code("src", "Gergur", "Tabs", "Tab.cs"), "private void OnProcessFailed(");
        Assert.Matches(@"DebugLog\.WriteAlways\(\$""engine process failed \(\{Id\}\)[^""]*""\);", failed);
        Assert.DoesNotMatch(@"WriteAlways\([^;]*\bUrl\b", failed);
    }

    [Fact]
    public void WhatTheBrowserDoesToATabIsRecordedForTheWatchdog()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        Assert.Contains("Breadcrumbs.Note", Body(tab, "public async Task ActivateAsync("));
        Assert.Contains("Breadcrumbs.Note", Body(tab, "public async Task<bool> TrySuspendAsync("));
        Assert.Contains("Breadcrumbs.Note", Body(tab, "public void Discard("));
        Assert.Contains("Breadcrumbs.Note", Body(tab, "private async Task BuildAndCountAsync("));
        Assert.Contains("Breadcrumbs.Note", Body(Code("src", "Gergur", "Tabs", "TabManager.cs"), "public async Task ActivateAsync("));
        // The suspend is noted on both sides of the call that can hang: a stall that ends the
        // log on "asking" and never reaches "answered" is the answer.
        string suspend = Body(tab, "public async Task<bool> TrySuspendAsync(");
        int asking = suspend.IndexOf("asking the engine to suspend", StringComparison.Ordinal);
        int call = suspend.IndexOf("await core.TrySuspendAsync()", StringComparison.Ordinal);
        int answered = suspend.IndexOf("suspend answered", StringComparison.Ordinal);
        Assert.True(asking >= 0 && call > asking && answered > call);
        // No address anywhere in what is remembered.
        foreach (Match note in Regex.Matches(tab, @"Breadcrumbs\.Note\([^;]*\);"))
            Assert.DoesNotMatch(@"\bUrl\b|\.Title\b", note.Value);
    }
}
