using GridLookout.Logging;
using Xunit;

namespace GridLookout.Tests.Logging;

public class FileLoggerTests : IDisposable
{
    private readonly string _dir;

    public FileLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.Logging." + Guid.NewGuid());
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

    private static string ReadTodayLog(string dir)
    {
        var path = Path.Combine(dir, $"gridlookout-{DateTime.Now:yyyyMMdd}.log");
        return File.ReadAllText(path);
    }

    [Fact]
    public void MinimumLevel_Info_DropsDebug_KeepsWarning()
    {
        var logger = new FileLogger(_dir, LogLevel.Info);

        logger.Debug("debug message should be dropped");
        logger.Warning("warning message should be kept");

        var content = ReadTodayLog(_dir);
        Assert.DoesNotContain("debug message should be dropped", content);
        Assert.Contains("warning message should be kept", content);
    }

    [Fact]
    public void MinimumLevel_Debug_KeepsEverything()
    {
        var logger = new FileLogger(_dir, LogLevel.Debug);

        logger.Debug("d");
        logger.Info("i");
        logger.Warning("w");
        logger.Error("e");

        var content = ReadTodayLog(_dir);
        Assert.Contains("[DEBUG] d", content);
        Assert.Contains("[INFO] i", content);
        Assert.Contains("[WARN] w", content);
        Assert.Contains("[ERROR] e", content);
    }

    [Fact]
    public void MinimumLevel_Error_DropsEverythingBelowError()
    {
        var logger = new FileLogger(_dir, LogLevel.Error);

        logger.Debug("d");
        logger.Info("i");
        logger.Warning("w");
        logger.Error("e");

        var content = ReadTodayLog(_dir);
        Assert.Contains("[ERROR] e", content);
        Assert.DoesNotContain("[DEBUG]", content);
        Assert.DoesNotContain("[INFO]", content);
        Assert.DoesNotContain("[WARN]", content);
    }

    [Fact]
    public void MinimumLevel_SettableAfterConstruction_AppliesToSubsequentWrites()
    {
        var logger = new FileLogger(_dir, LogLevel.Error);
        logger.Info("dropped before level change");

        logger.MinimumLevel = LogLevel.Info;
        logger.Info("kept after level change");

        var content = ReadTodayLog(_dir);
        Assert.DoesNotContain("dropped before level change", content);
        Assert.Contains("kept after level change", content);
    }

    [Theory]
    [InlineData(LogLevel.Debug, "[DEBUG]")]
    [InlineData(LogLevel.Info, "[INFO]")]
    [InlineData(LogLevel.Warning, "[WARN]")]
    [InlineData(LogLevel.Error, "[ERROR]")]
    public void LineFormat_ContainsUppercaseLevelTag(LogLevel level, string expectedTag)
    {
        var logger = new FileLogger(_dir, LogLevel.Debug);

        switch (level)
        {
            case LogLevel.Debug: logger.Debug("m"); break;
            case LogLevel.Info: logger.Info("m"); break;
            case LogLevel.Warning: logger.Warning("m"); break;
            case LogLevel.Error: logger.Error("m"); break;
        }

        var content = ReadTodayLog(_dir);
        Assert.Contains(expectedTag, content);
    }

