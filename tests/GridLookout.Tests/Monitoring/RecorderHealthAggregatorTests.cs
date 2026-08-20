using GridLookout.Layout;
using GridLookout.Monitoring;
using Xunit;

namespace GridLookout.Tests.Monitoring;

/// <summary>
/// Covers F2 (multi-recorder walls)'s <see cref="RecorderHealthAggregator"/> — the pure per-
/// recorder health rollup split out of <c>WallForm</c>/<c>Program.cs</c> specifically so it is
/// unit-testable (constructing a real <c>WallForm</c> needs an STA thread and a live MIP session —
/// see <c>WallSetSwapperTests</c>'s identical rationale for testing <c>WallSetSwapper</c> through a
/// fake form instead).
///
/// Buyer-review defect #9 fix: both aggregations now key by recorder ID (a <see cref="Guid"/>), not
/// display name — two differently-configured recorders sharing a name used to collapse into one row.
/// </summary>
public class RecorderHealthAggregatorTests
{
    // --- Aggregate: live per-tile facts -> per-recorder rollup -------------------------------------

    [Fact]
    public void Aggregate_GroupsByRecorderId_ClassifiesEverRenderedAndStalled()
    {
        var recorderA = Guid.NewGuid();
        var recorderB = Guid.NewGuid();
        var facts = new[]
        {
            new RecorderTileFact(recorderA, "Recorder A", EverRendered: true, Stalled: false),
            new RecorderTileFact(recorderA, "Recorder A", EverRendered: true, Stalled: true),
            new RecorderTileFact(recorderA, "Recorder A", EverRendered: false, Stalled: false),
            new RecorderTileFact(recorderB, "Recorder B", EverRendered: true, Stalled: false),
        };

        var result = RecorderHealthAggregator.Aggregate(facts);

        var a = result[recorderA];
        Assert.Equal("Recorder A", a.RecorderName);
        Assert.Equal(3, a.Expected);
        Assert.Equal(2, a.Rendering); // "rendering" mirrors TilesWithFrames: includes the stalled one
        Assert.Equal(1, a.Stalled);
        Assert.Equal(1, a.NeverFramed);

        var b = result[recorderB];
        Assert.Equal("Recorder B", b.RecorderName);
        Assert.Equal(1, b.Expected);
        Assert.Equal(1, b.Rendering);
        Assert.Equal(0, b.Stalled);
        Assert.Equal(0, b.NeverFramed);
    }

    [Fact]
    public void Aggregate_EmptyRecorderId_Excluded()
    {
        // Single-recorder-mode tiles (CameraInfo.RecorderId == Guid.Empty) can never be attributed
        // to a specific recorder — they must not silently create a bogus Guid.Empty entry.
        var facts = new[]
        {
            new RecorderTileFact(Guid.Empty, string.Empty, EverRendered: true, Stalled: false),
            new RecorderTileFact(Guid.NewGuid(), "Recorder A", EverRendered: true, Stalled: false),
        };

        var result = RecorderHealthAggregator.Aggregate(facts);

        Assert.Single(result);
        Assert.DoesNotContain(Guid.Empty, result.Keys);
    }

