using GridLookout.Monitoring;
using GridLookout.Tests.Config;
using Xunit;

namespace GridLookout.Tests.Monitoring;

public class ScreenshotPathsTests
{
    [Fact]
    public void ResolveWritableScreenshotDirectory_AppendsScreenshotsSubdirectory_UnderResolvedStateDir()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.state." + Guid.NewGuid());
        var fake = new FakeStateDirectory(writable: true, stateDir);

        var result = ScreenshotPaths.ResolveWritableScreenshotDirectory(fake, @"C:\exe-dir");

        Assert.Equal(Path.Combine(stateDir, "screenshots"), result);
    }

    [Fact]
    public void CandidateScreenshotDirectories_PrimaryIsResolvedWritableDirectory()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.state." + Guid.NewGuid());
        var fake = new FakeStateDirectory(writable: true, stateDir);

        var candidates = ScreenshotPaths.CandidateScreenshotDirectories(fake, @"C:\exe-dir", programDataRootOverride: @"C:\programdata-fake\GridLookout");

        Assert.Equal(Path.Combine(stateDir, "screenshots"), candidates[0]);
    }

    [Fact]
    public void CandidateScreenshotDirectories_IncludesTheOtherLocation_WhenExeDirIsPrimary()
    {
        // Primary resolves to the exe dir (writable:true, stateDir == exeDir) — the ProgramData
        // fallback location must still appear as a second candidate, since a DIFFERENT account
        // (e.g. the requester's own, over a remote shell) could have produced the opposite answer —
        // see ScreenshotPaths' own doc comment ("CROSS-ACCOUNT CAVEAT") for why this matters.
        var exeDir = @"C:\exe-dir";
        var fake = new FakeStateDirectory(writable: true, exeDir);
        var programDataRoot = @"C:\programdata-fake\GridLookout";

        var candidates = ScreenshotPaths.CandidateScreenshotDirectories(fake, exeDir, programDataRootOverride: programDataRoot);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(Path.Combine(exeDir, "screenshots"), candidates);
        Assert.Contains(Path.Combine(programDataRoot, "screenshots"), candidates);
    }

    [Fact]
    public void CandidateScreenshotDirectories_IncludesTheOtherLocation_WhenProgramDataIsPrimary()
    {
        // The mirror of the case above: primary resolves to the ProgramData fallback (writable:
        // false — the same shape a limited kiosk account produces) — the exe-dir location must
        // still appear as the second candidate.
        var exeDir = @"C:\exe-dir";
        var programDataRoot = @"C:\programdata-fake\GridLookout";
        var fake = new FakeStateDirectory(writable: false, Path.Combine(programDataRoot));

        var candidates = ScreenshotPaths.CandidateScreenshotDirectories(fake, exeDir, programDataRootOverride: programDataRoot);

        Assert.Equal(Path.Combine(programDataRoot, "screenshots"), candidates[0]);
        Assert.Equal(2, candidates.Count);
        Assert.Contains(Path.Combine(exeDir, "screenshots"), candidates);
    }

    [Fact]
    public void CandidateScreenshotDirectories_NoDuplicate_WhenPrimaryAlreadyEqualsAlternate()
    {
        // Degenerate case: the resolved primary happens to already equal one of the two fixed
        // candidates (it always does, by construction) — must not appear twice.
        var exeDir = @"C:\exe-dir";
        var fake = new FakeStateDirectory(writable: true, exeDir);

        var candidates = ScreenshotPaths.CandidateScreenshotDirectories(fake, exeDir, programDataRootOverride: @"C:\programdata-fake\GridLookout");

        Assert.Single(candidates, c => string.Equals(c, Path.Combine(exeDir, "screenshots"), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public void FileName_ThenTryParseScreenIndex_RoundTrips(int screenNumber)
    {
        var fileName = ScreenshotPaths.FileName(screenNumber);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

        var parsed = ScreenshotPaths.TryParseScreenIndex(nameWithoutExtension, out var index);

        Assert.True(parsed);
        Assert.Equal(screenNumber, index);
    }

    [Theory]
    [InlineData("not-a-screen-file")]
    [InlineData("")]
    [InlineData("screen-")]
    public void TryParseScreenIndex_UnrecognizedName_ReturnsFalse(string nameWithoutExtension)
    {
        Assert.False(ScreenshotPaths.TryParseScreenIndex(nameWithoutExtension, out _));
    }
}
