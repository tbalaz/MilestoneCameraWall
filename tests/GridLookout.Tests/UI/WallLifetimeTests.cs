using GridLookout.UI;
using Xunit;

namespace GridLookout.Tests.UI;

/// <summary>
/// Covers <see cref="WallLifetime"/>, which decides when closing a wall window ends the process.
///
/// The bug this guards: Program.cs pumps messages with <c>Application.Run()</c> and no main form, so
/// the loop survives every window closing. Closing a wall from compact mode's title bar therefore
/// left an invisible process running - still signed in to the Management Server, still holding the
/// KeepDisplayAwake assertion. The opposite failure is just as bad: the config-refresh timer closes
/// and rebuilds every window on a layout edit, so exiting on "last window closed" alone would kill
/// the wall on a routine Description change.
/// </summary>
public class WallLifetimeTests
{
    [Fact]
    public void ClosingTheOnlyWindow_Exits()
    {
        var lifetime = new WallLifetime();
        lifetime.NoteOpened();

        Assert.True(lifetime.NoteClosed());
    }

    [Fact]
    public void ClosingOneOfTwoWindows_DoesNotExitUntilTheSecondCloses()
    {
        var lifetime = new WallLifetime();
        lifetime.NoteOpened();
        lifetime.NoteOpened();

        Assert.False(lifetime.NoteClosed());
        Assert.True(lifetime.NoteClosed());
    }

    [Fact]
    public void RebuildClosingEveryWindow_DoesNotExit()
    {
        // A two-monitor wall being rebuilt after a $layout{} edit: both windows close, then two more
        // open. The app must survive all of it.
        var lifetime = new WallLifetime();
        lifetime.NoteOpened();
        lifetime.NoteOpened();

        lifetime.BeginRebuild();
        Assert.False(lifetime.NoteClosed());
        Assert.False(lifetime.NoteClosed());
        Assert.Equal(0, lifetime.OpenWindows);
        lifetime.EndRebuild();

        lifetime.NoteOpened();
        lifetime.NoteOpened();
        Assert.Equal(2, lifetime.OpenWindows);
    }

    [Fact]
    public void AfterARebuild_ClosingTheLastWindowStillExits()
    {
        // The rebuild flag must not latch: an operator closing the wall after a layout edit has to
        // shut the process down exactly as before.
        var lifetime = new WallLifetime();
        lifetime.NoteOpened();
        lifetime.BeginRebuild();
        lifetime.NoteClosed();
        lifetime.EndRebuild();
        lifetime.NoteOpened();

        Assert.True(lifetime.NoteClosed());
    }

    [Fact]
    public void RebuildThatProducesNoWindows_DoesNotExit()
    {
        // Every configured monitor has gone missing, so the rebuild yields nothing. The refresh timer
        // is still live and a later tick can bring the wall back, so this must not end the process.
        var lifetime = new WallLifetime();
        lifetime.NoteOpened();

        lifetime.BeginRebuild();
        Assert.False(lifetime.NoteClosed());
        lifetime.EndRebuild();

        Assert.Equal(0, lifetime.OpenWindows);
        Assert.False(lifetime.Rebuilding);
    }

    [Fact]
    public void DuplicateCloseNotification_DoesNotDriveTheCountNegative()
    {
        // WinForms can raise FormClosed for a form that was already accounted for; a negative count
        // would leave the app believing a window is still open and never exiting.
        var lifetime = new WallLifetime();
        lifetime.NoteOpened();

        Assert.True(lifetime.NoteClosed());
        Assert.True(lifetime.NoteClosed());
        Assert.Equal(0, lifetime.OpenWindows);
    }
}
