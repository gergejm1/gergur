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

    public async Task CloseTabAsync(Tab tab)
    {
        DebugLog.Write($"CloseTabAsync url={tab.Url}\n{Environment.StackTrace}");
        int index = _tabs.IndexOf(tab);
        if (index < 0)
            return;
        _tabs.RemoveAt(index);
        if (!HomePage.IsHome(tab.Url))
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
            await ActivateAsync(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else
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
