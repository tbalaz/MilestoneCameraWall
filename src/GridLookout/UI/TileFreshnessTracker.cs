namespace GridLookout.UI;

/// <summary>
/// Pure, SDK/UI-free tracker for the "freshest tile" high-water mark behind
/// <see cref="WallForm.FreshestTileAgeSeconds"/> — the signal
/// <see cref="GridLookout.Recovery.SessionLossDetector"/> uses to decide whether a wall's session
/// has actually died versus is merely between frames.
///
/// WHY THIS EXISTS (T1/R1 fix). <c>WallForm.BuildGrid</c> disposes and recreates every
/// <see cref="LiveTileSource"/> on EVERY page flip — auto-layout page rotation
/// (<c>PageSeconds</c> as low as 10s) and matrix page rotation (always rotates at a 10s floor for
/// a multi-page <c>$layout{}</c> token). Before this type, <c>FreshestTileAgeSeconds()</c> measured
/// every tile's age from the CURRENT grid build's own timestamp, which reset on every flip — so a
/// healthy, continuously-rotating wall could never accumulate the 60+ seconds of "freshest tile
/// age" recovery needs to trigger, even during a genuine outage, because each flip's fresh baseline
/// masked the elapsed time. This tracker instead keeps a monotonic high-water mark that SURVIVES
/// page flips: <see cref="Fold"/> is called with a tile source's last-frame timestamp right before
/// it is torn down, so a wall that WAS receiving frames recently still reports a small age even
/// mid-flip, while a wall that has genuinely stopped receiving frames (server down through several
/// flips) correctly accumulates age past the recovery threshold.
/// </summary>
public sealed class TileFreshnessTracker
{
    private readonly DateTime _wallShownUtc;
    private DateTime? _highWaterUtc;
    private bool _everHadTiles;

    /// <param name="wallShownUtc">Baseline for a tile that has never produced a frame since the
    /// form first existed — see <see cref="ComputeFreshestAgeSeconds"/>. Set ONCE per
    /// <c>WallForm</c> instance (its constructor — see <c>WallForm</c>'s field comment for why
    /// there, not "first grid build"); a fresh <c>WallForm</c> instance is what mid-session
    /// recovery constructs (Program.cs's <c>BuildWallForms</c>), so the baseline naturally resets
    /// after a recovery without this type needing to know anything about recovery itself.</param>
    public TileFreshnessTracker(DateTime wallShownUtc)
    {
        _wallShownUtc = wallShownUtc;
    }

    /// <summary>Folds one frame timestamp into the high-water mark — call with a tile source's
    /// <c>LastFrameUtc</c> before disposing it (a page-flip rebuild, or any other grid teardown).
    /// A no-op for <see cref="DateTime.MinValue"/> (a tile that never received a frame) and for any
    /// timestamp not newer than the current mark — the mark only ever moves forward.</summary>
    public void Fold(DateTime frameUtc)
    {
        if (frameUtc == DateTime.MinValue)
        {
            return;
        }

        if (_highWaterUtc is null || frameUtc > _highWaterUtc.Value)
        {
            _highWaterUtc = frameUtc;
        }
    }

    /// <summary>
    /// Freshest-tile age, in seconds, as of <paramref name="nowUtc"/>. Every timestamp in
    /// <paramref name="liveSourceFrameTimestamps"/> (the CURRENT grid's live tile sources'
    /// <c>LastFrameUtc</c>) is folded in first — exactly like a page-flip's outgoing sources are —
    /// so a currently live source's own frame always counts, same as before this type existed. The
    /// result is <c>nowUtc - max(high-water mark, wallShownUtc)</c>: the wall-shown baseline is a
    /// floor, never allowing the reported age to exceed how long this form has even existed.
    ///
    /// Returns null ONLY when this tracker has never had any tiles at all — no folded frame ever,
    /// no live sources ever passed with <paramref name="currentBuildHasTiles"/> true — matching a
    /// status/error card or a monitor with zero cameras assigned: there is nothing to judge. Once a
    /// build HAS had tiles (even if a later build on the same form has none, e.g. it flips to a
    /// "no cameras" status card), an age keeps being reported from then on — a tile that has NEVER
    /// received a frame since the form was shown still counts (measured from <paramref name="nowUtc"/>
    /// minus <c>wallShownUtc</c>), deliberately different from the per-tile STALLED-overlay gating
    /// (<c>WallForm.SweepStaleTiles</c>), which excludes a never-framed tile — a wall whose sources
    /// never reconnect after a rebuild is exactly the failure mode session-loss recovery exists to
    /// catch, and excluding it here would make the signal permanently null in that case.
    /// </summary>
    public double? ComputeFreshestAgeSeconds(DateTime nowUtc, IReadOnlyList<DateTime> liveSourceFrameTimestamps, bool currentBuildHasTiles)
        => ComputeFreshestAgeSeconds(nowUtc, liveSourceFrameTimestamps, currentBuildHasTiles, out _);

    /// <summary>
    /// Same contract as the three-argument overload, plus <paramref name="isRealFrame"/> — round-3
    /// panel-3 T1 fix. True only when the returned age's baseline is an actual folded frame
    /// timestamp (a live source's <c>LastFrameUtc</c>, now or from an earlier page-flip teardown);
    /// false when the age is measured from <see cref="_wallShownUtc"/> alone — either because no
    /// frame has EVER been folded, or because the high-water mark, though set, is somehow older than
    /// <see cref="_wallShownUtc"/> and gets clamped up to it (defensive; not reachable via the
    /// monotonic <see cref="Fold"/> in normal operation, but the flag must describe what the
    /// RETURNED age actually measures, not merely whether a frame was ever seen).
    ///
    /// WHY THIS EXISTS: <see cref="GridLookout.Recovery.SessionLossDetector.ShouldMarkHealthy"/>
    /// must never reset the recovery backoff off a young, still-outage-affected form whose reported
    /// age is small only because the form itself is young (measured from <see cref="_wallShownUtc"/>)
    /// — not because any frame has actually arrived. See that method's doc comment for the concrete
    /// failure this closes.
    /// </summary>
    public double? ComputeFreshestAgeSeconds(DateTime nowUtc, IReadOnlyList<DateTime> liveSourceFrameTimestamps, bool currentBuildHasTiles, out bool isRealFrame)
    {
        if (currentBuildHasTiles)
        {
            _everHadTiles = true;
        }

        foreach (var timestamp in liveSourceFrameTimestamps)
        {
            Fold(timestamp);
        }

        if (!_everHadTiles)
        {
            isRealFrame = false;
            return null;
        }

        var baseline = _highWaterUtc ?? _wallShownUtc;
        isRealFrame = _highWaterUtc is not null;
        if (baseline < _wallShownUtc)
        {
            baseline = _wallShownUtc;
            isRealFrame = false;
        }

        return (nowUtc - baseline).TotalSeconds;
    }
}
