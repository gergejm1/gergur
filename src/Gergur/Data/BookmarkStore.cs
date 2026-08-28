using System.Text.Json;
using Gergur.App;

namespace Gergur.Data;

public sealed record Bookmark(string Url, string Title, DateTime AddedUtc);

public sealed class BookmarkStore
{
    private static readonly string FilePath = Path.Combine(Settings.DataDir, "bookmarks.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<Bookmark> _items;

    public BookmarkStore()
    {
        _items = Load();
    }

    public IReadOnlyList<Bookmark> Items => _items;

    public bool Contains(string url) => _items.Any(b => b.Url == url);

    /// <summary>Returns true when the page is now bookmarked, false when it was removed.</summary>
    public bool Toggle(string url, string title)
    {
        int removed = _items.RemoveAll(b => b.Url == url);
        if (removed == 0)
            _items.Add(new Bookmark(url, title, DateTime.UtcNow));
        Save();
        return removed == 0;
    }

    private static List<Bookmark> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch { }
        return [];
    }

    private void Save()
    {
        Directory.CreateDirectory(Settings.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_items, JsonOptions));
    }
}
