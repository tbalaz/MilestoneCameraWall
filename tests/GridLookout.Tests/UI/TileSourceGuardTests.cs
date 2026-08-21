using GridLookout.UI;
using Xunit;

namespace GridLookout.Tests.UI;

/// <summary>
/// Covers <see cref="TileSourceGuard"/>, the panel-4 round-5 T2/R10 fix for a rotating matrix tile
/// (<c>A(3,4,5)</c> cells) occasionally painting the OUTGOING camera's stale frame under the
/// INCOMING camera's caption right after a rotation swap — see
/// <see cref="WallForm.SwapRotatingTileSource"/> and <see cref="WallForm.OnFrameReceived"/> for how
/// this pure identity check is wired to a real tile-source map. Plain <see cref="object"/> stand-ins
/// are used in place of a real <c>LiveTileSource</c> — the decision is reference-identity-only and
/// does not care what type the sources actually are.
/// </summary>
public class TileSourceGuardTests
{
    [Fact]
    public void FrameFromTheCurrentActiveSource_IsNotDropped()
    {
        var activeSource = new object();

        Assert.False(TileSourceGuard.ShouldDropFrame(activeSource, activeSource));
    }

    [Fact]
    public void FrameFromAnOutgoingSource_AfterASwap_IsDropped()
    {
        // The rotation swap has already repointed the tile at incomingSource by the time this
        // stale frame (produced by outgoingSource, queued before Shutdown() unhooked it) reaches
        // the guard.
        var outgoingSource = new object();
        var incomingSource = new object();

        Assert.True(TileSourceGuard.ShouldDropFrame(incomingSource, outgoingSource));
    }

    [Fact]
    public void FrameForATileNoLongerTracked_IsDropped()
    {
        // A full grid rebuild tore the tile down (and with it, its entry in the active-source map)
        // since the frame was queued — null current source can never match any real originator.
        var originatingSource = new object();

        Assert.True(TileSourceGuard.ShouldDropFrame(currentActiveSourceForTile: null, originatingSource));
    }
}
