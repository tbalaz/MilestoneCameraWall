using GridLookout.Config;

namespace GridLookout.Monitoring;

/// <summary>
/// Computes where remote-screenshot PNGs live — shared by both sides of the feature
/// (<see cref="ScreenshotResponder"/>, the running wall that writes them, and
/// <see cref="ScreenshotRequester"/>, the short-lived <c>--screenshot</c> CLI invocation that reads
/// them back) so Program.cs never has two independently-written copies of this path free to drift
/// apart.
///
/// CROSS-ACCOUNT CAVEAT (the whole reason <see cref="CandidateScreenshotDirectories"/> exists, not
/// just <see cref="ResolveWritableScreenshotDirectory"/> alone). <see cref="StateDirectory.Resolve"/>
/// decides between the exe directory and the <c>%ProgramData%\GridLookout</c> fallback by PROBING
/// WRITABILITY AS THE CALLING PROCESS'S OWN ACCOUNT — see that method's own doc comment. The
/// documented kiosk deployment (docs/camerawall-security.md, "Writable-state fallback") runs the
/// wall as a limited/standard account, for which the exe directory under
/// <c>%ProgramFiles%</c> is normally NOT writable, so the wall resolves to the ProgramData
/// fallback. A remote operator invoking <c>--screenshot</c> over ssh/psexec, however, typically
/// does so as an ADMINISTRATOR account — for which the exe directory very likely IS writable — so
/// that process's OWN probe would resolve to the exe directory instead, a DIFFERENT answer than the
/// wall's, even though both processes pass the identical <c>baseDir</c>. Resolving each side
/// independently and trusting the requester's own answer would silently look in the wrong place:
/// no files found, nothing printed, exit code 0 — indistinguishable from "the wall really has
/// nothing to show" while actually just meaning "wrong directory." <see cref="CandidateScreenshotDirectories"/>
/// is the fix: rather than trusting one probe, the requester checks its own resolved candidate
/// FIRST (correct and cheapest when both sides happen to run as equivalently-privileged accounts),
/// then falls back to the one other possible location — see <see cref="ScreenshotRequester.Run"/>'s
/// own doc comment for how it picks between them (first candidate that actually holds
/// <c>screen-*.png</c> files).
/// </summary>
public static class ScreenshotPaths
{
    /// <summary>Subdirectory name under the resolved state directory — final layout is
    /// <c>&lt;stateDir&gt;\screenshots\screen-&lt;n&gt;.png</c>.</summary>
    public const string ScreenshotsSubdirectoryName = "screenshots";

    /// <summary>Glob <see cref="ScreenshotRequester"/> lists with and <see cref="ScreenshotResponder"/>
    /// prunes against — one shared constant so the two sides can never drift apart on what counts as
    /// "a screenshot file".</summary>
    public const string FileNamePattern = "screen-*.png";

    /// <summary>The exact file name <see cref="ScreenshotResponder"/> writes for 1-based
    /// <paramref name="screenNumber"/> — the ONLY place that format string is written, so
    /// <see cref="TryParseScreenIndex"/>'s parsing (the inverse operation) can never silently drift
    /// out of sync with it.</summary>
    public static string FileName(int screenNumber) => $"screen-{screenNumber}.png";

    /// <summary>
    /// Parses the N back out of a <c>screen-N.png</c> file name (pass
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/>'s result, not a full path) — shared by
    /// <see cref="ScreenshotRequester"/> (numeric sort order so <c>screen-10.png</c> doesn't sort
    /// before <c>screen-2.png</c>) and <see cref="ScreenshotResponder"/> (pruning orphaned files left
    /// over from a monitor count that has since decreased). Returns false for any name that doesn't
    /// match the pattern GridLookout itself ever writes, so an unrelated file someone else dropped
    /// into the screenshots directory can never be mistaken for one.
    /// </summary>
    public static bool TryParseScreenIndex(string fileNameWithoutExtension, out int index)
    {
        var digits = new string(fileNameWithoutExtension.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out index);
    }

    /// <summary>
    /// Resolves the screenshot directory the SAME way every other piece of GridLookout's mutable
    /// state resolves (<see cref="IStateDirectory.Resolve"/> — see <see cref="StateDirectory"/>'s
    /// own doc comment): exe directory if a real probe write there succeeds for the CALLING
    /// process's account, <c>%ProgramData%\GridLookout</c> otherwise. Correct and authoritative for
    /// <see cref="ScreenshotResponder"/> (the running wall — there is only ever one true answer for
    /// where IT writes). For <see cref="ScreenshotRequester"/>, this is only the FIRST candidate to
    /// check — see <see cref="CandidateScreenshotDirectories"/> and this class's own doc comment for
    /// why a second candidate is also needed on that side.
    /// </summary>
    public static string ResolveWritableScreenshotDirectory(IStateDirectory stateDirectory, string baseDir)
    {
        stateDirectory.Resolve(baseDir, out var stateDir);
        return Path.Combine(stateDir, ScreenshotsSubdirectoryName);
    }

    /// <summary>
    /// The ordered list of directories <see cref="ScreenshotRequester"/> should check for
    /// <c>screen-*.png</c> files — see this class's own doc comment for the cross-account reason a
    /// single resolved path isn't reliable for the requester side. Always returns 1 or 2 entries:
    /// <see cref="ResolveWritableScreenshotDirectory"/>'s own answer first, then whichever of
    /// {exe-directory\screenshots, %ProgramData%\GridLookout\screenshots} it did NOT already return
    /// (never both — that would only happen with a manufactured
    /// <paramref name="programDataRootOverride"/> equal to <paramref name="baseDir"/>, not a real
    /// deployment).
    /// </summary>
    /// <param name="programDataRootOverride">Overrides the real <c>%ProgramData%\GridLookout</c>
    /// root — tests only, mirroring <see cref="StateDirectory"/>'s own constructor parameter of the
    /// same purpose so a test can hold a fixed, disposable directory instead of the machine's real
    /// ProgramData.</param>
    public static IReadOnlyList<string> CandidateScreenshotDirectories(IStateDirectory stateDirectory, string baseDir, string? programDataRootOverride = null)
    {
        var primary = ResolveWritableScreenshotDirectory(stateDirectory, baseDir);

        var programDataRoot = programDataRootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GridLookout");

        var candidates = new List<string> { primary };
        foreach (var alternate in new[]
                 {
                     Path.Combine(baseDir, ScreenshotsSubdirectoryName),
                     Path.Combine(programDataRoot, ScreenshotsSubdirectoryName),
                 })
        {
            if (!candidates.Contains(alternate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(alternate);
            }
        }

        return candidates;
    }
}
