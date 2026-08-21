namespace GridLookout.Recovery;

/// <summary>
/// Decides when a live wall has lost its Management Server session and needs full teardown +
/// re-login + rebuild, as opposed to the routine per-tick refresh Program.cs already does. See
/// Program.cs's refresh timer and RecoverSession.
///
/// WHY THIS EXISTS (B5/E1 fix). Before this type, session.Login() ran exactly once, at boot — a
/// recording-service restart or management-server reboot under a live wall (the most common field
/// event) had no coded recovery path; tiles just sat behind the STALLED overlay forever. Two
/// independent signals feed the decision here, because neither is reliable alone: the SDK's own
/// ReloadConfiguration/TryGetRecorderDescriptions calls degrade SILENTLY on failure (they catch
/// their own exceptions and log at Debug/Warning without ever surfacing to the caller), so
/// RecorderLocator.Locate can keep "succeeding" off stale cached configuration through an outage
/// with zero failures ever recorded — staleness is what actually fires in that case. Conversely, a
/// hard connectivity loss (VMS unreachable) throws on every tick and the staleness signal isn't
/// needed at all. Either firing is sufficient.
///
/// Pure and SDK/UI-free so both thresholds are unit-testable without a live VMS or a message pump.
/// </summary>
public sealed class SessionLossDetector
{
    /// <summary>Consecutive failed refresh ticks (an exception, or RecorderLocator.Locate
    /// returning null) before triggering recovery on that signal alone.</summary>
    public const int ConsecutiveFailureThreshold = 3;

    /// <summary>Floor, in seconds, for the staleness trigger — protects a small configured
    /// StaleSeconds from tripping a full session teardown over a momentary blip the per-tile
    /// STALLED overlay already covers on its own.</summary>
    public const int MinStaleTriggerSeconds = 60;

    /// <summary>Multiplier applied to the configured StaleSeconds to derive the staleness trigger
    /// threshold, before <see cref="MinStaleTriggerSeconds"/> is applied as a floor.</summary>
    public const int StaleTriggerMultiplier = 3;

    /// <summary>Base delay, in seconds, for the FIRST recovery-backoff gate — see
    /// <see cref="RecordRecovery"/>.</summary>
    public const int RecoveryBackoffBaseSeconds = 60;

    /// <summary>Ceiling, in seconds, the recovery-backoff gate is clamped to — see
    /// <see cref="RecordRecovery"/>.</summary>
    public const int RecoveryBackoffMaxSeconds = 900;

    private int _consecutiveFailures;
    private int _recoveryStreak;
    private DateTime? _nextAllowedRecoveryUtc;
    private bool _suppressionWarningLogged;

    /// <summary>Current consecutive-failure count — exposed for tests/diagnostics.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>Current recovery streak (recoveries since the last <see cref="MarkHealthy"/>) —
    /// exposed for tests/diagnostics.</summary>
    public int RecoveryStreak => _recoveryStreak;

    /// <summary>The earliest time <see cref="CanRecover"/> will next return true, or null if no
    /// recovery has ever been recorded (or the streak was reset by <see cref="MarkHealthy"/>) —
    /// exposed for tests/diagnostics and for Program.cs's suppression-warning message.</summary>
    public DateTime? NextAllowedRecoveryUtc => _nextAllowedRecoveryUtc;

    /// <summary>Call when a refresh tick fails (exception, or the recorder can no longer be
    /// located). Returns true once <see cref="ConsecutiveFailureThreshold"/> is reached — the
    /// caller is responsible for actually triggering recovery and then calling
    /// <see cref="Reset"/>.</summary>
    public bool RecordFailure()
    {
        _consecutiveFailures++;
        return _consecutiveFailures >= ConsecutiveFailureThreshold;
    }

