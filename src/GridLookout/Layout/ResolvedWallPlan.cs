namespace GridLookout.Layout;

/// <summary>
/// One reference inside a <see cref="ResolvedCell"/>, AFTER catalog resolution — the outcome of
/// resolving a <see cref="CellMember"/> against the live camera catalog and (for aliases)
/// <c>WallConfig.CameraBindings</c>. Unlike <see cref="CellMember"/> (which only records what was
/// WRITTEN), this records what it RESOLVED TO: either a specific camera id, or a reason it
/// couldn't. A fixed cell has exactly one of these; a rotation cell has one per written member, in
/// order — <c>WallForm</c> shows the first <see cref="Available"/> one and skips unavailable ones
/// when rotating, exactly like the pre-F3 "skip out-of-range ordinals" behavior it replaces.
/// </summary>
public sealed record ResolvedMember(
    CellMemberKind RefKind,
    string RefLabel,
    Guid? CameraId,
    bool Available,
    string? UnavailableReason)
{
    /// <summary>Convenience factory for a successfully resolved member.</summary>
    public static ResolvedMember ForCamera(CellMemberKind refKind, string refLabel, Guid cameraId) =>
        new(refKind, refLabel, cameraId, Available: true, UnavailableReason: null);

    /// <summary>Convenience factory for an unresolvable member — <paramref name="cameraId"/> is the
    /// PINNED target id when one exists (e.g. a camera that resolved successfully once but is now
    /// disabled/deleted — F3 rule 6e: the pin stays, the tile just shows unavailable), or null when
    /// nothing has ever resolved for this member (an unknown alias, a guid with no matching camera,
    /// or an ordinal never yet in range).</summary>
    public static ResolvedMember Unavailable(CellMemberKind refKind, string refLabel, Guid? cameraId, string reason) =>
        new(refKind, refLabel, cameraId, Available: false, UnavailableReason: reason);
}

/// <summary>One resolved grid cell — <see cref="Members"/>.Count &gt; 1 means "this tile rotates",
/// exactly like <see cref="LayoutCell"/>.Members before resolution. <see cref="FirstAvailable"/> is
/// what a freshly built tile shows; <c>WallForm</c>'s rotation logic advances through
/// <see cref="Members"/> the same way, skipping any with <see cref="ResolvedMember.Available"/>
/// false.
///
/// <see cref="Col"/>/<see cref="RowSpan"/>/<see cref="ColSpan"/> (F4 — cell spans) are carried
/// straight across from the <see cref="LayoutCell"/> this was resolved from — see that type's own
/// doc comment for what each means and why <see cref="Col"/> (not just list position) is needed at
/// all once a uniform-grid page's placeholders have been stripped. Defaults (0/1/1) are exactly
/// what every LEGACY (non-uniform) cell keeps, so nothing about a non-spanned page's resolved shape
/// changes.</summary>
public sealed record ResolvedCell(IReadOnlyList<ResolvedMember> Members, int Col = 0, int RowSpan = 1, int ColSpan = 1)
{
    public bool IsRotating => Members.Count > 1;

    /// <summary>The first available member, or null when every member is unavailable — the latter
    /// is when <c>WallForm</c> renders the UNAVAILABLE placeholder instead of a live tile (F3 rule
    /// 5), using <see cref="Members"/>[0]'s label/reason for the tile text (matching the pre-F3
    /// "error tile shows the FIRST written ordinal" convention).</summary>
    public ResolvedMember? FirstAvailable => Members.FirstOrDefault(m => m.Available);
}

public sealed record ResolvedRow(IReadOnlyList<ResolvedCell> Cells);

/// <summary>One resolved page. <see cref="IsUniform"/>/<see cref="GridColumns"/> (F4 — cell spans)
/// mirror <see cref="MatrixPage"/>'s own fields of the same name — see that type's doc comment; both
/// default false/0, exactly what an OLDER persisted plan (predating F4) reads back as, which is
/// "render through the legacy nested-panel path", the correct backward-compatible reading.</summary>
public sealed record ResolvedPage(IReadOnlyList<ResolvedRow> Rows, bool IsUniform = false, int GridColumns = 0);

/// <summary>One monitor's resolved layout — the direct <c>WallForm.RenderResolvedLayout</c> input,
/// replacing the pre-F3 <c>(MatrixPage, ordinal camera list)</c> pair.</summary>
public sealed record ResolvedMonitorPlan(int Monitor, IReadOnlyList<ResolvedPage> Pages);

/// <summary>
/// The complete resolved wall — one entry per monitor that ends up with SOME layout (freshly
/// resolved, reused from the persisted last-known-good plan, or a rule-6c carried-forward
/// per-monitor fallback). An empty <see cref="Monitors"/> list means "no monitor has a layout at
/// all" — Program.cs's <c>BuildWallForms</c> falls back to the <c>Monitors[]</c> config path in
/// exactly that case, the same structural fallback it already performed pre-F3 when
/// <c>LayoutSpecParser.ParseValid</c> returned zero entries.
/// </summary>
public sealed record ResolvedWallPlan(IReadOnlyList<ResolvedMonitorPlan> Monitors)
{
    public static readonly ResolvedWallPlan Empty = new(Array.Empty<ResolvedMonitorPlan>());
}
