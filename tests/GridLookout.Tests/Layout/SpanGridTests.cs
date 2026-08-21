using GridLookout.Layout;
using Xunit;

namespace GridLookout.Tests.Layout;

/// <summary>
/// Direct unit tests for <see cref="SpanGrid.Place"/> — the pure grid-fill validator/placement
/// engine behind F4's <c>:RxC</c> span suffix and <c>-</c> placeholder grammar. Constructs
/// <see cref="SpanGrid.RawEntry"/> lists directly rather than going through
/// <see cref="LayoutSpecParser"/>'s text tokenizer, so every geometry case (overlap, uncovered '-',
/// ragged rows, edge overrun) can be pinned exactly without fighting the grammar for a natural
/// example. See <see cref="LayoutSpecParserTests"/> for the end-to-end "this really is what
/// <c>$layout{}</c> text parses to" coverage.
/// </summary>
public class SpanGridTests
{
    private static LayoutCell Cell(int ordinal, int rowSpan = 1, int colSpan = 1) =>
        new LayoutCell(true, ordinal) with { RowSpan = rowSpan, ColSpan = colSpan };

    private static SpanGrid.RawEntry C(int ordinal, int rowSpan = 1, int colSpan = 1) =>
        SpanGrid.RawEntry.ForCell(Cell(ordinal, rowSpan, colSpan));

    private static SpanGrid.RawEntry Ph() => SpanGrid.RawEntry.Placeholder();

    // --- Valid placements ---

    [Fact]
    public void Place_LeftTallPlusRightStack_MatchesTheAdminGuideExample()
    {
        // $layout{A1:3x1,A2;-,B3;-,C4} — a tall left tile beside a 3-row right stack.
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>
        {
            new[] { C(1, rowSpan: 3, colSpan: 1), C(2) },
            new[] { Ph(), C(3) },
            new[] { Ph(), C(4) },
        };

        Assert.True(SpanGrid.Place(rawRows, out var rows, out var gridColumns, out var error));
        Assert.Null(error);
        Assert.Equal(2, gridColumns);
        Assert.Equal(3, rows!.Count);

        Assert.Equal(2, rows[0].Count);
        Assert.Equal(0, rows[0][0].Col);
        Assert.Equal(3, rows[0][0].RowSpan);
        Assert.Equal(1, rows[0][0].ColSpan);
        Assert.Equal(1, rows[0][1].Col);

        var row1 = Assert.Single(rows[1]);
        Assert.Equal(1, row1.Col); // B3 lands at column 1 — column 0 was consumed by A1's placeholder.
        Assert.Equal(3, row1.Ordinal);

        var row2 = Assert.Single(rows[2]);
        Assert.Equal(1, row2.Col);
        Assert.Equal(4, row2.Ordinal);
    }

    [Fact]
    public void Place_Hero1Big5Small_3x3Grid()
    {
        // $layout{A1:2x2,A4;-,-,B5;C6,C7,C8} — one 2x2 hero, top-right single, bottom row of three.
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>
        {
            new[] { C(1, rowSpan: 2, colSpan: 2), C(4) },
            new[] { Ph(), Ph(), C(5) },
            new[] { C(6), C(7), C(8) },
        };

        Assert.True(SpanGrid.Place(rawRows, out var rows, out var gridColumns, out var error));
        Assert.Null(error);
        Assert.Equal(3, gridColumns);

        Assert.Equal(2, rows![0].Count);
        Assert.Equal(0, rows[0][0].Col);
        Assert.Equal(2, rows[0][0].RowSpan);
        Assert.Equal(2, rows[0][0].ColSpan);
        Assert.Equal(2, rows[0][1].Col); // A4 at column 2, right of the 2-wide hero.

        var row1 = Assert.Single(rows[1]);
        Assert.Equal(2, row1.Col); // B5 — columns 0-1 consumed by the hero's downward span.

        Assert.Equal(3, rows[2].Count);
        Assert.Equal(0, rows[2][0].Col);
        Assert.Equal(1, rows[2][1].Col);
        Assert.Equal(2, rows[2][2].Col);
    }

    [Fact]
    public void Place_TwoOnePlusTwo_MiddleRowIsAFullWidthBanner()
    {
        // $layout{A1,A2;B3:1x2;C4,C5} — a 2-wide banner row between two 2-column rows.
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>
        {
            new[] { C(1), C(2) },
            new[] { C(3, rowSpan: 1, colSpan: 2) },
            new[] { C(4), C(5) },
        };

        Assert.True(SpanGrid.Place(rawRows, out var rows, out var gridColumns, out var error));
        Assert.Null(error);
        Assert.Equal(2, gridColumns);

        var banner = Assert.Single(rows![1]);
        Assert.Equal(0, banner.Col);
        Assert.Equal(2, banner.ColSpan);
        Assert.Equal(1, banner.RowSpan);
    }

