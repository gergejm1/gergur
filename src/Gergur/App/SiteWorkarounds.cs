using Gergur.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Gergur.App;

/// <summary>
/// Gmail, and why it gets no service worker.
///
/// Gmail registers a service worker so it can open offline. In this browser that worker,
/// once it controls a page, stops answering: every request the page then makes, the inbox
/// sync, the account menu, the chat frame, waits out the engine's one minute limit for the
/// worker to start and fails, and the page shows "Not connected" with a blank account
/// picture. Measured on 2026-11-02 against the real profile:
///
/// - A page controlled by the worker: every fetch from it hung to its timeout, example.com
///   and gstatic included, because the worker sits in front of all of them. The same
///   requests from any other page, same engine, answered in 100 to 350 ms.
/// - The worker's registration removed: the inbox loaded in about six seconds, complete,
///   with no banner. Gmail then registered a fresh worker on that load, and the next load
///   hung again for over a minute. So it is not a damaged store that a clean one cures, which
///   is what wiping the Service Worker folder did, for a few days, each time.
/// - The workers of x.com, Notion, Outlook and YouTube in the same profile all start and
///   answer normally. It is Gmail's alone.
///
/// Why Gmail's worker cannot start here was not found. Gmail runs without one, and without
/// offline mode, which nobody here uses, so it is not given one. What it costs is the
/// offline copy; what it buys is an inbox that opens.
/// </summary>
public static class SiteWorkarounds
{
    /// <summary>
    /// Run at the start of every page and frame. Does nothing anywhere but https://mail.google.com.
    /// There it unregisters any worker Gmail already has, so the next load is not stuck (the
    /// load it runs in may still be, because a worker already in control stays until the page
    /// goes), and then removes the feature from the page so Gmail sees a browser without it and
    /// registers nothing.
    ///
    /// "delete" and not an override of register(): a page that finds the feature present waits
    /// for it to become ready, and nothing here would ever make it so.
    ///
    /// The removal does not depend on the rest. A frame that is not allowed to read the
    /// feature (a sandboxed one) throws when it tries, and that must not leave the feature in
    /// place for the rest of the script to find.
    /// </summary>
    public const string GmailNoServiceWorkerScript = """
        (function () {
          try {
            if (location.protocol !== 'https:' || location.hostname !== 'mail.google.com') return;
            var workers = null;
            try { workers = navigator.serviceWorker; } catch (e) { }
            try { delete Navigator.prototype.serviceWorker; } catch (e) { }
            if (workers && workers.getRegistrations) {
              workers.getRegistrations().then(function (list) {
                list.forEach(function (r) { try { r.unregister(); } catch (e) { } });
              }, function () { });
            }
          } catch (e) { }
        })();
        """;

    /// <summary>The note that the registrations left by earlier runs have been cleared.</summary>
    internal static string ResetMarkerPath { get; set; } =
        Path.Combine(Settings.DataDir, "service-workers-reset");

    /// <summary>How long to wait for the engine to clear them before carrying on without.</summary>
    internal static readonly TimeSpan ResetTimeout = TimeSpan.FromSeconds(4);

    private static readonly object ResetGate = new();
    private static Task? _reset;

    /// <summary>
    /// Whether the registrations still have to be cleared: once, the first run after this was
    /// added, because Gmail's worker from before it is still registered and still stuck, and
    /// the script above only runs once the page has arrived.
    /// </summary>
    internal static bool ResetNeeded(bool markerExists) => !markerExists;

    /// <summary>
    /// The engine call that clears Gmail's service workers and nothing else: no other site's
    /// workers, and none of Gmail's other storage. Only Gmail's is broken, and the profile-wide
    /// clear the engine also offers would have taken every other site's with it.
    /// </summary>
    internal const string ClearGmailWorkersMethod = "Storage.clearDataForOrigin";
    internal const string ClearGmailWorkersParameters =
        """{"origin":"https://mail.google.com","storageTypes":"service_workers"}""";

    /// <summary>
    /// Clears Gmail's service worker registration, once. Never longer than
    /// <see cref="ResetTimeout"/>: a call that does not return is not allowed to stop a window
    /// opening, and the marker is only written when it did, so a run that timed out tries
    /// again on the next.
    /// </summary>
    public static Task ResetServiceWorkersOnceAsync(CoreWebView2 core)
    {
        lock (ResetGate)
            return _reset ??= ResetAsync(
                () => core.CallDevToolsProtocolMethodAsync(ClearGmailWorkersMethod, ClearGmailWorkersParameters),
                ResetMarkerPath, ResetTimeout);
    }

    /// <summary>
    /// The clear and its rules, apart from the engine so they can be run without one: nothing
    /// is done once the marker exists; the wait is never longer than <paramref name="timeout"/>;
    /// the marker is written only after the clear has finished; and nothing is thrown at the
    /// caller, because the caller is opening a window.
    /// </summary>
    internal static async Task ResetAsync(Func<Task> clear, string markerPath, TimeSpan timeout)
    {
        try
        {
            if (!ResetNeeded(File.Exists(markerPath)))
                return;

            var clearing = clear();
            if (await Task.WhenAny(clearing, Task.Delay(timeout)) != clearing)
            {
                DebugLog.WriteAlways("clearing service workers did not finish in time; carrying on");
                // Whatever it does later is not waited for, and not allowed to surface as an
                // unobserved failure either.
                _ = Observe(clearing);
                return;
            }
            await clearing;
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            await File.WriteAllTextAsync(markerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"service workers could not be cleared: {ex.Message}");
        }
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch { /* nobody is waiting for it any more */ }
    }
}
