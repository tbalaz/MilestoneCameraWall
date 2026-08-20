using GridLookout.Logging;
using VideoOS.Platform;

namespace GridLookout.Milestone;

/// <summary>One recording server as F2 multi-recorder discovery sees it — retained for EVERY
/// recorder under this Management Server (not just the one this host matches, unlike
/// <see cref="RecorderLocator.Locate"/>'s single-recorder <c>RecorderMatch</c>), so
/// <c>RecordingServers[]</c> selection (Program.cs) has a full catalog to select from.
/// <see cref="AllCameras"/> mirrors <c>RecorderMatch.AllCameras</c> (enabled+disabled cameras, F3's
/// stable catalog) with one difference: every <see cref="CameraInfo"/> here carries
/// <see cref="CameraInfo.RecorderName"/> set to this descriptor's <see cref="Name"/>, so a
/// multi-recorder caption ("Recorder / Camera" — see <see cref="CameraInfo.DisplayName"/>) never
/// needs a second lookup.</summary>
public sealed record RecorderDescriptor(
    string Name,
    Guid Id,
    string HostName,
    string Description,
    IReadOnlyList<CameraInfo> AllCameras);

/// <summary>One RAW <c>RecordingServers[]</c> config entry, exactly as it appears in
/// <c>WallConfig.RecordingServers</c> (plain strings, "" meaning "not set") — kept as its own type
/// here rather than referencing <c>GridLookout.Config.WallConfig</c> directly so this Milestone-
/// namespace validation/selection logic stays decoupled from the Config layer, the same "raw
/// strings in, no direct config-type dependency" convention <c>Layout.CameraBindingResolver</c>
/// already follows for <c>CameraBindings</c>. Program.cs is the only production caller, adapting a
/// real <c>WallConfig.RecordingServers</c> entry into this shape.</summary>
public sealed record RawRecordingServerEntry(string Id, string HostName);

/// <summary>One VALIDATED <c>RecordingServers[]</c> selector — the output of
/// <see cref="RecorderCatalog.ValidateSelectors"/>. Exactly one of <see cref="Id"/>/<see cref="HostName"/>
/// is ever meaningful: <see cref="ById"/> tells the caller which (ValidateSelectors already rejected
/// every entry with both or neither set, so this type never represents an invalid selector at
/// all).</summary>
public sealed record RecordingServerSelector(Guid Id, string HostName)
{
    public bool ById => Id != Guid.Empty;
}

/// <summary>Outcome of <see cref="RecorderCatalog.Select"/>: the recorders actually selected, plus
/// human-readable problem descriptions (an entry matching nothing, or resolving to a recorder
/// already selected by a different entry) for the caller to log. Deliberately returns problems as
/// DATA rather than logging them directly — Program.cs's refresh tick calls <c>Select</c> on every
/// <c>ConfigRefreshSeconds</c> tick, and re-logging the SAME problem every tick forever would be
/// exactly the log-spam <c>CameraBindings</c>' own "validate once, at startup" comment (Program.cs)
/// already warns against; the caller diffs <see cref="Problems"/> against the previous tick's list
/// and logs only on change.</summary>
public sealed record SelectionResult(
    IReadOnlyList<RecorderDescriptor> Selected,
    IReadOnlyList<string> Problems);

/// <summary>Layout-carrier recorder feature: the outcome of <see cref="RecorderCatalog.ResolveLayoutCarrier"/>.
/// <see cref="Carrier"/> is null in two cases: the defensive empty-selection case (see that method's
/// own doc comment — <see cref="Problem"/> is also null there), and FIX 2's PINNED-authority case —
/// an EXPLICIT <c>LayoutRecorder</c> configured but currently unmatched (or ambiguous — see
/// <see cref="ResolveLayoutCarrier"/>'s own doc comment), where <see cref="Problem"/> IS non-null.
/// Auto-carrier mode (blank config) never returns a null Carrier with selected.Count &gt; 0 — it
/// always floats to <c>selected[0]</c>, unchanged pre-fix behavior; see the class-level "pinned vs.
/// floating" note on <see cref="ResolveLayoutCarrier"/> for why the two modes differ. Problem is
/// DATA, not a log call, for the same reason <see cref="SelectionResult.Problems"/> is — the caller
/// applies log-on-change.</summary>
public sealed record LayoutCarrierResult(RecorderDescriptor? Carrier, string? Problem);

