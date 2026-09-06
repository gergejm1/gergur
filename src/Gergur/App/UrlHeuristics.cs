namespace Gergur.App;

public static class UrlHeuristics
{
    private static readonly string[] NavigableSchemes =
        ["http", "https", "file", "about", "edge", "data", "view-source"];

    /// <summary>Turns address-bar input into something navigable: URL as-is, host → https, else search.</summary>
    public static string ToNavigableUrl(string input, string searchUrlTemplate)
    {
        input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute)
            && NavigableSchemes.Contains(absolute.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return input;
        }

        // "example.com/path", "localhost:3000", "192.168.1.1" - a host, not a query
        bool looksLikeHost = !input.Contains(' ')
            && (input.Contains('.') || input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
        if (looksLikeHost && input.Length > 0)
            return "https://" + input;

        return string.Format(searchUrlTemplate, Uri.EscapeDataString(input));
    }
}
