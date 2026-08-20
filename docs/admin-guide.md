# GridLookout — Administrator Guide

Installation, configuration, and troubleshooting reference for GridLookout — a standalone
MIP SDK application (`src/GridLookout/`) that runs on (or near) a recording-server host,
logs into the Management Server, works out which recorder it's running for, and shows that
recorder's cameras fullscreen as a live grid. For what an operator sees on screen day to day, see
the Operator Guide.

## Requirements

- **Runtime**: `.NET Framework 4.8`, in-box on Windows 10 1903+, Windows 11, and Windows Server
  2022+. Windows 10 1809 and Windows Server 2019 need .NET Framework 4.8 installed first (it isn't
  in-box on those builds). See the Compatibility Matrix's "Wall machine" table for the full OS
  breakdown. The app itself is a plain net48 WinForms exe — nothing else needs installing once the
  runtime is present.
- **Build-time only**: the Milestone MIP SDK NuGet packages (`MilestoneSystems.VideoOS.Platform`
  and `MilestoneSystems.VideoOS.Platform.SDK`, both pinned to `25.2.3` in
  `src/GridLookout/GridLookout.csproj`) are restored automatically by `dotnet
  build`/`dotnet publish` from nuget.org — they are not a separate manual install step, and they
  are not needed at runtime beyond what gets copied into the output/publish folder.
- **Platform**: built `x64` only (`<PlatformTarget>x64</PlatformTarget>`).
- **Network reachability from the wall host**:
  - The Management Server (`ManagementServerUri`) — login and configuration reads.
  - The **recording server's own webserver, port 7563** — this is where live video for that
    recorder's cameras is actually streamed from; the app talks to it directly once it has logged
    in and located the recorder. If the wall runs on a different box than the recorder itself, this
    port must be reachable across whatever network sits between them.
  - When the wall's target recorder is a WORKGROUP box with `AuthMode=Windows`,
    NTLM/Negotiate auth against it works the same way the Recording Server itself reaches the
    Management Server (mirrored local accounts) — no domain membership is required on the wall host
    for that path.

## Install

### MSI install

1. Install `GridLookout-<version>.msi` — installs to `%ProgramFiles%\GridLookout`. The MSI ships
   `camerawall.template.json` only; it never ships, harvests, or owns a live `camerawall.json` (see
   "camerawall.json is seeded, not shipped" below) — nothing is configured yet at this point.
2. Run `GridLookout.exe` once (any account). On first run, if `camerawall.json` is missing and
   `camerawall.template.json` is present next to the exe, the app seeds a real `camerawall.json` by
   copying the template's text verbatim (comments intact) — to the exe directory if it's writable,
   otherwise to `%ProgramData%\GridLookout` (see "Credential handling" below). With
   `ManagementServerUri` still blank, this run just shows the "not configured" card — that's
   expected; the point of this run is to create the file to edit. Both the startup log line and the
   on-screen card name the seeded file's real path, so you don't have to guess which of the two
   possible locations it landed in.
3. Edit the seeded `camerawall.json` at the path named in step 2: at minimum, set
   `ManagementServerUri`. Everything else has a working default (see the config reference below).
   Manually copying `camerawall.template.json` over `camerawall.json` still works exactly as
   before — seeding only ever fires when `camerawall.json` is absent, so it's harmless, it's just no
   longer the only way to get a starting file.
4. Run `GridLookout.exe` again as whichever account will run it in production, to confirm it logs in
   and locates a recorder correctly, before wiring up the kiosk pieces (below).
5. Once confirmed, proceed to kiosk deployment (below) if this box is meant to run unattended.

**camerawall.json is seeded, not shipped.** The MSI never installs, upgrades, or removes a live
`camerawall.json` — only the never-edited-in-place `camerawall.template.json`. This means an
upgrade or a repair install never touches a configured wall's `ManagementServerUri` or DPAPI
credential; see the Security & Network Behavior guide's "Installation footprint" for what that
means for uninstall too.

### Building from source

```powershell
# Build (Release, from the repo root)
dotnet build src/GridLookout/GridLookout.csproj -c Release

# Run the tests (pure-logic unit tests, no MIP SDK / live server needed)
dotnet test tests/GridLookout.Tests
```

Building from source is a different path from the MSI above: the project's own
`<None Update="camerawall.json">` item copies `camerawall.json` itself (not a template) straight
into the build output, marked `CopyToOutputDirectory=PreserveNewest`. That preserves an
already-configured file across a rebuild into the *same* output folder, but a publish into a
fresh/clean output directory copies the blank file again — verify a configured `camerawall.json`
actually survives your specific redeploy mechanism before relying on it, or keep the real config in
the gitignored `camerawall.local.json` overlay instead. This is a build/publish-time concern,
separate from the runtime `%ProgramData%\GridLookout` fallback covered under "Credential handling"
below and in the Security & Network Behavior guide — if that runtime fallback is active on a given
kiosk, a previously-migrated config there is merged back on top of even a freshly-redeployed blank
file, so a fresh publish does not necessarily wipe the live config on such a machine.

## Configuration reference

`camerawall.json` — normally next to the exe, with a runtime fallback to
`%ProgramData%\GridLookout\camerawall.json` when the install directory isn't writable by the
running account (see "Credential handling" below and the Security & Network Behavior guide's
"Writable-state fallback" for the full disclosure) — schema: `src/GridLookout/Config/WallConfig.cs`.
On an MSI install this file doesn't exist until first run — see "MSI install" above for exactly
how and where it's created. All keys are optional except `ManagementServerUri`; anything omitted
takes its default.