/// <summary>Live per-recorder camera facts <see cref="RecorderCatalog.MergeCameras"/> produces —
/// <see cref="AllCameras"/> is the union across every selected recorder (enabled+disabled, F3's
/// stable catalog shape); <see cref="EnabledCameras"/> is the auto-layout/ordinal-resolution view,
/// sorted by "RecorderName / CameraName" (F2 point 4's merged-ordinal order) rather than
/// <c>RecorderMatch.Cameras</c>' single-recorder "Name only" sort.</summary>
public sealed record MergedCameraSet(
    IReadOnlyList<CameraInfo> AllCameras,
    IReadOnlyList<CameraInfo> EnabledCameras);

/// <summary>
/// F2 (multi-recorder walls): discovers EVERY recording server under this Management Server (not
/// just the one this host matches — the split-discovery-from-selection RecorderLocator's own doc
/// comment calls for) and selects/merges the subset a <c>RecordingServers[]</c> config names.
/// <see cref="Discover"/> is the only SDK-touching, untestable-without-a-live-session member (it
/// shares <see cref="RecorderLocator"/>'s tree-walking helpers — see those methods' own doc
/// comments for why); every other member here is pure/SDK-free and unit-tested directly.
///
/// Layout-carrier recorder feature: this class also owns multi-mode LAYOUT-SOURCE selection
/// (<see cref="ResolveLayoutCarrier"/>, <see cref="ResolveMultiRecorderLayoutSource"/>) alongside
/// recorder selection — deliberately kept here rather than split into the <c>Layout</c> namespace,
/// since "which recorder is the carrier" and "which string wins" are two halves of one decision
/// Program.cs always makes together, both scoped to the CURRENTLY selected recorder set exactly
/// like <see cref="Select"/> already is.
/// </summary>
public static class RecorderCatalog
{
    /// <summary>Optional logger — mirrors <see cref="RecorderLocator.Logger"/>; Program.cs sets it,
    /// tests leave it null.</summary>
    public static FileLogger? Logger { get; set; }

    /// <summary>Discovers every recording server item under every root, and — for each — its full
    /// camera set (enabled + disabled, F3-style). Returns an empty list (never null, never throws)
    /// on a discovery failure, exactly like <see cref="RecorderLocator.Locate"/>'s own
    /// "no roots -> empty candidates" degradation.</summary>
    public static IReadOnlyList<RecorderDescriptor> Discover()
    {
        var roots = Configuration.Instance.GetItems(ItemHierarchy.SystemDefined);
        if (roots is null || roots.Count == 0)
        {
            Logger?.Warning("RecorderCatalog.Discover: Configuration.Instance.GetItems() returned no root items.");
            return Array.Empty<RecorderDescriptor>();
        }

        var recorderItems = new List<Item>();
        foreach (var root in roots)
        {
            RecorderLocator.CollectServers(root, root, recorderItems, depth: 0);
        }

        var descriptors = new List<RecorderDescriptor>();
        foreach (var recorderItem in recorderItems)
        {
            var cameraItems = new List<Item>();
            RecorderLocator.CollectCameras(recorderItem, cameraItems, depth: 0);

            var allCameras = cameraItems
                .GroupBy(camera => camera.FQID.ObjectId)
                .Select(g => g.First())
                .Select(camera => new CameraInfo(camera.Name, camera.FQID.ObjectId, camera, camera.Enabled, recorderItem.Name, recorderItem.FQID.ObjectId))
                .ToList();

            string hostName = RecorderLocator.GetRecorderHostName(recorderItem) ?? string.Empty;
            string description = string.Empty;
            try
            {
                description = new VideoOS.Platform.ConfigurationItems.RecordingServer(recorderItem.FQID).Description ?? string.Empty;
            }
            catch (Exception ex)
            {
                // Layout-carrier recorder feature: EXACTLY ONE selected recorder's Description can
                // now be a live multi-mode layout source (case (b) — see WallConfig.Layout's doc
                // comment), so this failure is no longer purely cosmetic telemetry for every
                // recorder — for whichever one turns out to be the carrier, it means "layout
                // resolution sees this recorder as having no Description this tick" (same
                // empty-string degradation RecorderLocator.Locate already uses for single-recorder
                // mode). Still only ever a Warning: a recorder whose Description can't be read
                // degrades to no-tokens-for-it, never a fatal error.
                Logger?.Warning($"RecorderCatalog: could not read recorder description for '{recorderItem.Name}': {ex.Message}");
            }

            descriptors.Add(new RecorderDescriptor(recorderItem.Name, recorderItem.FQID.ObjectId, hostName, description, allCameras));
        }

        Logger?.Info($"RecorderCatalog: discovered {descriptors.Count} recorder(s): " +
            (descriptors.Count == 0 ? "(none)" : string.Join(", ", descriptors.Select(d => $"{d.Name} @{d.HostName}"))));
        return descriptors;
    }

