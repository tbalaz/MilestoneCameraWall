using System.Globalization;

namespace GridLookout.Layout;

/// <summary>One camera as <see cref="LayoutResolver"/> needs to see it — deliberately NOT
/// <c>Milestone.CameraInfo</c> (which carries a live SDK <c>Item</c>) so this whole resolver stays
/// SDK-free and testable with plain GUIDs, matching <c>LayoutSpecParser</c>/<c>LayoutEngine</c>'s
/// existing "pure logic" convention. Program.cs adapts a real <c>RecorderMatch.AllCameras</c>
/// snapshot into this shape at each resolve.</summary>
public sealed record CameraCatalogEntry(Guid Id, string Name, bool Enabled);

/// <summary>
/// Resolves <see cref="LayoutSpecParser.Parse"/>'s per-token results into an immutable
/// <see cref="ResolvedWallPlan"/> of stable camera ids — the piece that actually fixes F3's root
/// defect (ordinals silently re-pointing at the wrong camera after a rename/reorder/enable/disable).
/// SDK-free; <c>Program.cs</c> is the only production caller, threading in a live
/// <see cref="CameraCatalogEntry"/> snapshot, the config's validated <c>CameraBindings</c>
/// (<see cref="CameraBindingResolver"/>), and the previously persisted <see cref="LayoutStateFile"/>
/// (or null on a cold start / first run).
///
/// THE CENTRAL RULE — "pin on first success, never re-derive until the description's layout intent
/// changes":
/// <list type="bullet">
/// <item>An ORDINAL reference is the only kind that needs this at all — alias/guid references are
/// already stable identities (an alias always names the same <c>CameraBindings</c> target; a guid
/// literal IS the target) and never drift on their own. An ordinal is resolved against the CURRENT
/// enabled-camera order exactly once, and from that moment on the resulting camera id is what's
/// used — a later rename/reorder/enable/disable of ANY camera can never change what that cell shows
/// (F3 rule 6a).</item>
/// <item>"Once" is scoped by <see cref="LayoutFingerprint"/>, computed PER MONITOR (buyer-review
/// defects #4/#5/#7 fix — see that class's own doc comment for what widened) over that monitor's own
/// raw $layout token text, the resolved CameraBindings pairs its token references, and the
/// currently-selected recorder id set: unchanged fingerprint reuses THAT monitor's persisted plan
/// verbatim (F3 rule 6a); changed fingerprint means the operator edited layout intent (or an alias
/// binding, or the recorder selection) for THIS monitor specifically, so its VALID token is resolved
/// — and pinned — fresh (F3 rule 6b), untouched by anything that happened on a sibling monitor. A
/// token that's INVALID after a fingerprint change falls back to that monitor's OLD persisted plan
/// when one exists (F3 rule 6c: "stale but valid beats desktop"); with none to fall back to, that
/// monitor simply has no layout this cycle. Bug fix (predates the per-monitor fingerprint, still
/// needed): rule 6a's "unchanged fingerprint -> trust verbatim" shortcut is ALSO scoped by
/// <see cref="LayoutStateFile.CarriedForwardMonitors"/> — a monitor whose persisted entry came from a
/// rule-6c carry-forward is NEVER trusted by that shortcut, even when its own fingerprint matches (a
/// malformed token that never changes hashes the same way every time, so fingerprint-match alone
/// can't tell "still broken" from "fixed"), so its own current token is re-checked on every resolve
/// regardless. That is what lets a monitor whose token is malformed-then-later-valid-with-identical-
/// text (a parser fix, or any other cause) escape the carry-forward instead of being wedged on the
/// stale plan forever with no warning. See <see cref="Resolve"/> and LayoutResolverTests' carry-forward
/// tests.</item>
/// <item>A member that has NEVER successfully resolved (an out-of-range ordinal, an unknown alias)
/// has no id to pin — <see cref="PersistedMember.CameraId"/> stays null, and every subsequent
/// resolve — REGARDLESS of fingerprint — retries it fresh, because there is nothing pinned yet to
/// protect. The moment it succeeds, the id is written and never touched again.</item>
/// <item>Once a member IS pinned, a camera going missing/disabled shows the UNAVAILABLE placeholder
/// (F3 rule 5) but the pin itself is left completely alone (F3 rule 6e) — this is what stops a
/// temporarily-disabled camera from silently "falling through" to whatever ordinal N now means.</item>
/// </list>
///
/// Every $layout token that's a monitor's ONLY source of layout (i.e. this whole feature) is
/// exclusive of the config's <c>Monitors[]</c> auto-layout — see the product-decision note on
/// <see cref="Resolve"/> for exactly how that boundary is drawn.
///
/// A monitor with NO current token at all (valid or invalid) is NEVER considered here, even if a
/// persisted/hand-edited state file still carries an orphaned entry for it — removing a token closes
/// that monitor's wall (see <c>user-guide.md</c>'s "Layout tokens control everything when
/// present"), so there is nothing left to protect once the token itself is gone. This is a
/// deliberate simplification introduced alongside the per-monitor fingerprint: the pre-fix single
/// GLOBAL fingerprint used to widen consideration to every persisted monitor whenever the whole-
/// description hash happened to still match, as a defensive read for a hand-edited/corrupt state
/// file — that widening has no per-monitor equivalent worth keeping (an orphaned entry with no
/// current token was never reachable from live config anyway) and is intentionally dropped.
/// </summary>
public static class LayoutResolver
{
    /// <summary>Optional logger — Program.cs sets it so rule-6c carry-forward events (a malformed
    /// token falling back to last-known-good) are visible in the log, same convention as
    /// <see cref="LayoutSpecParser.Logger"/>/<c>Milestone.RecorderLocator.Logger</c>.</summary>
    public static GridLookout.Logging.FileLogger? Logger { get; set; }

