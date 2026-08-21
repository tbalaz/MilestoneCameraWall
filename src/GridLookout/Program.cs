using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text.Json;
using System.Windows.Forms;
using GridLookout.Auth;
using GridLookout.Config;
using GridLookout.Interop;
using GridLookout.Layout;
using GridLookout.Logging;
using GridLookout.Milestone;
using GridLookout.Monitoring;
using GridLookout.Recovery;
using GridLookout.UI;
using static GridLookout.Interop.CommandLineQuoting;

namespace GridLookout;

internal static class Program
{
    // Feature 2 (wall-health monitoring) probe-mode console attach — see RunHealthProbeMode.
    // GridLookout.csproj sets OutputType WinExe, so a --health-probe invocation launched from an
    // existing console (an admin running it by hand, or the watchdog script) would otherwise never
    // see its printed verdict: a GUI-subsystem process gets no console handle at all unless it asks
    // for one. The exit code still works either way (that's what the watchdog script actually acts
    // on) — this only recovers the printed JSON line for a human running the probe interactively.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    [STAThread]
    private static void Main(string[] args)
    {
        // Writable-state resolution (B4 fix) is computed FIRST, before anything WinForms-related —
        // both --health-probe mode (below) and the normal boot path need it, and probe mode must
        // run before Application.EnableVisualStyles()/any WinForms init at all (see
        // RunHealthProbeMode's doc comment). If the exe directory (e.g. a non-admin kiosk account
        // under %ProgramFiles%) can't be written to, both the logger and the config loader fall
        // back to %ProgramData%\GridLookout instead of failing — see StateDirectory, FileLogger's
        // fallbackLogDirectory param below, and WallConfigLoader's state-dir merge/rewrite-target
        // logic.
        var baseDir = AppContext.BaseDirectory;
        var stateDirectory = new StateDirectory();
        stateDirectory.Resolve(baseDir, out var stateDir);

        if (args.Any(a => string.Equals(a, "--health-probe", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunHealthProbeMode(baseDir, stateDirectory));
            return;
        }

        // Remote screenshot feature: same "before the mutex, before any WinForms init" placement as
        // --health-probe immediately above — a --screenshot invocation runs ALONGSIDE an
        // already-running wall process (over ssh/psexec, from a different Windows session), so it
        // must never fight it for the single-instance mutex. Unlike --health-probe it does no file
        // I/O of its own beyond listing screen-*.png afterward — see ScreenshotRequester's own doc
        // comment for the full request/response protocol against the RUNNING wall's
        // ScreenshotResponder (armed further below, right after this process wins the mutex).
        if (ScreenshotRequester.IsRequested(args))
        {
            Environment.Exit(RunScreenshotRequestMode(baseDir, stateDirectory));
            return;
        }

        // F3 point 9: a one-shot setup utility, not a wall-mode run — same "before the mutex, before
        // any WinForms init" placement as --health-probe above, so it can run alongside an already-
        // running wall without fighting it for the single-instance lock. Unlike --health-probe it
        // DOES perform a real MIP login (it needs the live camera catalog) — see
        // RunExportCameraBindingsMode's own doc comment for why that's still safe to run alongside a
        // live wall process.
        if (args.Any(a => string.Equals(a, "--export-camera-bindings", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunExportCameraBindingsMode(baseDir, stateDirectory, args));
            return;
        }

        // ApplicationConfiguration.Initialize() is a net6+-only generated helper (relies on
        // MSBuild-generated ApplicationConfiguration.g.cs, not available for net48) — the classic
        // net48 WinForms startup pair below is the direct equivalent.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Bootstrap order: the logger must exist before camerawall.json is loaded (so any
        // early/config-load failure is never silently lost), so it starts at the LogLevel
        // default (Info) and MinimumLevel is applied once the config is known, below.
        var logger = new FileLogger(Path.Combine(baseDir, "logs"), fallbackLogDirectory: Path.Combine(stateDir, "logs"));

        // T7(a)/R8: crash-loop backoff for the fatal-exception relaunch below — see
        // CrashRelaunchGuard's doc comment. Lives in the same writable state dir as the config
        // fallback (ProgramData when the exe dir isn't writable, the exe dir itself otherwise).
        var crashGuard = new CrashRelaunchGuard(stateDir);

        using var mutex = new Mutex(initiallyOwned: true, "Global\\GridLookout.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            logger.Warning("Another instance is already running — exiting.");
            return;
        }

        // Remote screenshot feature: created HERE, immediately after winning the single-instance
        // mutex — see ScreenshotResponder's own doc comment for the full request/response protocol
        // and, specifically, for why construction at THIS point (before LoginRetryLoop even starts,
        // before any WinForms Control/Form exists on this thread) is deliberate, not incidental: a
        // remote operator sanity-checking an unattended kiosk needs a screenshot to work even while
        // the wall is still showing its "Connecting to Management Server..." status card, or stuck
        // on a retry loop during an outage — exactly the situations a remote check is most useful
        // for. Wrapped in try/catch: creating a Global\-namespaced EventWaitHandle can fail outright
        // on an account that lacks the "Create global objects" privilege (SeCreateGlobalPrivilege —
        // granted by default to Administrators/SYSTEM/service accounts, not to a plain standard-user
        // kiosk account) — same never-let-an-optional-subsystem-crash-the-wall discipline every
        // other background feature in this file already follows (health timer, power-guard timer).
        // A failure here means --screenshot will report "GridLookout is not running." from a remote
        // shell — indistinguishable from the wall genuinely not running — until the account's
        // privilege is fixed; degraded, never fatal to the wall itself. Kept alive for the whole
        // process life (see its own disposal at the end of this method and at both early-return
        // paths below) — NOT recreated by RecoverSession, so it stays armed across every
        // session-recovery cycle, satisfying that requirement with no extra code: RecoverSession
        // simply never touches it.
        ScreenshotResponder? screenshotResponder = null;
        try
        {
            screenshotResponder = new ScreenshotResponder(ScreenshotPaths.ResolveWritableScreenshotDirectory(stateDirectory, baseDir), logger);
        }
        catch (Exception ex)
        {
            logger.Warning($"Remote screenshot responder could not be created ({ex.GetType().Name}: {ex.Message}) — --screenshot will report 'GridLookout is not running.' from a remote shell until this is fixed (check the account's 'Create global objects' privilege).");
        }

        // M3 fix: see the ThreadException subscription right below for what these feed. int (not
        // long) is fine — a wall throwing two billion UI exceptions has bigger problems; DateTime?
        // assignment is atomic enough for a diagnostic timestamp read by a 5s timer.
        int uiThreadExceptionCount = 0;
        DateTime? lastUiThreadExceptionUtc = null;

        // M3 fix (2026-08-21 external audit): WinForms routes UI-thread exceptions HERE, never to
        // AppDomain.UnhandledException below — so pre-fix these were logged and swallowed with no
        // health impact at all: an exception mid-RebuildWall could leave a half-torn wall whose
        // pulse timer kept ticking, invisible to the crash-relaunch guard AND --health-probe alike.
        // Product decision (owner): DEGRADE health, never relaunch — a transient tick exception
        // must not cost a wall restart, but health must stop claiming Healthy once UI integrity
        // can't be assumed. Sticky by design — see WallHealthState.UiThreadExceptionCount's doc
        // comment. Interlocked: the handler runs on the UI thread today, but nothing in the
        // ThreadException contract promises that for every message-loop configuration.
        Application.ThreadException += (_, e) =>
        {
            Interlocked.Increment(ref uiThreadExceptionCount);
            lastUiThreadExceptionUtc = DateTime.UtcNow;
            logger.Error("UI thread exception (wall keeps running; health reports Degraded until restart)", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception unhandledException)
            {
                logger.Error("FATAL unhandled exception — relaunching and exiting this process", unhandledException);
            }
            else
            {
                logger.Error($"FATAL unhandled exception (non-Exception payload) — relaunching and exiting this process: {e.ExceptionObject}");
            }

            // T7(a)/R8: crash-loop backoff — checked AFTER logging the crash itself (so the crash
            // is always on record) but BEFORE the mutex/relaunch dance below; no point releasing
            // the mutex or starting a child process this decision is about to veto.
            if (!crashGuard.ShouldRelaunch(DateTime.UtcNow))
            {
                logger.Error("FATAL: crash loop detected — GridLookout already relaunched itself 5 times within the last 10 minutes; this crash (the 6th) will not trigger another relaunch. Manual intervention needed.");
                Environment.Exit(1);
                return;
            }

            // E5 fix: Application.Restart() raced its own single-instance check — it started the
            // replacement process before this process's mutex handle was guaranteed released, so
            // the child often saw createdNew=false (thought another instance was running) and
            // exited immediately, leaving no wall at all. Sequence instead: release THIS process's
            // mutex handle, THEN start the child, THEN exit — ordering that guarantees the child
            // never loses the race.
            //
            // Dispose() is used deliberately, NOT ReleaseMutex(): this handler can run on ANY
            // thread (AppDomain.UnhandledException is not confined to the UI thread that created
            // the mutex with initiallyOwned: true), and ReleaseMutex() throws
            // ApplicationException when called from a thread that doesn't own the mutex — a real
            // possibility for a background-thread SDK callback exception. Dispose() only closes
            // THIS process's handle to the named kernel object and needs no ownership; with no
            // other instance running, this is the last open handle, so Windows destroys the named
            // mutex immediately, and the child's own createdNew check is then guaranteed to see
            // the name as free. No child-side retry loop is needed — one correct mechanism, not two.
            try
            {
                mutex.Dispose();
            }
            catch
            {
                // Must never block the restart attempt itself.
            }

            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    // ProcessStartInfo.ArgumentList isn't available on net48 (added in
                    // netstandard2.1+, and this project deliberately stays net48 — see the
                    // TargetFramework comment in GridLookout.csproj) — build a quoted command line
                    // string instead via CommandLineQuoting.BuildArgumentString (imported below via
                    // `using static`, so it's called unqualified as BuildArgumentString(args)).
                    var startInfo = new ProcessStartInfo(exePath)
                    {
                        UseShellExecute = false,
                        Arguments = BuildArgumentString(args),
                    };
                    Process.Start(startInfo);
                }
            }
            catch (Exception restartEx)
            {
                logger.Error("Failed to relaunch after fatal exception", restartEx);
            }

            Environment.Exit(1);
        };

        string? recorderArg = GetArgValue(args, "--recorder");
        string? monitorArg = GetArgValue(args, "--monitor");
        bool protectPasswordOnly = args.Any(a => string.Equals(a, "--protect-password", StringComparison.OrdinalIgnoreCase));

        var protector = new DpapiSecretProtector();
        // T3/R3/T4/R4/T5/R6: forwards WallConfigLoader's diagnostic callback to the real logger —
        // see WallConfigLoader's constructor doc comment.
        var loader = new WallConfigLoader(protector, stateDirectory, (level, msg) =>
        {
            switch (level)
            {
                case LogLevel.Debug: logger.Debug(msg); break;
                case LogLevel.Warning: logger.Warning(msg); break;
                case LogLevel.Error: logger.Error(msg); break;
                default: logger.Info(msg); break;
            }
        });

        // m1 fix (2026-08-21 external audit): boot-time sweep of orphaned atomic-write temp files
        // (config, health/layout state, screenshot PNGs) — see AtomicStateStore.SweepOrphanedTempFiles.
        // Age-gated inside the sweep; wrapped anyway: housekeeping must never delay or fail boot.
        try
        {
            stateDirectory.Resolve(baseDir, out var sweepStateDir);
            AtomicStateStore.SweepOrphanedTempFiles(baseDir);
            AtomicStateStore.SweepOrphanedTempFiles(sweepStateDir);
            foreach (var screenshotDir in ScreenshotPaths.CandidateScreenshotDirectories(stateDirectory, baseDir))
            {
                AtomicStateStore.SweepOrphanedTempFiles(screenshotDir);
            }
        }
        catch
        {
            // Pure housekeeping — never let it interfere with boot.
        }

        WallConfig config;
        try
        {
            config = loader.LoadOrCreate(baseDir);
        }
        catch (Exception ex)
        {
            // T1/B4 defense in depth: the state-dir fallback above should make this unreachable in
            // practice, but if it somehow still throws (e.g. ProgramData itself is inaccessible),
            // show the generic error card instead of letting an unhandled exception here reach the
            // fatal-restart handler and crash-loop with zero visible on-screen feedback.
            logger.Error("Config load/migration failed", ex);
            ShowConfigLoadFailedCard();
            return;
        }
        logger.MinimumLevel = config.LogLevel;
        // T2: same bootstrap-order reason as MinimumLevel above — the logger exists before
        // camerawall.json is read, so retention pruning can't run in FileLogger's constructor
        // either; apply it here, once, right after the config that carries the setting is known.
        // (m2 fix: ApplyRetention also REMEMBERS the day count now, so the logger re-runs the
        // sweep on every day rollover instead of only at boot.)
        logger.ApplyRetention(config.LogRetentionDays);
        logger.MaxMegabytesPerDay = config.LogMaxMegabytesPerDay;

        // T8(d)/R10: one line, every start, naming exactly where a support engineer should look —
        // both the log directory (B4's writable-state fallback means it's not always "next to the
        // exe") and which config file this run treated as authoritative (T3's reseed logic means
        // that's not always the exe-dir file either).
        logger.Info($"Effective log directory: {DescribeLogDirectory(logger)}. Effective config path: {loader.EffectiveConfigPath ?? "(unknown)"}.");

        // S8: the whole point of KioskLock is that once it's active there is no on-screen way left
        // to tell — Esc, Alt+F4/SC_CLOSE/taskbar-close (T1/R1, see WallForm.OnFormClosing +
        // KioskCloseGuard), and the double-click compact toggle are all no-ops on every WallForm
        // this run creates (status/retry cards included, see LoginRetryLoop below). This line is
        // the only place that fact is ever visible, and only to whoever can read the log.
        if (config.KioskLock)
        {
            logger.Info("KioskLock is active: Esc, Alt+F4, and window-close (taskbar/system-menu) are disabled on wall windows and connection-retry status cards; the double-click compact/fullscreen toggle is disabled too. Configuration-missing and configuration-load-failed cards remain closable (they mean \"not configured\" — locking those would brick the box). Operator stop path is Task Manager or an MSI uninstall.");
        }

        if (protectPasswordOnly)
        {
            logger.Info("Ran with --protect-password: any plaintext Password has been migrated to PasswordProtected. Exiting.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ManagementServerUri))
        {
            logger.Warning("ManagementServerUri is not configured — showing config-missing card; exiting when it closes.");
            // T6 follow-up: this card used to recompute Path.Combine(baseDir, "camerawall.json")
            // itself, which silently diverged from reality on an unwritable exe dir — T6's whole
            // point is that the admin now has a real, commented, editable file at
            // loader.EffectiveConfigPath (the state-dir copy in that case), and pointing the card
            // at baseDir instead sent a kiosk admin to edit a file that doesn't exist in a
            // directory their account can't write to. Falls back to the old baseDir computation
            // only in the defensive case where EffectiveConfigPath is somehow still null (shouldn't
            // happen — LoadOrCreate always sets it before returning).
            ShowConfigMissingCard();
            return;
        }

        var uri = new Uri(config.ManagementServerUri);
        RecorderLocator.Logger = logger;
        Layout.LayoutSpecParser.Logger = logger;
        Layout.LayoutResolver.Logger = logger;
        RecorderCatalog.Logger = logger;
        var session = new MilestoneSession(uri, logger);
        session.Initialize();

        // FIX 4 (poll off the UI thread): the live-description REST poll used to run synchronously
        // inside the refresh timer's Tick handler (a 10s-timeout HTTP call on the UI thread — see
        // MilestoneSession.TryGetRecorderDescriptions). Constructed once, for the life of the
        // process; the refresh tick below only ever calls TriggerPollIfIdle (never blocks) and reads
        // Latest (a thread-safe snapshot) — see DescriptionPollWorker's own doc comment.
        // config.AllowInsecureLayoutPoll is captured by value here — safe, camerawall.json is loaded
        // once at startup with no hot-reload, same as every other config field this file treats as a
        // run-for-life constant.
        var descriptionPoll = new DescriptionPollWorker(() => session.TryGetRecorderDescriptions(config.AllowInsecureLayoutPoll), logger);

        // F3 (referentially stable layouts): CameraBindings is static config for the whole process
        // lifetime (camerawall.json is only read once at LoadOrCreate above — same as every other
        // setting), so it's validated exactly ONCE here rather than on every rebuild — re-validating
        // per rebuild would re-log the same "bad alias/guid" warnings on every description/camera-list
        // change for the rest of the run, which is pure log spam for something that can only change
        // via a restart anyway. See Layout.CameraBindingResolver's own doc comment for the validation
        // rules (bad entries are dropped and warned, never fatal to the rest of the file).
        var cameraBindings = Layout.CameraBindingResolver.Resolve(config.CameraBindings, msg => logger.Warning(msg));

        // F2 (multi-recorder walls): selection precedence (highest first) is --recorder CLI arg
        // (forces legacy single-recorder mode, unchanged) > non-empty RecordingServers[] (multi
        // mode) > RecorderNameOverride > hostname self-location — see WallConfig.RecordingServers's
        // doc comment. RecordingServers[] SHAPE (both/neither Id/HostName, unparseable guid,
        // duplicate entries) is validated exactly ONCE here, at startup — same "validate once, warn
        // immediately" discipline cameraBindings above already follows, for the identical reason:
        // this can only change via a restart (F2 excludes RecordingServers[] hot-reload from v1), so
        // re-validating on every refresh tick would just re-log the same warnings forever. Whether a
        // valid selector matches a LIVE recorder is a separate, dynamic question only answerable
        // against a live RecorderCatalog.Discover() result — see RecorderCatalog.Select, called from
        // both the boot LoginRetryLoop and the refresh tick below.
        bool multiRecorderMode = RecorderCatalog.IsMultiRecorderMode(recorderArg, config.RecordingServers.Count);
        var recordingServerSelectors = multiRecorderMode
            ? RecorderCatalog.ValidateSelectors(
                config.RecordingServers.Select(s => new RawRecordingServerEntry(s.Id, s.HostName)).ToList(),
                msg => logger.Warning(msg))
            : Array.Empty<RecordingServerSelector>();

        if (multiRecorderMode)
        {
            logger.Info($"Multi-recorder mode: {recordingServerSelectors.Count} valid RecordingServers[] selector(s) configured.");
            WarnIfMergedOrdinalsUsed(config, logger);
        }

        // F3: last-known-good layout store — same AtomicStateStore mechanism health.json already
        // uses (atomic temp-file + File.Replace, same writable-state-directory resolution), a
        // second file name in that directory. Always constructed (not gated behind any config flag)
        // — F3 has no separate enable/disable switch: it's simply how $layout{} has always worked,
        // now referentially stable. An empty/never-created layout-state.json is treated as a cold
        // start (see LoadLayoutState below), so this costs nothing on a wall that never uses
        // $layout{} at all.
        var layoutStateStore = new AtomicStateStore(stateDirectory, baseDir);

        var statusScreen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        string effectiveRecorderOverride = !string.IsNullOrWhiteSpace(recorderArg) ? recorderArg! : config.RecorderNameOverride;

        // F2 (multi-recorder walls): mutable across boot/recovery/refresh-tick — see
        // LogSelectionProblemsOnChange's doc comment for why lastSelectionProblems exists at all
        // (log a dynamic "selector matched nothing" problem only on CHANGE, not every tick).
        // lastSelectedRecorders/lastUnavailableByRecorder feed the health-write timer's per-recorder
        // rollup (see it further below) and are updated only on a SUCCESSFUL rebuild, mirroring
        // lastDescription/lastCameraSignature's own "commit only after RebuildWall succeeds" rule.
        IReadOnlyList<string> lastSelectionProblems = Array.Empty<string>();
        IReadOnlyList<RecorderDescriptor>? lastSelectedRecorders = null;
        IReadOnlyDictionary<Guid, int> lastUnavailableByRecorder = new Dictionary<Guid, int>();

        // Layout-carrier recorder feature: same log-on-change discipline as lastSelectionProblems
        // above, tracking RecorderCatalog.ResolveLayoutCarrier's own Problem (wrapped as a 0-or-1-
        // element list) instead of RecorderCatalog.Select's. Deliberately a SEPARATE variable rather
        // than folded into lastSelectionProblems — a LayoutRecorder-not-in-selection problem and a
        // RecordingServers-entry-matched-nothing problem are different conditions with independent
        // on/off edges; merging them would make LogSelectionProblemsOnChange re-log BOTH every time
        // either one changes.
        IReadOnlyList<string> lastLayoutCarrierProblems = Array.Empty<string>();

        // FIX 2 (pinned carrier authority): set on every LoginRetryLoop attempt (boot AND every
        // RecoverSession re-entry, since both funnel through the same loop), and — round-4
        // buyer-review fix — refreshed on every successful multi-mode refresh tick too, so it can't
        // go stale between recoveries. True when multi mode has an explicit LayoutRecorder that
        // currently matches no selected recorder. The immediately following RebuildWall call (boot
        // at Main's own scope, or inside RecoverSession) reads this as its carrierPinnedMissing
        // argument — see that parameter's doc comment for its two effects (render the persisted
        // last-known-good plan instead of the auto-grid, and never persist over layout-state.json
        // for that build). The refresh tick itself never REBUILDS off this state — it detects the
        // same condition fresh each tick and skips the rebuild entirely (see that branch's own
        // comment).
        bool multiCarrierPinnedMissing = false;

        // FIX 3-lite (insecure opt-in): log-on-change state for "the bearer-token description poll
        // was refused this tick because ManagementServerUri isn't HTTPS and AllowInsecureLayoutPoll
        // is false" — same discipline as lastSelectionProblems/lastLayoutCarrierProblems above.
        IReadOnlyList<string> lastInsecurePollProblems = Array.Empty<string>();

        // F3 transactional swap's own form list — moved up here (was previously declared just
        // before the boot RebuildWall call) so the health-write timer below (also moved up, buyer-
        // review defect #3 fix) can close over it from process start: an empty list at this point is
        // exactly correct — GetHealthSnapshot()/GetRecorderTileFacts() over zero forms during
        // Starting/Connecting is the expected transient shape HealthStatusCalculator.Compute now
        // understands (see its own doc comment) — RebuildWall (below) mutates this SAME list
        // in-place (Clear + AddRange) on every successful build; nothing about that changes here.
        var wallForms = new List<WallForm>();

        // Buyer-review defect #3 fix: health.json is now written from PROCESS START, not only after
        // the boot LoginRetryLoop already succeeded — a hang/crash during the very first login
        // attempt used to be completely invisible to --health-probe (no file existed yet at all,
        // indistinguishable from Health.Enabled simply being off). controllerState starts at
        // Starting (before the very first login attempt ever begins); LoginRetryLoop (below) sets it
        // to Connecting the moment it starts running, on BOTH the boot call and every RecoverSession
        // re-entry; RebuildWall's callers set it to Running/Recovering around each rebuild attempt,
        // exactly as before this fix.
        var controllerState = ControllerState.Starting;
        AtomicStateStore? healthStore = null;
        System.Windows.Forms.Timer? healthTimer = null;
        if (config.Health.Enabled)
        {
            healthStore = new AtomicStateStore(stateDirectory, baseDir);
            using var healthProcess = Process.GetCurrentProcess();
            var healthPid = healthProcess.Id;
            var healthProcessStartUtc = healthProcess.StartTime.ToUniversalTime();

            void WriteHealthState()
            {
                try
                {
                    var uiPulseUtc = DateTime.UtcNow;
                    var forms = wallForms.Select(f => f.GetHealthSnapshot()).ToList();
                    // Buyer-review defect #1 fix: a configured RecordingServers[] selector currently
                    // matching no live recorder is itself a Degraded signal — see
                    // HealthStatusCalculator.Compute's own doc comment and
                    // WallHealthState.RecorderSelectionIncomplete's own doc comment for why this is
                    // persisted rather than folded silently into OverallStatus alone.
                    bool recorderSelectionIncomplete = multiRecorderMode && lastSelectionProblems.Count > 0;
                    // FIX 2 (pinned carrier authority): a SEPARATE signal from
                    // recorderSelectionIncomplete above — see WallHealthState.LayoutCarrierPinned's
                    // own doc comment for why the two aren't folded into one field. Reuses
                    // lastLayoutCarrierProblems, already maintained (log-on-change) by both the boot
                    // LoginRetryLoop and the refresh tick's multi-mode branch — Problem is non-null
                    // there ONLY in the pinned-missing case (RecorderCatalog.ResolveLayoutCarrier never
                    // sets it for auto-carrier mode), so this is exactly the right signal with no new
                    // tracking variable needed.
                    bool layoutCarrierPinned = multiRecorderMode && lastLayoutCarrierProblems.Count > 0;
                    // See OverallStatus's own doc comment for why a SELF-written value here can now
                    // be Unhealthy too (Running with zero forms) — "ui pulse fresh" is trivially true
                    // at the exact moment this tick is running; a stale-PULSE Unhealthy verdict is
                    // still only ever reachable from the external --health-probe, recomputing from
                    // the file's AGE.
                    var overallStatus = HealthStatusCalculator.Compute(uiPulseFresh: true, controllerState, forms, recorderSelectionIncomplete || layoutCarrierPinned, uiThreadExceptionsObserved: Volatile.Read(ref uiThreadExceptionCount) > 0);
                    // F2 (multi-recorder walls): additive per-recorder rollup — empty list (and
                    // therefore no change to health.json's values) whenever RecordingServers[] is
                    // empty/absent. See BuildRecorderHealthList's own doc comment for how the LIVE
                    // per-tile facts (this tick) and the STATIC unavailable-by-recorder counts (last
                    // successful rebuild) are combined.
                    var recorders = multiRecorderMode
                        ? BuildRecorderHealthList(wallForms, lastSelectedRecorders, lastUnavailableByRecorder)
                        : new List<RecorderHealth>();
                    var state = new WallHealthState
                    {
                        // Blank in camerawall.json (the template ships it blank, commented as
                        // "defaults to the machine name") falls back here rather than in
                        // HealthConfig itself — the C# property default only applies when the JSON
                        // key is ABSENT, not when it's present-but-empty, and the template
                        // deliberately shows every key for discoverability.
                        ControllerId = string.IsNullOrWhiteSpace(config.Health.ControllerId) ? Environment.MachineName : config.Health.ControllerId,
                        Pid = healthPid,
                        ProcessStartUtc = healthProcessStartUtc,
                        UiPulseUtc = uiPulseUtc,
                        ControllerState = controllerState,
                        Forms = forms,
                        Recorders = recorders,
                        RecorderSelectionIncomplete = recorderSelectionIncomplete,
                        LayoutCarrierPinned = layoutCarrierPinned,
                        UiThreadExceptionCount = Volatile.Read(ref uiThreadExceptionCount),
                        LastUiThreadExceptionUtc = lastUiThreadExceptionUtc,
                        OverallStatus = overallStatus,
                        WrittenUtc = uiPulseUtc,
                    };
                    healthStore.Write(HealthProbe.HealthFileName, JsonSerializer.Serialize(state, HealthJsonOptions.Default));
                }
                catch (Exception ex)
                {
                    // Never let a health-write failure (disk full, permission surprise) affect the
                    // wall itself — log and retry on the next tick, same never-throw discipline
                    // every other background timer in this file already follows.
                    logger.Warning($"Health state write failed (will retry next tick): {ex.Message}");
                }
            }

            // Buyer-review defect #3 fix: write ONCE, synchronously, right now — before the timer
            // even starts — so "from process start" genuinely means from process start, not "from
            // process start plus up to 5s" (the first tick's own delay). This is the write that lets
            // --health-probe observe ControllerState.Starting during the very first login attempt,
            // and is also what makes the watchdog's new "absent health file + process running a long
            // time" hung-detection meaningful — see scripts/install-kiosk.ps1.
            WriteHealthState();

            healthTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            healthTimer.Tick += (_, _) => WriteHealthState();
            // Deliberately never stopped for RecoverSession's duration (unlike refreshTimer below)
            // — the health signal matters MOST during a recovery, which is exactly when an outside
            // observer most needs proof the UI thread is still pumping messages.
            healthTimer.Start();
        }

        // Shared by boot AND mid-session recovery (T2/B5/E1 — see RecoverSession further below).
        // Owns its own status/error-card form per call so a fresh card always shows current state;
        // loops internally on ordinary retries and only returns when either a recorder is matched
        // or the operator closes the card (signaled via operatorCancelled, never by returning null
        // while still retrying).
        RecorderMatch? LoginRetryLoop(out bool operatorCancelled)
        {
            // Buyer-review defect #3 fix: set here, once, so BOTH call sites (the boot call below
            // and every RecoverSession re-entry) drive the SAME transition — was previously set
            // explicitly inside RecoverSession right before its own call into this loop; the boot
            // call had no equivalent at all before this fix, which is exactly the gap that left
            // health.json unable to distinguish "still booting" from "not writing anything yet".
            controllerState = ControllerState.Connecting;

            // Status/error cards never show the header (recorder name isn't known yet at this
            // point anyway) — recorderName is passed as an empty string, harmlessly unused.
            // S8: this loop also runs mid-session (RecoverSession re-enters LoginRetryLoop) so a
            // locked kiosk must stay locked here too, not just once the wall is showing — otherwise
            // a passerby could wait out a VMS blip and Esc-exit during the retry card.
            var loopStatusForm = new WallForm(1, statusScreen, fallbackWarning: false, recorderName: string.Empty, showHeader: false, kioskLock: config.KioskLock);
            loopStatusForm.ShowStatus("Connecting to Management Server...", isError: false);
            loopStatusForm.Show();

            bool loopCancelled = false;
            loopStatusForm.FormClosed += (_, _) => loopCancelled = true;

            RecorderMatch? match = null;
            while (!loopCancelled)
            {
                try
                {
                    var password = loader.GetPassword(config);
                    var credentials = CredentialFactory.Build(config, password, uri);
                    session.Login(credentials);

                    RecorderMatch? located;
                    IReadOnlyList<string> candidates;
                    if (multiRecorderMode)
                    {
                        // F2: discover the FULL catalog (every recorder under the Management
                        // Server), select the configured subset, and — on success — synthesize a
                        // RecorderMatch the rest of this file's rendering pipeline (ComputeWallFormSpecs/
                        // RebuildWall) consumes exactly like single-recorder mode's own RecorderMatch —
                        // see BuildMultiRecorderMatch's own doc comment for why that reuse is safe.
                        var catalog = RecorderCatalog.Discover();
                        var selection = RecorderCatalog.Select(recordingServerSelectors, catalog);
                        lastSelectionProblems = LogSelectionProblemsOnChange(selection.Problems, lastSelectionProblems, logger);
                        candidates = catalog.Select(d => $"{d.Name} @{d.HostName}").ToList();

                        if (selection.Selected.Count == 0)
                        {
                            located = null;
                        }
                        else
                        {
                            // Layout-carrier recorder feature: BuildMultiRecorderMatch also resolves
                            // WHICH recorder is the layout carrier (RecorderCatalog.ResolveLayoutCarrier)
                            // and returns any problem with that resolution as data — logged here the
                            // same log-on-change way selection.Problems already is, right above.
                            var (multiMatch, layoutCarrierProblems, carrierPinnedMissing) = BuildMultiRecorderMatch(selection.Selected, config.LayoutRecorder, logger);
                            lastLayoutCarrierProblems = LogSelectionProblemsOnChange(layoutCarrierProblems, lastLayoutCarrierProblems, logger);
                            located = multiMatch;
                            lastSelectedRecorders = selection.Selected;
                            // FIX 2: recorded for the RebuildWall call this loop's caller makes right
                            // after it returns (boot at Main scope, or RecoverSession) — see
                            // multiCarrierPinnedMissing's own declaration-site comment.
                            multiCarrierPinnedMissing = carrierPinnedMissing;
                        }
                    }
                    else
                    {
                        located = RecorderLocator.Locate(effectiveRecorderOverride, out candidates);
                    }

                    if (located is null)
                    {
                        // SECURITY: the wall may hang in a public place — the on-screen error says
                        // nothing about the VMS (no recorder names, hosts, counts, or local hostname).
                        // Full diagnostics go to the log, readable only with filesystem access. Same
                        // PUBLIC-SCREEN RULE for multi mode: candidate recorder names/hosts are never
                        // shown, only logged.
                        var candidateText = candidates.Count == 0 ? "(none found)" : string.Join("\n  ", candidates);
                        loopStatusForm.ShowStatus(
                            $"No matching host.\n" +
                            $"Retrying in {config.ReconnectSeconds}s...",
                            isError: true);
                        logger.Warning(multiRecorderMode
                            ? $"Multi-recorder selection matched zero recorders. Candidates in the catalog (name @registered-host):\n  {candidateText}"
                            : $"Recorder locate failed for host '{Dns.GetHostName()}'. Fix: pass --recorder <host or name> " +
                              $"or set RecorderNameOverride in camerawall.json (prefer the @host — names are renameable). " +
                              $"Candidates (name @registered-host):\n  {candidateText}");
                        PumpDelay(config.ReconnectSeconds, loopStatusForm, ref loopCancelled);
                        continue;
                    }

                    match = located;
                    break;
                }
                catch (Exception ex)
                {
                    logger.Error("Login/locate attempt failed", ex);
                    // PUBLIC-SCREEN RULE (2026-08-19, user-reported regression): this card can hang
                    // in a lobby for hours during an outage — it must disclose NOTHING, not even a
                    // local filesystem path (a path leaks the Windows username and machine layout;
                    // the earlier T8(d)/R10 change that named the log folder here was wrong). The
                    // effective log location is in the startup Info log line and the admin guide.
                    // The config-error cards (ShowConfigMissingCard / ShowConfigLoadFailedCard)
                    // still name paths: they appear only on an unconfigured/broken box during
                    // setup, where the admin is stuck without the path — deliberate, documented.
                    loopStatusForm.ShowStatus($"Reconnecting in {config.ReconnectSeconds}s...", isError: true);
                    PumpDelay(config.ReconnectSeconds, loopStatusForm, ref loopCancelled);
                }
            }

            operatorCancelled = loopCancelled;
            if (loopCancelled)
            {
                // T5: the one exit-reason line for this path — covers both the boot retry and
                // mid-session recovery's re-entry into this same loop, since both funnel through here.
                logger.Info("Operator closed the status window during retry — exiting.");
            }
            else
            {
                // T1/R1: this codebase's own teardown, not a user/OS close — must go through
                // CloseInternal() or a KioskLock'd status card would refuse to close itself once
                // login succeeds, wedging both the boot path and every mid-session recovery.
                loopStatusForm.CloseInternal();
            }
            return match;
        }

        var recorderMatch = LoginRetryLoop(out bool bootCancelled);
        if (bootCancelled || recorderMatch is null)
        {
            // Buyer-review defect #3 fix: the health timer (if enabled) has been running since
            // before this call — stop/dispose it explicitly rather than relying on process exit to
            // clean it up implicitly, matching the disposal this file already performs at the normal
            // end of Application.Run() further below.
            healthTimer?.Stop();
            healthTimer?.Dispose();
            descriptionPoll.Shutdown();
            screenshotResponder?.Dispose();
            session.Logout();
            return;
        }

        // Application.Run() below has no main form — the wall may own one window per configured
        // monitor and none of them is "the" main one — so the message loop outlives every window
        // unless something calls Application.Exit. WallLifetime decides when that is: the operator
        // closing the last window, but never the refresh timer closing windows to rebuild them.
        // Without it, closing a wall left an invisible process still logged in to the Management
        // Server and still holding the KeepDisplayAwake assertion.
        var lifetime = new WallLifetime();
        // F3 transactional swap: at boot there is no "old set" to preserve, so this is the trivial
        // case of RebuildWall's contract (build the new set, "close old" is a no-op over an empty
        // list) — using the SAME path boot and every later rebuild use, rather than a separate
        // one-shot boot routine, is what guarantees boot and recovery/config-refresh can never
        // silently drift apart in how a layout is built. wallForms itself was declared much earlier
        // now (buyer-review defect #3 fix — see that declaration's own comment); RebuildWall mutates
        // it in place here exactly as it always has.
        if (!RebuildWall(recorderMatch, config, monitorArg, cameraBindings, layoutStateStore, wallForms, lifetime, logger, multiRecorderMode, out var bootUnavailableByRecorder, carrierPinnedMissing: multiCarrierPinnedMissing))
        {
            // Build failure with an EMPTY old set means the wall genuinely failed to come up at
            // all — unlike a later rebuild failure (which just keeps the previous wall running),
            // there is nothing to fall back to here. Exit cleanly rather than run headless.
            logger.Error("Initial wall build failed — exiting.", null);
            healthTimer?.Stop();
            healthTimer?.Dispose();
            descriptionPoll.Shutdown();
            screenshotResponder?.Dispose();
            session.Logout();
            return;
        }
        lastUnavailableByRecorder = bootUnavailableByRecorder;
        // Boot succeeded — the wall is genuinely up. LoginRetryLoop already drove this to Connecting
        // for the boot attempt; RecoverSession (below) drives it back through Connecting/Recovering
        // on every later reconnect.
        controllerState = ControllerState.Running;

        // Display-sleep prevention: must be asserted from THIS thread (the STA main thread that
        // owns Application.Run's message pump) — SetThreadExecutionState's effect is per-thread,
        // not per-process. Assert once now that the wall is actually showing, then re-assert every
        // 60s on a dedicated timer since some display drivers silently drop the flag over time.
        System.Windows.Forms.Timer? powerGuardTimer = null;
        if (config.KeepDisplayAwake)
        {
            PowerGuard.KeepAwake();
            powerGuardTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
            powerGuardTimer.Tick += (_, _) => PowerGuard.KeepAwake();
            powerGuardTimer.Start();
        }

        string lastDescription = recorderMatch.Description;
        var lastCameraSignature = ComputeSignature(recorderMatch.Cameras);

        // F2 (multi-recorder walls): the multi-mode counterpart of lastCameraSignature above — see
        // RecorderCatalog.ComputeSelectionSignature's own doc comment for what it hashes. Unused
        // (stays 0) in single-recorder mode, exactly like lastDescription/lastCameraSignature stay
        // unused in multi mode (harmless — each mode's refresh-tick branch only ever reads its own
        // signature variable).
        int lastMultiSignature = multiRecorderMode && lastSelectedRecorders is not null
            ? RecorderCatalog.ComputeSelectionSignature(lastSelectedRecorders, config.Layout, ResolveLayoutCarrierDescriptionForSignature(config, lastSelectedRecorders))
            : 0;

        var sessionLossDetector = new SessionLossDetector();
        bool recovering = false;

        var refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(5, config.ConfigRefreshSeconds) * 1000,
        };

        // T2/B5/E1: full mid-session recovery. Tears down every wall form (bracketed with
        // lifetime.BeginRebuild/EndRebuild so WallLifetime doesn't mistake it for the operator
        // leaving), logs out, re-enters the same login+locate retry loop used at boot, and rebuilds
        // on success — or exits cleanly if the operator closes the retry card instead. refreshTimer
        // is stopped for the duration: LoginRetryLoop pumps Application.DoEvents() internally
        // (PumpDelay), and without stopping the timer first, a Tick firing during that pump could
        // re-enter this same method concurrently.
        void RecoverSession(string reason)
        {
            if (recovering)
            {
                return;
            }
            recovering = true;
            try
            {
                logger.Warning($"Session-loss recovery triggered: {reason}");
                controllerState = ControllerState.Recovering;
                refreshTimer.Stop();
                sessionLossDetector.Reset();
                // T2/R2: record this recovery toward the backoff gate BEFORE doing the actual work
                // — see SessionLossDetector.RecordRecovery's doc comment. Runs on every recovery
                // regardless of which trigger fired it (both call sites funnel through
                // TriggerOrSuppressRecovery, which already confirmed CanRecover() was true).
                sessionLossDetector.RecordRecovery(DateTime.UtcNow);

                // F3 transactional swap: the OLD wall forms are deliberately left running here, NOT
                // closed — this method still needs a fresh login+locate before it can build any
                // replacement wall at all, and the old set staying up (even once this process logs
                // out below, at which point its tiles will start going stale) beats a black screen
                // for however long that takes. RebuildWall (below) is what closes the old set, but
                // only AFTER the new set is fully built and shown — see that method's own doc
                // comment for the full "never flash the desktop, never lose the wall on a build
                // failure" contract this satisfies.
                session.Logout();

                // LoginRetryLoop itself sets controllerState = Connecting the moment it starts (see
                // its own doc comment) — no separate assignment needed here anymore.
                var recovered = LoginRetryLoop(out bool recoveryCancelled);
                if (recoveryCancelled || recovered is null)
                {
                    // LoginRetryLoop already logged the one exit-reason line (T5) — just leave.
                    Application.Exit();
                    return;
                }

                if (RebuildWall(recovered, config, monitorArg, cameraBindings, layoutStateStore, wallForms, lifetime, logger, multiRecorderMode, out var recoveredUnavailableByRecorder, carrierPinnedMissing: multiCarrierPinnedMissing))
                {
                    controllerState = ControllerState.Running;
                    lastDescription = recovered.Description;
                    lastCameraSignature = ComputeSignature(recovered.Cameras);
                    if (multiRecorderMode && lastSelectedRecorders is not null)
                    {
                        lastMultiSignature = RecorderCatalog.ComputeSelectionSignature(lastSelectedRecorders, config.Layout, ResolveLayoutCarrierDescriptionForSignature(config, lastSelectedRecorders));
                    }
                    lastUnavailableByRecorder = recoveredUnavailableByRecorder;
                }
                else
                {
                    // RebuildWall already logged the failure. lastDescription/lastCameraSignature
                    // are deliberately left unchanged (same rule as the ordinary refresh path below)
                    // so a subsequent description/camera-list change can still drive a retry — and
                    // the OLD wall (from the pre-recovery, now-logged-out session) is exactly what
                    // keeps running, per RebuildWall's "keep the old set on failure" contract.
                    controllerState = ControllerState.Recovering;
                }

                refreshTimer.Start();
            }
            finally
            {
                recovering = false;
            }
        }

        // T2/R2: gates BOTH recovery trigger sites (staleness and consecutive-failure, below)
        // behind SessionLossDetector's backoff — recovery tears the whole wall down and re-logs in,
        // so retrying it every single tick while the underlying cause is still present would
        // effectively DDoS the Management Server with reconnect attempts.
        void TriggerOrSuppressRecovery(string reason)
        {
            if (!sessionLossDetector.CanRecover(DateTime.UtcNow))
            {
                if (sessionLossDetector.ShouldLogSuppressionWarning())
                {
                    var remainingSeconds = sessionLossDetector.NextAllowedRecoveryUtc is DateTime nextAllowed
                        ? Math.Max(0, (nextAllowed - DateTime.UtcNow).TotalSeconds)
                        : 0;
                    logger.Warning(
                        $"Recovery suppressed for another {remainingSeconds:F0}s — last recovery did not restore frames.");
                }
                return;
            }

            RecoverSession(reason);
        }

        // FIX 4 + FIX 3-lite: the one gate every refresh-tick branch (multi and single mode alike)
        // goes through before ever starting a background description poll. Two independent reasons
        // to skip, checked in order:
        //   1. FIX 4 — RecorderCatalog.ShouldPollLiveDescriptions: multi mode with a non-blank config
        //      Layout never reads any recorder's Description as a layout source (case (a) always
        //      wins — see WallConfig.Layout), so polling for one is pure waste. Not a problem
        //      condition — nothing is logged.
        //   2. FIX 3-lite — MilestoneSession.IsLayoutPollAllowed: refuse the bearer-token REST call
        //      over plain http unless AllowInsecureLayoutPoll opts in. IS a problem condition — WARN,
        //      logged only on change, naming the flag that would fix it.
        // Either way "skipped" means exactly that: TriggerPollIfIdle is never called, so
        // descriptionPoll.Latest simply keeps whatever it last held (graceful degradation, same
        // contract as a poll that ran and failed).
        void MaybeTriggerDescriptionPoll(bool tickIsMultiRecorderMode)
        {
            if (!RecorderCatalog.ShouldPollLiveDescriptions(tickIsMultiRecorderMode, config.Layout))
            {
                return;
            }

            if (!MilestoneSession.IsLayoutPollAllowed(uri, config.AllowInsecureLayoutPoll))
            {
                lastInsecurePollProblems = LogSelectionProblemsOnChange(
                    new[]
                    {
                        $"REST description poll refused: ManagementServerUri ({uri}) is not HTTPS and " +
                        "AllowInsecureLayoutPoll is false in camerawall.json — set AllowInsecureLayoutPoll=true " +
                        "to allow this only for a lab/dev environment. Poll skipped; the last successfully-fetched " +
                        "description (if any) keeps being used.",
                    },
                    lastInsecurePollProblems, logger);
                return;
            }

            lastInsecurePollProblems = LogSelectionProblemsOnChange(Array.Empty<string>(), lastInsecurePollProblems, logger);
            descriptionPoll.TriggerPollIfIdle();
        }

        refreshTimer.Tick += (_, _) =>
        {
            // T8(a)/R10: a reentrant Tick while a recovery is already in progress must be a no-op —
            // RecoverSession stops this very timer for its duration, but SDK calls further down
            // this handler can pump Windows messages internally, so this guard is defense in depth
            // against a Tick somehow still landing mid-recovery.
            if (recovering)
            {
                return;
            }

            // All-tiles-stale signal, independent of this tick's own success/failure — see
            // WallForm.FreshestTileAgeSeconds. Checked first because the SDK's own
            // ReloadConfiguration/TryGetRecorderDescriptions calls degrade SILENTLY (they catch
            // their own exceptions and never throw), so RecorderLocator.Locate can keep
            // "succeeding" off stale cached configuration through an outage with zero failures ever
            // recorded — staleness is what actually fires for that case.
            // T1/R1 design decision: minimum ACROSS every wall form — recovery targets whole-wall
            // session loss (the Management Server connection itself), so it only fires when EVERY
            // form's freshest tile is stale. A single frozen form among otherwise-healthy ones
            // (e.g. one monitor's cable pulled) is deliberately NOT a recovery trigger here — that
            // case is already covered per-tile by each form's own STALLED overlay
            // (WallForm.SweepStaleTiles), which doesn't need a full session teardown to fix.
            //
            // F2 (multi-recorder walls) point 8: this stays whole-wall recovery even in multi-
            // recorder mode, UNCHANGED — SessionLossDetector has no per-recorder concept and none is
            // added here. One dead recorder among several healthy ones does NOT, by itself, drive
            // every tile stale (the healthy recorders' tiles keep framing), so this signal correctly
            // stays quiet; that dead recorder's own tiles are per-tile self-heal's job (already
            // landed — WallForm.SweepStaleTiles/TileRecoverSeconds) and surface separately via the
            // per-recorder health.json rollup below (see BuildRecorderHealthList), not via a session
            // teardown that would needlessly disrupt the still-healthy recorders too.
            double? freshestTileAgeSeconds = null;
            bool freshestTileIsRealFrame = false;
            foreach (var form in wallForms)
            {
                var formAge = form.FreshestTileAgeSeconds(out var formIsRealFrame);
                if (formAge is null)
                {
                    continue;
                }
                if (freshestTileAgeSeconds is null || formAge.Value < freshestTileAgeSeconds.Value)
                {
                    freshestTileAgeSeconds = formAge;
                    freshestTileIsRealFrame = formIsRealFrame;
                }
            }

            // T2/R2: frames genuinely flowing (comfortably under the per-tile STALLED threshold —
            // see SessionLossDetector.HealthyFreshnessThresholdSeconds for why this has its own
            // floor, distinct from the staleness TRIGGER threshold below) resets the recovery
            // backoff. Independent of RecordSuccess() further down, which only tracks consecutive
            // REFRESH TICK success/failure, not frame freshness.
            // Round-3 panel-3 T1: gated on freshestTileIsRealFrame too — see
            // SessionLossDetector.ShouldMarkHealthy's doc comment for the never-framed-young-form
            // bug this closes (a spurious healthy signal that defeated the recovery backoff).
            if (SessionLossDetector.ShouldMarkHealthy(freshestTileAgeSeconds, freshestTileIsRealFrame, config.StaleSeconds))
            {
                sessionLossDetector.MarkHealthy();
            }

            if (SessionLossDetector.IsStalenessTriggered(freshestTileAgeSeconds, config.StaleSeconds))
            {
                var thresholdSeconds = SessionLossDetector.StaleTriggerThresholdSeconds(config.StaleSeconds);
                var ageSeconds = freshestTileAgeSeconds!.Value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                TriggerOrSuppressRecovery($"every tile stale for {ageSeconds}s (threshold {thresholdSeconds}s)");
                return;
            }

            try
            {
                // Without this the SDK serves cached config and $layout{}/camera edits in
                // Management Client take minutes (or a restart) to appear — see MilestoneSession.
                // The stopwatch feeds the DEBUG timing line after Discover below: both calls run on
                // the UI thread, so their combined duration is exactly how long each tick freezes
                // the message pump — the number that decides whether the walk must move off-thread.
                var tickStopwatch = System.Diagnostics.Stopwatch.StartNew();
                session.ReloadConfiguration();
                var reloadConfigMs = tickStopwatch.ElapsedMilliseconds;

                if (multiRecorderMode)
                {
                    // F2 point 6: re-list the FULL catalog every tick and re-select — the rebuild
                    // trigger is a signature change over (selected recorder ids, camera ids+enabled
                    // per selected recorder, config Layout fingerprint), computed by
                    // RecorderCatalog.ComputeSelectionSignature.
                    //
                    // Live-lab bug fix (layout-carrier recorder feature): RecorderCatalog.Discover's
                    // own Description read goes through the SDK's ConfigurationItems.RecordingServer
                    // cache, which — same as single-recorder mode's own Description read just below
                    // in this file — neither session.ReloadConfiguration() above NOR
                    // Configuration.RefreshConfiguration invalidates (see
                    // MilestoneSession.TryGetRecorderDescriptions's own doc comment). Pre-feature,
                    // F2 point 4 correctly skipped this REST poll here entirely: no recorder's own
                    // Description was EVER a multi-mode layout source, so paying for it was pure
                    // waste. It is now, for exactly the layout-carrier recorder (case (b) — see
                    // WallConfig.Layout's doc comment) — skipping the poll left a live Management-
                    // Client edit to the carrier's Description invisible to every later refresh tick
                    // for the rest of the process's life: RecorderCatalog.ComputeSelectionSignature
                    // kept hashing the SAME stale, SDK-cached text every tick, so the signature never
                    // changed and no rebuild (and no log line at all) ever fired — see
                    // RecorderCatalog.ApplyLiveDescriptions's own doc comment for the full mechanism.
                    //
                    // FIX 4: MaybeTriggerDescriptionPoll only ever STARTS a background poll (or
                    // decides not to — see its own doc comment); descriptionPoll.Latest below reads
                    // whatever the MOST RECENTLY COMPLETED poll returned, which is last tick's result,
                    // not this tick's — the UI thread never blocks waiting for the in-flight one. This
                    // is why DOCS states carrier-apply latency as "one or two ConfigRefreshSeconds
                    // intervals", not one.
                    MaybeTriggerDescriptionPoll(tickIsMultiRecorderMode: true);
                    var catalog = RecorderCatalog.Discover();
                    logger.Debug($"Refresh tick UI-thread timings: reloadConfigMs={reloadConfigMs}, discoverMs={tickStopwatch.ElapsedMilliseconds - reloadConfigMs}, catalogRecorders={catalog.Count}, catalogCameras={catalog.Sum(d => d.AllCameras.Count)}.");
                    catalog = RecorderCatalog.ApplyLiveDescriptions(catalog, descriptionPoll.Latest);
                    var selection = RecorderCatalog.Select(recordingServerSelectors, catalog);
                    lastSelectionProblems = LogSelectionProblemsOnChange(selection.Problems, lastSelectionProblems, logger);

                    if (selection.Selected.Count == 0)
                    {
                        logger.Warning("Refresh tick: multi-recorder selection matched zero recorders.");
                        if (sessionLossDetector.RecordFailure())
                        {
                            TriggerOrSuppressRecovery($"{SessionLossDetector.ConsecutiveFailureThreshold} consecutive failed refresh ticks");
                        }
                        return;
                    }
                    sessionLossDetector.RecordSuccess();

                    // Round-4 buyer-review fix (sticky LayoutCarrierPinned): re-resolve the carrier
                    // EVERY tick, BEFORE the unchanged-signature early return below. Pre-fix, the
                    // problem list was only refreshed by BuildMultiRecorderMatch after the signature
                    // gate — so a carrier returning with an IDENTICAL configuration (signature equal
                    // to the last committed one, the common case after a plain outage) skipped that
                    // call forever: the outage-era problem text never cleared and health kept
                    // reporting LayoutCarrierPinned indefinitely. ResolveLayoutCarrier is cheap
                    // (in-memory string/guid comparisons over an already-fetched list — see
                    // ResolveLayoutCarrierDescriptionForSignature's own doc comment), so per-tick
                    // costs nothing measurable.
                    var tickCarrier = RecorderCatalog.ResolveLayoutCarrier(config.LayoutRecorder, selection.Selected);
                    lastLayoutCarrierProblems = LogSelectionProblemsOnChange(
                        tickCarrier.Problem is null ? Array.Empty<string>() : new[] { tickCarrier.Problem },
                        lastLayoutCarrierProblems, logger);
                    // Also keeps the Main-scope pinned-missing flag current between recoveries — the
                    // recovery path passes it to RebuildWall as carrierPinnedMissing, and pre-fix it
                    // only ever changed inside LoginRetryLoop.
                    multiCarrierPinnedMissing = !string.IsNullOrWhiteSpace(config.LayoutRecorder) && tickCarrier.Carrier is null;

                    // Layout-carrier recorder feature: the signature must be recomputed with the
                    // CURRENT carrier's Description text (selection.Selected now carries the LIVE
                    // REST-overlaid Description, per the ApplyLiveDescriptions call above — not the
                    // SDK-cached one Discover() alone would have returned) for a carrier-Description-
                    // only edit to be detected as a change at all; see ComputeSelectionSignature's
                    // own doc comment for why this term is the opposite of configLayout's "defensive
                    // constant" role right next to it.
                    var newMultiSignature = RecorderCatalog.ComputeSelectionSignature(selection.Selected, config.Layout, ResolveLayoutCarrierDescriptionForSignature(config, selection.Selected));
                    // Field diagnosability fix: this tick was previously completely silent when the
                    // signature came out unchanged — indistinguishable in the log from "nothing was
                    // even checked". DEBUG (not Info/Warning) since "unchanged" is the overwhelmingly
                    // common, expected outcome on most ticks; LogLevel Debug surfaces it on demand
                    // without adding noise to the default Info-level log.
                    logger.Debug($"Multi-recorder refresh tick: selection signature {(newMultiSignature == lastMultiSignature ? "unchanged" : "changed")} (old={lastMultiSignature}, new={newMultiSignature}).");
                    if (newMultiSignature == lastMultiSignature)
                    {
                        return;
                    }

                    var (refreshed, layoutCarrierProblems, carrierPinnedMissing) = BuildMultiRecorderMatch(selection.Selected, config.LayoutRecorder, logger);
                    lastLayoutCarrierProblems = LogSelectionProblemsOnChange(layoutCarrierProblems, lastLayoutCarrierProblems, logger);
                    if (carrierPinnedMissing)
                    {
                        // FIX 2 (pinned carrier authority): the explicitly configured LayoutRecorder
                        // currently matches no selected recorder (outage/removal/ambiguous name — see
                        // RecorderCatalog.ResolveLayoutCarrier). Never rebuild off "refreshed" here —
                        // its Description is empty (BuildMultiRecorderMatch never adopts another
                        // recorder's text for a pinned-missing carrier), and an empty layout source
                        // resolves to zero monitors, which would tear down the wall and show the
                        // desktop — exactly the kind of disruption pinning exists to prevent. Instead:
                        // do nothing. wallForms/lastMultiSignature are left exactly as they were, so
                        // the wall keeps its last-known-good layout untouched (the same "leave the old
                        // set running" contract RebuildWall already uses on a build FAILURE — this is
                        // that same contract, reached by choosing not to attempt a build at all).
                        // lastLayoutCarrierProblems (just updated above) already carries the WARN-on-
                        // change; WriteHealthState folds it into LayoutCarrierPinned/OverallStatus. The
                        // next tick re-evaluates fresh — the moment the carrier matches again,
                        // newMultiSignature differs from this still-uncommitted lastMultiSignature and
                        // a normal rebuild fires: "authority resumes".
                        return;
                    }

                    logger.Info("Recorder catalog or camera list changed — rebuilding grids without restart.");
                    if (RebuildWall(refreshed, config, monitorArg, cameraBindings, layoutStateStore, wallForms, lifetime, logger, multiRecorderMode, out var tickUnavailableByRecorder))
                    {
                        // T3/E4 mirror: commit only after the rebuild actually succeeded — see the
                        // single-recorder branch below for the identical rationale.
                        lastMultiSignature = newMultiSignature;
                        lastSelectedRecorders = selection.Selected;
                        lastUnavailableByRecorder = tickUnavailableByRecorder;
                    }
                    return;
                }

                var refreshedSingle = RecorderLocator.Locate(effectiveRecorderOverride, out _);
                if (refreshedSingle is null)
                {
                    logger.Warning("Refresh tick: recorder no longer found.");
                    if (sessionLossDetector.RecordFailure())
                    {
                        TriggerOrSuppressRecovery($"{SessionLossDetector.ConsecutiveFailureThreshold} consecutive failed refresh ticks");
                    }
                    return;
                }
                sessionLossDetector.RecordSuccess();

                // Description via live REST read — the SDK's cached copy never updates within a
                // session (see MilestoneSession.TryGetRecorderDescriptions). FIX 4: the poll itself
                // now runs in the background (see MaybeTriggerDescriptionPoll/descriptionPoll above);
                // this just consumes whatever the most recently completed poll returned. FIX 1:
                // looked up by the recorder's stable Id — RecorderIds[0] is single-recorder mode's own
                // one-element identity list (see RecorderMatch.RecorderIds' own doc comment) — never
                // by Name, so two differently-configured recorders sharing a display name can never
                // cross-apply each other's Description.
                MaybeTriggerDescriptionPoll(tickIsMultiRecorderMode: false);
                var restDescriptions = descriptionPoll.Latest;
                if (restDescriptions is not null && refreshedSingle.RecorderIds.Count > 0
                    && restDescriptions.TryGetValue(refreshedSingle.RecorderIds[0], out var liveDescription))
                {
                    refreshedSingle = refreshedSingle with { Description = liveDescription };
                }

                var newSignature = ComputeSignature(refreshedSingle.Cameras);
                if (refreshedSingle.Description == lastDescription && newSignature == lastCameraSignature)
                {
                    return;
                }

                logger.Info("Recorder description or camera list changed — rebuilding grids without restart.");

                // F3 transactional swap: RebuildWall builds the complete new form set (and shows
                // it) BEFORE touching the old one — see that method's doc comment. lifetime's
                // rebuild bracket now wraps only the OLD SET'S closes, inside RebuildWall itself,
                // not this whole block.
                if (RebuildWall(refreshedSingle, config, monitorArg, cameraBindings, layoutStateStore, wallForms, lifetime, logger, multiRecorderMode, out _))
                {
                    // T3/E4: commit ONLY after the rebuild actually succeeded, so a failed build
                    // (already logged by RebuildWall, old wall left running) leaves
                    // lastDescription/lastCameraSignature unchanged — next tick's comparison
                    // detects the SAME change again and retries, instead of silently giving up on a
                    // description/camera-list change that never actually got applied.
                    lastDescription = refreshedSingle.Description;
                    lastCameraSignature = newSignature;
                }
            }
            catch (Exception ex)
            {
                // Recoverable — the timer just tries again next tick — so Warning, not Error;
                // the exception text is still appended for diagnosability.
                logger.Warning($"Background refresh failed (will retry next tick): {ex}");
                if (sessionLossDetector.RecordFailure())
                {
                    TriggerOrSuppressRecovery($"{SessionLossDetector.ConsecutiveFailureThreshold} consecutive failed refresh ticks");
                }
            }
        };
        refreshTimer.Start();

        Application.Run();

        refreshTimer.Stop();
        healthTimer?.Stop();
        healthTimer?.Dispose();
        // Round-4 buyer-review hardening: stop the background description-poll worker for good —
        // a poll completing after this point must be discarded, not published to a torn-down wall.
        // See DescriptionPollWorker.Shutdown's own doc comment (including why mid-session recovery
        // deliberately does NOT call this).
        descriptionPoll.Shutdown();
        screenshotResponder?.Dispose();
        powerGuardTimer?.Stop();
        if (config.KeepDisplayAwake)
        {
            // Clear the assertion on clean exit so normal OS sleep/display-timeout behavior
            // resumes — never called when KeepDisplayAwake is false, since KeepAwake() was never
            // called either in that case.
            PowerGuard.Release();
        }
        session.Logout();
    }

    /// <summary>
    /// <c>GridLookout.exe --health-probe</c> entry point — orchestrated end-to-end by
    /// <see cref="GridLookout.Monitoring.HealthProbe.Run"/> (see that method's doc comment). Called
    /// from <see cref="Main"/> BEFORE the single-instance mutex and any WinForms/MIP
    /// initialization: a probe invocation runs ALONGSIDE a live wall process on a short interval
    /// (the watchdog scheduled task — see scripts/install-kiosk.ps1), so it must never touch the
    /// mutex, never spin up its own SDK session, and never mutate camerawall.json — it loads config
    /// via <see cref="WallConfigLoader.LoadReadOnly"/>, not <see cref="WallConfigLoader.LoadOrCreate"/>,
    /// specifically to guarantee the last point.
    /// </summary>
    private static int RunHealthProbeMode(string baseDir, StateDirectory stateDirectory)
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // No parent console (e.g. launched by Task Scheduler with no interactive session) — the
            // printed verdict simply goes nowhere; the exit code (what the watchdog actually acts
            // on) is unaffected either way.
        }

