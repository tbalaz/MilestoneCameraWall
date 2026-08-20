using GridLookout.Layout;
using Xunit;

namespace GridLookout.Tests.Layout;

public class LayoutResolverTests
{
    private static CameraCatalogEntry Cam(Guid id, string name, bool enabled = true) => new(id, name, enabled);

    private static LayoutResolver.ResolveInput Input(
        string description,
        int defaultMonitor,
        IReadOnlyList<CameraCatalogEntry> catalog,
        IReadOnlyList<Guid> orderedEnabledCameraIds,
        IReadOnlyDictionary<string, Guid>? bindings = null,
        LayoutStateFile? persistedState = null,
        IReadOnlyList<Guid>? recorderIds = null)
    {
        var tokenResults = LayoutSpecParser.Parse(description, defaultMonitor);
        return new LayoutResolver.ResolveInput(
            tokenResults, catalog, orderedEnabledCameraIds, bindings ?? new Dictionary<string, Guid>(),
            persistedState, recorderIds ?? Array.Empty<Guid>());
    }

    private static ResolvedMember SingleMember(LayoutResolver.ResolveResult result, int monitor) =>
        Assert.Single(Assert.Single(Assert.Single(result.Plan.Monitors.Single(m => m.Monitor == monitor).Pages).Rows).Cells).Members[0];

    // --- Basic resolution: ordinal / alias / guid ---

