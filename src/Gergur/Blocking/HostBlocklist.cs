using System.Collections.Frozen;

namespace Gergur.Blocking;

/// <summary>
/// Hosts-format blocklist ("0.0.0.0 ads.example.com" or bare "ads.example.com" lines).
/// Lookup is allocation-free: it runs on every network request.
/// </summary>
public sealed class HostBlocklist
{
    private static readonly HashSet<string> HostsFileNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost",
        "ip6-localhost", "ip6-loopback", "ip6-localnet", "ip6-mcastprefix",
        "ip6-allnodes", "ip6-allrouters", "ip6-allhosts", "0.0.0.0",
    };

    private readonly FrozenSet<string> _hosts;
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public int Count => _hosts.Count;

    public static HostBlocklist Empty { get; } = new(Enumerable.Empty<string>());

    public HostBlocklist(IEnumerable<string> hosts)
    {
        _hosts = hosts.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _lookup = _hosts.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public static HostBlocklist LoadFile(string path)
        => File.Exists(path) ? new HostBlocklist(ParseLines(File.ReadLines(path))) : Empty;

    public static IEnumerable<string> ParseLines(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw;
            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var host = parts.Length switch
            {
                0 => null,
                1 => parts[0],
                _ => parts[1], // "0.0.0.0 host" form
            };
            if (host is null || HostsFileNoise.Contains(host) || !host.Contains('.'))
                continue;
            yield return host;
        }
    }

    /// <summary>True when the host or any parent domain is listed (a.b.tracker.com matches tracker.com).</summary>
    public bool IsBlocked(string host)
    {
        if (_hosts.Count == 0 || string.IsNullOrEmpty(host))
            return false;
        ReadOnlySpan<char> span = host;
        while (true)
        {
            if (_lookup.Contains(span))
                return true;
            int dot = span.IndexOf('.');
            if (dot < 0)
                return false;
            span = span[(dot + 1)..];
            if (!span.Contains('.')) // never match a bare TLD
                return false;
        }
    }
}