        var protector = new DpapiSecretProtector();
        var loader = new WallConfigLoader(protector, stateDirectory);
        WallConfig config;
        try
        {
            config = loader.LoadReadOnly(baseDir);
        }
        catch
        {
            // A probe must never crash over an unreadable/corrupt config — fall back to defaults
            // (Health.Enabled false, StaleAfterSeconds 30) so the probe still runs and reports
            // truthfully off whatever health.json it can find, rather than exiting with no verdict
            // printed at all.
            config = new WallConfig();
        }

        return HealthProbe.Run(stateDirectory, baseDir, config, loader, Console.Out);
    }

    /// <summary>
    /// <c>GridLookout.exe --screenshot</c> entry point — see <see cref="ScreenshotRequester"/>'s own
    /// doc comment for the full request/response protocol against the RUNNING wall's
    /// <see cref="ScreenshotResponder"/>. Same "before the mutex, before any WinForms/MIP
    /// initialization" placement as <see cref="RunHealthProbeMode"/>, and for the identical reason:
    /// this runs ALONGSIDE a live wall process (a remote operator's sanity-check over ssh/psexec, not
    /// instead of the wall), so it must never touch the mutex, never spin up its own SDK session,
    /// and never mutate camerawall.json — unlike <see cref="RunHealthProbeMode"/> it doesn't even
    /// load config at all; it only needs <paramref name="baseDir"/>/<paramref name="stateDirectory"/>
    /// to compute the SAME candidate screenshot directories <see cref="ScreenshotPaths"/> computes
    /// everywhere else.
    /// </summary>
    private static int RunScreenshotRequestMode(string baseDir, StateDirectory stateDirectory)
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // No parent console (e.g. launched by a script with no interactive session) — see
            // AttachConsole's own doc comment; the exit code (what a calling script actually acts
            // on) is unaffected either way.
        }

        var candidateDirectories = ScreenshotPaths.CandidateScreenshotDirectories(stateDirectory, baseDir);
        return ScreenshotRequester.Run(candidateDirectories, Console.Out, Console.Error);
    }

    /// <summary>
    /// F3 point 9 — <c>GridLookout.exe --export-camera-bindings</c>: logs in with the configured
    /// credentials, locates the recorder exactly like the normal boot path does, and prints (stdout)
    /// plus writes (<c>camera-bindings.generated.json</c> next to <paramref name="baseDir"/>) a
    /// ready-to-paste <c>CameraBindings</c> skeleton mapping suggested kebab-case aliases to camera
    /// guids — see <c>Layout.CameraBindingsExporter</c> for the pure alias-generation logic. Runs
    /// BEFORE the single-instance mutex, same placement as <see cref="RunHealthProbeMode"/>, so an
    /// admin can regenerate the skeleton without stopping an already-running wall — it never touches
    /// camerawall.json (<see cref="WallConfigLoader.LoadReadOnly"/>, no migration/seeding side
    /// effects) and only ever WRITES the separate generated file, never the operator's own config.
    /// The plaintext password is used exactly once (to build the login credential) and is never
    /// logged or printed — see the "Never log credentials" quality bar this whole feature is held to.
    ///
    /// Exporter fix: a multi-recorder config (<see cref="WallConfig.RecordingServers"/> non-empty)
    /// with no <c>--recorder</c> given is checked and rejected FIRST, before any login/locate
    /// attempt — <see cref="RecorderLocator.Locate"/> is single-recorder-mode-only tree-walking logic
    /// with no <c>RecordingServers[]</c> concept; run against a multi-recorder config it degrades to
    /// hostname self-location, which (correctly, for its own contract) almost always matches nothing
    /// and prints a generic "No matching recorder found" that gives no hint the REAL problem is "this
    /// is a multi-recorder config, name one" — and does so on stderr. The new guard's message goes to
    /// <b>stdout</b> deliberately (<see cref="RecorderCatalog.BuildMultiRecorderExportError"/>),
    /// unlike every OTHER error in this method (all stderr): an operator invoking this flag from a
    /// script that only captures stdout was seeing a bare exit code 1 and nothing else — this is the
    /// one message in the method that must survive that capture pattern.
    /// </summary>
    private static int RunExportCameraBindingsMode(string baseDir, StateDirectory stateDirectory, string[] args)
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // No parent console — the printed skeleton simply goes nowhere; the generated file
            // (below) and the exit code are unaffected either way.
        }

        stateDirectory.Resolve(baseDir, out var stateDir);
        var logger = new FileLogger(Path.Combine(baseDir, "logs"), fallbackLogDirectory: Path.Combine(stateDir, "logs"));

        var protector = new DpapiSecretProtector();
        var loader = new WallConfigLoader(protector, stateDirectory);
        WallConfig config;
        try
        {
            config = loader.LoadReadOnly(baseDir);
        }
        catch (Exception ex)
        {
            logger.Error("--export-camera-bindings: config load failed", ex);
            Console.Error.WriteLine($"Could not load configuration — see the log for details: {ex.Message}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(config.ManagementServerUri))
        {
            Console.Error.WriteLine("ManagementServerUri is not configured — nothing to export.");
            return 1;
        }

        string? recorderArg = GetArgValue(args, "--recorder");

        // Exporter fix: same multi-recorder-mode test Main uses for the real wall (RecorderCatalog.IsMultiRecorderMode),
        // checked here BEFORE any login/locate attempt — see this method's own doc comment for why.
        // Uses ValidateSelectors (not raw config.RecordingServers) so a malformed entry never shows
        // up in the naming list, same discipline as every other RecordingServers[] consumer.
        if (RecorderCatalog.IsMultiRecorderMode(recorderArg, config.RecordingServers.Count))
        {
            var selectors = RecorderCatalog.ValidateSelectors(
                config.RecordingServers.Select(s => new RawRecordingServerEntry(s.Id, s.HostName)).ToList(),
                msg => logger.Warning(msg));
            var message = RecorderCatalog.BuildMultiRecorderExportError(selectors);
            logger.Error(message);
            Console.Out.WriteLine(message);
            return 1;
        }

        string effectiveRecorderOverride = !string.IsNullOrWhiteSpace(recorderArg) ? recorderArg! : config.RecorderNameOverride;

        var uri = new Uri(config.ManagementServerUri);
        RecorderLocator.Logger = logger;
        var session = new MilestoneSession(uri, logger);

        RecorderMatch? recorderMatch = null;
        try
        {
            session.Initialize();
            var password = loader.GetPassword(config);
            var credentials = CredentialFactory.Build(config, password, uri);
            session.Login(credentials);
            recorderMatch = RecorderLocator.Locate(effectiveRecorderOverride, out var candidates);
            if (recorderMatch is null)
            {
                var candidateText = candidates.Count == 0 ? "(none found)" : string.Join(", ", candidates);
                Console.Error.WriteLine($"No matching recorder found. Candidates: {candidateText}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            logger.Error("--export-camera-bindings: login/locate failed", ex);
            Console.Error.WriteLine($"Login or recorder lookup failed — see the log for details: {ex.Message}");
            return 1;
        }
        finally
        {
            session.Logout();
        }

        // recorderMatch is guaranteed non-null past this point — every path that would leave it
        // null already returned above.
        var skeleton = Layout.CameraBindingsExporter.BuildSkeleton(
            recorderMatch!.AllCameras.Select(c => (c.Name, c.Id)).ToList());
        var json = Layout.CameraBindingsExporter.RenderJson(skeleton);

        Console.Out.WriteLine(json);

        var outputPath = Path.Combine(baseDir, "camera-bindings.generated.json");
        try
        {
            File.WriteAllText(outputPath, json);
            logger.Info($"--export-camera-bindings: wrote {skeleton.Count} camera binding(s) to '{outputPath}'.");
        }
        catch (Exception ex)
        {
            logger.Warning($"--export-camera-bindings: could not write '{outputPath}': {ex.Message}");
            Console.Error.WriteLine($"Warning: could not write {outputPath}: {ex.Message}");
        }

        return 0;
    }

    // F3 (referentially stable layouts): the on-disk name for the last-known-good layout store —
    // see layoutStateStore in Main and Layout.LayoutStateFile.
    private const string LayoutStateFileName = "layout-state.json";

    /// <summary>Reads <see cref="LayoutStateFileName"/> back via <paramref name="store"/> — null
    /// (cold start) when the file doesn't exist yet OR is unreadable/malformed, so a corrupt state
    /// file degrades to "resolve everything fresh" instead of ever crashing the wall.</summary>
    private static Layout.LayoutStateFile? LoadLayoutState(AtomicStateStore store, FileLogger logger)
    {
        try
        {
            var raw = store.Read(LayoutStateFileName);
            return raw is null ? null : JsonSerializer.Deserialize<Layout.LayoutStateFile>(raw, Layout.LayoutJsonOptions.Default);
        }
        catch (Exception ex)
        {
            logger.Warning($"{LayoutStateFileName} unreadable/malformed ({ex.GetType().Name}: {ex.Message}) — treating as a cold start (no last-known-good plan to reuse).");
            return null;
        }
    }

    /// <summary>What one monitor's <see cref="WallForm"/> should be built from — either a resolved
    /// $layout{} plan (<see cref="TokenMode"/> true) or a slice of the config's <c>Monitors[]</c>
    /// auto-layout (<see cref="TokenMode"/> false). Produced by <see cref="ComputeWallFormSpecs"/>,
    /// consumed by <see cref="BuildOneWallForm"/> — splitting "what to build" from "how to build a
    /// WinForms window" is what lets <see cref="ComputeWallFormSpecs"/> stay SDK/UI-free.</summary>
    private sealed record MonitorRenderSpec(int Monitor, bool TokenMode, ResolvedMonitorPlan? ResolvedPlan, IReadOnlyList<CameraInfo>? AutoLayoutCameras);

    /// <summary>
    /// F3: resolves the recorder's $layout{} tokens (or falls back to <c>Monitors[]</c> auto-layout
    /// — decided product semantics: token mode is exclusive, so this fallback fires only when NO
    /// monitor ends up with any resolved layout at all — see <see cref="Layout.LayoutResolver.Resolve"/>'s
    /// doc comment) into one <see cref="MonitorRenderSpec"/> per monitor to build, plus the
    /// <see cref="Layout.LayoutStateFile"/> the caller should persist AFTER a successful build (never
    /// before — see <see cref="RebuildWall"/>). Pure/no WinForms — the actual <see cref="WallForm"/>
    /// construction is <see cref="BuildOneWallForm"/>'s job.
    /// </summary>
    private static (IReadOnlyList<MonitorRenderSpec> Specs, Layout.LayoutStateFile NewLayoutState) ComputeWallFormSpecs(
        RecorderMatch recorderMatch, WallConfig config, int defaultMonitor,
        IReadOnlyDictionary<string, Guid> cameraBindings, AtomicStateStore layoutStateStore, FileLogger logger,
        bool multiRecorderMode, bool carrierPinnedMissing = false)
    {
        // Layout-carrier recorder feature: multi mode STILL never reads $layout{} tokens from more
        // than ONE recorder's Description at a time (two recorders' descriptions could both claim
        // monitor 1, with no sane way to pick a winner) — but it is no longer true that it NEVER
        // reads any recorder's Description at all. Precedence: (a) a non-blank config Layout string
        // wins outright, unchanged pre-feature behavior; (b) else BuildMultiRecorderMatch has already
        // resolved exactly ONE selected recorder (the "layout-carrier") and put ITS Description into
        // recorderMatch.Description — see that method's own doc comment — so reading it here is
        // identical in shape to single-recorder mode's own read just below; (c) neither present ->
        // an empty layoutSource, which LayoutSpecParser.Parse below degrades to zero token results,
        // falling through to the "no monitor resolved anything" auto-grid branch further down, same
        // as always. See RecorderCatalog.ResolveMultiRecorderLayoutSource for this precedence's own
        // direct unit tests. Single-recorder mode is completely unchanged: recorderMatch.Description
        // is its ONLY source, exactly as before this feature and before F2.
        var layoutSource = multiRecorderMode
            ? RecorderCatalog.ResolveMultiRecorderLayoutSource(config.Layout, recorderMatch.Description)
            : recorderMatch.Description;
        var tokenResults = LayoutSpecParser.Parse(layoutSource, defaultMonitor);

        var catalogEntries = recorderMatch.AllCameras
            .Select(c => new Layout.CameraCatalogEntry(c.Id, c.Name, c.Enabled))
            .ToList();
        var orderedEnabledCameraIds = recorderMatch.Cameras.Select(c => c.Id).ToList();
        var persistedState = LoadLayoutState(layoutStateStore, logger);

        if (carrierPinnedMissing)
        {
            // Round-4 buyer-review fix (pinned carrier authority at boot/recovery): the configured
            // LayoutRecorder matched nothing this build, so layoutSource above is BLANK — not
            // because the operator removed the tokens, but because the carrier's Description is
            // temporarily unreadable. Letting the normal path run would resolve to zero monitors
            // and drop the wall to the Monitors[] auto-grid — a non-deterministic reshape during an
            // outage, exactly what pinning exists to prevent (the refresh tick already refuses to
            // rebuild in this state; boot and RecoverSession MUST rebuild, so they land here
            // instead). Render the persisted last-known-good plan directly — see
            // LayoutResolver.ResolveFromPersistedOnly's own doc comment for why this bypasses
            // Resolve's deliberate orphaned-entry rule. RebuildWall never persists over
            // layout-state.json while this flag is set, so returning persistedState as
            // NewLayoutState is inert by construction.
            var persistedOnly = Layout.LayoutResolver.ResolveFromPersistedOnly(
                persistedState, catalogEntries, orderedEnabledCameraIds, cameraBindings);
            if (persistedOnly.Monitors.Count > 0)
            {
                logger.Warning(
                    "Layout carrier is pinned-missing — rendering the last-known-good layout from " +
                    $"{LayoutStateFileName} unchanged (health reports LayoutCarrierPinned until the carrier returns).");
                var persistedSpecs = persistedOnly.Monitors
                    .Select(m => new MonitorRenderSpec(m.Monitor, TokenMode: true, m, AutoLayoutCameras: null))
                    .ToList();
                return (persistedSpecs, persistedState!);
            }

            // No last-known-good exists (a genuine first boot with the carrier already absent) —
            // fall through: the blank layoutSource resolves to zero monitors and the Monitors[]
            // auto-grid below is the only thing left to show. WARN so the operator can tell this
            // apart from a deliberate auto-grid configuration.
            logger.Warning(
                "Layout carrier is pinned-missing and no last-known-good layout exists in " +
                $"{LayoutStateFileName} — falling back to the Monitors[] auto-grid until the carrier appears.");
        }

        var resolveResult = Layout.LayoutResolver.Resolve(new Layout.LayoutResolver.ResolveInput(
            tokenResults, catalogEntries, orderedEnabledCameraIds, cameraBindings, persistedState, recorderMatch.RecorderIds));

        if (resolveResult.Plan.Monitors.Count > 0)
        {
            var tokenSpecs = resolveResult.Plan.Monitors
                .Select(m => new MonitorRenderSpec(m.Monitor, TokenMode: true, m, AutoLayoutCameras: null))
                .ToList();
            return (tokenSpecs, resolveResult.NewState);
        }

        // No monitor resolved to anything (rule 6d: every token malformed/absent, with no
        // last-known-good to fall back to either) — structural fallback to Monitors[] auto-layout,
        // byte-identical to the pre-F3 "parsedLayouts.Count == 0" branch.
        var fallbackSpecs = new List<MonitorRenderSpec>();
        foreach (var monitorCfg in config.Monitors)
        {
            var ordinals = ParseCameraRange(monitorCfg.Cameras, recorderMatch.Cameras.Count);
            var cameras = ordinals
                .Where(o => o >= 1 && o <= recorderMatch.Cameras.Count)
                .Select(o => recorderMatch.Cameras[o - 1])
                .ToList();
            fallbackSpecs.Add(new MonitorRenderSpec(monitorCfg.Monitor, TokenMode: false, ResolvedPlan: null, cameras));
        }

        return (fallbackSpecs, resolveResult.NewState);
    }

    /// <summary>Constructs and renders (but does not Show) one <see cref="WallForm"/> from
    /// <paramref name="spec"/> — the WinForms/SDK half of what <see cref="ComputeWallFormSpecs"/>
    /// decided. Showing/lifetime-registration is the caller's job (<see cref="RebuildWall"/>), so
    /// this stays a pure "build one thing" factory — the exact seam <see cref="WallSetSwapper.TryBuildSet{TForm}"/>
    /// needs to build the whole new set before anything about the old one is touched.</summary>
    private static WallForm BuildOneWallForm(MonitorRenderSpec spec, RecorderMatch recorderMatch, WallConfig config,
        Screen[] screens, int defaultMonitor, IReadOnlyDictionary<Guid, CameraInfo> cameraCatalog)
    {
        var (screen, fallback) = ResolveScreen(screens, spec.Monitor);
        var boundsOverride = ResolveWindowBoundsOverride(config, spec.Monitor, defaultMonitor);
        var form = new WallForm(spec.Monitor, screen, fallback, recorderMatch.Name, config.ShowHeader,
            config.TileBorderWidth, config.TileBorderColor, config.StaleSeconds, boundsOverride,
            config.FitFrameSizeToTile, config.MaxFps, config.TileScaleMode, config.KioskLock, config.TileRecoverSeconds);

        if (spec.TokenMode)
        {
            // spec.ResolvedPlan.Pages is 1 entry for a plain $layout{...} token, >1 for a
            // $layout{...|...} token — RenderResolvedLayout rotates through multiple pages the same
            // way RenderPagedAutoLayout below rotates auto-layout pages, using config.PageSeconds
            // (PageSize does not apply to matrix pages — see WallConfig.PageSeconds's doc comment
            // for the "0 still rotates a multi-page matrix" nuance).
            form.RenderResolvedLayout(spec.ResolvedPlan!, cameraCatalog, config.PageSeconds, config.TileRotateSeconds);
        }
        else
        {
            // Paging (PageSeconds/PageSize) applies ONLY to this auto-layout path — the $layout{}
            // token branch above is unaffected: an explicit matrix maps fixed references to fixed
            // tiles, so rotation is meaningless there and is ignored.
            form.RenderPagedAutoLayout(spec.AutoLayoutCameras!, config.PageSize, config.PageSeconds);
        }

        return form;
    }

    /// <summary>
    /// F3 point 7 — the transactional wall-set replacement every rebuild path (boot, config-refresh,
    /// and post-recovery in <see cref="Main"/>'s <c>RecoverSession</c>) funnels through:
    /// <list type="number">
    /// <item>Resolve a complete candidate plan (<see cref="ComputeWallFormSpecs"/>) — no window
    /// touched yet.</item>
    /// <item>Build the COMPLETE new form set via <see cref="WallSetSwapper.TryBuildSet{TForm}"/>,
    /// showing each one as it's built (old set, if any, is still fully up and untouched this whole
    /// time — no flash of desktop, ever).</item>
    /// <item>On success: close the OLD set (bracketed by <paramref name="lifetime"/>'s rebuild
    /// window so it's never mistaken for the operator leaving, and with
    /// <see cref="WallForm.SessionRecoveryInProgress"/> set on each old form right before its own
    /// close — not any earlier — so per-tile self-heal keeps working on the old wall for however
    /// long it's still the live one), then persist the resolved plan.</item>
    /// <item>On failure: the partially-built new forms are already disposed (inside
    /// <see cref="WallSetSwapper.TryBuildSet{TForm}"/>), the OLD set is left running completely
    /// untouched, nothing is persisted, and the failure is logged as an ERROR — the caller's own
    /// retry (the next refresh tick, or the caller falling through to
    /// <c>Application.Exit()</c> at boot with an empty old set) governs what happens next.</item>
    /// </list>
    /// Returns whether the swap succeeded.
    /// </summary>
    /// <summary>F2: empty per-recorder-unavailable rollup — the value <see cref="RebuildWall"/>'s
    /// <c>unavailableByRecorder</c> out param carries on every path that isn't multi-recorder mode's
    /// success path (single-recorder mode, or a failed/aborted rebuild), so callers never need a
    /// null check.</summary>
    private static readonly IReadOnlyDictionary<Guid, int> EmptyUnavailableByRecorder = new Dictionary<Guid, int>();

    /// <summary>F2: rolls up how many resolved cells render UNAVAILABLE per owning recorder, from
    /// the SAME resolved plan(s) <see cref="BuildOneWallForm"/> renders from — see
    /// <c>Monitoring.RecorderHealthAggregator.AggregateUnavailableByRecorder</c>'s own doc comment
    /// for why this stays in sync with what the wall actually shows without touching
    /// <c>WallForm</c>. A no-op (empty result) outside multi-recorder mode.</summary>
    private static IReadOnlyDictionary<Guid, int> ComputeUnavailableByRecorder(
        IReadOnlyList<MonitorRenderSpec> specs, IReadOnlyDictionary<Guid, CameraInfo> cameraCatalog, bool multiRecorderMode)
    {
        if (!multiRecorderMode)
        {
            return EmptyUnavailableByRecorder;
        }

        // Buyer-review defect #9: keyed by recorder ID, not name — see
        // RecorderHealthAggregator.Aggregate's own doc comment for why.
        var recorderIdByCameraId = new Dictionary<Guid, Guid>();
        foreach (var entry in cameraCatalog)
        {
            recorderIdByCameraId[entry.Key] = entry.Value.RecorderId;
        }

        var tokenPlans = specs.Where(s => s.TokenMode).Select(s => s.ResolvedPlan!).ToList();
        return RecorderHealthAggregator.AggregateUnavailableByRecorder(tokenPlans, recorderIdByCameraId);
    }

    /// <param name="carrierPinnedMissing">FIX 2 (pinned carrier authority), widened by the round-4
    /// buyer-review fix: true only for the one boot/recovery build made while an explicit
    /// multi-mode LayoutRecorder is currently unmatched (see <c>multiCarrierPinnedMissing</c>'s
    /// declaration-site comment in <c>Main</c>). Two effects, both scoped to that one build: (1)
    /// <c>ComputeWallFormSpecs</c> renders the persisted last-known-good plan directly instead of
    /// letting the necessarily-EMPTY layout source resolve to zero monitors and drop the wall to
    /// the <c>Monitors[]</c> auto-grid during an outage (round-4 fix — see
    /// <c>LayoutResolver.ResolveFromPersistedOnly</c>); (2) the persist call below is suppressed
    /// (original FIX 2), since writing this build's state would at best no-op and at worst
    /// overwrite <c>layout-state.json</c>'s good plan with an empty first-boot result — the
    /// carrier's later return would then re-pin ordinals fresh instead of resuming the old plan,
    /// the exact loss of pinned identity FIX 2 exists to prevent. The refresh tick's own
    /// pinned-missing handling never needs either effect: it skips calling RebuildWall entirely
    /// (see that branch's own comment). Defaults false — every other caller is unaffected.</param>
    private static bool RebuildWall(
        RecorderMatch recorderMatch, WallConfig config, string? monitorArg,
        IReadOnlyDictionary<string, Guid> cameraBindings, AtomicStateStore layoutStateStore,
        List<WallForm> wallForms, WallLifetime lifetime, FileLogger logger,
        bool multiRecorderMode, out IReadOnlyDictionary<Guid, int> unavailableByRecorder,
        bool carrierPinnedMissing = false)
    {
        unavailableByRecorder = EmptyUnavailableByRecorder;
        try
        {
            var screens = Screen.AllScreens
                .OrderBy(s => s.Bounds.Left)
                .ThenBy(s => s.Bounds.Top)
                .ToArray();

            int defaultMonitor = int.TryParse(monitorArg, out var argMonitor)
                ? argMonitor
                : (config.Monitors.Count > 0 ? config.Monitors[0].Monitor : 1);

            var (specs, newLayoutState) = ComputeWallFormSpecs(recorderMatch, config, defaultMonitor, cameraBindings, layoutStateStore, logger, multiRecorderMode, carrierPinnedMissing);
            var cameraCatalog = recorderMatch.AllCameras.ToDictionary(c => c.Id);
            unavailableByRecorder = ComputeUnavailableByRecorder(specs, cameraCatalog, multiRecorderMode);
            var oldForms = wallForms.ToList();

            bool built = WallSetSwapper.TryBuildSet(
                specs.Count,
                buildOne: i =>
                {
                    var form = BuildOneWallForm(specs[i], recorderMatch, config, screens, defaultMonitor, cameraCatalog);
                    lifetime.NoteOpened();
                    form.FormClosed += (_, _) =>
                    {
                        if (lifetime.NoteClosed())
                        {
                            logger.Info("Last wall window closed — exiting.");
                            Application.Exit();
                        }
                    };
                    form.Show();
                    return form;
                },
                disposePartial: f =>
                {
                    // MUST be CloseInternal(), not Dispose(): buildOne already called NoteOpened()
                    // and wired FormClosed -> lifetime.NoteClosed() for every form in the partial set
                    // (they were fully built and Shown before the LATER sibling failed). Disposing a
                    // Form directly does not reliably raise FormClosed the way Close() does, so a bare
                    // Dispose() here left NoteOpened permanently unbalanced by one per partial form —
                    // after one failed rebuild the "last window closed" exit could never fire again,
                    // and --health-probe would keep reporting the resulting zombie as healthy (its UI
                    // pulse keeps ticking). CloseInternal() bypasses the KioskLock close-gate (see its
                    // own doc comment) and drives the same Close() -> FormClosed -> NoteClosed() path
                    // every other WallForm teardown in this codebase already uses, so accounting stays
                    // exactly balanced without a separate idempotence-guarded NoteClosed() call here.
                    try { f.CloseInternal(); } catch { /* best-effort — see WallSetSwapper's own doc comment */ }
                },
                out var newForms,
                out var failure);

            if (!built)
            {
                logger.Error("Wall rebuild failed while constructing the new window set — keeping the previous wall running and retrying next tick.", failure);
                return false;
            }

            lifetime.BeginRebuild();
            try
            {
                foreach (var oldForm in oldForms)
                {
                    // Feature 1 (per-tile self-heal) guard — see WallForm.SessionRecoveryInProgress's
                    // own doc comment. Set right here, immediately before THIS form's own close, not
                    // any earlier (e.g. not before the new set was even built) — until this exact
                    // moment the old form was still potentially the live, in-use wall.
                    oldForm.SessionRecoveryInProgress = true;
                    // T1/R1: this codebase's own teardown, not a user/OS close — CloseInternal()
                    // bypasses the KioskLock gate that would otherwise refuse this and strand the swap.
                    oldForm.CloseInternal();
                }
            }
            finally
            {
                lifetime.EndRebuild();
            }

            wallForms.Clear();
            wallForms.AddRange(newForms);

            if (carrierPinnedMissing)
            {
                // FIX 2, extended by the round-4 fix above: while the carrier is pinned-missing,
                // newLayoutState is either the untouched persisted state ComputeWallFormSpecs just
                // rendered from (writing it back would be a pointless no-op at best) or the empty
                // "nothing to pin" result of a first-boot auto-grid fallback (writing THAT would
                // destroy a good last-known-good plan the disk may gain later) — either way, never
                // persist over layout-state.json in this state.
                logger.Debug($"{LayoutStateFileName} persist skipped this build: layout carrier is currently pinned-missing (see the LayoutRecorder warning above) — any last-known-good plan already on disk is left untouched.");
            }
            else
            {
                try
                {
                    layoutStateStore.Write(LayoutStateFileName, JsonSerializer.Serialize(newLayoutState, Layout.LayoutJsonOptions.Default));
                }
                catch (Exception ex)
                {
                    // Never let a state-write failure undo an already-successful, already-shown wall
                    // swap — log and let the NEXT successful rebuild retry the write, same
                    // never-block-the-wall discipline the health-write timer already follows.
                    logger.Warning($"{LayoutStateFileName} write failed (will retry on the next successful rebuild): {ex.Message}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            // Defense in depth only — ComputeWallFormSpecs/BuildOneWallForm are designed to never
            // throw (bad $layout{} tokens and missing monitors fall back rather than fail), but a
            // rebuild must never crash the kiosk over an unexpected exception here either.
            logger.Error("Wall rebuild failed unexpectedly — keeping the previous wall running and retrying next tick.", ex);
            return false;
        }
    }

    /// <summary>Resolves <see cref="WallConfig.WindowBounds"/> to a concrete override rectangle,
    /// but only for the first/default monitor's form — every other configured/laid-out monitor
    /// always stays fullscreen. Requires Width and Height both &gt; 0; a null or zero-sized config
    /// value falls back to the normal fullscreen-on-monitor behavior.</summary>
    private static Rectangle? ResolveWindowBoundsOverride(WallConfig config, int formMonitorNumber, int defaultMonitor)
    {
        if (formMonitorNumber != defaultMonitor)
        {
            return null;
        }

        var wb = config.WindowBounds;
        if (wb is null || wb.Width <= 0 || wb.Height <= 0)
        {
            return null;
        }

        return new Rectangle(wb.X, wb.Y, wb.Width, wb.Height);
    }

    private static (Screen screen, bool fallback) ResolveScreen(Screen[] screens, int monitorNumber)
    {
        int index = monitorNumber - 1;
        if (index >= 0 && index < screens.Length)
        {
            return (screens[index], false);
        }

        // Monitor number out of range -> fall back to primary, with a warning overlay — a
        // wallboard never fails to show video over a missing monitor.
        return (Screen.PrimaryScreen ?? screens[0], true);
    }

    private static List<int> ParseCameraRange(string spec, int totalCameras)
    {
        if (string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Range(1, totalCameras).ToList();
        }

        var result = new List<int>();
        // net48's String.Split has no single-char convenience overloads (Split(char, ...) was
        // added in .NET Core) — use the classic char[]-array overloads instead.
        foreach (var rawPart in spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Contains('-'))
            {
                var bounds = part.Split(new[] { '-' }, 2);
                if (int.TryParse(bounds[0], out var a) && int.TryParse(bounds[1], out var b))
                {
                    for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                    {
                        result.Add(i);
                    }
                }
            }
            else if (int.TryParse(part, out var single))
            {
                result.Add(single);
            }
        }
        return result;
    }

    // System.HashCode is .NET-Core-only (no net48 in-box type) — plain FNV-ish combine instead.
    // Only used to detect "did the camera list change" between refresh polls; it does not need
    // cryptographic or even collision-resistance quality, just cheap and deterministic.
    private static int ComputeSignature(IReadOnlyList<CameraInfo> cameras)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + cameras.Count;
            foreach (var camera in cameras)
            {
                hash = (hash * 31) + camera.Fqid.ObjectId.GetHashCode();
            }
            return hash;
        }
    }

    /// <summary>
    /// F2 (multi-recorder walls): synthesizes a <see cref="RecorderMatch"/> from a multi-recorder
    /// selection so the rest of this file's rendering pipeline (<see cref="ComputeWallFormSpecs"/>,
    /// <see cref="RebuildWall"/>, <see cref="BuildOneWallForm"/>) consumes multi mode through the
    /// EXACT same shape single-recorder mode always has — no separate multi-mode rendering path to
    /// keep in sync. <c>Name</c> becomes the wall header text (<see cref="UI.WallForm"/>'s
    /// <c>recorderName</c> constructor argument — F2 point 10: "N recorders" when more than one is
    /// selected, or that one recorder's own name).
    ///
    /// Layout-carrier recorder feature: <c>Description</c> is no longer always blank — it now
    /// carries the resolved layout-carrier recorder's OWN Description text, via
    /// <see cref="RecorderCatalog.ResolveLayoutCarrier"/>. This is deliberate, not a return to
    /// "every recorder's Description is a source": exactly ONE recorder's Description is ever
    /// threaded through, chosen the same way every refresh tick, so
    /// <see cref="ComputeWallFormSpecs"/>'s <c>layoutSource</c> selection can read it through the
    /// exact same <c>recorderMatch.Description</c> path single-recorder mode already uses — no
    /// separate plumbing. Any problem resolving the carrier (see
    /// <see cref="RecorderCatalog.LayoutCarrierResult.Problem"/>) is returned as data alongside the
    /// match, for the caller to log the same way <see cref="RecorderCatalog.SelectionResult.Problems"/>
    /// already is.
    ///
    /// FIX 2 (pinned carrier authority): <c>Description</c> is BLANK — never another recorder's
    /// text — whenever <see cref="RecorderCatalog.LayoutCarrierResult.Carrier"/> comes back null
    /// (pinned-missing; auto-carrier mode never returns null while <paramref name="selected"/> is
    /// non-empty, see <c>ResolveLayoutCarrier</c>'s own doc comment). <see cref="CarrierPinnedMissing"/>
    /// tells the caller that's WHY <c>Description</c> is blank this time, as opposed to a genuinely
    /// blank carrier Description — the two need different handling: a genuinely blank Description is
    /// nothing new (falls through to auto-grid, same as always), but a pinned-missing carrier must
    /// NOT be built from at all in the refresh tick (would tear down a healthy wall showing the
    /// desktop) — see that call site's own comment. Boot/recovery is the one place a pinned-missing
    /// build DOES still happen (a rebuild there is unavoidable — boot has nothing showing yet, and
    /// recovery's old forms hold a dead session) — the round-4 buyer-review fix makes that build
    /// render the persisted last-known-good plan instead of the auto-grid: see
    /// <c>RebuildWall</c>'s <c>carrierPinnedMissing</c> parameter and
    /// <c>LayoutResolver.ResolveFromPersistedOnly</c>.
    /// </summary>
    private static (RecorderMatch Match, IReadOnlyList<string> LayoutCarrierProblems, bool CarrierPinnedMissing) BuildMultiRecorderMatch(
        IReadOnlyList<RecorderDescriptor> selected, string layoutRecorderConfig, FileLogger logger)
    {
        var merged = RecorderCatalog.MergeCameras(selected, msg => logger.Warning(msg));
        string name = selected.Count == 1 ? selected[0].Name : $"{selected.Count} recorders";
        string hostName = string.Join(", ", selected.Select(r => r.HostName));
        // Buyer-review defects #4/#5/#7: the SELECTED recorder id set, not just the merged camera
        // catalog — see RecorderMatch.RecorderIds' own doc comment for why this feeds the layout
        // fingerprint independently of camera identity.
        var recorderIds = selected.Select(r => r.Id).ToList();

        var carrierResult = RecorderCatalog.ResolveLayoutCarrier(layoutRecorderConfig, selected);
        string carrierDescription = carrierResult.Carrier?.Description ?? string.Empty;
        var layoutCarrierProblems = carrierResult.Problem is null
            ? Array.Empty<string>()
            : new[] { carrierResult.Problem };
        bool carrierPinnedMissing = !string.IsNullOrWhiteSpace(layoutRecorderConfig) && carrierResult.Carrier is null;

        var match = new RecorderMatch(name, hostName, carrierDescription, merged.EnabledCameras, merged.AllCameras, recorderIds);
        return (match, layoutCarrierProblems, carrierPinnedMissing);
    }

    /// <summary>Layout-carrier recorder feature: the Description text
    /// <see cref="RecorderCatalog.ComputeSelectionSignature"/> should fold in as its dynamic
    /// "carrier changed" term — see that method's own doc comment. Blank whenever case (a)
    /// (non-blank config Layout) is active, since the carrier's Description isn't even consulted as
    /// a layout source in that case (see <see cref="RecorderCatalog.ResolveMultiRecorderLayoutSource"/>);
    /// otherwise the CURRENTLY resolved carrier's Description — <c>LayoutRecorder</c>'s match if one
    /// exists, the first <c>RecordingServers[]</c> entry for auto-carrier mode, or BLANK for FIX 2's
    /// pinned-missing case (never another recorder's text — see
    /// <see cref="RecorderCatalog.ResolveLayoutCarrier"/>'s own "PINNED vs. FLOATING" doc comment).
    /// Recomputing the carrier resolution here rather than threading it through from
    /// <see cref="BuildMultiRecorderMatch"/> keeps this callable from every site that needs a
    /// signature (boot, <c>RecoverSession</c>, the refresh tick) without also requiring that site to
    /// have already built a full <see cref="RecorderMatch"/> — cheap (a handful of string/guid
    /// comparisons over an already-in-memory list), not worth caching.</summary>
    private static string ResolveLayoutCarrierDescriptionForSignature(WallConfig config, IReadOnlyList<RecorderDescriptor> selected)
    {
        if (!string.IsNullOrWhiteSpace(config.Layout))
        {
            return string.Empty;
        }

        return RecorderCatalog.ResolveLayoutCarrier(config.LayoutRecorder, selected).Carrier?.Description ?? string.Empty;
    }

    /// <summary>F2: logs <paramref name="problems"/> only when they differ from
    /// <paramref name="lastProblems"/> — <see cref="RecorderCatalog.Select"/> runs on every
    /// <c>ConfigRefreshSeconds</c> tick in multi-recorder mode, and a selector matching nothing (or
    /// resolving to an already-selected recorder) is a condition that can persist for many ticks in
    /// a row; re-logging it every tick would be exactly the log spam
    /// <see cref="SelectionResult.Problems"/>'s own doc comment explains this method exists to
    /// avoid. Returns <paramref name="lastProblems"/> unchanged when nothing changed, so a caller can
    /// always just assign the return value back to its "last" variable.</summary>
    private static IReadOnlyList<string> LogSelectionProblemsOnChange(IReadOnlyList<string> problems, IReadOnlyList<string> lastProblems, FileLogger logger)
    {
        if (problems.SequenceEqual(lastProblems))
        {
            return lastProblems;
        }

        foreach (var problem in problems)
        {
            logger.Warning(problem);
        }

        return problems;
    }

    /// <summary>F2 point 4: logs an INFO advisory exactly once at startup (never per rebuild/tick)
    /// when a multi-recorder wall's configuration uses an ordinal camera reference — either a bare
    /// digit in <see cref="WallConfig.Layout"/> or a non-"all" <see cref="MonitorConfig.Cameras"/>
    /// range — since both index the MERGED camera list across every selected recorder, which shifts
    /// whenever ANY selected recorder's camera set changes, not just the one the operator is
    /// thinking about. <c>@alias</c>/<c>@{guid}</c> references don't have this problem (see
    /// <see cref="Layout.CellMemberKind"/>).</summary>
    private static void WarnIfMergedOrdinalsUsed(WallConfig config, FileLogger logger)
    {
        // defaultMonitor is irrelevant to this check (it only affects which monitor a BARE
        // "$layout{...}" token — no digits — targets); 1 is a harmless placeholder.
        var tokenResults = LayoutSpecParser.Parse(config.Layout, defaultMonitor: 1);
        bool layoutHasOrdinal = tokenResults.Any(r => r.IsValid && r.Layout!.Pages.Any(page =>
            page.Rows.Any(row => row.Any(cell => cell.Members.Any(m => m.Kind == Layout.CellMemberKind.Ordinal)))));
        bool monitorsHasRange = config.Monitors.Any(m => !string.Equals(m.Cameras, "all", StringComparison.OrdinalIgnoreCase));

        if (layoutHasOrdinal || monitorsHasRange)
        {
            logger.Info(
                "Multi-recorder mode: this configuration uses an ordinal camera reference (a plain " +
                "number — either in the config Layout or in Monitors[].Cameras) — ordinals index the " +
                "MERGED camera list across every selected recorder, sorted by \"RecorderName / " +
                "CameraName\". That order shifts whenever ANY selected recorder's camera set changes, " +
                "not just the one the operator is thinking about. Prefer @alias or @{guid} references " +
                "in the config Layout, which stay stable across recorder/camera churn.");
        }
    }

    /// <summary>F2: builds the <c>WallHealthState.Recorders</c> rollup — combines LIVE per-tile
    /// facts (<paramref name="wallForms"/>, current AT THIS HEALTH-WRITE TICK) with the STATIC
    /// unavailable-by-recorder counts computed once at the last successful rebuild
    /// (<paramref name="unavailableByRecorder"/> — see <see cref="ComputeUnavailableByRecorder"/>)
    /// and the id/name mapping from the last-selected catalog (<paramref name="selectedRecorders"/>).
    /// A recorder with zero live tiles right now (e.g. every one of its cameras happens to be
    /// disabled) still appears, with all-zero counts, as long as it's in
    /// <paramref name="selectedRecorders"/> — it must not silently vanish from the payload just
    /// because it currently has nothing rendering.</summary>
    private static List<RecorderHealth> BuildRecorderHealthList(
        List<WallForm> wallForms,
        IReadOnlyList<RecorderDescriptor>? selectedRecorders,
        IReadOnlyDictionary<Guid, int> unavailableByRecorder)
    {
        var liveFacts = new List<RecorderTileFact>();
        foreach (var form in wallForms)
        {
            liveFacts.AddRange(form.GetRecorderTileFacts());
        }
        // Buyer-review defect #9: keyed by recorder ID now, not display name — see
        // RecorderHealthAggregator.Aggregate's own doc comment. Two recorders sharing a name used
        // to collapse into one row here (and silently inherit whichever one's id the name lookup
        // happened to find first); an id can never collide.
        var liveByRecorder = RecorderHealthAggregator.Aggregate(liveFacts);

        var ids = new HashSet<Guid>();
        ids.UnionWith(liveByRecorder.Keys);
        ids.UnionWith(unavailableByRecorder.Keys);
        if (selectedRecorders is not null)
        {
            foreach (var recorder in selectedRecorders)
            {
                ids.Add(recorder.Id);
            }
        }

        var result = new List<RecorderHealth>();
        foreach (var id in ids)
        {
            liveByRecorder.TryGetValue(id, out var live);
            unavailableByRecorder.TryGetValue(id, out var unavailableCount);

            // Name is DISPLAY-ONLY (see RecorderHealth.Name's own doc comment) — prefer the
            // last-selected catalog's current name; fall back to whatever name the live-tile
            // rollup carried for this id (a recorder that just dropped out of selection but still
            // has stale live facts this tick); "" only in the defensive case where neither source
            // names it (an unavailable-only id with no matching live facts AND no longer selected —
            // should not happen in practice, mirrors this method's pre-existing defensive posture).
            string name = string.Empty;
            if (selectedRecorders is not null)
            {
                foreach (var recorder in selectedRecorders)
                {
                    if (recorder.Id == id)
                    {
                        name = recorder.Name;
                        break;
                    }
                }
            }
            if (name.Length == 0 && live is not null)
            {
                name = live.RecorderName;
            }

            result.Add(new RecorderHealth
            {
                Id = id.ToString(),
                Name = name,
                TilesExpected = live?.Expected ?? 0,
                TilesRendering = live?.Rendering ?? 0,
                TilesStalled = live?.Stalled ?? 0,
                TilesNeverFramed = live?.NeverFramed ?? 0,
                TilesUnavailable = unavailableCount,
            });
        }

        return result;
    }

    private static void PumpDelay(int seconds, Form form, ref bool cancelled)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            if (form.IsDisposed)
            {
                cancelled = true;
                return;
            }
            Thread.Sleep(100);
        }
    }

    /// <param name="configPath">T6 follow-up: the caller now passes <c>loader.EffectiveConfigPath</c>
    /// directly rather than this method recomputing <c>Path.Combine(baseDir, "camerawall.json")</c>
    /// itself — the two are byte-identical on a writable exe dir (no behavior change there), but
    /// diverge exactly on the unwritable/kiosk path T6 exists to serve, where the real, seeded,
    /// editable file lives under the state dir instead.</param>
    private static void ShowConfigMissingCard()
    {
        // T1/R1: deliberately constructed with NO kioskLock argument (defaults to false) even when
        // config.KioskLock is true elsewhere in this run — this card means "not configured at all",
        // and locking a box an operator cannot yet reach a working config on would brick it with no
        // recovery path but Task Manager. Config-missing and config-load-failed are the only two
        // cards in this codebase that stay intentionally escapable.
        //
        // PUBLIC-SCREEN RULE extension (2026-08-21 lab check): these two cards used to name the
        // ABSOLUTE effective config/log path on the assumption they only appear during supervised
        // setup — but a runtime config failure on a DEPLOYED kiosk shows the same card to every
        // passerby, and an absolute path leaks the Windows username and machine layout (the exact
        // reason the login/retry cards were fixed in T8(d)/R10). The cards now give
        // location-generic guidance; the exact paths stay in the startup Info log line and the
        // admin guide.
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var form = new WallForm(1, screen, fallbackWarning: false, recorderName: string.Empty, showHeader: false);
        form.ShowStatus(
            "GridLookout is not configured.\n\n" +
            "Edit camerawall.json — beside the application, or in\n" +
            "%ProgramData%\\GridLookout on locked-down installs —\n" +
            "set ManagementServerUri, then restart.",
            isError: true);
        Application.Run(form);
    }

    /// <summary>T1/B4 belt-and-suspenders card for the (should be unreachable) case where config
    /// load/migration still throws even after the StateDirectory fallback — see the try/catch
    /// around loader.LoadOrCreate in Main. Generic text only, no exception detail and (since the
    /// 2026-08-21 lab check) no absolute filesystem path — see ShowConfigMissingCard's
    /// PUBLIC-SCREEN RULE note; the caller already logged the detail via logger.Error before
    /// showing this.</summary>
    private static void ShowConfigLoadFailedCard()
    {
        // T1/R1: same "intentionally escapable" reasoning as ShowConfigMissingCard above — no
        // kioskLock argument, always unlocked, regardless of config.KioskLock (which in this path
        // may not have even been readable yet).
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var form = new WallForm(1, screen, fallbackWarning: false, recorderName: string.Empty, showHeader: false);
        form.ShowStatus(
            "GridLookout could not load its configuration.\n\n" +
            "See the 'logs' folder beside the application\n" +
            "(or %ProgramData%\\GridLookout\\logs) for details, then restart.",
            isError: true);
        Application.Run(form);
    }

    /// <summary>T8(d)/R10: human-readable, generic-safe (no server names — just a local filesystem
    /// path) description of where <paramref name="logger"/> is actually writing, for both on-screen
    /// cards and the startup Info log line. <see cref="FileLogger.EffectiveLogDirectory"/> is null
    /// when logging itself could not be set up at all (see <see cref="FileLogger.Disabled"/>).</summary>
    private static string DescribeLogDirectory(FileLogger logger) =>
        logger.EffectiveLogDirectory ?? "no writable location found — logging is disabled for this run";

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
