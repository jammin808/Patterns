# Pre-release review — findings and fixes

Before the first push to `main`-worthy state, the whole diff (~9,900 lines) went through a
high-effort code review (all Core/App sources plus tests, with call-path tracing and numeric
simulation of suspect loops). Thirteen findings came back; every one was addressed the same
session. Regression tests cover the behavioural ones (`tests/Patterns.Core.Tests/ReviewRegressionTests.cs`
and the headless suite).

## Broken features (fixed)

1. **Identify never rendered.** The identify deadline lived on `ShowState` behind `[JsonIgnore]`,
   and render sinks only ever see JSON-cloned snapshots — so the value was always `null` on the
   render path and no output ever drew its badge. *Fix:* the deadline is now carried by
   `ShowSnapshot`/`SnapshotBus` (runtime-only by construction, still never persisted). Regression
   test renders through the real bus. Also: badges now draw only on `Output` sinks — the preview
   used to be eligible and would have shown a meaningless "0".

2. **Single-instance guard never engaged.** The named mutex used `string.GetHashCode()`, which is
   randomized per process in .NET — two instances never computed the same name, so both kept
   autosaving and clobbered each other's settings. *Fix:* stable SHA-256-based folder key
   (case-insensitive), unit-tested.

3. **The scrolling message never crossed the screen.** The marquee loop marched copies rightward
   from the lead position, so text vanished mid-canvas and teleported back to the right edge
   (confirmed numerically: left ~54% of a 1080p canvas was never covered). *Fix:* copies now march
   leftward from the lead plus an incoming copy on the right; a sweep test asserts left-quarter
   coverage.

## Thread-safety / lifetime races (fixed)

4. **`ImageCache` disposed images other threads might still be drawing** (file-change reload and
   LRU eviction). *Fix:* replaced/evicted images are retired to a graveyard and disposed only
   after a 5-second hold — longer than any in-flight frame.

5. **Video frames could tear or be freed under a deferred GPU draw.** `DrawFrame` wrapped the
   decoder's buffer zero-copy into canvases that may not flush until end-of-frame, while the VLC
   thread kept writing into (or freeing) that buffer. *Fix:* every displayed frame becomes its own
   immutable `SKImage` (native-heap copy, no GC pressure); superseded frames retire on a
   2-second hold, so recorded draws always flush against live, immutable pixels.

## Correctness / consistency (fixed)

6. **Particles drifted apart across span seams.** Each sink integrated with its own frame-time
   deltas, so identically-seeded sims diverged (and their RNG streams with them). *Fix:*
   fixed-timestep integration (120 Hz) quantized against the shared show clock with a
   512-step-aligned baseline — every sink executes the identical step sequence, verified by a
   test advancing two sims at 60 Hz vs 24 Hz to bit-equal positions. Sinks stalled beyond ~17 s
   (e.g. a hidden preview) re-anchor rather than grinding through catch-up steps.

7. **Hot-plugged screens in Independent mode edited the program pattern.** The edit-target list
   gained the new screen but no per-screen assignment was created, so `ActivePattern` silently
   fell back to the program. *Fix:* screen-list rebuilds ensure assignments before rebuilding
   edit targets.

8. **NDI's "drop the DLL and re-enable" instruction could never work** — a failed runtime probe
   was memoized forever. *Fix:* re-enabling NDI clears a negative probe and rechecks the disk.

9. **Apply-style preset combos were one-shot per value.** Picking "HD 1080p" for one edit target
   and then again for another was a silent no-op (`Set` saw no change). *Fix:* the combos reset
   to placeholder after applying, so the same preset applies to any number of targets.

10. **A failed video open was cached as success-shaped.** Re-selecting the same file never
    retried. *Fix:* failures clear the active key so the next state change retries.

11. **Library thumbnails composited the logo watermark** over every preset despite the "pattern
    itself" intent. *Fix:* logo overlay disabled for thumbnails too.

## Cleanups (fixed)

12. **Dead branch** in the checkerboard's un-branded colour selection (unreachable by
    `Palette.Resolve`'s contract) — removed.

13. **Per-frame allocation on the particle hot path** — `Configure` built a ~20-field string key
    every frame per sink. *Fix:* configuration is gated by snapshot version + canvas size in
    `SinkState`; the key is only built when something actually changed.

## Outcome

- 92 tests green (86 core + 6 headless UI), portable publish verified.
- Accepted trade-offs, documented: a sink stalled >17 s re-anchors its particle field (visual
  jump on that sink only); retired video frames may outlive a closed source by ≤2 s (bounded,
  swept by the next source).

## Round 29 review — findings and fixes

