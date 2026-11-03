using System.Runtime.InteropServices;

namespace Gergur.UI;

/// <summary>
/// The Win32 half of hiding an agent's window, apart from MainForm so it can be run against
/// real windows: telling the system's Close from any other close, putting a window at the
/// back of the stack, and hiding it from the person altogether.
/// </summary>
internal static class WindowStacking
{
    internal const int WM_SYSCOMMAND = 0x0112;
    internal const int SC_CLOSE = 0xF060;
    internal static readonly IntPtr HWND_BOTTOM = new(1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Whether a message is the system's Close: Alt+F4, the taskbar's Close, the system
    /// menu's. The low four bits of the command are the system's own. A quit, or a window
    /// closed from code, is WM_CLOSE and never this.
    /// </summary>
    internal static bool IsSystemClose(int msg, IntPtr wParam)
        => msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_CLOSE;

    /// <summary>
    /// Puts <paramref name="window"/> at the back, with <paramref name="theirs"/> brought to
    /// the front first when there is one, so the person is left in their own window rather
    /// than in one now buried under everything. False if the system refused the move.
    /// </summary>
    internal static bool SendBack(IntPtr window, Form? theirs)
    {
        theirs?.Activate();
        return SetWindowPos(window, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Hides a window from the person entirely, or shows it again: cloaked by the window
    /// manager where it stands (what Windows does to a window on another virtual desktop:
    /// not drawn, not hit, not in Alt+Tab) and its taskbar button taken away.
    ///
    /// Cloaked rather than moved off screen or hidden. A hidden window's page goes hidden to
    /// itself and stops drawing, which an agent's screenshots and clicks need. Off screen
    /// worked too, but parking it means a monitor of another scale, and moving a window
    /// between scales is how its size drifted before. Measured on WebView2: a cloaked page
    /// stays "visible" to itself, keeps drawing frames, takes trusted clicks and captures.
    /// False if either half was refused; both are tried regardless.
    /// </summary>
    internal static bool SetHidden(IntPtr window, bool hidden)
    {
        bool cloaked = Cloak(window, hidden);
        bool taskbar = SetTaskbarButton(window, shown: !hidden);
        return cloaked && taskbar;
    }

    /// <summary>Cloaks or uncloaks a window where it stands. Can be done before it is first shown, so it never flashes.</summary>
    internal static bool Cloak(IntPtr window, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(window, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
    }

    /// <summary>Whether the window manager has this window cloaked, by us or by a virtual desktop.</summary>
    internal static bool IsCloaked(IntPtr window)
        => DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const int DWMWA_CLOAK = 13;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    /// <summary>
    /// Adds or takes away a window's taskbar button. Through the shell's own list rather
    /// than by changing the window's style, which the taskbar only notices when a window is
    /// shown and which would change the frame of a window whose title bar is drawn by hand.
    /// UI thread only: the shell object is apartment threaded.
    /// </summary>
    internal static bool SetTaskbarButton(IntPtr window, bool shown)
    {
        try
        {
            _taskbar ??= CreateTaskbarList();
            if (_taskbar is null)
                return false;
            int hr = shown ? _taskbar.AddTab(window) : _taskbar.DeleteTab(window);
            return hr == 0;
        }
        catch (COMException ex)
        {
            Diagnostics.DebugLog.WriteAlways($"taskbar button not {(shown ? "added" : "removed")}: {ex.Message}");
            return false;
        }
    }

    // Per thread: the shell object belongs to the apartment that made it, and a call into it
    // from another thread is marshalled to that one, which hangs for good once it has ended.
    // The app has one UI thread; tests make a thread per window.
    [ThreadStatic]
    private static ITaskbarList? _taskbar;

    private static ITaskbarList? CreateTaskbarList()
    {
        var type = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
        if (type is null || Activator.CreateInstance(type) is not ITaskbarList list)
            return null;
        return list.HrInit() == 0 ? list : null;
    }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr window);
        [PreserveSig] int DeleteTab(IntPtr window);
        [PreserveSig] int ActivateTab(IntPtr window);
        [PreserveSig] int SetActiveAlt(IntPtr window);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
