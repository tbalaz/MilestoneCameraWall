using GridLookout.Recovery;
using Xunit;

namespace GridLookout.Tests.Recovery;

/// <summary>
/// Covers <see cref="SessionLossDetector"/>, the B5/E1 mid-session recovery trigger — two
/// independent signals (consecutive failed refresh ticks, and "every tile stale" duration), either
/// sufficient on its own. See Program.cs's refresh timer / RecoverSession for how these are wired
/// to a live VMS session; this class itself is pure counters/thresholds.
/// </summary>
public class SessionLossDetectorTests
{
    [Fact]
    public void RecordFailure_BelowThreshold_DoesNotTrigger()
    {
        var detector = new SessionLossDetector();

        Assert.False(detector.RecordFailure());
        Assert.False(detector.RecordFailure());
    }

    [Fact]
    public void RecordFailure_AtThreshold_Triggers()
    {
        var detector = new SessionLossDetector();

        detector.RecordFailure();
        detector.RecordFailure();
        bool triggered = detector.RecordFailure();

        Assert.True(triggered);
        Assert.Equal(3, SessionLossDetector.ConsecutiveFailureThreshold);
        Assert.Equal(3, detector.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFailure_PastThreshold_KeepsReturningTrue()
    {
        // A caller that (for whatever reason) doesn't act on the first true result must still be
        // told "yes, recover" on every subsequent failure, not just the exact threshold tick.
        var detector = new SessionLossDetector();

        detector.RecordFailure();
        detector.RecordFailure();
        detector.RecordFailure();
        bool fourth = detector.RecordFailure();

        Assert.True(fourth);
    }

    [Fact]
    public void RecordSuccess_ResetsConsecutiveFailureCount()
    {
        var detector = new SessionLossDetector();
        detector.RecordFailure();
        detector.RecordFailure();

        detector.RecordSuccess();

        Assert.Equal(0, detector.ConsecutiveFailures);
        // Two more failures after a reset must NOT trigger — the count truly restarted.
        Assert.False(detector.RecordFailure());
        Assert.False(detector.RecordFailure());
    }

    [Fact]
    public void RecordSuccess_ThenThreeMoreFailures_TriggersAgain()
    {
        var detector = new SessionLossDetector();
        detector.RecordFailure();
        detector.RecordSuccess();

        detector.RecordFailure();
        detector.RecordFailure();
        bool triggered = detector.RecordFailure();

        Assert.True(triggered);
    }

    [Fact]
    public void Reset_ClearsConsecutiveFailureCount()
    {
        var detector = new SessionLossDetector();
        detector.RecordFailure();
        detector.RecordFailure();

        detector.Reset();

        Assert.Equal(0, detector.ConsecutiveFailures);
        Assert.False(detector.RecordFailure());
        Assert.False(detector.RecordFailure());
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(10, 60)]
    [InlineData(20, 60)]
    [InlineData(30, 90)]
    [InlineData(60, 180)]
    public void StaleTriggerThresholdSeconds_IsMaxOfFloorAndTripleConfiguredStaleSeconds(int configuredStaleSeconds, int expectedThreshold)
    {
        Assert.Equal(expectedThreshold, SessionLossDetector.StaleTriggerThresholdSeconds(configuredStaleSeconds));
    }

    [Fact]
    public void IsStalenessTriggered_NullAge_IsFalse()
    {
        Assert.False(SessionLossDetector.IsStalenessTriggered(null, configuredStaleSeconds: 10));
    }

    [Fact]
    public void IsStalenessTriggered_AgeAtThreshold_IsFalse()
    {
        // Strictly greater-than, not >=, at the boundary — see IsStalenessTriggered's implementation.
        var threshold = SessionLossDetector.StaleTriggerThresholdSeconds(10);
        Assert.False(SessionLossDetector.IsStalenessTriggered(threshold, configuredStaleSeconds: 10));
    }

    [Fact]
    public void IsStalenessTriggered_AgeJustOverThreshold_IsTrue()
    {
        var threshold = SessionLossDetector.StaleTriggerThresholdSeconds(10);
        Assert.True(SessionLossDetector.IsStalenessTriggered(threshold + 0.1, configuredStaleSeconds: 10));
    }

    [Fact]
    public void IsStalenessTriggered_AgeWellUnderThreshold_IsFalse()
    {
        Assert.False(SessionLossDetector.IsStalenessTriggered(5, configuredStaleSeconds: 10));
    }

    [Fact]
    public void IsStalenessTriggered_ZeroConfiguredStaleSeconds_StillUsesSixtySecondFloor()
    {
        // StaleSeconds: 0 disables the per-tile STALLED overlay, but session-loss recovery must
        // still eventually fire on a truly dead wall — the 60s floor applies regardless.
        Assert.False(SessionLossDetector.IsStalenessTriggered(59, configuredStaleSeconds: 0));
        Assert.True(SessionLossDetector.IsStalenessTriggered(61, configuredStaleSeconds: 0));
    }

    // --- T2/R2: recovery backoff ---

    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CanRecover_BeforeAnyRecoveryEverRecorded_ReturnsTrue()
    {
        var detector = new SessionLossDetector();

        Assert.True(detector.CanRecover(Now));
        Assert.Null(detector.NextAllowedRecoveryUtc);
    }

    [Fact]
    public void RecordRecovery_First_GatesForSixtySeconds()
    {
        var detector = new SessionLossDetector();

        detector.RecordRecovery(Now);

        Assert.Equal(Now.AddSeconds(60), detector.NextAllowedRecoveryUtc);
        Assert.False(detector.CanRecover(Now.AddSeconds(59)));
        Assert.True(detector.CanRecover(Now.AddSeconds(60)));
    }

    [Fact]
    public void RecordRecovery_Sequence_DoublesEachTime_60_120_240_480()
    {
        var detector = new SessionLossDetector();
        var now = Now;

        detector.RecordRecovery(now);
        Assert.Equal(now.AddSeconds(60), detector.NextAllowedRecoveryUtc);
        Assert.Equal(1, detector.RecoveryStreak);

        now = now.AddSeconds(60);
        detector.RecordRecovery(now);
        Assert.Equal(now.AddSeconds(120), detector.NextAllowedRecoveryUtc);
        Assert.Equal(2, detector.RecoveryStreak);

        now = now.AddSeconds(120);
        detector.RecordRecovery(now);
        Assert.Equal(now.AddSeconds(240), detector.NextAllowedRecoveryUtc);
        Assert.Equal(3, detector.RecoveryStreak);

        now = now.AddSeconds(240);
        detector.RecordRecovery(now);
        Assert.Equal(now.AddSeconds(480), detector.NextAllowedRecoveryUtc);
        Assert.Equal(4, detector.RecoveryStreak);
    }

    [Fact]
    public void RecordRecovery_FifthCallOnward_CapsAtNineHundredSeconds()
    {
        var detector = new SessionLossDetector();

        for (int i = 0; i < 4; i++)
        {
            detector.RecordRecovery(Now);
        }

        // 5th: uncapped would be 60*2^4 = 960 — clamped to 900.
        detector.RecordRecovery(Now);
        Assert.Equal(Now.AddSeconds(900), detector.NextAllowedRecoveryUtc);

        // 6th and beyond: still capped at 900, never grows further.
        detector.RecordRecovery(Now);
        Assert.Equal(Now.AddSeconds(900), detector.NextAllowedRecoveryUtc);
    }

    [Fact]
    public void MarkHealthy_ResetsStreakAndGate()
    {
        var detector = new SessionLossDetector();
        detector.RecordRecovery(Now);
        detector.RecordRecovery(Now.AddSeconds(60));
        Assert.False(detector.CanRecover(Now.AddSeconds(60)));

        detector.MarkHealthy();

        Assert.True(detector.CanRecover(Now.AddSeconds(60)));
        Assert.Null(detector.NextAllowedRecoveryUtc);
        Assert.Equal(0, detector.RecoveryStreak);

        // Streak genuinely restarted from zero — next RecordRecovery gates for 60s again, not 240s.
        detector.RecordRecovery(Now);
        Assert.Equal(Now.AddSeconds(60), detector.NextAllowedRecoveryUtc);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    [InlineData(20, 20)]
    public void HealthyFreshnessThresholdSeconds_IsMaxOfConfiguredAndTenSecondFloor(int configuredStaleSeconds, int expected)
    {
        Assert.Equal(expected, SessionLossDetector.HealthyFreshnessThresholdSeconds(configuredStaleSeconds));
    }

    [Fact]
    public void ShouldLogSuppressionWarning_FirstCallTrue_SubsequentCallsFalse_UntilNextGateWindow()
    {
        var detector = new SessionLossDetector();
        detector.RecordRecovery(Now);

        Assert.True(detector.ShouldLogSuppressionWarning());
        Assert.False(detector.ShouldLogSuppressionWarning());
        Assert.False(detector.ShouldLogSuppressionWarning());
    }

    [Fact]
    public void ShouldLogSuppressionWarning_ResetsOnNextRecordRecovery()
    {
        var detector = new SessionLossDetector();
        detector.RecordRecovery(Now);
        Assert.True(detector.ShouldLogSuppressionWarning());

        detector.RecordRecovery(Now.AddSeconds(60)); // a new gate window begins

        Assert.True(detector.ShouldLogSuppressionWarning());
    }

    [Fact]
    public void ShouldLogSuppressionWarning_ResetsOnMarkHealthy()
    {
        var detector = new SessionLossDetector();
        detector.RecordRecovery(Now);
        Assert.True(detector.ShouldLogSuppressionWarning());

        detector.MarkHealthy();
        detector.RecordRecovery(Now);

        Assert.True(detector.ShouldLogSuppressionWarning());
    }

    // --- Round-3 panel-3 T1: ShouldMarkHealthy requires a real frame, not just a small age ---

    [Fact]
    public void ShouldMarkHealthy_RealFrame_SmallAge_IsTrue()
    {
        Assert.True(SessionLossDetector.ShouldMarkHealthy(freshestTileAgeSeconds: 2, freshestIsRealFrame: true, configuredStaleSeconds: 10));
    }

    [Fact]
    public void ShouldMarkHealthy_NeverFramedForm_IsFalse_EvenWithConfigRefreshBelowStaleSecondsFloor()
    {
        // ConfigRefreshSeconds < max(StaleSeconds, 10): a never-framed young form's baseline-only age
        // (~one refresh interval) lands comfortably under the healthy threshold — exactly the config
        // the T1 bug fired under. Must still be false because freshestIsRealFrame is false.
        Assert.False(SessionLossDetector.ShouldMarkHealthy(freshestTileAgeSeconds: 5, freshestIsRealFrame: false, configuredStaleSeconds: 10));
    }

    [Fact]
    public void ShouldMarkHealthy_NeverFramedForm_IsFalse_EvenWithStaleSecondsAboveConfigRefresh()
    {
        // StaleSeconds > ConfigRefreshSeconds: the other legal config the task names — same
        // requirement, must still be false with no real frame regardless of the age value.
        Assert.False(SessionLossDetector.ShouldMarkHealthy(freshestTileAgeSeconds: 3, freshestIsRealFrame: false, configuredStaleSeconds: 120));
    }

    [Fact]
    public void ShouldMarkHealthy_RealFrame_ButAgeAtOrOverThreshold_IsFalse()
    {
        var threshold = SessionLossDetector.HealthyFreshnessThresholdSeconds(10);
        Assert.False(SessionLossDetector.ShouldMarkHealthy(threshold, freshestIsRealFrame: true, configuredStaleSeconds: 10));
    }

    [Fact]
    public void ShouldMarkHealthy_NullAge_IsFalseRegardlessOfRealFrameFlag()
    {
        Assert.False(SessionLossDetector.ShouldMarkHealthy(null, freshestIsRealFrame: true, configuredStaleSeconds: 10));
    }

    [Fact]
    public void Reset_DoesNotClearRecoveryStreakOrGate()
    {
        // Reset() is called on EVERY recovery (including the ones the backoff exists to damp) —
        // it must never silently undo RecordRecovery's gate.
        var detector = new SessionLossDetector();
        detector.RecordRecovery(Now);

        detector.Reset();

        Assert.Equal(1, detector.RecoveryStreak);
        Assert.False(detector.CanRecover(Now));
    }
}
