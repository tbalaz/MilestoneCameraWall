using GridLookout.Monitoring;
using Xunit;

namespace GridLookout.Tests.Monitoring;

public class HealthProbeEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    private static WallHealthState FreshState(DateTime? uiPulseUtc = null, ControllerState controllerState = ControllerState.Running) => new()
    {
        SchemaVersion = 1,
        ControllerId = "wall-01",
        Pid = 4242,
        ProcessStartUtc = Now.AddHours(-1),
        UiPulseUtc = uiPulseUtc ?? Now,
        ControllerState = controllerState,
        Forms = new List<WallFormHealth>
        {
            new() { MonitorNumber = 1, ExpectedTileCount = 4, TilesWithFrames = 4, StalledCount = 0, NeverFramedCount = 0, FreshestRenderedAgeSeconds = 1.0 },
        },
        OverallStatus = OverallStatus.Healthy,
        WrittenUtc = Now,
    };

    // --- Absent ---

    [Fact]
    public void Evaluate_NullState_Absent()
    {
        var verdict = HealthProbeEvaluator.Evaluate(null, pidAndStartTimeMatchLiveProcess: false, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Absent, verdict.ExitCode);
        Assert.Null(verdict.Status);
    }

    [Fact]
    public void Evaluate_StateExistsButPidDoesNotMatchLiveProcess_Absent()
    {
        var state = FreshState();

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: false, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Absent, verdict.ExitCode);
    }

    // --- Stale pulse -> suspected on first sample, confirmed across probe runs (M4 hysteresis) ---

    [Fact]
    public void Evaluate_UiPulseOlderThanStaleAfterSeconds_FirstObservation_Suspected_DegradedWithStreak()
    {
        // M4 fix (2026-08-21 external audit): one stale sample can no longer mean hung — a single
        // 31s-old pulse is indistinguishable from a healthy wall blocked in a slow synchronous SDK
        // call on the UI thread. The FIRST observation is Degraded ("suspected") and hands back the
        // streak marker the caller must persist; only a CONFIRMING later run may report hung.
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-31));

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
        Assert.Equal(OverallStatus.Degraded, verdict.Status);
        Assert.NotNull(verdict.NewStreak);
        Assert.Equal(state.UiPulseUtc, verdict.NewStreak!.StalePulseUtc);
        Assert.Equal(Now, verdict.NewStreak.FirstObservedUtc);
    }

    [Fact]
    public void Evaluate_UnchangedStalePulseAcrossRuns_AfterConfirmationWindow_Confirmed_UnhealthyOrHung()
    {
        // The confirming run: SAME pulse value as flagged before (the pump never advanced it), and
        // at least staleAfterSeconds since suspicion was first raised — only now is exit 2 legal.
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-61));
        var first = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);
        Assert.Equal(ProbeExitCode.Degraded, first.ExitCode);
        Assert.NotNull(first.NewStreak);

        var second = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now.AddSeconds(35), staleAfterSeconds: 30, priorStreak: first.NewStreak);

        Assert.Equal(ProbeExitCode.UnhealthyOrHung, second.ExitCode);
        Assert.Equal(OverallStatus.Unhealthy, second.Status);
    }

    [Fact]
    public void Evaluate_SamePulseBeforeConfirmationWindowElapses_StillSuspected_NotConfirmed()
    {
        // Same unmoved pulse, but the second run came only 5s after suspicion was first raised —
        // below the staleAfterSeconds confirmation floor. Still Degraded, still carrying the SAME
        // streak marker (FirstObservedUtc unchanged so the window eventually completes).
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-31));
        var first = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        var second = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now.AddSeconds(5), staleAfterSeconds: 30, priorStreak: first.NewStreak);

        Assert.Equal(ProbeExitCode.Degraded, second.ExitCode);
        Assert.NotNull(second.NewStreak);
        Assert.Equal(first.NewStreak!.FirstObservedUtc, second.NewStreak.FirstObservedUtc);
    }

    [Fact]
    public void Evaluate_PulseAdvancedBetweenRuns_ResetsSuspicion_FreshVerdict_ClearsStreak()
    {
        // A pulse that ADVANCED between probe runs — however slowly — resets the streak: a slow
        // pump is degraded-at-worst, not dead. NewStreak is null so the caller deletes any stored
        // marker.
        var suspectState = FreshState(uiPulseUtc: Now.AddSeconds(-31));
        var first = HealthProbeEvaluator.Evaluate(suspectState, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);
        Assert.NotNull(first.NewStreak);

        var advancedState = FreshState(uiPulseUtc: Now.AddSeconds(-1));

        var second = HealthProbeEvaluator.Evaluate(advancedState, pidAndStartTimeMatchLiveProcess: true, Now.AddSeconds(10), staleAfterSeconds: 30, priorStreak: first.NewStreak);

        Assert.Equal(ProbeExitCode.Healthy, second.ExitCode);
        Assert.Null(second.NewStreak);
    }

    [Fact]
    public void Evaluate_UiPulseExactlyAtStaleAfterSeconds_TreatedAsStale_SuspectedNotYetHung()
    {
        // Age must be STRICTLY less than the threshold to count as fresh — an exact-boundary pulse
        // is judged stale, matching HealthStatusCalculator's own "< " comparison. Under M4 that
        // first stale sample is "suspected" (Degraded + streak), never immediately hung.
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-30));

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.NotEqual(ProbeExitCode.UnhealthyOrHung, verdict.ExitCode);
        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
        Assert.NotNull(verdict.NewStreak);
    }

    // --- Starting/Connecting grace multiplier (blocking SDK Login()/Discover()/Locate() calls
    // freeze the message pump, so the pulse legitimately can't advance during them) ---

    [Theory]
    [InlineData(ControllerState.Starting)]
    [InlineData(ControllerState.Connecting)]
    public void Evaluate_StartingOrConnecting_PulseStaleWithinGraceMultiplier_NotYetHung(ControllerState controllerState)
    {
        // 31s old would trip the normal 30s threshold, but Starting/Connecting get 3x (90s).
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-31), controllerState: controllerState);
        state.Forms = new List<WallFormHealth>();

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.NotEqual(ProbeExitCode.UnhealthyOrHung, verdict.ExitCode);
        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
    }

    [Theory]
    [InlineData(ControllerState.Starting)]
    [InlineData(ControllerState.Connecting)]
    public void Evaluate_StartingOrConnecting_PulseStaleBeyondGraceMultiplier_SuspectedThenConfirmed_Hung(ControllerState controllerState)
    {
        // 91s old trips the 3x grace (90s) — but under M4 the first such sample is still only
        // "suspected"; hung requires a confirming second run on the same unmoved pulse.
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-91), controllerState: controllerState);

        var first = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);
        Assert.Equal(ProbeExitCode.Degraded, first.ExitCode);
        Assert.NotNull(first.NewStreak);

        var second = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now.AddSeconds(35), staleAfterSeconds: 30, priorStreak: first.NewStreak);
        Assert.Equal(ProbeExitCode.UnhealthyOrHung, second.ExitCode);
    }

    [Fact]
    public void Evaluate_Recovering_NoGraceMultiplier_StaleAt31Seconds_SuspectedThenConfirmed_Hung()
    {
        // Recovering only covers the brief session.Logout()/bookkeeping window — LoginRetryLoop
        // moves the state to Connecting before its own blocking calls run — so Recovering keeps the
        // tight 1x threshold rather than the Starting/Connecting grace (31s is already stale here,
        // where the same age inside Starting/Connecting would not even be suspected). Confirmation
        // across runs still applies per M4.
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-31), controllerState: ControllerState.Recovering);

        var first = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);
        Assert.Equal(ProbeExitCode.Degraded, first.ExitCode);
        Assert.NotNull(first.NewStreak);

        var second = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now.AddSeconds(31), staleAfterSeconds: 30, priorStreak: first.NewStreak);
        Assert.Equal(ProbeExitCode.UnhealthyOrHung, second.ExitCode);
    }

    // --- Fresh, healthy ---

    [Fact]
    public void Evaluate_FreshPulseAndAllTilesRendering_Healthy()
    {
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-1));

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Healthy, verdict.ExitCode);
        Assert.Equal(OverallStatus.Healthy, verdict.Status);
    }

    // --- Fresh, degraded ---

    [Fact]
    public void Evaluate_FreshPulseButOneTileStalled_Degraded()
    {
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-1));
        state.Forms[0].StalledCount = 1;

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
        Assert.Equal(OverallStatus.Degraded, verdict.Status);
    }

    [Fact]
    public void Evaluate_RecomputesFromRawAggregates_DoesNotTrustStoredOverallStatusBlindly()
    {
        // state.OverallStatus SAYS Healthy, but the per-tile aggregates say otherwise — the probe
        // must recompute independently, not trust the stored field.
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-1));
        state.Forms[0].NeverFramedCount = 1;
        state.OverallStatus = OverallStatus.Healthy; // deliberately wrong/stale

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
    }

    // --- Buyer-review defect #1: false-green health matrix, exercised end-to-end through Evaluate ---

    [Fact]
    public void Evaluate_RunningWithZeroForms_UnhealthyOrHung()
    {
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-1), controllerState: ControllerState.Running);
        state.Forms = new List<WallFormHealth>();

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.UnhealthyOrHung, verdict.ExitCode);
        Assert.Equal(OverallStatus.Unhealthy, verdict.Status);
    }

    [Theory]
    [InlineData(ControllerState.Starting)]
    [InlineData(ControllerState.Connecting)]
    [InlineData(ControllerState.Recovering)]
    public void Evaluate_TransitionalControllerStateWithZeroForms_Degraded_NotUnhealthy(ControllerState controllerState)
    {
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-1), controllerState: controllerState);
        state.Forms = new List<WallFormHealth>();

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
        Assert.Equal(OverallStatus.Degraded, verdict.Status);
    }

    [Fact]
    public void Evaluate_AnyUnavailableCell_Degraded_EvenWithNoStalledOrNeverFramedTiles()
    {
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-1));
        state.Forms[0].UnavailableCount = 2;

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
        Assert.Equal(OverallStatus.Degraded, verdict.Status);
    }

    [Fact]
    public void Evaluate_RecorderSelectionIncomplete_Degraded_EvenWithHealthyTiles()
    {
        // The persisted signal a configured RecordingServers[] selector matching no live recorder
        // is threaded through — see WallHealthState.RecorderSelectionIncomplete's own doc comment
        // for why this is a field on disk rather than folded silently into OverallStatus alone
        // (the external probe has no other way to reach the same verdict the controller does).
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-1));
        state.RecorderSelectionIncomplete = true;

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
        Assert.Equal(OverallStatus.Degraded, verdict.Status);
    }

    [Fact]
    public void Evaluate_LayoutCarrierPinned_Degraded_EvenWithHealthyTiles()
    {
        // FIX 2 (pinned carrier authority): a SEPARATE persisted signal from
        // RecorderSelectionIncomplete (see WallHealthState.LayoutCarrierPinned's own doc comment for
        // why) — an explicit multi-mode LayoutRecorder currently unmatched is Degraded on its own,
        // even when every RecordingServers[] selector still matches fine and every tile is healthy.
        var state = FreshState(uiPulseUtc: Now.AddSeconds(-1));
        state.LayoutCarrierPinned = true;

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
        Assert.Equal(OverallStatus.Degraded, verdict.Status);
    }

    // --- ConfigError short-circuit ---

    [Fact]
    public void Evaluate_ConfigErrorState_NeverClassifiedAsHungEvenWithStalePulse()
    {
        var state = FreshState(uiPulseUtc: Now.AddHours(-2), controllerState: ControllerState.ConfigError);

        var verdict = HealthProbeEvaluator.Evaluate(state, pidAndStartTimeMatchLiveProcess: true, Now, staleAfterSeconds: 30);

        Assert.NotEqual(ProbeExitCode.UnhealthyOrHung, verdict.ExitCode);
        Assert.Equal(ProbeExitCode.Degraded, verdict.ExitCode);
    }

    // --- ProcessStartMatches ---

    [Fact]
    public void ProcessStartMatches_IdenticalTimestamps_True()
    {
        var t = Now;
        Assert.True(HealthProbeEvaluator.ProcessStartMatches(t, t));
    }

    [Fact]
    public void ProcessStartMatches_WithinToleranceRounding_True()
    {
        Assert.True(HealthProbeEvaluator.ProcessStartMatches(Now, Now.AddMilliseconds(900)));
        Assert.True(HealthProbeEvaluator.ProcessStartMatches(Now, Now.AddMilliseconds(-900)));
    }

    [Fact]
    public void ProcessStartMatches_BeyondTolerance_False()
    {
        // A different process reusing the same pid started at a materially different time.
        Assert.False(HealthProbeEvaluator.ProcessStartMatches(Now, Now.AddMinutes(5)));
    }
}
