namespace Gergur.UI;

/// <summary>One line, or one block, of the main menu. Live values are read through functions so a step of the zoom or a toggle can redraw without rebuilding the menu.</summary>
internal abstract record MenuEntry;

/// <summary>An ordinary command: an icon, its words, and its shortcut on the right.</summary>
internal sealed record MenuAction(string Label, string? Shortcut, Action? Run, string? Glyph = null) : MenuEntry;

/// <summary>A hairline between groups.</summary>
internal sealed record MenuSeparator : MenuEntry;

/// <summary>A row of large buttons for the pages the menu leads to: history, downloads, bookmarks.</summary>
internal sealed record MenuTiles(IReadOnlyList<MenuTile> Tiles) : MenuEntry;

internal sealed record MenuTile(string Glyph, string Label, Action Run, Func<string?>? Badge = null);

/// <summary>Zoom out, the level (click to reset), zoom in, without the menu closing between steps.</summary>
internal sealed record MenuZoom(Func<int> Percent, Action Out, Action In, Action Reset) : MenuEntry;

/// <summary>An on and off switch, with an optional detail beside it ("142,000 rules").</summary>
internal sealed record MenuToggle(string Label, Func<string?> Detail, Func<bool> On, Action? Toggle, string? Glyph = null, string? Shortcut = null) : MenuEntry;

/// <summary>
/// A line that opens a list to its side: the vpn profiles, the phone drop, the bookmarks.
/// The list is the ToolStripMenuItem the menu has always built for it, so what those
/// submenus do and say is unchanged.
/// </summary>
internal sealed record MenuSubmenu(string Label, Func<string?> Detail, Func<ToolStripMenuItem> Build, string? Glyph = null) : MenuEntry;

/// <summary>Small buttons side by side along the bottom: settings, developer tools, more.</summary>
internal sealed record MenuFooter(IReadOnlyList<MenuEntry> Buttons) : MenuEntry;

/// <summary>
/// The main menu, as a panel rather than a list: tiles for the browser's own pages, a zoom
/// stepper, switches that show their state, and side lists for the vpn and the phone drop.
/// It takes the keyboard while it is open (arrows, Tab, Enter, Esc, Right for a side list).
/// </summary>
internal sealed class MainMenuFlyout : FlyoutPanel
{
    private Font _small = null!;
    private Font _icons = null!;
    private Font _tileIcons = null!;
    private Font _chevron = null!;
    private IReadOnlyList<MenuEntry> _entries = [];
    private readonly List<(MenuEntry Entry, Rectangle Bounds, HotSpot[] Spots)> _laid = new();
    private ToolStripDropDown? _submenu;
    private ToolStripMenuItem? _submenuOwner;
    // By name, not by object: every relayout (a zoom step, a toggle) makes new hot spots.
    private string? _submenuName;
    private string? _closedName;
    private DateTime _closedAtUtc;

    public MainMenuFlyout()
    {
        EnsureFonts();
        AccessibleName = "Menu";
    }

    protected override bool TakesFocus => true;

    protected override void MakeFonts()
    {
        base.MakeFonts();
        _small?.Dispose();
        _icons?.Dispose();
        _tileIcons?.Dispose();
        _chevron?.Dispose();
        _small = MakeFont("Segoe UI", 8.5f);
        _icons = MakeIconFont(10.5f);
        _tileIcons = MakeIconFont(14f);
        _chevron = MakeIconFont(7.5f);
    }

    /// <summary>Shows the menu with these entries, read fresh each time it opens.</summary>
    public void Open(Form owner, Control button, IReadOnlyList<MenuEntry> entries)
    {
        CloseSubmenu();
        _entries = entries;
        ShowFrom(owner, button);
    }

    /// <summary>What each entry was laid out as. For tests.</summary>
    internal IReadOnlyList<(MenuEntry Entry, Rectangle Bounds, HotSpot[] Spots)> Laid => _laid;

    /// <summary>Lays the entries out without showing anything. For tests.</summary>
    internal void LayOutForTest(IReadOnlyList<MenuEntry> entries)
    {
        _entries = entries;
        Relayout(keepPlace: false);
    }

