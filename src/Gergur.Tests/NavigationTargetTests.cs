using Gergur.App;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Which navigation a /navigate?wait call is waiting for.
///
/// This has been wrong twice, and neither way looked like an error. Releasing on any
/// completed navigation meant that navigating a tab that was still loading answered with
/// the old load's abort, in milliseconds, while the new page went on to load perfectly.
/// Claiming "the next navigation to start" was wrong the same way: right after /open the
/// engine has queued a navigation but not yet announced it, so the next wait claimed
/// somebody else's. The url is what tells them apart.
/// </summary>
public sealed class NavigationTargetTests
{
    [Fact]
    public void ANavigationToTheUrlThatWasAskedForIsTheRightOne()
    {
        // Spelled differently on purpose. Two identical strings match on the ordinal fast
        // path, so a test built from those would stay green with the whole comparison
        // below it deleted, which is the part that does the work.
        Assert.True(Tab.SameTarget("https://example.com/a?b=1", "https://example.com/a?b=1"));
        Assert.True(Tab.SameTarget("https://example.com:443/a", "https://example.com/a"));
    }

    [Fact]
    public void TheEnginesNormalisedFormIsStillTheSameUrl()
    {
        // Navigate("https://example.com") is announced as "https://example.com/". A plain
        // string comparison would miss its own navigation on nearly every call, and every
        // wait would sit out its timeout and report a page that loaded as one that did not.
        Assert.True(Tab.SameTarget("https://example.com", "https://example.com/"));
        Assert.True(Tab.SameTarget("HTTPS://Example.COM/", "https://example.com/"));
    }

    [Fact]
    public void AUnicodeHostMatchesThePunycodeTheEngineReports()
    {
        // .NET keeps the unicode host in AbsoluteUri; the engine always announces
        // punycode. Comparing the two whole strings meant a navigation to an accented
        // domain never found its waiter, sat out the full timeout, and reported a page
        // that loaded perfectly as one that had not.
        Assert.True(Tab.SameTarget("https://exämple.com", "https://xn--exmple-cua.com/"));
    }

    [Fact]
    public void APathIsComparedAsWrittenRatherThanFolded()
    {
        // Hosts are case insensitive. Paths are not, on most servers.
        Assert.False(Tab.SameTarget("https://example.com/A", "https://example.com/a"));
        Assert.True(Tab.SameTarget("https://EXAMPLE.com/a", "https://example.com/a"));
    }

    [Fact]
    public void ADifferentPageIsNotTheSameUrl()
    {
        Assert.False(Tab.SameTarget("https://example.com/", "https://example.org/"));
        Assert.False(Tab.SameTarget("https://example.com/a", "https://example.com/b"));
    }

    [Fact]
    public void AQueryIsPartOfWhichPageItIs()
    {
        Assert.False(Tab.SameTarget("https://example.com/?a=1", "https://example.com/?a=2"));
        Assert.True(Tab.SameTarget("https://example.com/?a=1", "https://example.com/?a=1"));
    }

    [Fact]
    public void AFragmentIsPartOfWhichPageItIsToo()
    {
        // Two differing fragments, which the query cases above never exercised: without
        // this the Fragment clause could be deleted and everything stayed green.
        Assert.False(Tab.SameTarget("https://example.com/#one", "https://example.com/#two"));
        Assert.True(Tab.SameTarget("https://example.com/#one", "https://example.com/#one"));
    }

    [Fact]
    public void ABareHashIsTheSamePageAsNoHash()
    {
        // One side reports the fragment as "#" and the other as "", and they are the same
        // page. Read as different, the wait sits out its whole timeout on a page that
        // loaded perfectly.
        Assert.True(Tab.SameTarget("https://example.com/#", "https://example.com/"));
    }

    [Fact]
    public void AFileUrlIsNotDecidedByItsDriveLetter()
    {
        // Windows paths are not case sensitive, and the home page is a file url, so a
        // drive letter alone deciding "different page" would break waiting on it.
        Assert.True(Tab.SameTarget("file:///C:/a/home.html", "file:///c:/a/home.html"));
        Assert.False(Tab.SameTarget("file:///C:/a/home.html", "file:///C:/a/other.html"));
    }

    [Fact]
    public void AboutBlankIsMatchedToo()
    {
        Assert.True(Tab.SameTarget("about:blank", "about:blank"));
        Assert.False(Tab.SameTarget("about:blank", "about:srcdoc"));
    }

    [Fact]
    public void SomethingThatIsNotAUrlStillMatchesItself()
    {
        // No Uri to normalise, so it falls back to comparing the text. Matching itself is
        // the floor: a waiter that cannot recognise its own navigation never gets an answer.
        Assert.True(Tab.SameTarget("not a url", "not a url"));
        Assert.False(Tab.SameTarget("not a url", "also not a url"));
    }
}

