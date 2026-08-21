using GridLookout.Monitoring;
using Xunit;

namespace GridLookout.Tests.Monitoring;

public class TileRecoverySchedulerTests
{
    [Fact]
    public void Disabled_TileRecoverSecondsZero_NeverDue()
    {
        var scheduler = new TileRecoveryScheduler(0);

        Assert.False(scheduler.Enabled);
        var eligibleSince = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(scheduler.IsAttemptDue(eligibleSince.AddYears(1), eligibleSince));
    }

    [Fact]
    public void FirstAttempt_DueExactlyTileRecoverSecondsAfterEligibility()
    {
        var scheduler = new TileRecoveryScheduler(30);
        var eligibleSince = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(scheduler.IsAttemptDue(eligibleSince.AddSeconds(29), eligibleSince));
        Assert.True(scheduler.IsAttemptDue(eligibleSince.AddSeconds(30), eligibleSince));
    }

    [Fact]
    public void IsAttemptDue_ReSeedingIgnoredOnceScheduleIsSet()
    {
        // The first call seeds NextAttemptUtc from eligibleSinceUtc; a later call with a DIFFERENT
        // eligibleSinceUtc (e.g. the caller re-derives it slightly differently tick-to-tick) must
        // not re-seed a later schedule than the one already committed.
        var scheduler = new TileRecoveryScheduler(30);
        var eligibleSince = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(scheduler.IsAttemptDue(eligibleSince.AddSeconds(5), eligibleSince));
        Assert.Equal(eligibleSince.AddSeconds(30), scheduler.NextAttemptUtc);

        // A much later "eligibleSince" passed on a subsequent tick must not push the schedule out.
        var laterEligibleSince = eligibleSince.AddSeconds(20);
        Assert.False(scheduler.IsAttemptDue(eligibleSince.AddSeconds(25), laterEligibleSince));
        Assert.Equal(eligibleSince.AddSeconds(30), scheduler.NextAttemptUtc);
    }

    [Fact]
    public void RecordAttempt_SchedulesDoublingBackoff_CappedAt8x()
    {
        var scheduler = new TileRecoveryScheduler(10);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        scheduler.RecordAttempt(t0);
        Assert.Equal(1, scheduler.AttemptCount);
        Assert.Equal(t0.AddSeconds(10), scheduler.NextAttemptUtc); // 1x

        scheduler.RecordAttempt(t0);
        Assert.Equal(2, scheduler.AttemptCount);
        Assert.Equal(t0.AddSeconds(20), scheduler.NextAttemptUtc); // 2x

        scheduler.RecordAttempt(t0);
        Assert.Equal(t0.AddSeconds(40), scheduler.NextAttemptUtc); // 4x

        scheduler.RecordAttempt(t0);
        Assert.Equal(t0.AddSeconds(80), scheduler.NextAttemptUtc); // 8x (cap reached)

        scheduler.RecordAttempt(t0);
        Assert.Equal(t0.AddSeconds(80), scheduler.NextAttemptUtc); // still 8x — capped, not 16x
    }

    [Fact]
    public void RecordAttempt_Disabled_NoOp()
    {
        var scheduler = new TileRecoveryScheduler(0);
        scheduler.RecordAttempt(DateTime.UtcNow);

        Assert.Equal(0, scheduler.AttemptCount);
        Assert.Null(scheduler.NextAttemptUtc);
    }

    [Fact]
    public void Reset_ClearsAttemptCountAndSchedule()
    {
        var scheduler = new TileRecoveryScheduler(10);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        scheduler.RecordAttempt(t0);
        scheduler.RecordAttempt(t0);

        scheduler.Reset();

        Assert.Equal(0, scheduler.AttemptCount);
        Assert.Null(scheduler.NextAttemptUtc);
    }

    [Fact]
    public void Reset_ThenNewSpell_StartsFreshAtBaseDelay_NotWherePreviousSpellLeftOff()
    {
        var scheduler = new TileRecoveryScheduler(10);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // First bad spell climbs the backoff to 4x (40s).
        scheduler.RecordAttempt(t0);
        scheduler.RecordAttempt(t0);
        scheduler.RecordAttempt(t0);
        Assert.Equal(t0.AddSeconds(40), scheduler.NextAttemptUtc);

        scheduler.Reset();

        // A brand-new, unrelated outage starting much later must be due exactly TileRecoverSeconds
        // after ITS OWN eligibility — not influenced by the previous spell's climbed-up backoff.
        var newEligibleSince = t0.AddDays(1);
        Assert.False(scheduler.IsAttemptDue(newEligibleSince.AddSeconds(9), newEligibleSince));
        Assert.True(scheduler.IsAttemptDue(newEligibleSince.AddSeconds(10), newEligibleSince));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public void NegativeOrZero_TreatedAsDisabled(int tileRecoverSeconds)
    {
        var scheduler = new TileRecoveryScheduler(tileRecoverSeconds);
        Assert.False(scheduler.Enabled);
    }
}
