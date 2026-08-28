namespace Gergur.Blocking;

/// <summary>
/// Loads the page-level cleanup script (cosmetic filtering + YouTube ad
/// neutralizer) injected into every document at creation time.
/// </summary>
public static class PageCleanup
{
    public static string? Script { get; } = Load();

    private static string? Load()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "adblock.js");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
