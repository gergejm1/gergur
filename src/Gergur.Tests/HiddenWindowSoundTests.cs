using System.Text.RegularExpressions;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// Nothing from an agent's hidden window is heard: the person asked never to hear a video an
/// agent plays there, even one it needs to play, until they show the window.
/// </summary>
public sealed class HiddenWindowSoundTests
{
    private static string Code(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gergur.slnx")))
            dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("Gergur.slnx not found above the test binaries");
        return Regex.Replace(File.ReadAllText(Path.Combine(root, "src", "Gergur", relative)), @"//[^\n]*", "");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]    // muted by choice
    [InlineData(false, true, true)]    // in a hidden window
    [InlineData(true, true, true)]
    public void TheEngineIsSilentWhenMutedOrHidden(bool muted, bool hidden, bool silent)
        => Assert.Equal(silent, Tab.EngineMuted(muted, hidden));

    [Fact]
    public void ATabFollowsItsWindowAndKeepsItsOwnChoice()
    {
        bool hidden = true;
        var manager = new TabManager(null!, new Control(), null!) { IsHiddenWindow = () => hidden };
        var tab = manager.AddSnapshotTab(new TabSnapshot("https://video.example/", "Video"));

        manager.RefreshSound();
        Assert.True(tab.SilencedByWindow);
        Assert.True(tab.EngineIsToldMuted);
        Assert.False(tab.IsMuted);           // silenced, not muted: the strip shows no mute

        tab.IsMuted = false;                 // unmuting while hidden is still not heard
        Assert.True(tab.EngineIsToldMuted);

        hidden = false;                      // the person shows the window
        manager.RefreshSound();
        Assert.False(tab.SilencedByWindow);
        Assert.False(tab.EngineIsToldMuted);

        tab.IsMuted = true;                  // their Ctrl+M
        hidden = true;                       // hidden again
        manager.RefreshSound();
        Assert.True(tab.EngineIsToldMuted);
        hidden = false;                      // and shown
        manager.RefreshSound();
        Assert.True(tab.IsMuted);            // still the choice they made
        Assert.True(tab.EngineIsToldMuted);
        Assert.False(tab.SilencedByWindow);
    }

    [Fact]
    public async Task ATabSetAsideForADownloadFollowsItsWindowToo()
    {
        bool hidden = false;
        var manager = new TabManager(null!, new Control(), null!) { IsHiddenWindow = () => hidden };
        var opener = manager.AddSnapshotTab(new TabSnapshot("https://files.example/", "Files"));
        var popup = await manager.CreatePopupTabAsync(opener);
        var download = new TaskCompletionSource();
        await manager.SetAsideAsync(popup, download.Task);
        Assert.True(manager.IsSetAside(popup));
        Assert.DoesNotContain(popup, manager.Tabs);

        hidden = true;
        manager.RefreshSound();
        Assert.True(popup.EngineIsToldMuted);  // its page is still live and could still play

        hidden = false;
        manager.RefreshSound();
        Assert.False(popup.EngineIsToldMuted);
        download.SetResult();
    }

    [Fact]
    public void EveryWayAWindowIsHiddenOrShownReachesItsTabs()
    {
        string form = Code("UI/MainForm.cs");
        string tab = Code("Tabs/Tab.cs");
        // The window says whether it is hidden; every change of that tells each tab.
        Assert.Contains("Tabs.IsHiddenWindow = () => _hiddenForAgent;", form);
        Assert.Matches(@"internal bool HiddenForAgent\s*\{\s*get => _hiddenForAgent;\s*private set\s*\{\s*_hiddenForAgent = value;\s*Tabs\?\.RefreshSound\(\);", form);
        // No other writes to the field than the setter, so no change can skip it.
        Assert.Single(Regex.Matches(form, @"_hiddenForAgent = "));
        // A view starts silent in a hidden window, before anything is navigated.
        Assert.Matches(@"_silencedByWindow = _owner\.IsHiddenWindow\(\);\s*if \(EngineIsToldMuted\)\s*core\.IsMuted = true;", tab);
        // A tab moved into or out of a hidden window follows its new one.
        Assert.Matches(@"void TransferTo\(TabManager newOwner\)\s*\{[^}]*RefreshSound\(\);", tab);
        // The engine is only ever told the combination, and IsMuted reports the choice alone.
        Assert.Single(Regex.Matches(tab, @"core\.IsMuted = (?!true;)"));
        Assert.Contains("core.IsMuted = EngineIsToldMuted;", tab);
        Assert.Matches(@"public bool IsMuted\s*\{\s*get => _muted;", tab);
    }
}