    [Fact]
    public void Aggregate_TwoRecordersSharingTheSameDisplayName_StayTwoSeparateRows()
    {
        // The exact buyer-review defect #9 scenario: distinct recorder ids that happen to share a
        // display name must NOT collapse into one health row.
        var recorderA = Guid.NewGuid();
        var recorderB = Guid.NewGuid();
        var facts = new[]
        {
            new RecorderTileFact(recorderA, "Recorder", EverRendered: true, Stalled: false),
            new RecorderTileFact(recorderB, "Recorder", EverRendered: false, Stalled: false),
        };

        var result = RecorderHealthAggregator.Aggregate(facts);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[recorderA].Expected);
        Assert.Equal(1, result[recorderA].Rendering);
        Assert.Equal(1, result[recorderB].Expected);
        Assert.Equal(0, result[recorderB].Rendering);
    }

    [Fact]
    public void Aggregate_NoFacts_ReturnsEmpty()
    {
        Assert.Empty(RecorderHealthAggregator.Aggregate(Array.Empty<RecorderTileFact>()));
    }

    // --- AggregateUnavailableByRecorder: resolved-plan -> per-recorder unavailable counts ----------

    private static ResolvedMember Unavailable(Guid? cameraId, string reason = "unavailable") =>
        ResolvedMember.Unavailable(CellMemberKind.Guid, "ref", cameraId, reason);

    private static ResolvedMember Available(Guid cameraId) =>
        ResolvedMember.ForCamera(CellMemberKind.Guid, "ref", cameraId);

    private static ResolvedMonitorPlan Plan(params ResolvedCell[] cells) =>
        new(1, new[] { new ResolvedPage(new[] { new ResolvedRow(cells) }) });

    [Fact]
    public void AggregateUnavailableByRecorder_PinnedButUnavailableCamera_AttributedToItsRecorder()
    {
        var cameraId = Guid.NewGuid();
        var recorderId = Guid.NewGuid();
        var plan = Plan(new ResolvedCell(new[] { Unavailable(cameraId, "disabled") }));
        var recorderIdByCameraId = new Dictionary<Guid, Guid> { [cameraId] = recorderId };

        var result = RecorderHealthAggregator.AggregateUnavailableByRecorder(new[] { plan }, recorderIdByCameraId);

        Assert.Equal(1, result[recorderId]);
    }

    [Fact]
    public void AggregateUnavailableByRecorder_NeverPinnedReference_NotCountedForAnyRecorder()
    {
        // An unknown alias/guid or still-out-of-range ordinal has no CameraId at all — nothing to
        // attribute, and it must not be silently dropped into some catch-all bucket either.
        var plan = Plan(new ResolvedCell(new[] { Unavailable(cameraId: null) }));

        var result = RecorderHealthAggregator.AggregateUnavailableByRecorder(new[] { plan }, new Dictionary<Guid, Guid>());

        Assert.Empty(result);
    }

    [Fact]
    public void AggregateUnavailableByRecorder_CellWithAnAvailableMember_NotCounted()
    {
        // FirstAvailable non-null means the cell renders LIVE (fixed cell, or a rotation cell with
        // some available member) — never the UNAVAILABLE placeholder — so it must not be counted
        // even if the cell also happens to carry other, unavailable, members.
        var liveId = Guid.NewGuid();
        var recorderId = Guid.NewGuid();
        var cell = new ResolvedCell(new[] { Available(liveId), Unavailable(Guid.NewGuid()) });
        var plan = Plan(cell);
        var recorderIdByCameraId = new Dictionary<Guid, Guid> { [liveId] = recorderId };

        var result = RecorderHealthAggregator.AggregateUnavailableByRecorder(new[] { plan }, recorderIdByCameraId);

        Assert.Empty(result);
    }

    [Fact]
    public void AggregateUnavailableByRecorder_CameraIdNotInMap_NotCounted()
    {
        // Pinned to a camera id, but the caller's recorderIdByCameraId map (built from the LIVE
        // merged catalog) doesn't have it — e.g. a single-recorder-mode caller passing an empty map.
        var cameraId = Guid.NewGuid();
        var plan = Plan(new ResolvedCell(new[] { Unavailable(cameraId) }));

        var result = RecorderHealthAggregator.AggregateUnavailableByRecorder(new[] { plan }, new Dictionary<Guid, Guid>());

        Assert.Empty(result);
    }

    [Fact]
    public void AggregateUnavailableByRecorder_RecorderIdIsEmpty_NotCounted()
    {
        // recorderIdByCameraId can map a camera to Guid.Empty (single-recorder-mode's convention —
        // see Milestone.CameraInfo.RecorderId's own doc comment) — must be excluded exactly like a
        // missing map entry, never surfaced as a bogus Guid.Empty row.
        var cameraId = Guid.NewGuid();
        var plan = Plan(new ResolvedCell(new[] { Unavailable(cameraId) }));
        var recorderIdByCameraId = new Dictionary<Guid, Guid> { [cameraId] = Guid.Empty };

        var result = RecorderHealthAggregator.AggregateUnavailableByRecorder(new[] { plan }, recorderIdByCameraId);

        Assert.Empty(result);
    }

    [Fact]
    public void AggregateUnavailableByRecorder_SumsAcrossMultipleMonitorsAndRecorders()
    {
        var camA1 = Guid.NewGuid();
        var camA2 = Guid.NewGuid();
        var camB1 = Guid.NewGuid();
        var recorderA = Guid.NewGuid();
        var recorderB = Guid.NewGuid();

        var planMonitor1 = Plan(
            new ResolvedCell(new[] { Unavailable(camA1) }),
            new ResolvedCell(new[] { Unavailable(camA2) }));
        var planMonitor2 = Plan(new ResolvedCell(new[] { Unavailable(camB1) }));

        var recorderIdByCameraId = new Dictionary<Guid, Guid>
        {
            [camA1] = recorderA,
            [camA2] = recorderA,
            [camB1] = recorderB,
        };

        var result = RecorderHealthAggregator.AggregateUnavailableByRecorder(new[] { planMonitor1, planMonitor2 }, recorderIdByCameraId);

        Assert.Equal(2, result[recorderA]);
        Assert.Equal(1, result[recorderB]);
    }

    [Fact]
    public void AggregateUnavailableByRecorder_TwoRecordersSharingTheSameName_CountedSeparately()
    {
        var camA = Guid.NewGuid();
        var camB = Guid.NewGuid();
        var recorderA = Guid.NewGuid();
        var recorderB = Guid.NewGuid();

        var plan = Plan(
            new ResolvedCell(new[] { Unavailable(camA) }),
            new ResolvedCell(new[] { Unavailable(camB) }));
        var recorderIdByCameraId = new Dictionary<Guid, Guid> { [camA] = recorderA, [camB] = recorderB };

        var result = RecorderHealthAggregator.AggregateUnavailableByRecorder(new[] { plan }, recorderIdByCameraId);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[recorderA]);
        Assert.Equal(1, result[recorderB]);
    }
}
