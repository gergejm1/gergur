namespace Gergur.Blocking;

/// <summary>
/// Loads the page-level cleanup script (cosmetic filtering + YouTube ad
/// neutralizer) injected into every document at creation time.
///
/// Read again whenever the file changes, rather than once per run. Anything in
/// <c>adblock.js</c> has to be tried against a real ad in the real browser, several times
/// over (see CLAUDE.md), and with the script read once at startup every attempt cost a
/// restart of the browser somebody was using. Now a changed file is picked up by the next
/// view a tab builds: a new tab or window, or a tab woken from being discarded. Pages
/// already open keep the script they were given, since it runs as a document is created.
/// </summary>
public static class PageCleanup
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "Assets", "adblock.js");
    private static readonly object Gate = new();
    private static Snapshot _last;

    /// <summary>The script as the file stands now, or null when there has never been one.</summary>
    public static string? Script
    {
        get
        {
            lock (Gate)
                return (_last = Read(FilePath, _last)).Text;
        }
    }

    /// <summary>
    /// The line in adblock.js the relaxed sites are written into. A placeholder inside the
    /// script's own closure rather than a global set beside it, so a page cannot read which
    /// sites the person has relaxed.
    /// </summary>
    internal const string RelaxedPlaceholder = "var RELAXED_SITES = [];";

    /// <summary>The script for these relaxed sites, or null when there has never been one. See <see cref="Compose"/>.</summary>
    public static string? ScriptFor(IReadOnlyList<string> relaxed) => Script is { } script ? Compose(script, relaxed) : null;

    /// <summary>
    /// <paramref name="script"/> with <paramref name="relaxed"/> written over its placeholder
    /// line, as a JSON array of plain names. A script without the line (an older copy being
    /// tried by hot reload) is returned as it is, and relaxes nothing.
    /// </summary>
    internal static string Compose(string script, IReadOnlyList<string> relaxed)
    {
        if (relaxed.Count == 0)
            return script;
        int at = script.IndexOf(RelaxedPlaceholder, StringComparison.Ordinal);
        if (at < 0)
            return script;
        // Each name has been through RelaxedSites.Clean, so it is letters, digits, dots and
        // dashes; serialising anyway means nothing in a hand-edited settings file can
        // break out of the string.
        string list = System.Text.Json.JsonSerializer.Serialize(relaxed);
        return string.Concat(script.AsSpan(0, at), $"var RELAXED_SITES = {list};", script.AsSpan(at + RelaxedPlaceholder.Length));
    }

    /// <summary>What was last read, and the file's stamp when it was.</summary>
    internal readonly record struct Snapshot(DateTime StampUtc, long Length, string? Text);

    /// <summary>
    /// The file's script, reusing <paramref name="last"/> when the file has not changed.
    ///
    /// A file that cannot be read keeps the last good script rather than losing the cleanup
    /// for every tab built until somebody notices: an editor saving over it holds it for a
    /// moment, and a half-written read is worse than a stale one. Length as well as the
    /// stamp, because two saves inside the file system's timestamp resolution read the same.
    /// </summary>
    internal static Snapshot Read(string path, Snapshot last)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return last;
            if (last.Text is not null && info.LastWriteTimeUtc == last.StampUtc && info.Length == last.Length)
                return last;
            string text = File.ReadAllText(path);
            return new Snapshot(info.LastWriteTimeUtc, info.Length, text);
        }
        catch
        {
            return last;
        }
    }
}
