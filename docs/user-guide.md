# GridLookout — Operator Guide

This is the guide for the person sitting in front of a GridLookout screen — the wall
itself, not the computer behind it. If you need to install, configure, or troubleshoot the
software, see `admin-guide.md` instead.

## What you're looking at

GridLookout shows the live cameras that belong to one recording server, arranged as a
grid that fills the screen. There is no menu, no toolbar, and nothing to click to "start" it — it
comes up automatically and stays fullscreen.

### The tiles

Each camera gets one rectangular tile showing its live video. If nothing is configured to arrange
them a specific way, the wall picks a balanced grid automatically based on how many cameras there
are (for example: 4 cameras → a 2×2 grid; 6 cameras → two rows of 3; 9 cameras → three rows of 3).

How the video sits inside its tile is a site setting (`TileScaleMode`): **Fit** keeps the image's
shape and letterboxes it (background bars at the sides or top/bottom), **Fill** covers the whole
tile and crops whatever overflows, **Stretch** covers the whole tile by distorting the image. If
the picture looks cropped or squashed compared to what you expect, that's this setting — ask your
administrator, it's one line in the configuration file.

### Captions (camera names)

If the site has captions turned on, every tile has a thin bar along its top showing:

```
3: Parking East
```

The number is the tile's **ordinal** — its position in this recorder's camera list — and the text
is the camera's display name. This caption is not just decoration: the number is exactly what you
(or whoever configures the layout) type when writing a custom layout. See "Changing the layout"
below.

### Header strip

If turned on for this screen, a strip along the very top shows the recorder's name on the left and
a live clock on the right, so you can confirm at a glance which recorder this wall belongs to and
that the display itself hasn't frozen (the clock keeps ticking even if a camera feed hasn't).

### Tile borders

A thin line may separate tiles from each other and from the edge of the screen, to make the grid
easier to read at a glance. Whether this line appears, and its color, is a site setting.

### Page rotation

