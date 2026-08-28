using System.Diagnostics;
using System.Runtime.InteropServices;
using Gergur.UI;

namespace Gergur;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [STAThread]
    private static void Main(string[] args)
    {
        var argList = args.ToList();
        // Settings that require new engine flags restart via "exe --wait-restart <oldPid>":
        // wait for the old instance AND its engine to die, or the flags silently no-op.
        int waitIndex = argList.IndexOf("--wait-restart");
        if (waitIndex >= 0 && waitIndex + 1 < argList.Count && int.TryParse(argList[waitIndex + 1], out int oldPid))
        {
            argList.RemoveRange(waitIndex, 2);
            try { Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }
            Thread.Sleep(3000); // grace for the shared browser process to exit
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\Gergur.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Single instance: surface the running window instead of silently exiting.
            FocusRunningInstance();
            return;
        }

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // SetColorMode is marked experimental
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        Application.Run(new MainForm(argList.FirstOrDefault()));
    }

    private static void FocusRunningInstance()
    {
        try
        {
            var other = Process.GetProcessesByName("Gergur")
                .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other is not null)
            {
                ShowWindow(other.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(other.MainWindowHandle);
            }
        }
        catch
        {
            // Focus stealing is best-effort; never block exit on it.
        }
    }
}
