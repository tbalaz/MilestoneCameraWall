using System.Net;
using GridLookout.Logging;
using VideoOS.Platform;

namespace GridLookout.Milestone;

/// <summary>The matched recorder plus everything WallForm needs: its description (for
/// <c>$layout{}</c> parsing) and its sorted enabled-camera list (ordinal order).</summary>
/// <param name="Cameras">ENABLED cameras only, sorted by name then id — UNCHANGED shape/ordering
/// from before F3. This is what auto-layout (<c>Monitors[]</c>, out of F3's scope) and legacy
/// ordinal <c>$layout{}</c> resolution both index into; <c>Program.ComputeSignature</c> also hashes
/// exactly this list, not <see cref="AllCameras"/> — see that method's doc comment for why an
/// enable/disable toggle deliberately still triggers the existing camera-list-changed rebuild path.</param>
/// <param name="AllCameras">F3 addition: EVERY camera under this recorder, enabled or not. Needed so
/// an alias/guid <c>$layout{}</c> reference to a disabled camera resolves to "found, but disabled"
/// (an UNAVAILABLE placeholder) rather than being indistinguishable from "no such camera at all" —
/// see <c>Milestone.CameraInfo.Enabled</c>'s doc comment.</param>
/// <param name="RecorderIds">Buyer-review defects #4/#5/#7 fix: the stable recorder identity (or
/// identities, in multi-recorder mode) backing THIS match right now — one element (the matched
/// recorder's own FQID ObjectId) in single-recorder mode, one per selected recorder in multi mode
/// (see <c>Program.BuildMultiRecorderMatch</c>). <c>Layout.LayoutFingerprint</c> folds this set into
/// its per-monitor hash so a `RecordingServers[]` change (or, in single mode, a re-match against a
/// different physical recorder) is treated as a change of layout identity — the exact "recorder
/// selection is absent from persisted layout identity" gap the review flagged. Deliberately NOT
/// threaded onto <see cref="Milestone.CameraInfo"/> itself — <c>CameraInfo.RecorderId</c> is a
/// SEPARATE, narrower field (empty in single-recorder mode, mirroring
/// <see cref="Milestone.CameraInfo.RecorderName"/>'s own single-mode-blank convention) that only
/// <c>RecorderCatalog.Discover</c> populates, because <c>Monitoring.RecorderHealthAggregator</c>
/// relies on THAT field being blank in single-recorder mode to keep <c>WallHealthState.Recorders</c>
/// empty there — populating a single-recorder id on every <see cref="CameraInfo"/> would silently
/// break that guard.</param>
public sealed record RecorderMatch(
    string Name,
    string HostName,
    string Description,
    IReadOnlyList<CameraInfo> Cameras,
    IReadOnlyList<CameraInfo> AllCameras,
    IReadOnlyList<Guid> RecorderIds);

/// <summary>
/// Finds the recording server matching this host. The item tree under
/// <c>Configuration.Instance.GetItems()</c> nests recording servers behind folder items — a
/// direct-children walk from the root finds nothing — so recorders are collected by a bounded
/// recursive walk: any non-folder item of <c>Kind.Server</c> that is not the root (management
/// server) item itself.
/// Cameras likewise are collected recursively under the recorder item, because they may sit
/// either directly under it or behind Hardware/folder items depending on hierarchy flavor.
/// Strongly-typed <c>HostName</c>/<c>Description</c> come from
/// <c>VideoOS.Platform.ConfigurationItems.RecordingServer(FQID)</c>.
/// </summary>
public static class RecorderLocator
{
    /// <summary>Optional logger — when set (Program.cs does), the item tree is dumped at Debug
    /// level on every locate, which is the only practical way to diagnose hierarchy-shape
    /// differences between XProtect versions in the field.</summary>
    public static FileLogger? Logger { get; set; }

