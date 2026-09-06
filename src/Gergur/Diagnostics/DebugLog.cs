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
        WriteAlways(message);
    }

    /// <summary>
    /// Writes whether or not GERGUR_DEBUG is set, for the few failures a user needs a
    /// record of in a normal run: a security control that could not be applied, or a
    /// service that did not start. Ordinary tracing still goes through <see cref="Write"/>,
    /// which stays off by default. A failure nobody can see is not a reported failure.
    /// </summary>
    public static void WriteAlways(string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
