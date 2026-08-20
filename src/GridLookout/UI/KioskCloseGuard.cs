using System.Windows.Forms;

namespace GridLookout.UI;

/// <summary>
/// Pure decision for the S8/T1(R1) close-gate — see <see cref="WallForm.OnFormClosing"/>.
///
/// WHY THIS EXISTS (panel-4 round-5 T1/R1 fix). Before this type,
/// <see cref="GridLookout.Config.WallConfig.KioskLock"/> (via <c>WallForm._kioskLock</c>) only
/// gated Esc (<c>WallForm.ProcessCmdKey</c>) and the
/// double-click compact/fullscreen toggle (<c>WallForm.ToggleCompact</c>). Alt+F4, a taskbar
/// "Close window" command, and SC_CLOSE from the system menu all bypass both of those and reach
/// <see cref="Form.Close"/> directly — on a multi-monitor wall, closing just one monitor's window
/// leaves the process alive but that one monitor dark, and the watchdog (which only relaunches a
/// fully-dead process — see scripts/install-kiosk.ps1) never notices or restores it.
///
/// Extracted as a static, SDK/UI-free decision (no live <see cref="Form"/>, no message loop) so it
/// is unit-testable in isolation; <see cref="WallForm.OnFormClosing"/> is the only caller. Public
/// (not internal, no InternalsVisibleTo exists in this repo) so <c>tests/GridLookout.Tests</c> can
/// exercise it directly — same convention as <c>JpegFrameDecoder</c>/<c>LayoutEngine</c>/other
/// pure-logic types here.
/// </summary>
public static class KioskCloseGuard
{
    /// <summary>
    /// True when a close attempt should be CANCELLED.
    /// <list type="bullet">
    /// <item><paramref name="allowClose"/> always wins (returns false) — set only by
    /// <see cref="WallForm.CloseInternal"/>, which every programmatic close of a WallForm in this
    /// codebase must go through (the config-refresh rebuild teardown, RecoverSession's teardown,
    /// and LoginRetryLoop's own status-card close-on-success — see Program.cs) precisely so none of
    /// this app's own internal teardown is refused by its own lock.</item>
    /// <item><see cref="CloseReason.ApplicationExitCall"/> always wins too (returns false) — WinForms'
    /// <c>Application.Exit()</c> raises <c>OnFormClosing</c> directly (not through <c>Close()</c>'s
    /// WM_CLOSE path) with this reason for every open form in turn, and ABORTS THE REST OF THE LOOP
    /// the moment any one form cancels — so refusing here would not just fail to close this one
    /// window, it would silently strand every other open window a clean process-wide exit was
    /// trying to close too.</item>
    /// <item>Otherwise, when <paramref name="kioskLock"/> is set: cancel only
    /// <see cref="CloseReason.UserClosing"/> and <see cref="CloseReason.None"/> — the two reasons
    /// Alt+F4 / SC_CLOSE / a taskbar "Close window" click actually raise on a borderless topmost
    /// form. Every other reason (<see cref="CloseReason.WindowsShutDown"/>,
    /// <see cref="CloseReason.TaskManagerClosing"/>, <see cref="CloseReason.FormOwnerClosing"/>,
    /// <see cref="CloseReason.MdiFormClosing"/>) passes through unblocked — KioskLock exists to stop
    /// a bystander, not to fight the OS shutting down or an admin ending the task.</item>
    /// <item>When <paramref name="kioskLock"/> is NOT set: never cancel (returns false) — matches
    /// every prior release's unlocked behavior exactly.</item>
    /// </list>
    /// </summary>
    public static bool ShouldCancelClose(bool kioskLock, bool allowClose, CloseReason reason)
    {
        if (allowClose || !kioskLock || reason == CloseReason.ApplicationExitCall)
        {
            return false;
        }

        return reason == CloseReason.UserClosing || reason == CloseReason.None;
    }
}
