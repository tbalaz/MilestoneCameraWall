using GridLookout.Layout;
using Xunit;

namespace GridLookout.Tests.Layout;

/// <summary>
/// Buyer-review defects #4/#5/#7 fix: <see cref="LayoutFingerprint"/> went from ONE global hash over
/// every monitor's raw token text to a PER-MONITOR hash over that monitor's own raw token text, the
/// resolved CameraBindings pairs it references, and the currently-selected recorder id set — see
/// that class's own doc comment. These tests exercise <see cref="LayoutFingerprint.ComputeForMonitor"/>
/// directly, one monitor's <see cref="TokenParseResult"/> at a time (sibling-isolation itself — "does
/// editing monitor 2 leave monitor 1's fingerprint untouched" — is proven at the integration level in
/// <c>LayoutResolverTests.Resolve_MalformedSiblingToken_DoesNotUnpinTheOtherMonitor</c> and its
/// stronger reorder-based sibling, since that requires two monitors resolved together).
/// </summary>
public class LayoutFingerprintTests
{
    private static readonly IReadOnlyDictionary<string, Guid> NoBindings = new Dictionary<string, Guid>();
    private static readonly IReadOnlyList<Guid> NoRecorderIds = Array.Empty<Guid>();

    private static TokenParseResult Monitor1Token(string description) =>
        LayoutSpecParser.Parse(description, defaultMonitor: 1).Single(r => r.Monitor == 1);

    // --- Raw token text ---

