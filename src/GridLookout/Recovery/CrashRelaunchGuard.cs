using System.Linq;
using System.Text.Json;

namespace GridLookout.Recovery;

/// <summary>
/// Persists a small crash marker (state dir: <c>crash-relaunch.json</c>) so the E5 fatal-exception
/// auto-relaunch (Program.cs's <c>AppDomain.CurrentDomain.UnhandledException</c> handler) can tell
/// an isolated crash (relaunch, as always) apart from a genuine crash LOOP — something wrong on
/// EVERY single start, where relaunching forever just burns CPU and spams the log with zero chance
/// of ever self-healing.
///
/// WHY THIS EXISTS (T7/R8 fix). Before this type, the fatal handler relaunched unconditionally,
/// every time, forever — the correct behavior for a one-off crash, but a silent infinite
/// crash-restart loop for anything systemic (a corrupt config that always throws during load, a
/// permanently wrong credential, etc.).
///
/// Marker IO never throws — a marker read/write failure must never block the crash handler's own
/// relaunch decision; on any IO failure this degrades to "assume no prior crashes, allow the
/// relaunch" (the behavior before this type existed).
///
/// SLIDING WINDOW, ROLLING MARKER (T5/panel-known-limits fix). The marker holds a rolling list of
/// crash timestamps (capped at <see cref="MaxTrackedCrashes"/> — old entries beyond that are
/// dropped, oldest first). Every call to <see cref="ShouldRelaunch"/> — allowed or refused — counts
/// how many recorded crashes fall within the trailing <see cref="WindowSeconds"/> of the CURRENT
/// crash time (a true sliding window, not anchored to the first crash in some earlier window: a
/// steady crash-every-few-minutes stream can no longer "reset" the count to zero just by outlasting
/// an old anchor point), refuses once that count reaches <see cref="MaxCrashesInWindow"/>, then
/// appends this crash to the list regardless of the decision — so a refused crash still ages out of
/// the window in due course, and <see cref="ShouldRelaunch"/> can resume allowing relaunches once
/// enough time has passed with no NEW crash pushing the trailing count back up.
///
/// There is deliberately no more "clear on success" — a successful recorder match used to wipe the
/// marker outright (see this type's git history), which meant a crash loop that resumed AFTER a
/// match reset to a fresh allowance every time and could relaunch forever. Now a match does nothing
/// to this guard at all: old crash timestamps simply age out of the window on their own, and a
/// crash loop that resumes post-match is caught by the exact same sliding window as a pre-match one.
/// </summary>
public sealed class CrashRelaunchGuard
{
    /// <summary>Crashes at or above this count within the trailing <see cref="WindowSeconds"/> stop
    /// the relaunch — i.e. up to <see cref="MaxCrashesInWindow"/> relaunches are allowed per
    /// trailing window, the NEXT one after that is blocked.</summary>
    public const int MaxCrashesInWindow = 5;

    /// <summary>Width, in seconds, of the trailing sliding window each <see cref="ShouldRelaunch"/>
    /// call measures back from ITS OWN crash time — not anchored to the first crash ever recorded.
    /// See the type's own doc comment.</summary>
    public const int WindowSeconds = 600;

    /// <summary>Rolling cap on how many crash timestamps the marker file keeps, oldest dropped
    /// first — bounds the marker's size independent of <see cref="MaxCrashesInWindow"/> (which only
    /// governs the relaunch decision itself); comfortably larger than anything a real trailing
    /// 10-minute window could ever need to inspect.</summary>
    private const int MaxTrackedCrashes = 10;

    private readonly string _markerPath;

    public CrashRelaunchGuard(string stateDir)
    {
        _markerPath = Path.Combine(stateDir, "crash-relaunch.json");
    }

    /// <summary>
    /// Call from the fatal-exception handler once per crash. Returns true if the caller should
    /// relaunch; false means a crash loop was detected (<see cref="MaxCrashesInWindow"/> or more
    /// crashes already recorded within the trailing <see cref="WindowSeconds"/> of <paramref name="nowUtc"/>
    /// — see the type's own doc comment) and the caller must exit WITHOUT relaunching. This crash's
    /// own timestamp is recorded either way (allowed or refused) so the sliding window keeps working
    /// correctly for the next call. Any marker read/write failure is swallowed and treated as "not a
    /// loop, allow the relaunch" — see the type's own doc comment.
    /// </summary>
    public bool ShouldRelaunch(DateTime nowUtc)
    {
        List<DateTime> crashes;
        try
        {
            crashes = ReadMarker() ?? new List<DateTime>();
        }
        catch
        {
            // Round-3 panel-3 T2 fix (carried forward under the T5 marker shape): the marker file
            // exists but is corrupt (unreadable, or fails to deserialize — e.g. truncated/garbled
            // JSON from a crash mid-write). Before that fix, this branch allowed the relaunch and
            // left the corrupt file in place, so every FUTURE crash hit this same catch too — the
            // guard was permanently dead until something else happened to replace the file.
            // Overwrite it with a fresh single-entry marker now so the guard self-heals in one step.
            // Marker IO must never throw past this method, so the overwrite itself is wrapped too —
            // a failed overwrite still falls through to "allow the relaunch," same as before.
            try
            {
                WriteMarker(new List<DateTime> { nowUtc });
            }
            catch
            {
                // Swallowed — see the type's own doc comment: marker IO must never block the decision.
            }
            return true;
        }

        try
        {
            var windowStart = nowUtc.AddSeconds(-WindowSeconds);
            int recentCount = crashes.Count(t => t > windowStart);
            bool allow = recentCount < MaxCrashesInWindow;

            crashes.Add(nowUtc);
            if (crashes.Count > MaxTrackedCrashes)
            {
                crashes.RemoveRange(0, crashes.Count - MaxTrackedCrashes);
            }
            WriteMarker(crashes);

            return allow;
        }
        catch
        {
            return true;
        }
    }

    private List<DateTime>? ReadMarker()
    {
        if (!File.Exists(_markerPath))
        {
            return null;
        }

        var json = File.ReadAllText(_markerPath);
        return JsonSerializer.Deserialize<List<DateTime>>(json);
    }

    private void WriteMarker(List<DateTime> crashes)
    {
        var json = JsonSerializer.Serialize(crashes);
        File.WriteAllText(_markerPath, json);
    }
}
