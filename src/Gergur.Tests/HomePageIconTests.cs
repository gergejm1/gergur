using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The new tab page asks for its icon at "favicon.png?v=&lt;hash&gt;". The engine caches
/// favicons per profile, keyed on the whole url, and the cache sits beside the profile
/// rather than beside the build, so recolouring the icon in place changes nothing anyone
/// can see: the logo on the page goes crimson, the tab strip goes on drawing the blue
/// icon it cached, and the build stays green throughout. That happened, and it took a
/// screen capture of the window chrome to find, because what the tab strip draws is the
/// one thing no check looks at.
///
/// Pinning the token to the icon's own bytes is what makes it self-maintaining. Nobody
/// has to remember to bump a number; changing the file fails this test, and the failure
/// says what to paste.
///
/// Every reference is resolved against the page's own url rather than pattern-matched,
/// because matching spellings was tried and leaked at every turn: single quotes, no
/// quotes, a rel of "icon apple-touch-icon", a preload, a plain img, and then "./" and a
/// leading slash once trimming was added to cope with the first round. Resolving asks
/// the only question that matters, which is what url the engine would end up requesting.
/// </summary>
public sealed class HomePageIconTests
{
    private const string IconFile = "favicon.png";

    private static string AssetPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    /// <summary>The first eight hex of the icon's sha256. Eight is plenty: this is a
    /// cache key that has to be new, not a defence against anything.</summary>
    private static string TokenFor(byte[] icon)
        => Convert.ToHexString(SHA256.HashData(icon))[..8].ToLowerInvariant();

    /// <summary>
    /// The page with everything that is not markup taken out: comments, and the contents
    /// of script and style, whichever comes first, in one pass left to right.
    ///
    /// One pass rather than three, because three passes have an order and every order is
    /// wrong somewhere. Comments first and a "&lt;!--" in a script string starts one.
    /// Scripts first and a "&lt;!-- e.g. &lt;script&gt; --&gt;" ahead of a real script
    /// swallows everything between them, and what follows is silently gone. Deleting
    /// markup that a browser would honour is not the safe direction: this test's floors
    /// are all met by the icon link near the top of the page, so anything erased below it
    /// takes its own coverage with it and nothing says so.
    ///
    /// Comments end the way HTML says they end, which is "--&gt;" or "--!&gt;", or at
    /// once for "&lt;!--&gt;" and "&lt;!---&gt;", or at the end of the file. Opening tags
    /// survive, because a script or a stylesheet is pulled in by an attribute on the tag
    /// itself, and dropping the tag with its body hid script src from both tests.
    /// </summary>
    private static string Markup(string html)
    {
        var kept = new System.Text.StringBuilder(html.Length);
        int at = 0;
        while (at < html.Length)
        {
            int comment = html.IndexOf("<!--", at, StringComparison.Ordinal);
            int script = OpeningTag(html, "script", at);
            int style = OpeningTag(html, "style", at);
            int next = Nearest(comment, script, style);
            if (next < 0)
            {
                kept.Append(html, at, html.Length - at);
                break;
            }

            kept.Append(html, at, next - at);
            if (next == comment)
            {
                at = EndOfComment(html, next);
            }
            else
            {
                string name = next == script ? "script" : "style";
                at = KeepTagDropContents(html, next, name, kept);
            }
        }
        return kept.ToString();
    }

    private static int Nearest(params int[] positions)
        => positions.Where(p => p >= 0).DefaultIfEmpty(-1).Min();

