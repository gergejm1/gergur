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

        _count = new Label { ForeColor = Palette.TextDim, TextAlign = ContentAlignment.MiddleLeft };
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
        int selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
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
        if (selected >= 0 && selected < _list.Items.Count)
            _list.Items[selected].Selected = true;

        _count.Text = _drop.Items.Count == 0
            ? "Nothing here yet."
            : $"{_drop.Items.Count} item{(_drop.Items.Count == 1 ? "" : "s")}";
        UpdateButtons();
        LayoutColumns();
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
        int available = _footer.ClientSize.Width - (pad * 2) - (gap * (buttons.Length - 1));
        int needed = widths.Sum();
        if (needed > available && available > 0)
        {
            // Shrink the row to fit rather than let one escape the footer. Captions
            // ellipsize, which is a better failure than a button nobody can click.
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
}
