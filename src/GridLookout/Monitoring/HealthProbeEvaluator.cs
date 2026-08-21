namespace GridLookout.Monitoring;

/// <summary>Exit codes for <c>GridLookout.exe --health-probe</c> — see
/// <see cref="HealthProbeEvaluator.Evaluate"/> and <c>HealthProbe</c> (the IO-touching
/// orchestration around this pure evaluator). Values are the CONTRACT the watchdog scheduled task
/// (<c>scripts/install-kiosk.ps1</c>) is written against — do not renumber.</summary>
public enum ProbeExitCode
{
    Healthy = 0,
    Degraded = 1,
    UnhealthyOrHung = 2,
    Absent = 3,
}

/// <summary>One probe verdict — see <see cref="HealthProbeEvaluator.Evaluate"/>.
/// <see cref="NewStreak"/> (M4 fix, 2026-08-21 external audit) is the hung-suspicion marker the
/// CALLER must persist for the next probe run (null = no suspicion → delete any stored marker):
/// the evaluator stays pure, <c>HealthProbe.Run</c> owns the file IO.</summary>
public sealed record ProbeVerdict(ProbeExitCode ExitCode, OverallStatus? Status, string Reason, HungStreakMarker? NewStreak = null);

/// <summary>M4 fix (2026-08-21 external audit): persisted between <c>--health-probe</c> runs (each
/// run is a separate short-lived process) so a hung verdict requires CONFIRMATION across runs —
/// see <see cref="HealthProbeEvaluator.Evaluate"/>'s stale-pulse branch. <see cref="StalePulseUtc"/>
/// is the exact pulse value that raised suspicion (a pulse that ADVANCED between probes, however
/// slowly, resets the streak — a slow-but-alive pump is not a hang); <see cref="FirstObservedUtc"/>
/// anchors the minimum confirmation window.</summary>
public sealed record HungStreakMarker(DateTime StalePulseUtc, DateTime FirstObservedUtc);

