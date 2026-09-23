using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gergur.App;

/// <summary>
/// Reading and changing settings from outside the settings dialog.
///
/// Before this, seeing what a setting was meant opening a json file, and changing one
/// meant closing the browser, editing that file and starting it again. Testing a change
/// twice cost three restarts, and one of those restarts is how a browsing session was
/// lost here. The dialog already knows which settings the engine only reads at startup;
/// this reuses that list rather than inventing a second opinion about it.
///
/// Not everything is on offer. The agent API grants arbitrary JavaScript in every
/// logged-in session this browser holds, and its whole security boundary is that it
/// listens on loopback behind a token. A handful of settings can move that boundary or
/// give away the credential to the one listener that reaches past it, so those are read
/// and written from the settings window and nowhere else.
/// </summary>
internal static class SettingsPatch
{
    /// <summary>What a snapshot says instead of a secret.</summary>
    public const string Hidden = "(hidden)";

    /// <summary>
    /// Settings whose value is a credential. Reported as present, never as themselves:
    /// <c>GET /settings</c> and the MCP tool behind it put their answer into a transcript,
    /// and DropKey is the pairing key for the phone drop, which is the only listener in
    /// this browser that reaches beyond loopback.
    /// </summary>
    private static readonly HashSet<string> Secret = new(StringComparer.Ordinal)
    {
        nameof(Settings.DropKey),
    };

    /// <summary>Whether this setting is redacted when the settings are read out.</summary>
    public static bool IsSecret(string name) => Secret.Contains(name);

