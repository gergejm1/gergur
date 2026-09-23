namespace Gergur.App;

public static class UrlHeuristics
{
    private static readonly string[] NavigableSchemes =
        ["http", "https", "file", "about", "edge", "data", "view-source"];

    /// <summary>Turns address-bar input into something navigable: URL as-is, host → https, else search.</summary>
    public static string ToNavigableUrl(string input, string? searchUrlTemplate)
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

        return Search(input, searchUrlTemplate);
    }

    /// <summary>
    /// The search url for a query, falling back to the built-in template when the
    /// configured one cannot produce one.
    ///
    /// string.Format throws on a template that names an argument it was not given, and
    /// this is called straight from the address bar's Enter key with no try/catch above
    /// it and no handler for an unhandled exception anywhere in the app. So a settings
    /// file holding "https://x/?q={1}" turned every search the user typed into a crash
    /// dialog, and it survived restarts because it was in the file. Hand-editing the json
    /// was the only way back.
    ///
    /// Falling back rather than throwing means a wrong template costs a search that went
    /// somewhere unexpected, not a browser that cannot be typed into.
    /// </summary>
    public static string Search(string query, string? searchUrlTemplate)
    {
        string escaped = Uri.EscapeDataString(query);

        // Usable, not merely formattable. Catching FormatException alone let through a
        // template that formats fine and is not a url: "{0}" produces the bare query, the
        // engine refuses it, and every search is a silent no-op with nothing said, which
        // is the browser-you-cannot-type-into this is here to prevent.
        return IsUsableSearchTemplate(searchUrlTemplate)
            ? string.Format(searchUrlTemplate!, escaped)
            : string.Format(DefaultSearchUrlTemplate, escaped);
    }

    /// <summary>What a search falls back to, and what a new settings file starts with.</summary>
    public const string DefaultSearchUrlTemplate = "https://www.google.com/search?q={0}";

    /// <summary>
    /// Whether a template can be used to search. The settings dialog has a person looking
    /// at what they typed; the agent API does not, and a template it accepts is persisted
    /// and used for every search the human types afterwards.
    /// </summary>
    public static bool IsUsableSearchTemplate(string? template)
    {
        if (template is null)
            return false;

        const string probe = "gergur-probe";
        string formatted;
        try
        {
            formatted = string.Format(template, probe);
        }
        catch (FormatException)
        {
            return false;
        }

        // The query has to actually appear in the result. Comparing against the template
        // instead was not enough: "{{0}}" formats to the literal "{0}", so the string
        // changed while the query was thrown away, and every search would have gone to
        // one fixed page carrying nothing.
        return formatted.Contains(probe, StringComparison.Ordinal)
            && formatted.Length <= 2048
            && Uri.TryCreate(formatted, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
