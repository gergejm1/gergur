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

    private string OrphansDir => Path.Combine(_root, "orphans");

    [Fact]
    public void AFileNoEntryRefersToIsMovedAsideOnOpen()
    {
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(FilesDir, "orphan.jpg"), "bytes");
        // A readable index that lists nothing. Without one there is no way to tell an
        // empty drop from an index that failed to load, and the sweep stands down.
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");

        _ = new DropStore(_root);

        // Out of the way, but not gone: tidying up must never be the thing that loses a
        // photo, whatever the judgement above it got wrong.
        Assert.Empty(Directory.GetFiles(FilesDir));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(OrphansDir, "orphan.jpg")));
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

    /// <summary>An entry as the index stores it, with a file beside it.</summary>
    private string EntryWithFile(string id, string stored)
    {
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(FilesDir, stored), "photo bytes");
        return $$"""
            {"Id":"{{id}}","Kind":"file","Text":"holiday.jpg","StoredName":"{{stored}}",
             "Size":11,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}
            """;
    }

    [Fact]
    public void AnInterruptedSaveIsFinishedRatherThanDiscarded()
    {
        // A save writes the staging file and then moves it. Interrupted between the two
        // (a crash, or the sharing violation from a backup or a scanner that SaveLocked
        // swallows), the staging file is the newer of the two indexes and the only record
        // of everything added in that session. Reading the older one and sweeping against
        // it deleted exactly those photos, while carefully keeping a staging file that
        // nothing ever read.
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");
        string staging = Path.Combine(_root, "items.json.tmp");
        File.WriteAllText(staging, "[" + EntryWithFile("a1", "a1.jpg") + "]");
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(1));

        var drop = new DropStore(_root);

        Assert.Equal("holiday.jpg", Assert.Single(drop.Items).Text);
        Assert.Single(Directory.GetFiles(FilesDir));
        Assert.False(File.Exists(staging), "the move should have been completed");
    }

    [Fact]
    public void AnInterruptedFirstSaveIsFinishedToo()
    {
        // No items.json at all: the very first save got as far as the staging file.
        string staging = Path.Combine(_root, "items.json.tmp");
        Directory.CreateDirectory(_root);
        File.WriteAllText(staging, "[" + EntryWithFile("a1", "a1.jpg") + "]");

        var drop = new DropStore(_root);

        Assert.Single(drop.Items);
        Assert.Single(Directory.GetFiles(FilesDir));
    }

    [Fact]
    public void AHalfWrittenStagingFileIsDiscarded()
    {
        // Interrupted during the write: it will not parse, so it is not an index by any
        // reading and cannot stand in for one. Keeping it was worse than useless, because
        // the next save wrote over it anyway while this launch stood down over it, so the
        // drop stayed in a degraded state that nothing could clear.
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");
        string staging = Path.Combine(_root, "items.json.tmp");
        File.WriteAllText(staging, """[{"Id":"a1","Kind":"fi""");
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(1));

        var drop = new DropStore(_root);

        Assert.Empty(drop.Items);
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void AStagingIndexThisBuildCannotUnderstandIsKeptAndNothingIsWrittenOver()
    {
        // It parses, but an entry is not usable: most likely an index written by a later
        // build than this one. Neither file may be replaced, and the photo stays put.
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(FilesDir, "a1.jpg"), "photo bytes");
        string index = Path.Combine(_root, "items.json");
        File.WriteAllText(index, "[]");
        string staging = index + ".tmp";
        File.WriteAllText(staging, """
            [{"Id":"a1","Kind":"file","Text":"holiday.jpg","StoredName":null,
              "Size":11,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(1));

        var drop = new DropStore(_root);
        drop.AddText("work from this session", from: "pc");

        Assert.True(File.Exists(staging), "the newer index was thrown away");
        Assert.Equal("[]", File.ReadAllText(index));
        Assert.True(File.Exists(index + ".recovered"));
        Assert.Single(Directory.GetFiles(FilesDir));
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
    private string OrphansDir => Path.Combine(_root, "orphans");

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
        // Set aside rather than deleted, so even a wrong judgement here costs nothing.
        Assert.Equal(2, Directory.GetFiles(OrphansDir).Length);
    }
}

/// <summary>
/// What happens when the index cannot be read at the moment the drop opens: a virus
/// scanner or a backup holding the file, most often, which looks exactly like corruption
/// from here. Reading it as an empty drop is survivable. Writing that empty drop back
/// over it is not, and used to end with every file quarantined on the launch after.
/// </summary>
public sealed class DropStoreLockedIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-locked-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "items.json");
    private string FilesDir => Path.Combine(_root, "files");

    /// <summary>A drop with one photo in it, closed and left on disk.</summary>
    private void GiveItAPhoto()
    {
        var first = new DropStore(_root);
        first.AddFile("holiday.jpg", new MemoryStream([1, 2, 3]), from: "phone");
    }

    [Fact]
    public void AnIndexThatCannotBeReadIsNotWrittenOver()
    {
        GiveItAPhoto();
        string original = File.ReadAllText(IndexPath);

        // Held open the way a scanner holds it: the read throws, so the list loads empty.
        using (File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var drop = new DropStore(_root);
            Assert.Empty(drop.Items);
            drop.AddText("something from this session", from: "pc");
        }

        Assert.Equal(original, File.ReadAllText(IndexPath));
        Assert.Contains("holiday.jpg", File.ReadAllText(IndexPath));
    }

    [Fact]
    public void WorkFromThatSessionIsKeptBesideIt()
    {
        GiveItAPhoto();

        using (File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var drop = new DropStore(_root);
            drop.AddText("something from this session", from: "pc");
        }

        string recovered = Path.Combine(_root, "items.json.recovered");
        Assert.True(File.Exists(recovered), "the session's own items were dropped on the floor");
        Assert.Contains("something from this session", File.ReadAllText(recovered));
    }

    [Fact]
    public void ThePhotoIsStillThereOnTheNextLaunch()
    {
        // The cascade this guards: read fails, empty list is saved over the index, the
        // launch after that sees an intact one-entry index and treats every file as an
        // orphan. Both halves are now closed, so the photo survives the whole sequence.
        GiveItAPhoto();

        using (File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var drop = new DropStore(_root);
            drop.AddText("something from this session", from: "pc");
        }

        var reopened = new DropStore(_root);

        Assert.Equal("holiday.jpg", Assert.Single(reopened.Items).Text);
        Assert.Single(Directory.GetFiles(FilesDir));
    }
}

/// <summary>
/// The two failures compounding: a leftover staging file from an earlier crash, and the
/// index briefly unreadable at the moment the drop opens. Each is survivable alone. Taken
/// together they used to destroy the index entry for a photo and set the photo aside on
/// the launch after, because the path that stands down over the staging file did not
/// report that the index had also failed to read.
/// </summary>
public sealed class DropStoreCompoundFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-compound-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "items.json");
    private string FilesDir => Path.Combine(_root, "files");
    private string OrphansDir => Path.Combine(_root, "orphans");

    [Fact]
    public void ACrashAndALockedIndexTogetherStillLeaveThePhotoAndItsEntry()
    {
        // Session one: a photo arrives from the phone.
        new DropStore(_root).AddFile("holiday.jpg", new MemoryStream([1, 2, 3]), from: "phone");
        string original = File.ReadAllText(IndexPath);

        // An earlier crash left a staging file that will not parse.
        string staging = IndexPath + ".tmp";
        File.WriteAllText(staging, """[{"Id":"a1","Kind":"fi""");
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(1));

        // Session two opens while something else holds the index, then does some work.
        using (File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var drop = new DropStore(_root);
            Assert.Empty(drop.Items);
            drop.AddText("a note typed this session", from: "pc");
        }

        // The index is untouched, and this session's work is beside it.
        Assert.Equal(original, File.ReadAllText(IndexPath));
        Assert.Contains("holiday.jpg", File.ReadAllText(IndexPath));
        Assert.True(File.Exists(IndexPath + ".recovered"));

        // Session three, with nothing holding anything: the photo is still there.
        var reopened = new DropStore(_root);

        Assert.Equal("holiday.jpg", Assert.Single(reopened.Items).Text);
        Assert.Single(Directory.GetFiles(FilesDir));
        Assert.False(Directory.Exists(OrphansDir) && Directory.GetFiles(OrphansDir).Length > 0,
            "the photo was set aside as an orphan");
    }

    [Fact]
    public void AStagingFileWrittenInTheSameTickAsTheIndexIsStillRecovered()
    {
        // File timestamps have about a millisecond of resolution, and two writes in a row
        // regularly land in the same tick, so "strictly newer" missed the case this
        // recovery exists for on roughly half the attempts.
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(IndexPath, "[]");
        string staging = IndexPath + ".tmp";
        File.WriteAllText(staging, """
            [{"Id":"a1","Kind":"text","Text":"kept","StoredName":"","Size":4,
              "From":"pc","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);
        var sameMoment = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(IndexPath, sameMoment);
        File.SetLastWriteTimeUtc(staging, sameMoment);

        var drop = new DropStore(_root);

        Assert.Equal("kept", Assert.Single(drop.Items).Text);
    }
}

/// <summary>
/// A photo that arrives while the index cannot be read. Its entry goes to the recovery
/// file, which is the only record of the name its file on disk was given, so a later
/// launch that ignores that file treats the photo as an orphan and sets it aside.
/// </summary>
public sealed class DropStoreRecoveredNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-recovered-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "items.json");
    private string FilesDir => Path.Combine(_root, "files");
    private string OrphansDir => Path.Combine(_root, "orphans");

    [Fact]
    public void APhotoThatArrivedWhileTheIndexWasLockedIsNotSetAside()
    {
        new DropStore(_root).AddFile("holiday.jpg", new MemoryStream([1, 2, 3]), from: "phone");

        // The phone sends another one while a scanner holds the index.
        using (File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var drop = new DropStore(_root);
            drop.AddFile("birthday.jpg", new MemoryStream([4, 5, 6]), from: "phone");
        }

        // Next launch, nothing holding anything: both files are still in the drop folder.
        _ = new DropStore(_root);

        Assert.Equal(2, Directory.GetFiles(FilesDir).Length);
        Assert.False(Directory.Exists(OrphansDir) && Directory.GetFiles(OrphansDir).Length > 0,
            "a photo the recovery file names was set aside anyway");
    }

    [Fact]
    public void SetAsideCountReportsWhatIsActuallyThere()
    {
        // Counted when the store opens rather than on every read, so this checks what a
        // launch finds: the drop window asks for it on every item the phone sends, and
        // three filesystem calls per arrival on the UI thread is not the way to answer.
        Assert.Equal(0, new DropStore(_root).SetAsideCount);

        Directory.CreateDirectory(OrphansDir);
        File.WriteAllText(Path.Combine(OrphansDir, "a1.jpg"), "x");
        File.WriteAllText(Path.Combine(OrphansDir, "a2.jpg"), "y");
        Assert.Equal(2, new DropStore(_root).SetAsideCount);

        File.WriteAllText(IndexPath + ".recovered", "[]");
        Assert.Equal(3, new DropStore(_root).SetAsideCount);
    }

    [Fact]
    public void WritingAListBesideTheIndexIsNoticedWithoutReopening()
    {
        // The one case that changes mid-session: a save that cannot touch the index
        // writes beside it, and the window has to start saying so.
        new DropStore(_root).AddFile("holiday.jpg", new MemoryStream([1, 2, 3]), from: "phone");

        using (File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var drop = new DropStore(_root);
            Assert.Equal(0, drop.SetAsideCount);

            drop.AddText("work from this session", from: "pc");

            Assert.Equal(1, drop.SetAsideCount);
        }
    }
}

/// <summary>
/// An index this build can read but not use every entry of. It stands down from sweeping,
/// because a file whose entry it dropped only looks unreferenced. It must still be
/// writable, or the user can never delete anything again for the life of that install.
/// </summary>
public sealed class DropStorePartialIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-partial-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "items.json");

    [Fact]
    public void DeletingSomethingStillSticks()
    {
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        File.WriteAllText(IndexPath, """
            [{"Id":"bad1","Kind":"text","Text":null,"StoredName":"","Size":1,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"},
             {"Id":"good1","Kind":"text","Text":"delete me","StoredName":"","Size":9,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);

        var drop = new DropStore(_root);
        Assert.Equal("delete me", Assert.Single(drop.Items).Text);

        drop.Remove("good1");

        // Written where the next launch will read it, not into a side file: an item that
        // comes back after being deleted, and cannot be deleted again, is a ghost.
        Assert.False(File.Exists(IndexPath + ".recovered"));
        Assert.Empty(new DropStore(_root).Items);
    }
}

/// <summary>
/// An index this build reads but cannot use every entry of: a field renamed in a later
/// build, opened by an older one. The entries it cannot use are not written back, so the
/// original is the only record of them, and the first save used to replace it outright.
/// </summary>
public sealed class DropStoreSupersededIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-superseded-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "items.json");
    private string FilesDir => Path.Combine(_root, "files");
    private string OrphansDir => Path.Combine(_root, "orphans");

    /// <summary>Twenty photos this build cannot read the names of, and their files.</summary>
    private void GiveItAnIndexFromTheFuture()
    {
        Directory.CreateDirectory(FilesDir);
        var entries = Enumerable.Range(1, 20).Select(n =>
        {
            File.WriteAllText(Path.Combine(FilesDir, $"a{n}.jpg"), "photo bytes");
            return $$"""
                {"Id":"a{{n}}","Kind":"file","Text":null,"StoredName":"a{{n}}.jpg",
                 "Size":11,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}
                """;
        });
        File.WriteAllText(IndexPath, "[" + string.Join(",", entries) + "]");
    }

    [Fact]
    public void TheOriginalIsKeptBeforeItIsWrittenOver()
    {
        GiveItAnIndexFromTheFuture();
        string original = File.ReadAllText(IndexPath);

        var drop = new DropStore(_root);
        Assert.Empty(drop.Items);
        drop.AddText("a note typed this session", from: "pc");

        // The note is saved where the next launch will read it, and the twenty entries
        // this build could not read are still on disk in full.
        Assert.Contains("a note typed this session", File.ReadAllText(IndexPath));
        Assert.Equal(original, File.ReadAllText(IndexPath + ".superseded"));
    }

    [Fact]
    public void ThePhotosAreNotSetAsideOnTheLaunchAfter()
    {
        GiveItAnIndexFromTheFuture();

        var first = new DropStore(_root);
        first.AddText("a note typed this session", from: "pc");

        // Launch two sees an intact one-entry index, so it sweeps. The kept copy is what
        // stops it treating twenty photos as files nothing refers to.
        _ = new DropStore(_root);

        Assert.Equal(20, Directory.GetFiles(FilesDir).Length);
        Assert.False(Directory.Exists(OrphansDir) && Directory.GetFiles(OrphansDir).Length > 0);
    }

    [Fact]
    public void TheCopyIsMadeOncePerSessionAndNotOnEverySave()
    {
        GiveItAnIndexFromTheFuture();

        var drop = new DropStore(_root);
        drop.AddText("first", from: "pc");
        drop.AddText("second", from: "pc");
        drop.AddText("third", from: "pc");

        Assert.Single(Directory.GetFiles(_root, "items.json.superseded*"));
    }

    [Fact]
    public void ASecondPartialIndexIsAlsoKept()
    {
        // The guard used to be "does a .superseded already exist", which is a latch that
        // never reopens: the second time this ever happened to a profile, the index was
        // overwritten with nothing kept, because round one had claimed the name.
        GiveItAnIndexFromTheFuture();
        new DropStore(_root).AddText("round one", from: "pc");

        GiveItAnIndexFromTheFuture();
        new DropStore(_root).AddText("round two", from: "pc");

        var copies = Directory.GetFiles(_root, "items.json.superseded*");
        Assert.Equal(2, copies.Length);
        Assert.All(copies, path => Assert.Contains("\"StoredName\":\"a1.jpg\"", File.ReadAllText(path)));
    }
}

