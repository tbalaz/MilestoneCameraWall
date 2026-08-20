using GridLookout.Logging;

namespace GridLookout.Milestone;

/// <summary>
/// FIX 4 (poll off the UI thread): runs <c>MilestoneSession.TryGetRecorderDescriptions</c> — a
/// synchronous HTTP call with a 10s timeout — on a background thread pool thread instead of the
/// WinForms refresh timer's UI thread, so a slow/unreachable Management Server can no longer freeze
/// the message pump (frame callbacks queue behind a wedged Tick handler). The UI thread only ever
/// reads <see cref="Latest"/> (a thread-safe snapshot, never blocks) and calls
/// <see cref="TriggerPollIfIdle"/> (fire-and-forget, never blocks) — it never awaits the poll itself.
///
/// Single-flight by construction: <see cref="TriggerPollIfIdle"/> is a no-op while a previous poll
/// is still running, so overlapping polls (a slow poll still in flight when the next
/// <c>ConfigRefreshSeconds</c> tick fires) can never happen — there is only ever at most one
/// in-flight HTTP call to the Management Server from this worker. A failed/refused poll (the
/// injected <paramref name="poll"/> function returns null — see
/// <c>MilestoneSession.TryGetRecorderDescriptions</c>'s own doc comment for when that happens)
/// leaves <see cref="Latest"/> exactly as it was: the same graceful degradation
/// <c>RecorderCatalog.ApplyLiveDescriptions</c> already applies for a null overlay — the wall keeps
/// using the last successfully-fetched descriptions (or the SDK-cached ones, if none has ever
/// succeeded) rather than losing them to one bad tick.
///
/// The <paramref name="poll"/> function is injected specifically so this class is testable without a
/// live SDK session/real HTTP — see <c>DescriptionPollWorkerTests</c>.
/// </summary>
public sealed class DescriptionPollWorker
{
    private readonly Func<Dictionary<Guid, string>?> _poll;
    private readonly FileLogger? _logger;

    // Round-4 buyer-review hardening: the in-flight gate is an Interlocked int, no longer a volatile
    // bool — a volatile check-then-set is two separate operations, so two truly concurrent callers
    // could both read false and both start a poll, breaking the single-flight contract. The
    // production caller is a single UI thread (where the volatile version could never actually
    // race), but the contract should hold by construction, not by caller discipline —
    // Interlocked.CompareExchange makes check-and-claim one atomic step. _latest stays volatile:
    // that one genuinely IS a single plain reference write needing only visibility.
    private int _inFlight; // 0 = idle, 1 = poll running — only ever touched via Interlocked.
    private volatile bool _shutdown;
    private volatile IReadOnlyDictionary<Guid, string>? _latest;

    public DescriptionPollWorker(Func<Dictionary<Guid, string>?> poll, FileLogger? logger = null)
    {
        _poll = poll;
        _logger = logger;
    }

    /// <summary>The most recently COMPLETED successful poll result — null until the first poll
    /// succeeds, or forever if every poll attempt so far has failed/been refused. Safe to read from
    /// the UI thread at any time; never blocks, never reflects a poll that's still in flight.</summary>
    public IReadOnlyDictionary<Guid, string>? Latest => _latest;

    /// <summary>Starts a background poll unless one is already running — see the class doc comment
    /// for the single-flight contract. Always returns immediately; the UI thread calling this on
    /// every refresh tick never blocks on the Management Server regardless of how slow or wedged it
    /// is.</summary>
    public void TriggerPollIfIdle()
    {
        if (_shutdown)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var result = _poll();
                // The _shutdown re-check discards a result that completed AFTER Shutdown() — the
                // process is exiting, and publishing one last snapshot nobody will read is at best
                // pointless and at worst hands a torn-down consumer a surprise. A null result:
                // this attempt failed/was refused — _latest is deliberately left untouched (see
                // the class doc comment's graceful-degradation contract).
                if (result is not null && !_shutdown)
                {
                    _latest = result;
                }
            }
            catch (Exception ex)
            {
                // Defense in depth only — MilestoneSession.TryGetRecorderDescriptions already
                // catches its own exceptions and returns null; this guards against a future poll
                // function that doesn't, so a background-thread exception can never go unobserved
                // or leave _inFlight stuck true.
                _logger?.Debug($"Background description poll threw unexpectedly (will retry next tick): {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        });
    }

    /// <summary>
    /// Round-4 buyer-review hardening: stops the worker for good — every later
    /// <see cref="TriggerPollIfIdle"/> is a no-op, and an in-flight poll's eventual result is
    /// discarded instead of published. Called from Program.cs's process-exit paths only; it does NOT
    /// block waiting for an in-flight poll (the underlying HTTP call is bounded by its own 10s
    /// timeout and dies with the process — there is nothing left that would consume a waited-for
    /// result anyway).
    ///
    /// Deliberately NOT called during mid-session recovery: the worker and its
    /// <c>MilestoneSession</c> outlive a recovery cycle (one instance each for the process's whole
    /// life), and a poll straddling the teardown degrades safely on its own — the re-login swaps
    /// the session's token, so the stale in-flight request either fails (null result, nothing
    /// published) or returns Description text at worst one tick stale, overwritten by the next
    /// poll. Shutting down and re-creating the worker per recovery would add lifecycle complexity
    /// for a window that already can't corrupt anything.
    /// </summary>
    public void Shutdown()
    {
        _shutdown = true;
    }
}
