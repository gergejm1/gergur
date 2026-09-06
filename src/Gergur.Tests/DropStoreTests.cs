using System.Text;
using Gergur.App;
using Gergur.Data;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The shared drop between phone and PC. The name of an uploaded file comes from the
/// phone, so the parts that decide where bytes land get the most attention here.
/// </summary>
public sealed class DropStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-drop-{Guid.NewGuid():N}");
    private readonly DropStore _drop;

    public DropStoreTests() => _drop = new DropStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Stream Bytes(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    // ---------------------------------------------------------------- classification

    [Theory]
    [InlineData("https://example.com/x")]
    [InlineData("http://192.168.0.20:24003/")]
    public void AWebAddressIsALink(string text) => Assert.Equal("link", DropStore.Classify(text));

    [Theory]
    [InlineData("just a message")]
    [InlineData("check https://example.com out")]   // a url inside prose is still prose
    [InlineData("file:///C:/secret.txt")]           // only http and https
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void AnythingElseIsAMessage(string text) => Assert.Equal("text", DropStore.Classify(text));

    // ---------------------------------------------------------------- untrusted filenames

    [Theory]
    [InlineData("photo.JPG", ".jpg")]
    [InlineData("report.pdf", ".pdf")]
    [InlineData("archive.tar.gz", ".gz")]
    public void AnExtensionIsKeptAndLowercased(string name, string expected)
        => Assert.Equal(expected, DropStore.SafeExtension(name));

    [Theory]
    [InlineData("../../../evil")]
    [InlineData("no-extension")]
    [InlineData("trailing.")]
    [InlineData("weird.a b")]
    [InlineData("bad.<>|")]
    [InlineData("long.thisistoolongtobeanextension")]
    public void AnythingThatIsNotPlainlyAnExtensionIsDropped(string name)
        => Assert.Equal("", DropStore.SafeExtension(name));

    [Theory]
    [InlineData(@"C:\Users\me\secret.txt", "secret.txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("folder/photo.jpg", "photo.jpg")]
    [InlineData("   ", "file")]
    public void OnlyTheLeafOfASuppliedNameIsShown(string supplied, string expected)
        => Assert.Equal(expected, DropStore.DisplayName(supplied));

    [Fact]
    public void AHostileNameCannotSteerWhereTheFileLands()
    {
        var item = _drop.AddFile(@"..\..\..\Windows\System32\evil.exe", Bytes("x"), "phone");

        string path = _drop.PathFor(item)!;
        // The stored name is our id plus the extension, and it stays inside the drop.
        Assert.Equal(_drop.FilesDir, Path.GetDirectoryName(Path.GetFullPath(path)));
        Assert.DoesNotContain("..", item.StoredName);
        Assert.Equal("evil.exe", item.Text); // shown to the user, not used as a path
        Assert.True(File.Exists(path));
    }

    // ---------------------------------------------------------------- items

    [Fact]
    public void ItemsComeBackNewestFirst()
    {
        _drop.AddText("first", "pc");
        _drop.AddText("second", "phone");

        Assert.Equal(["second", "first"], _drop.Items.Select(i => i.Text));
    }

    [Fact]
    public void AFileRecordsItsSizeAndIsReadableBack()
    {
        var item = _drop.AddFile("notes.txt", Bytes("hello drop"), "phone");

        Assert.True(item.IsFile);
        Assert.Equal("phone", item.From);
        Assert.Equal(10, item.Size);
        Assert.Equal("hello drop", File.ReadAllText(_drop.PathFor(item)!));
    }

    [Fact]
    public void AMessageHasNoFileOnDisk() => Assert.Null(_drop.PathFor(_drop.AddText("hi", "pc")));

    [Fact]
    public void RemovingAnItemDeletesItsFile()
    {
        var item = _drop.AddFile("a.txt", Bytes("x"), "phone");
        string path = _drop.PathFor(item)!;

        _drop.Remove(item.Id);

        Assert.False(File.Exists(path));
        Assert.Empty(_drop.Items);
    }

    [Fact]
    public void ClearingRemovesEveryStoredFile()
    {
        string a = _drop.PathFor(_drop.AddFile("a.txt", Bytes("x"), "phone"))!;
        string b = _drop.PathFor(_drop.AddFile("b.txt", Bytes("y"), "pc"))!;

        _drop.Clear();

        Assert.Empty(_drop.Items);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void ItemsSurviveARestart()
    {
        _drop.AddText("https://example.com", "phone");
        _drop.AddFile("k.txt", Bytes("keep"), "pc");

        var reopened = new DropStore(_root);

        Assert.Equal(2, reopened.Items.Count);
        Assert.Equal("keep", File.ReadAllText(reopened.PathFor(reopened.Items[0])!));
    }

    [Fact]
    public void FindReturnsNothingForAnUnknownId() => Assert.Null(_drop.Find("nope"));

    [Fact]
    public void AddingRaisesTheArrivalEventWithTheItem()
    {
        DropItem? seen = null;
        _drop.ItemAdded += (_, item) => seen = item;

        _drop.AddText("ping", "phone");

        Assert.Equal("ping", seen?.Text);
        Assert.Equal("phone", seen?.From);
    }
}

/// <summary>The pairing address has to be a private one, or it is the wrong interface.</summary>
public sealed class DropServerAddressTests
{
    [Theory]
    [InlineData("192.168.0.20")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    public void PrivateRangesAreAccepted(string ip)
        => Assert.True(DropServer.IsPrivate(System.Net.IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]  // just outside the 172.16/12 block
    [InlineData("172.15.0.1")]
    [InlineData("1.1.1.1")]
    public void PublicAddressesAreNot(string ip)
        => Assert.False(DropServer.IsPrivate(System.Net.IPAddress.Parse(ip)));
}

/// <summary>
/// Regression guards for defects review found in the drop, each of which was proven to
/// happen rather than merely reasoned about.
/// </summary>
public sealed class DropSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-drop-safe-{Guid.NewGuid():N}");
    private readonly DropStore _drop;

    public DropSafetyTests() => _drop = new DropStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ARightToLeftOverrideCannotDisguiseAnExecutable()
    {
        // "holiday<RLO>fdp.exe" renders as "holidayexe.pdf", so the user sees a document
        // and launches a program. The override must not survive into the display name.
        string disguised = "holiday\u202Efdp.exe";

        string shown = DropStore.DisplayName(disguised);

        Assert.DoesNotContain('\u202E', shown);
        Assert.Equal("holidayfdp.exe", shown);
    }

    [Theory]
    [InlineData("\u202Aleft")]
    [InlineData("\u2066isolate")]
    [InlineData("\u200Emark")]
    [InlineData("bell\u0007")]
    public void OtherDirectionAndControlCharactersAreStrippedToo(string name)
        => Assert.DoesNotContain(DropStore.DisplayName(name), c => char.IsControl(c) || c > '\u2000');

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("script.PS1")]
    [InlineData("shortcut.lnk")]
    [InlineData("payload.hta")]
    [InlineData("thing.scr")]
    [InlineData("installer.msi")]
    [InlineData("keys.reg")]
    public void ProgramsAreRecognisedSoAClickWillNotRunThem(string name)
        => Assert.True(DropStore.IsExecutable(name), $"{name} should be treated as executable");

    [Theory]
    [InlineData("holiday.jpg")]
    [InlineData("report.pdf")]
    [InlineData("notes.txt")]
    [InlineData("clip.mp4")]
    public void OrdinaryDocumentsStillOpen(string name)
        => Assert.False(DropStore.IsExecutable(name), $"{name} should open normally");

    [Fact]
    public async Task ConcurrentSendsDoNotLoseOrNullEntries()
    {
        // Uploads arrive on request threads while the UI thread reads the list. Before
        // the lock, 240 concurrent adds produced null slots and lost ids, and a reader
        // enumerating mid-insert threw on the UI thread, taking the browser with it.
        const int writers = 8, each = 30;
        var readerFailure = (Exception?)null;
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (var item in _drop.Items)
                        _ = item.Text.Length; // NREs if a null ever lands in the list
                }
            }
            catch (Exception ex) { readerFailure = ex; }
        });

        Parallel.For(0, writers, w =>
        {
            for (int i = 0; i < each; i++)
                _drop.AddText($"w{w}-{i}", "phone");
        });
        stop.Cancel();
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(readerFailure);
        var items = _drop.Items;
        Assert.Equal(writers * each, items.Count);
        Assert.DoesNotContain(items, i => i is null);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void AFileFromThePhoneIsMarkedAsComingFromTheNetwork()
    {
        var item = _drop.AddFile("report.pdf", new MemoryStream([1, 2, 3]), "phone");

        // The mark-of-the-web is what makes SmartScreen and Office treat it with suspicion.
        string zone = _drop.PathFor(item)! + ":Zone.Identifier";
        Assert.True(File.Exists(zone), "no Zone.Identifier stream was written");
        Assert.Contains("ZoneId=3", File.ReadAllText(zone));
    }
}
