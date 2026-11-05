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
/// <param name="Volume">How loud the page plays it, 0 to 1; null when the page has no volume to set.</param>
/// <param name="TabMuted">The tab itself is muted (Ctrl+M), whatever the page's volume says.</param>
internal sealed record NowPlayingView(string Title, string Artist, string Site, bool Playing, bool Controllable, bool CanNext, bool CanPrevious, bool TuckedAway = false, double? Volume = null, bool TabMuted = false)
{
    /// <summary>
    /// The view of a tab: what its page said (<paramref name="state"/>, null when it said
    /// nothing in time), its title and address, and whether the engine hears it.
    /// </summary>
    internal static NowPlayingView Of(MediaState? state, string tabTitle, string url, bool audible, bool tuckedAway = false, bool tabMuted = false)
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
            TuckedAway: tuckedAway,
            Volume: state is { HasMedia: true } ? state.Volume : null,
            TabMuted: tabMuted);
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
    public const string Volume = "\uE767";
    public const string Muted = "\uE74F";
}

/// <summary>
/// The panel the toolbar's note drops: the song playing in whichever tab is playing one, in
/// any of the person's windows, with the site's own previous, play or pause, and next, a way
/// to that tab, and a way to take the tab off the strip and listen from here (and bring it
/// back, or close it), and how loud it plays. It never takes the keyboard, so it can be used
/// without leaving what is being typed; the buttons keep it open, so a run of "next" is a run of
/// clicks.
/// </summary>
internal sealed class NowPlayingFlyout : FlyoutPanel
{
    private readonly Action<MediaAction> _run;
    private readonly Action _goToTab;
    private readonly Action _hideTab;
    private readonly Action _closeTab;
    private readonly Action<double> _setVolume;
    private readonly Action _toggleMute;
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
    private HotSpot? _mute;
    private Rectangle _volumeTrack;   // where the slider runs, from nothing to full
    private Rectangle _volumeGrab;    // the taller strip around it that takes the mouse
    private bool _dragging;
    private bool _overSlider;
    private double _heldLevel;
    private DateTime _heldUntilUtc;

    /// <summary>How long a level just set stands against what the page says: the page needs a moment to say it too.</summary>
    internal static readonly TimeSpan LevelHeld = TimeSpan.FromSeconds(1.5);

    /// <summary>How far a turn of the mouse wheel moves the slider.</summary>
    internal const double WheelStep = 0.05;

    /// <param name="run">Presses a button for the tab now playing.</param>
    /// <param name="goToTab">Brings that tab to the front, back on the strip if it was tucked away.</param>
    /// <param name="hideTab">Takes that tab off the strip; it keeps playing.</param>
    /// <param name="closeTab">Closes that tab, for one tucked away.</param>
    /// <param name="setVolume">Sets how loud that tab's music plays, 0 to 1.</param>
    /// <param name="toggleMute">Mutes that tab or takes its mute off.</param>
    public NowPlayingFlyout(Action<MediaAction> run, Action goToTab, Action hideTab, Action closeTab, Action<double> setVolume, Action toggleMute)
    {
        _run = run;
        _goToTab = goToTab;
        _hideTab = hideTab;
        _closeTab = closeTab;
        _setVolume = setVolume;
        _toggleMute = toggleMute;
        EnsureFonts();
        AccessibleName = "Now playing";
    }

    protected override bool TakesFocus => false;

    internal NowPlayingView? View => _view;

    /// <summary>The place on the slider a point falls at: 0 at its left end, 1 at its right, held to the ends past them.</summary>
    internal static double LevelAt(int x, Rectangle track)
        => track.Width <= 0 ? 0 : Math.Clamp((x - track.Left) / (double)track.Width, 0, 1);

    /// <summary>The level to show: the one just set while the page catches up, else the page's own.</summary>
    internal static double? ShownLevel(double? page, bool dragging, double held, DateTime heldUntilUtc, DateTime nowUtc)
        => page is null ? null : dragging || nowUtc < heldUntilUtc ? held : page;

    private double? Level => ShownLevel(_view?.Volume, _dragging, _heldLevel, _heldUntilUtc, DateTime.UtcNow);