    [Fact]
    public void LineFormat_StartsWithTimestamp()
    {
        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.Info("hello");

        var content = ReadTodayLog(_dir).TrimEnd();
        // "yyyy-MM-dd HH:mm:ss.fff [INFO] hello"
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INFO\] hello$", content);
    }

    [Fact]
    public void Error_WithException_AppendsExceptionToString()
    {
        var logger = new FileLogger(_dir, LogLevel.Info);
        Exception caught;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        logger.Error("something failed", caught);

        var content = ReadTodayLog(_dir);
        Assert.Contains("something failed", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("boom", content);
    }

    [Fact]
    public void Error_WithNullException_DoesNotThrow_LogsPlainMessage()
    {
        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.Error("plain error", exception: null);

        var content = ReadTodayLog(_dir);
        Assert.Contains("[ERROR] plain error", content);
    }

    [Fact]
    public void Constructor_DefaultsToInfo()
    {
        var logger = new FileLogger(_dir);
        Assert.Equal(LogLevel.Info, logger.MinimumLevel);
    }

    [Fact]
    public void Constructor_WritablePrimaryDirectory_UsesPrimary_IgnoresFallback()
    {
        var fallbackDir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.Logging.Fallback." + Guid.NewGuid());
        var logger = new FileLogger(_dir, LogLevel.Info, fallbackLogDirectory: fallbackDir);

        Assert.False(logger.Disabled);
        Assert.Equal(_dir, logger.EffectiveLogDirectory);
        Assert.False(Directory.Exists(fallbackDir));
    }

    [Fact]
    public void Constructor_UnwritablePrimaryDirectory_FallsBackAndStillLogs()
    {
        // T1/B4: same technique as StateDirectoryTests — a FILE standing in for the primary "logs"
        // directory fails every write deterministically, exercising the exact fallback branch a
        // real ACL-denied Program-Files install would hit.
        var fakePrimaryDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(fakePrimaryDir, string.Empty);
        var fallbackDir = Path.Combine(_dir, "fallback-logs");

        var logger = new FileLogger(fakePrimaryDir, LogLevel.Info, fallbackLogDirectory: fallbackDir);
        logger.Info("fell back and still logged");

        Assert.False(logger.Disabled);
        Assert.Equal(fallbackDir, logger.EffectiveLogDirectory);
        Assert.Contains("fell back and still logged", ReadTodayLog(fallbackDir));
    }

    [Fact]
    public void Constructor_PrimaryAndFallbackBothUnwritable_DisablesRatherThanThrows()
    {
        var fakePrimaryDir = Path.Combine(_dir, "not-actually-a-directory-1.tmp");
        var fakeFallbackDir = Path.Combine(_dir, "not-actually-a-directory-2.tmp");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(fakePrimaryDir, string.Empty);
        File.WriteAllText(fakeFallbackDir, string.Empty);

        var exception = Record.Exception(() =>
        {
            var logger = new FileLogger(fakePrimaryDir, LogLevel.Info, fallbackLogDirectory: fakeFallbackDir);
            logger.Info("must not throw even though nothing is writable");
            Assert.True(logger.Disabled);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_NoFallbackProvided_UnwritablePrimary_DisablesRatherThanThrows()
    {
        var fakePrimaryDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(fakePrimaryDir, string.Empty);

        var logger = new FileLogger(fakePrimaryDir);

        Assert.True(logger.Disabled);
    }

    // --- T6/R7: cross-user same-day log file ---

    private static string SanitizedCurrentUserName()
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(Environment.UserName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
    }

    [Fact]
    public void Write_PrimaryDailyFileUnwritable_RetriesWithPerUserFilename()
    {
        Directory.CreateDirectory(_dir);
        var logger = new FileLogger(_dir, LogLevel.Info);
        var primaryPath = Path.Combine(_dir, $"gridlookout-{DateTime.Now:yyyyMMdd}.log");
        File.WriteAllText(primaryPath, string.Empty);
        File.SetAttributes(primaryPath, FileAttributes.ReadOnly);

        try
        {
            logger.Info("cross-user retry message");

            var perUserPath = Path.Combine(_dir, $"gridlookout-{DateTime.Now:yyyyMMdd}-{SanitizedCurrentUserName()}.log");
            Assert.True(File.Exists(perUserPath));
            Assert.Contains("cross-user retry message", File.ReadAllText(perUserPath));
        }
        finally
        {
            File.SetAttributes(primaryPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Write_PrimaryAndPerUserBothUnwritable_DoesNotThrow_DropsLine()
    {
        Directory.CreateDirectory(_dir);
        var logger = new FileLogger(_dir, LogLevel.Info);
        var primaryPath = Path.Combine(_dir, $"gridlookout-{DateTime.Now:yyyyMMdd}.log");
        var perUserPath = Path.Combine(_dir, $"gridlookout-{DateTime.Now:yyyyMMdd}-{SanitizedCurrentUserName()}.log");
        File.WriteAllText(primaryPath, string.Empty);
        File.WriteAllText(perUserPath, string.Empty);
        File.SetAttributes(primaryPath, FileAttributes.ReadOnly);
        File.SetAttributes(perUserPath, FileAttributes.ReadOnly);

        try
        {
            var exception = Record.Exception(() => logger.Info("dropped on the floor — must never throw"));
            Assert.Null(exception);
        }
        finally
        {
            File.SetAttributes(primaryPath, FileAttributes.Normal);
            File.SetAttributes(perUserPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Write_PrimaryWritable_NeverCreatesPerUserFile()
    {
        // No-regression check: the overwhelmingly common case (primary file writable) must not
        // start creating a second file per line.
        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.Info("normal write");

        var perUserPath = Path.Combine(_dir, $"gridlookout-{DateTime.Now:yyyyMMdd}-{SanitizedCurrentUserName()}.log");
        Assert.False(File.Exists(perUserPath));
    }

    // --- T2: ApplyRetention ---

    [Fact]
    public void ApplyRetention_DeletesFilesOlderThanCutoff_KeepsNewerOnes()
    {
        Directory.CreateDirectory(_dir);
        var oldPath = Path.Combine(_dir, "gridlookout-20200101.log");
        var newPath = Path.Combine(_dir, "gridlookout-20260101.log");
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(newPath, "new");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow.AddDays(-2));

        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.ApplyRetention(30);

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public void ApplyRetention_Zero_DisablesPruning_KeepsEverything()
    {
        Directory.CreateDirectory(_dir);
        var oldPath = Path.Combine(_dir, "gridlookout-20200101.log");
        File.WriteAllText(oldPath, "old");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-400));

        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.ApplyRetention(0);

        Assert.True(File.Exists(oldPath));
    }

    [Fact]
    public void ApplyRetention_NegativeValue_AlsoDisablesPruning()
    {
        Directory.CreateDirectory(_dir);
        var oldPath = Path.Combine(_dir, "gridlookout-20200101.log");
        File.WriteAllText(oldPath, "old");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-400));

        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.ApplyRetention(-1);

        Assert.True(File.Exists(oldPath));
    }

    [Fact]
    public void ApplyRetention_PerUserVariant_AlsoPruned()
    {
        Directory.CreateDirectory(_dir);
        var perUserPath = Path.Combine(_dir, $"gridlookout-20200101-{SanitizedCurrentUserName()}.log");
        File.WriteAllText(perUserPath, "old");
        File.SetLastWriteTimeUtc(perUserPath, DateTime.UtcNow.AddDays(-40));

        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.ApplyRetention(30);

        Assert.False(File.Exists(perUserPath));
    }

    [Fact]
    public void ApplyRetention_LockedFile_DoesNotThrow_OtherOldFilesStillPruned()
    {
        // Same technique as Write_PrimaryDailyFileUnwritable_RetriesWithPerUserFilename above —
        // File.Delete throws UnauthorizedAccessException on a ReadOnly file, standing in for a
        // genuinely locked/permission-denied one.
        Directory.CreateDirectory(_dir);
        var lockedPath = Path.Combine(_dir, "gridlookout-20200101.log");
        var otherOldPath = Path.Combine(_dir, "gridlookout-20200102.log");
        File.WriteAllText(lockedPath, "old");
        File.WriteAllText(otherOldPath, "old");
        File.SetLastWriteTimeUtc(lockedPath, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(otherOldPath, DateTime.UtcNow.AddDays(-40));
        File.SetAttributes(lockedPath, FileAttributes.ReadOnly);

        try
        {
            var logger = new FileLogger(_dir, LogLevel.Info);
            var exception = Record.Exception(() => logger.ApplyRetention(30));

            Assert.Null(exception);
            Assert.True(File.Exists(lockedPath)); // still locked — couldn't delete, skipped, not fatal
            Assert.False(File.Exists(otherOldPath)); // unrelated old file still pruned
        }
        finally
        {
            File.SetAttributes(lockedPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void ApplyRetention_RemovedFiles_LogsInfoLineWithCount()
    {
        Directory.CreateDirectory(_dir);
        var oldPath1 = Path.Combine(_dir, "gridlookout-20200101.log");
        var oldPath2 = Path.Combine(_dir, "gridlookout-20200102.log");
        File.WriteAllText(oldPath1, "old");
        File.WriteAllText(oldPath2, "old");
        File.SetLastWriteTimeUtc(oldPath1, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(oldPath2, DateTime.UtcNow.AddDays(-40));

        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.ApplyRetention(30);

        var content = ReadTodayLog(_dir);
        Assert.Contains("removed 2 log file(s)", content);
    }

    [Fact]
    public void ApplyRetention_NothingToRemove_LogsNoLine()
    {
        Directory.CreateDirectory(_dir);
        var newPath = Path.Combine(_dir, "gridlookout-20260101.log");
        File.WriteAllText(newPath, "new");
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow.AddDays(-1));

        var logger = new FileLogger(_dir, LogLevel.Info);
        logger.ApplyRetention(30);

        // Nothing was removed, so ApplyRetention must never have called Info() — today's log file
        // shouldn't even exist yet.
        var todayPath = Path.Combine(_dir, $"gridlookout-{DateTime.Now:yyyyMMdd}.log");
        Assert.False(File.Exists(todayPath));
    }

    [Fact]
    public void ApplyRetention_DirectoryUnwritable_DoesNotThrow()
    {
        // Same "unwritable" simulation technique as the fallback-directory tests above: a FILE
        // stands in for the log directory so Directory.GetFiles/File.Delete fail deterministically.
        var fakeDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(fakeDir, string.Empty);
        var fallbackDir = Path.Combine(_dir, "fallback-logs");

        var logger = new FileLogger(fakeDir, LogLevel.Info, fallbackLogDirectory: fallbackDir);

        var exception = Record.Exception(() => logger.ApplyRetention(30));

        Assert.Null(exception);
    }
}
