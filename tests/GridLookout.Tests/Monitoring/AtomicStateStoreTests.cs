using GridLookout.Config;
using GridLookout.Monitoring;
using GridLookout.Tests.Config;
using Xunit;

namespace GridLookout.Tests.Monitoring;

public class AtomicStateStoreTests : IDisposable
{
    private readonly string _dir;

    public AtomicStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.AtomicStateStore." + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, _dir), _dir);

        store.Write("health.json", """{"a":1}""");

        Assert.Equal("""{"a":1}""", store.Read("health.json"));
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, _dir), _dir);

        Assert.Null(store.Read("health.json"));
    }

    [Fact]
    public void Write_CreatesDestinationDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "nested", "state");
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, nested), _dir);

        store.Write("health.json", "content");

        Assert.True(Directory.Exists(nested));
        Assert.Equal("content", File.ReadAllText(Path.Combine(nested, "health.json")));
    }

    [Fact]
    public void Write_OverwritesExistingFile_UsingReplaceNotAppend()
    {
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, _dir), _dir);

        store.Write("health.json", "first-version");
        store.Write("health.json", "second-version");

        Assert.Equal("second-version", store.Read("health.json"));
    }

    [Fact]
    public void Write_LeavesNoTempFilesBehindOnSuccess()
    {
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, _dir), _dir);

        store.Write("health.json", "content");

        var leftoverTempFiles = Directory.GetFiles(_dir, ".health.json.tmp-*");
        Assert.Empty(leftoverTempFiles);
    }

    [Fact]
    public void Write_FirstEverWrite_UsesMoveNotReplace_NoPriorFileNeeded()
    {
        // File.Replace requires the destination to already exist; the very first write to a fresh
        // state directory has no prior health.json — this must succeed via File.Move instead of
        // throwing FileNotFoundException.
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, _dir), _dir);

        var ex = Record.Exception(() => store.Write("health.json", "first-ever-write"));

        Assert.Null(ex);
        Assert.Equal("first-ever-write", store.Read("health.json"));
    }

    [Fact]
    public void Write_DestinationUntouchedWhenPathIsInvalid()
    {
        // Simulate a write failure (an invalid filename character) after a valid prior write —
        // the destination must still hold the LAST GOOD content, never a torn/partial file.
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, _dir), _dir);
        store.Write("health.json", "good-content");

        Assert.ThrowsAny<Exception>(() => store.Write("bad\0name.json", "should-not-land"));

        // The original, unrelated file is untouched.
        Assert.Equal("good-content", store.Read("health.json"));
    }

    [Fact]
    public void Constructor_ResolvesThroughIStateDirectory_NotABarePath()
    {
        // The whole point of taking IStateDirectory (not a bare string) is that a test can hold a
        // FIXED writable/unwritable outcome and directory independent of the real OS-level
        // permission of whatever "base dir" is passed — mirrors FakeStateDirectory's existing role
        // in WallConfigLoaderTests.
        var stateDirPath = Path.Combine(_dir, "programdata-style-fallback");
        var store = new AtomicStateStore(new FakeStateDirectory(writable: false, stateDirPath), _dir);

        store.Write("health.json", "content");

        Assert.Equal(stateDirPath, store.Directory);
        Assert.True(File.Exists(Path.Combine(stateDirPath, "health.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "health.json")));
    }
}
