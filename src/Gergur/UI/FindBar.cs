namespace Gergur.UI;

/// <summary>
/// The Ctrl+F bar. WebView2 ships its own find dialog, but it is suppressed
/// (SuppressDefaultFindDialog) so the counts draw in the same palette as the rest of
/// the chrome instead of a stray light-themed popup.
/// </summary>
public sealed class FindBar : Panel
{
    private readonly TextBox _input;
    private readonly Label _count;
    private readonly GlyphButton _previous;
    private readonly GlyphButton _next;
    private readonly GlyphButton _close;

    /// <summary>The search term changed; the caller runs the find.</summary>
    public event EventHandler<string>? TermChanged;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    /// <summary>Escape or the close button: the caller clears highlights and hides this.</summary>
    public event EventHandler? CloseRequested;

    public string Term => _input.Text;

    public FindBar()
    {
        Dock = DockStyle.Top;
        Height = 38;
        BackColor = Theme.ToolbarBg;
        Visible = false;

        _input = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            PlaceholderText = "Find in page",
            Location = new Point(10, 7),
            Width = 320,
        };
        _input.TextChanged += (_, _) => TermChanged?.Invoke(this, _input.Text);
        _input.KeyDown += OnInputKeyDown;

        _count = new Label
        {
            ForeColor = Theme.TextDim,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            Width = 90,
            Height = 24,
        };

        _previous = new GlyphButton(Glyphs.ChevronUp);
        _next = new GlyphButton(Glyphs.ChevronDown);
        _close = new GlyphButton(Glyphs.Cancel);
        _previous.Click += (_, _) => PreviousRequested?.Invoke(this, EventArgs.Empty);
        _next.Click += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        Controls.AddRange([_input, _count, _previous, _next, _close]);
        Resize += (_, _) => LayoutChildren();
        LayoutChildren();
    }

    private void LayoutChildren()
    {
        _input.Location = new Point(10, (Height - _input.Height) / 2);
        _count.Location = new Point(_input.Right + 10, (Height - _count.Height) / 2);
        int y = (Height - 28) / 2;
        _previous.Location = new Point(_count.Right + 4, y);
        _next.Location = new Point(_previous.Right + 4, y);
        _close.Location = new Point(_next.Right + 12, y);
    }

    /// <summary>Shows the bar and puts the caret in it, keeping any previous term.</summary>
    public void Open()
    {
        Visible = true;
        _input.Focus();
        _input.SelectAll();
    }

    public void SetStatus(int activeMatch, int matchCount)
    {
        _count.Text = _input.Text.Length == 0 ? ""
            : matchCount == 0 ? "No results"
            : $"{activeMatch}/{matchCount}";
        _count.ForeColor = matchCount == 0 && _input.Text.Length > 0 ? Theme.CloseHover : Theme.TextDim;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Enter:
                e.Handled = e.SuppressKeyPress = true;
                NextRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Keys.Shift | Keys.Enter:
                e.Handled = e.SuppressKeyPress = true;
                PreviousRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Keys.Escape:
                e.Handled = e.SuppressKeyPress = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
