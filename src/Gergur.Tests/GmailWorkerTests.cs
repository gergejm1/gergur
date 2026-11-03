using System.Diagnostics;
using System.Text.RegularExpressions;
using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Gmail's service worker wedges every page it controls in this browser, so Gmail is given
/// none. What was measured is in <see cref="SiteWorkarounds"/>. The script that does it runs
/// in every page of every site, so the thing worth proving is what it does NOT do: it has to
/// leave every other host, and Gmail over plain http, exactly as it found them.
///
/// Run under node against a stand-in for the parts of the page it touches, because a script
/// that does not parse, or throws on a host it was never meant for, fails silently in the
/// page and the first anyone hears of it is a site that stopped working.
/// </summary>
public sealed class GmailWorkerTests
{
    private static string Code(params string[] parts)
        => Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource(parts)), @"//[^\n]*", "");

    /// <summary>
    /// What the script leaves behind after running on a page at this address that has two
    /// service worker registrations: whether the page still has the feature, and how many
    /// registrations were removed.
    /// </summary>
    private static (bool HasServiceWorker, int Unregistered, bool Threw) RunOn(string protocol, string host)
    {
        string harness = $$"""
            "use strict";
            var removed = 0;
            function Navigator() {}
            var container = {
              getRegistrations: function () {
                return Promise.resolve([
                  { unregister: function () { removed++; return Promise.resolve(true); } },
                  { unregister: function () { removed++; return Promise.resolve(true); } }
                ]);
              }
            };
            Object.defineProperty(Navigator.prototype, 'serviceWorker', { configurable: true, get: function () { return container; } });
            Object.defineProperty(globalThis, 'Navigator', { value: Navigator, configurable: true, writable: true });
            Object.defineProperty(globalThis, 'navigator', { value: new Navigator(), configurable: true, writable: true });
            Object.defineProperty(globalThis, 'location', { value: { protocol: {{System.Text.Json.JsonSerializer.Serialize(protocol)}}, hostname: {{System.Text.Json.JsonSerializer.Serialize(host)}} }, configurable: true, writable: true });
            var threw = false;
            try { (function () { {{SiteWorkarounds.GmailNoServiceWorkerScript}}
            })(); } catch (e) { threw = true; }
            Promise.resolve().then(function () { return Promise.resolve(); }).then(function () {
              process.stdout.write(JSON.stringify({ has: 'serviceWorker' in navigator, removed: removed, threw: threw }));
            });
            """;
        using var result = System.Text.Json.JsonDocument.Parse(RunNode(harness));
        var root = result.RootElement;
        return (root.GetProperty("has").GetBoolean(), root.GetProperty("removed").GetInt32(), root.GetProperty("threw").GetBoolean());
    }

    /// <summary>
    /// Runs the text under node and returns what it wrote to standard output.
    ///
    /// Node is a hard requirement and not a reason to skip, as it is for EvalScriptTests: these
    /// passing on a machine without it would be results proving nothing, which this project
    /// treats as worse than no test. What is fixed here is the way it fails, with the reason
    /// and not an exception about a file that could not be found.
    /// </summary>
    private static string RunNode(string javascript)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gergur-gmail-{Guid.NewGuid():n}.js");
        File.WriteAllText(path, javascript);
        try
        {
            Process? node;
            try
            {
                node = Process.Start(new ProcessStartInfo("node", $"\"{path}\"")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Fail("node is needed to run the page scripts under test and was not found on PATH (checks.sh needs it too)");
                throw;
            }
            using (node)
            {
                Assert.NotNull(node);
                var printed = node.StandardOutput.ReadToEndAsync();
                var complaint = node.StandardError.ReadToEndAsync();
                Assert.True(node.WaitForExit(30_000), "node did not finish");
                Assert.True(Task.WaitAll([printed, complaint], 5_000), "node did not close its pipes");
                Assert.True(node.ExitCode == 0, complaint.Result);
                return printed.Result;
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* a temp file that outlives the run is not a failure */ }
        }
    }

    [Fact]
    public void OnGmailTheRegistrationsAreRemovedAndTheFeatureIsGone()
    {
        var (has, unregistered, threw) = RunOn("https:", "mail.google.com");

        Assert.False(threw);
        Assert.Equal(2, unregistered);
        Assert.False(has, "Gmail must find a browser without service workers, or it registers one");
    }

    [Theory]
    [InlineData("https:", "www.google.com")]
    [InlineData("https:", "accounts.google.com")]
    [InlineData("https:", "drive.google.com")]
    [InlineData("https:", "chat.google.com")]
    [InlineData("https:", "www.youtube.com")]
    [InlineData("https:", "x.com")]
    [InlineData("https:", "example.com")]
    // Names that merely contain it.
    [InlineData("https:", "mail.google.com.example.com")]
    [InlineData("https:", "notmail.google.com")]
    [InlineData("https:", "mail.google.com.")]
    [InlineData("https:", "")]
    // Not the real thing: an address on the machine, or a page that came by another scheme.
    [InlineData("http:", "mail.google.com")]
    [InlineData("file:", "")]
    [InlineData("about:", "")]
    public void EveryOtherPageKeepsItsServiceWorkersAndLosesNothing(string protocol, string host)
    {
        var (has, unregistered, threw) = RunOn(protocol, host);

        Assert.False(threw);
        Assert.Equal(0, unregistered);
        Assert.True(has, $"{protocol}//{host} lost the service worker feature");
    }

    [Fact]
    public void ARunWithNoServiceWorkerFeatureAtAllDoesNotThrow()
    {
        // A page in a context that never had it, which is every insecure origin.
        string harness = $$"""
            function Navigator() {}
            Object.defineProperty(globalThis, 'Navigator', { value: Navigator, configurable: true, writable: true });
            Object.defineProperty(globalThis, 'navigator', { value: new Navigator(), configurable: true, writable: true });
            Object.defineProperty(globalThis, 'location', { value: { protocol: 'https:', hostname: 'mail.google.com' }, configurable: true, writable: true });
            var threw = false;
            try { (function () { {{SiteWorkarounds.GmailNoServiceWorkerScript}}
            })(); } catch (e) { threw = true; }
            process.stdout.write(String(threw));
            """;
        Assert.Equal("false", RunNode(harness));
    }

    [Fact]
    public void AFrameThatMayNotReadTheFeatureStillLosesIt()
    {
        // A sandboxed frame with no origin of its own throws when it reads the property. Taking
        // the feature away must not wait behind that, or the frame keeps what the rest of the
        // script was written to remove.
        string harness = $$"""
            function Navigator() {}
            Object.defineProperty(Navigator.prototype, 'serviceWorker', { configurable: true, get: function () { throw new Error('SecurityError'); } });
            Object.defineProperty(globalThis, 'Navigator', { value: Navigator, configurable: true, writable: true });
            Object.defineProperty(globalThis, 'navigator', { value: new Navigator(), configurable: true, writable: true });
            Object.defineProperty(globalThis, 'location', { value: { protocol: 'https:', hostname: 'mail.google.com' }, configurable: true, writable: true });
            var threw = false;
            try { (function () { {{SiteWorkarounds.GmailNoServiceWorkerScript}}
            })(); } catch (e) { threw = true; }
            process.stdout.write(JSON.stringify({ threw: threw, has: 'serviceWorker' in navigator }));
            """;

        using var result = System.Text.Json.JsonDocument.Parse(RunNode(harness));

        Assert.False(result.RootElement.GetProperty("threw").GetBoolean());
        Assert.False(result.RootElement.GetProperty("has").GetBoolean(), "the frame still has the feature");
    }

    [Fact]
    public void EveryTabRunsItBeforeAnyPageScriptAndTheEngineIsClearedOnceFirst()
    {
        string tab = Code("src", "Gergur", "Tabs", "Tab.cs");
        Assert.Contains("AddScriptToExecuteOnDocumentCreatedAsync(SiteWorkarounds.GmailNoServiceWorkerScript)", tab);

        string env = Code("src", "Gergur", "App", "BrowserEnvironment.cs");
        int reset = env.IndexOf("SiteWorkarounds.ResetServiceWorkersOnceAsync(", StringComparison.Ordinal);
        int returned = env.IndexOf("return webView;", StringComparison.Ordinal);
        Assert.True(reset >= 0, "the first view no longer clears the old registrations");
        Assert.True(reset < returned, "the registrations must be cleared before the view is handed out and sent to a page");
    }

    // ------------------------------------------------------------------ clearing once

    [Fact]
    public void TheOldRegistrationsAreClearedOnTheFirstRunAndNotAgain()
    {
        Assert.True(SiteWorkarounds.ResetNeeded(markerExists: false));
        Assert.False(SiteWorkarounds.ResetNeeded(markerExists: true));
    }

    [Fact]
    public void TheOneTimeClearTouchesGmailsServiceWorkersAndNothingElse()
    {
        // Only Gmail's worker is broken. The profile-wide clear would also have taken every
        // other site's, which is more of the person's browsing than this needs.
        Assert.Equal("Storage.clearDataForOrigin", SiteWorkarounds.ClearGmailWorkersMethod);
        using var parameters = System.Text.Json.JsonDocument.Parse(SiteWorkarounds.ClearGmailWorkersParameters);
        var root = parameters.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal("https://mail.google.com", root.GetProperty("origin").GetString());
        Assert.Equal("service_workers", root.GetProperty("storageTypes").GetString());

        string source = Code("src", "Gergur", "App", "SiteWorkarounds.cs");
        Assert.DoesNotContain("ClearBrowsingDataAsync", source);
    }

    [Fact]
    public void TheEnginesClearIsBoundedByTheLimitTheWindowCanAfford()
    {
        Assert.InRange(SiteWorkarounds.ResetTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------------ the clear itself, run against a stand-in for the engine

    private static string NewMarker()
        => Path.Combine(Path.GetTempPath(), "gergur-tests", $"sw-reset-{Guid.NewGuid():n}", "service-workers-reset");

    private static void Tidy(string marker)
    {
        try { Directory.Delete(Path.GetDirectoryName(marker)!, recursive: true); } catch { /* it may never have been made */ }
    }

    [Fact]
    public async Task TheClearRunsOnceAndTheMarkerIsWrittenOnlyAfterItHasFinished()
    {
        string marker = NewMarker();
        try
        {
            // Looked at inside the clear and judged outside it: the clear's own exceptions are
            // swallowed by design, and an assertion thrown in there would vanish with them.
            int calls = 0;
            bool markerBeforeClearing = true, markerDuringClearing = true;
            await SiteWorkarounds.ResetAsync(async () =>
            {
                calls++;
                markerBeforeClearing = File.Exists(marker);
                await Task.Delay(30);
                markerDuringClearing = File.Exists(marker);
            }, marker, TimeSpan.FromSeconds(10));

            Assert.Equal(1, calls);
            Assert.False(markerBeforeClearing);
            Assert.False(markerDuringClearing, "the marker was written before the clear finished");
            Assert.True(File.Exists(marker), "a clear that finished leaves its marker");

            // The next run finds the marker and does nothing at all.
            await SiteWorkarounds.ResetAsync(() => { calls++; return Task.CompletedTask; }, marker, TimeSpan.FromSeconds(10));
            Assert.Equal(1, calls);
        }
        finally { Tidy(marker); }
    }

    [Fact]
    public async Task AClearThatNeverReturnsIsGivenUpOnAtTheLimitAndLeavesNoMarkerSoTheNextRunTriesAgain()
    {
        string marker = NewMarker();
        var never = new TaskCompletionSource();
        try
        {
            var waited = Stopwatch.StartNew();
            await SiteWorkarounds.ResetAsync(() => never.Task, marker, TimeSpan.FromMilliseconds(200));
            waited.Stop();

            Assert.InRange(waited.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(8));
            Assert.False(File.Exists(marker), "a clear that did not finish must not be recorded as done");

            int calls = 0;
            await SiteWorkarounds.ResetAsync(() => { calls++; return Task.CompletedTask; }, marker, TimeSpan.FromSeconds(10));
            Assert.Equal(1, calls);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            never.TrySetResult();
            Tidy(marker);
        }
    }

    [Fact]
    public async Task AClearThatFailsNeverThrowsIntoTheCallerThatOpenedAWindowAndLeavesNoMarker()
    {
        string marker = NewMarker();
        try
        {
            await SiteWorkarounds.ResetAsync(() => Task.FromException(new InvalidOperationException("engine went away")),
                marker, TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(marker));

            // Failing before it even returns a task is the same.
            await SiteWorkarounds.ResetAsync(() => throw new InvalidOperationException("no view to ask"),
                marker, TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(marker));
        }
        finally { Tidy(marker); }
    }

    private const string LateFailure = "failed long after anyone was waiting";

    /// <summary>
    /// Its own method so that nothing here still holds the task when the collector runs, which
    /// is the only way an exception nobody looked at can be told about.
    /// </summary>
    private static async Task GiveUpOnAClearThatFailsLaterAsync(string marker)
    {
        var late = new TaskCompletionSource();
        await SiteWorkarounds.ResetAsync(() => late.Task, marker, TimeSpan.FromMilliseconds(100));
        late.SetException(new InvalidOperationException(LateFailure));
        await Task.Delay(100);
    }

    [Fact]
    public async Task AClearThatFailsAfterTheLimitHasPassedDoesNotComeBackToHauntTheProcess()
    {
        string marker = NewMarker();
        var unobserved = new List<string>();
        void Watch(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // Only this test's own failure: other tests run alongside and have their own.
            string text = e.Exception.ToString();
            if (text.Contains(LateFailure, StringComparison.Ordinal))
                lock (unobserved) unobserved.Add(text);
        }
        TaskScheduler.UnobservedTaskException += Watch;
        try
        {
            await GiveUpOnAClearThatFailsLaterAsync(marker);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            lock (unobserved)
                Assert.Empty(unobserved);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Watch;
            Tidy(marker);
        }
    }
}
