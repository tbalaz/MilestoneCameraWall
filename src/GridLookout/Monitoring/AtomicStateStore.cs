using GridLookout.Config;

namespace GridLookout.Monitoring;

/// <summary>
/// Writes a small JSON state document (in practice, <c>health.json</c>) atomically: serialize to a
/// temp file in the SAME directory as the destination, then <see cref="File.Replace(string, string, string?)"/>
/// (or <see cref="File.Move(string, string)"/> when no destination file exists yet) into place — a
/// reader (the external <c>--health-probe</c>, or a customer's own monitoring agent tailing the
/// file) can therefore never observe a partially-written or truncated file, only the previous
/// complete version or the new complete version, never something in between.
///
/// Uses <see cref="IStateDirectory"/> — the SAME writable-state-directory mechanism every other
/// piece of GridLookout's mutable state already resolves through (see
/// <see cref="GridLookout.Config.StateDirectory"/>, used for the config fallback and the log
/// directory) — rather than inventing a second path-resolution scheme. Takes the interface (not a
/// pre-resolved string) specifically so tests can inject <c>FakeStateDirectory</c> and hold a fixed
/// writable/unwritable outcome, the same pattern <c>WallConfigLoaderTests</c> already relies on.
/// </summary>
public sealed class AtomicStateStore
{
    private readonly string _directory;

    /// <param name="stateDirectory">Resolved once, at construction — mirrors every other caller of
    /// <see cref="IStateDirectory.Resolve"/> in this codebase (one resolve per process run, not
    /// re-probed on every write).</param>
    /// <param name="baseDir">Normally the exe directory — see <see cref="IStateDirectory.Resolve"/>'s
    /// own doc comment for what this controls.</param>
    public AtomicStateStore(IStateDirectory stateDirectory, string baseDir)
    {
        stateDirectory.Resolve(baseDir, out _directory);
    }

    /// <summary>The directory this store actually reads/writes — exposed for diagnostics/tests
    /// (e.g. so a caller can name the effective health.json path in a log line).</summary>
    public string Directory => _directory;

    /// <summary>Atomically writes <paramref name="content"/> to <paramref name="fileName"/> under
    /// the resolved state directory (created if missing). Never leaves a torn/partial file at the
    /// destination path — either this call fully succeeds and the destination holds the new
    /// content, or it throws and the destination is untouched (still holding whatever it held
    /// before, if anything).</summary>
    public void Write(string fileName, string content)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var destinationPath = Path.Combine(_directory, fileName);
        var tempPath = Path.Combine(_directory, $".{fileName}.tmp-{Guid.NewGuid():N}");

        File.WriteAllText(tempPath, content);
        try
        {
            if (File.Exists(destinationPath))
            {
                // File.Replace requires the destination to already exist and both paths to be on
                // the same volume (guaranteed here — both are under the same resolved directory).
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup of the orphaned temp file — the write failure itself is the
                // thing the caller needs to know about; a leftover .tmp file is a minor annoyance,
                // never something worth masking the real exception for.
            }

            throw;
        }
    }

    /// <summary>Reads <paramref name="fileName"/> from the resolved state directory, or null if it
    /// doesn't exist. No caching — callers (the probe runs once and exits; the controller's health
    /// timer only ever writes, never reads its own output back) don't need it.</summary>
    public string? Read(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
