using System.Diagnostics;
using Gergur.App;
using Gergur.Data;
using Gergur.Diagnostics;

namespace Gergur.UI;

/// <summary>What the phone panel asks its window to do. Delegates, so the panel can be built and tested without one.</summary>
/// <param name="IsRunning">Whether the phone drop is listening.</param>
/// <param name="CanSendPage">Whether the tab in front is a page that can be sent (not the new tab page).</param>
/// <param name="SendPage">Puts the tab in front into the drop.</param>
/// <param name="Toggle">Turns the phone drop on (asking first) or off.</param>
/// <param name="Pair">Shows the pairing link.</param>
/// <param name="ShowAll">Opens the full drop window.</param>
/// <param name="OpenUrl">Opens a link in a new tab.</param>
/// <param name="Say">A line in the window's status bar, for when the panel has gone.</param>
internal sealed record PhoneActions(
    Func<bool> IsRunning,
    Func<bool> CanSendPage,
    Action SendPage,
    Action Toggle,
    Action Pair,
    Action ShowAll,
    Action<string> OpenUrl,
    Action<string> Say);

/// <summary>
/// The panel the phone button drops: everything the phone drop does, a click away.
/// Send the page in front, a file, or a message (type or paste it, Enter sends; a file or
/// a screenshot on the clipboard is sent as it is), drop files on the panel or on its
/// button, and the last few things either way, each one click from being opened or copied.
/// The full window is still there behind "Open drop window".
///
/// Opening a file that came from the phone is held to the drop window's rule: a program is
/// never run, since a click here would otherwise be code execution for whoever holds the
/// pairing key.
/// </summary>
internal sealed class PhoneFlyout : FlyoutPanel
{
    /// <summary>How many recent items the panel lists; the rest are in the drop window.</summary>
    internal const int MaxRows = 4;

    /// <summary>
    /// The largest file sent from here. The phone's own uploads stop at 100 MB; this is
    /// higher because a PC has the room, and there at all so a folder of films dropped by
    /// mistake is not quietly copied into the profile.
    /// </summary>
    internal const long MaxSendBytes = 1024L * 1024 * 1024;

    /// <summary>How long a note ("Sent", "Copied") stays under the message box.</summary>
    internal static readonly TimeSpan NoteFor = TimeSpan.FromSeconds(4);

    private readonly DropStore _drop;
    private readonly PhoneActions _act;
    private readonly TextBox _box;
    private Font _bold = null!;
    private Font _small = null!;
    private Font _icons = null!;
    private Font _bigIcons = null!;
    private Rectangle _header;
    private Rectangle _message;
    private Rectangle _compose;
    private Rectangle _hint;
    private Rectangle _recentHeader;
    private HotSpot? _turnOn;
    private HotSpot? _sendSpot;
    private HotSpot? _footer;
    private HotSpot? _turnOff;
    private readonly List<(HotSpot Spot, string Glyph, string Label)> _tiles = new();
    private readonly List<Row> _rows = new();
    private bool _running;
    private bool _dragging;
    private string? _note;
    private DateTime _noteUntilUtc;

    private sealed record Row(DropItem Item, Rectangle Bounds, HotSpot Main, HotSpot Action, string ActionGlyph);

