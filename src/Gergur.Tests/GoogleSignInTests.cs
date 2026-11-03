using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Data;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// "Sign in with Google": which site a sign-in is for, which account the person picked, that
/// only the person's own picks are kept, and what the settings page can forget. Every address
/// here is made up, and no test reaches the real google-accounts.json.
/// </summary>
public sealed class GoogleSignInTests : IDisposable
{
    private const string Google = GoogleSignIn.RealOrigin;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gergur-tests", "google-" + Guid.NewGuid().ToString("N"));

    public GoogleSignInTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gergur.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Gergur.slnx not found above the test binaries");
    }

    private static string Code(string relative)
        => Regex.Replace(File.ReadAllText(Path.Combine(Root(), "src", "Gergur", relative)), @"//[^\n]*", "");

    private static string E(string s) => Uri.EscapeDataString(s);

    public static TheoryData<string, string> Flows => new()
    {
        // The redirect a site sends the person to.
        { $"{Google}/o/oauth2/v2/auth?client_id=1.apps&redirect_uri={E("https://www.example.com/auth/callback")}&response_type=code", "example.com" },
        // The chooser it lands on, the site carried inside the address it continues to.
        { $"{Google}/v3/signin/accountchooser?continue={E($"{Google}/signin/oauth/consent?client_id=1.apps&redirect_uri={E("https://shop.example.org/cb")}")}&flowName=GeneralOAuthFlow", "shop.example.org" },
        // The Sign in with Google popup names the page's origin.
        { $"{Google}/gsi/select?client_id=1.apps&ux_mode=popup&origin={E("https://news.example.net")}", "news.example.net" },
        { $"{Google}/v3/signin/identifier?app_domain={E("https://app.example.com")}&continue={E($"{Google}/x")}", "app.example.com" },
        // An older popup's relay.
        { $"{Google}/o/oauth2/auth?redirect_uri={E("storagerelay://https/example.com?id=auth1")}", "example.com" },
        // A site whose sign-in goes back to it by continue alone.
        { $"{Google}/ServiceLogin?continue={E("https://www.youtube.com/signin?next=%2F")}", "youtube.com" },
        // Google's own services are not a site here, nor anything with no site in it.
        { $"{Google}/ServiceLogin?continue={E("https://mail.google.com/mail/")}", "" },
        { $"{Google}/v3/signin/accountchooser", "" },
        { $"{Google}/o/oauth2/auth?redirect_uri={E("javascript:alert(1)")}", "" },
        { $"{Google}/o/oauth2/auth?redirect_uri={E("https://10.0.0.1/cb")}", "" },
        // Not a Google page at all: the page itself is not a sign-in.
        { $"https://example.com/login?redirect_uri={E("https://other.example.org/")}", "" },
    };

    [Theory]
    [MemberData(nameof(Flows))]
    public void TheSiteASignInIsForIsReadFromItsAddress(string url, string site)
        => Assert.Equal(site, GoogleSignIn.SiteOf(url, Google));

    [Fact]
    public void ASignInNestedTooDeepIsNotFollowedForever()
    {
        string url = "https://shop.example.org/cb";
        for (int i = 0; i < 8; i++)
            url = $"{Google}/x?continue={E(url)}";
        Assert.Equal("", GoogleSignIn.SiteOf(url, Google));
    }

    [Theory]
    [InlineData("https://accounts.google.com/v3/signin/accountchooser?x=1", true)]
    [InlineData("https://ACCOUNTS.google.com/gsi/select", true)]
    [InlineData("http://accounts.google.com/", false)]
    [InlineData("https://accounts.google.com.example.com/", false)]
    [InlineData("https://myaccount.google.com/", false)]
    [InlineData("https://example.com/accounts.google.com", false)]
    [InlineData(null, false)]
    public void OnlyGooglesOwnSignInCounts(string? url, bool google)
        => Assert.Equal(google, GoogleSignIn.IsGooglePage(url, Google));

    [Theory]
    [InlineData(null, "http://accounts.localhost:41791", Google)]                           // not a test instance
    [InlineData(@"C:\scratch", null, Google)]
    [InlineData(@"C:\scratch", "http://accounts.localhost:41791/x", "http://accounts.localhost:41791")]
    [InlineData(@"C:\scratch", "http://127.0.0.1:41791", "http://127.0.0.1:41791")]
    [InlineData(@"C:\scratch", "https://evil.example.com", Google)]                         // never somewhere real
    [InlineData(@"C:\scratch", "not a url", Google)]
    public void AStandInForGoogleIsOnlyEverLocalAndOnlyInATestInstance(string? dataDir, string? test, string origin)
        => Assert.Equal(origin, GoogleSignIn.OriginFrom(dataDir, test));

    [Theory]
    [InlineData(" Person.A@Example.com ", "person.a@example.com")]
    [InlineData("a@b", "a@b")]
    [InlineData("no-at-sign", null)]
    [InlineData("@example.com", null)]
    [InlineData("person@", null)]
    [InlineData("a@b@c", null)]
    [InlineData("pers on@example.com", null)]
    [InlineData("person\"@example.com", null)]
    [InlineData("person<x>@example.com", null)]
    [InlineData("person\u202e@example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AnAddressIsKeptOnlyWhenItReadsAsOne(string? text, string? kept)
        => Assert.Equal(kept, GoogleSignIn.CleanEmail(text));

    [Fact]
    public void AnAddressIsAtMostTwoHundredCharacters()
    {
        Assert.NotNull(GoogleSignIn.CleanEmail(new string('a', 188) + "@example.com"));
        Assert.Null(GoogleSignIn.CleanEmail(new string('a', 189) + "@example.com"));
    }

    [Theory]
    [InlineData("GERGUR_GOOGLE:Person.A@Example.com", "https://accounts.google.com/v3/signin/accountchooser", "person.a@example.com")]
    [InlineData("GERGUR_GOOGLE:person.a@example.com", "https://example.com/fake-chooser", null)]           // any page can post the text
    [InlineData("GERGUR_GOOGLE:person.a@example.com", "http://accounts.google.com/", null)]
    [InlineData("GERGUR_GOOGLE:person.a@example.com", null, null)]
    [InlineData("GERGUR_GOOGLE:not an address", "https://accounts.google.com/x", null)]
    [InlineData("GERGUR_ERR:person.a@example.com", "https://accounts.google.com/x", null)]
    [InlineData(null, "https://accounts.google.com/x", null)]
    public void AChoiceIsTakenOnlyFromGooglesOwnPage(string? message, string? source, string? email)
        => Assert.Equal(email, GoogleSignIn.ChoiceFrom(message, source, Google));

    [Theory]
    [InlineData("https://accounts.google.com/signin/oauth/consent?client_id=1", true)]
    [InlineData("https://accounts.google.com/o/oauth2/v2/Consent", true)]
    [InlineData("https://accounts.google.com/v3/signin/accountchooser?continue=https%3A%2F%2Faccounts.google.com%2Fsignin%2Foauth%2Fconsent", false)]
    [InlineData("https://accounts.google.com/gsi/select?client_id=1", false)]
    [InlineData(null, false)]
    public void TheConsentScreenIsToldByItsPathNotWhatItCarries(string? url, bool consent)
        => Assert.Equal(consent, GoogleSignIn.IsConsentPage(url));

    [Theory]
    [InlineData(false, false, false, true)]    // the person, in their own window
    [InlineData(true, false, false, false)]    // an agent's window
    [InlineData(false, true, false, false)]    // an agent clicked in the person's tab
    [InlineData(false, false, true, false)]    // an agent opened the sign-in popup
    public void OnlyThePersonsOwnChoiceIsRemembered(bool agentWindow, bool agentInTab, bool agentInOpener, bool kept)
        => Assert.Equal(kept, GoogleSignIn.IsThePersonsChoice(agentWindow, agentInTab, agentInOpener));

    [Theory]
    [InlineData("""{"found":true,"accounts":3}""", true, 3)]
    [InlineData("""{"found":false,"accounts":0}""", false, 0)]
    [InlineData("""{"accounts":"x"}""", false, 0)]
    public void TheMarkSaysOnlyWhetherAndHowMany(string raw, bool found, int accounts)
        => Assert.Equal(new GoogleSignIn.Marked(found, accounts), GoogleSignIn.ParseMarked(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("not json")]
    public void AMarkThatIsNotOneIsNothing(string? raw) => Assert.Null(GoogleSignIn.ParseMarked(raw));

    [Fact]
    public void TheScriptsCarryTheirValuesAsLiterals()
    {
        string mark = GoogleSignIn.MarkJs("abc123", "o'neil\"@example.com");
        // Quotes of either kind arrive escaped, so a value can never close its string.
        Assert.Contains("const want = \"o\\u0027neil\\u0022@example.com\";", mark);
        Assert.Contains("setAttribute('data-gergur-google', \"abc123\")", mark);
        Assert.Contains("location.origin !== \"https://accounts.google.com\"", GoogleSignIn.ChoiceScriptFor(Google));
        // Never the addresses back, only whether and how many.
        Assert.Contains("return { found: !!hit, accounts: new Set(shown.map(of)).size };", mark);
    }

    [Fact]
    public async Task TheScriptsDecideRightOnAStandInChooser()
    {
        string dir = Path.Combine(_dir, "scripts");
        Directory.CreateDirectory(dir);
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(Root(), "src", "Gergur.Tests", "GoogleScripts.harness.js"));
        foreach (var (name, script) in new[]
        {
            ("choice.js", GoogleSignIn.ChoiceScriptFor("http://accounts.localhost:41791")),
            ("mark-b.js", GoogleSignIn.MarkJs("n0nce", "person.b@example.com")),
            ("mark-c.js", GoogleSignIn.MarkJs("n0nce", "person.c@example.com")),
            ("mark-nobody.js", GoogleSignIn.MarkJs("n0nce", "nobody@example.com")),
        })
        {
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, script);
            start.ArgumentList.Add(file);
        }
        using var node = Process.Start(start) ?? throw new InvalidOperationException("node did not start (it is needed by the checks)");
        var output = node.StandardOutput.ReadToEndAsync();
        var errors = node.StandardError.ReadToEndAsync();
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await node.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                node.Kill(entireProcessTree: true);
                Assert.Fail("the harness did not finish in 30 seconds");
            }
        }
        string said = await output + await errors;
        Assert.True(node.ExitCode == 0, said);
        Assert.Contains("all passed", said);
    }

    // ------------------------------------------------------------------ the store

    private string StorePath => Path.Combine(_dir, "google-accounts.json");

    [Fact]
    public void AChoiceIsKeptPerSiteAndTheLatestWins()
    {
        var store = new GoogleAccountStore(StorePath);
        var t = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(store.Remember("www.Example.com", "Person.A@Example.com", t));
        Assert.True(store.Remember("shop.example.org", "person.b@example.com", t));
        Assert.Equal("person.a@example.com", store.For("example.com"));
        Assert.True(store.Remember("example.com", "person.b@example.com", t.AddDays(1)));
        Assert.Equal("person.b@example.com", store.For("EXAMPLE.com"));
        Assert.Equal(["example.com", "shop.example.org"], store.Items.Select(a => a.Site));

        var again = new GoogleAccountStore(StorePath);
        Assert.Equal("person.b@example.com", again.For("example.com"));
        Assert.Equal(2, again.Items.Count);
    }

    [Theory]
    [InlineData("", "person.a@example.com")]
    [InlineData("localhost", "person.a@example.com")]
    [InlineData("10.0.0.1", "person.a@example.com")]
    [InlineData("example.com", "not an address")]
    public void NothingIsKeptThatDoesNotReadAsASiteAndAnAddress(string site, string email)
    {
        var store = new GoogleAccountStore(StorePath);
        Assert.False(store.Remember(site, email, DateTime.UtcNow));
        Assert.Empty(store.Items);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void ForgettingTakesItAwayForGood()
    {
        var store = new GoogleAccountStore(StorePath);
        store.Remember("example.com", "person.a@example.com", DateTime.UtcNow);
        store.Remember("shop.example.org", "person.b@example.com", DateTime.UtcNow);
        Assert.True(store.Forget("example.com"));
        Assert.False(store.Forget("example.com"));
        Assert.Null(new GoogleAccountStore(StorePath).For("example.com"));
        store.ForgetAll();
        Assert.Empty(new GoogleAccountStore(StorePath).Items);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""[null]""")]
    [InlineData("""[{"Site":"example.com","Email":"not an address","ChosenUtc":"2026-11-03T00:00:00Z"}]""")]
    [InlineData("""[{"Site":"*.example.com","Email":"a@b","ChosenUtc":"2026-11-03T00:00:00Z"}]""")]
    public void AFileThatCannotBeReadIsNeverWrittenOver(string content)
    {
        File.WriteAllText(StorePath, content);
        var store = new GoogleAccountStore(StorePath);
        Assert.True(store.Unreadable);
        Assert.Empty(store.Items);
        Assert.Throws<IOException>(() => store.Remember("example.com", "person.a@example.com", DateTime.UtcNow));
        Assert.Throws<IOException>(() => store.ForgetAll());
        Assert.Equal(content, File.ReadAllText(StorePath));
    }

    [Fact]
    public void TheRealFileIsWhereTheDataIs()
        => Assert.Equal(Path.Combine(Settings.DataDir, "google-accounts.json"), GoogleAccountStore.DefaultPath);

    // ------------------------------------------------------------------ the wiring

    [Fact]
    public void TheChoiceIsHeardOnlyFromGoogleAndKeptOnlyForThePerson()
    {
        string tab = Code("Tabs/Tab.cs");
        string form = Code("UI/MainForm.cs");
        Assert.Contains("await core.AddScriptToExecuteOnDocumentCreatedAsync(GoogleSignIn.ChoiceScript);", tab);
        Assert.Contains("else if (GoogleSignIn.ChoiceFrom(message, e.Source) is { } email)", tab);
        Assert.Matches(@"tab\.GoogleAccountChosen \+= \(_, choice\) =>\s*\{\s*if \(_session is null \|\| !GoogleSignIn\.IsThePersonsChoice\(OpenedByAgent, tab\)\)\s*return;", form);
        // An agent acting just before, or having scripted the page at all (a script can post
        // the choice, or click an entry, long after it ran), in the tab or the one that opened it.
        Assert.Contains("tab.AgentActedWithin(AgentQuiet) || tab.AgentScriptedThisDocument,", Code("App/GoogleSignIn.cs"));
        Assert.Contains("tab.Opener is { } opener && (opener.AgentActedWithin(AgentQuiet) || opener.AgentScriptedThisDocument)", Code("App/GoogleSignIn.cs"));
        Assert.Equal("GERGUR_GOOGLE:", GoogleSignIn.ChoiceMessage);
        // A tab moved to another window stops telling the old one.
        Assert.Matches(@"internal void DetachOwnerHandlers\(\)\s*\{[^}]*GoogleAccountChosen = null;", tab);
        Assert.Contains("'GERGUR_GOOGLE:' + email", GoogleSignIn.ChoiceScriptFor(Google));
    }

    [Fact]
    public void AnAgentsSignInClicksOnlyTheAccountAndOnlyOnAPageNobodyScripted()
    {
        string server = Code("App/AgentServer.cs");
        var google = Regex.Match(server, @"async Task<\(int, string, byte\[\]\)> GoogleLoginAsync\(Tab tab, string\? asked\)[\s\S]*?\r?\n        \}\r?\n").Value;
        Assert.NotEmpty(google);
        // Taken as the agent's action before anything, so its own click is never remembered as the person's.
        Assert.Matches(@"Acting\(tab\);\s*if \(tab\.FedCm is \{ \} fedCm && fedCm\.Current is \{ \} shown\s*&& FedCmWatch\.Applies\([^;]*\)\)\s*return await FedCmLoginAsync\(tab, fedCm, shown, asked\);\s*if \(GoogleSignIn\.IsGooglePage\(tab\.CommittedSourceForOtherThreads\)\)\s*return await GoogleLoginAsync\(tab, asked\);", server);
        // The account named, else the person's for that site, else nothing is clicked.
        Assert.Contains("_session.GoogleAccounts.For(site) is { } kept", google);
        Assert.Matches(@"if \(email is null\)\s*return \(200[^;]*NoGoogleAccountRemembered", google);
        Assert.Matches(@"if \(BoolFrom\(""submit""\) == false\)\s*return", google);
        // Nothing at all is clicked on Google's consent screen, before anything is marked.
        Assert.Matches(@"if \(GoogleSignIn\.IsConsentPage\(url\)\)\s*return \(200[^;]*GoogleStillAsking[^;]*;[\s\S]*GoogleSignIn\.MarkJs", google);
        // Not on a page an agent scripted, before the mark and again right before the click.
        Assert.Equal(2, Regex.Matches(google, @"if \(tab\.AgentScriptedThisDocument\)\s*return \(409").Count);
        // And the page still the one measured: one that moved on would take the click at that point.
        Assert.Matches(@"if \(tab\.AgentScriptedThisDocument\)\s*return \(409[^;]*;\s*if \(tab\.CommittedSourceForOtherThreads != url\)\s*return \(200[^;]*GoogleMovedOn[^;]*;\s*try\s*\{\s*await OnUiAsync\(\(\) => TabInput\.ClickAsync\(tab, spot\.X, spot\.Y\)\);", google);
        // One click, never repeated, and nothing after it is pressed.
        Assert.Single(Regex.Matches(google, @"TabInput\.ClickAsync"));
        Assert.DoesNotContain("PressAsync", google);
        Assert.DoesNotContain("InsertTextAsync", google);
    }

    // ------------------------------------------------------------------ named by an agent

    [Fact]
    public void AnAccountAnAgentNamedIsKeptAsItsUntilThePersonChooses()
    {
        var store = new GoogleAccountStore(StorePath);
        var t = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(store.Remember("example.com", "person.a@example.com", t, "Claude Code"));
        Assert.Equal("Claude Code", store.NamedByFor("example.com"));
        Assert.Equal("Claude Code", new GoogleAccountStore(StorePath).NamedByFor("example.com"));

        // The same account named again, by any agent, changes nothing (not even who named it).
        Assert.True(store.Remember("example.com", "person.a@example.com", t.AddHours(1), "Other Agent"));
        Assert.Equal("Claude Code", store.NamedByFor("example.com"));
        Assert.Equal(t, store.Items.Single().ChosenUtc);

        // The person choosing it themselves makes it theirs.
        Assert.True(store.Remember("example.com", "person.a@example.com", t.AddHours(2)));
        Assert.Null(store.NamedByFor("example.com"));

        // An agent naming another account takes over, as the latest; the person's pick then
        // takes it back.
        Assert.True(store.Remember("example.com", "person.b@example.com", t.AddHours(3), "Claude Code"));
        Assert.Equal(("person.b@example.com", "Claude Code"), (store.For("example.com"), store.NamedByFor("example.com")));
        Assert.True(store.Remember("example.com", "person.a@example.com", t.AddHours(4)));
        Assert.Equal(("person.a@example.com", (string?)null), (store.For("example.com"), store.NamedByFor("example.com")));
        Assert.Null(store.NamedByFor("nowhere.example"));
    }

    [Theory]
    [InlineData("Claude Code", "Claude Code")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("", "an agent")]
    [InlineData("\u202Ehidden\u0007", "hidden")]
    public void WhoNamedItIsOnePrintableLine(string name, string shown) => Assert.Equal(shown, GoogleAccountStore.CleanName(name));

    [Fact]
    public void WhoNamedItIsAtMostSixtyCharacters() => Assert.Equal(new string('x', 60), GoogleAccountStore.CleanName(new string('x', 200)));

    [Fact]
    public void AFileWithANameThatIsNotCleanIsNotRead()
    {
        File.WriteAllText(StorePath, """[{"Site":"example.com","Email":"a@b.example","ChosenUtc":"2026-11-03T00:00:00Z","NamedBy":"bad\u0007name"}]""");
        Assert.True(new GoogleAccountStore(StorePath).Unreadable);
    }

    [Fact]
    public void AnAgentsNamedAccountIsKeptOnlyOnceTheSignInIsPastGoogle()
    {
        string server = Code("App/AgentServer.cs");
        // Only an account the agent named, only with the site known, only once Google has
        // stopped asking: in both ways of signing in with Google.
        Assert.Equal(2, Regex.Matches(server, @"bool learned = asked is not null && !still && siteOut is not null && await RememberNamedAsync\(siteOut, email\);").Count);
        var keep = Regex.Match(server, @"async Task<bool> RememberNamedAsync\(string site, string email\)[\s\S]*?\r?\n        \}\r?\n").Value;
        Assert.Contains("_session.GoogleAccounts.For(site) == email", keep);
        Assert.Contains("_session.GoogleAccounts.Remember(site, email, DateTime.UtcNow, caller.Name)", keep);
        Assert.Contains("_session.Agents.Record(caller,", keep);
        Assert.Contains("catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)", keep);
    }

    // ------------------------------------------------------------------ FedCM

    private static string Shown(string type, params (string Email, string Idp, string State)[] accounts)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            dialogId = "d1",
            dialogType = type,
            title = "Sign in to example.com",
            accounts = accounts.Select((a, i) => new { accountId = "id" + i, email = a.Email, name = "N", idpConfigUrl = a.Idp, loginState = a.State }),
        });

    private const string GoogleConfig = Google + "/gsi/fedcm.json";

    [Fact]
    public void TheChooserIsReadFromWhatTheBrowserSays()
    {
        var d = FedCmWatch.ParseDialog(Shown("AccountChooser", ("person.a@example.com", GoogleConfig, "SignIn"), ("person.b@example.com", GoogleConfig, "SignUp")))!;
        Assert.Equal(("d1", "AccountChooser", 2), (d.Id, d.Type, d.Accounts.Count));
        Assert.Equal(new FedCmWatch.Account("id0", "person.a@example.com", GoogleConfig, true), d.Accounts[0]);
        Assert.False(d.Accounts[1].SignedInBefore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"dialogType":"AccountChooser"}""")]
    [InlineData("""{"dialogId":7}""")]
    public void WhatDoesNotReadAsAChooserIsNone(string? json) => Assert.Null(FedCmWatch.ParseDialog(json));

    [Fact]
    public void OddAccountsAreReadAsNothingChoosable()
    {
        var d = FedCmWatch.ParseDialog("""{"dialogId":"d","dialogType":"AccountChooser","accounts":[7,{"email":5,"loginState":"SignIn"},{"accountId":"x"}]}""")!;
        Assert.Equal(2, d.Accounts.Count);
        Assert.Equal(FedCmWatch.Refusal.NotGoogle, FedCmWatch.Choose(d, "person.a@example.com", Google).Why);
    }

    public static TheoryData<string, string, int, string> Choices => new()
    {
        // Google's, the one asked for, used with this site before: chosen.
        { Shown("AccountChooser", ("other@example.com", "https://idp.example/fedcm.json", "SignIn"), ("Person.A@Example.com", GoogleConfig, "SignIn")), "person.a@example.com", 1, nameof(FedCmWatch.Refusal.None) },
        // The same address from another provider is not Google's account.
        { Shown("AccountChooser", ("person.a@example.com", "https://idp.example/fedcm.json", "SignIn")), "person.a@example.com", -1, nameof(FedCmWatch.Refusal.NotGoogle) },
        { Shown("AccountChooser", ("person.a@example.com", "https://accounts.google.com.evil.example/fedcm.json", "SignIn")), "person.a@example.com", -1, nameof(FedCmWatch.Refusal.NotGoogle) },
        { Shown("AccountChooser", ("person.b@example.com", GoogleConfig, "SignIn")), "person.a@example.com", -1, nameof(FedCmWatch.Refusal.NotListed) },
        // A first sign-in to the site shares the person's details: theirs to agree to.
        { Shown("AccountChooser", ("person.a@example.com", GoogleConfig, "SignUp")), "person.a@example.com", -1, nameof(FedCmWatch.Refusal.FirstSignIn) },
        { Shown("AccountChooser", ("person.a@example.com", GoogleConfig, "")), "person.a@example.com", -1, nameof(FedCmWatch.Refusal.FirstSignIn) },
        // Not a chooser at all.
        { Shown("ConfirmIdpLogin", ("person.a@example.com", GoogleConfig, "SignIn")), "person.a@example.com", -1, nameof(FedCmWatch.Refusal.NotAChooser) },
        { Shown("Error"), "person.a@example.com", -1, nameof(FedCmWatch.Refusal.NotAChooser) },
    };

    [Theory]
    [MemberData(nameof(Choices))]
    public void OnlyGooglesAccountThatWasAskedForAndUsedHereBeforeIsChosen(string json, string email, int index, string why)
        => Assert.Equal((index, Enum.Parse<FedCmWatch.Refusal>(why)), FedCmWatch.Choose(FedCmWatch.ParseDialog(json)!, email, Google));

    [Theory]
    [InlineData("AutoReauthn", 1, GoogleConfig, true)]
    [InlineData("AutoReauthn", 2, GoogleConfig, false)]
    [InlineData("AutoReauthn", 1, "https://idp.example/fedcm.json", false)]
    [InlineData("AccountChooser", 1, GoogleConfig, false)]
    public void TheBrowsersQuietReturnIsToldApart(string type, int count, string idp, bool quiet)
    {
        var accounts = Enumerable.Range(0, count).Select(i => ($"p{i}@example.com", idp, "SignIn")).ToArray();
        Assert.Equal(quiet, FedCmWatch.IsQuietReturn(FedCmWatch.ParseDialog(Shown(type, accounts))!, Google));
    }

    [Fact]
    public void TheChooserTakesTheCallOnlyWhenItIsTheSignInMeant()
    {
        var chooser = FedCmWatch.ParseDialog(Shown("AccountChooser", ("person.a@example.com", GoogleConfig, "SignIn"), ("person.b@example.com", "https://idp.example/fedcm.json", "SignIn")))!;
        Assert.True(FedCmWatch.Applies(chooser, "person.a@example.com", Google));
        // Nobody named or remembered, an address it does not offer, or another provider's:
        // the call is about the page under it (a password form under One Tap).
        Assert.False(FedCmWatch.Applies(chooser, null, Google));
        Assert.False(FedCmWatch.Applies(chooser, "nobody@example.com", Google));
        Assert.False(FedCmWatch.Applies(chooser, "person.b@example.com", Google));
        // The browser's quiet return is the sign-in under way whatever was asked.
        var quiet = FedCmWatch.ParseDialog(Shown("AutoReauthn", ("person.a@example.com", GoogleConfig, "SignIn")))!;
        Assert.True(FedCmWatch.Applies(quiet, null, Google));
        // With no password field and a chooser up, the chooser's answer is the one given.
        Assert.Matches(@"if \(state is \{ Fields: 0, Elsewhere: false \} && tab\.FedCm is \{ \} watching && watching\.Current is \{ \} up\)\s*return await FedCmLoginAsync\(tab, watching, up, asked\);", Code("App/AgentServer.cs"));
    }

    [Theory]
    [InlineData("https://www.example.com/login", "example.com")]
    [InlineData("https://shop.example.org/", "shop.example.org")]
    [InlineData("https://accounts.google.com/gsi/select", "")]
    [InlineData("https://mail.google.com/", "")]
    [InlineData("about:blank", "")]
    [InlineData("https://10.0.0.1/", "")]
    [InlineData(null, "")]
    public void TheSiteUnderTheChooserIsThePagesOwn(string? url, string site) => Assert.Equal(site, GoogleSignIn.SiteOfPage(url, Google));

    [Fact]
    public void TheChooserIsWatchedOnlyInAnAgentsWindow()
    {
        string tab = Code("Tabs/Tab.cs");
        var refresh = Regex.Match(tab, @"private async Task RefreshFedCmAsync\(\)[\s\S]*?\r?\n    \}\r?\n").Value;
        Assert.NotEmpty(refresh);
        // Stopped outside an agent's window, before anything is started.
        Assert.Matches(@"bool agentWindow = _owner\.IsAgentWindow\(\);\s*if \(!agentWindow\)\s*\{\s*if \(Interlocked\.Exchange\(ref _fedCm, null\) is \{ \} old\)\s*old\.Stop\(\);\s*return;", refresh);
        // A start that lands after the tab moved to the person's window is stopped, not kept.
        Assert.Matches(@"if \(Core != core \|\| !_owner\.IsAgentWindow\(\)\)\s*watch\.Stop\(\);", refresh);
        // The only place a watch starts.
        Assert.Single(Regex.Matches(tab, @"FedCmWatch\.StartAsync"));
        // On every view and every move between windows, and let go with the view.
        Assert.Matches(@"GoogleSignIn\.ChoiceScript\);\s*await RefreshFedCmAsync\(\);", tab);
        Assert.Matches(@"internal void RefreshDialogMode\(\)[\s\S]*?_ = RefreshFedCmAsync\(\);", tab);
        Assert.Contains("Interlocked.Exchange(ref _fedCm, null)?.Dispose();", tab);
    }

    [Fact]
    public void AnAgentChoosesInTheChooserOnlyWhatItMay()
    {
        string server = Code("App/AgentServer.cs");
        var fedCm = Regex.Match(server, @"async Task<\(int, string, byte\[\]\)> FedCmLoginAsync\([\s\S]*?\r?\n        \}\r?\n").Value;
        Assert.NotEmpty(fedCm);
        // Chosen only once the account passed Choose, never on a scripted page, and only in the
        // chooser that was read, still up, over the same page.
        Assert.Matches(@"FedCmWatch\.Choose\(dialog, email, GoogleSignIn\.Origin\);\s*if \(why != FedCmWatch\.Refusal\.None\)", fedCm);
        // All looked at on the UI thread with nothing awaited before the choice.
        Assert.Matches(@"look = await OnUiAsync\(async \(\) =>\s*\{\s*if \(tab\.FedCm != watch\)\s*return FedCmLook\.MovedOn;\s*if \(tab\.AgentScriptedThisDocument\)\s*return FedCmLook\.Scripted;\s*if \(watch\.Current\?\.Id != dialog\.Id \|\| url != tab\.CommittedSourceForOtherThreads\)\s*return FedCmLook\.MovedOn;\s*await watch\.SelectAsync\(dialog\.Id, index\);\s*return FedCmLook\.Chosen;", fedCm);
        Assert.Matches(@"if \(look == FedCmLook\.Scripted\)\s*return \(409", fedCm);
        Assert.Single(Regex.Matches(fedCm, @"SelectAsync"));
        // The browser's own quiet return is waited for, never answered or remembered.
        var quiet = Regex.Match(fedCm, @"if \(FedCmWatch\.IsQuietReturn\(dialog, GoogleSignIn\.Origin\)\)\s*\{[\s\S]*?\r?\n            \}").Value;
        Assert.NotEmpty(quiet);
        Assert.DoesNotContain("SelectAsync", quiet);
        Assert.DoesNotContain("Remember", quiet);
        // Nothing else in the chooser is pressed.
        Assert.DoesNotContain("clickDialogButton", Code("Tabs/FedCmWatch.cs"));
        Assert.DoesNotContain("dismissDialog", Code("Tabs/FedCmWatch.cs"));
    }

    [Fact]
    public void TheAccountsNeverReachTheSettingsAnAgentReads()
    {
        Assert.DoesNotContain(typeof(Settings).GetProperties(), p => p.Name.Contains("Google", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("GoogleAccounts", Code("App/SettingsPatch.cs"));
    }
}
