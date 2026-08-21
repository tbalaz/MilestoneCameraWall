using GridLookout.Layout;
using GridLookout.Milestone;
using Xunit;

namespace GridLookout.Tests.Milestone;

/// <summary>
/// Covers F2 (multi-recorder walls)'s pure/SDK-free half of <see cref="RecorderCatalog"/> —
/// <see cref="RecorderCatalog.Discover"/> itself needs a live MIP session and is not covered here
/// (same "constructing a real thing needs an STA thread / live session" boundary
/// <c>WallSetSwapperTests</c> documents for <c>WallForm</c>). <see cref="VideoOS.Platform.Item"/> IS
/// safely constructible in a unit test, though (a plain object, no live connection needed) — camera
/// fixtures below use it directly rather than a fake, exactly the shape
/// <see cref="RecorderCatalog.Discover"/> itself produces.
/// </summary>
public class RecorderCatalogTests
{
    private static CameraInfo Camera(string name, bool enabled, string recorderName) =>
        new(name, Guid.NewGuid(), new VideoOS.Platform.Item { Name = name, Enabled = enabled }, enabled, recorderName);

    private static RecorderDescriptor Descriptor(string name, Guid id, string hostName, params CameraInfo[] cameras) =>
        new(name, id, hostName, Description: string.Empty, cameras);

    // --- IsMultiRecorderMode: F2 point 2 selection precedence ------------------------------------

    [Theory]
    [InlineData(null, 0, false)]                 // no --recorder, no RecordingServers -> single mode
    [InlineData(null, 2, true)]                   // no --recorder, RecordingServers configured -> multi mode
    [InlineData("", 2, true)]                     // blank --recorder counts as "not passed"
    [InlineData("   ", 2, true)]                  // whitespace-only --recorder counts as "not passed"
    [InlineData("rec01", 2, false)]                // --recorder ALWAYS forces single-recorder mode,
                                                    // even with RecordingServers[] configured — highest
                                                    // precedence tier per WallConfig.RecordingServers.
    [InlineData("rec01", 0, false)]
    public void IsMultiRecorderMode_FollowsPrecedence(string? recorderArg, int recordingServersCount, bool expected)
    {
        Assert.Equal(expected, RecorderCatalog.IsMultiRecorderMode(recorderArg, recordingServersCount));
    }

    // --- ShouldPollLiveDescriptions: FIX 4 — skip the poll entirely when the carrier is irrelevant --

    [Theory]
    [InlineData(false, "", true)]                  // single mode: always polls, Layout is irrelevant to it
    [InlineData(false, "$layout{A@front-gate}", true)] // single mode: still polls even with Layout set
    [InlineData(true, "", true)]                   // multi mode, blank Layout: carrier IS the source -> poll
    [InlineData(true, "   ", true)]                // whitespace-only Layout treated as blank -> poll
    [InlineData(true, "$layout{A@front-gate}", false)] // multi mode, non-blank Layout: carrier irrelevant -> skip
    public void ShouldPollLiveDescriptions_FollowsCarrierRelevance(bool multiRecorderMode, string configLayout, bool expected)
    {
        Assert.Equal(expected, RecorderCatalog.ShouldPollLiveDescriptions(multiRecorderMode, configLayout));
    }

    // --- ValidateSelectors: static RecordingServers[] shape validation ---------------------------

    [Fact]
    public void ValidateSelectors_IdOnly_Kept()
    {
        var id = Guid.NewGuid();
        var result = RecorderCatalog.ValidateSelectors(new[] { new RawRecordingServerEntry(id.ToString(), string.Empty) }, warn: null);

        var selector = Assert.Single(result);
        Assert.True(selector.ById);
        Assert.Equal(id, selector.Id);
    }

    [Fact]
    public void ValidateSelectors_HostNameOnly_Kept()
    {
        var result = RecorderCatalog.ValidateSelectors(new[] { new RawRecordingServerEntry(string.Empty, "rec02.internal") }, warn: null);

        var selector = Assert.Single(result);
        Assert.False(selector.ById);
        Assert.Equal("rec02.internal", selector.HostName);
    }