    public static RecorderMatch? Locate(string? nameOverride, out IReadOnlyList<string> candidateNames)
    {
        // SystemDefined explicitly: the parameterless GetItems() returns the USER-defined
        // hierarchy (camera groups, layout groups, video walls) which contains no recording
        // servers at all.
        var roots = Configuration.Instance.GetItems(ItemHierarchy.SystemDefined);
        if (roots is null || roots.Count == 0)
        {
            Logger?.Warning("Configuration.Instance.GetItems() returned no root items.");
            candidateNames = Array.Empty<string>();
            return null;
        }

        var recorderItems = new List<Item>();
        foreach (var root in roots)
        {
            DumpTree(root, 0);
            CollectServers(root, root, recorderItems, depth: 0);
        }

        // Each candidate is shown as "Name @host" — the display name (what --recorder matches
        // first) plus the REGISTERED host address (what automatic self-location compares against
        // the local hostname/FQDN, and what --recorder also accepts). Without the host shown, a
        // no-unique-match card gives no way to see WHY the hostname comparison failed.
        candidateNames = recorderItems
            .Select(i => $"{i.Name} @{GetRecorderHostName(i) ?? "?"}")
            .ToList();
        Logger?.Debug($"Recorder candidates: {(candidateNames.Count == 0 ? "(none)" : string.Join(", ", candidateNames))}");

        Item? matched;

        if (nameOverride is not null && !string.IsNullOrWhiteSpace(nameOverride))
        {
            // T4/CS8602: net48's BCL reference assembly predates C# 8 nullable annotations, so
            // string.IsNullOrWhiteSpace's parameter isn't marked [NotNullWhen(false)] the way it is
            // on modern .NET — the check above alone doesn't narrow nameOverride's nullability here.
            // The explicit `nameOverride is not null` clause is the REAL guard (a plain null-pattern
            // check the compiler always understands, regardless of BCL annotations); everything
            // below this point uses nameOverride only inside this narrowed branch.
            //
            // The override accepts the recorder's display name OR its registered host address
            // (hostname/FQDN/IP, as it appears in the recording server's configuration) — so
            // "--recorder 192.0.2.10" works when the recorder registered by IP. Priority:
            // exact name, then exact host, then short-name-vs-FQDN host equivalence
            // ("rec01" matches an override of "rec01.corp.local" and vice versa — DNS suffix is
            // cosmetics, not identity). The fuzzy tier requires a UNIQUE match: two recorders in
            // different domains sharing a short name stay ambiguous and keep the candidate card.
            matched = recorderItems.FirstOrDefault(i =>
                string.Equals(i.Name, nameOverride, StringComparison.OrdinalIgnoreCase))
                ?? recorderItems.FirstOrDefault(i =>
                string.Equals(GetRecorderHostName(i), nameOverride, StringComparison.OrdinalIgnoreCase));

            if (matched is null)
            {
                var overrideLabel = nameOverride.Split('.')[0];
                var labelMatches = recorderItems.Where(i =>
                {
                    var host = GetRecorderHostName(i);
                    // T4/CS8602: same net48-BCL-annotation gap as above — string.IsNullOrEmpty
                    // doesn't narrow host's nullability here, so use an explicit `is null` check
                    // (which the compiler DOES understand) instead of swapping the guard's meaning.
                    if (host is null || host.Length == 0)
                    {
                        return false;
                    }
                    // Never label-match pure IPs — "192" from "192.0.2.10" is not a hostname label.
                    if (System.Net.IPAddress.TryParse(host, out _) || System.Net.IPAddress.TryParse(nameOverride, out _))
                    {
                        return false;
                    }
                    return string.Equals(host.Split('.')[0], overrideLabel, StringComparison.OrdinalIgnoreCase);
                }).ToList();
                matched = labelMatches.Count == 1 ? labelMatches[0] : null;
            }
        }
        else
        {
            var localHost = Dns.GetHostName();
            string? localFqdn = null;
            try
            {
                localFqdn = Dns.GetHostEntry(localHost).HostName;
            }
            catch
            {
                // FQDN resolution is best-effort (e.g. WORKGROUP box with no DNS suffix) —
                // HostName-only matching still works.
            }

            var matches = recorderItems.Where(item =>
            {
                var host = GetRecorderHostName(item);
                return string.Equals(host, localHost, StringComparison.OrdinalIgnoreCase)
                    || (localFqdn is not null && string.Equals(host, localFqdn, StringComparison.OrdinalIgnoreCase));
            }).ToList();

            // Exactly one match required; zero or multiple -> caller shows the error card with
            // candidateNames.
            matched = matches.Count == 1 ? matches[0] : null;
        }

        if (matched is null)
        {
            return null;
        }

        var cameraItems = new List<Item>();
        CollectCameras(matched, cameraItems, depth: 0);

        // F3: collect EVERY camera (enabled or not) once, then derive both views from that single
        // dedup pass — AllCameras (unordered, disabled included) for LayoutResolver's alias/guid
        // lookups, and Cameras (the pre-F3 enabled-only, name-then-id-sorted "ordinal" list) by
        // filtering it. Two independent GroupBy passes over cameraItems would both be correct, but
        // would also let the two lists silently disagree about WHICH Item object represents a given
        // duplicate-object-id camera if that logic ever drifted between them.
        var allCameras = cameraItems
            // The same camera appears under both the "All cameras" folder and its hardware item —
            // dedup by object id before ordinal ordering.
            .GroupBy(camera => camera.FQID.ObjectId)
            .Select(g => g.First())
            .Select(camera => new CameraInfo(camera.Name, camera.FQID.ObjectId, camera, camera.Enabled))
            .ToList();

        var cameras = allCameras
            .Where(camera => camera.Enabled)
            .OrderBy(camera => camera.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(camera => camera.Id)
            .ToList();

        string hostName = GetRecorderHostName(matched) ?? string.Empty;
        string description = string.Empty;
        try
        {
            description = new VideoOS.Platform.ConfigurationItems.RecordingServer(matched.FQID).Description ?? string.Empty;
        }
        catch (Exception ex)
        {
            Logger?.Warning($"Could not read recorder description for '{matched.Name}' (auto layout will be used): {ex.Message}");
        }

        Logger?.Info($"Matched recorder '{matched.Name}' (host '{hostName}'), {cameras.Count} enabled camera(s), {allCameras.Count} total.");
        return new RecorderMatch(matched.Name, hostName, description, cameras, allCameras, new[] { matched.FQID.ObjectId });
    }

    // F2 (multi-recorder walls): CollectServers/CollectCameras/GetRecorderHostName are `internal`
    // (not `private`) specifically so RecorderCatalog.Discover — the "find EVERY recorder, not just
    // the one this host matches" half of F2 — walks the SAME item tree the SAME way as single-
    // recorder Locate above. Duplicating this recursive, depth-bounded, hierarchy-quirk-tolerant
    // walk instead of sharing it would let a future fix to one path silently drift from the other;
    // see this class's own doc comment for why the walk itself is subtle enough to be worth
    // protecting against that. Locate's own logic/behavior is completely unchanged by this — only
    // the accessibility modifier on these three helpers (still called the exact same way, from the
    // exact same call sites, within this file).
    internal static void CollectServers(Item root, Item current, List<Item> found, int depth)
    {
        if (depth > 4)
        {
            return;
        }

        foreach (var child in SafeChildren(current))
        {
            // Recording-server items carry FolderType=SystemDefined (they act as containers for
            // the "All cameras"/"All hardware" device folders), NOT FolderType.No — so any
            // Kind.Server item BELOW the root is a recorder candidate, regardless of folder type.
            // The root (site/management-server) item is never added because recursion starts at it.
            if (child.FQID.Kind == Kind.Server && child.FQID.ObjectId != root.FQID.ObjectId)
            {
                found.Add(child);
                continue; // cameras under it are collected later, only for the matched recorder
            }

            if (child.FQID.FolderType != FolderType.No)
            {
                CollectServers(root, child, found, depth + 1);
            }
        }
    }

    internal static void CollectCameras(Item current, List<Item> found, int depth)
    {
        if (depth > 4)
        {
            return;
        }

        foreach (var child in SafeChildren(current))
        {
            bool isFolder = child.FQID.FolderType != FolderType.No;
            if (!isFolder && child.FQID.Kind == Kind.Camera)
            {
                // F3: disabled cameras are collected too (not filtered out here as before) — the
                // enabled-only filter now happens once, in Locate, when deriving the Cameras view
                // from AllCameras. Discovery must retain disabled cameras so a stable alias/guid
                // $layout{} reference to one resolves to "found, but disabled" instead of being
                // indistinguishable from "no such camera at all" — see CameraInfo.Enabled.
                found.Add(child);
                continue;
            }

            if (isFolder || child.FQID.Kind == Kind.Hardware)
            {
                CollectCameras(child, found, depth + 1);
            }
        }
    }

    internal static string? GetRecorderHostName(Item recorderItem)
    {
        try
        {
            return new VideoOS.Platform.ConfigurationItems.RecordingServer(recorderItem.FQID).HostName;
        }
        catch
        {
            return null;
        }
    }

    private static List<Item> SafeChildren(Item item)
    {
        try
        {
            return item.GetChildren() ?? new List<Item>();
        }
        catch (Exception ex)
        {
            Logger?.Debug($"GetChildren failed on '{item.Name}': {ex.Message}");
            return new List<Item>();
        }
    }

    private static void DumpTree(Item item, int depth)
    {
        if (Logger is null || depth > 3)
        {
            return;
        }

        Logger.Debug($"{new string(' ', depth * 2)}- '{item.Name}' Kind={KindName(item.FQID.Kind)} Folder={item.FQID.FolderType} Enabled={item.Enabled}");
        foreach (var child in SafeChildren(item))
        {
            DumpTree(child, depth + 1);
        }
    }

    private static string KindName(Guid kind)
    {
        if (kind == Kind.Server) return "Server";
        if (kind == Kind.Camera) return "Camera";
        if (kind == Kind.Hardware) return "Hardware";
        if (kind == Kind.Folder) return "Folder";
        return kind.ToString();
    }
}
