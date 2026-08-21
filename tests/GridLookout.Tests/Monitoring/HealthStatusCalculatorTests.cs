using GridLookout.Monitoring;
using Xunit;

namespace GridLookout.Tests.Monitoring;

public class HealthStatusCalculatorTests
{
    private static WallFormHealth Healthy(int monitorNumber = 1) => new()
    {
        MonitorNumber = monitorNumber,
        ExpectedTileCount = 4,
        TilesWithFrames = 4,
        StalledCount = 0,
        NeverFramedCount = 0,
        UnavailableCount = 0,
        FreshestRenderedAgeSeconds = 1.0,
    };

    private static WallFormHealth Stalled(int monitorNumber = 1) => new()
    {
        MonitorNumber = monitorNumber,
        ExpectedTileCount = 4,
        TilesWithFrames = 3,
        StalledCount = 1,
        NeverFramedCount = 0,
        FreshestRenderedAgeSeconds = 5.0,
    };

    private static WallFormHealth NeverFramed(int monitorNumber = 1) => new()
    {
        MonitorNumber = monitorNumber,
        ExpectedTileCount = 4,
        TilesWithFrames = 3,
        StalledCount = 0,
        NeverFramedCount = 1,
        FreshestRenderedAgeSeconds = 5.0,
    };

    /// <summary>Buyer-review defect #1: a wall whose only "problem" is UNAVAILABLE cells (every
    /// referenced camera missing/disabled) — the pre-fix rule never looked at
    /// <see cref="WallFormHealth.UnavailableCount"/> at all, so an entirely-UNAVAILABLE wall could
    /// report Healthy.</summary>
    private static WallFormHealth AllUnavailable(int monitorNumber = 1) => new()
    {
        MonitorNumber = monitorNumber,
        ExpectedTileCount = 0, // UNAVAILABLE cells never had a LiveTileSource — never counted here.
        TilesWithFrames = 0,
        StalledCount = 0,
        NeverFramedCount = 0,
        UnavailableCount = 4,
        FreshestRenderedAgeSeconds = null,
    };

    // --- Pulse-stale wins unconditionally — checked first, before ControllerState or forms ---

    [Theory]
    [InlineData(ControllerState.Starting)]
    [InlineData(ControllerState.Connecting)]
    [InlineData(ControllerState.Running)]
    [InlineData(ControllerState.Recovering)]
    public void UiPulseNotFresh_AlwaysUnhealthy_RegardlessOfControllerStateOrTiles(ControllerState controllerState)
    {
        Assert.Equal(OverallStatus.Unhealthy, HealthStatusCalculator.Compute(uiPulseFresh: false, controllerState, new[] { Healthy() }, recorderSelectionIncomplete: false));
        Assert.Equal(OverallStatus.Unhealthy, HealthStatusCalculator.Compute(uiPulseFresh: false, controllerState, new[] { Stalled() }, recorderSelectionIncomplete: false));
        Assert.Equal(OverallStatus.Unhealthy, HealthStatusCalculator.Compute(uiPulseFresh: false, controllerState, Array.Empty<WallFormHealth>(), recorderSelectionIncomplete: false));
    }

    // --- Buyer-review defect #1: transitional ControllerState must precede the zero-forms rule ---

