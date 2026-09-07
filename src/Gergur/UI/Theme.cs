using System.Drawing.Text;

namespace Gergur.UI;

/// <summary>
/// One dark crimson palette for all custom-drawn chrome, matched to the logo.
///
/// Each shade is the blue it replaced with its hue moved and its lightness kept, which
/// is what preserves the steps between the tab strip, an idle tab and the active one:
/// those steps are the whole reason the strip is readable at a glance. The rotations are
/// close to the artwork's 134.5 degrees but were set by hand per colour, so they range
/// from about 90 to 133; that is fine for a background, and it is not fine for anything
/// drawn on one, which is why the accent below is the exact rotation and not a guess.
/// </summary>
public static class Theme
{
    public static readonly Color WindowBg = Color.FromArgb(22, 10, 14);
    public static readonly Color ToolbarBg = Color.FromArgb(37, 16, 22);
    public static readonly Color TabStripBg = Color.FromArgb(13, 5, 8);
    public static readonly Color TabBg = Color.FromArgb(40, 17, 24);
    public static readonly Color TabHover = Color.FromArgb(58, 26, 35);
    public static readonly Color TabActive = Color.FromArgb(78, 33, 46);
    public static readonly Color Text = Color.FromArgb(248, 236, 238);
    public static readonly Color TextDim = Color.FromArgb(172, 140, 148);
    /// <summary>
    /// The old accent rotated by the artwork's 134.5 degrees, and nothing else.
    ///
    /// Reaching for the CSS colour called "crimson" instead put this at 2.66:1 against
    /// the tab strip, under the 3:1 a control needs, and the active-tab underline is the
    /// only strong signal of which tab you are on: the fills either side of it differ by
    /// 1.33:1. It also took the VPN label and the settings headers below AA. Every one of
    /// those is back above where the blue was.
    /// </summary>
    public static readonly Color Accent = Color.FromArgb(250, 61, 77);
    public static readonly Color InputBg = Color.FromArgb(48, 20, 28);
    public static readonly Color Border = Color.FromArgb(92, 42, 56);

    /// <summary>
    /// The disc behind the close cross on hover, not the cross itself. On a blue theme
    /// any red read as "danger" on its own; on this one the disc has to be lighter than
    /// the chrome around it to be seen at all, and the cross on it is then drawn dark.
    /// </summary>
    public static readonly Color CloseHover = Color.FromArgb(240, 96, 108);

    /// <summary>Matches the logo/home-page background so pages blend into the chrome.</summary>
    public static readonly Color PageBg = Color.FromArgb(10, 4, 6);

    private static readonly string IconFontFamily = ResolveIconFont();

    private static string ResolveIconFont()
    {
        // Fluent is the Windows 11 icon font; MDL2 is the Windows 10 fallback.
        using var installed = new InstalledFontCollection();
        foreach (var family in installed.Families)
        {
            if (family.Name == "Segoe Fluent Icons")
                return family.Name;
        }
        return "Segoe MDL2 Assets";
    }

    public static Font IconFont(float size) => new(IconFontFamily, size);
}

/// <summary>
/// Light palette for the reading surfaces (history, downloads). These are long lists of
/// small text scanned line by line, where the dark chrome actively hurt legibility, so
/// they deliberately break from <see cref="Theme"/>.
/// </summary>
public static class ListTheme
{
    public static readonly Color Surface = Color.FromArgb(239, 241, 245);
    public static readonly Color RowBg = Color.White;
    public static readonly Color RowAlt = Color.FromArgb(247, 248, 250);
    // Selection follows the chrome so the two surfaces read as one browser; the greys
    // stay neutral, because tinting a whole page of small text costs legibility for
    // nothing. The selected pair measures 10.9:1, against 11.0:1 before.
    public static readonly Color RowSelected = Color.FromArgb(255, 219, 226);
    public static readonly Color Text = Color.FromArgb(16, 20, 28);
    public static readonly Color TextDim = Color.FromArgb(102, 112, 133);
    public static readonly Color TextSelected = Color.FromArgb(91, 11, 32);
    public static readonly Color Border = Color.FromArgb(199, 205, 217);
    public static readonly Color HeaderText = Color.FromArgb(90, 100, 120);
    public static readonly Color ButtonHover = Color.FromArgb(245, 230, 234);
}

/// <summary>Fluent/MDL2 glyph codepoints (identical in both fonts).</summary>
public static class Glyphs
{
    public const string Back = "\uE72B";
    public const string Forward = "\uE72A";
    public const string Refresh = "\uE72C";
    public const string StarOutline = "\uE734";
    public const string StarFilled = "\uE735";
    public const string Menu = "\uE700";
    public const string ChevronUp = "\uE70E";
    public const string ChevronDown = "\uE70D";
    public const string Cancel = "\uE711";
}
