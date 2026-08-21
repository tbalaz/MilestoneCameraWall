# GridLookout — Positioning vs. a Smart Client Kiosk

For integrators deciding between GridLookout and the free alternative most XProtect sites already
have available: a Smart Client instance set to auto-login and shown fullscreen on a wall PC. Both
are legitimate ways to put XProtect video on a wall. This page is the honest comparison, not a
sales pitch — every GridLookout claim below is sourced from this repo's code/docs; the Smart
Client column is general, publicly documented Milestone product behavior, not independently
verified against a specific XProtect version in this lab (footnoted where it matters).

## Feature comparison

| Capability | GridLookout | Smart Client (auto-login kiosk) |
|---|---|---|
| Self-heal on failure | Boot-time login retry loop, **and** mid-session recovery that re-logs in and rebuilds the wall on session loss (detected via connectivity failure or stale-frame staleness), with a backoff ladder (60s → capped at 900s) so a flapping VMS isn't hammered with reconnects. Lab-verified against a live XProtect system, including a rotating-wall case where the staleness trigger fired correctly under active page flips. | Relies on the OS/watchdog layer restarting the process; Smart Client itself has no equivalent built-in mid-session VMS-session recovery loop.¹ |
| Central layout control | One line in the recording server's **Description** field (`$layout{A1,A2;B3}` syntax) in Management Client changes what a remote wall shows — no visit to the wall PC. Picked up automatically within one or two `ConfigRefreshSeconds` intervals (default 60s, floored at 5s — the read runs in the background between ticks, so a change typically lands on the tick after the poll that fetched it). | Smart Client views/layouts are normally configured per-client or pushed via a separate Management Client "push view" action; not a single text field read on a polling interval.¹ |
| Crash/exit supervision | A per-user Task Scheduler watchdog task (`install-kiosk.ps1`, opt-out via `-NoWatchdog`), **plus** an independent in-process crash-relaunch guard that restarts the app after an unhandled exception (up to 5 times per 10-minute window). Two independent mechanisms, not one. | Typically OS-level only (a scheduled task or shell-replacement restart you configure yourself); no built-in in-process crash-relaunch guard.¹ |
| Kiosk lockdown | `KioskLock` config flag disables Esc-exit and the double-click windowed-mode toggle across every wall window and status card — the only stop paths left are Ctrl+Alt+Del or an uninstall. | Kiosk lockdown is a matter of general Windows kiosk tooling (shell replacement, Assigned Access), not an application-level flag.¹ |
| Licensing footprint | Works on **every** XProtect edition, including Express+ and Professional+ — no Smart Wall, Expert, or Corporate licensing needed. Needs one XProtect (or AD/mirror) account with a live-view-only role. | Smart Client itself ships with every edition too; a wall built from **Smart Wall views** specifically needs Smart Wall licensing (not required for a plain auto-login Smart Client kiosk).¹ |
| Footprint | One config file (`camerawall.json`, JSON with inline comments), one MSI, one kiosk script for autolaunch/watchdog setup. | Full Smart Client install; layout/config typically lives in Management Server configuration plus local Smart Client settings, not one portable file. |

¹ Not verified against a specific Smart Client version in this lab — stated from general Milestone
product documentation, offered for context, not as a tested claim on the same footing as the
GridLookout column.

## Honest limitations

- **Video quality.** GridLookout decodes MJPEG (JPEG-per-frame) live streams at a default cap of
  **12 fps** (`MaxFps`, configurable — `0` removes the cap and lets the server's native rate
  through). This is visibly lower quality than a native H.264 stream rendered by Smart Client. If a
  wall's job is close visual scrutiny of motion, that trade-off matters; if it's situational
  awareness across many cameras, it usually doesn't. We are not softening this: per-frame JPEG over
  Milestone's ImageServer channel (the recorder connection on TCP 7563) is the whole reason the app
  has no GPU dependency and a tiny footprint, and it is also why the video looks the way it looks.
- **Recorder-side transcode cost.** Requesting JPEG live streams asks the recording server to
  transcode from its native format. **Measured 2026-08-19**: the recording-server VM's average
  CPU stayed statistically flat (5.3–6.6%) from 4 through 20 concurrent 12 fps streams at small
  tile sizes — for a typical single wall, the added load is one or two CPU points, not a
  capacity problem. Larger frame sizes and multiple walls multiply that; pilot on a
  representative recorder before committing to a large fleet.
- **LAN-oriented.** Designed and tested for a wall on the same LAN as its recording server(s) (or a
  low-latency link to one). It has not been characterized over a WAN/VPN path to a remote site.

## What it deliberately is not

- **Not an operator console.** No PTZ, audio, playback, export, or alarm handling — by design, not
  omission. A wall on a public or semi-public screen that can move cameras or pull recordings is a
  security liability, so the recommended wall account is **live-view-only** and the application
  never asks for more. Operators who need those functions have a Smart Client seat; the wall is for
  watching, not driving.
- **Not a fleet manager.** GridLookout does not push config, orchestrate upgrades, or inventory
  installations — deployments at scale belong to the tooling you already run (Ansible, SCCM, GPO,
  Intune). The product is built to be *managed by* those tools rather than to replace them: silent
  MSI install, one JSON config file, a kiosk-wiring script, and `--health-probe` with
  machine-readable exit codes for any monitoring stack.
- **Not a failover cluster.** One wall controller drives its screens; a standby controller is a
  second license and your own switchover procedure. The self-healing that IS built in (per-tile
  reconnect, whole-session recovery, crash relaunch, watchdog) covers the failures a lone kiosk
  actually meets.

## Does it pay for itself?

A Single Controller license is **€500** perpetual, or **€180/year** on subscription (see the
[Commercial Licensing guide](../COMMERCIAL-LICENSE.md) for the full pricing table). Set
that against one avoided site visit — a truck roll to walk over to a wall PC and either fix a stuck
session or edit a layout locally, commonly quoted in the EUR 150–500 range depending on your own
distance/contract rate (that figure is the reader's own input, not a number this repo can verify).
The practical framing: **it pays for itself the first time you reconfigure a wall remotely, or the
wall self-heals an outage that would otherwise have needed someone on site** — not a formal ROI
percentage, just the arithmetic of one skipped visit against the license price.

## Buying

See the [Commercial Licensing guide](../COMMERCIAL-LICENSE.md) for the pricing table
(Single Controller / Site Pack / 10-Pack), the free-vs-commercial-use split, and how to buy.
