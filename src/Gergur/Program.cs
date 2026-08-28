using Gergur.UI;

namespace Gergur;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Single instance: a second launch just exits (personal browser, keep it simple).
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Gergur.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
            return;

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // SetColorMode is marked experimental
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