    /// <summary>
    /// Layout-carrier recorder feature — live-lab bug fix: overlays LIVE, REST-fetched Description
    /// text onto <paramref name="catalog"/>. <see cref="Discover"/>'s own Description read goes
    /// through the SDK's <c>ConfigurationItems.RecordingServer</c> cache, which — per
    /// <c>Milestone.MilestoneSession.TryGetRecorderDescriptions</c>'s own doc comment — NEITHER
    /// <c>SDK.Environment.ReloadConfiguration</c> NOR <c>Configuration.RefreshConfiguration</c>
    /// invalidates: a Description edit in Management Client never reaches an already-running
    /// session through that path at all, only a fresh REST poll does. Single-recorder mode's own
    /// refresh tick has always worked around this (it overwrites <c>RecorderLocator.Locate</c>'s
    /// SDK-cached Description with a live REST read the same way) — multi mode never needed the
    /// workaround before this feature, because Description was never a layout source there (F2
    /// point 4). It is now, for exactly the layout-carrier recorder (case (b) — see
    /// <c>WallConfig.Layout</c>'s doc comment), so multi mode's refresh tick needs the identical
    /// live overlay.
    ///
    /// FIX 1 (GUID-keyed overlay): matched by recorder <see cref="RecorderDescriptor.Id"/>, never by
    /// <see cref="RecorderDescriptor.Name"/> — two recorders can legitimately share a display name
    /// (Management Client does not enforce uniqueness), and the pre-fix name-keyed
    /// <paramref name="liveDescriptions"/> collapsed same-named recorders into one dictionary entry,
    /// so ONE recorder's Description got applied to every recorder sharing its name. Id is the
    /// recorder's stable FQID ObjectId and is unique by construction, so this overlay can never
    /// cross-apply between two different recorders regardless of naming. A recorder with no matching
    /// REST entry (poll failed entirely, or this recorder's id wasn't in the response) keeps its
    /// SDK-cached — possibly stale — Description rather than being dropped; degrade, don't fail,
    /// same discipline <see cref="Discover"/> itself already follows for a per-recorder Description
    /// read failure.
    /// </summary>
    public static IReadOnlyList<RecorderDescriptor> ApplyLiveDescriptions(
        IReadOnlyList<RecorderDescriptor> catalog, IReadOnlyDictionary<Guid, string>? liveDescriptions)
    {
        if (liveDescriptions is null || liveDescriptions.Count == 0)
        {
            return catalog;
        }

        return catalog
            .Select(r => liveDescriptions.TryGetValue(r.Id, out var live) ? r with { Description = live } : r)
            .ToList();
    }

