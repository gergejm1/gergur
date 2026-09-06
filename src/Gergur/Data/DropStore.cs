using System.Text.Json;
using Gergur.App;

namespace Gergur.Data;

/// <summary>One thing sent between the phone and the PC.</summary>
/// <param name="Kind">"text", "link" or "file".</param>
/// <param name="Text">The message, the url, or the original filename for a file.</param>
/// <param name="StoredName">The file on disk for a file item; empty otherwise.</param>
/// <param name="From">"phone" or "pc", so each side can tell what it did not send.</param>
public sealed record DropItem(
    string Id,
    string Kind,
    string Text,
    string StoredName,
    long Size,
    string From,
    DateTime AddedUtc)
{
    public bool IsFile => Kind == "file";
}

/// <summary>
/// The shared drop: a single list both the phone and the PC add to and read from, in
/// the spirit of a private chat used to pass things between your own devices. Files
/// live beside the metadata under the profile, so nothing leaves the machine except
/// over the local network to a paired phone.
/// </summary>
public sealed class DropStore
{
    private const int MaxItems = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _root;
    private readonly List<DropItem> _items;

    /// <summary>
    /// Writes arrive on request threads while the UI thread reads. Without this the list
    /// loses entries and nulls slots under concurrent adds, and a reader enumerating it
    /// mid-insert throws "Collection was modified" on the UI thread, where nothing
    /// catches it and the whole browser goes down with every tab.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>Raised whenever the list changes, from either side.</summary>
    public event EventHandler<DropItem>? ItemAdded;
    public event EventHandler? Changed;

    /// <param name="root">Override the drop folder; defaults to the profile's drop directory.</param>
    public DropStore(string? root = null)
    {
        _root = root ?? Path.Combine(Settings.DataDir, "drop");
        Directory.CreateDirectory(FilesDir);
        _items = Load(out bool indexIntact);
        if (indexIntact)
            SweepOrphans();
    }

    /// <summary>
    /// Deletes files under the drop that no entry refers to, and any half-written index
    /// left by an interrupted save.
    ///
    /// Entries go away without their files in more than one way: an index that failed to
    /// write, a save interrupted between the two, or an entry this build's stricter load
    /// filter now drops. Those files are photos and documents from the phone sitting on
    /// disk with nothing pointing at them and no way to reach them from either surface.
    ///
    /// Only from the constructor, before anything else can hold this store, so it cannot
    /// race an upload writing into the same directory, and only when the index was really
    /// read. An index that would not parse also produces an empty list, and sweeping on
    /// that would delete every file the drop holds on the one occasion they cannot be
    /// listed: the corruption case this store already goes out of its way to survive.
    /// </summary>
    private void SweepOrphans()
    {
        try
        {
            var referenced = _items
                .Where(i => i.StoredName.Length > 0)
                .Select(i => i.StoredName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.EnumerateFiles(FilesDir))
            {
                if (!referenced.Contains(Path.GetFileName(path)))
                    File.Delete(path);
            }

            // A staging file older than the index is left over from a save that finished;
            // a newer one is a save that wrote it and did not get to the move, which makes
            // it the more recent of the two indexes and not something to throw away.
            string staging = IndexPath + ".tmp";
            if (File.Exists(staging)
                && File.GetLastWriteTimeUtc(staging) <= File.GetLastWriteTimeUtc(IndexPath))
            {
                File.Delete(staging);
            }
        }
        catch
        {
            // Housekeeping. Never worth failing to open the drop over.
        }
    }

    public string FilesDir => Path.Combine(_root, "files");
    private string IndexPath => Path.Combine(_root, "items.json");

    /// <summary>
    /// Newest first, which is the order both surfaces display. A snapshot, never the live
    /// list: callers enumerate on their own thread while uploads land on another.
    /// </summary>
    public IReadOnlyList<DropItem> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    /// <summary>A message or a link. Which one is decided here, not by the caller.</summary>
    public DropItem AddText(string text, string from)
    {
        text = text.Trim();
        var item = new DropItem(
            NewId(), Classify(text), text, StoredName: "", Size: text.Length, from, DateTime.UtcNow);
        Insert(item);
        return item;
    }

    /// <summary>
    /// Records a file already on disk by moving it into the drop. The phone bridge
    /// streams uploads to a staging file rather than holding them in memory, so this is
    /// how they arrive.
    /// </summary>
    public DropItem AddFileFromPath(string originalName, string sourcePath, string from)
    {
        string id = NewId();
        string stored = id + StorableExtension(originalName);
        Directory.CreateDirectory(FilesDir);
        string destination = Path.Combine(FilesDir, stored);

        File.Move(sourcePath, destination, overwrite: true);
        long size = new FileInfo(destination).Length;
        if (from != "pc")
            MarkAsFromNetwork(destination);

        var item = new DropItem(id, "file", DisplayName(originalName), stored, size, from, DateTime.UtcNow);
        Insert(item);
        return item;
    }