    protected override Size LayOut()
    {
        _laid.Clear();
        int width = S(320);
        int pad = S(6);
        int inner = width - pad * 2;
        int y = pad;
        foreach (var entry in _entries)
        {
            switch (entry)
            {
                case MenuSeparator:
                {
                    var r = new Rectangle(pad, y, inner, S(9));
                    _laid.Add((entry, r, []));
                    y += r.Height;
                    break;
                }
                case MenuTiles tiles:
                {
                    var r = new Rectangle(pad, y, inner, S(64));
                    int gap = S(4);
                    int count = Math.Max(1, tiles.Tiles.Count);
                    int tileWidth = (inner - gap * (count - 1)) / count;
                    var spots = new HotSpot[tiles.Tiles.Count];
                    for (int i = 0; i < tiles.Tiles.Count; i++)
                    {
                        var tile = tiles.Tiles[i];
                        var box = new Rectangle(pad + i * (tileWidth + gap), y + S(2), tileWidth, r.Height - S(4));
                        spots[i] = new HotSpot(box, tile.Run, tile.Label);
                        AddSpot(spots[i]);
                    }
                    _laid.Add((entry, r, spots));
                    y += r.Height;
                    break;
                }
                case MenuZoom zoom:
                {
                    var r = new Rectangle(pad, y, inner, S(36));
                    int bw = S(32), bh = S(28), vw = S(56);
                    int top = y + (r.Height - bh) / 2;
                    var plus = new Rectangle(r.Right - S(6) - bw, top, bw, bh);
                    var level = new Rectangle(plus.Left - vw, top, vw, bh);
                    var minus = new Rectangle(level.Left - bw, top, bw, bh);
                    var spots = new[]
                    {
                        new HotSpot(minus, zoom.Out, "Zoom out", Tip: "Zoom out (Ctrl+-)", KeepOpen: true),
                        new HotSpot(level, zoom.Reset, "Reset zoom", Tip: "Reset to 100% (Ctrl+0)", KeepOpen: true),
                        new HotSpot(plus, zoom.In, "Zoom in", Tip: "Zoom in (Ctrl++)", KeepOpen: true),
                    };
                    foreach (var s in spots)
                        AddSpot(s);
                    _laid.Add((entry, r, spots));
                    y += r.Height;
                    break;
                }
                case MenuFooter footer:
                {
                    var r = new Rectangle(pad, y, inner, S(36));
                    int gap = S(4);
                    int count = Math.Max(1, footer.Buttons.Count);
                    int bw = (inner - gap * (count - 1)) / count;
                    var spots = new HotSpot[footer.Buttons.Count];
                    for (int i = 0; i < footer.Buttons.Count; i++)
                    {
                        var box = new Rectangle(pad + i * (bw + gap), y + S(2), bw, r.Height - S(4));
                        spots[i] = SpotFor(footer.Buttons[i], box);
                        AddSpot(spots[i]);
                    }
                    _laid.Add((entry, r, spots));
                    y += r.Height;
                    break;
                }
                default:
                {
                    var r = new Rectangle(pad, y, inner, S(32));
                    var spot = SpotFor(entry, r);
                    AddSpot(spot);
                    _laid.Add((entry, r, [spot]));
                    y += r.Height;
                    break;
                }
            }
        }
        return new Size(width, y + pad);
    }

    private HotSpot SpotFor(MenuEntry entry, Rectangle box) => entry switch
    {
        MenuAction a => new HotSpot(box, a.Run, a.Label, Tip: a.Shortcut is not null && box.Width < S(200) ? $"{a.Label} ({a.Shortcut})" : null),
        MenuToggle t => new HotSpot(box, t.Toggle, t.Label, KeepOpen: true),
        MenuSubmenu s => SubmenuSpot(s, box),
        _ => new HotSpot(box, null, ""),
    };

    private HotSpot SubmenuSpot(MenuSubmenu submenu, Rectangle box)
    {
        HotSpot? self = null;
        self = new HotSpot(box, () => OpenSubmenu(submenu, self!), submenu.Label, KeepOpen: true);
        return self;
    }

    // ---------------------------------------------------------------- side lists

