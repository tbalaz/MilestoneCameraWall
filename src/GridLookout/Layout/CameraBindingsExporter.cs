using System.Text;
using System.Text.RegularExpressions;

namespace GridLookout.Layout;

/// <summary>
/// Pure alias-generation logic behind <c>GridLookout.exe --export-camera-bindings</c> (F3 point 9)
/// — takes a snapshot of (camera name, camera id) pairs and produces a ready-to-paste
/// <c>CameraBindings</c> skeleton. Deliberately takes plain tuples rather than
/// <c>Milestone.CameraInfo</c> so this stays SDK-free and testable without a live MIP session — see
/// Program.cs's <c>--export-camera-bindings</c> mode for the one call site that adapts a real
/// <c>RecorderMatch.AllCameras</c> snapshot into this shape.
/// </summary>
public static class CameraBindingsExporter
{
    /// <summary>Builds the alias -&gt; guid-string skeleton, in stable (name-then-id) order, with
    /// collisions between generated aliases resolved by an incrementing numeric suffix
    /// (<c>front-gate</c>, <c>front-gate-2</c>, <c>front-gate-3</c>, ...) so every input camera gets
    /// exactly one entry, never silently dropped for colliding with another camera's name.</summary>
    public static IReadOnlyList<(string Alias, Guid CameraId, string CameraName)> BuildSkeleton(
        IReadOnlyList<(string Name, Guid Id)> cameras)
    {
        var ordered = cameras
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id)
            .ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string Alias, Guid CameraId, string CameraName)>(ordered.Count);

        foreach (var camera in ordered)
        {
            var baseAlias = Kebabify(camera.Name);
            var alias = baseAlias;
            int suffix = 2;
            while (!used.Add(alias))
            {
                alias = $"{baseAlias}-{suffix}";
                suffix++;
            }

            result.Add((alias, camera.Id, camera.Name));
        }

        return result;
    }

    /// <summary>Renders <paramref name="skeleton"/> as the <c>"CameraBindings": { ... }</c> JSON
    /// fragment printed to stdout and written to <c>camera-bindings.generated.json</c> — hand-built
    /// (not <c>System.Text.Json</c>) so each entry can carry an inline <c>// CameraName</c> comment,
    /// which <c>System.Text.Json</c> has no write-side support for (see
    /// <c>WallConfigLoader</c>'s own comment-preservation notes for why this codebase already treats
    /// that as a hard limitation, not an oversight).</summary>
    public static string RenderJson(IReadOnlyList<(string Alias, Guid CameraId, string CameraName)> skeleton)
    {
        var sb = new StringBuilder();
        sb.Append('{').Append(Environment.NewLine);
        sb.Append("  \"CameraBindings\": {").Append(Environment.NewLine);
        for (int i = 0; i < skeleton.Count; i++)
        {
            var (alias, cameraId, cameraName) = skeleton[i];
            var comma = i < skeleton.Count - 1 ? "," : string.Empty;
            sb.Append($"    \"{alias}\": \"{cameraId}\"{comma}  // {EscapeComment(cameraName)}").Append(Environment.NewLine);
        }

        sb.Append("  }").Append(Environment.NewLine);
        sb.Append('}').Append(Environment.NewLine);
        return sb.ToString();
    }

    // Comments are appended after the JSON on the SAME line, so the one character that would break
    // the line-oriented "// to end of line" reading is a literal newline in the camera name itself
    // — strip it defensively (a camera name should never legitimately contain one).
    private static string EscapeComment(string cameraName) => cameraName.Replace('\r', ' ').Replace('\n', ' ');

    // lowercase, non-[a-z0-9] runs collapsed to a single '-', leading/trailing '-' trimmed. Matches
    // LayoutSpecParser's CellMemberKind.Alias character class exactly, so every generated alias is
    // guaranteed to be pasteable straight into a $layout{} token without further editing.
    private static readonly Regex NonAliasRun = new("[^a-z0-9]+", RegexOptions.Compiled);

    private static string Kebabify(string cameraName)
    {
        var lowered = cameraName.ToLowerInvariant();
        var collapsed = NonAliasRun.Replace(lowered, "-").Trim('-');
        return collapsed.Length == 0 ? "camera" : collapsed;
    }
}
