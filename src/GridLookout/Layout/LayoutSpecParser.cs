using System.Globalization;
using System.Text.RegularExpressions;

namespace GridLookout.Layout;

/// <summary>Which grammar form a <see cref="CellMember"/> was written in — see
/// <see cref="LayoutSpecParser"/>'s class doc comment for the full grammar.</summary>
public enum CellMemberKind
{
    /// <summary>Legacy <c>3</c> form — a 1-based position in the recorder's sorted-by-name enabled
    /// camera list. The referentially UNSTABLE form (renaming/reordering/enabling/disabling a
    /// camera changes what a given ordinal points to) — <c>LayoutResolver</c> resolves it once and
    /// pins the result by camera id; see that class's doc comment.</summary>
    Ordinal,

    /// <summary><c>@front-gate</c> form — a stable alias, resolved against
    /// <c>WallConfig.CameraBindings</c> (<c>[a-z0-9-]+</c>, case-insensitive).</summary>
    Alias,

    /// <summary><c>@{8-4-4-4-12 guid}</c> form — the camera's FQID ObjectId, written literally.
    /// Referentially stable by construction: no lookup table involved at all.</summary>
    Guid,
}

/// <summary>
/// One reference written inside a single cell slot. A fixed cell (<c>A1</c>, <c>A@front-gate</c>,
/// <c>A@{guid}</c>) has exactly one; a rotation cell (<c>A(3,@yard-east,@{guid})</c>) has one per
/// comma-separated entry, in written order — members of a rotation cell may freely mix kinds.
/// Resolving a member to an actual camera (or deciding it's unavailable) is <c>LayoutResolver</c>'s
/// job, at a later, catalog-aware stage — this type only records WHAT was written and whether it's
/// structurally well-formed; it is SDK-free and camera-count-free by design (see the original
/// <see cref="LayoutCell"/> doc comment this one continues from).
/// </summary>
public sealed record CellMember(CellMemberKind Kind, int Ordinal, string Alias, Guid Guid)
{
    public static CellMember ForOrdinal(int ordinal) => new(CellMemberKind.Ordinal, ordinal, string.Empty, System.Guid.Empty);

    public static CellMember ForAlias(string alias) => new(CellMemberKind.Alias, 0, alias, System.Guid.Empty);

    public static CellMember ForGuid(Guid guid) => new(CellMemberKind.Guid, 0, string.Empty, guid);

    /// <summary>Structural (not semantic) validity — an <see cref="CellMemberKind.Ordinal"/> member
    /// must be &gt;= 1, exactly the rule the pre-F3 parser applied to every cell. Alias/Guid members
    /// are always structurally valid here (the alias/guid text itself was already validated at scan
    /// time — see <see cref="LayoutSpecParser"/> — this flag only ever goes false for an ordinal);
    /// whether an alias/guid actually resolves to a live camera is <c>LayoutResolver</c>'s job.</summary>
    public bool IsStructurallyValid => Kind != CellMemberKind.Ordinal || Ordinal >= 1;

    /// <summary>The operator-facing legend text for this member — what a tile caption or an
    /// UNAVAILABLE placeholder shows to name which reference this is. A Guid member shows only its
    /// first 8 hex characters (a full GUID is not a useful wallboard caption).</summary>
    public string RefLabel => Kind switch
    {
        CellMemberKind.Ordinal => Ordinal.ToString(CultureInfo.InvariantCulture),
        CellMemberKind.Alias => Alias,
        CellMemberKind.Guid => Guid.ToString("N").Substring(0, 8),
        _ => "?",
    };
}

/// <summary>
/// One matrix cell: one or more camera references (<see cref="CellMember"/>) plus whether every
/// member was structurally valid. A cell written as a plain single reference (<c>A1</c>,
/// <c>A@front-gate</c>, <c>A@{guid}</c>) carries exactly one member; a cell written in the rotation
/// form (<c>A(3,@yard-east,@{guid})</c>) carries all of them, in written order — WallForm treats
/// <see cref="Members"/>.Count &gt; 1 as "this tile rotates through its list". Whether a reference
/// actually resolves to a live camera is resolved later by <c>LayoutResolver</c>, which is the only
/// place that has the actual camera catalog — this class stays camera-catalog-free and SDK-free.
///
/// <see cref="RowSpan"/>/<see cref="ColSpan"/> (F4 — cell spans) default to 1/1 for EVERY cell
/// parsed via the legacy letter-grouped path (<see cref="LayoutSpecParser"/>'s
/// <c>TryParseSegment</c>) — only a uniform-grid page (<c>TryParseUniformSegment</c> +
/// <see cref="SpanGrid.Place"/>) ever produces a value other than 1. <see cref="Col"/> likewise
/// stays 0 (unused/meaningless) for a legacy cell — that path's row position is the outer
/// <c>MatrixPage.Rows</c> list index and its column position is simply this cell's OWN index within
/// that row's list (unchanged since before F4). A uniform-grid cell's <see cref="Col"/> is
/// meaningful and REQUIRED to find it on screen, because <see cref="SpanGrid.Place"/> strips every
/// <c>-</c> placeholder out of the row list it returns — once that happens, list index no longer
/// equals grid column for a row that has anything placed to its left by a taller cell above it.
/// </summary>
public sealed record LayoutCell(bool IsValid, IReadOnlyList<CellMember> Members, int RowSpan = 1, int ColSpan = 1, int Col = 0)
{
    /// <summary>Legacy convenience accessor (pre-F3): the first member's ordinal. Only meaningful
    /// when every member is <see cref="CellMemberKind.Ordinal"/> — true of every cell written in
    /// the original grammar, and of any still-ordinal-only cell under the extended one; an
    /// alias/guid member reports 0 here. Kept so pre-F3 callers/tests that only ever dealt with
    /// ordinal cells keep working unchanged — new code should read <see cref="Members"/> directly.</summary>
    public int Ordinal => Members[0].Ordinal;