/// <summary>The quarantine folder, which must not become its own way to lose things.</summary>
public sealed class DropStoreOrphanFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-orphanage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string FilesDir => Path.Combine(_root, "files");
    private string OrphansDir => Path.Combine(_root, "orphans");

    private void SetAsideOne(string name, string content)
    {
        Directory.CreateDirectory(FilesDir);
        File.WriteAllText(Path.Combine(FilesDir, name), content);
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");
        _ = new DropStore(_root);
    }

    [Fact]
    public void TwoFilesWithTheSameNameBothSurvive()
    {
        SetAsideOne("a1.jpg", "the first one");
        SetAsideOne("a1.jpg", "the second one");

        var kept = Directory.GetFiles(OrphansDir).Select(File.ReadAllText).ToList();

        Assert.Equal(2, kept.Count);
        Assert.Contains("the first one", kept);
        Assert.Contains("the second one", kept);
    }

    [Fact]
    public void AnOldPhotoIsSetAsideRatherThanDeletedOutright()
    {
        // The clock the prune reads is when the file was set aside, not when its bytes
        // were written. A move keeps the original time, so without stamping it, a photo
        // that arrived three months ago and became unreferenced today was quarantined
        // and deleted in the same pass, with nothing shown for it.
        Directory.CreateDirectory(FilesDir);
        string arrived = Path.Combine(FilesDir, "a1.jpg");
        File.WriteAllText(arrived, "a holiday photo");
        File.SetLastWriteTimeUtc(arrived, DateTime.UtcNow.AddDays(-90));
        File.WriteAllText(Path.Combine(_root, "items.json"), "[]");

        var drop = new DropStore(_root);

        Assert.Equal("a holiday photo", File.ReadAllText(Directory.GetFiles(OrphansDir).Single()));
        Assert.Equal(1, drop.SetAsideCount);
    }

    [Fact]
    public void FilesSetAsideLongAgoAreEventuallyRemoved()
    {
        SetAsideOne("a1.jpg", "old");
        string old = Directory.GetFiles(OrphansDir).Single();
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-61));
        SetAsideOne("a2.jpg", "recent");

        var left = Directory.GetFiles(OrphansDir).Select(File.ReadAllText).ToList();

        Assert.Equal(["recent"], left);
    }

    [Fact]
    public void FilesSetAsideRecentlyEnoughAreKept()
    {
        // Pins the retention itself, not merely that something is eventually removed:
        // without this, a lifetime of one day would pass the test above.
        SetAsideOne("a1.jpg", "not old enough");
        File.SetLastWriteTimeUtc(Directory.GetFiles(OrphansDir).Single(), DateTime.UtcNow.AddDays(-59));
        SetAsideOne("a2.jpg", "recent");

        Assert.Equal(2, Directory.GetFiles(OrphansDir).Length);
    }
}

