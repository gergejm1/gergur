using System.Text.Json;

namespace Gergur.Tabs;

/// <summary>
/// The DevTools protocol messages that make input a page cannot tell from a person's:
/// mouse presses, typing and keys arrive as the engine's own events, with isTrusted set,
/// rather than as el.click() or a value written into an input. Sites that ignore synthetic
/// events (uploads behind a button, editors, anything that checks isTrusted) answer these.
///
/// Only the parameters are built here, apart from the engine, so each can be tested; the
/// tab sends them with CallDevToolsProtocolMethodAsync. Coordinates are CSS pixels in the
/// main frame's viewport, which is what getBoundingClientRect gives.
/// </summary>
internal static class TrustedInput
{
    /// <summary>A key as the protocol wants it: its name, its physical code, the Windows key code, and the text it types, if any.</summary>
    internal sealed record Key(string Name, string Code, int VirtualKey, string? Text);

    /// <summary>A key and the modifiers held with it, parsed from "Enter", "Ctrl+A", "Shift+Tab" and the like.</summary>
    internal sealed record Chord(Key Key, int Modifiers)
    {
        public bool Alt => (Modifiers & 1) != 0;
        public bool Ctrl => (Modifiers & 2) != 0;
        public bool Meta => (Modifiers & 4) != 0;
        public bool Shift => (Modifiers & 8) != 0;
    }