    [Fact]
    public void Place_AllPlaceholderRow_ProducesAnEmptyPlacedRow_NotAnError()
    {
        // A 2-wide, 3-tall hero alone fills a 3x2 grid — rows 1 and 2 are ENTIRELY placeholders
        // (both columns of the hero's downward continuation), so they produce zero real cells.
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>
        {
            new[] { C(1, rowSpan: 3, colSpan: 2) },
            new[] { Ph(), Ph() },
            new[] { Ph(), Ph() },
        };

        Assert.True(SpanGrid.Place(rawRows, out var rows, out var gridColumns, out var error));
        Assert.Null(error);
        Assert.Equal(2, gridColumns);
        Assert.Equal(3, rows!.Count);
        Assert.Single(rows[0]);
        Assert.Empty(rows[1]);
        Assert.Empty(rows[2]);
    }

    // --- Invalid: overlap ---

    [Fact]
    public void Place_RealCellWritesOverAnAlreadyClaimedPosition_IsOverlap()
    {
        // Row A: a 2-tall tile at col 0 (rowSpan 2) plus a plain cell at col 1.
        // Row B: TWO fresh cells instead of a placeholder at col 0 — collides with A1's downward span.
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>
        {
            new[] { C(1, rowSpan: 2, colSpan: 1), C(2) },
            new[] { C(3), C(4) },
        };

        Assert.False(SpanGrid.Place(rawRows, out var rows, out _, out var error));
        Assert.Null(rows);
        Assert.Contains("col 1", error);
        Assert.Contains("overlaps", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("row A col 1", error);
    }

    // --- Invalid: uncovered placeholder ---

    [Fact]
    public void Place_PlaceholderWithNothingAbove_IsNotCoveredByAnySpan()
    {
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>
        {
            new[] { C(1), C(2) },  // neither cell spans downward
            new[] { Ph(), C(3) },  // row B col 1 has nothing claiming it
        };

        Assert.False(SpanGrid.Place(rawRows, out var rows, out _, out var error));
        Assert.Null(rows);
        Assert.Contains("row B col 1", error);
        Assert.Contains("not covered by any span", error, StringComparison.OrdinalIgnoreCase);
    }

    // --- Invalid: ragged rows ---

    [Fact]
    public void Place_RowsWithDifferentTotalWidth_IsRaggedRows()
    {
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>
        {
            new[] { C(1, rowSpan: 1, colSpan: 1), C(2) }, // width 2
            new[] { C(3) },                                // width 1 — no placeholder to make up the gap
        };

        Assert.False(SpanGrid.Place(rawRows, out var rows, out _, out var error));
        Assert.Null(rows);
        Assert.Contains("ragged rows", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("row A has width 2", error);
        Assert.Contains("row B has width 1", error);
    }

    // --- Invalid: edge overrun (bottom) ---

    [Fact]
    public void Place_RowSpanRunningPastTheLastRow_IsBottomEdgeOverrun()
    {
        // A single-row grid where the only cell claims a 3-row span — nothing below row A exists.
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>
        {
            new[] { C(1, rowSpan: 3, colSpan: 1) },
        };

        Assert.False(SpanGrid.Place(rawRows, out var rows, out _, out var error));
        Assert.Null(rows);
        Assert.Contains("row A col 1", error);
        Assert.Contains("bottom", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 row(s)", error);
    }

    [Fact]
    public void Place_EmptyRawRows_ReportsEmptySegment()
    {
        Assert.False(SpanGrid.Place(new List<IReadOnlyList<SpanGrid.RawEntry>>(), out var rows, out _, out var error));
        Assert.Null(rows);
        Assert.NotNull(error);
    }

    // --- Invalid: oversized grid (buyer-review defect #8) ---

    [Fact]
    public void Place_GridExceedingMaxCells_IsRejectedBeforeAnyAllocation()
    {
        // 65 rows x 64 columns = 4160 cells — one over SpanGrid.MaxGridCells (4096). Each row is a
        // single ColSpan=64 cell (legal per-span, at LayoutSpecParser.MaxSpanDimension exactly) so
        // this exercises the GRID-TOTAL guard specifically, independent of the per-span cap.
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>();
        for (int i = 0; i < 65; i++)
        {
            rawRows.Add(new[] { C(1, rowSpan: 1, colSpan: 64) });
        }

        Assert.False(SpanGrid.Place(rawRows, out var rows, out _, out var error));
        Assert.Null(rows);
        Assert.Contains("too large", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4096", error);
    }

    [Fact]
    public void Place_GridAtExactlyMaxCells_Succeeds()
    {
        // 64 rows x 64 columns = 4096 cells — exactly SpanGrid.MaxGridCells, the boundary must
        // still be accepted (the guard is "> MaxGridCells", not ">=").
        var rawRows = new List<IReadOnlyList<SpanGrid.RawEntry>>();
        for (int i = 0; i < 64; i++)
        {
            rawRows.Add(new[] { C(1, rowSpan: 1, colSpan: 64) });
        }

        Assert.True(SpanGrid.Place(rawRows, out var rows, out var gridColumns, out var error));
        Assert.Null(error);
        Assert.Equal(64, gridColumns);
        Assert.Equal(64, rows!.Count);
    }
}
