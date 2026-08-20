using System.Security.Cryptography;
using System.Text;

namespace GridLookout.Layout;

/// <summary>
/// Computes the "has THIS MONITOR's layout intent changed" fingerprint <c>LayoutResolver</c> uses to
/// decide, PER MONITOR, whether to reuse a persisted last-known-good plan (F3 rule 6a/6b) or treat
/// the current token as new intent. Buyer-review defects #4/#5/#7 fix — three problems with the
/// pre-fix fingerprint (a single SHA-256 over EVERY monitor's raw token text, and nothing else) are
/// addressed by widening what one monitor's fingerprint depends on:
/// <list type="bullet">
/// <item><b>#5 (sibling repin):</b> a single WHOLE-DESCRIPTION hash meant editing monitor 2's token
/// changed the ONE fingerprint every monitor was compared against, silently re-deriving monitor 1's
/// ordinals too. Computing one fingerprint per monitor, over only THAT monitor's own raw token text,
/// makes an edit to one monitor's token structurally unable to touch another's.</item>
/// <item><b>#4 (stale alias pins):</b> the old hash never looked at <c>CameraBindings</c> at all, so
/// retargeting an alias to a different camera left an already-pinned cell silently on the OLD camera
/// forever (the token TEXT — <c>A@front-gate</c> — never changed, only what it resolves to). Folding
/// in the RESOLVED (alias, camera-id) pair for every alias the monitor's token references means a
/// binding edit changes the fingerprint even though the token text is untouched, which forces a
/// fresh re-pin.</item>
/// <item><b>#7 (recorder-selection identity):</b> the old hash had no idea which recorder(s) an
/// ordinal was resolved against, so changing <c>RecordingServers[]</c> while leaving an ordinal
/// token's TEXT unchanged silently kept reusing camera ids from the OLD recorder set. Folding in the
/// currently-selected recorder id set (<see cref="Milestone.RecorderLocator.RecorderMatch.RecorderIds"/>)
/// means a recorder-selection change is itself a fingerprint change, for every monitor, forcing a
/// fresh re-pin against the new set.</item>
/// </list>
/// Pure/SDK-free.
/// </summary>
public static class LayoutFingerprint
{
    // U+0001 (SOH) — a control character that can never appear in a recorder description (which
    // comes from Management Client's plain-text description field), a CameraBindings alias
    // ([a-z0-9-]+ only), or a Guid.ToString() — used as a field separator so distinct hash inputs can
    // never collide by plain concatenation. Built via (char) cast rather than a source-level escape
    // so the exact byte is unambiguous regardless of editor/encoding/tooling.
    private static readonly string Separator = ((char)1).ToString();

    /// <summary>
    /// SHA-256, lowercase hex, of THIS ONE monitor's layout identity: its raw token text
    /// (<paramref name="tokenResult"/>.RawToken — valid or invalid; a newly-introduced typo is
    /// itself a change of intent, same as before this fix), the RESOLVED (alias, camera-id) pair for
    /// every <see cref="CellMemberKind.Alias"/> member the token references (sorted by alias name so
    /// hash order never depends on write order), and the currently-selected recorder id set (sorted
    /// so selection ORDER never matters, only membership).
    ///
    /// <paramref name="tokenResult"/>.Layout is null for an invalid token — there is nothing to walk
    /// for alias members in that case, which is fine: the raw text alone already captures "this
    /// monitor's current token", and an invalid token was never eligible for rule 6a's
    /// trust-verbatim shortcut anyway (see <see cref="LayoutResolver.Resolve"/>).
    /// </summary>
    public static string ComputeForMonitor(
        TokenParseResult tokenResult,
        IReadOnlyDictionary<string, Guid> cameraBindings,
        IReadOnlyList<Guid> recorderIds)
    {
        var sb = new StringBuilder();
        sb.Append(tokenResult.RawToken);
        sb.Append(Separator);

        if (tokenResult.Layout is not null)
        {
            var aliasNames = tokenResult.Layout.Pages
                .SelectMany(page => page.Rows)
                .SelectMany(row => row)
                .SelectMany(cell => cell.Members)
                .Where(member => member.Kind == CellMemberKind.Alias)
                .Select(member => member.Alias)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase);

            foreach (var alias in aliasNames)
            {
                // Guid.Empty (unbound) is itself a meaningful, hashable value — an alias gaining a
                // binding later is exactly the kind of change this fingerprint must catch.
                cameraBindings.TryGetValue(alias, out var boundId);
                sb.Append(alias);
                sb.Append('=');
                sb.Append(boundId.ToString());
                sb.Append(Separator);
            }
        }

        foreach (var recorderId in recorderIds.Distinct().OrderBy(id => id))
        {
            sb.Append(recorderId.ToString());
            sb.Append(Separator);
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);

        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            hex.Append(b.ToString("x2"));
        }

        return hex.ToString();
    }
}