    /// <summary>
    /// FIX 4 (poll off the UI thread): whether the background live-description REST poll
    /// (<c>Milestone.MilestoneSession.TryGetRecorderDescriptions</c>, wrapped by
    /// <see cref="DescriptionPollWorker"/>) is even worth attempting this tick. Single-recorder mode
    /// always polls — its ONE recorder's Description is unconditionally the layout source there (see
    /// <c>WallConfig.Layout</c>'s doc comment: "single-recorder mode ignores this field entirely"),
    /// so <paramref name="configLayout"/> is irrelevant to it and never consulted. Multi-recorder
    /// mode skips the poll when <paramref name="configLayout"/> is non-blank — case (a) of the
    /// layout-source precedence always wins outright then, so no recorder's Description (carrier or
    /// otherwise) is ever read as a layout source, and paying for the REST round-trip every tick
    /// would be pure waste. Pure/no SDK — Program.cs's refresh tick is the only production caller.
    /// </summary>
    public static bool ShouldPollLiveDescriptions(bool multiRecorderMode, string configLayout) =>
        !multiRecorderMode || string.IsNullOrWhiteSpace(configLayout);

    /// <summary>
    /// F2 point 2: whether multi-recorder mode is active, given the <c>--recorder</c> CLI arg and
    /// the configured <c>RecordingServers[]</c> count — the first (and only conditional) tier of the
    /// full 4-tier precedence documented on <c>WallConfig.RecordingServers</c>: <c>--recorder</c>
    /// (forces legacy single-recorder mode, checked here) &gt; non-empty <c>RecordingServers[]</c>
    /// (multi mode) &gt; <c>RecorderNameOverride</c> &gt; hostname self-location (both legacy
    /// single-recorder mode — unconditional once multi mode is ruled out; nothing here decides
    /// between those two, <c>RecorderLocator.Locate</c>'s own nameOverride-vs-hostname logic already
    /// does). Pure — Program.cs's <c>Main</c> is the only production caller.
    /// </summary>
    public static bool IsMultiRecorderMode(string? recorderArg, int recordingServersCount) =>
        string.IsNullOrWhiteSpace(recorderArg) && recordingServersCount > 0;

    /// <summary>
    /// Validates raw <c>RecordingServers[]</c> entries ONCE (Program.cs calls this exactly once at
    /// startup, mirroring <c>Layout.CameraBindingResolver.Resolve</c>'s "validate the static shape
    /// once, warn immediately" convention — this can only change via a restart, so re-validating on
    /// every refresh tick would just re-log the same warnings forever):
    /// <list type="bullet">
    /// <item>Exactly one of <see cref="RawRecordingServerEntry.Id"/>/<see cref="RawRecordingServerEntry.HostName"/>
    /// must be set — both or neither is dropped, warned.</item>
    /// <item>A non-empty Id must parse as a GUID — an unparseable one is dropped, warned.</item>
    /// <item>A duplicate Id (or duplicate HostName, case-insensitive) among the entries is dropped,
    /// warned — first occurrence wins, matching every other "first wins" convention in this
    /// codebase (<c>LayoutSpecParser</c>'s duplicate monitor tokens, <c>CameraBindingResolver</c>'s
    /// duplicate aliases).</item>
    /// </list>
    /// This does NOT check the entries against a live catalog — a selector that is well-formed but
    /// matches no recorder (or two DIFFERENT well-formed selectors that happen to resolve to the
    /// SAME recorder) can only be detected dynamically, against a live <see cref="Discover"/> result
    /// — see <see cref="Select"/> for that half.
    /// </summary>
    public static IReadOnlyList<RecordingServerSelector> ValidateSelectors(IReadOnlyList<RawRecordingServerEntry> raw, Action<string>? warn)
    {
        var result = new List<RecordingServerSelector>();
        var seenIds = new HashSet<Guid>();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in raw)
        {
            bool hasId = entry.Id.Length > 0;
            bool hasHost = entry.HostName.Length > 0;
            if (hasId == hasHost)
            {
                warn?.Invoke($"RecordingServers entry has {(hasId ? "both Id and HostName" : "neither Id nor HostName")} set — exactly one is required; entry ignored.");
                continue;
            }

            if (hasId)
            {
                if (!Guid.TryParse(entry.Id, out var id))
                {
                    warn?.Invoke($"RecordingServers entry Id '{entry.Id}' is not a valid GUID — entry ignored.");
                    continue;
                }

                if (!seenIds.Add(id))
                {
                    warn?.Invoke($"RecordingServers entry Id '{entry.Id}' is a duplicate — first occurrence wins, this entry ignored.");
                    continue;
                }

                result.Add(new RecordingServerSelector(id, string.Empty));
            }
            else
            {
                if (!seenHosts.Add(entry.HostName))
                {
                    warn?.Invoke($"RecordingServers entry HostName '{entry.HostName}' is a duplicate — first occurrence wins, this entry ignored.");
                    continue;
                }

                result.Add(new RecordingServerSelector(Guid.Empty, entry.HostName));
            }
        }

