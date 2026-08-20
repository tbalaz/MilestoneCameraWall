namespace GridLookout.Monitoring;

/// <summary>Shared System.Text.Json options for health.json — both the controller's writer
/// (Program.cs) and the probe's reader (<c>HealthProbe</c>) serialize/deserialize
/// <see cref="WallHealthState"/> through this SAME options instance, so the on-disk shape (enum
/// names as strings, case-insensitive property matching) can never silently drift between the two
/// sides of this feature.</summary>
public static class HealthJsonOptions
{
    public static readonly System.Text.Json.JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>
/// Coarse lifecycle state of the CONTROLLER process itself (not the video/session state — see
/// <see cref="OverallStatus"/> and <see cref="WallFormHealth"/> for that) — written by the running
/// wall into <c>health.json</c> on every health-write tick. A controller can be <see cref="Running"/>
/// while one recorder or several cameras are degraded; that distinction is exactly why this is a
/// separate field from <see cref="OverallStatus"/> rather than folded into it.
/// </summary>
public enum ControllerState
{
    /// <summary>Process has started but has not yet attempted to log in to the Management Server.</summary>
    Starting,

    /// <summary>Logging in / locating the recorder — the boot <c>LoginRetryLoop</c>, or its
    /// mid-session re-entry from <c>RecoverSession</c>.</summary>
    Connecting,

    /// <summary>Wall forms are built and showing; normal operation.</summary>
    Running,

    /// <summary>Full session-level recovery is in progress (<c>Program.RecoverSession</c>) — tearing
    /// down every wall form, logging out, and re-entering <see cref="Connecting"/>.</summary>
    Recovering,

    /// <summary>Configuration is present but incomplete/invalid (e.g. <c>ManagementServerUri</c> is
    /// blank) — the process is parked on an error card, not attempting to connect to anything.
    /// Modeled here so the shared health/probe types have a complete vocabulary even though the
    /// current <c>Program.cs</c> wiring does not yet emit it — see the emulator-productization plan
    /// / this feature's own report for why that wiring was deferred rather than risked.</summary>
    ConfigError,
}

/// <summary>
/// Wall-level health classification — see <c>HealthStatusCalculator.Compute</c> for the exact rule.
/// Deliberately computed the SAME WAY by both the controller (self-reporting into <c>health.json</c>,
/// where "ui pulse fresh" is trivially true — the write is happening right now) and the external
/// <c>--health-probe</c> reader (recomputing independently from the file's age, where "ui pulse
/// fresh" can genuinely be false) — see <c>HealthProbeEvaluator</c>. A controller writing its own
/// file can therefore only ever record <see cref="Healthy"/> or <see cref="Degraded"/>;
/// <see cref="Unhealthy"/> is a verdict only an outside observer can reach, by construction — the
/// whole reason this feature exists rather than an in-process "I am alive" timer (a hung message
/// pump can leave callback threads alive and would happily keep writing "Healthy" forever).
/// </summary>
public enum OverallStatus
{
    Healthy,
    Degraded,
    Unhealthy,
}

/// <summary>Per-<c>WallForm</c> tile aggregate — one entry per configured monitor/window. All four
/// counts come from <c>WallForm.GetHealthSnapshot()</c>, itself derived from the SAME per-tile
/// <c>LastRenderedUtc</c>/<c>TileRecoveryScheduler</c> state the per-tile self-heal feature uses, not
/// a second independent tracking scheme.</summary>
public sealed class WallFormHealth
{
    public int MonitorNumber { get; set; }

    /// <summary>Grid cells that are supposed to be showing live video right now — i.e. cells with a
    /// resolved camera, NOT counting <c>$layout{}</c> cells rendered as an error/"invalid ordinal"
    /// tile (those were never going to show video regardless of connectivity, so counting them here
    /// would make an operator-authoring mistake look like a health problem).</summary>
    public int ExpectedTileCount { get; set; }

