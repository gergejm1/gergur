using System.Runtime.InteropServices;
using Gergur.Data;
using Palette = Gergur.UI.ListTheme;

namespace Gergur.UI;

/// <summary>
/// Browsing history browser: search, open, forget. Reads the JSONL log through
/// <see cref="HistoryStore"/>.
///
/// Deliberately light, unlike the rest of the chrome: this is a reading surface,
/// a long list of small text scanned line by line, and dark rows made it hard to
/// pick a row out. The whole row is painted from DrawItem rather than per-column
/// from DrawSubItem, because WinForms does not reliably raise DrawSubItem for
/// every subitem, which left rows blank until they were selected.
/// </summary>
public sealed class HistoryForm : Form
{
    private const int MaxRows = 5000;
    private const int RowHeight = 24;


    private const TextFormatFlags CellFlags =
        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis
        | TextFormatFlags.NoPrefix;

    private readonly HistoryStore _history;
    private readonly Action<string> _openUrl;

    private readonly TextBox _search;
    private readonly ListView _list;
    private readonly Panel _footer;
    private readonly Label _count;
    private readonly Button _deleteButton;
    private readonly Button _clearButton;
    private readonly System.Windows.Forms.Timer _searchDebounce;

    public HistoryForm(HistoryStore history, Action<string> openUrl)
    {
        _history = history;
        _openUrl = openUrl;

        Text = "History";
        BackColor = Palette.Surface;
        ForeColor = Palette.Text;
        ClientSize = new Size(940, 620);
        MinimumSize = new Size(560, 320);
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

        // Typing reloads on a short delay: the log is re-read and re-filtered per keystroke.
        _searchDebounce = new System.Windows.Forms.Timer { Interval = 160 };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); Reload(); };

        _search = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10.5f),
            BackColor = Palette.RowBg,
            ForeColor = Palette.Text,
            PlaceholderText = "Search history",
        };
        _search.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        _search.KeyDown += OnSearchKeyDown;

        var searchHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 11, 12, 9),
            BackColor = Palette.Surface,
        };
        searchHost.Controls.Add(_search);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            OwnerDraw = true,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.None,
            BackColor = Palette.RowBg,
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 9.75f),
            // A blank image list is the only way to set ListView row height.
            SmallImageList = new ImageList { ImageSize = new Size(1, RowHeight) },
        };
        _list.Columns.Add("Visited", 160);
        _list.Columns.Add("Title", 340);
        _list.Columns.Add("Address", 400);
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawItem += OnDrawItem;
        _list.DrawSubItem += (_, e) => e.DrawDefault = false; // the whole row is drawn above
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += OnListKeyDown;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.Resize += (_, _) => LayoutColumns();
        _list.ContextMenuStrip = BuildContextMenu();

        _count = new Label
        {
            ForeColor = Palette.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _deleteButton = MakeButton("Delete selected", (_, _) => DeleteSelected());
        _clearButton = MakeButton("Clear all", (_, _) => ClearAll());

        // Laid out by hand: a docked FlowLayoutPanel's AutoSize measured this row short
        // and clipped the buttons against the window edge.
        _footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Palette.Surface,
        };
        _footer.Controls.AddRange([_count, _deleteButton, _clearButton]);
        _footer.Resize += (_, _) => LayoutFooter();

        Controls.Add(_list);
        Controls.Add(_footer);
        Controls.Add(searchHost);
        LayoutFooter();

        Load += (_, _) => { ApplyExplorerTheme(); Reload(); _search.Focus(); };
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false, // LayoutFooter measures and places these itself
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

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open in new tab", null, (_, _) => OpenSelected()));
        menu.Items.Add(new ToolStripMenuItem("Copy address", null, (_, _) => CopySelected()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Delete", null, (_, _) => DeleteSelected()));
        menu.Opening += (_, e) => e.Cancel = _list.SelectedItems.Count == 0;
        return menu;
    }

    // ------------------------------------------------------------------ data

    private void Reload()
    {
        var visits = _history.Read(_search.Text.Trim(), MaxRows);
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var visit in visits)
            {
                _list.Items.Add(new ListViewItem([
                    FormatWhen(visit.VisitedUtc),
                    string.IsNullOrWhiteSpace(visit.Title) ? visit.Url : visit.Title,
                    visit.Url,
                ])
                {
                    Tag = visit.Url,
                });
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        bool searching = _search.Text.Trim().Length > 0;
        string noun = visits.Count == 1 ? "page" : "pages";
        _count.Text = visits.Count == 0
            ? (searching ? "No matches." : "No history yet.")
            : $"{visits.Count:N0} {noun}{(visits.Count >= MaxRows ? " (most recent)" : "")}{(searching ? " matching" : "")}";
        UpdateButtons();
        LayoutColumns();
    }

    /// <summary>Local-time stamp, relative for the two days people actually scan for.</summary>
    private static string FormatWhen(DateTime visitedUtc)
    {
        var local = DateTime.SpecifyKind(visitedUtc, DateTimeKind.Utc).ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today)
            return $"Today  {local:HH:mm}";
        if (local.Date == today.AddDays(-1))
            return $"Yesterday  {local:HH:mm}";
        return local.Year == today.Year
            ? $"{local:ddd d MMM}  {local:HH:mm}"
            : $"{local:d MMM yyyy}  {local:HH:mm}";
    }

    private IReadOnlyList<string> SelectedUrls()
        => _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();

    private void OpenSelected()
    {
        foreach (var url in SelectedUrls())
            _openUrl(url);
    }

    private void CopySelected()
    {
        var urls = SelectedUrls();
        if (urls.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, urls));
    }

    private void DeleteSelected()
    {
        var urls = SelectedUrls();
        if (urls.Count == 0)
            return;
        _history.Remove(urls);
        Reload();
    }

    private void ClearAll()
    {
        if (MessageBox.Show(this, "Delete the entire browsing history?", "Gergur",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _history.Clear();
        Reload();
    }

    private void UpdateButtons()
    {
        _deleteButton.Enabled = _list.SelectedItems.Count > 0;
        _clearButton.Enabled = _list.Items.Count > 0 || _search.Text.Trim().Length > 0;
    }

    // ------------------------------------------------------------------ input

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyData == Keys.Escape)
        {
            e.Handled = e.SuppressKeyPress = true;
            if (_search.Text.Length > 0)
                _search.Clear();
            else
                Close();
        }
        else if (e.KeyData is Keys.Down or Keys.Enter && _list.Items.Count > 0)
        {
            e.Handled = e.SuppressKeyPress = true;
            _list.Items[0].Selected = true;
            _list.Focus();
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Enter:
                e.Handled = e.SuppressKeyPress = true;
                OpenSelected();
                break;
            case Keys.Delete:
                e.Handled = e.SuppressKeyPress = true;
                DeleteSelected();
                break;
            case Keys.Escape:
                e.Handled = e.SuppressKeyPress = true;
                Close();
                break;
        }
    }

    // ------------------------------------------------------------------ painting

    /// <summary>
    /// Right-aligned button row, measured and placed so nothing is ever clipped by the
    /// footer's edges. The count text takes whatever width is left.
    /// </summary>
    internal void LayoutFooter()
    {
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
        int pad = Scale(12);
        int gap = Scale(8);
        int height = Scale(30);
        int right = _footer.ClientSize.Width - pad;

        foreach (var button in new[] { _clearButton, _deleteButton })
        {
            int width = button.GetPreferredSize(Size.Empty).Width + gap;
            button.Size = new Size(width, height);
            button.Location = new Point(right - width, (_footer.ClientSize.Height - height) / 2);
            right = button.Left - gap;
        }
        _count.Bounds = new Rectangle(pad, 0, Math.Max(10, right - pad - gap), _footer.ClientSize.Height);
    }

    /// <summary>The footer and its buttons, for the layout test that guards against clipping.</summary>
    internal (Control Footer, IReadOnlyList<Control> Buttons) FooterControls
        => (_footer, [_deleteButton, _clearButton]);

    private void LayoutColumns()
    {
        if (_list.Columns.Count < 3)
            return;
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
        int when = Scale(150);
        int rest = Math.Max(Scale(200), _list.ClientSize.Width - when - Scale(4));
        _list.Columns[0].Width = when;
        _list.Columns[1].Width = rest * 45 / 100;
        _list.Columns[2].Width = rest - _list.Columns[1].Width;
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

    /// <summary>Paints the background and all three cells of one row.</summary>
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
                // Column 1 is the title, the line people actually read; the timestamp
                // and the raw address sit back a shade so it stays the anchor.
                var color = selected ? Palette.TextSelected
                    : column == 1 ? Palette.Text
                    : Palette.TextDim;
                var cell = new Rectangle(x + 8, e.Bounds.Top, Math.Max(0, width - 12), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Item.SubItems[column].Text, _list.Font, cell, color, CellFlags);
            }
            x += width;
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>Opts the list into the modern Explorer look (thin scrollbars, subtle
    /// hot-track). Cosmetic, so the return value is ignored.</summary>
    private void ApplyExplorerTheme()
    {
        try
        {
            SetWindowTheme(_list.Handle, "Explorer", null);
        }
        catch
        {
            // Cosmetic only; the owner-drawn rows carry the palette regardless.
        }
    }
}
