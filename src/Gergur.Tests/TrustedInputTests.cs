using System.Text.Json;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>The DevTools input messages behind trusted clicks, typing, keys, hovering, scrolling and uploads.</summary>
public sealed class TrustedInputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gergur-trusted-{Guid.NewGuid():N}");

    public TrustedInputTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static JsonElement[] Parse(IEnumerable<object> events)
        => events.Select(e => JsonDocument.Parse(JsonSerializer.Serialize(e)).RootElement.Clone()).ToArray();

    [Theory]
    [InlineData("Enter", "Enter", "Enter", 13, 0)]
    [InlineData("enter", "Enter", "Enter", 13, 0)]
    [InlineData("Tab", "Tab", "Tab", 9, 0)]
    [InlineData("Shift+Tab", "Tab", "Tab", 9, 8)]
    [InlineData("Ctrl+A", "a", "KeyA", 65, 2)]
    [InlineData("Ctrl+Shift+K", "K", "KeyK", 75, 10)]
    [InlineData("Esc", "Escape", "Escape", 27, 0)]
    [InlineData("F5", "F5", "F5", 116, 0)]
    [InlineData("ArrowDown", "ArrowDown", "ArrowDown", 40, 0)]
    [InlineData("Alt+Left", "ArrowLeft", "ArrowLeft", 37, 1)]
    [InlineData("Cmd+Enter", "Enter", "Enter", 13, 4)]
    [InlineData("7", "7", "Digit7", 55, 0)]
    [InlineData("Ctrl++", "+", "", 0, 2)]
    public void AChordIsParsedIntoItsKeyAndModifiers(string text, string name, string code, int vk, int modifiers)
    {
        var chord = TrustedInput.ParseChord(text);
        Assert.NotNull(chord);
        Assert.Equal(name, chord!.Key.Name);
        Assert.Equal(code, chord.Key.Code);
        Assert.Equal(vk, chord.Key.VirtualKey);
        Assert.Equal(modifiers, chord.Modifiers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Enterr")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+A")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("F25")]
    [InlineData("ab")]
    public void AnythingElseIsRefusedRatherThanPressedAsSomethingElse(string? text)
        => Assert.Null(TrustedInput.ParseChord(text));

    [Fact]
    public void EnterTypesAReturnAndCtrlATypesNothing()
    {
        var enter = Parse(TrustedInput.KeyEvents(TrustedInput.ParseChord("Enter")!));
        Assert.Equal("keyDown", enter[0].GetProperty("type").GetString());
        Assert.Equal("\r", enter[0].GetProperty("text").GetString());
        Assert.Equal("keyUp", enter[1].GetProperty("type").GetString());
        Assert.False(enter[1].TryGetProperty("text", out _));

        var selectAll = Parse(TrustedInput.KeyEvents(TrustedInput.ParseChord("Ctrl+A")!));
        Assert.Equal("rawKeyDown", selectAll[0].GetProperty("type").GetString());
        Assert.False(selectAll[0].TryGetProperty("text", out _));
        Assert.Equal(2, selectAll[0].GetProperty("modifiers").GetInt32());

        var tab = Parse(TrustedInput.KeyEvents(TrustedInput.ParseChord("Tab")!));
        Assert.Equal("rawKeyDown", tab[0].GetProperty("type").GetString());
    }

    [Fact]
    public void AClickMovesThenPressesAndReleasesTheLeftButton()
    {
        var events = Parse(TrustedInput.ClickEvents(120.5, 48));
        Assert.Equal(["mouseMoved", "mousePressed", "mouseReleased"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.All(events, e => Assert.Equal(120.5, e.GetProperty("x").GetDouble()));
        Assert.Equal("left", events[1].GetProperty("button").GetString());
        Assert.Equal(1, events[1].GetProperty("clickCount").GetInt32());
        Assert.Equal(1, events[1].GetProperty("buttons").GetInt32());
        Assert.Equal(0, events[2].GetProperty("buttons").GetInt32());

        var twice = Parse(TrustedInput.ClickEvents(1, 1, clickCount: 2));
        Assert.Equal([0, 1, 1, 2, 2], twice.Select(e => e.GetProperty("clickCount").GetInt32()));
    }

    [Fact]
    public void HoverAndWheelAreWhatTheProtocolExpects()
    {
        var hover = Parse([TrustedInput.HoverEvent(5, 6)])[0];
        Assert.Equal("mouseMoved", hover.GetProperty("type").GetString());
        Assert.Equal("none", hover.GetProperty("button").GetString());
        var wheel = Parse([TrustedInput.WheelEvent(5, 6, 0, 600)])[0];
        Assert.Equal("mouseWheel", wheel.GetProperty("type").GetString());
        Assert.Equal(600, wheel.GetProperty("deltaY").GetDouble());
    }

    [Fact]
    public void OnlyFilesThatAreThereAreUploaded()
    {
        string real = Path.Combine(_dir, "build.ipa");
        File.WriteAllText(real, "x");
        Assert.Null(TrustedInput.CheckFiles([real]));
        Assert.Contains("required", TrustedInput.CheckFiles([]));
        Assert.Contains("absolute", TrustedInput.CheckFiles(["build.ipa"]));
        Assert.Contains("no such file", TrustedInput.CheckFiles([Path.Combine(_dir, "missing.ipa")]));
        Assert.Contains("another computer", TrustedInput.CheckFiles([@"\\server\share\x.ipa"]));
        Assert.Contains("at most", TrustedInput.CheckFiles(Enumerable.Repeat(real, 101).ToArray()));
    }
}
