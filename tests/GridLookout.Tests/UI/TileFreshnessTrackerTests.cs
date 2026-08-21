using GridLookout.UI;
using Xunit;

namespace GridLookout.Tests.UI;

/// <summary>
/// Covers <see cref="TileFreshnessTracker"/>, the T1/R1 fix for staleness never tripping on a
/// rotating wall — see the type's own doc comment for the full bug description. WallForm itself
/// can't be unit-tested directly (a real WinForms Form/Screen), so this pure helper carries the
/// high-water-mark/freshest computation logic instead.
/// </summary>
public class TileFreshnessTrackerTests
{
    private static readonly DateTime WallShown = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoTilesEver_ReturnsNull()
    {
        var tracker = new TileFreshnessTracker(WallShown);

        var age = tracker.ComputeFreshestAgeSeconds(WallShown.AddSeconds(30), Array.Empty<DateTime>(), currentBuildHasTiles: false);

        Assert.Null(age);
    }

    [Fact]
    public void HasTiles_NeverFramedSinceShown_ReturnsAgeSinceWallShown()
    {
        var tracker = new TileFreshnessTracker(WallShown);

        // Grid was built (has tiles) but none of them has produced a frame yet — MinValue timestamps.
        var age = tracker.ComputeFreshestAgeSeconds(
            WallShown.AddSeconds(45),
            new[] { DateTime.MinValue, DateTime.MinValue },
            currentBuildHasTiles: true);

        Assert.Equal(45.0, age);
    }

    [Fact]
    public void LiveSourceWithRecentFrame_ReturnsSmallAge()
    {
        var tracker = new TileFreshnessTracker(WallShown);
        var recentFrame = WallShown.AddSeconds(100);

        var age = tracker.ComputeFreshestAgeSeconds(
            WallShown.AddSeconds(103),
            new[] { recentFrame },
            currentBuildHasTiles: true);

        Assert.Equal(3.0, age);
    }

    [Fact]
    public void PageFlip_FoldedOutgoingFrame_SurvivesIntoNextBuildWithNoFramesYet()
    {
        // The core T1/R1 scenario: a page flip disposes the outgoing tile sources (folding their
        // last-frame timestamps) and builds a fresh page whose tiles haven't produced a frame yet.
        // The reported age must reflect the FOLDED timestamp, not reset to "just built".
        var tracker = new TileFreshnessTracker(WallShown);
        var lastGoodFrameBeforeFlip = WallShown.AddSeconds(10);

        // Outgoing page's tile is disposed — WallForm.DisposeTiles folds it before teardown.
        tracker.Fold(lastGoodFrameBeforeFlip);

        // Incoming page's tiles are brand new — no frames received yet (MinValue).
        var age = tracker.ComputeFreshestAgeSeconds(
            WallShown.AddSeconds(70),
            new[] { DateTime.MinValue },
            currentBuildHasTiles: true);

        // 70s since wall shown, minus the 10s mark where the last real frame landed = 60s stale,
        // NOT reset to a few seconds just because a page flip happened at t=70.
        Assert.Equal(60.0, age);
    }

    [Fact]
    public void MultiplePageFlips_HighWaterMarkKeepsAdvancing()
    {
        var tracker = new TileFreshnessTracker(WallShown);

        tracker.Fold(WallShown.AddSeconds(10)); // page 1's outgoing tile
        tracker.Fold(WallShown.AddSeconds(20)); // page 2's outgoing tile (still receiving frames)
        tracker.Fold(WallShown.AddSeconds(30)); // page 3's outgoing tile

        var age = tracker.ComputeFreshestAgeSeconds(WallShown.AddSeconds(35), Array.Empty<DateTime>(), currentBuildHasTiles: true);

        Assert.Equal(5.0, age);
    }

    [Fact]
    public void HighWaterMark_NeverRegressesOnOlderFoldedTimestamp()
    {
        var tracker = new TileFreshnessTracker(WallShown);

        tracker.Fold(WallShown.AddSeconds(50));
        tracker.Fold(WallShown.AddSeconds(20)); // older — must not move the mark backwards

        var age = tracker.ComputeFreshestAgeSeconds(WallShown.AddSeconds(60), Array.Empty<DateTime>(), currentBuildHasTiles: true);

        Assert.Equal(10.0, age); // 60 - 50, not 60 - 20
    }

    [Fact]
    public void Fold_MinValue_IsNoOp()
    {
        var tracker = new TileFreshnessTracker(WallShown);
        tracker.Fold(WallShown.AddSeconds(15));

        tracker.Fold(DateTime.MinValue);

        var age = tracker.ComputeFreshestAgeSeconds(WallShown.AddSeconds(20), Array.Empty<DateTime>(), currentBuildHasTiles: true);
        Assert.Equal(5.0, age); // still measured from the real folded frame, not reset by MinValue
    }

