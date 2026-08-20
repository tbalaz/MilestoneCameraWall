namespace GridLookout.UI;

/// <summary>
/// Pure decision for the panel-4 round-5 T2/R10 stale-source-frame guard — see
/// <see cref="WallForm.OnFrameReceived"/>'s BeginInvoke lambda, the only caller.
///
/// WHY THIS EXISTS. A rotating matrix tile (<c>A(3,4,5)</c> cells — see
/// <see cref="WallForm.SwapRotatingTileSource"/>) reuses the SAME <c>PictureBox</c> across a swap
/// but tears down the OUTGOING <see cref="LiveTileSource"/> and builds a brand new one for the
/// incoming camera. <see cref="LiveTileSource.OnLiveContent"/> runs on an SDK callback thread, not
/// the UI thread, and synchronously invokes <see cref="LiveTileSource.FrameReceived"/> — which reads
/// a snapshot of that event's subscriber list before <see cref="LiveTileSource.Shutdown"/> (called
/// from the UI thread by the swap) has a chance to clear it. That snapshot can therefore still carry
/// the OUTGOING source's frame through to <c>PictureBox.BeginInvoke</c> after the swap has already
/// repointed the box at the incoming camera — painting the previous camera's stale frame under the
/// new camera's caption/badge, for as long as it takes the incoming source to produce its own first
/// frame (which may be a while, or — if the incoming source never manages to start — indefinitely).
///
/// Extracted as a static, SDK/UI-free decision (reference identity only, no <c>PictureBox</c>/
/// <c>LiveTileSource</c>/Form involved) so it is unit-testable without a live wall. Public (not
/// internal, no InternalsVisibleTo exists in this repo) so <c>tests/GridLookout.Tests</c> can
/// exercise it directly — same convention as <c>JpegFrameDecoder</c>/<c>LayoutEngine</c>/other
/// pure-logic types here.
/// </summary>
public static class TileSourceGuard
{
    /// <summary>
    /// True when a frame produced by <paramref name="originatingSource"/> should be DROPPED rather
    /// than painted — i.e. it is not (by reference) the source currently recorded as active for its
    /// tile. <paramref name="currentActiveSourceForTile"/> is null when the tile's box is no longer
    /// tracked at all (a full grid rebuild tore it down since the frame was queued) — a null current
    /// source can never match any real <paramref name="originatingSource"/>, so this also correctly
    /// drops that case.
    /// </summary>
    public static bool ShouldDropFrame(object? currentActiveSourceForTile, object originatingSource)
    {
        return !ReferenceEquals(currentActiveSourceForTile, originatingSource);
    }
}
