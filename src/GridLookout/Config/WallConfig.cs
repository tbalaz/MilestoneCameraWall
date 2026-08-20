using GridLookout.Logging;

namespace GridLookout.Config;

/// <summary>Authentication mode for the one Milestone Management Server credential the app
/// handles — both modes are explicit credentials read from config; there is no ambient/local
/// Windows account involvement of any kind.</summary>
public enum AuthMode
{
    /// <summary>AD/local mirror Windows account: <see cref="WallConfig.Username"/> +
    /// <see cref="WallConfig.Domain"/> + password, via Negotiate.</summary>
    Windows,

    /// <summary>Milestone basic (XProtect basic/IDP) user: <see cref="WallConfig.Username"/> +
    /// password, via the Basic scheme.</summary>
    Basic,
}

public sealed class MonitorConfig
{
    public int Monitor { get; set; } = 1;

    /// <summary>"all", "1-4", or "5,6,7" — ordinals in the recorder's sorted camera order. In F2
    /// multi-recorder mode (see <see cref="WallConfig.RecordingServers"/>), these ordinals index the
    /// MERGED camera list across every selected recorder, sorted by "RecorderName / CameraName" —
    /// see <c>Milestone.RecorderCatalog.MergeCameras</c>. That merge order shifts whenever ANY
    /// selected recorder's camera set changes, not just the one the operator is thinking about;
    /// Program.cs logs an INFO advisory at startup when multi mode is active and any Monitors[]
    /// entry uses a range other than "all", for the same reason it does for an ordinal
    /// <c>$layout{}</c> reference — see F2 point 4.</summary>
    public string Cameras { get; set; } = "all";
}

/// <summary>F2 (multi-recorder walls): one <see cref="WallConfig.RecordingServers"/> entry —
/// selects a recording server by EITHER its stable <see cref="Id"/> (authoritative) OR its exact
/// registered <see cref="HostName"/> (a migration fallback, matching <see cref="WallConfig.RecorderNameOverride"/>'s
/// existing host-matching convention). Exactly one of the two must be set; both or neither makes the
/// entry ignored (warned) at startup — see <c>Milestone.RecorderCatalog.ValidateSelectors</c>.</summary>
public sealed class RecordingServerConfig
{
    /// <summary>The recorder's stable FQID ObjectId, as a GUID string. Authoritative — prefer this
    /// over <see cref="HostName"/> whenever it's known, since a hostname can be re-registered to a
    /// different physical recorder while an id cannot.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Exact match against the recorder's REGISTERED host (as configured in Management
    /// Client), not a DNS/short-name fuzzy match — a migration fallback for a deployment that hasn't
    /// captured recorder ids yet. Mutually exclusive with <see cref="Id"/>.</summary>
    public string HostName { get; set; } = string.Empty;
}

/// <summary>Exact desktop coordinates/size for the first/default monitor's wall window — see
/// <see cref="WallConfig.WindowBounds"/>.</summary>
public sealed class WindowBoundsConfig
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

/// <summary>Wall-health monitoring — see <see cref="WallConfig.Health"/> and
/// <c>GridLookout.Monitoring.HealthProbe</c>. Entirely opt-in: <see cref="Enabled"/> defaults false,
/// and at false the running wall writes health.json for nobody and <c>--health-probe</c> always
/// reports "absent" — no behavior change at all from a build without this feature. This is an
/// EXTERNAL-OBSERVER design, not an in-process "I am alive" timer: the running wall only ever
/// WRITES a local file (no inbound listener, no outbound call from the long-lived GUI process
/// itself); the optional HTTPS POST to <see cref="Endpoint"/> is made by the separate, short-lived
/// <c>--health-probe</c> invocation only — see docs/security.md for the full network
/// disclosure this class is the source of truth for.</summary>
public sealed class HealthConfig
{
    public bool Enabled { get; set; }

