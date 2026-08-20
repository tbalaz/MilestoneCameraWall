using GridLookout.Recovery;
using Xunit;

namespace GridLookout.Tests.Recovery;

/// <summary>
/// Covers <see cref="CrashRelaunchGuard"/>, the T7(a)/R8 fix for an unconditional fatal-restart
/// relaunch spinning forever on a systemic (not one-off) crash, tightened under T5 to a true
/// sliding window with no more "clear on successful match" — see the type's own doc comment. Each
/// test constructs a FRESH guard instance pointed at the same temp directory to simulate separate
/// process runs — matching real usage, where Program.cs constructs exactly one
/// <see cref="CrashRelaunchGuard"/> per process start, with the marker file being the only thing
/// that persists across runs.
/// </summary>
public class CrashRelaunchGuardTests : IDisposable
{
    private readonly string _dir;

    public CrashRelaunchGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.CrashRelaunchGuard." + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldRelaunch_NoPriorMarker_ReturnsTrue()
    {
        var guard = new CrashRelaunchGuard(_dir);

        Assert.True(guard.ShouldRelaunch(Now));
    }

    [Fact]
    public void ShouldRelaunch_UpToFiveCrashesWithinWindow_AllReturnTrue_SixthReturnsFalse()
    {
        for (int i = 0; i < 5; i++)
        {
            // Fresh instance each call — simulates the process actually restarting.
            var guard = new CrashRelaunchGuard(_dir);
            Assert.True(guard.ShouldRelaunch(Now.AddSeconds(i)));
        }

        var sixthGuard = new CrashRelaunchGuard(_dir);
        Assert.False(sixthGuard.ShouldRelaunch(Now.AddSeconds(5)));
    }

