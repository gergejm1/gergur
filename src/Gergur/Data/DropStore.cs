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
        _items = Load();
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

    /// <summary>Stores a file's bytes and records it. The stored name is never caller-controlled.</summary>
    public DropItem AddFile(string originalName, Stream content, string from)
    {
        string id = NewId();
        // The phone supplies the name, so it never becomes a path: only its extension
        // is kept, and the file on disk is named after the id we generated.
        string extension = SafeExtension(originalName);
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
    /// turns "holiday‮fdp.exe" into "holidayexe.pdf" on screen, so the user sees a
    /// document and launches an executable. Control characters hide text the same way.
    /// </summary>
    private static bool IsDeceptive(char c)
        => char.IsControl(c)
        || c is >= '‪' and <= '‮'  // bidi embeddings and overrides
        || c is >= '⁦' and <= '⁩'  // bidi isolates
        || c is '‎' or '‏';        // left/right to left marks

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

    private List<DropItem> Load()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<List<DropItem>>(File.ReadAllText(IndexPath)) ?? [];
        }
        catch
        {
            // A corrupt index should not lose the browser; start the list empty.
        }
        return [];
    }

    /// <summary>Serializes the index. Callers must already hold <see cref="_gate"/>.</summary>
    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(_items, JsonOptions));
        }
        catch
        {
            // Best effort: the in-memory list still serves this session.
        }
    }
}
