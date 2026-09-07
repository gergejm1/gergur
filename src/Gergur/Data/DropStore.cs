using System.Text.Json;
using Gergur.App;
using Gergur.Diagnostics;

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

    /// <summary>
    /// Set when the index exists but could not be read in full. While it is set the index
    /// is never written over: see <see cref="SaveLocked"/>.
    /// </summary>
    private bool _indexUnreadable;

    /// <summary>
    /// Set when the index read but not every entry in it did. The list in memory is then
    /// missing whatever we could not parse, and writing it back loses those entries for
    /// good, so the first save keeps the original beside it. Cleared once that is done.
    ///
    /// This is not the same as refusing to write, which is what <see cref="_indexUnreadable"/>
    /// does: refusing meant a deleted item came back on every launch and could not be
    /// deleted again. Keeping a copy costs one file and loses nothing.
    /// </summary>
    private bool _indexPartial;

    /// <summary>
    /// Where this session writes when it may not touch the index. Resolved once, so a
    /// second session that also cannot read the index does not overwrite the first
    /// session's work: that turned "your work is preserved, just not reloaded" into
    /// "your work is gone".
    /// </summary>
    private string? _recoveryPath;

    private string RecoveryPath => _recoveryPath ??= FreeName(IndexPath + ".recovered");

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
        CountSetAside();
    }

    /// <summary>
    /// Moves files no entry refers to into <see cref="OrphansDir"/>, and removes a stale
    /// staging file.
    ///
    /// Entries go away without their files in more than one way: an index that failed to
    /// write, a save interrupted between the two, or an entry the load filter drops.
    /// Those files are photos and documents from the phone sitting on disk with nothing
    /// pointing at them and no way to reach them from either surface.
    ///
    /// Only from the constructor, before anything else can hold this store, so it cannot
    /// race an upload writing into the same directory, and only when the index was really
    /// read. An index that would not parse also produces an empty list, and acting on that
    /// would treat every file the drop holds as unreferenced on the one occasion they
    /// cannot be listed: the corruption case this store goes out of its way to survive.
    /// </summary>
    private void SweepOrphans()
    {
        try
        {
            // Anything already here is old enough to go before anything new arrives.
            // Running this last let a file be set aside and pruned in the same pass, on
            // the strength of a timestamp, which is how the ninth pass lost twenty photos.
            PruneOrphans();

            // Every list that names a file, not only the one in memory. A session that
            // opened while the index was locked wrote its work to the recovery file, and
            // a photo that arrived during it is named there and nowhere else. Reading
            // only the current index made that photo unreferenced on the next launch and
            // set it aside: the mapping from file to name existed, and we threw it away.
            //
            // Found by enumeration rather than by a list of names. The names are chosen
            // by FreeName, which numbers them when one is taken, and a hardcoded list of
            // four missed every ".superseded-2" the moment a profile had two of them.
            var referenced = _items.Select(i => i.StoredName)
                .Concat(IndexSiblings().SelectMany(NamesIn))
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The copies are read by the same parser that could not read the entries they
            // were kept for, so a name it filters out is not "no name": it is a name we
            // could not read. Standing down over the index and then trusting a half read
            // of its copy protected nothing in exactly the case the copy exists for, and
            // twenty photos went to orphans two launches later. Same doctrine either way:
            // if a list cannot be read in full, this launch cannot say what is unused.
            //
            // Only the deciding stops. The housekeeping below it is not a judgement about
            // any file the user owns.
            bool canDecide = IndexSiblings().All(FullyReadable);

            bool made = false;
            foreach (string path in canDecide ? Directory.EnumerateFiles(FilesDir) : [])
            {
                if (referenced.Contains(Path.GetFileName(path)))
                    continue;

                // Moved, not deleted. Every guard above is a judgement about whether the
                // list can be trusted, and this has already been wrong twice in ways that
                // ended with the user's photos gone. Quarantine keeps the tidying and
                // takes the whole class of mistake off the table: the worst a wrong
                // judgement can now do is put a file in the next folder along.
                if (!made)
                {
                    Directory.CreateDirectory(OrphansDir);
                    made = true;
                }
                SetAside(path);
            }

            // The staging file goes only when the current index already names everything
            // it does. Consulting it to decide what is live and then deleting it in the
            // same pass protected a file for exactly one launch, because the only list
            // naming it was the one just destroyed.
            string staging = IndexPath + ".tmp";
            if (File.Exists(staging))
            {
                if (!Readable(staging))
                {
                    // Locked right now, by the backup or the scanner this whole class is
                    // written around. Not evidence of anything either way, and certainly
                    // not something to delete on the strength of not being able to open
                    // it. It will still be here next launch.
                }
                else if (ReadIndex(staging) is null)
                {
                    // Half a write. It is not an index by any reading, so it is not
                    // evidence of anything and it cannot become one.
                    File.Delete(staging);
                }
                else if (FullyReadable(staging))
                {
                    // It reads, so what it names can be compared. Deleting one this build
                    // could only half read counted its unreadable entries as naming
                    // nothing, and the photos they accounted for went the launch after.
                    var live = _items.Select(i => i.StoredName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (NamesIn(staging).Where(n => n.Length > 0).All(live.Contains))
                        File.Delete(staging);
                }
            }

            PruneIndexCopies();
        }
        catch
        {
            // Housekeeping. Never worth failing to open the drop over.
        }
    }

    /// <summary>
    /// Every file sitting beside the index: the staging file, and the copies kept when
    /// one could not be read or could not be fully used. Enumerated rather than named,
    /// because the names are numbered when one is already taken and a written-out list
    /// stops being complete the second time anything goes wrong on a profile.
    /// </summary>
    private IEnumerable<string> IndexSiblings()
    {
        try
        {
            // The index itself is excluded by name, not by trusting the pattern. Windows
            // treats a trailing ".*" as "extension optional", so "items.json.*" matches
            // "items.json", and the prune below then read the live index as a spent copy
            // and deleted it: a drop of nothing but messages, untouched for sixty days,
            // emptied itself on open.
            string mine = Path.GetFileName(IndexPath);
            return Directory.EnumerateFiles(_root, mine + ".*")
                .Where(p => !string.Equals(Path.GetFileName(p), mine, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Removes index copies that are old and no longer account for anything: every file
    /// they named is gone from the drop. Without this they accumulate forever, and every
    /// name in them is exempt from the sweep for as long as they sit there, so the footer
    /// notice can never go back to saying nothing.
    /// </summary>
    private void PruneIndexCopies()
    {
        var cutoff = DateTime.UtcNow - OrphanLifetime;
        foreach (string path in IndexSiblings())
        {
            // Said twice on purpose. IndexSiblings already excludes it, and this is the
            // line that deletes, so it does not take that on trust.
            if (string.Equals(Path.GetFileName(path), Path.GetFileName(IndexPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The staging file has its own rule above; leave it alone.
            if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            // A recovery list is a session's own work, and nothing merges it back yet,
            // so it is the only copy of what that session did. Not ours to age out.
            if (Path.GetFileName(path).Contains(".recovered", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (File.GetLastWriteTimeUtc(path) >= cutoff)
                    continue;

                // The same inference the sweep above refuses to make, in the line that
                // actually deletes. A list that will not read names nothing to us, which
                // is not the same as naming nothing, and reading it as the latter deleted
                // the only record of twenty photos and then quarantined the photos.
                //
                // It is set aside instead of deleted, and set aside rather than left,
                // because leaving it made it immortal: it stays a sibling, so every later
                // launch sees a list it cannot read and stands the whole sweep down. One
                // unreadable file quietly turned the tidying off for the life of the
                // profile, and the drop window said "1 thing set aside" forever.
                if (!FullyReadable(path))
                {
                    Directory.CreateDirectory(OrphansDir);
                    SetAside(path);
                    continue;
                }

                if (NamesIn(path).Any(n => n.Length > 0 && File.Exists(Path.Combine(FilesDir, n))))
                    continue;
                File.Delete(path);
            }
            catch
            {
                // Housekeeping only.
            }
        }
    }

    /// <summary>How long a set-aside file is kept before it really is deleted.</summary>
    private static readonly TimeSpan OrphanLifetime = TimeSpan.FromDays(60);

    /// <summary>
    /// Moves one file into the quarantine folder without overwriting anything already
    /// there. Two files can carry the same stored name across a restore or a copied
    /// profile, and a function whose whole point is that tidying loses nothing cannot
    /// take one of them out on the way past.
    ///
    /// Numbered before the extension rather than after it, which is why this is not
    /// <see cref="FreeName"/>: "a1-2.jpg" still opens, "a1.jpg-2" does not.
    /// </summary>
    private void SetAside(string path)
    {
        string name = Path.GetFileName(path);
        string target = Path.Combine(OrphansDir, name);
        for (int n = 2; File.Exists(target) && n < 1000; n++)
        {
            target = Path.Combine(
                OrphansDir,
                $"{Path.GetFileNameWithoutExtension(name)}-{n}{Path.GetExtension(name)}");
        }

        try
        {
            File.Move(path, target);

            // Stamped on the way in, because a move keeps the original write time and
            // the prune reads that. Without this a photo that arrived three months ago
            // and became unreferenced today was set aside and deleted in the same pass:
            // the exact loss this folder exists to prevent, with nothing shown for it.
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // One file that will not move must not stop the rest being set aside, and
            // must certainly not skip the prune and the staging cleanup below it.
            DebugLog.WriteAlways($"could not set aside {name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes set-aside files that have been there long enough to be sure. Without this
    /// the folder only grows, and it grows with copies of the user's photos: a store that
    /// misjudges repeatedly, which is what this whole guard exists for, would fill the
    /// profile up. Two months is long past the point of noticing something went missing.
    /// </summary>
    private void PruneOrphans()
    {
        if (!Directory.Exists(OrphansDir))
            return;
        var cutoff = DateTime.UtcNow - OrphanLifetime;
        foreach (string path in Directory.EnumerateFiles(OrphansDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
            catch
            {
                // One stubborn file is not worth failing the open.
            }
        }
    }

    /// <summary>
    /// Whether the file can be opened at all right now. Says nothing about its contents:
    /// a file held by a backup or a scanner answers false and may be perfectly good, and
    /// that difference is why nothing is deleted on the strength of a failed open.
    /// </summary>
    private static bool Readable(string path)
    {
        try
        {
            using var probe = File.OpenRead(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a list beside the index can be read all the way through. A staging file
    /// caught mid write, or a copy from a build whose entries this one cannot parse,
    /// both answer false: neither can be used to decide that a file is unreferenced.
    /// </summary>
    private static bool FullyReadable(string path)
        => ReadIndex(path) is { } loaded && loaded.All(IsUsable);

    /// <summary>
    /// Stored names an index file mentions, or nothing when it is missing or unreadable.
    /// Used to decide what is referenced, so it errs towards naming more rather than less.
    /// </summary>
    private static IEnumerable<string> NamesIn(string indexPath)
        => ReadIndex(indexPath)?.Where(i => i?.StoredName is not null).Select(i => i.StoredName) ?? [];

    public string FilesDir => Path.Combine(_root, "files");

    /// <summary>
    /// Where files nothing refers to are put, rather than deleting them: tidying up must
    /// never be the thing that loses a photo. Counted in <see cref="SetAsideCount"/> so
    /// the drop window can say it is there. Safe to empty by hand.
    ///
    /// Eviction at <see cref="MaxItems"/> still deletes outright: that one is the user's
    /// own list overflowing, not us failing to account for a file.
    /// </summary>
    public string OrphansDir => Path.Combine(_root, "orphans");

    /// <summary>The drop folder itself: files, orphans and any recovery index live here.</summary>
    public string DropDir => _root;

    /// <summary>
    /// Files that were set aside because nothing referred to them, and any list this
    /// store had to write beside an index it could not read. Both are recovery, and both
    /// used to be invisible: written where nobody would look and never mentioned again.
    ///
    /// Counted when the store opens and after a save that writes one of those lists,
    /// rather than on every read: the drop window asks for this on every item the phone
    /// sends, and it was three filesystem calls on the UI thread each time.
    /// </summary>
    public int SetAsideCount { get; private set; }

    private void CountSetAside()
    {
        try
        {
            SetAsideCount =
                (Directory.Exists(OrphansDir) ? Directory.GetFiles(OrphansDir).Length : 0)
                + Directory.GetFiles(_root, "items.json.recovered*").Length
                + Directory.GetFiles(_root, "items.json.superseded*").Length;
        }
        catch
        {
            // Not worth failing anything over; the notice simply does not appear.
        }
    }
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
            // A staging file newer than the index is a save that wrote it and did not
            // reach the move: a crash, or a sharing violation from a backup or a virus
            // scanner, which SaveLocked swallows. It is then the more recent of the two
            // indexes and the only record of everything added in that session. Reading
            // the older one and sweeping against it deletes exactly those files, while
            // carefully preserving a staging file that nothing ever reads.
            //
            // So finish the move the save did not get to, but only once the file has been
            // proved good by parsing it: a write interrupted halfway leaves something
            // that will not parse, and that must not replace a working index.
            string staging = IndexPath + ".tmp";
            if (File.Exists(staging)
                && (!File.Exists(IndexPath)
                    || File.GetLastWriteTimeUtc(staging) >= File.GetLastWriteTimeUtc(IndexPath)))
            {
                var staged = ReadIndex(staging);
                if (staged is null)
                {
                    // Half a write. Not an index by any reading, so it is not evidence of
                    // anything either, and leaving it meant the next save wrote over it
                    // anyway while this launch stood down over it.
                    TryDelete(staging);
                }
                else if (staged.All(IsUsable))
                {
                    // Finish the move the save did not get to, keeping what it replaces.
                    if (File.Exists(IndexPath))
                        TryMove(IndexPath, FreeName(IndexPath + ".superseded"));
                    File.Move(staging, IndexPath, overwrite: true);
                }
                else
                {
                    // It parses but we cannot use all of it: most likely an index from a
                    // later build than this one. Understanding it is not required, and
                    // overwriting either file is not allowed.
                    _indexUnreadable = true;
                    return LoadUnswept();
                }
            }

            if (ReadIndex(IndexPath) is not { } loaded)
            {
                // Missing is a new drop. Present but unreadable is a file we must not
                // write over: a lock held by a backup or a scanner reads exactly like
                // corruption from here, and the file is the only copy of what is in it.
                _indexUnreadable = File.Exists(IndexPath);
                return [];
            }

            // Locking the list closed one source of nulls; this is the other. A hand
            // edited or truncated index can deserialize entries that are null, or whose
            // strings are, and those throw on the UI thread when the window renders them,
            // which is the crash the lock was meant to end.
            //
            // Every string is checked, not the three that were obviously used: an entry
            // with a null StoredName passed the old filter and then threw inside PathFor,
            // reached from opening a file in the drop window, which takes the browser
            // down with every tab. The list in IsUsable is still written out by hand, so
            // a string added to DropItem has to be added there too.
            var kept = loaded.Where(IsUsable).ToList();

            // Only a list that lost nothing can say what is unreferenced. Every entry
            // dropped here is one whose file is still on disk and would otherwise be
            // swept, and the reason it was dropped is that we could not read it properly.
            indexIntact = kept.Count == loaded.Count;
            _indexPartial = !indexIntact;

            // Deliberately not tied to indexIntact. An index that read fine but had one
            // unusable entry is still an index we understand, and refusing to write it
            // again meant the user could never delete anything for the life of that
            // install: Remove took the file away, the save went to a side file, and the
            // item came back on the next launch as something that no longer opens.
            // The sweep still stands down; that is what indexIntact is for.
            return kept;
        }
        catch
        {
            // Whatever went wrong here was in the staging-file handling above, since the
            // read itself no longer throws. Start empty, and leave the index alone.
            _indexUnreadable = File.Exists(IndexPath);
        }
        return [];
    }

    /// <summary>Best effort housekeeping: never worth failing to open the drop over.</summary>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>
    /// Moves or copies an index aside, dated from now. A move keeps the source's write
    /// time, so a copy of an index last touched two months ago was born already past
    /// the prune cutoff and deleted in the same pass that made it.
    /// </summary>
    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
            File.SetLastWriteTimeUtc(to, DateTime.UtcNow);
        }
        catch { }
    }

    /// <summary>
    /// The given name, or the first numbered variant that is free. Every one of these
    /// files is the only copy of something, so none of them may take another's place.
    /// </summary>
    private static string FreeName(string preferred)
    {
        if (!File.Exists(preferred))
            return preferred;
        for (int n = 2; n < 1000; n++)
        {
            string candidate = $"{preferred}-{n}";
            if (!File.Exists(candidate))
                return candidate;
        }
        return preferred + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    private static bool TryCopy(string from, string to)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
            File.SetLastWriteTimeUtc(to, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"could not keep a copy of the index: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The current index, read for its contents only. Used when something about the files
    /// on disk is unexplained, so the list is worth having but must not be used to decide
    /// that anything is unreferenced.
    /// </summary>
    private List<DropItem> LoadUnswept()
    {
        if (ReadIndex(IndexPath) is { } loaded)
            return loaded.Where(IsUsable).ToList();

        // The same rule as the main path, and the reason this is not one line: leaving it
        // out here left the whole guard bypassable. An index that exists but will not read
        // must not be written over, whichever route reached that conclusion.
        _indexUnreadable = File.Exists(IndexPath);
        return [];
    }

    /// <summary>
    /// Reads and parses an index file, or null when it is missing or will not parse. One
    /// copy of this: three callers read the index, and two of them drifting apart is how
    /// a file gets treated as unreferenced by one and referenced by the other.
    /// </summary>
    private static List<DropItem>? ReadIndex(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<DropItem>>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
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

            // An index we read but could not use all of is kept once, before the first
            // write replaces it with only the entries this build understood. Twenty
            // entries in and one out is a real sequence: a field renamed in a later
            // build, opened by this one. The photos survive as files either way, but
            // their names, dates and every message beside them live only in that file.
            // Gated on this session, not on whether a copy happens to exist already: an
            // on-disk latch meant the second partial index this profile ever saw was
            // overwritten with nothing kept, because round one had claimed the name.
            //
            // The flag is only cleared when the copy actually happened. Clearing it
            // regardless spent the one chance on a copy that failed, and the write below
            // then replaced the original with the reduced list and nothing kept: the same
            // lock from a backup or a scanner that the rest of this method is written for.
            if (_indexPartial && File.Exists(IndexPath))
            {
                if (TryCopy(IndexPath, FreeName(IndexPath + ".superseded")))
                    _indexPartial = false;
            }
            else
            {
                _indexPartial = false;
            }

            // An index we could not read is never written over. It reads as empty in
            // memory, so overwriting it replaces everything the user had with whatever
            // this session happens to hold, and the cause is as likely to be a lock held
            // by a backup or a virus scanner as real corruption. Writing beside it keeps
            // both: the original for a later launch that can read it, and this session's
            // work in a file that is plainly named.
            //
            // A partial index whose copy could not be made goes the same way: the list in
            // memory is missing entries, so it is not allowed to become the index.
            string target = _indexUnreadable || _indexPartial ? RecoveryPath : IndexPath;

            // Write beside it and move into place. A direct write that is interrupted
            // leaves a truncated index, which loads as an empty drop and orphans every
            // file it referenced, with nothing to tell the user it happened.
            string staging = target + ".tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(_items, JsonOptions));
            File.Move(staging, target, overwrite: true);

            if (target != IndexPath)
                CountSetAside();
        }
        catch
        {
            // Best effort: the in-memory list still serves this session.
        }
    }
}
