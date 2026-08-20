# Security & Network Behavior

This document describes every network connection the application makes, how the single
credential is stored, and what data leaves the machine. Every claim below has been checked
against the current source (`src/GridLookout`) as of this revision, and the source is public —
https://github.com/IT42-d-o-o/GridLookout — so every claim here can be verified by reading it.

## Network connections (complete list)

| Direction | Target | Port | Protocol | Purpose |
|---|---|---|---|---|
| Outbound | Management Server (`ManagementServerUri`) | as configured (80/443 default) | HTTP or HTTPS | Login, configuration read, recorder `$layout{}` description poll |
| — (same target as above) | — | — | — | The description poll specifically carries this app's session token as an `Authorization: Bearer` header and is **refused over plain `http://`** unless `AllowInsecureLayoutPoll=true` is explicitly set (default `false`) — see "Credential handling" below for the identical `AllowInsecureBasic` gate on login itself. Runs in the background (a single-flight worker, never on the UI thread), roughly every `ConfigRefreshSeconds`. |
| Outbound | Recording Server(s) of the matched recorder | 7563 (XProtect default) | Milestone ImageServer (TCP) | JPEG live streams, one connection per tile |
| Outbound (optional, **off by default**) | `Health.Endpoint` (customer-configured) | as configured | HTTPS POST (plain `http://` **refused** unless `Health.AllowInsecureEndpoint=true`, default `false` — the POST can carry the configured bearer token) | Wall-health liveness/aggregate report — see "Wall-health monitoring" below |

That is the entire list of connections **the application itself opens**. Specifically:

- **No inbound listener** — it is a pure client; no port is opened on the kiosk machine, in either
  the base product or the optional wall-health feature described below.