| Key | Type / values | Default | Purpose |
|---|---|---|---|
| `ManagementServerUri` | string, e.g. `http://vms-mgmt.example.local` | `""` (required) | Management Server address. Empty → app shows a fullscreen "not configured" card and does not attempt to run. |
| `AuthMode` | `Windows` \| `Basic` | `Basic` | See "Authentication modes" below. |
| `Username` | string | `""` | Required by both modes — the app's one Management Server credential. |
| `Domain` | string | `""` | Used by `Windows` mode only (`DOMAIN\user` or `MACHINE\localuser`). |
| `Password` | string | `""` | **Dev-only plaintext.** Auto-migrated to `PasswordProtected` and blanked on the very next load — never left populated once the app has run once under the right account. |
| `PasswordProtected` | string, base64 | `""` | DPAPI `CurrentUser`-scope encrypted blob. Only the Windows account that wrote it can decrypt it. |
| `AllowInsecureBasic` | bool | `false` | Must be explicitly `true` to permit `AuthMode=Basic` over plain `http://`. Refused (login throws, retried) otherwise — a production install must not leak Basic credentials over an unencrypted channel by accident. |
| `RecorderNameOverride` | string | `""` | Skip hostname-based self-location; match this recorder by exact name OR by its registered host address (hostname/FQDN/IP). |
| `RecordingServers` | array of `{ "Id": "<recorder guid>" }` or `{ "HostName": "<host>" }` | `[]` | Multi-recorder walls: show cameras from several recording servers under this ONE Management Server (one login, one session) instead of just the recorder this host self-locates as. Empty/absent = single-recorder mode, unchanged. Each entry selects ONE recorder by exactly one of `Id` (authoritative) or `HostName` (exact match against the registered host, a migration fallback) — both or neither makes the entry ignored with a warning; a selector matching no live recorder is also a warning, never fatal — the wall continues with the rest. Never implicitly expands to "every recorder". Selection precedence: `--recorder` CLI arg (forces single-recorder mode) > non-empty `RecordingServers` (multi mode) > `RecorderNameOverride` > hostname self-location. Not hot-reloaded — editing this list requires a restart. |
| `Layout` | string | `""` | Multi-recorder mode only: the `$layout{}`/`$layoutN{}` matrix for a multi-recorder wall — same grammar as the recorder-Description tokens above, read from this config string instead. A non-blank value here always wins outright (highest precedence). Blank hands layout selection to the `LayoutRecorder` below instead. See "Multi-recorder walls" below. |
| `LayoutRecorder` | string (recorder name or Id — never a HostName) | `""` | Multi-recorder mode only: which ONE selected recorder's own Description supplies `$layout{}` tokens when `Layout` above is blank — lets an operator manage a multi-recorder wall's layout from Management Client instead of editing this file. Matched against the CURRENTLY selected recorders only. **Blank** (default) = auto-carrier, floats to the FIRST `RecordingServers` entry. **Non-blank** = PINNED: a value that doesn't match exactly one currently selected recorder (unmatched, or ambiguous — two selected recorders share that name) does NOT fall back to another recorder's Description — the wall keeps its last-known-good layout, logs a warning (on change), and flags `health.json`'s `LayoutCarrierPinned` until it resolves again. See "Multi-recorder walls" below. |
| `AllowInsecureLayoutPoll` | bool | `false` | Must be explicitly `true` to permit the layout-carrier description poll (above) over plain `http://` `ManagementServerUri` — that poll sends this app's session token as an `Authorization: Bearer` header. Refused (poll skipped, warning logged on change) otherwise. Mirrors `AllowInsecureBasic`; does not affect login itself. |
| `Health.AllowInsecureEndpoint` | bool | `false` | Must be explicitly `true` to permit the `--health-probe` POST to a plain `http://` `Health.Endpoint` — that POST can carry the configured bearer token plus the wall's health content, cleartext on the wire. Refused (POST skipped, reason in the probe's printed JSON) otherwise. The health-endpoint mirror of `AllowInsecureLayoutPoll`. |
| `ReconnectSeconds` | int | `15` | Delay between login/locate retry attempts while the wall can't connect. |
| `ConfigRefreshSeconds` | int | `60` | How often the recorder's Description (`$layout{}`) and camera list are re-read; enforced minimum of 5s at runtime regardless of a lower configured value. Grid rebuilds without a restart when a change is detected. |
| `LogLevel` | `Debug` \| `Info` \| `Warning` \| `Error` | `Info` | Minimum severity written to the log file. |
| `LogRetentionDays` | int | `30` | Max age in days of `gridlookout-*.log` files (daily files and per-user variants alike); anything older is deleted once at startup, right after the config is loaded. `0` disables pruning — keep forever. |
| `ShowHeader` | bool | `false` | `true`: adds a per-window header strip (recorder name + ticking clock) and a per-tile caption bar (`"<ordinal>: <camera name>"`). `false`: original edge-to-edge video, no strips at all. |
| `TileBorderWidth` | int (px) | `1` | Margin/border between tiles and around the grid edge. `0` = tiles sit edge-to-edge. |
| `TileBorderColor` | string `"#RRGGBB"` | `"#404040"` | Border color. An unparseable value silently falls back to black rather than throwing. |
| `StaleSeconds` | int | `10` | Seconds with no new frame before a tile (that has received at least one frame) shows the red STALLED overlay. `0` disables the feature entirely (no overlay, no sweep timer allocated). |
| `FitFrameSizeToTile` | bool | `true` | Requests each tile's live-stream frame at its actual on-screen pixel size (computed once per grid build from the form's current bounds) instead of a flat `1280x720` for every tile, clamped to 320x180–1280x720 and rounded to even numbers. `false`: every tile always requests the flat `1280x720` (pre-optimization behavior) — set this if a driver/server misbehaves with non-standard requested sizes. |
| `MaxFps` | int | `12` | Caps the requested live-stream frame rate per tile via the SDK's own `FPS` property on the live source. `0` leaves it unset, so the server's native/default rate applies. |
| `PageSeconds` | int | `0` | Seconds each auto-layout page is shown before rotating to the next. `0` (default) disables auto-layout rotation — a single page shows every camera. A nonzero value below 10 is treated as 10. A `$layout{}` matrix pages independently of this setting, via `\|` inside the token — see "Page rotation" below. |
| `PageSize` | int | `9` | Cameras per rotating auto-layout page. Only the current page's live streams exist at a time (memory scales with this, not total camera count). No effect when `PageSeconds` is `0`, when the recorder has fewer cameras than this, or for `$layout{}` matrix pages (those are sized by whatever the operator wrote in each page's segment). A value below 1 is treated as 1. |
| `TileRotateSeconds` | int | `10` | Seconds between camera flips for a `$layout{}` cell written in the rotation form, e.g. `A(3,4,5)`, instead of a fixed ordinal. A value below 5 is treated as 5. **Rotating a tile reconnects that camera's live stream every cycle** — the 5s floor exists for the same reason `PageSeconds`/`PageSize` are floored, to prevent reconnect thrash. Unlike `PageSeconds` for auto-layout, `0`/unset does not disable rotation — writing more than one ordinal in a cell's parens is itself the request to rotate; an unset value just falls back to the 5s floor. No effect on cells with a single ordinal. A rotating tile carries an always-visible `⟳ <index>/<total>` watermark badge in its top-right corner — its 1-based position in that cell's written ordinal list, over the list's size — independent of `ShowHeader`, so a captionless public wall still discloses which tiles cycle; drawn in `Segoe UI Symbol` rather than the caption font used elsewhere (`Segoe UI` has no glyph for `⟳`/U+27F3 and renders it as a tofu box). |
| `TileScaleMode` | `Fit` \| `Fill` \| `Stretch` | `Fit` | How each tile scales its live frame to its cell. `Fit`: aspect-preserving letterbox (PictureBox SizeMode Zoom) — no crop, may show bars. `Fill`: aspect-preserving cover — scales until the tile is fully covered, crops the overflow, centered; no native `PictureBoxSizeMode` equivalent, so this is custom-painted (`UI/ScalableTilePictureBox.cs`). `Stretch`: ignores aspect ratio, fills the cell exactly (PictureBox SizeMode StretchImage) — can visibly distort the picture. Case-insensitive; an unrecognized value falls back to `Fit` and logs one `WARNING` line rather than crashing config load over a typo. |
| `KeepDisplayAwake` | bool | `true` | Asserts `ES_SYSTEM_REQUIRED`/`ES_DISPLAY_REQUIRED` for as long as the app runs, so Windows never blanks/sleeps the display regardless of power plan. `false`: the app never touches thread execution state. |
| `WindowBounds` | `{ "X", "Y", "Width", "Height" }` or `null` | `null` | Pins the **first/default monitor's** window to exact desktop coordinates/size instead of fullscreen (still borderless, still topmost). Requires `Width` and `Height` both `> 0`; a `null` or zero-sized value falls back to normal fullscreen. Every *other* configured monitor always stays fullscreen regardless of this setting. |
| `Monitors` | array of `{ "Monitor": n, "Cameras": "..." }` | `[{ "Monitor": 1, "Cameras": "all" }]` | Per-monitor camera assignment, used **only when the recorder's Description carries no valid `$layoutN{}` tokens**. When any valid token exists, `Monitors` config is ignored entirely (see "Token mode is exclusive" below). `Cameras` accepts `"all"`, a range (`"1-4"`), or a comma list (`"5,6,7"`), all in the recorder's sorted-ordinal order. |
| `KioskLock` | bool | `false` | `true` disables `Esc`-exit, the double-click compact/fullscreen toggle, and Alt+F4/window-close on every wall window **and** on the connecting/retry status cards, so a passerby cannot exit or de-kiosk the wall from the keyboard or mouse. The "not configured" and "could not load its configuration" cards are deliberately exempt and stay closable. There is no on-screen recovery hotkey (this product has no F-key/minimize hotkey at all). With this on, the operator stop path is: disable the watchdog scheduled task (if one is registered), then `Ctrl+Alt+Del` → Task Manager to end the process, or an MSI uninstall. One `INFO` line is logged at startup when active. See "Locking down the kiosk" below. |
| `TileRecoverSeconds` | int | `30` | Per-tile self-heal: seconds a stalled (has-framed, no new frame for longer than `StaleSeconds`) or never-framed tile waits before reconnecting on its own, independent of every other tile. `0` disables per-tile recovery entirely — the wall then only recovers via whole-session recovery (when every tile is stale). A nonzero value below 10 is floored to 10. Each subsequent retry for the same bad spell doubles the wait up to 8x this value, resetting on the next good frame. If `StaleSeconds` is `0` (STALLED overlay disabled), a framed tile still uses this value as its own stale-onset threshold. Recovery attempts and successes are logged at INFO with the tile's label. |
| `Health` | object (see below) | `{ "Enabled": false, ... }` | Wall-health monitoring — opt-in liveness reporting. Sub-keys: `Enabled` (bool, default `false`), `ControllerId` (string, default machine name), `Endpoint` (string, default `""`), `StaleAfterSeconds` (int, default `30`), `TimeoutSeconds` (int, default `5`), `BearerToken` (plaintext; auto-migrated to `BearerTokenProtected` and blanked, like `Password`), `BearerTokenProtected` (DPAPI blob, base64). See "Wall-health monitoring (optional)" below. |
| `CameraBindings` | object, key-value pairs (string → string) | `{}` | Referentially stable `$layout{}` aliases: maps your own stable alias names (lowercase letters/digits/hyphens only, e.g. `"front-gate"`) to camera GUID strings. Enables `$layout{}` tokens to reference cameras by stable alias (e.g. `A@front-gate`) or GUID (e.g. `A@{8fa2c1e4...}`) instead of unstable ordinals. Run `GridLookout.exe --export-camera-bindings` to generate a ready-to-paste skeleton with suggested aliases for all cameras on the recorder. An alias with a bad format or an unparseable guid is dropped at startup with a warning in the log; every other entry still loads. First-write-wins on a duplicate alias (case-insensitive). |