    /// <param name="RecorderIds">Buyer-review defect #7 fix — see <c>Milestone.RecorderLocator.RecorderMatch.RecorderIds</c>'s
    /// own doc comment. Folded into every monitor's <see cref="LayoutFingerprint.ComputeForMonitor"/>
    /// call so a `RecordingServers[]` change is itself a fingerprint change for every monitor.</param>
    public sealed record ResolveInput(
        IReadOnlyList<TokenParseResult> TokenResults,
        IReadOnlyList<CameraCatalogEntry> Catalog,
        IReadOnlyList<Guid> OrderedEnabledCameraIds,
        IReadOnlyDictionary<string, Guid> CameraBindings,
        LayoutStateFile? PersistedState,
        IReadOnlyList<Guid> RecorderIds);

    public sealed record ResolveResult(ResolvedWallPlan Plan, LayoutStateFile NewState);

    /// <summary>
    /// Product-decision note (do not "fix"): an empty <see cref="ResolvedWallPlan.Monitors"/> in the
    /// result means NO monitor has a $layout-derived layout right now — Program.cs's caller then
    /// falls back to the config's <c>Monitors[]</c> auto-layout for EVERY monitor, exactly as it did
    /// before F3. The moment even one monitor resolves to something (fresh, or carried forward via
    /// rule 6c), token mode is exclusive for the WHOLE wall: only monitors present in the result get
    /// wall windows; every other configured monitor is left showing the desktop, on purpose. This
    /// method has no opinion on that boundary itself — it is drawn entirely by whether
    /// <see cref="ResolvedWallPlan.Monitors"/> ends up empty, which Program.cs checks the same way it
    /// already checked <c>LayoutSpecParser.ParseValid(...).Count == 0</c> pre-F3.
    /// </summary>
    public static ResolveResult Resolve(ResolveInput input)
    {
        var catalogById = input.Catalog.ToDictionary(c => c.Id);

        var validByMonitor = input.TokenResults
            .Where(r => r.IsValid)
            .ToDictionary(r => r.Layout!.Monitor);

        // First invalid result per monitor — the "current token" for a monitor with no valid entry,
        // used both for the rule-6c fallback decision below and as that monitor's own fingerprint
        // input. A monitor that ALSO has a valid entry (a duplicate $layoutN{} the parser already
        // marked Invalid — "first token wins") never consults this map; the valid entry is
        // authoritative for it, exactly like LayoutSpecParser's own duplicate-token rule intends.
        var invalidByMonitor = input.TokenResults
            .Where(r => !r.IsValid && r.Monitor.HasValue)
            .GroupBy(r => r.Monitor!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var oldPlanByMonitor = input.PersistedState?.ResolvedPlan ?? new Dictionary<string, List<PersistedPage>>();
        var oldFingerprintByMonitor = input.PersistedState?.MonitorFingerprints ?? new Dictionary<string, string>();

        // Bug fix: per-monitor, not a blanket "was there ANY carry-forward this resolve" flag — see
        // LayoutStateFile.CarriedForwardMonitors' doc comment for why a single flag (whether applied
        // to the fingerprint we write, or to a "trust everything" gate) either lets a carried
        // monitor's stale plan hide behind the fingerprint forever, or strips an unrelated healthy
        // sibling monitor of its own pin. Old files with no such list read back as "nothing carried".
        var previouslyCarriedForward = new HashSet<string>(
            input.PersistedState?.CarriedForwardMonitors ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        var finalMonitors = new List<ResolvedMonitorPlan>();
        var newPersistedPlan = new Dictionary<string, List<PersistedPage>>();
        var newMonitorFingerprints = new Dictionary<string, string>();
        var newCarriedForwardMonitors = new HashSet<string>(StringComparer.Ordinal);

        // Buyer-review defects #4/#5/#7 fix: EXACTLY the monitors with a current token (valid or
        // invalid) are considered — no widening from oldPlanByMonitor.Keys the way the pre-fix
        // single GLOBAL fingerprint did. See the class doc comment for why an orphaned persisted-only
        // entry (no current token at all) is now intentionally out of scope.
        var monitorsToConsider = new HashSet<int>(validByMonitor.Keys);
        monitorsToConsider.UnionWith(invalidByMonitor.Keys);

        // net48's KeyValuePair<TKey,TValue> has no Deconstruct (added in netstandard2.1+) — plain
        // .TryGetValue/indexer access throughout below instead of a tuple-pattern foreach.
        foreach (var monitor in monitorsToConsider)
        {
            var monitorKey = monitor.ToString(CultureInfo.InvariantCulture);
            var wasCarriedForward = previouslyCarriedForward.Contains(monitorKey);

            // The token that currently speaks for this monitor — valid if it has one, otherwise the
            // (first) invalid one; monitorsToConsider guarantees one of the two always exists.
            var currentToken = validByMonitor.TryGetValue(monitor, out var currentValid)
                ? currentValid
                : invalidByMonitor[monitor];
            var freshFingerprint = LayoutFingerprint.ComputeForMonitor(currentToken, input.CameraBindings, input.RecorderIds);

            bool oldEntryExists = oldPlanByMonitor.TryGetValue(monitorKey, out var oldPages);
            bool monitorFingerprintUnchanged = oldEntryExists
                && oldFingerprintByMonitor.TryGetValue(monitorKey, out var storedFingerprint)
                && string.Equals(storedFingerprint, freshFingerprint, StringComparison.Ordinal);

            // Rule 6a, scoped to THIS monitor's OWN fingerprint: trust the persisted pin verbatim —
            // no re-derivation, no current-token check at all — only when THIS monitor's own layout
            // identity (token text + resolved CameraBindings pairs it references + recorder
            // selection — see LayoutFingerprint's doc comment) is unchanged AND this monitor's own
            // persisted entry is itself trustworthy (a genuine prior pin, not a rule-6c carry-forward
            // sitting there only because ITS token was malformed). A carried-forward entry is never
            // trusted this way, fingerprint match or not — see the class doc comment. A monitor with
            // NO stored fingerprint at all (an older file, migrated below) reads as "changed" here.
            if (monitorFingerprintUnchanged && !wasCarriedForward)
            {
                var (resolvedPages, persistedPagesOut) = ReapplyPersistedPages(
                    oldPages!, catalogById, input.OrderedEnabledCameraIds, input.CameraBindings);

                finalMonitors.Add(new ResolvedMonitorPlan(monitor, resolvedPages));
                newPersistedPlan[monitorKey] = persistedPagesOut;
                newMonitorFingerprints[monitorKey] = freshFingerprint;
                continue;
            }

            if (validByMonitor.TryGetValue(monitor, out var validResult))
            {
                if (wasCarriedForward)
                {
                    // The exact escape moment this whole mechanism exists to make visible: a monitor
                    // that was stuck showing a carried-forward stale plan has a valid token again
                    // (fingerprint changed, or this same text simply parses differently now) and is
                    // about to be re-pinned fresh — i.e. the wall just unpinned itself. INFO, not
                    // WARNING: this is recovery, not a problem.
                    Logger?.Info(
                        $"Monitor {monitor}: the $layout token is valid again after being carried forward — re-pinning fresh.");
                }
                else if (oldEntryExists && !oldFingerprintByMonitor.ContainsKey(monitorKey))
                {
                    // Schema-tolerant migration (LayoutStateFile.MonitorFingerprints' own doc
                    // comment): a persisted plan exists for this monitor but no per-monitor
                    // fingerprint does — either an older file written before this fix, or a
                    // hand-edited one. Re-resolve once (this branch), logged so an operator seeing a
                    // tile move after an upgrade has an explanation on record; the NEXT resolve has a
                    // stored fingerprint and this branch is never reached again for this monitor.
                    Logger?.Info(
                        $"Monitor {monitor}: layout-state.json has no per-monitor fingerprint for it yet (upgraded from an older file, or hand-edited) — re-resolving once; an ordinal reference may re-pin to a different camera this cycle.");
                }

                var (resolvedPages, persistedPagesOut) = ResolveFreshPages(
                    validResult.Layout!.Pages, catalogById, input.OrderedEnabledCameraIds, input.CameraBindings);

                finalMonitors.Add(new ResolvedMonitorPlan(monitor, resolvedPages));
                newPersistedPlan[monitorKey] = persistedPagesOut;
                newMonitorFingerprints[monitorKey] = freshFingerprint;
                continue;
            }

            // This monitor's CURRENT token is invalid (LayoutSpecParser already logged why) — rule
            // 6c: fall back to whatever was last known good for it, if anything. Unconditional on
            // fingerprint state (always has been — see the class doc comment): a malformed token
            // that never changes hashes the same way every time, so fingerprint match/mismatch alone
            // was never what decides this branch either way.
            if (oldEntryExists)
            {
                var (resolvedPages, persistedPagesOut) = ReapplyPersistedPages(
                    oldPages!, catalogById, input.OrderedEnabledCameraIds, input.CameraBindings);

                Logger?.Warning(
                    $"Monitor {monitor}: the current $layout token is malformed — keeping the last-known-good layout (stale but valid beats no layout at all).");
                finalMonitors.Add(new ResolvedMonitorPlan(monitor, resolvedPages));
                newPersistedPlan[monitorKey] = persistedPagesOut;
                newMonitorFingerprints[monitorKey] = freshFingerprint;
                newCarriedForwardMonitors.Add(monitorKey);
            }

            // else: no persisted fallback either — this monitor gets no wall this cycle. If NO
            // monitor ends up with an entry at all, the caller falls back to Monitors[] auto-layout
            // (rule 6d) — see this method's own doc comment.
        }

        var newState = new LayoutStateFile
        {
            SchemaVersion = 1,
            MonitorFingerprints = newMonitorFingerprints,
            ResolvedPlan = newPersistedPlan,
            CarriedForwardMonitors = newCarriedForwardMonitors.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        };

        return new ResolveResult(new ResolvedWallPlan(finalMonitors), newState);
    }

    /// <summary>
    /// Round-4 buyer-review fix (pinned carrier authority at boot/recovery): renders the persisted
    /// last-known-good plan DIRECTLY, with no current tokens at all — the entry point for the one
    /// situation where "no current token" does NOT mean "the operator removed the layout": the
    /// configured layout-carrier recorder is pinned-missing (absent from the current selection), so
    /// its Description — the wall's only layout source — is temporarily unreadable, not deliberately
    /// blank. <see cref="Resolve"/> deliberately ignores persisted monitors without a current token
    /// (see the class doc comment's "orphaned entry" rule) because in every OTHER situation a
    /// missing token IS operator intent; routing this case through it would therefore resolve to
    /// zero monitors and drop the wall to the <c>Monitors[]</c> auto-grid — exactly the
    /// non-deterministic reshape during an outage that carrier pinning exists to prevent.
    ///
    /// Availability is recomputed fresh against the live catalog (same
    /// <see cref="ReapplyPersistedPages"/> path rule 6a uses — a camera that went missing while the
    /// carrier is unreachable still shows UNAVAILABLE, not a stale live tile), but nothing is
    /// re-pinned and no new state is produced: the caller (Program.cs's
    /// <c>ComputeWallFormSpecs</c>) never persists over the on-disk last-known-good while the
    /// carrier is missing (<c>RebuildWall</c>'s <c>carrierPinnedMissing</c> parameter), so the plan
    /// on disk survives untouched for the carrier's return. An absent/empty
    /// <paramref name="persistedState"/> (genuine first boot — nothing known-good exists) returns
    /// <see cref="ResolvedWallPlan.Empty"/> and the caller falls back to auto-grid, the only option
    /// left.
    /// </summary>
    public static ResolvedWallPlan ResolveFromPersistedOnly(
        LayoutStateFile? persistedState,
        IReadOnlyList<CameraCatalogEntry> catalog,
        IReadOnlyList<Guid> orderedEnabledCameraIds,
        IReadOnlyDictionary<string, Guid> bindings)
    {
        if (persistedState is null || persistedState.ResolvedPlan.Count == 0)
        {
            return ResolvedWallPlan.Empty;
        }

        var catalogById = catalog.ToDictionary(c => c.Id);
        var monitors = new List<ResolvedMonitorPlan>();

        // Deterministic monitor order (the dictionary's own order is serialization-dependent);
        // non-numeric keys (a hand-edited/corrupt file) are skipped rather than thrown on, the same
        // schema-tolerant reading LayoutStateFile applies everywhere else.
        foreach (var kv in persistedState.ResolvedPlan.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var monitor))
            {
                continue;
            }

            var (resolvedPages, _) = ReapplyPersistedPages(
                kv.Value, catalogById, orderedEnabledCameraIds, bindings);
            monitors.Add(new ResolvedMonitorPlan(monitor, resolvedPages));
        }

        return monitors.Count == 0
            ? ResolvedWallPlan.Empty
            : new ResolvedWallPlan(monitors.OrderBy(m => m.Monitor).ToList());
    }

