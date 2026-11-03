using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Gergur.App;

/// <summary>What the settings page acts through: the window it is open in. An interface so the page's requests can be answered in a test.</summary>
internal interface ISettingsHost
{
    Settings Settings { get; }

    /// <summary>The settings as the running engine was started with them, to tell which changes still need a restart.</summary>
    Settings StartedWith { get; }

    /// <summary>Writes the settings out and puts in force what can be without a restart. Null when saved, otherwise why not, in a sentence the page can show.</summary>
    string? Commit();

    bool PhoneRunning { get; }

    /// <summary>The WireGuard profiles there are to choose from.</summary>
    IReadOnlyList<string> VpnProfiles { get; }

    /// <summary>Turns the phone drop on (asking first, as the menu does) or off. Done after the request has been answered.</summary>
    void TogglePhone();

    /// <summary>Shows the pairing link. Done after the request has been answered.</summary>
    void ShowPairing();

    /// <summary>Restarts Gergur, tabs and all. Done after the request has been answered.</summary>
    void Restart();

    /// <summary>The Google account the person signs in to each site with.</summary>
    Data.GoogleAccountStore GoogleAccounts { get; }

    /// <summary>Tells every open settings page to read everything again.</summary>
    void Refresh();
}

/// <summary>
/// The settings page's side of things: every setting described for the page out of the
/// attributes on <see cref="Settings"/> itself (so there is still one place to keep
/// current, as there was for the old settings window's property grid), and changes made
/// one at a time and saved straight away.
///
/// This page is where the settings the agent API refuses are changed
/// (<see cref="SettingsPatch.DialogOnly"/>): a person is looking at it, and the API refuses
/// to read, script or type into the browser's own pages, this one included. Two of them
/// are not plain values even here. The phone drop is turned on and off through the same
/// path as the menu, which starts and stops its listener and asks first, and the pairing
/// key is never sent to the page at all: it can be replaced, which is how a phone is
/// revoked, and shown as the pairing link, which is the browser's own window.
/// </summary>
internal static class SettingsPage
{
    /// <summary>The order the sections come in, by the category each setting declares: everyday first, the engine last.</summary>
    private static readonly string[] SectionOrder = ["Browsing", "Privacy and blocking", "VPN", "Phone drop", "Agent API", "Engine"];

    /// <summary>Settings worth a second look before changing, shown with a warning.</summary>
    private static readonly HashSet<string> Caution = new(StringComparer.Ordinal)
    {
        nameof(Settings.DisableSiteIsolation),
        nameof(Settings.ExtraBrowserArguments),
        nameof(Settings.DisableFedCm),
    };

    /// <summary>Changed with their own buttons, never as a value: see the class summary.</summary>
    private static readonly HashSet<string> NotAValue = new(StringComparer.Ordinal)
    {
        nameof(Settings.DropEnabled),
        nameof(Settings.DropKey),
    };

    /// <summary>The section a category is shown under: its name without the leading digit that orders the property grid.</summary>
    internal static string SectionOf(string category)
    {
        string trimmed = category.TrimStart();
        int space = trimmed.IndexOf(' ');
        string name = space > 0 && trimmed[..space].All(char.IsAsciiDigit) ? trimmed[(space + 1)..] : trimmed;
        // Each of those settings carries its own Restart mark on the page, so the heading need not.
        const string RestartNote = " (restart required)";
        return name.EndsWith(RestartNote, StringComparison.Ordinal) ? name[..^RestartNote.Length] : name;
    }