### Token mode is exclusive

The `$layout{}`/`$layoutN{}` tokens in the recording server's Description field control which monitors show a wall window and how cameras are arranged **only when at least one valid token exists**. When tokens are present, the `Monitors` config array is ignored entirely — every monitor that is **not** named in a token deliberately shows the Windows desktop instead of a wall window, freeing it for other applications.

**Example:** A recorder with Description `$layout{A1,A2} $layout2{A3,A4}` will show a wall on monitors 1 and 2 (as specified), but monitor 3 and any others show the desktop (since no `$layout3{}` token addresses them). Adding a new `$layout3{A5}` token later will place a wall on monitor 3 without requiring a restart — the wall re-reads the Description every `ConfigRefreshSeconds` (default 60) and rebuilds as needed.

**Troubleshooting:** If a monitor unexpectedly shows the desktop, verify:
1. The recorder has a valid `$layoutN{}` token for that monitor (where N is the monitor number, or omitted for monitor 1)
2. The token syntax is correct (must be `letter+digits`, e.g. `A1`, `B5`) — see "How a malformed token behaves" immediately below: a token that has PREVIOUSLY resolved for this monitor never goes desktop over a later typo, it keeps showing its last-known-good layout instead (logged as a `WARNING`, never silent). Desktop only happens when this monitor has never had a valid token at all.
3. Check the log file for `WARNING` lines mentioning `"Duplicate $layout token"` or parse failures

### How a malformed token behaves