    private static readonly Dictionary<string, Key> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = new("Enter", "Enter", 13, "\r"),
        ["Return"] = new("Enter", "Enter", 13, "\r"),
        ["Tab"] = new("Tab", "Tab", 9, null),
        ["Escape"] = new("Escape", "Escape", 27, null),
        ["Esc"] = new("Escape", "Escape", 27, null),
        ["Backspace"] = new("Backspace", "Backspace", 8, null),
        ["Delete"] = new("Delete", "Delete", 46, null),
        ["Del"] = new("Delete", "Delete", 46, null),
        ["Space"] = new(" ", "Space", 32, " "),
        ["ArrowUp"] = new("ArrowUp", "ArrowUp", 38, null),
        ["Up"] = new("ArrowUp", "ArrowUp", 38, null),
        ["ArrowDown"] = new("ArrowDown", "ArrowDown", 40, null),
        ["Down"] = new("ArrowDown", "ArrowDown", 40, null),
        ["ArrowLeft"] = new("ArrowLeft", "ArrowLeft", 37, null),
        ["Left"] = new("ArrowLeft", "ArrowLeft", 37, null),
        ["ArrowRight"] = new("ArrowRight", "ArrowRight", 39, null),
        ["Right"] = new("ArrowRight", "ArrowRight", 39, null),
        ["Home"] = new("Home", "Home", 36, null),
        ["End"] = new("End", "End", 35, null),
        ["PageUp"] = new("PageUp", "PageUp", 33, null),
        ["PageDown"] = new("PageDown", "PageDown", 34, null),
        ["Insert"] = new("Insert", "Insert", 45, null),
    };

    /// <summary>
    /// Parses a key chord. Modifiers (Ctrl, Control, Alt, Shift, Meta, Cmd, Win) are joined to
    /// the key with "+"; the key is a name above, F1 to F24, or one character. Null for
    /// anything else, so a typo is refused rather than pressed as something else.
    /// </summary>
    internal static Chord? ParseChord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string trimmed = text.Trim();
        // "Ctrl++" presses plus: the last part is the key even when it is "+".
        string keyPart;
        string[] modifierParts;
        if (trimmed.Length > 1 && trimmed.EndsWith("++", StringComparison.Ordinal))
        {
            keyPart = "+";
            modifierParts = trimmed[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else
        {
            var parts = trimmed.Split('+', StringSplitOptions.TrimEntries);
            if (parts.Any(p => p.Length == 0))
                return null;
            keyPart = parts[^1];
            modifierParts = parts[..^1];
        }
        int modifiers = 0;
        foreach (string m in modifierParts)
        {
            int bit = m.ToLowerInvariant() switch
            {
                "alt" or "option" => 1,
                "ctrl" or "control" => 2,
                "meta" or "cmd" or "command" or "win" => 4,
                "shift" => 8,
                _ => -1,
            };
            if (bit < 0 || (modifiers & bit) != 0)
                return null;
            modifiers |= bit;
        }
        var key = KeyFor(keyPart, shifted: (modifiers & 8) != 0);
        return key is null ? null : new Chord(key, modifiers);
    }

    private static Key? KeyFor(string name, bool shifted)
    {
        if (Named.TryGetValue(name, out var named))
            return named;
        if (name.Length >= 2 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24)
            return new Key("F" + f, "F" + f, 111 + f, null);
        if (name.Length != 1)
            return null;
        char c = name[0];
        if (char.IsAsciiLetter(c))
        {
            char upper = char.ToUpperInvariant(c);
            string typed = shifted ? upper.ToString() : char.ToLowerInvariant(c).ToString();
            return new Key(typed, "Key" + upper, upper, typed);
        }
        if (char.IsAsciiDigit(c))
            return new Key(c.ToString(), "Digit" + c, c, c.ToString());
        // Any other single character: typed as itself, with no physical key claimed for it.
        return char.IsControl(c) ? null : new Key(c.ToString(), "", 0, c.ToString());
    }

    /// <summary>The keyDown and keyUp for a chord. A modifier held with the key never types text: Ctrl+A selects, it does not write "a".</summary>
    internal static IReadOnlyList<object> KeyEvents(Chord chord)
    {
        var key = chord.Key;
        bool types = key.Text is not null && !chord.Ctrl && !chord.Alt && !chord.Meta;
        object Event(string type, bool withText) => withText
            ? new { type, modifiers = chord.Modifiers, key = key.Name, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey, text = key.Text, unmodifiedText = key.Text }
            : new { type, modifiers = chord.Modifiers, key = key.Name, code = key.Code, windowsVirtualKeyCode = key.VirtualKey, nativeVirtualKeyCode = key.VirtualKey };
        // "keyDown" with text is what produces a keypress and input; "rawKeyDown" is a key
        // that types nothing, which is what the browser itself sends for those.
        return [Event(types ? "keyDown" : "rawKeyDown", types), Event("keyUp", false)];
    }

    /// <summary>A press and release of the left button at a point, after moving there: a click as a person makes one.</summary>
    internal static IReadOnlyList<object> ClickEvents(double x, double y, int clickCount = 1)
    {
        var events = new List<object> { Mouse("mouseMoved", x, y, button: "none", buttons: 0, clickCount: 0) };
        for (int i = 1; i <= clickCount; i++)
        {
            events.Add(Mouse("mousePressed", x, y, button: "left", buttons: 1, clickCount: i));
            events.Add(Mouse("mouseReleased", x, y, button: "left", buttons: 0, clickCount: i));
        }
        return events;
    }

    /// <summary>The pointer moved to a point and left there: what hover menus and tooltips wait for.</summary>
    internal static object HoverEvent(double x, double y) => Mouse("mouseMoved", x, y, button: "none", buttons: 0, clickCount: 0);

    /// <summary>The wheel turned over a point, by a distance in CSS pixels; positive deltaY scrolls down.</summary>
    internal static object WheelEvent(double x, double y, double deltaX, double deltaY)
        => new { type = "mouseWheel", x, y, deltaX, deltaY, button = "none", modifiers = 0 };

    private static object Mouse(string type, double x, double y, string button, int buttons, int clickCount)
        => new { type, x, y, button, buttons, clickCount, modifiers = 0, pointerType = "mouse" };

    /// <summary>The protocol's parameters as the json it is sent as.</summary>
    internal static string Json(object parameters) => JsonSerializer.Serialize(parameters);

    /// <summary>
    /// Files fit to hand to a file input: every one an absolute path to a file that is there.
    /// The reason when not, in a sentence an agent can act on; null when they are.
    /// </summary>
    internal static string? CheckFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return "files required: one or more absolute paths to files on this computer";
        if (files.Count > 100)
            return "at most 100 files at once";
        foreach (string file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file))
                return $"not an absolute path: {file}";
            if (file.StartsWith(@"\\", StringComparison.Ordinal) || file.StartsWith("//", StringComparison.Ordinal))
                return $"files on another computer are not uploaded: {file}";
            if (!File.Exists(file))
                return $"no such file: {file}";
        }
        return null;
    }
}