/// <summary>
/// Pure verdict logic for <c>--health-probe</c> mode — no file IO, no <c>Process</c> class, so it's
/// unit-testable against synthetic <see cref="WallHealthState"/> values. See <c>HealthProbe</c> for
/// the IO-touching orchestration around this (reading health.json, matching the recorded pid against
/// a live process, printing the verdict, optionally POSTing).
/// </summary>
public static class HealthProbeEvaluator
{
    /// <summary>
    /// <see cref="System.Diagnostics.Process.StartTime"/> and a <see cref="DateTime"/> round-tripped
    /// through JSON can differ by up to roughly a second due to OS-reported precision and
    /// serialization rounding — anything within this tolerance is judged the SAME process; anything
    /// further apart means the recorded pid has been reused by a DIFFERENT process since
    /// health.json was written (the classic pid-reuse false-positive a plain "does a process with
    /// this name exist" check — the pre-existing watchdog's old behavior — could never catch).
    /// </summary>
    private static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>True when <paramref name="actualStartUtc"/> (a live process's own reported start
    /// time) is close enough to <paramref name="recordedStartUtc"/> (the value health.json recorded)
    /// to be judged the same process instance — see <see cref="ProcessStartTolerance"/>.</summary>
    public static bool ProcessStartMatches(DateTime recordedStartUtc, DateTime actualStartUtc)
    {
        return (recordedStartUtc - actualStartUtc).Duration() < ProcessStartTolerance;
    }

    /// <summary>
    /// The full probe decision. <paramref name="state"/> is null when health.json is absent,
    /// unreadable, or fails to deserialize — treated identically to "no controller has ever reported
    /// in", i.e. <see cref="ProbeExitCode.Absent"/>. <paramref name="pidAndStartTimeMatchLiveProcess"/>
    /// is computed by the caller (needs <c>System.Diagnostics.Process</c> — see
    /// <see cref="ProcessStartMatches"/> for the comparison this should be built from) — false here
    /// is ALSO <see cref="ProbeExitCode.Absent"/>: a health.json whose recorded process is gone (or
    /// whose pid was reused by something else entirely) describes a controller that, from the
    /// outside, is exactly as absent as one that never wrote a file at all.
    /// </summary>
    public static ProbeVerdict Evaluate(WallHealthState? state, bool pidAndStartTimeMatchLiveProcess, DateTime nowUtc, int staleAfterSeconds, HungStreakMarker? priorStreak = null)
    {
        if (state is null)
        {
            return new ProbeVerdict(ProbeExitCode.Absent, null, "health.json not found, unreadable, or malformed");
        }

        if (!pidAndStartTimeMatchLiveProcess)
        {
            return new ProbeVerdict(ProbeExitCode.Absent, null, "recorded pid/process-start-time in health.json does not match a live process");
        }

        // ConfigError is a deliberately-parked state (an unconfigured kiosk sitting on the "not
        // configured" card, awaiting an admin) — never classify it as hung/unhealthy just because
        // its uiPulseUtc has necessarily gone stale while parked there, or a customer who opts in to
        // -RestartHung would get a restart loop on a box that is doing exactly what it should.
        if (state.ControllerState == ControllerState.ConfigError)
        {
            return new ProbeVerdict(ProbeExitCode.Degraded, OverallStatus.Degraded, "controller is parked awaiting configuration (ConfigError)");
        }

        // Starting/Connecting cover the SDK's blocking, synchronous session.Login() /
        // RecorderCatalog.Discover() / RecorderLocator.Locate() calls — no WinForms message pump
        // runs while one is in flight, so the UI pulse legitimately stops advancing for however long
        // a slow-but-not-actually-hung connection attempt takes (an unreachable Management Server's
        // TCP timeout alone can exceed a low StaleAfterSeconds). Buyer-review defect #3 made
        // health.json exist from process start, which put this window in front of the probe for the
        // first time — previously boot was invisible (Absent) and nothing could trip -RestartHung
        // during it. Grant the SAME grace multiplier the watchdog's own absent+long-running rule uses
        // (install-kiosk.ps1 — "process running longer than 3x StaleAfterSeconds") so a merely-slow
        // boot/reconnect isn't misjudged as hung and killed mid-connection into a kill loop. A
        // genuinely wedged Connecting state is still caught — just at 3x instead of 1x. Running and
        // the brief Recovering bookkeeping window keep the tight 1x threshold: once a monitor is
        // Running, a stale pulse is exactly what a hung message pump looks like.
        const int StartupGraceMultiplier = 3;
        int effectiveStaleAfterSeconds = state.ControllerState is ControllerState.Starting or ControllerState.Connecting
            ? staleAfterSeconds * StartupGraceMultiplier
            : staleAfterSeconds;

        var pulseAgeSeconds = (nowUtc - state.UiPulseUtc).TotalSeconds;
        bool uiPulseFresh = pulseAgeSeconds < effectiveStaleAfterSeconds;
        if (!uiPulseFresh)
        {
            // M4 fix (2026-08-21 external audit): the hung verdict was previously SINGLE-SAMPLE —
            // one stale pulse ⇒ exit 2 ⇒ with -RestartHung, an immediate Stop-Process. But the
            // refresh tick runs synchronous SDK calls on the UI thread while Running (the exact
            // blocking the startup-grace comment above acknowledges for boot), so one slow
            // Management Server response — a half-open TCP connection can block 40-60s — read as
            // "hung" and got a HEALTHY wall killed. Hysteresis: the first stale observation is
            // DEGRADED ("suspected"), and only a later probe run that finds the SAME pulse value
            // still unmoved, at least staleAfterSeconds after suspicion was first raised, confirms
            // UnhealthyOrHung. A pulse that advanced between runs — however slowly — resets the
            // streak: a slow pump is degraded, not dead. The marker rides between probe processes
            // via NewStreak (see ProbeVerdict) — HealthProbe.Run persists/deletes it.
            if (priorStreak is not null
                && priorStreak.StalePulseUtc == state.UiPulseUtc
                && (nowUtc - priorStreak.FirstObservedUtc).TotalSeconds >= staleAfterSeconds)
            {
                return new ProbeVerdict(ProbeExitCode.UnhealthyOrHung, OverallStatus.Unhealthy,
                    $"UI pulse stale ({pulseAgeSeconds:F0}s old, threshold {effectiveStaleAfterSeconds}s) and unchanged since first flagged " +
                    $"{(nowUtc - priorStreak.FirstObservedUtc).TotalSeconds:F0}s ago — message pump hung (confirmed across probe runs)",
                    priorStreak);
            }

            var streak = priorStreak is not null && priorStreak.StalePulseUtc == state.UiPulseUtc
                ? priorStreak
                : new HungStreakMarker(state.UiPulseUtc, nowUtc);
            return new ProbeVerdict(ProbeExitCode.Degraded, OverallStatus.Degraded,
                $"UI pulse stale ({pulseAgeSeconds:F0}s old, threshold {effectiveStaleAfterSeconds}s) — suspected hang, awaiting a " +
                "confirming probe run before reporting hung (one sample can't distinguish a hang from a blocking SDK call on a slow Management Server)",
                streak);
        }

        // Recomputed from the raw per-tile aggregates (and, buyer-review defect #1 fix, the
        // persisted ControllerState/RecorderSelectionIncomplete signals too) rather than trusting
        // state.OverallStatus as stored — see HealthStatusCalculator's own doc comment for why both
        // sides share this one rule instead of the probe blindly trusting whatever the controller
        // wrote. This is also what lets the PROBE reach OverallStatus.Unhealthy on a live wall now
        // (Running with zero forms) even though uiPulseFresh is true — see the switch arm below.
        //
        // FIX 2: state.LayoutCarrierPinned is OR'd in alongside RecorderSelectionIncomplete — a
        // pinned-but-missing layout carrier is a Degraded condition exactly like an incomplete
        // recorder selection is, and HealthStatusCalculator.Compute takes a single "recorder
        // selection is degraded" bool rather than one parameter per specific cause; the two flags
        // still surface SEPARATELY in health.json itself (see WallHealthState.LayoutCarrierPinned's
        // own doc comment for why they're distinct fields) so an external collector can tell them
        // apart even though they fold together here.
        var recomputed = HealthStatusCalculator.Compute(uiPulseFresh: true, state.ControllerState, state.Forms, state.RecorderSelectionIncomplete || state.LayoutCarrierPinned, uiThreadExceptionsObserved: state.UiThreadExceptionCount > 0);
        return recomputed switch
        {
            OverallStatus.Healthy => new ProbeVerdict(ProbeExitCode.Healthy, OverallStatus.Healthy, "all tiles rendering fresh, UI responsive"),
            OverallStatus.Degraded => new ProbeVerdict(ProbeExitCode.Degraded, OverallStatus.Degraded, "UI responsive but one or more tiles stalled/never-framed/unavailable, a configured recorder is missing, the pinned layout carrier is currently unmatched, or the process has swallowed a UI-thread exception (M3 — sticky until restart)"),
            OverallStatus.Unhealthy => new ProbeVerdict(ProbeExitCode.UnhealthyOrHung, OverallStatus.Unhealthy, "UI responsive but the controller is Running with zero configured wall windows — nothing is actually showing"),
            _ => new ProbeVerdict(ProbeExitCode.UnhealthyOrHung, OverallStatus.Unhealthy, "unexpected recomputed state"),
        };
    }
}
