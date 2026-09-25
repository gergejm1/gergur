using System.Text.Json;
using Gergur.Data;
using Microsoft.Web.WebView2.Core;

namespace Gergur.App;

/// <summary>
/// The browser's own pages: the new tab page, History, Downloads and Bookmarks. They are
/// files in Assets shown in a tab like any page, and they reach the browser only through
/// web messages, which this answers.
///
/// Every page in a WebView2 can post web messages, including any site the person visits,
/// so a message is only treated as a request when it comes from one of these files, by
/// exact path, and each page may only ask for what it shows. A site cannot read history
/// by posting "history.list"; the history page can, and the new tab page cannot.
/// </summary>
public static class InternalPages
{
    public enum Page { Home, History, Downloads, Bookmarks }

    private static readonly string AssetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string UrlOf(Page page) => new Uri(PathOf(page)).AbsoluteUri;

    private static string PathOf(Page page) => Path.Combine(AssetsDir, page switch
    {
        Page.Home => "home.html",
        Page.History => "history.html",
        Page.Downloads => "downloads.html",
        _ => "bookmarks.html",
    });

    /// <summary>
    /// Which of the browser's pages this url is, or null for anything else. The file must
    /// be the one in this install's Assets folder: a page of the same name anywhere else on
    /// disk is just a file. The query and fragment are ignored.
    /// </summary>
    public static Page? Identify(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile)
            return null;
        string path;
        try { path = Path.GetFullPath(uri.LocalPath); }
        catch { return null; }
        foreach (var page in Enum.GetValues<Page>())
        {
            if (string.Equals(path, PathOf(page), StringComparison.OrdinalIgnoreCase))
                return page;
        }
        return null;
    }

    /// <summary>
    /// What the address bar shows for one of these pages, instead of a file path into the
    /// build output. The new tab page shows nothing, so the address bar is ready to type in.
    /// </summary>
    public static string AddressOf(Page page) => page == Page.Home ? "" : $"gergur://{page.ToString().ToLowerInvariant()}";

    /// <summary>
    /// A url as the agent API reports it: one of these pages by its gergur:// name, the new
    /// tab page included, and anything else as it is.
    /// </summary>
    public static string NameOf(string url) => Identify(url) switch
    {
        null => url,
        Page.Home => "gergur://newtab",
        { } page => AddressOf(page),
    };

    /// <summary>The page a typed "gergur://history" means, or null when it is not one.</summary>
    public static string? Resolve(string typed)
    {
        typed = typed.Trim().TrimEnd('/');
        foreach (var page in Enum.GetValues<Page>())
        {
            if (page != Page.Home && string.Equals(typed, AddressOf(page), StringComparison.OrdinalIgnoreCase))
                return UrlOf(page);
        }
        return string.Equals(typed, "gergur://newtab", StringComparison.OrdinalIgnoreCase) ? UrlOf(Page.Home) : null;
    }

    /// <summary>Whether this page may ask for this operation.</summary>
    internal static bool Allowed(Page page, string op)
    {
        if (op == "open")
            return true;
        return page switch
        {
            Page.Home => op == "bookmarks.list",
            Page.History => op.StartsWith("history.", StringComparison.Ordinal),
            Page.Downloads => op.StartsWith("downloads.", StringComparison.Ordinal),
            Page.Bookmarks => op.StartsWith("bookmarks.", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Addresses a page may open. Not javascript: or data:, which would run in whatever tab
    /// they land in. Nor a file on another machine: opening file://server/share connects to
    /// it and offers Windows credentials.
    /// </summary>
    internal static bool Openable(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeFile && !uri.IsUnc));

    /// <summary>What a page's requests act on, passed in so a test can supply its own.</summary>
    internal sealed record Services(
        HistoryStore History,
        BookmarkStore Bookmarks,
        DownloadManager Downloads,
        Action<string, bool> Open);

    /// <summary>
    /// Answers one request from one of these pages, as the json posted back to it:
    /// {id, ok: true, result} or {id, ok: false, error}. Anything malformed is answered
    /// as an error rather than ignored, so the page can say so instead of waiting.
    /// </summary>
    internal static string Answer(Page page, string requestJson, Services services)
    {
        long id = 0;
        try
        {
            using var request = JsonDocument.Parse(requestJson);
            var root = request.RootElement;
            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsed))
                id = parsed;
            string op = root.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String
                ? opElement.GetString()!
                : "";
            var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;

            if (!Allowed(page, op))
                return Reply(id, error: $"this page cannot ask for {op}");
            return Reply(id, result: Run(op, args, services));
        }
        catch (PageRequestException refused)
        {
            return Reply(id, error: refused.Message);
        }
        catch (BookmarkStore.UnreadableException refused)
        {
            return Reply(id, error: refused.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A bookmarks file that could not be written. Answered, so the page says so
            // rather than waiting five seconds and reporting that the browser never replied.
            return Reply(id, error: "Gergur could not save that change: something else has the file open, or it is read-only.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Reply(id, error: "that request was not understood");
        }
        catch (Exception ex)
        {
            // Anything else still gets an answer: a page left without one waits five seconds
            // and then blames the browser for not replying.
            Diagnostics.DebugLog.WriteAlways($"a page request failed: {ex.Message}");
            return Reply(id, error: "Gergur could not do that.");
        }
    }

    private static object Run(string op, JsonElement args, Services s)
    {
        switch (op)
        {
            case "open":
            {
                string url = Text(args, "url");
                if (!Openable(url))
                    throw new PageRequestException("that address cannot be opened from here");
                s.Open(url, Flag(args, "newTab"));
                return new { };
            }

            case "history.list":
            {
                string? q = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("q", out var qe) && qe.ValueKind == JsonValueKind.String
                    ? qe.GetString()
                    : null;
                int max = Math.Clamp(Number(args, "max") ?? 500, 1, 5000);
                var visits = s.History.TryRead(string.IsNullOrWhiteSpace(q) ? null : q.Trim(), max)
                    ?? throw new PageRequestException("History could not be read: something else has the file open. Try again in a moment.");
                return visits
                    // The browser's own pages are not places the person went.
                    .Where(v => Identify(v.Url) is null)
                    .Select(v => new { url = v.Url, title = v.Title, visitedUtc = Iso(v.VisitedUtc) })
                    .ToArray();
            }
            case "history.remove":
            {
                var urls = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("urls", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToArray()
                    : [];
                int removed = s.History.Remove(urls);
                if (removed < 0)
                    throw new PageRequestException("History could not be changed: something else has the file open. Try again in a moment.");
                return new { removed };
            }
            case "history.clear":
                if (!s.History.Clear())
                    throw new PageRequestException("History could not be cleared: something else has the file open. Try again in a moment.");
                return new { };

            case "bookmarks.list":
                // An empty list here would read "No bookmarks yet" and invite the one thing
                // that will be refused.
                if (s.Bookmarks.Unreadable)
                    throw new PageRequestException($"bookmarks.json could not be read when Gergur started, so nothing here is saved this run. {BookmarkStore.WhatToDo}");
                return s.Bookmarks.Items
                    .Select(b => new { url = b.Url, title = b.Title, addedUtc = Iso(b.AddedUtc) })
                    .ToArray();
            case "bookmarks.remove":
                s.Bookmarks.Remove(Text(args, "url"));
                return new { };
            case "bookmarks.add":
                // The bookmarks page's Undo. Nothing else offers it: a page that could add
                // any url as a bookmark is the Undo's case, not a new feature.
                if (!s.Bookmarks.Insert(Text(args, "url"), Text(args, "title"), Number(args, "index") ?? s.Bookmarks.Items.Count))
                    throw new PageRequestException("that bookmark could not be put back");
                return new { };
            case "bookmarks.rename":
                if (!s.Bookmarks.Rename(Text(args, "url"), Text(args, "title")))
                    throw new PageRequestException("that bookmark could not be renamed");
                return new { };

            case "downloads.list":
                return s.Downloads.Items.Select(Describe).ToArray();
            case "downloads.open":
                if (!DownloadOf(args, s).Open())
                    throw new PageRequestException("That file is no longer where it was saved. It may have been moved or deleted.");
                return new { };
            case "downloads.showInFolder":
                if (!DownloadOf(args, s).ShowInFolder())
                    throw new PageRequestException("That file and its folder are no longer there.");
                return new { };
            case "downloads.cancel":
                DownloadOf(args, s).Cancel();
                return new { };
            case "downloads.clearFinished":
                s.Downloads.ClearFinished();
                return new { };

            default:
                throw new PageRequestException($"there is no {op}");
        }
    }

    /// <summary>One download as the page shows it. Also what a "downloads" push carries.</summary>
    internal static object Describe(DownloadItem item) => new
    {
        id = item.Id,
        fileName = item.FileName,
        // Clipped: a data: download carries its whole file in the url.
        url = item.Uri.Length > 2048 ? item.Uri[..2048] : item.Uri,
        state = item.State switch
        {
            CoreWebView2DownloadState.Completed => "completed",
            CoreWebView2DownloadState.Interrupted => "interrupted",
            _ => "inProgress",
        },
        received = item.BytesReceived,
        total = item.TotalBytes,
        startedUtc = Iso(item.StartedUtc),
        status = item.Describe(),
    };

    private static DownloadItem DownloadOf(JsonElement args, Services s)
        => Number(args, "id") is { } id && s.Downloads.Find(id) is { } item
            ? item
            : throw new PageRequestException("that download is no longer in the list");

    private static string Text(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()!
            : throw new PageRequestException($"{name} is required");

    private static bool Flag(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.True;

    private static int? Number(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)
            ? n
            : null;

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O");

    private static string Reply(long id, object? result = null, string? error = null)
        => error is null
            ? JsonSerializer.Serialize(new { id, ok = true, result })
            : JsonSerializer.Serialize(new { id, ok = false, error });

    /// <summary>
    /// The agent API reached one of these pages. Thrown by the tab where a script or capture
    /// would run, and answered by the agent server with <see cref="Status"/>.
    /// </summary>
    /// <param name="leaving">The tab is on its way from one of these pages to somewhere else,
    /// which is where every new window and blank tab starts. That is "not yet", not "never",
    /// and saying "cannot be read" there sent an agent away from a site it could read.</param>
    public sealed class OwnPageRefusedException(bool leaving = false) : Exception(leaving ? LeavingText : Text)
    {
        public const string Text = "the browser's own pages (history, downloads, bookmarks, the new tab page) cannot be read or scripted through this API";
        public const string LeavingText = "that tab is still leaving one of the browser's own pages, which cannot be read or scripted through this API; "
            + "navigate with wait: true, or try again once the new page has loaded. "
            + "If that navigation does not produce a page (a download, say), navigate the tab somewhere else";

        public bool Leaving { get; } = leaving;

        /// <summary>503 for a tab still leaving, like the other not-ready answers; 403 otherwise.</summary>
        public int Status => Leaving ? 503 : 403;
    }

    /// <summary>A request refused with a reason the page can show.</summary>
    private sealed class PageRequestException(string message) : Exception(message);
}
