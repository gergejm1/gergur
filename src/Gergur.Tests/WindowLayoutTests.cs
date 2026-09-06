using Gergur.App;
using Gergur.Data;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Guards the footers of the three tool windows. All three originally used a docked
/// FlowLayoutPanel whose AutoSize measured the button row short, so the buttons were
/// clipped by the window edge. These build each window off-screen, lay it out at both
/// its narrowest and a wide size, and assert every button sits fully inside its footer.
/// </summary>
public sealed class WindowLayoutTests
{
    /// <summary>WinForms needs an STA thread; no message loop is started, so this cannot hang.</summary>
    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }

    private static void AssertFooterFits(Form form, (Control Footer, IReadOnlyList<Control> Buttons) parts, Action layout)
    {
        foreach (var size in new[] { form.MinimumSize, new Size(1100, 700) })
        {
            form.Size = size;
            form.PerformLayout();
            layout();

            var footer = parts.Footer;
            Assert.True(footer.ClientRectangle.Height > 0, "the footer collapsed to nothing");

            foreach (var button in parts.Buttons)
            {
                Assert.True(button.Width > 0 && button.Height > 0,
                    $"'{button.Text}' has no size at {size.Width}x{size.Height}");

                // The whole button, not just its origin, has to be inside the footer.
                Assert.True(footer.ClientRectangle.Contains(button.Bounds),
                    $"'{button.Text}' at {button.Bounds} escapes the footer {footer.ClientRectangle} at window {size.Width}x{size.Height}");

                // And it must be wide enough for its own caption.
                int needed = button.GetPreferredSize(Size.Empty).Width;
                Assert.True(button.Width >= needed,
                    $"'{button.Text}' is {button.Width}px wide but needs {needed}px");
            }

            // Buttons must not sit on top of one another either.
            var ordered = parts.Buttons.OrderBy(b => b.Left).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                Assert.True(ordered[i].Left >= ordered[i - 1].Right,
                    $"'{ordered[i - 1].Text}' and '{ordered[i].Text}' overlap at {size.Width}x{size.Height}");
            }
        }
    }

    [Fact]
    public void DownloadsFooterButtonsAreNeverClipped() => OnSta(() =>
    {
        using var form = new DownloadsForm(new DownloadManager());
        _ = form.Handle; // realise the handle so DeviceDpi and docking are meaningful
        AssertFooterFits(form, form.FooterControls, form.LayoutFooter);
    });

    [Fact]
    public void SettingsFooterButtonsAreNeverClipped() => OnSta(() =>
    {
        using var form = new SettingsForm(new Settings());
        _ = form.Handle;
        AssertFooterFits(form, form.FooterControls, form.LayoutFooter);
    });

    [Fact]
    public void HistoryFooterButtonsAreNeverClipped() => OnSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"gergur-layout-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var form = new HistoryForm(new HistoryStore(path), _ => { });
            _ = form.Handle;
            AssertFooterFits(form, form.FooterControls, form.LayoutFooter);
        }
        finally { try { File.Delete(path); } catch { } }
    });
}
