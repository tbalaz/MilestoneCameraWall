namespace GridLookout.Monitoring;

/// <summary>
/// Byte-payload counterpart to <see cref="AtomicStateStore"/>'s atomic-write discipline: write a
/// temp file in the SAME directory as the destination, then <see cref="File.Replace(string, string, string?)"/>
/// (or <see cref="File.Move(string, string)"/> when no destination file exists yet) into place — a
/// reader (a remote operator's <c>scp</c>/copy of a <c>screen-N.png</c>, mid-write) can therefore
/// never observe a partially-written or truncated file, only the previous complete version or the
/// new complete version.
///
/// <see cref="AtomicStateStore.Write"/> only accepts a <see cref="string"/> (it serializes JSON
/// text) — that API doesn't fit a PNG's binary payload, so <see cref="ScreenshotResponder"/> uses
/// this instead of forcing a second, DIFFERENT atomicity mechanism into existence for no reason:
/// same algorithm, same guarantee, just over <c>byte[]</c>. Kept as its own small, static,
/// dependency-free method (rather than inlined into the capture routine) specifically so unit tests
/// can exercise the atomic-write behavior with plain bytes — no <see cref="System.Drawing.Bitmap"/>/
/// GDI+/live-screen dependency at all. See <see cref="ScreenshotResponder"/>'s own doc comment for
/// why the actual screen capture (as opposed to this write step) stays out of the unit-test surface.
/// </summary>
public static class AtomicBinaryFileWriter
{
    /// <summary>Atomically writes <paramref name="content"/> to <paramref name="destinationPath"/>
    /// (parent directory created if missing). Never leaves a torn/partial file at the destination —
    /// either this call fully succeeds and the destination holds the new content, or it throws and
    /// the destination is untouched (still holding whatever it held before, if anything).</summary>
    public static void Write(string destinationPath, byte[] content)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException($"'{destinationPath}' has no directory component.", nameof(destinationPath));
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.tmp-{Guid.NewGuid():N}");

        File.WriteAllBytes(tempPath, content);
        try
        {
            if (File.Exists(destinationPath))
            {
                // File.Replace requires the destination to already exist and both paths to be on
                // the same volume (guaranteed here — both are under the same resolved directory) —
                // same reasoning as AtomicStateStore.Write's identical branch.
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
}
