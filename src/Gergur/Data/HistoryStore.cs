using System.Text.Json;
using Gergur.App;

namespace Gergur.Data;

/// <summary>Append-only JSONL log - greppable, no database, no UI needed for v1.</summary>
public sealed class HistoryStore
{
    public static readonly string FilePath = Path.Combine(Settings.DataDir, "history.jsonl");

    private sealed record Entry(DateTime T, string Url, string Title);

    public void Append(string url, string title)
    {
        if (HomePage.IsHome(url))
            return;
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(new Entry(DateTime.UtcNow, url, title)) + Environment.NewLine);
        }
        catch
        {
            // History is best-effort; never block browsing over it.
        }
    }
}
