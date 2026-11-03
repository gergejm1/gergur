using System.Text.Json;
using System.Web;
using Gergur.Blocking;

namespace Gergur.App;

/// <summary>
/// "Sign in with Google": which site a Google account chooser is choosing for, and which of
/// the person's Google accounts they picked there.
///
/// The person asked for this: most of their sign-ins are through Google, and all an agent then
/// has to do is click the right account. Which one is right is learned from the person's own
/// sign-ins, never from an agent's: a script on Google's sign-in pages (<see cref="ChoiceScript"/>)
/// tells the window which account entry was clicked, and the window keeps it for the site the
/// sign-in was for (<see cref="Data.GoogleAccountStore"/>), only in the person's own window
/// and only when no agent acted in that tab just before. An agent's `gergur_login` on a chooser
/// then clicks that account (or the one it names). Kept in its own file, not the settings, so it
/// never reaches `GET /settings`; the settings page lists it and forgets it.
///
/// What is not here: Google's consent screen ("Continue to share your name and email") is never
/// clicked, that is the person's decision; nor a password or a check Google asks for, nor the
/// One Tap card a site shows in a frame of its own page.
///
/// Google's pages are not something to try things against, so this was written against a
/// stand-in page on a local host: the account entries carry the address in `data-identifier`
/// (the account chooser) or `data-email` (the Sign in with Google popup). If Google names them
/// differently, nothing is recorded and the agent is told the account is not listed; nothing is
/// clicked by guess.
/// </summary>
internal static class GoogleSignIn
{
    internal const string RealOrigin = "https://accounts.google.com";

    /// <summary>
    /// Where Google's sign-in pages are: Google, except in a test instance (both
    /// <c>GERGUR_DATA_DIR</c> and <c>GERGUR_TEST_GOOGLE_ORIGIN</c> set), where a local stand-in
    /// takes its place so the whole path can be run without going near Google or a real account.
    /// </summary>
    internal static string Origin { get; } = OriginFrom(
        Environment.GetEnvironmentVariable("GERGUR_DATA_DIR"),
        Environment.GetEnvironmentVariable("GERGUR_TEST_GOOGLE_ORIGIN"));

    internal static string OriginFrom(string? dataDir, string? testOrigin)
        => !string.IsNullOrWhiteSpace(dataDir) && Uri.TryCreate(testOrigin?.Trim(), UriKind.Absolute, out var test)
            && (test.Scheme == Uri.UriSchemeHttp || test.Scheme == Uri.UriSchemeHttps)
            && (test.IsLoopback || test.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                ? test.GetLeftPart(UriPartial.Authority)
                : RealOrigin;

    /// <summary>Whether this page is one of Google's sign-in pages.</summary>
    internal static bool IsGooglePage(string? url) => IsGooglePage(url, Origin);

    internal static bool IsGooglePage(string? url, string origin)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);

    // Where a sign-in carries the site it is for, in the order they are looked at. continue and
    // followup carry a further Google address, which is looked into in turn.
    private static readonly string[] Carriers = ["redirect_uri", "origin", "app_domain", "continue", "followup"];

    /// <summary>
    /// The site a Google sign-in page is choosing an account for, as the settings show it
    /// ("example.com"), or empty when it cannot be told. Read from the sign-in's own address:
    /// the redirect, origin or app domain it names, followed through the Google addresses it
    /// continues to. Google's own services are not a site here.
    /// </summary>
    internal static string SiteOf(string? url) => SiteOf(url, Origin, 0);