    public PhoneFlyout(DropStore drop, PhoneActions actions)
    {
        _drop = drop;
        _act = actions;
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.TabActive,
            ForeColor = Theme.Text,
            PlaceholderText = "Type or paste a message or link",
            AccessibleName = "Message to send to your phone",
        };
        Controls.Add(_box);
        EnsureFonts();
        AccessibleName = "Phone drop";
        AllowDrop = true;
        _box.AllowDrop = true;
        DragEnter += OnDragEnter;
        DragLeave += (_, _) => SetDragging(false);
        DragDrop += OnDragDrop;
        _box.DragEnter += OnDragEnter;
        _box.DragDrop += OnDragDrop;
        // The outline and the send arrow follow the box.
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _box.TextChanged += (_, _) => { if (_sendSpot is { } send) Invalidate(send.Bounds); };
    }

    protected override bool TakesFocus => true;

    /// <summary>The message box, for tests.</summary>
    internal TextBox Box => _box;

    /// <summary>Whether the message box is laid out (the drop is on). For tests: the box itself reads hidden while the panel is.</summary>
    internal bool ComposeShown => _compose != Rectangle.Empty;

    /// <summary>The note under the message box, or null. For tests.</summary>
    internal string? Note => _note;

    protected override void MakeFonts()
    {
        base.MakeFonts();
        _bold?.Dispose();
        _small?.Dispose();
        _icons?.Dispose();
        _bigIcons?.Dispose();
        _bold = MakeFont("Segoe UI Semibold", 10f);
        _small = MakeFont("Segoe UI", 8.5f);
        _icons = MakeIconFont(10f);
        _bigIcons = MakeIconFont(14f);
        _box.Font = Font;
    }

    /// <summary>Opened from the button: the keyboard goes straight to the message box when there is one.</summary>
    public void ShowFromButton(Form owner, Control button)
    {
        ShowFrom(owner, button);
        if (_box.Visible)
            _box.Focus();
    }

    /// <summary>The drop changed, on whichever thread changed it: show it, if the panel is up.</summary>
    public void DropChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(() =>
            {
                if (Visible)
                    Relayout();
            });
        }
        catch (InvalidOperationException) { }   // the handle went between the check and the call
    }

    protected override void OnWatch()
    {
        if (_note is not null && DateTime.UtcNow >= _noteUntilUtc)
        {
            _note = null;
            Invalidate();
        }
        // Turned on or off from somewhere else (the menu, the settings page) while open.
        if (_act.IsRunning() != _running)
            Relayout();
    }

    // ---------------------------------------------------------------- layout

    protected override Size LayOut()
    {
        _tiles.Clear();
        _rows.Clear();
        _turnOn = _sendSpot = _turnOff = null;
        _running = _act.IsRunning();
        int width = S(380);
        int pad = S(8);
        int y = pad;
        _header = new Rectangle(S(16), y, width - S(32), S(36));
        y += _header.Height;

        if (!_running)
        {
            _box.Visible = false;
            _compose = _hint = Rectangle.Empty;
            _message = new Rectangle(S(16), y, width - S(32), S(56));
            y += _message.Height;
            _turnOn = new HotSpot(new Rectangle((width - S(180)) / 2, y, S(180), S(36)), _act.Toggle, "Turn on phone drop");
            AddSpot(_turnOn);
            y += _turnOn.Bounds.Height + S(14);
        }
        else
        {
            _message = Rectangle.Empty;
            // Three tiles: the things done most, the way the menu's tiles are.
            int gap = S(8);
            int tileWidth = (width - pad * 2 - gap * 2) / 3;
            var tiles = new (string Glyph, string Label, string Spoken, Action? Run, bool KeepOpen)[]
            {
                (PhoneGlyphs.Share, "Send page", "Send this page", _act.CanSendPage() ? SendPage : null, true),
                (PhoneGlyphs.File, "Send file", "Send a file", PickFiles, false),
                (PhoneGlyphs.Pair, "Pair phone", "Pair a phone", _act.Pair, false),
            };
            for (int i = 0; i < tiles.Length; i++)
            {
                var t = tiles[i];
                var spot = new HotSpot(new Rectangle(pad + i * (tileWidth + gap), y, tileWidth, S(62)), t.Run, t.Spoken, KeepOpen: t.KeepOpen);
                AddSpot(spot);
                _tiles.Add((spot, t.Glyph, t.Label));
            }
            y += S(62) + S(10);

            _compose = new Rectangle(S(12), y, width - S(24), S(36));
            int boxHeight = _box.PreferredHeight;
            _box.Bounds = new Rectangle(_compose.X + S(12), _compose.Y + (_compose.Height - boxHeight) / 2,
                _compose.Width - S(12) - S(40), boxHeight);
            _box.Visible = true;
            _sendSpot = new HotSpot(new Rectangle(_compose.Right - S(36), _compose.Y + S(2), S(32), S(32)),
                SendTyped, "Send message", Tip: "Send (Enter)", KeepOpen: true);
            AddSpot(_sendSpot);
            y += _compose.Height;
            _hint = new Rectangle(S(16), y + S(2), width - S(32), S(22));
            y += S(28);
        }

        var items = _drop.Items;
        if (items.Count > 0)
        {
            _recentHeader = new Rectangle(S(16), y, width - S(32), S(24));
            y += _recentHeader.Height;
            var now = DateTime.UtcNow;
            foreach (var item in items.Take(MaxRows))
            {
                var bounds = new Rectangle(pad, y, width - pad * 2, S(46));
                var actionBox = new Rectangle(bounds.Right - S(8) - S(30), bounds.Y + (bounds.Height - S(30)) / 2, S(30), S(30));
                var mainBox = new Rectangle(bounds.X, bounds.Y, actionBox.X - bounds.X - S(4), bounds.Height);
                string line = Line(item);
                var captured = item;
                HotSpot main;
                HotSpot action;
                string glyph;
                switch (item.Kind)
                {
                    case "file":
                        bool program = DropStore.IsExecutable(item.Text) || DropStore.IsExecutable(item.StoredName);
                        main = new HotSpot(mainBox, () => OpenFile(captured), $"Open {line}",
                            Tip: program ? "A program: Gergur will not run it" : null, KeepOpen: program);
                        action = new HotSpot(actionBox, () => ShowInFolder(captured), $"Show {line} in folder", Tip: "Show in folder");
                        glyph = PhoneGlyphs.Folder;
                        break;
                    case "link":
                        main = new HotSpot(mainBox, () => _act.OpenUrl(captured.Text), $"Open {line}", Tip: "Open in a new tab");
                        action = new HotSpot(actionBox, () => Copy(captured.Text, "Link copied."), $"Copy {line}", Tip: "Copy link", KeepOpen: true);
                        glyph = PhoneGlyphs.Copy;
                        break;
                    default:
                        main = new HotSpot(mainBox, () => Copy(captured.Text, "Copied."), $"Copy {line}", Tip: "Copy", KeepOpen: true);
                        action = new HotSpot(actionBox, () => Copy(captured.Text, "Copied."), $"Copy text {line}", Tip: "Copy", KeepOpen: true);
                        glyph = PhoneGlyphs.Copy;
                        break;
                }
                AddSpot(main);
                AddSpot(action);
                _rows.Add(new Row(captured, bounds, main, action, glyph));
                y += bounds.Height;
            }
        }
        else
        {
            _recentHeader = Rectangle.Empty;
        }

        y += S(8);
        int footerWidth = _running ? (width - pad * 2) * 2 / 3 : width - pad * 2;
        _footer = new HotSpot(new Rectangle(pad, y, footerWidth, S(36)), _act.ShowAll,
            items.Count > MaxRows ? $"Open drop window ({items.Count})" : "Open drop window");
        AddSpot(_footer);
        if (_running)
        {
            _turnOff = new HotSpot(new Rectangle(pad + footerWidth, y, width - pad * 2 - footerWidth, S(36)), _act.Toggle, "Turn off");
            AddSpot(_turnOff);
        }
        y += S(36) + pad;
        return new Size(width, y);
    }

    // ---------------------------------------------------------------- painting

    protected override void PaintContent(Graphics g)
    {
        DrawLabel(g, "Phone drop", _bold, _header, Theme.Text);
        DrawLabel(g, _running ? "on" : "off", _small, _header, _running ? Theme.Live : Theme.TextDim, right: true);

        if (_message != Rectangle.Empty)
        {
            DrawLabel(g, "Send links, messages and files to your phone.", Font,
                new Rectangle(_message.X, _message.Y + S(4), _message.Width, S(22)), Theme.Text, center: true);
            DrawLabel(g, "Only a phone with the pairing link can reach it.", _small,
                new Rectangle(_message.X, _message.Y + S(28), _message.Width, S(20)), Theme.TextDim, center: true);
        }
        if (_turnOn is { } on)
        {
            using var path = Rounded(on.Bounds, S(8));
            using var fill = new SolidBrush(IsHot(on) || IsPressed(on) ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent);
            g.FillPath(fill, path);
            DrawLabel(g, "Turn on", _bold, on.Bounds, Color.White, center: true);
        }

        foreach (var (spot, glyph, label) in _tiles)
            PaintTile(g, spot, glyph, label);

        if (_compose != Rectangle.Empty)
        {
            using (var path = Rounded(_compose, S(8)))
            using (var fill = new SolidBrush(Theme.TabActive))
                g.FillPath(fill, path);
            if (_box.Focused)
            {
                using var path = Rounded(_compose, S(8));
                using var pen = new Pen(Theme.Border);
                g.DrawPath(pen, path);
            }
            if (_sendSpot is { } send)
            {
                PaintHover(g, send);
                DrawLabel(g, PhoneGlyphs.Send, _icons, send.Bounds, _box.TextLength > 0 ? Theme.Accent : Theme.TextDim, center: true);
            }
            DrawLabel(g, _note ?? "Enter sends. Drop files here, or paste one.", _small, _hint,
                _note is null ? Theme.TextDim : Theme.Live);
        }

        if (_recentHeader != Rectangle.Empty)
            DrawLabel(g, "Recent", _small, _recentHeader, Theme.TextDim);
        var now = DateTime.UtcNow;
        foreach (var row in _rows)
            PaintRow(g, row, now);

        if (_footer is { } footer)
        {
            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, S(12), footer.Bounds.Top - S(4), Width - S(12), footer.Bounds.Top - S(4));
            PaintHover(g, footer);
            DrawLabel(g, footer.Spoken, Font, Rectangle.Inflate(footer.Bounds, -S(8), 0), Theme.Text);
        }
        if (_turnOff is { } off)
        {
            PaintHover(g, off);
            DrawLabel(g, off.Spoken, Font, Rectangle.Inflate(off.Bounds, -S(8), 0), Theme.TextDim, right: true);
        }

        // Without the phone drop on, a note has nowhere else to go.
        if (_note is not null && _compose == Rectangle.Empty && _turnOn is { } below)
            DrawLabel(g, _note, _small, new Rectangle(S(16), below.Bounds.Bottom + S(1), Width - S(32), S(14)), Theme.TextDim, center: true);

        if (_dragging)
        {
            var area = Rectangle.Inflate(ClientRectangle, -S(6), -S(6));
            using (var veil = new SolidBrush(Color.FromArgb(230, Theme.MenuBg)))
                g.FillRectangle(veil, ClientRectangle);
            using (var path = Rounded(area, S(10)))
            using (var pen = new Pen(Theme.Accent, Math.Max(1f, S(2))) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                g.DrawPath(pen, path);
            DrawLabel(g, PhoneGlyphs.Upload, _bigIcons, new Rectangle(area.X, area.Y + area.Height / 2 - S(40), area.Width, S(30)), Theme.Text, center: true);
            DrawLabel(g, _running ? "Drop to send to your phone" : "Turn on the phone drop first", _bold,
                new Rectangle(area.X, area.Y + area.Height / 2 - S(4), area.Width, S(24)), Theme.Text, center: true);
        }
    }

    private void PaintTile(Graphics g, HotSpot spot, string glyph, string label)
    {
        var r = spot.Bounds;
        using (var path = Rounded(r, S(8)))
        using (var fill = new SolidBrush(spot.Enabled && (IsHot(spot) || IsPressed(spot)) ? Theme.TabHover : Theme.TabActive))
            g.FillPath(fill, path);
        var color = spot.Enabled ? Theme.Text : Theme.TextDim;
        DrawLabel(g, glyph, _bigIcons, new Rectangle(r.X, r.Y + S(8), r.Width, S(24)), color, center: true);
        DrawLabel(g, label, _small, new Rectangle(r.X, r.Y + S(36), r.Width, S(18)), color, center: true);
    }

    private void PaintRow(Graphics g, Row row, DateTime now)
    {
        var item = row.Item;
        PaintHover(g, row.Main, row.Bounds);
        var badge = new Rectangle(row.Bounds.X + S(8), row.Bounds.Y + (row.Bounds.Height - S(30)) / 2, S(30), S(30));
        string ext = item.Kind == "file" ? DownloadText.Extension(item.Text) : "";
        int hue = DownloadText.Hue(item.Kind switch
        {
            "file" => ext.Length > 0 ? ext : "file",
            "link" => DownloadText.Site(item.Text) is { Length: > 0 } site ? site : "link",
            _ => "message",
        });
        using (var path = Rounded(badge, S(8)))
        using (var fill = new SolidBrush(DownloadText.FromHsl(hue, 0.34, 0.23)))
            g.FillPath(fill, path);
        var ink = DownloadText.FromHsl(hue, 0.75, 0.88);
        switch (item.Kind)
        {
            case "file":
                DrawLabel(g, ext.Length > 0 ? ext : "FILE", _small, badge, ink, center: true);
                break;
            case "link":
                DrawLabel(g, PhoneGlyphs.Link, _icons, badge, ink, center: true);
                break;
            default:
                DrawLabel(g, PhoneGlyphs.Message, _icons, badge, ink, center: true);
                break;
        }

        int left = badge.Right + S(10);
        int right = row.Action.Bounds.Left - S(6);
        DrawLabel(g, Line(item), Font, new Rectangle(left, row.Bounds.Y + S(5), right - left, S(20)), Theme.Text);
        DrawLabel(g, Detail(item, now), _small, new Rectangle(left, row.Bounds.Y + S(25), right - left, S(16)), Theme.TextDim);

        PaintHover(g, row.Action);
        DrawLabel(g, row.ActionGlyph, _icons, row.Action.Bounds, Theme.Text, center: true);
    }

    // ---------------------------------------------------------------- words

    /// <summary>The first line of a recent item: the file's name, the link without its scheme, or the message on one line.</summary>
    internal static string Line(DropItem item) => item.Kind switch
    {
        "file" => item.Text,
        "link" => WithoutScheme(item.Text),
        _ => item.Text.ReplaceLineEndings(" ").Trim(),
    };

    /// <summary>The second line: who it came from, how big a file is, and when.</summary>
    internal static string Detail(DropItem item, DateTime nowUtc)
    {
        string from = item.From == "phone" ? "From your phone" : "From this PC";
        string size = item.Kind == "file" ? $" \u00B7 {DownloadItem.Size(item.Size)}" : "";
        return $"{from}{size} \u00B7 {DownloadText.Ago(item.AddedUtc, nowUtc)}";
    }

    private static string WithoutScheme(string url)
    {
        foreach (string scheme in (ReadOnlySpan<string>)["https://", "http://"])
        {
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return url[scheme.Length..].TrimEnd('/');
        }
        return url;
    }

    // ---------------------------------------------------------------- doing

    private void Say(string note)
    {
        _note = note;
        _noteUntilUtc = DateTime.UtcNow + NoteFor;
        if (Visible)
            Invalidate();
        else
            _act.Say(note);
    }

    private void SendPage()
    {
        _act.SendPage();
        Say("This page is on your phone's drop.");
    }

    /// <summary>Sends what is in the message box. Nothing typed, nothing sent.</summary>
    internal void SendTyped()
    {
        string text = _box.Text.Trim();
        if (text.Length == 0)
        {
            _box.Focus();
            return;
        }
        if (!_act.IsRunning())
        {
            Say("Turn on the phone drop first.");
            return;
        }
        _drop.AddText(text, from: "pc");
        _box.Clear();
        Say("Sent.");
        // Listed under Recent now, rather than whenever the window passes the change on.
        if (Visible)
            Relayout();
        _box.Focus();
    }

    private void PickFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Send to your phone" };
        if (dialog.ShowDialog(Owner) == DialogResult.OK)
            _ = SendFilesAsync(dialog.FileNames);
    }

    /// <summary>
    /// Which of <paramref name="paths"/> can be sent, and if any cannot, why, in a few words.
    /// A folder or a missing file is skipped, and so is anything over <see cref="MaxSendBytes"/>.
    /// </summary>
    internal static (List<string> Send, string? Skipped) Sendable(IEnumerable<string> paths, Func<string, long?> sizeOf)
    {
        var send = new List<string>();
        int folders = 0, big = 0, missing = 0;
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                folders++;
                continue;
            }
            switch (sizeOf(path))
            {
                case null:
                    missing++;
                    break;
                case > MaxSendBytes:
                    big++;
                    break;
                default:
                    send.Add(path);
                    break;
            }
        }
        var why = new List<string>();
        if (folders > 0)
            why.Add(folders == 1 ? "a folder" : $"{folders} folders");
        if (big > 0)
            why.Add(big == 1 ? "a file over 1 GB" : $"{big} files over 1 GB");
        if (missing > 0)
            why.Add(missing == 1 ? "a file that could not be read" : $"{missing} files that could not be read");
        return (send, why.Count == 0 ? null : "Skipped " + string.Join(" and ", why) + ".");
    }

    private static long? SizeOf(string path)
    {
        try { return new FileInfo(path) is { Exists: true } f ? f.Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Copies files into the drop off the UI thread (a large one takes a while) and says how
    /// it went. Copied, never moved: the drop's own add-from-path moves its source, which is
    /// right for an upload's staging file and wrong for somebody's document.
    /// </summary>
    internal async Task SendFilesAsync(IEnumerable<string> paths)
    {
        if (!_act.IsRunning())
        {
            Say("Turn on the phone drop first.");
            return;
        }
        var list = paths.ToList();
        Say(list.Count == 1 ? $"Sending {Path.GetFileName(list[0])}..." : $"Sending {list.Count} files...");
        List<string> send = [];
        string? skipped = null;
        int sent = 0;
        string? lastSent = null;
        string? failed = null;
        try
        {
            await Task.Run(() =>
            {
                // Here rather than on the UI thread: a dropped path on a network share that
                // has gone away can take a long time to answer whether it is a folder.
                (send, skipped) = Sendable(list, SizeOf);
                foreach (string path in send)
                {
                    try
                    {
                        using var source = File.OpenRead(path);
                        // Grown past the limit since it was measured: refused, not copied whole.
                        if (source.Length > MaxSendBytes)
                        {
                            failed = Path.GetFileName(path);
                            continue;
                        }
                        _drop.AddFile(Path.GetFileName(path), source, from: "pc");
                        sent++;
                        lastSent = Path.GetFileName(path);
                    }
                    catch (Exception ex)
                    {
                        // Anything at all: this runs from a discarded task, so an exception let
                        // out would leave the panel saying "Sending..." for good.
                        DebugLog.WriteAlways($"a file could not be sent to the phone drop: {ex.Message}");
                        failed = Path.GetFileName(path);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"sending to the phone drop failed: {ex.Message}");
            failed ??= "A file";
        }
        if (IsDisposed)
            return;
        if (send.Count == 0 && failed is null)
        {
            Say(skipped ?? "Nothing to send.");
            return;
        }
        string done = sent switch
        {
            0 => "",
            1 => $"Sent {lastSent}.",
            _ => $"Sent {sent} files.",
        };
        if (failed is not null)
            done = (done + $" {failed} could not be read.").Trim();
        if (skipped is not null)
            done = (done + " " + skipped).Trim();
        Say(done);
        if (Visible)
            Relayout();
    }

    /// <summary>A screenshot or picture on the clipboard, sent as a png.</summary>
    private void SendImage(Image image)
    {
        if (!_act.IsRunning())
        {
            Say("Turn on the phone drop first.");
            return;
        }
        using var png = new MemoryStream();
        image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        png.Position = 0;
        string name = $"Pasted {DateTime.Now:yyyy-MM-dd HHmm}.png";
        try
        {
            _drop.AddFile(name, png, from: "pc");
            Say("Sent the picture.");
            if (Visible)
                Relayout();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("The picture could not be saved to the drop.");
        }
    }

    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            Say(done);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Say("The clipboard is busy. Try again.");
        }
    }

    private void OpenFile(DropItem item)
    {
        if (_drop.PathFor(item) is not { } path || !File.Exists(path))
        {
            Say("That file is no longer in the drop.");
            return;
        }
        // Never launch a program that arrived over the network: see the class summary.
        if (DropStore.IsExecutable(item.Text) || DropStore.IsExecutable(item.StoredName))
        {
            Say("That is a program. Gergur will not run it; use Show in folder.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _act.Say($"{item.Text} could not be opened.");
        }
    }

    private void ShowInFolder(DropItem item)
    {
        if (_drop.PathFor(item) is not { } path || !File.Exists(path))
        {
            Say("That file is no longer in the drop.");
            return;
        }
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _act.Say("The folder could not be opened.");
        }
    }

    // ---------------------------------------------------------------- keyboard

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_box.Focused)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    Dismiss();
                    return true;
                case Keys.Enter:
                    SendTyped();
                    return true;
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    if (PasteSomethingElse())
                        return true;
                    return false;   // ordinary text: the box pastes it
                case Keys.Tab:
                case Keys.Down:
                    // Out of the box and on to the panel's own places.
                    ActiveControl = null;
                    Focus();
                    MoveFocus(+1);
                    return true;
                case Keys.Shift | Keys.Tab:
                case Keys.Up:
                    ActiveControl = null;
                    Focus();
                    MoveFocus(-1);
                    return true;
            }
            // Space, the arrows along the line, Home and the rest belong to the box.
            return false;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Typing with the keyboard on the panel's places rather than the box: the letters go to the box.</summary>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (e.Handled || !_box.Visible || _box.Focused || char.IsControl(e.KeyChar))
            return;
        _box.Focus();
        _box.AppendText(e.KeyChar.ToString());
        e.Handled = true;
    }

    /// <summary>Files or a picture on the clipboard are sent as they are, rather than pasted as nothing.</summary>
    private bool PasteSomethingElse()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                _ = SendFilesAsync(files);
                return true;
            }
            if (!Clipboard.ContainsText() && Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                using (image)
                    SendImage(image);
                return true;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Held by another program this moment: let the box try its own paste.
        }
        return false;
    }

    // ---------------------------------------------------------------- drag and drop

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetDataPresent(DataFormats.FileDrop) == true;

    internal void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool files = HasFiles(e);
        e.Effect = files && _act.IsRunning() ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragging(files);
    }

    internal void OnDragDrop(object? sender, DragEventArgs e)
    {
        SetDragging(false);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            _ = SendFilesAsync(paths);
    }

    internal void SetDragging(bool dragging)
    {
        if (_dragging == dragging)
            return;
        _dragging = dragging;
        Invalidate();
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

/// <summary>Segoe MDL2 / Fluent codepoints for the phone button and panel.</summary>
internal static class PhoneGlyphs
{
    public const string Phone = "\uE8EA";     // CellPhone
    public const string Share = "\uE72D";
    public const string File = "\uE8E5";      // OpenFile
    public const string Pair = "\uED14";      // QRCode
    public const string Send = "\uE724";
    public const string Copy = "\uE8C8";
    public const string Folder = "\uED25";    // OpenFolderHorizontal, as the downloads panel uses
    public const string Link = "\uE71B";
    public const string Message = "\uE8BD";
    public const string Upload = "\uE898";
}
