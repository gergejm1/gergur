using System.Text.Json;
using Gergur.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Gergur.Tabs;

/// <summary>
/// Sends <see cref="TrustedInput"/>'s messages to a tab's engine: clicks, typing, keys,
/// hovering, the wheel, and files for a file input. UI thread only, like every call into
/// the engine. Each checks that the tab is not on one of the browser's own pages before it
/// touches the engine, and again with nothing awaited between that check and the message
/// that acts (the click's first event, the files): the preparation in between awaits, and
/// the tab may have gone to gergur://settings by then.
/// </summary>
internal static class TabInput
{
    /// <summary>The protocol call failed: no view, or the engine refused it. The message says which.</summary>
    /// <param name="mayHaveLanded">True when the press had already gone out: the page may
    /// have acted on it, so the caller must not try again another way and click twice.</param>
    /// <param name="timedOut">The engine did not answer in time, as opposed to refusing.</param>
    /// <param name="busy">Another click or upload held the tab: nothing of this one went out.</param>
    internal sealed class InputFailedException(string message, bool mayHaveLanded = false, bool timedOut = false, bool busy = false) : Exception(message)
    {
        public bool MayHaveLanded { get; } = mayHaveLanded;
        public bool TimedOut { get; } = timedOut;
        public bool Busy { get; } = busy;
    }

    /// <summary>How long an action waits for another in the same tab to finish, kept short so both fit in one tool call.</summary>
    internal static readonly TimeSpan TurnWait = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The most one whole action (a click with its chooser watch, an upload, a run of key
    /// presses) may take, shared by all its protocol calls. Inside the 20 seconds an MCP tool
    /// call has, with room for finding the element first.
    /// </summary>
    internal static readonly TimeSpan ActionBudget = TimeSpan.FromSeconds(8);

    /// <summary>When the action under way must be done by; flows with the action's awaits.</summary>
    private static readonly AsyncLocal<DateTime?> ActionDeadline = new();

    private static void BeginAction() => ActionDeadline.Value = DateTime.UtcNow + ActionBudget;

    /// <summary>
    /// What a call may wait now: its own budget, or what is left of the action's if less.
    /// Internal for tests.
    /// </summary>
    internal static TimeSpan BudgetFor(DateTime? deadline, DateTime nowUtc)
    {
        if (deadline is not { } until)
            return CallBudget;
        var left = until - nowUtc;
        return left <= TimeSpan.Zero ? TimeSpan.Zero : left < CallBudget ? left : CallBudget;
    }

    /// <summary>
    /// One click or upload at a time per tab. Interception of the file chooser is switched on
    /// and off per action, so a second action overlapping the first would have the first
    /// one's end switch it off under the second, and a chooser the second opens would come
    /// up as a native dialog from a hidden window.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Tab, SemaphoreSlim> OneAtATime = new();

    private static async Task<IDisposable> TakeTurnAsync(Tab tab)
    {
        var gate = OneAtATime.GetValue(tab, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TurnWait))
            throw new InputFailedException("another click or upload in that tab is still under way; try again in a moment", busy: true);
        return new Turn(gate);
    }

    private sealed class Turn(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }

    /// <summary>
    /// How long one protocol call may take. The engine answers input once the page has
    /// taken it, and a page held by a modal dialog in the person's window never does: the
    /// call would wait for as long as the dialog is up.
    /// </summary>
    internal static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(5);

    private static CoreWebView2 CoreOf(Tab tab)
    {
        tab.RefuseOwnPage();
        return tab.Core ?? throw new InputFailedException("that tab has no page to act on");
    }

