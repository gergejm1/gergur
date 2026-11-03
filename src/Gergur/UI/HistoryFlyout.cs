using Gergur.Data;

namespace Gergur.UI;

/// <summary>
/// The panel the menu's History tile drops: the last few places visited, each opening in
/// the tab in front, and a way through to the full history page. Drawn as the downloads
/// panel is, so the two read as one family.
///
/// The history log is read whole on every read, so it is read off the UI thread: the
/// panel opens at once saying so, and fills in when the read comes back.
/// </summary>
internal sealed class HistoryFlyout : FlyoutPanel
{
    /// <summary>How many places the panel lists; the rest are on the page.</summary>
    internal const int MaxRows = 8;

    private readonly Func<IReadOnlyList<HistoryVisit>?> _read;
    private readonly Action<string> _open;
    private readonly Action _showAll;
    private Font _bold = null!;
    private Font _small = null!;
    private IReadOnlyList<HistoryVisit>? _visits;
    private bool _loading;
    private bool _failed;
    private int _readId;
    private Rectangle _header;
    private Rectangle _message;
    private readonly List<(HistoryVisit Visit, HotSpot Spot)> _rows = new();

    /// <param name="read">Reads the newest visits, or null when the file cannot be read. Runs off the UI thread.</param>
    /// <param name="open">Opens a visited address in the tab in front.</param>
    /// <param name="showAll">Opens the history page.</param>
    public HistoryFlyout(Func<IReadOnlyList<HistoryVisit>?> read, Action<string> open, Action showAll)
    {
        _read = read;
        _open = open;
        _showAll = showAll;
        EnsureFonts();
        AccessibleName = "History";
    }

    protected override bool TakesFocus => true;

    protected override void MakeFonts()
    {
        base.MakeFonts();
        _bold?.Dispose();
        _small?.Dispose();
        _bold = MakeFont("Segoe UI Semibold", 10.5f);
        _small = MakeFont("Segoe UI", 8.5f);
    }

    /// <summary>Shows the panel from the button and reads the history afresh.</summary>
    public void ShowFromButton(Form owner, Control button)
    {
        _loading = true;
        _failed = false;
        ShowFrom(owner, button);
        int id = ++_readId;
        Task.Run(_read).ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            BeginInvoke(() =>
            {
                // A later opening has its own read on the way; this one is stale.
                if (id != _readId || IsDisposed)
                    return;
                Fill(t.IsCompletedSuccessfully ? t.Result : null);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Puts the visits read into the panel. Null: the file could not be read.</summary>
    internal void Fill(IReadOnlyList<HistoryVisit>? visits)
    {
        _loading = false;
        _failed = visits is null;
        _visits = visits is null ? null : Recent(visits, MaxRows);
        if (Visible)
            Relayout();
    }

    /// <summary>
    /// The newest <paramref name="max"/> places from a newest-first list: web pages only,
    /// each address once. The browser's own pages are file addresses, so asking for http
    /// and https leaves them out along with files and data addresses.
    /// </summary>
    internal static IReadOnlyList<HistoryVisit> Recent(IReadOnlyList<HistoryVisit> visits, int max)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HistoryVisit>(max);
        foreach (var visit in visits)
        {
            if (!Uri.TryCreate(visit.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !seen.Add(visit.Url))
                continue;
            result.Add(visit);
            if (result.Count == max)
                break;
        }
        return result;
    }

    protected override Size LayOut()
    {
        _rows.Clear();
        int width = S(380);
        int pad = S(8);
        int y = pad;
        _header = new Rectangle(S(16), y, width - S(32), S(36));
        y += _header.Height;

        int rowHeight = S(48);
        var visits = _visits ?? [];
        if (_loading && visits.Count == 0 || _failed || visits.Count == 0)
        {
            _message = new Rectangle(S(16), y, width - S(32), S(56));
            y += _message.Height;
        }
        else
        {
            _message = Rectangle.Empty;
            foreach (var visit in visits)
            {
                string url = visit.Url;
                string name = string.IsNullOrWhiteSpace(visit.Title) ? url : visit.Title;
                var spot = new HotSpot(new Rectangle(pad, y, width - pad * 2, rowHeight), () => _open(url), $"Open {name}");
                AddSpot(spot);
                _rows.Add((visit, spot));
                y += rowHeight;
            }
        }

        y += S(6);
        var all = new HotSpot(new Rectangle(pad, y, width - pad * 2, S(36)), _showAll, "Show all history");
        AddSpot(all);
        y += all.Bounds.Height + pad;
        return new Size(width, y);
    }

    protected override void PaintContent(Graphics g)
    {
        DrawLabel(g, "History", _bold, _header, Theme.Text);
        if (_message != Rectangle.Empty)
        {
            string text = _failed ? "History could not be read just now. Try again in a moment."
                : _loading ? "Reading history..."
                : "No history yet. Pages you visit show up here.";
            DrawLabel(g, text, Font, _message, Theme.TextDim, center: true);
        }

        var now = DateTime.UtcNow;
        foreach (var (visit, spot) in _rows)
            PaintRow(g, visit, spot, now);

        var all = Spots[^1];
        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, S(12), all.Bounds.Top - S(4), Width - S(12), all.Bounds.Top - S(4));
        PaintHover(g, all);
        var label = new Rectangle(all.Bounds.X + S(8), all.Bounds.Y, all.Bounds.Width - S(16), all.Bounds.Height);
        DrawLabel(g, all.Spoken, Font, label, Theme.Text);
        DrawLabel(g, "Ctrl+H", _small, label, Theme.TextDim, right: true);
    }

    private void PaintRow(Graphics g, HistoryVisit visit, HotSpot spot, DateTime now)
    {
        PaintHover(g, spot);
        var r = spot.Bounds;
        string site = DownloadText.Site(visit.Url);
        // A letter on a colour taken from the site, so the same site looks the same each time.
        var badge = new Rectangle(r.X + S(8), r.Y + (r.Height - S(28)) / 2, S(28), S(28));
        int hue = DownloadText.Hue(site.Length > 0 ? site : visit.Url);
        using (var path = Rounded(badge, S(7)))
        using (var fill = new SolidBrush(DownloadText.FromHsl(hue, 0.34, 0.23)))
            g.FillPath(fill, path);
        string letter = site.Length > 0 ? char.ToUpperInvariant(site[0]).ToString() : "?";
        DrawLabel(g, letter, _bold, badge, DownloadText.FromHsl(hue, 0.75, 0.88), center: true);

        int left = badge.Right + S(12);
        var titleBox = new Rectangle(left, r.Y + S(6), r.Right - S(8) - left, S(20));
        string title = string.IsNullOrWhiteSpace(visit.Title) ? visit.Url : visit.Title;
        DrawLabel(g, title, Font, titleBox, Theme.Text);
        var detailBox = new Rectangle(left, r.Y + S(26), titleBox.Width, S(16));
        string detail = site.Length > 0 ? $"{site} \u00B7 {DownloadText.Ago(visit.VisitedUtc, now)}" : DownloadText.Ago(visit.VisitedUtc, now);
        DrawLabel(g, detail, _small, detailBox, Theme.TextDim);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _readId++;
            _bold.Dispose();
            _small.Dispose();
        }
        base.Dispose(disposing);
    }
}
