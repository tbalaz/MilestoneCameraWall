namespace GridLookout.Layout;

/// <summary>Shared System.Text.Json options for <c>layout-state.json</c> — mirrors
/// <c>Monitoring.HealthJsonOptions</c> exactly (indented, case-insensitive property matching) so
/// both F3 state files follow the same on-disk conventions. No enum string converter is needed here
/// (unlike health.json) — every enum-shaped field in <see cref="LayoutStateFile"/>
/// (<see cref="PersistedMember.RefKind"/>) is already a plain string by design, specifically so an
/// unrecognized future value degrades to <see cref="LayoutResolver"/>'s "never pinned, retry live"
/// path instead of failing deserialization outright.</summary>
public static class LayoutJsonOptions
{
    public static readonly System.Text.Json.JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// On-disk shape of <c>layout-state.json</c> (F3's last-known-good store) — written/read via the
/// SAME <c>Monitoring.AtomicStateStore</c> mechanism <c>health.json</c> already uses (atomic
/// temp-file + <c>File.Replace</c>, same writable-state-directory resolution), just a second file
/// name in that directory. Plain mutable classes (not records) so <c>System.Text.Json</c> can
/// (de)serialize them directly, matching <c>Monitoring.WallHealthState</c>'s own convention.
/// </summary>
public sealed class LayoutStateFile
{
    /// <summary>Bumped on any breaking change to this shape — an older-schema file is treated as
    /// absent (fresh resolve) rather than crashing the wall over a format it doesn't understand.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Buyer-review defects #4/#5/#7 fix: monitor number (as a string, matching
    /// <see cref="ResolvedPlan"/>'s keys) to THAT monitor's own <see cref="LayoutFingerprint.ComputeForMonitor"/>
    /// hash — replaces the pre-fix single, whole-description <c>DescriptionFingerprint</c>.
    /// <see cref="LayoutResolver"/> reuses a monitor's <see cref="ResolvedPlan"/> entry verbatim only
    /// when a fresh per-monitor fingerprint matches THIS monitor's own stored value — an edit to a
    /// sibling monitor's token, or a change to a DIFFERENT monitor's alias bindings, can never touch
    /// this dictionary's other entries. A monitor present in <see cref="ResolvedPlan"/> but absent
    /// here (an older file written before this fix, or a hand-edited/corrupt one) is schema-tolerant
    /// read: <see cref="LayoutResolver"/> treats a missing entry exactly like a fingerprint mismatch
    /// — that ONE monitor re-resolves fresh once (logged at INFO), after which its entry here is
    /// populated and steady-state trust-verbatim resumes.</summary>
    public Dictionary<string, string> MonitorFingerprints { get; set; } = new();

    /// <summary>Monitor number (as a string — JSON object keys are always strings) to that
    /// monitor's resolved pages. Only monitors that successfully resolved (fresh or carried
    /// forward — see F3 rule 6c) appear here; a monitor with no layout at all is simply absent.</summary>
    public Dictionary<string, List<PersistedPage>> ResolvedPlan { get; set; } = new();

