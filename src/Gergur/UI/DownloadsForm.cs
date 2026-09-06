using System.Diagnostics;
using Gergur.App;
using Microsoft.Web.WebView2.Core;
using Palette = Gergur.UI.ListTheme;

namespace Gergur.UI;

/// <summary>
/// The downloads list, replacing the engine's own flyout (suppressed in Tab). Same light
/// reading palette as the history window, and the same whole-row painting from DrawItem,
/// since WinForms will not reliably raise DrawSubItem for every cell.
/// </summary>
public sealed class DownloadsForm : Form
{
    private const int RowHeight = 26;

    private const TextFormatFlags CellFlags =
        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis
        | TextFormatFlags.NoPrefix;

    private readonly DownloadManager _downloads;
    private readonly ListView _list;
    private readonly Panel _footer;
    private readonly Label _count;
    private readonly Button _openButton;
    private readonly Button _folderButton;
    private readonly Button _cancelButton;
    private readonly Button _clearButton;

    public DownloadsForm(DownloadManager downloads)
    {
        _downloads = downloads;

        Text = "Downloads";
        BackColor = Palette.Surface;
        ForeColor = Palette.Text;
        ClientSize = new Size(880, 520);
        MinimumSize = new Size(560, 300);
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
        _list.Columns.Add("File", 300);
        _list.Columns.Add("Status", 170);
        _list.Columns.Add("From", 320);
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawItem += OnDrawItem;
        _list.DrawSubItem += (_, e) => e.DrawDefault = false;
        _list.DoubleClick += (_, _) => OpenSelectedFile();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.Resize += (_, _) => LayoutColumns();

        _count = new Label
        {
            ForeColor = Palette.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _openButton = MakeButton("Open", (_, _) => OpenSelectedFile());
        _folderButton = MakeButton("Show in folder", (_, _) => ShowSelectedInFolder());
        _cancelButton = MakeButton("Cancel", (_, _) => CancelSelected());
        _clearButton = MakeButton("Clear finished", (_, _) => { _downloads.ClearFinished(); Reload(); });

        // Laid out by hand rather than with a docked FlowLayoutPanel: its AutoSize
        // measured the row short and clipped the buttons at the window edge, and a
        // 48px footer left them too little height as well.
        _footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Palette.Surface,
        };
        _footer.Controls.AddRange([_count, _openButton, _folderButton, _cancelButton, _clearButton]);
        _footer.Resize += (_, _) => LayoutFooter();

        Controls.Add(_list);
        Controls.Add(_footer);
        LayoutFooter();

        _downloads.Changed += OnDownloadsChanged;
        FormClosed += (_, _) => _downloads.Changed -= OnDownloadsChanged;
        Load += (_, _) => Reload();
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

    // ------------------------------------------------------------------ data

    private void OnDownloadsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;
        // Progress events arrive off the UI thread's normal flow; marshal to be safe.
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
            foreach (var item in _downloads.Items)
            {
                _list.Items.Add(new ListViewItem([item.FileName, StatusText(item), HostOf(item.Uri)])
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

        int running = _downloads.RunningCount;
        _count.Text = _downloads.Items.Count == 0
            ? "No downloads yet."
            : running > 0
                ? $"{_downloads.Items.Count} download{(_downloads.Items.Count == 1 ? "" : "s")}, {running} in progress"
                : $"{_downloads.Items.Count} download{(_downloads.Items.Count == 1 ? "" : "s")}";
        UpdateButtons();
        LayoutColumns();
    }

    private static string StatusText(DownloadItem item) => item.State switch
    {
        CoreWebView2DownloadState.Completed => $"Done  ·  {item.Describe()}",
        CoreWebView2DownloadState.Interrupted => "Stopped",
        _ => item.Describe(),
    };

    private static string HostOf(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : uri;

    private DownloadItem? Selected
        => _list.SelectedItems.Count > 0 ? (DownloadItem)_list.SelectedItems[0].Tag! : null;

    private void OpenSelectedFile()
    {
        if (Selected is not { } item || !File.Exists(item.FilePath))
            return;
        try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
        catch { }
    }

    private void ShowSelectedInFolder()
    {
        if (Selected is not { } item || item.FilePath.Length == 0)
            return;
        try
        {
            if (File.Exists(item.FilePath))
                Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
            else if (Path.GetDirectoryName(item.FilePath) is { } dir && Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
        }
        catch { }
    }

    private void CancelSelected()
    {
        Selected?.Cancel();
        Reload();
    }

    private void UpdateButtons()
    {
        var item = Selected;
        _openButton.Enabled = item is not null && File.Exists(item.FilePath);
        _folderButton.Enabled = item is not null && item.FilePath.Length > 0;
        _cancelButton.Enabled = item is { IsRunning: true };
        _clearButton.Enabled = _downloads.Items.Any(i => !i.IsRunning);
    }

    // ------------------------------------------------------------------ painting

    /// <summary>
    /// Right-aligned button row, measured and placed by hand so it keeps its shape at
    /// any window width and nothing is ever clipped by the footer's edges. The status
    /// text takes whatever is left.
    /// </summary>
    internal void LayoutFooter()
    {
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
        int pad = Scale(12);
        int gap = Scale(8);
        int height = Scale(30);
        int right = _footer.ClientSize.Width - pad;

        foreach (var button in new[] { _clearButton, _cancelButton, _folderButton, _openButton })
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
        => (_footer, [_openButton, _folderButton, _cancelButton, _clearButton]);

    private void LayoutColumns()
    {
        if (_list.Columns.Count < 3)
            return;
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
        int status = Scale(180);
        int rest = Math.Max(Scale(220), _list.ClientSize.Width - status - Scale(4));
        _list.Columns[0].Width = rest * 55 / 100;
        _list.Columns[1].Width = status;
        _list.Columns[2].Width = rest - _list.Columns[0].Width;
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

        // A running download draws its progress as a tint behind the row, which reads
        // faster than any number in the status column.
        if (e.Item.Tag is DownloadItem { IsRunning: true, TotalBytes: > 0 } running)
        {
            double fraction = Math.Clamp(running.BytesReceived / (double)running.TotalBytes, 0, 1);
            var bar = e.Bounds with { Width = (int)(e.Bounds.Width * fraction) };
            using var progress = new SolidBrush(Color.FromArgb(38, 61, 123, 250));
            e.Graphics.FillRectangle(progress, bar);
        }

        int x = e.Bounds.Left;
        for (int column = 0; column < _list.Columns.Count; column++)
        {
            int width = _list.Columns[column].Width;
            if (width > 0 && column < e.Item.SubItems.Count)
            {
                var color = selected ? Palette.TextSelected
                    : column == 0 ? Palette.Text
                    : Palette.TextDim;
                var cell = new Rectangle(x + 8, e.Bounds.Top, Math.Max(0, width - 12), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Item.SubItems[column].Text, _list.Font, cell, color, CellFlags);
            }
            x += width;
        }
    }
}
