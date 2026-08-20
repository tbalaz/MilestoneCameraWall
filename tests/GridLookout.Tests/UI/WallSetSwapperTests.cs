using GridLookout.UI;
using Xunit;

namespace GridLookout.Tests.UI;

/// <summary>
/// Covers <see cref="WallSetSwapper.TryBuildSet{TForm}"/> — the build half of F3 point 7's
/// transactional wall replacement (see Program.cs's <c>RebuildWall</c>, which wraps this with the
/// WinForms-specific "show new, then close old, then persist" sequencing). Exercised here with a
/// lightweight fake "form" (see <see cref="FakeForm"/>) instead of a real <c>WallForm</c> — a real
/// one needs an STA thread and a live MIP session to construct, neither of which a fast unit test
/// wants to stand up; the generic <c>TForm</c> parameter exists specifically to make this seam
/// testable without either.
/// </summary>
public class WallSetSwapperTests
{
    // Plain constructor, not an `init` property — net48 has no in-box System.Runtime.CompilerServices
    // .IsExternalInit, and this test assembly (unlike src/GridLookout, which carries its own
    // internal shim — see PolyfillAttributes.cs there) doesn't need one just for this.
    private sealed class FakeForm
    {
        public FakeForm(int index)
        {
            Index = index;
        }

        public int Index { get; }
        public bool Disposed { get; set; }

        /// <summary>Mirrors <c>WallForm.FormClosed</c> for the lifetime-accounting regression test
        /// below. The real <c>WallForm.CloseInternal()</c> reliably raises <c>FormClosed</c> (it
        /// calls <c>Form.Close()</c>, the same path every other WallForm teardown in this codebase
        /// uses); a bare <c>Form.Dispose()</c> does not raise it the same way — that gap is exactly
        /// the coordinator-found defect this test exists to pin.</summary>
        public event Action? Closed;

        public void CloseInternal() => Closed?.Invoke();

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TryBuildSet_AllSucceed_ReturnsTrueWithFullSetInOrder()
    {
        var built = WallSetSwapper.TryBuildSet(
            count: 3,
            buildOne: i => new FakeForm(i),
            disposePartial: _ => Assert.Fail("disposePartial must not run on a successful build"),
            out var newForms,
            out var failure);

        Assert.True(built);
        Assert.Null(failure);
        Assert.Equal(new[] { 0, 1, 2 }, newForms.Select(f => f.Index));
    }

    [Fact]
    public void TryBuildSet_FailsOnThirdOfFive_DisposesTheThreeAlreadyBuilt_LeavesNewFormsEmpty()
    {
        var disposed = new List<int>();

        var built = WallSetSwapper.TryBuildSet(
            count: 5,
            buildOne: i =>
            {
                if (i == 2)
                {
                    throw new InvalidOperationException("simulated build failure");
                }

                return new FakeForm(i);
            },
            disposePartial: f => disposed.Add(f.Index),
            out var newForms,
            out var failure);

        Assert.False(built);
        Assert.Empty(newForms);
        Assert.NotNull(failure);
        Assert.IsType<InvalidOperationException>(failure);
        // Indices 0 and 1 were built before index 2 threw — both must be torn down; index 2 itself
        // never finished building (buildOne threw before returning it), so it was never added to
        // the built list and disposePartial is never asked to dispose it.
        Assert.Equal(new[] { 0, 1 }, disposed);
    }

    [Fact]
    public void TryBuildSet_FailsOnFirst_DisposesNothing_StillReportsFailure()
    {
        var disposeCalls = 0;

        var built = WallSetSwapper.TryBuildSet<FakeForm>(
            count: 2,
            buildOne: _ => throw new InvalidOperationException("first build fails immediately"),
            disposePartial: _ => disposeCalls++,
            out var newForms,
            out var failure);

        Assert.False(built);
        Assert.Empty(newForms);
        Assert.Equal(0, disposeCalls);
        Assert.NotNull(failure);
    }

