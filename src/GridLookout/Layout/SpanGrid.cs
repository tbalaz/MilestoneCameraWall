namespace GridLookout.Layout;

/// <summary>
/// F4 (cell spans) grid-fill validator/placement engine — the pure, testable piece
/// <see cref="LayoutSpecParser.TryParseUniformSegment"/> hands its tokenized rows to once every
/// entry (real cell or <c>-</c> placeholder) in a uniform-grid page has been read off left to right,
/// top to bottom. Two responsibilities, both here rather than split across the tokenizer:
/// <list type="number">
/// <item>Decide whether the page's rows actually form a legal rectangular grid at all — every
/// row's total column coverage must sum to the SAME value (that becomes <c>gridColumns</c>), every
/// covered position must be claimed by exactly one span origin or its placeholders, and no span may
/// run past the grid's own edges.</item>
/// <item>Strip every <c>-</c> placeholder out (they carry no camera reference — they exist purely
/// to confirm coverage) and stamp each surviving cell's actual <see cref="LayoutCell.Col"/>, so
/// downstream code (<c>LayoutResolver</c>, <c>WallForm</c>) never has to re-derive grid position
/// from a jagged list.</item>
/// </list>
///
/// WHY A CELL'S OWN ORIGIN ROW NEVER NEEDS A PLACEHOLDER FOR ITS OWN <c>ColSpan</c>: the row-width
/// formula below counts a <c>ColSpan</c>-N cell as contributing N to ITS OWN row's total via the ONE
/// written token. An additional placeholder in that same row would double-count those columns and
/// make a literal-width-C row sum to more than C. A <c>-</c> is therefore only ever legal in a row a
/// span reaches by <c>RowSpan</c> (a row OTHER than the one the origin cell was written in); a wide
/// (<c>ColSpan</c> &gt; 1) cell needs <c>ColSpan</c>-many separate <c>-</c> tokens, side by side, in
/// every row its <c>RowSpan</c> continues into — one per covered column, matching "each <c>-</c>
/// counting 1" in the row-width formula below.
///
/// Pure logic, no I/O, no MIP SDK dependency — every failure returns false with a diagnostic naming
/// the offending grid position (row LETTER, 1-based column, matching the operator-facing convention
/// the rest of this grammar already uses), never throws.
/// </summary>
public static class SpanGrid
{
    /// <summary>Hard ceiling on total grid cells (buyer-review defect #8) — <c>rowCount *
    /// commonWidth</c>, checked right after Phase 1 computes both and BEFORE Phase 2 allocates the
    /// <c>claimedBy</c> rectangular array, so a huge grid (many rows, each individually legal —
    /// <see cref="LayoutSpecParser.MaxSpanDimension"/> already bounds a SINGLE span, not the page as
    /// a whole) is rejected as a malformed-token diagnostic (the same "keep the last-known-good
    /// layout" fallback every other grammar error already gets) instead of reaching an allocation
    /// attempt. 4096 cells is far beyond any real wallboard grid (a dense 20-camera wall is nowhere
    /// close) while staying cheap to check before any array exists.</summary>
    public const int MaxGridCells = 4096;

    /// <summary>One written entry in a uniform-grid page's row, before <see cref="Place"/> — either
    /// a real cell (<see cref="Cell"/> set, <see cref="Cell"/>.Col not yet meaningful) or a
    /// placeholder consuming a position a span from elsewhere covers.</summary>
    public sealed record RawEntry(bool IsPlaceholder, LayoutCell? Cell)
    {
        public static RawEntry Placeholder() => new(true, null);

        public static RawEntry ForCell(LayoutCell cell) => new(false, cell);
    }

