using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The main menu is rebuilt from scratch every time it is opened, and an attempt to
/// dispose the items it was replacing threw on the very first rebuild: disposing a
/// ToolStripItem removes it from its owner's collection, so disposing them in a foreach
/// over that collection modifies what it is enumerating. The menu never appeared, the
/// exception escaped into the message loop, and everything reachable only from the menu
/// went with it.
///
/// These pin WinForms' own behaviour rather than MainForm's: the rebuild here is a
/// miniature of RebuildMenuItems, not a call into it, because building the real menu
/// needs a live AppSession. So they record what is safe to write and what is not; they
/// would not catch MainForm going back to the unsafe form on its own.
///
/// Which is worth knowing, because MainForm does now dispose the tree it replaces, and
/// the safe shape it uses is the one below.
/// </summary>
public sealed class MenuRebuildTests
{
    /// <summary>WinForms wants an STA thread; no message loop runs, so this cannot hang.</summary>
    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }

    /// <summary>What MainForm.RebuildMenuItems does to the collection, in miniature.</summary>
    private static ToolStripItem[] Rebuild(ContextMenuStrip menu, int items)
    {
        var replaced = menu.Items.Cast<ToolStripItem>().ToArray();
        menu.Items.Clear();
        foreach (var item in replaced)
            item.Dispose();

        for (int i = 0; i < items; i++)
            menu.Items.Add(new ToolStripMenuItem($"item {i}"));
        return replaced;
    }

    [Fact]
    public void RebuildingAPopulatedMenuDoesNotThrow()
    {
        OnSta(() =>
        {
            using var menu = new ContextMenuStrip();

            Rebuild(menu, 25);                       // the first build, into an empty menu
            Assert.Equal(25, menu.Items.Count);

            Rebuild(menu, 25);                       // the first open, over a full one
            Rebuild(menu, 25);                       // and again

            Assert.Equal(25, menu.Items.Count);
        });
    }

    [Fact]
    public void RebuildingReclaimsTheSubmenusItReplaces()
    {
        // What disposal actually buys, measured rather than assumed: ToolStripItem's own
        // IsDisposed does not flip, but the drop-down hanging off it is disposed, and
        // that is the object holding resources. Items.Clear on its own leaves it live,
        // which is one abandoned submenu per menu open for the life of the process.
        OnSta(() =>
        {
            using var menu = new ContextMenuStrip();

            var withSubmenu = new ToolStripMenuItem("parent");
            withSubmenu.DropDownItems.Add(new ToolStripMenuItem("child"));
            menu.Items.Add(withSubmenu);
            var submenu = withSubmenu.DropDown;
            Assert.False(submenu.IsDisposed);

            Rebuild(menu, 3);

            Assert.True(submenu.IsDisposed, "the replaced submenu was detached but left alive");
        });
    }

    [Fact]
    public void ClearingAloneLeavesTheSubmenuAlive()
    {
        // The behaviour the rebuild above exists to avoid, kept so the difference is on
        // the record rather than in somebody's memory.
        OnSta(() =>
        {
            using var menu = new ContextMenuStrip();
            var withSubmenu = new ToolStripMenuItem("parent");
            withSubmenu.DropDownItems.Add(new ToolStripMenuItem("child"));
            menu.Items.Add(withSubmenu);
            var submenu = withSubmenu.DropDown;

            menu.Items.Clear();

            Assert.False(submenu.IsDisposed);
        });
    }

    [Fact]
    public void DisposingTheMenuTakesItsItemsWithIt()
    {
        OnSta(() =>
        {
            var menu = new ContextMenuStrip();
            Rebuild(menu, 5);

            menu.Dispose();

            Assert.True(menu.IsDisposed);
            Assert.Empty(menu.Items);
        });
    }

    [Fact]
    public void DisposingItemsWhileEnumeratingThemIsWhatBroke()
    {
        // Kept as the record of why the obvious way is wrong, so nobody writes it again.
        OnSta(() =>
        {
            using var menu = new ContextMenuStrip();
            Rebuild(menu, 4);

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (ToolStripItem item in menu.Items)
                    item.Dispose();
            });
        });
    }
}
