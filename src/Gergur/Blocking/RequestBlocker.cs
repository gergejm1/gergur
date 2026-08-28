using Gergur.App;
using Microsoft.Web.WebView2.Core;

namespace Gergur.Blocking;

/// <summary>
/// Layer-2 blocking (layer 1 is the engine's Strict tracking prevention):
/// kills requests to blocklisted hosts before they leave the machine.
/// </summary>
public sealed class RequestBlocker
{
    private const string StevenBlackHostsUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";

    private HostBlocklist _list;
    private long _blockedCount;

    public bool Enabled { get; set; }
    public string BlocklistPath { get; } = Path.Combine(Settings.DataDir, "blocklist.txt");
    public long BlockedCount => Interlocked.Read(ref _blockedCount);
    public int RuleCount => _list.Count;

    public RequestBlocker(bool enabled)
    {
        Enabled = enabled;
        _list = HostBlocklist.LoadFile(BlocklistPath);
    }

    public void Attach(CoreWebView2 core)
    {
        // Document sources only: worker-sourced filters fan out to every WebView2
        // in the environment, so each worker request would be handled N times.
        core.AddWebResourceRequestedFilter(
            "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        // Hot path - fires synchronously for every request of every tab. Stay cheap.
        if (!Enabled || _list.Count == 0)
            return;
        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && _list.IsBlocked(uri.Host))
        {
            var core = (CoreWebView2)sender!;
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            Interlocked.Increment(ref _blockedCount);
        }
    }

    /// <summary>Downloads/refreshes the StevenBlack hosts list. Returns the new rule count.</summary>
    public async Task<int> UpdateBlocklistAsync()
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        string text = await http.GetStringAsync(StevenBlackHostsUrl);
        Directory.CreateDirectory(Settings.DataDir);
        await File.WriteAllTextAsync(BlocklistPath, text);
        _list = HostBlocklist.LoadFile(BlocklistPath);
        return _list.Count;
    }
}