If this recorder has more cameras than comfortably fit on one screen, the wall may be set up to
rotate through pages automatically instead of showing everything at once — a handful of cameras
for a while, then the next handful, and so on. If captions and the header strip are both turned
on, the header shows which page you're on and how many there are (e.g. `Lobby Recorder — page
2/3`), and each tile's ordinal caption still refers to that camera's position in the *whole*
recorder's list, not just the page on screen — so a `$layout{}` matrix written against the
captions you see stays correct no matter which page happened to be showing when you read them. A
site with a `$layout{}` matrix in play can rotate too, on its own schedule, if the administrator
wrote more than one page into that matrix — same header indicator either way. Whether rotation is
on, and how fast, is a site setting; ask your administrator if this screen seems to be sitting on
one page longer than expected.

## Reading the STALLED overlay

If a camera's video freezes — the tile keeps showing the last picture it received, but no new
video is actually arriving — the wall will eventually notice and stamp a red banner across that
tile:

```
STALLED — last frame 14:32:07
```

This is the single most important thing to watch for on a camera wall: a frozen frame looks
exactly like a live one until you notice nothing in it has moved. The STALLED banner is what turns
that invisible failure into something you can actually see. The timestamp shown is when the last
real frame arrived, in local time.

A tile that has never shown any video at all (still connecting, or in an error state) does **not**
get this banner — it's a different situation and looks different on screen. STALLED specifically
means "this camera was working and then stopped."

### The NO SIGNAL overlay

If a camera never delivers its first frame, the wall shows a red "NO SIGNAL" overlay instead. The
wall is automatically retrying that tile with growing wait times; you do not need to do anything.
Both the NO SIGNAL and STALLED overlays disappear automatically the moment the next good frame
arrives. If NO SIGNAL persists for more than a minute or two, the camera itself or its network path
is likely down — report it to your network or camera team.

## Hotkeys

| Key / action | Effect |
|---|---|
| **Esc** | Closes the wallboard application entirely (all windows, all monitors it's showing on) — unless `KioskLock` is enabled for this wall, in which case Esc does nothing. |
| **Double-click** anywhere on a wall window | Toggles between fullscreen kiosk mode and a normal window — unless `KioskLock` is enabled, in which case double-clicking does nothing. |
| **Alt+F4** / the window's close control | Closes the wall the same as Esc — unless `KioskLock` is enabled, in which case it does nothing either. |

In the windowed state the wall behaves like any application: it has a **real title bar with
minimize, maximize and close buttons**, appears in the taskbar, can be dragged and resized, and is
no longer always-on-top. Use the title bar's minimize button to park it and the taskbar to bring
it back; double-click the video area again to return to fullscreen kiosk mode. (There is
deliberately no minimize hotkey — function keys kept colliding with other software, e.g. F9 is
taken by Snipping Tool.)

**`KioskLock`.** Some sites are configured with `KioskLock` on — a site setting, not something
you control from this screen. When it's on, none of the three rows above work: Esc no longer
exits, double-clicking no longer switches to a windowed view, and Alt+F4/the close control no
longer closes the window — on the wall itself or on a "Connecting..." or error status card. (A
"not configured" or "could not load its configuration" card, if you ever see one, is a different
kind of screen and stays closable either way — that's by design, not a gap in the lock.) This is
deliberate on a public/unattended wall, and there is no hotkey substitute for it — GridLookout has
no minimize/escape hotkey of any kind, locked or not (see the parenthetical above). If you need
the wall taken down and it isn't responding to Esc, Alt+F4, or a double-click, that is expected
under `KioskLock`; recovering it also isn't a simple Task Manager kill on most deployments — a
watchdog task normally relaunches the wall within a minute if it's just closed, so it has to be
disabled first. That two-step recovery (plus the uninstall alternative) is for whoever administers
the machine, not something you do from the wall itself — ask your administrator.

## Changing the layout

By default, the wall just arranges all of this recorder's cameras in an automatic grid. An
administrator can override that per-recorder by writing a small piece of text — called a
`$layout{}` token — into the recording server's **Description** field in Management Client
(Recording Servers → select the recorder → Description). No restart is needed; the wall reads the
description on a timer and rebuilds the grid on its own (see "How long changes take" below).

**If your wall shows cameras from several recording servers** (a multi-recorder wall), only ONE of
those recorders' Description fields is read for the layout token — your administrator sets up which
one (the "layout-carrier" recorder) when configuring the wall. Ask them which recorder that is
before editing a Description expecting it to change the layout; editing the wrong one's Description
has no effect on the wall at all.

If your administrator explicitly named that carrier recorder (rather than leaving it to default),
the wall treats that choice as fixed: should that recorder ever go offline or be removed, the wall
does **not** start reading some other recorder's Description instead — it keeps showing the layout
it had, unchanged, until the named recorder is back. This is deliberate: picking up a different,
unrelated recorder's Description automatically would be far more surprising than a layout that
simply stops updating for a while.

### The grammar

```
$layout{A1,A2;B3}
```

- Each entry is a **row letter** (A, B, C, … — A is the top row) followed by a **camera number**
  (the ordinal shown in that camera's tile caption, 1-based).
- Entries are separated by a comma `,` or a semicolon `;` — both mean the same thing; use whichever
  reads more clearly to you.
- Rows render top to bottom in letter order. Within a row, cameras render left to right in the
  order you wrote them.
- The same camera can be listed more than once (it will simply appear in more than one tile).
- Cameras you don't list are not shown at all — only what's in the token appears.
- Spaces are ignored, so `$layout{A1, A2 ; B3}` and `$layout{A1,A2;B3}` are the same thing.
- Letters are not case-sensitive (`a1` and `A1` are the same row).

### Worked examples

**1. One row, three cameras side by side**

```
$layout{A1,A2,A3}
```
Row A holds cameras 1, 2, and 3, in that left-to-right order — one wide row, no other rows.

**2. Two uneven rows**

```
$layout{A1,A2;B3}
```
Row A: cameras 1 and 2, side by side (each taking half the row's width). Row B, below it: camera 3
alone, stretched across the full width.

**3. Comma and semicolon mixed — same result either way**

```
$layout{A1;A2,A3;B4}
```
The separator doesn't decide the row — the **letter** does. This is row A with cameras 1, 2, 3
(all three, because they all say "A"), then row B with camera 4. Mixing `,` and `;` freely is
fine.

**4. Showing the same camera twice**

```
$layout{A1,A1,A2}
```
One row, three tiles: camera 1 appears in the first two tiles, camera 2 in the third. Useful when
you want a camera to stand out or to fill an otherwise-empty slot.

**5. Two monitors, different layouts on each**

```
$layout{A1,A2} $layout2{A3,A4,A5}
```
If this recorder box drives two monitors, `$layout{...}` (no number) targets the first/main
monitor and `$layout2{...}` targets the second. Here the main screen shows cameras 1 and 2 side by
side, and the second screen shows cameras 3, 4, and 5 in one row — completely independent layouts,
one `$layout{}`-family token per screen (`$layout3{}`, `$layout4{}`, etc. for more monitors).
Exactly one token per monitor: if two tokens target the same monitor (for example two plain
`$layout{}` tokens with no number), only the first is used — the rest are ignored, with a warning
in the log.

**6. A matrix that rotates through pages of its own**

```
$layout{A1,A2;B3,B4|A5,A6;B7,B8}
```
A `|` inside the token splits it into pages instead of one fixed layout — this is a 2-page matrix:
page one shows cameras 1, 2, 3, 4 (two rows of two), then it rotates to page two showing cameras
5, 6, 7, 8 in the same arrangement, then back to page one, and so on. With no `|` a matrix never
rotates, exactly as in every example above — this is purely opt-in by adding pages.

**7. A single tile that rotates through several cameras**

```
$layout{A1,A2,A(3,4,5);B6,B(7,8)}
```

Instead of (or alongside) pages, an INDIVIDUAL tile can cycle through cameras on its own — write
the camera numbers inside parentheses, separated by commas, instead of one plain number. Here row A
has three tiles: camera 1, camera 2, and a rotating tile that cycles 3 → 4 → 5 → 3 → … in the same
spot. Row B has two tiles: camera 6, and a rotating tile cycling 7 → 8 → 7 → …. Fixed and rotating
tiles sit side by side in the same row exactly like this — nothing else about the layout changes.

A rotating tile carries a small `⟳ 2/3` watermark badge in its top-right corner — the rotation
glyph, then its position in that tile's rotation set out of the total — so you can tell at a glance
both which tiles are cycling and where in the cycle each one currently is. For example `⟳ 2/3`
means: this tile rotates through 3 cameras and is currently showing the 2nd of those 3. The badge
is always visible, even when captions/header are switched off — on a public wall the rotation state
is disclosed without exposing any camera names. That fraction always counts positions within the
tile's own written list, not the camera's ordinal. How often it flips is set by your administrator
(`TileRotateSeconds` in the app's configuration, not something you control from the Description
field) — ask them if a rotating tile feels too fast or too slow. Every rotating tile on the wall
flips together, at the same moment, on that one shared interval.

If one of the numbers inside the parentheses is wrong (points at a camera that doesn't exist), that
number is simply skipped when the rotation reaches it — the tile keeps cycling through its other,
valid numbers. Only if every member in the parentheses is unavailable does the tile show the usual
dark **"UNAVAILABLE"** tile instead.

### If you get a number wrong

```
$layout{A1,A9}
```
If this recorder only has 5 cameras, tile A9 shows a dark **"UNAVAILABLE"** tile naming the
problem ("ordinal 9 is out of range…") instead of video, but tile A1 still renders normally — one
bad entry never breaks the rest of the layout, and the out-of-range number is retried
automatically, so the tile comes alive on its own if camera 9 later exists. If the whole token is unreadable garbage rather than just a wrong number, the wall keeps
showing this monitor's LAST layout that worked (a warning is logged for your administrator) —
nothing changes on screen. Only if this monitor's token has NEVER worked, with nothing to fall
back to, does it show the desktop instead — see "Layout tokens control everything when present"
below for the full rule.

### Making one tile bigger than the others

By default every tile in a `$layout{}` token is the same size. An administrator can make a tile
bigger — for example a "hero" camera that should stand out — by adding `:RxC` (rows x columns) right
after that tile's camera reference, and marking every extra grid position it now covers with a `-`.
For example:

```
$layout{A1:2x2,A3;-,-,B4;C5,C6,C7}
```

Camera 1 fills a 2x2 block in the top-left corner (twice the width and height of an ordinary tile);
the `-,-` in row B mark the two positions it still covers there. Everything else fills in around it
as usual. Ask your administrator if you want a tile enlarged this way — it's done in the same
Description field as the rest of the layout.

### Stable aliases and GUIDs

Cameras in a `$layout{}` token can be referenced by stable alias instead of just ordinals — ask your
administrator for the alias list. Using an alias (like `A@front-gate` instead of `A3`) means renaming
or reordering a camera never shifts which feed appears in that tile. You can also reference a camera
by its literal GUID if needed (e.g. `A@{8fa2c1e4-1b3d-4a5e-9c6f-2d7e8b9a0c1d}`). Malformed tokens
now show up as warnings in the log. A tile showing **"UNAVAILABLE"** means the referenced camera is
missing or disabled — the wall keeps the layout rather than silently substituting a different camera.

### How ordinals map to cameras

The number in `$layout{}` is the camera's position in this recorder's camera list, sorted
alphabetically by name. **The caption on each tile (`3: Parking East`) is the legend** — turn on
captions (ask your administrator if they aren't on), read off the numbers you see on screen, and
those are exactly the numbers to use when writing the layout.

Two rename rules, easy to conflate:
- A layout that is **already applied** never shifts on a rename: each ordinal was pinned to its
  camera the first time it resolved, so renaming or reordering cameras cannot silently change what
  an existing tile shows.
- A rename DOES change the number that camera will have the **next time you write or edit** a
  token — so re-check the on-screen captions before writing new ordinals after renames. (Aliases —
  next section — avoid this entirely.)

On a multi-recorder wall (your administrator will know), ordinals index the MERGED camera list
across every selected recorder, sorted by "Recorder / Camera" — captions there read
`Recorder / Camera` so the legend rule above still works the same way.

### How long changes take to apply

The wall doesn't watch the Description field continuously — it re-reads it in the background on a
periodic interval your administrator configures (`ConfigRefreshSeconds`); it will rebuild the grid
on its own, with no restart needed, typically within one or two of those intervals of saving a
change in Management Client (the read itself happens in the background between ticks, so it can
take slightly more than a single interval to be picked up and applied). Ask your administrator what
that interval is set to at your site, and how long it's actually taking to apply, if it seems slower
than expected.

### Layout tokens control everything when present

**Important:** The moment you write **any valid** `$layoutN{}` token into the Description, token mode takes over for the **whole wall**, not just that one monitor — the automatic grid is overridden everywhere. All-or-nothing, wall-wide:

- No tokens anywhere → automatic grid on every configured monitor
- One or more valid `$layout{}` / `$layout2{}` tokens exist → tokens are the complete wall specification; any monitor not named by a token shows the Windows desktop instead (including monitor 1, if only `$layout2{}` exists)

This means:
- To keep a wall on monitor 2 while adding a token, you **must** add a `$layout2{}` token for it (e.g. `$layout{A1,A2} $layout2{A3,A4}`), otherwise monitor 2 reverts to the desktop
- Removing a token dynamically closes the wall on its monitor within one or two `ConfigRefreshSeconds` intervals (see "How long changes take to apply" above)
- A typo in a token (`$layout{A}` instead of `$layout{A1}`) is logged as a warning, not silently ignored — and its monitor does NOT necessarily go blank: if that monitor's token has resolved successfully before, it keeps showing that last-known-good layout instead of the desktop. Only a monitor whose token has never once resolved (nothing to fall back to yet) shows the desktop — double-check syntax if a monitor has never shown video at all

**Troubleshooting:** Monitor showing desktop when it shouldn't?
1. Verify the Description has a `$layoutN{}` token for that monitor (`$layout{}` for monitor 1, `$layout2{}` for monitor 2, etc.)
2. Check the token syntax — each entry must be a letter followed by one or more digits (e.g. `A1`, `B12`)
3. Ask your administrator to check the wall's log file for warnings about parse errors or duplicate tokens

