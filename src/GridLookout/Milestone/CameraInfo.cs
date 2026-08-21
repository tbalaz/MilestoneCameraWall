namespace GridLookout.Milestone;

/// <summary>One camera belonging to the matched recorder. Carries the full
/// <see cref="VideoOS.Platform.Item"/> (not just its FQID) because
/// <see cref="GridLookout.UI.LiveTileSource"/> needs the Item to construct the live source.
///
/// F3 (referentially stable layouts): <see cref="Id"/> is the camera's FQID ObjectId — the stable
/// identity <c>Layout.LayoutResolver</c> pins <c>$layout{}</c> ordinal/alias/guid references to,
/// instead of the array-position "ordinal" this type used to be exclusively indexed by. It's a
/// convenience duplicate of <see cref="Fqid"/>.ObjectId (kept as its own property, not just inferred
/// at call sites) specifically so every F3 call site names it the same way it appears in
/// <c>CameraBindings</c> config and <c>layout-state.json</c> — "Id", not "the ObjectId part of the
/// Fqid". <see cref="Enabled"/> mirrors the source <c>Item.Enabled</c>: <see cref="RecorderMatch.Cameras"/>
/// (the pre-F3 sorted "ordinal" list) still contains ENABLED cameras only, but
/// <see cref="RecorderMatch.AllCameras"/> (new in F3) retains disabled ones too — an alias/guid
/// reference to a disabled camera must resolve to "camera found, but disabled" (an UNAVAILABLE
/// tile), never to "camera not found at all", and telling those two apart needs the disabled
/// cameras to still be in the catalog.
///
/// F2 (multi-recorder walls): <see cref="RecorderName"/> is "" (the default) for every camera
/// <c>RecorderLocator.Locate</c> produces (single-recorder mode — unchanged) and is set to the
/// owning recorder's display name only by <c>RecorderCatalog.Discover</c> (multi-recorder mode).
/// <see cref="DisplayName"/> centralizes the "empty means single mode, don't qualify" rule so
/// <c>WallForm</c>/<c>LiveTileSource</c> caption and log-label call sites never have to repeat it.
/// <see cref="RecorderId"/> mirrors that SAME single-mode-blank convention (<c>Guid.Empty</c>
/// there, set only by <c>RecorderCatalog.Discover</c>) — buyer-review defect #9 fix: it is the
/// stable per-recorder key <c>Monitoring.RecorderHealthAggregator</c> now groups by, instead of the
/// display name (two differently-configured recorders can legitimately share a name; two recorder
/// FQID ObjectIds never collide). Kept blank in single-recorder mode on purpose, exactly like
/// <see cref="RecorderName"/> — <c>RecorderHealthAggregator.Aggregate</c> relies on that blankness to
/// keep <c>Monitoring.WallHealthState.Recorders</c> an empty list outside multi-recorder mode; see
/// that method's own doc comment.</summary>
public sealed record CameraInfo(string Name, Guid Id, VideoOS.Platform.Item Item, bool Enabled, string RecorderName = "", Guid RecorderId = default)
{
    public VideoOS.Platform.FQID Fqid => Item.FQID;

    /// <summary>F2: "RecorderName / Name" in multi-recorder mode (distinguishes duplicate camera
    /// names across recorders in tile captions and per-tile log lines), plain <see cref="Name"/>
    /// unchanged in single-recorder mode (<see cref="RecorderName"/> is always "" there).</summary>
    public string DisplayName => string.IsNullOrEmpty(RecorderName) ? Name : $"{RecorderName} / {Name}";
}
