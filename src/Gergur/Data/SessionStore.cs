using System.Text.Json;
using Gergur.App;

namespace Gergur.Data;

public sealed record SessionTab(string Url, string Title);
public sealed record SessionData(List<SessionTab> Tabs, int ActiveIndex);

/// <summary>
/// Saves open tabs on exit. On restore, background tabs come back as Discarded
/// snapshots - they hold zero processes until clicked, so startup stays cheap.
/// </summary>
public static class SessionStore
{
    private static readonly string FilePath = Path.Combine(Settings.DataDir, "session.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SessionData? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(FilePath));
        }
        catch { }
        return null;
    }

    public static void Save(SessionData session)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(session, JsonOptions));
        }
        catch { }
    }
}