A second full pass (the render core, the Core services, the App services, the view models and
views), with the suite run against every change: 982 core + 504 headless UI tests green. The
architecture questions of the round are answered in `docs/PLAN.md` §46; the findings are here.

### Broken behaviour (fixed)

1. **A look's transition was lost to the desk's chrome.** A write to any `[JsonIgnore]` property
   published a whole-show snapshot and claimed the next version — so a device streaming readings,
   or the 200 ms tally timer while a sting played, minted a version between a recall's publish and
   the sinks' next frame, and the recall's own fade or wipe became a plain switch. *Fix:* the change
   tracker tells runtime-only writes apart; they refresh the recovery record and the remotes and
   never publish.

2. **Typing a cue step's value lost focus every keystroke.** The value box's setter rebuilt the
   step rows, discarding the TextBox under the operator's hand — one character per click. *Fix:*
   rows refresh in place.

3. **Typing a screen label re-mounted the wall.** Each keystroke rebuilt every switcher tile and its
   two render pipelines. *Fix:* titles refresh in place.

4. **Preview tiles and screen layers drew at the wrong device scale.** The multiview's Preview
   tile and another target inside a layer box never took the tile's own scale, so hairlines
   aliased and the test card claimed a 1:1 read on a scaled picture. *Fix:* the scale is carried,
   as the Program and Screen tiles already did.

5. **ICS calendar feeds read in the machine's calendar.** `TryParseExact` with a null provider:
   on a Thai or Arabic Windows every event fell outside the 24-hour window and the ticker went
   blank. *Fix:* invariant.

6. **A cue sheet's one-second follow re-imported as a follow at once.** `IsYes("1")` ran before
   the duration parse. *Fix:* a number is a delay before a word is a yes.

7. **A migration that threw quarantined a readable settings file** and booted the save before it;
   a null media path was one trigger. *Fix:* the file read is the show; a failed upgrade is logged
   and tried again next start; the path and the extension helpers are null-safe.

8. **The validator's content-change rule missed presets**, so a video stinger could share a cue
   with `ScreenPreset`. *Fix:* the one table (`ActionSpec.ChangesContent`) is consulted.

9. **The support bundle blanked every input label's key** (the regex matched a bare `Key`).
   *Fix:* named secrets only.

10. **Library thumbnails carried the badge, the weather chip, the PiP and the lower third on air.**
    *Fix:* a thumbnail is the pattern alone (the logo was fixed in round 1; these were the rest).

11. **A slow DNS answer redirected OSC feedback or the beacon to a host since replaced.** *Fix:* an
    answer is thrown away unless it belongs to the socket that asked.

12. **A serial device disabled or edited faulted a task** (its port closed under the read) and the
    health line counted an "unobserved exception" some seconds later. *Fix:* a cancelled read is
    the closing, not a fault.

13. **A MIDI surface's tick could open its port after Dispose had closed it**, leaking a
    single-client winmm port until the process ended. *Fix:* Dispose waits for the tick in flight;
    overlapping ticks are guarded.

14. **An audio output that would not open left the file locked.** *Fix:* the half-built chain is
    disposed with the device.

15. **The show state was read off the UI thread** by the support bundle and the management
    check-in. *Fix:* the words are gathered on the UI thread; the file and network work stays off it.

### Cost (fixed)

16. Whole-show JSON clone per publish, twice under EDIT SAFE → sections copied only when they moved
    (`docs/PLAN.md` §46.2): 10.5 ms → 0.25 ms per publish on a corporate show.
17. Two publishes per pointer move on a drag → one; every multi-property gesture → one edit.
18. The image cache stat'ed its file on every frame of every sink → trusted for half a second.
19. Retired live frames held two seconds by time alone (1080p60 ≈ 1 GB per source) → one pool for
    every source, 400 ms and at most twelve frames, the number the Machine page prints.
20. The presets folder was listed on the UI thread every second and after every action → listed at
    most every two seconds and at once after a save.
21. The NDI pickers' list was cleared every three seconds → rewritten only when the sources changed.
22. A string built per frame for the stage name, the trim and blend keys, and the lower third's date
    and time on every text element → none.
23. The autosave's disk write on the UI thread → a worker, in order; the clock timer at 4 Hz → on
    the second.
24. The change tracker held every item ever added to a list → weak.

### Cleanups

25. Dead: `ShowLog.Recorded`, `LayerConfig.HasSource`, `SinkState.Particles`, `FpsMeter.Reset`,
    `BeaconService.LastSeenUtc`, `ManagementService.CommandsRun`, `TakeoverResult.ScreensWereLive`,
    six unbound view-model members. Duplicates folded: three copies of the retired-frame list, two
    `Mb` formatters, five on/off word tables (the validator's gate and the executor's table now read
    the same one).