    [Theory]
    [InlineData(ControllerState.Starting)]
    [InlineData(ControllerState.Connecting)]
    [InlineData(ControllerState.Recovering)]
    public void UiPulseFresh_TransitionalControllerState_Degraded_EvenWithZeroForms(ControllerState controllerState)
    {
        // Starting/Connecting/Recovering legitimately have zero (or a stale) form set — this must
        // NOT be classified as Unhealthy the way Running-with-zero-forms now is (see the next test).
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, controllerState, Array.Empty<WallFormHealth>(), recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Degraded, status);
    }

    [Theory]
    [InlineData(ControllerState.Starting)]
    [InlineData(ControllerState.Connecting)]
    [InlineData(ControllerState.Recovering)]
    public void UiPulseFresh_TransitionalControllerState_Degraded_EvenWithCleanForms(ControllerState controllerState)
    {
        // A stale set of forms surviving from before a reconnect began must not read as Healthy
        // either — the controller itself says it isn't fully up yet.
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, controllerState, new[] { Healthy() }, recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Degraded, status);
    }

    // --- Buyer-review defect #1: Running + ZERO forms is the false-green case the review named ---

    [Fact]
    public void UiPulseFresh_RunningWithZeroForms_Unhealthy()
    {
        // Was UiPulseFresh_NoFormsAtAll_Healthy pre-fix — the review cited this exact test by name
        // as evidence of the false-green defect ("the test suite explicitly expects 'no forms' to be
        // Healthy"). A Running controller with zero configured wall windows means nothing is
        // actually showing; that is now Unhealthy, not vacuously Healthy.
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, ControllerState.Running, Array.Empty<WallFormHealth>(), recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Unhealthy, status);
    }

    [Fact]
    public void UiPulseFresh_RunningAllFormsClean_Healthy()
    {
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, ControllerState.Running, new[] { Healthy(1), Healthy(2) }, recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Healthy, status);
    }

    [Fact]
    public void UiPulseFresh_OneFormStalled_Degraded()
    {
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, ControllerState.Running, new[] { Healthy(1), Stalled(2) }, recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Degraded, status);
    }

    [Fact]
    public void UiPulseFresh_OneFormNeverFramed_Degraded()
    {
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, ControllerState.Running, new[] { Healthy(1), NeverFramed(2) }, recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Degraded, status);
    }

    // --- Buyer-review defect #1: an all-UNAVAILABLE wall must not read as Healthy ---

    [Fact]
    public void UiPulseFresh_OneFormAllUnavailable_Degraded_NotHealthy()
    {
        // Was silently ignored pre-fix — an UNAVAILABLE cell never had a LiveTileSource, so it never
        // contributed to StalledCount/NeverFramedCount, the only two signals the old rule checked.
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, ControllerState.Running, new[] { AllUnavailable() }, recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Degraded, status);
    }

    [Fact]
    public void UiPulseFresh_MixOfHealthyAndAllUnavailableForms_Degraded()
    {
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, ControllerState.Running, new[] { Healthy(1), AllUnavailable(2) }, recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Degraded, status);
    }

    // --- Buyer-review defect #1: a configured recorder disappearing from the catalog ---

    [Fact]
    public void UiPulseFresh_RecorderSelectionIncomplete_Degraded_EvenWithEveryTileHealthy()
    {
        // A configured RecordingServers[] selector matching no live recorder — every visible tile on
        // the wall can be perfectly healthy while this is still true (the missing recorder simply
        // contributes zero tiles, invisibly).
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, ControllerState.Running, new[] { Healthy() }, recorderSelectionIncomplete: true);
        Assert.Equal(OverallStatus.Degraded, status);
    }

    [Fact]
    public void UiPulseFresh_RecorderSelectionComplete_HealthyFormsStayHealthy()
    {
        var status = HealthStatusCalculator.Compute(uiPulseFresh: true, ControllerState.Running, new[] { Healthy() }, recorderSelectionIncomplete: false);
        Assert.Equal(OverallStatus.Healthy, status);
    }

    // --- Multiple simultaneous problems still cap at Degraded, never past it (pulse is what reaches Unhealthy) ---

    [Fact]
    public void UiPulseFresh_StalledAndNeverFramedAndUnavailableAndSelectionIncomplete_StillJustDegraded()
    {
        var status = HealthStatusCalculator.Compute(
            uiPulseFresh: true,
            ControllerState.Running,
            new[] { Stalled(1), NeverFramed(2), AllUnavailable(3) },
            recorderSelectionIncomplete: true);
        Assert.Equal(OverallStatus.Degraded, status);
    }
}
