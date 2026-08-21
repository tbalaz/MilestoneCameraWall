namespace GridLookout.Monitoring;

/// <summary>
/// The one classification rule shared by BOTH sides of the health feature — the controller
/// (self-reporting into <c>health.json</c>, where <c>uiPulseFresh</c> is trivially true because the
/// write is happening right now) and the external <c>--health-probe</c> reader (recomputing
/// independently from the file's age, where <c>uiPulseFresh</c> can genuinely be false — see
/// <c>HealthProbeEvaluator</c>). Reusing this ONE method from both sides means the probe never has
/// to trust a stored <see cref="OverallStatus"/> value blindly; it recomputes from the raw per-tile
/// aggregates every time, which also makes this the single place the classification rule can ever
/// drift from what both call sites intend.
///
/// Buyer-review defect #1 fix — the pre-fix rule only ever looked at <see cref="WallFormHealth.StalledCount"/>/
/// <see cref="WallFormHealth.NeverFramedCount"/>, so a wall that was entirely UNAVAILABLE, entirely
/// absent (zero forms), mid-reconnect, or missing a configured recorder could all report Healthy —
/// four separate ways for a visibly broken wall to look green externally. The matrix below closes
/// all four, in a specific order that matters (see the inline comments at each check).
/// </summary>
public static class HealthStatusCalculator
{
    /// <summary>
    /// Checked in this exact order — later checks assume every earlier one already passed:
    /// <list type="number">
    /// <item><b>UI pulse stale</b> → <see cref="OverallStatus.Unhealthy"/>, unconditionally. A hung
    /// message pump makes every other signal in <paramref name="forms"/> untrustworthy (frozen at
    /// whatever they last held before the hang), so nothing below is even worth inspecting.</item>
    /// <item><b>Transitional <paramref name="controllerState"/></b> (<see cref="ControllerState.Starting"/>/
    /// <see cref="ControllerState.Connecting"/>/<see cref="ControllerState.Recovering"/>) →
    /// <see cref="OverallStatus.Degraded"/>. MUST be checked before the zero-forms rule below: a
    /// booting or mid-reconnect wall legitimately has zero (or a stale set of) forms yet, and that is
    /// expected transient behavior, not a broken wall — Degraded communicates "not fully up" without
    /// crying Unhealthy over normal startup/reconnect.</item>
    /// <item><b><see cref="ControllerState.Running"/> with ZERO <paramref name="forms"/></b> →
    /// <see cref="OverallStatus.Unhealthy"/>. Once the controller claims to be Running, zero
    /// configured wall windows means nothing is actually showing — the false-green case the review
    /// specifically named ("An all-red UNAVAILABLE wall—or even no wall windows—can therefore be
    /// green externally").</item>
    /// <item><b>Any UNAVAILABLE cell, any stalled/never-framed tile, or an incomplete recorder
    /// selection</b> → <see cref="OverallStatus.Degraded"/>. Previously only stalled/never-framed
    /// tiles counted; an all-UNAVAILABLE wall (every configured camera missing/disabled) and a
    /// configured-but-vanished recorder (<paramref name="recorderSelectionIncomplete"/> — see
    /// <c>WallHealthState.RecorderSelectionIncomplete</c>'s own doc comment) now both count too.</item>
    /// <item>Otherwise → <see cref="OverallStatus.Healthy"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="uiThreadExceptionsObserved">M3 fix (2026-08-21 external audit): true when the
    /// process has swallowed at least one <c>Application.ThreadException</c> — see
    /// <see cref="WallHealthState.UiThreadExceptionCount"/>'s doc comment for why this degrades
    /// (sticky, never relaunches) rather than staying invisible.</param>
    public static OverallStatus Compute(
        bool uiPulseFresh,
        ControllerState controllerState,
        IReadOnlyList<WallFormHealth> forms,
        bool recorderSelectionIncomplete,
        bool uiThreadExceptionsObserved = false)
    {
        if (!uiPulseFresh)
        {
            return OverallStatus.Unhealthy;
        }

        if (controllerState is ControllerState.Starting or ControllerState.Connecting or ControllerState.Recovering)
        {
            return OverallStatus.Degraded;
        }

        if (controllerState == ControllerState.Running && forms.Count == 0)
        {
            return OverallStatus.Unhealthy;
        }

        bool anyUnavailableCell = forms.Any(f => f.UnavailableCount > 0);
        bool anyDegradedTile = forms.Any(f => f.StalledCount > 0 || f.NeverFramedCount > 0);

        return anyUnavailableCell || anyDegradedTile || recorderSelectionIncomplete || uiThreadExceptionsObserved
            ? OverallStatus.Degraded
            : OverallStatus.Healthy;
    }
}
