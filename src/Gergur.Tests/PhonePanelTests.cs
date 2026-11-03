using System.Text.RegularExpressions;
using Gergur.Data;
using Gergur.UI;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The phone panel: what it offers on and off, sending a message and files, the rule
/// against running a program that came from the phone, and the dot on its button.
/// Nothing here touches the clipboard: it is the person's.
/// </summary>
public sealed class PhonePanelTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 2, 22, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gergur-tests", "phone-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static string Code(params string[] parts)
        => Regex.Replace(File.ReadAllText(OwnPagesHardeningTests.FindSource(parts)), @"//[^\n]*", "");

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

    /// <summary>Waits on the STA thread, running its message loop: a control made on it put a WinForms context there, and the await comes back through it.</summary>
    private static void Pump(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "the send never finished");
        task.GetAwaiter().GetResult();
    }

    private sealed class Calls
    {
        public bool Running = true;
        public bool CanSend = true;
        public readonly List<string> Log = new();
    }

    private static PhoneActions Actions(Calls calls) => new(
        IsRunning: () => calls.Running,
        CanSendPage: () => calls.CanSend,
        SendPage: () => calls.Log.Add("send page"),
        Toggle: () => calls.Log.Add("toggle"),
        Pair: () => calls.Log.Add("pair"),
        ShowAll: () => calls.Log.Add("show all"),
        OpenUrl: url => calls.Log.Add("open " + url),
        Say: text => calls.Log.Add("say " + text));

    private static DropItem Item(string kind, string text, string from, long size = 0, int minutesAgo = 5)
        => new("id", kind, text, kind == "file" ? "id.pdf" : "", size, from, Now.AddMinutes(-minutesAgo));

    [Theory]
    [InlineData("link", "https://www.example.com/a/b/", "www.example.com/a/b")]
    [InlineData("link", "http://example.com", "example.com")]
    [InlineData("text", "two\nlines ", "two lines")]
    [InlineData("file", "report.pdf", "report.pdf")]
    public void AnItemIsOneLine(string kind, string text, string expected)
        => Assert.Equal(expected, PhoneFlyout.Line(Item(kind, text, "pc")));

    [Fact]
    public void ItsSecondLineSaysWhereFromHowBigAndWhen()
    {
        Assert.Equal("From your phone \u00B7 2 MB \u00B7 5 min ago", PhoneFlyout.Detail(Item("file", "a.pdf", "phone", 2 * 1024 * 1024), Now));
        Assert.Equal("From this PC \u00B7 Just now", PhoneFlyout.Detail(Item("text", "hi", "pc", minutesAgo: 0), Now));
    }

    [Fact]
    public void FoldersMissingFilesAndHugeFilesAreSkippedAndSaidSo()
    {
        Directory.CreateDirectory(_root);
        var sizes = new Dictionary<string, long?> { ["a.txt"] = 10, ["huge.mkv"] = PhoneFlyout.MaxSendBytes + 1, ["gone.txt"] = null };
        var (send, skipped) = PhoneFlyout.Sendable(["a.txt", _root, "huge.mkv", "gone.txt"], p => sizes[p]);
        Assert.Equal(["a.txt"], send);
        Assert.Equal("Skipped a folder and a file over 1 GB and a file that could not be read.", skipped);

        var (all, none) = PhoneFlyout.Sendable(["a.txt"], p => sizes[p]);
        Assert.Single(all);
        Assert.Null(none);
    }

    [Fact]
    public void OffItOffersToTurnOnAndNothingElseButTheWindow()
    {
        OnSta(() =>
        {
            var calls = new Calls { Running = false };
            using var panel = new PhoneFlyout(new DropStore(_root), Actions(calls));
            panel.Relayout(keepPlace: false);
            Assert.Equal(["Turn on phone drop", "Open drop window"], panel.Spots.Select(s => s.Spoken));
            Assert.False(panel.ComposeShown);
            panel.RunSpot(panel.Spots[0]);
            Assert.Equal(["toggle"], calls.Log);
        });
    }

    [Fact]
    public void OnItOffersTheThreeActionsTheMessageBoxRecentItemsAndTurningOff()
    {
        OnSta(() =>
        {
            var drop = new DropStore(_root);
            drop.AddText("https://example.com/x", from: "phone");
            drop.AddText("hello there", from: "pc");
            var calls = new Calls { CanSend = false };
            using var panel = new PhoneFlyout(drop, Actions(calls));
            panel.Relayout(keepPlace: false);

            Assert.Equal(new[]
            {
                "Send this page", "Send a file", "Pair a phone", "Send message",
                "Copy hello there", "Copy text hello there",
                "Open example.com/x", "Copy example.com/x",
                "Open drop window", "Turn off",
            }, panel.Spots.Select(s => s.Spoken));
            // The new tab page cannot be sent: the tile shows, greyed.
            Assert.False(panel.Spots[0].Enabled);
            Assert.True(panel.ComposeShown);

            panel.RunSpot(panel.Spots.Single(s => s.Spoken == "Open example.com/x"));
            Assert.Contains("open https://example.com/x", calls.Log);
        });
    }

    [Fact]
    public void OnlyTheLatestFewAreListed()
    {
        OnSta(() =>
        {
            var drop = new DropStore(_root);
            for (int i = 0; i < PhoneFlyout.MaxRows + 3; i++)
                drop.AddText($"note {i}", from: "pc");
            using var panel = new PhoneFlyout(drop, Actions(new Calls()));
            panel.Relayout(keepPlace: false);
            Assert.Equal(PhoneFlyout.MaxRows, panel.Spots.Count(s => s.Spoken.StartsWith("Copy note")));
            Assert.Contains(panel.Spots, s => s.Spoken == $"Open drop window ({PhoneFlyout.MaxRows + 3})");
        });
    }

    [Fact]
    public void EnterSendsWhatWasTypedAndEmptySendsNothing()
    {
        OnSta(() =>
        {
            var drop = new DropStore(_root);
            var calls = new Calls();
            using var panel = new PhoneFlyout(drop, Actions(calls));
            panel.Relayout(keepPlace: false);

            panel.SendTyped();
            Assert.Empty(drop.Items);

            panel.Box.Text = "  pick up milk  ";
            panel.SendTyped();
            var item = Assert.Single(drop.Items);
            Assert.Equal(("text", "pick up milk", "pc"), (item.Kind, item.Text, item.From));
            Assert.Equal("", panel.Box.Text);
            Assert.Equal("Sent.", panel.Note);

            // Turned off since the panel opened: refused, and kept in the box.
            calls.Running = false;
            panel.Box.Text = "later";
            panel.SendTyped();
            Assert.Single(drop.Items);
            Assert.Equal("later", panel.Box.Text);
        });
    }

    [Fact]
    public void AFileIsCopiedIntoTheDropAndTheOriginalLeftWhereItWas()
    {
        OnSta(() =>
        {
            var drop = new DropStore(Path.Combine(_root, "drop"));
            string source = Path.Combine(_root, "notes.txt");
            File.WriteAllText(source, "hello");
            using var panel = new PhoneFlyout(drop, Actions(new Calls()));
            Pump(panel.SendFilesAsync([source, _root]));

            Assert.True(File.Exists(source));
            var item = Assert.Single(drop.Items);
            Assert.Equal(("file", "notes.txt", "pc", 5L), (item.Kind, item.Text, item.From, item.Size));
            Assert.Equal("hello", File.ReadAllText(drop.PathFor(item)!));
            Assert.Equal("Sent notes.txt. Skipped a folder.", panel.Note);
        });
    }

    [Fact]
    public void NothingIsSentWhileTheDropIsOff()
    {
        OnSta(() =>
        {
            var drop = new DropStore(Path.Combine(_root, "drop"));
            string source = Path.Combine(_root, "notes.txt");
            File.WriteAllText(source, "hello");
            using var panel = new PhoneFlyout(drop, Actions(new Calls { Running = false }));
            Pump(panel.SendFilesAsync([source]));
            Assert.Empty(drop.Items);
            Assert.Equal("Turn on the phone drop first.", panel.Note);
        });
    }

    [Fact]
    public void AProgramFromThePhoneIsNeverRun()
    {
        OnSta(() =>
        {
            var drop = new DropStore(_root);
            using (var bytes = new MemoryStream([0x4d, 0x5a]))
                drop.AddFile("setup.exe", bytes, from: "phone");
            var calls = new Calls();
            using var panel = new PhoneFlyout(drop, Actions(calls));
            panel.Relayout(keepPlace: false);

            var open = panel.Spots.Single(s => s.Spoken.StartsWith("Open setup"));
            Assert.True(open.KeepOpen);
            Assert.Equal("A program: Gergur will not run it", open.Tip);
            panel.RunSpot(open);
            Assert.Equal("That is a program. Gergur will not run it; use Show in folder.", panel.Note);
        });
    }

    [Fact]
    public void OnlyWhatCameFromThePhoneSinceItWasLastOpenedIsNew()
    {
        var items = new[]
        {
            Item("text", "new", "phone", minutesAgo: 1),
            Item("text", "mine", "pc", minutesAgo: 1),
            Item("text", "old", "phone", minutesAgo: 30),
        };
        Assert.Equal(1, MainForm.UnseenFromPhone(items, Now.AddMinutes(-10)));
        Assert.Equal(0, MainForm.UnseenFromPhone(items, Now));
    }

    [Fact]
    public void ThePhoneButtonSitsInTheToolbarAndTakesDroppedFiles()
    {
        string form = Code("Gergur", "UI", "MainForm.cs");
        // In the toolbar whether or not the agents button is (the window itself is built in AgentsPanelTests).
        Assert.Contains("[_menuButton, _agentsButton, _phoneButton, _bookmarkButton, _downloadsButton]", form);
        Assert.Contains("[_menuButton, _phoneButton, _bookmarkButton, _downloadsButton]", form);
        Assert.Contains("_phoneButton.AllowDrop = true;", form);
        Assert.Contains("_phoneButton.DragDrop += (_, e) => _phonePanel?.OnDragDrop(_phoneButton, e);", form);
        Assert.Contains("session.Drop.Changed += OnDropChanged;", form);
        // Every window listens to the drop, and every one lets go of it.
        Assert.Contains("_session.Drop.Changed -= OnDropChanged;", form);
        Assert.Contains("_session.Drop.Changed -= _phonePanel.DropChanged;", form);

        // Sent files are copied, never moved: AddFileFromPath moves its source.
        string panel = Code("Gergur", "UI", "PhoneFlyout.cs");
        Assert.DoesNotContain("AddFileFromPath", panel);
    }
}