    /// <summary>Fresh resolve of a just-parsed token's pages — every Ordinal member is re-indexed
    /// against <paramref name="orderedEnabledCameraIds"/> RIGHT NOW and the result is what gets
    /// pinned (persisted); every Alias/Guid member is resolved against the current bindings/catalog
    /// (they have no separate "pin" step since they're already stable identities).</summary>
    private static (IReadOnlyList<ResolvedPage> Resolved, List<PersistedPage> Persisted) ResolveFreshPages(
        IReadOnlyList<MatrixPage> pages,
        IReadOnlyDictionary<Guid, CameraCatalogEntry> catalogById,
        IReadOnlyList<Guid> orderedEnabledCameraIds,
        IReadOnlyDictionary<string, Guid> bindings)
    {
        var resolvedPages = new List<ResolvedPage>();
        var persistedOut = new List<PersistedPage>();

        foreach (var page in pages)
        {
            var resolvedRows = new List<ResolvedRow>();
            var persistedRows = new List<PersistedRow>();

            foreach (var row in page.Rows)
            {
                var resolvedCells = new List<ResolvedCell>();
                var persistedCells = new List<PersistedCell>();

                foreach (var cell in row)
                {
                    var resolvedMembers = new List<ResolvedMember>();
                    var persistedMembers = new List<PersistedMember>();

                    foreach (var member in cell.Members)
                    {
                        var (resolved, persisted) = ResolveMemberFresh(member, catalogById, orderedEnabledCameraIds, bindings);
                        resolvedMembers.Add(resolved);
                        persistedMembers.Add(persisted);
                    }

                    resolvedCells.Add(new ResolvedCell(resolvedMembers, cell.Col, cell.RowSpan, cell.ColSpan));
                    persistedCells.Add(new PersistedCell { Members = persistedMembers, Col = cell.Col, RowSpan = cell.RowSpan, ColSpan = cell.ColSpan });
                }

                resolvedRows.Add(new ResolvedRow(resolvedCells));
                persistedRows.Add(new PersistedRow { Cells = persistedCells });
            }

            // F4: IsUniform/GridColumns carry straight across from the just-parsed MatrixPage — see
            // ResolvedPage/PersistedPage's own doc comments for why they default false/0 rather than
            // "spans default to 1" the way a per-cell RowSpan/ColSpan does.
            resolvedPages.Add(new ResolvedPage(resolvedRows, page.IsUniform, page.GridColumns));
            persistedOut.Add(new PersistedPage { Rows = persistedRows, IsUniform = page.IsUniform, GridColumns = page.GridColumns });
        }

        return (resolvedPages, persistedOut);
    }

