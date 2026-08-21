using GridLookout.Layout;
using Xunit;

namespace GridLookout.Tests.Layout;

public class LayoutSpecParserTests
{
    [Fact]
    public void Parse_BasicExample_ProducesExpectedRows()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A2,B3;B1}", defaultMonitor: 1);

        var token = Assert.Single(result);
        Assert.Equal(1, token.Monitor);
        Assert.Equal(2, token.Rows.Count);

        Assert.Single(token.Rows[0]);
        Assert.Equal(new LayoutCell(true, 2), token.Rows[0][0]);

        Assert.Equal(2, token.Rows[1].Count);
        Assert.Equal(new LayoutCell(true, 3), token.Rows[1][0]);
        Assert.Equal(new LayoutCell(true, 1), token.Rows[1][1]);
    }

    [Fact]
    public void Parse_TwoTokensSameMonitor_FirstWins()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,B2} $layout{A3,A4|B5}", defaultMonitor: 1);

        var token = Assert.Single(result);
        Assert.Equal(1, token.Monitor);
        Assert.Equal(new LayoutCell(true, 1), token.Rows[0][0]);
    }

    [Fact]
    public void Parse_BareAndNumberedTokenCollidingOnDefaultMonitor_FirstWins()
    {
        var result = LayoutSpecParser.ParseValid("$layout2{A1} $layout{A9}", defaultMonitor: 2);

        var token = Assert.Single(result);
        Assert.Equal(2, token.Monitor);
        Assert.Equal(new LayoutCell(true, 1), token.Rows[0][0]);
    }

    [Fact]
    public void Parse_TwoTokensDifferentMonitors_BothKept()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1} $layout2{A2}", defaultMonitor: 1);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Monitor);
        Assert.Equal(2, result[1].Monitor);
    }

    [Fact]
    public void Parse_InvalidFirstTokenDoesNotClaimMonitor_ValidSecondRenders()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,banana} $layout{A5}", defaultMonitor: 1);

        var token = Assert.Single(result);
        Assert.Equal(1, token.Monitor);
        Assert.Equal(new LayoutCell(true, 5), token.Rows[0][0]);
    }

    [Fact]
    public void Parse_MixedSeparators_BothCommaAndSemicolonWork()
    {
        var viaComma = LayoutSpecParser.ParseValid("$layout{A1,A2,B1}", 1).Single();
        var viaSemicolon = LayoutSpecParser.ParseValid("$layout{A1;A2;B1}", 1).Single();
        var viaMixed = LayoutSpecParser.ParseValid("$layout{A1,A2;B1}", 1).Single();

        Assert.Equal(viaComma.Rows, viaSemicolon.Rows);
        Assert.Equal(viaComma.Rows, viaMixed.Rows);
    }

    [Fact]
    public void Parse_Lowercase_IsCaseInsensitive()
    {
        var lower = LayoutSpecParser.ParseValid("$layout{a2,b3;b1}", 1).Single();
        var upper = LayoutSpecParser.ParseValid("$layout{A2,B3;B1}", 1).Single();

        Assert.Equal(upper.Rows, lower.Rows);
    }

    [Fact]
    public void Parse_Whitespace_IsIgnoredEverywhere()
    {
        var spaced = LayoutSpecParser.ParseValid("$layout{ A2 , B3 ; B1 }", 1).Single();
        var tight = LayoutSpecParser.ParseValid("$layout{A2,B3;B1}", 1).Single();

        Assert.Equal(tight.Rows, spaced.Rows);
    }

    [Fact]
    public void Parse_Duplicates_AreAllowed()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A1,B1}", 1).Single();

        Assert.Equal(2, result.Rows[0].Count);
        Assert.Equal(new LayoutCell(true, 1), result.Rows[0][0]);
        Assert.Equal(new LayoutCell(true, 1), result.Rows[0][1]);
    }

    [Fact]
    public void Parse_ZeroOrdinal_ProducesInvalidCellMarker_ButTokenStillParses()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A0,B2}", 1).Single();

        Assert.False(result.Rows[0][0].IsValid);
        Assert.Equal(0, result.Rows[0][0].Ordinal);
        Assert.True(result.Rows[1][0].IsValid);
    }

    [Fact]
    public void Parse_GarbageToken_ReturnsNoLayoutForThatMonitor()
    {
        var result = LayoutSpecParser.ParseValid("$layout{qwerty}", 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_EmptyToken_ReturnsNoLayoutForThatMonitor()
    {
        var result = LayoutSpecParser.ParseValid("$layout{}", 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_IgnoresCityAndBuildingTags_ParsesLayoutOnly()
    {
        var result = LayoutSpecParser.ParseValid("$city{Zagreb}$building{HQ}$layout{A1}", defaultMonitor: 1);

        var token = Assert.Single(result);
        Assert.Equal(1, token.Monitor);
        Assert.Single(token.Rows);
        Assert.Equal(new LayoutCell(true, 1), token.Rows[0][0]);
    }

    [Fact]
    public void Parse_NumberedLayoutToken_TargetsThatMonitor()
    {
        var result = LayoutSpecParser.ParseValid("$layout2{A4}", defaultMonitor: 1);

        var token = Assert.Single(result);
        Assert.Equal(2, token.Monitor);
        Assert.Equal(new LayoutCell(true, 4), token.Rows[0][0]);
    }

    [Fact]
    public void Parse_BareLayoutToken_UsesConfiguredDefaultMonitor()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1}", defaultMonitor: 3);

        var token = Assert.Single(result);
        Assert.Equal(3, token.Monitor);
    }

    [Fact]
    public void Parse_MultipleMonitorTokens_ProducesOneEntryEach()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A2;B3} $layout2{A4}", defaultMonitor: 1);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Monitor == 1);
        Assert.Contains(result, r => r.Monitor == 2);
    }

    [Fact]
    public void Parse_NullOrWhitespaceDescription_ReturnsEmpty()
    {
        Assert.Empty(LayoutSpecParser.ParseValid(null, 1));
        Assert.Empty(LayoutSpecParser.ParseValid("   ", 1));
        Assert.Empty(LayoutSpecParser.ParseValid(string.Empty, 1));
    }

    [Fact]
    public void Parse_NoLayoutTokenPresent_ReturnsEmpty()
    {
        var result = LayoutSpecParser.ParseValid("$city{Zagreb}$building{HQ}", 1);
        Assert.Empty(result);
    }

    // --- '|' page-separated matrix tokens (multi-page $layout{}) ---

    [Fact]
    public void Parse_NoPipe_ProducesExactlyOnePage_IdenticalToOldParse()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A2,B3;B1}", defaultMonitor: 1).Single();

        var page = Assert.Single(result.Pages);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(new LayoutCell(true, 2), page.Rows[0][0]);
        Assert.Equal(new LayoutCell(true, 3), page.Rows[1][0]);
        Assert.Equal(new LayoutCell(true, 1), page.Rows[1][1]);

        // Backward-compatible accessor still matches: pre-paging callers/tests that only ever
        // dealt with single-page tokens keep working unchanged.
        Assert.Equal(page.Rows, result.Rows);
    }

    [Fact]
    public void Parse_TwoPipeSeparatedPages_ProducesTwoPagesInOrder()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A2|A3,A4}", defaultMonitor: 1).Single();

        Assert.Equal(2, result.Pages.Count);
        Assert.Single(result.Pages[0].Rows);
        Assert.Equal(new LayoutCell(true, 1), result.Pages[0].Rows[0][0]);
        Assert.Equal(new LayoutCell(true, 2), result.Pages[0].Rows[0][1]);
        Assert.Single(result.Pages[1].Rows);
        Assert.Equal(new LayoutCell(true, 3), result.Pages[1].Rows[0][0]);
        Assert.Equal(new LayoutCell(true, 4), result.Pages[1].Rows[0][1]);
    }

    [Fact]
    public void Parse_ThreePipeSeparatedPages_EachPageKeepsItsOwnRowGrammar()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A2;B3,B4|A5,A6;B7,B8|A9;B10}", defaultMonitor: 1).Single();

        Assert.Equal(3, result.Pages.Count);

        Assert.Equal(2, result.Pages[0].Rows.Count);
        Assert.Equal(2, result.Pages[0].Rows[0].Count);
        Assert.Equal(new LayoutCell(true, 3), result.Pages[0].Rows[1][0]);
        Assert.Equal(new LayoutCell(true, 4), result.Pages[0].Rows[1][1]);

        Assert.Equal(2, result.Pages[1].Rows.Count);
        Assert.Equal(new LayoutCell(true, 5), result.Pages[1].Rows[0][0]);
        Assert.Equal(new LayoutCell(true, 8), result.Pages[1].Rows[1][1]);

        Assert.Equal(2, result.Pages[2].Rows.Count);
        Assert.Single(result.Pages[2].Rows[0]);
        Assert.Equal(new LayoutCell(true, 9), result.Pages[2].Rows[0][0]);
        Assert.Equal(new LayoutCell(true, 10), result.Pages[2].Rows[1][0]);
    }

    [Fact]
    public void Parse_TrailingPipe_EmptyLastSegmentIsSkipped_NotAnError()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A2|A3,A4|}", defaultMonitor: 1).Single();

        Assert.Equal(2, result.Pages.Count);
    }

    [Fact]
    public void Parse_LeadingAndDoubledPipe_EmptySegmentsAreSkipped()
    {
        var result = LayoutSpecParser.ParseValid("$layout{|A1,A2||A3,A4}", defaultMonitor: 1).Single();

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(new LayoutCell(true, 1), result.Pages[0].Rows[0][0]);
        Assert.Equal(new LayoutCell(true, 3), result.Pages[1].Rows[0][0]);
    }

    [Fact]
    public void Parse_AllSegmentsEmpty_TreatedLikeEmptyToken_ReturnsNoLayoutForThatMonitor()
    {
        var result = LayoutSpecParser.ParseValid("$layout{ | | }", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_GarbageInOnePageSegment_DropsTheWholeToken()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A2|qwerty}", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_PipeInsideMultiMonitorTokens_EachTokenPagesIndependently()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A2|A3,A4} $layout2{A5,A6,A7|A8}", defaultMonitor: 1);

        Assert.Equal(2, result.Count);

        var monitor1 = result.Single(r => r.Monitor == 1);
        Assert.Equal(2, monitor1.Pages.Count);
        Assert.Equal(new LayoutCell(true, 1), monitor1.Pages[0].Rows[0][0]);
        Assert.Equal(new LayoutCell(true, 3), monitor1.Pages[1].Rows[0][0]);

        var monitor2 = result.Single(r => r.Monitor == 2);
        Assert.Equal(2, monitor2.Pages.Count);
        Assert.Equal(3, monitor2.Pages[0].Rows[0].Count);
        Assert.Single(monitor2.Pages[1].Rows[0]);
        Assert.Equal(new LayoutCell(true, 8), monitor2.Pages[1].Rows[0][0]);
    }

    // --- Rotating cells: A(3,4,5) ---

    [Fact]
    public void Parse_RotatingCell_ParsedAsOneCellWithAllOrdinals()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A(3,4,5)}", defaultMonitor: 1).Single();

        var cell = Assert.Single(Assert.Single(result.Rows));
        Assert.True(cell.IsValid);
        Assert.Equal(new[] { 3, 4, 5 }, cell.Ordinals);
        Assert.Equal(3, cell.Ordinal); // convenience accessor == first ordinal
    }

    [Fact]
    public void Parse_MixedFixedAndRotatingRow_BothCellFormsCoexist()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A(2,3),B4}", defaultMonitor: 1).Single();

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Rows[0].Count);
        Assert.Equal(new LayoutCell(true, 1), result.Rows[0][0]);
        Assert.Equal(new LayoutCell(true, new[] { 2, 3 }), result.Rows[0][1]);
        Assert.Single(result.Rows[1]);
        Assert.Equal(new LayoutCell(true, 4), result.Rows[1][0]);
    }

    [Fact]
    public void Parse_RotatingCellInsidePipeSeparatedPage_ParsesPerPage()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A(2,3)|A4}", defaultMonitor: 1).Single();

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(2, result.Pages[0].Rows[0].Count);
        Assert.Equal(new[] { 2, 3 }, result.Pages[0].Rows[0][1].Ordinals);
        Assert.Single(result.Pages[1].Rows[0]);
        Assert.Equal(new LayoutCell(true, 4), result.Pages[1].Rows[0][0]);
    }

    [Fact]
    public void Parse_RotatingCellWithSpacesInsideParens_WhitespaceIgnored()
    {
        var spaced = LayoutSpecParser.ParseValid("$layout{A( 3 , 4 , 5 )}", 1).Single();
        var tight = LayoutSpecParser.ParseValid("$layout{A(3,4,5)}", 1).Single();

        Assert.Equal(tight.Rows, spaced.Rows);
    }

    [Fact]
    public void Parse_RotatingCellWithZeroOrdinal_ProducesInvalidCellMarker_ButTokenStillParses()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A(0,4,5)}", 1).Single();

        var cell = result.Rows[0][0];
        Assert.False(cell.IsValid);
        Assert.Equal(new[] { 0, 4, 5 }, cell.Ordinals);
    }

    [Fact]
    public void Parse_EmptyParens_IsGarbage_DropsTheWholeToken()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A()}", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ParensWithTrailingComma_IsGarbage_DropsTheWholeToken()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A(3,4,)}", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ParensWithNonNumericContent_IsGarbage_DropsTheWholeToken()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A(x,y)}", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_TwoCellsWithNoSeparatorBetween_IsGarbage_DropsTheWholeToken()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1B2}", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_SingleOrdinalInParens_IsOneCellWithOneOrdinal_NotRotating()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A(3)}", defaultMonitor: 1).Single();

        var cell = Assert.Single(Assert.Single(result.Rows));
        Assert.True(cell.IsValid);
        Assert.Single(cell.Ordinals);
        Assert.Equal(3, cell.Ordinal);
    }

    [Fact]
    public void Parse_RotatingCell_CaseInsensitiveRowLetter()
    {
        var lower = LayoutSpecParser.ParseValid("$layout{a(3,4)}", 1).Single();
        var upper = LayoutSpecParser.ParseValid("$layout{A(3,4)}", 1).Single();

        Assert.Equal(upper.Rows, lower.Rows);
    }

    // --- F3 grammar extension: alias / guid / mixed-rotation members ---

    [Fact]
    public void Parse_AliasCell_ParsesAsAliasMember()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A@front-gate}", defaultMonitor: 1).Single();

        var cell = Assert.Single(Assert.Single(result.Rows));
        Assert.True(cell.IsValid);
        var member = Assert.Single(cell.Members);
        Assert.Equal(CellMemberKind.Alias, member.Kind);
        Assert.Equal("front-gate", member.Alias);
    }

    [Fact]
    public void Parse_GuidCell_ParsesAsGuidMember()
    {
        var guid = Guid.NewGuid();
        var result = LayoutSpecParser.ParseValid($"$layout{{A@{{{guid}}}}}", defaultMonitor: 1).Single();

        var cell = Assert.Single(Assert.Single(result.Rows));
        Assert.True(cell.IsValid);
        var member = Assert.Single(cell.Members);
        Assert.Equal(CellMemberKind.Guid, member.Kind);
        Assert.Equal(guid, member.Guid);
    }

    [Fact]
    public void Parse_MixedRotationCell_OrdinalAliasAndGuidCoexist()
    {
        var guid = Guid.NewGuid();
        var result = LayoutSpecParser.ParseValid($"$layout{{A(3,@yard-east,@{{{guid}}})}}", defaultMonitor: 1).Single();

        var cell = Assert.Single(Assert.Single(result.Rows));
        Assert.True(cell.IsValid);
        Assert.Equal(3, cell.Members.Count);
        Assert.Equal(CellMemberKind.Ordinal, cell.Members[0].Kind);
        Assert.Equal(3, cell.Members[0].Ordinal);
        Assert.Equal(CellMemberKind.Alias, cell.Members[1].Kind);
        Assert.Equal("yard-east", cell.Members[1].Alias);
        Assert.Equal(CellMemberKind.Guid, cell.Members[2].Kind);
        Assert.Equal(guid, cell.Members[2].Guid);
    }

    [Fact]
    public void Parse_AliasCell_PreservesWrittenCaseAndHyphens()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A@Front-Gate-02}", defaultMonitor: 1).Single();
        var member = Assert.Single(Assert.Single(Assert.Single(result.Rows)).Members);
        Assert.Equal("Front-Gate-02", member.Alias);
    }

    [Fact]
    public void Parse_MalformedGuidLiteral_IsGarbage_DropsTheWholeToken()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A@{not-a-guid}}", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_UnterminatedGuidBrace_IsGarbage_DropsTheWholeToken()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A@{deadbeef}", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_BareAtWithNoAliasOrBrace_IsGarbage_DropsTheWholeToken()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A@}", defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_GuidTokenBody_DoesNotTruncateAtInnerBrace()
    {
        // The whole point of the depth-aware token scan (see LayoutSpecParser's class doc comment):
        // a guid cell's OWN "}" must not be mistaken for the outer $layout{...} token's closing
        // brace — a naive [^}]* regex would stop here and silently drop the second cell.
        var guid = Guid.NewGuid();
        var result = LayoutSpecParser.ParseValid($"$layout{{A@{{{guid}}},B2}}", defaultMonitor: 1).Single();

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(CellMemberKind.Guid, result.Rows[0][0].Members[0].Kind);
        Assert.Equal(new LayoutCell(true, 2), result.Rows[1][0]);
    }

    [Fact]
    public void Parse_TwoGuidTokensOnDifferentMonitors_BothParseFully()
    {
        // Two tokens, each with its own nested-brace guid cell — the depth scan must resync at the
        // FIRST token's true close, not stop at either guid's inner '}'.
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var result = LayoutSpecParser.ParseValid($"$layout{{A@{{{guid1}}}}} $layout2{{A@{{{guid2}}}}}", defaultMonitor: 1);

        Assert.Equal(2, result.Count);
        Assert.Equal(guid1, result[0].Rows[0][0].Members[0].Guid);
        Assert.Equal(guid2, result[1].Rows[0][0].Members[0].Guid);
    }

    // --- F4 grammar extension: cell spans (:RxC) and the '-' placeholder ---

    [Fact]
    public void Parse_SpanSuffixOnOrdinalCell_SetsRowSpanAndColSpan()
    {
        // A1 claims a 2x2 block; row B's two placeholders confirm the downward+rightward coverage
        // (see SpanGridTests for the geometry engine's own direct coverage of this shape).
        var result = LayoutSpecParser.ParseValid("$layout{A1:2x2;-,-}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(2, page.GridColumns);
        Assert.Equal(2, page.Rows.Count);

        var cell = Assert.Single(page.Rows[0]);
        Assert.Equal(2, cell.RowSpan);
        Assert.Equal(2, cell.ColSpan);
        Assert.Equal(0, cell.Col);
        Assert.Equal(1, cell.Ordinal);
        Assert.Empty(page.Rows[1]); // both columns of row B were placeholders — no real cell there.
    }

    [Fact]
    public void Parse_SpanSuffixOnAliasCell_SetsRowSpanAndColSpan()
    {
        var result = LayoutSpecParser.ParseValid("$layout{B@front-gate:1x2}", defaultMonitor: 1).Single();
        var cell = Assert.Single(result.Pages[0].Rows[0]);

        Assert.Equal(1, cell.RowSpan);
        Assert.Equal(2, cell.ColSpan);
        var member = Assert.Single(cell.Members);
        Assert.Equal(CellMemberKind.Alias, member.Kind);
        Assert.Equal("front-gate", member.Alias);
    }

    [Fact]
    public void Parse_SpanSuffixOnRotationCell_SetsRowSpanAndColSpan()
    {
        // A rowSpan-2 rotating tile needs an actual second row to span into — row B's placeholder
        // confirms that coverage, exactly like a fixed cell's span would.
        var result = LayoutSpecParser.ParseValid("$layout{A(3,@yard-east):2x1;-}", defaultMonitor: 1).Single();
        var cell = Assert.Single(result.Pages[0].Rows[0]);

        Assert.Equal(2, cell.RowSpan);
        Assert.Equal(1, cell.ColSpan);
        Assert.Equal(2, cell.Members.Count);
        Assert.Equal(CellMemberKind.Ordinal, cell.Members[0].Kind);
        Assert.Equal(CellMemberKind.Alias, cell.Members[1].Kind);
        Assert.Empty(result.Pages[0].Rows[1]); // row B's only entry was the covering placeholder.
    }

    [Fact]
    public void Parse_UniformPage_AbsentSuffixDefaultsToOneByOne()
    {
        // A page that trips into uniform mode via ONE spanned cell still lets its OTHER cells stay
        // plain 1x1 — the suffix is per-cell, not page-wide. No ';' here, so both cells share row 0;
        // GridColumns is the sum of what each entry contributes (2 + 1), not just the spanned one's.
        var result = LayoutSpecParser.ParseValid("$layout{A1:1x2,A2}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(3, page.GridColumns);
        Assert.Equal(2, page.Rows[0].Count);
        Assert.Equal(1, page.Rows[0][1].RowSpan);
        Assert.Equal(1, page.Rows[0][1].ColSpan);
        Assert.Equal(2, page.Rows[0][1].Col);
    }

    [Fact]
    public void Parse_LeftTallPlusRightStack_AdminGuideExample_ParsesToTheDocumentedGeometry()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1:3x1,A2;-,B3;-,C4}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(2, page.GridColumns);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(2, page.Rows[0].Count); // A1 (spanning) + A2
        Assert.Single(page.Rows[1]);          // B3 — the '-' placeholder is consumed, not a cell
        Assert.Single(page.Rows[2]);          // C4
        Assert.Equal(3, page.Rows[0][0].RowSpan);
        Assert.Equal(1, page.Rows[1][0].Col); // lands to the right of A1's downward span
    }

    [Theory]
    [InlineData("$layout{A1:0x2}")]   // R must be >= 1
    [InlineData("$layout{A1:2x0}")]   // C must be >= 1
    [InlineData("$layout{A1:AxB}")]   // non-numeric
    [InlineData("$layout{A1:2}")]     // missing the 'x' and second number entirely
    [InlineData("$layout{A1:2x}")]    // missing the second number
    [InlineData("$layout{A1:65x1}")]  // buyer-review defect #8: R exceeds the 64-row cap
    [InlineData("$layout{A1:1x65}")]  // buyer-review defect #8: C exceeds the 64-column cap
    [InlineData("$layout{A1:999999999999x1}")] // grossly oversized R — still just a malformed token
    public void Parse_MalformedSpanSuffix_IsGarbage_DropsTheWholeToken(string description)
    {
        var result = LayoutSpecParser.ParseValid(description, defaultMonitor: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_SpanSuffixAtExactlyTheCap_IsAccepted()
    {
        // ColSpan=64 is the documented ceiling itself (LayoutSpecParser.MaxSpanDimension) — must
        // still parse; only VALUES ABOVE the cap are rejected. RowSpan stays 1 here (a single row,
        // 64 columns wide, no continuation rows needed) so this exercises the SUFFIX cap in
        // isolation from SpanGrid's own bottom-overrun rule — see SpanGridTests' Place_* tests for
        // the grid-total (4096-cell) guard's own direct coverage.
        var result = LayoutSpecParser.ParseValid("$layout{A1:1x64}", defaultMonitor: 1).Single();
        var cell = Assert.Single(result.Pages[0].Rows[0]);
        Assert.Equal(1, cell.RowSpan);
        Assert.Equal(64, cell.ColSpan);
    }

    [Fact]
    public void Parse_OversizedSpanSuffix_LogsAWarningDiagnostic()
    {
        var (logContent, results) = CaptureLoggedWarnings(() =>
            LayoutSpecParser.Parse("$layout{A1:65x1}", defaultMonitor: 1));

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("[WARN]", logContent);
        Assert.Contains("64", result.Diagnostic!);
    }

    [Fact]
    public void Parse_MalformedSpanSuffix_LogsAWarningDiagnostic()
    {
        var (logContent, results) = CaptureLoggedWarnings(() =>
            LayoutSpecParser.Parse("$layout{A1:0x2}", defaultMonitor: 1));

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("[WARN]", logContent);
    }

    [Fact]
    public void Parse_UniformPage_OverlapBetweenSpanAndFreshCell_IsGarbage_WithDiagnostic()
    {
        // Row A's tile spans down into row B col 1 — row B writes a FRESH cell there instead of '-'.
        var results = LayoutSpecParser.Parse("$layout{A1:2x1,A2;B3,B4}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("overlap", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UniformPage_PlaceholderNotCoveredByAnySpan_IsGarbage_WithDiagnostic()
    {
        var results = LayoutSpecParser.Parse("$layout{A1,A2;-,B3}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("not covered", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UniformPage_RaggedRows_IsGarbage_WithDiagnostic()
    {
        var results = LayoutSpecParser.Parse("$layout{A1:1x1,A2;B3}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("ragged rows", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UniformPage_RowSpanPastGridBottom_IsGarbage_WithDiagnostic()
    {
        var results = LayoutSpecParser.Parse("$layout{A1:3x1}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("bottom", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_PageWithNeitherSpanNorPlaceholder_IsNotUniform_LegacyPathUnchanged()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A2;B3}", defaultMonitor: 1).Single();

        Assert.False(result.Pages[0].IsUniform);
        Assert.Equal(0, result.Pages[0].GridColumns);
        // Every cell keeps the F4 defaults — a legacy page's shape is byte-for-byte unchanged.
        Assert.Equal(new LayoutCell(true, 1), result.Rows[0][0]);
    }

    // --- F4 grammar parity fix: uniform mode uses the SAME row rule as legacy (';' OR a
    // letter-change starts a new row — never a silent single-row reinterpretation) ---

    [Fact]
    public void Parse_CommaOnlyWithSpan_RaggedLetterRows_IsInvalid_LoudFailureNotSilentReshape()
    {
        // "A1:1x2,B2" — the letter changes (A -> B) with only ',' between, so this is STILL two
        // rows under the SAME row rule the legacy grammar uses (letter-change starts a new row).
        // Row A sums to 2 (A1's ColSpan), row B sums to 1 (B2 alone) — ragged, so this is a loud
        // diagnostic + last-known-good carry-forward, never a silent "it's just one wide row now."
        var results = LayoutSpecParser.Parse("$layout{A1:1x2,B2}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("ragged rows", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(LayoutSpecParser.ParseValid("$layout{A1:1x2,B2}", defaultMonitor: 1));
    }

    [Fact]
    public void Parse_CommaOnlyWithSpan_ConsistentLetterRows_IsValid_TwoRowsNotOne()
    {
        // "A1:1x2,B2,B3" — letter changes ONCE (A -> B), so row A = [A1] (width 2 via its own
        // ColSpan) and row B = [B2, B3] (width 1+1 = 2) — a VALID 2x2 grid with a wide top tile,
        // written entirely with ',' and letters, no ';' anywhere.
        var result = LayoutSpecParser.ParseValid("$layout{A1:1x2,B2,B3}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(2, page.GridColumns);
        Assert.Equal(2, page.Rows.Count);

        Assert.Single(page.Rows[0]);
        Assert.Equal(2, page.Rows[0][0].ColSpan);
        Assert.Equal(1, page.Rows[0][0].Ordinal);

        Assert.Equal(2, page.Rows[1].Count);
        Assert.Equal(0, page.Rows[1][0].Col);
        Assert.Equal(1, page.Rows[1][1].Col);
    }

    [Fact]
    public void Parse_LettersAloneAndExplicitSemicolon_ProduceIdenticalRowStructure_ForTheSameLayout()
    {
        // Grammar-consistency parity (mirrors the legacy grammar's own
        // Parse_MixedSeparators_BothCommaAndSemicolonWork claim): writing the SAME logical layout
        // via letter-changes-with-',' only, or via an explicit ';' at every row break, must produce
        // the identical row structure — two rows either way.
        var viaLettersOnly = LayoutSpecParser.ParseValid("$layout{A1:1x2,B2,B3}", defaultMonitor: 1).Single().Pages[0];
        var viaSemicolon = LayoutSpecParser.ParseValid("$layout{A1:1x2;B2,B3}", defaultMonitor: 1).Single().Pages[0];

        Assert.Equal(viaLettersOnly.Rows, viaSemicolon.Rows);
        Assert.Equal(2, viaLettersOnly.Rows.Count);
        Assert.Equal(viaLettersOnly.GridColumns, viaSemicolon.GridColumns);
    }

    [Fact]
    public void Parse_ExplicitSemicolonBetweenSameLetterCells_IsCosmetic_MergesLikeTheLegacyGrammar()
    {
        // ';' is NOT an independent row-break trigger for a real cell — only a letter-change is
        // (full parity with the legacy grammar's own "," and ";" are interchangeable" rule; see
        // Parse_MixedSeparators_BothCommaAndSemicolonWork). "B2;B3" (same letter B, ';' between)
        // merges into ONE row exactly like "B2,B3" would — so this token is a VALID 2x2 grid
        // (row A width 2 via A1's span, row B width 1+1=2), not ragged.
        var result = LayoutSpecParser.ParseValid("$layout{A1:1x2;B2;B3}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(2, page.GridColumns);
        Assert.Equal(2, page.Rows.Count);
        Assert.Single(page.Rows[0]);
        Assert.Equal(2, page.Rows[1].Count); // B2 and B3 merged into the same row.
    }

    [Fact]
    public void Parse_NonContiguousLetterReuseWithSpan_IsInvalid_LoudFailureNotSilentThreeRowReshape()
    {
        // "A1,B2,A3" under the LEGACY grammar merges into 2 rows (row A = [A1, A3], row B = [B2] —
        // grouped by letter VALUE, not position). Adding a span must NOT silently reinterpret that
        // same text as 3 rows (A, B, A-again) just because it's now uniform-grid — that's exactly
        // the class of silent wall-reshape this whole fix exists to prevent. Instead, a letter that
        // reappears after its row already closed is a loud grammar error.
        var results = LayoutSpecParser.Parse("$layout{A1:1x2,B2,A3}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("reappears", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(LayoutSpecParser.ParseValid("$layout{A1:1x2,B2,A3}", defaultMonitor: 1));
    }

    [Fact]
    public void Parse_LegacyNonContiguousLetterReuse_StillMergesByValue_UnaffectedByF4()
    {
        // The legacy (non-uniform) path is completely untouched by F4 — no span, no placeholder, so
        // "A1,B2,A3" still merges via letter-VALUE grouping exactly as it always has: row A =
        // [A1, A3], row B = [B2].
        var result = LayoutSpecParser.ParseValid("$layout{A1,B2,A3}", defaultMonitor: 1).Single();

        Assert.False(result.Pages[0].IsUniform);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Rows[0].Count); // row A: A1, A3
        Assert.Single(result.Rows[1]); // row B: B2
    }

    [Fact]
    public void Parse_AdminGuideLeftTallExample_StillUsesExplicitSemicolonsForEachRow()
    {
        // The admin guide's own worked example still needs explicit ';' between rows B and C
        // because those rows START with a placeholder (no letter to trigger a row break) — this
        // pins that the fix didn't regress the one example every other F4 test already exercises.
        var result = LayoutSpecParser.ParseValid("$layout{A1:3x1,A2;-,B3;-,C4}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(2, page.Rows[0].Count);
        Assert.Single(page.Rows[1]);
        Assert.Single(page.Rows[2]);
    }

    // --- Live-lab bugfix: a row left open by placeholders ONLY (no real cell ever claimed a
    // letter for it) must still be closed by an explicit ';' before the next real cell — the
    // letter-change trigger can never fire there (there's no letter to compare against), so ';'
    // is the only signal available and MUST be honored. Root cause: the real-cell row-transition
    // check tested "does the open row's letter differ from mine", which is vacuously false when
    // the open row's letter is unset (null) — so it silently fell through to "continue this row"
    // regardless of ';', merging a placeholder-only row into whatever real-celled row followed it. ---

    [Fact]
    public void Parse_TallSpanWithMultipleConsecutiveDashRows_RegressionFixture()
    {
        // Verbatim live-lab repro: a 13-column, 8-row grid — row A has two 4x4 heroes (A1, A2) and
        // one 5x5 hero (A5); rows 2-4 (0-based rows 1-3) are fully span-covered (13 dashes each,
        // NO real cell ever claims a letter for them); row E has two more 4x4 heroes (E3, E4)
        // plus 5 dashes covering A5's last reach; rows F/G/H are 8 dashes (E3/E4's continuation)
        // plus 5 fresh 1x1 cells each. Before the fix, the ';' between the last all-dash row and
        // row E was silently ignored (no letter to trigger on), merging them into one width-26 row
        // and failing the whole token as "ragged rows".
        const string repro = "$layout{A1:4x4,A2:4x4,A5:5x5;-,-,-,-,-,-,-,-,-,-,-,-,-;-,-,-,-,-,-,-,-,-,-,-,-,-;-,-,-,-,-,-,-,-,-,-,-,-,-;E3:4x4,E4:4x4,-,-,-,-,-;-,-,-,-,-,-,-,-,F6,F7,F8,F9,F10;-,-,-,-,-,-,-,-,G11,G12,G13,G14,G15;-,-,-,-,-,-,-,-,H16,H17,H1,H2,H3}";

        var result = LayoutSpecParser.ParseValid(repro, defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(13, page.GridColumns);
        Assert.Equal(8, page.Rows.Count);

        // Row A (idx0): the three heroes, correctly spanned and positioned.
        Assert.Equal(3, page.Rows[0].Count);
        Assert.Equal(0, page.Rows[0][0].Col);
        Assert.Equal(4, page.Rows[0][0].RowSpan);
        Assert.Equal(4, page.Rows[0][0].ColSpan);
        Assert.Equal(1, page.Rows[0][0].Ordinal);
        Assert.Equal(4, page.Rows[0][1].Col);
        Assert.Equal(2, page.Rows[0][1].Ordinal);
        Assert.Equal(8, page.Rows[0][2].Col);
        Assert.Equal(5, page.Rows[0][2].RowSpan);
        Assert.Equal(5, page.Rows[0][2].ColSpan);

        // Rows 1-3 (idx1-3): entirely consumed by the heroes' downward continuation — zero real
        // cells each. This is exactly the shape that used to merge into one giant row.
        Assert.Empty(page.Rows[1]);
        Assert.Empty(page.Rows[2]);
        Assert.Empty(page.Rows[3]);

        // Row E (idx4): the transition that was actually broken — a real cell (E3) following a
        // row that was open but letterless (idx3 was pure placeholders) must start a FRESH row,
        // not merge into idx3.
        Assert.Equal(2, page.Rows[4].Count);
        Assert.Equal(0, page.Rows[4][0].Col);
        Assert.Equal(4, page.Rows[4][0].RowSpan);
        Assert.Equal(4, page.Rows[4][0].ColSpan);
        Assert.Equal(3, page.Rows[4][0].Ordinal);
        Assert.Equal(4, page.Rows[4][1].Col);
        Assert.Equal(4, page.Rows[4][1].Ordinal);

        // Rows F/G/H (idx5-7): 8 columns still covered by E3/E4's downward reach, then 5 fresh
        // 1x1 cells at columns 8-12 each.
        foreach (var rowIdx in new[] { 5, 6, 7 })
        {
            Assert.Equal(5, page.Rows[rowIdx].Count);
            Assert.Equal(8, page.Rows[rowIdx][0].Col);
            Assert.Equal(12, page.Rows[rowIdx][4].Col);
        }

        // Duplicate ordinals across cells are allowed in span mode (product-confirmed) — H1/H2/H3
        // reuse the same ordinal VALUES as A1/A2 elsewhere in the grid; they're independent cells.
        Assert.Equal(1, page.Rows[7][2].Ordinal);
        Assert.Equal(2, page.Rows[7][3].Ordinal);
        Assert.Equal(3, page.Rows[7][4].Ordinal);
    }

    [Fact]
    public void Parse_FourRowTallSpan_ThreeConsecutiveDashLedRowsEachClaimedByAFollowingCell_IsValid()
    {
        // "$layout{A1:4x1,A2;-,B3;-,C4;-,D5}" — a rowSpan-4 tile beside a 3-row stack (one more
        // row than the admin-guide's rowSpan-3 example), each dash-led row immediately claimed by
        // its own real cell via ',' — not the exact bug shape, but the natural extension of the
        // documented example to a taller span, and worth pinning as its own regression.
        var result = LayoutSpecParser.ParseValid("$layout{A1:4x1,A2;-,B3;-,C4;-,D5}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(2, page.GridColumns);
        Assert.Equal(4, page.Rows.Count);
        Assert.Equal(4, page.Rows[0][0].RowSpan);
        Assert.Single(page.Rows[1]);
        Assert.Single(page.Rows[2]);
        Assert.Single(page.Rows[3]);
        Assert.Equal(1, page.Rows[1][0].Col);
        Assert.Equal(1, page.Rows[2][0].Col);
        Assert.Equal(1, page.Rows[3][0].Col);
    }

    [Fact]
    public void Parse_FourByFourSpanWithThreeFullWidthDashRows_IsValid()
    {
        // A 4x4 hero fills a 4-column grid; the 3 remaining rows are ENTIRELY its own downward
        // continuation (no other cell fits beside it) — 3 consecutive full-width dash rows with
        // nothing else in the grid, the shape the live-lab report originally suspected was broken.
        var result = LayoutSpecParser.ParseValid("$layout{A1:4x4;-,-,-,-;-,-,-,-;-,-,-,-}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(4, page.GridColumns);
        Assert.Equal(4, page.Rows.Count);
        Assert.Single(page.Rows[0]);
        Assert.Equal(4, page.Rows[0][0].RowSpan);
        Assert.Equal(4, page.Rows[0][0].ColSpan);
        Assert.Empty(page.Rows[1]);
        Assert.Empty(page.Rows[2]);
        Assert.Empty(page.Rows[3]);
    }

    [Fact]
    public void Parse_PlaceholderOnlyRowFollowedByRealCellViaSemicolon_StartsAFreshRow()
    {
        // Isolates the exact root cause in the smallest possible shape: row A's hero reaches down
        // into row B (placeholders only, no real cell ever claims a letter there); row C then
        // starts with a REAL cell right after an explicit ';' — this must be its own fresh row,
        // not a silent merge into row B's width.
        var result = LayoutSpecParser.ParseValid("$layout{A1:2x2;-,-;B3,B4}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(2, page.GridColumns);
        Assert.Equal(3, page.Rows.Count);
        Assert.Single(page.Rows[0]);
        Assert.Empty(page.Rows[1]); // both placeholders consumed — row B claims no real cell.
        Assert.Equal(2, page.Rows[2].Count); // row C is its OWN row, not merged into row B.
    }

    [Fact]
    public void Parse_PlaceholderOnlyRowThenReusedLetterWithoutSemicolon_StillCaughtAsReappearance()
    {
        // The one combination where the null-currentRowLetter fallback and the reappearance guard
        // interact: row A claims 'A' and closes it; row B is placeholders only (letterless); then
        // 'A3' arrives with only ',' before it (no ';'). The null-fallback alone would just CLAIM
        // row B (same as row C claiming its placeholder in the admin-guide example) — but the
        // reappearance check fires first, because 'A' is already in closedRowLetters and row B's
        // open letter (null) isn't 'A'. Confirms the two checks compose correctly rather than one
        // silently overriding the other.
        var results = LayoutSpecParser.Parse("$layout{A1:2x2;-,-;A3,A4}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("reappears", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_DuplicateOrdinalsAcrossCells_AreAllowedInSpanMode()
    {
        // Matches the legacy grammar's own Parse_Duplicates_AreAllowed guarantee — a span/placeholder
        // page places no additional restriction on reusing the same camera ordinal in different cells.
        var result = LayoutSpecParser.ParseValid("$layout{A1:1x2,B1,B2}", defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        var heroOrdinal = page.Rows[0][0].Ordinal;
        var stackOrdinal = page.Rows[1][0].Ordinal;
        Assert.Equal(1, heroOrdinal);
        Assert.Equal(1, stackOrdinal); // same camera ordinal (1) reused in a different cell — fine.
    }

    [Fact]
    public void Parse_HyphenatedAlias_NeverMisdetectedAsAPlaceholder()
    {
        // '@front-gate-02' has TWO hyphens, neither bounded by a separator — must stay on the
        // legacy (non-uniform) path exactly like before F4.
        var result = LayoutSpecParser.ParseValid("$layout{A@front-gate-02,B@loading-dock}", defaultMonitor: 1).Single();

        Assert.False(result.Pages[0].IsUniform);
        Assert.Equal("front-gate-02", result.Rows[0][0].Members[0].Alias);
    }

    [Fact]
    public void Parse_MultiPageToken_LegacyPageAndUniformPageCanCoexist()
    {
        var result = LayoutSpecParser.ParseValid("$layout{A1,A2|A1:1x2}", defaultMonitor: 1).Single();

        Assert.Equal(2, result.Pages.Count);
        Assert.False(result.Pages[0].IsUniform);
        Assert.True(result.Pages[1].IsUniform);
    }

    [Fact]
    public void Parse_SpannedLayoutString_ParsesTheSameWayForTheMultiRecorderConfigLayoutSource()
    {
        // Program.cs's ComputeWallFormSpecs feeds config.Layout (multi-recorder mode) through this
        // EXACT same LayoutSpecParser.Parse call it uses for a single recorder's Description — see
        // that method's own doc comment ("through this SAME LayoutSpecParser/LayoutResolver
        // pipeline"). Nothing about F4 needed separate wiring for multi mode; this test pins that a
        // spanned token reaches the SAME uniform-grid result regardless of which source string
        // supplied it.
        const string multiRecorderLayoutConfigValue = "$layout{A1:2x2,A3;-,-,B4;C5,C6,C7}";

        var result = LayoutSpecParser.ParseValid(multiRecorderLayoutConfigValue, defaultMonitor: 1).Single();
        var page = result.Pages[0];

        Assert.True(page.IsUniform);
        Assert.Equal(3, page.GridColumns);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(2, page.Rows[0][0].RowSpan);
        Assert.Equal(2, page.Rows[0][0].ColSpan);
    }

    // --- F3 parser output contract: TokenParseResult / diagnostics / logging ---

    [Fact]
    public void Parse_ValidToken_ProducesOneValidTokenParseResult()
    {
        var results = LayoutSpecParser.Parse("$layout{A1}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.Equal(TokenStatus.Valid, result.Status);
        Assert.True(result.IsValid);
        Assert.Equal(1, result.Monitor);
        Assert.NotNull(result.Layout);
        Assert.Null(result.Diagnostic);
        Assert.Contains("$layout{A1}", result.RawToken);
    }

    [Fact]
    public void Parse_GarbageToken_ProducesInvalidTokenParseResultNamingTheMonitor()
    {
        var results = LayoutSpecParser.Parse("$layout2{qwerty}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.Equal(TokenStatus.Invalid, result.Status);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Monitor);
        Assert.Null(result.Layout);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public void Parse_UnterminatedToken_ProducesInvalidResult_MonitorStillKnown()
    {
        var results = LayoutSpecParser.Parse("$layout{A1", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.Equal(TokenStatus.Invalid, result.Status);
        // The monitor digit group parses fine even though the body never closed — "unknown
        // monitor" is reserved for the rarer case where even THAT can't be parsed (see the overflow
        // test below).
        Assert.Equal(1, result.Monitor);
        Assert.Contains("unterminated", result.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MonitorDigitOverflow_ProducesInvalidResultWithUnknownMonitor()
    {
        var results = LayoutSpecParser.Parse("$layout99999999999999999999{A1}", defaultMonitor: 1);

        var result = Assert.Single(results);
        Assert.Equal(TokenStatus.Invalid, result.Status);
        Assert.Null(result.Monitor);
    }

    [Fact]
    public void Parse_DuplicateMonitorToken_SecondIsInvalidResult_FirstStaysValid()
    {
        var results = LayoutSpecParser.Parse("$layout{A1} $layout{A2}", defaultMonitor: 1);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsValid);
        Assert.False(results[1].IsValid);
        Assert.Equal(1, results[1].Monitor);
        Assert.Contains("duplicate", results[1].Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EveryInvalidToken_LogsAWarningNamingTheMonitorAndReason()
    {
        var (logContent, results) = CaptureLoggedWarnings(() =>
            LayoutSpecParser.Parse("$layout2{qwerty}", defaultMonitor: 1));

        var result = Assert.Single(results);
        Assert.False(result.IsValid);
        Assert.Contains("monitor 2", logContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[WARN]", logContent);
    }

    [Fact]
    public void Parse_MonitorOverflowToken_LogsWarningSayingUnknownMonitor()
    {
        var (logContent, _) = CaptureLoggedWarnings(() =>
            LayoutSpecParser.Parse("$layout99999999999999999999{A1}", defaultMonitor: 1));

        Assert.Contains("unknown monitor", logContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UnterminatedToken_LogsWarning()
    {
        var (logContent, _) = CaptureLoggedWarnings(() =>
            LayoutSpecParser.Parse("$layout{A1", defaultMonitor: 1));

        Assert.Contains("unterminated", logContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_DuplicateToken_LogsWarning()
    {
        var (logContent, _) = CaptureLoggedWarnings(() =>
            LayoutSpecParser.Parse("$layout{A1} $layout{A2}", defaultMonitor: 1));

        Assert.Contains("duplicate", logContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ValidToken_LogsNoWarningAtAll()
    {
        var (logContent, _) = CaptureLoggedWarnings(() =>
            LayoutSpecParser.Parse("$layout{A1,B2}", defaultMonitor: 1));

        Assert.DoesNotContain("[WARN]", logContent);
    }

    /// <summary>Runs <paramref name="body"/> with <see cref="LayoutSpecParser.Logger"/> pointed at a
    /// fresh temp directory's <see cref="GridLookout.Logging.FileLogger"/>, returns the resulting
    /// log file content alongside <paramref name="body"/>'s own return value, and ALWAYS resets
    /// <see cref="LayoutSpecParser.Logger"/> back to null afterward (try/finally) —
    /// <see cref="LayoutSpecParser.Logger"/> is a shared static, so leaking a non-null value out of
    /// one test would bleed into every test that runs after it in this class.</summary>
    private static (string LogContent, IReadOnlyList<TokenParseResult> Results) CaptureLoggedWarnings(Func<IReadOnlyList<TokenParseResult>> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.Layout." + Guid.NewGuid());
        try
        {
            var logger = new GridLookout.Logging.FileLogger(dir, GridLookout.Logging.LogLevel.Debug);
            LayoutSpecParser.Logger = logger;
            var results = body();
            var logPath = Path.Combine(dir, $"gridlookout-{DateTime.Now:yyyyMMdd}.log");
            var content = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
            return (content, results);
        }
        finally
        {
            LayoutSpecParser.Logger = null;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
