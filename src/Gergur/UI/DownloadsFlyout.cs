using Gergur.App;
using Microsoft.Web.WebView2.Core;

namespace Gergur.UI;

/// <summary>
/// The panel that drops from the downloads button: the latest downloads with their
/// progress, Open (click the row) and Show in folder, Cancel while running, and a way
/// through to the full downloads page.
///
/// It opens by itself when a download starts in its window, so that a download is seen
/// to have happened, and closes by itself a few seconds later unless the pointer is on
/// it. Opened that way it never takes the keyboard (see <see cref="FlyoutPanel"/>).
/// Opened from the button it stays until something else is clicked.
/// </summary>
internal sealed class DownloadsFlyout : FlyoutPanel
{
    /// <summary>How many downloads the panel lists; the rest are on the page.</summary>
    internal const int MaxRows = 5;

    /// <summary>How long a panel that opened by itself stays, and how long after the pointer leaves it.</summary>
    internal static readonly TimeSpan AutoHideAfter = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan AutoHideAfterHover = TimeSpan.FromSeconds(3);

    private readonly DownloadManager _downloads;
    private readonly Action _showAll;
    private readonly Action<string> _say;
    private Font _bold = null!;
    private Font _small = null!;
    private Font _icons = null!;
    private readonly List<Row> _rows = new();
    private HotSpot? _showAllSpot;
    private Rectangle _header;
    private DateTime? _autoHideAtUtc;
    // Whether each finished file has gone from disk, asked once per opening rather than on
    // every paint: a running download repaints several times a second, and a file on a
    // slow or sleeping drive would stall the window each time.
    private readonly Dictionary<DownloadItem, bool> _missing = new();

    private sealed record Row(DownloadItem Item, Rectangle Bounds, HotSpot Main, HotSpot? Action, string ActionGlyph);

    public DownloadsFlyout(DownloadManager downloads, Action showAll, Action<string> say)
    {
        _downloads = downloads;
        _showAll = showAll;
        _say = say;
        EnsureFonts();
        AccessibleName = "Downloads";
    }

    protected override bool TakesFocus => false;

    protected override void MakeFonts()
    {
        base.MakeFonts();
        _bold?.Dispose();
        _small?.Dispose();
        _icons?.Dispose();
        _bold = MakeFont("Segoe UI Semibold", 10.5f);
        _small = MakeFont("Segoe UI", 8.5f);
        _icons = MakeIconFont(10f);
    }

    /// <summary>Opened from the button: stays until something else is clicked.</summary>
    public void ShowFromButton(Form owner, Control button)
    {
        _autoHideAtUtc = null;
        if (!Visible)
            _missing.Clear();
        ShowFrom(owner, button);
    }

    /// <summary>Opened by a download starting: goes again by itself unless it is being looked at.</summary>
    public void ShowForNewDownload(Form owner, Control button)
    {
        // Already open from the button: leave it that way rather than putting a timer on it.
        if (!Visible)
        {
            _autoHideAtUtc = DateTime.UtcNow + AutoHideAfter;
            _missing.Clear();
        }
        ShowFrom(owner, button);
    }

    protected override void OnWatch()
    {
        if (_autoHideAtUtc is not { } at)
            return;
        if (Bounds.Contains(Cursor.Position))
        {
            _autoHideAtUtc = DateTime.UtcNow + AutoHideAfterHover;
            return;
        }
        if (DateTime.UtcNow >= at)
            Dismiss();
    }