/// <summary>
/// The whole sequence, end to end: an old drop, a build that cannot read its index, and
/// then an interrupted save. Each step is survivable and each was fixed on its own; taken
/// in order they used to end with twenty photos deleted and the window reporting one file
/// set aside, which was a JSON list.
/// </summary>
public sealed class DropStoreCompoundLossTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-loss-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "items.json");
    private string FilesDir => Path.Combine(_root, "files");
    private string OrphansDir => Path.Combine(_root, "orphans");

    [Fact]
    public void TwentyPhotosSurviveAnUnreadableIndexFollowedByAnInterruptedSave()
    {
        // 1. Twenty photos arrive and sit in the drop for three months.
        Directory.CreateDirectory(FilesDir);
        var entries = Enumerable.Range(1, 20).Select(n =>
        {
            string path = Path.Combine(FilesDir, $"a{n}.jpg");
            File.WriteAllText(path, $"photo {n}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-90));
            return $$"""
                {"Id":"a{{n}}","Kind":"file","Text":null,"StoredName":"a{{n}}.jpg",
                 "Size":8,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}
                """;
        });
        File.WriteAllText(IndexPath, "[" + string.Join(",", entries) + "]");

        // 2. A later build renamed a field, so this one reads the index but can use none
        //    of it. The first save keeps the original beside the new one.
        new DropStore(_root).AddText("a note", from: "pc");
        Assert.Equal(20, Directory.GetFiles(FilesDir).Length);

        // 3. A launch that sweeps: the kept copy still names the photos.
        _ = new DropStore(_root);
        Assert.Equal(20, Directory.GetFiles(FilesDir).Length);

        // 4. A save is interrupted, leaving a staging file newer than the index. The next
        //    launch finishes that move, which is where the surviving copy of round two
        //    used to be overwritten by the index being replaced.
        string staging = IndexPath + ".tmp";
        File.WriteAllText(staging, """
            [{"Id":"n2","Kind":"text","Text":"another note","StoredName":"",
              "Size":12,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(1));

        var last = new DropStore(_root);

        Assert.Equal("another note", Assert.Single(last.Items).Text);
        Assert.Equal(20, Directory.GetFiles(FilesDir).Length);
        Assert.False(Directory.Exists(OrphansDir) && Directory.GetFiles(OrphansDir).Length > 0,
            "photos were set aside even though a kept index still names them");
    }
}

/// <summary>
/// The copies kept beside the index. Their names are numbered when one is taken, and the
/// sweep used to look for four literal names, so the second copy a profile ever made
/// protected nothing: every photo it accounted for was set aside on the next launch.
/// </summary>
public sealed class DropStoreIndexCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-copies-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "items.json");
    private string FilesDir => Path.Combine(_root, "files");
    private string OrphansDir => Path.Combine(_root, "orphans");

    private int OrphanCount => Directory.Exists(OrphansDir) ? Directory.GetFiles(OrphansDir).Length : 0;

    /// <summary>An index this build cannot use, naming files that do exist.</summary>
    private void GiveItAPartialIndex(string prefix, int count)
    {
        Directory.CreateDirectory(FilesDir);
        var entries = Enumerable.Range(1, count).Select(n =>
        {
            File.WriteAllText(Path.Combine(FilesDir, $"{prefix}{n}.jpg"), $"photo {prefix}{n}");
            return $$"""
                {"Id":"{{prefix}}{{n}}","Kind":"file","Text":null,"StoredName":"{{prefix}}{{n}}.jpg",
                 "Size":8,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}
                """;
        });
        File.WriteAllText(IndexPath, "[" + string.Join(",", entries) + "]");
    }

    [Fact]
    public void TheSecondCopyProtectsItsPhotosJustLikeTheFirst()
    {
        GiveItAPartialIndex("a", 20);
        new DropStore(_root).AddText("round one", from: "pc");

        GiveItAPartialIndex("b", 20);
        new DropStore(_root).AddText("round two", from: "pc");

        // Round two's copy is numbered, because round one took the plain name.
        Assert.Equal(2, Directory.GetFiles(_root, "items.json.superseded*").Length);

        _ = new DropStore(_root);

        Assert.Equal(40, Directory.GetFiles(FilesDir).Length);
        Assert.Equal(0, OrphanCount);
    }

    [Fact]
    public void APromotionOnAProfileThatAlreadyHasACopyDoesNotStrandItsPhotos()
    {
        // The promotion path numbers its copy too, and the sweep runs later in the same
        // constructor: twenty photos went to orphans on that launch.
        GiveItAPartialIndex("a", 20);
        new DropStore(_root).AddText("round one", from: "pc");

        // An ordinary readable index naming twenty photos, plus an interrupted save.
        GiveItAPartialIndex("b", 20);
        string readable = File.ReadAllText(IndexPath).Replace("\"Text\":null", "\"Text\":\"photo.jpg\"");
        File.WriteAllText(IndexPath, readable);
        string staging = IndexPath + ".tmp";
        File.WriteAllText(staging, """
            [{"Id":"n1","Kind":"text","Text":"a note","StoredName":"",
              "Size":6,"From":"pc","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddHours(1));

        _ = new DropStore(_root);

        Assert.Equal(40, Directory.GetFiles(FilesDir).Length);
        Assert.Equal(0, OrphanCount);
    }

    [Fact]
    public void ACopyThatCouldNotBeMadeDoesNotCountAsMade()
    {
        // The copy is the only thing standing between a reduced list and the original,
        // and it gets one chance per session. Clearing the flag whether or not it
        // succeeded spent that chance on a copy that never happened.
        //
        // The lock has to land after the load and before the save, which is the real
        // window: a scanner or a backup taking the file while the drop is open.
        GiveItAPartialIndex("a", 20);
        string original = File.ReadAllText(IndexPath);

        var drop = new DropStore(_root);
        using (File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            drop.AddText("while it was held", from: "pc");
        }

        // Released now. The next save must still keep a copy before it writes.
        drop.AddText("after it was released", from: "pc");

        var copies = Directory.GetFiles(_root, "items.json.superseded*");
        Assert.Single(copies);
        Assert.Equal(original, File.ReadAllText(copies[0]));
        Assert.Equal(20, Directory.GetFiles(FilesDir).Length);
    }

    [Fact]
    public void OneFileThatWillNotMoveDoesNotStopTheRest()
    {
        // Everything after the set-aside loop is housekeeping the drop depends on, and a
        // single locked file used to throw out of the whole sweep.
        Directory.CreateDirectory(FilesDir);
        foreach (string name in new[] { "a1.jpg", "a2.jpg", "a3.jpg" })
            File.WriteAllText(Path.Combine(FilesDir, name), name);
        File.WriteAllText(IndexPath, "[]");

        using (File.Open(Path.Combine(FilesDir, "a2.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _ = new DropStore(_root);
        }

        // The two that could move did, and the one that could not is still in the drop
        // rather than half moved or lost.
        Assert.Equal(2, OrphanCount);
        Assert.Equal("a2.jpg", Path.GetFileName(Directory.GetFiles(FilesDir).Single()));
    }

    [Fact]
    public void ASecondSessionDoesNotOverwriteTheFirstSessionsRecoveryList()
    {
        new DropStore(_root).AddFile("holiday.jpg", new MemoryStream([1, 2, 3]), from: "phone");

        foreach (string note in new[] { "first session", "second session" })
        {
            using var held = File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None);
            new DropStore(_root).AddText(note, from: "pc");
        }

        var lists = Directory.GetFiles(_root, "items.json.recovered*").Select(File.ReadAllText).ToList();

        Assert.Equal(2, lists.Count);
        Assert.Contains(lists, text => text.Contains("first session"));
        Assert.Contains(lists, text => text.Contains("second session"));
    }

    [Fact]
    public void AnIndexCopyIsRemovedOnceNothingItNamesIsLeft()
    {
        GiveItAPartialIndex("a", 2);
        new DropStore(_root).AddText("round one", from: "pc");
        string copy = Directory.GetFiles(_root, "items.json.superseded*").Single();

        // The photos are gone from the drop, and the copy is old. It accounts for
        // nothing now, and nothing else ever removes it.
        foreach (string path in Directory.GetFiles(FilesDir))
            File.Delete(path);
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddDays(-61));

        var drop = new DropStore(_root);

        Assert.False(File.Exists(copy));
        Assert.Equal(0, drop.SetAsideCount);
    }

    [Fact]
    public void AnIndexCopyIsKeptWhileItStillAccountsForAPhoto()
    {
        GiveItAPartialIndex("a", 2);
        new DropStore(_root).AddText("round one", from: "pc");
        string copy = Directory.GetFiles(_root, "items.json.superseded*").Single();
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddDays(-61));

        _ = new DropStore(_root);

        Assert.True(File.Exists(copy), "the only record of two photos still on disk was deleted");
    }
}

/// <summary>
/// The housekeeping that removes spent index copies, and the one file it must never
/// touch. Windows treats a trailing ".*" as "extension optional", so the pattern that
/// finds the copies also matches the index itself.
/// </summary>
public sealed class DropStoreIndexPruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gergur-prune-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "items.json");

    [Fact]
    public void ADropOfNothingButMessagesSurvivesBeingLeftAlone()
    {
        // No entry names a file, so nothing the prune looks for is on disk, and after
        // sixty days the index is older than the cutoff. It was deleted on open, and the
        // whole drop went with it, silently.
        var first = new DropStore(_root);
        first.AddText("the wifi password is on the fridge", from: "phone");
        first.AddText("https://example.com/flat-viewing", from: "phone");
        File.SetLastWriteTimeUtc(IndexPath, DateTime.UtcNow.AddDays(-61));

        _ = new DropStore(_root);
        var reopened = new DropStore(_root);

        Assert.True(File.Exists(IndexPath), "the live index was pruned as though it were a copy");
        Assert.Equal(2, reopened.Items.Count);
    }

    [Fact]
    public void ARecoveryListIsNotAgedOutWhileNothingReadsItBack()
    {
        // It is a session's own work and the only copy of it, so it is not ours to
        // expire on a timer. CLAUDE.md says it is preserved; this is that promise.
        new DropStore(_root).AddFile("holiday.jpg", new MemoryStream([1, 2, 3]), from: "phone");
        using (File.Open(IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            new DropStore(_root).AddText("the address of the flat viewing", from: "pc");
        }

        string recovered = Directory.GetFiles(_root, "items.json.recovered*").Single();
        File.SetLastWriteTimeUtc(recovered, DateTime.UtcNow.AddDays(-61));

        _ = new DropStore(_root);

        Assert.True(File.Exists(recovered), "a session's only record of its own work was aged out");
    }

    [Fact]
    public void ACopyOfAnOldIndexIsNotBornExpired()
    {
        // Move and copy both keep the source's write time, so a copy of an index last
        // touched two months ago was already past the cutoff the moment it was made.
        //
        // The entries name a file that is no longer on disk, which is the case where the
        // copy is all that is left of it: its name, its date, and when it arrived. That
        // is also the only case the prune will consider deleting, so it is the one that
        // shows whether the copy was born expired.
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        File.WriteAllText(IndexPath, """
            [{"Id":"a1","Kind":"file","Text":null,"StoredName":"a1.jpg",
              "Size":5,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}]
            """);
        File.SetLastWriteTimeUtc(IndexPath, DateTime.UtcNow.AddDays(-61));

        new DropStore(_root).AddText("a note", from: "pc");
        Assert.Single(Directory.GetFiles(_root, "items.json.superseded*"));

        // The prune only runs when a store opens, so the copy has to survive the launch
        // after the one that made it. That is where an inherited write time kills it.
        _ = new DropStore(_root);

        Assert.Single(Directory.GetFiles(_root, "items.json.superseded*"));
    }

    [Fact]
    public void ACopyThisBuildCannotReadStopsItDecidingWhatIsUnused()
    {
        // The copy is read by the same parser that failed on the entries it was kept for.
        // Filtering those names out and then calling the rest unreferenced set twenty
        // photos aside two launches after the build that could not read them.
        // StoredName is the field the later build renamed, which is the case that bites:
        // it is the one the copy is read for, so filtering those entries out leaves the
        // copy naming nothing at all while still looking like a readable list.
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        var entries = Enumerable.Range(1, 20).Select(n =>
        {
            File.WriteAllText(Path.Combine(_root, "files", $"a{n}.jpg"), $"photo {n}");
            return $$"""
                {"Id":"a{{n}}","Kind":"file","Text":"photo{{n}}.jpg","StoredName":null,
                 "Size":8,"From":"phone","AddedUtc":"2026-01-01T00:00:00Z"}
                """;
        });
        File.WriteAllText(IndexPath, "[" + string.Join(",", entries) + "]");

        // Launch one keeps the copy and writes a small readable index beside it.
        new DropStore(_root).AddText("a note", from: "pc");

        // Launch two: the index is intact, so the sweep would ordinarily run.
        _ = new DropStore(_root);

        Assert.Equal(20, Directory.GetFiles(Path.Combine(_root, "files")).Length);
        Assert.False(Directory.Exists(Path.Combine(_root, "orphans")));
    }
}