    [Fact]
    public void OnceEverHadTiles_LaterBuildWithNoTiles_StillReportsAge()
    {
        // e.g. a config refresh drops every camera and the form flips to ShowNoCameras() — the
        // form once had tiles, so the signal must keep reporting an age, not go back to null.
        var tracker = new TileFreshnessTracker(WallShown);
        tracker.ComputeFreshestAgeSeconds(WallShown.AddSeconds(5), new[] { WallShown.AddSeconds(5) }, currentBuildHasTiles: true);

        var age = tracker.ComputeFreshestAgeSeconds(WallShown.AddSeconds(65), Array.Empty<DateTime>(), currentBuildHasTiles: false);

        Assert.Equal(60.0, age);
    }

    [Fact]
    public void Baseline_NeverGoesBelowWallShownUtc()
    {
        var tracker = new TileFreshnessTracker(WallShown);

        // No frame ever folded, but the form has had tiles — baseline is wallShownUtc itself.
        var age = tracker.ComputeFreshestAgeSeconds(WallShown.AddSeconds(1), Array.Empty<DateTime>(), currentBuildHasTiles: true);

        Assert.Equal(1.0, age);
    }

    // --- Round-3 panel-3 T1: isRealFrame signal (SessionLossDetector.ShouldMarkHealthy's gate) ---

    [Fact]
    public void IsRealFrame_NeverFramedForm_IsFalseRegardlessOfHowSmallTheAgeIs()
    {
        // The exact T1 bug scenario: a brand-new form (e.g. just rebuilt by mid-session recovery)
        // whose tiles have never produced a single frame — MinValue timestamps only — checked on the
        // very first tick, when the age is still small purely because the form itself is young.
        var tracker = new TileFreshnessTracker(WallShown);

        var age = tracker.ComputeFreshestAgeSeconds(
            WallShown.AddSeconds(1),
            new[] { DateTime.MinValue, DateTime.MinValue },
            currentBuildHasTiles: true,
            out var isRealFrame);

        Assert.Equal(1.0, age); // small age...
        Assert.False(isRealFrame); // ...but NOT backed by any real frame.
    }

    [Fact]
    public void IsRealFrame_NoTilesEver_IsFalse()
    {
        var tracker = new TileFreshnessTracker(WallShown);

        var age = tracker.ComputeFreshestAgeSeconds(
            WallShown.AddSeconds(30), Array.Empty<DateTime>(), currentBuildHasTiles: false, out var isRealFrame);

        Assert.Null(age);
        Assert.False(isRealFrame);
    }

    [Fact]
    public void IsRealFrame_LiveSourceWithRecentFrame_IsTrue()
    {
        var tracker = new TileFreshnessTracker(WallShown);
        var recentFrame = WallShown.AddSeconds(100);

        var age = tracker.ComputeFreshestAgeSeconds(
            WallShown.AddSeconds(103), new[] { recentFrame }, currentBuildHasTiles: true, out var isRealFrame);

        Assert.Equal(3.0, age);
        Assert.True(isRealFrame);
    }

    [Fact]
    public void IsRealFrame_FoldedHighWaterFromPreviousPage_StillCountsAsARealFrame()
    {
        // The page-flip scenario: the outgoing page's tile DID receive a real frame before this
        // tracker's current build (folded via Fold(), same as WallForm.DisposeTiles does before
        // teardown) — the incoming page's tiles haven't produced anything yet (MinValue), but the
        // reported freshest value is still backed by a real frame: the folded one.
        var tracker = new TileFreshnessTracker(WallShown);
        tracker.Fold(WallShown.AddSeconds(10));

        var age = tracker.ComputeFreshestAgeSeconds(
            WallShown.AddSeconds(70), new[] { DateTime.MinValue }, currentBuildHasTiles: true, out var isRealFrame);

        Assert.Equal(60.0, age);
        Assert.True(isRealFrame);
    }

    [Fact]
    public void IsRealFrame_ThreeArgOverload_DefaultsToDiscardingTheFlag_BehavesIdenticallyToBefore()
    {
        // The pre-existing 3-argument overload (used by every other test in this file) must keep
        // working byte-identically now that it just delegates to the 4-argument one.
        var tracker = new TileFreshnessTracker(WallShown);

        var age = tracker.ComputeFreshestAgeSeconds(WallShown.AddSeconds(5), Array.Empty<DateTime>(), currentBuildHasTiles: true);

        Assert.Equal(5.0, age);
    }
}
