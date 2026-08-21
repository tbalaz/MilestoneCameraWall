using GridLookout.Logging;
using GridLookout.Monitoring;
using Xunit;

namespace GridLookout.Tests.Monitoring;

public class ScreenshotResponderTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly string _logDir;
    private readonly FileLogger _logger;

    public ScreenshotResponderTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.ScreenshotResponder." + Guid.NewGuid());
        _logger = new FileLogger(_logDir, LogLevel.Debug);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // Bare (unprefixed) names, unique per test via a GUID suffix — no Global\/Local\ prefix at all,
    // so these need no special privilege (unlike the real Global\-namespaced production names — see
    // ScreenshotResponder's own "PRIVILEGE CAVEAT" doc section) and never collide across parallel
    // test runs.
    private static string UniqueEventName(string suffix) => $"GridLookout.Test.{suffix}.{Guid.NewGuid():N}";

    private string ReadTodayLog()
    {
        var path = Path.Combine(_logDir, $"gridlookout-{DateTime.Now:yyyyMMdd}.log");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    [Fact]
    public void Constructor_CreatesBothNamedEvents_OpenableByName()
    {
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");

        using var responder = new ScreenshotResponder("unused-output-dir", _logger, requestName, doneName, captureAction: _ => { });

        // OpenExisting is itself the assertion — it throws WaitHandleCannotBeOpenedException if the
        // named event was never created.
        using var openedRequest = EventWaitHandle.OpenExisting(requestName);
        using var openedDone = EventWaitHandle.OpenExisting(doneName);
    }

    [Fact]
    public void Constructor_ReopeningAlreadyExistingNames_DoesNotThrow()
    {
        // Regression test for the crash-relaunch handoff race (see ScreenshotResponder's own
        // "RE-OPEN CAVEAT" doc section): the CHILD process's ScreenshotResponder construction can
        // run while the crashing PARENT's own handles to these SAME names are still open, landing
        // on this exact "construct against an already-existing named event" path — even for the
        // IDENTICAL Windows account on both sides. Verified empirically (see the task's own research
        // notes) that without the second, broader ACE this throws UnauthorizedAccessException even
        // same-account; this test is what would have caught that regression.
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");

        using var first = new ScreenshotResponder("unused-output-dir", _logger, requestName, doneName, captureAction: _ => { });

        var ex = Record.Exception(() =>
        {
            using var second = new ScreenshotResponder("unused-output-dir", _logger, requestName, doneName, captureAction: _ => { });
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndDoesNotThrow()
    {
        var responder = new ScreenshotResponder("unused-output-dir", _logger, UniqueEventName("request"), UniqueEventName("done"), captureAction: _ => { });

        var ex = Record.Exception(() =>
        {
            responder.Dispose();
            responder.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void OnRequest_InvokesCaptureAction_BeforeSignalingDone()
    {
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        var captured = false;

        using var responder = new ScreenshotResponder("unused-output-dir", _logger, requestName, doneName, captureAction: _ => { captured = true; });
        using var requestEvent = EventWaitHandle.OpenExisting(requestName);
        using var doneEvent = EventWaitHandle.OpenExisting(doneName);

        requestEvent.Set();
        var signaled = doneEvent.WaitOne(WaitTimeout);

        // The responder's own code sets Done inside a `finally` AFTER calling the capture action
        // (see RunCaptureAndSignalDone) — Done becoming signaled therefore structurally proves the
        // capture action already ran; this asserts that empirically too.
        Assert.True(signaled, "Done was never signaled.");
        Assert.True(captured, "Capture action never ran before Done was signaled.");
    }

    [Fact]
    public void OnRequest_PassesConfiguredOutputDirectory_ToCaptureAction()
    {
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        string? receivedDirectory = null;
        var expectedDirectory = Path.Combine(_logDir, "screenshots");

        using var responder = new ScreenshotResponder(expectedDirectory, _logger, requestName, doneName, captureAction: dir => { receivedDirectory = dir; });
        using var requestEvent = EventWaitHandle.OpenExisting(requestName);
        using var doneEvent = EventWaitHandle.OpenExisting(doneName);

        requestEvent.Set();
        Assert.True(doneEvent.WaitOne(WaitTimeout));

        Assert.Equal(expectedDirectory, receivedDirectory);
    }

    [Fact]
    public void OnRequest_CaptureThrows_StillSignalsDone_AndLogsWarning()
    {
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");

        using var responder = new ScreenshotResponder("unused-output-dir", _logger, requestName, doneName, captureAction: _ => throw new InvalidOperationException("boom"));
        using var requestEvent = EventWaitHandle.OpenExisting(requestName);
        using var doneEvent = EventWaitHandle.OpenExisting(doneName);

        requestEvent.Set();
        var signaled = doneEvent.WaitOne(WaitTimeout);

        Assert.True(signaled, "A capture failure must still signal Done — the requester must not hang.");

        var logContent = ReadTodayLog();
        Assert.Contains("Screenshot capture failed", logContent);
        Assert.Contains("boom", logContent);
    }

    [Fact]
    public void OnRequest_OverlappingRequest_SignalsDoneWithoutStartingSecondCapture()
    {
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        var firstCaptureEntered = new ManualResetEventSlim(false);
        var releaseFirstCapture = new ManualResetEventSlim(false);
        var captureCount = 0;

        using var responder = new ScreenshotResponder("unused-output-dir", _logger, requestName, doneName, captureAction: _ =>
        {
            Interlocked.Increment(ref captureCount);
            firstCaptureEntered.Set();
            releaseFirstCapture.Wait(WaitTimeout);
        });
        using var requestEvent = EventWaitHandle.OpenExisting(requestName);
        using var doneEvent = EventWaitHandle.OpenExisting(doneName);

        requestEvent.Set();
        Assert.True(firstCaptureEntered.Wait(WaitTimeout), "First capture never started.");

        // A second request arrives while the first capture is still blocked inside captureAction —
        // must signal Done immediately (the re-entrancy guard's branch in OnRequestSignaled) rather
        // than hanging or starting a second concurrent capture.
        requestEvent.Set();
        var secondSignaled = doneEvent.WaitOne(WaitTimeout);
        Assert.True(secondSignaled, "Overlapping request must still signal Done promptly.");

        releaseFirstCapture.Set();

        // The first capture's own Done signal fires once we release it — drain it so this test
        // leaves nothing pending, and so the final count assertion below is stable.
        doneEvent.WaitOne(WaitTimeout);

        Assert.Equal(1, captureCount);
    }

    [Fact]
    public void OnRequest_WithAmbientSynchronizationContext_MarshalsViaPost()
    {
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        var recordingContext = new RecordingSynchronizationContext();
        var originalContext = SynchronizationContext.Current;

        ScreenshotResponder responder;
        try
        {
            // Installed only around construction — ScreenshotResponder reads
            // SynchronizationContext.Current ONCE, at construction time (see its own doc comment),
            // so this is enough to make it capture recordingContext regardless of what the test
            // host's own thread normally carries.
            SynchronizationContext.SetSynchronizationContext(recordingContext);
            responder = new ScreenshotResponder("unused-output-dir", _logger, requestName, doneName, captureAction: _ => { });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        using (responder)
        {
            using var requestEvent = EventWaitHandle.OpenExisting(requestName);
            using var doneEvent = EventWaitHandle.OpenExisting(doneName);

            requestEvent.Set();
            var signaled = doneEvent.WaitOne(WaitTimeout);

            Assert.True(signaled);
            Assert.Equal(1, recordingContext.PostCount);
        }
    }

    [Fact]
    public void OnRequest_WithNoSynchronizationContext_RunsCaptureDirectly()
    {
        var requestName = UniqueEventName("request");
        var doneName = UniqueEventName("done");
        var captured = false;
        var originalContext = SynchronizationContext.Current;

        ScreenshotResponder responder;
        try
        {
            // Forced null regardless of whatever the test host's own thread happens to carry — this
            // is also exactly the situation on GridLookout's own boot thread at the point
            // ScreenshotResponder is actually constructed in production (see that class's own doc
            // comment: no WindowsFormsSynchronizationContext has been installed yet at that point in
            // Program.cs's boot sequence, so SynchronizationContext.Current is null there on every
            // real run). This test documents that the DEFAULT/common construction context takes the
            // direct-execution path, not the Post path exercised above.
            SynchronizationContext.SetSynchronizationContext(null);
            responder = new ScreenshotResponder("unused-output-dir", _logger, requestName, doneName, captureAction: _ => { captured = true; });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        using (responder)
        {
            using var requestEvent = EventWaitHandle.OpenExisting(requestName);
            using var doneEvent = EventWaitHandle.OpenExisting(doneName);

            requestEvent.Set();
            var signaled = doneEvent.WaitOne(WaitTimeout);

            Assert.True(signaled);
            Assert.True(captured);
        }
    }
}
