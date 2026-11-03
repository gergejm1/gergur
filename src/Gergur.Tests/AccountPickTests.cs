using System.Drawing;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Choosing one of several saved accounts, and filling a two-step sign-in's password page
/// (gergur_login's account). The engine's part, its list and what it fills, was measured in a
/// scratch profile and is the live test's job; these hold what Gergur decides around it.
/// </summary>
public sealed class AccountPickTests
{
    private static string Code(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gergur.slnx")))
            dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("Gergur.slnx not found above the test binaries");
        return Regex.Replace(File.ReadAllText(Path.Combine(root, "src", "Gergur", relative)), @"//[^\n]*", "");
    }

    private static readonly AgentServer.LoginState OnePage = new(1, Filled: true, User: "person-b", Button: true, UserField: true);
    private static readonly AgentServer.LoginState PasswordOnly = new(1, Filled: false, User: null, Button: true);

    [Fact]
    public void TheAccountIsTheOneAskedForOrTheOneATwoStepPageCarries()
    {
        Assert.Equal("person-a", AgentServer.WantedAccount(OnePage, "person-a"));
        Assert.Null(AgentServer.WantedAccount(OnePage, null));
        Assert.Equal("person-a", AgentServer.WantedAccount(PasswordOnly with { PageUser = "person-a" }, null));
        Assert.Equal("person-a", AgentServer.WantedAccount(PasswordOnly with { User = "person-a" }, null));   // read-only, shown
        // What the engine already filled is not overridden by a stray field on the page.
        Assert.Null(AgentServer.WantedAccount(PasswordOnly with { Filled = true, PageUser = "token" }, null));
        // An editable username field: the page carries nothing, the person or agent types it.
        Assert.Null(AgentServer.WantedAccount(OnePage with { Filled = false, User = null, PageUser = "x" }, null));
    }

    [Fact]
    public void WhatHasToHappenForTheAccountToBeInTheForm()
    {
        // Already there, or nothing to choose by: nothing.
        Assert.Equal(AgentServer.PickPlan.None, AgentServer.PlanPick(OnePage, "person-b"));
        Assert.Equal(AgentServer.PickPlan.None, AgentServer.PlanPick(OnePage, null));
        Assert.Equal(AgentServer.PickPlan.None, AgentServer.PlanPick(OnePage with { Filled = false, User = null }, null));
        // Another account wanted in a form with its own username field: chosen there.
        Assert.Equal(AgentServer.PickPlan.UserField, AgentServer.PlanPick(OnePage, "person-a"));
        Assert.Equal(AgentServer.PickPlan.UserField, AgentServer.PlanPick(OnePage with { Filled = false }, "person-b"));
        // A password-only page: through a field of Gergur's own, which the engine fills by itself.
        Assert.Equal(AgentServer.PickPlan.AddedField, AgentServer.PlanPick(PasswordOnly, null));
        Assert.Equal(AgentServer.PickPlan.AddedField, AgentServer.PlanPick(PasswordOnly, "person-a"));
        Assert.Equal(AgentServer.PickPlan.AddedField, AgentServer.PlanPick(PasswordOnly with { Filled = true }, "person-a"));
        // A read-only username that already matches, filled: nothing.
        Assert.Equal(AgentServer.PickPlan.None, AgentServer.PlanPick(PasswordOnly with { Filled = true, User = "person-a" }, "person-a"));
        // Never on a page of several password fields, or none.
        Assert.Equal(AgentServer.PickPlan.None, AgentServer.PlanPick(OnePage with { Fields = 2 }, "person-a"));
        Assert.Equal(AgentServer.PickPlan.None, AgentServer.PlanPick(PasswordOnly with { Fields = 0 }, null));
    }

    [Theory]
    [InlineData("person-a", "person-a")]
    [InlineData("  someone@example.com ", "someone@example.com")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("two\nlines", null)]
    public void AnAccountIsOnePrintableLine(string raw, string? clean)
        => Assert.Equal(clean, AgentServer.CleanAccount(raw));

    [Fact]
    public void AnAccountTooLongIsRefused()
    {
        Assert.NotNull(AgentServer.CleanAccount(new string('a', 200)));
        Assert.Null(AgentServer.CleanAccount(new string('a', 201)));
    }

    [Theory]
    [InlineData("""{"user":"person-a","filled":true}""", "person-a", true)]
    [InlineData("""{"user":null,"filled":false}""", null, false)]
    [InlineData("""{"filled":"yes"}""", null, false)]
    public void WhatWasChosenIsRead(string raw, string? user, bool filled)
        => Assert.Equal(new AgentServer.PickState(user, filled), AgentServer.ParsePickState(raw));

