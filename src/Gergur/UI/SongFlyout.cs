using Gergur.Music;

namespace Gergur.UI;

/// <summary>What the song panel says: listening still, or the answer.</summary>
/// <param name="Headline">The song's name, or what happened.</param>
/// <param name="Detail">Its artist, or what to do about it.</param>
/// <param name="Busy">Still listening: "Listen again" waits.</param>
internal sealed record SongAnswer(string Headline, string Detail, bool Busy)
{
    public static readonly SongAnswer Listening = new("Listening...", "This can take up to two minutes.", Busy: true);

    public static readonly SongAnswer NothingPlaying = new("Nothing is playing in Gergur", "Play something, then ask again.", false);

    public static readonly SongAnswer Failed = new("That did not work", "The song could not be looked up.", false);

    /// <summary>The answer to show for what the recognizer found (null: it heard nothing), and whether its library is still growing.</summary>
    internal static SongAnswer Of(Identification? found, bool growing)
    {
        if (found is null)
            return new("Could not hear that tab", "It needs to be playing, with sound.", false);
        if (found.Song is { } song)
            return new(song.Title, song.Artist.Length > 0 ? song.Artist : song.Site, false);
        string detail = found.Known == 0
            ? (growing ? "Its song library is still filling up." : "Its song library is off in the settings.")
            : $"Checked {found.Known.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} song{(found.Known == 1 ? "" : "s")}{(growing ? "; more are being added." : ".")}";
        return new("Could not find this song", detail, false);
    }
}

/// <summary>Icons for the song button, from the system icon font.</summary>
internal static class SongGlyphs
{
    public const string Listen = "\uE720";   // Microphone
}

/// <summary>
/// The panel the toolbar's microphone drops: "What song is this?" for whatever plays in
/// Gergur, on any site. Opening it starts listening; it says "Listening...", then the song,
/// or that it does not know it. It never takes the keyboard.
/// </summary>
internal sealed class SongFlyout : FlyoutPanel
{
    private readonly Action _listen;
    private Font _bold = null!;
    private Font _small = null!;
    private Font _icons = null!;
    private SongAnswer _answer = SongAnswer.Listening;
    private string _where = "";
    private Rectangle _header;
    private Rectangle _info;
    private HotSpot? _again;

    /// <param name="listen">Listens again to whatever plays.</param>
    public SongFlyout(Action listen)
    {
        _listen = listen;
        EnsureFonts();
        AccessibleName = "What song is this?";
    }

    protected override bool TakesFocus => false;

    internal SongAnswer Answer => _answer;

    /// <summary>Where it listened, as shown under the answer.</summary>
    internal string Where => _where;

    protected override void MakeFonts()
    {
        base.MakeFonts();
        _bold?.Dispose();
        _small?.Dispose();
        _icons?.Dispose();
        _bold = MakeFont("Segoe UI Semibold", 10.5f);
        _small = MakeFont("Segoe UI", 8.5f);
        _icons = MakeIconFont(13f);
    }

    public void ShowFromButton(Form owner, Control button) => ShowFrom(owner, button);

    /// <summary>Puts up what it is doing or what it found, and where (a site, "" for nowhere).</summary>
    public void SetAnswer(SongAnswer answer, string where)
    {
        if (answer == _answer && where == _where)
            return;
        _answer = answer;
        _where = where;
        if (Visible)
            Relayout();
    }

    protected override Size LayOut()
    {
        int width = S(320);
        int pad = S(8);
        int y = pad;
        _header = new Rectangle(S(16), y, width - S(32), S(28));
        y += _header.Height;
        _info = new Rectangle(S(16), y, width - S(32), S(60));
        y += _info.Height + S(8);
        _again = new HotSpot(new Rectangle(pad, y, width - pad * 2, S(36)), _answer.Busy ? null : _listen, "Listen again", KeepOpen: true);
        AddSpot(_again);
        y += _again.Bounds.Height + pad;
        return new Size(width, y);
    }

    protected override void PaintContent(Graphics g)
    {
        DrawLabel(g, "What song is this?", _small, _header, Theme.TextDim);

        var badge = new Rectangle(_info.X, _info.Y + (_info.Height - S(40)) / 2, S(40), S(40));
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(_answer.Busy ? Theme.Accent : Theme.TabHover))
            g.FillEllipse(fill, badge);
        g.SmoothingMode = old;
        DrawLabel(g, SongGlyphs.Listen, _icons, badge, Theme.Text, center: true);

        int textLeft = badge.Right + S(12);
        int textWidth = _info.Right - textLeft;
        DrawLabel(g, _answer.Headline, _bold, new Rectangle(textLeft, _info.Y + S(2), textWidth, S(22)), Theme.Text);
        DrawLabel(g, _answer.Detail, _small, new Rectangle(textLeft, _info.Y + S(24), textWidth, S(18)), Theme.TextDim);
        DrawLabel(g, _where, _small, new Rectangle(textLeft, _info.Y + S(42), textWidth, S(18)), Theme.TextDim);

        var again = _again!;
        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, S(12), again.Bounds.Top - S(4), Width - S(12), again.Bounds.Top - S(4));
        PaintHover(g, again);
        DrawLabel(g, again.Spoken, Font, new Rectangle(again.Bounds.X + S(8), again.Bounds.Y, again.Bounds.Width - S(16), again.Bounds.Height),
            again.Enabled ? Theme.Text : Theme.TextDim);
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