    /// <summary>Legacy convenience accessor (pre-F3) — see <see cref="Ordinal"/>'s doc comment; the
    /// same "Ordinal-kind-only is meaningful" caveat applies per element.</summary>
    public IReadOnlyList<int> Ordinals => Members.Select(m => m.Ordinal).ToList();

    /// <summary>Convenience constructor for the pre-F3 single-ordinal case — equivalent to
    /// <c>new LayoutCell(isValid, new[] { CellMember.ForOrdinal(ordinal) })</c>.</summary>
    public LayoutCell(bool isValid, int ordinal)
        : this(isValid, new[] { CellMember.ForOrdinal(ordinal) })
    {
    }

    /// <summary>Convenience constructor for the pre-F3 rotation-of-ordinals case.</summary>
    public LayoutCell(bool isValid, IReadOnlyList<int> ordinals)
        : this(isValid, ordinals.Select(CellMember.ForOrdinal).ToList())
    {
    }

    /// <summary>Structural value equality over <see cref="Members"/> plus the F4 span/position
    /// fields — the compiler-synthesized record equality would otherwise compare
    /// <see cref="Members"/> by reference (lists don't override <c>Equals</c>), which would make two
    /// cells with the same written members compare unequal whenever they're backed by different
    /// list instances.</summary>
    public bool Equals(LayoutCell? other)
    {
        return other is not null
            && IsValid == other.IsValid
            && RowSpan == other.RowSpan
            && ColSpan == other.ColSpan
            && Col == other.Col
            && Members.SequenceEqual(other.Members);
    }

    public override int GetHashCode()
    {
        // net48 doesn't ship System.HashCode (added in netstandard2.1) — combine manually with the
        // standard FNV-ish "hash * 31 + value" pattern instead of pulling in a NuGet polyfill for
        // one method.
        unchecked
        {
            int hash = IsValid.GetHashCode();
            hash = (hash * 31) + RowSpan.GetHashCode();
            hash = (hash * 31) + ColSpan.GetHashCode();
            hash = (hash * 31) + Col.GetHashCode();
            foreach (var member in Members)
            {
                hash = (hash * 31) + member.GetHashCode();
            }

            return hash;
        }
    }
}

/// <summary>
/// One page of a <c>$layout{...}</c> token's matrix — rows of cells (letter order top-to-bottom,
/// written order left-to-right within a row). A token with N <c>|</c>-separated segments produces
/// N of these, rotated by <c>WallForm.RenderMatrixLayout</c> the same way auto-layout pages
/// rotate.
///
/// F4 (cell spans): <see cref="IsUniform"/> is true only for a page that used a <c>:RxC</c> span
/// suffix or a <c>-</c> placeholder anywhere — see <see cref="LayoutSpecParser"/>'s "GRAMMAR (F4)"
/// section. When true, <see cref="Rows"/> holds ONLY real cells (every <c>-</c> placeholder was
/// consumed by <see cref="SpanGrid.Place"/> already) and <see cref="GridColumns"/> is the page's
/// common column count (every row's own coverage sums to this — <see cref="SpanGrid"/> would have
/// rejected the token otherwise). <see cref="GridColumns"/> is 0/unused when <see cref="IsUniform"/>
/// is false — a legacy page's row widths are read straight off <c>Rows[r].Count</c> exactly as
/// before F4, since a legacy page is free to have a DIFFERENT cell count per row. Defaults
/// (false/0) are also what an OLDER persisted <c>layout-state.json</c> (predating F4) deserializes
/// to on its <c>PersistedPage</c> counterpart — see that type's own doc comment — which is exactly
/// "render this page through the legacy path", the correct backward-compatible reading.
/// </summary>
public sealed record MatrixPage(IReadOnlyList<IReadOnlyList<LayoutCell>> Rows, bool IsUniform = false, int GridColumns = 0);

/// <summary>
/// One <c>$layout{...}</c> / <c>$layoutN{...}</c> token, resolved to a target monitor number and
/// its <see cref="Pages"/> — one <see cref="MatrixPage"/> per <c>|</c>-separated segment inside the
/// token body (a token without <c>|</c> has exactly one page).
/// </summary>
public sealed record ParsedLayout(int Monitor, IReadOnlyList<MatrixPage> Pages)
{
    /// <summary>Convenience accessor for the token's first page's rows. <see cref="Pages"/> always
    /// has at least one entry for any <see cref="ParsedLayout"/> that made it into
    /// <see cref="LayoutSpecParser.Parse"/>'s results, so this is always safe.</summary>
    public IReadOnlyList<IReadOnlyList<LayoutCell>> Rows => Pages[0].Rows;
}

/// <summary>Whether one <c>$layoutN{...}</c> token parsed successfully — see
/// <see cref="TokenParseResult"/>.</summary>
public enum TokenStatus
{
    Valid,
    Invalid,
}

/// <summary>
/// One <c>$layoutN{...}</c> token's outcome from <see cref="LayoutSpecParser.Parse"/> — the F3
/// "never silently drop a token" contract: every token found in the description produces exactly
/// one of these, and every <see cref="TokenStatus.Invalid"/> one has ALREADY been logged via
/// <see cref="LayoutSpecParser.Logger"/> by the time <c>Parse</c> returns it (naming the monitor it
/// targeted, or "unknown monitor" when even that couldn't be parsed, plus the reason) — before F3,
/// a malformed token vanished with zero trace of why. <see cref="Monitor"/> is null only when the
/// digit group itself couldn't be parsed (an absurdly large <c>$layoutNNNN...{</c> literal) — every
/// other failure mode still names a monitor, including an unterminated token (missing <c>}</c>),
/// since the monitor digits are read before the body is even scanned.
/// </summary>
public sealed record TokenParseResult(TokenStatus Status, int? Monitor, string RawToken, ParsedLayout? Layout, string? Diagnostic)
{
    public bool IsValid => Status == TokenStatus.Valid;

    public static TokenParseResult Valid(ParsedLayout layout, string rawToken) =>
        new(TokenStatus.Valid, layout.Monitor, rawToken, layout, null);