    protected override Size LayOut()
    {
        _rows.Clear();
        int width = S(360);
        int pad = S(8);
        int y = pad;

        _header = new Rectangle(S(16), y, width - S(32), S(36));
        y += _header.Height;

        var items = _downloads.Items;
        int rowHeight = S(64);
        if (items.Count == 0)
        {
            y += rowHeight;
        }
        else
        {
            foreach (var item in items.Take(MaxRows))
            {
                var bounds = new Rectangle(pad, y, width - pad * 2, rowHeight);
                var buttonBox = new Rectangle(bounds.Right - S(8) - S(32), bounds.Y + (rowHeight - S(32)) / 2, S(32), S(32));
                var captured = item;
                string name = captured.FileName;
                HotSpot main;
                HotSpot? action = null;
                string glyph = "";
                if (captured.State == CoreWebView2DownloadState.Completed)
                {
                    main = new HotSpot(new Rectangle(bounds.X, bounds.Y, buttonBox.X - bounds.X - S(4), rowHeight),
                        () => { if (!captured.Open()) _say($"{name} could not be opened. It may have been moved or deleted."); },
                        $"Open {name}");
                    action = new HotSpot(buttonBox,
                        () => { if (!captured.ShowInFolder()) _say($"{name} is no longer where it was saved."); },
                        $"Show {name} in folder", Tip: "Show in folder");
                    glyph = FolderGlyph;
                }
                else if (captured.State == CoreWebView2DownloadState.InProgress)
                {
                    main = new HotSpot(new Rectangle(bounds.X, bounds.Y, buttonBox.X - bounds.X - S(4), rowHeight), null, name);
                    action = new HotSpot(buttonBox, captured.Cancel, $"Cancel {name}", Tip: "Cancel", KeepOpen: true);
                    glyph = Glyphs.Cancel;
                }
                else
                {
                    main = new HotSpot(bounds, null, name);
                }
                AddSpot(main);
                if (action is not null)
                    AddSpot(action);
                _rows.Add(new Row(captured, bounds, main, action, glyph));
                y += rowHeight;
            }
        }

        y += S(6);
        int more = Math.Max(0, items.Count - MaxRows);
        _showAllSpot = new HotSpot(new Rectangle(pad, y, width - pad * 2, S(36)), _showAll,
            more > 0 ? $"Show all downloads ({items.Count})" : "Show all downloads");
        AddSpot(_showAllSpot);
        y += _showAllSpot.Bounds.Height + pad;
        return new Size(width, y);
    }

    /// <summary>
    /// The open folder seen from the front (OpenFolderHorizontal), in both the Windows 10
    /// and 11 icon fonts. Not E838 or E8B7: rendered here, those are the folder seen edge on,
    /// a tall thin shape nobody reads as a folder at this size.
    /// </summary>
    private const string FolderGlyph = "\uED25";

