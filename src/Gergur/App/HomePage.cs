namespace Gergur.App;

public static class HomePage
{
    public static string Url { get; } =
        new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "home.html")).AbsoluteUri;

    /// <summary>True for the built-in home page and other "nothing here" urls.</summary>
    public static bool IsHome(string? url)
        => string.IsNullOrEmpty(url)
        || url == "about:blank"
        || (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && url.EndsWith("/assets/home.html", StringComparison.OrdinalIgnoreCase));
}