    [Fact]
    public void TryBuildSet_DisposePartialThrows_OriginalFailureStillSurfaces()
    {
        // "Never let cleanup hide the real error" — a disposal hiccup on top of an already-failed
        // build must not mask (or replace) the original exception the caller needs to see.
        var built = WallSetSwapper.TryBuildSet(
            count: 2,
            buildOne: i => i == 0 ? new FakeForm(0) : throw new InvalidOperationException("real failure"),
            disposePartial: _ => throw new Exception("disposal itself blows up"),
            out var newForms,
            out var failure);

        Assert.False(built);
        Assert.Empty(newForms);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("real failure", failure!.Message);
    }

    [Fact]
    public void TryBuildSet_FailurePath_KeepsWallLifetimeAccountingBalanced()
    {
        // Regression test for a coordinator-found defect in Program.cs's RebuildWall: buildOne()
        // calls lifetime.NoteOpened() and wires a close notification -> lifetime.NoteClosed() for
        // every form it successfully builds (mirrored here). On a mid-set failure the ALREADY-BUILT
        // partial forms must be torn down through a mechanism that reliably fires that same close
        // notification — Program.cs's disposePartial now calls WallForm.CloseInternal() (which drives
        // Close() -> FormClosed, the same path every other WallForm teardown in this codebase uses).
        // The pre-fix code called Dispose() there instead, which does not raise FormClosed the same
        // way, so NoteOpened() for each partial form was never balanced by a NoteClosed() — after one
        // failed rebuild the "last window closed" exit could never fire again (a zombie process that
        // --health-probe would still report as healthy, since its UI pulse keeps ticking).
        var lifetime = new WallLifetime();

        // Three OLD forms already open from a prior successful rebuild — exactly the refresh-tick
        // scenario the defect report describes. The failure below must never touch their count.
        lifetime.NoteOpened();
        lifetime.NoteOpened();
        lifetime.NoteOpened();

        var lastWindowSignalCount = 0;

        var built = WallSetSwapper.TryBuildSet(
            count: 3,
            buildOne: i =>
            {
                if (i == 1)
                {
                    // "fails at form 2 of 3" (1-based, per the coordinator's report) = index 1 here.
                    throw new InvalidOperationException("simulated build failure on the second form");
                }

                var form = new FakeForm(i);
                lifetime.NoteOpened();
                form.Closed += () =>
                {
                    if (lifetime.NoteClosed())
                    {
                        lastWindowSignalCount++;
                    }
                };
                return form;
            },
            disposePartial: f => f.CloseInternal(), // the FIXED mechanism — mirrors Program.cs post-fix
            out var newForms,
            out var failure);

        Assert.False(built);
        Assert.Empty(newForms);
        Assert.NotNull(failure);

        // The one partial form (index 0) was opened then torn down inside TryBuildSet — its
        // NoteOpened/NoteClosed pair is exactly balanced, so only the 3 OLD forms remain counted, and
        // the failure alone never signals "last window closed" (they're all still up).
        Assert.Equal(3, lifetime.OpenWindows);
        Assert.Equal(0, lastWindowSignalCount);

        // The OLD forms now close for real (simulated directly via NoteClosed(), same as the operator
        // closing each one — not through a FakeForm, since these three represent the pre-existing old
        // wall, not anything TryBuildSet touched). The count must reach EXACTLY zero and NoteClosed()
        // must return true EXACTLY once — proving the failed rebuild above left nothing permanently
        // over-counted (the exact zombie-process failure mode the defect report describes: before the
        // fix, this third call would still return false because the leaked partial-form count meant
        // OpenWindows never reached zero).
        Assert.False(lifetime.NoteClosed());
        Assert.False(lifetime.NoteClosed());
        Assert.True(lifetime.NoteClosed());
        Assert.Equal(0, lifetime.OpenWindows);
    }

    [Fact]
    public void TryBuildSet_ZeroCount_SucceedsWithEmptySet()
    {
        var built = WallSetSwapper.TryBuildSet<FakeForm>(
            count: 0,
            buildOne: _ => throw new InvalidOperationException("must never be called"),
            disposePartial: _ => { },
            out var newForms,
            out var failure);

        Assert.True(built);
        Assert.Empty(newForms);
        Assert.Null(failure);
    }
}
