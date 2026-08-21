namespace GridLookout.Config;

/// <summary>
/// Resolves the app's writable mutable-state root. The exe directory is used IF a probe write
/// there succeeds (the ordinary dev/portable case, and any MSI install with a writable
/// INSTALLDIR) — that keeps behavior byte-identical to before this type existed. Otherwise (e.g. a
/// non-admin/limited kiosk account under %ProgramFiles%, which denies write to a standard user)
/// %ProgramData%\GridLookout is used instead, created on first use.
///
/// WHY THIS EXISTS (B4 fix). The documented kiosk first run puts camerawall.json next to the exe
/// in %ProgramFiles% and runs the app as a limited account. WallConfigLoader's DPAPI
/// password-migration rewrite then tried to write back into that read-only directory, threw
/// UnauthorizedAccessException before any window existed, and FileLogger's own log directory was
/// equally unwritable — the result was an invisible, log-free crash-restart loop (see
/// docs/critiques/gridlookout-panel-1.md, findings B4/S2/U2/I3). This resolver gives both
/// WallConfigLoader and FileLogger a writable fallback instead of crashing.
///
/// SDK-free (plain File/Directory/Environment calls only) so it's unit-testable without a VMS or
/// elevated permissions — the ProgramData root is overridable via the constructor for exactly that
/// reason (tests point it at a disposable temp directory rather than the real
/// %ProgramData%\GridLookout).
/// </summary>
public sealed class StateDirectory : IStateDirectory
{
    private readonly string _programDataRoot;

    /// <param name="programDataRootOverride">Overrides the ProgramData fallback root (tests only —
    /// production callers omit this and get the real %ProgramData%\GridLookout).</param>
    public StateDirectory(string? programDataRootOverride = null)
    {
        _programDataRoot = programDataRootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GridLookout");
    }

    /// <summary>
    /// Resolves the writable state directory for <paramref name="exeDir"/>. Returns true and sets
    /// <paramref name="stateDir"/> to <paramref name="exeDir"/> itself when a probe write there
    /// succeeds; returns false and sets <paramref name="stateDir"/> to the ProgramData fallback
    /// (created if missing) otherwise. Performs one real probe write + delete per call — cheap, and
    /// deliberately not cached, since the answer can only meaningfully change between process runs
    /// (an ACL change mid-run is not a scenario this app needs to react to live).
    /// </summary>
    public bool Resolve(string exeDir, out string stateDir)
    {
        if (CanWrite(exeDir))
        {
            stateDir = exeDir;
            return true;
        }

        try
        {
            Directory.CreateDirectory(_programDataRoot);
            stateDir = _programDataRoot;
            return false;
        }
        catch
        {
            // T8(c)/R10 + m7 fix (2026-08-21 external audit): creating the ProgramData fallback
            // itself can throw (ProgramData inaccessible too, or _programDataRoot names an invalid
            // path) — same never-throw discipline as CanWrite() below. Pre-fix this returned TRUE
            // with stateDir = exeDir, i.e. it CLAIMED the exe dir was writable one branch after
            // CanWrite() proved it isn't — misdirecting migration writes and their error messages
            // at a location known to refuse them. Now it stays truthful: false (the exe dir is NOT
            // writable — that much is proven) with stateDir still naming the ProgramData fallback,
            // so subsequent writes fail LOUDLY at the path an admin would actually need to fix,
            // and land in the config layer's existing degrade-with-warning paths rather than
            // behind a false "writable" flag. Reachable only when BOTH locations are broken — a
            // machine that needs an admin regardless; honesty about which dir failed is what
            // shortens that visit.
            stateDir = _programDataRoot;
            return false;
        }
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            var probePath = Path.Combine(directory, $".gridlookout-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
