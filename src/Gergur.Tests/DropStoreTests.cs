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

/// <summary>
/// What happens when items.json has been edited, truncated or corrupted outside the
/// browser. Nothing on DropItem is nullable, so anything null in there arrived from
/// outside the type system and reaches the UI thread, where a throw takes every tab down.
/// </summary>
public sealed class DropStoreCorruptIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-corrupt-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DropStore LoadWith(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "items.json"), json);
        return new DropStore(_root);
    }

    [Fact]
    public void AFileEntryWithNoStoredNameIsDroppedRatherThanCrashingLater()
    {
        // This one passed the original filter (Id, Kind and Text were all present) and
        // then threw a NullReferenceException inside PathFor, which is reached by opening
        // an item in the drop window: on the UI thread, with nothing to catch it.
        var drop = LoadWith("""
            [{"Id":"a1","Kind":"file","Text":"holiday.jpg","StoredName":null,
              "Size":10,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);

        Assert.Empty(drop.Items);
    }

    [Theory]
    [InlineData("""[{"Id":null,"Kind":"text","Text":"hi","StoredName":"","Size":2,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"}]""")]
    [InlineData("""[{"Id":"a","Kind":null,"Text":"hi","StoredName":"","Size":2,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"}]""")]
    [InlineData("""[{"Id":"a","Kind":"text","Text":null,"StoredName":"","Size":2,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"}]""")]
    [InlineData("""[{"Id":"a","Kind":"text","Text":"hi","StoredName":"","Size":2,"From":null,"AddedUtc":"2026-01-01T00:00:00Z"}]""")]
    [InlineData("[null]")]
    public void AnyNullFieldDisqualifiesTheEntry(string json)
        => Assert.Empty(LoadWith(json).Items);

    [Fact]
    public void AGoodEntryBesideABadOneStillLoads()
    {
        var drop = LoadWith("""
            [{"Id":"bad","Kind":"file","Text":"x","StoredName":null,"Size":1,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"},
             {"Id":"good","Kind":"text","Text":"kept","StoredName":"","Size":4,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);

        Assert.Equal("kept", Assert.Single(drop.Items).Text);
    }

    [Fact]
    public void EveryLoadedItemSurvivesTheCallsTheDropWindowMakes()
    {
        // The window asks PathFor for every row it draws, so whatever survives Load has
        // to answer without throwing whatever the file said.
        var drop = LoadWith("""
            [{"Id":"a1","Kind":"file","Text":"holiday.jpg","StoredName":null,"Size":10,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"},
             {"Id":"a2","Kind":"file","Text":"ok.jpg","StoredName":"a2.jpg","Size":10,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);

        foreach (var item in drop.Items)
        {
            drop.PathFor(item);
            _ = item.IsFile;
        }
    }

    [Fact]
    public void ATruncatedFileLoadsAsAnEmptyDropRatherThanThrowing()
        => Assert.Empty(LoadWith("""[{"Id":"a1","Kind":"fi""").Items);
}

/// <summary>
/// Which extensions a file is allowed to keep on disk. This was a deny list of things
/// Windows runs, and a deny list of those cannot be finished by hand: .msix, .appx, .wsc,
/// .sct and .mst were all missing, and all were stored runnable.
/// </summary>
public sealed class StorableExtensionTests
{
    [Theory]
    [InlineData("holiday.jpg", ".jpg")]
    [InlineData("HOLIDAY.JPG", ".jpg")]
    [InlineData("report.pdf", ".pdf")]
    [InlineData("clip.mp4", ".mp4")]
    [InlineData("photo.heic", ".heic")]
    [InlineData("archive.zip", ".zip")]
    [InlineData("sheet.xlsx", ".xlsx")]
    public void AnOrdinaryFileKeepsItsExtension(string name, string expected)
        => Assert.Equal(expected, DropStore.StorableExtension(name));

    [Theory]
    // the deny list caught these
    [InlineData("setup.exe")]
    [InlineData("run.bat")]
    [InlineData("script.ps1")]
    [InlineData("link.lnk")]
    // and these it did not
    [InlineData("app.msix")]
    [InlineData("app.appx")]
    [InlineData("thing.wsc")]
    [InlineData("thing.sct")]
    [InlineData("patch.mst")]
    [InlineData("old.hlp")]
    // nor anything nobody has thought of yet, which is the point of the change
    [InlineData("thing.somethingnew")]
    [InlineData("no-extension-at-all")]
    public void AnythingNotOnTheListIsStoredAsBin(string name)
        => Assert.Equal(".bin", DropStore.StorableExtension(name));

    [Fact]
    public void TheNameYouSentIsStillWhatYouSee()
    {
        // The cost of the allow list is the file association, not the name: the drop
        // still shows what the phone called it, and so does the download back.
        using var drop = new TempDrop();
        var item = drop.Store.AddFile("installer.msix", new MemoryStream([1, 2, 3]), from: "phone");

        Assert.Equal("installer.msix", item.Text);
        Assert.EndsWith(".bin", item.StoredName);
    }

    private sealed class TempDrop : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-ext-{Guid.NewGuid():N}");
        public DropStore Store { get; }
        public TempDrop() => Store = new DropStore(_root);
        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Housekeeping on open. Files whose entry is gone are photos and documents from the
/// phone sitting on disk with nothing pointing at them, which neither surface can reach
/// and nothing else ever removes.
/// </summary>
public sealed class DropStoreSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-sweep-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string FilesDir => Path.Combine(_root, "files");

    [Fact]
    public void AFileNoEntryRefersToIsRemovedOnOpen()
    {
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(FilesDir, "orphan.jpg"), "bytes");
        // A readable index that lists nothing. Without one there is no way to tell an
        // empty drop from an index that failed to load, and the sweep stands down.
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");

        _ = new DropStore(_root);

        Assert.Empty(Directory.GetFiles(FilesDir));
    }

    [Fact]
    public void FilesThatStillHaveAnEntryAreLeftAlone()
    {
        var first = new DropStore(_root);
        var item = first.AddFile("holiday.jpg", new MemoryStream([1, 2, 3]), from: "phone");
        string kept = first.PathFor(item)!;

        _ = new DropStore(_root);

        Assert.True(File.Exists(kept), "a file with a live entry was swept");
    }

    [Fact]
    public void AFileWhoseEntryTheLoadFilterDroppedIsKept()
    {
        // The opposite of what it looks like it should do, and deliberately so. An entry
        // the filter drops is one we could not read, and its file is then unreferenced
        // only because of that. Sweeping there turns a single unreadable entry into a
        // deleted photo. The worst case is a future build's index opened by this one:
        // one renamed field nulls every entry, and the sweep would empty the drop.
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(FilesDir, "a1.jpg"), "bytes");
        File.WriteAllText(Path.Combine(_root, "items.json"), """
            [{"Id":"a1","Kind":"file","Text":"holiday.jpg","StoredName":null,
              "Size":5,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);

        var drop = new DropStore(_root);

        Assert.Empty(drop.Items);
        Assert.Single(Directory.GetFiles(FilesDir));
    }

    [Fact]
    public void AStaleStagingFileIsCleanedUp()
    {
        // Older than the index means saves have succeeded since it was left behind, so
        // it is dead weight.
        Directory.CreateDirectory(FilesDir);
        string staging = Path.Combine(_root, "items.json.tmp");
        File.WriteAllText(staging, "{ truncated");
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(-1));

        _ = new DropStore(_root);

        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void AStagingFileNewerThanTheIndexIsLeftAlone()
    {
        // A save writes the staging file and then moves it into place. Interrupted
        // between the two, the staging file is the newer of the two indexes, so deleting
        // it throws away exactly what writing it that way existed to protect.
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");
        string staging = Path.Combine(_root, "items.json.tmp");
        File.WriteAllText(staging, "[]");
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(1));

        _ = new DropStore(_root);

        Assert.True(File.Exists(staging));
    }

    [Fact]
    public void AStagingFileIsLeftAloneWhenTheRealIndexIsMissing()
    {
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(_root, "items.json.tmp"), "[]");

        _ = new DropStore(_root);

        Assert.True(File.Exists(Path.Combine(_root, "items.json.tmp")));
    }

    [Fact]
    public void SavingLeavesNoStagingFileBehind()
    {
        var drop = new DropStore(_root);
        drop.AddText("hello", from: "pc");

        Assert.False(File.Exists(Path.Combine(_root, "items.json.tmp")));
        Assert.True(File.Exists(Path.Combine(_root, "items.json")));
    }
}

/// <summary>
/// The sweep against a broken index. An index that will not parse loads as an empty
/// list, which is indistinguishable from a drop with nothing in it, so a sweep that
/// trusts the list would delete every file on the one occasion they cannot be listed.
/// </summary>
public sealed class DropStoreSweepSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-sweepsafe-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string FilesDir => Path.Combine(_root, "files");

    private void GiveItFiles()
    {
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(FilesDir, "a1.jpg"), "a holiday photo");
        File.WriteAllText(Path.Combine(FilesDir, "a2.pdf"), "a document");
    }

    [Fact]
    public void ACorruptIndexDoesNotTakeTheFilesWithIt()
    {
        GiveItFiles();
        File.WriteAllText(Path.Combine(_root, "items.json"), "{ this is not the file it was");

        var drop = new DropStore(_root);

        Assert.Empty(drop.Items);
        Assert.Equal(2, Directory.GetFiles(FilesDir).Length);
    }

    [Fact]
    public void AMissingIndexDoesNotTakeTheFilesWithIt()
    {
        // The index can go without the files going: a failed write, a restore, a sync
        // client. Deleting them is not recovery, it is the second half of the loss.
        GiveItFiles();

        var drop = new DropStore(_root);

        Assert.Empty(drop.Items);
        Assert.Equal(2, Directory.GetFiles(FilesDir).Length);
    }

    [Fact]
    public void AnEmptyIndexThatReallyIsEmptyStillSweeps()
    {
        // The difference that matters: this index parsed, and it says there is nothing.
        GiveItFiles();
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");

        var drop = new DropStore(_root);

        Assert.Empty(drop.Items);
        Assert.Empty(Directory.GetFiles(FilesDir));
    }
}