- **No telemetry, no analytics, no crash reporting sent by this application** — GridLookout
  itself never contacts its developer or any third party outside the targets above. Two
  disclosures worth being precise about, because both are true at the same time as the sentence
  before this one:
  - Every login carries a Milestone **Installed Integration Insights (III)** identifier — a
    fixed GUID + integration name required by the Milestone Technology Partner program to
    identify GridLookout to the Management Server it logs into (see the `IntegrationId` constant
    in `MilestoneSession.cs`). If the customer's own XProtect system has its own telemetry
    channel enabled — a setting the **customer** controls in their XProtect configuration, and
    one that cannot function at all on an air-gapped VMS — that channel may report which
    integrations are installed/in use,
    including GridLookout, to Milestone. That report travels over the customer's own VMS
    telemetry path, not a connection GridLookout opens.
  - The MIP SDK redistributable bundle GridLookout ships includes `Microsoft.ApplicationInsights.dll`
    as a required SDK dependency (Milestone's SDK links against it internally). GridLookout code
    does not call it, and its presence does not add a network target: the table above is
    independently verifiable at any time with a live netstat/firewall capture while the app runs.
- **No update check, no license server, no "phone home"** — updates are manual MSI installs
  (SHA-256 checksums published with each release). The application functions indefinitely
  without internet access; only reachability of the Management Server and Recording Server is
  required.
- **No DNS lookups** beyond resolving the two targets above (plus a local hostname/FQDN lookup
  used for automatic recorder matching — this stays on the local network/DNS resolver and never
  reaches the two targets above or anywhere else).

Air-gapped VMS networks are therefore fully supported — the application makes no connection
that leaves the VMS network. Customer-controlled XProtect telemetry (see above) is the one
channel outside GridLookout's own code that can report integration usage — it cannot function at
all on an air-gapped VMS (there is nowhere for it to send to), and on a network-connected VMS it
reports nothing about GridLookout, or any other integration, if the customer has disabled XProtect
telemetry. Neither case depends on anything GridLookout configures.

## Credential handling

The application uses exactly **one credential**: the XProtect (or AD/mirror) account it logs
in with. **Both** authentication modes require this one explicit, config-file credential — there
is no mode that uses the currently logged-on Windows identity or any other ambient credential.

- `AuthMode: "Basic"` (default) — an XProtect basic user. Authenticates via HTTP Basic.
- `AuthMode: "Windows"` — an explicit AD or local-mirror account (`Username` + `Domain` +
  password), authenticated via Negotiate. This is **not** "the current Windows identity" and it
  is **not** passwordless — it is a second explicit-credential mode, not an ambient one. The
  account must be filled in by the administrator exactly like Basic mode.
- In both modes the password lives in `camerawall.json` — normally next to the executable, with a
  documented fallback to `%ProgramData%\GridLookout\camerawall.json` when the install directory
  isn't writable by the account running the wall; see "Writable-state fallback" below for the full
  disclosure.
  1. At first run the administrator writes it as `"Password": "..."` (plaintext).
  2. On the next application start it is protected with **Windows DPAPI, CurrentUser scope**
     and written to `"PasswordProtected"`. It can only be decrypted by the same Windows user
     account on the same machine. The `Password` key is **not removed** from the file — its
     value is overwritten with an empty string (`""`) and the key remains present, empty,
     alongside the populated `PasswordProtected` key. This happens in whichever file/location the
     plaintext was actually read from and is writable — "Writable-state fallback" below covers the
     one case where the plaintext side of that cannot be completed.
  3. To encrypt immediately without starting the wall: `GridLookout.exe --protect-password`.
- Basic auth over plain HTTP is refused unless `AllowInsecureBasic: true` is set explicitly —
  the default requires HTTPS before a Basic-mode password is sent. This HTTP guard is specific
  to `AuthMode: "Basic"`; Negotiate (`AuthMode: "Windows"`) has no equivalent config check, so
  operators using Windows mode should still point `ManagementServerUri` at HTTPS as a matter of
  practice, not because the application enforces it for that mode.
- Recommended: create a dedicated XProtect basic user, or a dedicated AD/mirror account for
  Windows mode, with a role granting **live view only** on the wall's cameras. The application
  needs no playback, export, PTZ, or administration permission.

### --export-camera-bindings

Unlike `--health-probe`, this CLI mode performs a REAL login to the Management Server (it needs the
live camera catalog) using the credentials already configured in `camerawall.json`/DPAPI-protected
storage — same credential, same login path the wall itself uses. It writes only
`camera-bindings.generated.json` (camera names and guids — no credentials) and never touches
`camerawall.json` itself.

## Wall-health monitoring (optional, off by default)

GridLookout can optionally report its own liveness to an external observer — a customer's own
monitoring agent reading a local file, and/or an HTTPS collector. This is **entirely opt-in**:
`Health.Enabled` in `camerawall.json` defaults to `false`, and at that default the feature writes
nothing, opens nothing, and changes nothing else about this document's "no inbound listener, no
telemetry" claims above.

**Design.** The running wall (the long-lived kiosk process) only ever WRITES a local file,
`health.json`, on a 5-second timer — it never opens an outbound connection itself. A separate,
short-lived CLI invocation, `GridLookout.exe --health-probe`, reads that file back, checks the
recorded process is actually still alive and its message pump isn't hung, prints a one-line
verdict, and exits with a status code a watchdog script can act on. `scripts/install-kiosk.ps1`'s
watchdog scheduled task is the intended caller, once a minute, alongside its existing
process-existence check.

- **No inbound listener either way.** `--health-probe` makes no network connection to *receive*
  anything; it only optionally *sends* one outbound POST (see below) and then exits.
- **`health.json` location and content.** Lives under the same writable state directory as
  `camerawall.json` (see "Writable-state fallback" below) — normally next to the executable, with
  the same automatic `%ProgramData%\GridLookout` fallback. Written from process launch onward
  (`ControllerState` starts at `Starting`, before the very first login attempt — not only once the
  wall is already up). Contents are liveness/aggregate data only: schema version, a
  customer-supplied free-text `ControllerId`, process id and start time, a UI-thread pulse
  timestamp, a coarse controller state (`Starting`/`Connecting`/`Running`/`Recovering` — exact
  casing as written to the file), whether a
  configured `RecordingServers` selector currently matches no live recorder, whether an explicitly
  configured `LayoutRecorder` currently matches no selected recorder (`LayoutCarrierPinned` — the
  wall keeps its last-known-good layout while true), and, per configured
  monitor window, tile counts (expected / rendering / stalled / never-framed / **unavailable**) and
  the freshest render age — plus, in multi-recorder mode (`RecordingServers` configured), a
  per-recorder breakdown of the same tile counts KEYED BY the recorder's stable id (never by its
  display name — two differently-configured recorders can share a name), with that recorder's
  current display name carried alongside for readability, so one degraded recorder is visible even
  while others stay healthy. **It never contains camera names, the Management Server URI,
  credentials, or any VMS identity of any kind** — with one exception: recorder NAMES appear in
  multi-recorder mode's per-recorder breakdown, so treat `health.json` as VMS-identity-bearing in
  that configuration only.