    /// <summary>Tiles that have rendered at least one frame since being built (see
    /// <c>WallForm</c>'s <c>LastRenderedUtc</c> — set only after JPEG decode AND UI-thread
    /// <c>PictureBox</c> assignment, never merely "the SDK delivered bytes").</summary>
    public int TilesWithFrames { get; set; }

    /// <summary>Tiles that HAVE rendered before but are currently past the STALLED threshold — see
    /// <c>WallForm.SweepStaleTiles</c>.</summary>
    public int StalledCount { get; set; }

    /// <summary>Tiles that have NEVER rendered a frame since being built (still connecting, or
    /// broken from the start) — a strict count of "zero frames ever", independent of whether
    /// per-tile self-heal (<c>TileRecoverSeconds</c>) is even enabled; that setting only controls
    /// whether GridLookout ATTEMPTS to fix this on its own, not whether the health signal reports
    /// it.</summary>
    public int NeverFramedCount { get; set; }

    /// <summary>Age, in seconds, of the freshest <c>LastRenderedUtc</c> across this form's tiles as
    /// of the health-write tick — null when this form has no tiles that have ever rendered
    /// anything (a status/error card, a zero-camera monitor, or every tile still never-framed).</summary>
    public double? FreshestRenderedAgeSeconds { get; set; }

    /// <summary>F3 (referentially stable layouts): grid cells rendered as the UNAVAILABLE
    /// placeholder (an unknown alias/guid, a still-out-of-range ordinal, or a well-formed reference
    /// to a now-missing/disabled camera — see <c>Layout.LayoutResolver</c>). These cells never had a
    /// <c>LiveTileSource</c> at all, so they are correctly excluded from <see cref="ExpectedTileCount"/>
    /// (which already only counts cells with a resolved camera — see that property's own doc
    /// comment) and from <see cref="NeverFramedCount"/> (which would otherwise misreport a
    /// deliberately-unavailable cell as a broken connection forever). A dedicated counter rather than
    /// folding these into an existing bucket, because "expected but currently unavailable" is neither
    /// "connecting" nor "broken" — it's an operator-authoring/catalog fact, not a connectivity
    /// one.</summary>
    public int UnavailableCount { get; set; }
}

/// <summary>F2 (multi-recorder walls): one recorder's tile aggregate across every wall
/// form/monitor it contributes tiles to — additive alongside <see cref="WallFormHealth"/>'s
/// per-form view, so a customer's collector can see a SPECIFIC recorder degrade even while another
/// selected recorder (possibly sharing a monitor with it — see F2 point 4's mixed-recorder cell
/// grammar) stays healthy. Only ever populated when <c>WallConfig.RecordingServers</c> is non-empty
/// (multi-recorder mode); <see cref="WallHealthState.Recorders"/> is an empty list for every
/// single-recorder deployment, which never changes health.json's values for them — see that
/// property's own doc comment.</summary>
public sealed class RecorderHealth
{
    /// <summary>The recorder's stable FQID ObjectId (see <c>Milestone.RecorderDescriptor.Id</c>), as
    /// a GUID string. Buyer-review defect #9 fix: this is now the KEY <c>Program.BuildRecorderHealthList</c>
    /// groups by (was <see cref="Name"/> — two differently-configured recorders can legitimately
    /// share a display name, which used to collapse them into one row and arbitrarily inherit
    /// whichever one's id the name lookup found first) — always populated for every entry that
    /// reaches this list, since the id itself is what put it there.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display-only — never part of how entries are grouped or looked up (see
    /// <see cref="Id"/>'s own doc comment). "" only in the defensive case where a recorder produced
    /// an unavailable count but no live tiles this tick AND is no longer in the last-selected
    /// catalog either (should not happen in practice — the controller only ever builds tiles from a
    /// just-selected catalog).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Tiles this recorder's cameras occupy right now (across every monitor) — mirrors
    /// <see cref="WallFormHealth.ExpectedTileCount"/>'s "cells with a resolved camera" convention,
    /// scoped to this one recorder.</summary>
    public int TilesExpected { get; set; }