    internal static string SiteOf(string? url, string origin, int depth = 0)
    {
        if (depth > 4 || !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
            return "";
        // An older popup's redirect: storagerelay://https/example.com?id=auth123
        if (uri.Scheme == "storagerelay")
            return depth > 0 ? RelaxedSites.Clean(uri.AbsolutePath.Trim('/').Split('/')[0]) : "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "";
        bool google = IsGooglePage(uri.AbsoluteUri, origin) || IsGoogleHost(uri.Host);
        if (!google)
            return depth > 0 ? RelaxedSites.Clean(uri.Host) : "";
        var query = HttpUtility.ParseQueryString(uri.Query);
        foreach (string carrier in Carriers)
        {
            if (query[carrier] is { Length: > 0 } carried && SiteOf(carried, origin, depth + 1) is { Length: > 0 } site)
                return site;
        }
        return "";
    }

    /// <summary>
    /// The site a page itself is on, as the settings show it ("example.com"): for Google's own
    /// sign-in chooser drawn over a site's page (FedCM), where the page is the site. Empty for
    /// anything not a web address and for Google's own pages.
    /// </summary>
    internal static string SiteOfPage(string? url) => SiteOfPage(url, Origin);

    internal static string SiteOfPage(string? url, string origin)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !IsGooglePage(uri.AbsoluteUri, origin) && !IsGoogleHost(uri.Host)
                ? RelaxedSites.Clean(uri.Host)
                : "";

    private static bool IsGoogleHost(string host)
        => host.Equals("google.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A Google account as kept and compared: trimmed, lower case, one @ with something either
    /// side, at most 200 characters, no spaces, quotes, angle brackets or control characters.
    /// Null for anything else.
    /// </summary>
    internal static string? CleanEmail(string? text)
    {
        string email = (text ?? "").Trim().ToLowerInvariant();
        int at = email.IndexOf('@');
        if (email.Length is < 3 or > 200 || at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1)
            return null;
        foreach (char c in email)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or '\\' or '<' or '>' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                return null;
        }
        return email;
    }

    /// <summary>How long after an agent acted in a tab a choice made there is not taken for the person's.</summary>
    internal static readonly TimeSpan AgentQuiet = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether an account clicked on Google's sign-in was the person's own choice: in one of their
    /// windows, with no agent acting in that tab, or in the tab that opened it (a sign-in popup),
    /// within <see cref="AgentQuiet"/>. An agent's trusted click looks exactly like the person's
    /// to the page, so this is decided by who was there, never by the event.
    /// </summary>
    internal static bool IsThePersonsChoice(bool windowOpenedByAgent, bool agentActedInTab, bool agentActedInOpener)
        => !windowOpenedByAgent && !agentActedInTab && !agentActedInOpener;

    /// <summary>The same, read off the tab at the moment the choice arrives.</summary>
    /// <remarks>
    /// A page an agent ran script in counts as the agent acting there for as long as that page
    /// is shown, not just for <see cref="AgentQuiet"/>: a script can post the choice, or click an
    /// entry, long after it ran.
    /// </remarks>
    internal static bool IsThePersonsChoice(bool windowOpenedByAgent, Tabs.Tab tab)
        => IsThePersonsChoice(windowOpenedByAgent,
            tab.AgentActedWithin(AgentQuiet) || tab.AgentScriptedThisDocument,
            tab.Opener is { } opener && (opener.AgentActedWithin(AgentQuiet) || opener.AgentScriptedThisDocument));

    /// <summary>
    /// The account a web message says was chosen, when it is the choice script's message, from
    /// the top page of Google's sign-in, carrying an address; null for anything else. Any page
    /// can post the same text, so where it came from is what counts.
    /// </summary>
    internal static string? ChoiceFrom(string? message, string? source) => ChoiceFrom(message, source, Origin);

    internal static string? ChoiceFrom(string? message, string? source, string origin)
        => message is not null && message.StartsWith(ChoiceMessage, StringComparison.Ordinal) && IsGooglePage(source, origin)
            ? CleanEmail(message[ChoiceMessage.Length..])
            : null;

    /// <summary>
    /// Whether this Google page is a consent screen ("Sign in to example.com", Continue), which
    /// is the person's to answer: an agent's sign-in never clicks anything there, an account
    /// chip on it included.
    /// </summary>
    internal static bool IsConsentPage(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.Contains("consent", StringComparison.OrdinalIgnoreCase);

    /// <summary>The prefix of the message the choice script posts.</summary>
    internal const string ChoiceMessage = "GERGUR_GOOGLE:";

    /// <summary>
    /// Run at the start of every page; does nothing anywhere but the top page of Google's
    /// sign-in. There, a click (or Enter or Space) on an account entry posts that account to the
    /// window, which decides whether it was the person (see the class summary). Only the
    /// address is posted, and only one shaped like one.
    /// </summary>
    internal static string ChoiceScript => ChoiceScriptFor(Origin);

    internal static string ChoiceScriptFor(string origin) => """
        (function () {
          try {
            if (location.origin !== ORIGIN || window.top !== window) return;
            var pick = function (e) {
              if (e.type === 'keydown' && e.key !== 'Enter' && e.key !== ' ') return;
              var t = e.target && e.target.closest ? e.target.closest('[data-identifier],[data-email]') : null;
              if (!t) return;
              var email = (t.getAttribute('data-identifier') || t.getAttribute('data-email') || '').trim();
              if (email.length > 200 || !/^[^\s@]+@[^\s@]+$/.test(email)) return;
              try { window.chrome.webview.postMessage('GERGUR_GOOGLE:' + email); } catch (x) { }
            };
            document.addEventListener('click', pick, true);
            document.addEventListener('keydown', pick, true);
          } catch (e) { }
        })();
        """.Replace("ORIGIN", JsonSerializer.Serialize(origin));

    /// <summary>
    /// Marks the shown account entry for <paramref name="email"/> with <c>data-gergur-google</c>
    /// set to the nonce, to be clicked like anything else. Answers how many accounts the page
    /// lists and whether that one is among them, never the addresses themselves.
    /// </summary>
    internal static string MarkJs(string nonce, string email) => """
        (() => {
          const want = EMAIL;
          const of = el => (el.getAttribute('data-identifier') || el.getAttribute('data-email') || '').trim().toLowerCase();
          document.querySelectorAll('[data-gergur-google]').forEach(el => el.removeAttribute('data-gergur-google'));
          const shown = [...document.querySelectorAll('[data-identifier],[data-email]')].filter(el => {
            const r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0 && of(el).includes('@');
          });
          const hit = shown.find(el => of(el) === want);
          if (hit) hit.setAttribute('data-gergur-google', NONCE);
          return { found: !!hit, accounts: new Set(shown.map(of)).size };
        })()
        """.Replace("NONCE", JsonSerializer.Serialize(nonce)).Replace("EMAIL", JsonSerializer.Serialize(email));

    internal sealed record Marked(bool Found, int Accounts);

    internal static Marked? ParseMarked(string? raw)
    {
        try
        {
            if (raw is null)
                return null;
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return new Marked(
                root.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True,
                root.TryGetProperty("accounts", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out int n) ? n : 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
