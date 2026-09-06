using QRCoder;

namespace Gergur.UI;

/// <summary>
/// The pairing screen for the phone drop: a QR of the drop's address, so the phone is
/// paired by pointing a camera at it rather than typing an ip, a port and a key by hand.
///
/// The link is shown underneath as well, because a QR is useless if the camera will not
/// focus, and because seeing the address makes it obvious this is your own machine on
/// your own network rather than something in the cloud.
/// </summary>
public sealed class PairingForm : Form
{
    private readonly string _url;
    private Bitmap? _qr;

    public PairingForm(string url)
    {
        _url = url;

        Text = "Pair your phone";
        BackColor = Theme.WindowBg;
        ForeColor = Theme.Text;
        ClientSize = new Size(420, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
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

        var heading = new Label
        {
            Text = "Scan with your iPhone camera",
            Dock = DockStyle.Top,
            Height = 46,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12f, FontStyle.Regular),
            ForeColor = Theme.Text,
        };

        // White plate behind the code: scanners want dark-on-light, and the dark chrome
        // around it would otherwise bleed into the quiet zone.
        var plate = new Panel
        {
            Dock = DockStyle.Top,
            Height = 320,
            BackColor = Theme.WindowBg,
            Padding = new Padding(50, 8, 50, 8),
        };
        var image = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try
        {
            _qr = Render(url);
            image.Image = _qr;
        }
        catch (Exception ex)
        {
            // No QR is survivable: the link below still pairs the phone.
            image.BackColor = Theme.InputBg;
            Diagnostics.DebugLog.WriteAlways($"pairing QR not rendered: {ex.Message}");
        }
        plate.Controls.Add(image);

        var caption = new Label
        {
            Text = "Then choose Share, Add to Home Screen.\nBoth devices must be on the same Wi-Fi.",
            Dock = DockStyle.Top,
            Height = 52,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.TextDim,
        };

        var link = new TextBox
        {
            Text = url,
            Dock = DockStyle.Top,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextDim,
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI", 9.5f),
        };

        var copy = MakeButton("Copy link");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_url); copy.Text = "Copied"; }
            catch { copy.Text = "Could not copy"; }
        };
        var close = MakeButton("Close");
        close.DialogResult = DialogResult.OK;

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.WindowBg };
        buttons.Controls.AddRange([copy, close]);
        buttons.Resize += (_, _) => LayoutButtons(buttons, copy, close);

        // Docked top controls stack in reverse add order, so add bottom-up.
        Controls.Add(link);
        Controls.Add(caption);
        Controls.Add(plate);
        Controls.Add(heading);
        Controls.Add(buttons);
        LayoutButtons(buttons, copy, close);

        AcceptButton = close;
        CancelButton = close;
    }

    /// <summary>
    /// Medium error correction: it survives a bit of glare or a poor angle without
    /// making the modules so small that a phone struggles to resolve them.
    /// </summary>
    internal static Bitmap Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        // PngByteQRCode has no System.Drawing dependency, so it behaves the same
        // whatever the host does about GDI.
        byte[] png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 12);
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }

    private static Button MakeButton(string text)
        => new()
        {
            Text = text,
            AutoSize = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.TabBg,
            ForeColor = Theme.Text,
            UseVisualStyleBackColor = false,
        };

    private void LayoutButtons(Control host, Button copy, Button close)
    {
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
        int pad = Scale(16), gap = Scale(8), height = Scale(32);
        int width = Math.Max(Scale(90), (host.ClientSize.Width - (pad * 2) - gap) / 2);
        int y = (host.ClientSize.Height - height) / 2;
        close.Bounds = new Rectangle(host.ClientSize.Width - pad - width, y, width, height);
        copy.Bounds = new Rectangle(close.Left - gap - width, y, width, height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _qr?.Dispose();
            _qr = null;
        }
        base.Dispose(disposing);
    }
}
