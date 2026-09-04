# Patterns — Research & Architecture Plan

A portable Windows application that generates test patterns for corporate events, shows, and
festivals: LED walls, video walls, blended projection, broadcast screens, and NDI networks.

This document records the research, the decisions taken, and the architecture the code follows.
It is written before implementation and kept in sync with it.

---

## 1. Who uses this, and what actually matters

Field research context (LED techs, projectionists, screens operators at live events):

- **Setup time is compressed.** The app must open instantly from a USB stick on a rented media
  server, with zero installation, zero registry writes, and zero prerequisites. Everything lives
  next to the `.exe`.
- **Pixel accuracy is the whole point.** A 1‑px grid that lands "between" LED pixels is worse
  than no grid. Rendering must be device-pixel exact, DPI-scaling proof, with antialiasing off
  for alignment content.
- **It must never crash during a show.** Operators leave a pattern (or countdown) on screen for
  hours. Render loops must be allocation-free, exceptions must be contained per-frame, and a
  corrupt settings file must never prevent startup.
- **Screens teams think in walls, not monitors.** LED walls are described as *panels of W×H px,
  arranged C columns × R rows*; video walls as *N standard displays*; projection as *overlapping
  projectors with a blend width*. The UI speaks that language directly.
- **Everything ends up on a network now.** NDI output lets the pattern generator feed vision
  mixers, media servers and monitoring without a physical output.

## 2. Technology selection

| Option | Verdict |
|---|---|
| C++ / raw DirectX | Fastest possible, but development and stability cost is unjustified for 2D pattern rendering. |
| Electron / web | Fails the brief: heavy, GC jank, poor multi-screen fullscreen control, large memory. |
| WPF (.NET) | Viable, but pixel-exact rendering fights the WPF layout/DPI system; retained-mode is a poor fit for a per-frame engine; cannot be built or tested on Linux CI easily. |
| **Avalonia 11 + SkiaSharp** | **Chosen.** GPU-accelerated Skia compositor, immediate-mode custom drawing (pixel-exact, AA off where needed), first-class multi-window/multi-screen API, single-file self-contained portable exe, compile-time-checked (compiled) bindings, and the rendering core is testable headlessly on CI. |

Version pins: **Avalonia 11.3.x** (mature LTS line; deliberately not the newer 12.x — this tool
values proven stability over new API surface), **SkiaSharp 3.116.x** (the exact version Avalonia
resolves, so a single native `libSkiaSharp` ships), **.NET 8** (LTS).

External integrations, both **feature-detected and optional at runtime** so the portable exe has
no hard native prerequisites:

- **NDI** — P/Invoke against `Processing.NDI.Lib.x64.dll`. Looked up next to the exe, via
  `NDI_RUNTIME_DIR_V6`/`V5`, and in the standard NDI Runtime install folders. If not found the
  NDI page explains how to enable it (install the free NDI runtime, or drop the DLL beside the
  exe). The app never crashes for lack of NDI.
- **Video decode** — LibVLCSharp with *callback rendering* (frames decoded into a shared BGRA
  buffer that the engine composites like any other layer — so video also reaches NDI and spanned
  outputs). Enabled when a `libvlc` directory sits next to the exe or VLC is installed; images
  work natively without it.

## 3. Architecture — one engine, many sinks

The heart of the app is a UI-toolkit-independent rendering core (`Patterns.Core`, depends only on
SkiaSharp):

```
                 ┌────────────────────────────┐
   ShowState ───▶│  Snapshot (immutable-ish)  │───┐  published on change (version counter)
 (UI thread,     └────────────────────────────┘   │
  observable)                                     ▼
                                      ┌──────────────────────┐
                                      │     PatternEngine    │  Render(SKCanvas, ctx)
                                      │  pattern renderers   │
                                      │  overlays, particles │
                                      └──────────────────────┘
                                        ▲       ▲        ▲
                          UI preview ───┘   output ──────┘└────── NDI sender thread
                          (fit-scaled)      windows               (raster surface,
                                            (1:1 device px,       paced BGRA frames)
                                            span offsets)
```

- **`ShowState`** is the single mutable model the UI edits (observable POCOs, JSON-serializable).
  Any change bumps a version; each sink clones a **snapshot** when it notices a new version, so
  render threads never read a model mid-edit.
- **`PatternEngine`** renders a snapshot to any `SKCanvas` given a `RenderContext` (canvas size,
  time, frame index, viewport offset for spanning, fit scale, sink kind). The same code draws the
  preview, every fullscreen output, preset thumbnails, and NDI frames — one implementation to
  test, one visual truth everywhere.