    private static async Task<string> CallAsync(CoreWebView2 core, string method, object parameters)
    {
        try
        {
            var budget = BudgetFor(ActionDeadline.Value, DateTime.UtcNow);
            if (budget <= TimeSpan.Zero)
                throw new InputFailedException($"the action ran out of time before {method}", timedOut: true);
            var call = core.CallDevToolsProtocolMethodAsync(method, TrustedInput.Json(parameters));
            using var giveUp = new CancellationTokenSource();
            if (await Task.WhenAny(call, Task.Delay(budget, giveUp.Token)) != call)
            {
                _ = call.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                throw new InputFailedException($"the page did not take {method} within {budget.TotalSeconds:0.#} seconds", timedOut: true);
            }
            giveUp.Cancel();
            return await call;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            DebugLog.Write($"{method} failed: {ex.Message}");
            throw new InputFailedException($"the engine refused {method}: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a click's events in order. A failure once the button has gone down is passed on
    /// as one that may have landed: the page may have acted on the press already.
    /// </summary>
    private static async Task SendClickAsync(CoreWebView2 core, double x, double y, int clickCount)
    {
        var events = TrustedInput.ClickEvents(x, y, clickCount);
        for (int i = 0; i < events.Count; i++)
        {
            try
            {
                await CallAsync(core, "Input.dispatchMouseEvent", events[i]);
            }
            catch (InputFailedException failed) when (i > 0)
            {
                // The button may be down in the page: let it go, or the next hover is a drag.
                // Not awaited and not checked: a page that did not take the press may not
                // take this either, and the agent is already being told what happened.
                ReleaseQuietly(core, events[^1]);
                throw new InputFailedException(failed.Message, mayHaveLanded: true, timedOut: failed.TimedOut);
            }
        }
    }

    private static void ReleaseQuietly(CoreWebView2 core, object release)
    {
        try
        {
            _ = core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", TrustedInput.Json(release))
                .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            DebugLog.Write($"a held mouse button could not be released: {ex.Message}");
        }
    }

    /// <summary>
    /// Clicks at a point as a person would, with any file chooser the click opens caught
    /// rather than shown: a native file dialog from a hidden agent window would land on the
    /// person's screen, modal, over whatever they were doing. True when a chooser was
    /// caught, so the caller can say to use the upload tool instead.
    /// </summary>
    internal static async Task<bool> ClickAsync(Tab tab, double x, double y, int clickCount = 1)
    {
        var core = CoreOf(tab);
        using var turn = await TakeTurnAsync(tab);
        BeginAction();
        using var chooser = await ChooserWatch.StartAsync(core);
        tab.RefuseOwnPage();   // again: the watch awaited, and the page may have moved
        await SendClickAsync(core, x, y, clickCount);
        // A chooser opened by script just after the click (a handler that awaits first)
        // still has a moment to be caught before interception goes off again.
        return await chooser.OpenedWithinAsync(TimeSpan.FromMilliseconds(400)) is not null;
    }

    /// <summary>Types text into whatever has the focus, as the keyboard would: beforeinput and input fire as for a person.</summary>
    /// <remarks>A timeout is passed on as one that may have landed: the text may be in the
    /// field already, and setting it again another way would type it twice.</remarks>
    internal static async Task InsertTextAsync(Tab tab, string text)
    {
        try
        {
            await CallAsync(CoreOf(tab), "Input.insertText", new { text });
        }
        catch (InputFailedException failed) when (failed.TimedOut)
        {
            throw new InputFailedException(failed.Message, mayHaveLanded: true, timedOut: true);
        }
    }

    /// <summary>Presses and releases a key chord in whatever has the focus.</summary>
    internal static async Task PressAsync(Tab tab, TrustedInput.Chord chord, int times = 1)
    {
        var core = CoreOf(tab);
        BeginAction();
        for (int i = 0; i < times; i++)
            foreach (var e in TrustedInput.KeyEvents(chord))
                await CallAsync(core, "Input.dispatchKeyEvent", e);
    }

    /// <summary>Moves the pointer to a point and leaves it there.</summary>
    internal static async Task HoverAsync(Tab tab, double x, double y)
        => await CallAsync(CoreOf(tab), "Input.dispatchMouseEvent", TrustedInput.HoverEvent(x, y));

    /// <summary>Turns the wheel over a point.</summary>
    internal static async Task WheelAsync(Tab tab, double x, double y, double deltaX, double deltaY)
        => await CallAsync(CoreOf(tab), "Input.dispatchMouseEvent", TrustedInput.WheelEvent(x, y, deltaX, deltaY));

    /// <summary>
    /// Gives files to the file input a selector names, as choosing them in the dialog would:
    /// the input's change event fires and the page reads them as it would the person's.
    /// False when nothing matches the selector.
    /// </summary>
    internal static async Task<bool> SetFilesAsync(Tab tab, string selector, IReadOnlyList<string> files)
    {
        var core = CoreOf(tab);
        using var turn = await TakeTurnAsync(tab);
        BeginAction();
        using var doc = JsonDocument.Parse(await CallAsync(core, "DOM.getDocument", new { depth = 0 }));
        int root = doc.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
        using var found = JsonDocument.Parse(await CallAsync(core, "DOM.querySelector", new { nodeId = root, selector }));
        int node = found.RootElement.GetProperty("nodeId").GetInt32();
        if (node == 0)
            return false;
        tab.RefuseOwnPage();   // again, just before the files go in
        await CallAsync(core, "DOM.setFileInputFiles", new { files, nodeId = node });
        return true;
    }

    /// <summary>
    /// Clicks a button that opens a file chooser and answers the chooser with files, for an
    /// upload behind a button rather than a visible input. False when no chooser opened
    /// within the wait, which usually means the click did not reach the button.
    /// </summary>
    internal static async Task<bool> UploadThroughChooserAsync(Tab tab, double x, double y, IReadOnlyList<string> files, TimeSpan wait)
    {
        var core = CoreOf(tab);
        using var turn = await TakeTurnAsync(tab);
        BeginAction();
        using var chooser = await ChooserWatch.StartAsync(core);
        tab.RefuseOwnPage();   // again: the watch awaited, and the page may have moved
        await SendClickAsync(core, x, y, 1);
        if (await chooser.OpenedWithinAsync(wait) is not { } backendNodeId)
            return false;
        tab.RefuseOwnPage();
        await CallAsync(core, "DOM.setFileInputFiles", new { files, backendNodeId });
        return true;
    }

    /// <summary>
    /// File choosers caught for as long as this lives: the engine is told to report them
    /// rather than show the dialog, and goes back to showing them when this is disposed,
    /// so the person's own uploads in that tab afterwards work as ever.
    /// </summary>
    private sealed class ChooserWatch : IDisposable
    {
        private readonly CoreWebView2 _core;
        private readonly CoreWebView2DevToolsProtocolEventReceiver _receiver;
        private readonly TaskCompletionSource<int> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ChooserWatch(CoreWebView2 core)
        {
            _core = core;
            _receiver = core.GetDevToolsProtocolEventReceiver("Page.fileChooserOpened");
            _receiver.DevToolsProtocolEventReceived += OnOpened;
        }

        public static async Task<ChooserWatch> StartAsync(CoreWebView2 core)
        {
            var watch = new ChooserWatch(core);
            try
            {
                await CallAsync(core, "Page.enable", new { });
                await CallAsync(core, "Page.setInterceptFileChooserDialog", new { enabled = true });
            }
            catch
            {
                watch.Dispose();
                throw;
            }
            return watch;
        }

        private void OnOpened(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                _opened.TrySetResult(doc.RootElement.GetProperty("backendNodeId").GetInt32());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                DebugLog.Write($"a file chooser was reported in a shape not understood: {ex.Message}");
            }
        }

        /// <summary>The input the chooser was for, if one opened within the wait.</summary>
        public async Task<int?> OpenedWithinAsync(TimeSpan wait)
        {
            var (id, inTime) = await Tab.WithBudget(_opened.Task, wait, 0);
            return inTime ? id : null;
        }

        public void Dispose()
        {
            _receiver.DevToolsProtocolEventReceived -= OnOpened;
            // Not awaited: the person's next chooser is seconds away at the soonest, and the
            // engine runs protocol calls in order, so this lands before anything after it.
            try
            {
                _ = _core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", TrustedInput.Json(new { enabled = false }))
                    .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The view is gone, and with it the interception.
            }
        }
    }
}