    public static TokenParseResult Invalid(int? monitor, string rawToken, string diagnostic) =>
        new(TokenStatus.Invalid, monitor, rawToken, null, diagnostic);
}

/// <summary>
/// Parses <c>$layout{...}</c> / <c>$layoutN{...}</c> matrix tokens out of a recorder description
/// string that may also carry this project's org tags (<c>$city{}</c> / <c>$building{}</c>, see
/// RecorderOrgTaggingService in MilestoneDashboard.Api) — those are ignored here. Pure logic, no
/// MIP SDK dependency.
///
/// GRAMMAR (F3). A cell is one row letter followed by either a single reference or a
/// parenthesized, comma-separated rotation list of references — <c>A1</c>, <c>A@front-gate</c>,
/// <c>A@{8-4-4-4-12 guid}</c>, or <c>A(3,@yard-east,@{guid})</c> (rotation members may freely mix
/// kinds). Legacy ordinal-only tokens parse byte-identically to before this feature.
///
/// TOKENIZATION. Token bodies are found by a hand-written brace-DEPTH scan
/// (<see cref="Parse"/>), not a single "stop at the first <c>}</c>" regex — the guid form's own
/// <c>{...}</c> would otherwise truncate the token early (e.g. <c>$layout{A@{f47a...}, B2}</c> —
/// a naive <c>[^}]*</c> regex matches only up to the guid's closing brace). A depth scan reproduces
/// the OLD single-brace behavior exactly whenever no cell uses the guid form (depth never exceeds
/// 1), so no pre-F3 token's parse result changes.
///
/// GRAMMAR (F4 — cell spans). Any cell form above may carry a <c>:RxC</c> suffix (rows x columns,
/// e.g. <c>A1:2x2</c>, <c>B@front-gate:1x2</c>, <c>A(3,@yard-east):2x1</c> — R,C both &gt;= 1;
/// absent = 1x1). A page segment that uses a span suffix OR the placeholder token <c>-</c>
/// (standing in for a grid position an earlier row/column's span already covers, e.g.
/// <c>$layout{A1:3x1,A2;-,B3;-,C4}</c> — a tall left tile beside a 3-row right stack) becomes a
/// UNIFORM-GRID page: every row's total column coverage must sum to the same value, and every
/// covered position must be accounted for by exactly one span origin or its <c>-</c> markers — see
/// <see cref="SpanGrid"/> for the placement/validation algorithm and
/// <see cref="TryParseUniformSegment"/> for the tokenizer that feeds it.
///
/// The ROW RULE for a REAL CELL is IDENTICAL in both grammars — the letter decides, <c>,</c>/<c>;</c>
/// are fully interchangeable (see <c>Parse_MixedSeparators_BothCommaAndSemicolonWork</c>), so
/// <c>$layout{A1,B2}</c> and <c>$layout{A1;B2}</c> mean the same two rows whether or not the page
/// also happens to use a span. A <c>-</c> placeholder is the one necessary extension: having no
/// letter of its own, it can't trigger a row break by letter-change, so an explicit <c>;</c> right
/// before one is the only way to start a row that begins with a placeholder — see
/// <see cref="TryParseUniformSegment"/>'s doc comment. A real cell whose letter reappears after an
/// earlier row already closed it (e.g. <c>A1,B2,A3</c> plus any span) is a GRAMMAR ERROR here rather
/// than silently re-merged the way the legacy dictionary-based grouping would — see that same doc
/// comment's "loud failure on non-contiguous letter reuse" section; this is what keeps a span/`-`
/// addition from ever silently reshaping a page that already parsed one way under the legacy rule.
/// A page with neither a span suffix nor a <c>-</c> keeps the ORIGINAL
/// letter-GROUPED implementation and parses byte-for-byte identically to every release before F4
/// (<see cref="TryParseSegment"/>, untouched by this feature — it groups by letter VALUE via a
/// dictionary rather than a sequential row-break scan, which only matters for the vanishingly rare
/// case of a non-contiguous letter reuse like <c>A1,B2,A3</c>; every normal, contiguously-lettered
/// token parses identically either way). Which grammar a page uses is decided per PAGE SEGMENT (the
/// same <c>|</c>-delimited unit <see cref="TryParseTokenPages"/> already parses independently) — a
/// multi-page token can freely mix a legacy page and a uniform-grid page.
/// </summary>
public static class LayoutSpecParser
{
    // Only the OPENING "$layoutN{" is a fixed-length regex match now — the matching close brace is
    // found by depth-counting from here (see Parse), not by this regex, so the guid form's nested
    // '{'/'}' can never confuse it.
    private static readonly Regex TokenStartRegex = new(
        @"\$layout(\d*)\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Optional logger — Program.cs sets it so every token diagnostic (duplicate token,
    /// AND — new in F3 — every malformed/unterminated token) reaches the log file; tests leave it
    /// null, or set/reset it around a single test (see LayoutSpecParserTests).</summary>
    public static GridLookout.Logging.FileLogger? Logger { get; set; }

    /// <summary>
    /// Extracts every <c>$layoutN{...}</c> token from <paramref name="description"/> and returns
    /// ONE <see cref="TokenParseResult"/> per token found — see that type's doc comment for the
    /// "never silently drop, always log why" contract this satisfies. <paramref name="defaultMonitor"/>
    /// is the monitor number used for the bare <c>$layout{...}</c> token (no digit) — the spec ties
    /// this to the configured default monitor (<c>Monitors[0].Monitor</c>), not a hardcoded 1.
    /// At most ONE VALID layout per monitor: the caller opens one window per valid entry, so a
    /// second valid token targeting the same monitor is recorded as <see cref="TokenStatus.Invalid"/>
    /// (first wins) rather than opening two overlapping windows on the same screen — same "first
    /// wins" behavior as before F3, now visible as a result entry instead of only a log line.
    /// </summary>
    public static IReadOnlyList<TokenParseResult> Parse(string? description, int defaultMonitor)
    {
        var results = new List<TokenParseResult>();
        // T4/CS8602: net48's BCL reference assembly predates C# 8 nullable annotations, so
        // string.IsNullOrWhiteSpace's parameter isn't marked [NotNullWhen(false)] the way it is on
        // modern .NET — the check below alone doesn't narrow description's nullability here (same
        // gap RecorderLocator.cs's nameOverride handling already documents). The explicit
        // `description is null` clause is the REAL guard the compiler understands.
        if (description is null || string.IsNullOrWhiteSpace(description))
        {
            return results;
        }

        var seenMonitors = new HashSet<int>();
        int searchFrom = 0;
        while (searchFrom <= description.Length)
        {
            var startMatch = TokenStartRegex.Match(description, searchFrom);
            if (!startMatch.Success)
            {
                break;
            }

            int? monitor;
            string digits = startMatch.Groups[1].Value;
            if (digits.Length == 0)
            {
                monitor = defaultMonitor;
            }
            else if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMonitor))
            {
                monitor = parsedMonitor;
            }
            else
            {
                // Digits present but too large for Int32 (e.g. a 20-digit "$layoutNNNN...{" typo) —
                // the ONE case where even the monitor itself can't be named; every other failure
                // path below still knows the monitor before the body is scanned.
                monitor = null;
            }

            // Depth-counting scan for the matching close brace — see the class doc comment for why
            // a plain "next '}'" regex is wrong once the guid form exists.
            int contentStart = startMatch.Index + startMatch.Length;
            int depth = 1;
            int i = contentStart;
            while (i < description.Length && depth > 0)
            {
                if (description[i] == '{')
                {
                    depth++;
                }
                else if (description[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                i++;
            }

            if (depth != 0)
            {
                // Unterminated — no matching '}' anywhere in the rest of the description. Nothing
                // past this point can be reliably re-synchronized (we don't know where this token
                // "should" have ended), so it's both the last token AND the diagnosed one.
                var rawUnterminated = description.Substring(startMatch.Index);
                const string unterminatedDiagnostic = "unterminated token — no matching '}' found";
                results.Add(TokenParseResult.Invalid(monitor, rawUnterminated, unterminatedDiagnostic));
                Logger?.Warning($"$layout token for {MonitorLabel(monitor)} dropped: {unterminatedDiagnostic}. Raw: {Truncate(rawUnterminated)}");
                break;
            }

            string rawToken = description.Substring(startMatch.Index, i - startMatch.Index + 1);
            searchFrom = i + 1;

            if (monitor is null)
            {
                const string overflowDiagnostic = "monitor number is too large to parse";
                results.Add(TokenParseResult.Invalid(null, rawToken, overflowDiagnostic));
                Logger?.Warning($"$layout token for unknown monitor dropped: {overflowDiagnostic}. Raw: {Truncate(rawToken)}");
                continue;
            }

            string body = description.Substring(contentStart, i - contentStart);
            if (!TryParseTokenPages(body, out var pages, out var pagesError))
            {
                results.Add(TokenParseResult.Invalid(monitor, rawToken, pagesError!));
                Logger?.Warning($"$layout token for monitor {monitor} dropped: {pagesError}. Raw: {Truncate(rawToken)}");
                continue;
            }

            if (!seenMonitors.Add(monitor.Value))
            {
                var duplicateDiagnostic = $"duplicate $layout token for monitor {monitor} ignored — one layout per monitor, first token wins";
                results.Add(TokenParseResult.Invalid(monitor, rawToken, duplicateDiagnostic));
                Logger?.Warning(duplicateDiagnostic);
                continue;
            }

            results.Add(TokenParseResult.Valid(new ParsedLayout(monitor.Value, pages!), rawToken));
        }

        return results;
    }

    /// <summary>Convenience view over <see cref="Parse"/>'s per-token results for callers that only
    /// want the successfully parsed layouts — every test written before F3's diagnostics existed
    /// uses this (see LayoutSpecParserTests), and it's a fine choice for any future caller that
    /// genuinely doesn't care why a token failed. <c>LayoutResolver</c> (the only PRODUCTION caller
    /// — see Program.cs) deliberately does NOT use this: it needs the Invalid entries too, to decide
    /// whether to keep a last-known-good plan for that monitor (F3 rule 6c).</summary>
    public static IReadOnlyList<ParsedLayout> ParseValid(string? description, int defaultMonitor) =>
        Parse(description, defaultMonitor)
            .Where(r => r.IsValid)
            .Select(r => r.Layout!)
            .ToList();

    private static string MonitorLabel(int? monitor) => monitor?.ToString(CultureInfo.InvariantCulture) ?? "unknown monitor";

    /// <summary>Caps a raw token's length in a log line — a hand-typed typo is normally short, but
    /// nothing stops a description from being enormous, and the log must never balloon over one bad
    /// token.</summary>
    private static string Truncate(string s) => s.Length <= 80 ? s : s.Substring(0, 80) + "…";

    /// <summary>
    /// Splits a token's body on <c>|</c> into page segments, then applies the rows/cells grammar
    /// (<see cref="TryParseSegment"/>) to each segment independently — a body with no <c>|</c> is
    /// exactly one segment. Empty segments (a leading, trailing, or doubled <c>|</c>) are skipped
    /// rather than treated as errors; if every segment is empty the whole token is unparseable (same
    /// as an empty <c>$layout{}</c> token). Any ONE segment containing a structurally-unrecognizable
    /// entry makes the WHOLE token unparseable, not just that page — "garbage anywhere drops the
    /// whole token" applies per-token, not per-page.
    /// </summary>
    private static bool TryParseTokenPages(string content, out IReadOnlyList<MatrixPage>? pages, out string? error)
    {
        pages = null;
        error = null;

        var builtPages = new List<MatrixPage>();
        foreach (var rawSegment in content.Split('|'))
        {
            if (string.IsNullOrWhiteSpace(rawSegment))
            {
                continue;
            }

            // F4: decide legacy vs. uniform-grid PER SEGMENT, on the already-whitespace-stripped
            // text (matches what each branch's own parser scans) — see IsUniformGridSegment's doc
            // comment for why ':' / a bounded '-' can only ever mean "this page uses spans", never
            // collide with the pre-F4 alphabet.
            var strippedForDetection = Regex.Replace(rawSegment, @"\s+", string.Empty);
            if (IsUniformGridSegment(strippedForDetection))
            {
                if (!TryParseUniformSegment(strippedForDetection, out var uniformRows, out var gridColumns, out var uniformError))
                {
                    error = $"page segment '{rawSegment.Trim()}': {uniformError}";
                    return false;
                }

                builtPages.Add(new MatrixPage(uniformRows!, IsUniform: true, GridColumns: gridColumns));
                continue;
            }

            if (!TryParseSegment(rawSegment, out var rows, out var segmentError))
            {
                error = $"page segment '{rawSegment.Trim()}': {segmentError}";
                return false;
            }

            builtPages.Add(new MatrixPage(rows!));
        }

        if (builtPages.Count == 0)
        {
            error = "token body is empty";
            return false;
        }

        pages = builtPages;
        return true;
    }

    /// <summary>
    /// Parses one page segment into row-grouped cells via a hand-written left-to-right scan (not a
    /// regex global match — a rotation cell's members can now be alias/guid text, not just digits,
    /// so a single "find all cells" regex would need to duplicate the same alternation
    /// <see cref="TryParseMember"/> already expresses more clearly as code). Structural validity is
    /// all-or-nothing per segment: every character in the (whitespace-stripped) segment must belong
    /// either to a matched cell or to a run of <c>,</c>/<c>;</c> separator characters
    /// between/around cells (stray/doubled/leading/trailing separators are tolerated); anything else
    /// — including two cells with no separator between them, an empty <c>()</c>, a trailing comma
    /// inside parens, or an unrecognized member — is unrecognizable garbage and invalidates the
    /// WHOLE segment (and therefore the whole token, per <see cref="TryParseTokenPages"/>).
    /// </summary>
    private static bool TryParseSegment(string rawSegment, out IReadOnlyList<IReadOnlyList<LayoutCell>>? rows, out string? error)
    {
        rows = null;
        error = null;

        var stripped = Regex.Replace(rawSegment, @"\s+", string.Empty);
        if (stripped.Length == 0)
        {
            error = "empty page segment";
            return false;
        }

        // SortedDictionary<char,...> gives row-letter order for free; List preserves written
        // (insertion) order within a row, and duplicates are allowed (same reference twice is fine,
        // including within one rotation cell's own list).
        var rowMap = new SortedDictionary<char, List<LayoutCell>>();

        int pos = 0;
        while (pos < stripped.Length)
        {
            int sepStart = pos;
            while (pos < stripped.Length && (stripped[pos] == ',' || stripped[pos] == ';'))
            {
                pos++;
            }

            if (pos >= stripped.Length)
            {
                break; // trailing separator run only — tolerated, not an error.
            }

            bool consumedSeparator = pos > sepStart;
            if (!consumedSeparator && sepStart != 0)
            {
                // Two matched cells with nothing between them (e.g. "A1B2") — the grammar requires
                // an explicit separator between entries; only the run before the FIRST cell may be
                // empty (sepStart == 0, i.e. this is the very first entry in the segment).
                error = $"unrecognized entry near '{Snippet(stripped, pos)}' — cells must be separated by ',' or ';'";
                return false;
            }

            char letter = stripped[pos];
            if (!((letter >= 'A' && letter <= 'Z') || (letter >= 'a' && letter <= 'z')))
            {
                error = $"unrecognized entry near '{Snippet(stripped, pos)}' — expected a row letter";
                return false;
            }

            char rowLetter = char.ToUpperInvariant(letter);
            pos++;

            List<CellMember> members;
            if (pos < stripped.Length && stripped[pos] == '(')
            {
                pos++; // consume '('
                members = new List<CellMember>();
                while (true)
                {
                    if (!TryParseMember(stripped, ref pos, out var member, out var memberError))
                    {
                        error = $"malformed rotation entry near '{Snippet(stripped, pos)}'" + (memberError is null ? string.Empty : $" ({memberError})");
                        return false;
                    }

                    members.Add(member);

                    if (pos < stripped.Length && stripped[pos] == ',')
                    {
                        pos++;
                        continue;
                    }

                    break;
                }

                if (pos >= stripped.Length || stripped[pos] != ')')
                {
                    error = $"unterminated rotation cell '{rowLetter}(...)' — missing ')'";
                    return false;
                }

                pos++; // consume ')'
            }
            else
            {
                if (!TryParseMember(stripped, ref pos, out var single, out var memberError))
                {
                    error = $"unrecognized entry near '{Snippet(stripped, pos)}'" + (memberError is null ? string.Empty : $" ({memberError})");
                    return false;
                }

                members = new List<CellMember> { single };
            }

            bool cellValid = members.All(m => m.IsStructurallyValid);
            if (!rowMap.TryGetValue(rowLetter, out var row))
            {
                row = new List<LayoutCell>();
                rowMap[rowLetter] = row;
            }

            row.Add(new LayoutCell(cellValid, members));
        }

        rows = rowMap.Values
            .Select(row => (IReadOnlyList<LayoutCell>)row)
            .ToList();
        return true;
    }

    /// <summary>
    /// True when <paramref name="stripped"/> (whitespace already removed) uses ANY of the F4
    /// span-suffix (<c>:RxC</c>) or placeholder (<c>-</c>) grammar — the trigger for the
    /// uniform-grid page path (<see cref="TryParseUniformSegment"/>) instead of the legacy
    /// letter-grouped one (<see cref="TryParseSegment"/>). Neither trigger character can appear
    /// anywhere in the pre-F4 alphabet by construction: <c>:</c> is unused entirely (ordinal digits,
    /// <c>@alias</c> — <c>[a-z0-9-]+</c> — <c>@{guid}</c> — hyphens only, no colon — row letters,
    /// <c>(</c>/<c>)</c>/<c>,</c>/<c>;</c>), and a hyphen immediately bounded by separators or a
    /// segment edge can only be the bare placeholder — a legal ALIAS hyphen is always preceded by
    /// alias characters (ultimately an <c>@</c>), never by a separator or the start of an entry, so
    /// this can never false-positive on a hyphenated alias like <c>@front-gate</c>.
    /// </summary>
    private static bool IsUniformGridSegment(string stripped)
    {
        if (stripped.IndexOf(':') >= 0)
        {
            return true;
        }

        for (int i = 0; i < stripped.Length; i++)
        {
            if (stripped[i] != '-')
            {
                continue;
            }

            bool boundaryBefore = i == 0 || stripped[i - 1] == ',' || stripped[i - 1] == ';';
            bool boundaryAfter = i == stripped.Length - 1 || stripped[i + 1] == ',' || stripped[i + 1] == ';';
            if (boundaryBefore && boundaryAfter)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses one UNIFORM-grid page segment (triggered by <see cref="IsUniformGridSegment"/>) — the
    /// F4 span-suffix/placeholder grammar. ROW RULE for a REAL CELL WHEN THE OPEN ROW ALREADY HAS A
    /// CLAIMED LETTER: EXACTLY the legacy grammar's own — the row LETTER decides membership,
    /// <c>,</c> and <c>;</c> are fully interchangeable (never a row break by themselves; see
    /// <c>Parse_MixedSeparators_BothCommaAndSemicolonWork</c> and this file's own parity tests) — a
    /// real cell CONTINUES the currently open row when its letter matches, and starts a new one only
    /// when the letter differs.
    ///
    /// TWO CASES WHERE THERE IS NO LETTER TO COMPARE AGAINST, AND <c>;</c> BECOMES THE ONLY SIGNAL:
    /// <list type="bullet">
    /// <item>A <c>-</c> placeholder ENTRY carries no letter of its own, so nothing about it can ever
    /// trigger a letter-change — it always "continues the current row, whatever came before," UNLESS
    /// an explicit <c>;</c> precedes it (the only way to start a row that BEGINS with a
    /// placeholder).</item>
    /// <item>A REAL CELL following a row that is open but has NO claimed letter yet — i.e. every
    /// entry in it so far has been a placeholder — has nothing to compare its own letter against
    /// either. An explicit <c>;</c> here is therefore the ONLY way to start a fresh row instead of
    /// silently claiming (merging into) the still-open placeholder-only row. THIS WAS A LIVE BUG:
    /// the original F4 fix only checked "does my letter differ from the open row's letter", which is
    /// vacuously false when the open row's letter is unset — so a real cell after 2+ consecutive
    /// placeholder-only rows silently merged into the last one regardless of a preceding <c>;</c>,
    /// corrupting any tall (RowSpan &gt;= 3) span's page into fewer, wider rows. See
    /// <c>Parse_TallSpanWithMultipleConsecutiveDashRows_RegressionFixture</c> and
    /// <c>Parse_PlaceholderOnlyRowFollowedByRealCellViaSemicolon_StartsAFreshRow</c>.</item>
    /// </list>
    /// See the left-tall-tile-plus-stack admin-guide example, where rows B/C each open with a
    /// placeholder and their FOLLOWING real cell (comma-joined, no further <c>;</c>) correctly
    /// CLAIMS that same still-letterless row rather than starting yet another new one.
    ///
    /// LOUD FAILURE ON NON-CONTIGUOUS LETTER REUSE. This is a SEQUENTIAL scan (the row a real cell
    /// belongs to is decided the moment it's read), NOT the legacy path's group-by-letter-VALUE
    /// dictionary (which would re-merge a letter that reappears later, e.g. <c>A1,B2,A3</c> → row
    /// A=[A1,A3], row B=[B2], REGARDLESS of position). Silently reinterpreting that same text as
    /// THREE rows once a span/placeholder is added (A, B, A-again) would be exactly the silent
    /// wall-reshape bug F3 exists to prevent — so instead, a real cell whose letter was already used
    /// by a row that has since been CLOSED (a different letter came after it) is a GRAMMAR ERROR
    /// here: "row letter 'X' reappears after an earlier row already closed it." This never fires for
    /// the ascending-contiguous-letters style every documented example uses.
    ///
    /// This method only TOKENIZES — grid-fill validation (row-width equality, coverage, overlap,
    /// edge-overrun) is entirely <see cref="SpanGrid.Place"/>'s job, called once at the end here with
    /// every row-group collected.
    /// </summary>
    private static bool TryParseUniformSegment(string stripped, out IReadOnlyList<IReadOnlyList<LayoutCell>>? rows, out int gridColumns, out string? error)
    {
        rows = null;
        gridColumns = 0;
        error = null;

        var rawRows = new List<List<SpanGrid.RawEntry>>();
        List<SpanGrid.RawEntry>? currentRow = null;
        char? currentRowLetter = null;
        // Letters belonging to a row that has been left (a DIFFERENT letter, or an explicit ';'
        // before a placeholder, started a new row after it) — see this method's doc comment's "loud
        // failure on non-contiguous letter reuse" section. A row's own letter is added here the
        // moment something else causes it to close, never while it's still the open row.
        var closedRowLetters = new HashSet<char>();

        int pos = 0;
        while (pos < stripped.Length)
        {
            // Consume a run of separators; ',' and ';' are interchangeable for a REAL CELL (see this
            // method's doc comment), but whether ';' specifically appeared anywhere in this run is
            // still tracked — it's the ONLY row-break signal a placeholder (which has no letter) can
            // ever respond to.
            bool sawSemicolon = false;
            while (pos < stripped.Length && (stripped[pos] == ',' || stripped[pos] == ';'))
            {
                sawSemicolon |= stripped[pos] == ';';
                pos++;
            }

            if (pos >= stripped.Length)
            {
                break; // trailing separator run only — tolerated, not an error.
            }

            if (stripped[pos] == '-')
            {
                // IsUniformGridSegment already established that a bare '-' reaching here is
                // unambiguous — it can never be part of an alias (those always start with '@').
                if (currentRow is null || sawSemicolon)
                {
                    if (currentRow is not null && currentRowLetter is { } lettersBeingLeft)
                    {
                        closedRowLetters.Add(lettersBeingLeft);
                    }

                    currentRow = new List<SpanGrid.RawEntry>();
                    rawRows.Add(currentRow);
                    currentRowLetter = null;
                }

                currentRow!.Add(SpanGrid.RawEntry.Placeholder());
                pos++;
                continue;
            }

            char letter = stripped[pos];
            if (!((letter >= 'A' && letter <= 'Z') || (letter >= 'a' && letter <= 'z')))
            {
                error = $"unrecognized entry near '{Snippet(stripped, pos)}' — expected a row letter or '-'";
                return false;
            }

            char rowLetter = char.ToUpperInvariant(letter);
            pos++; // consume the row letter — this DOES decide row placement here (see this
                   // method's doc comment), unlike the earlier F4 draft.

            List<CellMember> members;
            if (pos < stripped.Length && stripped[pos] == '(')
            {
                pos++; // consume '('
                members = new List<CellMember>();
                while (true)
                {
                    if (!TryParseMember(stripped, ref pos, out var member, out var memberError))
                    {
                        error = $"malformed rotation entry near '{Snippet(stripped, pos)}'" + (memberError is null ? string.Empty : $" ({memberError})");
                        return false;
                    }

                    members.Add(member);

                    if (pos < stripped.Length && stripped[pos] == ',')
                    {
                        pos++;
                        continue;
                    }

                    break;
                }

                if (pos >= stripped.Length || stripped[pos] != ')')
                {
                    error = $"unterminated rotation cell '{letter}(...)' — missing ')'";
                    return false;
                }

                pos++; // consume ')'
            }
            else
            {
                if (!TryParseMember(stripped, ref pos, out var single, out var memberError))
                {
                    error = $"unrecognized entry near '{Snippet(stripped, pos)}'" + (memberError is null ? string.Empty : $" ({memberError})");
                    return false;
                }

                members = new List<CellMember> { single };
            }

            int rowSpan = 1;
            int colSpan = 1;
            if (pos < stripped.Length && stripped[pos] == ':')
            {
                pos++; // consume ':'
                if (!TryParseSpanSuffix(stripped, ref pos, out rowSpan, out colSpan, out var spanError))
                {
                    error = spanError;
                    return false;
                }
            }

            // Loud failure on non-contiguous letter reuse (see this method's doc comment) — checked
            // BEFORE the row-transition decision below, against closedRowLetters as it stands right
            // now: a letter that belongs to the CURRENTLY OPEN row (still un-closed) is fine (that's
            // just "continue this row" or "claim this still-letterless row"); one that belongs to a
            // row already left behind is the silent-reshape trap this rejects instead.
            if (closedRowLetters.Contains(rowLetter) && currentRowLetter != rowLetter)
            {
                error = $"row letter '{rowLetter}' reappears after an earlier row already used and closed it — use a different row letter for each row";
                return false;
            }

            // The row rule for a REAL CELL: when the currently open row already has a claimed
            // letter, ONLY a letter-change starts a new row — ';' is fully cosmetic there, exactly
            // like the legacy grammar (a same-letter re-encounter, ',' or ';' between, continues the
            // open row). But when the open row has NO claimed letter yet (it was opened by one or
            // more placeholders only, and no real cell has claimed it — see the placeholder branch
            // above), there is no letter to compare against, so letter-change can never fire; an
            // explicit ';' is then the ONLY signal available, and MUST be honored, or a placeholder
            // -only row could never be closed by a following real cell (this was the actual bug a
            // 13x8 grid with 3+ consecutive full-width dash rows exposed — see
            // Parse_TallSpanWithMultipleConsecutiveDashRows_RegressionFixture). Without an explicit
            // ';', a real cell simply CLAIMS the still-open, still-letterless row instead (this is
            // what makes "-,B3" in the admin-guide's left-tall example correctly land B3 in the SAME
            // row as its preceding placeholder).
            bool startsNewRow = currentRow is null
                || (currentRowLetter is { } existingLetter ? existingLetter != rowLetter : sawSemicolon);
            if (startsNewRow)
            {
                if (currentRow is not null && currentRowLetter is { } lettersBeingLeft)
                {
                    closedRowLetters.Add(lettersBeingLeft);
                }

                currentRow = new List<SpanGrid.RawEntry>();
                rawRows.Add(currentRow);
            }

            currentRowLetter = rowLetter;

            bool cellValid = members.All(m => m.IsStructurallyValid);
            currentRow!.Add(SpanGrid.RawEntry.ForCell(new LayoutCell(cellValid, members, RowSpan: rowSpan, ColSpan: colSpan)));
        }

        if (rawRows.Count == 0)
        {
            error = "empty page segment";
            return false;
        }

        return SpanGrid.Place(rawRows, out rows, out gridColumns, out error);
    }

    /// <summary>Hard ceiling on a single <c>:RxC</c> suffix's R or C (buyer-review defect #8): with
    /// no cap, a typo'd or hostile span suffix (a stray extra digit is all it takes) parses as a
    /// perfectly valid positive int up to <see cref="int.MaxValue"/>, then flows unchecked into
    /// <see cref="SpanGrid.Place"/>'s rectangular-array allocation — an attacker/typo-induced
    /// allocation attempt instead of the "keep the last-known-good layout" fallback every other
    /// malformed token already gets. 64 is generous for any real wall (a 64-tall or 64-wide single
    /// span dwarfs any physical monitor grid) while keeping the worst case
    /// (<see cref="MaxSpanDimension"/> squared, see <see cref="SpanGrid.MaxGridCells"/>) cheap to
    /// allocate even before the grid-total check.</summary>
    internal const int MaxSpanDimension = 64;

    /// <summary>Parses the <c>RxC</c> body of a <c>:RxC</c> span suffix starting at
    /// <paramref name="pos"/> (just past the <c>:</c>) — R,C both required to be integers in
    /// <c>[1, <see cref="MaxSpanDimension"/>]</c>; <c>x</c>/<c>X</c> is accepted as the separator
    /// (matches the grammar's own case-insensitivity everywhere else). A zero, non-numeric, or
    /// over-the-cap R/C is a GRAMMAR error (drops the whole token via the same malformed-token WARN
    /// path as any other unrecognized entry), not a per-cell semantic one — there is no sensible
    /// "structurally valid but 0x0" (or "structurally valid but 999999x1") cell the way an
    /// out-of-range ordinal can still parse.</summary>
    private static bool TryParseSpanSuffix(string s, ref int pos, out int rowSpan, out int colSpan, out string? error)
    {
        rowSpan = 1;
        colSpan = 1;
        error = null;

        int rowsStart = pos;
        while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9')
        {
            pos++;
        }

        if (pos == rowsStart || !int.TryParse(s.Substring(rowsStart, pos - rowsStart), NumberStyles.None, CultureInfo.InvariantCulture, out var rows) || rows < 1)
        {
            error = $"malformed span suffix near '{Snippet(s, rowsStart)}' — expected 'RxC' with R >= 1";
            return false;
        }

        if (rows > MaxSpanDimension)
        {
            error = $"malformed span suffix near '{Snippet(s, rowsStart)}' — R={rows} exceeds the {MaxSpanDimension}-row cap on a single span";
            return false;
        }

        if (pos >= s.Length || (s[pos] != 'x' && s[pos] != 'X'))
        {
            error = $"malformed span suffix near '{Snippet(s, rowsStart)}' — expected 'RxC' (missing 'x')";
            return false;
        }

        pos++; // consume 'x'/'X'

        int colsStart = pos;
        while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9')
        {
            pos++;
        }

        if (pos == colsStart || !int.TryParse(s.Substring(colsStart, pos - colsStart), NumberStyles.None, CultureInfo.InvariantCulture, out var cols) || cols < 1)
        {
            error = $"malformed span suffix near '{Snippet(s, rowsStart)}' — expected 'RxC' with C >= 1";
            return false;
        }

        if (cols > MaxSpanDimension)
        {
            error = $"malformed span suffix near '{Snippet(s, rowsStart)}' — C={cols} exceeds the {MaxSpanDimension}-column cap on a single span";
            return false;
        }

        rowSpan = rows;
        colSpan = cols;
        return true;
    }

    /// <summary>True while <paramref name="c"/> is a legal alias character per F3's naming rule
    /// (<c>[a-z0-9-]+</c>, case-insensitive) — see <see cref="CellMemberKind.Alias"/>.</summary>
    private static bool IsAliasChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';

    /// <summary>Parses ONE reference starting at <paramref name="pos"/> — a bare digit run
    /// (<see cref="CellMemberKind.Ordinal"/>), <c>@alias</c> (<see cref="CellMemberKind.Alias"/>),
    /// or <c>@{guid}</c> (<see cref="CellMemberKind.Guid"/>) — advancing <paramref name="pos"/> past
    /// whatever it consumed. Returns false (with <paramref name="error"/> set) on anything else,
    /// including a syntactically-not-a-GUID <c>@{...}</c> body — an unparseable guid literal is a
    /// GRAMMAR error (drops the whole token), unlike an out-of-range ordinal, which is only a
    /// SEMANTIC one (see <see cref="CellMember.IsStructurallyValid"/> — a "0" or negative-looking
    /// ordinal digit run still parses fine here; only the value check downstream flags it).</summary>
    private static bool TryParseMember(string s, ref int pos, out CellMember member, out string? error)
    {
        member = CellMember.ForOrdinal(0);
        error = null;

        if (pos >= s.Length)
        {
            error = "expected a camera reference";
            return false;
        }

        char c = s[pos];
        if (c >= '0' && c <= '9')
        {
            int start = pos;
            while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9')
            {
                pos++;
            }

            if (!int.TryParse(s.Substring(start, pos - start), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            {
                error = "ordinal number is too large";
                return false;
            }

            member = CellMember.ForOrdinal(ordinal);
            return true;
        }

        if (c == '@')
        {
            pos++;
            if (pos < s.Length && s[pos] == '{')
            {
                pos++;
                int closeIdx = s.IndexOf('}', pos);
                if (closeIdx < 0)
                {
                    error = "unterminated '@{...}' guid reference — missing '}'";
                    return false;
                }

                string guidText = s.Substring(pos, closeIdx - pos);
                if (!Guid.TryParseExact(guidText, "D", out var guid))
                {
                    error = $"'{guidText}' is not a valid GUID (expected 8-4-4-4-12 hyphenated form)";
                    return false;
                }

                pos = closeIdx + 1;
                member = CellMember.ForGuid(guid);
                return true;
            }

            int aliasStart = pos;
            while (pos < s.Length && IsAliasChar(s[pos]))
            {
                pos++;
            }

            if (pos == aliasStart)
            {
                error = "'@' must be followed by an alias (letters/digits/hyphen) or '{guid}'";
                return false;
            }

            member = CellMember.ForAlias(s.Substring(aliasStart, pos - aliasStart));
            return true;
        }

        error = $"unrecognized character '{c}'";
        return false;
    }

    /// <summary>Up to 12 characters of <paramref name="s"/> starting at <paramref name="pos"/>, for
    /// an error message — never the whole (potentially huge) remaining string.</summary>
    private static string Snippet(string s, int pos)
    {
        if (pos >= s.Length)
        {
            return "<end of token>";
        }

        int len = Math.Min(12, s.Length - pos);
        return s.Substring(pos, len);
    }
}