- **Sinks** own their per-thread mutable state (paint caches, particle simulations seeded
  identically for visual consistency, FPS meters). Nothing Skia-stateful crosses threads.

### Pixel exactness

Output windows render at **1:1 device pixels**: the Skia canvas transform is reset to identity
(undoing DPI scaling), sizes come from the screen's pixel bounds, alignment patterns draw with
antialiasing off on integer coordinates. Spanned mode computes the union pixel rect of the
selected screens; each window translates by its screen's offset within that union.

### Smoothness & efficiency

- Redraw is driven by the compositor (`RequestAnimationFrame`) only while the current snapshot
  **is animated** (motion patterns, particles, seconds-bearing clock, countdown, video). Static
  patterns render once and then cost ~0 CPU/GPU.
- The per-frame path performs **no heap allocation**: paints/fonts are cached per sink, particle
  pools are pre-allocated arrays of structs, text uses cached buffers with fixed digit advances
  (no jitter, no shaping cost).
- NDI runs on its own thread with a raster surface at the configured resolution; the NDI SDK's
  clocked send paces the frame rate exactly.

### Stability engineering

- Per-frame exception containment: a renderer that throws is disabled for the session and the
  sink paints an unmissable error card instead of crashing; the show goes on.
- Settings are written atomically (temp file + rename) with a `.bak` generation; a corrupt file
  is quarantined and defaults load. The app always starts.
- Blackout (Space) is honoured before any pattern code runs — it cannot be broken by a pattern bug.
- Screen hot-plug re-syncs output windows; a vanished screen closes its window gracefully.
- Global `UnhandledException`/`UnobservedTaskException` handlers log to `patterns.log` beside the
  exe (portable) and attempt graceful continuation.

## 4. Feature plan (mapped to requirements)

| Requirement | Design |
|---|---|
| Multi-screen: duplicate / independent / span | Output modes over enumerated screens; independent mode gives each screen its own pattern config; span treats selected screens as one pixel canvas via union-rect viewports. |
| LED wall setting | Tile W×H px (free input + common panel presets), wall defined either as columns×rows or by target canvas (derives the grid), per-tile borders, tile numbering (row/col, linear, or serpentine data-run order), row/column indices, center cross, dimension readouts. |
| Video wall setting | Element = standard display resolution (presets + custom) landscape/portrait, C×R elements, optional bezel/gap px, numbering and alignment marks; canvas = element grid. |
| Projection blend | N projectors in a row (or column), native res per projector, overlap px, blend-zone hatching with centerline, selectable blend curve (linear/cosine/S-curve/gamma) drawn as ramps in the zones, per-projector hue-coded grids, 50%-grey double-stack check mode. |
| Time & date | Overlay layer on any pattern: 12/24 h, seconds, date formats, 9-position anchor, size, pill background. |
| Countdown | Target-time or duration; labels (Lunch/Dinner/Rehearsal/Doors/Show/custom); end behaviours (hold zero, flash, message); optional progress bar; drawn by the same overlay layer. |
| Pattern library up to 4K | Parametric patterns × resolution presets (720p→DCI 4K, portrait variants, common LED processor rasters); preset gallery with live-rendered thumbnails; built-in + user presets. |
| Motion setting | Moving bar (px/s or px/frame judder mode), bouncing box with FPS/frametime readout, frame-flash (drop detector), animated zone plate, scrolling grid, colour cycle. |
| Particle generator / mini studio | Pooled CPU sim (up to ~20k particles), emitter shape/rate/velocity/spread, gravity/wind/drag, size & alpha over life, shapes (circle/square/star/streak/logo sprite), brand palette; presets: snow, confetti, starfield, rain, bokeh, embers; parameters editable live = the "mini studio". |
| Sleek UI | Dark professional theme (Fluent + custom styles, Inter font), left nav rail, live preview center, parameter panel right, transport bar (OUTPUTS ON/OFF / IDENTIFY / BLACKOUT), keyboard shortcuts. |
| Brand colour schemes | Brand kit: primary/secondary/accent/background + logo; patterns and particles consume the palette; kits save/load as JSON for repeat clients. |
| User graphics & videos | Media pattern: images (PNG/JPEG/BMP/WebP) with fit modes; video via optional libVLC (loop, fit modes) composited through the engine (reaches outputs + NDI). |
| Company logo | Brand kit logo (PNG w/ alpha) usable as overlay watermark (position/scale/opacity) and as particle sprite. |
| NDI feeds | Sender with configurable name/resolution/rate rendering the program; feature-detected runtime; independent of physical outputs. |

