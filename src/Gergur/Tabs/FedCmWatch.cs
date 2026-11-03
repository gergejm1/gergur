using System.Text.Json;
using Gergur.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Gergur.Tabs;

/// <summary>
/// "Sign in with Google" by FedCM: the account chooser the browser draws itself when a site's
/// Google button or One Tap asks for a sign-in (Google made it the only way in August 2025; see
/// <c>Settings.DisableFedCm</c>). It is not a page, so nothing can click in it; the DevTools
/// protocol's FedCm domain reports it (<c>dialogShown</c>, <c>dialogClosed</c>) and chooses in it
/// (<c>selectAccount</c>).
///
/// Watched only in an agent's own windows, never the person's: their sign-ins are theirs, and
/// nothing here learns from them. Turned on when the view is built in an agent's window and off
/// when the tab moves into the person's (<see cref="Tab.RefreshDialogMode"/>).
///
/// Events arrive on the UI thread; the dialog is read from the agent API's threads, so it is kept
/// as one immutable record swapped whole.
/// </summary>
internal sealed class FedCmWatch : IDisposable
{
    /// <summary>One account the chooser offers, as the protocol describes it.</summary>
    /// <param name="SignedInBefore">The protocol's loginState "SignIn": the person has used this
    /// account with this site before. "SignUp" is a first sign-in, where choosing it shares their
    /// details with the site, which is the person's to agree to.</param>
    internal sealed record Account(string Id, string Email, string IdpConfigUrl, bool SignedInBefore);

    /// <summary>A chooser that is up: its id, its kind (AccountChooser, AutoReauthn, ConfirmIdpLogin, Error) and its accounts.</summary>
    internal sealed record Dialog(string Id, string Type, IReadOnlyList<Account> Accounts);

    private readonly CoreWebView2 _core;
    private readonly CoreWebView2DevToolsProtocolEventReceiver _shown;
    private readonly CoreWebView2DevToolsProtocolEventReceiver _closed;
    private Dialog? _dialog;
    private long _changes;

    private FedCmWatch(CoreWebView2 core)
    {
        _core = core;
        _shown = core.GetDevToolsProtocolEventReceiver("FedCm.dialogShown");
        _closed = core.GetDevToolsProtocolEventReceiver("FedCm.dialogClosed");
        _shown.DevToolsProtocolEventReceived += OnShown;
        _closed.DevToolsProtocolEventReceived += OnClosed;
    }

    /// <summary>Starts watching this view. Null, written down, when the engine will not.</summary>
    internal static async Task<FedCmWatch?> StartAsync(CoreWebView2 core)
    {
        var watch = new FedCmWatch(core);
        try
        {
            await core.CallDevToolsProtocolMethodAsync("FedCm.enable", """{"disableRejectionDelay":true}""");
            DebugLog.Write("FedCM watched in an agent's window");
            return watch;
        }
        catch (Exception ex)
        {
            // Anything at all: the watch is optional, and this is awaited inside the view's
            // build, which must not fail for it.
            watch.Dispose();
            DebugLog.WriteAlways($"FedCM could not be watched in an agent's window: {ex.Message}");
            return null;
        }
    }

    /// <summary>The chooser up now, or null.</summary>
    internal Dialog? Current => Volatile.Read(ref _dialog);

    /// <summary>Goes up by one whenever a chooser shows or closes, to wait for the next.</summary>
    internal long Changes => Interlocked.Read(ref _changes);

    /// <summary>Chooses the account at <paramref name="index"/> in the chooser <paramref name="dialogId"/>. UI thread.</summary>
    internal Task<string> SelectAsync(string dialogId, int index)
        => _core.CallDevToolsProtocolMethodAsync("FedCm.selectAccount",
            JsonSerializer.Serialize(new { dialogId, accountIndex = index }));

