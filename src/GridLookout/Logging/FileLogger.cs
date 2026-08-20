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
        var fileNamePrefix = $"gridlookout-{DateTime.Now:yyyyMMdd}";

        lock (_lock)
        {
            try
            {
                var path = Path.Combine(_logDirectory, $"{fileNamePrefix}.log");
                File.AppendAllText(path, line + Environment.NewLine);
                return;
            }
            catch
            {
                // T6/R7: fall through to the per-user retry below rather than dropping the line
                // immediately — two different Windows accounts (e.g. an interactive admin who ran
                // the app once, then the kiosk service account) can each hold their own
                // lock/exclusive-open on the SAME shared same-day log file, so this collision is a
                // real cross-user scenario in the field, not just hypothetical.
            }

            try
            {
                var perUserPath = Path.Combine(_logDirectory, $"{fileNamePrefix}-{SanitizeForFileName(Environment.UserName)}.log");
                File.AppendAllText(perUserPath, line + Environment.NewLine);
            }
            catch
            {
                // Both the shared and per-user files failed — degrade exactly like before this
                // retry existed: drop the line silently. Logging must never crash the wallboard.
            }
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
