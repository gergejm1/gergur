using System.Diagnostics;
using System.Net.Sockets;

namespace Gergur.App;

/// <summary>
/// Manages the local wireproxy process: a userspace WireGuard client that tunnels
/// to Cloudflare WARP and exposes a SOCKS5 proxy only this browser points at.
/// Nothing system-wide changes. Provisioned by scripts/setup-warp.ps1.
/// </summary>
public sealed class VpnTunnel : IDisposable
{
    public static readonly string VpnDir = Path.Combine(Settings.DataDir, "vpn");
    private static readonly string WireproxyExe = Path.Combine(VpnDir, "wireproxy.exe");
    private static readonly string ConfigPath = Path.Combine(VpnDir, "wireproxy.conf");

    private Process? _process;

    public static bool IsProvisioned => File.Exists(WireproxyExe) && File.Exists(ConfigPath);

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Starts wireproxy and waits until its SOCKS5 port accepts connections.</summary>
    public async Task<bool> StartAsync(int port, TimeSpan timeout)
    {
        if (!IsProvisioned)
            return false;
        if (IsRunning)
            return true;
        try
        {
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
    }

    public void Dispose() => Stop();
}
