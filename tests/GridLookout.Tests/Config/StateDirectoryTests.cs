using GridLookout.Config;
using Xunit;

namespace GridLookout.Tests.Config;

/// <summary>
/// Covers <see cref="StateDirectory"/>, the B4/crash-loop fix's writable-state resolver.
///
/// The unwritable-directory case is simulated by pointing "exeDir" at a path that is actually a
/// FILE, not a directory — Directory.CreateDirectory/File.WriteAllText both fail deterministically
/// underneath it (no valid subpath exists), which lands in the exact same catch-all in
/// StateDirectory.CanWrite() a real ACL-denied directory would hit in production. This is
/// deliberately preferred over setting a real deny ACL in the test: it's deterministic, needs no
/// elevation, and can't leave a locked-down temp directory behind if a test run is interrupted.
/// </summary>
public class StateDirectoryTests : IDisposable
{
    private readonly string _dir;

    public StateDirectoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.StateDirectory." + Guid.NewGuid());
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
    public void Resolve_WritableExeDir_ReturnsExeDirItselfAndTrue()
    {
        var stateDirectory = new StateDirectory(Path.Combine(_dir, "programdata-override"));

        bool writable = stateDirectory.Resolve(_dir, out var stateDir);

        Assert.True(writable);
        Assert.Equal(_dir, stateDir);
    }

    [Fact]
    public void Resolve_WritableExeDir_LeavesNoProbeFileBehind()
    {
        var stateDirectory = new StateDirectory(Path.Combine(_dir, "programdata-override"));

        stateDirectory.Resolve(_dir, out _);

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Resolve_UnwritableExeDir_FallsBackToProgramDataOverride_ReturnsFalse()
    {
        var fakeExeDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        File.WriteAllText(fakeExeDir, string.Empty);
        var programDataOverride = Path.Combine(_dir, "programdata-override");
        var stateDirectory = new StateDirectory(programDataOverride);

        bool writable = stateDirectory.Resolve(fakeExeDir, out var stateDir);

        Assert.False(writable);
        Assert.Equal(programDataOverride, stateDir);
    }

    [Fact]
    public void Resolve_UnwritableExeDir_CreatesProgramDataOverrideIfMissing()
    {
        var fakeExeDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        File.WriteAllText(fakeExeDir, string.Empty);
        var programDataOverride = Path.Combine(_dir, "programdata-override");
        Assert.False(Directory.Exists(programDataOverride));

        var stateDirectory = new StateDirectory(programDataOverride);
        stateDirectory.Resolve(fakeExeDir, out _);

        Assert.True(Directory.Exists(programDataOverride));
    }

    [Fact]
    public void Resolve_UnwritableExeDir_DoesNotThrow()
    {
        // The whole point of this type: a probe failure must never surface as an exception to the
        // caller (Program.cs's config-load path) — it must always be a clean bool + fallback path.
        var fakeExeDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        File.WriteAllText(fakeExeDir, string.Empty);
        var stateDirectory = new StateDirectory(Path.Combine(_dir, "programdata-override"));

        var exception = Record.Exception(() => stateDirectory.Resolve(fakeExeDir, out _));

        Assert.Null(exception);
    }

    [Fact]
    public void Resolve_CalledTwice_IsIdempotentForAWritableDir()
    {
        var stateDirectory = new StateDirectory(Path.Combine(_dir, "programdata-override"));

        bool first = stateDirectory.Resolve(_dir, out var firstDir);
        bool second = stateDirectory.Resolve(_dir, out var secondDir);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(firstDir, secondDir);
    }

    [Fact]
    public void Resolve_ExeDirAndProgramDataOverrideBothUnwritable_FallsBackToExeDir_DoesNotThrow()
    {
        // T8(c)/R10: CreateDirectory(_programDataRoot) can itself throw (e.g. ProgramData is also
        // inaccessible) — same file-stands-in-for-a-directory technique as the exe-dir case above,
        // this time applied to the ProgramData override too, so BOTH candidate directories fail
        // deterministically.
        var fakeExeDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        File.WriteAllText(fakeExeDir, string.Empty);
        var fakeProgramDataParent = Path.Combine(_dir, "also-not-a-directory.tmp");
        File.WriteAllText(fakeProgramDataParent, string.Empty);
        var unreachableProgramDataOverride = Path.Combine(fakeProgramDataParent, "GridLookout");

        var stateDirectory = new StateDirectory(unreachableProgramDataOverride);

        bool writable = false;
        string? stateDir = null;
        var exception = Record.Exception(() => writable = stateDirectory.Resolve(fakeExeDir, out stateDir));

        Assert.Null(exception);
        Assert.True(writable);
        Assert.Equal(fakeExeDir, stateDir);
    }
}
