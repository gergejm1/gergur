using Gergur.App;

namespace Gergur.UI;

/// <summary>
/// Every setting in settings.json, editable without a text editor. A PropertyGrid over
/// an annotated Settings clone: the categories and the help text under the grid come
/// from the attributes on the properties themselves, so there is one place to keep
/// current rather than a hand-built form that drifts.
///
/// Edits land on a clone, so Cancel really cancels.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly Settings _live;
    private readonly Settings _working;
    private readonly PropertyGrid _grid;
    private readonly Panel _footer;
    private readonly Label _note;
    private readonly Button _save;
    private readonly Button _cancel;

    /// <summary>Settings that changed and only take effect on a restart. Empty when none did.</summary>
    public IReadOnlyList<string> ChangedRestartSettings { get; private set; } = [];

    public SettingsForm(Settings settings)
    {
        _live = settings;
        _working = settings.Clone();

        Text = "Settings";
        BackColor = Theme.WindowBg;
        ForeColor = Theme.Text;
        ClientSize = new Size(680, 640);
        MinimumSize = new Size(480, 400);
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

        _grid = new PropertyGrid
        {
            Dock = DockStyle.Fill,
            SelectedObject = _working,
            PropertySort = PropertySort.Categorized,
            ToolbarVisible = false,
            HelpVisible = true,
            BackColor = Theme.WindowBg,
            ViewBackColor = Theme.InputBg,
            ViewForeColor = Theme.Text,
            ViewBorderColor = Theme.Border,
            LineColor = Theme.Border,
            CategoryForeColor = Theme.Accent,
            CategorySplitterColor = Theme.Border,
            HelpBackColor = Theme.ToolbarBg,
            HelpForeColor = Theme.TextDim,
            HelpBorderColor = Theme.Border,
            CommandsBackColor = Theme.ToolbarBg,
        };

        _save = MakeButton("Save", DialogResult.OK);
        _cancel = MakeButton("Cancel", DialogResult.Cancel);
        _save.Click += (_, _) => Apply();

        _note = new Label
        {
            ForeColor = Theme.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Engine settings apply on restart; the rest on new tabs.",
            AutoEllipsis = true,
        };

        // Laid out by hand: a docked FlowLayoutPanel's AutoSize measured this row short
        // and clipped the buttons against the window edge.
        _footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Theme.ToolbarBg,
        };
        _footer.Controls.AddRange([_note, _save, _cancel]);
        _footer.Resize += (_, _) => LayoutFooter();

        Controls.Add(_grid);
        Controls.Add(_footer);
        LayoutFooter();
        AcceptButton = _save;
        CancelButton = _cancel;
    }

    /// <summary>
    /// Right-aligned Cancel then Save, measured and placed so neither is ever clipped.
    /// The note takes whatever width is left.
    /// </summary>
    internal void LayoutFooter()
    {
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
        int pad = Scale(12);
        int gap = Scale(8);
        int height = Scale(30);
        int right = _footer.ClientSize.Width - pad;

        foreach (var button in new[] { _cancel, _save })
        {
            int width = Math.Max(Scale(86), button.GetPreferredSize(Size.Empty).Width + gap);
            button.Size = new Size(width, height);
            button.Location = new Point(right - width, (_footer.ClientSize.Height - height) / 2);
            right = button.Left - gap;
        }
        _note.Bounds = new Rectangle(pad, 0, Math.Max(10, right - pad - gap), _footer.ClientSize.Height);
    }

    /// <summary>The footer and its buttons, for the layout test that guards against clipping.</summary>
    internal (Control Footer, IReadOnlyList<Control> Buttons) FooterControls
        => (_footer, [_save, _cancel]);

    private static Button MakeButton(string text, DialogResult result)
        => new()
        {
            Text = text,
            DialogResult = result,
            AutoSize = false, // LayoutFooter measures and places these itself
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.TabBg,
            ForeColor = Theme.Text,
            Padding = new Padding(12, 3, 12, 3),
            UseVisualStyleBackColor = false,
        };

    private void Apply()
    {
        var changed = _live.DifferencesFrom(_working).ToList();
        ChangedRestartSettings = changed.Where(Settings.RestartRequired.Contains).ToList();
        _live.CopyFrom(_working);
        _live.Save();
    }
}
