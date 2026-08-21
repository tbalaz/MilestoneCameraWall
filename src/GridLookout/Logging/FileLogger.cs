namespace GridLookout.Logging;

/// <summary>Severity of a log line — see <see cref="FileLogger.MinimumLevel"/>.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Leveled plain-text log next to the exe (logs/gridlookout-yyyyMMdd.log), falling back to
/// a second directory if the first can't be written to. Line format:
/// "yyyy-MM-dd HH:mm:ss.fff [LEVEL] message".</summary>
public sealed class FileLogger
{
    private readonly string _logDirectory;
    private readonly object _lock = new();

    // m2 fix (2026-08-21 external audit) — all guarded by _lock:
    private int _retentionDays;                 // remembered so the daily rollover can re-run retention
    private string? _currentDayStamp;           // yyyyMMdd of the day the counters below describe
    private long _bytesWrittenToday;            // approximate (line lengths), seeded from the file on rollover/restart
    private bool _capNoticeWrittenToday;        // the one "cap reached" line per day
    private long _droppedSinceLastSuccess;      // lines lost to IO failure or the daily cap

    /// <summary>m2 fix: per-day byte cap, in megabytes — 0 disables. When a day's log reaches the
    /// cap, ONE final "cap reached" line is written and further lines are dropped (counted, and
    /// reported by a notice line on the next day's first successful write) until the day rolls
    /// over. Bounds a runaway Debug/error storm on an unattended kiosk that nobody is watching —
    /// pre-fix, only day-count retention existed and a single day could fill the disk. Settable
    /// like <see cref="MinimumLevel"/> (config is loaded after the logger exists).</summary>
    public int MaxMegabytesPerDay { get; set; } = 50;

    /// <summary>Messages below this level are dropped. Settable so Program.cs can bootstrap the
    /// logger at the default (<see cref="LogLevel.Info"/>) before camerawall.json is loaded, then
    /// apply the configured level once it's known.</summary>
    public LogLevel MinimumLevel { get; set; }

    /// <summary>True when neither the primary nor the fallback log directory could be prepared
    /// (last resort) — logging is disabled outright rather than throwing, since a wallboard must
    /// never fail to start over this. Exposed for tests/diagnostics.</summary>
    public bool Disabled { get; private set; }

    /// <summary>The directory actually in use (primary or fallback) — null if <see cref="Disabled"/>.</summary>
    public string? EffectiveLogDirectory { get; }

    /// <param name="logDirectory">Primary log directory — normally next to the exe.</param>
    /// <param name="minimumLevel">See <see cref="MinimumLevel"/>.</param>
    /// <param name="fallbackLogDirectory">
    /// B4 fix: tried when <paramref name="logDirectory"/> can't be created/written to (e.g. a
    /// limited kiosk account under a read-only %ProgramFiles% install) — normally
    /// %ProgramData%\GridLookout\logs, see <see cref="GridLookout.Config.StateDirectory"/>. Before
    /// this fallback existed, an unwritable primary directory silently dropped every log line
    /// (see docs/critiques/gridlookout-panel-1.md finding B4/S2) — now the fallback is tried, and
    /// only if THAT also fails does logging fall back further to <see cref="Disabled"/> (still
    /// never throwing). Null (default) skips straight to that last resort, same as before this
    /// parameter existed.
    /// </param>
    public FileLogger(string logDirectory, LogLevel minimumLevel = LogLevel.Info, string? fallbackLogDirectory = null)
    {
        MinimumLevel = minimumLevel;

        if (TryPrepareDirectory(logDirectory))
        {
            _logDirectory = logDirectory;
            EffectiveLogDirectory = logDirectory;
            return;
        }

        if (fallbackLogDirectory is not null && TryPrepareDirectory(fallbackLogDirectory))
        {
            _logDirectory = fallbackLogDirectory;
            EffectiveLogDirectory = fallbackLogDirectory;
            return;
        }

        // Last resort: neither directory is usable — disable rather than throw. A wallboard must
        // never fail to start because logging couldn't be set up.
        _logDirectory = logDirectory;
        Disabled = true;
    }