    /// <summary>
    /// Validates and places one uniform-grid page's rows (already grouped positionally by <c>;</c> —
    /// see <see cref="LayoutSpecParser.TryParseUniformSegment"/>) into a rectangular grid. On
    /// success, <paramref name="rows"/> holds ONLY real cells (placeholders consumed), each with its
    /// <see cref="LayoutCell.Col"/> set to its actual grid column — the row is simply the returned
    /// list's own index, the same convention the legacy letter-grouped path already uses.
    /// <paramref name="gridColumns"/> is the page's common column count.
    /// </summary>
    public static bool Place(
        IReadOnlyList<IReadOnlyList<RawEntry>> rawRows,
        out IReadOnlyList<IReadOnlyList<LayoutCell>>? rows,
        out int gridColumns,
        out string? error)
    {
        rows = null;
        gridColumns = 0;
        error = null;

        int rowCount = rawRows.Count;
        if (rowCount == 0)
        {
            error = "empty page segment";
            return false;
        }

        // Phase 1: every row's total coverage (a cell contributes its ColSpan, a placeholder
        // contributes 1) must sum to the SAME value — that becomes the grid's column count. A
        // uniform-grid page does NOT get the legacy grammar's "rows may have different cell
        // counts" freedom; ragged rows here are a malformed token, same as any other grammar error.
        var rowWidths = new int[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            int width = 0;
            foreach (var entry in rawRows[r])
            {
                width += entry.IsPlaceholder ? 1 : entry.Cell!.ColSpan;
            }

            rowWidths[r] = width;
        }

        int commonWidth = rowWidths[0];
        if (commonWidth == 0)
        {
            error = $"row {RowLetter(0)}: empty row — a uniform-grid page cannot have a row with no cells";
            return false;
        }

        for (int r = 1; r < rowCount; r++)
        {
            if (rowWidths[r] != commonWidth)
            {
                error = $"ragged rows — row {RowLetter(0)} has width {commonWidth} but row {RowLetter(r)} has width {rowWidths[r]}; a page with any span or '-' must be a uniform grid (every row's column coverage must match)";
                return false;
            }
        }

        // Buyer-review defect #8: reject an oversized grid HERE, before the claimedBy allocation
        // below even runs — checked as a long product first so the multiplication itself can never
        // overflow int on the way to deciding whether to reject it.
        if ((long)rowCount * commonWidth > MaxGridCells)
        {
            error = $"grid too large — {rowCount} row(s) x {commonWidth} column(s) = {(long)rowCount * commonWidth} cells exceeds the {MaxGridCells}-cell limit";
            return false;
        }

        // Phase 2: top-to-bottom placement. claimedBy[r,c] names the (row,col) origin that already
        // claims this position via a downward (RowSpan > 1) span, or null when nothing has claimed
        // it yet — processing rows top to bottom means, by the time row r is reached, every claim
        // an EARLIER row's cell could have made on row r already exists.
        //
        // NOTE — no separate "runs past the RIGHT edge" check is needed here: Phase 1 already
        // proved every row's entries sum to EXACTLY commonWidth, and a running cursor built only
        // from non-negative per-entry widths can never exceed a total its own remaining entries
        // (each >= 1) still have to contribute — so `col + colSpan` can never exceed `commonWidth`
        // for any entry once Phase 1 has passed. A RowSpan running past the grid's BOTTOM is the
        // only "edge overrun" that phase 1's width check can't already rule out, since row COUNT is
        // independent of row WIDTH.
        var claimedBy = new (int Row, int Col)?[rowCount, commonWidth];
        var placedRows = new List<List<LayoutCell>>(rowCount);

        for (int r = 0; r < rowCount; r++)
        {
            var placedRow = new List<LayoutCell>();
            int col = 0;

            foreach (var entry in rawRows[r])
            {
                if (entry.IsPlaceholder)
                {
                    if (claimedBy[r, col] is null)
                    {
                        error = $"row {RowLetter(r)} col {col + 1}: '-' not covered by any span";
                        return false;
                    }

                    col += 1;
                    continue;
                }

                var cell = entry.Cell!;
                int rowSpan = cell.RowSpan;
                int colSpan = cell.ColSpan;

                if (r + rowSpan > rowCount)
                {
                    error = $"row {RowLetter(r)} col {col + 1}: span runs past the bottom of the grid ({rowCount} row(s) tall)";
                    return false;
                }

                for (int cc = col; cc < col + colSpan; cc++)
                {
                    if (claimedBy[r, cc] is { } claimant)
                    {
                        error = $"row {RowLetter(r)} col {cc + 1}: overlaps the span from row {RowLetter(claimant.Row)} col {claimant.Col + 1}";
                        return false;
                    }
                }

                // Claim every position this cell's span reaches, including its own origin square —
                // covers both future rows (RowSpan > 1) and makes the claim map complete for the
                // overlap check above regardless of which position within the span is examined.
                for (int rr = r; rr < r + rowSpan; rr++)
                {
                    for (int cc = col; cc < col + colSpan; cc++)
                    {
                        claimedBy[rr, cc] = (r, col);
                    }
                }

                placedRow.Add(cell with { Col = col });
                col += colSpan;
            }

            placedRows.Add(placedRow);
        }

        rows = placedRows.Select(row => (IReadOnlyList<LayoutCell>)row).ToList();
        gridColumns = commonWidth;
        return true;
    }

    /// <summary>Row index to the operator-facing row letter (0 → "A", 1 → "B", …) used in every
    /// diagnostic — matches the legacy grammar's own row-letter convention.</summary>
    private static string RowLetter(int rowIndex) => ((char)('A' + rowIndex)).ToString();
}
