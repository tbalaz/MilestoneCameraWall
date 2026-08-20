using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GridLookout.Logging;

namespace GridLookout.Config;

/// <summary>
/// Loads camerawall.json, merges an optional camerawall.local.json overlay (same directory, dev
/// override, gitignored), and migrates a plaintext <see cref="WallConfig.Password"/> to a DPAPI
/// <see cref="WallConfig.PasswordProtected"/> blob on first load. SDK-free.
/// </summary>
public sealed class WallConfigLoader
{
    private readonly ISecretProtector _protector;
    private readonly IStateDirectory _stateDirectory;
    private readonly Action<LogLevel, string>? _log;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The file this loader last treated as authoritative for the primary (non-overlay)
    /// settings — the exe-dir camerawall.json when its directory is writable, otherwise the
    /// state-dir copy (see <see cref="LoadOrCreate"/>'s writable-state fallback and the T3/R3
    /// snapshot-shadowing logic). Null until <see cref="LoadOrCreate"/> has run at least once. Used
    /// for on-screen/log diagnostics (Program.cs's startup Info line) and for
    /// <see cref="GetPassword"/>'s DPAPI-wedge error message.</summary>
    public string? EffectiveConfigPath { get; private set; }

    /// <param name="stateDirectory">Resolves the writable fallback used when <paramref name="protector"/>'s
    /// caller (Program.cs) can't write next to the exe — see <see cref="StateDirectory"/> and
    /// <see cref="LoadOrCreate"/>'s doc comment. Defaults to a real <see cref="StateDirectory"/>
    /// pointed at %ProgramData%\GridLookout; tests inject a fake pointed at a temp directory (see
    /// <see cref="IStateDirectory"/>'s doc comment for why an interface, not just the concrete
    /// type, is needed for that).</param>
    /// <param name="log">Optional logger callback (T3/R3, T4/R4, T5/R6) — Program.cs passes one
    /// that forwards to the real <see cref="FileLogger"/>; tests pass a collector, or omit it
    /// entirely (every call site here is null-conditional, so omitting is always safe).</param>
    public WallConfigLoader(ISecretProtector protector, IStateDirectory? stateDirectory = null, Action<LogLevel, string>? log = null)
    {
        _protector = protector;
        _stateDirectory = stateDirectory ?? new StateDirectory();
        _log = log;
    }

