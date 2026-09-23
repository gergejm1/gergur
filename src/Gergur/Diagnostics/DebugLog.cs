using Gergur.App;

namespace Gergur.Diagnostics;

/// <summary>
/// The trace log. <see cref="Write"/> is enabled by GERGUR_DEBUG=1 and a no-op otherwise;
/// <see cref="WriteAlways"/> is for failures and always writes.
/// </summary>
public static class DebugLog
{
    /// <summary>
    /// Where the log goes. Settable so the test assembly can send it somewhere of its own:
    /// tests fail builds and writes on purpose, and each one landed in the real log as a
    /// failure indistinguishable from a real one, in the file the error answers point at.
    /// </summary>
    internal static string FilePath { get; set; } = Path.Combine(Settings.DataDir, "debug.log");
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("GERGUR_DEBUG") == "1";

    /// <summary>
    /// Past this the log starts over, keeping the previous one beside it as debug.log.old.
    /// Writing whether or not tracing is on needs a ceiling: an agent retrying a request
    /// that keeps failing writes a stack trace every time.
    /// </summary>
    internal const long MaxBytes = 4 * 1024 * 1024;

    // One writer at a time. Request threads log concurrently, and a second append while
    // the first holds the file fails with a sharing violation that WriteAlways swallows,
    // so the line explaining a failure was the one most likely to go missing.
    private static readonly object Gate = new();

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
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Append(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}", MaxBytes);
        }
        catch { }
    }

    /// <summary>The file handling on its own, so a test can drive it without the real profile.</summary>
    internal static void Append(string path, string line, long maxBytes)
    {
        lock (Gate)
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length >= maxBytes)
            {
                // Something holding the log open, which is what tailing it looks like, stops
                // it being moved. Keep appending past the cap rather than lose every line
                // from then on, exactly while somebody is watching; the next write after
                // they stop rolls it over.
                try { File.Move(path, path + ".old", overwrite: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            File.AppendAllText(path, line);
        }
    }
}