    /// <summary>Monitor numbers (as strings, matching <see cref="ResolvedPlan"/>'s keys) whose entry
    /// in <see cref="ResolvedPlan"/> is a rule-6c carry-forward (that monitor's CURRENT $layout token
    /// is malformed) rather than a genuine fresh pin. Still needed even after <see cref="MonitorFingerprints"/>
    /// went per-monitor (buyer-review defects #4/#5/#7): a monitor's OWN fingerprint only proves its
    /// current token hasn't changed, not that the token is currently VALID — a carried-forward
    /// monitor's fingerprint reflects the malformed token that triggered the carry-forward in the
    /// first place, and would otherwise keep matching itself resolve after resolve (a malformed token
    /// that never changes still hashes the same way every time), letting a monitor stay silently
    /// wedged on stale-but-matching-fingerprint pages forever instead of re-checking whether its
    /// token has since been fixed. <see cref="LayoutResolver"/> uses this list to force that
    /// re-check: a marked monitor is NEVER trusted verbatim by rule 6a, fingerprint match or not, so
    /// its own current token is re-parsed on every resolve regardless. An older file with no such
    /// property deserializes to an empty list — "nothing here is a carry-forward, everything is a
    /// genuine pin" — the correct reading for a file written before this existed.</summary>
    public List<string> CarriedForwardMonitors { get; set; } = new();
}

/// <summary>
/// F4 (cell spans): <see cref="IsUniform"/>/<see cref="GridColumns"/> mirror
/// <c>MatrixPage</c>/<c>ResolvedPage</c>'s fields of the same name. BACKWARD READ COMPATIBILITY —
/// this is why they default to <c>false</c>/<c>0</c> rather than the "spans default to 1" rule that
/// applies to <see cref="PersistedCell.RowSpan"/>/<see cref="PersistedCell.ColSpan"/>: an OLDER
/// <c>layout-state.json</c> written before F4 has no <c>IsUniform</c>/<c>GridColumns</c> properties
/// at all, and <c>System.Text.Json</c> leaves an absent property at its C# default/initializer
/// value rather than touching it — so an old file's page deserializes with <c>IsUniform: false</c>,
/// which is EXACTLY "render this page through the legacy nested-panel path", the correct reading (a
/// page written before spans existed never used them). Defaulting <c>GridColumns</c> to 1 instead of
/// 0 would have been the wrong choice here: <c>LayoutResolver</c>/<c>WallForm</c> only ever consult
/// <see cref="GridColumns"/> when <see cref="IsUniform"/> is true, but a stray "1" would silently
/// mean something (a 1-column uniform grid) if a future bug ever read it without checking
/// <see cref="IsUniform"/> first — 0 fails loudly/obviously instead. See
/// LayoutStatePersistenceTests' old-file backward-read test.
/// </summary>
public sealed class PersistedPage
{
    public List<PersistedRow> Rows { get; set; } = new();

    public bool IsUniform { get; set; }

    public int GridColumns { get; set; }
}

public sealed class PersistedRow
{
    public List<PersistedCell> Cells { get; set; } = new();
}

/// <summary><see cref="Col"/>/<see cref="RowSpan"/>/<see cref="ColSpan"/> (F4 — cell spans) mirror
/// <c>LayoutCell</c>/<c>ResolvedCell</c>'s fields of the same name. Unlike <see cref="PersistedPage"/>'s
/// page-level fields, these DO default to the "spans default to 1" rule (<see cref="RowSpan"/>/
/// <see cref="ColSpan"/> = 1, <see cref="Col"/> = 0) because that is what a plain 1x1 cell — every
/// cell an older file ever wrote — already means; an old file missing these properties deserializes
/// to exactly the same values a legacy cell would have been given if F4 had always existed.</summary>
public sealed class PersistedCell
{
    public List<PersistedMember> Members { get; set; } = new();

    public int Col { get; set; }

    public int RowSpan { get; set; } = 1;

    public int ColSpan { get; set; } = 1;
}

/// <summary>The persisted form of a <see cref="ResolvedMember"/> — deliberately carries ONLY the
/// structural reference (what kind, what label, what it's pinned to), never
/// <see cref="ResolvedMember.Available"/>/<see cref="ResolvedMember.UnavailableReason"/>, which are
/// always recomputed fresh against the LIVE camera catalog on every apply (F3 rule 6e: a camera
/// going missing/disabled must show unavailable immediately, without needing a new pin — and a
/// camera coming back must show live again immediately too).</summary>
public sealed class PersistedMember
{
    /// <summary>String form of <see cref="CellMemberKind"/> — plain string rather than the enum
    /// itself so a future <see cref="CellMemberKind"/> addition can't fail JSON deserialization of
    /// an old file outright (an unrecognized value just fails <c>TryParseMember</c>'s job here,
    /// which <c>LayoutResolver</c> already treats as "never pinned, retry live" — see that class).</summary>
    public string RefKind { get; set; } = string.Empty;

    public string RefLabel { get; set; } = string.Empty;

    /// <summary>Null when this member has never successfully resolved (an unknown alias, a
    /// still-out-of-range ordinal, or a guid with no matching camera at pin time) — such a member is
    /// retried fresh on every resolve, fingerprint notwithstanding, until it succeeds once, at which
    /// point this is set and never touched again (F3 rule 6a/6e — see <see cref="LayoutResolver"/>'s
    /// class doc comment for the "pin on first success" rule this field is the persisted half of).</summary>
    public Guid? CameraId { get; set; }
}
