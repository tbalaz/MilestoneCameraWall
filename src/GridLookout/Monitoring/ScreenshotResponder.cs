using System.Drawing;
using System.Drawing.Imaging;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using GridLookout.Logging;

namespace GridLookout.Monitoring;

/// <summary>
/// The running-wall side of the remote-screenshot feature: arms two named, cross-session
/// auto-reset events at boot and, on a Request signal, captures every <see cref="Screen"/> to
/// <c>screen-&lt;n&gt;.png</c> under the resolved screenshot directory, then signals Done.
///
/// WHY THIS EXISTS. GridLookout kiosks run unattended, often headless-to-a-remote-operator (RDP
/// disconnected or physically inaccessible) — the only way to sanity-check "what is actually on
/// the screens right now" is over ssh/psexec. Interactively touching the kiosk (stopping it,
/// screenshotting some OTHER session, RDP'ing in and disturbing the console session's lock state)
/// is exactly what this feature avoids: <c>GridLookout.exe --screenshot</c>, run from a SEPARATE
/// process/session while the wall keeps running undisturbed, is the entire point — see
/// <see cref="ScreenshotRequester"/> for that side.
///
/// IPC MECHANISM. Two named, auto-reset <see cref="EventWaitHandle"/>s in the <c>Global\</c> kernel
/// namespace (<see cref="RequestEventName"/>, <see cref="DoneEventName"/>) — <c>Global\</c>,
/// specifically, not <c>Local\</c>, because the requester runs in a DIFFERENT Windows session than
/// the wall (a remote admin shell is its own session; the kiosk's autologon session is another) —
/// <c>Local\</c>-namespaced objects are only visible within the creating session. Created with an
/// <see cref="EventWaitHandleSecurity"/> ACL granting Authenticated Users
/// <see cref="EventWaitHandleRights.Modify"/> (needed to call <c>Set()</c>) |
/// <see cref="EventWaitHandleRights.Synchronize"/> (needed to wait on it) — the default ACL a
/// same-account process gets is NOT sufficient for a DIFFERENT account's remote shell to signal
/// these, which is the entire point of a remote check. Uses the net48
/// <c>EventWaitHandle(bool, EventResetMode, string, out bool, EventWaitHandleSecurity)</c>
/// constructor overload for exactly this reason.
///
/// PRIVILEGE CAVEAT. Creating a <c>Global\</c>-namespaced kernel object requires the
/// <c>SeCreateGlobalPrivilege</c> ("Create global objects") Windows privilege — granted by default
/// to Administrators/SYSTEM/service accounts, NOT to a plain standard-user account. The documented
/// kiosk account (docs/security.md, "Writable-state fallback") is exactly the kind of
/// limited account that might lack it. Program.cs wraps construction of this type in a try/catch
/// for that reason — a privilege shortfall here must degrade the wall to "remote screenshot
/// unavailable" (<see cref="ScreenshotRequester"/> then reports "GridLookout is not running.",
/// indistinguishable from the wall genuinely not running — see that class's own doc comment), never
/// crash the boot sequence over an optional diagnostic feature.
///
/// RE-OPEN CAVEAT (why <see cref="BuildEventWaitHandleSecurity"/> grants a SECOND, broader ACE, not
/// just the Authenticated Users one above). Verified empirically against the real net48
/// constructor: when a named event with this constructor overload ALREADY EXISTS, the Windows ACL
/// passed to THAT call is checked against whatever access the constructor internally requests to
/// open it — which is broader than <see cref="EventWaitHandleRights.Modify"/>|
/// <see cref="EventWaitHandleRights.Synchronize"/> — and this fails with
/// <see cref="UnauthorizedAccessException"/> even for the EXACT SAME Windows account that created
/// the object in the first place, if that account's own ACE doesn't cover it. This matters here
/// specifically because of Program.cs's crash-relaunch handoff (see its
/// <c>AppDomain.CurrentDomain.UnhandledException</c> handler): the CHILD process's own
/// <see cref="ScreenshotResponder"/> construction can run WHILE the crashing PARENT process's handles
/// to these same two names are still open (the parent calls <c>Process.Start</c> before
/// <c>Environment.Exit</c> — the mutex has its own, separate E5 fix for the identical race; these
/// named events have no equivalent because unlike the mutex they aren't a single-instance gate, see
/// Program.cs's own construction-site comment) — landing the child on exactly this "already exists"
/// path. Without the second ACE below, that race would silently and PERMANENTLY disable screenshot
/// capability for the child's entire remaining life (caught by Program.cs's try/catch, logged as a
/// Warning, never retried). The fix: grant the CURRENT process's own account
/// <see cref="EventWaitHandleRights.FullControl"/> in addition to Authenticated Users'
/// Modify|Synchronize — broad enough to satisfy the re-open access check regardless of exactly which
/// rights the BCL requests internally, without weakening the cross-account grant a genuinely
/// different remote-shell account still needs (that account only ever needs Modify+Synchronize —
/// signal it and wait on it — never FullControl).
///
/// TESTABILITY. <see cref="Screen.AllScreens"/> + <see cref="Graphics.CopyFromScreen(Point, Point, Size)"/>
/// requires a live interactive session and is explicitly excluded from the unit-test surface (see
/// the constructor's <paramref name="captureAction"/> parameter — production code omits it and gets
/// <see cref="CaptureAllScreens"/>; tests inject a fake that never touches the screen, so the
/// signal/write-then-Done ORDERING and the capture-failure-still-signals-Done contract are both
/// testable without a live desktop).
/// </summary>
public sealed class ScreenshotResponder : IDisposable
{
    /// <summary>See this class's own "IPC MECHANISM" doc comment section. <c>Global\</c> namespace
    /// — must match <see cref="ScreenshotRequester"/>'s default exactly; both sides reference this
    /// SAME constant rather than each hardcoding the literal string a second time.</summary>
    public const string RequestEventName = @"Global\GridLookout.Screenshot.Request";

