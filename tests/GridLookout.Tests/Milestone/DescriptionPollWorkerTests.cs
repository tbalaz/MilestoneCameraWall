using GridLookout.Milestone;
using Xunit;

namespace GridLookout.Tests.Milestone;

/// <summary>
/// FIX 4 (poll off the UI thread): covers <see cref="DescriptionPollWorker"/>'s single-flight/skip
/// contract with a fake, injected poll function — no real HTTP, no live SDK session, per the class's
/// own "testable without a live SDK/real HTTP" design note. A generous 5s timeout bounds every wait
/// so a genuine regression (a stuck background task) fails the test loudly instead of hanging the
/// suite.
/// </summary>
public class DescriptionPollWorkerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retries <paramref name="triggerAndCheck"/> (call <c>TriggerPollIfIdle</c>, then report whether
    /// the expected effect landed) with a short SLEEP between attempts, rather than
    /// <c>SpinWait.SpinUntil</c>'s default hot-spin. There's an inherent, unavoidable few-instruction
    /// window between a poll's effect becoming observable (e.g. <c>Latest</c> being assigned) and the
    /// worker's <c>finally</c> block clearing its in-flight flag on the background thread — a bare
    /// spin loop re-invoking <c>TriggerPollIfIdle</c> thousands of times a second across that window
    /// competes with the ThreadPool for the same core(s) and was observed to occasionally starve the
    /// pooled worker thread long enough to blow even a 5s budget. A small sleep between attempts costs
    /// a few milliseconds of test time in the common case and reliably lets the background thread run.
    /// </summary>
    private static bool RetryTriggerUntil(DescriptionPollWorker worker, Func<bool> reachedExpectedState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            worker.TriggerPollIfIdle();
            if (reachedExpectedState())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return reachedExpectedState();
    }

    [Fact]
    public void Latest_BeforeAnyPoll_IsNull()
    {
        var worker = new DescriptionPollWorker(() => new Dictionary<Guid, string>());

        Assert.Null(worker.Latest);
    }

    [Fact]
    public void TriggerPollIfIdle_PollSucceeds_LatestReflectsResult()
    {
        var id = Guid.NewGuid();
        var worker = new DescriptionPollWorker(() => new Dictionary<Guid, string> { [id] = "$layout{A1}" });

        worker.TriggerPollIfIdle();

        Assert.True(SpinWait.SpinUntil(() => worker.Latest is not null, WaitTimeout), "poll never completed");
        Assert.Equal("$layout{A1}", worker.Latest![id]);
    }

    [Fact]
    public void TriggerPollIfIdle_PollReturnsNull_LatestStaysNull()
    {
        // Failure/refusal contract: a null poll result (poll failed, or MilestoneSession.IsLayoutPollAllowed
        // refused it) must never be mistaken for "the live description set is now empty" — Latest
        // stays exactly as it was (null here, since none has ever succeeded).
        var callCount = 0;
        var worker = new DescriptionPollWorker(() => { Interlocked.Increment(ref callCount); return null; });

        worker.TriggerPollIfIdle();

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref callCount) == 1, WaitTimeout), "poll function was never invoked");
        Assert.Null(worker.Latest);
    }

    [Fact]
    public void TriggerPollIfIdle_PollReturnsNullAfterAPriorSuccess_KeepsPreviousLatest()
    {
        // Graceful degradation: one bad tick must not erase a previously-fetched good result — the
        // wall keeps using the last description it actually had.
        var id = Guid.NewGuid();
        var results = new Queue<Dictionary<Guid, string>?>(new Dictionary<Guid, string>?[]
        {
            new() { [id] = "$layout{A1}" },
            null,
        });
        var callCount = 0;
        var worker = new DescriptionPollWorker(() =>
        {
            var result = results.Dequeue();
            Interlocked.Increment(ref callCount);
            return result;
        });

        worker.TriggerPollIfIdle();
        Assert.True(SpinWait.SpinUntil(() => worker.Latest is not null, WaitTimeout), "first poll never completed");
        var firstLatest = worker.Latest;

        Assert.True(
            RetryTriggerUntil(worker, () => Volatile.Read(ref callCount) == 2, WaitTimeout),
            "second poll never ran");

        Assert.Same(firstLatest, worker.Latest);
    }

    [Fact]
    public void TriggerPollIfIdle_CalledAgainWhileFirstStillInFlight_IsSkipped_NoOverlappingPolls()
    {
        // Single-flight contract: overlapping polls are forbidden. The first poll blocks on
        // pollStarted/releasePoll so the test controls exactly when it's "in flight"; two more
        // triggers land during that window and must both be no-ops.
        var callCount = 0;
        var pollStarted = new ManualResetEventSlim(false);
        var releasePoll = new ManualResetEventSlim(false);
        var worker = new DescriptionPollWorker(() =>
        {
            Interlocked.Increment(ref callCount);
            pollStarted.Set();
            releasePoll.Wait(WaitTimeout);
            return new Dictionary<Guid, string>();
        });

        worker.TriggerPollIfIdle();
        Assert.True(pollStarted.Wait(WaitTimeout), "first poll never started");

        // Both of these must be no-ops — a poll is already in flight.
        worker.TriggerPollIfIdle();
        worker.TriggerPollIfIdle();

        releasePoll.Set();
        Assert.True(SpinWait.SpinUntil(() => worker.Latest is not null, WaitTimeout), "poll never completed");

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void TriggerPollIfIdle_CalledAgainAfterPriorPollCompleted_RunsAgain()
    {
        // Single-flight only blocks OVERLAPPING polls — a new trigger once the previous one has
        // actually finished must start a fresh poll, not stay permanently stuck skipping.
        var callCount = 0;
        var worker = new DescriptionPollWorker(() => { Interlocked.Increment(ref callCount); return new Dictionary<Guid, string>(); });

        worker.TriggerPollIfIdle();
        Assert.True(SpinWait.SpinUntil(() => worker.Latest is not null, WaitTimeout), "first poll never completed");

        Assert.True(
            RetryTriggerUntil(worker, () => Volatile.Read(ref callCount) == 2, WaitTimeout),
            "second poll never ran");
    }

    [Fact]
    public void TriggerPollIfIdle_PollThrows_DoesNotPropagate_LeavesLatestUnchangedAndWorkerUsable()
    {
        var threw = new ManualResetEventSlim(false);
        var firstCall = true;
        var worker = new DescriptionPollWorker(() =>
        {
            if (firstCall)
            {
                firstCall = false;
                threw.Set();
                throw new InvalidOperationException("simulated unexpected failure");
            }

            return new Dictionary<Guid, string> { [Guid.NewGuid()] = "$layout{A1}" };
        });

        worker.TriggerPollIfIdle();
        // No observable exception on the caller's side — Task.Run's exception is caught inside the
        // worker, not rethrown (there is no caller-side await to catch it anyway).
        Assert.True(threw.Wait(WaitTimeout), "poll function was never invoked");
        Assert.Null(worker.Latest);

        // The worker must not be wedged (_inFlight stuck true) by the thrown exception — a later
        // trigger still runs and succeeds normally.
        Assert.True(
            RetryTriggerUntil(worker, () => worker.Latest is not null, WaitTimeout),
            "worker got stuck after the poll function threw");
    }

    [Fact]
    public void Shutdown_BeforeAnyTrigger_LaterTriggersNeverInvokeThePollFunction()
    {
        // Round-4 buyer-review hardening: Shutdown() is terminal — a post-shutdown trigger must not
        // start a poll at all (the process is exiting; a new HTTP call would outlive its consumers).
        var callCount = 0;
        var worker = new DescriptionPollWorker(() => { Interlocked.Increment(ref callCount); return new Dictionary<Guid, string>(); });

        worker.Shutdown();
        worker.TriggerPollIfIdle();
        worker.TriggerPollIfIdle();

        // Negative assertion, so give any (wrongly) started background task a real chance to run
        // before checking — SpinUntil returning false IS the expected outcome here.
        Assert.False(
            SpinWait.SpinUntil(() => Volatile.Read(ref callCount) > 0, TimeSpan.FromMilliseconds(300)),
            "poll function ran after Shutdown()");
        Assert.Null(worker.Latest);
    }

    [Fact]
    public void Shutdown_WhileAPollIsInFlight_ThatPollsLateResultIsDiscarded()
    {
        // Round-4 buyer-review hardening: a poll that was already running when Shutdown() was called
        // completes on its background thread, but its result must be thrown away, never published
        // through Latest — the same "nothing new is observable after shutdown" contract as above.
        var id = Guid.NewGuid();
        var pollStarted = new ManualResetEventSlim(false);
        var releasePoll = new ManualResetEventSlim(false);
        var pollReturned = new ManualResetEventSlim(false);
        var worker = new DescriptionPollWorker(() =>
        {
            pollStarted.Set();
            releasePoll.Wait(WaitTimeout);
            pollReturned.Set();
            return new Dictionary<Guid, string> { [id] = "$layout{A1}" };
        });

        worker.TriggerPollIfIdle();
        Assert.True(pollStarted.Wait(WaitTimeout), "poll never started");

        worker.Shutdown();
        releasePoll.Set();
        Assert.True(pollReturned.Wait(WaitTimeout), "poll never returned");

        // pollReturned fires just BEFORE the worker's own publish-or-discard decision runs, so this
        // is a negative assertion with a settle window, same pattern as the test above.
        Assert.False(
            SpinWait.SpinUntil(() => worker.Latest is not null, TimeSpan.FromMilliseconds(300)),
            "a poll completing after Shutdown() still published its result");
    }
}