    [Fact]
    public void ComputeForMonitor_SameTokenText_SameBindingsAndRecorderIds_ProducesSameFingerprint()
    {
        var a = Monitor1Token("$layout{A1,B2}");
        var b = Monitor1Token("$layout{A1,B2}");

        Assert.Equal(
            LayoutFingerprint.ComputeForMonitor(a, NoBindings, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(b, NoBindings, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_DifferentTokenText_ProducesDifferentFingerprint()
    {
        var a = Monitor1Token("$layout{A1,B2}");
        var b = Monitor1Token("$layout{A1,B3}");

        Assert.NotEqual(
            LayoutFingerprint.ComputeForMonitor(a, NoBindings, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(b, NoBindings, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_IgnoresSurroundingOrgTags_OnlyLayoutTokenTextMatters()
    {
        // The whole reason this hashes RawToken, not the raw description: $city{}/$building{} are
        // edited independently (RecorderOrgTaggingService) and must never look like a layout edit.
        var withoutTags = Monitor1Token("$layout{A1,B2}");
        var withTags = Monitor1Token("$city{Zagreb}$building{HQ}$layout{A1,B2}");

        Assert.Equal(
            LayoutFingerprint.ComputeForMonitor(withoutTags, NoBindings, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(withTags, NoBindings, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_InvalidTokenBecomingValid_ChangesFingerprint()
    {
        // A newly-fixed typo (or a newly-introduced one) is itself a change of intent, even though
        // one of the two token sets is Invalid (Layout is null there — see the class doc comment for
        // why that's fine: the raw text alone already captures "this monitor's current token").
        var broken = Monitor1Token("$layout{qwerty}");
        var fixed_ = Monitor1Token("$layout{A1}");

        Assert.NotEqual(
            LayoutFingerprint.ComputeForMonitor(broken, NoBindings, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(fixed_, NoBindings, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_RenamedCameraSameToken_FingerprintUnaffected()
    {
        // Fingerprinting never touches camera identity at all — this is really just re-asserting
        // "same raw token text -> same fingerprint" from the angle LayoutResolver actually cares
        // about: a camera rename changes nothing about the description text itself.
        var before = Monitor1Token("$layout{A1,A2,A3}");
        var after = Monitor1Token("$layout{A1,A2,A3}");

        Assert.Equal(
            LayoutFingerprint.ComputeForMonitor(before, NoBindings, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(after, NoBindings, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_AddingASpanSuffix_ChangesFingerprint()
    {
        // F4 (cell spans): fingerprinting hashes RawToken text unchanged by this feature, so adding
        // ':2x2' to a cell is just more raw-token text — but it's exactly the case F3 rule 6b exists
        // for: LayoutResolver must treat this as new layout intent (re-resolve and re-pin every
        // ordinal fresh) rather than silently reusing the pre-span persisted plan.
        var before = Monitor1Token("$layout{A1}");
        var after = Monitor1Token("$layout{A1:2x2;-,-}");

        Assert.NotEqual(
            LayoutFingerprint.ComputeForMonitor(before, NoBindings, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(after, NoBindings, NoRecorderIds));
    }

    // --- Buyer-review defect #4: resolved CameraBindings pairs the token references ---

    [Fact]
    public void ComputeForMonitor_AliasRetargeted_ChangesFingerprint_EvenWithIdenticalTokenText()
    {
        // The exact defect: token text (A@front-gate) never changes when an admin repoints the
        // alias to a different camera in CameraBindings — only the RESOLVED pair does.
        var token = Monitor1Token("$layout{A@front-gate}");
        var bindingsBefore = new Dictionary<string, Guid> { ["front-gate"] = Guid.NewGuid() };
        var bindingsAfter = new Dictionary<string, Guid> { ["front-gate"] = Guid.NewGuid() };

        Assert.NotEqual(
            LayoutFingerprint.ComputeForMonitor(token, bindingsBefore, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(token, bindingsAfter, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_AliasNewlyBound_ChangesFingerprint()
    {
        // An alias that was unbound (no CameraBindings entry at all) gaining one is also a change —
        // Guid.Empty (unbound) is itself a meaningful, hashable value.
        var token = Monitor1Token("$layout{A@front-gate}");
        var boundLater = new Dictionary<string, Guid> { ["front-gate"] = Guid.NewGuid() };

        Assert.NotEqual(
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(token, boundLater, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_AliasBindingUnchanged_SameFingerprint()
    {
        var token = Monitor1Token("$layout{A@front-gate}");
        var cameraId = Guid.NewGuid();
        var bindings = new Dictionary<string, Guid> { ["front-gate"] = cameraId };

        Assert.Equal(
            LayoutFingerprint.ComputeForMonitor(token, bindings, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(token, bindings, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_OrdinalOnlyToken_UnaffectedByAnyCameraBindingsChange()
    {
        // A purely-ordinal token references no alias at all — CameraBindings changing (for ANY
        // alias, related or not) must never move this monitor's fingerprint.
        var token = Monitor1Token("$layout{A1,B2}");
        var bindingsA = new Dictionary<string, Guid> { ["unrelated"] = Guid.NewGuid() };
        var bindingsB = new Dictionary<string, Guid> { ["unrelated"] = Guid.NewGuid(), ["another"] = Guid.NewGuid() };

        Assert.Equal(
            LayoutFingerprint.ComputeForMonitor(token, bindingsA, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(token, bindingsB, NoRecorderIds));
    }

    [Fact]
    public void ComputeForMonitor_UnrelatedAliasBindingChange_DoesNotAffectAMonitorThatDoesNotReferenceIt()
    {
        var token = Monitor1Token("$layout{A@front-gate}");
        var frontGateId = Guid.NewGuid();
        var bindingsBefore = new Dictionary<string, Guid> { ["front-gate"] = frontGateId };
        var bindingsAfter = new Dictionary<string, Guid> { ["front-gate"] = frontGateId, ["back-door"] = Guid.NewGuid() };

        Assert.Equal(
            LayoutFingerprint.ComputeForMonitor(token, bindingsBefore, NoRecorderIds),
            LayoutFingerprint.ComputeForMonitor(token, bindingsAfter, NoRecorderIds));
    }

    // --- Buyer-review defect #7: currently-selected recorder id set ---

    [Fact]
    public void ComputeForMonitor_RecorderIdSetChanges_ChangesFingerprint_EvenWithIdenticalTokenAndBindings()
    {
        var token = Monitor1Token("$layout{A1}");
        var before = new[] { Guid.NewGuid() };
        var after = new[] { Guid.NewGuid() };

        Assert.NotEqual(
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, before),
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, after));
    }

    [Fact]
    public void ComputeForMonitor_RecorderIdSetGrows_ChangesFingerprint()
    {
        var token = Monitor1Token("$layout{A1}");
        var recorderA = Guid.NewGuid();
        var recorderB = Guid.NewGuid();

        Assert.NotEqual(
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, new[] { recorderA }),
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, new[] { recorderA, recorderB }));
    }

    [Fact]
    public void ComputeForMonitor_RecorderIdSetOrder_DoesNotMatter()
    {
        var token = Monitor1Token("$layout{A1}");
        var recorderA = Guid.NewGuid();
        var recorderB = Guid.NewGuid();

        Assert.Equal(
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, new[] { recorderA, recorderB }),
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, new[] { recorderB, recorderA }));
    }

    [Fact]
    public void ComputeForMonitor_RecorderIdSetUnchanged_SameFingerprint()
    {
        var token = Monitor1Token("$layout{A1}");
        var recorderIds = new[] { Guid.NewGuid() };

        Assert.Equal(
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, recorderIds),
            LayoutFingerprint.ComputeForMonitor(token, NoBindings, recorderIds));
    }
}