    /// <summary>See <see cref="RequestEventName"/>'s doc comment — same reasoning, the "capture is
    /// finished, files are fully written" signal.</summary>
    public const string DoneEventName = @"Global\GridLookout.Screenshot.Done";

    private readonly string _outputDirectory;
    private readonly FileLogger _logger;
    private readonly Action<string> _captureAction;
    private readonly SynchronizationContext? _uiContext;
    private readonly EventWaitHandle _requestEvent;
    private readonly EventWaitHandle _doneEvent;
    private readonly RegisteredWaitHandle _registeredWait;

    // Re-entrancy guard: a screenshot request that arrives while a capture is already running must
    // not start a SECOND overlapping capture (two concurrent CopyFromScreen/PNG-write passes over
    // the SAME fixed screen-N.png paths could interleave writes — AtomicBinaryFileWriter makes any
    // one write atomic, but does nothing to order two independent writers against each other).
    // Interlocked (not a plain bool) because OnRequestSignaled can run on more than one ThreadPool
    // thread if signals arrive close together — same "must be safe from any thread" reasoning
    // Program.cs's own AppDomain.UnhandledException handler doc comment gives for using Dispose()
    // over ReleaseMutex() on the single-instance mutex.
    private int _captureInProgress;
    private bool _disposed;

