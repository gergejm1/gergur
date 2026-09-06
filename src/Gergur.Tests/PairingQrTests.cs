using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The pairing QR. Asserts the structure a scanner actually looks for, so a change that
/// produced a blank or malformed image would fail rather than ship an unscannable code.
/// </summary>
public sealed class PairingQrTests
{
    private const string Url = "http://192.168.0.20:24003/?k=B6717A67D71A9FFBF9D2E284";

    [Fact]
    public void ARenderedCodeIsSquareAndBigEnoughToScan()
    {
        using var qr = PairingForm.Render(Url);

        Assert.Equal(qr.Width, qr.Height);
        // Version 1 with a quiet zone is 29 modules; anything smaller is not a QR.
        Assert.True(qr.Width >= 29 * 4, $"only {qr.Width}px across, too small to resolve");
    }

    [Fact]
    public void TheThreeFinderPatternsArePresent()
    {
        using var qr = PairingForm.Render(Url);
        // Scanners locate a code by its three corner squares. Sample inside each,
        // past the quiet zone, and require dark.
        int inset = qr.Width / 10;

        Assert.Equal(0, qr.GetPixel(inset, inset).R);                        // top left
        Assert.Equal(0, qr.GetPixel(qr.Width - 1 - inset, inset).R);         // top right
        Assert.Equal(0, qr.GetPixel(inset, qr.Height - 1 - inset).R);        // bottom left
    }

    [Fact]
    public void TheQuietZoneIsWhite()
    {
        using var qr = PairingForm.Render(Url);
        // A code butted against dark chrome will not scan; the border must be light.
        Assert.Equal(255, qr.GetPixel(1, 1).R);
        Assert.Equal(255, qr.GetPixel(qr.Width - 2, 1).R);
    }

    [Fact]
    public void DifferentUrlsProduceDifferentCodes()
    {
        // Guards against returning a placeholder or a cached image for everything.
        using var a = PairingForm.Render(Url);
        using var b = PairingForm.Render("http://192.168.0.99:24003/?k=DIFFERENTKEY0000");

        bool differs = false;
        for (int x = 0; x < Math.Min(a.Width, b.Width) && !differs; x += 3)
        {
            for (int y = 0; y < Math.Min(a.Height, b.Height); y += 3)
            {
                if (a.GetPixel(x, y).R != b.GetPixel(x, y).R)
                    differs = true;
            }
        }
        Assert.True(differs, "two different urls rendered the same image");
    }

    [Fact]
    public void ALongUrlStillEncodes()
    {
        // A longer key or a hostname instead of an ip must not throw.
        using var qr = PairingForm.Render("http://a-rather-long-hostname.local:24003/?k=" + new string('A', 64));
        Assert.True(qr.Width > 0);
    }
}
