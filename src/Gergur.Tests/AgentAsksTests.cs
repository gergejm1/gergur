using System.Text.RegularExpressions;
using Gergur.App;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>gergur_ask_person: questions an agent waits on the person for, and the bar they are answered in.</summary>
public sealed class AgentAsksTests
{
    private static readonly AgentCaller Claude = new("mcp:claude", "Claude", "MCP");
    private static readonly AgentCaller Other = new("mcp:other", "Other", "MCP");
    private static readonly DateTime Now = new(2026, 11, 3, 2, 0, 0, DateTimeKind.Utc);

    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { work(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }

    [Fact]
    public void AQuestionIsKeptAsOneShortPrintableLine()
    {
        Assert.Null(AgentAsks.Clean(null));
        Assert.Null(AgentAsks.Clean("  \r\n\t "));
        Assert.Null(AgentAsks.Clean(((char)7).ToString()));
        Assert.Equal("Type the code from your phone", AgentAsks.Clean($"  Type the\r\ncode {(char)0x2028} from\t your phone "));
        string cut = AgentAsks.Clean(new string('a', 1000))!;
        Assert.Equal(AgentAsks.MaxQuestionLength, cut.Length);
        Assert.EndsWith("…", cut);
        // A right-to-left override cannot turn the question around in the bar.
        Assert.Equal("abc", AgentAsks.Clean("a" + (char)0x202E + "bc"));
        // And the cut never leaves half a character.
        string emoji = char.ConvertFromUtf32(0x1F600);
        string longer = new string('a', AgentAsks.MaxQuestionLength - 2) + emoji + "tail";
        Assert.False(char.IsHighSurrogate(AgentAsks.Clean(longer)![^2]));
    }

    [Fact]
    public void AskingTheSameThingAgainGivesTheSameQuestion()
    {
        var asks = new AgentAsks();
        var first = asks.Ask(Claude, "Solve the captcha", Now)!;
        var again = asks.Ask(Claude, "Solve the captcha", Now)!;
        Assert.Equal(first.Id, again.Id);
        Assert.Single(asks.Pending(Now));
        // Another agent asking the same words is a question of its own.
        Assert.NotEqual(first.Id, asks.Ask(Other, "Solve the captcha", Now)!.Id);
    }

    [Fact]
    public void NoMoreThanTenWaitAtOnce()
    {
        var asks = new AgentAsks();
        for (int i = 0; i < AgentAsks.MaxPending; i++)
            Assert.NotNull(asks.Ask(Claude, $"q{i}", Now));
        Assert.Null(asks.Ask(Claude, "one too many", Now));
        Assert.Equal("q0", asks.Pending(Now)[0].Question);   // the oldest first
    }

    [Fact]
    public async Task AnAnswerIsHandedToTheAgentThatAskedOnce()
    {
        var asks = new AgentAsks();
        var q = asks.Ask(Claude, "Approve the login on your phone", DateTime.UtcNow)!;
        Assert.True(asks.Answer(q.Id, done: true, "  approved  ", DateTime.UtcNow));
        Assert.Empty(asks.Pending(Now));
        Assert.False(asks.Answer(q.Id, done: false, "twice", DateTime.UtcNow));   // answered already

        // Not the other agent's to read.
        Assert.False((await asks.WaitAsync(q.Id, Other.Key, TimeSpan.Zero)).Known);

        var (known, reply) = await asks.WaitAsync(q.Id, Claude.Key, TimeSpan.Zero);
        Assert.True(known);
        Assert.Equal(new AgentReply(true, "approved"), reply);
        // Handed over once.
        Assert.False((await asks.WaitAsync(q.Id, Claude.Key, TimeSpan.Zero)).Known);
    }

    [Fact]
    public async Task AWaitThatRunsOutSaysNotYetAndAnotherWaitStillGetsTheAnswer()
    {
        var asks = new AgentAsks();
        var q = asks.Ask(Claude, "Pick a seat", DateTime.UtcNow)!;
        var (known, reply) = await asks.WaitAsync(q.Id, Claude.Key, TimeSpan.FromMilliseconds(30));
        Assert.True(known);
        Assert.Null(reply);

        var waiting = asks.WaitAsync(q.Id, Claude.Key, TimeSpan.FromSeconds(10));
        asks.Answer(q.Id, done: false, "none left by the window", DateTime.UtcNow);
        var (_, answered) = await waiting;
        Assert.Equal(new AgentReply(false, "none left by the window"), answered);
    }

    [Fact]
    public void AnAnswerIsCutAndOldQuestionsAreForgotten()
    {
        var asks = new AgentAsks();
        var q = asks.Ask(Claude, "Anything", Now)!;
        Assert.True(asks.Answer(q.Id, true, new string('x', 5000), Now));
        asks.Ask(Claude, "Old", Now);
        Assert.Single(asks.Pending(Now));
        Assert.Empty(asks.Pending(Now + AgentAsks.KeptFor + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AskingAndAnsweringAreAnnounced()
    {
        var asks = new AgentAsks();
        int changes = 0;
        asks.Changed += () => changes++;
        var q = asks.Ask(Claude, "Hi", Now)!;
        asks.Ask(Claude, "Hi", Now);          // the same one: nothing new to show
        asks.Answer(q.Id, true, "", Now);
        Assert.Equal(2, changes);
    }

    // ---------------------------------------------------------------- the bar

    [Fact]
    public void TheBarShowsTheOldestQuestionAndKeepsAReplyBeingTyped()
    {
        OnSta(() =>
        {
            using var bar = new AskBar { Width = 900 };
            var first = new AgentQuestion("ask-1", Claude.Key, "Claude", "Type the 2FA code", Now);
            var second = new AgentQuestion("ask-2", Other.Key, "Other", "Pick a plan", Now);
            bar.SetQuestions([first, second], canShow: true);
            Assert.Equal("Claude asks: Type the 2FA code", bar.Caption);
            Assert.Contains("Claude asks", bar.AccessibleName);

            bar.ReplyBoxForTest.Text = "4821";
            bar.SetQuestions([first, second], canShow: true);   // the same question again
            Assert.Equal("4821", bar.ReplyBoxForTest.Text);

            bar.SetQuestions([second], canShow: false);         // the next one
            Assert.Equal("", bar.ReplyBoxForTest.Text);
            Assert.False(bar.ShowButtonForTest.Visible);
        });
    }

    [Fact]
    public void EnterInTheReplyBoxAnswersDone()
    {
        OnSta(() =>
        {
            using var form = new Form();
            var bar = new AskBar { Dock = DockStyle.Top };
            form.Controls.Add(bar);
            form.CreateControl();
            bar.SetQuestions([new AgentQuestion("ask-7", Claude.Key, "Claude", "Code?", Now)], canShow: false);
            (string Id, bool Done, string Text)? got = null;
            bar.Answered += (id, done, text) => got = (id, done, text);
            bar.ReplyBoxForTest.Text = "123456";
            var key = typeof(TextBox).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            key.Invoke(bar.ReplyBoxForTest, [new KeyEventArgs(Keys.Enter)]);
            Assert.Equal(("ask-7", true, "123456"), got);
            Assert.Equal("", bar.ReplyBoxForTest.Text);
        });
    }

    [Fact]
    public void OnlyThePersonsWindowsShowTheBar()
    {
        string form = Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource("src", "Gergur", "UI", "MainForm.cs")), @"//[^\n]*", "");
        Assert.Contains("IReadOnlyList<AgentQuestion> waiting = _openedByAgent ? [] : session.Asks.Pending(DateTime.UtcNow);", form);
        // And it is unhooked with the window, or a closed window would be asked to update.
        Assert.Contains("_agentsHookedTo.Asks.Changed -= OnAsksChanged;", form);
    }
}
