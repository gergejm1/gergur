using System.Runtime.InteropServices;

namespace Gergur.UI;

/// <summary>
/// The Win32 half of hiding an agent's window again, apart from MainForm so it can be run
/// against real windows on a hidden desktop: telling the system's Close from any other
/// close, and putting a window at the back of the stack.
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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