    [Theory]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("not json")]
    public void AnythingElseIsNoReading(string raw) => Assert.Null(AgentServer.ParsePickState(raw));

    [Fact]
    public void TheNewLookIsReadToo()
        => Assert.Equal(new AgentServer.LoginState(1, false, null, true, false, UserField: false, PageUser: "person-a"),
            AgentServer.ParseLoginState("""{"fields":1,"filled":false,"user":null,"button":true,"userField":false,"pageUser":"person-a"}"""));

    [Fact]
    public void ThePickNeverReadsThePasswordOut()
    {
        foreach (string js in new[] { AgentServer.PickStateJs("abc", "[x]"), AgentServer.LoginStateJs("abc") })
            foreach (Match m in Regex.Matches(js, @"\bp\.value\b[.\w]*"))
                Assert.Equal("p.value.length", m.Value);
        // The others only ever empty it.
        foreach (string js in new[] { AgentServer.ClearForPickJs("abc", "[x]"), AgentServer.EmptyPasswordJs("abc") })
            foreach (Match m in Regex.Matches(js, @"\bp\.value\b[^;]*"))
                Assert.Equal("p.value = ''", m.Value);
    }

    [Fact]
    public void AParkedWindowIsOffEveryScreen()
    {
        var screens = Rectangle.FromLTRB(-1920, -200, 3840, 1080);
        var window = new Rectangle(100, 100, 1200, 800);
        var spot = AccountPick.Parked(window, screens);
        Assert.True(spot.X + window.Width < screens.Left - 1000, "its right edge, and a list beside a field, stay left of every screen");
    }

    [Fact]
    public void ThePickIsWiredAsMeasured()
    {
        string server = Code("App/AgentServer.cs");
        string pick = Code("Tabs/AccountPick.cs");
        string form = Code("UI/MainForm.cs");
        var login = Regex.Match(server, @"async Task<\(int, string, byte\[\]\)> LoginAsync\(\)[\s\S]*?\n        switch \(\(method, path\)\)").Value;
        Assert.NotEmpty(login);
        // Not on a page an agent scripted, checked before and again right before the keyboard is borrowed.
        Assert.Matches(@"plan != PickPlan\.None\)\s*\{\s*if \(tab\.AgentScriptedThisDocument\)\s*return \(409", login);
        Assert.Matches(@"if \(tab\.AgentScriptedThisDocument\)\s*return \(null, false, \(409[^;]*;\s*var outcome = await OnUiAsync\(\(\) => AccountPick\.RunAsync", login);
        // Stopped rather than take the keyboard from another app, with nothing left filled.
        Assert.Matches(@"if \(outcome == AccountPick\.Outcome\.Interrupted\)\s*return \(null, false, \(503[^;]*PickInterrupted", login);
        // Bounded, so a pick whose window closed under it cannot hold the turn for good.
        Assert.Contains("AccountPick.RunAsync(window, tab, spot.X, spot.Y, wanted!, Chosen)).WaitAsync(PickBudget);", login);
        // An account that is not text is refused, never ignored into the default account
        // (the decision itself: AnAccountThatIsNotTextIsRefused).
        Assert.Matches(@"if \(AccountNotText\(body\) \|\| BodyString\(""account""\)[^)]*\)[^)]*\) is null\)\s*return \(400", login);
        // Which tab is in front is read on the UI thread.
        Assert.Matches(@"await OnUiAsync\(async \(\) =>\s*\{\s*if \(window\.Tabs is \{ \} tabs && tabs\.ActiveTab != tab\)", login);
        // Only in an agent's own window, one at a time, released whatever happens.
        Assert.Contains("if (!window.OpenedByAgent)", login);
        Assert.Contains("AccountPick.Turn.WaitAsync(", login);
        Assert.Matches(@"finally\s*\{\s*AccountPick\.Turn\.Release\(\);", login);
        // Another account's password is never left to be sent.
        Assert.Contains("if (wanted is not null && chosen != wanted)", login);
        // Emptied in one place, the outer finally, whatever ended the pick: refused, failed or thrown.
        Assert.Single(Regex.Matches(login, @"EmptyPasswordJs\(nonce\)"));
        Assert.Matches(@"finally\s*\{\s*if \(!keep\)\s*await QuietlyAsync\(EmptyPasswordJs\(nonce\)[^;]*;\s*if \(added\)\s*await QuietlyAsync\(RemovePickFieldJs\(nonce\)", login);
        // Kept only when the form holds the very account wanted.
        Assert.Equal(2, Regex.Matches(login, @"keep = true;").Count);
        // A pick that did not come out right answers no account, whatever the field holds as typed.
        Assert.Matches(@"if \(after is \{ Filled: true \} && after\.User == wanted\)\s*\{\s*keep = true;\s*return \(wanted, true, null\);\s*\}\s*return \(null, true, null\);", login);
        Assert.Matches(@"if \(first is \{ Filled: true \} && \(wanted is null \|\| first\.User == wanted\)\)\s*\{\s*keep = true;", login);
        // The tidying after (emptying, removing Gergur's own field) cannot throw over the answer.
        Assert.Matches(@"async Task QuietlyAsync\(string js, string what\)\s*\{\s*try\s*\{[^}]*\}\s*catch \(Exception ex\)", login);
        // The focus, the front window and the window's place are given back whatever happened.
        // Given back first, with nothing awaited before it, and the unguard cannot throw past it.
        // The unguard is always sent: a guard script that ran and then timed out is still taken off.
        Assert.Matches(@"finally\s*\{\s*GiveBack\(window\.Handle, foreground, active, focus\);\s*if \(park\)\s*SetWindowPos[^;]*;\s*try\s*\{\s*await tab\.ReadScriptAsync\(UnguardJs[^;]*;\s*\}\s*catch \(Exception", pick);
        Assert.DoesNotContain("guarded", pick);
        // The list is the one this window owns, not any popup of the engine nearby.
        Assert.Contains("bool owned = GetAncestor(h, RootOwner) == windowHandle;", pick);
        Assert.Contains("if (owned && Beside(rect, around))", pick);
        Assert.Contains("bool park = window.HiddenForAgent;", pick);
        // Checked right after the focus is given and after the click, before anything is typed.
        Assert.Matches(@"tab\.FocusView\(\);\s*if \(TookTheFront\(gergurInFront, IsOurs\(GetForegroundWindow\(\)\)\)\)\s*return Outcome\.Interrupted;\s*await TabInput\.ClickAsync", pick);
        Assert.Matches(@"await Task\.Delay\(150\);\s*if \(TookTheFront\(gergurInFront, IsOurs\(GetForegroundWindow\(\)\)\)\)\s*return Outcome\.Interrupted;\s*await TabInput\.InsertTextAsync", pick);
        // The list is looked for cloaked or not: the hidden window's own list is cloaked with it.
        Assert.DoesNotContain("Cloaked(", pick);
        // No key unless the list is there, and the page's own Enter held off while they go in.
        Assert.Matches(@"await tab\.ReadScriptAsync\(GuardJs[^;]*;\s*tab\.FocusView\(\);", pick);
        Assert.Matches(@"if \(!ListShown\(tab, window\.Handle\)\)\s*return Outcome\.NoList;\s*await TabInput\.PressAsync\(tab, TrustedInput\.ParseChord\(""ArrowDown""\)", pick);

        // The hidden window activating for it does not become the window requests go to.
        Assert.Matches(@"if \(!HiddenForAgent\)\s*_session\?\.NoteActive\(this\);", form);
    }

    [Theory]
    [InlineData(true, true, false)]     // the person is in Gergur: the borrow they agreed to
    [InlineData(false, false, false)]   // another app kept the front, or the person switched to a third
    [InlineData(false, true, true)]     // Gergur took it from another app
    public void TheKeyboardIsNeverTakenFromAnotherApp(bool gergurInFront, bool gergurInFrontNow, bool took)
        => Assert.Equal(took, AccountPick.TookTheFront(gergurInFront, gergurInFrontNow));

    [Theory]
    [InlineData("""{"account":"person-a"}""", false)]
    [InlineData("""{"account":null}""", false)]
    [InlineData("""{"submit":false}""", false)]
    [InlineData("""[]""", false)]
    [InlineData("""{"account":42}""", true)]
    [InlineData("""{"account":["person-a"]}""", true)]
    [InlineData("""{"account":{"name":"person-a"}}""", true)]
    [InlineData("""{"account":true}""", true)]
    public void AnAccountThatIsNotTextIsRefused(string json, bool refused)
    {
        using var body = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(refused, AgentServer.AccountNotText(body));
    }

    [Fact]
    public void NoBodyNamesNoAccount() => Assert.False(AgentServer.AccountNotText(null));

    [Theory]
    [InlineData(10, 20, 20, true)]    // the pick's window took the front: given back
    [InlineData(10, 30, 20, true)]    // something it owns (the list) holds it: given back
    [InlineData(10, 40, 40, false)]   // the person went to another app or window mid-pick: left there
    [InlineData(10, 10, 10, false)]   // nothing moved
    [InlineData(0, 20, 20, false)]    // there was no front window to give back
    public void TheFrontIsGivenBackOnlyFromThePicksOwnWindow(int was, int now, int nowRootOwner, bool giveBack)
        => Assert.Equal(giveBack, AccountPick.GiveFrontBack(was, now, nowRootOwner, pickWindow: 20));

    [Fact]
    public void EnterIsPressedAgainOnlyWhileTheListIsUp()
    {
        var pick = Code("Tabs/AccountPick.cs");
        Assert.Matches(@"for \(int attempt = 0; attempt < EnterAttempts; attempt\+\+\)\s*\{\s*if \(!ListShown\(tab, window\.Handle\)\)\s*break;\s*await TabInput\.PressAsync\(tab, ListEnter\);", pick);
        Assert.InRange(AccountPick.EnterAttempts, 1, 3);
    }

    [Fact]
    public void TheListIsTheOneBesideThisWindow()
    {
        var parked = new Rectangle(-6000, 0, 1000, 760);
        Assert.True(AccountPick.Beside(new Rectangle(-5886, 97, 342, 213), parked));        // as measured, under the field
        Assert.False(AccountPick.Beside(new Rectangle(200, 300, 342, 213), parked));        // a list in the person's window
        Assert.False(AccountPick.Beside(new Rectangle(-5886, 97, 0, 0), parked));           // an empty one
    }

    [Fact]
    public void TheGuardHoldsOffTheSubmitNotTheKeyAndComesOff()
    {
        Assert.Contains("e.preventDefault(); e.stopImmediatePropagation();", AccountPick.GuardJs);
        Assert.Contains("window.addEventListener('submit', stop, true)", AccountPick.GuardJs);
        Assert.Contains("window.removeEventListener('submit', stop, true)", AccountPick.UnguardJs);
        // The list takes its choice through the page's keydown: holding the key off held the choice off.
        foreach (string type in new[] { "keydown", "keypress", "keyup" })
            Assert.DoesNotContain($"'{type}'", AccountPick.GuardJs);
    }

    [Fact]
    public void TheListsEnterGoesAsAKeyThatTypesNothing()
    {
        var events = TrustedInput.KeyEvents(AccountPick.ListEnter);
        Assert.Equal("rawKeyDown", events[0].GetType().GetProperty("type")!.GetValue(events[0]));
        Assert.Null(events[0].GetType().GetProperty("text"));
        Assert.Equal(13, events[0].GetType().GetProperty("windowsVirtualKeyCode")!.GetValue(events[0]));
        Assert.Contains("TabInput.PressAsync(tab, ListEnter)", Code("Tabs/AccountPick.cs"));
    }

    [Fact]
    public void TheToolTakesAnAccount()
    {
        string server = Code("App/AgentServer.cs");
        Assert.Matches(@"Tool\(""gergur_login"",[\s\S]*?account = Text\(", server);
        Assert.Contains("BodyString(\"account\")", server);
    }

    [Fact]
    public void ATestInstanceKeepsToItsOwnFolder()
    {
        string local = @"C:\Users\someone\AppData\Local";
        Assert.Equal(Path.Combine(local, "Gergur"), Settings.DataDirFrom(null, local));
        Assert.Equal(Path.Combine(local, "Gergur"), Settings.DataDirFrom("  ", local));
        Assert.Equal(@"C:\scratch\gergur-test", Settings.DataDirFrom(@"C:\scratch\gergur-test", local));
        // The person's browser keeps its names; a test instance gets its own, the same for the same folder.
        Assert.Equal("", Settings.SuffixFor(null, Path.Combine(local, "Gergur")));
        string a = Settings.SuffixFor(@"C:\scratch\a", @"C:\scratch\a");
        Assert.StartsWith(".", a);
        Assert.Equal(a, Settings.SuffixFor(@"C:\Scratch\A", @"C:\Scratch\A"));
        Assert.NotEqual(a, Settings.SuffixFor(@"C:\scratch\b", @"C:\scratch\b"));
        Assert.Contains(@"""Local\Gergur.SingleInstance"" + Settings.InstanceSuffix", Code("Program.cs"));
        Assert.Contains("Environment.UserName + Settings.InstanceSuffix", Code("App/SingleInstance.cs"));
    }
}
