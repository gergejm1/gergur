using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Every icon the chrome draws is in the font it is drawn with. Segoe Fluent Icons is the
/// Windows 11 font and Segoe MDL2 Assets the Windows 10 one this machine has, and a
/// codepoint only Fluent has draws as an empty box on MDL2: the Hide eye did, on the
/// toolbar and on the agent band, and nothing but looking at a capture showed it.
/// </summary>
public sealed class IconGlyphTests
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int count, [Out] ushort[] indices, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    private const uint GGI_MARK_NONEXISTING_GLYPHS = 1;

    private static bool Has(string family, char c)
    {
        using var font = new Font(family, 12f);
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        IntPtr hdc = g.GetHdc();
        IntPtr hfont = font.ToHfont();
        try
        {
            IntPtr old = SelectObject(hdc, hfont);
            var indices = new ushort[1];
            GetGlyphIndicesW(hdc, c.ToString(), 1, indices, GGI_MARK_NONEXISTING_GLYPHS);
            SelectObject(hdc, old);
            return indices[0] != 0xFFFF;
        }
        finally
        {
            DeleteObject(hfont);
            g.ReleaseHdc(hdc);
        }
    }

    [Fact]
    public void EveryIconIsInTheWindows10FontOrHasAStandIn()
    {
        // Every Windows 10 and 11 install has it; the check is meaningless without it.
        Assert.Contains(new System.Drawing.Text.InstalledFontCollection().Families, f => f.Name == "Segoe MDL2 Assets");
        string ui = Path.GetDirectoryName(OwnPagesHardeningTests.FindSource("src", "Gergur", "UI", "Theme.cs"))!;
        var used = Directory.GetFiles(ui, "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\\u([EF][0-9A-Fa-f]{3})").Select(m => (char)Convert.ToInt32(m.Groups[1].Value, 16)))
            .Distinct()
            .ToArray();
        Assert.True(used.Length > 20, $"only {used.Length} icon codepoints found; did the pattern change?");
        var missing = used
            .Where(c => !Has("Segoe MDL2 Assets", c))
            .Where(c => !AgentGlyphs.FluentOnly.ContainsKey(c.ToString()))
            .Select(c => $"U+{(int)c:X4}")
            .ToArray();
        Assert.True(missing.Length == 0, "not in Segoe MDL2 Assets and no stand-in: " + string.Join(", ", missing));
        // And every stand-in is itself in it.
        foreach (string standIn in AgentGlyphs.FluentOnly.Values)
            Assert.True(Has("Segoe MDL2 Assets", standIn[0]), $"stand-in U+{(int)standIn[0]:X4} is missing too");
    }
}
