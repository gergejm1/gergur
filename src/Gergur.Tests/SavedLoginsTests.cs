using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Saved passwords and agents: the engine saves and fills them, agents sign in with
/// /login and never read one. What can be pinned without an engine; the page scripts and
/// the engine's own behaviour are the live harness in the scratchpad.
/// </summary>
public sealed class SavedLoginsTests
{
    private static string Source(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Gergur", relative));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gergur.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Gergur.slnx not found above the test binaries");
    }

    private static string Code(string relative) => Regex.Replace(Source(relative), @"//[^\n]*", "");

    // ---------------------------------------------------------------- who is asked to save

    [Theory]
    [InlineData(true, false, true)]    // the person's window asks
    [InlineData(true, true, false)]    // an agent's never does
    [InlineData(false, false, false)]  // nor anything with saving off
    [InlineData(false, true, false)]
    public void OnlyThePersonsWindowsOfferToSave(bool setting, bool agentWindow, bool saves)
        => Assert.Equal(saves, BrowserEnvironment.SavesPasswords(new Settings { SavePasswords = setting }, agentWindow));

    [Fact]
    public void EveryPlaceAViewGetsItsSettingsKnowsWhoseWindowItIs()
    {
        string env = Code("App/BrowserEnvironment.cs");
        string tab = Code("Tabs/Tab.cs");
        Assert.Contains("IsPasswordAutosaveEnabled = SavesPasswords(settings, agentWindow);", env);
        Assert.DoesNotContain("IsPasswordAutosaveEnabled = settings.SavePasswords", env);
        Assert.Contains("ApplyViewSettings(webView.CoreWebView2, Settings, agentWindow);", env);
        Assert.Contains("CreateWebViewAsync(_owner.Host, visible: false, agentWindow: _owner.IsAgentWindow())", tab);
        Assert.Contains("ApplyViewSettings(core, settings, _owner.IsAgentWindow());", tab);
        // Moved between windows, or the window claimed: saving follows, like the dialogs.
        Assert.Matches(@"void RefreshDialogMode\(\)[\s\S]{0,500}IsPasswordAutosaveEnabled = BrowserEnvironment\.SavesPasswords\(_owner\.Env\.Settings, agentWindow\);", tab);
    }

    // ---------------------------------------------------------------- scripted pages

    [Theory]
    [InlineData(0, 0, 100, false, false)]     // never scripted
    [InlineData(0, 50, 100, false, false)]    // scripted on the page before
    [InlineData(0, 100, 100, false, true)]    // the same mark counts
    [InlineData(0, 150, 100, false, true)]
    [InlineData(0, 150, 0, false, true)]      // scripted, and no page start seen yet
    [InlineData(1, 50, 100, false, true)]     // a script still running, whatever page it began on
    [InlineData(0, 50, 100, true, true)]      // back or forward since a script
    public void AScriptSinceThePageBeganOrStillRunningCounts(int running, long script, long document, bool history, bool scripted)
        => Assert.Equal(scripted, Tab.ScriptedThisDocument(running, script, document, history));

    [Fact]
    public void ANewDocumentStartsThePageAfreshAndOnlyAFreshLoadForgetsHistory()
    {
        string tab = Code("Tabs/Tab.cs");
        Assert.Contains("core.ContentLoading += OnContentLoading;", tab);
        Assert.Matches(@"void OnContentLoading\([^)]*\)\s*\{\s*Interlocked\.Exchange\(ref _documentStarted, Interlocked\.Increment\(ref s_marks\)\);", tab);
        Assert.Matches(@"if \(_lastNavigationStarted is \{ History: false \} started && started\.Id == e\.NavigationId\)\s*Volatile\.Write\(ref _historyAfterScript, 0\);", tab);
        Assert.Matches(@"if \(history && Interlocked\.Read\(ref _agentScript\) != 0\)\s*Volatile\.Write\(ref _historyAfterScript, 1\);", tab);
        // Marks, not clock readings: a clock can step back and ties within its resolution.
        Assert.DoesNotContain("_documentStartedTicks", tab);
    }

    [Fact]
    public void ScriptsAreCountedInAndOut()
    {
        var tab = new Tab(null!);
        Assert.False(tab.AgentScriptedThisDocument);
        tab.AgentScriptStarting();
        Assert.True(tab.AgentScriptedThisDocument);
        tab.AgentScriptOver();
        Assert.True(tab.AgentScriptedThisDocument);   // no new page since
    }

    // ---------------------------------------------------------------- run_javascript and screenshots

    [Fact]
    public void AScriptIsNotRunWhereAPasswordIsFilledInAndItsAnswerIsKeptBackIfOneArrived()
    {
        string server = Code("App/AgentServer.cs");
        var eval = Regex.Match(server, @"case \(""POST"", ""/eval""\):[\s\S]*?\n            case ").Value;
        Assert.NotEmpty(eval);
        int check = eval.IndexOf("ReadScriptAsync(PasswordStateJs", StringComparison.Ordinal);
        int refuse = eval.IndexOf("Json(new { error = PasswordOnPage })", StringComparison.Ordinal);
        int starting = eval.IndexOf("tab.AgentScriptStarting();", StringComparison.Ordinal);
        int run = eval.IndexOf("var answer = await EvaluateAsync();", StringComparison.Ordinal);
        int after = eval.IndexOf("ReadScriptAsync(PasswordStateJs", run, StringComparison.Ordinal);
        Assert.True(check > 0 && refuse > check, "the password check comes first and refuses");
        Assert.True(starting > refuse && run > starting, "counted in before it runs");
        Assert.True(after > run, "looked at again once it has run");
        Assert.Contains("return HoldsNoPassword(passwordAfter) ? answer : (409, \"application/json\", Json(new { error = PasswordDuringScript }))", eval);
        Assert.Matches(@"var answer = await EvaluateAsync\(\);\s*if \(answer\.Item1 != 200\)\s*return answer;", eval);
        Assert.Matches(@"finally\s*\{\s*tab\.AgentScriptOver\(\);\s*\}", eval);
        // The engine hands back a script's value as JSON, so a string would come back quoted and
        // never equal "0": every script was refused that way in the first live run. A number.
        Assert.Contains("if (!HoldsNoPassword(passwordBefore))", eval);
        Assert.Matches(@"return state;\s*\} catch \(e\) \{\s*return 2;", AgentServer.PasswordStateJs);
        Assert.DoesNotContain("String(", AgentServer.PasswordStateJs);
        // Every way of running the agent's script is inside EvaluateAsync.
        int local = eval.IndexOf("async Task<(int, string, byte[])> EvaluateAsync()", StringComparison.Ordinal);
        Assert.True(local > 0 && eval.IndexOf("ReadScriptAsync(js,", StringComparison.Ordinal) > local
            && eval.IndexOf("ExecuteScriptAwaitingAsync(js,", StringComparison.Ordinal) > local);
    }

    [Theory]
    [InlineData("0", true, false)]
    [InlineData("1", false, false)]
    [InlineData("2", false, true)]
    [InlineData("null", false, false)]       // the page could not be read: not taken for none
    [InlineData("\"0\"", false, false)]      // a string comes back quoted: the first live run's bug
    [InlineData("\"2\"", false, false)]
    [InlineData("", false, false)]
    public void ThePasswordAnswerIsReadAsTheEngineHandsItBack(string raw, bool none, bool plainText)
    {
        Assert.Equal(none, AgentServer.HoldsNoPassword(raw));
        Assert.Equal(plainText, AgentServer.ShowsPasswordAsText(raw));
    }

    [Fact]
    public void APasswordInPlainTextIsNotPhotographed()
    {
        string server = Code("App/AgentServer.cs");
        var shot = Regex.Match(server, @"case \(""GET"", ""/screenshot""\):[\s\S]*?\n            case ").Value;
        Assert.NotEmpty(shot);
        int ready = shot.IndexOf("WaitForPageAsync", StringComparison.Ordinal);
        int check = shot.IndexOf("ReadScriptAsync(PasswordStateJs", StringComparison.Ordinal);
        int refuse = shot.IndexOf("if (ShowsPasswordAsText(shownPassword))", StringComparison.Ordinal);
        int chrome = shot.IndexOf("if (wantsChrome)", check, StringComparison.Ordinal);
        int capture = shot.IndexOf("CaptureScreenshotAsync", check, StringComparison.Ordinal);
        Assert.True(ready > 0 && check > ready && refuse > check, "checked once the page is there");
        Assert.True((chrome < 0 || chrome > refuse) && capture > refuse, "and before either kind of picture");
    }

    [Fact]
    public async Task ThePageScriptsDecideRightOnAStandInPage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gergur-tests", "login-scripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string state = Path.Combine(dir, "password-state.js");
            string login = Path.Combine(dir, "login-state.js");
            File.WriteAllText(state, AgentServer.PasswordStateJs);
            File.WriteAllText(login, AgentServer.LoginStateJs("n0nce"));
            var start = new ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Path.Combine(RepoRoot(), "src", "Gergur.Tests", "LoginScripts.harness.js"));
            start.ArgumentList.Add(state);
            start.ArgumentList.Add(login);
            const string pickField = "[data-gergur-login=\"pick-n0nce\"]";
            foreach (var (name, script) in new[]
            {
                ("add-pick.js", AgentServer.AddPickFieldJs("n0nce")),
                ("pick-state.js", AgentServer.PickStateJs("n0nce", pickField)),
                ("clear-for-pick.js", AgentServer.ClearForPickJs("n0nce", pickField)),
                ("empty-password.js", AgentServer.EmptyPasswordJs("n0nce")),
                ("remove-pick.js", AgentServer.RemovePickFieldJs("n0nce")),
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
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- /login

    [Fact]
    public void TheLoginLookNeverReadsThePasswordOut()
    {
        string js = AgentServer.LoginStateJs("0123abcd");
        // The only use of the password field's value is how long it is.
        foreach (Match m in Regex.Matches(js, @"\bp\.value\b[.\w]*"))
            Assert.Equal("p.value.length", m.Value);
        Assert.Contains("'pass-0123abcd'", js);
        Assert.Contains("'go-0123abcd'", js);
        Assert.DoesNotContain(".value", AgentServer.StillAskingJs("0123abcd"));
    }

    private static string Answer(object? answer) => JsonSerializer.Serialize(answer);

    [Fact]
    public void WhereSigningInStops()
    {
        var filled = new AgentServer.LoginState(1, true, "someone@example.com", true);
        // Nothing to read, or nothing filled: says so, sends nothing.
        Assert.Equal((200, Answer(new { ok = false, filled = false, error = AgentServer.NoPasswordField })),
            Map(AgentServer.LoginStop(null, submit: true, scripted: false)));
        Assert.Equal((200, Answer(new { ok = false, filled = false, error = AgentServer.NoPasswordField })),
            Map(AgentServer.LoginStop(new(0, false, null, false), submit: true, scripted: false)));
        Assert.Equal((200, Answer(new { ok = false, filled = false, error = AgentServer.PasswordElsewhere })),
            Map(AgentServer.LoginStop(new(0, false, null, false, Elsewhere: true), submit: true, scripted: false)));
        Assert.Equal((200, Answer(new { ok = false, filled = false, error = AgentServer.NoSavedLogin })),
            Map(AgentServer.LoginStop(filled with { Filled = false }, submit: true, scripted: false)));
        // Only looking works anywhere, a scripted page included.
        Assert.Equal((200, Answer(new { ok = true, filled = true, user = "someone@example.com", submitted = false })),
            Map(AgentServer.LoginStop(filled, submit: false, scripted: true)));
        // Sending does not, nor from a page with two password fields.
        Assert.Equal((409, Answer(new { error = AgentServer.ScriptedLoginPage })),
            Map(AgentServer.LoginStop(filled, submit: true, scripted: true)));
        Assert.Equal((409, Answer(new { error = AgentServer.SeveralPasswordFields })),
            Map(AgentServer.LoginStop(filled with { Fields = 2 }, submit: true, scripted: false)));
        // Filled, one field, untouched page: go on and sign in.
        Assert.Null(AgentServer.LoginStop(filled, submit: true, scripted: false));

        static (int, string)? Map((int Status, object Answer)? stop) => stop is { } s ? (s.Status, Answer(s.Answer)) : null;
    }

    [Fact]
    public void SigningInLooksAtTheScriptedPageAgainRightBeforeActing()
    {
        string server = Code("App/AgentServer.cs");
        var login = Regex.Match(server, @"async Task<\(int, string, byte\[\]\)> LoginAsync\(\)[\s\S]*?\n        switch \(\(method, path\)\)").Value;
        Assert.NotEmpty(login);
        Assert.Contains("Acting(tab);", login);
        Assert.Contains("OwnPageRefused(tab)", login);
        Assert.Contains("LoginStop(state, submit: BoolFrom(\"submit\") != false, tab.AgentScriptedThisDocument)", login);
        // Again directly before the button click and before Enter.
        Assert.Matches(@"if \(tab\.AgentScriptedThisDocument\)\s*return \(409[^;]*;\s*try\s*\{\s*await OnUiAsync\(\(\) => TabInput\.ClickAsync", login);
        Assert.Matches(@"if \(tab\.AgentScriptedThisDocument\)\s*return \(409[^;]*;\s*await OnUiAsync\(async \(\) => \{ await TabInput\.PressAsync\(tab, TrustedInput\.ParseChord\(""Enter""\)", login);
        // A click that may have landed is never followed by Enter.
        Assert.Matches(@"when \(failed\.MayHaveLanded\)\s*\{[^}]*via = ""button"";", login);
        // Nothing in the answers is a field's value.
        Assert.DoesNotContain(".value", login);
    }

    [Theory]
    [InlineData("""{"fields":0}""", 0, false, null, false)]
    [InlineData("""{"fields":1,"filled":true,"user":"someone@example.com","button":true}""", 1, true, "someone@example.com", true)]
    [InlineData("""{"fields":2,"filled":false,"user":null,"button":false}""", 2, false, null, false)]
    [InlineData("""{"fields":0,"elsewhere":true}""", 0, false, null, false, true)]
    public void TheLoginLookIsRead(string raw, int fields, bool filled, string? user, bool button, bool elsewhere = false)
        => Assert.Equal(new AgentServer.LoginState(fields, filled, user, button, elsewhere), AgentServer.ParseLoginState(raw));

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("""{"fields":"1"}""")]
    [InlineData("""{"fields":1.5}""")]
    [InlineData("not json")]
    public void AnythingElseIsNoAnswer(string raw) => Assert.Null(AgentServer.ParseLoginState(raw));

    [Fact]
    public void TheToolIsListedRoutedAndLogged()
    {
        Assert.Contains(AgentServer.McpTools(), t => t.GetType().GetProperty("name")?.GetValue(t) as string == "gergur_login");
        string server = Code("App/AgentServer.cs");
        Assert.Contains(@"""gergur_login"" => (""POST"", ""/login""),", server);
        Assert.Contains(@"case (""POST"", ""/login""):", server);
        Assert.Contains("gergur_login", AgentServer.McpInstructions);
        Assert.Equal("Tried a saved login in t3", AgentActivity.Describe("POST", "/login", "t3", null));
    }
}
