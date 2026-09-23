using Gergur.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Gergur.App;

/// <summary>
/// A picture of a whole window, chrome included.
///
/// The engine's own capture returns the page and nothing else, so the tab strip, the
/// toolbar and the status bar are invisible to anything driving this browser. Two faults
/// reported here lived in exactly that region: a tab drawing a stale favicon, and the
/// main menu opening on the wrong monitor. Both needed a photograph taken from outside
/// the application to see at all, which is a poor place to have to stand and a worse one
/// to leave the next person in.
///
/// PrintWindow rather than a screen grab, because it asks the window to draw itself:
/// nothing has to come to the front, and whatever is sitting on top of it does not end
/// up in the picture.
/// </summary>
internal static class WindowCapture
{
    /// <summary>Renders the whole window, including its non-client frame.</summary>
    private const uint PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    /// <summary>
    /// The window as a PNG, with a reason when it cannot be drawn rather than an exception
    /// thrown at somebody who asked for a picture.
    ///
    /// The reason matters because one of these is the caller's to fix and the others are
    /// not: a minimised window is the most likely failure here and the only one anybody
    /// can do anything about, and folding it in with "could not be captured" left them
    /// guessing at a black rectangle.
    /// </summary>
    public static (byte[] Png, string? Why) Of(Form window)
    {
        try
        {
            if (window.IsDisposed)
                return ([], "that window is closing");
            if (!window.IsHandleCreated)
                return ([], "that window is not open yet");

            // PrintWindow answers TRUE for a minimised window and draws nothing into the
            // bitmap, so without this the caller gets a black png at 200 and no reason to
            // doubt it.
            if (window.WindowState == FormWindowState.Minimized)
                return ([], "that window is minimised; restore it first");

            var bounds = window.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return ([], "that window has no size to photograph");

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(window.Handle, dc, PW_RENDERFULLCONTENT))
                        return ([], "the window refused to draw itself");
                }
                finally
                {
                    graphics.ReleaseHdc(dc);
                }
            }

            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return (png.ToArray(), null);
        }
        catch (Exception ex)
        {
            // A capture is a convenience. Nothing here is worth taking a request down for.
            // The reason is logged rather than returned: everywhere else in this API the
            // exception text was dropped because it carries paths, and this one goes into
            // a 503 body and from there into a transcript.
            DebugLog.Write($"Window capture failed: {ex}");
            return ([], "the window could not be captured");
        }
    }
}
