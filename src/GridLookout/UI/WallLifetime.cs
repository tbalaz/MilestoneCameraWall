namespace GridLookout.UI;

/// <summary>
/// Decides when the process should exit as wall windows open and close.
///
/// WHY THIS EXISTS. Program.cs runs the message loop as <c>Application.Run()</c> with no main form,
/// because the wall may own several windows (one per configured monitor) and none of them is "the"
/// main one. That overload keeps pumping after every window has closed, so before this type, closing
/// a wall from compact mode's title bar left an invisible process running: still logged in to the
/// Management Server, still holding the KeepDisplayAwake power assertion, still ticking its refresh
/// timer, and reachable only through Task Manager. Esc was the only clean exit.
///
/// The complication is that a close is not always the operator leaving. The config-refresh timer
/// closes every wall form and rebuilds it whenever the recorder's Description or camera list
/// changes, so "last window closed" on its own would tear the app down on a routine layout edit.
/// Hence the explicit rebuild window: closes that happen between <see cref="BeginRebuild"/> and
/// <see cref="EndRebuild"/> never trigger an exit.
///
/// Pure and UI-free so the decision is unit-testable without a message pump or a real Form; the
/// caller wires the exit action (see Program.cs).
/// </summary>
public sealed class WallLifetime
{
    private int _openWindows;
    private bool _rebuilding;

    /// <summary>Windows currently open, as far as this tracker has been told.</summary>
    public int OpenWindows => _openWindows;

    /// <summary>True while a config-driven rebuild is in progress.</summary>
    public bool Rebuilding => _rebuilding;

    /// <summary>Call once per wall window shown.</summary>
    public void NoteOpened() => _openWindows++;

    /// <summary>
    /// Call once per wall window closed. Returns true when this was the last window and the app
    /// should exit; false when other windows remain, or when a rebuild is replacing them.
    /// Never drops below zero, so a duplicate close notification cannot make the count negative
    /// and strand the app with a phantom open window.
    /// </summary>
    public bool NoteClosed()
    {
        if (_openWindows > 0)
        {
            _openWindows--;
        }

        return !_rebuilding && _openWindows == 0;
    }

    /// <summary>Marks the start of a config-driven rebuild; closes are expected and must not exit.</summary>
    public void BeginRebuild() => _rebuilding = true;

    /// <summary>
    /// Marks the end of a rebuild. Deliberately does NOT exit even if the rebuild produced no
    /// windows at all (every monitor gone, say): the refresh timer is still live and the next tick
    /// can legitimately bring the wall back, which is the whole point of rebuilding without a
    /// restart. Only an operator closing the last window ends the process.
    /// </summary>
    public void EndRebuild() => _rebuilding = false;
}