26. **A test that only passed warm.** `DeskPollTests` asserted the desk-tick line was empty right
    after boot and counted six ticks from zero — true only while the boot beat the desk's own
    one-second timer, so a cold boot (the first test of a run; new test classes moved it there)
    failed it. It now counts from the ticks the timer already took.

### Recommended, not done (taken in round 30)

- Coalescing publishes across a dispatcher frame (the design and the reason it waits: §46.2) —
  still waits (§47.6).
- The rest of this list was taken in round 30, below.

## Round 30 review — the recommendations, taken

The round-29 list, in the order `docs/PLAN.md` §46.5 gave; §47 says how. The suite ran against
every change: 1000 core + 511 headless UI tests green.

### Done

1. **The Library** — a catalogue reconciled in place and one thumbnail queue: one file picked draws
   one thumbnail (it drew eighty-six), the other tiles keep their instances and pictures, two
   builds in a row are one pass, the thumbnails draw over the published snapshot and share what
   they do not write.
2. **The CPU rasters** — a frame per size per sink for the fractal and reactive patterns and the
   lower third's fractal element: a multiview, a screen layer or a dissolve no longer disposes and
   reallocates a frame twice per frame.
3. **The multiview's words** — badges, captions and the air state once per snapshot, not per tile
   per frame.
4. **The run list's rows** — reconciled by cue in place; the caller keeps the selection and the
   scroll position across a publish.
5. **A PDF page outside the window** — rendered on a worker and published when it lands; the desk's
   thread never waits on PDFium's gate for a page.
6. **`ShowActions` by area** — ten partials, no verb changed.
7. **The lower-thirds designer** — its edits in a Core class with no desk in it, tested there.
8. **The rig editor** — the placements, the planned screens, the gaps and the feeds' screens in a
   service; a screen's role and name as verbs the page, a cue, the wire and the journal share.

### Found on the way (fixed)

9. **A fresh show's first publish differed from its second for nothing** — the playlist's first
   part arrived on the first poll rather than at creation. Normalised at creation.
10. **A worker asking for `Dispatcher.UIThread` between two headless tests** minted a dispatcher
    with no run loop for the next test — a `PlatformNotSupportedException` from `PushFrame` in an
    unrelated test, once in a suite, introduced by the thumbnail worker and confirmed absent on
    the round-28 build. The queue holds the dispatcher it was made on, posts nothing once disposed,
    and its `Dispose` waits for the tile in hand.

### Measured, and left

- The shader uniforms rebuilt per frame: a few hundred bytes of managed allocation per frame per
  sink, whose values change every frame anyway (§47.6).
- The label box's per-keystroke write stays a direct edit; the verb is for the deliberate rename.
- `PdfDeckSource.Open` still reads the page count and the first page's size under the PDFium gate
  on the caller's thread: two metadata calls, bounded by one page render of another deck.

## Round 31 review — the peel finished, a twin, the room's other boxes

The round asked for three things and a judgement; `docs/PLAN.md` §48 says how. The suite ran
against every change: 1,022 core + 516 headless UI tests green.

### Done

1. **The peel** — the Assistant, break-music and Audio pages are page objects reached as
   `Assistant.X`, `Music.X`, `Audio.X`; the desk keeps one hook per page; the Spotify rules moved
   to Core with tests; the partial left behind is named for what it holds.
2. **The twin** — a second Patterns kept in step over a plain newline link: the whole show, the
   air record, then every section the publish names dirty (the bus now says which) and a beat a
   second; the standby lands each section in place with its outputs held closed and takes the
   show over by press, by `TWIN TAKEOVER`, or by itself, through the one restore path a watchdog
   restart uses. The machine's own sections never travel; a main saying goodbye is never taken
   from. The WATCHDOG tile, the super-check, the health line, `TWIN STATUS`, the beacon's twin
   port, a help topic and REMOTE.md carry it.
3. **Endpoints** — projectors (PJLink, with authentication and the reply words), Disguise d3
   (OSC), Pixera (JSON-RPC with handle look-ups), any OSC box and any web API (a new HTTP link)
   as devices of the Interactive area with a profile each; presets on the page, the profile on
   the card and in STATE, the words in the cue editor's hint and the assistant's catalogue;
   `docs/ENDPOINTS.md` with the honest notes per protocol.
4. **The assistant** — its brief lists the room's boxes by the operator's names with each one's
   words (never an address or a password) and the twin's role; the assessment of where the AI
   goes next, and where it deliberately does not, is §48.5.

