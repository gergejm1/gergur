using System.Text.Json;
using Gergur.App;
using Gergur.Blocking;
using Gergur.Diagnostics;

namespace Gergur.Data;

/// <summary>
/// A Google account to sign in to a site with: chosen by the person on Google's sign-in, or
/// named to an agent that then signed in with it (<paramref name="NamedBy"/>, the agent's
/// name, shown in the settings so a wrong one can be seen and forgotten).
/// </summary>
public sealed record GoogleAccount(string Site, string Email, DateTime ChosenUtc, string? NamedBy = null);

/// <summary>
/// Which Google account the person signs in to each site with, learned from their own sign-ins
/// (see <see cref="GoogleSignIn"/>), so an agent signing in to that site can click the same one.
/// One account per site, the latest chosen. Its own file, never the settings, so it does not
/// reach <c>GET /settings</c>; listed and forgotten on the settings page. Held to the same rule
/// as the bookmarks: a file that is there and cannot be read is never written over.
///
/// Thread safe: written from the UI thread when the person chooses, read from the agent API's.
/// </summary>
public sealed class GoogleAccountStore
{
    public static readonly string DefaultPath = Path.Combine(Settings.DataDir, "google-accounts.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<GoogleAccount> _items;
    private readonly bool _unreadable;

    /// <param name="path">Where the list lives. Defaults to the profile; a test passes its own.</param>
    public GoogleAccountStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        (_items, _unreadable) = Load(_path);
    }

    /// <summary>Whether the file was there and could not be read, so nothing is remembered this run.</summary>
    public bool Unreadable => _unreadable;

    /// <summary>Raised after any change, so an open settings page can redraw.</summary>
    public event EventHandler? Changed;

    /// <summary>Every site and its account, sites in order.</summary>
    public IReadOnlyList<GoogleAccount> Items
    {
        get
        {
            lock (_gate)
                return _items.OrderBy(a => a.Site, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>The account the person signs in to <paramref name="site"/> with, or null.</summary>
    public string? For(string site)
    {
        string clean = RelaxedSites.Clean(site);
        lock (_gate)
            return _items.FirstOrDefault(a => a.Site == clean)?.Email;
    }

    /// <summary>
    /// Remembers an account for a site: the person's own choice (<paramref name="namedBy"/>
    /// null), or one named to an agent that signed in with it (the agent's name). The latest
    /// wins, except that an agent naming the account already kept changes nothing, so a
    /// person's own choice stays marked as theirs. False, with nothing kept, for a site or an
    /// address that does not read as one. Throws (IOException) when the file could not be
    /// read at startup.
    /// </summary>
    public bool Remember(string site, string email, DateTime nowUtc, string? namedBy = null)
    {
        string clean = RelaxedSites.Clean(site);
        if (clean.Length == 0 || GoogleSignIn.CleanEmail(email) is not { } address)
            return false;
        string? by = namedBy is null ? null : CleanName(namedBy);
        bool changed = Commit(items =>
        {
            int at = items.FindIndex(a => a.Site == clean);
            if (at >= 0 && items[at].Email == address && (by is not null || items[at].NamedBy is null))
                return false;
            if (at >= 0)
                items.RemoveAt(at);
            items.Add(new GoogleAccount(clean, address, nowUtc, by));
            return true;
        });
        return changed || For(clean) == address;
    }

    /// <summary>Who named an account, as the settings show it: one printable line of at most 60 characters.</summary>
    internal static string CleanName(string name)
    {
        string line = new(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
        line = line.Trim();
        if (line.Length > 60)
            line = line[..60];
        return line.Length == 0 ? "an agent" : line;
    }

    /// <summary>Who named the account kept for a site: null when the person chose it, or when there is none.</summary>
    public string? NamedByFor(string site)
    {
        string clean = RelaxedSites.Clean(site);
        lock (_gate)
            return _items.FirstOrDefault(a => a.Site == clean)?.NamedBy;
    }

    /// <summary>Forgets the account for one site. Whether there was one.</summary>
    public bool Forget(string site)
    {
        string clean = RelaxedSites.Clean(site);
        return Commit(items => items.RemoveAll(a => a.Site == clean) > 0);
    }

    /// <summary>Forgets every site's account.</summary>
    public void ForgetAll() => Commit(items =>
    {
        bool any = items.Count > 0;
        items.Clear();
        return any;
    });

    private static (List<GoogleAccount> Items, bool Unreadable) Load(string path)
    {
        if (!File.Exists(path))
            return ([], false);
        try
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return ([], false);
            var items = JsonSerializer.Deserialize<List<GoogleAccount>>(text) ?? [];
            if (items.Any(a => a is null || RelaxedSites.Clean(a.Site ?? "") != a.Site || GoogleSignIn.CleanEmail(a.Email) != a.Email
                || (a.NamedBy is not null && CleanName(a.NamedBy) != a.NamedBy)))
                throw new JsonException("an entry is not a site and an address");
            return (items, false);
        }
        catch (Exception ex)
        {
            DebugLog.WriteAlways($"google-accounts.json could not be read, so Google accounts are not remembered this run: {ex.Message}");
            return ([], true);
        }
    }

    /// <summary>
    /// Makes a change and writes it, or puts the list back and throws when it cannot be written.
    /// A change that changes nothing writes nothing. Refused (an IOException) when the file
    /// could not be read at startup.
    /// </summary>
    private bool Commit(Func<List<GoogleAccount>, bool> change)
    {
        lock (_gate)
        {
            if (_unreadable)
                throw new IOException("Google accounts are not being remembered: google-accounts.json could not be read when Gergur started, and saving now would replace it. "
                    + BookmarkStore.WhatToDo.Replace("bookmarks", "Google accounts"));
            var before = _items.ToList();
            if (!change(_items))
                return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                Settings.WriteAtomically(_path, JsonSerializer.Serialize(_items, JsonOptions));
            }
            catch
            {
                _items.Clear();
                _items.AddRange(before);
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
