namespace GridLookout.Config;

/// <summary>
/// Abstraction over <see cref="StateDirectory"/> so <see cref="WallConfigLoader"/>'s state-dir
/// merge/reseed logic (T3/R3 — see <see cref="WallConfigLoader.LoadOrCreate"/>) is unit-testable
/// with a FIXED writable/unwritable outcome, independent of whatever the real exe directory's
/// actual OS-level write permission happens to be during a test run. Mirrors
/// <see cref="ISecretProtector"/> in this same folder for the same reason: a test needs to hold
/// "the exe dir is unwritable" true while still using a completely normal, real, writable
/// directory for the exe dir itself (so it can freely create/read camerawall.json and control its
/// last-write time) — the real <see cref="StateDirectory"/>'s write-probe can't be told that
/// without an actual ACL-denied directory, which the existing tests deliberately avoid (see
/// StateDirectoryTests' own doc comment on why).
/// </summary>
public interface IStateDirectory
{
    /// <summary>See <see cref="StateDirectory.Resolve"/>.</summary>
    bool Resolve(string exeDir, out string stateDir);
}