    [Fact]
    public void Resolve_Ordinal_ResolvesToTheNthEnabledCamera()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };
        var ordered = new[] { idA, idB };

        var result = LayoutResolver.Resolve(Input("$layout{A2}", 1, catalog, ordered));

        var member = SingleMember(result, 1);
        Assert.True(member.Available);
        Assert.Equal(idB, member.CameraId);
    }

    [Fact]
    public void Resolve_Alias_ResolvesViaCameraBindings()
    {
        var idX = Guid.NewGuid();
        var catalog = new[] { Cam(idX, "Front Gate") };
        var bindings = new Dictionary<string, Guid> { ["front-gate"] = idX };

        var result = LayoutResolver.Resolve(Input("$layout{A@front-gate}", 1, catalog, Array.Empty<Guid>(), bindings));

        var member = SingleMember(result, 1);
        Assert.True(member.Available);
        Assert.Equal(idX, member.CameraId);
    }

    [Fact]
    public void Resolve_Guid_ResolvesDirectlyAgainstCatalog()
    {
        var idX = Guid.NewGuid();
        var catalog = new[] { Cam(idX, "Front Gate") };

        var result = LayoutResolver.Resolve(Input($"$layout{{A@{{{idX}}}}}", 1, catalog, Array.Empty<Guid>()));

        var member = SingleMember(result, 1);
        Assert.True(member.Available);
        Assert.Equal(idX, member.CameraId);
    }

    // --- Unavailable rule (F3 rule 5): unified placeholder for every unresolvable reference ---

    [Fact]
    public void Resolve_UnknownAlias_IsUnavailable_NeverSubstitutesAnotherCamera()
    {
        var result = LayoutResolver.Resolve(Input("$layout{A@no-such-alias}", 1, Array.Empty<CameraCatalogEntry>(), Array.Empty<Guid>()));

        var member = SingleMember(result, 1);
        Assert.False(member.Available);
        Assert.Null(member.CameraId);
        Assert.Contains("no-such-alias", member.UnavailableReason);
    }

    [Fact]
    public void Resolve_GuidNotInCatalog_IsUnavailable_ReasonSaysNotFound()
    {
        var missingId = Guid.NewGuid();
        var result = LayoutResolver.Resolve(Input($"$layout{{A@{{{missingId}}}}}", 1, Array.Empty<CameraCatalogEntry>(), Array.Empty<Guid>()));

        var member = SingleMember(result, 1);
        Assert.False(member.Available);
        Assert.Equal(missingId, member.CameraId); // a guid literal IS its own id even when unavailable
        Assert.Contains("not found", member.UnavailableReason);
    }

    [Fact]
    public void Resolve_DisabledCamera_IsUnavailable_ReasonSaysDisabled()
    {
        var idX = Guid.NewGuid();
        var catalog = new[] { Cam(idX, "Loading Dock", enabled: false) };

        var result = LayoutResolver.Resolve(Input($"$layout{{A@{{{idX}}}}}", 1, catalog, Array.Empty<Guid>()));

        var member = SingleMember(result, 1);
        Assert.False(member.Available);
        Assert.Equal(idX, member.CameraId);
        Assert.Contains("disabled", member.UnavailableReason);
    }

    [Fact]
    public void Resolve_OutOfRangeOrdinal_IsUnavailable()
    {
        var idA = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A") };

        var result = LayoutResolver.Resolve(Input("$layout{A9}", 1, catalog, new[] { idA }));

        var member = SingleMember(result, 1);
        Assert.False(member.Available);
        Assert.Null(member.CameraId);
        Assert.Contains("out of range", member.UnavailableReason);
    }

    // --- Fingerprint pin/reuse/changed (F3 rule 6a/6b) ---

    [Fact]
    public void Resolve_FirstResolve_PersistsPinnedCameraId()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };

        var result = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA, idB }));

        var persistedMember = result.NewState.ResolvedPlan["1"][0].Rows[0].Cells[0].Members[0];
        Assert.Equal(idA, persistedMember.CameraId);
        Assert.True(result.NewState.MonitorFingerprints.ContainsKey("1"));
        Assert.NotEmpty(result.NewState.MonitorFingerprints["1"]);
    }

    [Fact]
    public void Resolve_UnchangedFingerprint_ReusesPinnedCameraId_DespiteReorder()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };

        // First resolve: A1 pins to idA (idA sorts first).
        var first = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA, idB }));
        Assert.Equal(idA, SingleMember(first, 1).CameraId);

        // "Rename" flips the sort order (idB now sorts first) — but the description's $layout TEXT
        // is byte-identical, so the fingerprint is unchanged.
        var second = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idB, idA }, persistedState: first.NewState));

        // Must still be idA — the whole point of F3: a rename/reorder never silently repoints a cell.
        Assert.Equal(idA, SingleMember(second, 1).CameraId);
    }

    [Fact]
    public void Resolve_ChangedFingerprint_ReResolvesOrdinalsFreshAgainstCurrentOrder()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };

        var first = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA, idB }));
        Assert.Equal(idA, SingleMember(first, 1).CameraId);

        // A genuinely edited token (A1 -> A2) changes the fingerprint — this IS new intent, so a
        // fresh resolve against the CURRENT order is correct, not a bug.
        var second = LayoutResolver.Resolve(Input("$layout{A2}", 1, catalog, new[] { idA, idB }, persistedState: first.NewState));

        Assert.Equal(idB, SingleMember(second, 1).CameraId);
    }

    [Fact]
    public void Resolve_NeverPinnedMember_RetriesEveryResolve_EvenWithUnchangedFingerprint()
    {
        var idA = Guid.NewGuid();
        var catalog = new List<CameraCatalogEntry> { Cam(idA, "Cam A") };

        // A2 is out of range with only one camera — never pins.
        var first = LayoutResolver.Resolve(Input("$layout{A2}", 1, catalog, new[] { idA }));
        Assert.False(SingleMember(first, 1).Available);

        // A camera was added (camera COUNT changed, so Program.cs's signature check would trigger a
        // rebuild in real life) — same fingerprint (token text unchanged), but A2 can resolve now.
        var idB = Guid.NewGuid();
        catalog.Add(Cam(idB, "Cam B"));
        var second = LayoutResolver.Resolve(Input("$layout{A2}", 1, catalog, new[] { idA, idB }, persistedState: first.NewState));

        Assert.True(SingleMember(second, 1).Available);
        Assert.Equal(idB, SingleMember(second, 1).CameraId);
    }

    [Fact]
    public void Resolve_PinnedCameraLaterDisabled_StaysUnavailable_PinIsNeverRederived()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new List<CameraCatalogEntry> { Cam(idA, "Cam A"), Cam(idB, "Cam B") };

        var first = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA, idB }));
        Assert.Equal(idA, SingleMember(first, 1).CameraId);

        // idA gets disabled — same fingerprint.
        var catalogAfterDisable = new[] { Cam(idA, "Cam A", enabled: false), Cam(idB, "Cam B") };
        var second = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalogAfterDisable, new[] { idB }, persistedState: first.NewState));

        var member = SingleMember(second, 1);
        Assert.False(member.Available);
        // Rule 6e: the pin itself is untouched — it does NOT silently fall through to whatever
        // ordinal 1 means now (idB, the only remaining enabled camera).
        Assert.Equal(idA, member.CameraId);
    }

    // --- Rule 6c: malformed token falls back to last-known-good; rule 6d: structural fallback ---

    [Fact]
    public void Resolve_MalformedTokenWithPersistedEntry_CarriesForwardLastKnownGood()
    {
        var idA = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A") };

        var first = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA }));
        Assert.Single(first.Plan.Monitors);

        // Typo introduced — token becomes malformed (garbage), which also changes the fingerprint.
        var second = LayoutResolver.Resolve(Input("$layout{qwerty}", 1, catalog, new[] { idA }, persistedState: first.NewState));

        var monitor1 = Assert.Single(second.Plan.Monitors);
        Assert.Equal(1, monitor1.Monitor);
        var member = monitor1.Pages[0].Rows[0].Cells[0].Members[0];
        Assert.Equal(idA, member.CameraId); // stale but valid — still the ORIGINAL A1 pin

        // Bug fix (live-reproduced): the carry-forward must be MARKED so a later resolve knows this
        // monitor's persisted entry isn't a genuine pin — see CarriedForwardMonitors' doc comment for
        // why the single whole-description fingerprint can't carry this signal by itself.
        Assert.Contains("1", second.NewState.CarriedForwardMonitors);
        Assert.DoesNotContain("1", first.NewState.CarriedForwardMonitors);
    }

    [Fact]
    public void Resolve_CarriedForwardMonitor_ReResolvesEvenWhenFingerprintStillMatches()
    {
        // The literal live-reproduced defect: a parser fix (or any cause) makes the SAME raw token
        // text parse successfully where it used to be malformed. LayoutFingerprint.ComputeForMonitor
        // hashes only THIS monitor's raw text (plus bindings/recorder ids, both empty here), so its
        // fingerprint is IDENTICAL before and after such a fix — this test constructs that exact
        // situation directly (computing "$layout{A2}"'s own per-monitor fingerprint and using it as
        // the persisted one) rather than patching LayoutSpecParser mid-test, and proves the
        // CarriedForwardMonitors mark — not the fingerprint — is what makes a monitor re-check its
        // own token instead of trusting a stale carried plan forever.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };
        var ordered = new[] { idA, idB };

        var nowValidToken = LayoutSpecParser.Parse("$layout{A2}", 1).Single(r => r.Monitor == 1);
        var matchingFingerprint = LayoutFingerprint.ComputeForMonitor(nowValidToken, new Dictionary<string, Guid>(), Array.Empty<Guid>());

        // Hand-built as if this exact text had been carried forward (stale idA pin from an earlier
        // "$layout{A1}") while it still parsed as malformed, right up until this fingerprint's text.
        var persisted = new LayoutStateFile
        {
            SchemaVersion = 1,
            MonitorFingerprints = new Dictionary<string, string> { ["1"] = matchingFingerprint },
            ResolvedPlan = new Dictionary<string, List<PersistedPage>>
            {
                ["1"] = new List<PersistedPage>
                {
                    new()
                    {
                        Rows = new List<PersistedRow>
                        {
                            new()
                            {
                                Cells = new List<PersistedCell>
                                {
                                    new() { Members = new List<PersistedMember> { new() { RefKind = "Ordinal", RefLabel = "1", CameraId = idA } } },
                                },
                            },
                        },
                    },
                },
            },
            CarriedForwardMonitors = new List<string> { "1" },
        };

        var result = LayoutResolver.Resolve(Input("$layout{A2}", 1, catalog, ordered, persistedState: persisted));

        // Bug: a naive "fingerprint matches -> trust verbatim" rule 6a would have reused idA here even
        // though it's exactly wrong. Fix: this monitor is marked carried-forward, so its own current
        // token is re-checked and re-pinned fresh against A2 regardless of the fingerprint match.
        Assert.Equal(idB, SingleMember(result, 1).CameraId);
        Assert.DoesNotContain("1", result.NewState.CarriedForwardMonitors); // escaped — no longer marked
    }

    [Fact]
    public void Resolve_MalformedTokenCarryForward_ThenTokenBecomesValidAgain_FullyReResolvesInsteadOfStayingPinned()
    {
        // End-to-end regression for the live-reproduced defect: good token -> malformed carry-forward
        // -> token becomes valid again (simulated here with different text, since we can't literally
        // patch LayoutSpecParser mid-test to make identical text parse differently — see
        // Resolve_CarriedForwardMonitor_ReResolvesEvenWhenFingerprintStillMatches above for the
        // fingerprint-identical variant of this same proof).
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };
        var ordered = new[] { idA, idB };

        var good = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, ordered));
        Assert.Equal(idA, SingleMember(good, 1).CameraId);

        var malformed = LayoutResolver.Resolve(Input("$layout{qwerty}", 1, catalog, ordered, persistedState: good.NewState));
        Assert.Equal(idA, SingleMember(malformed, 1).CameraId); // carried forward
        Assert.Contains("1", malformed.NewState.CarriedForwardMonitors); // fix: marked, not just fingerprint-frozen

        var revalid = LayoutResolver.Resolve(Input("$layout{A2}", 1, catalog, ordered, persistedState: malformed.NewState));

        Assert.Equal(idB, SingleMember(revalid, 1).CameraId); // fresh resolve against current order, not the stale idA pin
        Assert.DoesNotContain("1", revalid.NewState.CarriedForwardMonitors); // escaped — mark cleared
    }

    [Fact]
    public void Resolve_MalformedSiblingToken_DoesNotUnpinTheOtherMonitor()
    {
        // Regression guard for the fingerprint-freeze approach this fix deliberately avoided: with a
        // GLOBAL (whole-description) fingerprint, freezing it whenever ANY monitor carries forward
        // would strip an unrelated healthy sibling monitor of its own rule-6a pin for as long as the
        // other monitor's token stays broken — reopening exactly the rename/reorder drift bug F3
        // exists to prevent, just for the innocent monitor instead of the broken one.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };

        var first = LayoutResolver.Resolve(Input("$layout{A1} $layout2{A1}", 1, catalog, new[] { idA, idB }));
        Assert.Equal(idA, SingleMember(first, 1).CameraId);

        // Only monitor 2's token gets a typo — monitor 1's text is untouched.
        var second = LayoutResolver.Resolve(Input("$layout{A1} $layout2{qwerty}", 1, catalog, new[] { idA, idB }, persistedState: first.NewState));
        Assert.Equal(2, second.Plan.Monitors.Count); // guard: carry-forward for monitor 2 actually fired
        Assert.Equal(idA, SingleMember(second, 1).CameraId);
        Assert.Contains("2", second.NewState.CarriedForwardMonitors);
        Assert.DoesNotContain("1", second.NewState.CarriedForwardMonitors);

        // Nothing about the description is edited further; a rename flips the enabled-camera sort
        // order while monitor 2's token is still sitting there malformed.
        var third = LayoutResolver.Resolve(Input("$layout{A1} $layout2{qwerty}", 1, catalog, new[] { idB, idA }, persistedState: second.NewState));

        // Monitor 1 must still be idA — its own pin was never touched by monitor 2's problem.
        Assert.Equal(idA, SingleMember(third, 1).CameraId);
    }

    [Fact]
    public void Resolve_MismatchAfterCarryForward_LogsInfoLine_SoOperatorSeesTheWallUnpinned()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };
        var ordered = new[] { idA, idB };

        var (logContent, _) = CaptureLoggedLines(() =>
        {
            var good = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, ordered));
            var malformed = LayoutResolver.Resolve(Input("$layout{qwerty}", 1, catalog, ordered, persistedState: good.NewState));
            LayoutResolver.Resolve(Input("$layout{A2}", 1, catalog, ordered, persistedState: malformed.NewState));
        });

        Assert.Contains("[INFO]", logContent);
        Assert.Contains("re-pinning fresh", logContent);
    }

    [Fact]
    public void Resolve_MalformedTokenWithNoPersistedEntry_MonitorGetsNoLayout()
    {
        var result = LayoutResolver.Resolve(Input("$layout{qwerty}", 1, Array.Empty<CameraCatalogEntry>(), Array.Empty<Guid>()));

        Assert.Empty(result.Plan.Monitors);
    }

    [Fact]
    public void Resolve_NoTokensAtAllColdStart_ProducesEmptyPlan_CallerFallsBackToAutoLayout()
    {
        var result = LayoutResolver.Resolve(Input("$city{Zagreb}", 1, Array.Empty<CameraCatalogEntry>(), Array.Empty<Guid>()));

        Assert.Empty(result.Plan.Monitors);
        Assert.Empty(result.NewState.MonitorFingerprints);
        Assert.Empty(result.NewState.ResolvedPlan);
    }

    // --- F4 (cell spans): RowSpan/ColSpan/Col/IsUniform/GridColumns carry through resolution ---

    [Fact]
    public void Resolve_FreshUniformPage_CarriesSpanAndPositionOntoResolvedCells()
    {
        var idA = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A") };

        // A single 2x2 tile fills the whole grid — row B is entirely the placeholders confirming
        // its downward+rightward coverage, so it resolves to zero cells (see
        // SpanGridTests.Place_AllPlaceholderRow_ProducesAnEmptyPlacedRow_NotAnError for the
        // geometry engine's own direct coverage of this shape).
        var result = LayoutResolver.Resolve(Input("$layout{A1:2x2;-,-}", 1, catalog, new[] { idA }));

        var monitor1 = result.Plan.Monitors.Single(m => m.Monitor == 1);
        var page = monitor1.Pages[0];
        Assert.True(page.IsUniform);
        Assert.Equal(2, page.GridColumns);

        var spannedCell = page.Rows[0].Cells[0];
        Assert.Equal(0, spannedCell.Col);
        Assert.Equal(2, spannedCell.RowSpan);
        Assert.Equal(2, spannedCell.ColSpan);
        Assert.True(spannedCell.Members[0].Available);
        Assert.Equal(idA, spannedCell.Members[0].CameraId);
        Assert.Empty(page.Rows[1].Cells);

        // The persisted half carries the same fields, so a later reload (ReapplyPersistedPages)
        // sees the identical geometry — see Resolve_ReappliedUniformPage_KeepsSpanAndPosition below.
        var persistedCell = result.NewState.ResolvedPlan["1"][0].Rows[0].Cells[0];
        Assert.Equal(0, persistedCell.Col);
        Assert.Equal(2, persistedCell.RowSpan);
        Assert.Equal(2, persistedCell.ColSpan);
        Assert.True(result.NewState.ResolvedPlan["1"][0].IsUniform);
        Assert.Equal(2, result.NewState.ResolvedPlan["1"][0].GridColumns);
    }

    [Fact]
    public void Resolve_ReappliedUniformPage_KeepsSpanAndPosition_UnchangedFingerprint()
    {
        // Rule 6a: same fingerprint reuses the persisted plan verbatim via ReapplyPersistedPages —
        // this pins that F4's geometry fields survive THAT path too, not just a fresh resolve.
        // A1 spans 1x2 (row A, columns 0-1); row B is three plain 1x1 cells (columns 0-2) — the
        // letter genuinely changes (A -> B) so this is two rows under the parity-fixed row rule
        // (a same-letter row wouldn't break here even across ';' — see LayoutSpecParserTests).
        var idA = Guid.NewGuid();
        var catalog = new List<CameraCatalogEntry> { Cam(idA, "Cam A") };
        var ordered = new[] { idA };

        var first = LayoutResolver.Resolve(Input("$layout{A1:1x2,A2;B3,B4,B5}", 1, catalog, ordered));
        var second = LayoutResolver.Resolve(Input("$layout{A1:1x2,A2;B3,B4,B5}", 1, catalog, ordered, persistedState: first.NewState));

        var page = second.Plan.Monitors.Single(m => m.Monitor == 1).Pages[0];
        Assert.True(page.IsUniform);
        Assert.Equal(3, page.GridColumns);

        Assert.Equal(0, page.Rows[0].Cells[0].Col);
        Assert.Equal(2, page.Rows[0].Cells[0].ColSpan);
        Assert.Equal(2, page.Rows[0].Cells[1].Col); // A2, to the right of A1's 2-wide span

        Assert.Equal(0, page.Rows[1].Cells[0].Col);
        Assert.Equal(1, page.Rows[1].Cells[1].Col);
        Assert.Equal(2, page.Rows[1].Cells[2].Col);
    }

    [Fact]
    public void Resolve_LegacyNonUniformPage_ResolvedCellsKeepDefaultSpanFields()
    {
        // Zero behavior change for every existing (non-spanned) config: resolved cells still carry
        // RowSpan=1/ColSpan=1/Col=0 and the page still reports IsUniform=false/GridColumns=0.
        var idA = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A") };

        var result = LayoutResolver.Resolve(Input("$layout{A1,A2}", 1, catalog, new[] { idA }));

        var page = result.Plan.Monitors.Single(m => m.Monitor == 1).Pages[0];
        Assert.False(page.IsUniform);
        Assert.Equal(0, page.GridColumns);
        foreach (var cell in page.Rows[0].Cells)
        {
            Assert.Equal(1, cell.RowSpan);
            Assert.Equal(1, cell.ColSpan);
            Assert.Equal(0, cell.Col);
        }
    }

    [Fact]
    public void Resolve_TokensRemovedThenReAdded_TreatedAsNewIntent_NotStalePinReuse()
    {
        // Regression guard for the "persist even on an empty plan" fix: without it, removing then
        // re-adding a BYTE-IDENTICAL token would spuriously match a stale persisted fingerprint and
        // reuse an outdated pin — resurrecting the exact bug F3 exists to fix.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };

        var withToken = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA, idB }));
        Assert.Equal(idA, SingleMember(withToken, 1).CameraId);

        // Operator removes the $layout token entirely. Monitor "1" is no longer considered AT ALL
        // (buyer-review defects #4/#5/#7 fix — see LayoutResolver's own class doc comment for why an
        // orphaned persisted-only entry is now intentionally out of scope), so both ResolvedPlan and
        // MonitorFingerprints come back empty — there is nothing left with a current token to persist
        // a fingerprint FOR.
        var removed = LayoutResolver.Resolve(Input("$city{Zagreb}", 1, catalog, new[] { idA, idB }, persistedState: withToken.NewState));
        Assert.Empty(removed.Plan.Monitors);
        Assert.Empty(removed.NewState.MonitorFingerprints);
        Assert.Empty(removed.NewState.ResolvedPlan);

        // Cameras get reordered while no $layout token is present (no pin to protect anyway).
        // Operator re-adds the SAME token text.
        var readded = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idB, idA }, persistedState: removed.NewState));

        // Must resolve fresh against the CURRENT order (idB), not silently reuse the old idA pin —
        // removed.NewState has NO entry for monitor "1" at all (neither ResolvedPlan nor
        // MonitorFingerprints), so there is nothing for readded's own fingerprint to match.
        Assert.Equal(idB, SingleMember(readded, 1).CameraId);
    }

    [Fact]
    public void Resolve_MixedRotationCell_EachMemberResolvedIndependently()
    {
        var idA = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A") };
        var bindings = new Dictionary<string, Guid>(); // "broken-alias" deliberately unbound

        var result = LayoutResolver.Resolve(Input("$layout{A(1,@broken-alias)}", 1, catalog, new[] { idA }, bindings));

        var cell = result.Plan.Monitors.Single().Pages[0].Rows[0].Cells[0];
        Assert.True(cell.IsRotating);
        Assert.Equal(2, cell.Members.Count);
        Assert.True(cell.Members[0].Available);
        Assert.Equal(idA, cell.Members[0].CameraId);
        Assert.False(cell.Members[1].Available);
    }

    [Fact]
    public void Resolve_TwoMonitors_BothResolveIndependently()
    {
        var idA = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A") };

        var result = LayoutResolver.Resolve(Input("$layout{A1} $layout2{A1}", 1, catalog, new[] { idA }));

        Assert.Equal(2, result.Plan.Monitors.Count);
        Assert.Contains(result.Plan.Monitors, m => m.Monitor == 1);
        Assert.Contains(result.Plan.Monitors, m => m.Monitor == 2);
    }

    // --- Buyer-review defect #5: per-monitor fingerprint, proven with an ACTUAL sibling reorder ---

    [Fact]
    public void Resolve_EditingOneMonitorsToken_WithASimultaneousCameraReorder_NeverRepinsTheOtherMonitor()
    {
        // Stronger than Resolve_MalformedSiblingToken_DoesNotUnpinTheOtherMonitor above (which never
        // actually changes the camera order on the SAME tick monitor 2's token changes, so it can't
        // by itself distinguish "monitor 1 was trusted verbatim" from "monitor 1 was freshly
        // re-resolved and just happened to land on the same camera"). This test changes BOTH monitor
        // 2's token AND the enabled-camera order in the exact same resolve — under the pre-fix GLOBAL
        // fingerprint, monitor 2's edit would have changed the ONE fingerprint monitor 1 was compared
        // against too, silently re-deriving monitor 1's ordinal against the NEW (reordered) camera
        // list. Per-monitor fingerprints make that structurally impossible.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };

        var first = LayoutResolver.Resolve(Input("$layout{A1} $layout2{A1}", 1, catalog, new[] { idA, idB }));
        Assert.Equal(idA, SingleMember(first, 1).CameraId);
        Assert.Equal(idA, SingleMember(first, 2).CameraId);

        // Monitor 2's token text changes (still valid — A1 -> A2) AND the camera order flips, in the
        // SAME resolve.
        var second = LayoutResolver.Resolve(Input("$layout{A1} $layout2{A2}", 1, catalog, new[] { idB, idA }, persistedState: first.NewState));

        // Monitor 1's token text is byte-identical to `first` — its own fingerprint is therefore
        // unchanged, so rule 6a trusts its persisted pin (idA) verbatim, completely untouched by
        // monitor 2's edit or the reorder.
        Assert.Equal(idA, SingleMember(second, 1).CameraId);
        // Monitor 2 DID get new intent (A1 -> A2) — a fresh resolve against the CURRENT (reordered)
        // camera list is correct for it specifically: ordinal 2 now means idA.
        Assert.Equal(idA, SingleMember(second, 2).CameraId);
    }

    // --- Buyer-review defect #4: an alias retargeted in CameraBindings forces a fresh re-pin ---

    [Fact]
    public void Resolve_AliasRetargetedInCameraBindings_RePinsToTheNewCamera_EvenWithIdenticalTokenText()
    {
        var oldCameraId = Guid.NewGuid();
        var newCameraId = Guid.NewGuid();
        var catalog = new[] { Cam(oldCameraId, "Old Camera"), Cam(newCameraId, "New Camera") };
        var bindingsBefore = new Dictionary<string, Guid> { ["front-gate"] = oldCameraId };

        var first = LayoutResolver.Resolve(Input("$layout{A@front-gate}", 1, catalog, Array.Empty<Guid>(), bindingsBefore));
        Assert.Equal(oldCameraId, SingleMember(first, 1).CameraId);

        // Token text is BYTE-IDENTICAL — only the CameraBindings entry it references changed.
        var bindingsAfter = new Dictionary<string, Guid> { ["front-gate"] = newCameraId };
        var second = LayoutResolver.Resolve(Input("$layout{A@front-gate}", 1, catalog, Array.Empty<Guid>(), bindingsAfter, persistedState: first.NewState));

        // Pre-fix, this would have stayed silently pinned to oldCameraId forever (alias members
        // aren't ordinals, so they were never subject to any re-derivation rule at all under the old
        // global-text-only fingerprint).
        Assert.Equal(newCameraId, SingleMember(second, 1).CameraId);
    }

    [Fact]
    public void Resolve_UnrelatedCameraBindingsEdit_DoesNotDisturbAnAlreadyPinnedAlias()
    {
        var cameraId = Guid.NewGuid();
        var catalog = new[] { Cam(cameraId, "Front Gate") };
        var bindingsBefore = new Dictionary<string, Guid> { ["front-gate"] = cameraId };

        var first = LayoutResolver.Resolve(Input("$layout{A@front-gate}", 1, catalog, Array.Empty<Guid>(), bindingsBefore));
        Assert.Equal(cameraId, SingleMember(first, 1).CameraId);

        // A DIFFERENT alias is added — "front-gate" itself is untouched.
        var bindingsAfter = new Dictionary<string, Guid> { ["front-gate"] = cameraId, ["back-door"] = Guid.NewGuid() };
        var second = LayoutResolver.Resolve(Input("$layout{A@front-gate}", 1, catalog, Array.Empty<Guid>(), bindingsAfter, persistedState: first.NewState));

        Assert.Equal(cameraId, SingleMember(second, 1).CameraId);
    }

    // --- Buyer-review defect #7: a RecordingServers[] selection change forces a fresh re-pin ---

    [Fact]
    public void Resolve_RecorderSelectionChanges_RePinsOrdinalsFresh_EvenWithIdenticalTokenTextAndCameraOrder()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };
        var ordered = new[] { idA, idB };
        var recorderSetBefore = new[] { Guid.NewGuid() };
        var recorderSetAfter = new[] { Guid.NewGuid() }; // a DIFFERENT recorder now backs the same ordinal

        var first = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, ordered, recorderIds: recorderSetBefore));
        Assert.Equal(idA, SingleMember(first, 1).CameraId);

        // Token text AND camera order are byte-identical — only the recorder selection changed
        // (e.g. RecordingServers[] was edited to point at a different recording server).
        var second = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, ordered, recorderIds: recorderSetAfter, persistedState: first.NewState));

        // Pre-fix, this stayed silently pinned to whatever camera the OLD recorder set's ordinal 1
        // meant — the review's exact "UNAVAILABLE cells until the state file is deleted or token
        // text changed" defect. A fresh re-pin (landing on the same idA here, since the CATALOG
        // itself didn't change, only its selection identity) proves the fingerprint moved.
        Assert.Equal(idA, SingleMember(second, 1).CameraId);
        Assert.NotEqual(first.NewState.MonitorFingerprints["1"], second.NewState.MonitorFingerprints["1"]);
    }

    [Fact]
    public void Resolve_RecorderSelectionUnchanged_TrustsThePinVerbatim()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };
        var recorderIds = new[] { Guid.NewGuid() };

        var first = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA, idB }, recorderIds: recorderIds));
        Assert.Equal(idA, SingleMember(first, 1).CameraId);

        // Catalog reorders, but the token text, bindings, AND recorder selection are all unchanged.
        var second = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idB, idA }, recorderIds: recorderIds, persistedState: first.NewState));

        Assert.Equal(idA, SingleMember(second, 1).CameraId); // still trusted verbatim, per F3's core rule
    }

    // --- Buyer-review defects #4/#5/#7: intentional simplification — orphaned entries are dropped ---

    [Fact]
    public void Resolve_PersistedMonitorWithNoCurrentToken_NeverCarriedForward_EvenIfHandEditedIntoTheStateFile()
    {
        // Deliberate behavior change documented on LayoutResolver's class doc comment: the pre-fix
        // GLOBAL fingerprint used to widen "monitors to consider" to every persisted monitor whenever
        // the whole-description hash still matched, as a defensive read for a hand-edited/corrupt
        // state file. Per-monitor fingerprints drop that widening — a monitor with NO current token
        // at all (valid or invalid) is simply never considered, orphaned entry or not.
        var idA = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A") };

        var handEdited = new LayoutStateFile
        {
            SchemaVersion = 1,
            MonitorFingerprints = new Dictionary<string, string> { ["2"] = "whatever-was-there" },
            ResolvedPlan = new Dictionary<string, List<PersistedPage>>
            {
                ["2"] = new List<PersistedPage>
                {
                    new()
                    {
                        Rows = new List<PersistedRow>
                        {
                            new() { Cells = new List<PersistedCell> { new() { Members = new List<PersistedMember> { new() { RefKind = "Ordinal", RefLabel = "1", CameraId = idA } } } } },
                        },
                    },
                },
            },
        };

        // Only monitor 1 has a token; monitor 2 has no current token at all despite the orphaned
        // ResolvedPlan/MonitorFingerprints entries above.
        var result = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA }, persistedState: handEdited));

        Assert.Single(result.Plan.Monitors);
        Assert.Equal(1, result.Plan.Monitors[0].Monitor);
        Assert.False(result.NewState.ResolvedPlan.ContainsKey("2"));
        Assert.False(result.NewState.MonitorFingerprints.ContainsKey("2"));
    }

    // --- Buyer-review defects #4/#5/#7: schema-tolerant migration from a pre-fix state file ---

    [Fact]
    public void Resolve_PersistedPlanWithNoStoredMonitorFingerprint_ReResolvesOnceThenStabilizes()
    {
        // Simulates a layout-state.json written by the OLD global-fingerprint code (ResolvedPlan
        // populated, but MonitorFingerprints entirely absent/empty — exactly what an old file
        // deserializes to, see LayoutStatePersistenceTests' backward-read tests).
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new List<CameraCatalogEntry> { Cam(idA, "Cam A"), Cam(idB, "Cam B") };

        var preMigration = new LayoutStateFile
        {
            SchemaVersion = 1,
            MonitorFingerprints = new Dictionary<string, string>(), // old file — nothing here
            ResolvedPlan = new Dictionary<string, List<PersistedPage>>
            {
                ["1"] = new List<PersistedPage>
                {
                    new()
                    {
                        Rows = new List<PersistedRow>
                        {
                            new() { Cells = new List<PersistedCell> { new() { Members = new List<PersistedMember> { new() { RefKind = "Ordinal", RefLabel = "1", CameraId = idA } } } } },
                        },
                    },
                },
            },
        };

        // First resolve after the upgrade: no stored fingerprint for monitor 1 -> re-resolve once,
        // fresh against the CURRENT camera order (idB first) — NOT a trust-verbatim reuse of idA.
        var (logContent, migrated) = CaptureLoggedLines(() =>
            LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idB, idA }, persistedState: preMigration)));

        Assert.Equal(idB, SingleMember(migrated, 1).CameraId);
        Assert.Contains("[INFO]", logContent);
        Assert.Contains("no per-monitor fingerprint", logContent);
        Assert.True(migrated.NewState.MonitorFingerprints.ContainsKey("1"));

        // Second resolve: the fingerprint is now stored — steady-state trust-verbatim resumes, even
        // though the catalog order changes YET AGAIN.
        var stabilized = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA, idB }, persistedState: migrated.NewState));
        Assert.Equal(idB, SingleMember(stabilized, 1).CameraId); // unchanged — the migrated pin holds
    }

    /// <summary>Runs <paramref name="body"/> with <see cref="LayoutResolver.Logger"/> pointed at a
    /// fresh temp directory's <see cref="GridLookout.Logging.FileLogger"/>, returns the resulting log
    /// file content, and ALWAYS resets <see cref="LayoutResolver.Logger"/> back to null afterward
    /// (try/finally) — same convention as <c>LayoutSpecParserTests.CaptureLoggedWarnings</c>, since
    /// <see cref="LayoutResolver.Logger"/> is a shared static and leaking a non-null value out of one
    /// test would bleed into every test that runs after it.</summary>
    private static (string LogContent, T Result) CaptureLoggedLines<T>(Func<T> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.Layout.Resolver." + Guid.NewGuid());
        try
        {
            var logger = new GridLookout.Logging.FileLogger(dir, GridLookout.Logging.LogLevel.Debug);
            LayoutResolver.Logger = logger;
            var result = body();
            var logPath = Path.Combine(dir, $"gridlookout-{DateTime.Now:yyyyMMdd}.log");
            var content = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
            return (content, result);
        }
        finally
        {
            LayoutResolver.Logger = null;
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

    private static (string LogContent, object? Result) CaptureLoggedLines(Action body) =>
        CaptureLoggedLines<object?>(() =>
        {
            body();
            return null;
        });

    // --- ResolveFromPersistedOnly (round-4 buyer-review fix: pinned carrier authority at
    // boot/recovery) — renders the last-known-good plan with NO current tokens at all, the one
    // situation where a missing token is a temporarily-unreadable carrier Description, not
    // operator intent. ---

    [Fact]
    public void ResolveFromPersistedOnly_NullOrEmptyState_ReturnsEmptyPlan()
    {
        var empty = new Dictionary<string, Guid>();

        Assert.Empty(LayoutResolver.ResolveFromPersistedOnly(null, Array.Empty<CameraCatalogEntry>(), Array.Empty<Guid>(), empty).Monitors);
        Assert.Empty(LayoutResolver.ResolveFromPersistedOnly(new LayoutStateFile(), Array.Empty<CameraCatalogEntry>(), Array.Empty<Guid>(), empty).Monitors);
    }

    [Fact]
    public void ResolveFromPersistedOnly_RendersEveryPersistedMonitor_PinsIntact_NoTokensNeeded()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };
        var ordered = new[] { idA, idB };
        var persisted = LayoutResolver.Resolve(Input("$layout2{A2}$layout1{A1}", 1, catalog, ordered)).NewState;

        var plan = LayoutResolver.ResolveFromPersistedOnly(persisted, catalog, ordered, new Dictionary<string, Guid>());

        // Both monitors render, ordered by monitor number regardless of persisted key order.
        Assert.Equal(new[] { 1, 2 }, plan.Monitors.Select(m => m.Monitor).ToArray());
        Assert.Equal(idA, Assert.Single(Assert.Single(Assert.Single(plan.Monitors[0].Pages).Rows).Cells).Members[0].CameraId);
        Assert.Equal(idB, Assert.Single(Assert.Single(Assert.Single(plan.Monitors[1].Pages).Rows).Cells).Members[0].CameraId);
    }

    [Fact]
    public void ResolveFromPersistedOnly_AvailabilityIsRecomputedAgainstTheLiveCatalog()
    {
        // F3 rule 6e still applies while the carrier is missing: a camera that got disabled shows
        // UNAVAILABLE (recomputed fresh), but the pin itself is untouched.
        var idA = Guid.NewGuid();
        var catalogThen = new[] { Cam(idA, "Cam A") };
        var persisted = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalogThen, new[] { idA })).NewState;

        var catalogNow = new[] { Cam(idA, "Cam A", enabled: false) };
        var plan = LayoutResolver.ResolveFromPersistedOnly(persisted, catalogNow, Array.Empty<Guid>(), new Dictionary<string, Guid>());

        var member = Assert.Single(Assert.Single(Assert.Single(plan.Monitors.Single().Pages).Rows).Cells).Members[0];
        Assert.False(member.Available);
        Assert.Equal(idA, member.CameraId);
        Assert.Contains("disabled", member.UnavailableReason);
    }

    [Fact]
    public void ResolveFromPersistedOnly_PinnedOrdinalSurvivesACatalogReorder()
    {
        // The central F3 pin rule holds on this path too: ordinal A1 was pinned to idA when
        // resolved; a reordered enabled-camera list while the carrier is missing must not re-point
        // it (this path never re-derives, it only re-renders).
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A"), Cam(idB, "Cam B") };
        var persisted = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA, idB })).NewState;

        var plan = LayoutResolver.ResolveFromPersistedOnly(persisted, catalog, new[] { idB, idA }, new Dictionary<string, Guid>());

        Assert.Equal(idA, Assert.Single(Assert.Single(Assert.Single(plan.Monitors.Single().Pages).Rows).Cells).Members[0].CameraId);
    }

    [Fact]
    public void ResolveFromPersistedOnly_NonNumericMonitorKey_IsSkippedNotThrown()
    {
        // Schema-tolerant reading, same as everywhere else this file is consumed: a hand-edited or
        // corrupt key degrades to "that entry doesn't render", never a crash at boot.
        var idA = Guid.NewGuid();
        var catalog = new[] { Cam(idA, "Cam A") };
        var persisted = LayoutResolver.Resolve(Input("$layout{A1}", 1, catalog, new[] { idA })).NewState;
        persisted.ResolvedPlan["bogus"] = persisted.ResolvedPlan["1"];

        var plan = LayoutResolver.ResolveFromPersistedOnly(persisted, catalog, new[] { idA }, new Dictionary<string, Guid>());

        Assert.Equal(1, Assert.Single(plan.Monitors).Monitor);
    }
}