    /// <summary>Stores a file's bytes and records it. The stored name is never caller-controlled.</summary>
    public DropItem AddFile(string originalName, Stream content, string from)
    {
        string id = NewId();
        // The phone supplies the name, so it never becomes a path: only its extension
        // is kept, and the file on disk is named after the id we generated.
        string extension = StorableExtension(originalName);
        string stored = id + extension;

        Directory.CreateDirectory(FilesDir);
        string path = Path.Combine(FilesDir, stored);
        long size;
        using (var file = File.Create(path))
        {
            content.CopyTo(file);
            size = file.Length;
        }
        if (from != "pc")
            MarkAsFromNetwork(path);

        var item = new DropItem(id, "file", DisplayName(originalName), stored, size, from, DateTime.UtcNow);
        Insert(item);
        return item;
    }

    public DropItem? Find(string id)
    {
        lock (_gate)
            return _items.FirstOrDefault(i => i.Id == id);
    }

    /// <summary>
    /// Absolute path of a stored file, or null when the item is not one.
    ///
    /// Re-checks the stored name rather than trusting it. It is generated here, but it
    /// makes a round trip through items.json, and Remove and Clear delete whatever this
    /// returns, so the invariant is worth re-establishing on the way out.
    /// </summary>
    public string? PathFor(DropItem item)
    {
        if (!item.IsFile || item.StoredName.Length == 0)
            return null;
        if (item.StoredName.Contains("..", StringComparison.Ordinal)
            || item.StoredName.Contains('/') || item.StoredName.Contains('\\')
            || Path.IsPathRooted(item.StoredName))
            return null;
        return Path.Combine(FilesDir, item.StoredName);
    }