## 5. Testing strategy

`Patterns.Core` never touches Avalonia, so the real renderer runs on CI against raster surfaces:

- **Pixel tests**: SMPTE/EBU bar values at sample points, grid line positions, LED tile border
  pixels, checkerboard phase, blend-zone widths, span viewport stitching (rendering the full
  canvas must equal rendering each screen viewport side by side).
- **Math tests**: LED wall derivation (canvas ⇄ grid), blend canvas width, countdown arithmetic
  across midnight, snapshot versioning, settings round-trip & corruption recovery, particle
  determinism for a fixed seed.
- **Interop tests**: NDI struct sizes/offsets on x64 asserted so a marshalling regression cannot
  silently corrupt frames.

## 6. Delivery

- `build/publish-win-x64.(sh|cmd)` → single-file, self-contained, portable `Patterns.exe`
  (no trimming — reflection-free start-up speed is fine and stability wins).
- GitHub Actions: restore, build, test on every push; portable exe artifact from a Windows runner.
- README: operator-focused quick start, hotkeys, NDI/VLC enablement, LED/blend recipes.

## 7. Out of scope for v1 (kept in mind by the architecture)

Audio test signals, NDI receive, DMX/Art-Net triggers, genlock, 10-bit output paths, multiple
simultaneous NDI senders (the sender abstraction already allows N), macOS/Linux builds (Avalonia
makes them near-free later).

## 8. Run mode — the seven-phase roadmap

The design review (published separately as *Patterns Run Mode*) settled a show-caller layer on top
of the switcher. It is being built in phases; each lands with tests, docs and a green CI run.

