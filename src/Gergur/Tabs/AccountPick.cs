using System.Runtime.InteropServices;
using Gergur.UI;

namespace Gergur.Tabs;

/// <summary>
/// Chooses one of the person's saved accounts in an agent's page, the way a person does: click
/// the username field, type the account, take it from the engine's list. The engine shows that
/// list only to a page holding the real keyboard focus (focus emulation over the protocol does
/// not do), and every Gergur window shares one UI thread, so for about a second the agent's page
/// has this app's keyboard. The person agreed to that while they are in Gergur. Around it:
///
/// - never from another app: if giving the page the focus brings Gergur to the front over the
///   app the person is in (Windows allows it after a while without input, and to a process that
///   app started), it is given straight back and the pick stops (<see cref="Outcome.Interrupted"/>).
///   Measured in a test instance: the person's typing in another app went into the page;
/// - a hidden window is moved off every screen first, since the list is a window of the engine's
///   own that is not cloaked and draws beside the field;
/// - no key goes in unless the list is there (<see cref="Outcome.NoList"/>), and while they do,
///   the page's own Enter and submit are held off: without the list, Enter in the field sent the
///   form, measured;
/// - the foreground window, this thread's active window and its focus are given back after,
///   whatever happened;
/// - one pick at a time across the app (<see cref="Turn"/>), as two would fight over the focus.
///
/// Measured on WebView2 156 in a scratch profile before this was written: the list showed only
/// with focus, typing the account narrowed it to that account, ArrowDown and Enter filled it,
/// about 1.1 s with the focus held.
/// </summary>
internal static class AccountPick
{
    /// <summary>One pick at a time, app wide.</summary>
    internal static readonly SemaphoreSlim Turn = new(1, 1);

    internal enum Outcome
    {
        /// <summary>The list was there and its first entry taken; the caller reads what came of it.</summary>
        Done,
        /// <summary>No list came up for what was typed (nothing saved under it): no key was pressed.</summary>
        NoList,
        /// <summary>The person was in another app and Gergur would have taken the keyboard from it: stopped, given back.</summary>
        Interrupted,
    }