    /// <summary>Where the next "&lt;name" opening tag starts, or -1.</summary>
    private static int OpeningTag(string html, string name, int from)
    {
        for (int at = from; ; )
        {
            int found = html.IndexOf("<" + name, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return -1;
            int after = found + name.Length + 1;
            // "<style" is an opening tag; "<styles" is some other element entirely.
            if (after >= html.Length || html[after] == '>' || html[after] == '/'
                || char.IsWhiteSpace(html[after]))
                return found;
            at = found + 1;
        }
    }

    /// <summary>Just past the end of the comment beginning at <paramref name="start"/>.</summary>
    private static int EndOfComment(string html, int start)
    {
        int at = start + "<!--".Length;
        if (at < html.Length && html[at] == '>')
            return at + 1;                                   // <!-->
        if (at + 1 < html.Length && html[at] == '-' && html[at + 1] == '>')
            return at + 2;                                   // <!--->

        int plain = html.IndexOf("-->", at, StringComparison.Ordinal);
        int bang = html.IndexOf("--!>", at, StringComparison.Ordinal);
        int end = Nearest(plain, bang);
        if (end < 0)
            return html.Length;                              // unterminated: runs to EOF
        return end + (end == bang ? "--!>".Length : "-->".Length);
    }

    /// <summary>Copies the opening tag, drops the contents, returns where to carry on.</summary>
    private static int KeepTagDropContents(
        string html, int start, string name, System.Text.StringBuilder kept)
    {
        int tagEnd = html.IndexOf('>', start);
        if (tagEnd < 0)
        {
            kept.Append(html, start, html.Length - start);
            return html.Length;
        }
        kept.Append(html, start, tagEnd + 1 - start);

        int close = html.IndexOf("</" + name, tagEnd, StringComparison.OrdinalIgnoreCase);
        if (close < 0)
            return html.Length;
        int closeEnd = html.IndexOf('>', close);
        return closeEnd < 0 ? html.Length : closeEnd + 1;
    }

    private static readonly Regex Attribute = new(
        @"\b(?<name>[A-Za-z-]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>=`]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IEnumerable<string> ValuesOf(string markup, string attribute)
        => Attribute.Matches(markup)
            .Where(m => m.Groups["name"].Value.Equals(attribute, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups["v"].Value);

    /// <summary>Every href in the markup, then every src.</summary>
    private static List<string> References(string markup)
        => ValuesOf(markup, "href").Concat(ValuesOf(markup, "src")).ToList();

    /// <summary>
    /// Resolving against the page's own url is only what the engine does while the page
    /// has no base element. One `&lt;base href&gt;` moves every relative reference
    /// somewhere else, the icon fetch fails from wherever that is, and the profile goes on
    /// drawing the icon it cached, so a base element has to fail here rather than be
    /// resolved wrongly in silence.
    /// </summary>
    private static void NoBaseElement(string page, string markup)
        => Assert.True(!Regex.IsMatch(markup, @"<base\b", RegexOptions.IgnoreCase),
            $"{page} has a <base> element. These tests resolve every reference against the "
            + "page's own url, which is no longer what the engine will do, so teach Resolve "
            + "about it before trusting either of them again.");

    /// <summary>
    /// Where a reference actually points, resolved against the page the way the engine
    /// resolves it, and the query it carries. Path is null for anything that is not a
    /// local file, which is the only honest answer for a data url or an absolute http one.
    ///
    /// The query is split off by hand rather than read back off the Uri, because the file
    /// scheme has no query component in .NET: resolve "favicon.png?v=x" against a file url
    /// and the "?v=x" comes back as part of the file name.
    /// </summary>
    private static (string? Path, string Query) Resolve(string pagePath, string reference)
    {
        string[] parts = reference.Split('#')[0].Split('?', 2);
        string query = parts.Length == 2 ? "?" + parts[1] : "";
        if (parts[0].Length == 0 || !Uri.TryCreate(new Uri(pagePath), parts[0], out var resolved))
            return (null, query);
        return (resolved.IsFile ? resolved.LocalPath : null, query);
    }

    [Fact]
    public void EveryRequestForTheIconCarriesItsCurrentToken()
    {
        string page = AssetPath("home.html");
        string iconFile = AssetPath(IconFile);
        Assert.True(File.Exists(page), $"not shipped: {page}");
        Assert.True(File.Exists(iconFile), $"not shipped: {iconFile}");

        string markup = Markup(File.ReadAllText(page));
        string token = TokenFor(File.ReadAllBytes(iconFile));
        NoBaseElement(page, markup);

        // Whatever element does the asking, and however it is spelled or written. One
        // untokened request is enough to re-prime the cache entry the token retires.
        var requests = References(markup)
            .Select(reference => (Text: reference, Target: Resolve(page, reference)))
            .Where(r => string.Equals(r.Target.Path, iconFile, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(requests.Count > 0,
            $"{page} never asks for the icon beside it. Either it stopped asking, or it is "
            + $"asking for something that does not resolve to {iconFile}, which would fail "
            + "to load and leave the cached icon in place.");

        foreach (var request in requests)
        {
            Assert.True(request.Target.Query == $"?v={token}",
                $"{page} asks for \"{request.Text}\". Every profile that already cached the "
                + "icon will go on drawing whatever it cached, with nothing failing "
                + $"anywhere. It has to carry the icon's own token: {IconFile}?v={token}");
        }

        // And one icon link, so there is a single answer to which url the engine takes.
        var iconLinks = Regex.Matches(markup, @"<link\b[^>]*>", RegexOptions.IgnoreCase)
            .Where(tag => ValuesOf(tag.Value, "rel")
                .SelectMany(rel => rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Any(t => t.Equals("icon", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(iconLinks.Count == 1,
            $"{page} declares {iconLinks.Count} icon links. The engine picks one of them and "
            + "is not obliged to pick the first, so more than one means the url it actually "
            + "requests is not the one this test checked.");
    }

    [Fact]
    public void EveryAssetThePageNamesIsShippedWhereItPoints()
    {
        // A token pinned to a file nobody copied out would pass while the tab showed
        // nothing at all. Derived from the page rather than a hardcoded list, and checked
        // where the reference actually points rather than where it was hoped to point.
        string page = AssetPath("home.html");
        Assert.True(File.Exists(page), $"not shipped: {page}");
        string markup = Markup(File.ReadAllText(page));
        NoBaseElement(page, markup);

        var references = References(markup);

        // Anything the reader above could not parse is a reference this test would skip
        // in silence, which is how a missing asset passes. srcset is counted here and not
        // read, deliberately: it is the one spelling a plain "src=" search walks past,
        // because src is followed by set, so counting it is what makes adding one fail
        // loudly instead of quietly going unchecked.
        int declared = Regex.Matches(markup, @"\b(?:href|src|srcset|imagesrcset)\s*=",
            RegexOptions.IgnoreCase).Count;

        // CSS names assets too, and the reader never sees inside a style block. Counted
        // for the same reason srcset is: a url() the test cannot resolve has to fail here
        // rather than quietly stop being covered.
        var css = Regex.Matches(File.ReadAllText(page), @"<style\b[^>]*>(.*?)</style\s*>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        int cssReferences = css.Sum(block => Regex.Matches(block.Groups[1].Value, @"\burl\s*\(").Count);
        Assert.True(cssReferences == 0,
            $"{page} names {cssReferences} asset(s) from CSS. This test resolves href and "
            + "src only, so teach it about url() before adding one, or it stops covering "
            + "what the page actually asks for.");

        Assert.True(references.Count == declared,
            $"{page} has {declared} reference attributes but only {references.Count} could "
            + "be read. The rest would be skipped rather than checked, so fix the page or "
            + "the reader before trusting this.");

        var local = references
            .Select(reference => (Text: reference, Target: Resolve(page, reference)))
            .Where(r => r.Target.Path is not null)
            .ToList();

        Assert.True(local.Count > 0, $"{page} names no local assets, which it should.");
        foreach (var reference in local)
        {
            Assert.True(File.Exists(reference.Target.Path!),
                $"{page} asks for \"{reference.Text}\", which resolves to "
                + $"{reference.Target.Path}, and nothing is there.");
        }
    }
}