    /// <summary>Call when a refresh tick succeeds — resets the consecutive-failure counter so an
    /// isolated blip never accumulates toward the threshold across unrelated ticks.</summary>
    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
    }

    /// <summary>The staleness trigger threshold, in seconds, for a given configured StaleSeconds —
    /// <c>max(<see cref="MinStaleTriggerSeconds"/>, <see cref="StaleTriggerMultiplier"/> *
    /// <paramref name="configuredStaleSeconds"/>)</c>.</summary>
    public static int StaleTriggerThresholdSeconds(int configuredStaleSeconds) =>
        Math.Max(MinStaleTriggerSeconds, StaleTriggerMultiplier * configuredStaleSeconds);

    /// <summary>
    /// True when <paramref name="freshestTileAgeSeconds"/> — the age, in seconds, of the most
    /// recently updated live tile across the WHOLE wall (null meaning "no tile to judge, e.g.
    /// before the wall has ever built any tiles") — exceeds the threshold derived from
    /// <paramref name="configuredStaleSeconds"/>. If even the freshest tile anywhere on the wall is
    /// older than the threshold, every tile is at least that stale.
    /// </summary>
    public static bool IsStalenessTriggered(double? freshestTileAgeSeconds, int configuredStaleSeconds)
    {
        return freshestTileAgeSeconds is not null
            && freshestTileAgeSeconds.Value > StaleTriggerThresholdSeconds(configuredStaleSeconds);
    }

    /// <summary>Resets the consecutive-failure count — call once recovery begins, so the next
    /// session's failure count starts clean rather than carrying over stale history from before
    /// the outage. Deliberately does NOT touch <see cref="RecoveryStreak"/> or
    /// <see cref="NextAllowedRecoveryUtc"/> (the T2/R2 recovery-backoff state, set by
    /// <see cref="RecordRecovery"/>) — those must survive exactly this call, since it runs on every
    /// recovery, including the ones the backoff exists to damp. Clearing them here would silently
    /// defeat T2 entirely (every recovery would reset its own gate right after setting it).</summary>
    public void Reset()
    {
        _consecutiveFailures = 0;
    }

    /// <summary>
    /// True when a recovery may run right now — either none has ever been recorded (or the streak
    /// was reset by <see cref="MarkHealthy"/>), or the backoff gate set by the last
    /// <see cref="RecordRecovery"/> call has elapsed. Callers (Program.cs's two recovery trigger
    /// sites — staleness and consecutive-failure) must check this BEFORE actually running recovery.
    /// </summary>
    public bool CanRecover(DateTime nowUtc) => _nextAllowedRecoveryUtc is null || nowUtc >= _nextAllowedRecoveryUtc.Value;

    /// <summary>
    /// Call every time recovery actually runs (regardless of which trigger fired it — see
    /// <see cref="CanRecover"/>'s doc comment). Increments the recovery streak and sets the next
    /// allowed recovery time to <c>nowUtc + min(<see cref="RecoveryBackoffBaseSeconds"/> * 2^(streak-1),
    /// <see cref="RecoveryBackoffMaxSeconds"/>)</c> seconds — 60s after the 1st recovery, then 120,
    /// 240, 480, capped at 900 from the 5th recovery onward.
    ///
    /// WHY THIS EXISTS (T2/R2 fix). Recovery tears the whole wall down and re-logs in; if the
    /// underlying cause is still present (VMS still down, network still broken), the very next
    /// refresh tick trips the SAME trigger again and recovers again — and again — with zero
    /// damping, effectively DDoS-ing the Management Server with reconnect attempts. This backoff
    /// makes each successive un-recovered outage wait longer before the next attempt.
    /// </summary>
    public void RecordRecovery(DateTime nowUtc)
    {
        _recoveryStreak++;
        var delaySeconds = Math.Min(RecoveryBackoffBaseSeconds * Math.Pow(2, _recoveryStreak - 1), RecoveryBackoffMaxSeconds);
        _nextAllowedRecoveryUtc = nowUtc.AddSeconds(delaySeconds);
        _suppressionWarningLogged = false;
    }

    /// <summary>
    /// Call from the refresh tick whenever frames are genuinely flowing (see
    /// <see cref="HealthyFreshnessThresholdSeconds"/> for the exact comparison Program.cs uses) —
    /// resets the recovery streak and clears the backoff gate, so a wall that has visibly
    /// recovered on its own doesn't carry a stale, ever-growing backoff into its NEXT unrelated
    /// outage. Independent of <see cref="RecordSuccess"/>, which only tracks consecutive REFRESH
    /// TICK success/failure, not frame freshness.
    /// </summary>
    public void MarkHealthy()
    {
        _recoveryStreak = 0;
        _nextAllowedRecoveryUtc = null;
        _suppressionWarningLogged = false;
    }

    /// <summary>
    /// The freshness-age threshold, in seconds, below which <see cref="MarkHealthy"/> should be
    /// called — <c>max(<paramref name="configuredStaleSeconds"/>, 10)</c>. The 10s floor exists
    /// because <c>StaleSeconds: 0</c> is a documented-valid config value (disables the per-tile
    /// STALLED overlay — see <see cref="WallForm"/> — but session-loss recovery still uses the 60s
    /// floor from <see cref="StaleTriggerThresholdSeconds"/>); without this floor, "age &lt;
    /// configuredStaleSeconds" can never be true at StaleSeconds: 0 (age is never negative), so
    /// <see cref="MarkHealthy"/> would never fire and a wall running with StaleSeconds: 0 would be
    /// permanently stuck at the 900s backoff cap after its first two or three recoveries, forever —
    /// even once frames are flowing perfectly normally again.
    /// </summary>
    public static int HealthyFreshnessThresholdSeconds(int configuredStaleSeconds) =>
        Math.Max(configuredStaleSeconds, 10);

    /// <summary>
    /// True when Program.cs's refresh tick should call <see cref="MarkHealthy"/> this tick — requires
    /// BOTH <paramref name="freshestIsRealFrame"/> AND an age under
    /// <see cref="HealthyFreshnessThresholdSeconds"/>.
    ///
    /// WHY THE REAL-FRAME REQUIREMENT (round-3 panel-3 T1 fix). Before this method, the age check
    /// ran alone. <c>TileFreshnessTracker.ComputeFreshestAgeSeconds</c> reports a NEVER-framed wall
    /// form's age from its own wall-shown baseline (see that type's doc comment) — so immediately
    /// after a mid-session recovery rebuild, while the underlying outage is STILL ongoing, the very
    /// first refresh tick sees an age of roughly one <c>ConfigRefreshSeconds</c> interval with ZERO
    /// real frames received. That age is UNDER the healthy threshold whenever
    /// <c>ConfigRefreshSeconds &lt; max(StaleSeconds, 10)</c> or <c>StaleSeconds &gt;
    /// ConfigRefreshSeconds</c> — both legal configs — so the age-only check wrongly called
    /// <see cref="MarkHealthy"/> and reset the recovery backoff streak on no evidence the outage had
    /// actually ended. The staleness TRIGGER path (<see cref="IsStalenessTriggered"/>) is
    /// deliberately NOT changed to require a real frame — that behavior (baseline-inclusive age
    /// counts toward tripping recovery) is correct and unrelated: a wall that never reconnects after
    /// a rebuild is exactly the failure mode recovery exists to catch.
    /// </summary>
    public static bool ShouldMarkHealthy(double? freshestTileAgeSeconds, bool freshestIsRealFrame, int configuredStaleSeconds)
    {
        return freshestTileAgeSeconds is not null
            && freshestIsRealFrame
            && freshestTileAgeSeconds.Value < HealthyFreshnessThresholdSeconds(configuredStaleSeconds);
    }

    /// <summary>
    /// Call when a recovery trigger fires but <see cref="CanRecover"/> says no (still gated).
    /// Returns true exactly once per gate window — the caller should log a Warning only on a true
    /// result — so a tick-by-tick trigger condition that persists through an entire backoff window
    /// produces exactly one log line, not one per tick. The one-shot resets automatically the next
    /// time <see cref="RecordRecovery"/> or <see cref="MarkHealthy"/> runs, so a NEW gate window (or
    /// a healthy reset) gets its own single warning if it, too, ends up suppressed later.
    /// </summary>
    public bool ShouldLogSuppressionWarning()
    {
        if (_suppressionWarningLogged)
        {
            return false;
        }

        _suppressionWarningLogged = true;
        return true;
    }
}
