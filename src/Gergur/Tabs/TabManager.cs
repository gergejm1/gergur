using Gergur.App;
using Gergur.Blocking;
using Gergur.Diagnostics;

namespace Gergur.Tabs;

/// <summary>Ordered tab list, active-tab tracking, create/close/activate/cycle.</summary>
public sealed class TabManager
{
    private readonly List<Tab> _tabs = new();
    private readonly Stack<TabSnapshot> _recentlyClosed = new();

    public BrowserEnvironment Env { get; }
    public Control Host { get; }
    public RequestBlocker Blocker { get; }

    public IReadOnlyList<Tab> Tabs => _tabs;
    public Tab? ActiveTab { get; private set; }

    /// <summary>Anything the chrome shows changed: list, order, active tab, or a tab's title/state.</summary>
    public event EventHandler? Changed;
    /// <summary>A brand-new tab exists; MainForm wires its per-tab events here.</summary>
    public event EventHandler<Tab>? TabCreated;
    public event EventHandler? LastTabClosed;

    public TabManager(BrowserEnvironment env, Control host, RequestBlocker blocker)
    {
        Env = env;
        Host = host;
        Blocker = blocker;
    }

    public async Task<Tab> CreateTabAsync(string? url, bool activate = true)
    {
        var tab = new Tab(this);
        RegisterTab(tab);
        if (activate)
            await ActivateAsync(tab);
        if (!string.IsNullOrEmpty(url))
            await tab.NavigateAsync(url);
        RaiseChanged();
        return tab;
    }

    /// <summary>Restored session tab: stays a Discarded snapshot (zero processes) until clicked.</summary>
    public Tab AddSnapshotTab(TabSnapshot snapshot)
    {
        var tab = new Tab(this, snapshot);
        RegisterTab(tab);
        RaiseChanged();
        return tab;
    }

    /// <summary>window.open target: an activated tab whose navigation the opener drives.</summary>
    internal async Task<Tab> CreatePopupTabAsync() => await CreateTabAsync(url: null, activate: true);

    private void RegisterTab(Tab tab)
    {
        DebugLog.Write($"RegisterTab url={tab.Url} count_after={_tabs.Count + 1}");
        _tabs.Add(tab);
        tab.Updated += (_, _) => RaiseChanged();
        TabCreated?.Invoke(this, tab);
    }

    public async Task ActivateAsync(Tab tab)
    {
        DebugLog.Write($"ActivateAsync url={tab.Url} inList={_tabs.Contains(tab)}");
        if (!_tabs.Contains(tab))
            return;
        var previous = ActiveTab;
        ActiveTab = tab;
        await tab.ActivateAsync(); // show new first, then hide old: no blank flash
        if (previous is not null && previous != tab)
            previous.Deactivate();
        RaiseChanged();
    }

    public async Task ReactivateAsync(Tab tab)
    {
        if (_tabs.Contains(tab) && ActiveTab == tab)
            await ActivateAsync(tab);
    }

    public Task CloseTabAsync(Tab tab) => RemoveAndDisposeAsync(tab, remember: true, activateInstead: null);

    /// <summary>
    /// Opens a tab on somebody else's behalf, an agent's, and takes it away again if its
    /// page could not start. Returns the tab, or null when it was taken away.
    ///
    /// Taken away without a trace. Closing it the ordinary way left two: the person's
    /// status bar said a tab that no longer existed could not start, and for a tab opened
    /// in front the tab that came forward was whichever sat next to it rather than the one
    /// they had been reading. It is also kept off their Ctrl+Shift+T stack on purpose.
    /// Today it could not reach it anyway, because a tab whose page never started never
    /// recorded a url and about:blank is not remembered, but that is a side effect of
    /// NavigateAsync rather than a decision, and this should not depend on it.
    /// </summary>
    internal async Task<Tab?> OpenOrDiscardAsync(string url, bool activate)
    {
        var previous = ActiveTab;
        var tab = new Tab(this) { ReportsBuildFailure = false };
        bool kept = false;
        try
        {
            RegisterTab(tab);
            if (activate)
                await ActivateAsync(tab);
            await tab.NavigateAsync(url);
            kept = tab.HasView;
        }
        finally
        {
            // Quiet only for the trial, whatever happens to it. A kept tab that stayed
            // quiet would go blank without a word the next time its view could not be
            // rebuilt, which is the silence the failure message exists to end.
            tab.ReportsBuildFailure = true;
        }
        if (kept)
        {
            RaiseChanged();
            return tab;
        }
        await RemoveAndDisposeAsync(tab, remember: false, activateInstead: previous);
        return null;
    }

