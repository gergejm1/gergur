using System.Drawing.Text;

namespace Gergur.UI;

/// <summary>One dark-blue palette for all custom-drawn chrome, matched to the logo.</summary>
public static class Theme
{
    public static readonly Color WindowBg = Color.FromArgb(10, 13, 22);
    public static readonly Color ToolbarBg = Color.FromArgb(16, 22, 37);
    public static readonly Color TabStripBg = Color.FromArgb(5, 7, 13);
    public static readonly Color TabBg = Color.FromArgb(17, 24, 40);
    public static readonly Color TabHover = Color.FromArgb(26, 36, 58);
    public static readonly Color TabActive = Color.FromArgb(33, 47, 78);
    public static readonly Color Text = Color.FromArgb(232, 238, 248);
    public static readonly Color TextDim = Color.FromArgb(136, 148, 172);
    public static readonly Color Accent = Color.FromArgb(61, 123, 250);
    public static readonly Color InputBg = Color.FromArgb(20, 28, 48);
    public static readonly Color Border = Color.FromArgb(42, 58, 92);
    public static readonly Color CloseHover = Color.FromArgb(196, 66, 66);
    /// <summary>Matches the logo/home-page background so pages blend into the chrome.</summary>
    public static readonly Color PageBg = Color.FromArgb(5, 4, 10);

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
    public static readonly Color RowSelected = Color.FromArgb(214, 229, 255);
    public static readonly Color Text = Color.FromArgb(16, 20, 28);
    public static readonly Color TextDim = Color.FromArgb(102, 112, 133);
    public static readonly Color TextSelected = Color.FromArgb(11, 42, 91);
    public static readonly Color Border = Color.FromArgb(199, 205, 217);
    public static readonly Color HeaderText = Color.FromArgb(90, 100, 120);
    public static readonly Color ButtonHover = Color.FromArgb(228, 234, 245);
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