    /// <summary>
    /// Settings this API will not change, whoever asks.
    ///
    /// The test is not how dangerous a setting sounds, it is whether changing it widens
    /// what this API can reach, and whether that outlives the agent that changed it.
    /// Every one of these is persisted and in force at the next launch, long after the
    /// session that asked is gone, and none of them shows up anywhere a person would
    /// notice. So they are changed from the settings window, where somebody is looking
    /// at what they typed. Refused out loud rather than quietly dropped.
    /// </summary>
    public static readonly IReadOnlySet<string> DialogOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        // Who can reach this browser from off the machine, and with what credential.
        nameof(Settings.DropKey),
        nameof(Settings.DropEnabled),
        nameof(Settings.DropPort),
        nameof(Settings.AgentServerEnabled),
        nameof(Settings.AgentServerPort),
        // Arbitrary engine flags, which is the same as turning the rest of its security off.
        nameof(Settings.ExtraBrowserArguments),
        // The renderer sandbox boundary. Off, it stays off for every future session.
        nameof(Settings.DisableSiteIsolation),
        // Whether traffic goes through the tunnel, which profile, and what skips it.
        // "VpnBypassHosts": "*" sends everything around it while GET /settings still
        // reports the vpn as on and the status bar still shows it, so the browser would
        // quietly stop doing the one thing it was turned on for.
        nameof(Settings.VpnEnabled),
        nameof(Settings.VpnProfile),
        nameof(Settings.VpnBypassHosts),
        // The port the engine's --proxy-server flag is built from. An agent that points it
        // at a port it holds, and waits for the next launch, receives every request this
        // browser makes, while GET /settings still reports the vpn as on and the status
        // bar still shows it. Word for word the reason the bypass list is refused.
        nameof(Settings.VpnLocalPort),
        // Where every search the person types goes. Validating it was not enough: a
        // template can be perfectly well formed and still point at somebody else's
        // server, and it outlives the agent that set it by surviving restarts. The
        // validation stays for the crash it prevents in hand-edited files.
        nameof(Settings.SearchUrlTemplate),
    };

    /// <summary>The values a setting spelled as a string is allowed to take.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [nameof(Settings.TrackingPrevention)] = ["None", "Basic", "Balanced", "Strict"],
        [nameof(Settings.PageTheme)] = ["Auto", "Light", "Dark"],
    };

    /// <summary>
    /// What a number setting may be. A type check alone lets through a port of 0, which
    /// persists and locks this API out of itself until somebody hand-edits the json, and
    /// a sleep timer of -5, which is not a state any part of this was written for.
    /// </summary>
    private static readonly Dictionary<string, (int Low, int High)> Range = new(StringComparer.Ordinal)
    {
        [nameof(Settings.SuspendAfterMinutes)] = (1, 1440),
        [nameof(Settings.DiscardAfterMinutes)] = (2, 1440),
        [nameof(Settings.SleepAllWhenBackgroundedMinutes)] = (0, 1440),
        [nameof(Settings.V8ScavengerMaxMb)] = (0, 4096),
    };

    /// <summary>Every writable setting, by the name it has in the json file.</summary>
    private static IEnumerable<PropertyInfo> Writable()
        => typeof(Settings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    /// <summary>
    /// The current values, for a caller that wants to know before it changes one. A
    /// credential is listed but not given: a caller needs to know DropKey exists and is
    /// set, and never needs to be told what it is.
    /// </summary>
    public static Dictionary<string, object?> Snapshot(Settings settings)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in Writable())
        {
            object? value = property.GetValue(settings);
            if (Secret.Contains(property.Name))
                value = value is string set && set.Length > 0 ? Hidden : "";
            snapshot[property.Name] = value;
        }
        return snapshot;
    }

    /// <summary>
    /// Applies what it can and reports the rest. Returns an error message when the patch
    /// itself is unusable, and null when it was applied.
    ///
    /// Nothing is applied at all if any value is refused: half a settings change is
    /// harder to reason about than none, and the caller gets to fix its request.
    ///
    /// Changes memory only. Writing them out is the caller's decision, because
    /// <see cref="Settings.Save"/> writes to one fixed path under %LOCALAPPDATA% and a
    /// helper that reaches it on its own cannot be exercised without replacing the real
    /// settings of whoever runs the code. That is not hypothetical: a run of
    /// SettingsPatchTests took out the vpn profile, the blocklist and the phone drop's
    /// pairing key.
    /// </summary>
    public static string? Apply(
        Settings settings,
        JsonElement patch,
        List<string> applied,
        List<string> needsRestart,
        List<string> unknown)
    {
        var byName = Writable().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var pending = new List<(PropertyInfo Property, object Value)>();

        foreach (var field in patch.EnumerateObject())
        {
            if (!byName.TryGetValue(field.Name, out var property))
            {
                unknown.Add(field.Name);
                continue;
            }

            if (DialogOnly.Contains(property.Name))
                return $"{property.Name} is changed from the settings window only";

            object? value = Read(field.Value, property.PropertyType);
            if (value is null)
                return $"{property.Name} expects {Describe(property.PropertyType)}";

            if (Refuse(property.Name, value) is { } refusal)
                return refusal;

            pending.Add((property, value));
        }

        // Applied to a copy first, so a pair that only makes sense together can be judged
        // before anything lands and the all-or-nothing promise still holds.
        var proposed = settings.Clone();
        foreach (var (property, value) in pending)
            property.SetValue(proposed, value);

        // Only when this patch actually moves one of them. Judging the pair on the whole
        // resulting state meant a settings file that already held a bad pair, which the
        // settings window can still produce, made every unrelated patch fail: turning the
        // blocklist off came back as a complaint about DiscardAfterMinutes, and an unknown
        // name came back as that instead of being reported as unknown.
        bool touchesTimers = pending.Any(p =>
            p.Property.Name is nameof(Settings.SuspendAfterMinutes) or nameof(Settings.DiscardAfterMinutes));
        if (touchesTimers && RefuseTogether(proposed) is { } together)
            return together;

        foreach (var (property, value) in pending)
        {
            property.SetValue(settings, value);
            applied.Add(property.Name);
            if (Settings.RestartRequired.Contains(property.Name))
                needsRestart.Add(property.Name);
        }

        return null;
    }

    /// <summary>Why this value is not allowed, or null when it is.</summary>
    private static string? Refuse(string name, object value)
    {
        if (value is int number && Range.TryGetValue(name, out var range)
            && (number < range.Low || number > range.High))
            return $"{name} expects a whole number from {range.Low} to {range.High}";

        if (value is string text && Allowed.TryGetValue(name, out var allowed)
            && !allowed.Contains(text, StringComparer.Ordinal))
            return $"{name} expects one of {string.Join(", ", allowed)}";

        return null;
    }

    /// <summary>
    /// Refusals that need more than one value to judge.
    ///
    /// Each setting is checked on its own above, which cannot catch a pair that is fine
    /// apart and nonsense together: discarding a tab before it has ever been suspended
    /// skips the cheap half of the memory policy entirely, and nothing downstream would
    /// report it as anything other than tabs behaving oddly.
    /// </summary>
    private static string? RefuseTogether(Settings settings)
        => settings.DiscardAfterMinutes <= settings.SuspendAfterMinutes
            ? $"{nameof(Settings.DiscardAfterMinutes)} has to be more than "
                + $"{nameof(Settings.SuspendAfterMinutes)}, or a tab is discarded before it ever sleeps"
            : null;

    private static object? Read(JsonElement value, Type type)
    {
        if (type == typeof(bool))
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        if (type == typeof(int))
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
                ? number
                : null;
        if (type == typeof(string))
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return null;
    }

    private static string Describe(Type type)
        => type == typeof(bool) ? "true or false"
        : type == typeof(int) ? "a whole number"
        : type == typeof(string) ? "a string"
        : type.Name;
}
