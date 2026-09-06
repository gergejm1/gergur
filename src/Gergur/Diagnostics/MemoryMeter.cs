using System.Diagnostics;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace Gergur.Diagnostics;

public sealed record ProcessSample(string Kind, int Pid, long PrivateBytes);

public sealed record MemorySnapshot(
    DateTime TakenUtc,
    long EnginePrivateBytes,
    long ShellPrivateBytes,
    int RendererCount,
    IReadOnlyList<ProcessSample> Processes)
{
    public long TotalPrivateBytes => EnginePrivateBytes + ShellPrivateBytes;

    public static string Format(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes >> 20} MB";
}

/// <summary>
/// Measures only OUR engine's processes (via the environment's process list - a
/// machine-wide msedgewebview2.exe scan would catch other apps' WebViews too).
/// Private bytes, not working set: shared pages would be double-counted otherwise.
/// </summary>
public static class MemoryMeter
{
    public static MemorySnapshot Capture(CoreWebView2Environment environment)
    {
        var samples = new List<ProcessSample>();
        long engineBytes = 0;
        int rendererCount = 0;

        try
        {
            foreach (var info in environment.GetProcessInfos())
            {
                long bytes = TryGetPrivateBytes(info.ProcessId);
                if (bytes < 0)
                    continue; // process died between enumeration and query
                engineBytes += bytes;
                if (info.Kind == CoreWebView2ProcessKind.Renderer)
                    rendererCount++;
                samples.Add(new ProcessSample(info.Kind.ToString(), info.ProcessId, bytes));
            }
        }
        catch
        {
            // Engine gone (shutdown/crash): report shell only.
        }

        using var self = Process.GetCurrentProcess();
        long shellBytes = self.PrivateMemorySize64;
        samples.Add(new ProcessSample("Shell", self.Id, shellBytes));

        return new MemorySnapshot(DateTime.UtcNow, engineBytes, shellBytes, rendererCount, samples);
    }

    private static long TryGetPrivateBytes(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.PrivateMemorySize64;
        }
        catch
        {
            return -1;
        }
    }

    public static string ToCsv(MemorySnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("taken_utc,kind,pid,private_bytes");
        foreach (var p in snapshot.Processes)
            sb.AppendLine($"{snapshot.TakenUtc:O},{p.Kind},{p.Pid},{p.PrivateBytes}");
        sb.AppendLine($"{snapshot.TakenUtc:O},TOTAL,,{snapshot.TotalPrivateBytes}");
        return sb.ToString();
    }
}
