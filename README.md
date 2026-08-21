# GridLookout

*GridLookout for Milestone XProtect*

Formerly published as MilestoneCameraWall.

A standalone camera-wall / video-wall application for Milestone XProtect, built on the MIP SDK.
Run it directly on a recording-server host (or any Windows box): it logs into the Management
Server, works out which recording server it is running on, and displays that recorder's cameras
fullscreen as a live video grid — no Smart Client required.

Designed for unattended NOC/lobby/kiosk screens: autolaunch, watchdog, self-healing session
recovery, encrypted credentials, kiosk lockdown, multi-monitor.

**Download:** the latest MSI (with published SHA-256) is under
[Releases](https://github.com/IT42-d-o-o/GridLookout/releases). The MSI is currently
**unsigned** (no Authenticode publisher) — verify the published SHA-256 before installing;
Windows SmartScreen will show an "unknown publisher" prompt.

## Features

- **Self-locating** — matches the local hostname against the recording servers registered in the
  Management Server (`--recorder <name>` overrides).
- **Auto layout** — 1..12+ cameras arrange themselves into a balanced grid automatically.
- **Page rotation** — when a recorder has more cameras than fit comfortably, the wall can cycle
  through pages instead of cramming them into one grid. Only the current page's live streams
  exist, so memory scales with page size, not total camera count. `$layout{...|...}` matrices
  page too, on their own `|`-separated pages.
- **Operator-controlled layout matrix** — write a `$layout{...}` token into the recording server's
  *Description* field in Management Client and the wall re-arranges live (no restart; applies
  within one or two `ConfigRefreshSeconds` intervals — the description read runs in the
  background between refresh ticks). A
  cell can also rotate through several cameras in place — `A(3,4,5)` cycles that tile 3 → 4 → 5 → 3…
  every `TileRotateSeconds` — mixed freely with fixed cells.
- **Multi-monitor** — every attached monitor can carry its own layout (`$layout2{...}` targets
  monitor 2; with no tokens present, the `Monitors` config section assigns camera ranges per
  monitor). Token mode is exclusive by design: once any valid `$layoutN{}` token exists, only
  tokened monitors get wall windows — unnamed monitors deliberately show the desktop, freeing
  them for other applications.
- **Header & captions** — optional header strip (recorder name + clock) and per-tile captions
  showing `<ordinal>: <camera name>` so the operator always knows which number maps to which camera.
  A rotating tile additionally carries a small `⟳ <index>/<total>` watermark badge in its top-right
  corner (e.g. `⟳ 2/3`) — always visible, even with `ShowHeader: false` — disclosing both that it
  cycles and its current position in that tile's rotation set.
- **Tile borders** — configurable width/color separator between tiles.
- **Tile scale mode** — `TileScaleMode`: `Fit` (aspect-preserving letterbox, default), `Fill`
  (aspect-preserving cover, crops overflow, centered), or `Stretch` (ignores aspect ratio).
- **Stale-feed overlay** — a tile whose stream silently freezes gets a red "STALLED — last frame
  HH:mm:ss" stamp after `StaleSeconds` without a frame; a frozen frame that looks live is the worst
  NOC failure mode.
- **Display sleep prevention** — holds the display awake via `SetThreadExecutionState`
  (`KeepDisplayAwake`, on by default) so the kiosk screen never blanks regardless of power plan.
- **Window bounds override** — `WindowBounds { X, Y, Width, Height }` pins the wall to an exact
  region instead of fullscreen (video-wall segments, partial-screen kiosks).
- **One config-file credential** — `Basic` (XProtect basic user) or `Windows` (AD/local mirror
  account), both explicit; no ambient/local Windows account involvement of any kind.
- **Encrypted credentials** — a plaintext `Password` in the config file is auto-migrated to a
  DPAPI-protected value on first run.
- **Per-tile self-heal** — a stalled or never-framed tile reconnects on its own with exponential
  backoff (`TileRecoverSeconds`), independent of whole-wall recovery; a never-framed tile shows a
  red "NO SIGNAL" overlay while retries run.
- **Wall-health monitoring (optional)** — `GridLookout.exe --health-probe` reports process
  liveness and per-tile freshness for external watchdogs (exit codes healthy/degraded/hung/absent),
  from an atomically-written `health.json` with per-recorder aggregates; optional outbound HTTPS
  report to a customer-configured endpoint. Off by default, no inbound listener either way.
- **Stable camera references** — `$layout` cells accept `A@alias` / `A@{guid}` beside legacy
  ordinals (`CameraBindings` config + `--export-camera-bindings` generator), so renames never shift
  which camera lands in which tile; a missing/disabled camera shows an UNAVAILABLE tile rather than
  silently substituting the next one, and a malformed token keeps the last-known-good layout.
- **Cell spans** — `A1:2x2` grows a tile across rows/columns (`-` marks covered positions):
  hero layouts, 1-big-plus-N-small, golden-ratio walls. Spanned tiles request
  proportionally larger frames.
- **Multi-recorder walls** — `RecordingServers[]` mixes cameras from several recording servers
  under one Management Server (one login/session); captions become `Recorder / Camera`; health
  reports per-recorder tile aggregates.
- **Layout-carrier recorder** — `LayoutRecorder` names which ONE selected recorder's Description
  carries the wall's `$layout{}` tokens in multi-recorder mode, so the layout is managed from
  Management Client with no file edits. Authority is pinned: a missing carrier never hands layout
  control to another recorder — the wall keeps its last-known-good layout (across restarts and
  session recovery too, via `layout-state.json`) and flags `LayoutCarrierPinned` in health until
  the carrier returns, at which point health clears automatically.
- **Remote screenshot** — `GridLookout.exe --screenshot`, run alongside the wall (over ssh/psexec,
  from another session), asks the running wall to save `screen-N.png` of every monitor to its
  state directory — a remote "what is actually on the screens" check that never touches the kiosk
  autologon session. No inbound listener; plain named-event IPC, local machine only.
- **Session-loss recovery** — a live wall that loses its VMS (recording-service restart,
  management-server reboot) detects it (every-tile-stale or repeated refresh failures), tears
  down, re-logs-in, and rebuilds — with exponential backoff (60s → 15 min cap) so a long outage
  never hammers the IDP. Works on rotating walls too.
- **Crash-relaunch guard** — an unhandled crash relaunches a fresh process (single-instance
  safe); 5 relaunches within 10 minutes and it stops and says so, instead of looping forever.
- **First-run seeding & locked-down installs** — the MSI ships only `camerawall.template.json`;
  first run seeds a real `camerawall.json` (to `%ProgramData%\GridLookout` when the install dir
  isn't writable — the normal case for a standard-user kiosk account), so upgrades never touch a
  configured wall.
- **Kiosk mode** — `scripts/install-kiosk.ps1` wires autolaunch (Run key or shell replacement)
  and a watchdog task for the current user (`-NoWatchdog` opts out). Accounts and autologon are
  deliberately out of scope (use your own tooling, e.g. Sysinternals Autologon).
- **KioskLock** — one config flag disables Esc, Alt+F4, window-close, and the compact toggle on
  wall windows and connection-retry cards for public screens (Ctrl+Alt+Del/Task Manager remains
  the operator stop path, by design).
- **Leveled file logging** — DEBUG/INFO/WARN/ERROR with a configurable floor.
- **Double-click** toggles fullscreen kiosk mode ↔ a normal window with a real title bar
  (minimize/maximize/close, taskbar, draggable/resizable). **Esc** exits. Both are disabled when
  `KioskLock` is on.

## The `$layout{}` grammar

Written into the recording server's **Description** field:

```
$layout{A1,A2;B3}          rows A (cameras 1,2) and B (camera 3)
$layout2{A4,A5}            same, but on monitor 2
$layout{A1,A2|A3,A4}       two pages: page 1 shows cameras 1,2; page 2 shows cameras 3,4
```

- A letter picks the **row** (A = top), the number is the **camera ordinal**.
- `,` and `;` both separate entries; row membership comes from the letter, so
  `$layout{A1,A2;B3}` and `$layout{A1;A2;B3}` are identical.
- Duplicates are allowed — the same camera can appear in several tiles.
- A cell written `A(3,4,5)` instead of a plain number rotates that tile through cameras 3, 4, 5,
  3, … in place, on a shared timer paced by `TileRotateSeconds`.
- Ordinals are 1-based positions in the recorder's sorted camera list. Each tile's caption shows
  its ordinal (`3: Parking East`), so the wall itself is the legend for writing matrices.
- No `$layout{}` token → automatic balanced grid of all cameras.
- `|` splits the token into rotating **pages** — see "Page rotation" below.
- **One token per monitor**: if two valid tokens target the same monitor (e.g. two bare
  `$layout{}` tokens), the first wins and the rest are ignored with a logged warning — use
  `$layout2{}`, `$layout3{}`, … to address additional monitors.

## Configuration (`camerawall.json`)

| Key | Default | Purpose |
|---|---|---|
| `ManagementServerUri` | — | e.g. `http://vms-mgmt.example.local` |
| `AuthMode` | `Basic` | `Basic` \| `Windows` — both explicit credentials |
| `Username` / `Domain` / `Password` | — | Required by both modes; `Password` is DPAPI-encrypted on first run |
| `AllowInsecureBasic` | `false` | Permit Basic auth over http |
| `RecorderNameOverride` | — | Skip hostname matching |
| `ReconnectSeconds` | `15` | Login retry interval |
| `ConfigRefreshSeconds` | `60` | How often the description/`$layout{}` is re-read |
| `ShowHeader` | `false` | Header strip with recorder name + clock |
| `TileBorderWidth` | `1` | Border between tiles, px (0 = none) |
| `TileBorderColor` | `#404040` | Border color, `#RRGGBB` |
| `StaleSeconds` | `10` | Seconds without a frame before the STALLED overlay (0 = off) |
| `FitFrameSizeToTile` | `true` | Request each tile's live frame at its actual on-screen size instead of a flat 1280x720 |
| `MaxFps` | `12` | Caps requested live frame rate per tile (0 = uncapped/server default) |
| `PageSeconds` | `0` | Seconds per page before rotating (0 = off) — auto-layout pages AND multi-page `$layout{...|...}` matrices alike; a single-page matrix has nothing to rotate — see "Page rotation" |
| `PageSize` | `9` | Cameras per rotating auto-layout page — only the current page's live streams exist, so RAM scales with this |
| `TileRotateSeconds` | `10` | Seconds between camera flips for a `$layout{}` cell written `A(3,4,5)` (floored at 5s) |
| `TileScaleMode` | `Fit` | `Fit` (aspect-preserving letterbox) \| `Fill` (aspect-preserving cover, crops overflow) \| `Stretch` (ignores aspect ratio). Case-insensitive; an unrecognized value falls back to `Fit` with a logged warning |
| `KeepDisplayAwake` | `true` | Prevent display/system sleep while the wall runs |
| `KioskLock` | `false` | Disable Esc / Alt+F4 / window-close / compact toggle on wall windows and retry cards (config-error cards stay closable) |
| `WindowBounds` | `null` | `{ "X", "Y", "Width", "Height" }` — exact window region instead of fullscreen |
| `LogRetentionDays` | `30` | Days of daily log files kept (pruned at startup; `0` = keep forever) |
| `LogLevel` | `Info` | `Debug` \| `Info` \| `Warning` \| `Error` |
| `Monitors` | — | Per-monitor camera assignment |
| `TileRecoverSeconds` | `30` | Per-tile self-heal reconnect base interval (0 = off, floored at 10; doubles per retry up to 8×) |
| `CameraBindings` | `{}` | Alias → camera GUID map for stable `A@alias` layout references (`--export-camera-bindings` generates a skeleton) |
| `RecordingServers` | `[]` | Multi-recorder mode: entries selecting recorders by `Id` (preferred) or `HostName`; empty = single-recorder mode |
| `Layout` | `""` | Multi-recorder only: config-string layout that outranks the carrier's Description when non-blank |
| `LayoutRecorder` | `""` | Multi-recorder only: which ONE selected recorder's Description carries the wall's `$layout{}` tokens (display Name or Id; pinned authority — see admin guide) |
| `AllowInsecureLayoutPoll` | `false` | Permit the bearer-token description poll over plain `http://` (lab/dev only) |
| `Health` | off | Wall-health monitoring section: `Enabled`, `ControllerId`, `Endpoint` (+`AllowInsecureEndpoint`, default `false` — plain-http POST refused otherwise), `StaleAfterSeconds`, `TimeoutSeconds`, `BearerToken`(`Protected`) |
| `PasswordProtected` | — | DPAPI blob the app writes when it migrates a plaintext `Password`; never hand-edited |

A `camerawall.local.json` next to the config overlays it (useful for keeping credentials out of
the main file).

## Page rotation

When a recorder has more cameras than fit comfortably on screen, set `PageSeconds`/`PageSize` and
the wall cycles through pages instead of one dense grid — only the current page's live streams
exist at any moment, so memory scales with page size, not total camera count (e.g. 20 cameras with
`PageSize: 10` roughly halves live-stream memory versus showing all 20 at once). Tile captions
always show the camera's true position in the full recorder list, never its position within the
page. A `$layout{...}` matrix always overrides auto-layout paging — but a matrix can page on its
own terms via `|` inside the token (`$layout{A1,A2|A3,A4}`), rotating on the same `PageSeconds`
interval (minimum effective interval 10s either way).

## Build

Targets **.NET Framework 4.8** (in-box on Windows 10 1903+/Windows 11/Server 2022+; Windows 10
1809 and Server 2019 need .NET Framework 4.8 installed once). Requires the Milestone MIP SDK NuGet packages (restored automatically).

```
dotnet build src/GridLookout/GridLookout.csproj -c Release
dotnet test tests/GridLookout.Tests
```

> Why net48? The MIP SDK's standalone login path binds `System.IdentityModel` 4.0.0.0, which has
> no .NET (Core) facade — a modern-.NET standalone app cannot complete the login handshake. See
> the comment in the `.csproj`.

## Kiosk install

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-kiosk.ps1 `
    -ExePath 'C:\Program Files\GridLookout\GridLookout.exe' [-Shell]
```

Run while logged on as the account that should show the wall (no elevation — it configures that
account's own HKCU). Sets up autolaunch (Run key, or shell replacement with `-Shell`) plus a
1-minute watchdog task that restarts the wall if it exits (`-NoWatchdog` skips it). No accounts
are created and no autologon is configured (use e.g. Sysinternals Autologon for that).
`-Uninstall` removes the wiring and needs no other parameters.

## Documentation

The MSI installs these guides to `%ProgramFiles%\GridLookout\docs\` as well, for offline kiosk boxes.

- [User guide](docs/user-guide.md) — operating the wall, layouts, rotation
- [Admin guide](docs/admin-guide.md) — deployment, configuration, troubleshooting
- [Security & network behavior](docs/security.md) — every connection, credential storage, what
  the app does and does not send (nothing of its own)
- [Compatibility matrix](docs/compatibility.md) — XProtect versions, editions, Windows requirements
- [GridLookout vs. a Smart Client kiosk](docs/positioning.md) - honest comparison, limitations, payback
- [Third-party notices](docs/gridlookout-NOTICES.md) — the MSI's third-party licenses and the
  Milestone MIP SDK redistribution authorization (installed as `NOTICES.md`)

## License

**PolyForm Noncommercial 1.0.0** — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
Licensed by **IT42 d.o.o.** Created by **Tomislav Balaz** ([@tbalaz](https://github.com/tbalaz)).

- ✅ Free for **personal use**, hobby projects, research, education, and noncommercial
  organizations.
- ✅ **30-day commercial evaluation** under the
  [PolyForm Free Trial 1.0.0](LICENSE-TRIAL) — then buy or stop.
- ❌ **Not licensed for other commercial or business use** — companies wanting to run this in
  production need a commercial license from IT42 d.o.o.
- Redistribution must keep the license terms and the Required Notice (attribution) intact.

### Commercial licensing

Business/production use is licensed **per wall-controller PC** (unlimited monitors and
cameras per PC), perpetual or yearly: **€500** perpetual / **€180 per year** per controller,
**5-Pack €2,100** (or €750/yr), **10-Pack €3,600** (or €1,200/yr); published marginal
per-controller tiers up to 100 controllers, negotiated master agreement beyond. 30-day commercial
evaluation permitted. Full pricing and terms:
[COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md).

**Contact: [info@it42.hr](mailto:info@it42.hr)**