/// <summary>
/// Which waiter a starting navigation belongs to. This is where both earlier versions of
/// the /navigate?wait bug lived, so it is pulled out of the live browser and pinned here.
/// </summary>
public sealed class LoadWaiterClaimTests
{
    private static int Claim(string started, params (ulong? Id, string Url)[] waiters)
        => Tab.ClaimIndex(waiters, started);

    [Fact]
    public void TheWaiterThatAskedForThisUrlClaimsIt()
        => Assert.Equal(0, Claim("https://example.com/", (null, "https://example.com/")));

    [Fact]
    public void ANavigationNobodyAskedForIsClaimedByNobody()
    {
        // The whole fix. Right after /open the engine has queued a navigation and not yet
        // announced it, so a wait for a different page must not take that one and then
        // answer about its abort.
        Assert.Equal(-1, Claim("https://already-loading.example/", (null, "https://example.com/")));
    }

    [Fact]
    public void AWaiterThatAlreadyHasOneDoesNotTakeAnother()
    {
        // Otherwise a second navigation to the same url would move the first caller's
        // answer onto a page it was not waiting for.
        Assert.Equal(-1, Claim("https://example.com/", (7UL, "https://example.com/")));
    }

    [Fact]
    public void TwoWaitsForTheSamePageArePairedOldestFirst()
    {
        var waiters = new (ulong?, string)[] { (null, "https://example.com/"), (null, "https://example.com/") };

        Assert.Equal(0, Tab.ClaimIndex(waiters, "https://example.com/"));

        // The first one has its navigation now, so the next one to start is the second's.
        waiters[0] = (11UL, "https://example.com/");
        Assert.Equal(1, Tab.ClaimIndex(waiters, "https://example.com/"));
    }

    [Fact]
    public void AWaiterIsFoundPastOnesForOtherPages()
        => Assert.Equal(1, Claim(
            "https://example.org/",
            (null, "https://example.com/"),
            (null, "https://example.org/")));

    [Fact]
    public void TheEnginesNormalisedUrlStillFindsItsWaiter()
    {
        // Navigate("https://example.com") is announced as "https://example.com/". Missing
        // this means every wait sits out its timeout and reports a load that happened as
        // one that did not.
        Assert.Equal(0, Claim("https://example.com/", (null, "https://example.com")));
    }

    [Fact]
    public void NobodyWaitingIsNotAnError()
        => Assert.Equal(-1, Tab.ClaimIndex([], "https://example.com/"));
}

/// <summary>
/// The one shape /eval answers in. Nothing asserted it before, and it is built from three
/// places, so a drift in any of them is a caller reading the wrong key.
/// </summary>
public sealed class EvalEnvelopeTests
{
    [Fact]
    public void AValueIsCarriedUnderResult()
        => Assert.Equal("""{"ok":true,"result":"hello"}""", Tab.Succeeded("\"hello\""));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NoResultAtAllReadsAsNullRatherThanBrokenJson(string? engineAnswer)
    {
        // The engine hands back an empty string for a script with no value, and pasting
        // that in makes {"ok":true,"result":} which is not json at all.
        Assert.Equal("""{"ok":true,"result":null}""", Tab.Succeeded(engineAnswer!));
    }

    [Fact]
    public void AFailureCarriesTheReason()
    {
        string json = Tab.Failed("it went wrong");

        Assert.Contains("\"ok\":false", json);
        Assert.Contains("it went wrong", json);
    }

    [Fact]
    public void AReasonWithQuotesInItIsStillJson()
    {
        // Engine messages carry quotes and backslashes; built by hand this would be a 200
        // application/json with a body nothing can parse.
        string json = Tab.Failed("""TypeError: cannot read "x" of null\undefined""");

        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.GetProperty("ok").GetBoolean());
    }
}

/// <summary>
/// What the page is allowed to say back.
///
/// The wrapper builds its reply with JSON.stringify, but the page owns JSON.stringify,
/// and a page that hooks postMessage can read the token out of the wrapper's own call and
/// answer in its place. Whatever comes back is served verbatim as application/json, so
/// this is the only thing between a hostile page and a 200 whose body nothing can parse.
/// </summary>
public sealed class EvalReplyTests
{
    [Fact]
    public void TheShapeTheWrapperSendsIsAccepted()
    {
        Assert.True(Tab.IsEnvelope("""{"ok":true,"result":1}"""));
        Assert.True(Tab.IsEnvelope("""{"ok":false,"error":"no"}"""));
        Assert.True(Tab.IsEnvelope("""{"ok":true,"result":{"nested":[1,2]}}"""));
    }

