namespace GridLookout.Tests.Monitoring;

/// <summary>
/// Test double for <see cref="SynchronizationContext"/> — records how many times <see cref="Post"/>
/// was called, then invokes the posted callback SYNCHRONOUSLY on the calling thread (no real
/// message pump exists in a test process, so there is nothing to marshal onto). Used to exercise
/// <c>ScreenshotResponder</c>'s "marshal via Post when a WinForms SynchronizationContext is
/// available" branch — see that class's own doc comment for why a real xUnit test thread normally
/// has NO ambient context at all (exercising the OTHER branch, direct execution, without any test
/// double needed) and why this one exists specifically to cover the branch that isn't reachable by
/// default.
/// </summary>
public sealed class RecordingSynchronizationContext : SynchronizationContext
{
    public int PostCount { get; private set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        PostCount++;
        d(state);
    }
}
