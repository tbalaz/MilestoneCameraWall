using System.Runtime.InteropServices;

namespace GridLookout.Interop;

/// <summary>
/// Thin wrapper around kernel32's <c>SetThreadExecutionState</c> — keeps the display (and system)
/// awake while the camera wall is showing live video on an otherwise-idle kiosk box. The state
/// this API sets belongs to the CALLING thread, not the process, so <see cref="KeepAwake"/> must
/// always be called from the same thread that owns the WinForms message pump (the STA main
/// thread) — calling it from a background/SDK callback thread would have no effect on the thread
/// Windows actually watches for "is this app still active" purposes.
/// </summary>
public static class PowerGuard
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    /// <summary>
    /// Asserts that the system and display must stay awake. Idempotent — safe (and expected) to
    /// call repeatedly, since some display drivers silently drop the assertion after a while.
    /// </summary>
    public static void KeepAwake()
    {
        SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
    }

    /// <summary>
    /// Clears the assertion, restoring normal OS sleep/display-timeout behavior. Call once on
    /// clean exit of a run that called <see cref="KeepAwake"/>.
    /// </summary>
    public static void Release()
    {
        SetThreadExecutionState(ExecutionState.Continuous);
    }
}