    [Theory]
    [InlineData("123")]                       // valid json, not an envelope
    [InlineData("\"hello\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"result":1}""")]          // no ok at all
    [InlineData("""{"ok":"yes"}""")]          // ok, but not a boolean
    [InlineData("""{"ok":1}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void AnythingElseIsRefused(string reply)
        => Assert.False(Tab.IsEnvelope(reply));
}

/// <summary>
/// The search template is the one free-form string the agent API accepts that is used as
/// a format template and then navigated to. Both of its failure modes are quiet.
/// </summary>
public sealed class SearchTemplateTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q={0}")]
    [InlineData("https://duckduckgo.com/?q={0}&ia=web")]
    [InlineData("http://localhost:8080/search?q={0}")]
    public void AnOrdinaryTemplateIsUsable(string template)
        => Assert.True(UrlHeuristics.IsUsableSearchTemplate(template));

    [Theory]
    [InlineData("https://x/?q={1}")]          // names an argument it was not given
    [InlineData("https://x/?q={")]            // unclosed
    public void ATemplateThatCannotBeFormattedIsRefused(string template)
        => Assert.False(UrlHeuristics.IsUsableSearchTemplate(template));

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/fixed?q={{0}}")]   // escaped: formats to the literal {0}
    public void ATemplateThatIgnoresTheQueryIsRefused(string template)
    {
        // Every search would go to the same page carrying nothing, which is not a search.
        // The escaped form is the one that slipped through a check comparing the result
        // against the template: the string changes while the query is thrown away.
        Assert.False(UrlHeuristics.IsUsableSearchTemplate(template));
    }

    [Fact]
    public void ATemplateThatWouldBuildAnEnormousUrlIsRefused()
        => Assert.False(UrlHeuristics.IsUsableSearchTemplate("https://x/?q={0,1000000}"));

    [Theory]
    [InlineData("javascript:alert({0})")]
    [InlineData("file:///c:/{0}")]
    [InlineData("not a url at all {0}")]
    public void ATemplateThatIsNotAWebUrlIsRefused(string template)
        => Assert.False(UrlHeuristics.IsUsableSearchTemplate(template));

    [Fact]
    public void ABrokenTemplateSearchesRatherThanThrows()
    {
        // The crash this guards: string.Format throws on "{1}", the address bar calls it
        // straight from the Enter key with no try/catch, and there is no unhandled
        // exception handler in the app. A settings file holding one turned every search
        // the user typed into a crash dialog, and it survived restarts.
        string url = UrlHeuristics.ToNavigableUrl("some search terms", "https://x/?q={1}");

        Assert.StartsWith("https://www.google.com/search?q=", url);
        Assert.Contains("some%20search%20terms", url);
    }

    [Fact]
    public void ANullTemplateSearchesRatherThanThrows()
    {
        // "SearchUrlTemplate": null in a hand-edited file deserialises straight over the
        // initialiser, and string.Format throws ArgumentNullException, which is not a
        // FormatException. Nothing catches it above the address bar, so this was the same
        // crash-on-every-search by a different route.
        string url = UrlHeuristics.ToNavigableUrl("cats", null);

        Assert.StartsWith("https://www.google.com/search?q=", url);
        Assert.EndsWith("cats", url);
    }

    [Theory]
    [InlineData("{0}")]                    // formats fine, is not a url
    [InlineData("search {0}")]
    [InlineData("https://example.com/")]   // formats fine, throws the query away
    public void ATemplateThatFormatsButIsNotAUrlStillSearches(string template)
    {
        // Catching FormatException alone let these through: the engine refuses what comes
        // out, TryNavigateCore returns false, and every search from the address bar is a
        // silent no-op with nothing said. That is the browser-you-cannot-type-into this
        // fallback exists to prevent, by a different route than the crash.
        string url = UrlHeuristics.ToNavigableUrl("cats", template);

        Assert.StartsWith("https://www.google.com/search?q=", url);
        Assert.EndsWith("cats", url);
    }

    [Fact]
    public void AGoodTemplateIsStillUsed()
        => Assert.Equal(
            "https://duckduckgo.com/?q=cats",
            UrlHeuristics.ToNavigableUrl("cats", "https://duckduckgo.com/?q={0}"));

    [Fact]
    public void TheFallbackIsWhatANewSettingsFileStartsWith()
    {
        // If these drift, the fallback quietly sends people somewhere they never chose.
        Assert.Equal(UrlHeuristics.DefaultSearchUrlTemplate, new Settings().SearchUrlTemplate);
    }
}
