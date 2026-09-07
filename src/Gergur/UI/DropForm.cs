using System.Diagnostics;
using Gergur.Data;
using Palette = Gergur.UI.ListTheme;

namespace Gergur.UI;

/// <summary>
/// The PC side of the phone drop: the same shared list the phone sees, with the actions
/// that only make sense here (open a link in a tab, open a file, reveal it in Explorer).
/// Light reading palette and whole-row painting, matching History and Downloads.
/// </summary>
public sealed class DropForm : Form
{
    private const int RowHeight = 26;

    private const TextFormatFlags CellFlags =
        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis
        | TextFormatFlags.NoPrefix;

    private readonly DropStore _drop;
    private readonly Action<string> _openUrl;
    private readonly ListView _list;
    private readonly TextBox _compose;
    private readonly Panel _footer;
    private readonly Label _count;
    private readonly Button _sendButton;
    private readonly Button _openButton;
    private readonly Button _folderButton;
    private readonly Button _removeButton;

    /// <summary>One line describing an item, for the list and the tray balloon.</summary>
    public static string Summarize(DropItem item) => item.Kind switch
    {
        "file" => $"{item.Text} ({Bytes(item.Size)})",
        "link" => item.Text,
        _ => item.Text.ReplaceLineEndings(" ").Trim(),
    };

    private static string Bytes(long n) => n switch
    {
        >= 1 << 20 => $"{n / (double)(1 << 20):0.#} MB",
        >= 1 << 10 => $"{n / (double)(1 << 10):0.#} KB",
        _ => $"{n} B",
    };

