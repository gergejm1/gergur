using System.ComponentModel;
using Gergur.App;

namespace Gergur.UI;

public sealed class AddressBar : TextBox
{
    /// <summary>Raised with a fully navigable URL (heuristics already applied).</summary>
    public event EventHandler<string>? NavigationRequested;
    /// <summary>Escape pressed: caller restores the current URL and returns focus to the page.</summary>
    public event EventHandler? Escaped;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SearchUrlTemplate { get; set; } = "https://www.google.com/search?q={0}";

    /// <summary>Supplies address-bar suggestions (typically from browsing history),
    /// refreshed each time the bar gains focus so newly-visited sites appear.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<IEnumerable<string>>? SuggestionProvider { get; set; }

    public AddressBar()
    {
        BorderStyle = BorderStyle.FixedSingle;
        Font = new Font("Segoe UI", 10.5f);
        BackColor = Theme.InputBg;
        ForeColor = Theme.Text;
        AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        AutoCompleteSource = AutoCompleteSource.CustomSource;
        AutoCompleteCustomSource = new AutoCompleteStringCollection();
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        RefreshSuggestions();
        BeginInvoke(SelectAll); // after the click that focused us has been processed
    }

    private void RefreshSuggestions()
    {
        if (SuggestionProvider is null)
            return;
        try
        {
            var source = new AutoCompleteStringCollection();
            source.AddRange(SuggestionProvider().ToArray());
            AutoCompleteCustomSource = source;
        }
        catch
        {
            // Suggestions are a convenience; never let them break the address bar.
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Enter:
                e.Handled = e.SuppressKeyPress = true;
                var text = Text.Trim();
                if (text.Length > 0)
                    NavigationRequested?.Invoke(this, UrlHeuristics.ToNavigableUrl(text, SearchUrlTemplate));
                break;
            case Keys.Escape:
                e.Handled = e.SuppressKeyPress = true;
                Escaped?.Invoke(this, EventArgs.Empty);
                break;
            case Keys.Control | Keys.A:
                e.Handled = e.SuppressKeyPress = true;
                SelectAll();
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }
}