    /// <summary>Everything the page shows, as it is now.</summary>
    internal static object Describe(ISettingsHost host)
    {
        var settings = host.Settings;
        var sections = new List<(string Title, List<object> Items)>();
        foreach (var property in SettingsPatch.Writable())
        {
            if (property.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false })
                continue;
            string section = SectionOf(property.GetCustomAttribute<CategoryAttribute>()?.Category ?? "Other");
            var list = sections.FirstOrDefault(s => s.Title == section).Items;
            if (list is null)
            {
                list = new List<object>();
                sections.Add((section, list));
            }
            list.Add(DescribeOne(host, property));
        }
        int Order(string title) => Array.IndexOf(SectionOrder, title) is var i and >= 0 ? i : SectionOrder.Length;
        return new
        {
            sections = sections
                .OrderBy(s => Order(s.Title))
                .Select(s => new { title = s.Title, items = s.Items })
                .ToArray(),
            restartPending = RestartPending(host),
            notSaving = settings.NotSavingBecause,
            googleAccounts = host.GoogleAccounts.Items.Select(a => new { site = a.Site, email = a.Email, namedBy = a.NamedBy }).ToArray(),
            googleAccountsUnreadable = host.GoogleAccounts.Unreadable,
        };
    }

    /// <summary>
    /// Forgets the Google account for one site, or for every site when <paramref name="site"/>
    /// is null. An agent signing in there afterwards is told no account is remembered.
    /// </summary>
    internal static object ForgetGoogleAccount(ISettingsHost host, string? site)
    {
        try
        {
            if (site is null)
                host.GoogleAccounts.ForgetAll();
            else if (!host.GoogleAccounts.Forget(site))
                throw new SettingsPageException($"No Google account is remembered for {site}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsPageException("It was not forgotten. " + ex.Message);
        }
        host.Refresh();
        return new { };
    }

    private static object DescribeOne(ISettingsHost host, PropertyInfo property)
    {
        string name = property.Name;
        var settings = host.Settings;
        string label = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? name;
        string description = property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        object? value = property.GetValue(settings);
        string kind;
        string[]? choices = null;
        int? min = null, max = null;

        if (name == nameof(Settings.DropEnabled))
        {
            kind = "phone";
            value = host.PhoneRunning;
        }
        else if (name == nameof(Settings.DropKey))
        {
            // Whether there is one, never what it is.
            kind = "key";
            value = settings.DropKey.Length > 0;
        }
        else if (property.PropertyType == typeof(bool))
        {
            kind = "bool";
        }
        else if (property.PropertyType == typeof(int))
        {
            kind = "int";
            if (SettingsPatch.RangeOf(name) is { } range)
                (min, max) = range;
        }
        else if (name == nameof(Settings.VpnProfile))
        {
            kind = "choice";
            choices = ["", .. host.VpnProfiles];
        }
        else if (SettingsPatch.ChoicesOf(name) is { } allowed)
        {
            kind = "choice";
            choices = allowed;
        }
        else
        {
            kind = "text";
        }

        return new
        {
            name,
            label,
            description,
            kind,
            value,
            choices,
            min,
            max,
            restart = Settings.RestartRequired.Contains(name),
            caution = Caution.Contains(name),
        };
    }

    /// <summary>The labels of settings changed since the engine started that only a restart puts in force.</summary>
    internal static string[] RestartPending(ISettingsHost host)
        => SettingsPatch.Writable()
            .Where(p => Settings.RestartRequired.Contains(p.Name) && !Equals(p.GetValue(host.Settings), p.GetValue(host.StartedWith)))
            .Select(p => p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? p.Name)
            .ToArray();

    /// <summary>
    /// Changes one setting and saves. Refused, with the reason, when the value is not one the
    /// setting can take; nothing changes then. A save that fails leaves the change in force
    /// for this run and says so, as the settings window did.
    /// </summary>
    internal static object Change(ISettingsHost host, string name, JsonElement value)
    {
        var property = SettingsPatch.Writable().FirstOrDefault(p => p.Name == name)
            ?? throw new SettingsPageException($"there is no setting called {name}");
        if (NotAValue.Contains(property.Name))
            throw new SettingsPageException($"{name} is changed with its own button");
        // Said with the name the page shows, not the one in the json file.
        string label = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? property.Name;
        object parsed = SettingsPatch.Read(value, property.PropertyType)
            ?? throw new SettingsPageException($"{label} expects {SettingsPatch.Describe(property.PropertyType)}");
        if (parsed is string text)
            parsed = text.Trim();
        if (SettingsPatch.Refuse(property.Name, parsed) is { } refusal)
            throw new SettingsPageException(refusal.StartsWith(property.Name + " ", StringComparison.Ordinal) ? label + refusal[property.Name.Length..] : refusal);
        if (Refuse(host, property.Name, parsed) is { } why)
            throw new SettingsPageException(why);

        string? problem = null;
        if (!Equals(property.GetValue(host.Settings), parsed))
        {
            property.SetValue(host.Settings, parsed);
            problem = host.Commit();
        }
        return new
        {
            value = property.GetValue(host.Settings),
            saved = problem is null,
            problem,
            restartPending = RestartPending(host),
        };
    }

    /// <summary>What the page refuses on top of the API's own checks.</summary>
    private static string? Refuse(ISettingsHost host, string name, object value)
    {
        // A format string the address bar navigates to: one naming an argument it was not
        // given used to throw straight from the Enter key, a crash on every search.
        if (name == nameof(Settings.SearchUrlTemplate) && !UrlHeuristics.IsUsableSearchTemplate((string)value))
            return $"The search url needs to be an http or https address with {{0}} where the search terms go, for example {UrlHeuristics.DefaultSearchUrlTemplate}";
        if (name == nameof(Settings.VpnProfile) && (string)value is { Length: > 0 } profile && !host.VpnProfiles.Contains(profile))
            return $"There is no vpn profile called {profile}.";
        // Two listeners on one port: whichever starts second fails.
        string[] ports = [nameof(Settings.VpnLocalPort), nameof(Settings.AgentServerPort), nameof(Settings.DropPort)];
        if (ports.Contains(name) && value is int port
            && ports.Where(p => p != name).Any(p => (int)typeof(Settings).GetProperty(p)!.GetValue(host.Settings)! == port))
            return $"Port {port} is already used by another of Gergur's listeners.";
        return null;
    }

    /// <summary>
    /// A new pairing key, which every phone paired with the old one loses the drop with.
    /// Put back if it cannot be saved: a key minted and not kept locks out tomorrow the
    /// phone paired with it today, and the old key is still the one on disk.
    /// </summary>
    internal static object NewPairingKey(ISettingsHost host)
    {
        string previous = host.Settings.DropKey;
        host.Settings.DropKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        if (host.Commit() is { } problem)
        {
            host.Settings.DropKey = previous;
            throw new SettingsPageException("The pairing key was not changed. " + problem);
        }
        return new { };
    }

    /// <summary>A request refused with a reason the page can show.</summary>
    internal sealed class SettingsPageException(string message) : Exception(message);
}
