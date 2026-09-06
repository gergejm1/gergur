using System.Diagnostics;
using System.Net.Sockets;

namespace Gergur.App;

/// <summary>One WireGuard config the tunnel can run: WARP, or anything dropped in vpn\profiles.</summary>
public sealed record VpnProfile(string Name, string ConfigPath);

/// <summary>
/// Manages the local wireproxy process: a userspace WireGuard client that tunnels
/// to a WireGuard endpoint and exposes a SOCKS5 proxy only this browser points at.
/// Nothing system-wide changes. Cloudflare WARP is provisioned by
/// scripts/setup-warp.ps1; any other provider is just a .conf in vpn\profiles.
///
/// The SOCKS5 address never changes, so switching profiles only restarts wireproxy.
/// Turning the VPN on or off is what needs a browser restart, because the proxy is
/// a browser-process flag.
/// </summary>
public sealed class VpnTunnel : IDisposable
{
    public static readonly string VpnDir = Path.Combine(Settings.DataDir, "vpn");
    /// <summary>Extra WireGuard configs live here, one .conf per exit location.</summary>
    public static readonly string ProfilesDir = Path.Combine(VpnDir, "profiles");

    private static readonly string WireproxyExe = Path.Combine(VpnDir, "wireproxy.exe");
    private static readonly string WarpProfile = Path.Combine(VpnDir, "wgcf-profile.conf");
    private static readonly string ConfigPath = Path.Combine(VpnDir, "wireproxy.conf");

    public const string WarpName = "Cloudflare WARP";

    private Process? _process;

    public static bool IsProvisioned => File.Exists(WireproxyExe) && ListProfiles().Count > 0;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Name of the profile the running tunnel was started with.</summary>
    public string? ActiveProfileName { get; private set; }

    /// <summary>WARP first when it exists, then every .conf in vpn\profiles by name.</summary>
    public static IReadOnlyList<VpnProfile> ListProfiles()
    {
        var profiles = new List<VpnProfile>();
        if (File.Exists(WarpProfile))
            profiles.Add(new VpnProfile(WarpName, WarpProfile));
        try
        {
            if (Directory.Exists(ProfilesDir))
            {
                foreach (var path in Directory.EnumerateFiles(ProfilesDir, "*.conf").OrderBy(p => p))
                    profiles.Add(new VpnProfile(Path.GetFileNameWithoutExtension(path), path));
            }
        }
        catch
        {
            // An unreadable profiles folder just means fewer choices, not a failure.
        }
        return profiles;
    }

    /// <summary>The named profile, or the first available when the name is unknown or blank.</summary>
    public static VpnProfile? ResolveProfile(string? name)
    {
        var profiles = ListProfiles();
        return profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault();
    }

    /// <summary>Copies a WireGuard .conf into vpn\profiles and returns its profile name.</summary>
    public static string ImportProfile(string sourcePath)
    {
        Directory.CreateDirectory(ProfilesDir);
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string target = Path.Combine(ProfilesDir, name + ".conf");
        File.Copy(sourcePath, target, overwrite: true);
        return name;
    }

    /// <summary>wireproxy's own config: which WireGuard profile to dial, and where to listen.</summary>
    internal static string BuildWireproxyConfig(string wgConfigPath, int port)
        => $"WGConfig = {wgConfigPath}{Environment.NewLine}{Environment.NewLine}"
         + $"[Socks5]{Environment.NewLine}BindAddress = 127.0.0.1:{port}{Environment.NewLine}";

    /// <summary>Starts wireproxy on the given profile and waits until its SOCKS5 port accepts connections.</summary>
    public async Task<bool> StartAsync(int port, string? profileName, TimeSpan timeout)
    {
        if (!File.Exists(WireproxyExe))
            return false;
        if (IsRunning)
            return true;
        if (ResolveProfile(profileName) is not { } profile)
            return false;

        try
        {
            // Written every start: the profile, port, or install path may have changed
            // since setup-warp.ps1 wrote its version of this file.
            File.WriteAllText(ConfigPath, BuildWireproxyConfig(profile.ConfigPath, port));
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = WireproxyExe,
                Arguments = $"-c \"{ConfigPath}\"",
                WorkingDirectory = VpnDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            return false;
        }
        ActiveProfileName = profile.Name;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process is null || _process.HasExited)
                return false;
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(1));
                return true;
            }
            catch
            {
                await Task.Delay(300);
            }
        }
        return false;
    }

    /// <summary>Swaps the exit location without touching the browser: the SOCKS5 address is unchanged.</summary>
    public async Task<bool> SwitchProfileAsync(int port, string profileName, TimeSpan timeout)
    {
        Stop();
        return await StartAsync(port, profileName, timeout);
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch { }
        _process = null;
        ActiveProfileName = null;
    }

    public void Dispose() => Stop();
}