        return result;
    }

    /// <summary>
    /// Matches each already-validated <paramref name="selectors"/> entry against
    /// <paramref name="catalog"/> — Id is authoritative; HostName is an exact (case-insensitive)
    /// match against the recorder's REGISTERED host, a migration fallback (see the F2 contract).
    /// An entry matching nothing is NOT fatal: it is recorded in <see cref="SelectionResult.Problems"/>
    /// and the wall continues with whatever else selected — NEVER implicitly falls back to "select
    /// everything". Two DIFFERENT selectors that resolve to the SAME recorder (e.g. one entry by Id,
    /// another by that same recorder's HostName) are deduplicated the same way — the second is a
    /// problem, not a second copy of that recorder's cameras (which would otherwise make the later
    /// <c>ToDictionary(c =&gt; c.Id)</c> merge in Program.cs throw on the duplicate camera ids).
    /// Pure — no logging here; see <see cref="SelectionResult.Problems"/>'s own doc comment for why.
    /// </summary>
    public static SelectionResult Select(IReadOnlyList<RecordingServerSelector> selectors, IReadOnlyList<RecorderDescriptor> catalog)
    {
        var selected = new List<RecorderDescriptor>();
        var problems = new List<string>();

        foreach (var selector in selectors)
        {
            RecorderDescriptor? match = selector.ById
                ? catalog.FirstOrDefault(r => r.Id == selector.Id)
                : catalog.FirstOrDefault(r => string.Equals(r.HostName, selector.HostName, StringComparison.OrdinalIgnoreCase));

            string selectorLabel = selector.ById ? $"Id={selector.Id}" : $"HostName='{selector.HostName}'";

            if (match is null)
            {
                problems.Add($"RecordingServers entry ({selectorLabel}) matched no recorder in the catalog — ignored; the wall continues with the remaining configured recorders.");
                continue;
            }

            if (selected.Any(r => r.Id == match.Id))
            {
                problems.Add($"RecordingServers entry ({selectorLabel}) resolves to recorder '{match.Name}', already selected by a different entry — duplicate ignored.");
                continue;
            }

            selected.Add(match);
        }

        return new SelectionResult(selected, problems);
    }

    /// <summary>
    /// Layout-carrier recorder feature: which ONE selected recorder's Description supplies
    /// <c>$layout{}</c> tokens for a multi-recorder wall — see <c>WallConfig.LayoutRecorder</c>'s
    /// own doc comment for the full precedence this is one half of. Matched against
    /// <paramref name="selected"/> — the CURRENTLY selected recorders only, not the wider catalog,
    /// mirroring <see cref="Select"/>'s own "the wall continues with whatever else selected"
    /// discipline. Tries a GUID parse first (matches <see cref="RecorderDescriptor.Id"/>) — a
    /// recorder display name that happens to BE a bare GUID string is not a realistic deployment
    /// (Management Client names are operator-chosen free text), so this ordering never actually
    /// shadows a legitimate name match in practice — then falls back to an exact, case-insensitive
    /// name match.
    ///
    /// FIX 2 (pinned carrier authority) — PINNED vs. FLOATING, the two modes this method now tells
    /// apart:
    /// <list type="bullet">
    /// <item><b>Auto-carrier (blank <paramref name="layoutRecorderConfig"/>)</b> — the operator never
    /// named an authority, so there is nothing to be unfaithful to: floats to
    /// <c>selected[0]</c> (<see cref="Select"/> preserves <c>RecordingServers[]</c> config order, so
    /// this really is "the first RecordingServers[] entry"), unconditionally, exactly like every
    /// release before this fix. <see cref="LayoutCarrierResult.Problem"/> is always null here.</item>
    /// <item><b>Pinned (non-blank <paramref name="layoutRecorderConfig"/>)</b> — the operator DID
    /// name an authority. When it matches EXACTLY ONE selected recorder, that recorder is the
    /// carrier, full stop. When it matches NONE (removed/offline/typo) or MORE THAN ONE (two
    /// selected recorders share the configured display name — the same collision FIX 1 closes for
    /// the description overlay; a GUID value can never be ambiguous, ids are unique by construction),
    /// the pin holds and NO recorder's Description is substituted — <see cref="LayoutCarrierResult.Carrier"/>
    /// is null, <see cref="LayoutCarrierResult.Problem"/> explains why. This is the fix itself: before
    /// it, an unmatched/ambiguous pin silently floated to <c>selected[0]</c>, so an unrelated
    /// recorder's Description could reshape the wall precisely during the outage the pin was meant
    /// to protect against. The caller (<c>Program.BuildMultiRecorderMatch</c>) is what actually keeps
    /// the wall on its last-known-good layout while <see cref="LayoutCarrierResult.Carrier"/> is
    /// null — see that method's own doc comment.</item>
    /// </list>
    /// Pure — no logging; the caller applies the same log-on-change discipline
    /// <c>Program.LogSelectionProblemsOnChange</c> already established for
    /// <see cref="SelectionResult.Problems"/>, wrapping <see cref="LayoutCarrierResult.Problem"/> as
    /// a 0-or-1-element list.
    /// </summary>
    public static LayoutCarrierResult ResolveLayoutCarrier(string layoutRecorderConfig, IReadOnlyList<RecorderDescriptor> selected)
    {
        if (selected.Count == 0)
        {
            // Defensive only — every production caller already checked selection.Selected.Count > 0
            // before reaching here (same precondition BuildMultiRecorderMatch's own callers observe).
            return new LayoutCarrierResult(null, null);
        }

        if (string.IsNullOrWhiteSpace(layoutRecorderConfig))
        {
            // Auto-carrier — always floats; see this method's own doc comment for why that stays
            // unconditional even after FIX 2.
            return new LayoutCarrierResult(selected[0], null);
        }

        if (Guid.TryParse(layoutRecorderConfig, out var id))
        {
            // A parsed guid is unique by construction — Select() already dedups selected recorders
            // by Id, so at most one match is even possible here; no ambiguity case to consider.
            var byId = selected.FirstOrDefault(r => r.Id == id);
            if (byId is not null)
            {
                return new LayoutCarrierResult(byId, null);
            }

            return PinnedMissing(layoutRecorderConfig, selected,
                $"LayoutRecorder '{layoutRecorderConfig}' does not match any currently selected recorder's Id");
        }

        var nameMatches = selected.Where(r => string.Equals(r.Name, layoutRecorderConfig, StringComparison.OrdinalIgnoreCase)).ToList();
        if (nameMatches.Count == 1)
        {
            return new LayoutCarrierResult(nameMatches[0], null);
        }

        if (nameMatches.Count > 1)
        {
            // FIX 1's collision, one layer up: two selected recorders share this display name, so
            // "the" carrier is genuinely ambiguous — picking either one silently (the pre-fix
            // FirstOrDefault behavior) is exactly the wrong-recorder-wins defect this feature closes.
            // Treated identically to no-match: pin holds, no Description adopted from anyone.
            return PinnedMissing(layoutRecorderConfig, selected,
                $"LayoutRecorder '{layoutRecorderConfig}' matches {nameMatches.Count} currently selected recorders by name (ambiguous — Management Client does not enforce unique display names; use the recorder's Id instead)");
        }

        return PinnedMissing(layoutRecorderConfig, selected,
            $"LayoutRecorder '{layoutRecorderConfig}' does not match any currently selected recorder's name");
    }

    /// <summary>FIX 2: the shared "pin holds, nothing adopted" result for
    /// <see cref="ResolveLayoutCarrier"/>'s no-match and ambiguous-match cases — see that method's
    /// own doc comment for the PINNED-vs-FLOATING distinction this implements.</summary>
    private static LayoutCarrierResult PinnedMissing(string layoutRecorderConfig, IReadOnlyList<RecorderDescriptor> selected, string reason) =>
        new(
            Carrier: null,
            Problem: $"{reason} (selected: {string.Join(", ", selected.Select(r => r.Name))}) — authority is PINNED: " +
                     "no other recorder's Description is adopted as a substitute layout source; the wall keeps its " +
                     $"last-known-good layout until '{layoutRecorderConfig}' resolves to exactly one selected recorder again.");

    /// <summary>
    /// Layout-carrier recorder feature: the multi-mode layout-SOURCE precedence itself — case (a)
    /// (<paramref name="configLayout"/> non-blank always wins, unchanged pre-feature behavior) vs.
    /// case (b) (the resolved carrier's own Description text) — see
    /// <c>WallConfig.Layout</c>'s doc comment for the full three-case precedence this covers
    /// the first two of. Case (c) (both blank -&gt; auto-grid) needs no special handling here: an
    /// empty return already degrades to "no monitor resolved anything" through the existing
    /// <c>Layout.LayoutSpecParser.Parse</c>/<c>Layout.LayoutResolver.Resolve</c> pipeline exactly
    /// like a single recorder with a blank Description always has. Extracted as its own pure
    /// function (rather than left inline in <c>Program.ComputeWallFormSpecs</c>) specifically so
    /// this precedence has a direct unit test independent of the live SDK.
    /// </summary>
    public static string ResolveMultiRecorderLayoutSource(string configLayout, string layoutCarrierDescription) =>
        string.IsNullOrWhiteSpace(configLayout) ? layoutCarrierDescription : configLayout;

    /// <summary>
    /// Exporter fix: the one-line, actionable error <c>--export-camera-bindings</c> prints (to
    /// stdout — see <c>Program.RunExportCameraBindingsMode</c>'s own doc comment for why stdout
    /// specifically, not the stderr every OTHER error in that method uses) and logs when run against
    /// a multi-recorder config with no <c>--recorder</c> given. Before this fix, that combination
    /// fell through to <c>RecorderLocator.Locate</c>'s single-recorder hostname self-location, which
    /// (correctly, for its own single-recorder-mode contract) almost always matches nothing and
    /// prints a generic "No matching recorder found" — accurate, but gives no hint that the REAL
    /// problem is "this is a multi-recorder config, name one", and lands on stderr, which a caller
    /// capturing only stdout never sees at all. Takes already-VALIDATED selectors (the caller runs
    /// <see cref="ValidateSelectors"/> first, same as every other production call site in this
    /// class) rather than raw config, so a malformed entry never shows up in the naming list.
    /// Labeled the same way <see cref="Select"/>'s own problem strings label a selector
    /// (<c>Id=&lt;guid&gt;</c> or the raw <c>HostName</c>) — pre-login, there is no live recorder
    /// NAME to show yet (discovering one needs the very login this error is short-circuiting), so
    /// the selector's own configured identity is the best available hint for which entry is which.
    /// </summary>
    public static string BuildMultiRecorderExportError(IReadOnlyList<RecordingServerSelector> selectors)
    {
        var labels = selectors.Select(s => s.ById ? s.Id.ToString() : s.HostName);
        return $"Multi-recorder configuration: pass --recorder <name> (selected: {string.Join(", ", labels)})";
    }

    /// <summary>
    /// Merges every selected recorder's camera set into ONE catalog for the wall to render from —
    /// <see cref="MergedCameraSet.AllCameras"/> (enabled+disabled, F3 shape) and
    /// <see cref="MergedCameraSet.EnabledCameras"/> (enabled only, sorted by "RecorderName /
    /// CameraName" — F2 point 4's merged-ordinal order, so a bare <c>$layout{}</c> ordinal or config
    /// <c>Monitors[].Cameras</c> range indexes a deterministic, documented order across recorders).
    /// A camera id appearing under more than one selected recorder (should be impossible — Milestone
    /// camera FQID ObjectIds are globally unique — but never trusted blindly across a module
    /// boundary any more than <c>Layout.LayoutResolver.LookupCamera</c> trusts its own invariants)
    /// keeps only the first occurrence and warns, rather than letting Program.cs's later
    /// <c>ToDictionary(c =&gt; c.Id)</c> throw and take the whole wall down with it.
    /// </summary>
    public static MergedCameraSet MergeCameras(IReadOnlyList<RecorderDescriptor> selected, Action<string>? warn)
    {
        var seen = new HashSet<Guid>();
        var allCameras = new List<CameraInfo>();

        foreach (var recorder in selected)
        {
            foreach (var camera in recorder.AllCameras)
            {
                if (!seen.Add(camera.Id))
                {
                    warn?.Invoke($"Camera id {camera.Id} appears under more than one selected recorder (also seen under '{recorder.Name}') — keeping the first occurrence only.");
                    continue;
                }

                allCameras.Add(camera);
            }
        }

        var enabledCameras = allCameras
            .Where(c => c.Enabled)
            .OrderBy(c => c.RecorderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id)
            .ToList();

        return new MergedCameraSet(allCameras, enabledCameras);
    }

    /// <summary>
    /// F2 point 6: the config-refresh rebuild-trigger signature — "selected recorder ids, camera
    /// ids+enabled per selected recorder, config Layout fingerprint" per the F2 contract. Unlike
    /// single-recorder mode's <c>Program.ComputeSignature</c> (which hashes only the pre-filtered
    /// ENABLED camera list, because <c>RecorderMatch.Cameras</c> already IS that filtered view — see
    /// that method's own doc comment), this hashes every selected recorder's FULL camera set
    /// (enabled and disabled) so a plain enable/disable toggle on a currently-disabled camera still
    /// triggers a rebuild, matching the same "camera ids+enabled" rule <c>RecorderMatch.Cameras</c>'
    /// doc comment describes for F3. <paramref name="configLayout"/> never actually changes within
    /// one process run (camerawall.json is loaded once at startup — no hot-reload), so including it
    /// is a defensive constant, not something this term expects to see change tick-to-tick on its
    /// own; it costs one extra hash term and future-proofs this signature if hot-reload is ever
    /// added. Recorders are hashed in Id order (not discovery/selection order) so this signature is
    /// deterministic regardless of catalog enumeration order.
    ///
    /// Layout-carrier recorder feature: <paramref name="layoutCarrierDescription"/> is the OPPOSITE
    /// of <paramref name="configLayout"/>'s "defensive constant" role above — it is EXACTLY the value
    /// this signature exists to catch changing, tick to tick, since the whole point of the feature is
    /// that an operator edits it live from Management Client. The caller
    /// (<c>Program.ResolveLayoutCarrierDescriptionForSignature</c>) passes the resolved carrier's
    /// current Description text when <paramref name="configLayout"/> is blank (case (b) is active —
    /// see <c>WallConfig.Layout</c>'s doc comment) and an empty string when it's non-blank (case (a)
    /// is active, so the carrier's Description isn't even consulted as a layout source — hashing it
    /// anyway would cause a spurious rebuild on every unrelated edit to that recorder's Description).
    /// Required, not optional/defaulted: a call site that forgets this argument silently reintroduces
    /// the exact bug this feature fixes (carrier edits never triggering a rebuild) with no compiler
    /// help, so every caller — production and test alike — must state its intent explicitly.
    /// </summary>
    public static int ComputeSelectionSignature(IReadOnlyList<RecorderDescriptor> selected, string configLayout, string layoutCarrierDescription)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + (configLayout ?? string.Empty).GetHashCode();
            hash = (hash * 31) + (layoutCarrierDescription ?? string.Empty).GetHashCode();

            foreach (var recorder in selected.OrderBy(r => r.Id))
            {
                hash = (hash * 31) + recorder.Id.GetHashCode();
                foreach (var camera in recorder.AllCameras.OrderBy(c => c.Id))
                {
                    hash = (hash * 31) + camera.Id.GetHashCode();
                    hash = (hash * 31) + camera.Enabled.GetHashCode();
                }
            }

            return hash;
        }
    }
}
