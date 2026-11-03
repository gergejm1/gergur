using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Gergur.Blocking;

/// <summary>
/// uBlock Origin Lite, the Manifest V3 uBlock, run inside the engine as a browser extension and
/// kept up to date from its own releases.
///
/// Why: YouTube decides per session whether a browser blocks ads, and once it has, it refuses
/// every video a playlist fetches in the page ("Ad blockers violate YouTube's Terms of
/// Service"), while a reload of one song still plays. uBlock's maintainers answer YouTube's
/// changes within days (its rules ask the player for a video in a form YouTube serves without
/// ads, and ask again when refused), which no script kept here can match. Its rules are built
/// into each release, so staying current means taking each release.
///
/// Where: <c>extensions\ubol\current</c> in the data folder, always that one path, so the
/// engine gives it the same extension id each time and keeps its settings. An update is
/// downloaded into <c>pending</c> and swapped in at the next start, before the engine runs
/// (<see cref="PromotePending"/>), since the files of a loaded extension are in use.
///
/// Trust: the release comes from github.com/uBlockOrigin/uBOL-home over HTTPS, the address
/// built here from the release's tag, never taken from the answer. Releases are not signed,
/// so this is the same trust as installing it from that page by hand. The archive is unpacked
/// only inside its folder and only when its manifest is uBO Lite's, at that version.
/// </summary>
public static class UBlockLite
{
    /// <summary>The folder every uBO Lite file lives under.</summary>
    internal static string Root => Path.Combine(Settings.DataDir, "extensions", "ubol");

    internal static string CurrentFolder => Path.Combine(Root, "current");

    internal static string PendingFolder => Path.Combine(Root, "pending");

    /// <summary>Written into a folder last, once every file is in it: a folder without it is half unpacked.</summary>
    internal const string CompleteMarker = ".gergur-complete";

    internal const string LatestReleaseApi = "https://api.github.com/repos/uBlockOrigin/uBOL-home/releases/latest";

    /// <summary>Checked after start and then this often. uBO Lite releases about daily; GitHub allows 60 unauthenticated calls an hour.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private const long MaxDownloadBytes = 64L * 1024 * 1024;
    private const long MaxUnpackedBytes = 256L * 1024 * 1024;
    private const int MaxEntries = 10_000;

