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
    public void SettingsFooterButtonsAreNeverClipped() => OnSta(() =>
    {
        using var form = new SettingsForm(new Settings());
        _ = form.Handle; // realise the handle so DeviceDpi and docking are meaningful
        AssertFooterFits(form, form.FooterControls, form.LayoutFooter);
    });

    [Fact]
    public void DropFooterButtonsAreNeverClipped() => OnSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), $"gergur-drop-layout-{Guid.NewGuid():N}");
        try
        {
            using var form = new DropForm(new DropStore(root), _ => { });
            _ = form.Handle;
            AssertFooterFits(form, form.FooterControls, form.LayoutFooter);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    });
}

/// <summary>
/// The drop window's footer. The store sets files aside instead of deleting them when it
/// cannot account for them, and a recovery folder nobody is told about is not recovery.
/// </summary>
public sealed class DropFooterTextTests
{
    [Theory]
    [InlineData(0, "Nothing here yet.")]
    [InlineData(1, "1 item")]
    [InlineData(4, "4 items")]
    public void TheOrdinaryCountReadsAsBefore(int items, string expected)
        => Assert.Equal(expected, Gergur.UI.DropForm.CountText(items, setAside: 0));

    [Fact]
    public void FilesSetAsideAreMentionedWhereTheUserWillSeeThem()
    {
        string text = Gergur.UI.DropForm.CountText(items: 3, setAside: 2);

        Assert.Contains("3 items", text);
        Assert.Contains("2 things set aside", text);
        Assert.Contains("click to open", text);
    }

    [Fact]
    public void OneSetAsideThingIsNotCalledThings()
        => Assert.Contains("1 thing set aside", Gergur.UI.DropForm.CountText(items: 0, setAside: 1));
}

/// <summary>
/// The drop window's count label. It carries the only notice the user ever gets that a
/// file was set aside, and it used to be given whatever width the buttons did not want:
/// measured at 0 pixels at the minimum window size and 75 at the default, against 239
/// pixels of text.
/// </summary>
public sealed class DropFooterLabelTests
{
    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    [Theory]
    [InlineData(820)]    // the default window size
    [InlineData(1100)]
    public void TheSetAsideNoticeIsReadableAtOrdinaryWindowSizes(int width) => OnSta(() =>
        WithForm(width, form =>
        {
            int wanted = TextRenderer.MeasureText(form.CountLabel.Text, form.CountLabel.Font).Width;
            Assert.True(
                form.CountLabel.Width >= Math.Min(wanted, 200),
                $"at {width}px the notice had {form.CountLabel.Width}px for {wanted}px of text");
        }));

    [Theory]
    [InlineData(520)]    // the minimum window size
    [InlineData(600)]
    public void TheNoticeIsStillVisibleWhenTheWindowIsSmall(int width) => OnSta(() =>
        WithForm(width, form =>
        {
            // There is genuinely not room for everything here, and the notice used to be
            // the thing that got nothing: measured at 0 pixels at this size. It now keeps
            // a floor, and the buttons give up the tails of their captions instead.
            Assert.True(
                form.CountLabel.Width >= 120,
                $"at {width}px the set-aside notice had {form.CountLabel.Width}px and shows nothing");
            Assert.True(form.CountLabel.AutoEllipsis, "and what it cannot show needs a mark");
            Assert.True(form.CountLabel.TabStop, "the way back to the files must be reachable by keyboard");

            foreach (var button in form.FooterControls.Buttons)
            {
                Assert.True(
                    button.Width >= 40 && form.FooterControls.Footer.ClientRectangle.Contains(button.Bounds),
                    $"at {width}px '{button.Text}' is {button.Width}px at {button.Bounds}");
                Assert.True(((Button)button).AutoEllipsis, $"'{button.Text}' would be cut with no mark");
            }
        }));

    private static void WithForm(int width, Action<Gergur.UI.DropForm> check)
    {
        string root = Path.Combine(Path.GetTempPath(), $"gergur-label-{Guid.NewGuid():N}");
        try
        {
            // The state the notice exists for, which is also the state that squeezes the
            // row. Set up on disk so the real code path produces it: setting the label
            // text and calling LayoutFooter by hand passed while the shipped window
            // showed 93 pixels of it, because Reload never re-measured the footer.
            Directory.CreateDirectory(Path.Combine(root, "orphans"));
            File.WriteAllText(Path.Combine(root, "orphans", "a1.jpg"), "x");
            File.WriteAllText(Path.Combine(root, "orphans", "a2.jpg"), "y");
            var store = new Gergur.Data.DropStore(root);
            store.AddText("one", from: "pc");
            store.AddText("two", from: "pc");
            store.AddText("three", from: "pc");

            using var form = new Gergur.UI.DropForm(store, _ => { });
            _ = form.Handle;
            form.ClientSize = new Size(width, 560);
            form.Reload();
            check(form);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