    /// <summary>
    /// Clicks the field at the point (page pixels), types the account, takes the first entry of
    /// the list it narrows to, and gives everything back. UI thread. Whether the right account
    /// came out is the caller's to read from the page.
    /// </summary>
    internal static async Task<Outcome> RunAsync(MainForm window, Tab tab, double x, double y, string account, Func<Task<bool>> chosen)
    {
        IntPtr foreground = GetForegroundWindow(), active = GetActiveWindow(), focus = GetFocus();
        bool gergurInFront = IsOurs(foreground);
        bool park = window.HiddenForAgent;
        RECT was = default;
        if (park && GetWindowRect(window.Handle, out was))
        {
            var spot = Parked(Rectangle.FromLTRB(was.Left, was.Top, was.Right, was.Bottom), SystemInformation.VirtualScreen);
            SetWindowPos(window.Handle, IntPtr.Zero, spot.X, spot.Y, 0, 0, NoSizeNoZOrderNoActivate);
        }
        else
        {
            park = false;
        }
        try
        {
            // Before the list can open: running a script once it is up may close it. Taken off
            // in the finally whatever this answers: a script that ran and then timed out would
            // otherwise leave every later submit on the page held off, the person's included.
            await tab.ReadScriptAsync(GuardJs, TimeSpan.FromSeconds(2));
            tab.FocusView();
            if (TookTheFront(gergurInFront, IsOurs(GetForegroundWindow())))
                return Outcome.Interrupted;
            await TabInput.ClickAsync(tab, x, y);
            await Task.Delay(150);
            if (TookTheFront(gergurInFront, IsOurs(GetForegroundWindow())))
                return Outcome.Interrupted;
            await TabInput.InsertTextAsync(tab, account);
            await Task.Delay(350);   // for the list to narrow to what was typed
            if (!ListShown(tab, window.Handle))
                return Outcome.NoList;
            await TabInput.PressAsync(tab, TrustedInput.ParseChord("ArrowDown")!);
            // The list sometimes ignores an Enter that comes soon after it shows or narrows:
            // measured, the list still up 1.5 s after an Enter pressed about 470 ms after the
            // typing, in three picks of fifteen. So Enter waits a little, and is pressed again
            // while the list is still there and nothing was chosen. Never with the list gone.
            await Task.Delay(400);
            for (int attempt = 0; attempt < EnterAttempts; attempt++)
            {
                if (!ListShown(tab, window.Handle))
                    break;
                await TabInput.PressAsync(tab, ListEnter);
                for (int i = 0; i < 4; i++)
                {
                    await Task.Delay(125);   // the engine fills both fields a moment later
                    if (await chosen())
                        return Outcome.Done;
                }
            }
            return Outcome.Done;
        }
        finally
        {
            // Given back first, with nothing awaited before it: a script that throws (the page
            // gone, the view disposed) must not leave the person's focus taken or the window parked.
            GiveBack(window.Handle, foreground, active, focus);
            if (park)
                SetWindowPos(window.Handle, IntPtr.Zero, was.Left, was.Top, 0, 0, NoSizeNoZOrderNoActivate);
            try
            {
                await tab.ReadScriptAsync(UnguardJs, TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                // A page that went away took its guard with it.
                Diagnostics.DebugLog.WriteAlways($"the submit guard could not be taken off after choosing an account: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Enter as the list hears it: a key that types nothing, so it goes as a rawKeyDown. The
    /// usual Enter types "\r" and goes as a keyDown, which the list passes over to the page
    /// (measured: the list stayed up, the page got Enter and tried to send the form).
    /// </summary>
    internal static readonly TrustedInput.Chord ListEnter = new(new TrustedInput.Key("Enter", "Enter", 13, Text: null), 0);

    /// <summary>How many times Enter is pressed while the list stays up with nothing chosen.</summary>
    internal const int EnterAttempts = 3;

    /// <summary>
    /// Whether giving the page the focus brought Gergur to the front over another app: the
    /// person's keyboard was there, and is not Gergur's to borrow. Gergur already in front is the
    /// borrow agreed to; the front moving to some other app (the person switching) is not ours.
    /// </summary>
    internal static bool TookTheFront(bool gergurInFront, bool gergurInFrontNow)
        => !gergurInFront && gergurInFrontNow;

    /// <summary>
    /// Where to put a window so nothing of it, nor a list the engine draws beside a field in it,
    /// lands on any screen: wholly left of the leftmost one, with room to spare.
    /// </summary>
    internal static Point Parked(Rectangle window, Rectangle virtualScreen)
        => new(virtualScreen.Left - window.Width - 4000, virtualScreen.Top);

    /// <summary>Whether a list rect is the one for this window: beside it, the way the engine draws a list under a field.</summary>
    internal static bool Beside(Rectangle list, Rectangle window)
        => list.Width > 0 && list.Height > 0 && Rectangle.Inflate(window, 600, 600).IntersectsWith(list);

    // The engine's list: a shown top-level window of its browser process, owned by this window
    // and beside it. Owned, so a list or popup of the person's own window is never taken for it
    // (the window is not always parked: an agent window the person was shown is not). Cloaked or
    // not: one this hidden window owns is cloaked with it by the window manager (inherited),
    // which is also why it never shows.
    private static bool ListShown(Tab tab, IntPtr windowHandle)
    {
        uint browser;
        try
        {
            browser = tab.Core?.BrowserProcessId ?? 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return false;
        }
        if (browser == 0 || !GetWindowRect(windowHandle, out var w))
            return false;
        var around = Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom);
        bool found = false;
        var seen = new List<string>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == browser && IsWindowVisible(h) && GetWindowRect(h, out var r))
            {
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                bool owned = GetAncestor(h, RootOwner) == windowHandle;
                seen.Add(rect + (owned ? " owned" : ""));
                if (owned && Beside(rect, around))
                    found = true;
            }
            return true;
        }, IntPtr.Zero);
        Diagnostics.DebugLog.Write($"account pick: window {around}, focus {GetFocus()}, active {GetActiveWindow()}, engine windows shown: {string.Join(" ", seen)}; list {(found ? "found" : "not found")}");
        return found;
    }

    private static bool IsOurs(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // Holds off the form being sent while the keys go to the list. Only the submit, not the Enter
    // key: the engine takes the list's choice through the page's own keydown, so holding the key
    // off held the choice off too (measured). Without the list, Enter in the field sends the form,
    // which this stops; a page that signs in from its own Enter handler is not stopped, and is
    // why no key goes in unless the list is up.
    internal const string GuardJs = """
        (() => {
            if (window.__gergurPickGuard) return true;
            const stop = e => { e.preventDefault(); e.stopImmediatePropagation(); };
            window.__gergurPickGuard = stop;
            window.addEventListener('submit', stop, true);
            return true;
        })()
        """;

    internal const string UnguardJs = """
        (() => {
            const stop = window.__gergurPickGuard;
            if (stop) window.removeEventListener('submit', stop, true);
            delete window.__gergurPickGuard;
            return true;
        })()
        """;

    /// <summary>
    /// Whether to put the foreground back: only when the pick's own window (or something it
    /// owns, such as the list) holds it, which is what the pick did. The person moving to
    /// another app or window during the pick is theirs and is left alone; measured in the live
    /// test, the person went from Gergur to a console mid-pick, and putting the front back would
    /// have pulled them out of it.
    /// </summary>
    internal static bool GiveFrontBack(IntPtr was, IntPtr now, IntPtr nowRootOwner, IntPtr pickWindow)
        => was != IntPtr.Zero && now != was && nowRootOwner == pickWindow;

    private static void GiveBack(IntPtr pickWindow, IntPtr foreground, IntPtr active, IntPtr focus)
    {
        try
        {
            if (active != IntPtr.Zero && IsWindow(active))
                SetActiveWindow(active);
            if (Control.FromHandle(focus) is { IsDisposed: false } control)
                control.Focus();
            var now = GetForegroundWindow();
            if (IsWindow(foreground) && GiveFrontBack(foreground, now, GetAncestor(now, RootOwner), pickWindow))
                SetForegroundWindow(foreground);
        }
        catch (Exception ex)
        {
            // Never thrown on: the pick's own outcome is what the caller reports.
            Diagnostics.DebugLog.WriteAlways($"the focus could not be given back after choosing an account: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private const uint NoSizeNoZOrderNoActivate = 0x0001 | 0x0004 | 0x0010;
    private const uint RootOwner = 3;   // GA_ROOTOWNER

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