    /// <summary>Free-text identity a customer's own collector uses to tell multiple walls apart —
    /// defaults to the machine name, never derived from anything VMS-side. (Round-5 doc-accuracy
    /// fix: this field itself carries no VMS identity, but multi-recorder health.json entries DO
    /// carry recorder display names — see security.md's health.json content-class
    /// disclosure; the old claim here that "no recorder identity ever appears" predated F2's
    /// per-recorder rollup.)</summary>
    public string ControllerId { get; set; } = Environment.MachineName;

    /// <summary>Optional HTTPS endpoint <c>--health-probe</c> POSTs the raw health.json content to.
    /// Empty (default) means no POST is ever attempted — the local file remains usable by an
    /// installed monitoring agent with no network involvement at all. A plain <c>http://</c> value
    /// is REFUSED unless <see cref="AllowInsecureEndpoint"/> opts in — see that property.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Round-5 buyer-review fix (the health-endpoint mirror of the top-level
    /// <c>AllowInsecureLayoutPoll</c>): must be explicitly <c>true</c> to permit the
    /// <c>--health-probe</c> POST to a plain <c>http://</c> <see cref="Endpoint"/> — that POST can
    /// carry <c>Authorization: Bearer</c> (see <see cref="BearerToken"/>) plus the wall's health
    /// content, all cleartext on the wire over http. Refused (POST skipped, reason in the probe's
    /// printed JSON) otherwise. Lab/dev opt-in only, exactly like its layout-poll sibling.</summary>
    public bool AllowInsecureEndpoint { get; set; }

    /// <summary>Age, in seconds, past which the health.json UI-pulse timestamp (see
    /// <c>GridLookout.Monitoring.HealthProbeEvaluator</c>) is judged a hung UI thread rather than a
    /// merely-quiet tick.</summary>
    public int StaleAfterSeconds { get; set; } = 30;

    /// <summary>Timeout, in seconds, for the optional outbound POST — bounds how long a single
    /// <c>--health-probe</c> invocation can block on an unreachable/slow customer endpoint.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Dev-only plaintext bearer token; auto-migrated to <see cref="BearerTokenProtected"/>
    /// on first run and blanked — mirrors <see cref="WallConfig.Password"/>/<see cref="WallConfig.PasswordProtected"/>
    /// exactly, including the DPAPI-unavailable degradation path (see
    /// <see cref="GridLookout.Config.WallConfigLoader"/>'s Password-migration doc comments; the
    /// BearerToken migration block mirrors that logic field-for-field).</summary>
    public string BearerToken { get; set; } = string.Empty;

    /// <summary>DPAPI CurrentUser-scope blob, base64 — same guarantees as
    /// <see cref="WallConfig.PasswordProtected"/>.</summary>
    public string BearerTokenProtected { get; set; } = string.Empty;
}

/// <summary>camerawall.json schema.</summary>
public sealed class WallConfig
{
    public string ManagementServerUri { get; set; } = string.Empty;

    /// <summary>Two modes, both explicit credentials supplied via this config file (no
    /// ambient/local Windows account involvement of any kind): <see cref="Config.AuthMode.Basic"/>
    /// (XProtect basic user) or <see cref="Config.AuthMode.Windows"/> (AD/local mirror account via
    /// <see cref="Username"/>+<see cref="Domain"/>). Either way the credential is DPAPI-protected
    /// after first run — see <see cref="PasswordProtected"/>.</summary>
    public AuthMode AuthMode { get; set; } = AuthMode.Basic;

