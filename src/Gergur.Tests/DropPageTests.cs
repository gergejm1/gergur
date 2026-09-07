using System.Text.RegularExpressions;
using Gergur.App;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The pages served to the phone. They are raw strings in C# with their own inline
/// script, so nothing about them is checked by the compiler: a renamed element id or a
/// handler bound to something that is not there compiles and ships. These are the cheap
/// invariants worth holding, alongside the behaviour that was verified in a real browser.
/// </summary>
public sealed class DropPageTests
{
    private const string Key = "0123456789ABCDEF01234567";

    /// <summary>Every id the script looks up, and every id the markup declares.</summary>
    private static (HashSet<string> LookedUp, HashSet<string> Declared) Ids(string html)
    {
        var lookedUp = Regex.Matches(html, """getElementById\("([^"]+)"\)""")
            .Select(m => m.Groups[1].Value).ToHashSet();
        var declared = Regex.Matches(html, "id=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
        return (lookedUp, declared);
    }

    [Fact]
    public void EveryElementTheDropPageScriptBindsToExists()
    {
        var (lookedUp, declared) = Ids(DropPage.Html);

        Assert.NotEmpty(lookedUp);
        Assert.Empty(lookedUp.Except(declared));
    }

    [Fact]
    public void EveryElementTheSetupPageScriptBindsToExists()
    {
        var (lookedUp, declared) = Ids(DropPage.Setup(Key, "192.168.0.20", 24003));

        Assert.NotEmpty(lookedUp);
        Assert.Empty(lookedUp.Except(declared));
    }

    [Fact]
    public void EveryRequestTheDropPageMakesChecksWhetherItSucceeded()
    {
        // The guard that stopped every refusal being rendered as "sent". Counted, not
        // merely present: dropping it from one of the three chains is how "sent" comes
        // back for that one, and an assertion that it appears somewhere would not notice.
        Assert.Contains("function ok(r)", DropPage.Html);

        int fetches = Regex.Matches(DropPage.Html, @"fetch\(").Count;
        int guarded = Regex.Matches(DropPage.Html, @"\.then\(ok\)").Count;

        Assert.Equal(3, fetches);      // items, send, upload
        Assert.Equal(fetches, guarded);
    }

    [Fact]
    public void TheSetupPageCarriesTheAddressToPasteIntoShortcuts()
    {
        string page = DropPage.Setup(Key, "192.168.0.20", 24003);

        Assert.Contains($"http://192.168.0.20:24003/share?k={Key}&amp;text=", page);
        Assert.Contains("Show in Share Sheet", page);
        Assert.Contains("Get Contents of URL", page);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoAddressTheSetupPageSaysSoRatherThanPrintingAPlaceholder(string? address)
    {
        string page = DropPage.Setup(Key, address, 24003);

        Assert.Contains("Cannot tell what this PC", page);
        Assert.DoesNotContain("your-pc-ip", page);
        Assert.DoesNotContain("Copy the address", page);
    }

    [Fact]
    public void TheConfirmationPageEscapesWhatItShows()
    {
        string page = DropPage.Result("Message sent", "<script>alert(1)</script>");

        Assert.DoesNotContain("<script>alert(1)</script>", page);
        Assert.Contains("&lt;script&gt;", page);
    }

    [Fact]
    public void AFailedFirstLoadSaysSoInsteadOfShowingAnEmptyPage()
    {
        // The list is hidden until something renders, and the status line clears itself
        // after a couple of seconds, so a wrong or stale key used to leave a blank page.
        Assert.Contains("empty.textContent = why", DropPage.Html);
        Assert.Contains("empty.hidden = false", DropPage.Html);
        // And the ordinary wording goes back when a load succeeds.
        Assert.Contains("empty.textContent = \"Nothing here yet.\"", DropPage.Html);
    }

    [Fact]
    public void AStatusMessageDoesNotInheritTheLastOnesTimer()
    {
        // Without clearing it, a second message is erased by the first one's countdown,
        // which is how an error could flash for a fraction of a second. Order matters as
        // much as presence: clearing after the new timer is set cancels the new one, and
        // the status line then never clears at all.
        string say = Body(DropPage.Html, "function say(text, hold)");

        int cleared = say.IndexOf("clearTimeout", StringComparison.Ordinal);
        int set = say.IndexOf("setTimeout", StringComparison.Ordinal);

        Assert.True(cleared >= 0 && set >= 0, "say() no longer manages its own timer");
        Assert.True(cleared < set, "the timer is cleared after it is set, which cancels the new one");
    }

    [Fact]
    public void AMultiFileSendKeepsGoingAfterOneFails()
    {
        // The chain used to abort, leaving the rest of a batch silently unsent and the
        // label stuck at "Sending 2 of 5". Recording the failure is only half of it: a
        // rethrow after recording aborts the chain just the same.
        string handler = Body(DropPage.Html, "document.getElementById(\"pick\")");

        Assert.Contains("failures.push", handler);
        Assert.Contains("failures.length", handler);
        Assert.DoesNotContain("throw", handler);
    }

    /// <summary>
    /// One handler or function, from its marker to the blank line that ends it. Bounded
    /// rather than running to the end of the script, or an assertion about this handler
    /// quietly becomes an assertion about everything after it.
    /// </summary>
    private static string Body(string page, string marker)
    {
        int at = page.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the page no longer contains {marker}");

        int end = page.IndexOf("\n\n", at, StringComparison.Ordinal);
        if (end < 0)
            end = page.IndexOf("</script>", at, StringComparison.Ordinal);
        return page[at..(end < 0 ? page.Length : end)];
    }

    [Fact]
    public void AFailurePageDoesNotShowASuccessTick()
    {
        string bad = DropPage.Result("Nothing to send", "The share arrived empty.", good: false);
        string good = DropPage.Result("Link sent", "https://example.com");

        Assert.DoesNotContain("&#10003;", bad);
        Assert.Contains("&#10003;", good);
    }
}
