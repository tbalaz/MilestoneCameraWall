namespace GridLookout.Monitoring;

/// <summary>
/// Per-tile self-heal scheduling — pure timing/backoff math, SDK/UI-free, so it's unit-testable
/// without a live VMS or a message pump. One instance lives on each <c>WallForm</c> active-tile
/// entry, created alongside its <c>LiveTileSource</c>/<c>PictureBox</c>/stale-overlay triple.
///
/// WHY THIS EXISTS (per-tile self-heal defect fix). <c>WallForm.SweepStaleTiles</c> historically
/// only toggled a STALLED overlay label — it never reconnected a stalled or never-framed tile.
/// Recovery only ever happened at the WHOLE-SESSION level (see
/// <see cref="GridLookout.Recovery.SessionLossDetector"/>), which requires EVERY tile on the wall
/// to be stale simultaneously — a single camera whose stream wedged (a network blip to just that
/// camera, a recorder-side channel restart, or a live source that simply never delivered its first
/// frame) sat behind a STALLED/blank tile forever, since every OTHER tile staying healthy meant the
/// whole-wall trigger never fired. This type tracks, per tile, when the next reconnect attempt is
/// due and how many attempts have run so far — <c>WallForm</c> asks it "is a reconnect due right
/// now" on each sweep tick and, if so, tears down and rebuilds just that one tile's
/// <c>LiveTileSource</c> while leaving every other tile untouched.
///
/// SCHEDULE SHAPE. The first attempt fires <c>TileRecoverSeconds</c> after the tile first became
/// eligible for recovery (see <see cref="IsAttemptDue"/>'s <c>eligibleSinceUtc</c> parameter — NOT
/// immediately on the tick eligibility is noticed, so a tile that merely blips for a couple of
/// seconds around the StaleSeconds threshold doesn't immediately get torn down). Each subsequent
/// attempt doubles the wait from the PREVIOUS attempt (1x, 2x, 4x, 8x, ...), capped at 8x
/// <c>TileRecoverSeconds</c> so a persistently broken tile still retries every 8x interval forever
/// rather than backing off to nothing. <see cref="Reset"/> — called only when the tile actually
/// receives a frame, never merely on a successful <c>Init()</c> call (the SDK can report a
/// successful start even when the live session never actually delivers data) — clears the attempt
/// count and schedule, so the NEXT unrelated outage starts fresh at the base delay again instead of
/// wherever this outage's backoff had climbed to.
/// </summary>
public sealed class TileRecoveryScheduler
{
    /// <summary>0 disables per-tile self-heal entirely — <see cref="IsAttemptDue"/> always returns
    /// false in that case, matching camerawall.json's <c>TileRecoverSeconds: 0</c> = "sweep behaves
    /// exactly as before this feature existed" contract.</summary>
    private readonly int _tileRecoverSeconds;

    private int _attemptCount;
    private DateTime? _nextAttemptUtc;

    public TileRecoveryScheduler(int tileRecoverSeconds)
    {
        _tileRecoverSeconds = Math.Max(0, tileRecoverSeconds);
    }

    /// <summary>True when this scheduler will ever schedule an attempt — false when
    /// <c>TileRecoverSeconds</c> is 0 (feature off).</summary>
    public bool Enabled => _tileRecoverSeconds > 0;

    /// <summary>Number of reconnect attempts issued since construction or the last <see cref="Reset"/>
    /// — exposed for logging ("tile recovered after N attempts") and for tests.</summary>
    public int AttemptCount => _attemptCount;

    /// <summary>The next scheduled attempt time, or null if none is scheduled yet (either disabled,
    /// or the tile hasn't been judged eligible for recovery on any tick so far) — exposed for
    /// tests/diagnostics.</summary>
    public DateTime? NextAttemptUtc => _nextAttemptUtc;

    /// <summary>
    /// Call once per sweep tick for a tile currently judged to need recovery (stale-with-frames or
    /// never-framed past its own threshold — the caller computes which and supplies
    /// <paramref name="eligibleSinceUtc"/> accordingly: the moment the tile WENT stale for a
    /// has-framed tile, or the tile's own construction/last-camera-change time for a never-framed
    /// tile). Returns true exactly when a reconnect attempt is due THIS tick — the caller is
    /// responsible for actually tearing down/rebuilding the tile's <c>LiveTileSource</c> and then
    /// calling <see cref="RecordAttempt"/>.
    ///
    /// The very first call for a given bad spell seeds <see cref="NextAttemptUtc"/> to
    /// <c>eligibleSinceUtc + TileRecoverSeconds</c> — every subsequent call (until the schedule is
    /// cleared by <see cref="RecordAttempt"/> moving it forward, or <see cref="Reset"/>) ignores
    /// <paramref name="eligibleSinceUtc"/> and just compares against the already-seeded value, so a
    /// caller re-deriving <paramref name="eligibleSinceUtc"/> slightly differently tick-to-tick
    /// (e.g. because <c>LastRenderedUtc</c> hasn't changed) can never re-seed a LATER schedule than
    /// the one already committed.
    /// </summary>
    public bool IsAttemptDue(DateTime nowUtc, DateTime eligibleSinceUtc)
    {
        if (!Enabled)
        {
            return false;
        }

        _nextAttemptUtc ??= eligibleSinceUtc.AddSeconds(_tileRecoverSeconds);

        return nowUtc >= _nextAttemptUtc.Value;
    }

    /// <summary>Call immediately after actually issuing a reconnect attempt (tearing down + rebuilding
    /// the tile's live source) — advances the attempt counter and schedules the NEXT attempt at
    /// <c>min(TileRecoverSeconds * 2^(attempt-1), TileRecoverSeconds * 8)</c> seconds from now: base
    /// delay after attempt 1, doubling after each subsequent attempt, capped at 8x from attempt 4
    /// onward.</summary>
    public void RecordAttempt(DateTime nowUtc)
    {
        if (!Enabled)
        {
            return;
        }

        _attemptCount++;
        var delaySeconds = Math.Min(Math.Pow(2, _attemptCount - 1) * _tileRecoverSeconds, 8.0 * _tileRecoverSeconds);
        _nextAttemptUtc = nowUtc.AddSeconds(delaySeconds);
    }

    /// <summary>Call when the tile successfully receives (and renders) a frame — resets the attempt
    /// count and clears the schedule so the NEXT bad spell (an unrelated future outage) starts fresh
    /// at the base <c>TileRecoverSeconds</c> delay, not wherever this spell's backoff had climbed
    /// to. Safe/cheap to call unconditionally on every received frame, not just after a recovery —
    /// a tile that never needed recovery at all just resets an already-zero counter.</summary>
    public void Reset()
    {
        _attemptCount = 0;
        _nextAttemptUtc = null;
    }
}