    /// <summary>Creates <paramref name="directory"/> if needed and proves it's actually writable
    /// with a real probe write+delete — Directory.CreateDirectory alone can succeed (directory
    /// already exists) while writes into it still fail on ACL, so create-only is not sufficient.</summary>
    private static bool TryPrepareDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, $".gridlookout-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>T2: deletes gridlookout-*.log files (the glob catches both the normal daily file
    /// and the per-user cross-user-retry variant — see <see cref="Write"/> — since both live in the
    /// same effective log directory and share the same prefix) whose last-write time is older than
    /// <paramref name="retentionDays"/> days. Bootstrap-order note: this logger is constructed
    /// BEFORE camerawall.json is read (see <see cref="MinimumLevel"/>'s own doc comment for why),
    /// so retention can't run in the constructor either — call this once, right after
    /// <see cref="MinimumLevel"/> is applied from the loaded config, same pattern. <c>0</c> (or
    /// negative) disables pruning — keep forever. Never throws: a single locked/permission-denied
    /// file is skipped (not fatal — it just isn't removed this run, and gets another chance next
    /// time), and any directory-listing failure is swallowed outright — a wallboard must never fail
    /// to start over housekeeping. Logs one Info line naming how many files were removed, but only
    /// when that count is &gt; 0, so an ordinary run with nothing to prune stays silent.</summary>
    public void ApplyRetention(int retentionDays)
    {
        // m2 fix: remembered so the day-rollover check in Write can re-run this sweep — pre-fix
        // retention ran exactly once at boot, so a wall that stays up for months never pruned
        // again until its next restart.
        _retentionDays = retentionDays;

        if (retentionDays <= 0 || Disabled)
        {
            return;
        }

        try
        {
            var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
            int removed = 0;
            foreach (var path in Directory.GetFiles(_logDirectory, "gridlookout-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch
                {
                    // A single locked/permission-denied file must not abort the sweep — leave it,
                    // it gets another chance on the next start.
                }
            }

            if (removed > 0)
            {
                Info($"Log retention: removed {removed} log file(s) older than {retentionDays} day(s) from '{_logDirectory}'.");
            }
        }
        catch
        {
            // Retention must never throw or block startup — same never-throw discipline as
            // TryPrepareDirectory/Write above.
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warning(string message) => Write(LogLevel.Warning, message);

    public void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>Error variant that appends <paramref name="exception"/>'s full <c>ToString()</c>
    /// (type, message, stack trace) after the message.</summary>
    public void Error(string message, Exception? exception)
    {
        if (exception is null)
        {
            Write(LogLevel.Error, message);
            return;
        }
        Write(LogLevel.Error, $"{message}{Environment.NewLine}{exception}");
    }

    private void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel || Disabled)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Tag(level)}] {message}";
        var dayStamp = DateTime.Now.ToString("yyyyMMdd");
        var fileNamePrefix = $"gridlookout-{dayStamp}";

        lock (_lock)
        {
            // m2 fix: day rollover — reset the per-day byte counter (seeding it from the file's
            // existing length so a mid-day RESTART doesn't reset the cap) and re-run retention so
            // a long-running wall keeps pruning without ever restarting.
            if (_currentDayStamp != dayStamp)
            {
                _currentDayStamp = dayStamp;
                _capNoticeWrittenToday = false;
                _bytesWrittenToday = 0;
                try
                {
                    var existing = Path.Combine(_logDirectory, $"{fileNamePrefix}.log");
                    if (File.Exists(existing))
                    {
                        _bytesWrittenToday = new FileInfo(existing).Length;
                    }
                }
                catch
                {
                    // Seeding is best-effort — an unreadable length just means the cap is measured
                    // from here rather than from the true file size.
                }

                if (_retentionDays > 0)
                {
                    // Runs holding _lock — fine: the lock is reentrant for ApplyRetention's own
                    // Info line, whose Write re-entry sees the already-updated day stamp and cannot
                    // recurse back into this branch; the sweep itself is a quick once-a-day listing.
                    ApplyRetention(_retentionDays);
                }
            }

            // m2 fix: per-day byte cap — one final line marks the cutoff, everything after is
            // counted as dropped until the next day.
            long capBytes = (long)MaxMegabytesPerDay * 1024 * 1024;
            if (capBytes > 0 && _bytesWrittenToday >= capBytes)
            {
                if (!_capNoticeWrittenToday)
                {
                    _capNoticeWrittenToday = true;
                    TryAppend(fileNamePrefix, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] Daily log cap reached ({MaxMegabytesPerDay} MB) — further lines today are dropped and counted; raise LogMaxMegabytesPerDay in camerawall.json if this cap is too tight for this site.");
                }
                _droppedSinceLastSuccess++;
                return;
            }

            // m2 fix: a run of dropped lines (cap, disk full, both files locked) is no longer
            // silent forever — the first successful write afterwards is preceded by one notice
            // naming the count.
            if (_droppedSinceLastSuccess > 0)
            {
                var dropped = _droppedSinceLastSuccess;
                _droppedSinceLastSuccess = 0;
                TryAppend(fileNamePrefix, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] {dropped} log line(s) were dropped since the last successful write (daily cap, disk full, or the log file was locked).");
            }

            if (!TryAppend(fileNamePrefix, line))
            {
                _droppedSinceLastSuccess++;
            }
        }
    }

    /// <summary>The two-stage append (shared daily file, then the per-user variant — T6/R7: two
    /// different Windows accounts can each hold their own exclusive open on the SAME shared
    /// same-day file, a real cross-user field scenario) extracted so <see cref="Write"/>'s cap and
    /// dropped-line accounting has ONE success/failure signal to count against. Adds the written
    /// length to the per-day byte counter on success. Never throws.</summary>
    private bool TryAppend(string fileNamePrefix, string line)
    {
        try
        {
            var path = Path.Combine(_logDirectory, $"{fileNamePrefix}.log");
            File.AppendAllText(path, line + Environment.NewLine);
            _bytesWrittenToday += line.Length + Environment.NewLine.Length;
            return true;
        }
        catch
        {
            // Fall through to the per-user retry.
        }

        try
        {
            var perUserPath = Path.Combine(_logDirectory, $"{fileNamePrefix}-{SanitizeForFileName(Environment.UserName)}.log");
            File.AppendAllText(perUserPath, line + Environment.NewLine);
            _bytesWrittenToday += line.Length + Environment.NewLine.Length;
            return true;
        }
        catch
        {
            // Both files failed — the caller counts the drop; logging must never crash the wall.
            return false;
        }
    }

    /// <summary>Replaces any character invalid in a Windows file name with '_' — the per-user log
    /// retry (see <see cref="Write"/>) embeds <see cref="Environment.UserName"/> directly in a
    /// file name, and a domain/local account name is not guaranteed to already be filename-safe.</summary>
    private static string SanitizeForFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => level.ToString().ToUpperInvariant(),
    };
}
