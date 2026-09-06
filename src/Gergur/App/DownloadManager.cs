using Microsoft.Web.WebView2.Core;

namespace Gergur.App;

/// <summary>
/// One download in flight or finished. Values are snapshotted onto fields as the engine
/// reports them: reading a CoreWebView2DownloadOperation after the fact can throw, and
/// the downloads window must never be the thing that takes the browser down.
/// </summary>
public sealed class DownloadItem
{
    private readonly CoreWebView2DownloadOperation _operation;

    public string Uri { get; }
    public string FilePath { get; private set; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public long BytesReceived { get; private set; }
    public long TotalBytes { get; private set; }
    public CoreWebView2DownloadState State { get; private set; }

    public string FileName => FilePath.Length == 0 ? "(unknown)" : Path.GetFileName(FilePath);
    public bool IsRunning => State == CoreWebView2DownloadState.InProgress;

    /// <summary>Raised on progress and on state changes, so the list can repaint.</summary>
    public event EventHandler? Changed;

    internal DownloadItem(CoreWebView2DownloadOperation operation)
    {
        _operation = operation;
        Uri = Read(() => operation.Uri, "");
        FilePath = Read(() => operation.ResultFilePath, "");
        TotalBytes = Read(() => (long?)operation.TotalBytesToReceive ?? 0L, 0L);
        BytesReceived = Read(() => operation.BytesReceived, 0L);
        State = Read(() => operation.State, CoreWebView2DownloadState.InProgress);

        operation.BytesReceivedChanged += (_, _) =>
        {
            BytesReceived = Read(() => operation.BytesReceived, BytesReceived);
            Changed?.Invoke(this, EventArgs.Empty);
        };
        operation.StateChanged += (_, _) =>
        {
            State = Read(() => operation.State, State);
            FilePath = Read(() => operation.ResultFilePath, FilePath);
            TotalBytes = Read(() => (long?)operation.TotalBytesToReceive ?? TotalBytes, TotalBytes);
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Cancel()
    {
        try { _operation.Cancel(); } catch { }
    }

    /// <summary>Human-readable progress: "2.4 MB of 11.8 MB", or the terminal state.</summary>
    public string Describe() => State switch
    {
        CoreWebView2DownloadState.Completed => Size(TotalBytes > 0 ? TotalBytes : BytesReceived),
        CoreWebView2DownloadState.Interrupted => "Stopped",
        _ when TotalBytes > 0 => $"{Size(BytesReceived)} of {Size(TotalBytes)}",
        _ => Size(BytesReceived),
    };

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    private static T Read<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch { return fallback; }
    }
}

/// <summary>
/// Every download of every window, newest first. Shared through <see cref="AppSession"/>
/// so tearing a tab off does not split the list in two.
/// </summary>
public sealed class DownloadManager
{
    private readonly List<DownloadItem> _items = new();

    public IReadOnlyList<DownloadItem> Items => _items;
    public int RunningCount => _items.Count(i => i.IsRunning);

    /// <summary>Raised when a download starts, progresses, or finishes.</summary>
    public event EventHandler? Changed;

    internal DownloadItem Track(CoreWebView2DownloadOperation operation)
    {
        var item = new DownloadItem(operation);
        item.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _items.Insert(0, item);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    /// <summary>Forgets finished downloads. Anything still running stays in the list.</summary>
    public void ClearFinished()
    {
        _items.RemoveAll(i => !i.IsRunning);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
