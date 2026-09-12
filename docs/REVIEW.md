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