    /// <summary>Moves the slider to a level and has the page play at it.</summary>
    internal void SetLevel(double level)
    {
        _heldLevel = Math.Clamp(level, 0, 1);
        _heldUntilUtc = DateTime.UtcNow + LevelHeld;
        Invalidate();
        _setVolume(_heldLevel);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _view?.Volume is null || !_volumeGrab.Contains(e.Location))
            return;
        _dragging = true;
        SetLevel(LevelAt(e.X, _volumeTrack));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            double level = LevelAt(e.X, _volumeTrack);
            if (level != _heldLevel)
                SetLevel(level);
            return;
        }
        bool over = _view?.Volume is not null && _volumeGrab.Contains(e.Location);
        if (over != _overSlider)
        {
            _overSlider = over;
            Cursor = over ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            StopDragging();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        StopDragging();
    }

    private void StopDragging()
    {
        if (!_dragging)
            return;
        _dragging = false;
        _heldUntilUtc = DateTime.UtcNow + LevelHeld;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Level is { } level && e.Delta != 0)
            SetLevel(level + Math.Sign(e.Delta) * WheelStep);
    }

    /// <summary>A slider pulled past the panel's edge is still the slider: that press does not close it.</summary>
    protected override bool OverOwnedPopup(Point screen) => _dragging;

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
        y += big + S(10);

        int row2 = S(28);
        _mute = new HotSpot(new Rectangle(S(16), y, row2, row2), _toggleMute,
            view.TabMuted ? "Unmute tab" : "Mute tab", view.TabMuted ? "Unmute tab" : "Mute tab", KeepOpen: true);
        AddSpot(_mute);
        int trackLeft = _mute.Bounds.Right + S(12);
        int trackRight = width - S(16) - S(40);
        _volumeTrack = new Rectangle(trackLeft, y + row2 / 2 - S(2), Math.Max(1, trackRight - trackLeft), S(4));
        _volumeGrab = new Rectangle(trackLeft - S(6), y, _volumeTrack.Width + S(12), row2);
        y += row2 + S(12);

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
        PaintVolume(g, view);

        var goTo = _goTo!;
        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, S(12), goTo.Bounds.Top - S(4), Width - S(12), goTo.Bounds.Top - S(4));
        PaintHover(g, goTo);
        DrawLabel(g, goTo.Spoken, Font, goTo.Bounds, Theme.Text, center: true);
        var other = _hideOrClose!;
        PaintHover(g, other);
        DrawLabel(g, other.Spoken, Font, other.Bounds, Theme.Text, center: true);
    }

    private void PaintVolume(Graphics g, NowPlayingView view)
    {
        double? level = Level;
        bool silent = view.TabMuted || level == 0;
        var mute = _mute!;
        PaintHover(g, mute);
        DrawLabel(g, silent ? MediaGlyphs.Muted : MediaGlyphs.Volume, _icons, mute.Bounds, Theme.Text, center: true);

        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Rounded(_volumeTrack, S(2)))
        using (var back = new SolidBrush(Theme.Border))
            g.FillPath(back, path);
        if (level is { } shown)
        {
            int at = _volumeTrack.Left + (int)Math.Round(_volumeTrack.Width * shown);
            Color color = view.TabMuted ? Theme.TextDim : Theme.Accent;
            if (at > _volumeTrack.Left)
            {
                using var path = Rounded(new Rectangle(_volumeTrack.Left, _volumeTrack.Top, at - _volumeTrack.Left, _volumeTrack.Height), S(2));
                using var fill = new SolidBrush(color);
                g.FillPath(fill, path);
            }
            int knob = S(12);
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, at - knob / 2, _volumeTrack.Top + _volumeTrack.Height / 2 - knob / 2, knob, knob);
            g.SmoothingMode = old;
            var percent = new Rectangle(_volumeTrack.Right + S(8), mute.Bounds.Top, Width - S(16) - _volumeTrack.Right - S(8), mute.Bounds.Height);
            DrawLabel(g, ((int)Math.Round(shown * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%", _small, percent, Theme.TextDim, right: true);
        }
        g.SmoothingMode = old;
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
