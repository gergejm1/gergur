using System.Text.Json;
using Gergur.App;
using Gergur.Data;
using Xunit;
using Page = Gergur.App.InternalPages.Page;

namespace Gergur.Tests;

/// <summary>
/// The settings page's requests, answered against a fake window: what it describes, what
/// it will and will not change, the pairing key never leaving the browser, and which pages
/// may ask at all. Nothing here can reach the real settings file: the host's Commit is the
/// only save, and the fake one only counts.
/// </summary>
public sealed class SettingsPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gergur-tests", "settings-page-" + Guid.NewGuid().ToString("N"));

    public SettingsPageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeHost : ISettingsHost
    {
        public Settings Settings { get; } = new() { DropKey = "0123456789ABCDEF01234567" };
        public Settings StartedWith { get; }
        public bool PhoneRunning { get; set; }
        public IReadOnlyList<string> VpnProfiles { get; set; } = ["Cloudflare WARP", "Sweden"];
        public string? Problem { get; set; }
        public int Commits;
        public readonly List<string> Did = new();

        public FakeHost() => StartedWith = Settings.Clone();

        public string? Commit()
        {
            Commits++;
            return Problem;
        }

        public void TogglePhone() => Did.Add("phone");
        public void ShowPairing() => Did.Add("pairing");
        public void Restart() => Did.Add("restart");
    }

    private InternalPages.Services Services(ISettingsHost? host)
        => new(
            new HistoryStore(Path.Combine(_dir, "history.jsonl")),
            new BookmarkStore(Path.Combine(_dir, "bookmarks.json")),
            new DownloadManager(),
            (_, _) => { },
            host);

    private JsonElement Ask(ISettingsHost? host, string op, object? args = null, Page page = Page.Settings)
        => JsonDocument.Parse(InternalPages.Answer(page, JsonSerializer.Serialize(new { id = 1, op, args = args ?? new { } }), Services(host))).RootElement;

    private static JsonElement Item(JsonElement listed, string name)
        => listed.GetProperty("result").GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("name").GetString() == name);

    [Fact]
    public void OnlyTheSettingsPageMayAskAndOnlyForSettings()
    {
        var host = new FakeHost();
        foreach (var page in new[] { Page.Home, Page.History, Page.Downloads, Page.Bookmarks })
            Assert.Contains("cannot ask for", Ask(host, "settings.set", new { name = "BlocklistEnabled", value = false }, page).GetProperty("error").GetString());
        Assert.Contains("cannot ask for", Ask(host, "history.clear").GetProperty("error").GetString());
        Assert.True(host.Settings.BlocklistEnabled);
        Assert.Equal(0, host.Commits);
    }

    [Fact]
    public void EverySettingIsDescribedInSectionsWithTheEngineLast()
    {
        var listed = Ask(new FakeHost(), "settings.list");
        Assert.True(listed.GetProperty("ok").GetBoolean());
        var titles = listed.GetProperty("result").GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("title").GetString()!).ToArray();
        Assert.Equal(["Browsing", "Privacy and blocking", "VPN", "Phone drop", "Agent API", "Engine"], titles);

        var tracking = Item(listed, "TrackingPrevention");
        Assert.Equal("choice", tracking.GetProperty("kind").GetString());
        Assert.Equal(["None", "Basic", "Balanced", "Strict"], tracking.GetProperty("choices").EnumerateArray().Select(c => c.GetString()!));
        var port = Item(listed, "AgentServerPort");
        Assert.Equal(("int", 1024, 65535, true), (port.GetProperty("kind").GetString(), port.GetProperty("min").GetInt32(), port.GetProperty("max").GetInt32(), port.GetProperty("restart").GetBoolean()));
        Assert.True(Item(listed, "DisableSiteIsolation").GetProperty("caution").GetBoolean());
        Assert.Equal(["", "Cloudflare WARP", "Sweden"], Item(listed, "VpnProfile").GetProperty("choices").EnumerateArray().Select(c => c.GetString()!));
    }

    [Fact]
    public void ThePairingKeyNeverReachesThePage()
    {
        var host = new FakeHost();
        string raw = InternalPages.Answer(Page.Settings, """{"id":1,"op":"settings.list","args":{}}""", Services(host));
        Assert.DoesNotContain(host.Settings.DropKey, raw);
        var key = Item(JsonDocument.Parse(raw).RootElement, "DropKey");
        Assert.Equal(("key", true), (key.GetProperty("kind").GetString(), key.GetProperty("value").GetBoolean()));
    }

    [Fact]
    public void AChangeIsMadeAndSavedAtOnce()
    {
        var host = new FakeHost();
        var answer = Ask(host, "settings.set", new { name = "BlocklistEnabled", value = false });
        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.False(host.Settings.BlocklistEnabled);
        Assert.Equal(1, host.Commits);
        Assert.True(answer.GetProperty("result").GetProperty("saved").GetBoolean());

        // The same value again changes nothing and writes nothing.
        Ask(host, "settings.set", new { name = "BlocklistEnabled", value = false });
        Assert.Equal(1, host.Commits);

        // Text is trimmed.
        Ask(host, "settings.set", new { name = "RelaxedSites", value = "  youtube.com  " });
        Assert.Equal("youtube.com", host.Settings.RelaxedSites);
    }

    [Fact]
    public void ARestartSettingIsListedAsWaitingUntilItIsPutBack()
    {
        var host = new FakeHost();
        var answer = Ask(host, "settings.set", new { name = "VpnEnabled", value = true });
        Assert.Equal(["VPN enabled"], answer.GetProperty("result").GetProperty("restartPending").EnumerateArray().Select(n => n.GetString()!));
        answer = Ask(host, "settings.set", new { name = "VpnEnabled", value = false });
        Assert.Empty(answer.GetProperty("result").GetProperty("restartPending").EnumerateArray());
    }

    [Theory]
    [InlineData("DropPort", 80, "Phone drop port expects a whole number from 1024 to 65535")]
    [InlineData("AgentServerPort", 24003, "already used by another of Gergur's listeners")]
    [InlineData("TrackingPrevention", "Paranoid", "expects one of None, Basic, Balanced, Strict")]
    [InlineData("SearchUrlTemplate", "https://example.com/search", "{0}")]
    [InlineData("SearchUrlTemplate", "javascript:alert({0})", "http or https")]
    [InlineData("VpnProfile", "Narnia", "no vpn profile called Narnia")]
    [InlineData("BlocklistEnabled", "yes", "expects true or false")]
    [InlineData("NoSuchSetting", true, "there is no setting called NoSuchSetting")]
    public void ABadValueIsRefusedWithTheReasonAndChangesNothing(string name, object value, string reason)
    {
        var host = new FakeHost();
        string before = JsonSerializer.Serialize(host.Settings);
        var answer = Ask(host, "settings.set", new { name, value });
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains(reason, answer.GetProperty("error").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(host.Settings));
        Assert.Equal(0, host.Commits);
    }

    [Theory]
    [InlineData("DropEnabled", true)]
    [InlineData("DropKey", "attacker-chosen-key-1234")]
    public void ThePhoneDropAndItsKeyAreNotPlainValues(string name, object value)
    {
        var host = new FakeHost();
        var answer = Ask(host, "settings.set", new { name, value });
        Assert.Contains("its own button", answer.GetProperty("error").GetString());
        Assert.Equal("0123456789ABCDEF01234567", host.Settings.DropKey);
        Assert.False(host.Settings.DropEnabled);
    }

    [Fact]
    public void ANewPairingKeyReplacesTheOldOrIsPutBackWhenItCannotBeSaved()
    {
        var host = new FakeHost();
        Assert.True(Ask(host, "settings.newPairingKey").GetProperty("ok").GetBoolean());
        Assert.Matches("^[0-9A-F]{24}$", host.Settings.DropKey);
        Assert.NotEqual("0123456789ABCDEF01234567", host.Settings.DropKey);

        string kept = host.Settings.DropKey;
        host.Problem = "It was not saved.";
        var refused = Ask(host, "settings.newPairingKey");
        Assert.Contains("The pairing key was not changed.", refused.GetProperty("error").GetString());
        Assert.Equal(kept, host.Settings.DropKey);
    }

    [Fact]
    public void ASaveThatFailsKeepsTheChangeAndSaysSo()
    {
        var host = new FakeHost { Problem = "It was not saved: the settings file could not be written." };
        var result = Ask(host, "settings.set", new { name = "FormAutofill", value = false }).GetProperty("result");
        Assert.False(result.GetProperty("saved").GetBoolean());
        Assert.Contains("could not be written", result.GetProperty("problem").GetString());
        Assert.False(host.Settings.FormAutofill);
    }

    [Fact]
    public void TheButtonsAskTheWindow()
    {
        var host = new FakeHost();
        Ask(host, "settings.togglePhone");
        Ask(host, "settings.showPairing");
        Ask(host, "settings.restart");
        Assert.Equal(["phone", "pairing", "restart"], host.Did);
    }

    [Fact]
    public void AWindowWithoutASettingsHostRefusesRatherThanThrows()
        => Assert.Contains("cannot be changed", Ask(null, "settings.list").GetProperty("error").GetString());

    [Fact]
    public void ItIsOneOfTheBrowsersOwnPagesAndCannotBeReadByAnAgent()
    {
        Assert.Equal(Page.Settings, InternalPages.Identify(InternalPages.UrlOf(Page.Settings)));
        Assert.Equal(InternalPages.UrlOf(Page.Settings), InternalPages.Resolve("gergur://settings"));
        Assert.Equal("gergur://settings", InternalPages.NameOf(InternalPages.UrlOf(Page.Settings)));
        Assert.Contains("settings", InternalPages.OwnPageRefusedException.Text);
        Assert.Throws<InternalPages.OwnPageRefusedException>(() =>
            new Gergur.Tabs.Tab(null!, new Gergur.Tabs.TabSnapshot(InternalPages.UrlOf(Page.Settings), "Settings")).RefuseOwnPage());
    }
}
