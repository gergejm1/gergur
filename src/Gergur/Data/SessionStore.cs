using System.Text.Json;
using Gergur.App;

namespace Gergur.Data;

public sealed record SessionTab(string Url, string Title);
public sealed record SessionWindow(List<SessionTab> Tabs, int ActiveIndex);

/// <summary>
/// Saves the open windows on exit. On restore, background tabs come back as Discarded
/// snapshots - they hold zero processes until clicked, so startup stays cheap no
/// matter how many windows or tabs were open.
/// </summary>
public static class SessionStore
{
    private static readonly string FilePath = Path.Combine(Settings.DataDir, "session.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record SessionFile(List<SessionWindow>? Windows);
    /// <summary>The pre-multi-window shape: a bare tab list. Still read, never written.</summary>
    private sealed record LegacySessionFile(List<SessionTab>? Tabs, int ActiveIndex);

    /// <summary>Windows to restore, oldest first. Empty when there is nothing to restore.</summary>
    public static IReadOnlyList<SessionWindow> Load() => LoadFrom(FilePath);

    internal static IReadOnlyList<SessionWindow> LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Array.Empty<SessionWindow>();
            string json = File.ReadAllText(path);

            var file = JsonSerializer.Deserialize<SessionFile>(json);
            if (file?.Windows is { Count: > 0 } windows)
                return windows;

            var legacy = JsonSerializer.Deserialize<LegacySessionFile>(json);
            if (legacy?.Tabs is { Count: > 0 } tabs)
                return [new SessionWindow(tabs, legacy.ActiveIndex)];
        }
        catch { }
        return Array.Empty<SessionWindow>();
    }

    public static void Save(IReadOnlyList<SessionWindow> windows)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SessionFile(windows.ToList()), JsonOptions));
        }
        catch { }
    }
}