    private void OpenSubmenu(MenuSubmenu submenu, HotSpot spot)
    {
        // A second click on the line whose list is open closes it. The list has usually
        // closed itself already by then, on the mouse going down outside it, so that click
        // is recognised by having just closed the same list.
        bool same = _submenu is { Visible: true } ? _submenuName == spot.Spoken
            : _closedName == spot.Spoken && (DateTime.UtcNow - _closedAtUtc).TotalMilliseconds < 300;
        CloseSubmenu();
        if (same)
            return;

        var item = submenu.Build();
        if (item.DropDownItems.Count == 0)
        {
            item.Dispose();
            return;
        }
        var drop = item.DropDown;
        // Cut loose from the item it was built under before it is shown. A list that still
        // belongs to an item is placed by WinForms beside that item, whatever point Show is
        // given, and this item sits on no strip, so every list came up at 0,0: the top left
        // of the primary monitor, whichever screen the window was on. Measured on the
        // hidden-desktop harness on all three monitors; the item is still disposed with it.
        drop.OwnerItem = null;
        drop.Renderer = new ChromeRenderer();
        drop.ItemClicked += (_, e) =>
        {
            // A line with its own list opens that list rather than doing anything.
            if (e.ClickedItem is ToolStripMenuItem { HasDropDownItems: true } || e.ClickedItem is { Enabled: false })
                return;
            BeginInvoke(Dismiss);
        };
        drop.Closed += (_, _) =>
        {
            if (ReferenceEquals(_submenu, drop))
            {
                _closedName = _submenuName;
                _closedAtUtc = DateTime.UtcNow;
                _submenu = null;
                _submenuName = null;
                Invalidate();
            }
            // After the click has run: disposing in here would pull the item out from
            // under its own Click handler.
            BeginInvoke(() => { item.Dispose(); drop.Dispose(); });
        };
        _submenu = drop;
        _submenuOwner = item;
        _submenuName = spot.Spoken;

        var row = RectangleToScreen(spot.Bounds);
        var panel = Bounds;
        var size = drop.GetPreferredSize(Size.Empty);
        var screen = Screen.FromRectangle(panel).WorkingArea;
        drop.Show(SubmenuPlacement(panel, row, size, screen, S(2)));
        Invalidate();
    }