    /// <param name="outputDirectory">Where to write screen-N.png — see
    /// <see cref="ScreenshotPaths.ResolveWritableScreenshotDirectory"/>; Program.cs computes this
    /// once at boot and passes the resolved string in, rather than this type re-deriving it, so the
    /// SAME computation Program.cs already performs isn't duplicated a second time.</param>
    /// <param name="logger">Never thrown to — a capture failure is logged as a Warning (see
    /// <see cref="RunCaptureAndSignalDone"/>) and otherwise swallowed; a screenshot subsystem must
    /// never be able to crash the wall it's diagnosing.</param>
    /// <param name="requestEventName">Defaults to <see cref="RequestEventName"/> — overridable ONLY
    /// so tests can use short-lived, collision-proof random names instead of the real
    /// <c>Global\</c>-namespaced production names (which additionally require
    /// <c>SeCreateGlobalPrivilege</c> — see this class's own "PRIVILEGE CAVEAT" section — a plain
    /// unprefixed test name needs no such privilege).</param>
    /// <param name="doneEventName">See <paramref name="requestEventName"/>.</param>
    /// <param name="captureAction">Defaults to <see cref="CaptureAllScreens"/> (the real
    /// CopyFromScreen-based capture). Tests inject a fake so the signal/write-then-Done ordering,
    /// the capture-failure-still-signals-Done contract, and the overlapping-request guard are all
    /// testable without a live screen/session — see this class's own "TESTABILITY" doc section.</param>
    public ScreenshotResponder(
        string outputDirectory,
        FileLogger logger,
        string? requestEventName = null,
        string? doneEventName = null,
        Action<string>? captureAction = null)
    {
        _outputDirectory = outputDirectory;
        _logger = logger;
        _captureAction = captureAction ?? CaptureAllScreens;

        // Captured HERE, at construction time, on whichever thread constructs this instance —
        // Program.cs constructs it on the boot (STA/UI) thread, right after winning the
        // single-instance mutex and BEFORE any WinForms Control/Form has ever been created on that
        // thread (see Program.cs's own construction-site comment). WindowsFormsSynchronizationContext
        // is installed lazily, on first Control construction — at THIS point in the boot sequence
        // none has happened yet, so SynchronizationContext.Current is null in practice on every real
        // run, and OnRequestSignaled's fallback path (direct execution on the ThreadPool wait-
        // callback thread, no Post at all) is what actually runs for every capture. This is NOT a
        // bug or an unfinished corner: it's the reason a screenshot request answers even while the
        // wall is showing its "Connecting to Management Server..." status card or stuck on a retry
        // loop during an outage — LoginRetryLoop pumps Application.DoEvents() only inside its own
        // delay loop (see PumpDelay), and session.Login() itself is a fully blocking, synchronous
        // SDK call with NO message pumping at all. A Post()'d capture would sit in that Form's
        // message queue, UNPROCESSED, for however long the blocking call takes — comfortably past
        // ScreenshotRequester's 15-second wait, producing a spurious "timed out" (exit 3) against a
        // perfectly healthy wall. Do NOT "fix" the null context by moving construction later (e.g.
        // after the first WallForm exists) — that would silently reintroduce exactly this failure
        // mode for every request that lands during boot/reconnect, the two situations a remote
        // sanity-check is most likely to be used for in the first place. The Post path still exists
        // for whatever future WinForms refactor might install a context earlier; it is exercised by
        // ScreenshotResponderTests via an explicitly-installed test SynchronizationContext.
        _uiContext = SynchronizationContext.Current;

        var security = BuildEventWaitHandleSecurity();
        _requestEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, requestEventName ?? RequestEventName, out _, security);
        _doneEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, doneEventName ?? DoneEventName, out _, security);

        // executeOnlyOnce:false — repeating registration, armed for the whole process life (see
        // this class's own doc comment and Program.cs's disposal-site comment for why it must stay
        // armed across every RecoverSession cycle, not just the boot login). Timeout.Infinite: this
        // callback exists purely to react to a signal, never to a timeout, so timedOut is always
        // false in OnRequestSignaled and is ignored there.
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(_requestEvent, OnRequestSignaled, state: null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Grants Authenticated Users Modify+Synchronize (see this class's own "IPC MECHANISM"
    /// doc comment) PLUS the current account FullControl (see "RE-OPEN CAVEAT") — two ACEs, two
    /// different reasons.</summary>
    private static EventWaitHandleSecurity BuildEventWaitHandleSecurity()
    {
        var security = new EventWaitHandleSecurity();
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, domainSid: null);
        security.AddAccessRule(new EventWaitHandleAccessRule(authenticatedUsers, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));

        // See "RE-OPEN CAVEAT" above — WindowsIdentity.GetCurrent().User can in principle be null
        // (documented by the BCL for certain identity types with no mapped SID); skip the second ACE
        // rather than throw if so — degrades back to the original re-open risk, never crashes boot.
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new EventWaitHandleAccessRule(currentUser, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>ThreadPool wait callback for <see cref="RequestEventName"/> — see this class's own
    /// doc comment for the marshal-vs-direct-execution decision this makes.</summary>
    private void OnRequestSignaled(object? state, bool timedOut)
    {
        if (Interlocked.CompareExchange(ref _captureInProgress, 1, 0) != 0)
        {
            // A capture is already running (an overlapping request arrived while it was still in
            // flight) — signal Done immediately rather than starting a second concurrent capture.
            // The contract this satisfies is "the requester never hangs", not "every Request gets
            // its own dedicated capture" — the answer already in flight covers this request too.
            SafeSetDone();
            return;
        }

        var context = _uiContext;
        if (context is not null)
        {
            try
            {
                context.Post(_ => RunCaptureAndSignalDone(), state: null);
                return;
            }
            catch
            {
                // Post() itself throwing (not the posted work — that has its own try/catch inside
                // RunCaptureAndSignalDone) must be caught HERE, not left to escape: this method runs
                // on a ThreadPool wait-callback thread, and an unhandled exception on ANY thread
                // reaches AppDomain.CurrentDomain.UnhandledException, which Program.cs wires to the
                // fatal-exception crash-relaunch handler — a screenshot request must never be able
                // to restart the wall. Fall through to direct execution so Done still gets signaled
                // and _captureInProgress still gets released (both happen inside
                // RunCaptureAndSignalDone's own finally, below).
            }
        }

        RunCaptureAndSignalDone();
    }

    /// <summary>Runs <see cref="_captureAction"/>, then ALWAYS signals Done and releases the
    /// re-entrancy guard — via <c>finally</c>, so a capture exception can never leave the requester
    /// hanging. Runs either on the UI thread (via <see cref="SynchronizationContext.Post"/>) or
    /// directly on the ThreadPool wait-callback thread — see <see cref="OnRequestSignaled"/>.</summary>
    private void RunCaptureAndSignalDone()
    {
        try
        {
            _captureAction(_outputDirectory);
        }
        catch (Exception ex)
        {
            // Never let a capture failure (permission denied writing the output directory, a
            // display driver refusing CopyFromScreen, disk full — none of these are the wall's own
            // fault) crash the wall or its UI thread. Logged and swallowed; the requester sees only
            // "Done fired, but no/fewer files than expected" — see ScreenshotRequester's own doc
            // comment for that tradeoff.
            _logger.Warning($"Screenshot capture failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _captureInProgress, 0);
            SafeSetDone();
        }
    }

    private void SafeSetDone()
    {
        try
        {
            _doneEvent.Set();
        }
        catch
        {
            // e.g. the handle was disposed concurrently with process shutdown — never let a Set()
            // failure throw out of a ThreadPool/UI-thread callback (see OnRequestSignaled's own
            // doc comment for why an escaped exception here is worse than a missed signal: the
            // requester times out and reports that plainly, rather than the wall crash-relaunching).
        }
    }

    /// <summary>
    /// Real capture implementation (the <see cref="_captureAction"/> default) — captures every
    /// <see cref="Screen.AllScreens"/> entry to <c>screen-&lt;i+1&gt;.png</c> under
    /// <paramref name="outputDirectory"/>, overwriting the fixed filenames each time (no unbounded
    /// disk growth across repeated requests — see <see cref="AtomicBinaryFileWriter"/> for the
    /// overwrite mechanics), then prunes any orphaned <c>screen-N.png</c> left over from a PAST run
    /// with MORE monitors (see <see cref="PruneOrphanedScreenFiles"/>). Deliberately excluded from
    /// the unit-test surface — see this class's own "TESTABILITY" doc section.
    /// </summary>
    private static void CaptureAllScreens(string outputDirectory)
    {
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var bounds = screens[i].Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                // Bounds.Location is in VIRTUAL-DESKTOP coordinates (can be negative for a monitor
                // positioned above/left of the primary) — CopyFromScreen's source point accepts
                // that directly; the destination point into the freshly-allocated bitmap is always
                // (0,0) regardless.
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, ImageFormat.Png);

            var destinationPath = Path.Combine(outputDirectory, ScreenshotPaths.FileName(i + 1));
            AtomicBinaryFileWriter.Write(destinationPath, pngStream.ToArray());
        }

        PruneOrphanedScreenFiles(outputDirectory, screens.Length);
    }

    /// <summary>Deletes any <c>screen-N.png</c> whose N exceeds <paramref name="currentScreenCount"/>
    /// — e.g. <c>screen-3.png</c> surviving after the box drops from three monitors to two. Without
    /// this, that file would keep being listed by <see cref="ScreenshotRequester"/> forever (it's
    /// never overwritten once its own index stops being captured). Best-effort: a locked/
    /// permission-denied file is skipped, never fatal to the capture that just successfully ran.</summary>
    private static void PruneOrphanedScreenFiles(string outputDirectory, int currentScreenCount)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(outputDirectory, ScreenshotPaths.FileNamePattern))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (ScreenshotPaths.TryParseScreenIndex(name, out var index) && index > currentScreenCount)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best-effort — a stray un-deletable orphan is a minor annoyance, never worth
                    // failing an otherwise-successful capture over.
                }
            }
        }
    }

    /// <summary>
    /// Releases the named event handles and unregisters the ThreadPool wait — called from
    /// Program.cs's existing end-of-`Application.Run()` shutdown sequence (alongside
    /// <c>refreshTimer.Stop()</c>/<c>healthTimer.Dispose()</c>/<c>PowerGuard.Release()</c>) and from
    /// its two early-return paths before that point is ever reached (boot login cancelled/failed,
    /// initial wall build failed) — see those call sites' own comments. On a path this file does
    /// NOT cover — a hard process kill, or the crash-relaunch handler's <c>Environment.Exit</c> —
    /// this is skipped entirely; that's accepted as ordinary OS process-exit handle cleanup (every
    /// kernel handle a process holds is closed by Windows on process exit regardless), the same
    /// tradeoff <c>Program.cs</c>'s own health/power-guard timers already make for the identical
    /// reason.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Unregister(null): no "wait for the last callback to fully complete" handle is supplied —
        // acceptable here because RunCaptureAndSignalDone's own finally already guarantees Done
        // gets signaled and the re-entrancy flag gets released even if a callback is still in
        // flight at the moment of Dispose; the process is tearing down either way at this call site.
        try
        {
            _registeredWait.Unregister(null);
        }
        catch
        {
            // Never let teardown itself throw — same discipline as every other shutdown step in
            // Program.cs's own end-of-Application.Run() sequence.
        }

        _requestEvent.Dispose();
        _doneEvent.Dispose();
    }
}