| Phase | What lands | Status |
| --- | --- | --- |
| 1 | One action layer (`ShowActions`) for the desk, keyboard, output windows, remote, schedule and recovery, journaled to `patterns.showlog.jsonl`; OUTPUTS ON/OFF naming; snapshot-level CUT; tolerant enum loading; looks and stingers with ids. | done |
| 2 | Content-target model (a joined canvas holds content of its own, keyed `a+b`); the wall — PGM/PVW miniatures per target at true shape, OWN / MON / ARM / OUTPUT, tally; aspect-locked panes following the selected target; scoped TAKE (un-armed targets keep their picture). | done |
| 3 | Cue stack: two lists of one model (caller's stack, clicker list — the old presenter steps migrate into it), typed actions with one spec table, a simulating validator with per-cue *Broken* (never a global arm gate), the Cues page with FIRE, looks with their own cut / fade, blackout as transport across a cue. | done |
| 4 | Run layout (LIVE strip, the wall beside the stack, transport row, the type scale); the executor with the one gate (armed, hold, blackout, executing, standby-id fence, 300 ms lockout, confirm window); history and journal; asynchronous settling; AirLabel; the schedule, part start times and plain F-keys held while armed; Enter / ↑ ↓ / Esc; STOP ALL; the sidecar keeps the caller's place and a relaunch restores it disarmed. | done |
| 5 | The CUE verbs, STOPALL and HELLO (origins by name); the control-state push and the compact `cuestack` STATE block; `/api/cues`, the `/api/state?since=` long-poll, `/pgm.jpg`, the client header on cue commands; the `/run` tablet page; Companion module 1.1.0; the pop-out Run window. | done |
| 6 | The shell: five groups on the rail (SHOW · PLAN · BUILD · SETUP · ADMIN) with a page strip over the layout and one page table pinned to the window's tabs; PREP · SHOW · RUN as the header mode selector; four pages re-cut (the Show panel without the transport, Looks with all the wall-clock automation, Screens with transitions and the EDIT SAFE default, Machine); the SHOW CONTROLS drawer — message, clock, countdown, audio volume behind SEND — and the AudioVolume verb a cue can use too; the PREP chip on the LIVE strip; docs, screenshots, Help. | done |
| 8 | Break music from Spotify: `SpotifyConfig` + `SpotifyItemConfig` on the show (off by default; the sign-in in a `patterns.spotify.json` sidecar, never in a show file); PKCE over a loopback redirect (`LoopbackCallback`, 127.0.0.1 only, three fixed ports); `SpotifyService`, a reconciling poll over the live model with a `Transport` seam so the suite runs offline, whose applied key advances only on success, whose first tick never pauses anybody's Spotify, and whose failures are sentences (`CommandFailure` alone reaches the cue rows — a rate limit never does); the shared `MusicLevel` duck rule and `AppServices.MusicDuckSource`; the four `Spotify*` verbs across cues (Soft when off or not connected, Hard only for a name that resolves to nothing), STATE (`music{…}`), the `MUSIC` / `SPOTIFY` protocol and Companion 1.2.0; the Audio-page and Show-panel blocks and the ♪ BREAK MUSIC chip on the LIVE strip; STOP ALL pauses it. | done |
| 9 | The stinger library splits into VOGs and stingers (schema 6; every older item migrates to a VOG with the same behaviour): one collection and one numbering, a per-item kind, and for a stinger an after-policy — back, hold for the operator's take (bounded by their TAKE, an optional hold limit and STOP ALL), GO the caller's next cue through the real gate (never a confirm on the caller's behalf), or a named look or cue — with any policy that cannot run putting the show back and journaling Failed; the music rule extended in Core (`MusicLevel`: the VOG duck as a step, the sting fade as an anchored ramp the file track and break music both follow, the player polling at 50 ms while it moves); a sting's clip dissolves in over the same fade; a kind-checked `VOG` / `STING` beside the untouched `STINGER`; `stingerKind` and `stingHold` on the wire; the STING HOLD banner, chip, phone row, tablet chip and Companion feedback; the recovery sidecar pinned to the pre-sting content and the settings saver deferred while a clip or a hold owns the screens. | done |
| 7 | Multiview tiles as content targets: the rig's pixel geometry on the snapshot (`RigGeometry`, `ShowSnapshot.Rig`, `SnapshotBus.Displays`) with a 1920×1080 (16:9) fallback; every `Program`/`Screen` tile a true miniature at its target's real shape with the wall's own labels and tally; a joined canvas addressable by its member key in a tile and in an NDI sender, and a member screen drawn as its slice of the canvas; a tile naming nothing or a ghost draws a slate instead of the program; `Rig` reduced to a wrapper over the Core maths so the wall, the outputs, `/mv.jpg` and an NDI sender agree; no identify badge inside a tile; `/mv.jpg?w=`. | done |

## 9. Round 9 — heard from the desk

Bugs first, then show-critical capability, then creative surface; one green commit per item.

| Item | What lands | Status |
| --- | --- | --- |
| Stop fade | A stopped sound or clip fades out and can never be heard again: every WASAPI stinger sound is a voice behind a sample-accurate `GainSampleProvider` (a 20 ms slew for live gain, a release that ramps to silence and ends the stream so the output closes itself); a voice is never reused — STOP and the next press release it, a fresh voice always opens, the sweep disposes what has gone silent; `StingerConfig.StopFadeMs` (50–1000, default 200); a retired libVLC clip fades its volume over the same stop fade and is then silenced three ways (mute, zero volume, no audio track) re-asserted every 50 ms — libVLC drops audio writes made before its audio output exists, which is how a clip stopped in its first moments came up at full volume a beat later under the next one — and lives only `AudioFade.RetireHoldMs` (the longer of the crossfade and the stop fade, plus 300 ms) instead of a flat four seconds; the `VoiceFactory` and `SourceFactory` seams run the whole path headless. | done |
| Ticker | The scroll phase wraps modulo the copy period (it wrapped modulo canvas + period, so every wrap snapped the train by the remainder — the jump every few seconds); `TickerLine` on the snapshot (`SnapshotBus` re-anchors it at the publish clock only when the speed changes, the sandbox keeps a line of its own) so a span, an NDI sender and a late-opened output draw one train from the snapshot alone; `ShowSnapshot.PublishedClock`; `MessageBackground` Auto / None / Solid / Fade with a strength, the fade darkest at the anchored edge; per-sink text-width and gradient caches. | done |

Deferred on purpose: MON persisted per show (runtime only for now); clearing `CutAtVersion` for a
sink that skipped the cut frame (a sink that renders every publish never sees the difference);
per-target overlays (the countdown, message and clock are
rig-wide and travel with looks — "countdown on Centre, branding on the sides" needs an overlay set
per content target, which the target model now makes possible); per-kind settle windows for
Requested cues (one 12 s number still covers the stream, the audio track, a stinger and break
music); an older build still silently drops a newer file's unknown blocks on save; the sting fade is
one show-wide number rather than per item; a held stinger shows whatever the decoder leaves on its
last frame, so stings meant to hold should be cut with a hold frame.

Noted while building the shell, for a later round: the playlist parts' start times are shown on the
Looks page but the parts themselves still belong to the program pattern's playlist (a playlist on a
screen's own pattern is edited on Media only); the header wraps its transport onto a second line
below about 1 300 px rather than shrinking the labels; RUN entered from PREP is a rehearsal — cues
run against held outputs and the LIVE strip says so — which is deliberate, not a guard to add.