    /// <summary>
    /// Where a side list goes: to the left of the panel, beside the line that opened it,
    /// since the panel hangs from the right end of the toolbar and the room is to its left.
    /// To the right when the left has no room, and kept on the panel's own screen.
    /// </summary>
    internal static Point SubmenuPlacement(Rectangle panel, Rectangle row, Size list, Rectangle screen, int overlap)
    {
        int x = panel.Left - list.Width + overlap;
        if (x < screen.Left)
            x = panel.Right - overlap;
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - list.Width));
        int y = Math.Clamp(row.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - list.Height));
        return new Point(x, y);
    }

    private void CloseSubmenu()
    {
        var drop = _submenu;
        _submenu = null;
        _submenuName = null;
        if (drop is { IsDisposed: false, Visible: true })
            drop.Close();
        else
        {
            // Never shown, or already closed: the list no longer belongs to the item, so it goes too.
            _submenuOwner?.Dispose();
            drop?.Dispose();
        }
        _submenuOwner = null;
    }

    protected override bool OverOwnedPopup(Point screen)
        => _submenu is { IsDisposed: false, Visible: true } drop && drop.Bounds.Contains(screen);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Only the way into a side list is handled here. Once one is open, WinForms' own
        // menu filter (ToolStripManager) sends the keys to it rather than to this panel, and
        // the list moves, picks, skips greyed lines and closes on Left or Esc by itself.
        // Seen on the hidden-desktop harness: keys posted to this panel never reached it
        // while a list was up.
        if (keyData is Keys.Right or Keys.Enter or Keys.Space
            && FocusedSpot() is { } spot && _laid.Any(l => l.Entry is MenuSubmenu && l.Spots.Contains(spot)))
        {
            // A key is never the second click that closes the list it just closed: Left
            // and then Right again at once has to open it.
            _closedName = null;
            RunSpot(spot);
            if (_submenu is { } drop)
                SelectFirst(drop);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Puts the list's highlight on its first line that can be used, as opening one by keyboard does.</summary>
    internal static void SelectFirst(ToolStripDropDown list)
        => list.Items.Cast<ToolStripItem>().FirstOrDefault(Usable)?.Select();

    /// <summary>A line the keyboard lands on: not a rule, shown, and not greyed, which is what the list's own arrow keys skip too.</summary>
    private static bool Usable(ToolStripItem item) => item is not ToolStripSeparator && item.Available && item.Enabled;

    private HotSpot? FocusedSpot() => Spots.FirstOrDefault(IsHot);

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
            CloseSubmenu();
    }

    // ---------------------------------------------------------------- painting

    protected override void PaintContent(Graphics g)
    {
        foreach (var (entry, bounds, spots) in _laid)
        {
            switch (entry)
            {
                case MenuSeparator:
                    using (var pen = new Pen(Theme.Border))
                        g.DrawLine(pen, bounds.Left + S(8), bounds.Top + bounds.Height / 2, bounds.Right - S(8), bounds.Top + bounds.Height / 2);
                    break;
                case MenuAction action:
                    PaintLine(g, spots[0], action.Glyph, action.Label, action.Shortcut, action.Run is not null);
                    break;
                case MenuToggle toggle:
                    PaintToggle(g, spots[0], toggle);
                    break;
                case MenuSubmenu submenu:
                    PaintSubmenu(g, spots[0], submenu);
                    break;
                case MenuTiles tiles:
                    for (int i = 0; i < tiles.Tiles.Count; i++)
                        PaintTile(g, spots[i], tiles.Tiles[i]);
                    break;
                case MenuZoom zoom:
                    PaintZoom(g, bounds, spots, zoom);
                    break;
                case MenuFooter footer:
                    for (int i = 0; i < footer.Buttons.Count; i++)
                        PaintFooterButton(g, spots[i], footer.Buttons[i]);
                    break;
            }
        }
    }

    private void PaintLine(Graphics g, HotSpot spot, string? glyph, string label, string? right, bool enabled)
    {
        PaintHover(g, spot);
        var r = spot.Bounds;
        var color = enabled ? Theme.Text : Theme.TextDim;
        if (glyph is not null)
            DrawLabel(g, glyph, _icons, new Rectangle(r.X + S(8), r.Y, S(24), r.Height), color, center: true);
        var text = new Rectangle(r.X + S(40), r.Y, r.Width - S(52), r.Height);
        DrawLabel(g, label, Font, text, color);
        if (right is not null)
            DrawLabel(g, right, _small, text, Theme.TextDim, right: true);
    }

    private void PaintToggle(Graphics g, HotSpot spot, MenuToggle toggle)
    {
        PaintHover(g, spot);
        var r = spot.Bounds;
        bool on = toggle.On();
        var color = spot.Enabled ? Theme.Text : Theme.TextDim;
        if (toggle.Glyph is not null)
            DrawLabel(g, toggle.Glyph, _icons, new Rectangle(r.X + S(8), r.Y, S(24), r.Height), color, center: true);

        // The switch: a pill in the accent when on, an outline when off, the knob at its end.
        var track = new Rectangle(r.Right - S(12) - S(34), r.Y + (r.Height - S(18)) / 2, S(34), S(18));
        using (var path = Rounded(track, track.Height / 2))
        {
            if (on)
            {
                using var fill = new SolidBrush(Theme.Accent);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(Theme.TextDim, Math.Max(1f, S(1)));
                g.DrawPath(pen, path);
            }
        }
        int knob = track.Height - S(6);
        var knobBox = new Rectangle(on ? track.Right - S(3) - knob : track.Left + S(3), track.Top + S(3), knob, knob);
        using (var brush = new SolidBrush(on ? Theme.MenuBg : Theme.TextDim))
            g.FillEllipse(brush, knobBox);

        var text = new Rectangle(r.X + S(40), r.Y, track.Left - S(8) - (r.X + S(40)), r.Height);
        DrawLabel(g, toggle.Label, Font, text, color);
        string? detail = toggle.Detail() ?? toggle.Shortcut;
        if (detail is not null)
            DrawLabel(g, detail, _small, text, Theme.TextDim, right: true);
    }

    private void PaintSubmenu(Graphics g, HotSpot spot, MenuSubmenu submenu)
    {
        bool open = _submenuName == spot.Spoken && _submenu is { Visible: true };
        if (open)
        {
            using var path = Rounded(spot.Bounds, S(6));
            using var brush = new SolidBrush(Theme.TabHover);
            g.FillPath(brush, path);
        }
        else
        {
            PaintHover(g, spot);
        }
        var r = spot.Bounds;
        if (submenu.Glyph is not null)
            DrawLabel(g, submenu.Glyph, _icons, new Rectangle(r.X + S(8), r.Y, S(24), r.Height), Theme.Text, center: true);
        var chevron = new Rectangle(r.Right - S(28), r.Y, S(20), r.Height);
        DrawLabel(g, ChevronLeft, _chevron, chevron, Theme.TextDim, center: true);
        var text = new Rectangle(r.X + S(40), r.Y, chevron.Left - S(4) - (r.X + S(40)), r.Height);
        DrawLabel(g, submenu.Label, Font, text, Theme.Text);
        if (submenu.Detail() is { } detail)
            DrawLabel(g, detail, _small, text, Theme.TextDim, right: true);
    }

    /// <summary>The side lists open to the left, so the arrow says so. MDL2 and Fluent both have it.</summary>
    private const string ChevronLeft = "\uE76B";

    private void PaintTile(Graphics g, HotSpot spot, MenuTile tile)
    {
        // Tiles sit on a faint fill of their own even at rest, so they read as buttons.
        using (var path = Rounded(spot.Bounds, S(8)))
        using (var rest = new SolidBrush(Theme.TabBg))
            g.FillPath(rest, path);
        PaintHover(g, spot);
        var r = spot.Bounds;
        DrawLabel(g, tile.Glyph, _tileIcons, new Rectangle(r.X, r.Y + S(8), r.Width, S(24)), Theme.Text, center: true);
        DrawLabel(g, tile.Label, _small, new Rectangle(r.X, r.Y + S(34), r.Width, S(20)), Theme.Text, center: true);
        if (tile.Badge?.Invoke() is { Length: > 0 } badge)
        {
            int w = Math.Max(S(18), TextRenderer.MeasureText(badge, _small).Width + S(8));
            var pill = new Rectangle(r.Right - w - S(6), r.Y + S(6), w, S(18));
            using var path = Rounded(pill, pill.Height / 2);
            using var brush = new SolidBrush(Theme.Accent);
            g.FillPath(brush, path);
            DrawLabel(g, badge, _small, pill, Theme.MenuBg, center: true);
        }
    }

    private void PaintZoom(Graphics g, Rectangle row, HotSpot[] spots, MenuZoom zoom)
    {
        DrawLabel(g, ZoomGlyph, _icons, new Rectangle(row.X + S(8), row.Y, S(24), row.Height), Theme.Text, center: true);
        DrawLabel(g, "Zoom", Font, new Rectangle(row.X + S(40), row.Y, S(120), row.Height), Theme.Text);
        // One outlined group, the way a stepper reads.
        var group = Rectangle.Union(spots[0].Bounds, spots[2].Bounds);
        using (var path = Rounded(group, S(6)))
        using (var pen = new Pen(Theme.Border))
            g.DrawPath(pen, path);
        foreach (var spot in spots)
            PaintHover(g, spot);
        DrawLabel(g, "\u2212", Font, spots[0].Bounds, Theme.Text, center: true);
        DrawLabel(g, $"{zoom.Percent()}%", Font, spots[1].Bounds, Theme.Text, center: true);
        DrawLabel(g, "+", Font, spots[2].Bounds, Theme.Text, center: true);
    }

    private const string ZoomGlyph = "\uE71E";

    private void PaintFooterButton(Graphics g, HotSpot spot, MenuEntry entry)
    {
        bool open = _submenuName == spot.Spoken && _submenu is { Visible: true };
        using (var path = Rounded(spot.Bounds, S(6)))
        using (var rest = new SolidBrush(open ? Theme.TabHover : Theme.TabBg))
            g.FillPath(rest, path);
        PaintHover(g, spot);
        string? glyph = entry switch { MenuAction a => a.Glyph, MenuSubmenu s => s.Glyph, _ => null };
        string label = entry switch { MenuAction a => a.Label, MenuSubmenu s => s.Label, _ => "" };
        var r = spot.Bounds;
        int textWidth = TextRenderer.MeasureText(label, _small).Width;
        int iconWidth = glyph is null ? 0 : S(20);
        int left = r.X + (r.Width - textWidth - iconWidth) / 2;
        if (glyph is not null)
            DrawLabel(g, glyph, _icons, new Rectangle(left, r.Y, S(16), r.Height), Theme.Text, center: true);
        DrawLabel(g, label, _small, new Rectangle(left + iconWidth, r.Y, textWidth + S(2), r.Height), Theme.Text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseSubmenu();
            _small.Dispose();
            _icons.Dispose();
            _tileIcons.Dispose();
            _chevron.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Segoe MDL2 / Fluent codepoints for the menu's lines, identical in both fonts.</summary>
internal static class MenuGlyphs
{
    public const string NewTab = "\uE710";
    public const string NewWindow = "\uE78B";
    public const string Reopen = "\uE7A7";
    public const string History = "\uE81C";
    public const string Bookmarks = "\uE728";
    public const string Find = "\uE721";
    public const string Mute = "\uE74F";
    public const string Print = "\uE749";
    public const string Save = "\uE74E";
    public const string Relax = "\uE785";
    public const string Shield = "\uEA18";
    public const string Vpn = "\uE705";
    public const string Phone = "\uE8EA";
    public const string Settings = "\uE713";
    public const string DevTools = "\uEC7A";
}
