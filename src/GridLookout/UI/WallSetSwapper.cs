namespace GridLookout.UI;

/// <summary>
/// The build half of F3's transactional wall replacement (point 7) — builds a complete NEW set of
/// <typeparamref name="TForm"/> before the caller touches the OLD set at all. Generic over
/// <typeparamref name="TForm"/> (rather than hardcoded to <c>WallForm</c>) specifically so this can
/// be unit-tested with a lightweight fake instead of a real WinForms <c>Form</c> backed by a live
/// MIP session — constructing a real <c>WallForm</c> needs an STA thread and SDK <c>Item</c>
/// objects neither of which a fast unit test wants to stand up.
///
/// Deliberately does NOT close the old set itself — Program.cs owns that step (wrapped in
/// <c>WallLifetime.BeginRebuild</c>/<c>EndRebuild</c> and the <c>SessionRecoveryInProgress</c>
/// flag, both of which are WallForm-specific and have no business living in a generic helper) so the
/// full "build+show new, THEN close old, THEN persist" ordering the contract requires stays visible
/// as one linear sequence at the call site rather than being split across two layers.
/// </summary>
public static class WallSetSwapper
{
    /// <summary>
    /// Builds <paramref name="count"/> new forms via <paramref name="buildOne"/>(0..count-1), in
    /// order. On success, every form is already built (and, per the contract, already Shown —
    /// <paramref name="buildOne"/> is expected to Show its own form before returning it, exactly
    /// like the pre-F3 <c>ShowWallForms</c> did) and <paramref name="newForms"/> holds the complete
    /// set. On ANY exception from <paramref name="buildOne"/> — at any index, including the first —
    /// every form built so far this call is torn down via <paramref name="disposePartial"/> (best
    /// effort: a disposal failure is swallowed, never allowed to mask the original build failure),
    /// <paramref name="newForms"/> is left EMPTY, and <paramref name="failure"/> carries the
    /// exception. The caller's own (untouched) old set is exactly what keeps running — this method
    /// never sees or touches it.
    /// </summary>
    public static bool TryBuildSet<TForm>(
        int count,
        Func<int, TForm> buildOne,
        Action<TForm> disposePartial,
        out List<TForm> newForms,
        out Exception? failure)
    {
        var built = new List<TForm>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                built.Add(buildOne(i));
            }

            newForms = built;
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            foreach (var partial in built)
            {
                try
                {
                    disposePartial(partial);
                }
                catch
                {
                    // Best-effort teardown of a partially-built form — the ORIGINAL build failure is
                    // what the caller needs to see; a disposal hiccup on top of it must never mask
                    // that (same "never let cleanup hide the real error" rule WallForm.DisposeTiles
                    // already follows for its own tile teardown).
                }
            }

            newForms = new List<TForm>();
            failure = ex;
            return false;
        }
    }
}
