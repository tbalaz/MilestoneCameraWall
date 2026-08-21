using GridLookout.Logging;
using GridLookout.Monitoring;
using Xunit;

namespace GridLookout.Tests.Monitoring;

public class ScreenshotRequesterTests : IDisposable
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly string _dir;
    private readonly string _logDir;
    private readonly FileLogger _logger;

    public ScreenshotRequesterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.ScreenshotRequester." + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _logDir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.ScreenshotRequesterLog." + Guid.NewGuid());
        _logger = new FileLogger(_logDir, LogLevel.Debug);
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

        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // Bare (unprefixed) names, unique per test — see ScreenshotResponderTests' identical helper for
    // why no Global\/Local\ prefix is needed for these test-only names.
    private static string UniqueEventName(string suffix) => $"GridLookout.Test.{suffix}.{Guid.NewGuid():N}";

    [Theory]
    [InlineData("--screenshot", true)]
    [InlineData("--Screenshot", true)]
    [InlineData("--SCREENSHOT", true)]
    [InlineData("--screenshotx", false)]
    [InlineData("screenshot", false)]
    [InlineData("--health-probe", false)]
    public void IsRequested_MatchesFlagCaseInsensitively_ExactTextOnly(string arg, bool expected)
    {
        Assert.Equal(expected, ScreenshotRequester.IsRequested(new[] { arg }));
    }

    [Fact]
    public void IsRequested_FindsFlagAmongOtherArgs()
    {
        Assert.True(ScreenshotRequester.IsRequested(new[] { "--recorder", "foo", "--screenshot" }));
    }

    [Fact]
    public void IsRequested_False_WhenArgsEmpty()
    {
        Assert.False(ScreenshotRequester.IsRequested(Array.Empty<string>()));
    }

    [Fact]
    public void Run_EventsAbsent_ReturnsExitCode2_AndPrintsNotRunningMessage()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScreenshotRequester.Run(
            new[] { _dir },
            stdout,
            stderr,
            requestEventName: UniqueEventName("request-never-created"),
            doneEventName: UniqueEventName("done-never-created"),
            timeout: ShortTimeout);

        Assert.Equal(ScreenshotRequester.ExitCodeNotRunning, exitCode);
        Assert.Contains("GridLookout is not running.", stdout.ToString());
    }

    [Fact]
    public void Run_DrainsStaleDoneSignal_ThenTimesOut_ReturnsExitCode3()
    {
        // Discriminating test for the drain step (see Run's own comment on it): pre-signal Done
        // BEFORE the requester ever runs, simulating a leftover signal from a previous request whose
        // own requester process gave up without consuming it. No real responder is listening here —
        // nothing will EVER signal Done again for THIS request. If the drain step were missing or
        // broken (e.g. checking Done before Setting Request, or treating the pre-existing signal as
        // this request's own answer), this would incorrectly return 0 immediately; with a correct
        // drain-then-signal, it must instead genuinely time out.
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        using var requestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, requestName);
        using var doneEvent = new EventWaitHandle(false, EventResetMode.AutoReset, doneName);
        doneEvent.Set(); // stale leftover signal from an earlier, abandoned request

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScreenshotRequester.Run(
            new[] { _dir },
            stdout,
            stderr,
            requestEventName: requestName,
            doneEventName: doneName,
            timeout: ShortTimeout);

        Assert.Equal(ScreenshotRequester.ExitCodeTimeout, exitCode);
        Assert.Contains("Timed out", stdout.ToString());
    }

    [Fact]
    public void Run_Success_WithLiveResponder_ReturnsExitCode0_AndListsFullPathsInNumericOrder()
    {
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");

        // A fake capture action stands in for ScreenshotResponder's real CaptureAllScreens (which
        // needs a live screen/session — explicitly excluded from the unit-test surface, see
        // ScreenshotResponder's own doc comment). Writes deliberately OUT of numeric order (10
        // before 2) so ListScreenshotFiles' sort-by-index behavior is actually exercised, not just
        // coincidentally correct because the files happened to already be alphabetical.
        using var responder = new ScreenshotResponder(_dir, _logger, requestName, doneName, captureAction: dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "screen-10.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "screen-2.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "screen-1.png"), new byte[] { 1 });
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScreenshotRequester.Run(
            new[] { _dir },
            stdout,
            stderr,
            requestEventName: requestName,
            doneEventName: doneName,
            timeout: WaitTimeout);

        Assert.Equal(ScreenshotRequester.ExitCodeSuccess, exitCode);
        var lines = stdout.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("screen-1.png", lines[0]);
        Assert.EndsWith("screen-2.png", lines[1]);
        Assert.EndsWith("screen-10.png", lines[2]);
        Assert.All(lines, line => Assert.True(Path.IsPathRooted(line), $"'{line}' is not a full path."));
    }

    [Fact]
    public void Run_Success_ChecksSecondCandidateDirectory_WhenFirstHasNoFiles()
    {
        // Cross-account fallback: the FIRST candidate directory exists but is empty (e.g. it's the
        // requester's own account's guess, which turned out to be the wrong one — see ScreenshotPaths'
        // own "CROSS-ACCOUNT CAVEAT" doc comment) — the SECOND candidate is where the responder
        // actually wrote. Run must fall through to it rather than reporting nothing.
        var emptyFirstCandidate = Path.Combine(_dir, "empty-first-candidate");
        Directory.CreateDirectory(emptyFirstCandidate);
        var secondCandidate = Path.Combine(_dir, "actual-output");

        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        using var responder = new ScreenshotResponder(secondCandidate, _logger, requestName, doneName, captureAction: dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "screen-1.png"), new byte[] { 1 });
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScreenshotRequester.Run(
            new[] { emptyFirstCandidate, secondCandidate },
            stdout,
            stderr,
            requestEventName: requestName,
            doneEventName: doneName,
            timeout: WaitTimeout);

        Assert.Equal(ScreenshotRequester.ExitCodeSuccess, exitCode);
        Assert.Contains(Path.Combine(secondCandidate, "screen-1.png"), stdout.ToString());
    }

    [Fact]
    public void Run_Success_PrefersFreshCandidate_OverStaleFilesInAnEarlierCandidate()
    {
        // Discriminating test for the freshness rule (see ScreenshotRequester's own "FRESHNESS" doc
        // section): candidate A (checked FIRST) holds a screen-*.png left over from a much earlier
        // deployment/account combination — candidate B is where THIS request's responder actually
        // wrote. A naive "first candidate with ANY file" rule would report A's stale image; the
        // freshness check must skip past it to B instead.
        var staleCandidate = Path.Combine(_dir, "stale-candidate");
        Directory.CreateDirectory(staleCandidate);
        var staleFile = Path.Combine(staleCandidate, "screen-1.png");
        File.WriteAllBytes(staleFile, new byte[] { 1 });
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-30));

        var freshCandidate = Path.Combine(_dir, "fresh-candidate");
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        using var responder = new ScreenshotResponder(freshCandidate, _logger, requestName, doneName, captureAction: dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "screen-1.png"), new byte[] { 2 });
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScreenshotRequester.Run(
            new[] { staleCandidate, freshCandidate },
            stdout,
            stderr,
            requestEventName: requestName,
            doneEventName: doneName,
            timeout: WaitTimeout);

        Assert.Equal(ScreenshotRequester.ExitCodeSuccess, exitCode);
        var printed = stdout.ToString();
        Assert.Contains(Path.Combine(freshCandidate, "screen-1.png"), printed);
        Assert.DoesNotContain(staleCandidate, printed);
    }

    [Fact]
    public void Run_DoneButOnlyStaleFilesExist_ReturnsExitCode4_NoFreshCapture_AndNeverPrintsStalePaths()
    {
        // m5 fix (2026-08-21 external audit): the pre-fix fallback printed STALE files (a prior
        // request's, or months-old imagery from a different account/deployment split) with exit 0
        // whenever the current capture wrote nothing — misleading "success" in exactly the
        // failed-capture case a remote operator most needs to notice. Now a Done with no file
        // provably fresh from THIS request is its own exit code (4), with guidance pointing at the
        // wall's log — and the stale paths are never offered as if they were the answer.
        var staleCandidate = Path.Combine(_dir, "only-stale-candidate");
        Directory.CreateDirectory(staleCandidate);
        var staleFile = Path.Combine(staleCandidate, "screen-1.png");
        File.WriteAllBytes(staleFile, new byte[] { 1 });
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-30));

        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        using var responder = new ScreenshotResponder(staleCandidate, _logger, requestName, doneName, captureAction: _ => { /* writes nothing this time */ });

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScreenshotRequester.Run(
            new[] { staleCandidate },
            stdout,
            stderr,
            requestEventName: requestName,
            doneEventName: doneName,
            timeout: WaitTimeout);

        Assert.Equal(ScreenshotRequester.ExitCodeNoFreshCapture, exitCode);
        var printed = stdout.ToString();
        Assert.Contains("no screenshot file from THIS request", printed);
        Assert.DoesNotContain(staleFile, printed);
    }

    [Fact]
    public void Run_DoneButZeroFilesWrittenAnywhere_ReturnsExitCode4_NoFreshCapture_WithGuidance()
    {
        // m5 fix companion of the stale-files case above: Done fired but the capture wrote nothing
        // AT ALL (capture-side failure — see ScreenshotResponder's capture-failure-still-signals-
        // Done contract). No longer silent exit 0; the requester names the failure and exits 4.
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        using var responder = new ScreenshotResponder(_dir, _logger, requestName, doneName, captureAction: _ => { /* writes nothing */ });

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScreenshotRequester.Run(
            new[] { _dir },
            stdout,
            stderr,
            requestEventName: requestName,
            doneEventName: doneName,
            timeout: WaitTimeout);

        Assert.Equal(ScreenshotRequester.ExitCodeNoFreshCapture, exitCode);
        Assert.Contains("no screenshot file from THIS request", stdout.ToString());
    }

    [Fact]
    public void Run_UnexpectedException_ReturnsExitCode1_AndPrintsToBothStreams()
    {
        // An empty event name is invalid input — EventWaitHandle.OpenExisting("") throws a plain
        // ArgumentException ("Empty name is not legal."), NOT WaitHandleCannotBeOpenedException — a
        // real, deterministic way to reach the generic catch-all, with no artificial test-only seam.
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScreenshotRequester.Run(
            new[] { _dir },
            stdout,
            stderr,
            requestEventName: string.Empty,
            doneEventName: UniqueEventName("done"),
            timeout: ShortTimeout);

        Assert.Equal(ScreenshotRequester.ExitCodeUnexpectedError, exitCode);
        Assert.Contains("--screenshot failed", stdout.ToString());
        Assert.Contains("--screenshot failed", stderr.ToString());
    }
}