### Found on the way (fixed)

5. **A proposal's new lower-third design** did not adopt the show's default style the way the
   designer's own NEW does — it does now, through the designer.
6. **`TryRecover` was one method** carrying the whole restore; the restore is its own method
   now, so the twin's takeover and a watchdog restart cannot drift apart.
7. **The twin's beat and flush rode dispatcher timers** — under the headless suite's load a
   one-second timer went unfired for four seconds while a 200 ms one fired; the beat now comes
   from a worker that waits the second and asks the UI thread to tick (a hung desk still stops
   beating, which is the point), the flush the same way, and the main beats once as a standby
   joins. The twin tests accept connections until one says JOIN, because a dial the standby
   cut short itself sits in the listener's backlog saying nothing.

### Measured, and left

- **A thread as a twin** — no: a thread shares the crash and the graphics device; the watchdog
  already covers the hung UI thread; the process is the unit of resilience (§48.2).
- **A separate endpoints service** — no: the Interactive area already had every seam; a profile
  per device was the whole of the missing piece, and the verb, the wire, OSC, Companion and the
  assistant came with it unchanged.
- **Encryption on the twin link** — left: the same LAN trust as the control wire and the beacon,
  with an optional key against an accidental join; said on the page and in the plan.
- **Companion actions for the twin** — left: `TWIN TAKEOVER` and `TWIN STANDBY` are one line
  each on the generic TCP module, and a takeover is a press the operator makes looking at the
  desk.

## Round 32 review — pages, a standby that runs itself, hot-plug, warp and black, the caller's pad

The round asked five questions; `docs/PLAN.md` §49 says how. The suite ran against every
change: 1,037 core + 528 headless UI tests green.

### Done

1. **The peel** — `ScreensPage`, `MediaPage` and `ShowPage` as page objects reached as
   `Screens.X`, `Media.X`, `Show.X`; the desk's partials from 7,935 to 6,077 lines; the desk
   keeps only what every page needs.
2. **The twin as a second process** — the main starts, restarts, adopts and ends its own standby
   in a folder beside its own (`--home`, `--standby-of`, `--key`); a standby that took the show
   marks it on disk; a returning main holds its outputs while that process lives; `TWIN
   TAKEBACK` hands the show back with the edits made meanwhile, and the standby follows again.
3. **Hot-plug** — a display that re-indexed keeps its screen; one unplugged leaves its screen
   waiting with everything kept and the alert on the status line, the journal, the health line,
   the super-check and the brief; its own display back is adopted and turned on; a stranger is
   offered as a substitute (the mode forced first when it must be) or as its own screen.
4. **Warp and blend** — the research written down with the 2×2's middle explained; edge bends
   through a Coons patch, black-level matching by the pedestal rule, arrange as a blend grid.
5. **The caller's pad and notes** — the pad, a note on any cue in the show, the row's menu by
   right-click with the ✎ chip for touch; the evaluation is §49.5.

### Found on the way (fixed)

6. **A display unplugged on the left orphaned every screen to its right** — display ids embed
   the index; the rig now remembers each screen's display and re-identifies by name, size and
   place.
7. **A hot-plug pass published mid-move** — each rename republished, and the desk's reconcile
   on that publish added a placement for a display the pass was about to give to a waiting
   screen; the pass is one quiet edit with one publish, losses before renames, a swapped screen
   moved aside.
8. **A standby that took over stopped dialling** — the main could never take the show back
   over the link; it dials again after a takeover, saying so in its join.
9. **A dial that failed logged once a second** — one line for the first failure, then one in
   thirty.
10. **A restart of the main ended its standby** — RESTART and UPDATE APPLY keep the standby for
    the desk that comes back, which adopts it.

### Measured, and left

- **A full mesh warp** — left: the patch covers curved screens and lens bows; a dome or a set
  piece needs a grid of control points and a draggable grid over the preview, which is the next
  piece of work, not this round's.
- **Camera calibration** — left, with the pieces named: the capture inputs, the pattern
  renderer and the mesh exist; the solver is the work.
- **Feathered pedestal edges** — left on purpose: the black floor steps at the band's edge, so a
  hard step is right; a feather only hides a misaligned zone.
- **Keyboard access to the cue row's menu** — left: the standby row's note is one right-click
  or one ✎ away; a keyboard shortcut would collide with the caller's keys (Enter, arrows, Space).
- **Wire verbs for the substitute** — left: a hot-plug offer is a walk-up decision made looking
  at the screen; the status line, the health line and the brief carry the words.
