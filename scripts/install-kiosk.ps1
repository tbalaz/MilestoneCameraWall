<#
.SYNOPSIS
    Wires GridLookout into the CURRENT user's session as a kiosk: autolaunch (Run key or
    shell replacement) plus a watchdog scheduled task (unless -NoWatchdog). Run it while logged on
    as whichever account should show the wall.

    OUT OF SCOPE - this script touches no accounts and no machine policy:
    - Accounts: it configures whoever runs it (HKCU only). Create/choose the kiosk account with
      your own tooling and log on as it.
    - Autologon: machine-level Windows policy. Use Sysinternals Autologon
      (https://learn.microsoft.com/sysinternals/downloads/autologon) or your management tooling.
    - Credentials: the app's ONE credential (the Management Server account) lives in
      camerawall.json - set Username/Password there at first run; the app encrypts the password
      (DPAPI, this user) on its next start. Nothing account-related is stored by this script.
    Without autologon the wallboard still autolaunches on every logon of this user; with it, the
    chain is complete: boot -> logon -> autolaunch -> watchdog keeps it running.

.PARAMETER Shell
    Switch. When set, replaces THIS user's shell (HKCU\...\Winlogon\Shell) with the wallboard exe
    directly - true kiosk, no explorer.exe, nothing behind the video wall. Ctrl+Alt+Del remains
    available for recovery. When NOT set (default), the wallboard is launched via this user's Run
    key instead - desktop/explorer still exists behind it.

.PARAMETER ExePath
    Full path to GridLookout.exe. Required for a normal (install) run. NOT required -- and not
    accepted together -- with -Uninstall: T4/R4 fix, -Uninstall only touches this user's HKCU
    autolaunch keys and the (name-keyed, not path-keyed) watchdog scheduled task, so it needs no
    exe path at all, and demanding one broke uninstall at exactly the moment it's most likely to be
    run: after the MSI (and GridLookout.exe itself) have already been removed.

.PARAMETER NoWatchdog
    Switch. Skips registering the relaunch watchdog scheduled task (step 2 below). By default the
    watchdog IS registered regardless of -Shell — a plain Run-key install left the wall just as
    unprotected against a crash, an Esc exit, or a closed compact-mode window as a -Shell install,
    so E6/I7/M6 removed the old -Shell-only gate. Use -NoWatchdog only if something else already
    supervises the process (e.g. a different watchdog, or a container/session manager).

.PARAMETER RestartHung
    Switch. Wall-health monitoring (F1) opt-in extra — has NO effect unless camerawall.json's
    Health.Enabled is also true (health.json otherwise never exists, so the probe below never
    reports "hung" at all). When both are on, the watchdog additionally kills and relaunches a
    process the watchdog judges HUNG — either because its OWN health.json says its UI thread is hung
    (GridLookout.exe --health-probe exit code 2), OR (buyer-review defect #3 fix) because health.json
    is ABSENT while the process has been running for longer than 3x Health.StaleAfterSeconds — a
    startup/early hang that never got as far as writing a first health.json used to be silently
    invisible to this switch entirely; not merely one whose VIDEO looks stale, which would fight
    GridLookout.Recovery.SessionLossDetector's own backoff-gated recovery. Default OFF: a hung
    process is still visible to an admin (or a customer's own monitoring agent reading health.json/
    the optional HTTPS POST) without this script ever killing anything on its own; opt in only once
    you're comfortable with that tradeoff.

.PARAMETER Uninstall
    Switch. Removes the autolaunch wiring (Run key or Shell value) and the watchdog task for this
    user (registered by either mode, whether or not -NoWatchdog was used at install time —
    Unregister-ScheduledTask no-ops harmlessly if it was never registered), then exits. Takes no
    other parameter -- see -ExePath's note above.

.EXAMPLE
    .\install-kiosk.ps1 -ExePath 'C:\Program Files\GridLookout\GridLookout.exe' -Shell

.EXAMPLE
    .\install-kiosk.ps1 -ExePath 'C:\Program Files\GridLookout\GridLookout.exe' -NoWatchdog

.EXAMPLE
    .\install-kiosk.ps1 -Uninstall
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [switch]$Shell,

    # T4/R4 fix: mandatory only in the 'Install' parameter set. -Uninstall (its own, separate
    # parameter set below) needs no exe path -- see the .PARAMETER ExePath doc above -- so it no
    # longer demands one that may no longer even exist on disk by the time uninstall runs.
    [Parameter(ParameterSetName = 'Install', Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$ExePath,

    [Parameter(ParameterSetName = 'Install')]
    [switch]$NoWatchdog,

    [Parameter(ParameterSetName = 'Install')]
    [switch]$RestartHung,

    [Parameter(ParameterSetName = 'Uninstall')]
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$winlogonKey = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
$taskName = 'GridLookout-Watchdog'

if ($Uninstall) {
    Write-Host "Removing kiosk wiring for user '$env:USERNAME'..."
    Remove-ItemProperty -Path $runKey -Name 'GridLookout' -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $winlogonKey -Name 'Shell' -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Done - autolaunch and watchdog removed. (Autologon, if configured, is yours to remove.)"
    return
}

# ---------------------------------------------------------------------------
# Step 1: autolaunch for the CURRENT user - Run key (default) or shell replacement (-Shell).
# ---------------------------------------------------------------------------
if ($Shell) {
    Write-Host "Configuring -Shell mode: replacing $env:USERNAME's Winlogon Shell with the wallboard exe..."
    if (-not (Test-Path $winlogonKey)) {
        New-Item -Path $winlogonKey -Force | Out-Null
    }
    Set-ItemProperty -Path $winlogonKey -Name 'Shell' -Value "`"$ExePath`"" -Type String
} else {
    Write-Host "Configuring Run-key autolaunch for '$env:USERNAME' (desktop/explorer stays present behind the app)..."
    Set-ItemProperty -Path $runKey -Name 'GridLookout' -Value "`"$ExePath`"" -Type String
}

# ---------------------------------------------------------------------------
# Step 2: watchdog scheduled task - checked every minute, registered for THIS user, LogonType
# Interactive - deliberately not SYSTEM: a SYSTEM-launched GUI process lands in session 0, invisible
# on the kiosk desktop. The task only runs while this user has a session, which admin-managed
# autologon guarantees on an unattended box. Covers app crashes, a user closing the window from
# compact mode, and Esc exits.
#
# E6/I7/M6: registered in BOTH autolaunch modes by default, not just -Shell - a Run-key install
# (the default mode) leaves exactly the same unsupervised-exit gap as -Shell, so gating watchdog
# registration on -Shell was never correct. -NoWatchdog is the explicit opt-out for a deployment
# that already has its own supervision.
#
# F1/wall-health: the watchdog body now runs "<exe> --health-probe" (GridLookout.Monitoring.HealthProbe)
# instead of only checking whether a process named GridLookout exists, but the process-existence
# check STILL runs first and gates the relaunch, for two reasons: (1) it is the fallback for the
# common case where Health.Enabled is false in camerawall.json (health.json then never exists, so
# the probe always returns exit code 3/absent — confirming absence via the process check as well
# means this script's relaunch behavior needs no knowledge of whether the feature is even on), and
# (2) exit code 3 also covers "the recorded pid doesn't match a live process" (a stale/foreign
# health.json), which must NOT trigger a relaunch while the ACTUAL wall process is fine. Exit code 2
# (hung UI thread) only triggers a kill+relaunch when -RestartHung was passed at install time —
# see that parameter's own doc comment for why this defaults off.
#
# Buyer-review defect #3 fix: exit code 3 (absent) ALSO now counts as hung — but ONLY when
# Health.Enabled is true AND the process has been running for longer than 3x StaleAfterSeconds.
# Before this fix, a wall that hung/crashed during its very first login attempt (before EVER writing
# a health.json) was invisible to -RestartHung entirely: exit code 3 never triggered anything, no
# matter how long the process sat there. This closes that gap without breaking the two legitimate
# "absent" cases the process-existence check above already carries: Health.Enabled=false (the
# generated script reads that live and no-ops this rule when it's off, matching -RestartHung's own
# "no effect unless Health.Enabled" contract) and a process that only just started (the 3x-threshold
# grace period covers ordinary Starting/Connecting time before the very first health-write tick).
#
# Health.Enabled/StaleAfterSeconds are read from camerawall.json LIVE, INSIDE the generated
# watchdog script — every tick, not once at install time. They are runtime-editable settings (an
# admin can flip Health.Enabled on/off, or retune StaleAfterSeconds, without ever re-running this
# installer), so baking them in as install-time literals — the way $exeName/$ExePath/
# $restartHungLiteral legitimately are, since those genuinely don't change without a reinstall —
# would leave a watchdog task frozen on whatever the config said at install time (typically OFF,
# the shipped default) until someone remembered to re-run install-kiosk.ps1 after editing the config.
# $restartHungLiteral itself stays install-time-baked deliberately: it is a SCRIPT PARAMETER
# (-RestartHung), not a camerawall.json setting, so there is no live source to re-read it from.
#
# Built as a SEPARATE watchdog script, invoked via powershell.exe -EncodedCommand (a base64 blob of
# the script text, embedded install-time values for $exeName/$exePath/$restartHung baked in as
# PowerShell literals) rather than the old -Command "..." string. -EncodedCommand sidesteps the
# nested-quoting problem entirely: $ExePath can contain spaces (a "Program Files" install always
# does) and this script now has real control flow (if/elseif, a try/catch around the probe
# invocation and another around the live config read), which is exactly the shape that breaks
# silently inside a hand-escaped -Command string.
# `(Get-ScheduledTask -TaskName 'GridLookout-Watchdog').Actions.Arguments` after registration shows
# the raw base64 for anyone auditing the installed task; decode it
# ([System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($b64))) to read exactly
# what runs. The generated text is also parse-checked below before it's ever base64-encoded, so a
# quoting/escaping mistake in this file fails the install loudly instead of silently doing nothing
# on a kiosk at 3am.
# ---------------------------------------------------------------------------
if ($NoWatchdog) {
    Write-Host "Skipping watchdog registration (-NoWatchdog) - nothing will relaunch the wallboard if it crashes, is closed, or exits."
} else {
    Write-Host "Registering watchdog scheduled task (1-min interval, runs as '$env:USERNAME')..."
    $exeName = [System.IO.Path]::GetFileNameWithoutExtension($ExePath)
    $restartHungLiteral = if ($RestartHung) { '$true' } else { '$false' }

    # Install-time values ($exeName/$ExePath/$restartHungLiteral) are substituted into this
    # double-quoted here-string NOW, at install time - the scheduled task itself never re-resolves
    # them (they genuinely can't change without a reinstall/re-run). Backtick-escaped `$`-prefixed
    # names ($proc/$code/$hung/$healthOn/$staleAfter/$cfgPath/$cfgJson/$runningSeconds/
    # $LASTEXITCODE/$_/$env:ProgramData) are the ones meant to stay literal PowerShell variables
    # INSIDE the generated script, resolved fresh every minute when the watchdog actually runs — this
    # is what lets Health.Enabled/StaleAfterSeconds be edited in camerawall.json after install and
    # take effect on the very next tick, with no need to re-run this installer.
    $watchdogScriptText = @"
`$proc = @(Get-Process -Name '$exeName' -ErrorAction SilentlyContinue) | Select-Object -First 1
if (-not `$proc) {
    Start-Process -FilePath '$ExePath'
} else {
    try {
        & '$ExePath' --health-probe *> `$null
        `$code = `$LASTEXITCODE
    } catch {
        `$code = -1
    }
    `$hung = `$false
    if (`$code -eq 2) {
        `$hung = `$true
    } elseif (`$code -eq 3) {
        `$healthOn = `$false
        `$staleAfter = 30
        `$cfgPath = Join-Path (Split-Path '$ExePath' -Parent) 'camerawall.json'
        if (-not (Test-Path `$cfgPath)) {
            # StateDirectory's writable-state fallback (see docs/security.md's
            # "Writable-state fallback") — the exe directory wasn't writable at first run, so the
            # live config lives under %ProgramData%\GridLookout instead.
            `$cfgPath = Join-Path `$env:ProgramData 'GridLookout\camerawall.json'
        }
        if (Test-Path `$cfgPath) {
            try {
                `$cfgJson = Get-Content -Path `$cfgPath -Raw | ConvertFrom-Json
                if (`$cfgJson.Health) {
                    if (`$null -ne `$cfgJson.Health.Enabled) { `$healthOn = [bool]`$cfgJson.Health.Enabled }
                    if (`$null -ne `$cfgJson.Health.StaleAfterSeconds) { `$staleAfter = [int]`$cfgJson.Health.StaleAfterSeconds }
                }
            } catch {
                # Unreadable/malformed config this tick - stay with the safe defaults above
                # (health monitoring effectively OFF for this rule) rather than fail the watchdog.
            }
        }
        if (`$healthOn) {
            `$runningSeconds = ((Get-Date) - `$proc.StartTime).TotalSeconds
            if (`$runningSeconds -gt (3 * `$staleAfter)) {
                `$hung = `$true
            }
        }
    }
    if (`$hung -and $restartHungLiteral) {
        # M4 fix (2026-08-21 external audit): watchdog kills used to bypass every backoff — the
        # app's CrashRelaunchGuard marker is written only by its own fatal-exception relaunch path,
        # so a persistently slow Management Server (probe reads a blocked-but-recovering pump as
        # hung) became an indefinite kill/restart cycle. The probe itself now needs two consecutive
        # runs to confirm a hang (hysteresis); this adds the second layer: at most one hung-kill
        # per 10 minutes, tracked in a marker file next to the watchdog's own script. A kill
        # skipped here is re-evaluated next minute — nothing is lost, only rate-limited.
        `$killMarkerPath = Join-Path `$env:LOCALAPPDATA 'GridLookout\watchdog-last-hung-kill.txt'
        `$allowKill = `$true
        try {
            if (Test-Path `$killMarkerPath) {
                `$lastKill = [DateTime]::Parse((Get-Content -Path `$killMarkerPath -Raw).Trim(), [System.Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
                if (((Get-Date).ToUniversalTime() - `$lastKill).TotalMinutes -lt 10) { `$allowKill = `$false }
            }
        } catch {
            # Unreadable marker - treat as no prior kill (worst case: one kill sooner than the cap).
        }
        if (`$allowKill) {
            try {
                New-Item -ItemType Directory -Force (Split-Path `$killMarkerPath -Parent) | Out-Null
                Set-Content -Path `$killMarkerPath -Value ((Get-Date).ToUniversalTime().ToString('o'))
            } catch {
                # Marker write failing must not stop the kill itself.
            }
            Stop-Process -Id `$proc.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            Start-Process -FilePath '$ExePath'
        }
    }
}
"@

    # Parse-check the GENERATED watchdog script text itself, not just this outer installer - it's
    # the thing that actually runs every minute via -EncodedCommand, and a here-string
    # escaping/quoting mistake in the block above would otherwise fail silently on a kiosk instead
    # of loudly here at install time.
    $watchdogParseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($watchdogScriptText, [ref]$null, [ref]$watchdogParseErrors) | Out-Null
    if ($watchdogParseErrors -and $watchdogParseErrors.Count -gt 0) {
        throw "Generated watchdog script failed to parse - this is a bug in install-kiosk.ps1, not a config problem: $($watchdogParseErrors -join '; ')"
    }

    $encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($watchdogScriptText))

    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -EncodedCommand $encodedCommand"
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration ([TimeSpan]::MaxValue)
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
}

Write-Host ""
Write-Host "Done. Summary:"
Write-Host "  Configured user:   $env:USERNAME (current session; HKCU only)"
Write-Host "  Autolaunch mode:   $(if ($Shell) { 'Shell replacement (true kiosk)' } else { 'Run key (desktop present)' })"
Write-Host "  Watchdog:          $(if ($NoWatchdog) { 'NOT registered (-NoWatchdog)' } else { "$taskName task, 1-min check, runs as $env:USERNAME" })"
Write-Host "  Restart on hang:   $(if ($NoWatchdog) { 'n/a' } elseif ($RestartHung) { 'ON (-RestartHung) - kills+relaunches on a hung UI thread (health-probe exit 2), or on an absent health.json after a long-running process (exit 3, only if Health.Enabled - read live from camerawall.json on every check, no reinstall needed after editing it) - see docs/admin-guide.md' } else { 'OFF (default) - a hung process is only reported, never killed by this script' })"
Write-Host "  Autologon:         NOT configured (machine policy - e.g. Sysinternals Autologon)"
Write-Host ""
Write-Host "Milestone credentials: set Username/Password (AuthMode Basic or Windows) in"
Write-Host "camerawall.json next to the exe. The plaintext Password is encrypted (DPAPI, this"
Write-Host "user) on the app's next start; or run '$ExePath --protect-password' now."
Write-Host ""
Write-Host "Wall-health monitoring (health.json + --health-probe) is OFF by default - set"
Write-Host "Health.Enabled: true in camerawall.json to turn it on; see docs/security.md."