    public DropForm(DropStore drop, Action<string> openUrl)
    {
        _drop = drop;
        _openUrl = openUrl;

        Text = "Phone drop";
        BackColor = Palette.Surface;
        ForeColor = Palette.Text;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(520, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        try
        {
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "gergur.ico"));
        }
        catch
        {
            // Missing icon asset is cosmetic only.
        }

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            OwnerDraw = true,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.None,
            BackColor = Palette.RowBg,
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 9.75f),
            SmallImageList = new ImageList { ImageSize = new Size(1, RowHeight) },
        };
        _list.Columns.Add("From", 70);
        _list.Columns.Add("Item", 480);
        _list.Columns.Add("When", 150);
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawItem += OnDrawItem;
        _list.DrawSubItem += (_, e) => e.DrawDefault = false;
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.Resize += (_, _) => LayoutColumns();

        _compose = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10.5f),
            BackColor = Palette.RowBg,
            ForeColor = Palette.Text,
            PlaceholderText = "Send a message or link to your phone",
        };
        _compose.KeyDown += (_, e) =>
        {
            if (e.KeyData != Keys.Enter)
                return;
            e.Handled = e.SuppressKeyPress = true;
            Send();
        };

        var composeHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 11, 12, 9),
            BackColor = Palette.Surface,
        };
        composeHost.Controls.Add(_compose);

        _count = new Label
        {
            ForeColor = Palette.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
            // A hard cut with no ellipsis reads as a finished sentence that happens to
            // stop. If there is genuinely no room, say so with the dots.
            AutoEllipsis = true,
        };
        // Files are set aside under the profile, where nobody would ever find them.
        // Telling the user it happened is only half of it; this is the other half.
        //
        // The drop folder rather than the quarantine folder: what was set aside can be a
        // file in orphans or an index written beside items.json, and opening a folder
        // this handler had to create to have somewhere to point at is worse than useless.
        _count.Click += (_, _) => OpenSetAsideFolder();
        _sendButton = MakeButton("Send", (_, _) => Send());
        _openButton = MakeButton("Open", (_, _) => OpenSelected());
        _folderButton = MakeButton("Show in folder", (_, _) => ShowInFolder());
        _removeButton = MakeButton("Remove", (_, _) => RemoveSelected());

        _footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Palette.Surface };
        _footer.Controls.AddRange([_count, _openButton, _folderButton, _removeButton, _sendButton]);
        _footer.Resize += (_, _) => LayoutFooter();

        Controls.Add(_list);
        Controls.Add(_footer);
        Controls.Add(composeHost);
        LayoutFooter();

        _drop.Changed += OnDropChanged;
        FormClosed += (_, _) => _drop.Changed -= OnDropChanged;
        Load += (_, _) => { Reload(); _compose.Focus(); };
    }

    private void OpenSetAsideFolder()
    {
        if (_drop.SetAsideCount == 0)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(_drop.DropDir) { UseShellExecute = true });
        }
        catch
        {
            // Explorer refusing to open is not worth an error dialog here.
        }
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.RowBg,
            ForeColor = Palette.Text,
            Padding = new Padding(10, 3, 10, 3),
            UseVisualStyleBackColor = false,
            // Buttons do not ellipsize on their own, so a squeezed row used to hard cut
            // "Show in folder" to "Show in fo" and read as a different action.
            AutoEllipsis = true,
        };
        button.FlatAppearance.BorderColor = Palette.Border;
        button.FlatAppearance.MouseOverBackColor = Palette.ButtonHover;
        button.Click += onClick;
        return button;
    }

    // ------------------------------------------------------------------ data

    private void OnDropChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
            BeginInvoke(Reload);
        else
            Reload();
    }

    private void Reload()
    {
        // Remember what was selected, not where it was. The list is newest first and the
        // phone can add a row at any moment, so restoring by position quietly moves the
        // selection onto a different item, which is then what Open or Delete acts on.
        string? selected = _list.SelectedItems.Count > 0
            && _list.SelectedItems[0].Tag is DropItem chosen
            ? chosen.Id
            : null;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var item in _drop.Items)
            {
                _list.Items.Add(new ListViewItem([
                    item.From == "phone" ? "Phone" : "PC",
                    Summarize(item),
                    When(item.AddedUtc),
                ])
                {
                    Tag = item,
                });
            }
        }
        finally
        {
            _list.EndUpdate();
        }
        if (selected is not null)
        {
            foreach (ListViewItem row in _list.Items)
            {
                if (row.Tag is DropItem item && item.Id == selected)
                {
                    row.Selected = true;
                    row.Focused = true;
                    row.EnsureVisible();
                    break;
                }
            }
        }

        int setAside = _drop.SetAsideCount;
        _count.Text = CountText(_drop.Items.Count, setAside);
        _count.Cursor = setAside > 0 ? Cursors.Hand : Cursors.Default;
        UpdateButtons();
        LayoutColumns();
    }

    /// <summary>
    /// What the footer says. The second half exists because the store now sets things
    /// aside rather than deleting them when it cannot account for a file, and a recovery
    /// folder nobody is told about is not recovery: it is a folder quietly filling up
    /// with the user's photos in a place they will never look.
    /// </summary>
    internal static string CountText(int items, int setAside)
    {
        string counted = items == 0 ? "Nothing here yet." : $"{items} item{(items == 1 ? "" : "s")}";
        return setAside == 0
            ? counted
            : $"{counted}   ·   {setAside} thing{(setAside == 1 ? "" : "s")} set aside, click to open";
    }

    private static string When(DateTime utc)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.Date == DateTime.Today ? $"Today  {local:HH:mm}" : $"{local:d MMM}  {local:HH:mm}";
    }

    private DropItem? Selected
        => _list.SelectedItems.Count > 0 ? (DropItem)_list.SelectedItems[0].Tag! : null;

    private void Send()
    {
        string text = _compose.Text.Trim();
        if (text.Length == 0)
            return;
        _drop.AddText(text, from: "pc");
        _compose.Clear();
        Reload();
    }

    private void OpenSelected()
    {
        if (Selected is not { } item)
            return;
        if (item.Kind == "link")
        {
            _openUrl(item.Text);
            return;
        }
        if (_drop.PathFor(item) is { } path && File.Exists(path))
        {
            // Never launch an executable that arrived over the network. A drop moves
            // files between your devices; running one is not part of the deal, and a
            // click here would be code execution for anyone holding the pairing key.
            if (DropStore.IsExecutable(item.Text) || DropStore.IsExecutable(item.StoredName))
            {
                MessageBox.Show(this,
                    $"\"{item.Text}\" is a program, not a document.\n\n"
                    + "Gergur will not run files that arrived from your phone. Use Show in folder "
                    + "if you are certain you want it, and check what it is first.",
                    "Gergur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
            return;
        }
        if (item.Kind == "text")
        {
            try { Clipboard.SetText(item.Text); _count.Text = "Copied."; }
            catch { }
        }
    }

    private void ShowInFolder()
    {
        if (Selected is { } item && _drop.PathFor(item) is { } path && File.Exists(path))
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch { }
        }
    }

    private void RemoveSelected()
    {
        if (Selected is { } item)
        {
            _drop.Remove(item.Id);
            Reload();
        }
    }

    private void UpdateButtons()
    {
        var item = Selected;
        _openButton.Enabled = item is not null;
        _openButton.Text = item?.Kind switch { "link" => "Open in tab", "text" => "Copy", _ => "Open" };
        _folderButton.Enabled = item is { IsFile: true };
        _removeButton.Enabled = item is not null;
    }

    // ------------------------------------------------------------------ painting

    internal void LayoutFooter()
    {
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
        int pad = Scale(12), gap = Scale(8), height = Scale(30);
        var buttons = new[] { _sendButton, _removeButton, _folderButton, _openButton };

        // Measure the whole row before placing any of it. Walking right to left and
        // trusting there is room pushed the leftmost button off the edge at the minimum
        // window width, which is exactly what the layout test caught.
        var widths = buttons
            .Select(b => Math.Max(Scale(64), b.GetPreferredSize(Size.Empty).Width + gap))
            .ToArray();
        // The label on the left is not leftovers. It is the only place the user is told
        // that a file was set aside, and taking whatever the buttons did not want left it
        // 0 pixels wide at the minimum window size and 75 at the default: the sentence was
        // cut off before it said anything.
        //
        // Taken off the top, before the buttons are sized. Reserving only what was spare
        // was arithmetic that cancelled out: the buttons always got their preferred width
        // first, so the label came out 0 pixels wide at the minimum window size, which is
        // where it most needs to be readable. The buttons ellipsize (see MakeButton), so
        // what they give up here is the tail of a caption, not the ability to be clicked.
        int row = _footer.ClientSize.Width - (pad * 2) - (gap * (buttons.Length - 1));
        int floor = Scale(40) * buttons.Length;
        int countWanted = TextRenderer.MeasureText(_count.Text, _count.Font).Width + gap;
        int countRoom = Math.Clamp(Math.Min(countWanted, Scale(200)), 0, Math.Max(0, row - floor));

        int available = row - countRoom;
        int needed = widths.Sum();
        if (needed > available && available > 0)
        {
            // Shrink the row to fit rather than let one escape the footer. Captions
            // ellipsize (see MakeButton), which is a better failure than a button that
            // has slid off the edge and cannot be clicked at all.
            for (int i = 0; i < widths.Length; i++)
                widths[i] = Math.Max(Scale(40), widths[i] * available / needed);
        }

        int right = _footer.ClientSize.Width - pad;
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Size = new Size(widths[i], height);
            buttons[i].Location = new Point(right - widths[i], (_footer.ClientSize.Height - height) / 2);
            right = buttons[i].Left - gap;
        }
        _count.Bounds = new Rectangle(pad, 0, Math.Max(0, right - pad - gap), _footer.ClientSize.Height);
    }

    private void LayoutColumns()
    {
        if (_list.Columns.Count < 3)
            return;
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
        int from = Scale(70), when = Scale(140);
        _list.Columns[0].Width = from;
        _list.Columns[1].Width = Math.Max(Scale(150), _list.ClientSize.Width - from - when - Scale(4));
        _list.Columns[2].Width = when;
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(Palette.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        using var border = new Pen(Palette.Border);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        TextRenderer.DrawText(e.Graphics, e.Header!.Text, e.Font ?? Font,
            Rectangle.Inflate(e.Bounds, -8, 0), Palette.HeaderText, CellFlags);
    }

    private void OnDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        bool selected = e.Item.Selected;
        var rowBg = selected ? Palette.RowSelected
            : e.ItemIndex % 2 == 1 ? Palette.RowAlt
            : Palette.RowBg;
        using (var background = new SolidBrush(rowBg))
            e.Graphics.FillRectangle(background, e.Bounds);

        int x = e.Bounds.Left;
        for (int column = 0; column < _list.Columns.Count; column++)
        {
            int width = _list.Columns[column].Width;
            if (width > 0 && column < e.Item.SubItems.Count)
            {
                var color = selected ? Palette.TextSelected
                    : column == 1 ? Palette.Text
                    : Palette.TextDim;
                var cell = new Rectangle(x + 8, e.Bounds.Top, Math.Max(0, width - 12), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Item.SubItems[column].Text, _list.Font, cell, color, CellFlags);
            }
            x += width;
        }
    }

    /// <summary>The footer and its buttons, for the layout test that guards against clipping.</summary>
    internal (Control Footer, IReadOnlyList<Control> Buttons) FooterControls
        => (_footer, [_sendButton, _openButton, _folderButton, _removeButton]);

    /// <summary>The count label, for the test that it is not squeezed out of existence.</summary>
    internal Label CountLabel => _count;
}