    /// <summary>Re-derives Available/UnavailableReason for a PERSISTED plan against the current
    /// catalog, WITHOUT touching any already-pinned member's <see cref="PersistedMember.CameraId"/>
    /// — the only members that get re-resolved here are the never-pinned ones (see the class doc
    /// comment's "pin on first success" rule).</summary>
    private static (IReadOnlyList<ResolvedPage> Resolved, List<PersistedPage> Persisted) ReapplyPersistedPages(
        List<PersistedPage> persistedPages,
        IReadOnlyDictionary<Guid, CameraCatalogEntry> catalogById,
        IReadOnlyList<Guid> orderedEnabledCameraIds,
        IReadOnlyDictionary<string, Guid> bindings)
    {
        var resolvedPages = new List<ResolvedPage>();
        var persistedOut = new List<PersistedPage>();

        foreach (var page in persistedPages)
        {
            var resolvedRows = new List<ResolvedRow>();
            var persistedRows = new List<PersistedRow>();

            foreach (var row in page.Rows)
            {
                var resolvedCells = new List<ResolvedCell>();
                var persistedCells = new List<PersistedCell>();

                foreach (var cell in row.Cells)
                {
                    var resolvedMembers = new List<ResolvedMember>();
                    var persistedMembers = new List<PersistedMember>();

                    foreach (var member in cell.Members)
                    {
                        var (resolved, persisted) = ReapplyMember(member, catalogById, orderedEnabledCameraIds, bindings);
                        resolvedMembers.Add(resolved);
                        persistedMembers.Add(persisted);
                    }

                    resolvedCells.Add(new ResolvedCell(resolvedMembers, cell.Col, cell.RowSpan, cell.ColSpan));
                    persistedCells.Add(new PersistedCell { Members = persistedMembers, Col = cell.Col, RowSpan = cell.RowSpan, ColSpan = cell.ColSpan });
                }

                resolvedRows.Add(new ResolvedRow(resolvedCells));
                persistedRows.Add(new PersistedRow { Cells = persistedCells });
            }

            // F4: carried straight across from the persisted page — see ResolveFreshPages' matching
            // comment. An OLDER file's page has IsUniform=false/GridColumns=0 already (see
            // PersistedPage's doc comment), so this naturally round-trips a pre-F4 file unchanged.
            resolvedPages.Add(new ResolvedPage(resolvedRows, page.IsUniform, page.GridColumns));
            persistedOut.Add(new PersistedPage { Rows = persistedRows, IsUniform = page.IsUniform, GridColumns = page.GridColumns });
        }

        return (resolvedPages, persistedOut);
    }

