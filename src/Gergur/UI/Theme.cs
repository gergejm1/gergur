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

/// <summary>Fluent/MDL2 glyph codepoints (identical in both fonts).</summary>
public static class Glyphs
{
    public const string Back = "\uE72B";
    public const string Forward = "\uE72A";
    public const string Refresh = "\uE72C";
    public const string StarOutline = "\uE734";
    public const string StarFilled = "\uE735";
    public const string Menu = "\uE700";
}