    /// <summary>
    /// Loads <paramref name="primaryFileName"/> from <paramref name="directory"/> (creating a
    /// default in-memory config if the file is absent — Program.cs is responsible for showing the
    /// "not configured" error card in that case, never for exiting silently). Merges
    /// <paramref name="overlayFileName"/> on top if present — any key in the overlay wins over the
    /// same key in the primary file; keys absent from the overlay keep the primary's value. Then
    /// runs the password-migration branch: rewrites ONLY the one file the plaintext Password
    /// actually came from (overlay wins if present in both, matching merge precedence), touching
    /// only that file's Password/PasswordProtected keys and preserving its other keys exactly as
    /// they were on disk — merged/cross-file values are never baked into either file (a dev
    /// overlay's lab ManagementServerUri etc. must never leak into the shipped camerawall.json).
    ///
    /// WRITABLE-STATE FALLBACK (B4 fix — see <see cref="StateDirectory"/>). <paramref name="directory"/>
    /// is normally the exe directory. If a probe write there fails (e.g. a limited kiosk account
    /// under a Program-Files install), the migration rewrite targets %ProgramData%\GridLookout
    /// instead of throwing, and — on every load, not just the one that migrates — a same-named
    /// state-dir copy of <paramref name="primaryFileName"/> is merged on top of the exe-dir
    /// template (state-dir wins), so a config previously migrated there is found on every
    /// subsequent run. Merge order is primary (exe-dir template) -&gt; state-dir copy -&gt;
    /// overlay, so the dev overlay still wins over everything as before. On a writable exe dir —
    /// the overwhelmingly common case — this whole mechanism is a no-op and behavior is
    /// byte-identical to before this fix.
    ///
    /// T6: FIRST-RUN TEMPLATE SEEDING. The MSI no longer ships <paramref name="primaryFileName"/>
    /// at all (see Product.GridLookout.wxs's CAMERAWALL.JSON OWNERSHIP comment) — only
    /// <paramref name="templateFileName"/>, a blank/cleared-placeholder jsonc file beside the exe.
    /// When <paramref name="primaryFileName"/> is absent from <paramref name="directory"/> (the
    /// exe dir) AND <paramref name="templateFileName"/> is present there, this seeds a real
    /// <paramref name="primaryFileName"/> by copying the template's raw TEXT verbatim — never
    /// through the JSON parse/reserialize path used everywhere else in this class, which would
    /// silently strip every comment (<see cref="JsonCommentHandling.Skip"/> is input-only; nothing
    /// in <c>System.Text.Json</c> writes comments back out) — to the effective writable location:
    /// <paramref name="directory"/> itself when writable, otherwise the state dir. The seeded file
    /// is then loaded through the exact same merge path as a hand-edited file, so a still-blank
    /// <c>ManagementServerUri</c> in the template still produces the existing "not configured"
    /// card; only now the admin has a real, commented file to edit at the path the startup log
    /// already names via <see cref="EffectiveConfigPath"/>, instead of nothing. Never overwrites
    /// an existing file at the target (checked immediately before the copy) — seeding is at most a
    /// one-time event, and a genuinely missing template (dev workspace without the MSI's staged
    /// layout, or a hand-run exe copied without it) falls through to the pre-T6 in-memory-defaults
    /// behavior unchanged. A copy failure (e.g. a race losing to a second launch) is caught and
    /// logged as a Warning rather than propagated — the existing "not configured" card is always
    /// the safe fallback, never a crash.
    /// </summary>
    public WallConfig LoadOrCreate(string directory, string primaryFileName = "camerawall.json", string? overlayFileName = "camerawall.local.json", string templateFileName = "camerawall.template.json")
    {
        var primaryPath = Path.Combine(directory, primaryFileName);
        var overlayPath = overlayFileName is null ? null : Path.Combine(directory, overlayFileName);
        var templatePath = Path.Combine(directory, templateFileName);

        bool exeDirWritable = _stateDirectory.Resolve(directory, out var stateDir);

        SeedFromTemplateIfMissing(primaryPath, templatePath, exeDirWritable, stateDir, primaryFileName);

        JsonObject? primaryObject = File.Exists(primaryPath) ? ParseObject(File.ReadAllText(primaryPath)) : null;
        JsonObject? overlayObject = (overlayPath is not null && File.Exists(overlayPath)) ? ParseObject(File.ReadAllText(overlayPath)) : null;

        // T4(a)/R4: the exe-dir file's OWN plaintext Password (and, mirrored below, Health.BearerToken)
        // can only ever be blanked by rewriting IT in place — impossible when its directory isn't
        // writable (migration always targets the state-dir copy instead in that case, see the
        // migration blocks below). There is no other self-healing path for this, so warn on EVERY
        // start while it persists.
        if (!exeDirWritable && primaryObject is not null)
        {
            if (!string.IsNullOrEmpty(GetStringOrEmpty(primaryObject, "Password")))
            {
                // Round-3 panel-3 T4 fix: the previous wording ("a DPAPI-protected copy is used from the
                // state directory instead for this run") is false on the very FIRST unwritable-dir run —
                // no state-dir copy exists yet at this point; it's only created later in this same
                // LoadOrCreate call, as a side effect of the migration block below. What this run actually
                // uses is whatever value LoadOrCreate found (the exe-dir plaintext on a first run, or an
                // already-migrated state-dir copy on every run after that) — true in both cases without
                // needing to tell them apart here.
                _log?.Invoke(LogLevel.Warning,
                    $"'{primaryPath}' still contains a plaintext Password and cannot be auto-blanked because its " +
                    "directory isn't writable by this account. This run uses whichever value it found (the state-" +
                    "directory copy if one already exists, otherwise this file's own plaintext). A DPAPI-protected " +
                    $"copy lives (or will be written, starting with this run) at '{Path.Combine(stateDir, primaryFileName)}' " +
                    "and is what every future start reads instead — but the plaintext value stays on disk in this " +
                    "file too; remove it manually.");
            }

            var primaryHealthObjectForWarning = GetNestedObject(primaryObject, "Health");
            if (primaryHealthObjectForWarning is not null && !string.IsNullOrEmpty(GetStringOrEmpty(primaryHealthObjectForWarning, "BearerToken")))
            {
                _log?.Invoke(LogLevel.Warning,
                    $"'{primaryPath}' still contains a plaintext Health.BearerToken and cannot be auto-blanked " +
                    "because its directory isn't writable by this account. This run uses whichever value it " +
                    "found (the state-directory copy if one already exists, otherwise this file's own " +
                    $"plaintext). A DPAPI-protected copy lives (or will be written, starting with this run) at " +
                    $"'{Path.Combine(stateDir, primaryFileName)}' and is what every future start reads instead " +
                    "— but the plaintext value stays on disk in this file too; remove it manually.");
            }
        }

        string? statePrimaryPath = null;
        JsonObject? statePrimaryObject = null;
        if (!exeDirWritable)
        {
            statePrimaryPath = Path.Combine(stateDir, primaryFileName);
            statePrimaryObject = File.Exists(statePrimaryPath) ? ParseObject(File.ReadAllText(statePrimaryPath)) : null;

            // T3/R3: snapshot-shadowing fix. Without this, an admin editing camerawall.json
            // directly in the install dir would be silently shadowed forever by a stale
            // ProgramData snapshot from before the edit — the merge below always preferred the
            // state-dir copy. Only the exe-dir file being NEWER AND materially configured counts
            // as a real edit; a newer-but-BLANK exe-dir file is what an admin gets by manually
            // copying camerawall.template.json over camerawall.json (a deliberate factory-reset
            // gesture) and must NOT wipe a working kiosk's state-dir configuration — see the
            // "else" branch below. (Installer-builder-flagged correction: this can no longer be
            // "a fresh MSI upgrade lays down [a blank exe-dir file]" — camerawall.json isn't an
            // MSI component at all as of the S6/U3/I4 fix, only camerawall.template.json is; see
            // Product.GridLookout.wxs's CAMERAWALL.JSON OWNERSHIP comment. An upgrade now lays
            // down only the template, never touching a live camerawall.json.)
            if (primaryObject is not null && statePrimaryObject is not null
                && File.GetLastWriteTimeUtc(primaryPath) > File.GetLastWriteTimeUtc(statePrimaryPath))
            {
                if (!string.IsNullOrWhiteSpace(GetStringOrEmpty(primaryObject, "ManagementServerUri")))
                {
                    _log?.Invoke(LogLevel.Info,
                        $"'{primaryPath}' is newer than the state-dir copy '{statePrimaryPath}' and is materially " +
                        "configured (non-empty ManagementServerUri) — treating it as an admin edit or MSI re-seed; " +
                        "re-seeding the state-dir copy from it.");

                    bool primaryHasPassword = !string.IsNullOrEmpty(GetStringOrEmpty(primaryObject, "Password"));
                    bool primaryHasProtected = !string.IsNullOrEmpty(GetStringOrEmpty(primaryObject, "PasswordProtected"));

                    if (!primaryHasPassword && !primaryHasProtected)
                    {
                        // Reviewer-caught bug (pre-ship): the exe-dir template is NEVER authoritative
                        // for PasswordProtected — nothing ever writes that field there (migration
                        // always targets the state-dir copy while the exe dir is unwritable, which is
                        // the only way this branch runs at all). Without this, an admin manually
                        // removing the stuck plaintext Password T4(a) warns about (a routine, expected
                        // response to that warning) bumps the exe-dir file's mtime, which then wins
                        // this reseed and — with no Password AND no PasswordProtected of its own —
                        // silently overwrites the state-dir copy's WORKING blob with nothing. Carry
                        // the existing state-dir blob forward into the reseed so a reseed can replace
                        // settings but can never silently drop a working credential.
                        var existingProtected = GetStringOrEmpty(statePrimaryObject, "PasswordProtected");
                        if (!string.IsNullOrEmpty(existingProtected))
                        {
                            SetStringValue(primaryObject, "PasswordProtected", existingProtected);
                        }
                    }

                    // Mirrors the Password carry-forward immediately above, one level down under
                    // "Health" — same reviewer-caught-bug rationale: an exe-dir file with neither
                    // Health.BearerToken nor Health.BearerTokenProtected of its own must not let this
                    // reseed silently drop a working state-dir BearerTokenProtected blob.
                    var primaryHealthForReseed = GetNestedObject(primaryObject, "Health");
                    bool primaryHasBearerToken = primaryHealthForReseed is not null && !string.IsNullOrEmpty(GetStringOrEmpty(primaryHealthForReseed, "BearerToken"));
                    bool primaryHasBearerProtected = primaryHealthForReseed is not null && !string.IsNullOrEmpty(GetStringOrEmpty(primaryHealthForReseed, "BearerTokenProtected"));
                    if (!primaryHasBearerToken && !primaryHasBearerProtected)
                    {
                        var stateHealthForReseed = GetNestedObject(statePrimaryObject, "Health");
                        var existingBearerProtected = stateHealthForReseed is not null ? GetStringOrEmpty(stateHealthForReseed, "BearerTokenProtected") : string.Empty;
                        if (!string.IsNullOrEmpty(existingBearerProtected))
                        {
                            var healthSectionForCarryForward = primaryHealthForReseed;
                            if (healthSectionForCarryForward is null)
                            {
                                healthSectionForCarryForward = new JsonObject();
                                primaryObject["Health"] = healthSectionForCarryForward;
                            }
                            SetStringValue(healthSectionForCarryForward, "BearerTokenProtected", existingBearerProtected);
                        }
                    }

                    if (!primaryHasPassword)
                    {
                        // No plaintext Password to migrate below, so nothing else will touch
                        // statePrimaryPath this run — this verbatim write IS the reseed (now
                        // carrying forward any existing PasswordProtected per the fix above).
                        // Skipping it would silently re-detect "exe-dir is newer" on every future
                        // boot forever, since nothing else would ever write the state-dir copy.
                        // M2 fix: non-fatal on a transient filesystem failure — the merged config
                        // is already fully loaded in memory, so this session runs fine either way,
                        // and the very "exe-dir is newer" re-detection described above is what
                        // retries the reseed on the next boot.
                        try
                        {
                            WriteJsonObject((JsonObject)primaryObject.DeepClone(), statePrimaryPath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _log?.Invoke(LogLevel.Warning,
                                $"Could not reseed the state-dir config copy at '{statePrimaryPath}' " +
                                $"({ex.GetType().Name}: {ex.Message}) — this session runs on the already-loaded " +
                                "settings; the reseed is retried on the next start.");
                        }
                    }
                    // else: leave the verbatim write undone — the password-migration block below
                    // will seed from primaryObject (statePrimaryObject is cleared just below) and
                    // write the BLANKED+PROTECTED result straight to statePrimaryPath in one shot.
                    // Writing the plaintext here first would put it on disk in %ProgramData%, even
                    // briefly — north star: "one credential, protected", no such window.

                    // Either branch: the merge/migration further down must use the FRESH exe-dir
                    // content (primaryObject, now possibly carrying the preserved PasswordProtected
                    // per the fix above), not the stale snapshot.
                    statePrimaryObject = null;
                }
                else
                {
                    _log?.Invoke(LogLevel.Info,
                        $"'{primaryPath}' is newer than the state-dir copy but is a blank/unconfigured template " +
                        "(likely an MSI upgrade re-seeding the install dir) — keeping the existing state-dir " +
                        $"configuration. Delete '{statePrimaryPath}' for a factory reset.");
                }
            }
        }

        var merged = primaryObject is not null ? (JsonObject)primaryObject.DeepClone() : new JsonObject();
        if (statePrimaryObject is not null)
        {
            foreach (var kvp in statePrimaryObject)
            {
                merged[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
        if (overlayObject is not null)
        {
            foreach (var kvp in overlayObject)
            {
                merged[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        var config = DeserializeTolerantly(merged);

        // Hoisted here (used to be computed only at the very end) so BOTH migration blocks below —
        // Password's and, mirroring it, Health.BearerToken's — can early-`return config;` on a
        // DPAPI-unavailable degradation without ever leaving EffectiveConfigPath unset. It depends
        // only on exeDirWritable/statePrimaryPath/stateDir/primaryFileName, none of which the
        // migration blocks below can change, so computing it earlier is behavior-neutral.
        EffectiveConfigPath = exeDirWritable ? primaryPath : (statePrimaryPath ?? Path.Combine(stateDir, primaryFileName));

        if (!string.IsNullOrEmpty(config.Password))
        {
            string protectedValue;
            try
            {
                protectedValue = _protector.Protect(config.Password);
            }
            catch (CryptographicException ex)
            {
                // DPAPI itself can be unavailable for the whole logon session (observed live
                // 2026-08-19: ProtectedData.Protect throwing "Access is denied" machine-wide for
                // the user until the next clean logon/reboot). Before this catch, that transient
                // OS condition BRICKED the wall at config load — the one component whose north-star
                // job is to never die over a recoverable fault, killed by a hiccup in the mechanism
                // that only exists to protect a value we are holding in plaintext anyway. Degrade
                // instead: run this session on the in-memory plaintext, leave the file untouched
                // (no blanking without a stored protected value!), and let migration succeed on a
                // future start once DPAPI works again.
                _log?.Invoke(LogLevel.Warning,
                    "DPAPI is unavailable in this Windows session (Protect failed: " + ex.Message +
                    ") — running with the plaintext Password for this session only. The config file " +
                    "is left unchanged; encryption/migration will be retried on the next start.");
                return config;
            }
            config.PasswordProtected = protectedValue;
            config.Password = string.Empty;

            var overlayPassword = overlayObject is not null ? GetStringOrEmpty(overlayObject, "Password") : string.Empty;
            if (!string.IsNullOrEmpty(overlayPassword) && overlayObject is not null && overlayPath is not null)
            {
                if (exeDirWritable)
                {
                    RewritePasswordFields(overlayObject, overlayPath, protectedValue);
                }
                else
                {
                    // T5/R6: a state-dir copy of the OVERLAY file is never read back on a later
                    // load — LoadOrCreate only merges a state-dir copy of the PRIMARY file (see the
                    // T3 block above) — so writing one here would be a copy nothing ever reads.
                    // Skip it and warn instead; the in-memory config above already got the
                    // DPAPI-protected value for THIS run, only the on-disk overlay file itself
                    // keeps its plaintext.
                    _log?.Invoke(LogLevel.Warning,
                        $"'{overlayPath}' contains a plaintext Password but its directory isn't writable by this " +
                        "account, so it cannot be auto-blanked. It stays plaintext on disk; remove it manually.");
                }
            }
            else if (primaryObject is not null || statePrimaryObject is not null)
            {
                // Seed from whichever copy is authoritative for the rewrite: the exe-dir template
                // when it's writable (unchanged behavior), otherwise the existing state-dir copy
                // if one's already there (so its other keys survive re-migration), else the
                // exe-dir template as the seed for a FIRST write into the state dir — the T3 block
                // above already cleared statePrimaryObject when the exe-dir file won a reseed, so
                // this naturally seeds from the FRESH exe-dir content in that case too.
                bool seedCameFromTargetPath = exeDirWritable || statePrimaryObject is not null;
                var seedObject = exeDirWritable ? primaryObject! : (statePrimaryObject ?? primaryObject!);
                var targetPath = exeDirWritable ? primaryPath : (statePrimaryPath ?? Path.Combine(stateDir, primaryFileName));
                // T3: the targeted, comment-preserving splice reads ITS OWN raw text from
                // targetPath and edits only the Password/PasswordProtected characters in it — safe
                // ONLY when targetPath's on-disk content IS seedObject (an in-place edit). When the
                // T3-above reseed logic just cleared statePrimaryObject to fall through to
                // primaryObject, seedObject was parsed from a DIFFERENT file (primaryPath) than
                // targetPath (statePrimaryPath) — the state-dir file may still physically hold
                // STALE content there (e.g. an old ManagementServerUri/Username) that a splice
                // would silently leave untouched. That case needs a full reseed (reserialize the
                // FRESH seedObject wholesale), not a targeted edit of the stale bytes already on
                // disk — allowTargetedSplice: false skips straight to that.
                RewritePasswordFields(seedObject, targetPath, protectedValue, allowTargetedSplice: seedCameFromTargetPath);
            }
            else
            {
                // Defensive fallback: Password was non-empty but no on-disk copy (primary, state,
                // or overlay) existed to rewrite in place (shouldn't happen — Password can only be
                // non-empty if it was read from one of them). Write the full effective config so
                // the migration still self-heals rather than silently losing the protected blob.
                Save(config, exeDirWritable ? primaryPath : (statePrimaryPath ?? Path.Combine(stateDir, primaryFileName)));
            }
        }

        // --- Health.BearerToken -> Health.BearerTokenProtected migration -----------------------
        // Mirrors the Password -> PasswordProtected migration immediately above field-for-field
        // (same DPAPI-unavailable degradation, same overlay-vs-primary-vs-state-dir rewrite-target
        // selection, same targeted-splice-with-reserialize-fallback rewrite) — see that block's
        // comments for the rationale behind each branch. Kept as a SEPARATE block rather than a
        // shared helper: the two fields differ in one structural way (BearerToken is nested one
        // level down, under "Health" — see sectionName below) that would otherwise thread an extra
        // parameter through every line of the Password block purely for BearerToken's benefit, and
        // the Password path above is covered by 30+ existing tests this feature must not risk
        // regressing by refactoring code they pin.
        if (!string.IsNullOrEmpty(config.Health.BearerToken))
        {
            string protectedToken;
            try
            {
                protectedToken = _protector.Protect(config.Health.BearerToken);
            }
            catch (CryptographicException ex)
            {
                _log?.Invoke(LogLevel.Warning,
                    "DPAPI is unavailable in this Windows session (Protect failed: " + ex.Message +
                    ") — running with the plaintext Health.BearerToken for this session only. The config " +
                    "file is left unchanged; encryption/migration will be retried on the next start.");
                return config;
            }
            config.Health.BearerTokenProtected = protectedToken;
            config.Health.BearerToken = string.Empty;

            var overlayHealthObject = overlayObject is not null ? GetNestedObject(overlayObject, "Health") : null;
            var overlayBearerToken = overlayHealthObject is not null ? GetStringOrEmpty(overlayHealthObject, "BearerToken") : string.Empty;
            if (!string.IsNullOrEmpty(overlayBearerToken) && overlayObject is not null && overlayPath is not null)
            {
                if (exeDirWritable)
                {
                    RewritePasswordFields(overlayObject, overlayPath, protectedToken,
                        plainFieldName: "BearerToken", protectedFieldName: "BearerTokenProtected", sectionName: "Health");
                }
                else
                {
                    // T5/R6 mirror: a state-dir copy of the OVERLAY file is never read back on a
                    // later load, so writing one here would be a copy nothing ever reads.
                    _log?.Invoke(LogLevel.Warning,
                        $"'{overlayPath}' contains a plaintext Health.BearerToken but its directory isn't " +
                        "writable by this account, so it cannot be auto-blanked. It stays plaintext on disk; " +
                        "remove it manually.");
                }
            }
            else if (primaryObject is not null || statePrimaryObject is not null)
            {
                bool seedCameFromTargetPath = exeDirWritable || statePrimaryObject is not null;
                var seedObject = exeDirWritable ? primaryObject! : (statePrimaryObject ?? primaryObject!);
                var targetPath = exeDirWritable ? primaryPath : (statePrimaryPath ?? Path.Combine(stateDir, primaryFileName));
                RewritePasswordFields(seedObject, targetPath, protectedToken,
                    allowTargetedSplice: seedCameFromTargetPath,
                    plainFieldName: "BearerToken", protectedFieldName: "BearerTokenProtected", sectionName: "Health");
            }
            else
            {
                // Defensive fallback — same rationale as Password's identical branch above.
                Save(config, exeDirWritable ? primaryPath : (statePrimaryPath ?? Path.Combine(stateDir, primaryFileName)));
            }
        }

        return config;
    }

    /// <summary>
    /// Read-only counterpart to <see cref="LoadOrCreate"/> for <c>GridLookout.exe --health-probe</c>
    /// (see <c>GridLookout.Monitoring.HealthProbe</c>) — a probe invocation typically runs on a
    /// short interval (the watchdog scheduled task) and must NEVER perform any of
    /// <see cref="LoadOrCreate"/>'s side effects: no first-run template seeding, no Password/
    /// BearerToken migration/blanking, no T3 snapshot-shadowing reseed write, not even the T4(a)
    /// stuck-plaintext diagnostic log line (the controller process already logs that once at its
    /// own startup; repeating it every probe tick would be log spam, and writing anything at all
    /// from a process that runs every minute risks racing the controller's own writes).
    ///
    /// Merges the SAME three layers <see cref="LoadOrCreate"/> does (exe-dir primary -&gt; state-dir
    /// copy, when the exe dir isn't writable -&gt; dev overlay) in the SAME precedence, but WITHOUT
    /// the T3 staleness comparison between the exe-dir file and the state-dir copy — the state-dir
    /// copy (when one exists) always wins here. In practice this can read a point-in-time-stale
    /// Health config exactly once, immediately after an admin hand-edits the exe-dir file directly
    /// on an unwritable-exe-dir kiosk; the running wall's own next <see cref="LoadOrCreate"/> call
    /// (its own restart) performs the real reseed, after which this method's plain
    /// "state-dir-wins" merge is reading fresh content again — an acceptable eventual-consistency
    /// tradeoff for a mode that must not itself write to disk.
    /// </summary>
    public WallConfig LoadReadOnly(string directory, string primaryFileName = "camerawall.json", string? overlayFileName = "camerawall.local.json")
    {
        var primaryPath = Path.Combine(directory, primaryFileName);
        var overlayPath = overlayFileName is null ? null : Path.Combine(directory, overlayFileName);

        bool exeDirWritable = _stateDirectory.Resolve(directory, out var stateDir);

        JsonObject? primaryObject = File.Exists(primaryPath) ? ParseObject(File.ReadAllText(primaryPath)) : null;
        JsonObject? overlayObject = (overlayPath is not null && File.Exists(overlayPath)) ? ParseObject(File.ReadAllText(overlayPath)) : null;

        JsonObject? statePrimaryObject = null;
        if (!exeDirWritable)
        {
            var statePrimaryPath = Path.Combine(stateDir, primaryFileName);
            statePrimaryObject = File.Exists(statePrimaryPath) ? ParseObject(File.ReadAllText(statePrimaryPath)) : null;
        }

        var merged = primaryObject is not null ? (JsonObject)primaryObject.DeepClone() : new JsonObject();
        if (statePrimaryObject is not null)
        {
            foreach (var kvp in statePrimaryObject)
            {
                merged[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
        if (overlayObject is not null)
        {
            foreach (var kvp in overlayObject)
            {
                merged[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        return merged.Deserialize<WallConfig>(SerializerOptions) ?? new WallConfig();
    }

    /// <summary>Returns the effective plaintext password: decrypts <see cref="WallConfig.PasswordProtected"/>
    /// if present, otherwise falls back to a (should-no-longer-exist) plaintext <see cref="WallConfig.Password"/>.
    /// T4(b)/R4: a <see cref="CryptographicException"/> here means the protected blob was created
    /// under a DIFFERENT Windows account than the one currently running GridLookout (DPAPI is
    /// scoped to the encrypting account — see <see cref="DpapiSecretProtector"/>) — logs a clear
    /// Error line naming the fix, then rethrows so the existing login-retry loop
    /// (Program.cs's LoginRetryLoop) handles it exactly like any other failed login attempt: no
    /// crash, no new on-screen text, just another retry-with-countdown card.</summary>
    public string GetPassword(WallConfig config)
    {
        if (!string.IsNullOrEmpty(config.PasswordProtected))
        {
            try
            {
                return _protector.Unprotect(config.PasswordProtected);
            }
            catch (CryptographicException ex)
            {
                var pathDescription = EffectiveConfigPath ?? "the config file holding PasswordProtected";
                _log?.Invoke(LogLevel.Error,
                    $"DPAPI could not decrypt PasswordProtected in '{pathDescription}' — this blob was created " +
                    "under a DIFFERENT Windows account than the one currently running GridLookout. Fix: delete " +
                    $"that file and re-run as the account that owns this wall. Details: {ex.Message}");
                throw;
            }
            catch (FormatException ex)
            {
                // Round-3 panel-3 T5 fix: a mangled (non-base64) PasswordProtected value throws
                // FormatException from Convert.FromBase64String (inside the protector, before DPAPI
                // itself ever runs) — previously only CryptographicException was caught here, so this
                // case fell through to the generic retry-with-countdown card with zero guidance,
                // indistinguishable on screen from a wrong URL or a down server.
                var pathDescription = EffectiveConfigPath ?? "the config file holding PasswordProtected";
                _log?.Invoke(LogLevel.Error,
                    $"PasswordProtected in '{pathDescription}' is corrupt (not valid base64) and cannot be " +
                    "decrypted by any account. Fix: delete the PasswordProtected value from that file (or the " +
                    $"whole file) and re-enter the password so it can be re-protected. Details: {ex.Message}");
                throw;
            }
        }

        return config.Password ?? string.Empty;
    }

    /// <summary>Mirrors <see cref="GetPassword"/> exactly for <see cref="HealthConfig.BearerToken"/>/
    /// <see cref="HealthConfig.BearerTokenProtected"/> — same DPAPI-wedge and corrupt-blob error
    /// handling, same rethrow-after-logging contract. Returns empty string when no token is
    /// configured at all — a <see cref="HealthConfig.Endpoint"/> with no token is a valid, common
    /// setup (an unauthenticated POST to a customer's own collector), not an error.</summary>
    public string GetBearerToken(HealthConfig health)
    {
        if (!string.IsNullOrEmpty(health.BearerTokenProtected))
        {
            try
            {
                return _protector.Unprotect(health.BearerTokenProtected);
            }
            catch (CryptographicException ex)
            {
                var pathDescription = EffectiveConfigPath ?? "the config file holding Health.BearerTokenProtected";
                _log?.Invoke(LogLevel.Error,
                    $"DPAPI could not decrypt Health.BearerTokenProtected in '{pathDescription}' — this blob was " +
                    "created under a DIFFERENT Windows account than the one currently running GridLookout. Fix: " +
                    $"delete that value and re-run as the account that owns this wall. Details: {ex.Message}");
                throw;
            }
            catch (FormatException ex)
            {
                var pathDescription = EffectiveConfigPath ?? "the config file holding Health.BearerTokenProtected";
                _log?.Invoke(LogLevel.Error,
                    $"Health.BearerTokenProtected in '{pathDescription}' is corrupt (not valid base64) and cannot " +
                    "be decrypted by any account. Fix: delete that value and re-enter the token so it can be " +
                    $"re-protected. Details: {ex.Message}");
                throw;
            }
        }

        return health.BearerToken ?? string.Empty;
    }

    public void Save(WallConfig config, string path)
    {
        var json = JsonSerializer.Serialize(config, SerializerOptions);
        WriteAllTextAtomic(path, json);
    }

    /// <summary>
    /// M1 fix (2026-08-21 config-robustness review): one mistyped VALUE must never brick the whole
    /// config load. Pre-fix, a bare <c>merged.Deserialize&lt;WallConfig&gt;</c> meant a single type
    /// mismatch anywhere — <c>"MaxFps": "twelve"</c>, an int-overflow digit string,
    /// <c>"AuthMode": "Negotiate"</c> — threw out of <see cref="LoadOrCreate"/> into Program.cs's
    /// config-failed card and exited; on a watchdogged kiosk that is a permanent
    /// card-instead-of-video loop until a human visits, violating the north-star rule ("bad config
    /// values fall back with a logged warning") that <c>TileScaleModeParser</c> already models for
    /// its one field.
    ///
    /// Strategy — fast path plus per-field salvage, deliberately SCHEMA-FREE so it never needs
    /// updating when <see cref="WallConfig"/> grows a field (a hand-maintained field→type table
    /// would silently rot): the healthy-file case is the exact same single Deserialize as before.
    /// Only when that throws does the salvage pass probe each top-level property individually (a
    /// one-property Deserialize per probe — boot-time only, cost irrelevant); a property that fails
    /// as a whole and is an OBJECT or ARRAY is then probed one inner field / element at a time, so
    /// e.g. <c>"Health": { "Enabled": "yes", "Endpoint": "https://…" }</c> loses only
    /// <c>Health.Enabled</c> to its default, not the whole Health section — mirroring the
    /// per-monitor (not whole-wall) blast-radius discipline the layout resolver applies to bad
    /// tokens. Every dropped field is named in ONE Warning naming the built-in default takes over.
    /// Unknown keys pass probes untouched (System.Text.Json ignores them), same as before.
    /// </summary>
    private WallConfig DeserializeTolerantly(JsonObject merged)
    {
        try
        {
            return merged.Deserialize<WallConfig>(SerializerOptions) ?? new WallConfig();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var sanitized = new JsonObject();
            var dropped = new List<string>();

            foreach (var kvp in merged)
            {
                var value = kvp.Value;
                if (ProbeBind(kvp.Key, value))
                {
                    sanitized[kvp.Key] = value?.DeepClone();
                    continue;
                }

                if (value is JsonObject nested)
                {
                    var keptInner = new JsonObject();
                    foreach (var inner in nested)
                    {
                        if (ProbeBind(kvp.Key, new JsonObject { [inner.Key] = inner.Value?.DeepClone() }))
                        {
                            keptInner[inner.Key] = inner.Value?.DeepClone();
                        }
                        else
                        {
                            dropped.Add($"{kvp.Key}.{inner.Key}");
                        }
                    }

                    sanitized[kvp.Key] = keptInner;
                    continue;
                }

                if (value is JsonArray array)
                {
                    var keptElements = new JsonArray();
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (ProbeBind(kvp.Key, new JsonArray { array[i]?.DeepClone() }))
                        {
                            keptElements.Add(array[i]?.DeepClone());
                        }
                        else
                        {
                            dropped.Add($"{kvp.Key}[{i}]");
                        }
                    }

                    sanitized[kvp.Key] = keptElements;
                    continue;
                }

                dropped.Add(kvp.Key);
            }

            _log?.Invoke(LogLevel.Warning, dropped.Count > 0
                ? $"camerawall.json contains {dropped.Count} value(s) of the wrong type — using the built-in default for: " +
                  $"{string.Join(", ", dropped)}. Every other setting was loaded normally. Fix the named value(s) to clear " +
                  "this warning (the admin guide's configuration table shows each field's expected type)."
                : $"camerawall.json failed to bind ({ex.Message}) but no single field could be isolated as the cause — " +
                  "attempting to load it as-is field-by-field.");

            try
            {
                return sanitized.Deserialize<WallConfig>(SerializerOptions) ?? new WallConfig();
            }
            catch (Exception ex2) when (ex2 is JsonException or NotSupportedException)
            {
                // Pathological (every field passed its individual probe yet the combination still
                // fails) — should be unreachable, but the north-star rule holds regardless: run on
                // full defaults (Program.cs then shows the "not configured" card if the URI is
                // blank) rather than the config-failed card loop.
                _log?.Invoke(LogLevel.Error,
                    $"camerawall.json could not be loaded even after dropping mistyped fields ({ex2.Message}) — " +
                    "running with built-in defaults for every setting this session.");
                return new WallConfig();
            }
        }
    }

    /// <summary>One-property binding probe for <see cref="DeserializeTolerantly"/>'s salvage pass —
    /// true when a <see cref="WallConfig"/> containing ONLY this property deserializes cleanly.
    /// Clones the candidate node (a <see cref="JsonNode"/> can have at most one parent, and the
    /// original must stay attached to the merged tree).</summary>
    private static bool ProbeBind(string key, JsonNode? value)
    {
        try
        {
            _ = new JsonObject { [key] = value?.DeepClone() }.Deserialize<WallConfig>(SerializerOptions);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>M2 fix (2026-08-21 config-robustness review): the atomic string-payload twin of
    /// <c>Monitoring.AtomicBinaryFileWriter</c> — same temp-file-then-<see cref="File.Replace(string, string, string?)"/>
    /// (or <see cref="File.Move(string, string)"/> when the destination doesn't exist yet)
    /// algorithm, same guarantee: the destination only ever holds its previous complete content or
    /// the new complete content, never a torn/truncated intermediate. Every config write in this
    /// class MUST go through here, never bare <see cref="File.WriteAllText(string, string)"/> —
    /// a power cut mid-write used to leave a truncated camerawall.json behind, which the next
    /// boot's parse throws on, landing the kiosk in the config-error-card loop until a human
    /// visits (exactly the "bad config must degrade, never brick" north-star violation the
    /// tolerant-bind fix above also closes from the read side). Kept as a local private mirror
    /// rather than calling the Monitoring type directly so the Config layer doesn't grow a
    /// dependency on the Monitoring namespace (Monitoring already depends on Config —
    /// <c>HealthEndpointClient</c> consumes <see cref="HealthConfig"/>).</summary>
    private static void WriteAllTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        var tempPath = string.IsNullOrEmpty(directory)
            ? $"{path}.tmp-{Guid.NewGuid():N}"
            : Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");

        File.WriteAllText(tempPath, content);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temp cleanup — the write failure itself is what the caller needs to
                // see; a leftover .tmp file is a minor annoyance, never worth masking it for.
            }

            throw;
        }
    }

    /// <summary>T6: see <see cref="LoadOrCreate"/>'s "FIRST-RUN TEMPLATE SEEDING" doc comment for
    /// the full rationale. Copies <paramref name="templatePath"/>'s raw text (comments intact) to
    /// whichever location is authoritative for a fresh write — <paramref name="primaryPath"/> when
    /// <paramref name="exeDirWritable"/>, otherwise <paramref name="primaryFileName"/> under
    /// <paramref name="stateDir"/> — but ONLY when both (a) <paramref name="primaryPath"/> (the
    /// exe-dir file — always the trigger, regardless of where the write ultimately lands) is
    /// missing and (b) the seed target itself doesn't already exist. (b) matters only in the
    /// state-dir case: it stops this from ever clobbering an already-configured kiosk whose
    /// exe-dir file happens to be absent/inaccessible but whose ProgramData copy is the real,
    /// working config — "never overwrite an existing camerawall.json anywhere" holds even though
    /// the exe-dir and state-dir files have different identities.</summary>
    private void SeedFromTemplateIfMissing(string primaryPath, string templatePath, bool exeDirWritable, string stateDir, string primaryFileName)
    {
        if (File.Exists(primaryPath) || !File.Exists(templatePath))
        {
            return;
        }

        var seedTargetPath = exeDirWritable ? primaryPath : Path.Combine(stateDir, primaryFileName);
        if (File.Exists(seedTargetPath))
        {
            return;
        }

        try
        {
            // Raw text copy, NOT File.Copy — the state-dir target's directory may not exist yet
            // (StateDirectory.Resolve only guarantees it CAN be created, see its own doc comment),
            // and File.WriteAllText needs the same directory-exists precondition either way, so
            // read+write covers both targets uniformly without a separate Directory.CreateDirectory
            // call for the exe-dir case (which always already exists — it holds the exe itself).
            if (!exeDirWritable)
            {
                Directory.CreateDirectory(stateDir);
            }

            var templateText = File.ReadAllText(templatePath);
            WriteAllTextAtomic(seedTargetPath, templateText);
            _log?.Invoke(LogLevel.Info, $"Seeded '{seedTargetPath}' from template '{templatePath}' (camerawall.json did not exist yet). Edit the seeded file to configure this wall.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // T6: never let a seeding failure (e.g. a race against a second launch, or a
            // permission surprise this run's earlier writable-probe didn't catch) block config
            // load — LoadOrCreate falls through to its normal "file still missing" path, which
            // Program.cs already handles via the generic "not configured" card.
            _log?.Invoke(LogLevel.Warning, $"Could not seed '{seedTargetPath}' from template '{templatePath}': {ex.Message}. Continuing without a seeded config.");
        }
    }

    /// <summary>
    /// T3: rewrites only the Password/PasswordProtected VALUES of the on-disk file at
    /// <paramref name="path"/> — every other byte is preserved exactly as it was, INCLUDING jsonc
    /// <c>//</c> comments the shipped template carries (T6's first-run seeding preserves them via a
    /// raw text copy; before this fix, the very next migration silently destroyed them by
    /// reserializing the whole file through <see cref="JsonObject.ToJsonString"/> — which, unlike
    /// the read side's <see cref="JsonCommentHandling.Skip"/>, has no comment-preserving write mode
    /// at all).
    ///
    /// Does a targeted raw-text splice instead of a parse/reserialize round-trip: scans
    /// <paramref name="path"/>'s own text for each field's JSON string value (see
    /// <see cref="TryFindJsonStringValue"/>) and replaces only the characters between its quotes.
    /// Falls back to the old full-reserialize behavior (<see cref="RewritePasswordFieldsViaReserialize"/>
    /// — still correct, just loses comments) in three cases, each logged as a Warning naming the
    /// reason: (a) <paramref name="path"/> doesn't exist yet or can't be read — nothing to splice
    /// against (this is the normal, unremarkable path when a migration seeds a brand-new file at a
    /// DIFFERENT location than the source it was seeded from, e.g. a state-dir copy that doesn't
    /// exist yet — see <see cref="LoadOrCreate"/>'s writable-state fallback); (b) either field's key
    /// isn't present in the file's raw text as a plain JSON string value at all — the splice has no
    /// anchor to replace (or insert) against; (c) an existing Password/PasswordProtected value
    /// contains a backslash-escape (an embedded <c>\"</c> or <c>\\</c>) — not expected for either
    /// field in practice, but the simple quote-scanning splice below cannot safely locate the true
    /// end of such a value, so rather than risk corrupting the file it bails.
    /// </summary>
    /// <param name="allowTargetedSplice">False when <paramref name="path"/>'s on-disk content is
    /// NOT known to be <paramref name="sourceObject"/> itself — e.g. the T3/R3 snapshot-shadowing
    /// reseed in <see cref="LoadOrCreate"/>, where a newer exe-dir file's content is being written
    /// into a DIFFERENT, possibly stale, state-dir file. Reading and editing that stale file's raw
    /// text would silently leave its other fields (a stale ManagementServerUri, etc.) untouched
    /// instead of being overwritten by the fresh seed — so this skips straight to
    /// <see cref="RewritePasswordFieldsViaReserialize"/> (a full, correct overwrite) without even
    /// attempting a splice or logging a bailout warning, since this isn't an anomaly, just a case
    /// the targeted splice was never meant to handle. Defaults to true (the common, same-file
    /// in-place-edit case).</param>
    /// <param name="plainFieldName">Defaults to "Password" — pass "BearerToken" for the
    /// Health.BearerToken migration (see <see cref="LoadOrCreate"/>'s BearerToken block), which
    /// shares this entire rewrite engine rather than duplicating it.</param>
    /// <param name="protectedFieldName">Defaults to "PasswordProtected" — pairs with
    /// <paramref name="plainFieldName"/>.</param>
    /// <param name="sectionName">Null (default) for a top-level field (Password's case) — the name
    /// of the nesting JSON object (e.g. "Health") for a nested field (BearerToken's case). Only
    /// consulted by the reserialize fallback (<see cref="RewritePasswordFieldsViaReserialize"/>);
    /// the targeted text splice locates fields by a global regex scan of the raw file text and does
    /// not need to know about nesting at all.</param>
    private void RewritePasswordFields(JsonObject sourceObject, string path, string protectedValue,
        bool allowTargetedSplice = true, string plainFieldName = "Password", string protectedFieldName = "PasswordProtected", string? sectionName = null)
    {
        // M2 fix (2026-08-21 config-robustness review): the whole write chain below — targeted
        // splice AND the reserialize fallback — degrades on a transient filesystem failure (file
        // locked by a backup agent, read-only attribute, disk full) instead of throwing out of
        // LoadOrCreate into Program.cs's config-failed card, which would brick the boot over a
        // condition the next start may not even have. Same philosophy as the DPAPI-unavailable
        // catch in LoadOrCreate: run this session on the in-memory value, leave the file exactly
        // as it was, retry the migration on the next start. The plaintext staying on disk one more
        // session is the status quo that migration exists to improve, not a new exposure.
        try
        {
            if (allowTargetedSplice)
            {
                string? rawText = null;
                try
                {
                    if (File.Exists(path))
                    {
                        rawText = File.ReadAllText(path);
                    }
                }
                catch (IOException)
                {
                    rawText = null;
                }

                string? bailoutReason;
                if (rawText is not null)
                {
                    if (TryRewritePasswordFieldsInPlace(rawText, protectedValue, plainFieldName, protectedFieldName, out var rewrittenText, out bailoutReason))
                    {
                        WriteAllTextAtomic(path, rewrittenText);
                        return;
                    }
                }
                else
                {
                    bailoutReason = "the file does not exist yet at this path (nothing to splice against)";
                }

                _log?.Invoke(LogLevel.Warning,
                    $"Could not perform a comment-preserving rewrite of {plainFieldName}/{protectedFieldName} in '{path}' " +
                    $"({bailoutReason}) — falling back to a full reserialize, which strips any jsonc comments " +
                    "in this file.");
            }

            RewritePasswordFieldsViaReserialize(sourceObject, path, protectedValue, plainFieldName, protectedFieldName, sectionName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke(LogLevel.Warning,
                $"Could not write the {plainFieldName} -> {protectedFieldName} migration to '{path}' " +
                $"({ex.GetType().Name}: {ex.Message}) — running this session with the in-memory value; " +
                "the file is left untouched and the migration is retried on the next start.");
        }
    }

    /// <summary>The pre-T3 behavior: reserialize the whole in-memory <paramref name="sourceObject"/>
    /// tree with the two fields set, adding either key if it was missing. Kept as the fallback for
    /// the cases <see cref="RewritePasswordFields"/>'s targeted splice can't handle — see that
    /// method's doc comment for exactly when. Loses jsonc comments (System.Text.Json has no
    /// comment-preserving write mode), but never corrupts the file.</summary>
    /// <param name="sectionName">Null writes <paramref name="plainFieldName"/>/<paramref name="protectedFieldName"/>
    /// directly on the cloned top-level object (Password's case). Non-null descends into (creating,
    /// if entirely absent — e.g. a pre-Health-feature camerawall.json) a nested object under this
    /// key first (BearerToken's "Health" case) — every OTHER Health.* setting then falls back to
    /// <c>HealthConfig</c>'s own C# defaults on the next load, same "missing key -&gt; type default"
    /// rule every other config section already follows.</param>
    private static void RewritePasswordFieldsViaReserialize(JsonObject sourceObject, string path, string protectedValue,
        string plainFieldName = "Password", string protectedFieldName = "PasswordProtected", string? sectionName = null)
    {
        var rewritten = (JsonObject)sourceObject.DeepClone();
        JsonObject target = rewritten;
        if (sectionName is not null)
        {
            var existingSection = GetNestedObject(rewritten, sectionName);
            if (existingSection is null)
            {
                existingSection = new JsonObject();
                rewritten[sectionName] = existingSection;
            }
            target = existingSection;
        }

        SetStringValue(target, plainFieldName, string.Empty);
        SetStringValue(target, protectedFieldName, protectedValue);

        var json = rewritten.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteAllTextAtomic(path, json);
    }

    /// <summary>Attempts the targeted, comment-preserving splice described on
    /// <see cref="RewritePasswordFields"/>. Returns false (with <paramref name="bailoutReason"/> set)
    /// when either field's key isn't present as a plain JSON string value, or when an existing value
    /// contains a backslash-escape this simple scanner can't safely skip past. Locates fields by a
    /// GLOBAL regex scan of <paramref name="text"/> (see <see cref="TryFindJsonStringValue"/>), so
    /// nesting (e.g. BearerToken living under "Health") needs no special handling here — the exact
    /// key name is unique enough in practice that a plain text scan finds it regardless of depth.</summary>
    private static bool TryRewritePasswordFieldsInPlace(string text, string protectedValue, string plainFieldName, string protectedFieldName, out string result, out string? bailoutReason)
    {
        result = text;
        bailoutReason = null;

        bool foundPassword = TryFindJsonStringValue(text, plainFieldName, out var pwStart, out var pwEnd, out var pwHasEscape);
        bool foundProtected = TryFindJsonStringValue(text, protectedFieldName, out var ppStart, out var ppEnd, out var ppHasEscape);

        if (!foundPassword || !foundProtected)
        {
            bailoutReason = $"\"{plainFieldName}\" and/or \"{protectedFieldName}\" was not found as a plain (unescaped) JSON string value in this file";
            return false;
        }

        if (pwHasEscape || ppHasEscape)
        {
            bailoutReason = $"an existing {plainFieldName}/{protectedFieldName} value contains a backslash-escaped character, which this targeted rewrite does not support";
            return false;
        }

        // Apply from the LATER offset first so an earlier edit's insertion never shifts the
        // character positions already computed for a later one.
        var edits = new List<(int Start, int End, string Value)>
        {
            (pwStart, pwEnd, string.Empty),
            (ppStart, ppEnd, EscapeJsonStringValue(protectedValue)),
        };
        edits.Sort((a, b) => b.Start.CompareTo(a.Start));

        var sb = new StringBuilder(text);
        foreach (var edit in edits)
        {
            sb.Remove(edit.Start, edit.End - edit.Start);
            sb.Insert(edit.Start, edit.Value);
        }

        result = sb.ToString();
        return true;
    }

    /// <summary>Locates <paramref name="propertyName"/>'s JSON string value in <paramref name="text"/>
    /// — the raw index range of the characters BETWEEN its quotes (<paramref name="valueStart"/>
    /// inclusive, <paramref name="valueEnd"/> exclusive — i.e. the index of the closing quote) — by
    /// an exact, literal <c>"PropertyName"</c> key match (case-insensitive, matching
    /// <see cref="GetStringOrEmpty"/>'s existing case-insensitive key lookup elsewhere in this
    /// class) followed by <c>:</c> and an opening quote. Returns false (all out params default) when
    /// the key isn't found, isn't followed by a string value, or the string value is unterminated.
    /// <paramref name="hasEscape"/> is true when the value contains a backslash escape sequence —
    /// see <see cref="TryRewritePasswordFieldsInPlace"/> for why that triggers a bailout rather than
    /// a (possibly corrupting) attempt to skip past it.</summary>
    private static bool TryFindJsonStringValue(string text, string propertyName, out int valueStart, out int valueEnd, out bool hasEscape)
    {
        valueStart = -1;
        valueEnd = -1;
        hasEscape = false;

        var keyMatch = Regex.Match(text, "\"" + Regex.Escape(propertyName) + "\"", RegexOptions.IgnoreCase);
        if (!keyMatch.Success)
        {
            return false;
        }

        int i = keyMatch.Index + keyMatch.Length;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        if (i >= text.Length || text[i] != ':')
        {
            return false;
        }
        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        if (i >= text.Length || text[i] != '"')
        {
            return false;
        }
        i++;
        valueStart = i;

        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\')
            {
                hasEscape = true;
                i += 2;
                continue;
            }
            i++;
        }

        if (i >= text.Length)
        {
            valueStart = -1;
            return false; // unterminated string value
        }

        valueEnd = i;
        return true;
    }

    /// <summary>Minimal JSON string escaping for the one value ever spliced in by
    /// <see cref="TryRewritePasswordFieldsInPlace"/> — the DPAPI-protected blob. In practice this is
    /// always base64 (<see cref="DpapiSecretProtector"/>), which needs no escaping at all, but this
    /// keeps the splice correct for any <see cref="ISecretProtector"/> implementation.</summary>
    private static string EscapeJsonStringValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>T3/R3: writes <paramref name="obj"/> verbatim to <paramref name="path"/> — used
    /// only for the "reseed the state-dir copy from a newer, materially-configured exe-dir file"
    /// case that has no plaintext Password to migrate (see <see cref="LoadOrCreate"/>); when there
    /// IS a plaintext Password, <see cref="RewritePasswordFields"/> performs the reseed AND the
    /// blank/protect in one write instead of calling this first.</summary>
    private static void WriteJsonObject(JsonObject obj, string path)
    {
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteAllTextAtomic(path, json);
    }

    /// <summary>Looks up <paramref name="sectionName"/> as a nested <see cref="JsonObject"/> within
    /// <paramref name="obj"/> — case-insensitive, matching <see cref="GetStringOrEmpty"/>'s existing
    /// key-lookup convention. Used for <c>Health.BearerToken</c>/<c>Health.BearerTokenProtected</c>,
    /// which live one level down from every other migration-relevant field this class handles.
    /// Returns null when <paramref name="obj"/> itself is null, the key is absent, or the value
    /// present under that key isn't a JSON object (e.g. a hand-edited file where "Health" was
    /// accidentally set to a string or number) — every call site treats null exactly like "no
    /// plaintext/protected value here", never throws.</summary>
    private static JsonObject? GetNestedObject(JsonObject? obj, string sectionName)
    {
        if (obj is null)
        {
            return null;
        }

        var match = obj.FirstOrDefault(p => string.Equals(p.Key, sectionName, StringComparison.OrdinalIgnoreCase));
        return match.Value as JsonObject;
    }

    private static string GetStringOrEmpty(JsonObject obj, string propertyName)
    {
        var match = obj.FirstOrDefault(p => string.Equals(p.Key, propertyName, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null || match.Value is null)
        {
            return string.Empty;
        }

        try
        {
            return match.Value.GetValue<string>() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            // Value present but not a JSON string (shouldn't happen for Password) — treat as empty.
            return string.Empty;
        }
    }

    private static void SetStringValue(JsonObject obj, string propertyName, string value)
    {
        var existingKey = obj.FirstOrDefault(p => string.Equals(p.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Key;
        obj[existingKey ?? propertyName] = value;
    }

    private static JsonObject ParseObject(string json)
    {
        var node = JsonNode.Parse(json, nodeOptions: null, documentOptions: ParseOptions);
        return node as JsonObject ?? new JsonObject();
    }
}