    private static (ResolvedMember Resolved, PersistedMember Persisted) ReapplyMember(
        PersistedMember persisted,
        IReadOnlyDictionary<Guid, CameraCatalogEntry> catalogById,
        IReadOnlyList<Guid> orderedEnabledCameraIds,
        IReadOnlyDictionary<string, Guid> bindings)
    {
        if (persisted.CameraId.HasValue)
        {
            // Already pinned — the id is untouchable; only its live availability can change.
            var kind = ParseKind(persisted.RefKind);
            var resolved = BuildAvailability(kind, persisted.RefLabel, persisted.CameraId.Value, catalogById);
            return (resolved, persisted);
        }

        // Never successfully pinned — nothing to protect yet, so retry exactly like a fresh resolve.
        var syntheticMember = ToCellMember(persisted);
        return ResolveMemberFresh(syntheticMember, catalogById, orderedEnabledCameraIds, bindings);
    }

    private static (ResolvedMember Resolved, PersistedMember Persisted) ResolveMemberFresh(
        CellMember member,
        IReadOnlyDictionary<Guid, CameraCatalogEntry> catalogById,
        IReadOnlyList<Guid> orderedEnabledCameraIds,
        IReadOnlyDictionary<string, Guid> bindings)
    {
        switch (member.Kind)
        {
            case CellMemberKind.Ordinal:
            {
                if (member.IsStructurallyValid && member.Ordinal >= 1 && member.Ordinal <= orderedEnabledCameraIds.Count)
                {
                    var id = orderedEnabledCameraIds[member.Ordinal - 1];
                    return (BuildAvailability(member.Kind, member.RefLabel, id, catalogById),
                        Persisted(member.Kind, member.RefLabel, id));
                }

                var reason = !member.IsStructurallyValid
                    ? $"ordinal {member.Ordinal} is not valid"
                    : $"ordinal {member.Ordinal} is out of range (only {orderedEnabledCameraIds.Count} enabled camera(s))";
                return (ResolvedMember.Unavailable(member.Kind, member.RefLabel, cameraId: null, reason),
                    Persisted(member.Kind, member.RefLabel, cameraId: null));
            }

            case CellMemberKind.Alias:
            {
                if (bindings.TryGetValue(member.Alias, out var id))
                {
                    return (BuildAvailability(member.Kind, member.RefLabel, id, catalogById),
                        Persisted(member.Kind, member.RefLabel, id));
                }

                var reason = $"alias '{member.Alias}' has no CameraBindings entry";
                return (ResolvedMember.Unavailable(member.Kind, member.RefLabel, cameraId: null, reason),
                    Persisted(member.Kind, member.RefLabel, cameraId: null));
            }

            case CellMemberKind.Guid:
            default:
            {
                // A guid literal IS its own target id — there's no separate "does it exist" lookup
                // step the way ordinal/alias have; BuildAvailability below is what decides whether
                // that id currently has a live, enabled camera behind it.
                return (BuildAvailability(member.Kind, member.RefLabel, member.Guid, catalogById),
                    Persisted(member.Kind, member.RefLabel, member.Guid));
            }
        }
    }

