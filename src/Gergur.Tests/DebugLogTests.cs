using Gergur.Diagnostics;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// The log's file handling, on a path of the test's own. Never through WriteAlways, which
/// writes into the real profile.
/// </summary>
public sealed class DebugLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gergur-log-{Guid.NewGuid():N}");
    private readonly string _path;

    public DebugLogTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "debug.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void BelowTheCapItOnlyAppends()
    {
        DebugLog.Append(_path, "one\n", maxBytes: 1000);
        DebugLog.Append(_path, "two\n", maxBytes: 1000);

        Assert.Equal("one\ntwo\n", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".old"));
    }

    [Fact]
    public void PastTheCapItStartsOverAndKeepsOnePrevious()
    {
        File.WriteAllText(_path, new string('a', 100));

        DebugLog.Append(_path, "fresh\n", maxBytes: 50);
        Assert.Equal("fresh\n", File.ReadAllText(_path));
        Assert.Equal(new string('a', 100), File.ReadAllText(_path + ".old"));

        // The next roll replaces the kept file rather than failing because it exists.
        File.AppendAllText(_path, new string('b', 100));
        DebugLog.Append(_path, "again\n", maxBytes: 50);
        Assert.Equal("again\n", File.ReadAllText(_path));
        Assert.StartsWith("fresh\n", File.ReadAllText(_path + ".old"));
    }

    [Fact]
    public void SomebodyWatchingTheLogDoesNotSilenceIt()
    {
        // Tailing the file holds it open without letting it be moved. Rolling over used to
        // throw at that point, before the append, and every line from then on was lost,
        // exactly while somebody was watching for one.
        File.WriteAllText(_path, new string('a', 100));
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            DebugLog.Append(_path, "while watched\n", maxBytes: 50);
            Assert.EndsWith("while watched\n", ReadShared(_path));
        }

        // Once nobody holds it, the next write rolls it over as usual.
        DebugLog.Append(_path, "after\n", maxBytes: 50);
        Assert.Equal("after\n", File.ReadAllText(_path));
        Assert.EndsWith("while watched\n", File.ReadAllText(_path + ".old"));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TestsNeverWriteTheRealLog()
    {
        // A test run used to add its deliberate failures to the user's own log. The test
        // assembly moves the log somewhere of its own before any test runs; this fails if
        // that stops happening, or if a write stops going where FilePath says.
        string real = Path.GetFullPath(Path.Combine(Gergur.App.Settings.DataDir, "debug.log"));
        Assert.NotEqual(real, Path.GetFullPath(DebugLog.FilePath), StringComparer.OrdinalIgnoreCase);
        Assert.False(
            Path.GetFullPath(DebugLog.FilePath).StartsWith(Path.GetFullPath(Gergur.App.Settings.DataDir), StringComparison.OrdinalIgnoreCase),
            "the test run's log is inside the real profile");

        string marker = $"test-run-marker {Guid.NewGuid():N}";
        DebugLog.WriteAlways(marker);
        Assert.Contains(marker, ReadShared(TestLog.Path));
    }

    [Fact]
    public void WritersAtTheSameTimeDoNotLoseLines()
    {
        // Several request threads failing at once. Unserialised, the second append meets
        // the first one's handle and throws, and WriteAlways swallows that: a lost line.
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => DebugLog.Append(_path, $"line {i}\n", maxBytes: long.MaxValue));

        Assert.Equal(200, File.ReadAllLines(_path).Length);
    }
}