One rule, applied per monitor, the same everywhere in this codebase (Management Client's Description field for single-recorder mode, or the config `Layout` string for multi-recorder mode):

- **A monitor whose token has PREVIOUSLY resolved successfully** (this run, since its last pin) keeps showing that last-known-good layout when its CURRENT token becomes malformed — a `WARNING` is logged naming the monitor, nothing changes on screen. This is F3 rule 6c: "stale but valid beats desktop."
- **A monitor whose token has NEVER resolved** (broken from the very first time GridLookout evaluated it, with nothing pinned to fall back to) has nothing to carry forward — it shows the Windows desktop, exactly like a monitor with no token at all, until the token is fixed.
- **The whole wall falls back to the `Monitors[]` automatic grid** only when NO monitor anywhere ends up with a resolved layout — a true cold start with no `$layout` token in play at all, or every token broken since the very first resolve. The moment even ONE monitor resolves anything (fresh or carried forward), token mode is exclusive for the wall — see "Token mode is exclusive" above.
- **A monitor simply never named by any token** (no `$layoutN{}` for it at all, while at least one sibling monitor IS in token mode) always shows the desktop — this is the ordinary, intentional case "Token mode is exclusive" above describes, not a malformed-token fallback.

## Referentially stable $layout{} layouts (CameraBindings)

Previously, a `$layout{}` matrix cell (e.g. `A3`) was a raw *ordinal* — the camera's position in the
recorder's alphabetically-sorted enabled-camera list. Renaming, reordering, enabling, or disabling
any camera on the recorder could silently shift what ordinal N pointed at, putting the wrong live
feed in a tile with no warning.

A cell can now be written three ways:

- `A3` — legacy ordinal (still supported, still unstable across renames/reorders — kept only for
  backward compatibility).
- `A@front-gate` — a stable **alias**, looked up in `camerawall.json`'s new `CameraBindings` section.
- `A@{8fa2c1e4-1b3d-4a5e-9c6f-2d7e8b9a0c1d}` — the camera's own GUID, written literally.

Rotation cells can mix all three: `A(3,@yard-east,@{guid})`.

### Generating CameraBindings

Run `GridLookout.exe --export-camera-bindings` (from an elevated prompt, next to the installed
exe). It logs in with the configured credentials, prints a ready-to-paste `CameraBindings` block to
the console, and also writes it to `camera-bindings.generated.json` next to the exe. Copy the
entries you want into `camerawall.json`'s `CameraBindings` section, then edit your recorder's
description to reference the aliases (`A@front-gate` instead of `A3`).

### How a reference is resolved

- An ordinal is resolved against the CURRENT camera order once, then **pinned** to that camera's id
  — a later rename/reorder never moves it. Editing the `$layout{}` token text (any change) is what
  tells GridLookout you intend a fresh resolve.
- An alias/guid reference to a camera that's currently missing or disabled shows a dark
  "UNAVAILABLE — ..." tile instead of the video — never a silently wrong camera.
- If a `$layout{}` token becomes malformed (a typo), GridLookout keeps showing the LAST GOOD layout
  for that monitor (logged as a Warning) rather than blanking it or falling back to auto-layout.

### Cell spans and the `-` placeholder

Any cell form above can carry a `:RxC` suffix (rows x columns) to make it occupy more than one grid
position: `A1:2x2`, `B@front-gate:1x2`, `A(3,@yard-east):2x1`. R and C must each be a whole number
`>= 1`; a `0` or non-numeric span (`A1:0x2`, `A1:AxB`) is a malformed token — same "keep the last
good layout, log a warning" handling as any other typo.

A spanned cell's coverage of the rows/columns BELOW or TO THE RIGHT of where it's written has to be
marked explicitly with a `-` placeholder — GridLookout never infers it. Every position a span
reaches needs exactly one of: the span's own origin cell, or a `-`. Concretely, a cell with `ColSpan`
greater than 1 needs `ColSpan`-many `-` tokens, side by side, in every ADDITIONAL row its `RowSpan`
reaches — its own row needs none, because the `:RxC` suffix already accounts for that row's width.

**The moment a page uses any span or `-`, it becomes a UNIFORM grid**, and two rules kick in that
don't apply to an ordinary (non-spanned) page:

- **Every row's total column coverage must be equal.** Add up each row's cells (a plain cell counts
  1, a spanned cell counts its `ColSpan`) plus one for every `-` — every row must reach the SAME
  total. A page where row A sums to 3 and row B sums to 2 is "ragged rows" and is rejected, even
  though an ordinary (non-uniform) page is completely free to have rows of different lengths.
  Rejected means rejected LOUDLY: the same "keep the last good layout, log a Warning naming the
  reason" handling as any other malformed token — never a silent reshape of the wall.
- **Rows still work exactly the way they do everywhere else in this grammar**: for an ordinary
  (lettered) cell, the row LETTER decides which row it's in — `,` and `;` are still fully
  interchangeable, exactly like `user-guide.md`'s "The grammar" section already
  documents (`$layout{A1,B2}` and `$layout{A1;B2}` are still two rows either way, span or no span).
  The only thing that changes is what a `-` placeholder does with that rule: it has no letter of its
  own, so it can never TRIGGER a new row by letter-change — it simply continues whichever row is
  already open — but an explicit `;` written right before one still starts a new row, exactly like
  it would before a lettered cell. That's why the left-tall-plus-stack example below still needs
  `;` before each `-,B3`/`-,C4` pair: those rows begin with a placeholder, which has nothing else to
  signal "new row" with.
- **One narrow exception, and it fails loudly:** if a row letter reappears after a LATER row already
  closed it out (e.g. writing `A1,B2,A3` — back to `A` after `B`), a uniform-grid page rejects the
  token with a "row letter reappears" diagnostic instead of guessing what you meant. An ordinary
  (non-spanned) page tolerates this — cells sharing a letter are grouped together no matter where
  they appear — but a uniform page can't safely reproduce that without risking exactly the kind of
  silent, unintended reshape this whole uniform-grid rule exists to prevent. In practice this only
  comes up if you write letters out of order; every example in this section writes them in order.

Every covered position must be accounted for by EXACTLY one span origin or one `-` — GridLookout
rejects, with a diagnostic naming the exact position (row letter, 1-based column):

- a `-` with nothing above/left of it claiming that position ("not covered by any span"),
- two spans (or a span and a fresh cell) both claiming the same position ("overlaps the span from
  row … col …"),
- a span whose `RowSpan` would reach past the page's own row count ("runs past the bottom of the
  grid").

**Examples:**

Hero camera (2x2) with five small ones around it, on a 3x3 grid:

```
$layout{A1:2x2,A4;-,-,B5;C6,C7,C8}
```

Row A: the hero (`A1`, 2 rows x 2 columns) plus one small tile (`A4`) to its right. Row B: two `-`
covering the hero's continuation, plus one more small tile (`B5`). Row C: three small tiles, full
width — the hero doesn't reach down that far.

A tall tile on the left beside a 3-row stack on the right:

```
$layout{A1:3x1,A2;-,B3;-,C4}
```

`A1` spans all 3 rows in column 1; `A2`, `B3`, `C4` stack in column 2, one per row. Rows B and C each
start with a `-` marking where `A1` still reaches.

Two small tiles, a full-width banner, then two more small tiles:

```
$layout{A1,A2;B3:1x2;C4,C5}
```

The middle row is ONE tile (`B3`) spanning both columns (`1x2`, so no vertical reach — no `-` needed
anywhere) instead of two side-by-side tiles.

A page with **no** span suffix and **no** `-` is unaffected by any of this — it keeps the original
letter-grouped grammar exactly as documented above, rows may have different cell counts, and `,`/`;`
stay interchangeable.

### layout-state.json

GridLookout persists the resolved plan to `layout-state.json`, in the same writable state directory
as `health.json` (next to `camerawall.json` when that directory is writable, otherwise
`%ProgramData%\GridLookout`). This is runtime state, not something to hand-edit — delete it to force
a full, clean re-resolve of every `$layout{}` token (e.g. after a bulk camera reorganization you
know is intentional).

### Memory/CPU tuning

`FitFrameSizeToTile` and `MaxFps` exist because a decoded video frame's memory cost scales with its
pixel count and its arrival rate, and the wall was previously requesting every tile at a flat
1280x720 with no rate cap regardless of how large that tile actually is on screen. Expected effect
of the defaults on a typical dense grid (e.g. 16-20 tiles):

- **`FitFrameSizeToTile=true`**: a tile that's actually rendered at, say, 640x360 on screen (half
  the linear dimensions of 1280x720) now requests a frame at roughly that size instead of 1280x720
  — a **quarter** the decoded-bitmap memory for that tile, because memory scales with width ×
  height, not the linear dimension. Requested size is clamped to a **320x180 floor** (never asks
  for something too small to look acceptable) and a **1280x720 ceiling** (never asks for more than
  the pre-optimization baseline), and rounded down to even numbers (some encoders reject odd
  dimensions). Sizing is computed once when the grid is built (normally fullscreen at startup) —
  it does **not** re-negotiate live as the window resizes or toggles compact mode; the video simply
  scales visually in that case, same as before.
- **`MaxFps=12`** vs. an uncapped source running up to 30fps is roughly **60% fewer frames
  decoded and turned into bitmaps per second** — the expensive part (JPEG decode + GDI bitmap
  allocation/replacement) is skipped for every dropped frame, not just its network bytes.
- **When to set `FitFrameSizeToTile=false`**: if a recording server/driver responds badly to a
  non-standard requested resolution (letterboxing incorrectly, refusing the stream, or similar) —
  this restores the flat 1280x720 request for every tile, matching pre-optimization behavior at
  the cost of the memory savings above.
- **Cell spans (`:RxC` — see below)**: a spanned tile's requested size scales up with how many grid
  cells it covers, same clamp either way — a hero tile spanning most of the grid can legitimately
  request close to the 1280x720 ceiling even on a dense wall, while an ordinary 1x1 tile on that same
  wall requests something much smaller.

### Page rotation

`PageSeconds`/`PageSize` exist for the same reason as the frame-sizing tuning above: RAM scales
with how many `LiveTileSource`s exist at once, and without paging that's every camera the recorder
has, all the time. With paging on, only the current page's live streams exist — a page flip tears
down the outgoing page and builds the incoming one, so RAM scales with **page size**, not total
camera count. Concretely: 20 cameras with `PageSize: 10` runs roughly **half** the live streams of
showing all 20 in one dense grid, at the cost of each camera only being visible half the time.
Applies only to the auto-layout path (no `$layout{}` matrix in the recorder description) — a
matrix always overrides it, but a matrix can page on its own terms instead: put `|` inside the
token body to split it into pages, e.g. `$layout{A1,A2;B3,B4|A5,A6;B7,B8}` is a 2-page matrix that
rotates the same way. Minimum effective rotation interval is **10 seconds** either way — a page
flip tears down and rebuilds live sources, which costs roughly 2s, so a faster interval would
thrash. A `$layout{}` matrix with more than one `|` page rotates even if `PageSeconds` is left at
its `0` default (falls back to the 10s floor) — writing multiple pages is itself the request to
rotate; only the auto-layout path treats `0` as "rotation off".

### `camerawall.local.json` overlay

An optional file, same directory, named `camerawall.local.json` — a dev/override layer merged on
top of `camerawall.json` key-by-key (any key present in the overlay wins; keys absent from it keep
the primary file's value). It's the intended place to keep lab/dev credentials or a different
`ManagementServerUri` out of the file that actually ships. It is gitignored and never shipped in
the MSI/publish output. When a plaintext `Password` is migrated to a DPAPI blob, only the one file
it was actually read from gets rewritten (respecting overlay-wins-over-primary precedence) — the
other file's contents are never touched or leaked into it. **Unlike the primary file, the overlay's
rewrite has no writable-state fallback:** if the install directory isn't writable, the running
process's in-memory config still gets the DPAPI-protected value for that run, but no
`%ProgramData%\GridLookout\camerawall.local.json` copy is ever written — the overlay file itself
keeps its plaintext `Password` on disk indefinitely, and a `WARNING` is logged every start naming
the file and telling you to remove the plaintext manually.

## Multi-recorder walls

Set `RecordingServers` to show cameras from several recording servers under one Management Server
on a single wall — one login, one MIP session, exactly like single-recorder mode:

```jsonc
// Inside camerawall.json (fragment — these keys sit at the top level of the config object):
{
  "RecordingServers": [
    { "Id": "8fa2c1e4-1b3d-4a5e-9c6f-2d7e8b9a0c1d" },
    { "HostName": "rec02.internal" }
  ],
  "LayoutRecorder": "8fa2c1e4-1b3d-4a5e-9c6f-2d7e8b9a0c1d",
  "Layout": ""
}
```

`LayoutRecorder` accepts a recorder's display **Name** or its **Id** — never a `HostName`; the
example above reuses the `Id` from the `RecordingServers` entry so it's unambiguous which recorder
it names (a display Name works equally well, e.g. `"LayoutRecorder": "REC-02"`, as long as it's
exact and unique among the currently selected recorders).

- **Captions and log lines** become `"RecorderName / CameraName"` in multi mode (single mode is
  unchanged) — needed because two recorders can have identically-named cameras.
- **Layout source — precedence (highest first):**
  1. A non-blank `Layout` config string — wins outright, exactly like the pre-`LayoutRecorder`
     behavior.
  2. Else the **layout-carrier recorder**'s own Description `$layout{}` tokens — `LayoutRecorder`
     names which ONE selected recorder is the carrier (by exact name or by Id). Multi mode still
     never reads `$layout{}` from more than one recorder's Description at a time (two recorders'
     descriptions could both claim monitor 1, with no sane way to pick a winner) — but it is no
     longer true that it reads NONE of them.
  3. Neither present → auto-grid of every selected recorder's enabled cameras.

  Whichever string wins is fed through the exact same parser/resolver pipeline as single-recorder
  mode (fingerprinting, last-known-good carry-forward, and `layout-state.json` all still apply).
- **Pinned vs. floating carrier authority.** `LayoutRecorder` **blank** (default) is auto-carrier:
  the operator never named an authority, so it floats unconditionally to the first
  `RecordingServers` entry — including on a match failure, since there is nothing to be unfaithful
  to. `LayoutRecorder` **non-blank** is PINNED: naming a recorder that's currently unmatched
  (removed, offline, a typo) or ambiguous (two selected recorders share that display name — use the
  Id instead) does **not** fall back to a different recorder's Description. The wall keeps its
  last-known-good layout exactly as it was, a warning is logged (once, when the condition starts —
  not every tick), and `health.json`'s `LayoutCarrierPinned` flips true (surfacing as `Degraded`)
  until `LayoutRecorder` again resolves to exactly one selected recorder — at which point authority
  resumes automatically, no restart needed. Pinning survives a controller restart or a mid-session
  recovery that happens **while the carrier is still missing** too: the wall re-renders the
  persisted last-known-good plan from `layout-state.json` at boot instead of falling back to the
  auto-grid (only a genuine first boot with the carrier already absent — no `layout-state.json`
  written yet — shows the auto-grid until the carrier appears). This split matters because an
  unmatched pin silently floating to another recorder would let that unrelated recorder's
  Description reshape the wall precisely during the outage the pin exists to protect against.
- **Managing layout from Management Client.** With `Layout` left blank, write the `$layout{}` token
  into the layout-carrier recorder's own Description field — Recording Servers → select the carrier
  recorder → Description, exactly the single-recorder-mode workflow described in "Changing the
  layout" above. GridLookout polls that recorder's Description in the background (never on the UI
  thread — see "Background description poll" below) roughly every `ConfigRefreshSeconds`, so a
  change applies live — no file edit, no restart — typically within one or two
  `ConfigRefreshSeconds` intervals (the poll completes in the background; the tick after it applies
  the result).
