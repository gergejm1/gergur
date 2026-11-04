using Gergur.App;
using Gergur.Tabs;

namespace Gergur.UI;

/// <summary>What the now-playing panel shows.</summary>
/// <param name="Title">The song, as the site names it, or the tab's title.</param>
/// <param name="Artist">Who, when the site says; "" otherwise.</param>
/// <param name="Site">The site playing it, without "www.".</param>
/// <param name="Playing">Playing now, rather than paused.</param>
/// <param name="Controllable">The page has media the panel can play and pause.</param>
/// <param name="CanNext">The site has a "next" of its own.</param>
/// <param name="CanPrevious">The site has a "previous" of its own.</param>
/// <param name="TuckedAway">The tab is off the strip: this panel is the way to it.</param>
internal sealed record NowPlayingView(string Title, string Artist, string Site, bool Playing, bool Controllable, bool CanNext, bool CanPrevious, bool TuckedAway = false)
{
    /// <summary>
    /// The view of a tab: what its page said (<paramref name="state"/>, null when it said
    /// nothing in time), its title and address, and whether the engine hears it.
    /// </summary>
    internal static NowPlayingView Of(MediaState? state, string tabTitle, string url, bool audible, bool tuckedAway = false)
    {
        string title = state is { Title.Length: > 0 } ? state.Title : MediaControl.Clean(tabTitle);
        if (title.Length == 0)
            title = NowPlaying.SiteOf(url);
        return new NowPlayingView(
            title,
            state?.Artist ?? "",
            NowPlaying.SiteOf(url),
            Playing: state is { Playing: true } || (audible && state is not { Paused: true }),
            Controllable: state is { HasMedia: true },
            CanNext: state is { HasMedia: true, CanNext: true },
            CanPrevious: state is { HasMedia: true, CanPrevious: true },
            TuckedAway: tuckedAway);
    }
}

/// <summary>Icons from the system icon font, in its private use area.</summary>
internal static class MediaGlyphs
{
    public const string Note = "\uE8D6";
    public const string Play = "\uE768";
    public const string Pause = "\uE769";
    public const string Previous = "\uE892";
    public const string Next = "\uE893";
}

/// <summary>
/// The panel the toolbar's note drops: the song playing in whichever tab is playing one, in
/// any of the person's windows, with the site's own previous, play or pause, and next, a way
/// to that tab, and a way to take the tab off the strip and listen from here (and bring it
/// back, or close it). It never takes the keyboard, so it can be used without leaving what is
/// being typed; the buttons keep it open, so a run of "next" is a run of clicks.
/// </summary>
internal sealed class NowPlayingFlyout : FlyoutPanel
{
    private readonly Action<MediaAction> _run;
    private readonly Action _goToTab;
    private readonly Action _hideTab;
    private readonly Action _closeTab;
    private Font _bold = null!;
    private Font _small = null!;
    private Font _icons = null!;
    private Font _bigIcons = null!;
    private NowPlayingView? _view;
    private Rectangle _header;
    private Rectangle _info;
    private HotSpot? _previous;
    private HotSpot? _playPause;
    private HotSpot? _next;
    private HotSpot? _goTo;
    private HotSpot? _hideOrClose;

    /// <param name="run">Presses a button for the tab now playing.</param>
    /// <param name="goToTab">Brings that tab to the front, back on the strip if it was tucked away.</param>
    /// <param name="hideTab">Takes that tab off the strip; it keeps playing.</param>
    /// <param name="closeTab">Closes that tab, for one tucked away.</param>
    public NowPlayingFlyout(Action<MediaAction> run, Action goToTab, Action hideTab, Action closeTab)
    {
        _run = run;
        _goToTab = goToTab;
        _hideTab = hideTab;
        _closeTab = closeTab;
        EnsureFonts();
        AccessibleName = "Now playing";
    }

    protected override bool TakesFocus => false;

    internal NowPlayingView? View => _view;

    protected override void MakeFonts()
    {
        base.MakeFonts();
        _bold?.Dispose();
        _small?.Dispose();
        _icons?.Dispose();
        _bigIcons?.Dispose();
        _bold = MakeFont("Segoe UI Semibold", 10.5f);
        _small = MakeFont("Segoe UI", 8.5f);
        _icons = MakeIconFont(12f);
        _bigIcons = MakeIconFont(15f);
    }

    /// <summary>Shows the panel from the button with what is known so far.</summary>
    public void ShowFromButton(Form owner, Control button, NowPlayingView view)
    {
        _view = view;
        ShowFrom(owner, button);
    }

    /// <summary>Puts a newer view up, when it changed. Null: nothing plays any more, and the panel goes.</summary>
    public void SetView(NowPlayingView? view)
    {
        if (view is null)
        {
            Dismiss();
            return;
        }
        if (view == _view)
            return;
        _view = view;
        if (Visible)
            Relayout();
    }

