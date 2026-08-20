using System.Text.RegularExpressions;

namespace GridLookout.Layout;

/// <summary>
/// Validates <c>WallConfig.CameraBindings</c> (raw alias -&gt; camera-guid-string config) into a
/// normalized, lookup-ready alias -&gt; <see cref="Guid"/> map. Pure/SDK-free — takes the raw
/// dictionary and a warning sink rather than reading config or logging directly, so it's usable
/// both from <c>Program.cs</c> (real <c>FileLogger</c>) and tests (a collector delegate).
///
/// Today's config shape is deliberately flat (<c>"alias": "guid"</c>) rather than the
/// recorder-qualified <c>{ "RecorderId": ..., "CameraId": ... }</c> shape a future multi-recorder
/// feature (F2) will need — see F3's "keep CameraKey shapes extensible" scope note. A flat string
/// is enough for a single-recorder deployment (today's only supported topology — see
/// RecorderLocator), and F2 can widen this to an object with a "CameraGuid" fallback without
/// breaking any file already on disk, exactly the same additive-JSON-shape discipline the rest of
/// this codebase (e.g. <c>HealthConfig</c>) already follows.
/// </summary>
public static class CameraBindingResolver
{
    // [a-z0-9-]+, case-insensitive — F3's alias naming rule (see LayoutSpecParser's
    // CellMemberKind.Alias doc comment; this is the SAME character class, checked here at config
    // load time so a bad alias is caught once at startup rather than silently never matching any
    // $layout{} token that references it).
    private static readonly Regex AliasPattern = new("^[a-z0-9-]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Resolves <paramref name="rawBindings"/> into alias (lowercased) -&gt; camera id. Each entry
    /// is validated independently — one bad entry never blocks the rest of the file from loading:
    /// <list type="bullet">
    /// <item>Alias key doesn't match <c>[a-z0-9-]+</c> — entry dropped, warned.</item>
    /// <item>Value isn't a parseable GUID (accepts any <see cref="Guid.TryParse(string, out Guid)"/>
    /// form — an operator hand-typing this is more forgiving than the strict 8-4-4-4-12-only
    /// <c>$layout{}</c> grammar the parser enforces) — entry dropped, warned.</item>
    /// <item>Duplicate alias (case-insensitive) — the FIRST occurrence in <paramref name="rawBindings"/>
    /// wins (matches <c>LayoutSpecParser</c>'s own "first wins" convention for duplicate $layout
    /// tokens); every later duplicate is dropped, warned.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<string, Guid> Resolve(IReadOnlyDictionary<string, string>? rawBindings, Action<string>? warn)
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (rawBindings is null || rawBindings.Count == 0)
        {
            return result;
        }

        // net48's KeyValuePair<TKey,TValue> has no Deconstruct (added in netstandard2.1+) — plain
        // .Key/.Value access instead of a `foreach (var (k, v) in ...)` tuple pattern.
        foreach (var entry in rawBindings)
        {
            var rawAlias = entry.Key;
            var rawGuid = entry.Value;
            if (string.IsNullOrWhiteSpace(rawAlias) || !AliasPattern.IsMatch(rawAlias))
            {
                warn?.Invoke($"CameraBindings: alias '{rawAlias}' is not a valid alias (expected [a-z0-9-]+) — entry ignored.");
                continue;
            }

            if (result.ContainsKey(rawAlias))
            {
                warn?.Invoke($"CameraBindings: duplicate alias '{rawAlias}' (case-insensitive) — first occurrence wins, this entry ignored.");
                continue;
            }

            if (!Guid.TryParse(rawGuid, out var cameraId))
            {
                warn?.Invoke($"CameraBindings: alias '{rawAlias}' has an unparseable camera guid ('{rawGuid}') — entry ignored.");
                continue;
            }

            result[rawAlias] = cameraId;
        }

        return result;
    }
}