    private async Task RemoveAndDisposeAsync(Tab tab, bool remember, Tab? activateInstead)
    {
        DebugLog.Write($"CloseTabAsync url={tab.Url}\n{Environment.StackTrace}");
        int index = _tabs.IndexOf(tab);
        if (index < 0)
            return;
        _tabs.RemoveAt(index);
        if (remember && !HomePage.IsHome(tab.Url))
            _recentlyClosed.Push(new TabSnapshot(tab.Url, tab.Title));
        bool wasActive = ActiveTab == tab;
        if (wasActive)
            ActiveTab = null;
        tab.Dispose();

        if (_tabs.Count == 0)
        {
            RaiseChanged();
            LastTabClosed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (wasActive)
        {
            var next = activateInstead is not null && _tabs.Contains(activateInstead)
                ? activateInstead
                : _tabs[Math.Min(index, _tabs.Count - 1)];
            await ActivateAsync(next);
        }
        else
            RaiseChanged();
    }

    /// <summary>How many closed tabs Ctrl+Shift+T can bring back.</summary>
    internal int RecentlyClosedCount => _recentlyClosed.Count;

    /// <summary>
    /// Removes a tab without disposing it: it is moving to another window. The caller
    /// must hand it to a new manager, or the live WebView leaks with no strip showing it.
    /// </summary>
    internal async Task ReleaseAsync(Tab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
            return;
        _tabs.RemoveAt(index);
        tab.DetachOwnerHandlers();
        bool wasActive = ActiveTab == tab;
        if (wasActive)
            ActiveTab = null;
        if (wasActive && _tabs.Count > 0)
            await ActivateAsync(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else
            RaiseChanged();
    }

    /// <summary>Takes over a tab another window released, live WebView and all.</summary>
    internal void Adopt(Tab tab)
    {
        tab.TransferTo(this);
        RegisterTab(tab); // re-wires Updated, and TabCreated re-wires the window's handlers
        RaiseChanged();
    }

    /// <summary>Reorders a tab (drag-to-reorder). Keeps the same tab active.</summary>
    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count || to < 0 || to >= _tabs.Count || from == to)
            return;
        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);
        RaiseChanged();
    }

    public async Task ActivateNextAsync(int direction)
    {
        if (_tabs.Count < 2 || ActiveTab is null)
            return;
        int index = (_tabs.IndexOf(ActiveTab) + direction + _tabs.Count) % _tabs.Count;
        await ActivateAsync(_tabs[index]);
    }

    /// <summary>Ctrl+1..8 → that tab; Ctrl+9 → pass -1 for the last tab (browser convention).</summary>
    public async Task ActivateIndexAsync(int index)
    {
        if (_tabs.Count == 0)
            return;
        if (index < 0 || index >= _tabs.Count)
            index = _tabs.Count - 1;
        await ActivateAsync(_tabs[index]);
    }

    public async Task ReopenClosedAsync()
    {
        if (_recentlyClosed.Count == 0)
            return;
        var snapshot = _recentlyClosed.Pop();
        var tab = AddSnapshotTab(snapshot);
        await ActivateAsync(tab);
    }

    public void DisposeAll()
    {
        foreach (var tab in _tabs)
            tab.Dispose();
        _tabs.Clear();
        ActiveTab = null;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