    private void OnShown(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        var dialog = ParseDialog(e.ParameterObjectAsJson);
        Volatile.Write(ref _dialog, dialog);
        Interlocked.Increment(ref _changes);
        DebugLog.Write($"FedCM chooser shown: {dialog?.Type ?? "unreadable"}, {dialog?.Accounts.Count ?? 0} accounts");
    }

    private void OnClosed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        Volatile.Write(ref _dialog, null);
        Interlocked.Increment(ref _changes);
        DebugLog.Write("FedCM chooser closed");
    }

    /// <summary>The dialogShown event's parameters as a dialog; null when they do not read as one.</summary>
    internal static Dialog? ParseDialog(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "dialogId") is not { } id)
                return null;
            var accounts = new List<Account>();
            if (root.TryGetProperty("accounts", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object)
                        continue;
                    accounts.Add(new Account(Str(a, "accountId") ?? "", Str(a, "email") ?? "", Str(a, "idpConfigUrl") ?? "", Str(a, "loginState") == "SignIn"));
                }
            }
            return new Dialog(id, Str(root, "dialogType") ?? "", accounts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The browser signing the person back in by itself (FedCM's auto re-authentication): one
    /// account, already used with the site, Google's, and nothing to choose.
    /// </summary>
    internal static bool IsQuietReturn(Dialog dialog, string googleOrigin)
        => dialog.Type == "AutoReauthn" && dialog.Accounts.Count == 1
            && App.GoogleSignIn.IsGooglePage(dialog.Accounts[0].IdpConfigUrl, googleOrigin);

    /// <summary>
    /// Whether the chooser is the sign-in <c>gergur_login</c> means: the browser's quiet return,
    /// or a chooser offering Google's account <paramref name="email"/> (named, or remembered for
    /// the site). Otherwise the call is about the page (a password form under One Tap, say),
    /// and the chooser is left alone.
    /// </summary>
    internal static bool Applies(Dialog dialog, string? email, string googleOrigin)
        => IsQuietReturn(dialog, googleOrigin)
            || email is not null && dialog.Accounts.Any(a => App.GoogleSignIn.IsGooglePage(a.IdpConfigUrl, googleOrigin) && App.GoogleSignIn.CleanEmail(a.Email) == email);

    internal enum Refusal { None, NotAChooser, NotGoogle, NotListed, FirstSignIn }

    /// <summary>
    /// Which account in the chooser to take for <paramref name="email"/>: only Google's (its
    /// config at <paramref name="googleOrigin"/>), only that address, and only one already used
    /// with this site. Otherwise why not.
    /// </summary>
    internal static (int Index, Refusal Why) Choose(Dialog dialog, string email, string googleOrigin)
    {
        if (dialog.Type != "AccountChooser")
            return (-1, Refusal.NotAChooser);
        var google = dialog.Accounts
            .Select((a, i) => (a, i))
            .Where(x => App.GoogleSignIn.IsGooglePage(x.a.IdpConfigUrl, googleOrigin))
            .ToList();
        if (google.Count == 0)
            return (-1, Refusal.NotGoogle);
        var match = google.FirstOrDefault(x => App.GoogleSignIn.CleanEmail(x.a.Email) == email);
        if (match.a is null)
            return (-1, Refusal.NotListed);
        return match.a.SignedInBefore ? (match.i, Refusal.None) : (-1, Refusal.FirstSignIn);
    }

    public void Dispose()
    {
        _shown.DevToolsProtocolEventReceived -= OnShown;
        _closed.DevToolsProtocolEventReceived -= OnClosed;
        Volatile.Write(ref _dialog, null);
    }

    /// <summary>Stops watching and tells the engine so. UI thread.</summary>
    internal void Stop()
    {
        Dispose();
        try
        {
            _ = _core.CallDevToolsProtocolMethodAsync("FedCm.disable", "{}")
                .ContinueWith(t => DebugLog.Write($"FedCm.disable failed: {t.Exception?.InnerException?.Message}"), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            // The engine already gone: nothing left to tell. The handlers are off either way.
            DebugLog.Write($"FedCm.disable could not be sent: {ex.Message}");
        }
    }
}