    /// <summary>Tiles that have rendered at least one frame — mirrors
    /// <see cref="WallFormHealth.TilesWithFrames"/> exactly (INCLUDES currently-stalled tiles;
    /// <see cref="TilesStalled"/> is a SUBSET of this, not a separate bucket — same convention, so a
    /// customer comparing this per-recorder breakdown against the per-form aggregate in the same
    /// health.json never sees two different counting rules for what looks like the same
    /// number).</summary>
    public int TilesRendering { get; set; }

    /// <summary>Mirrors <see cref="WallFormHealth.StalledCount"/>, scoped to this recorder.</summary>
    public int TilesStalled { get; set; }

    /// <summary>Mirrors <see cref="WallFormHealth.NeverFramedCount"/>, scoped to this recorder.</summary>
    public int TilesNeverFramed { get; set; }

    /// <summary>Mirrors <see cref="WallFormHealth.UnavailableCount"/>, scoped to this recorder —
    /// best-effort: an UNAVAILABLE cell is only attributable to a recorder when its (pinned or
    /// attempted) reference resolved to a camera id found in that recorder's catalog; a reference
    /// that never resolved at all (unknown alias/guid, still-out-of-range ordinal) has no recorder
    /// to attribute and is not counted in ANY recorder's row here — see
    /// <c>Monitoring.RecorderHealthAggregator.AggregateUnavailableByRecorder</c>'s own doc comment.
    /// The sum of this field across every <see cref="WallHealthState.Recorders"/> entry is therefore
    /// always &lt;= the corresponding <see cref="WallFormHealth.UnavailableCount"/> total.</summary>
    public int TilesUnavailable { get; set; }
}

/// <summary>
/// The full <c>health.json</c> payload — written atomically (see <c>AtomicStateStore</c>) by the
/// running wall's health-write timer, and read back by <c>--health-probe</c> (see
/// <c>HealthProbe</c>/<c>HealthProbeEvaluator</c>). Contains ONLY liveness/aggregate data — no
/// camera names, VMS URI, recorder names, or credentials of any kind; see
/// docs/security.md's health.json section for the full content-class disclosure this
/// type is the source of truth for.
/// </summary>
public sealed class WallHealthState
{
    /// <summary>Bumped on any breaking change to this shape — a probe/collector reading an older
    /// schema than it understands should treat unknown fields as absent, not crash.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Config-driven (<see cref="GridLookout.Config.HealthConfig.ControllerId"/>) — free-text
    /// identity the customer's own collector uses to tell multiple walls apart; never derived from
    /// anything VMS-side.</summary>
    public string ControllerId { get; set; } = string.Empty;

    public int Pid { get; set; }

    public DateTime ProcessStartUtc { get; set; }

    /// <summary>Set from a WinForms UI-thread timer tick — see <c>Program.cs</c>'s health-write
    /// timer. THIS is the hang detector: a hung message pump stops this timer from ticking at all,
    /// so the value on disk simply stops advancing, which <c>--health-probe</c> (an OUTSIDE
    /// process, immune to the same hang) can detect by comparing its age to
    /// <see cref="GridLookout.Config.HealthConfig.StaleAfterSeconds"/>. <see cref="LiveTileSource.LastFrameUtc"/>-style
    /// "I received bytes on a callback thread" would NOT catch this — SDK callback threads can stay
    /// alive while the UI thread is wedged.</summary>
    public DateTime UiPulseUtc { get; set; }

    public ControllerState ControllerState { get; set; }

    public List<WallFormHealth> Forms { get; set; } = new();