    private static ResolvedMember BuildAvailability(CellMemberKind kind, string refLabel, Guid cameraId, IReadOnlyDictionary<Guid, CameraCatalogEntry> catalogById)
    {
        if (!catalogById.TryGetValue(cameraId, out var entry))
        {
            return ResolvedMember.Unavailable(kind, refLabel, cameraId, $"camera {ShortGuid(cameraId)} not found");
        }

        if (!entry.Enabled)
        {
            return ResolvedMember.Unavailable(kind, refLabel, cameraId, $"{entry.Name} (disabled)");
        }

        return ResolvedMember.ForCamera(kind, refLabel, cameraId);
    }

    private static PersistedMember Persisted(CellMemberKind kind, string refLabel, Guid? cameraId) =>
        new() { RefKind = kind.ToString(), RefLabel = refLabel, CameraId = cameraId };

    private static CellMemberKind ParseKind(string refKind) => refKind switch
    {
        nameof(CellMemberKind.Alias) => CellMemberKind.Alias,
        nameof(CellMemberKind.Guid) => CellMemberKind.Guid,
        _ => CellMemberKind.Ordinal,
    };

    /// <summary>Reconstructs a synthetic <see cref="CellMember"/> from a never-pinned persisted
    /// member so it can be retried through the exact same <see cref="ResolveMemberFresh"/> path a
    /// brand-new token uses — see <see cref="ReapplyMember"/>.</summary>
    private static CellMember ToCellMember(PersistedMember persisted) => persisted.RefKind switch
    {
        nameof(CellMemberKind.Alias) => CellMember.ForAlias(persisted.RefLabel),
        nameof(CellMemberKind.Guid) => CellMember.ForGuid(Guid.Empty), // never-pinned Guid member cannot occur — see ResolveMemberFresh's Guid case — but stay total rather than throw.
        _ => int.TryParse(persisted.RefLabel, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            ? CellMember.ForOrdinal(ordinal)
            : CellMember.ForOrdinal(0),
    };

    private static string ShortGuid(Guid id) => id.ToString("N").Substring(0, 8);
}