    private static readonly Regex Tag = new(@"^\d{4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);

    /// <summary>The engine's id for it this run, once loaded; null while it is not.</summary>
    public static string? LoadedId { get; private set; }

    /// <summary>The version loaded this run, for the settings and the log.</summary>
    public static string? LoadedVersion { get; private set; }

    /// <summary>Raised on the thread that finished it, when a release has been downloaded. The text says what happens next.</summary>
    public static event Action<string>? Updated;

    // ------------------------------------------------------------------ versions and manifests

    /// <summary>Whether <paramref name="tag"/> is a uBO Lite release tag (2026.1103.1743): digits only, so it can name a path and a url.</summary>
    internal static bool IsTag(string? tag) => tag is not null && Tag.IsMatch(tag);

    /// <summary>Compares two release tags part by part as numbers. Null or malformed sorts first.</summary>
    internal static int CompareTags(string? a, string? b)
    {
        int[]? A = IsTag(a) ? a!.Split('.').Select(int.Parse).ToArray() : null;
        int[]? B = IsTag(b) ? b!.Split('.').Select(int.Parse).ToArray() : null;
        if (A is null || B is null)
            return (A is null ? 0 : 1) - (B is null ? 0 : 1);
        for (int i = 0; i < 3; i++)
            if (A[i] != B[i])
                return A[i].CompareTo(B[i]);
        return 0;
    }

    /// <summary>The download address for a tag, built here: never one taken from an answer.</summary>
    internal static string DownloadUrl(string tag)
        => IsTag(tag)
            ? $"https://github.com/uBlockOrigin/uBOL-home/releases/download/{tag}/uBOLite_{tag}.chromium.zip"
            : throw new ArgumentException("not a uBO Lite release tag", nameof(tag));

    /// <summary>The tag of the latest release in GitHub's answer, or null.</summary>
    internal static string? TagFrom(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("tag_name", out var t) || t.ValueKind != JsonValueKind.String
                || !IsTag(t.GetString()))
                return null;
            string tag = t.GetString()!;
            // Tags are dated. One from years ahead would sort above every real release for
            // good, and since an older one is never taken, updates would stop.
            return int.Parse(tag.AsSpan(0, 4)) <= DateTime.UtcNow.Year + 1 ? tag : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The version in a manifest, when it is uBO Lite's: Manifest V3, its short name, its
    /// author. Anything else (another extension, a broken file) is null.
    /// </summary>
    internal static string? VersionFromManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object)
                return null;
            string? S(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool v3 = m.TryGetProperty("manifest_version", out var mv) && mv.ValueKind == JsonValueKind.Number && mv.GetInt32() == 3;
            // Never one that updates itself from elsewhere, takes messages from web pages, or
            // pins its own id: uBO Lite has none of these, and each would reach past this updater.
            if (m.TryGetProperty("update_url", out _) || m.TryGetProperty("externally_connectable", out _) || m.TryGetProperty("key", out _))
                return null;
            return v3 && S("short_name") == "uBO Lite" && S("author") == "Raymond Hill" && IsTag(S("version")) ? S("version") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The uBO Lite version unpacked in <paramref name="folder"/>, or null when it is not a whole one.</summary>
    internal static string? VersionIn(string folder)
    {
        try
        {
            if (!File.Exists(Path.Combine(folder, CompleteMarker)))
                return null;
            string manifest = Path.Combine(folder, "manifest.json");
            return File.Exists(manifest) ? VersionFromManifest(File.ReadAllText(manifest)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ unpacking

    /// <summary>
    /// Unpacks <paramref name="zip"/> into <paramref name="into"/> (emptied first) and marks it
    /// whole, or throws and leaves no marker. Every entry must land inside the folder, and the
    /// archive is bounded in entries and size, so a damaged or hostile archive cannot write
    /// elsewhere or fill the disk.
    /// </summary>
    internal static void Unpack(string zip, string into, long maxBytes = MaxUnpackedBytes, int maxEntries = MaxEntries)
    {
        if (Directory.Exists(into))
            Directory.Delete(into, recursive: true);
        Directory.CreateDirectory(into);
        string root = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > maxEntries)
            throw new InvalidDataException("too many entries");
        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            bool folder = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            string target = Path.GetFullPath(Path.Combine(into, entry.FullName));
            // A folder entry may be the root itself ("./"), which has no separator after it.
            string compared = folder ? target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar : target;
            if (!compared.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (!folder && compared.Length == root.Length))
                throw new InvalidDataException("an entry outside its folder");
            if (folder)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Counted as it is written, not by what the archive says each entry holds: a
            // hostile archive can declare a small size and inflate far past it.
            using var from = entry.Open();
            using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            int read;
            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidDataException("too large unpacked");
                to.Write(buffer, 0, read);
            }
        }
        File.WriteAllText(Path.Combine(into, CompleteMarker), DateTime.UtcNow.ToString("O"));
    }

    // ------------------------------------------------------------------ at start

    /// <summary>
    /// Before the engine starts: a whole update waiting in pending, newer than what is
    /// current, becomes current. Anything half there is cleared. Never throws: browsing goes
    /// on with whatever is current.
    /// </summary>
    public static void PromotePending() => PromotePending(CurrentFolder, PendingFolder);

    internal static void PromotePending(string current, string pending)
    {
        string old = current + ".old";
        try
        {
            if (!Directory.Exists(pending))
                return;
            string? waiting = VersionIn(pending);
            if (waiting is null || CompareTags(waiting, VersionIn(current)) <= 0)
            {
                Directory.Delete(pending, recursive: true);
                return;
            }
            if (Directory.Exists(old))
                Directory.Delete(old, recursive: true);
            if (Directory.Exists(current))
                Directory.Move(current, old);
            Directory.Move(pending, current);
            DebugLog.WriteAlways($"uBO Lite {waiting} put in place");
            try { Directory.Delete(old, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"uBO Lite update could not be put in place: {ex.Message}");
            // The copy that worked goes back, rather than this run going without one.
            try
            {
                if (!Directory.Exists(current) && Directory.Exists(old))
                    Directory.Move(old, current);
            }
            catch (Exception back)
            {
                DebugLog.WriteAlways($"uBO Lite: the previous copy could not be put back either: {back.Message}");
            }
        }
    }

    private static readonly object LoadGate = new();
    private static Task? _loading;
    private static bool _nothingToLoad;

    /// <summary>
    /// Raised, off the UI thread, when a load that ran past its wait finished after all: tabs
    /// built meanwhile registered Gergur's own YouTube code and are to hand YouTube over.
    /// </summary>
    public static event Action? LoadedLate;

    /// <summary>
    /// Before a page: loads the current uBO Lite into the profile if the engine does not
    /// already have it, and records its id. Once per run, and again only when the last try
    /// found nothing downloaded and there is a copy now (a first download that arrived with
    /// no tab open to load it into). Bounded, and never throws: the caller is opening a window.
    /// </summary>
    public static Task EnsureLoadedOnceAsync(CoreWebView2Profile profile, Settings settings)
    {
        if (!settings.UBlockLite)
            return Task.CompletedTask;
        lock (LoadGate)
        {
            if (_loading is null || (_loading.IsCompleted && _nothingToLoad && VersionIn(CurrentFolder) is not null))
                _loading = LoadAsync(profile);
            return _loading;
        }
    }

    /// <summary>
    /// UI thread: loads it now, for a first download that arrived after the first page (when
    /// there was nothing to load). One load at a time: a load under way is the one returned.
    /// Never throws.
    /// </summary>
    public static Task LoadNowAsync(CoreWebView2Profile profile, Settings settings)
    {
        if (!settings.UBlockLite)
            return Task.CompletedTask;
        lock (LoadGate)
        {
            if (LoadedId is not null)
                return Task.CompletedTask;
            if (_loading is { IsCompleted: false })
                return _loading;
            return _loading = LoadAsync(profile);
        }
    }

    private static async Task LoadAsync(CoreWebView2Profile profile)
    {
        try
        {
            string? version = VersionIn(CurrentFolder);
            _nothingToLoad = version is null;
            if (version is null)
            {
                DebugLog.WriteAlways("uBO Lite is not downloaded yet; it is fetched in the background");
                return;
            }
            var loading = LoadFolderAsync(profile, CurrentFolder);
            if (await Task.WhenAny(loading, Task.Delay(TimeSpan.FromSeconds(10))) != loading)
            {
                DebugLog.WriteAlways("uBO Lite did not load in time; carrying on without waiting");
                _ = loading.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        Loaded(t.Result, version, late: true);
                        LoadedLate?.Invoke();
                    }
                    else
                    {
                        DebugLog.WriteAlways($"uBO Lite could not be loaded: {t.Exception?.GetBaseException().Message}");
                    }
                }, TaskScheduler.Default);
                return;
            }
            Loaded(await loading, version, late: false);
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"uBO Lite could not be loaded: {ex.Message}");
        }
    }

    private static void Loaded(string id, string version, bool late)
    {
        lock (LoadGate)
        {
            LoadedId = id;
            LoadedVersion = version;
        }
        // The folder's version: the engine reports none of its own for an extension.
        DebugLog.WriteAlways($"uBO Lite {version} loaded{(late ? " (late)" : "")}");
    }

    /// <summary>The extension from <paramref name="folder"/> in the profile: the one already there (the engine keeps them), or added now.</summary>
    private static async Task<string> LoadFolderAsync(CoreWebView2Profile profile, string folder)
    {
        foreach (var installed in await profile.GetBrowserExtensionsAsync())
        {
            if (IsUBlockLite(installed.Name))
            {
                if (!installed.IsEnabled)
                    await installed.EnableAsync(true);
                return installed.Id;
            }
        }
        var added = await profile.AddBrowserExtensionAsync(folder);
        return added.Id;
    }

    /// <summary>Whether an installed extension's name is uBO Lite's (its manifest name is localised; English and the short name).</summary>
    internal static bool IsUBlockLite(string? name)
        => name is not null && (name.Contains("uBlock Origin Lite", StringComparison.OrdinalIgnoreCase) || name.Equals("uBO Lite", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ keeping it current

    /// <summary>
    /// Off the UI thread: checks for a newer release after <paramref name="firstAfter"/> and
    /// then every <see cref="CheckEvery"/> until <paramref name="stop"/>. A first download
    /// (nothing current yet) goes straight into current, and <paramref name="firstInstalled"/>
    /// is told so the window can load it (engine calls belong on the UI thread); later ones
    /// wait in pending for the next start.
    /// </summary>
    public static async Task KeepCurrentAsync(Action firstInstalled, TimeSpan firstAfter, CancellationToken stop)
    {
        try
        {
            await Task.Delay(firstAfter, stop);
            while (!stop.IsCancellationRequested)
            {
                await CheckOnceAsync(firstInstalled, stop);
                await Task.Delay(CheckEvery, stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static async Task CheckOnceAsync(Action firstInstalled, CancellationToken stop)
    {
        try
        {
            // What a crash or a locked file left behind before.
            foreach (var leftover in Directory.Exists(Root) ? Directory.GetFiles(Root, "download-*.zip") : [])
                try { File.Delete(leftover); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            // A plain client name: GitHub asks for one, and nothing about the person goes in it.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Gergur-browser");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string? latest = TagFrom(await http.GetStringAsync(LatestReleaseApi, stop));
            if (latest is null)
            {
                DebugLog.WriteAlways("uBO Lite: the latest release could not be read");
                return;
            }
            string? current = VersionIn(CurrentFolder);
            string? pending = VersionIn(PendingFolder);
            if (CompareTags(latest, current) <= 0 || CompareTags(latest, pending) <= 0)
                return;

            bool first = current is null;
            string into = first ? CurrentFolder : PendingFolder;
            string zip = Path.Combine(Root, $"download-{latest}.zip");
            Directory.CreateDirectory(Root);
            try
            {
                using (var response = await http.GetAsync(DownloadUrl(latest), HttpCompletionOption.ResponseHeadersRead, stop))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                        throw new InvalidDataException("the download is larger than any uBO Lite release");
                    // The client's timeout covers the headers only, read this way: the body
                    // gets its own, so a stalled download cannot hold every later check.
                    using var bodyTime = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    bodyTime.CancelAfter(TimeSpan.FromMinutes(5));
                    await using var body = await response.Content.ReadAsStreamAsync(bodyTime.Token);
                    await using var file = File.Create(zip);
                    await CopyBoundedAsync(body, file, MaxDownloadBytes, bodyTime.Token);
                }
                Unpack(zip, into);
                if (VersionIn(into) != latest)
                {
                    Directory.Delete(into, recursive: true);
                    throw new InvalidDataException("the archive is not uBO Lite " + latest);
                }
            }
            finally
            {
                try { File.Delete(zip); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            DebugLog.WriteAlways($"uBO Lite {latest} downloaded{(first ? "" : ", in use from the next start")}");
            // A first one is said once it is actually running (AppSession), not here.
            if (first)
                firstInstalled();
            else
                Updated?.Invoke($"uBlock Origin Lite {latest} is downloaded; it takes over when Gergur restarts.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"uBO Lite update check failed: {ex.Message}");
        }
    }

    internal static async Task CopyBoundedAsync(Stream from, Stream to, long limit, CancellationToken stop)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, stop)) > 0)
        {
            total += read;
            if (total > limit)
                throw new InvalidDataException("the download is larger than any uBO Lite release");
            await to.WriteAsync(buffer.AsMemory(0, read), stop);
        }
    }
}