    /// <summary>F2 (multi-recorder walls): per-recorder rollup — see <see cref="RecorderHealth"/>'s
    /// own doc comment. Additive: always an empty list for single-recorder mode
    /// (<c>WallConfig.RecordingServers</c> empty/absent), so this key's mere presence in health.json
    /// never signals multi-recorder mode by itself for an existing single-recorder deployment beyond
    /// the added (empty) JSON array.</summary>
    public List<RecorderHealth> Recorders { get; set; } = new();

    /// <summary>Buyer-review defect #1 fix: true when <c>WallConfig.RecordingServers</c> is
    /// non-empty (multi-recorder mode) AND at least one currently-configured selector matches no
    /// live recorder in the catalog (see <c>Milestone.RecorderCatalog.SelectionResult.Problems</c>)
    /// — i.e. a configured recorder has disappeared. Always <c>false</c> in single-recorder mode
    /// (there is no selector concept to go incomplete). Persisted here — not just folded silently
    /// into <see cref="OverallStatus"/> — specifically so <c>--health-probe</c> (which recomputes
    /// <see cref="OverallStatus"/> independently rather than trusting this field, per
    /// <c>HealthStatusCalculator</c>'s own doc comment) has the raw signal to recompute FROM: without
    /// this field on disk, the external probe would have no way to reach the same Degraded verdict
    /// the self-reporting controller does, reopening exactly the "external observer can't see what
    /// the controller sees" gap this whole feature exists to close.</summary>
    public bool RecorderSelectionIncomplete { get; set; }

    /// <summary>FIX 2 (pinned carrier authority): true when multi-recorder mode has an EXPLICIT
    /// <c>LayoutRecorder</c> configured (see <c>Config.WallConfig.LayoutRecorder</c>'s "PINNED vs.
    /// FLOATING" doc comment) that currently matches no selected recorder (removed, offline, or
    /// ambiguous — see <c>Milestone.RecorderCatalog.ResolveLayoutCarrier</c>). A SEPARATE signal from
    /// <see cref="RecorderSelectionIncomplete"/> — that field means a <c>RecordingServers[]</c>
    /// selector matched nothing; this one means the NAMED layout authority specifically is
    /// unreachable, a different failure an operator needs to act on differently (fix the pin, or
    /// wait for the recorder to return) — folding them into one field would let one condition mask
    /// the other in the log/health output. Always <c>false</c> for auto-carrier mode (blank
    /// <c>LayoutRecorder</c>) and for single-recorder mode: neither has a pinned authority to lose.
    /// While true, the wall deliberately keeps its last-known-good layout rather than adopting
    /// another recorder's Description — see <c>Program.BuildMultiRecorderMatch</c>'s own doc
    /// comment.</summary>
    public bool LayoutCarrierPinned { get; set; }

    /// <summary>See <see cref="OverallStatus"/>'s own doc comment for why a self-written value here
    /// can only ever be <see cref="Monitoring.OverallStatus.Healthy"/> or
    /// <see cref="Monitoring.OverallStatus.Degraded"/>, never <see cref="Monitoring.OverallStatus.Unhealthy"/>
    /// — buyer-review defect #1 fix note: that invariant is now conditioned on <c>ControllerState ==
    /// Running with at least one form</c>; see <c>HealthStatusCalculator.Compute</c>'s own doc
    /// comment for the one case (Running with ZERO forms) where the controller's own self-write CAN
    /// now legitimately record <see cref="Monitoring.OverallStatus.Unhealthy"/> too.</summary>
    public OverallStatus OverallStatus { get; set; }

    /// <summary>When this exact file was written — a convenience for a human/collector reading the
    /// raw JSON; <see cref="UiPulseUtc"/> (not this field) is what <c>--health-probe</c>'s
    /// staleness check actually uses, since a hung write path could in principle still leave a
    /// stale <see cref="WrittenUtc"/> sitting fresh-looking on disk under some future refactor —
    /// keeping the probe pinned to the field that is DEFINITIONALLY the hang signal is the safer
    /// invariant to preserve.</summary>
    public DateTime WrittenUtc { get; set; }
}