    [Fact]
    public void ShouldRelaunch_SixthCrash_LeavesMarkerDescribingAnActiveLoop()
    {
        for (int i = 0; i < 5; i++)
        {
            new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(i));
        }
        new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(5)); // 6th — blocked

        // A 7th attempt shortly after is STILL recognized as looping, not given a fresh allowance.
        Assert.False(new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(6)));
    }

    [Fact]
    public void ShouldRelaunch_WindowExpired_ResetsCount_AllowsRelaunchAgain()
    {
        for (int i = 0; i < 5; i++)
        {
            new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(i));
        }
        Assert.False(new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(5)));

        // More than WindowSeconds later — the whole burst above has aged out of the trailing
        // window, so a fresh crash here sees zero (or few) recent crashes and is allowed again.
        var afterWindow = Now.AddSeconds(CrashRelaunchGuard.WindowSeconds + 1);
        Assert.True(new CrashRelaunchGuard(_dir).ShouldRelaunch(afterWindow));
    }

    [Fact]
    public void ShouldRelaunch_UnwritableStateDir_DoesNotThrow_ReturnsTrue()
    {
        // Same technique as StateDirectoryTests — a FILE stands in for the directory, so every
        // marker read/write underneath it fails deterministically.
        var fakeStateDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        File.WriteAllText(fakeStateDir, string.Empty);
        var guard = new CrashRelaunchGuard(fakeStateDir);

        var exception = Record.Exception(() =>
        {
            var result = guard.ShouldRelaunch(Now);
            Assert.True(result);
        });

        Assert.Null(exception);
    }

    // --- T5: true sliding window (not anchored to the first crash) ---

    [Fact]
    public void ShouldRelaunch_ContinuousCrashesEvery100Seconds_StaysBlockedPastWhereAnAnchoredWindowWouldHaveReset()
    {
        // With the OLD anchored-at-first-crash window, a crash stream continuing past
        // WindowSeconds measured from the FIRST crash would silently reset the count to zero and
        // allow a fresh burst of 5 — even though crashes never actually stopped. A true sliding
        // window (measured back from EACH new crash, not from the first one ever recorded) must
        // keep refusing as long as 5+ crashes remain in the trailing 10 minutes, however long the
        // overall crash stream runs.
        for (int i = 0; i <= 6; i++) // Now, Now+100, ..., Now+600 (7 crashes)
        {
            var result = new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(i * 100));
            if (i < 5)
            {
                Assert.True(result); // crashes 1-5 (Now .. Now+400) allowed
            }
            else
            {
                Assert.False(result); // crashes 6-7 (Now+500, Now+600) blocked
            }
        }

        // Now+700 is more than WindowSeconds (600s) after the very FIRST crash — an anchored
        // window would have expired here and wrongly allowed a relaunch. Sliding must still
        // refuse: crashes at Now+200..Now+600 (5 of them) all remain within the trailing 600s of
        // Now+700.
        Assert.False(new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(700)));
    }

    [Fact]
    public void ShouldRelaunch_SixCrashesThenNoMoreForOverTenMinutes_SeventhIsAllowedAgain()
    {
        // Companion to the "stays blocked" test above: once the crash stream genuinely STOPS
        // (rather than just outlasting an old anchor point), the trailing window empties out and
        // a later, isolated crash is allowed again — the guard must not permanently latch closed.
        for (int i = 0; i < 6; i++)
        {
            new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(i));
        }

        var longAfter = Now.AddSeconds(CrashRelaunchGuard.WindowSeconds + 60);
        Assert.True(new CrashRelaunchGuard(_dir).ShouldRelaunch(longAfter));
    }

    // --- T5: no more "clear on successful match" — a post-match crash loop is caught too ---

    [Fact]
    public void ShouldRelaunch_SixCrashesAcrossWhatWouldHaveBeenAPostMatchBoundary_StillBlocksTheSixth()
    {
        // A successful recorder match no longer clears the marker (see the type's own doc comment
        // for the pre-T5 CrashRelaunchGuard.Clear() behavior this replaces) — a crash loop that
        // resumes AFTER a match must be caught by the very same window as a pre-match loop. Five
        // crashes, then (in real usage) a match would happen here; this guard has no Clear() method
        // anymore, so nothing resets, and a sixth crash 30 seconds later is still correctly
        // recognized as the SAME loop.
        for (int i = 0; i < 5; i++)
        {
            Assert.True(new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(i)));
        }

        // A "match" would happen here in Program.cs — no marker-clearing call exists anymore.

        Assert.False(new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(35)));
    }

    // --- T5: rolling cap on the marker's own tracked-crash list ---

    [Fact]
    public void ShouldRelaunch_MarkerListNeverExceedsTenEntries()
    {
        var markerPath = Path.Combine(_dir, "crash-relaunch.json");
        for (int i = 0; i < 15; i++)
        {
            // Spaced 2 hours apart so the sliding-window refuse logic never kicks in — this test is
            // only about the marker's own rolling cap, not the refuse threshold.
            new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddHours(2 * i));
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(markerPath));
        Assert.Equal(10, doc.RootElement.GetArrayLength());
    }

    // --- Round-3 panel-3 T2: corrupt marker file must not permanently disable the guard ---

    [Fact]
    public void ShouldRelaunch_CorruptMarkerFile_DoesNotThrow_OverwritesWithFreshMarker_ReturnsTrue()
    {
        var markerPath = Path.Combine(_dir, "crash-relaunch.json");
        File.WriteAllText(markerPath, "{ this is not valid json !!!");
        var guard = new CrashRelaunchGuard(_dir);

        bool result = false;
        var exception = Record.Exception(() => result = guard.ShouldRelaunch(Now));

        Assert.Null(exception);
        Assert.True(result);

        // The corrupt file must have been overwritten with a fresh marker — a one-entry crash
        // timestamp array — not left corrupt, or every future crash would hit the same read
        // failure and the guard would be dead forever.
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(markerPath));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal(Now, doc.RootElement[0].GetDateTime());
    }

    [Fact]
    public void ShouldRelaunch_CorruptMarkerFile_GuardStillDetectsALoopAfterwards()
    {
        // Proves the guard actually self-heals, not just "doesn't throw" — after the corrupt-file
        // recovery counts as crash #1, four more crashes are still allowed and the 6th (within the
        // same 10-minute window) is correctly blocked, exactly as an uncorrupted run would behave.
        var markerPath = Path.Combine(_dir, "crash-relaunch.json");
        File.WriteAllText(markerPath, "not json at all");
        Assert.True(new CrashRelaunchGuard(_dir).ShouldRelaunch(Now));

        for (int i = 1; i < 5; i++)
        {
            Assert.True(new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(i)));
        }

        Assert.False(new CrashRelaunchGuard(_dir).ShouldRelaunch(Now.AddSeconds(5)));
    }
}