    public void Remove(string id)
    {
        DropItem? item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return;
            _items.Remove(item);
            SaveLocked();
        }
        // File and event work happens outside the lock: neither needs it, and holding it
        // across disk IO would block every request thread.
        DeleteFile(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        DropItem[] removed;
        lock (_gate)
        {
            removed = _items.ToArray();
            _items.Clear();
            SaveLocked();
        }
        foreach (var item in removed)
            DeleteFile(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>http and https text is a link; anything else is a message.</summary>
    internal static string Classify(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !text.Any(char.IsWhiteSpace)
            ? "link"
            : "text";

    /// <summary>
    /// The extension only, lowercased, and only when it looks like one. A name from the
    /// phone must never be able to steer where the file lands.
    /// </summary>
    internal static string SafeExtension(string originalName)
    {
        string extension = Path.GetExtension(originalName ?? "");
        if (extension.Length is < 2 or > 12)
            return "";
        foreach (char c in extension[1..])
        {
            if (!char.IsLetterOrDigit(c))
                return "";
        }
        return extension.ToLowerInvariant();
    }

    /// <summary>The filename to show, stripped of any path and of anything deceptive.</summary>
    internal static string DisplayName(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
            return "file";
        string name = originalName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var cleaned = new string(name.Where(c => !IsDeceptive(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    /// <summary>
    /// Characters that make a name read as something it is not. A right-to-left override
    /// (U+202E) turns "holiday\u202Efdp.exe" into "holidayexe.pdf" on screen, so the user
    /// sees a document and launches an executable. Control characters hide text the same
    /// way.
    ///
    /// Written as escapes rather than as the characters themselves. This is the file that
    /// defends against that trick, and putting the raw overrides in the source means the
    /// next person to review it reads whatever their editor chooses to render.
    /// </summary>
    private static bool IsDeceptive(char c)
        => char.IsControl(c)
        || c is >= '\u202A' and <= '\u202E'  // bidi embeddings and overrides
        || c is >= '\u2066' and <= '\u2069'  // bidi isolates
        || c is '\u200E' or '\u200F';        // left and right to left marks

    /// <summary>
    /// The extension a stored file is allowed to carry on disk. Anything not on the list
    /// becomes ".bin", so a double-click in Explorer opens nothing that runs.
    ///
    /// This was a deny list, and the comment claimed that renaming made an incomplete
    /// list harmless. It did not: the rename consulted the same list, so a type missing
    /// from it was still stored runnable, and .msix, .appx, .wsc, .sct and .mst all were.
    /// A deny list of what Windows will execute cannot be finished by hand. An allow list
    /// can, because what this feature actually moves between two of your own devices is
    /// pictures, video, documents and archives.
    ///
    /// The cost is that an unusual but harmless type is stored as .bin and will not open
    /// on a double-click. The name you sent is kept for display and for the download back
    /// to the phone, so nothing is lost but the association.
    /// </summary>
    internal static string StorableExtension(string originalName)
    {
        string extension = SafeExtension(originalName);
        return extension.Length > 0 && Storable.Contains(extension) ? extension : ".bin";
    }

    /// <summary>
    /// Extensions kept as they are on disk. Nothing here is executed by Explorer on a
    /// double-click, which is the whole test for being on this list.
    /// </summary>
    private static readonly HashSet<string> Storable = new(StringComparer.OrdinalIgnoreCase)
    {
        // pictures
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff",
        ".avif", ".ico", ".psd", ".raw", ".dng",
        // video and audio
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp",
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".aiff", ".caf",
        // documents
        ".pdf", ".txt", ".md", ".rtf", ".csv", ".tsv", ".log", ".json", ".xml", ".yaml", ".yml",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".pages", ".numbers", ".key", ".epub", ".mobi", ".ics", ".vcf",
        // archives and data
        ".zip", ".7z", ".rar", ".gz", ".tar", ".bz2", ".xz", ".bin", ".dat",
    };

    /// <summary>
    /// Extensions Windows will execute. This feature moves files between your devices;
    /// it is not a way to run one, so a click never launches these. Anyone holding the
    /// pairing key could otherwise put an executable in the drop.
    /// </summary>
    public static bool IsExecutable(string name)
    {
        string extension = Path.GetExtension(name);
        return (extension.Length > 0 ? extension : name).ToLowerInvariant()
            is ".exe" or ".com" or ".scr" or ".pif" or ".bat" or ".cmd" or ".ps1" or ".psm1"
            or ".hta" or ".js" or ".jse" or ".vbs" or ".vbe" or ".wsf" or ".wsh" or ".msi"
            or ".msp" or ".msc" or ".reg" or ".lnk" or ".url" or ".scf" or ".cpl" or ".inf"
            or ".chm" or ".jar" or ".application" or ".gadget";
    }

    /// <summary>
    /// Tags a file as having come from the network, so SmartScreen and Office give it the
    /// suspicion it deserves instead of trusting it as something created on this machine.
    /// </summary>
    private static void MarkAsFromNetwork(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch
        {
            // Not every filesystem carries alternate data streams; the deny list stands regardless.
        }
    }

    private void Insert(DropItem item)
    {
        var evicted = new List<DropItem>();
        lock (_gate)
        {
            _items.Insert(0, item);
            while (_items.Count > MaxItems)
            {
                evicted.Add(_items[^1]);
                _items.RemoveAt(_items.Count - 1);
            }
            SaveLocked();
        }
        foreach (var oldest in evicted)
            DeleteFile(oldest);
        ItemAdded?.Invoke(this, item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteFile(DropItem item)
    {
        if (PathFor(item) is { } path)
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];

    /// <param name="indexIntact">
    /// Whether the index was read and every entry in it survived. False means the list in
    /// memory is not a complete account of what the drop holds, so it cannot be used to
    /// decide that a file on disk is unreferenced. Anything else risks deleting the user's
    /// photos on the one launch where they could not be listed: an unreadable file, or a
    /// future build's index read by this one, where a renamed field nulls every entry.
    /// </param>
    private List<DropItem> Load(out bool indexIntact)
    {
        indexIntact = false;
        try
        {
            if (!File.Exists(IndexPath))
                return [];
            var loaded = JsonSerializer.Deserialize<List<DropItem>>(File.ReadAllText(IndexPath));
            if (loaded is null)
                return [];

            // Locking the list closed one source of nulls; this is the other. A hand
            // edited or truncated index can deserialize entries that are null, or whose
            // strings are, and those throw on the UI thread when the window renders them,
            // which is the crash the lock was meant to end.
            //
            // Every string is checked, not the three that were obviously used: an entry
            // with a null StoredName passed the old filter and then threw inside PathFor,
            // reached from opening a file in the drop window, which takes the browser
            // down with every tab. Listing fields by hand is what left the gap, so this
            // asks the record for all of them.
            var kept = loaded.Where(IsUsable).ToList();

            // Only a list that lost nothing can say what is unreferenced. Every entry
            // dropped here is one whose file is still on disk and would otherwise be
            // swept, and the reason it was dropped is that we could not read it properly.
            indexIntact = kept.Count == loaded.Count;
            return kept;
        }
        catch
        {
            // A corrupt index should not lose the browser; start the list empty.
        }
        return [];
    }

    /// <summary>
    /// Whether an entry read back from the index can be used without throwing. Nothing on
    /// <see cref="DropItem"/> is nullable, so anything null here came from a file that was
    /// edited or truncated outside the browser.
    /// </summary>
    internal static bool IsUsable(DropItem? item)
        => item is not null
        && item.Id is not null && item.Kind is not null && item.Text is not null
        && item.StoredName is not null && item.From is not null;

    /// <summary>Serializes the index. Callers must already hold <see cref="_gate"/>.</summary>
    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(_root);
            // Write beside it and move into place. A direct write that is interrupted
            // leaves a truncated index, which loads as an empty drop and orphans every
            // file it referenced, with nothing to tell the user it happened.
            string staging = IndexPath + ".tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(_items, JsonOptions));
            File.Move(staging, IndexPath, overwrite: true);
        }
        catch
        {
            // Best effort: the in-memory list still serves this session.
        }
    }
}