- **Background description poll and `AllowInsecureLayoutPoll`.** The carrier's Description is
  fetched via a REST call carrying this app's session token as an `Authorization: Bearer` header —
  refused over plain `http://` `ManagementServerUri` unless `AllowInsecureLayoutPoll=true` (poll
  skipped, warning logged on change, naming the flag). Skipped entirely (not attempted at all) when
  `Layout` above is non-blank, since no recorder's Description is consulted as a layout source in
  that case. Single-recorder mode's own Description poll is unaffected by either setting except the
  HTTPS gate — it always polls, since `Layout` is multi-mode-only.
- **Ordinal references** (`A3`) index the MERGED camera list across every selected recorder, sorted
  by `"RecorderName / CameraName"` — that order shifts whenever ANY selected recorder's camera set
  changes, not just the one you're editing. Prefer `@alias`/`@{guid}` (see `CameraBindings` above),
  which stay stable. GridLookout logs an `INFO` advisory at startup if an ordinal is used here, or
  in `Monitors[].Cameras`, while multi mode is active.
- **Session-loss recovery stays whole-wall** — one dead recorder's tiles recover via per-tile
  self-heal; recovery only tears the whole wall down when every tile across every recorder is stale.
- **Health.** With `Health.Enabled`, `health.json` gains a `Recorders` array — one entry per
  selected recorder with `id`, `name`, `tilesExpected`, `tilesRendering`, `tilesStalled`,
  `tilesNeverFramed`, `tilesUnavailable` — so a single dead recorder shows as degraded even while
  others stay healthy. Empty for single-recorder deployments. `id` is the recorder's stable FQID
  (always populated for every entry); `name` is display-only, resolved from the current selection —
  two differently-configured recorders sharing a display name are still tracked as two separate
  rows, keyed by `id`. A `RecordingServers[]` entry that currently matches NO live recorder at all
  (the recorder disappeared from the catalog) doesn't add a row here — instead it flips top-level
  `OverallStatus` to Degraded directly (see "Wall-health monitoring" above), so a vanished recorder
  is never invisible to the overall status even though it can't appear in a per-recorder list it was
  never resolved into.
- Out of scope today: multiple Management Servers, federation, `RecordingServers` hot-reload
  (requires a restart), and `--export-camera-bindings` remains single-recorder-only (use
  `--recorder <name>` to target one recorder at a time).

## Credential handling

- A plaintext `Password` in either `camerawall.json` or `camerawall.local.json` is migrated
  **automatically, on every load** — before the app does anything else — to a DPAPI-encrypted
  `PasswordProtected` blob (base64). This happens unconditionally, not just when
  `--protect-password` is passed.
- **Where the blob is written, and whether the plaintext gets blanked, depends on whether the
  install directory is writable by the running account.** If it is (the ordinary case), the
  blanking happens in the file the plaintext came from, exactly as before. If it isn't — the
  normal state for a `%ProgramFiles%\GridLookout` kiosk install running as a limited account — the
  working blob is instead written to a `%ProgramData%\GridLookout\camerawall.json` copy that gets
  merged back on top of the exe-directory `camerawall.json` on every load, and the exe-directory
  file's plaintext `Password` is **left in place** (not blanked) with a warning logged, since the app has
  no permission to rewrite that file. See the Security & Network Behavior guide's "Writable-state
  fallback" for the complete disclosure, including what to do about that leftover plaintext.
- **DPAPI scope is `CurrentUser`.** This is the load-bearing detail: the encrypted blob can only be
  decrypted by the same Windows account, on the same machine, that encrypted it. Concretely, that
  means:
  - You must run the app (even briefly) **as the exact account that will run it in production**
    (typically the kiosk account) for the migration to produce a blob that account can later
    decrypt.
  - A `PasswordProtected` blob copied from one machine or account to another will **not** decrypt —
    regenerate it under the target account instead.
- `--protect-password` CLI flag: runs just far enough to load the config (which performs the
  migration described above) and then exits immediately, without going on to connect and show the
  wall. Use it to seed the encrypted blob under the kiosk account non-interactively — run once as
  that account with a plaintext `Password` set, e.g.:
  ```
  GridLookout.exe --protect-password
  ```
- The app handles exactly one credential — the Management Server account set in `Username`
  (+ `Domain` for `Windows` mode) and `Password`/`PasswordProtected` — with no ambient/local
  Windows account involvement of any kind. There is no mode that avoids configuring a credential.

## Authentication modes

| `AuthMode` | Credential source | Notes |
|---|---|---|
| `Windows` | `Username` + `Domain` + password, via `Negotiate` | Explicit AD or local mirror account. |
| `Basic` | `Username` + password, via `Basic` scheme (Milestone basic/IDP user under the hood) | Refused over `http://` unless `AllowInsecureBasic=true`. Milestone strongly prefers HTTPS for basic users in production; `AllowInsecureBasic` exists for lab/dev servers running with encryption off. |

An empty `Username` under either mode is a config error — the app throws a clear exception on
startup telling you to fill `Username`/`Password` in `camerawall.json`.

Login failure of any kind (bad credentials, unreachable server, refused insecure-Basic
combination) surfaces as a fullscreen error card with a retry countdown (`ReconnectSeconds`) — the
app never sits on a blocking dialog.

## Command-line arguments

| Argument | Effect |
|---|---|
| `--recorder <name>` | Overrides `RecorderNameOverride` for this run only (not written back to the config file) — matches an exact recorder name instead of hostname-based self-location. |
| `--monitor <n>` | Overrides which monitor number is treated as the "default"/first monitor — used both to resolve the bare (unnumbered) `$layout{}` token and as which monitor is eligible for the `WindowBounds` override, in place of `Monitors[0].Monitor` from config. |
| `--protect-password` | Loads config (migrating any plaintext `Password` to `PasswordProtected`) and exits immediately — does not connect or show the wall. See "Credential handling" above. |
| `--health-probe` | External health check of an already-running wall — exits 0/1/2/3 (healthy/degraded/hung/absent). Full contract under "Wall-health monitoring" below. |
| `--screenshot` | Asks an already-running wall to save `screen-<n>.png` of every attached display, then prints the paths. Full contract (exit codes, freshness caveats) under "Remote screenshot" below. |
| `--export-camera-bindings` | Logs in, prints/writes a ready-to-paste `CameraBindings` skeleton (`camera-bindings.generated.json`), and exits. See "Stable camera references" above. |

## Kiosk deployment

`install-kiosk.ps1` (shipped inside `INSTALLDIR` by the MSI; also at `scripts/install-kiosk.ps1` in
the repo) wires GridLookout into the **current user's** session as a kiosk: autolaunch (Run key or
shell replacement) plus a watchdog scheduled task (unless `-NoWatchdog`). Run it while logged on as
whichever account should show the wall — it touches no accounts and no machine policy, only this
user's own `HKCU` hive.

```powershell
.\install-kiosk.ps1 -ExePath 'C:\Program Files\GridLookout\GridLookout.exe' -Shell
```

| Parameter | Meaning |
|---|---|
| `-ExePath` | Full path to `GridLookout.exe`. Must already exist (`Test-Path` validated). Required. |
| `-Shell` | Switch. When set: replaces this user's Winlogon shell with `-ExePath` directly — true kiosk, no `explorer.exe`, nothing behind the video wall. `Ctrl+Alt+Del` remains available for recovery regardless. When omitted (default): the wallboard launches via this user's Run key instead — desktop/Explorer still exists behind it. |
| `-NoWatchdog` | Switch. Skips registering the relaunch watchdog scheduled task (see below). The watchdog is registered by default regardless of `-Shell` — a plain Run-key install leaves the wall just as unprotected against a crash, an Esc exit, or a closed compact-mode window as a `-Shell` install does. Use this only if something else already supervises the process (a different watchdog, or a container/session manager). |
| `-RestartHung` | Switch. Escalates the watchdog from report-only to kill+relaunch when `--health-probe` reports a hung UI thread (exit 2), or an absent `health.json` from a long-running process (exit 3, only while `Health.Enabled` is true — read live from `camerawall.json` on every check, no reinstall needed after editing it). Off by default: a hung process is reported, never killed. |
| `-Uninstall` | Switch. Removes the autolaunch wiring (Run key or Shell value) and the watchdog task for this user — registered by either mode, whether or not `-NoWatchdog` was used at install time (`Unregister-ScheduledTask` no-ops harmlessly if it was never registered) — then exits. |