    protected override void PaintContent(Graphics g)
    {
        int running = _downloads.RunningCount;
        DrawLabel(g, "Downloads", _bold, _header, Theme.Text);
        if (running > 0)
            DrawLabel(g, running == 1 ? "1 in progress" : $"{running} in progress", _small, _header, Theme.TextDim, right: true);

        if (_rows.Count == 0)
        {
            var empty = new Rectangle(S(16), _header.Bottom, Width - S(32), S(64));
            DrawLabel(g, "No downloads yet. Files you download show up here.", Font, empty, Theme.TextDim, center: true);
        }

        var now = DateTime.UtcNow;
        foreach (var row in _rows)
            PaintRow(g, row, now);

        if (_showAllSpot is { } all)
        {
            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, S(12), all.Bounds.Top - S(4), Width - S(12), all.Bounds.Top - S(4));
            PaintHover(g, all);
            var label = new Rectangle(all.Bounds.X + S(8), all.Bounds.Y, all.Bounds.Width - S(16), all.Bounds.Height);
            DrawLabel(g, all.Spoken, Font, label, Theme.Text);
            DrawLabel(g, "Ctrl+J", _small, label, Theme.TextDim, right: true);
        }
    }

    private void PaintRow(Graphics g, Row row, DateTime now)
    {
        var item = row.Item;
        PaintHover(g, row.Main, row.Bounds);

        // The badge: the file's extension on a colour taken from it, as the downloads page
        // draws it, so the same file looks the same in both places.
        string ext = DownloadText.Extension(item.FileName);
        var badge = new Rectangle(row.Bounds.X + S(8), row.Bounds.Y + (row.Bounds.Height - S(36)) / 2, S(36), S(36));
        int hue = DownloadText.Hue(ext.Length > 0 ? ext : "file");
        using (var path = Rounded(badge, S(9)))
        using (var fill = new SolidBrush(DownloadText.FromHsl(hue, 0.34, 0.23)))
            g.FillPath(fill, path);
        DrawLabel(g, ext.Length > 0 ? ext : "FILE", _small, badge, DownloadText.FromHsl(hue, 0.75, 0.88), center: true);

        int textLeft = badge.Right + S(12);
        int textRight = (row.Action?.Bounds.Left ?? row.Bounds.Right) - S(8);
        var nameBox = new Rectangle(textLeft, row.Bounds.Y + S(10), textRight - textLeft, S(20));
        bool interrupted = item.State == CoreWebView2DownloadState.Interrupted;
        DrawLabel(g, item.FileName, Font, nameBox, interrupted ? Theme.TextDim : Theme.Text);

        var statusBox = new Rectangle(textLeft, row.Bounds.Y + S(34), textRight - textLeft, S(18));
        if (item.IsRunning)
        {
            // A thin bar over the status line, the page's own shape.
            var bar = new Rectangle(textLeft, row.Bounds.Y + S(32), textRight - textLeft, S(4));
            using (var track = new SolidBrush(Theme.TabActive))
                g.FillRectangle(track, bar);
            double? fraction = DownloadText.Fraction(item.BytesReceived, item.TotalBytes);
            var done = fraction is { } f
                ? new Rectangle(bar.X, bar.Y, (int)Math.Round(bar.Width * f), bar.Height)
                : new Rectangle(bar.X, bar.Y, bar.Width / 3, bar.Height);   // size unknown: a part, not a lie
            using (var accent = new SolidBrush(Theme.Accent))
                g.FillRectangle(accent, done);
            statusBox.Y += S(4);
        }
        bool missing = IsMissing(item);
        DrawLabel(g, DownloadText.Status(item.State, item.BytesReceived, item.TotalBytes, item.Uri, item.StartedUtc, now, missing),
            _small, statusBox, interrupted ? Theme.Accent : Theme.TextDim);

        if (row.Action is { } action)
        {
            PaintHover(g, action);
            DrawLabel(g, row.ActionGlyph, _icons, action.Bounds, Theme.Text, center: true);
        }
    }

    /// <summary>A finished download whose file is no longer there. See <see cref="_missing"/>.</summary>
    internal bool IsMissing(DownloadItem item)
    {
        if (item.State != CoreWebView2DownloadState.Completed || item.FilePath.Length == 0)
            return false;
        if (!_missing.TryGetValue(item, out bool missing))
            _missing[item] = missing = !File.Exists(item.FilePath);
        return missing;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold.Dispose();
            _small.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The words and colours a download is shown with, apart from any window so they can be
/// tested. Kept in step with the downloads page's own helpers in bridge.js (extensionOf,
/// hueOf, siteOf), so the panel and the page describe a file the same way.
/// </summary>
internal static class DownloadText
{
    /// <summary>The extension as a badge shows it: one to four letters or digits, upper case, or nothing.</summary>
    internal static string Extension(string fileName)
    {
        string name = Path.GetFileName(fileName ?? "");
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return "";
        string ext = name[(dot + 1)..];
        if (ext.Length > 4 || !ext.All(char.IsAsciiLetterOrDigit))
            return "";
        return ext.ToUpperInvariant();
    }

    /// <summary>FNV-1a of the text, as a hue: the same number bridge.js's hueOf gives.</summary>
    internal static int Hue(string text)
    {
        uint h = 0x811c9dc5;
        foreach (char c in text ?? "")
        {
            h ^= c;
            h = unchecked(h * 0x01000193);
        }
        return (int)(h % 360);
    }

    /// <summary>A colour from hue (degrees), saturation and lightness (0 to 1), as CSS hsl() reads them.</summary>
    internal static Color FromHsl(int hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double hp = (hue % 360 + 360) % 360 / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        double m = lightness - c / 2;
        return Color.FromArgb(
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }

    /// <summary>How far along, or null when the size is not known.</summary>
    internal static double? Fraction(long received, long total)
        => total > 0 ? Math.Clamp(received / (double)total, 0, 1) : null;

    /// <summary>The site a download came from, without "www.", or nothing for a data: or blob: download.</summary>
    internal static string Site(string url)
    {
        if (!System.Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "";
        string host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    /// <summary>How long ago, in the fewest words: "Just now", "5 min ago", "3 h ago", "Yesterday", then a date.</summary>
    internal static string Ago(DateTime startedUtc, DateTime nowUtc)
    {
        var age = nowUtc - startedUtc;
        if (age < TimeSpan.FromMinutes(1))
            return "Just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromHours(24))
            return $"{(int)age.TotalHours} h ago";
        var local = startedUtc.ToLocalTime();
        return age < TimeSpan.FromHours(48)
            ? "Yesterday"
            : local.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The second line of a download. Running: how much of how much, and from where.
    /// Finished: its size, where from, when. Stopped: that it stopped, which is the thing to
    /// know, before anything else.
    /// </summary>
    internal static string Status(CoreWebView2DownloadState state, long received, long total, string url,
        DateTime startedUtc, DateTime nowUtc, bool missing)
    {
        string site = Site(url);
        string from = site.Length > 0 ? " \u00B7 " + site : "";
        switch (state)
        {
            case CoreWebView2DownloadState.InProgress:
                string amount = total > 0
                    ? $"{DownloadItem.Size(received)} of {DownloadItem.Size(total)}"
                    : DownloadItem.Size(received);
                return amount + from;
            case CoreWebView2DownloadState.Interrupted:
                return "Failed or cancelled" + from;
            default:
                if (missing)
                    return "Deleted or moved" + from;
                string size = DownloadItem.Size(total > 0 ? total : received);
                return size + from + " \u00B7 " + Ago(startedUtc, nowUtc);
        }
    }
}
