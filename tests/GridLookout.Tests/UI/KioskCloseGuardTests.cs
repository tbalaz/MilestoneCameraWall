using System.Windows.Forms;
using GridLookout.UI;
using Xunit;

namespace GridLookout.Tests.UI;

/// <summary>
/// Covers <see cref="KioskCloseGuard"/>, the panel-4 round-5 T1/R1 fix for KioskLock only gating
/// Esc and the double-click compact toggle. Alt+F4, a taskbar "Close window" command, and SC_CLOSE
/// from the system menu all bypassed both of those and reached <c>Form.Close()</c> directly, so a
/// locked wall could still be closed by any of the three. See <see cref="WallForm.OnFormClosing"/>
/// for how this pure decision is wired to a real form.
/// </summary>
public class KioskCloseGuardTests
{
    [Fact]
    public void UserClose_Locked_IsCancelled()
    {
        Assert.True(KioskCloseGuard.ShouldCancelClose(kioskLock: true, allowClose: false, CloseReason.UserClosing));
    }

    [Fact]
    public void UserClose_Unlocked_IsAllowed()
    {
        // Matches every prior release's behavior exactly when KioskLock was never turned on.
        Assert.False(KioskCloseGuard.ShouldCancelClose(kioskLock: false, allowClose: false, CloseReason.UserClosing));
    }

    [Fact]
    public void ProgrammaticInternalClose_IsAlwaysAllowed_EvenWhenLocked()
    {
        // WallForm.CloseInternal() sets allowClose before calling Close() — this is what lets the
        // config-refresh rebuild teardown, RecoverSession's teardown, and LoginRetryLoop's own
        // status-card close-on-success (see Program.cs) close a KioskLock'd form without being
        // refused by their own lock.
        Assert.False(KioskCloseGuard.ShouldCancelClose(kioskLock: true, allowClose: true, CloseReason.UserClosing));
    }

    [Fact]
    public void ApplicationExitCall_IsAlwaysAllowed_EvenWhenLocked()
    {
        // Application.Exit() raises OnFormClosing directly (not through Close()'s WM_CLOSE path)
        // with this reason for every open form in turn, and ABORTS THE REST OF THE LOOP the moment
        // any one form cancels — refusing here would strand every other open window a clean
        // process-wide exit was trying to close too, not just this one.
        Assert.False(KioskCloseGuard.ShouldCancelClose(kioskLock: true, allowClose: false, CloseReason.ApplicationExitCall));
    }

    [Fact]
    public void CloseReasonNone_Locked_IsCancelled()
    {
        // SC_CLOSE from the system menu / a taskbar "Close window" click can surface as
        // CloseReason.None rather than UserClosing on a borderless topmost form — both must be
        // refused identically.
        Assert.True(KioskCloseGuard.ShouldCancelClose(kioskLock: true, allowClose: false, CloseReason.None));
    }

    [Theory]
    [InlineData(CloseReason.WindowsShutDown)]
    [InlineData(CloseReason.TaskManagerClosing)]
    [InlineData(CloseReason.FormOwnerClosing)]
    [InlineData(CloseReason.MdiFormClosing)]
    public void OtherCloseReasons_PassThroughUnblocked_EvenWhenLocked(CloseReason reason)
    {
        // KioskLock exists to stop a bystander, not to fight the OS shutting down or an admin
        // ending the task via Task Manager.
        Assert.False(KioskCloseGuard.ShouldCancelClose(kioskLock: true, allowClose: false, reason));
    }
}