    [Fact]
    public void ValidateSelectors_BothIdAndHostName_EntryDropped_Warned()
    {
        var warnings = new List<string>();
        var result = RecorderCatalog.ValidateSelectors(
            new[] { new RawRecordingServerEntry(Guid.NewGuid().ToString(), "rec02.internal") }, warnings.Add);

        Assert.Empty(result);
        Assert.Single(warnings);
        Assert.Contains("both", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSelectors_NeitherIdNorHostName_EntryDropped_Warned()
    {
        var warnings = new List<string>();
        var result = RecorderCatalog.ValidateSelectors(new[] { new RawRecordingServerEntry(string.Empty, string.Empty) }, warnings.Add);

        Assert.Empty(result);
        Assert.Single(warnings);
        Assert.Contains("neither", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSelectors_UnparseableGuid_EntryDropped_Warned()
    {
        var warnings = new List<string>();
        var result = RecorderCatalog.ValidateSelectors(new[] { new RawRecordingServerEntry("not-a-guid", string.Empty) }, warnings.Add);

        Assert.Empty(result);
        Assert.Single(warnings);
    }

    [Fact]
    public void ValidateSelectors_DuplicateId_FirstWins_SecondWarned()
    {
        var id = Guid.NewGuid();
        var warnings = new List<string>();
        var result = RecorderCatalog.ValidateSelectors(
            new[]
            {
                new RawRecordingServerEntry(id.ToString(), string.Empty),
                new RawRecordingServerEntry(id.ToString(), string.Empty),
            }, warnings.Add);

        Assert.Single(result);
        Assert.Single(warnings);
        Assert.Contains("duplicate", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSelectors_DuplicateHostNameCaseInsensitive_FirstWins_SecondWarned()
    {
        var warnings = new List<string>();
        var result = RecorderCatalog.ValidateSelectors(
            new[]
            {
                new RawRecordingServerEntry(string.Empty, "rec02.internal"),
                new RawRecordingServerEntry(string.Empty, "REC02.INTERNAL"),
            }, warnings.Add);

        Assert.Single(result);
        Assert.Equal("rec02.internal", result[0].HostName);
        Assert.Single(warnings);
    }

    [Fact]
    public void ValidateSelectors_OneBadEntryAmongGoodOnes_OnlyTheBadOneIsDropped()
    {
        var goodId = Guid.NewGuid();
        var warnings = new List<string>();
        var result = RecorderCatalog.ValidateSelectors(
            new[]
            {
                new RawRecordingServerEntry(goodId.ToString(), string.Empty),
                new RawRecordingServerEntry("bogus", string.Empty),
            }, warnings.Add);

        Assert.Single(result);
        Assert.Equal(goodId, result[0].Id);
        Assert.Single(warnings);
    }

    // --- Select: dynamic, catalog-aware matching --------------------------------------------------

    [Fact]
    public void Select_MatchesById()
    {
        var id = Guid.NewGuid();
        var catalog = new[] { Descriptor("rec01", id, "rec01.internal") };
        var selectors = new[] { new RecordingServerSelector(id, string.Empty) };

        var result = RecorderCatalog.Select(selectors, catalog);

        Assert.Single(result.Selected);
        Assert.Equal("rec01", result.Selected[0].Name);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Select_MatchesByHostName_CaseInsensitive()
    {
        var catalog = new[] { Descriptor("rec01", Guid.NewGuid(), "rec01.internal") };
        var selectors = new[] { new RecordingServerSelector(Guid.Empty, "REC01.INTERNAL") };

        var result = RecorderCatalog.Select(selectors, catalog);

        Assert.Single(result.Selected);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Select_NoMatch_NotFatal_RecordedAsProblem_RestStillSelected()
    {
        var realId = Guid.NewGuid();
        var catalog = new[] { Descriptor("rec01", realId, "rec01.internal") };
        var selectors = new[]
        {
            new RecordingServerSelector(Guid.NewGuid(), string.Empty), // matches nothing
            new RecordingServerSelector(realId, string.Empty),
        };

        var result = RecorderCatalog.Select(selectors, catalog);

        Assert.Single(result.Selected);
        Assert.Equal("rec01", result.Selected[0].Name);
        Assert.Single(result.Problems);
        Assert.Contains("matched no recorder", result.Problems[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_TwoSelectorsResolveToSameRecorder_SecondIsDuplicateProblem_OnlyOneCopySelected()
    {
        var id = Guid.NewGuid();
        var catalog = new[] { Descriptor("rec01", id, "rec01.internal") };
        var selectors = new[]
        {
            new RecordingServerSelector(id, string.Empty),
            new RecordingServerSelector(Guid.Empty, "rec01.internal"), // same recorder, by host this time
        };

        var result = RecorderCatalog.Select(selectors, catalog);

        Assert.Single(result.Selected);
        Assert.Single(result.Problems);
        Assert.Contains("duplicate", result.Problems[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_EmptySelectors_SelectsNothing_NoProblems()
    {
        var catalog = new[] { Descriptor("rec01", Guid.NewGuid(), "rec01.internal") };

        var result = RecorderCatalog.Select(Array.Empty<RecordingServerSelector>(), catalog);

        Assert.Empty(result.Selected);
        Assert.Empty(result.Problems);
    }

    // --- MergeCameras: merged-ordinal resolution order + dedup -------------------------------------

    [Fact]
    public void MergeCameras_SortsEnabledCamerasByRecorderNameThenCameraName()
    {
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal",
            Camera("Zebra Cam", enabled: true, recorderName: "Recorder A"),
            Camera("Alpha Cam", enabled: true, recorderName: "Recorder A"));
        var recB = Descriptor("Recorder B", Guid.NewGuid(), "b.internal",
            Camera("Beta Cam", enabled: true, recorderName: "Recorder B"));

        var merged = RecorderCatalog.MergeCameras(new[] { recA, recB }, warn: null);

        // Sorted by "RecorderName / CameraName" — Recorder A's cameras (alphabetized within it)
        // before Recorder B's, NOT a flat name-only sort (which would put "Alpha" first overall).
        Assert.Equal(new[] { "Alpha Cam", "Zebra Cam", "Beta Cam" }, merged.EnabledCameras.Select(c => c.Name));
    }

    [Fact]
    public void MergeCameras_ExcludesDisabledCamerasFromEnabledView_ButKeepsThemInAllCameras()
    {
        var rec = Descriptor("Recorder A", Guid.NewGuid(), "a.internal",
            Camera("Live Cam", enabled: true, recorderName: "Recorder A"),
            Camera("Disabled Cam", enabled: false, recorderName: "Recorder A"));

        var merged = RecorderCatalog.MergeCameras(new[] { rec }, warn: null);

        Assert.Equal(2, merged.AllCameras.Count);
        Assert.Single(merged.EnabledCameras);
        Assert.Equal("Live Cam", merged.EnabledCameras[0].Name);
    }

    [Fact]
    public void MergeCameras_DuplicateCameraIdAcrossRecorders_KeepsFirstOnly_Warns()
    {
        var sharedId = Guid.NewGuid();
        var camA = new CameraInfo("Cam", sharedId, new VideoOS.Platform.Item { Name = "Cam", Enabled = true }, true, "Recorder A");
        var camB = camA with { RecorderName = "Recorder B" };

        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal", camA);
        var recB = Descriptor("Recorder B", Guid.NewGuid(), "b.internal", camB);

        var warnings = new List<string>();
        var merged = RecorderCatalog.MergeCameras(new[] { recA, recB }, warnings.Add);

        Assert.Single(merged.AllCameras);
        Assert.Equal("Recorder A", merged.AllCameras[0].RecorderName);
        Assert.Single(warnings);
    }

    // --- ComputeSelectionSignature: F2 point 6 rebuild-trigger signature ---------------------------

    [Fact]
    public void ComputeSelectionSignature_SameInputs_SameSignature()
    {
        var id = Guid.NewGuid();
        var camId = Guid.NewGuid();
        var rec = Descriptor("Recorder A", id, "a.internal",
            new CameraInfo("Cam", camId, new VideoOS.Platform.Item { Name = "Cam", Enabled = true }, true, "Recorder A"));

        var a = RecorderCatalog.ComputeSelectionSignature(new[] { rec }, "layout-text", "");
        var b = RecorderCatalog.ComputeSelectionSignature(new[] { rec }, "layout-text", "");

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeSelectionSignature_CameraEnabledToggle_ChangesSignature()
    {
        var id = Guid.NewGuid();
        var camId = Guid.NewGuid();
        var enabled = Descriptor("Recorder A", id, "a.internal",
            new CameraInfo("Cam", camId, new VideoOS.Platform.Item { Name = "Cam", Enabled = true }, true, "Recorder A"));
        var disabled = Descriptor("Recorder A", id, "a.internal",
            new CameraInfo("Cam", camId, new VideoOS.Platform.Item { Name = "Cam", Enabled = false }, false, "Recorder A"));

        var sigEnabled = RecorderCatalog.ComputeSelectionSignature(new[] { enabled }, "layout-text", "");
        var sigDisabled = RecorderCatalog.ComputeSelectionSignature(new[] { disabled }, "layout-text", "");

        Assert.NotEqual(sigEnabled, sigDisabled);
    }

    [Fact]
    public void ComputeSelectionSignature_LayoutStringChange_ChangesSignature()
    {
        var rec = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");

        var sigA = RecorderCatalog.ComputeSelectionSignature(new[] { rec }, "$layout{A1}", "");
        var sigB = RecorderCatalog.ComputeSelectionSignature(new[] { rec }, "$layout{A2}", "");

        Assert.NotEqual(sigA, sigB);
    }

    [Fact]
    public void ComputeSelectionSignature_RecorderEnumerationOrder_DoesNotAffectSignature()
    {
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");
        var recB = Descriptor("Recorder B", Guid.NewGuid(), "b.internal");

        var sig1 = RecorderCatalog.ComputeSelectionSignature(new[] { recA, recB }, "layout-text", "");
        var sig2 = RecorderCatalog.ComputeSelectionSignature(new[] { recB, recA }, "layout-text", "");

        Assert.Equal(sig1, sig2);
    }

    [Fact]
    public void ComputeSelectionSignature_SelectedRecorderSetChange_ChangesSignature()
    {
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");
        var recB = Descriptor("Recorder B", Guid.NewGuid(), "b.internal");

        var sigOne = RecorderCatalog.ComputeSelectionSignature(new[] { recA }, "layout-text", "");
        var sigTwo = RecorderCatalog.ComputeSelectionSignature(new[] { recA, recB }, "layout-text", "");

        Assert.NotEqual(sigOne, sigTwo);
    }

    [Fact]
    public void ComputeSelectionSignature_LayoutCarrierDescriptionChange_ChangesSignature()
    {
        // Feature (layout-carrier recorder): the whole point of this term — see
        // ComputeSelectionSignature's own doc comment for why it is deliberately the OPPOSITE of
        // configLayout's defensive-constant role right next to it. Same recorders, same camera set,
        // same config Layout string — only the carrier's Description text differs.
        var rec = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");

        var sigA = RecorderCatalog.ComputeSelectionSignature(new[] { rec }, "", "$layout{A1}");
        var sigB = RecorderCatalog.ComputeSelectionSignature(new[] { rec }, "", "$layout{A2}");

        Assert.NotEqual(sigA, sigB);
    }

    [Fact]
    public void ComputeSelectionSignature_LayoutCarrierDescriptionUnchanged_SignatureUnchanged()
    {
        var rec = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");

        var sigA = RecorderCatalog.ComputeSelectionSignature(new[] { rec }, "", "$layout{A1}");
        var sigB = RecorderCatalog.ComputeSelectionSignature(new[] { rec }, "", "$layout{A1}");

        Assert.Equal(sigA, sigB);
    }

    // --- ResolveLayoutCarrier: layout-carrier recorder feature — which recorder supplies tokens ----

    [Fact]
    public void ResolveLayoutCarrier_Unset_FallsBackToFirstSelected_NoProblem()
    {
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");
        var recB = Descriptor("Recorder B", Guid.NewGuid(), "b.internal");

        var result = RecorderCatalog.ResolveLayoutCarrier(string.Empty, new[] { recA, recB });

        Assert.Equal(recA, result.Carrier);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void ResolveLayoutCarrier_MatchesById()
    {
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");
        var recB = Descriptor("Recorder B", Guid.NewGuid(), "b.internal");

        var result = RecorderCatalog.ResolveLayoutCarrier(recB.Id.ToString(), new[] { recA, recB });

        Assert.Equal(recB, result.Carrier);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void ResolveLayoutCarrier_MatchesByNameCaseInsensitive()
    {
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");
        var recB = Descriptor("Recorder B", Guid.NewGuid(), "b.internal");

        var result = RecorderCatalog.ResolveLayoutCarrier("recorder b", new[] { recA, recB });

        Assert.Equal(recB, result.Carrier);
        Assert.Null(result.Problem);
    }

    // FIX 2 (pinned carrier authority): an EXPLICIT LayoutRecorder that currently matches nothing (or
    // matches ambiguously) is PINNED, not floating — Carrier comes back null (never another
    // recorder's Description is adopted) and Problem explains why. Auto-carrier mode (blank config,
    // covered by ResolveLayoutCarrier_Unset_FallsBackToFirstSelected_NoProblem above) is UNCHANGED —
    // it still floats to selected[0] unconditionally, since the operator never named an authority to
    // begin with.

    [Fact]
    public void ResolveLayoutCarrier_ExplicitConfig_NoMatch_PinnedMissing_NoDescriptionAdoptedFromAnotherRecorder()
    {
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");
        var recB = Descriptor("Recorder B", Guid.NewGuid(), "b.internal");

        var result = RecorderCatalog.ResolveLayoutCarrier("Recorder Z", new[] { recA, recB });

        // Pre-fix, this fell back to recA (an unrelated recorder's Description would be adopted as
        // the layout source) — FIX 2 pins instead: Carrier is null, nothing is adopted.
        Assert.Null(result.Carrier);
        Assert.NotEqual(recA, result.Carrier);
        Assert.NotEqual(recB, result.Carrier);
        Assert.NotNull(result.Problem);
        Assert.Contains("Recorder Z", result.Problem);
        Assert.Contains("PINNED", result.Problem);
    }

    [Fact]
    public void ResolveLayoutCarrier_NamesARealRecorderNotInCurrentSelection_PinnedMissing()
    {
        // A recorder that's real (has a name an operator might plausibly type) but simply isn't part
        // of THIS wall's RecordingServers[] selection right now.
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");
        var notSelected = Descriptor("Recorder Elsewhere", Guid.NewGuid(), "elsewhere.internal");

        var result = RecorderCatalog.ResolveLayoutCarrier("Recorder Elsewhere", new[] { recA });

        Assert.Null(result.Carrier);
        Assert.NotEqual(recA, result.Carrier);
        Assert.NotEqual(notSelected, result.Carrier);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void ResolveLayoutCarrier_ExplicitConfig_AmbiguousNameAcrossTwoSelectedRecorders_PinnedMissing()
    {
        // Two selected recorders share the configured display name (Management Client does not
        // enforce uniqueness) — the same collision FIX 1 closes for the description overlay, one
        // layer up. Picking either one silently (the pre-fix FirstOrDefault) would be exactly the
        // wrong-recorder-wins defect this feature closes, so this is treated as pinned-missing too.
        var recA = Descriptor("Recorder", Guid.NewGuid(), "a.internal");
        var recB = Descriptor("Recorder", Guid.NewGuid(), "b.internal");

        var result = RecorderCatalog.ResolveLayoutCarrier("Recorder", new[] { recA, recB });

        Assert.Null(result.Carrier);
        Assert.NotNull(result.Problem);
        Assert.Contains("ambiguous", result.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveLayoutCarrier_ExplicitConfig_CarrierReturns_AuthorityResumes()
    {
        // "Carrier returns → authority resumes": the SAME LayoutRecorder value, evaluated again once
        // the named recorder is back among the currently selected recorders, resolves to it directly
        // — no restart, no separate "re-arm" step needed; ResolveLayoutCarrier is pure/stateless, so
        // simply calling it again with the recorder present is the whole story.
        var recA = Descriptor("Recorder A", Guid.NewGuid(), "a.internal");
        var carrier = Descriptor("Recorder B", Guid.NewGuid(), "b.internal");

        var whileMissing = RecorderCatalog.ResolveLayoutCarrier("Recorder B", new[] { recA });
        Assert.Null(whileMissing.Carrier);
        Assert.NotNull(whileMissing.Problem);

        var afterReturn = RecorderCatalog.ResolveLayoutCarrier("Recorder B", new[] { recA, carrier });
        Assert.Equal(carrier, afterReturn.Carrier);
        Assert.Null(afterReturn.Problem);
    }

    [Fact]
    public void ResolveLayoutCarrier_EmptySelected_ReturnsNullCarrier_NoProblem()
    {
        // Defensive only — every production caller already checked selection.Selected.Count > 0.
        var result = RecorderCatalog.ResolveLayoutCarrier("anything", Array.Empty<RecorderDescriptor>());

        Assert.Null(result.Carrier);
        Assert.Null(result.Problem);
    }

    // --- ResolveMultiRecorderLayoutSource: layout-source precedence (a)/(b)/(c) --------------------

    [Fact]
    public void ResolveMultiRecorderLayoutSource_NonBlankConfigLayout_WinsEvenWithCarrierTokensPresent()
    {
        var result = RecorderCatalog.ResolveMultiRecorderLayoutSource("$layout{A@front-gate}", "$layout{A1,A2}");

        Assert.Equal("$layout{A@front-gate}", result);
    }

    [Fact]
    public void ResolveMultiRecorderLayoutSource_BlankConfigLayout_UsesCarrierDescription()
    {
        var result = RecorderCatalog.ResolveMultiRecorderLayoutSource(string.Empty, "$layout{A1,A2}");

        Assert.Equal("$layout{A1,A2}", result);
    }

    [Fact]
    public void ResolveMultiRecorderLayoutSource_BothBlank_ReturnsBlank()
    {
        var result = RecorderCatalog.ResolveMultiRecorderLayoutSource(string.Empty, string.Empty);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ResolveMultiRecorderLayoutSource_WhitespaceOnlyConfigLayout_TreatedAsBlank_UsesCarrier()
    {
        var result = RecorderCatalog.ResolveMultiRecorderLayoutSource("   ", "$layout{A1}");

        Assert.Equal("$layout{A1}", result);
    }

    // --- BuildMultiRecorderExportError: exporter fix — clear, actionable, names the selectors ------

    [Fact]
    public void BuildMultiRecorderExportError_ListsEachSelectorLabel_IdAndHostName()
    {
        var id = Guid.NewGuid();
        var selectors = new[]
        {
            new RecordingServerSelector(id, string.Empty),
            new RecordingServerSelector(Guid.Empty, "rec02.internal"),
        };

        var message = RecorderCatalog.BuildMultiRecorderExportError(selectors);

        Assert.Contains("Multi-recorder configuration", message);
        Assert.Contains("--recorder", message);
        Assert.Contains(id.ToString(), message);
        Assert.Contains("rec02.internal", message);
    }

    // --- Layout-carrier feature end-to-end: a malformed CARRIER token inherits rule 6c -------------
    // (last-known-good carry-forward) unchanged, because ResolveMultiRecorderLayoutSource's output
    // feeds the exact same LayoutSpecParser/LayoutResolver pipeline single-recorder mode already
    // uses — provenance-agnostic. This is a thin composition check, not a re-test of rule 6c itself
    // (see LayoutResolverTests for the exhaustive coverage of that rule).

    [Fact]
    public void LayoutCarrierFeature_MalformedCarrierDescription_FallsBackToLastKnownGoodPlan()
    {
        var idA = Guid.NewGuid();
        var catalog = new[] { new CameraCatalogEntry(idA, "Cam A", Enabled: true) };
        var ordered = new[] { idA };

        // First tick: the carrier's Description has a good token — resolves and pins idA.
        var goodSource = RecorderCatalog.ResolveMultiRecorderLayoutSource(configLayout: string.Empty, layoutCarrierDescription: "$layout{A1}");
        var goodTokens = LayoutSpecParser.Parse(goodSource, defaultMonitor: 1);
        var goodResult = LayoutResolver.Resolve(new LayoutResolver.ResolveInput(
            goodTokens, catalog, ordered, new Dictionary<string, Guid>(), PersistedState: null, Array.Empty<Guid>()));
        var monitor1 = Assert.Single(goodResult.Plan.Monitors);
        Assert.Equal(idA, monitor1.Pages[0].Rows[0].Cells[0].Members[0].CameraId);

        // Next tick: someone typos the carrier recorder's Description in Management Client — still
        // routed through the SAME precedence function, still lands on the SAME resolver pipeline.
        var malformedSource = RecorderCatalog.ResolveMultiRecorderLayoutSource(configLayout: string.Empty, layoutCarrierDescription: "$layout{qwerty}");
        var malformedTokens = LayoutSpecParser.Parse(malformedSource, defaultMonitor: 1);
        var malformedResult = LayoutResolver.Resolve(new LayoutResolver.ResolveInput(
            malformedTokens, catalog, ordered, new Dictionary<string, Guid>(), PersistedState: goodResult.NewState, Array.Empty<Guid>()));

        var carriedMonitor = Assert.Single(malformedResult.Plan.Monitors);
        Assert.Equal(idA, carriedMonitor.Pages[0].Rows[0].Cells[0].Members[0].CameraId); // stale but valid
        Assert.Contains("1", malformedResult.NewState.CarriedForwardMonitors);
    }

    // --- ApplyLiveDescriptions + the actual refresh-tick signature path: live-lab bug fix ----------
    // Program.cs's refresh tick, in order: RecorderCatalog.Discover() [SDK, untestable] ->
    // RecorderCatalog.ApplyLiveDescriptions(catalog, session.TryGetRecorderDescriptions()) [SDK REST
    // call untestable, but the overlay itself is pure] -> RecorderCatalog.Select(...) ->
    // RecorderCatalog.ResolveLayoutCarrier(...) (via Program.ResolveLayoutCarrierDescriptionForSignature)
    // -> RecorderCatalog.ComputeSelectionSignature(...). Every step below IS that pure chain, run in
    // that exact order, so this proves the FIX (and reproduces the BUG it fixes) at the composition
    // level the live-lab report demanded — not just ResolveMultiRecorderLayoutSource in isolation.

    [Fact]
    public void RefreshTickSignaturePath_CarrierDescriptionChangedOnlyViaLiveRestOverlay_SignatureChanges()
    {
        var carrierId = Guid.NewGuid();
        // "Boot-time" catalog: RecorderCatalog.Discover() as it would read at process start — the
        // carrier's Description matches what was live on the server at that moment.
        var bootCatalog = new[] { new RecorderDescriptor("CAMWALL-01", carrierId, "camwall01.internal", "$layout{A1,A2,A3}", Array.Empty<CameraInfo>()) };
        var selectors = new[] { new RecordingServerSelector(carrierId, string.Empty) };
        var bootSelection = RecorderCatalog.Select(selectors, bootCatalog);
        var bootCarrier = RecorderCatalog.ResolveLayoutCarrier(layoutRecorderConfig: "CAMWALL-01", bootSelection.Selected);
        var bootSignature = RecorderCatalog.ComputeSelectionSignature(bootSelection.Selected, configLayout: "", bootCarrier.Carrier!.Description);

        // Live-lab scenario: an operator PATCHes the carrier's Description via Management Client
        // mid-session. RecorderCatalog.Discover()'s own SDK-cached read would still return the OLD
        // text (the bug) — simulated here by re-discovering the SAME stale catalog — but the REST
        // poll (session.TryGetRecorderDescriptions(), simulated as this dictionary) DOES see the
        // new token, and ApplyLiveDescriptions overlays it before anything downstream runs. FIX 1:
        // keyed by the recorder's Id, not its name.
        var rediscoveredStaleCatalog = bootCatalog; // SDK cache: unchanged, exactly the reported bug
        var liveRestDescriptions = new Dictionary<Guid, string>
        {
            [carrierId] = "$layout{A1:2x2,A2,A3;-,-,B4,B5;C6,C7,C8,C9}",
        };
        var freshCatalog = RecorderCatalog.ApplyLiveDescriptions(rediscoveredStaleCatalog, liveRestDescriptions);
        var tickSelection = RecorderCatalog.Select(selectors, freshCatalog);
        var tickCarrier = RecorderCatalog.ResolveLayoutCarrier(layoutRecorderConfig: "CAMWALL-01", tickSelection.Selected);
        var tickSignature = RecorderCatalog.ComputeSelectionSignature(tickSelection.Selected, configLayout: "", tickCarrier.Carrier!.Description);

        Assert.NotEqual("$layout{A1,A2,A3}", tickCarrier.Carrier.Description); // overlay actually applied
        Assert.Equal("$layout{A1:2x2,A2,A3;-,-,B4,B5;C6,C7,C8,C9}", tickCarrier.Carrier.Description);
        Assert.NotEqual(bootSignature, tickSignature); // -> Program.cs's refresh tick WOULD rebuild
    }

    [Fact]
    public void RefreshTickSignaturePath_WithoutLiveRestOverlay_StaleCachedDescriptionNeverChangesSignature()
    {
        // Regression guard reproducing the reported bug directly: skip ApplyLiveDescriptions (as the
        // code did before this fix) and feed RecorderCatalog.Discover()'s raw, SDK-cached catalog
        // straight to Select/ResolveLayoutCarrier/ComputeSelectionSignature. Even though the real
        // server-side Description changed, the SDK-cached text (session.ReloadConfiguration() does
        // NOT invalidate it — see MilestoneSession.TryGetRecorderDescriptions's doc comment) never
        // does, so this signature must NOT change — silently, exactly matching the field report
        // ("ZERO lines matching rebuild/refresh/signature").
        var carrierId = Guid.NewGuid();
        var staleCatalog = new[] { new RecorderDescriptor("CAMWALL-01", carrierId, "camwall01.internal", "$layout{A1,A2,A3}", Array.Empty<CameraInfo>()) };
        var selectors = new[] { new RecordingServerSelector(carrierId, string.Empty) };

        var bootSelection = RecorderCatalog.Select(selectors, staleCatalog);
        var bootCarrier = RecorderCatalog.ResolveLayoutCarrier(layoutRecorderConfig: "CAMWALL-01", bootSelection.Selected);
        var bootSignature = RecorderCatalog.ComputeSelectionSignature(bootSelection.Selected, configLayout: "", bootCarrier.Carrier!.Description);

        // "Re-discovery" without the live overlay — RecorderCatalog.Discover() alone, post-PATCH,
        // still returns the pre-PATCH text (this IS the bug: the SDK cache is stale).
        var rediscoveredStaleCatalog = staleCatalog;
        var tickSelection = RecorderCatalog.Select(selectors, rediscoveredStaleCatalog);
        var tickCarrier = RecorderCatalog.ResolveLayoutCarrier(layoutRecorderConfig: "CAMWALL-01", tickSelection.Selected);
        var tickSignature = RecorderCatalog.ComputeSelectionSignature(tickSelection.Selected, configLayout: "", tickCarrier.Carrier!.Description);

        Assert.Equal(bootSignature, tickSignature); // bug reproduced: no overlay -> no detected change
    }

    // --- ApplyLiveDescriptions: the fix's own unit coverage -----------------------------------------
    // FIX 1 (GUID-keyed overlay): keyed by Id, never by Name — see the class doc comment on
    // ApplyLiveDescriptions for why (two recorders can legitimately share a display name).

    [Fact]
    public void ApplyLiveDescriptions_MatchingId_OverwritesDescription()
    {
        var id = Guid.NewGuid();
        var catalog = new[] { new RecorderDescriptor("CAMWALL-01", id, "camwall01.internal", "$layout{A1}", Array.Empty<CameraInfo>()) };
        var live = new Dictionary<Guid, string> { [id] = "$layout{A1,A2}" };

        var result = RecorderCatalog.ApplyLiveDescriptions(catalog, live);

        Assert.Equal("$layout{A1,A2}", Assert.Single(result).Description);
    }

    [Fact]
    public void ApplyLiveDescriptions_NoMatchingIdInLiveDictionary_KeepsSdkCachedDescription()
    {
        var id = Guid.NewGuid();
        var catalog = new[] { new RecorderDescriptor("CAMWALL-01", id, "camwall01.internal", "$layout{A1}", Array.Empty<CameraInfo>()) };
        var live = new Dictionary<Guid, string> { [Guid.NewGuid()] = "$layout{B1}" };

        var result = RecorderCatalog.ApplyLiveDescriptions(catalog, live);

        Assert.Equal("$layout{A1}", Assert.Single(result).Description);
    }

    [Fact]
    public void ApplyLiveDescriptions_NullLiveDescriptions_PollFailed_ReturnsCatalogUnchanged()
    {
        var id = Guid.NewGuid();
        var catalog = new[] { new RecorderDescriptor("CAMWALL-01", id, "camwall01.internal", "$layout{A1}", Array.Empty<CameraInfo>()) };

        var result = RecorderCatalog.ApplyLiveDescriptions(catalog, null);

        Assert.Same(catalog, result);
    }

    [Fact]
    public void ApplyLiveDescriptions_DuplicateDisplayNames_EachRecorderGetsItsOwnDescriptionByGuid()
    {
        // The exact defect FIX 1 closes: two DIFFERENT recorders sharing a display name (Management
        // Client does not enforce uniqueness) each carry a DIFFERENT live Description, and each must
        // keep its own — not have one recorder's text collapse onto the other via a name collision.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var catalog = new[]
        {
            new RecorderDescriptor("Recorder", idA, "a.internal", "$layout{A1}", Array.Empty<CameraInfo>()),
            new RecorderDescriptor("Recorder", idB, "b.internal", "$layout{B1}", Array.Empty<CameraInfo>()),
        };
        var live = new Dictionary<Guid, string>
        {
            [idA] = "$layout{A1,A2}",
            [idB] = "$layout{B1,B2}",
        };

        var result = RecorderCatalog.ApplyLiveDescriptions(catalog, live);

        Assert.Equal("$layout{A1,A2}", result.Single(r => r.Id == idA).Description);
        Assert.Equal("$layout{B1,B2}", result.Single(r => r.Id == idB).Description);
    }
}
