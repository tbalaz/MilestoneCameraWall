# Compatibility Matrix

## Milestone XProtect

| VMS | Status |
|---|---|
| XProtect 2025 R3 (25.3) | **Tested** — development and continuous live testing |
| XProtect 2023 R1 – 2025 R2 | Expected to work (MIP SDK compatibility window) — not yet verified |
| Older than 2023 R1 | Unknown — not tested |

Built against MIP SDK **25.2.3**.

**Designed to work on every XProtect edition — including Express+ and Professional+.** The
application only needs login, configuration read, and live JPEG streams — capabilities present
across all editions per Milestone's own MIP/VMS support documentation; it does not use Smart Wall
and needs no Smart Wall / Expert / Corporate licensing. Edition coverage beyond the tested row
above is a design expectation, not a per-edition validation — only the **Tested** row has been
verified in this lab. The XProtect user it logs in with needs a role with live-view permission on
the displayed cameras.

## Wall machine (where the application runs)

| Component | Requirement |
|---|---|
| OS (tested) | Windows 11 x64 |
| OS (expected) | Windows 10 x64 1903+ and Windows Server 2022+ (.NET Framework 4.8 preinstalled); Windows 10 1809 / Server 2019 after installing .NET Framework 4.8 |
| Architecture | x64 only |
| Runtime | .NET Framework 4.8 — in-box on Windows 10 1903+/Windows 11/Server 2022+ (**no runtime installation needed there**); Windows 10 1809 / Server 2019 need the .NET Framework 4.8 feature installed first, per the OS row above |
| GPU | None required — JPEG decoding is CPU-side; any display output works |
| Memory | Measured: 401 MB working set at 4 large (near-fullscreen) tiles, 2026-08-18; ≈170 MB flat from 4 through 20 tiles in a 1280×720 window, 2026-08-19 — with `FitFrameSizeToTile` (default), memory tracks total decoded frame area, not tile count. Recording-server CPU measured statistically flat (5.3–6.6% VM average) from 4 to 20 concurrent 12 fps JPEG streams. Page/tile rotation further reduces concurrent streams. **Method caveat:** single-lab observations (one development workstation driving virtualized lab recorders with synthetic 12 fps JPEG sources) — useful sizing guidance, not capacity guarantees; validate against your own camera resolutions, frame rates, and hardware during a pilot. |
| Network | Reachability of the Management Server and the recorder's Recording Server (default TCP 7563); LAN recommended for full-frame-rate walls |

## Verification status

Rows marked *Tested* are exercised against a live XProtect system on every release. Rows
marked *Expected* follow from platform/SDK compatibility guarantees but have not had a live
pass — treat them as supported-on-paper until listed as tested.
