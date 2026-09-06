using Gergur.App;

namespace Gergur.Diagnostics;

/// <summary>Dev-time trace log. Enabled by GERGUR_DEBUG=1; no-op otherwise.</summary>
public static class DebugLog
{
    private static readonly string FilePath = Path.Combine(Settings.DataDir, "debug.log");
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("GERGUR_DEBUG") == "1";

    public static void Write(string message)
    {
        if (!Enabled)
            return;
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
