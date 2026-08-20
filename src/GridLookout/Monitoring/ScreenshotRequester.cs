namespace GridLookout.Monitoring;

/// <summary>
/// The <c>GridLookout.exe --screenshot</c> side of the remote-screenshot feature — a short-lived
/// process, invoked ALONGSIDE an already-running wall (over ssh/psexec, from a different Windows
/// session), that asks the wall to capture its screens and prints back where the PNGs landed. See
/// <see cref="ScreenshotResponder"/> for the running-wall side of this same protocol and for why the
/// IPC events live in the <c>Global\</c> kernel namespace.
///
/// PROTOCOL (see <see cref="Run"/>): open both named events (fail fast — see the
/// <see cref="ExitCodeNotRunning"/> doc comment — if they don't exist), drain any stale Done
/// signal left over from a PRIOR request (see that step's own comment below), signal Request, wait
/// on Done with a bounded timeout, then list whatever <c>screen-*.png</c> files exist.
///
/// WHAT "SUCCESS" ACTUALLY MEANS. A Done signal means the responder's capture attempt FINISHED —
/// see <see cref="ScreenshotResponder.RunCaptureAndSignalDone"/>'s own doc comment: a capture
/// failure is logged and swallowed there, Done still fires. This method therefore cannot
/// distinguish "captured fine" from "capture failed, nothing was written" purely from the exit
/// code — exit 0 with an EMPTY file list is a legitimate, silent outcome of a capture-side failure.
/// See docs/admin-guide.md's "Remote screenshot" section for the operator-facing version
/// of that caveat.
///
/// FRESHNESS (see <see cref="ListScreenshotFiles"/>). Because <see cref="ScreenshotPaths"/> hands
/// back MORE THAN ONE candidate directory (a cross-account resolution ambiguity — see that class's
/// own "CROSS-ACCOUNT CAVEAT" doc comment), a naive "first candidate that has ANY file" rule can
/// report months-old imagery from an EARLIER deployment/account combination as if it were this
/// request's own answer — exactly the failure this whole feature exists to prevent, and silently.
/// <see cref="Run"/> therefore prefers the first candidate whose newest file was written AT OR
/// AFTER this request's own Request signal, falling back to the old "first non-empty" behavior only
/// if NO candidate qualifies (better than reporting nothing outright — see that fallback's own
/// comment for why exit 0 stops guaranteeing freshness on that specific path only).
/// </summary>
public static class ScreenshotRequester
{
    /// <summary>The exact <c>--screenshot</c> flag text — see <see cref="IsRequested"/>.</summary>
    public const string ArgName = "--screenshot";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Case-insensitive, exact-flag match — same convention Program.cs already uses for
    /// <c>--health-probe</c>/<c>--export-camera-bindings</c>/<c>--protect-password</c>. Exposed as
    /// its own public method (rather than an inline <c>args.Any(...)</c> lambda in Program.cs, as
    /// those other flags use) specifically so a unit test binds to the SAME code Program.cs actually
    /// calls, instead of re-asserting an independently-written copy of the match rule against
    /// itself.</summary>
    public static bool IsRequested(string[] args) => args.Any(a => string.Equals(a, ArgName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the full requester protocol and returns the process exit code Program.cs should use.
    /// </summary>
    /// <param name="candidateScreenshotDirectories">Checked in order after a successful Done signal
    /// — see <see cref="ListScreenshotFiles"/> for exactly how one candidate is chosen (freshness
    /// first, "any file at all" as the fallback) and <see cref="ScreenshotPaths"/>'s own doc comment
    /// for why more than one candidate is needed at all (the requester's own account may resolve
    /// <see cref="Config.StateDirectory"/> differently than the wall's account did). If NONE of them
    /// have any file (a legitimate outcome — see this class's own "WHAT 'SUCCESS' ACTUALLY MEANS" doc
    /// section), nothing is printed and this still returns 0.</param>
    /// <param name="stdout">Where the file-not-running message, the timeout message, and the
    /// resulting file paths are printed — <c>Console.Out</c> in production.</param>
    /// <param name="stderr">Where an UNEXPECTED failure (exit 1) is ALSO printed, in addition to
    /// stdout — see the design contract this satisfies in this method's final catch clause.</param>
    /// <param name="requestEventName">Defaults to <see cref="ScreenshotResponder.RequestEventName"/>
    /// — overridable for tests, same reasoning as <see cref="ScreenshotResponder"/>'s own
    /// constructor parameters of the same purpose.</param>
    /// <param name="doneEventName">See <paramref name="requestEventName"/>.</param>
    /// <param name="timeout">Defaults to 15 seconds.</param>
    public static int Run(
        IReadOnlyList<string> candidateScreenshotDirectories,
        TextWriter stdout,
        TextWriter stderr,
        string? requestEventName = null,
        string? doneEventName = null,
        TimeSpan? timeout = null)
    {
        EventWaitHandle? requestEvent = null;
        EventWaitHandle? doneEvent = null;
        try
        {
            try
            {
                requestEvent = EventWaitHandle.OpenExisting(requestEventName ?? ScreenshotResponder.RequestEventName);
                doneEvent = EventWaitHandle.OpenExisting(doneEventName ?? ScreenshotResponder.DoneEventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Neither event exists — the overwhelmingly common cause is exactly what the
                // message says: no running wall has created them yet (ScreenshotResponder creates
                // both together, at boot — see its own doc comment — so "one exists but not the
                // other" is not a reachable steady state to distinguish from this).
                stdout.WriteLine("GridLookout is not running.");
                return ExitCodeNotRunning;
            }

            // requestEvent/doneEvent are guaranteed non-null past this point — the only path that
            // could leave either one null is the catch above, which already returned. Same idiom
            // Program.cs's own RunExportCameraBindingsMode uses for recorderMatch after its
            // login/locate try/catch ("guaranteed non-null past this point — every path that would
            // leave it null already returned above").
            //
            // Drain any Done signal already sitting there BEFORE this request even begins — e.g. a
            // PREVIOUS --screenshot invocation set Request, the responder finished and signalled
            // Done, but that requester process had already given up (killed, or its own timeout
            // fired a moment too early) without ever consuming it. Auto-reset events stay signaled
            // until something waits on them; without this drain, THIS request could read that STALE
            // signal via the WaitOne below and report success instantly, without the responder ever
            // having serviced THIS request at all. WaitOne(0) — a zero-timeout poll, never blocks —
            // returns true and consumes the signal if one was already there, false (harmlessly) if
            // not.
            doneEvent!.WaitOne(0);

            // Captured immediately before Set() — the freshness floor ListScreenshotFiles checks
            // candidate files against, below. See this class's own "FRESHNESS" doc section.
            var requestedUtc = DateTime.UtcNow;
            requestEvent!.Set();

            var effectiveTimeout = timeout ?? DefaultTimeout;
            if (!doneEvent.WaitOne(effectiveTimeout))
            {
                stdout.WriteLine($"Timed out after {effectiveTimeout.TotalSeconds:F0}s waiting for GridLookout to respond to the screenshot request.");
                return ExitCodeTimeout;
            }

            foreach (var path in ListScreenshotFiles(candidateScreenshotDirectories, requestedUtc))
            {
                stdout.WriteLine(path);
            }

            return ExitCodeSuccess;
        }
        catch (Exception ex)
        {
            // Anything else — OpenExisting throwing something other than
            // WaitHandleCannotBeOpenedException (e.g. UnauthorizedAccessException opening a
            // Global\-namespaced handle without sufficient rights, or an invalid event name),
            // Set()/WaitOne() failing, or a file-system error listing the candidate directories.
            var message = $"--screenshot failed: {ex.GetType().Name}: {ex.Message}";
            stderr.WriteLine(message);
            stdout.WriteLine(message);
            return ExitCodeUnexpectedError;
        }
        finally
        {
            requestEvent?.Dispose();
            doneEvent?.Dispose();
        }
    }

    /// <summary>Exit code 0 — the responder signalled Done; see this class's own "WHAT 'SUCCESS'
    /// ACTUALLY MEANS" doc section for why this does not by itself guarantee any files exist.</summary>
    public const int ExitCodeSuccess = 0;

    /// <summary>Exit code 1 — an exception outside the two specifically-handled cases below.</summary>
    public const int ExitCodeUnexpectedError = 1;

    /// <summary>Exit code 2 — <see cref="ScreenshotResponder.RequestEventName"/>/
    /// <see cref="ScreenshotResponder.DoneEventName"/> don't exist, meaning no running wall has
    /// created them.</summary>
    public const int ExitCodeNotRunning = 2;

    /// <summary>Exit code 3 — Request was signalled but Done never came back within the timeout.</summary>
    public const int ExitCodeTimeout = 3;

    /// <summary>How much a file's <see cref="File.GetLastWriteTimeUtc(string)"/> is allowed to
    /// precede <c>requestedUtc</c> and still count as "from this request" — absorbs clock-precision/
    /// filesystem-timestamp-granularity slack between this process's <see cref="DateTime.UtcNow"/>
    /// read and the moment the responder's own <see cref="AtomicBinaryFileWriter.Write"/> call
    /// actually lands the file. Generous relative to how tight the real race is (the responder writes
    /// within milliseconds of the Request signal) without being so generous it would forgive a
    /// GENUINELY stale, wrong-candidate file — see this class's own "FRESHNESS" doc section.</summary>
    private static readonly TimeSpan FreshnessTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Picks ONE candidate directory's <c>screen-*.png</c> files, full paths, sorted by the numeric
    /// screen index (not lexicographically — <c>screen-10.png</c> must sort after
    /// <c>screen-2.png</c>, not before it). Two passes over <paramref name="candidateDirectories"/>:
    /// first, the FIRST candidate whose newest file's write time is at or after
    /// <paramref name="requestedUtc"/> minus <see cref="FreshnessTolerance"/> — proof that file came
    /// from THIS request, not a leftover from a different account/deployment (see this class's own
    /// "FRESHNESS" doc section). If NO candidate has a fresh file, falls back to the first candidate
    /// that has ANY file at all (better than reporting nothing — this is the one path where a
    /// non-empty result doesn't guarantee freshness; docs/admin-guide.md's "Remote
    /// screenshot" section carries the operator-facing version of that caveat). Empty if no candidate
    /// has any matching file anywhere — see this class's own "WHAT 'SUCCESS' ACTUALLY MEANS" doc
    /// section for why that's a legitimate outcome, not treated as an error here.
    /// </summary>
    private static IReadOnlyList<string> ListScreenshotFiles(IReadOnlyList<string> candidateDirectories, DateTime requestedUtc)
    {
        var freshnessFloorUtc = requestedUtc - FreshnessTolerance;
        string[]? firstNonEmptyCandidate = null;

        foreach (var candidate in candidateDirectories)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var files = Directory.GetFiles(candidate, ScreenshotPaths.FileNamePattern);
            if (files.Length == 0)
            {
                continue;
            }

            firstNonEmptyCandidate ??= files;

            var newestWriteUtc = files.Max(File.GetLastWriteTimeUtc);
            if (newestWriteUtc >= freshnessFloorUtc)
            {
                return SortByScreenIndex(files);
            }
        }

        return firstNonEmptyCandidate is null ? Array.Empty<string>() : SortByScreenIndex(firstNonEmptyCandidate);
    }

    private static IReadOnlyList<string> SortByScreenIndex(string[] files) =>
        files.Select(Path.GetFullPath).OrderBy(ExtractScreenIndex).ToList();

    /// <summary>Parses the <c>N</c> out of a <c>screen-N.png</c> path for numeric sort order —
    /// falls back to <see cref="int.MaxValue"/> (sorts last) for any name that doesn't match the
    /// pattern GridLookout itself ever writes, so an unrelated file someone else dropped into the
    /// screenshots directory can never throw here.</summary>
    private static int ExtractScreenIndex(string path) =>
        ScreenshotPaths.TryParseScreenIndex(Path.GetFileNameWithoutExtension(path), out var index) ? index : int.MaxValue;
}
