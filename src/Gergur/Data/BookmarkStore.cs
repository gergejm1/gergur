using System.Text.Json;
using Gergur.App;
using Gergur.Diagnostics;

namespace Gergur.Data;

public sealed record Bookmark(string Url, string Title, DateTime AddedUtc);

public sealed class BookmarkStore
{
    public static readonly string DefaultPath = Path.Combine(Settings.DataDir, "bookmarks.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<Bookmark> _items;

    /// <param name="path">Where the list lives. Defaults to the profile; a test passes its
    /// own, since this file is the user's and nothing a test reaches may write it.</param>
    public BookmarkStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        (_items, _unreadable) = Load(_path);
    }

    // The file was there and could not be read or parsed, so the list is empty rather than
    // the person's. Saving it would write that empty list over theirs, so nothing is saved
    // this run: the settings file is held to the same rule.
    private readonly bool _unreadable;

    /// <summary>Whether the file was there and could not be read, so the list is not theirs.</summary>
    public bool Unreadable => _unreadable;

    /// <summary>What to do about it, for anywhere that says so.</summary>
    public const string WhatToDo = @"It is in %LOCALAPPDATA%\Gergur. If another program has it open, close that program and restart Gergur. "
        + "If it is damaged, rename it and restart Gergur to start a new list; the renamed copy keeps your old bookmarks.";

    /// <summary>
    /// A change refused because the bookmarks file could not be read when Gergur started.
    /// An IOException, so every place that already says "could not be saved" catches it,
    /// with a message that says why and what to do.
    /// </summary>
    public sealed class UnreadableException() : IOException(
        "Bookmarks are not being saved: bookmarks.json could not be read when Gergur started, and saving now would replace it. "
        + WhatToDo);

    public IReadOnlyList<Bookmark> Items => _items;

    /// <summary>
    /// Raised after any change, so the bookmarks bar, the new tab page and the bookmarks
    /// page can redraw. The bar used to be the only view and was rebuilt on every menu open.
    /// </summary>
    public event EventHandler? Changed;

    public bool Contains(string url) => _items.Any(b => b.Url == url);

    /// <summary>Returns true when the page is now bookmarked, false when it was removed.</summary>
    public bool Toggle(string url, string title)
    {
        bool added = false;
        Commit(() =>
        {
            if (_items.RemoveAll(b => b.Url == url) == 0)
            {
                _items.Add(new Bookmark(url, title, DateTime.UtcNow));
                added = true;
            }
        });
        return added;
    }

    /// <summary>
    /// Puts a bookmark back at a position, for the bookmarks page's Undo after a delete.
    /// Refused for a blank title or a url already bookmarked; the index is clamped.
    /// </summary>
    public bool Insert(string url, string title, int index)
    {
        title = title.Trim();
        if (title.Length == 0 || url.Length == 0 || Contains(url))
            return false;
        Commit(() => _items.Insert(Math.Clamp(index, 0, _items.Count), new Bookmark(url, title, DateTime.UtcNow)));
        return true;
    }

    /// <summary>Removes the bookmark for this url. Returns whether there was one.</summary>
    public bool Remove(string url)
    {
        if (!Contains(url))
            return false;
        Commit(() => _items.RemoveAll(b => b.Url == url));
        return true;
    }

    /// <summary>
    /// Renames the bookmark for this url, keeping its place in the list. A blank title is
    /// refused rather than saved: the bar and the tiles would draw an empty button.
    /// </summary>
    public bool Rename(string url, string title)
    {
        title = title.Trim();
        int at = _items.FindIndex(b => b.Url == url);
        if (at < 0 || title.Length == 0)
            return false;
        Commit(() => _items[at] = _items[at] with { Title = title });
        return true;
    }

    private static (List<Bookmark> Items, bool Unreadable) Load(string path)
    {
        if (!File.Exists(path))
            return ([], false);
        try
        {
            string text = File.ReadAllText(path);
            // Empty, which a crash during the old unbuffered write could leave: there is
            // nothing in it to lose, and refusing every change until the person found and
            // deleted it would be all cost.
            if (string.IsNullOrWhiteSpace(text))
                return ([], false);
            var items = JsonSerializer.Deserialize<List<Bookmark>>(text) ?? [];
            // Parses, but not as bookmarks: [null] or [{}]. The bar would throw drawing one,
            // and this is still somebody's file, so it is held to the same rule.
            if (items.Any(b => b is null || string.IsNullOrWhiteSpace(b.Url) || b.Title is null))
                throw new JsonException("an entry has no url or title");
            return (items, false);
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"bookmarks.json could not be read, so bookmark changes are not saved this run: {ex.Message}");
            return ([], true);
        }
    }

    /// <summary>
    /// Makes a change and writes it, or puts the list back and throws when it cannot be
    /// written. Changing the list first and writing second left memory and disk
    /// disagreeing on a failed write: the bookmark gone from the bar, still in the file,
    /// and back at the next start. Written through a temporary file, so a crash mid-write
    /// cannot leave half a list either.
    /// </summary>
    private void Commit(Action change)
    {
        if (_unreadable)
            throw new UnreadableException();
        var before = _items.ToList();
        change();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Settings.WriteAtomically(_path, JsonSerializer.Serialize(_items, JsonOptions));
        }
        catch
        {
            _items.Clear();
            _items.AddRange(before);
            throw;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