What the script does:

1. **Accounts — out of scope.** It configures whoever runs it (`HKCU` only). Create/choose the
   kiosk account with your own tooling and log on as it before running this script.
2. **Autologon — deliberately NOT configured.** Autologon is machine-level Windows policy that
   belongs to the machine's administrator, not an application script. Set it up yourself —
   Sysinternals Autologon (https://learn.microsoft.com/sysinternals/downloads/autologon) or your
   own management tooling. Without autologon, the wallboard still autolaunches on every logon of
   this user; with it, the full unattended chain works: boot → logon → autolaunch → watchdog.
3. **Autolaunch** — two mutually exclusive modes, chosen by `-Shell`, written to this user's
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (default) or
   `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` (`-Shell`).
4. **Watchdog** — registered in **both** autolaunch modes by default, not just `-Shell`. A
   scheduled task (`GridLookout-Watchdog`) running **as this user** (`Interactive` logon type —
   deliberately not `SYSTEM`, whose GUI launches land invisibly in session 0), checked every
   1 minute, starts `-ExePath` if the wallboard process is absent. Covers crashes, Esc exits, and
   a user closing the window from compact mode; only runs while this user has a session, which
   autologon (step 2, admin-managed) guarantees on an unattended box. Skip it with `-NoWatchdog`.
5. **Credentials in kiosk context** — one config-file credential, `Basic` or `Windows`, both
   explicit (no ambient/local Windows account involvement): either run the exe once as the kiosk
   account with a plaintext `Password` set (auto-migrates to a DPAPI blob), or run
   `GridLookout.exe --protect-password` in a shell running as the kiosk account.

The app is also single-instance (a named mutex prevents a second copy from starting) and relaunches
itself on any unhandled exception, independent of the watchdog task: it logs the failure, releases
its own single-instance mutex handle, starts a fresh process of the same exe with the original
command-line arguments, then exits — not `Application.Restart()` (an earlier implementation using
that call raced the single-instance check and sometimes left the wall closed with no relaunch at
all). GridLookout relaunches itself up to 5 times within a trailing 10-minute sliding window
(measured back from each new crash, not anchored to the first one — a slow-but-steady crash stream
can't outlast the window by simply waiting out an old anchor point); a 6th crash within that
trailing window stops the relaunch loop rather than spinning indefinitely — including a crash loop
that resumes after a previously successful recorder match, which counts toward the same window —
and recovery falls to the watchdog task's normal 1-minute poll instead of the immediate self-relaunch.

## Locking down the kiosk

`KioskLock: true` in `camerawall.json` removes exit/de-kiosk paths on the wall windows and on the
connecting/retry status cards: `Esc` no longer closes the app, double-clicking no longer toggles
compact/windowed mode, and `Alt+F4`/the window-close control are gated the same way — all funnel
through the same gate in `WallForm`, so this is one setting, not several things to keep in sync.
The two configuration-error cards — "not configured" (missing `ManagementServerUri`) and "could
not load its configuration" — are deliberately **not** gated by `KioskLock` and stay closable:
those cards mean there is no working config to run against, and a locked, unconfigurable box would
be unrecoverable without a hard stop. See "Public-screen hygiene" under Troubleshooting for what
each card type shows.

**There is no on-screen recovery hotkey.** GridLookout has never had an F-key/minimize hotkey (see
"Hotkeys" in the Operator Guide — global F-keys tend to collide with other software), so with
`KioskLock` on there is nothing left to gate a "deliberate" escape behind on a gated card. The
watchdog scheduled task (see "Kiosk deployment" above) relaunches the app if the process disappears,
so on a kiosk installed with `install-kiosk.ps1` (default, not `-NoWatchdog`) a stop must disable the
watchdog **before** killing the process, or the watchdog just restarts it within its 1-minute poll
interval. Operator stop paths once `KioskLock` is active:

1. **Stop the watchdog first** (skip this step only if the deployment used `-NoWatchdog`) — as the
   account the task runs under, or an admin:
   ```powershell
   # Stop this run and prevent further relaunches until re-enabled:
   Stop-ScheduledTask -TaskName 'GridLookout-Watchdog'
   Disable-ScheduledTask -TaskName 'GridLookout-Watchdog'
   ```
   or the `schtasks` equivalent:
   ```
   schtasks /end /tn "GridLookout-Watchdog"
   schtasks /change /tn "GridLookout-Watchdog" /disable
   ```
2. **Then end the process** — `Ctrl+Alt+Del` → Task Manager → end `GridLookout.exe`, or
   `taskkill /IM GridLookout.exe /F` from an elevated prompt. `Ctrl+Alt+Del` is the Windows secure
   attention sequence and cannot be intercepted or blocked by any application, GridLookout included.
3. Alternatively, an MSI uninstall removes the app (and, if the kiosk script registered it, the
   watchdog task) outright.

Re-enable the watchdog later with `Enable-ScheduledTask -TaskName 'GridLookout-Watchdog'` (or
`schtasks /change /tn "GridLookout-Watchdog" /enable`) before relaunching the wall unattended.

Because the on-screen posture then gives an operator no other way to tell the setting is even
active, `Program.Main` logs exactly one `INFO` line at startup when `KioskLock` is on — check the
log (see "Logging" below) if it's unclear whether a given deployment has it enabled.

Leave this `false` (the default — unlocked, matching every prior release) unless the deployment is
genuinely unattended and public-facing, where a passerby closing or de-kiosking the wall is a real
risk worth trading the Esc/double-click convenience for.

## Logging

- Plain-text log file at `<install dir>\logs\gridlookout-yyyyMMdd.log` — one file per calendar
  day, a new one starts automatically on the day's first write. When the install directory isn't
  writable by the running account, logging falls back to
  `%ProgramData%\GridLookout\logs\gridlookout-yyyyMMdd.log` instead (same naming pattern) — see
  the Security & Network Behavior guide's "Writable-state fallback" for when this engages. If a same-day log file
  in whichever directory is active already belongs to a different Windows account, the app falls
  back to a per-user variant of that day's filename (`gridlookout-yyyyMMdd-<username>.log`)
  rather than fighting over one file two accounts
  can't both write to.
- Line format: `yyyy-MM-dd HH:mm:ss.fff [LEVEL] message`.
- Levels, low to high severity — `DEBUG` (per-frame/refresh chatter, e.g. every-300th-frame counts,
  the full configuration item-tree dump used to diagnose recorder self-location), `INFO` (startup
  milestones, login, recorder matched, first frame received per camera, layout rebuilds), `WARNING`
  (retries, fallback-monitor use, recoverable per-frame/status errors), `ERROR` (exceptions, with
  the full exception text including stack trace appended).
  Filtered by `LogLevel` in `camerawall.json` (default `Info`).
- Bootstrap ordering: the logger is created at the `Info` floor **before** `camerawall.json` is
  read, so a config file that's missing or fails to parse is always logged regardless of what
  `LogLevel` it would have configured. The configured floor is only applied once the config load
  succeeds.
- **Retention, no size-based rotation.** At startup (right after `camerawall.json` is loaded),
  `gridlookout-*.log` files — the daily files and any per-user variants alike — older than
  `LogRetentionDays` days are deleted from whichever log directory (install dir or ProgramData
  fallback) is active on that machine. Default `30` days; set `LogRetentionDays` to `0` to disable
  pruning and keep every day-file forever. There is still no size-based rotation within a day.
- All logging I/O is wrapped in try/catch (both directory creation and each write) — a logging
  failure never crashes the app. An unwritable install path alone doesn't lose any log lines any
  more: it just falls back to `%ProgramData%\GridLookout\logs`, per the first bullet above. Only
  both the install directory AND `%ProgramData%` being unwritable drops log lines — that
  combination is what "nothing gets logged" actually requires now.
- Per-camera log lines are tagged `[camera name]` so you can grep/search a specific tile's history
  directly, e.g. `[Parking East] live content error: ...`.

## Per-tile self-heal

If a single tile stalls (no frames for StaleSeconds) or never receives its first frame,
GridLookout now reconnects that tile on its own — the whole wall no longer needs to go
stale before recovery kicks in. Configure via `TileRecoverSeconds` in camerawall.json
(default 30, floored at 10 when nonzero, 0 disables per-tile recovery). Each retry doubles
the wait up to 8x the base value, resetting on the next good frame. A never-framed tile
shows a red "NO SIGNAL" overlay once its first retry fires; a previously-framed stalled
tile keeps the existing red "STALLED" overlay. Both clear automatically on the next frame.
Recovery attempts and successes are logged at INFO with the tile's label.

Note: if StaleSeconds is set to 0 (STALLED overlay disabled), a framed tile still uses
TileRecoverSeconds as its own stale-onset threshold, so the first reconnect for that tile
happens at roughly 2x TileRecoverSeconds after its last frame, not 1x.

## Wall-health monitoring (optional)

GridLookout can report its own liveness for external monitoring. Off by default
(`Health.Enabled: false` in camerawall.json). When enabled, the running wall writes
`health.json` every 5 seconds into the same directory as camerawall.json (install dir, or
the %ProgramData%\GridLookout fallback — see "Writable-state fallback"), starting from
process launch (`ControllerState: "Starting"` before the first login attempt, `"Connecting"`
during it, `"Running"` once the wall is showing, `"Recovering"` during a later reconnect) —
not only after the first successful login. Contents are liveness/aggregate data only: process
id/start time, a UI-thread pulse timestamp, controller state, per-monitor tile counts
(expected/rendering/stalled/never-framed/**unavailable**), whether a configured
`RecordingServers[]` selector currently matches no live recorder
(`RecorderSelectionIncomplete`), and whether an explicitly configured `LayoutRecorder` currently
matches no selected recorder (`LayoutCarrierPinned` — see "Multi-recorder walls" below; the wall
keeps its last-known-good layout while this is true). It never contains camera
names, the Management Server URI, or credentials — **with one exception**: in multi-recorder
mode (`RecordingServers` configured), the `Recorders[]` array's per-recorder breakdown DOES
include each recorder's display name, so treat `health.json` as VMS-identity-bearing in that
configuration only (see docs/security.md's "Wall-health monitoring" section for the
full content-class disclosure).

**Health status matrix** — `OverallStatus` (and the probe's exit code below) follows this
order: a stale UI pulse is always Unhealthy/hung; otherwise a transitional controller state
(`Starting`/`Connecting`/`Recovering`) is Degraded regardless of tile counts (expected during
normal startup/reconnect); otherwise `Running` with **zero** configured wall windows is
Unhealthy (nothing is actually showing); otherwise ANY UNAVAILABLE cell, ANY stalled/
never-framed tile, an incomplete recorder selection, or a pinned-missing layout carrier
(`LayoutCarrierPinned`) is Degraded; anything else is Healthy.

Run `GridLookout.exe --health-probe` to check health.json and get a verdict:

| Exit code | Meaning |
|---|---|
| 0 | Healthy |
| 1 | Degraded (UI responsive, but 1+ tile stalled/never-framed/unavailable, or a configured recorder is missing) |
| 2 | Unhealthy / hung (UI pulse stale past `Health.StaleAfterSeconds`, OR the UI is responsive but `Running` with zero configured wall windows) — while the controller is `Starting`/`Connecting` (the SDK's blocking login/discovery calls freeze the UI pulse, since nothing is hung, it just can't tick), the stale threshold is 3x `Health.StaleAfterSeconds` instead of 1x, so a merely-slow connection attempt isn't misjudged as a hang |
| 3 | Absent (no health.json, or recorded process not actually running) |

The probe also prints a one-line JSON verdict to stdout. If `Health.Endpoint` is configured
(a customer-run HTTPS collector), the probe POSTs the health.json content there too — with one
addition: a `probeVerdict` field carrying the probe's OWN evaluated status string (the same
value as the printed stdout verdict), so the remote collector sees what an outside observer
actually concluded, not just the controller's self-report. The self-report's own "unhealthy" is
narrower than the probe's: a hung UI thread can never self-diagnose its own hang (that verdict is
reachable only from the outside, by this same probe recomputing from the file's age — see the
health status matrix above), so a stale-pulse `Unhealthy` never comes from the controller itself.
But the self-report CAN reach `Unhealthy` on its own for a condition it CAN see while still
responsive — `Running` with zero configured wall windows (nothing is actually showing) — so
`probeVerdict` is not the only source of `Unhealthy` in the POST body, just the only source of a
stale-pulse one. Uses `Authorization: Bearer <token>` if `Health.BearerToken` is set (DPAPI-encrypted on
first run, same as the VMS password). A POST failure only affects a field in the printed JSON
— never the exit code.

`install-kiosk.ps1`'s scheduled watchdog task automatically switches to probe mode when
`Health.Enabled` is true. The process-existence check still gates everything (see the script's
own comments): if the process isn't running at all, it's simply relaunched. If it IS running,
exit code 2 (hung) — or exit code 3 (absent) while the process has been running for longer
than 3x `Health.StaleAfterSeconds` (a startup hang that never got as far as writing a first
health.json) — is treated as hung, but only **kills and relaunches** the process when
`-RestartHung` was passed at install time; without it, a hung process is only ever reported,
never killed by this script. Off by default, since a hung-but-alive process is a different
failure mode than a crashed one and some deployments may want to investigate before an
automatic restart. `Health.Enabled` and `Health.StaleAfterSeconds` are read live from
`camerawall.json` by the watchdog **on every check**, not baked in at install time — editing
either setting takes effect on the next 1-minute tick, no re-run of `install-kiosk.ps1`
needed. (`-RestartHung` itself IS an install-time value, since it's a script switch with no
corresponding config-file setting to re-read.)

## Remote screenshot

`GridLookout.exe --screenshot`, run while another GridLookout instance is already showing the wall,
captures whatever is currently on the screens and saves one PNG per monitor — **without disturbing
the running kiosk**: the wall keeps rendering, keeps its VMS session, and never loses focus or
flickers. This exists for exactly one purpose: a remote operator (over `ssh`/`psexec`, from a
DIFFERENT Windows session than the kiosk's own autologon session) sanity-checking an unattended box
without physically walking up to it or disturbing the console session's lock state.

```powershell
# From a remote admin shell, e.g. psexec \\kiosk-host -u DOMAIN\admin cmd:
& 'C:\Program Files\GridLookout\GridLookout.exe' --screenshot
```

On success it prints the full path to every `screen-<n>.png` it found, one per line, and exits 0.
Fetch them however suits the remote session (`Copy-Item`/`scp` over the admin share, etc.).

**Exit codes** — this is a SEPARATE scale from the `--health-probe` exit codes above; the two flags
are unrelated and their numbers do not mean the same thing:

| Exit code | Meaning |
|---|---|
| 0 | The running wall answered and finished its capture attempt. Files are listed above the exit — but see the caveats below; 0 does not by itself guarantee any files were actually written. |
| 1 | Unexpected error (a malformed invocation, or a filesystem/permission failure listing the output directory). Printed to both stdout and stderr. |
| 2 | GridLookout is not running (no wall process has the remote-screenshot IPC events armed). |
| 3 | Timed out after 15 seconds waiting for the running wall to respond — the wall may be hung; consider `--health-probe` next. |

**Where files land.** `<state directory>\screenshots\screen-<n>.png`, one per **attached display**
(`screen-1.png`, `screen-2.png`, … — every display Windows reports, NOT just the monitors the wall
is configured to occupy: a display the wall deliberately leaves showing the desktop is captured
too, complete with whatever applications are on it — see the security guide's disclosure),
overwritten in place on every capture — no accumulation across
repeated requests, and a monitor count that DROPS between captures has its now-orphaned higher-numbered
file deleted automatically (no stale `screen-3.png` left behind forever after a box goes from three
monitors to two). "State directory" is the SAME resolved location `camerawall.json`/`health.json` use
(see "Writable-state fallback" in docs/security.md) — normally next to the exe, or
`%ProgramData%\GridLookout` on the documented limited-account kiosk setup. Because the remote
`--screenshot` invocation and the running wall can be DIFFERENT Windows accounts with different write
access to the install directory, the requester checks both possible locations and reports from
whichever one actually holds files newly written by THIS request (see the freshness caveat below) —
you don't need to know or guess which one your kiosk uses.

**Two caveats that matter when reading the result:**

- **Exit 0 with no files listed is possible.** A capture failure on the wall's side (a display
  driver refusing the screen copy, the output directory unexpectedly unwritable) is logged as a
  Warning in the wall's own log and does not crash it — but it also does not surface as a
  `--screenshot` error, since the wall genuinely finished attempting the request. If nothing is
  printed, check the wall's log for `Screenshot capture failed` before assuming there's simply
  nothing to see.
- **Exit 0 USUALLY guarantees the images are fresh from THIS request, but not always.** The tool
  prefers whichever of the two possible locations holds a file written at or after the moment it
  sent the request; if a capture failure (see above) left only an OLDER file behind in every
  checked location, it falls back to reporting that old file rather than nothing. This is the one
  case exit 0 doesn't guarantee freshness — check the printed files' modified timestamps if you
  need to be certain.
- **The interactive session must be unlocked and rendering.** A locked workstation, a disconnected
  RDP session, or an idle/minimized RDP client has no rendering desktop surface — GDI screen capture
  then either produces solid-black frames or fails the capture outright (the wall logs
  `Screenshot capture failed: ... The handle is invalid`, still signals completion, and the
  requester exits 0 with stale or no files — see the freshness caveat above). Either way it is
  inherent to Windows screen capture on a non-rendering desktop, not a GridLookout limitation:
  check the kiosk console session's state before suspecting the video wall itself.

**Requirements.** No configuration flag enables/disables this — it's always available on any running
wall, subject to one requirement: the account running the wall needs the Windows "Create global
objects" privilege (granted by default to Administrators/SYSTEM/service accounts) to arm the
underlying IPC objects at boot. If a limited kiosk account lacks it, the wall logs a Warning at
startup and `--screenshot` reports exit code 2 (indistinguishable from "not running") until the
account's privilege is fixed — see docs/security.md for the full mechanism.

## Troubleshooting

> **Public-screen hygiene:** on-screen error cards are deliberately generic ("No matching host",
> "Connection failed") — a kiosk in a public place must not display VMS topology. All diagnostics
> (local hostname, candidate recorders as `name @registered-host`, full exception text) are in the
> `logs` folder — next to the exe normally, or under `%ProgramData%\GridLookout\logs` if the
> writable-state fallback is active on this machine (see "Logging" above).

**Black tile (no video, camera visible in the grid but no picture)**

- Check the recorder host's port **7563** is reachable from the machine running the wall — this is
  the actual live-video source; if the wall runs on a different box than the recorder, this is the
  first thing to test (`Test-NetConnection <recorder-host> -Port 7563`).
- Check the log for that camera's tag: a live-content error is logged as
  `WARNING [camera] live content error: <message>` — this surfaces licensing problems, codec
  refusals, and driver-level errors reported by the SDK.
- The app never requests "native" resolution (`0x0`) — some camera drivers refuse that outright.
  With the default `FitFrameSizeToTile: true` it requests each tile's actual on-screen size
  (clamped 320×180–1280×720, see the tuning section above); with it `false`, a flat 1280×720 for
  every tile. If a specific camera/driver refuses the requested size either way, that shows up as
  the same live-content error above.
- A known SDK quirk: sometimes a camera reaches "LiveFeed" status but no frames actually arrive.
  The app has a one-shot workaround for this (re-toggles the live-start flag once), logged as
  `INFO [camera] LiveFeed up but no frames — re-toggling LiveModeStart.` If frames still never
  arrive after that, the issue is upstream of the wall app (device/codec/network).
- A camera **disabled** in Management Client leaves the enabled-camera ordinal list (so an
  auto-grid wall, or a fresh ordinal resolve, simply won't include it — which can look like a
  missing/renumbered layout rather than a connectivity problem). A `$layout{}` cell already
  **pinned** to it (an alias, a guid literal, or a previously-resolved ordinal) behaves
  differently: the cell stays in place and shows the dark `UNAVAILABLE — <name> (disabled)` tile
  instead of vanishing (F3 rule: the pin is never silently re-pointed). Check the Management
  Client device list if a camera you expect is simply not present.

**STALLED overlay**

Means a tile that *was* successfully receiving frames has gone `StaleSeconds` (default 10s)
without a new one — the picture on screen is a frozen last-known frame, not blank. This is
different from a black tile (which never received a frame in the first place, and never shows the
STALLED banner). Investigate the same way as a black tile — the connection likely died mid-stream.

**Login failures**

Fullscreen red status card reading "Reconnecting in Ns..." — deliberately generic, per the
public-screen hygiene rule above: it includes no exception message and **no filesystem path**
(a path on a public screen discloses the Windows username and machine layout). Common causes:
`ManagementServerUri` wrong/unreachable, `AuthMode` mismatched with how the account is actually
set up, `AllowInsecureBasic=false` blocking a Basic-over-http combination deliberately, or the
account lacking an XProtect role. Full exception detail (including stack trace) is logged at
`ERROR` as `Login/locate attempt failed` — the log location is under "Logging" above (also named
by the startup `INFO` line and, as a setup-time exception, on the configuration-error cards).
Note: the retry loop does **not** re-read `camerawall.json` between attempts — after fixing the
config, restart the application.

**"No matching host."**

Also a deliberately generic on-screen card (same public-screen hygiene rule) — it never lists
candidate recorders. The full candidate list (every recorder-name candidate found in the
Management Server's configuration, as `name @registered-host`) plus this host's own local hostname
is logged at `WARNING` as `Recorder locate failed for host '<hostname>'`. Check the log: either
none of the candidates matched this host's name/FQDN, or more than one did. Fix by setting
`RecorderNameOverride` in `camerawall.json` (or `--recorder <name>`) to one of the candidates named
in the log, exactly.

**Layout token typos (`$layout{}`)**

Two different failure shapes, worth telling apart:
- A syntactically valid entry with an **out-of-range camera number** (e.g. `$layout{A9}` on a
  5-camera recorder) renders that one tile as the dark `UNAVAILABLE` tile naming the reference and
  the reason ("ordinal 9 is out of range …") — the rest of the layout still renders normally, and
  the ordinal is retried on every refresh until it comes into range.
- A **malformed token** (garbled text inside the braces, a span/row-shape violation) is rejected by
  the parser with a `WARNING` log line naming the monitor and the reason. The monitor is NOT torn
  down: if it has a last-known-good layout on record (`layout-state.json`), that keeps rendering
  ("stale but valid beats no layout at all", logged as a carry-forward); a monitor that never had a
  valid layout has nothing to fall back to and shows no wall window. Only when NO monitor resolves
  any layout at all does the wall fall back to the `Monitors[]` automatic grid. If a layout change
  doesn't seem to take effect, check the log for the parser's `WARNING` before assuming the refresh
  hasn't happened yet.

**Duplicate `$layout{}` tokens for the same monitor**

The wall opens exactly one window per monitor. If the Description carries two valid tokens
targeting the same monitor — most commonly two bare `$layout{}` tokens with no digit — only the
FIRST is used; every further one is ignored with a `WARNING` log line ("Duplicate $layout token
for monitor N ignored"). An unparseable token does not claim its monitor, so a valid token after a
garbled one still renders. Use `$layout2{}`, `$layout3{}`, … to address additional monitors.

**Memory usage**

Two measured datapoints (method matters — `FitFrameSizeToTile: true` means requested frame sizes
follow on-screen tile size, so memory tracks *frame area*, not tile count):

- **401 MB working set at 4 large tiles** (near-fullscreen window, live measurement, 2026-08-18).
- **Flat ≈170 MB from 4 through 20 tiles** in a 1280×720 window (tile-scaling campaign,
  2026-08-19: 168/167/171/170 MB average at 4/9/16/20 concurrent streams — per-tile frames clamp
  at the 320×180 floor in the denser grids, so the per-stream marginal cost at that size is
  negligible). A fullscreen wall with few large tiles sits closer to the first figure; a dense
  many-tile grid sits closer to the second, because each tile's requested frame is small.

The same campaign measured the **recording server's** cost: 5.3–6.6% average VM CPU from 4 to 20
concurrent JPEG streams (12 fps, small frames) — statistically flat over the 2-camera baseline.
Each tile runs its own `JPEGLiveSource` and decodes its own frames independently (one decoder per
tile, no shared/pooled decode path), so working-set memory grows with total decoded frame area —
that growth alone is not evidence of a leak. Don't alarm on the absolute number; watch instead for
unbounded, continuous growth over hours/days at a fixed tile count, which would indicate an actual
problem rather than expected per-tile decode overhead.
