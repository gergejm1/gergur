using Gergur.Tabs;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Telling a finished sign-in apart from a real load failure. An OAuth helper runs a
/// one-shot listener on a loopback port and closes it the moment it has the code, so
/// the browser's follow-up request is reset. That is success, not an error, but it must
/// not swallow genuine failures.
/// </summary>
public sealed class OAuthCallbackTests
{
    private const CoreWebView2WebErrorStatus Reset = CoreWebView2WebErrorStatus.ConnectionReset;

    [Theory]
    [InlineData("http://127.0.0.1:47465/?code=8fb5e24b&state=f2d9")]
    [InlineData("http://localhost:5000/callback?code=abc")]
    [InlineData("http://[::1]:8080/?access_token=xyz")]
    public void LoopbackRedirectCarryingAnAuthResultIsTreatedAsSuccess(string url)
        => Assert.True(Tab.IsFinishedOAuthCallback(url, Reset));

    [Theory]
    [InlineData(CoreWebView2WebErrorStatus.ConnectionAborted)]
    [InlineData(CoreWebView2WebErrorStatus.ServerUnreachable)]
    [InlineData(CoreWebView2WebErrorStatus.CannotConnect)]
    public void AnyOfTheDeadListenerErrorsCounts(CoreWebView2WebErrorStatus status)
        => Assert.True(Tab.IsFinishedOAuthCallback("http://127.0.0.1:47465/?code=abc", status));

    [Fact]
    public void ARealSiteFailingIsStillAnError()
        => Assert.False(Tab.IsFinishedOAuthCallback("https://github.com/login/oauth?code=abc", Reset));

    [Fact]
    public void ALoopbackPageWithNoAuthResultIsStillAnError()
        => Assert.False(Tab.IsFinishedOAuthCallback("http://127.0.0.1:3000/", Reset));

    [Fact]
    public void ADifferentFailureOnTheSameUrlIsStillAnError()
        => Assert.False(Tab.IsFinishedOAuthCallback(
            "http://127.0.0.1:47465/?code=abc", CoreWebView2WebErrorStatus.HostNameNotResolved));

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("about:blank")]
    public void UnparseableOrSchemelessUrlsAreNotSwallowed(string url)
        => Assert.False(Tab.IsFinishedOAuthCallback(url, Reset));
}

/// <summary>
/// The query parsing behind it. Substring matching read "error_code=200051" on a denied
/// sign-in as a "code=" parameter and reported the refusal as success, hiding the real
/// failure from the user.
/// </summary>
public sealed class AuthorizationResultTests
{
    [Theory]
    [InlineData("?code=8fb5e24b")]
    [InlineData("?state=x&code=abc")]
    [InlineData("?access_token=xyz")]
    public void AnActualAuthorizationResultCounts(string query)
        => Assert.True(Tab.HasAuthorizationResult(query));

    [Theory]
    [InlineData("?error=access_denied&error_code=200051")] // the bug: contains "code="
    [InlineData("?error_code=200051")]
    [InlineData("?error=access_denied")]
    [InlineData("?code=abc&error=consent_required")]       // an error anywhere wins
    public void ADenialIsNeverReportedAsSuccess(string query)
        => Assert.False(Tab.HasAuthorizationResult(query));

    [Theory]
    [InlineData("?mycode=x")]      // must be the whole key, not a suffix
    [InlineData("?codes=x")]
    [InlineData("?code=")]         // present but empty
    [InlineData("?code")]
    [InlineData("")]
    [InlineData("?")]
    public void NothingElseCounts(string query)
        => Assert.False(Tab.HasAuthorizationResult(query));
}
