using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Photographing a window, and refusing to.
///
/// The refusals are the point. PrintWindow answers TRUE for a minimised window and draws
/// nothing, so the honest-looking outcome is a black png served at 200 with nothing to
/// say it is wrong. An agent has no way to tell that from a window that really is black.
/// </summary>
public sealed class WindowCaptureTests
{
    /// <summary>WinForms wants an STA thread; no message loop runs, so this cannot hang.</summary>
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

    [Fact]
    public void AMinimisedWindowIsRefusedAndSaysWhy()
    {
        OnSta(() =>
        {
            using var form = new QuietForm { Size = new Size(400, 300) };
            form.Show();
            form.WindowState = FormWindowState.Minimized;

            var (png, why) = WindowCapture.Of(form);

            Assert.Empty(png);
            Assert.NotNull(why);
            Assert.Contains("minimised", why);
            // Distinguishable from the other refusals, or the endpoint cannot tell the
            // caller the one thing they can act on.
            Assert.DoesNotContain("closing", why);
        });
    }

    [Fact]
    public void AClosedWindowIsRefusedAndSaysWhy()
    {
        OnSta(() =>
        {
            var form = new QuietForm { Size = new Size(400, 300) };
            form.Show();
            form.Dispose();

            var (png, why) = WindowCapture.Of(form);

            Assert.Empty(png);
            Assert.Equal("that window is closing", why);
        });
    }

    [Fact]
    public void AWindowThatWasNeverShownIsRefused()
    {
        OnSta(() =>
        {
            using var form = new QuietForm();

            var (png, why) = WindowCapture.Of(form);

            // No handle, so nothing to ask to draw itself. Asserting only that some reason
            // came back would pass for the wrong reason as readily as the right one, and
            // this used to be folded in with the disposed case and reported as "closing",
            // which a window that was never opened is not.
            Assert.Empty(png);
            Assert.Equal("that window is not open yet", why);
        });
    }

    // There is no test for the zero-size refusal: WinForms enforces a minimum window
    // size, so a real Form cannot reach it. The check stays because WindowCapture takes
    // any Form and a zero-area Bitmap throws, but nothing here can prove it fires, and a
    // test that quietly asserted nothing would be worse than saying so.

    [Fact]
    public void AnOrdinaryWindowIsPhotographed()
    {
        // The floor: the refusals must not be so eager that nothing is ever captured.
        // What this cannot tell you is whether a WebView2 page area lands in the image;
        // PrintWindow's blind spot is hardware composited content, and that needs a
        // person looking at a real capture.
        OnSta(() =>
        {
            using var form = new QuietForm { Size = new Size(400, 300), BackColor = Color.Crimson };
            form.Show();

            var (png, why) = WindowCapture.Of(form);

            Assert.Null(why);
            Assert.NotEmpty(png);
            // PNG magic, so this is an image and not an error page or an empty buffer.
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        });
    }
}

/// <summary>
/// A window that appears without taking the keyboard. The checks script runs this suite
/// every time a session stops, often while somebody is typing in another app, and a plain
/// Show() activated each of these and took their keystrokes.
/// </summary>
internal sealed class QuietForm : Form
{
    protected override bool ShowWithoutActivation => true;
}