- **The optional outbound POST.** Only attempted by `--health-probe`, only when `Health.Endpoint`
  is a non-empty, customer-configured URL, and only with the exact `health.json` content described
  above PLUS one additive field, `probeVerdict` — the probe's own independently-evaluated status
  string (identical to the `status` field in its printed stdout verdict) — nothing else is added,
  nothing else is collected. This addition is deliberate: the rest of the payload is the
  controller's SELF-report, which can never reach "unhealthy" for a STALE-PULSE hang specifically
  (a hung UI thread cannot self-diagnose its own hang — see "Design" above; that verdict is only
  reachable from the outside, by this same probe recomputing from the file's age); it CAN still
  reach "unhealthy" on its own for a condition it can see while still responsive — `Running` with
  zero configured wall windows. `probeVerdict` is what additionally lets the same POST body carry
  "absent" (and the stale-pulse "unhealthy"), the verdicts only an outside observer can reach. Uses
  `Authorization: Bearer <token>` when
  `Health.BearerToken`/`BearerTokenProtected` is configured, protected with the SAME DPAPI
  CurrentUser mechanism as the Management Server password (see "Credential handling" above) — first
  run takes plaintext, the next start encrypts and blanks it. The POST is **refused outright when
  `Health.Endpoint` is not HTTPS** unless `Health.AllowInsecureEndpoint=true` explicitly opts in
  (default `false`; the refusal reason appears in the probe's printed JSON) — the same
  no-bearer-token-cleartext-by-default rule `AllowInsecureLayoutPoll` applies to the description
  poll. A POST failure (unreachable endpoint,
  timeout, TLS error, non-2xx response) only logs and appears as a field in the probe's own printed
  JSON verdict — it never changes the probe's exit code, and never affects the running wall at all
  (the wall never makes this call in the first place).
- **Deliberately excluded from this feature:** no vendor/default endpoint (blank means nothing is
  ever sent), no automatic restart for stale video (that would fight
  `GridLookout.Recovery.SessionLossDetector`'s own backoff-gated recovery — the probe only ever
  judges the UI thread's own responsiveness, never camera/recorder staleness), and no restart of a
  hung process at all unless the deployment explicitly opts in with `install-kiosk.ps1 -RestartHung`.

## Remote screenshot (`GridLookout.exe --screenshot`)

Unlike every other CLI mode in this document, `--screenshot` makes **no network connection at all**
— it is purely local-machine IPC between two processes of the same executable (the running wall,
and the short-lived `--screenshot` invocation), using two named, auto-reset `Global\`-namespaced
Windows events (`Global\GridLookout.Screenshot.Request`/`.Done`) with an ACL granting Authenticated
Users the rights to signal and wait on them — deliberately so a remote admin shell in a DIFFERENT
Windows session (e.g. an `ssh`/`psexec` session) can trigger a capture without needing to be the
same account the kiosk runs as. This is the one place in the product where a same-machine,
different-account actor can act on the running wall process at all — see
docs/admin-guide.md's "Remote screenshot" section for the full request/response protocol,
exit codes, and operator-facing caveats.

**What lands on disk, and how sensitive it is.** Each capture writes one `screen-<n>.png` per
**attached display** (every display Windows reports — `Screen.AllScreens` — NOT just the monitors
the wall occupies): a literal screenshot of each display's complete desktop pixels at capture time.
That means live camera imagery (still frames of every visible tile, including any on-screen
captions, which can carry recorder and camera names), and — on any display the wall deliberately
leaves showing the desktop, or if another window overlaps the wall — whatever OTHER applications
are visible there too.
**Treat the screenshots folder with the same sensitivity as recorded video** — same category of data
as what the cameras themselves show, just a single frame instead of a stream — plus whatever else
was on screen. Files are overwritten
in place on every capture (fixed filenames, no accumulation), live under the same writable state
directory as `camerawall.json`/`health.json` (see "Writable-state fallback" below — normally next to
the executable, or the `%ProgramData%\GridLookout` fallback), and inherit that directory's NTFS
permissions — no separate ACL is applied to the screenshots subfolder itself. Operators can delete
the `screenshots` subfolder freely at any time; nothing reads it back except the next
`--screenshot` invocation's own file listing, and it's recreated on demand.

**Privilege requirement.** Creating a `Global\`-namespaced Windows object requires the "Create global
objects" privilege (`SeCreateGlobalPrivilege`) — granted by default to Administrators/SYSTEM/service
accounts, not to a plain standard-user account. If the account running the wall lacks it, the wall
logs a Warning at startup and continues running normally (this is an optional diagnostic feature,
never a boot-blocking dependency) — `--screenshot` then reports exit code 2 ("GridLookout is not
running"), indistinguishable from the wall genuinely not running, until the account's privilege is
fixed.

**What this feature does NOT do:** no new listening port, no new outbound connection, no change to
`camerawall.json`, and no persistence beyond the fixed-filename PNGs themselves — a captured frame
is not logged and not transmitted anywhere by GridLookout. It carries no VMS **credentials**; it
CAN carry VMS-adjacent identity visually (tile captions showing recorder/camera names — see the
sensitivity note above), which is why the folder inherits the recorded-video handling rule rather
than a lesser one.

## Writable-state fallback: %ProgramData%\GridLookout

The credential and log locations described above and below are the **primary** locations — next
to the executable. That is not the only place either one can end up, and which one is actually in
use on a given machine is not cosmetic: it changes what survives an uninstall (see "Installation
footprint" below) and where an administrator needs to look.

**When it engages.** At startup the application probes whether it can actually write to its own
install directory — a real write-then-delete of a temp file, not a permission-bit read. If that
probe fails, the application uses `%ProgramData%\GridLookout` instead of failing to start. This is
not a rare edge case: it is the expected result of the documented kiosk setup (MSI installs to
`%ProgramFiles%\GridLookout`; the wall then runs as a standard/limited kiosk account, and Windows
denies that account write access to `%ProgramFiles%` by default). Treat it as the normal operating
mode for a properly locked-down kiosk, not an unusual failure path.

**What lives there when it engages:**
- A copy of `camerawall.json`, including the DPAPI-protected `PasswordProtected` credential blob,
  at `%ProgramData%\GridLookout\camerawall.json`. It is merged on top of the exe-directory template
  on every load, so a credential migrated there once continues to be found on every subsequent run.
- The log files, under `%ProgramData%\GridLookout\logs\` (same filename pattern as the primary
  location — see "What is displayed vs. what is logged" below).

**The DPAPI scope guarantee is unchanged either way** — the blob is still `CurrentUser`-scope,
decryptable only by the same Windows account on the same machine that encrypted it, regardless of
which of the two directories holds the file. What changes is **file lifetime, not encryption
strength**: the `%ProgramData%\GridLookout` copy is not something the MSI installed, so it is not
something the MSI's uninstall or upgrade removes — see "Installation footprint" below for what that
means in practice.

**Plaintext left behind in an unwritable install directory.** If a plaintext `"Password": "..."`
was written into the exe-directory `camerawall.json` before it turned out the running account
lacks write access there, the migration still completes — a working `PasswordProtected` blob is
still produced in the `%ProgramData%\GridLookout` copy — but the application cannot blank the
plaintext out of the exe-directory file it has no permission to write to. It logs a warning when
this happens rather than silently leaving it; it does not retry or attempt to escalate privilege.
An administrator who sees that warning (or who knows in advance the install directory is
read-only for the running account) should manually delete or blank the `Password` value in the
exe-directory `camerawall.json` — the application cannot do this for them in this situation.

**NTFS/ACL advice, updated:** if the kiosk machine is physically accessible to untrusted users,
restrict NTFS permissions on **both** the install directory (e.g. `%ProgramFiles%\GridLookout`)
**and** `%ProgramData%\GridLookout`. Which one actually holds the live config and logs on a given
machine depends on the running account's write access at startup, and can differ between two
otherwise-identically-deployed kiosks — restricting only one is not sufficient.

## What is displayed vs. what is logged

- **On-screen error cards are deliberately generic** (e.g. "No matching host") — they disclose
  no server names, recorder names, hostnames, or addresses, because kiosk screens are often in
  public view. (Verified against the two error-card call sites in `Program.cs`: the local
  hostname and candidate recorder list used for diagnosis go to the log only, never to
  `statusForm.ShowStatus`.)
- **Log files** (normally `logs\` next to the executable, with an automatic fallback to
  `%ProgramData%\GridLookout\logs\` when the install directory isn't writable — see
  "Writable-state fallback" above; there is no config option to relocate them beyond that
  automatic fallback) do contain VMS details: recorder names, hostnames, camera names — this is
  required for diagnostics. If the kiosk machine is physically accessible to untrusted users,
  restrict NTFS permissions on both possible log directories (see "Writable-state fallback"
  above) or relocate the install.
- **`health.json`** (see "Wall-health monitoring" above) sits between those two categories: not
  shown on screen, and mostly excluded from the VMS-detail category logs carry — with the one
  disclosure already made above: in multi-recorder mode its per-recorder breakdown DOES carry
  recorder display names (never camera names, URIs, or credentials). Same NTFS-restriction advice
  as the log/config locations if the machine is physically accessible to untrusted users.
- **`layout-state.json`** — same content class as `health.json` (see above) but one level more
  detailed: it persists the resolved `$layout{}` plan as camera GUIDs and reference labels (ordinal
  numbers / aliases / short guids) — no camera NAMES, no VMS URI, no credentials. Written locally
  only, next to `health.json`; nothing about it is ever transmitted off the box.

## Installation footprint

- MSI installs to `%ProgramFiles%\GridLookout` — no services, no drivers, no group policy
  changes. It does write one small HKLM install-marker key (`SOFTWARE\GridLookout`: install
  path + version, used for uninstall cleanup) and one HKCU key recording that the Start Menu
  shortcut was installed — neither configures any machine behavior.
- The optional kiosk script (`install-kiosk.ps1`) writes **HKCU only** (Run key or per-user
  Winlogon shell) for autolaunch, plus registers a per-user Task Scheduler watchdog task that
  runs as that same user. It touches no other accounts and configures no autologon.
- **`camerawall.json` is never an MSI-installed file, so uninstall and every MSI upgrade leave it
  alone, wherever it lives.** The MSI ships only `camerawall.template.json` — never
  `camerawall.json` itself; the live file (including whatever DPAPI-protected credential it holds)
  is created at first run instead (see the Administrator Guide's "MSI install" for exactly how and
  where), which makes it invisible to the installer's lifecycle by construction, both on an
  upgrade's "remove the old version before installing the new one" step and on a plain uninstall.
  This holds no matter which of the two possible locations (install directory, or the
  `%ProgramData%\GridLookout` fallback below) it's actually in. It also explains why the
  install-directory copy specifically survives a plain uninstall: `RemoveFolder` on `INSTALLDIR`
  (see "MSI installs to..." above) only removes that directory if it's empty at uninstall time, and
  a `camerawall.json` (or a `logs\` folder) sitting in it is exactly the kind of file that keeps it
  non-empty. Reconnecting after an upgrade or reinstall is **not** required in either location —
  re-enter the connection details and credential only if that machine's `camerawall.json` is
  deliberately deleted. Locations that reliably survive an uninstall: the `logs\` folder next to
  the executable, an install-directory `camerawall.json` (if the install directory stayed
  writable), and, on a fallback machine, the whole `%ProgramData%\GridLookout` tree — see
  "Writable-state fallback" above for everything that tree can contain.
- **`KioskLock`** (a `camerawall.json` setting, not an installer or network setting) is a UI-only
  lockdown — when enabled it disables `Esc`-exit, the double-click compact-mode toggle, and
  Alt+F4/the window-close control on every wall window and on the connecting/retry status cards.
  The "not configured" and "could not load its configuration" cards are deliberately excluded from
  the lock and stay closable — a locked, unconfigurable box would have no recovery path at all. It
  changes nothing else about this footprint or about the "Network connections"/"Credential
  handling" sections above: no new network target, no new credential, no additional registry or
  file-system write. See the Administrator Guide's "Locking down the kiosk" for exactly what it
  does and its recovery path.
- Third-party license texts, plus the Milestone MIP SDK redistribution-authorization statement,
  ship as `NOTICES.md` at the install root (`%ProgramFiles%\GridLookout\NOTICES.md`); in-repo:
  `docs/gridlookout-NOTICES.md`.
