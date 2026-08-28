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
    private const string EasyListUrl = "https://easylist.to/easylist/easylist.txt";

    private HostBlocklist _list;
    private long _blockedCount;

    public bool Enabled { get; set; }
    public string BlocklistPath { get; } = Path.Combine(Settings.DataDir, "blocklist.txt");
    public string EasyListHostsPath { get; } = Path.Combine(Settings.DataDir, "easylist-hosts.txt");
    public long BlockedCount => Interlocked.Read(ref _blockedCount);
    public int RuleCount => _list.Count;

    public RequestBlocker(bool enabled)
    {
        Enabled = enabled;
        _list = LoadLists();
    }

    private HostBlocklist LoadLists()
    {
        var lines = Enumerable.Empty<string>();
        if (File.Exists(BlocklistPath))
            lines = lines.Concat(File.ReadLines(BlocklistPath));
        if (File.Exists(EasyListHostsPath))
            lines = lines.Concat(File.ReadLines(EasyListHostsPath));
        return new HostBlocklist(HostBlocklist.ParseLines(lines));
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

    /// <summary>Downloads/refreshes the StevenBlack hosts list plus EasyList's
    /// simple domain rules. Returns the combined rule count.</summary>
    public async Task<int> UpdateBlocklistAsync()
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        Directory.CreateDirectory(Settings.DataDir);

        string hosts = await http.GetStringAsync(StevenBlackHostsUrl);
        await File.WriteAllTextAsync(BlocklistPath, hosts);
        try
        {
            string easyList = await http.GetStringAsync(EasyListUrl);
            var easyHosts = ExtractSimpleHostRules(easyList.Split('\n'));
            await File.WriteAllLinesAsync(EasyListHostsPath, easyHosts);
        }
        catch
        {
            // EasyList is a bonus layer; the hosts list alone is still a valid update.
        }
        _list = LoadLists();
        return _list.Count;
    }

    /// <summary>
    /// Takes only EasyList rules of the form "||domain.tld^" with no options,
    /// paths, wildcards, or exceptions - the subset that maps safely onto
    /// host-level blocking without needing the full ABP rule engine.
    /// </summary>
    internal static IEnumerable<string> ExtractSimpleHostRules(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length < 6 || !line.StartsWith("||", StringComparison.Ordinal) || !line.EndsWith("^", StringComparison.Ordinal))
                continue;
            var host = line[2..^1];
            if (host.Contains('/') || host.Contains('*') || host.Contains('$') || host.Contains('^') || !host.Contains('.'))
                continue;
            if (Uri.CheckHostName(host) == UriHostNameType.Dns)
                yield return host;
        }
    }
}