    public string Username { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    /// <summary>Dev-only plaintext password; auto-migrated to <see cref="PasswordProtected"/> on
    /// first run and blanked. Never left populated once the app has run once.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>DPAPI CurrentUser-scope blob, base64 — only the account that wrote it can
    /// decrypt it.</summary>
    public string PasswordProtected { get; set; } = string.Empty;

    public bool AllowInsecureBasic { get; set; }

    public string RecorderNameOverride { get; set; } = string.Empty;

    public int ReconnectSeconds { get; set; } = 15;

    public int ConfigRefreshSeconds { get; set; } = 60;

    /// <summary>Minimum severity written to the log file (Debug/Info/Warning/Error). Bootstrap
    /// order note: FileLogger is constructed at the default (<see cref="LogLevel.Info"/>) BEFORE
    /// this config is loaded (so early startup/config-load messages are never silently lost),
    /// then Program.cs applies this value to <see cref="FileLogger.MinimumLevel"/>.</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>T2: days of gridlookout-*.log files (the normal daily file and the per-user
    /// cross-user-retry variant both count — see <see cref="GridLookout.Logging.FileLogger.Write"/>)
    /// to keep in the effective log directory; anything older is deleted once at startup via
    /// <see cref="GridLookout.Logging.FileLogger.ApplyRetention"/>, called from
    /// <c>Program.Main</c> right after this config is loaded (same bootstrap-order note as
    /// <see cref="LogLevel"/> — the logger exists before this config does). Default <c>30</c>.
    /// <c>0</c> disables pruning entirely — keep forever.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>When true, each wall window shows a header strip (recorder name + ticking clock)
    /// and each camera tile shows a caption bar with its display name. When false (default):
    /// edge-to-edge video, no strips.</summary>
    public bool ShowHeader { get; set; }

    /// <summary>Pixel width of the thin border shown between camera tiles (and around the grid
    /// edge). 0 means no border — tiles sit edge-to-edge.</summary>
    public int TileBorderWidth { get; set; } = 1;

    /// <summary>Border color as "#RRGGBB", applied behind tile margins. Invalid values fall back
    /// to black at render time (see <c>WallForm</c>).</summary>
    public string TileBorderColor { get; set; } = "#404040";

    /// <summary>Seconds of no new live frame before a tile shows a red "STALLED" overlay; applies
    /// only to tiles that have received at least one frame (a tile still connecting/erroring keeps
    /// its existing status appearance, never gets marked stalled). 0 disables the feature entirely
    /// — no overlay, no sweep timer. See <c>WallForm</c>.</summary>
    public int StaleSeconds { get; set; } = 10;

    /// <summary>When true (default), the app asserts <c>ES_SYSTEM_REQUIRED</c>/<c>ES_DISPLAY_REQUIRED</c>
    /// via <see cref="GridLookout.Interop.PowerGuard"/> for as long as it runs, so Windows
    /// display/system sleep never kicks in on an otherwise-idle kiosk box showing live video. False
    /// means the app never touches thread execution state at all.</summary>
    public bool KeepDisplayAwake { get; set; } = true;

    /// <summary>Optional exact desktop coordinates/size for the first/default monitor's wall
    /// window (still borderless, still topmost) — null (default) keeps the normal
    /// fullscreen-on-monitor behavior. Only the first/default monitor's form is affected: any
    /// additional monitors configured via <see cref="Monitors"/> or <c>$layoutN</c> tokens always
    /// stay fullscreen on their own screen regardless of this setting.</summary>
    public WindowBoundsConfig? WindowBounds { get; set; }

    public List<MonitorConfig> Monitors { get; set; } = new()
    {
        new MonitorConfig { Monitor = 1, Cameras = "all" },
    };

    /// <summary>When true (default), each tile's requested live-stream frame size is computed from
    /// its actual on-screen pixel size (grid geometry at the moment the grid is built) instead of a
    /// flat 1280x720 for every tile — a decoded frame is roughly proportional to width*height, so a
    /// smaller on-screen tile requests a smaller frame and decodes/holds a proportionally smaller
    /// bitmap. Sizing is computed once per grid build from the form's current bounds (normally
    /// fullscreen); it does NOT re-negotiate live on resize — see <c>WallForm.BuildGrid</c>. When
    /// false: every tile requests the flat 1280x720, regardless of on-screen size (set this false
    /// if a driver/server misbehaves with non-standard requested sizes).</summary>
    public bool FitFrameSizeToTile { get; set; } = true;

    /// <summary>Caps the live-stream frame rate requested per tile via <c>JPEGLiveSource.FPS</c>
    /// (declared on the SDK's <c>VideoLiveSource</c> base — "Possible downscale of FPS" per its own
    /// XML doc comment). Default <c>12</c> — most wallboard viewing does not need the source's
    /// native rate (commonly up to 30fps), and every dropped frame is a decode + bitmap allocation
    /// never done. <c>0</c> disables the cap: no FPS is set on the source, so the server's own
    /// default/native rate applies.</summary>
    public int MaxFps { get; set; } = 12;

    /// <summary>Seconds each auto-layout page is shown before rotating to the next; <c>0</c>
    /// (default) disables AUTO-LAYOUT rotation entirely — a single page shows every camera,
    /// exactly the pre-paging auto-layout behavior. Read by two independent rotation paths:
    /// <list type="bullet">
    /// <item>Auto-layout (<c>Monitors[]</c> / <see cref="GridLookout.UI.WallForm.RenderPagedAutoLayout"/>):
    /// <c>0</c> means off, as above. A nonzero value below 10 is treated as 10 — a page flip tears
    /// down and rebuilds live sources (see <see cref="PageSize"/>), which costs roughly 2s, so a
    /// faster interval would thrash.</item>
    /// <item>Matrix (<c>$layout{...|...}</c> — <c>|</c>-separated pages inside the token —
    /// <see cref="GridLookout.UI.WallForm.RenderMatrixLayout"/>): only relevant when the
    /// token actually has more than one <c>|</c> page; a single-page (no <c>|</c>) matrix never
    /// rotates regardless of this value. When it DOES have multiple pages, <c>0</c>/unset does
    /// NOT disable rotation the way it does for auto-layout — writing more than one <c>|</c> page
    /// is itself the operator's request to rotate, so an unset value falls back to the same 10s
    /// floor instead of freezing on page one forever; a nonzero value is clamped to that same 10s
    /// floor exactly like the auto-layout path. <see cref="PageSize"/> is never consulted for
    /// matrix pages — each page's cell count is whatever the operator wrote in that page's
    /// segment.</item>
    /// </list></summary>
    public int PageSeconds { get; set; } = 0;

    /// <summary>Cameras per rotating page while auto-layout page rotation
    /// (<see cref="GridLookout.UI.WallForm.RenderPagedAutoLayout"/>) is active. Only the
    /// current page's <c>LiveTileSource</c>s exist at any moment — a page flip tears down the
    /// outgoing page's live sources and builds the incoming page's via the same
    /// <c>LiveTileSource.Shutdown()</c> + <c>DisposeTiles</c> teardown a config-refresh rebuild
    /// already uses — so memory scales with page size, not total camera count. Has no effect when
    /// <see cref="PageSeconds"/> is 0, when the recorder has fewer cameras than this, OR when the
    /// recorder description carries a <c>$layout{}</c> matrix — matrix pages are sized by whatever
    /// the operator wrote in each <c>|</c>-separated segment, never by this setting, whether the
    /// matrix has one page or several. Clamped at use-site: a value below 1 is treated as
    /// 1.</summary>
    public int PageSize { get; set; } = 9;

    /// <summary>Seconds between camera flips for a rotating tile — a <c>$layout{}</c> matrix cell
    /// written as <c>A(3,4,5)</c> instead of the fixed <c>A1</c> form (see
    /// <see cref="GridLookout.Layout.LayoutSpecParser"/>). Default <c>10</c>; clamped at
    /// use-site (<see cref="GridLookout.UI.WallForm.RenderMatrixLayout"/>) to a 5s floor —
    /// a flip reconnects that camera's live stream, which costs real time, so a faster interval
    /// would thrash. Unlike <see cref="PageSeconds"/> for auto-layout, <c>0</c>/unset does NOT
    /// disable rotation: writing more than one ordinal in a cell's parens is itself the operator's
    /// request to rotate, exactly like writing more than one <c>|</c> page is for matrix paging —
    /// an unset value just falls back to the 5s floor instead of freezing on the first camera.
    /// Only one shared timer drives every rotating tile on a page in lockstep, allocated only when
    /// the page actually has at least one rotating cell.</summary>
    public int TileRotateSeconds { get; set; } = 10;

    /// <summary>How each tile scales its live frame to fill its cell: <c>"Fit"</c> (default,
    /// aspect-preserving letterbox — PictureBox SizeMode Zoom), <c>"Fill"</c> (aspect-preserving
    /// cover — scale until fully covered, crop overflow, centered; no native PictureBoxSizeMode
    /// equivalent, drawn by <see cref="GridLookout.UI.ScalableTilePictureBox"/>), or
    /// <c>"Stretch"</c> (ignore aspect ratio — PictureBoxSizeMode StretchImage). Case-insensitive;
    /// parsed by <see cref="TileScaleModeParser"/>, which never throws — an unrecognized value
    /// falls back to Fit with a logged warning rather than crashing config load over a typo.</summary>
    public string TileScaleMode { get; set; } = "Fit";

    /// <summary>S8: when true, a wall window can no longer be exited or de-kiosked from the
    /// keyboard/mouse — <c>Esc</c> no longer calls <c>Application.Exit()</c> and the double-click
    /// compact/fullscreen toggle (<c>WallForm.ToggleCompact</c>) is disabled, so a passerby cannot
    /// blank the wall by exiting the app or minimizing it to a normal, non-topmost window that then
    /// stays that way forever (the watchdog only checks whether the process exists, not its window
    /// state). Default <c>false</c> — unlocked, matching every prior release's behavior. With this
    /// true, the ONLY operator stop path is Task Manager or an MSI uninstall; there is deliberately
    /// no secondary hotkey escape hatch (the product has no minimize hotkey at all — see
    /// <c>WallForm</c>'s Esc doc comment — so there is nothing left to gate a "deliberate" bypass
    /// behind). <c>Program.Main</c> logs one Info line at startup when this is active, precisely
    /// because the on-screen posture then gives an operator no other way to tell.</summary>
    public bool KioskLock { get; set; }

    /// <summary>Seconds a stalled (has-framed, no new frame for longer than <see cref="StaleSeconds"/>)
    /// or never-framed (no first frame within this many seconds of the tile being built) tile waits
    /// before <c>WallForm</c>'s stale sweep tears down and rebuilds just that ONE tile's live
    /// source, independent of every other tile on the wall — the per-tile counterpart to
    /// <c>GridLookout.Recovery.SessionLossDetector</c>'s whole-session recovery, which only fires
    /// when EVERY tile is stale simultaneously and therefore never helps a single wedged camera.
    /// Default <c>30</c>. <c>0</c> disables per-tile recovery entirely — the stale sweep then
    /// behaves exactly as it did before this feature existed (STALLED overlay only, no
    /// reconnect, no NO SIGNAL overlay for never-framed tiles). A nonzero value below 10 is floored
    /// to 10 — a reconnect attempt tears down and rebuilds a live source, which costs real time, so
    /// a faster base interval would thrash exactly like <see cref="TileRotateSeconds"/>'s floor.
    /// Each subsequent attempt for the SAME bad spell doubles the wait, capped at 8x this value —
    /// see <c>GridLookout.Monitoring.TileRecoveryScheduler</c>.</summary>
    public int TileRecoverSeconds { get; set; } = 30;

    /// <summary>Wall-health monitoring — see <see cref="HealthConfig"/>'s own doc comment. Default
    /// (a fresh <see cref="HealthConfig"/>) has <see cref="HealthConfig.Enabled"/> false, i.e. this
    /// entire feature is off by default and writes nothing.</summary>
    public HealthConfig Health { get; set; } = new();

    /// <summary>F3 (referentially stable layouts): stable alias -&gt; camera-guid-string bindings a
    /// <c>$layout{}</c> token can reference via <c>A@alias</c> instead of a positional ordinal (e.g.
    /// <c>{ "front-gate": "8fa2...guid..." }</c>). Alias keys must match <c>[a-z0-9-]+</c>
    /// (case-insensitive); values must parse as a GUID. Both are validated once at config load —
    /// see <c>Layout.CameraBindingResolver</c> — with a bad entry ignored (warned, not fatal) rather
    /// than blocking the rest of the file. Raw string-to-string here (not a richer object) because
    /// today's only supported topology is a single recording server (see
    /// <c>Milestone.RecorderLocator</c>); a future multi-recorder feature can widen an individual
    /// value to an object without breaking any file already on disk — see
    /// <c>Layout.CameraBindingResolver</c>'s own doc comment.</summary>
    public Dictionary<string, string> CameraBindings { get; set; } = new();

    /// <summary>
    /// F2 (multi-recorder walls): recording servers this wall should show, all under ONE Management
    /// Server / one login / one <c>MilestoneSession</c> (several Management Servers, or federation
    /// across a parent/child site tree, are out of scope — see <c>Milestone.MilestoneSession</c>'s
    /// <c>masterOnly: true</c> comment). Empty (the default) or absent means single-recorder mode —
    /// the ENTIRE pre-F2 codepath, byte-for-byte unchanged. A non-empty list means multi-recorder
    /// mode: <c>Milestone.RecorderCatalog</c> discovers every recorder under the Management Server
    /// and selects the subset matching these entries — an entry matching nothing is a startup/refresh
    /// warning, never fatal, and this list NEVER implicitly expands to "every recorder" (adding a new
    /// recorder to the site must never silently open dozens of new streams on an existing wall).
    ///
    /// Selection precedence overall (highest first): <c>--recorder</c> CLI arg (forces legacy
    /// single-recorder mode, exactly as before F2) &gt; a non-empty <see cref="RecordingServers"/>
    /// (multi mode) &gt; <see cref="RecorderNameOverride"/> (legacy single mode) &gt; hostname
    /// self-location (legacy single mode).
    ///
    /// Hot-reload is explicitly out of scope for v1 — editing this list requires a restart; see
    /// <see cref="Layout"/> and <see cref="LayoutRecorder"/>'s doc comments for the two configs that
    /// pair with it (which recorder(s) are shown vs. which one, if any, supplies the layout).</summary>
    public List<RecordingServerConfig> RecordingServers { get; set; } = new();

    /// <summary>
    /// F2 (multi-recorder walls): the <c>$layout{}</c>/<c>$layoutN{}</c> matrix for a multi-recorder
    /// wall — same token grammar <c>Layout.LayoutSpecParser</c> already parses out of a SINGLE
    /// recorder's Description (see that class's own doc comment), just read from THIS config string
    /// instead. Only consulted when <see cref="RecordingServers"/> is non-empty (multi-recorder
    /// mode); single-recorder mode keeps reading the recorder's own Description exactly as before F2
    /// and ignores this field entirely.
    ///
    /// Layout-carrier recorder feature: a non-blank value here always wins outright (unchanged
    /// pre-feature behavior — see <see cref="LayoutRecorder"/> for the full multi-mode precedence).
    /// Blank (default) hands layout selection to exactly ONE selected recorder's own Description
    /// instead (the "layout-carrier" recorder — <see cref="LayoutRecorder"/> names which one),
    /// letting an operator manage a multi-recorder wall's layout from Management Client without a
    /// file edit + restart; NOT a return to the pre-F2 "every recorder's Description is a source"
    /// ambiguity — still exactly ONE recorder's Description is ever read. Both this field and the
    /// carrier's Description (when in play) feed the exact SAME
    /// <c>Layout.LayoutSpecParser</c>/<c>Layout.LayoutResolver</c> pipeline — fingerprinting,
    /// last-known-good carry-forward, and <c>layout-state.json</c> persistence all still apply.
    /// Neither present means auto-grid of every selected recorder's enabled cameras, exactly like a
    /// single recorder with no <c>$layout{}</c> token in its Description.
    ///
    /// A bare ordinal reference (e.g. <c>A3</c>) here indexes the MERGED camera list across every
    /// selected recorder, sorted by "RecorderName / CameraName" — legal, but that order shifts
    /// whenever ANY selected recorder's camera set changes. Prefer <c>@alias</c>/<c>@{guid}</c>
    /// references, which stay stable across recorder/camera churn exactly like they do in
    /// single-recorder mode; Program.cs logs an INFO advisory once at startup when an ordinal
    /// reference is used here in multi mode.</summary>
    public string Layout { get; set; } = string.Empty;

    /// <summary>
    /// Layout-carrier recorder feature: in multi-recorder mode, which ONE selected recorder's
    /// Description supplies <c>$layout{}</c> tokens when <see cref="Layout"/> itself is blank (case
    /// (b) of the precedence below) — before this existed, a multi-recorder wall had no
    /// Management-Client-editable layout source at all; every layout change needed a config file
    /// edit + restart. Value is either the recorder's display NAME (exact, case-insensitive) or its
    /// stable Id (guid string) — matched against the CURRENTLY selected recorders only (see
    /// <c>Milestone.RecorderCatalog.ResolveLayoutCarrier</c>), never the wider catalog.
    ///
    /// PINNED vs. FLOATING (FIX 2) — blank and non-blank behave differently on a match failure, and
    /// that difference is deliberate:
    /// <list type="bullet">
    /// <item><b>Blank (default)</b> — auto-carrier: no authority was ever named, so it floats to the
    /// FIRST <see cref="RecordingServers"/> entry unconditionally. Nothing to be unfaithful to.</item>
    /// <item><b>Non-blank</b> — pinned: naming a real recorder that isn't part of THIS wall's
    /// <see cref="RecordingServers"/> selection, or that no longer matches (removed, offline, or
    /// ambiguous — two selected recorders sharing that display name), does NOT fall back to another
    /// recorder's Description. The pin holds: the wall keeps showing its last-known-good layout, a
    /// warning is logged (only on change, same discipline as every other F2 selection problem), and
    /// <c>health.json</c> flags <c>LayoutCarrierPinned</c> so an external observer sees the same
    /// condition. Authority resumes automatically the moment this value again resolves to exactly
    /// one selected recorder — no restart needed.</item>
    /// </list>
    ///
    /// Full multi-mode layout-source precedence (highest first):
    /// <list type="number">
    /// <item>A non-blank <see cref="Layout"/> string — unchanged pre-feature behavior, always wins
    /// outright regardless of this field.</item>
    /// <item>Else the layout-carrier recorder's own Description <c>$layout{}</c> tokens. The
    /// carrier's Description is polled in the background every <see cref="ConfigRefreshSeconds"/>
    /// tick (see <c>Milestone.DescriptionPollWorker</c>) exactly like single-recorder mode already
    /// re-reads its one recorder's Description, so a Management-Client edit to the carrier's
    /// Description applies live — no restart, no file edit — typically within one or two
    /// <see cref="ConfigRefreshSeconds"/> intervals (the poll completes in the background; the
    /// following tick is what applies its result).</item>
    /// <item>Neither present -&gt; auto-grid of every selected recorder's enabled cameras, same as
    /// before.</item>
    /// </list>
    ///
    /// Ignored entirely in single-recorder mode (zero behavior change there — that mode already has
    /// exactly one recorder, so there's nothing to select between).</summary>
    public string LayoutRecorder { get; set; } = string.Empty;

    /// <summary>
    /// FIX 3-lite: the layout-carrier description poll (<c>Milestone.MilestoneSession.TryGetRecorderDescriptions</c>)
    /// sends the SDK session's own OAuth bearer token in an <c>Authorization</c> header. Refused over
    /// plain <c>http://</c> <see cref="ManagementServerUri"/> unless this is explicitly <c>true</c> —
    /// mirrors <see cref="AllowInsecureBasic"/>'s identical opt-in gate for <c>AuthMode=Basic</c>
    /// login. Default <c>false</c>: a production install must not leak a bearer token over an
    /// unencrypted channel by accident. Does NOT affect login itself (see <see cref="AllowInsecureBasic"/>
    /// / <c>Auth.CredentialFactory</c> for that) — only this one bearer-token REST poll; Windows-auth
    /// login over http stays allowed regardless, exactly as before.</summary>
    public bool AllowInsecureLayoutPoll { get; set; }
}
