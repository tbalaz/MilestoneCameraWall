using GridLookout.Config;

namespace GridLookout.Tests.Config;

/// <summary>Test double for <see cref="IStateDirectory"/> — returns a FIXED writable/unwritable
/// outcome and a caller-supplied state directory, independent of the actual OS-level write
/// permission of whatever "exe dir" path is passed to <c>WallConfigLoader.LoadOrCreate</c>. This is
/// what makes the T3/R3 snapshot-shadowing tests in WallConfigLoaderTests deterministic: the exe
/// dir stays a completely normal, real, writable temp directory (so the test itself can freely
/// create camerawall.json and set its last-write time), while WallConfigLoader is still told
/// "you can't write there."</summary>
public sealed class FakeStateDirectory : IStateDirectory
{
    private readonly bool _writable;
    private readonly string _stateDir;

    public FakeStateDirectory(bool writable, string stateDir)
    {
        _writable = writable;
        _stateDir = stateDir;
    }

    public bool Resolve(string exeDir, out string stateDir)
    {
        stateDir = _stateDir;
        return _writable;
    }
}