    protected override Size LayOut()
    {
        var view = _view ?? new NowPlayingView("", "", "", false, false, false, false);
        int width = S(340);
        int pad = S(8);
        int y = pad;
        _header = new Rectangle(S(16), y, width - S(32), S(32));
        y += _header.Height;
        _info = new Rectangle(S(16), y, width - S(32), S(48));
        y += _info.Height + S(8);

        int small = S(40), big = S(48), gap = S(20);
        int left = (width - (small * 2 + big + gap * 2)) / 2;
        int row = y + (big - small) / 2;
        _previous = new HotSpot(new Rectangle(left, row, small, small),
            view.CanPrevious ? () => _run(MediaAction.Previous) : null, "Previous", "Previous", KeepOpen: true);
        _playPause = new HotSpot(new Rectangle(left + small + gap, y, big, big),
            view.Controllable ? () => _run(MediaAction.Toggle) : null,
            view.Playing ? "Pause" : "Play", view.Playing ? "Pause" : "Play", KeepOpen: true);
        _next = new HotSpot(new Rectangle(left + small + gap + big + gap, row, small, small),
            view.CanNext ? () => _run(MediaAction.Next) : null, "Next", "Next", KeepOpen: true);
        AddSpot(_previous);
        AddSpot(_playPause);
        AddSpot(_next);
        y += big + S(12);

        int half = (width - pad * 2) / 2;
        _goTo = new HotSpot(new Rectangle(pad, y, half, S(36)), _goToTab, view.TuckedAway ? "Show tab" : "Go to tab");
        _hideOrClose = view.TuckedAway
            ? new HotSpot(new Rectangle(pad + half, y, half, S(36)), _closeTab, "Close tab")
            : new HotSpot(new Rectangle(pad + half, y, half, S(36)), _hideTab, "Hide tab", "Keep it playing, off the tab strip", KeepOpen: true);
        AddSpot(_goTo);
        AddSpot(_hideOrClose);
        y += _goTo.Bounds.Height + pad;
        return new Size(width, y);
    }

    protected override void PaintContent(Graphics g)
    {
        var view = _view;
        DrawLabel(g, view is { TuckedAway: true } ? "Now playing, tab hidden" : "Now playing", _small, _header, Theme.TextDim);
        if (view is null)
            return;

        // A letter on a colour taken from the site, as the history panel draws one.
        var badge = new Rectangle(_info.X, _info.Y + (_info.Height - S(36)) / 2, S(36), S(36));
        int hue = DownloadText.Hue(view.Site.Length > 0 ? view.Site : view.Title);
        using (var path = Rounded(badge, S(8)))
        using (var fill = new SolidBrush(DownloadText.FromHsl(hue, 0.34, 0.23)))
            g.FillPath(fill, path);
        DrawLabel(g, MediaGlyphs.Note, _icons, badge, DownloadText.FromHsl(hue, 0.75, 0.88), center: true);

        int textLeft = badge.Right + S(12);
        var titleBox = new Rectangle(textLeft, _info.Y + S(4), _info.Right - textLeft, S(22));
        DrawLabel(g, view.Title, _bold, titleBox, Theme.Text);
        string detail = view.Artist.Length > 0 && view.Site.Length > 0 ? $"{view.Artist} \u00B7 {view.Site}" : view.Artist + view.Site;
        var detailBox = new Rectangle(textLeft, titleBox.Bottom, titleBox.Width, S(18));
        DrawLabel(g, detail, _small, detailBox, Theme.TextDim);

        PaintRound(g, _previous!, MediaGlyphs.Previous, _icons, filled: false);
        PaintRound(g, _playPause!, view.Playing ? MediaGlyphs.Pause : MediaGlyphs.Play, _bigIcons, filled: true);
        PaintRound(g, _next!, MediaGlyphs.Next, _icons, filled: false);

        var goTo = _goTo!;
        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, S(12), goTo.Bounds.Top - S(4), Width - S(12), goTo.Bounds.Top - S(4));
        PaintHover(g, goTo);
        DrawLabel(g, goTo.Spoken, Font, goTo.Bounds, Theme.Text, center: true);
        var other = _hideOrClose!;
        PaintHover(g, other);
        DrawLabel(g, other.Spoken, Font, other.Bounds, Theme.Text, center: true);
    }

    private void PaintRound(Graphics g, HotSpot spot, string glyph, Font font, bool filled)
    {
        var r = spot.Bounds;
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        bool lit = spot.Enabled && (IsHot(spot) || IsPressed(spot));
        Color back = filled
            ? (spot.Enabled ? (lit ? Theme.CloseHover : Theme.Accent) : Theme.Border)
            : (lit ? Theme.TabHover : Theme.MenuBg);
        using (var brush = new SolidBrush(back))
            g.FillEllipse(brush, r);
        g.SmoothingMode = old;
        Color ink = !spot.Enabled ? Theme.TextDim : Theme.Text;
        DrawLabel(g, glyph, font, r, ink, center: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold.Dispose();
            _small.Dispose();
            _icons.Dispose();
            _bigIcons.Dispose();
        }
        base.Dispose(disposing);
    }
}
