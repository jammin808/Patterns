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

## Round 33 review — the mesh, the camera, and a machine that keeps quiet

The round asked for the two projection pieces left from round 32 and for a machine that never
interrupts the show; `docs/PLAN.md` §50 says how. The suite ran against every change: 1,048
core + 536 headless UI tests green.

### Done

1. **The mesh** — `WarpGrid`: a lattice of 3×3 to 17×17 nodes per output, Coons patches with
   Catmull-Rom tangents per cell, the bends folded into the edge nodes, the density changed with
   the shape kept; the editor on the Screens page, the lattice shown on the projector with the
   picked node lit, arrow-key nudges, Reset mesh.
2. **Camera calibration** — Gray-code structured light out of every projector, the camera in
   (NDI, or photographs by a plan), each camera pixel decoded to the projector pixel that lit
   it, a homography per projector, the canvas, a 9×9 mesh from the local lookups, a blend mask
   that sums to one across every overlap; APPLY and UNDO, DEMO against a room that is not there,
   the report, `CALIBRATE …` on the wire, the help topic.
3. **The show lock** — notifications, system sounds, other apps' audio (the break-music player
   allowed by name), the accessibility shortcuts, sleep and the screensaver, the Windows key —
   on with the outputs and off with them, put back on release and on exit, restored from a
   receipt after a crash; the pending restart read and said; the foreground watched; the
   administrator's script for Windows Update; `docs/SHOW-MACHINE.md`.

### Found on the way (fixed)

4. **The structured light on a blended rig lit nothing** — a member of a joined canvas gets a
   viewport keyed by the canvas, so the pattern lookup by screen id found no pattern; the
   viewport now carries the output's own id and the overlay is keyed on it. Found by the test
   that applies a solution (which joins the two projectors) and then reads the viewports.
5. **The solver's lookup biased the nodes** — a 5×5 mean around the camera pixel pulled edge
   nodes up to 13 px inward; bilinear between four camera pixels with pixel centres at +0.5,
   falling back to the fit, brought them within six.
6. **A projector's light was cut before its blend faded** — the canvas's pixel size came from
   the widest share, so a narrower projector's share ran past its raster; it comes from the
   smallest share now.
7. **The corner check named corners a projector does reach** — the auto canvas is the box
   around keystoned light, so a corner sits a few pixels outside it; the check has a tolerance
   of 3% of the canvas.
8. **A strong machine's super-check went amber** — an unreported show lock read as a failing
   row; the facts carry the report or nothing, and nothing makes no row.
9. **The lock's minute checks stalled against a clock that stepped back** — a clock earlier
   than the last check read as "not yet"; a backwards clock counts as elapsed.
10. **The twin launcher's test failed on CI and nowhere else** — it named `/show/twin-standby`
    as the standby's folder, which the launcher makes before it starts the process; a runner
    that is not root cannot make a folder at the file system's root, so nothing started and the
    suite went red on GitHub while passing on a root container. Reproduced as an unprivileged
    user; the test uses a temp folder. Found with it: a start that failed said only "starting
    in 2 s" on the Machine page — it now names the reason (a read-only show drive, a path the
    account may not write) until a try succeeds.

### Measured, and left

- **Colour matching by measurement** — left: the trims stay by eye on the grey check; the
  camera's colour is not calibrated and a phone's is not trustworthy.
- **A lens model** — left: a homography plus the local nodes covers a bowing lens and a curved
  screen; a fisheye or a mirror rig is a different solver.
- **More than one camera position** — left: a wall the camera cannot see whole is two runs; a
  stitched multi-camera solve is a round of its own.
- **Teams' window on the desk** — cannot be prevented from here without ending Teams; its audio
  is muted, the foreground change is logged, and the doc says to quit it before doors.
- **Windows Update's restart** — a policy, and an administrator's: the script sets it once; the
  lock reads and says whether a restart is pending rather than pretending to hold it.
- **The mesh on NDI and the preview** — never, on purpose: the outputs alone warp, so the picture
  the desk and the stream see is the picture, not the projector's correction.
- **A per-edge start position** — left as before; the mask from a calibration makes it moot on
  a calibrated rig.

## Round 34 review — the twin's ownership hardened

A review of the branch found the twin's remaining edge cases reachable from a live show and
asked for one invariant: at any instant, exactly one Patterns process can possess output
authority. `docs/PLAN.md` §51 says how. The suite ran against every change: 1,049 core +
543 headless UI tests green.

### Done

1. **The key** — a main with no key is given one before its port opens and says so; every join
   is checked against it, whatever it claims. A keyless main used to hand any machine on the
   network the whole mirrored show (secrets among it) and let it close the main's outputs.
2. **Fail closed** — the takeover marks itself on disk first and ends the hung main second,
   confirming it gone; either failing, it is refused with the reason, the marker cleared and the
   hold kept; TAKE OVER ANYWAY / `TWIN TAKEOVER FORCE` override by hand only; a refused takeover
   by itself is tried again after ten seconds.
3. **The standby that dies with the show** — the main lands what it sent and puts the show back
   on itself, rather than releasing its hold and waiting for a press.
4. **The wall switch** — a takeover cue and a take-back cue per machine, fired once the show is
   on; taking over by itself from another machine needs the takeover cue, and the status line
   says so when it has none.

### Found on the way (fixed)

5. **"Ended" on a timeout** — the process probe returned true after `Kill` whatever
   `WaitForExit(5000)` said, so a process still on its way out was reported as ended and the
   screens opened on the strength of it; it returns what the wait returns. The start-up
   reclaim of orphaned windows shares the fix.
6. **The held show thrown away before the marker could use it** — a local standby's socket
   closes before its marker reads dead, and the link-loss path dropped the show and air it had
   sent; they are kept while the marker holds.
7. **A cue by id only** — `CueFire` found a cue by its id alone, so a cue named in a setting or
   on the wire by its number or name was "no cue"; it resolves all three, as CUE STANDBY did.
8. **The page and the help called the key optional** — they say what happens now.
9. **A versions test that failed under load** — it picked the oldest kept version as "the show
   as it was named A", which held only if the autosave had not written the file before the
   test's own saves; on a loaded machine (the Core suite running alongside) the autosave landed
   first and an older, unnamed version sat behind it. It picks the newest timed version, which
   is that show whatever the autosave did; verified three times under the same load.

### Measured, and left

- **A cut cable with the switch cue set** — the standby takes the wall and the main keeps its
  outputs live behind a switcher that no longer shows them; the room follows the switch, which
  is the intended resolution, and the operators see it on both status lines once the link is
  back. A witness on a third machine is the next step, not this round's.
- **The protocol in the clear** — newline JSON over TCP on a LAN, as the remote control is; the
  key is now mandatory for a main, and a challenge on the join (an HMAC of a nonce with the key)
  would cost little if a show network is ever not trusted.
- **Two competing standbys** — the main holds for one; two that both took over would each mark
  their own folder and the room would show the switch's choice. Not tested this round.
- **A restart of the main mid-takeover** — the marker path covers a main that comes back after;
  one that comes back during (between the marker and the kill) reads the marker and holds, which
  is right; not tested as a race.

## Round 35 review — Round A of the nodes: the launch, the caller, the stage

*The brief: "Implement in the order you recommend" over `docs/NODES.md`. The order was A — the
node kernel, the caller node, the stage timer and messages, the NODES rail — then B, C, D.*

### Done

- `--node caller|timer|arcade`: `AppServices.Profile`, outputs held by the node's own sentence,
  the heavy engines and the default sandbox skipped, the rail and lazy pages filtered, the beacon
  carrying the kind and the ports. `docs/PLAN.md` §53.1.
- The beacon's peers kept, `NodeRegistry` and `NodeCard` (Core, pure), the NODES rail item above
  STREAM with its word and hue, the Nodes page with LINK / OPEN and the plan offers, `NODES` on
  the wire. §53.2.
- The caller over the twin's link: a third join kind, the desk hosting callers with the twin off
  (its own key, its listener, its beacon's port), the show mirrored, the caller's stacks back
  with echo cut both ways, LIVE once a second, ACT forwarded with `OriginKind.Caller`, PLAN
  offered once and diffed, APPLY with a version kept first, DISMISS, UNLINK. §53.3.
- The stage: `StageConfig`, `StageTimer` on the countdown's clock, PAUSE / RESUME / ADD / MINUS /
  FLASH, messages to the speaker and the crew with receipts, `/stage` and `/timer`, `/api/stage`
  long-poll and ACK, the cue actions, the sender named. §53.4.
- Help topics "nodes" and "stage"; REMOTE.md rows; README bullets; `docs/NODES.md` marked.

### Found on the way (fixed)

1. **LIVE, ACT and PLAN unparsed** — the wire's word table stopped at the standby's words; both
   peers dropped the caller's. A round-trip test over every `TwinWord` now guards the table.
2. **Every caller cut within a millisecond of linking** — a status notification from the accept
   thread threw "Call from invalid thread", swallowed by the reader loop's catch as a peer going
   away. Posted to the UI thread; the loop logs any fault on a line that is not a socket closing,
   so a dropped peer is never dropped silently again.
3. **Empty stacks in a plan**, **a second list of one role folded onto the first**, **two help
   topics out of show order** — §53.5.

### Measured, and left

- **The caller's key by hand** — the desk's key is typed on the caller's Machine page; a beacon
  cannot carry it. A pairing code shown on the desk's Nodes page (the key behind a six-digit
  code, once, for a minute) is the next step for the venue.
- **One section, whole** — a caller's edit sends the whole Stacks section, as every mirrored
  section travels; two callers editing at once resolve last-writer-wins per section on the desk.
  Cue-level deltas would be the change if a show ever has two callers typing at once.
- **The pause is the countdown's armed time moved** — a paused timer survives a desk restart as
  its remaining seconds and its paused flag, which the countdown's recovery puts back; it has not
  been tested across a restart this round.
- **The LIVE word once a second** — for a caller on a phone hotspot that is one small datagram;
  it was not throttled further.
- **The stage pages against the named references** — the stage timer and the messaging platform
  this was to be measured against sit as local projects the build machine could not reach; what
  is built is the professional shape as described. When they are reachable, the comparison is
  the first thing to do.

## Round 36 review — Round B of the nodes: the arcade

*The brief's second row: the arcade node — the engine, the games, the pads, the picture, the verbs.*

### Done

- `Patterns.Core.Arcade`: the engine (a fixed 120 Hz step with an accumulator and a cap,
  interpolated rendering, input at the step, seeded and recorded matches with a replay, no
  allocation in a step or a frame), Pong, Snake and Breakout as pure state machines, the pads,
  the board. `docs/PLAN.md` §54.1–54.2.
- `ArcadeService`: the loop on its own thread paced to the picture's rate, triple-buffered
  frames, the pads merged (keyboard, wire, phone, XInput), NDI out through `NdiFrameSender`, the
  board as a file; `ArcadeSurface` and the Arcade page; `/pad` and `/api/arcade`; the ARCADE verbs
  and cue actions; a desk that sends them to the arcade nodes it hears and runs them itself with
  none heard. §54.3–54.4.
- Help topic "arcade"; REMOTE.md rows; README bullet; `docs/NODES.md` marked.

### Found on the way (fixed)

1. **1/60 s was one step, not two** — the accumulator compared against a float `1/120` that
   rounds up; a hair of slack in the comparison, and a test that pins two steps per 60 Hz frame.
2. **A node's card had no wire** — the desk could see an arcade and not speak to it; the beacon's
   wire port is on the card now.

### Measured, and left

- **Silent games** — no sound this round; the node's own device is the plan (§54.6).
- **The frame ring lane** — NDI on the same machine costs an encode and a decode the ring would
  not; the ring source on the desk is a day's work for the day a hub shares the show machine.
- **The pad over HTTP** — a POST per press and release, 5–20 ms on a LAN; a WebSocket would halve
  it and was not needed for Pong at a party.
- **Determinism across machines** — the replay test holds on one build and one machine (float
  maths, one platform); across CPUs it is expected and not proven this round.

## Round 37 review — Round C of the nodes: audience play

*The brief's third row: the room out front — joining, questions, results on the wall, messages
back, moderation, draughts and the path.*

### Done

- `Patterns.Core.Play`: the room (join, five kinds of question, answers and results, a quiz's
  speed points and clock, the queue through the list, the assistant and the host, messages to
  the room, a group or a phone, reset, export), draughts, the path with its file, the QR encoder,
  the wall's renderer. `docs/PLAN.md` §55.1–55.2.
- `PlayService`: the room under a lock, the phones' API, the host's verbs, the wall on the
  arcade's lane, the story file and the export in the node's folder, the queue's second look
  through the desk's assistant over the wire. `/play`, `/host`, `/api/play/*`, the feed. The
  Arcade page's AUDIENCE block. PLAY verbs on the wire, in cues, from the desk to the hub.
  `ASSISTANT ASK` / `MODERATE` on the desk's wire. §55.3–55.4.
- Help topic "audience"; REMOTE.md rows; README bullet; `docs/NODES.md` marked.

### Found on the way (fixed)

1. **A settings part of a question line was read as an option** — "correct=2 time=15" is one
   part between the bars; it holds only key=value words now, and a quiz without a right answer
   is refused with the line's shape.
2. **The story's file did not read back** — its JSON was written with lower-case names and read
   with upper; the reader is a plain shape now and the names match.

### Measured, and left

- **The vote over a long-poll** — two seconds at most between a press and the wall; a WebSocket
  would make it live and was not needed for a fork every few minutes.
- **Determinism of the queue's assistant** — the word it gives is the model's; a doubtful or an
  unusable answer waits for the host, which is the fence the doc asked for.
- **A few hundred phones** — the server is the desk's accept loop, one task per request, the
  state a long-poll; not load-tested this round beyond two phones in a test.
- **The audience network** — a VLAN apart from the show LAN and no captive portal are the
  venue's to make; the page and the help say so.

## Round 38 review — Round D of the nodes: rig day, gamified

*The brief's last row: gamification where it earns its place, with limits.*

### Done

- `Patterns.Core.RigDay`: the show-ready score, the alignment game, Blend Quest, the on-time
  streak — pure. `docs/PLAN.md` §56.1–56.3.
- `RigDayService` behind one switch (`Install.RigDayGames`; the Machine page; `RIGDAY ON` /
  `OFF`): the facts every two seconds, the bar on the health line, the game with the solver's
  targets ringed on the projector's lattice and the keys on the Screens page, the quest's words,
  the streak's chip on the Run surface. `RIGDAY STATUS`, `ALIGN …` on the wire; the kinds are the
  desk's alone, never a cue's.
- Help topic "rig-day"; REMOTE.md rows; README bullet; `docs/NODES.md` marked.

### Found on the way (fixed)

1. **A nudge that set the point instead of moving it** — `WarpGrid.Moved` sets an offset; the
   game's nudges are relative (`Nudged`), and `RigEditor.NudgeMeshPoint` is the relative edit.
2. **A 2×2's diagonals as joins** — two projectors that touch only at a corner are the boss's;
   a join shares an edge.

### Measured, and left

- **Quiet in a show** — the games read facts every two seconds while on; nothing of them runs
  while off, which is the default and one switch away.
- **The chime** — the bar says it fills; a sound waits for a sound path that is not the show's.
- **The streak's judgement** — within the running order's thirty-second drift; a caller who
  wants a tighter measure has the timing line itself.

## Round 39 review — the hardening round

*A re-review of rounds 35–38 asked for five things before another feature: the audience off the
control socket, budgets and a load test, the twin's fences closed, the snapshot's collections
frozen, the node kernel built. This review grows as each lands.*

### H1 — the audience listener of its own

- **Done.** The listener, the route table both ways, the budgets, the signalled long-poll, the
  assistant's wire switch, the pages, the verbs, the tests, the load test. `docs/PLAN.md` §57.1.
- **Found on the way:** the load test itself — the first version woke the phones on the
  question's *add* rather than its *open*, because an add moves the room's revision too; the
  test writes the question first. A real page does the same (it renders whatever the room has).
- **Measured, and left:** two hundred phones from one address in four seconds on the build
  machine; a thousand over a venue's Wi-Fi is the venue's to measure. The per-address budgets
  assume phones on distinct addresses — a room behind one NAT needs them raised on the Remote
  page (`AudienceBudget` is a record; the seats are the one setting persisted).

### H2 — the twin's fences closed

- **Done.** The three-answer `ProcessSight`, the probe that tells gone from unreadable, the
  four fence sites (the twin takeover on this machine, the start-up takeover, the marker's
  hold, the ownership read), the wall switch before the outputs and the refusal of an automatic
  takeover it cannot make, the tests. `docs/PLAN.md` §57.2.
- **A rule, written down:** *a process that cannot be read is a fence, not an absence.* Every
  fence used to collapse "I could not see it" into "it is gone"; a hold kept a little long costs
  a press, a hold dropped costs two desks on one set of screens.
- **Changed on purpose:** a start-up takeover that finds an owner it cannot end now leaves the
  ownership record in place (it used to clear it) — the next start must find the hung owner
  again rather than open a second set behind it. The ask, which was ours, is still cleared.
- **The order of the wall switch:** before the outputs, on takeover, costs the room a moment of
  no signal from this machine's input (the outputs open in the same call, so it is the window's
  open time); after, it would cost a takeover the room cannot see. On take-back the order is the
  other way round for the same reason — the standby's picture is up until the switch has moved.
- **Left:** a cue that *reports* fired but whose endpoint verb fails later (an HTTP verb sent
  and refused) is still "fired" here; the endpoint's own health line says so. Making the wall
  switch wait for the switcher's answer is an endpoint round, not a twin one.

### H3 — the published snapshot's lists frozen

- **Done.** `ShowCollection<T>`, the freeze in the publish walk, every list in the show moved
  over, the tests. `docs/PLAN.md` §57.3.
- **Found on the way:** the type walk found five lists the file-by-file pass had missed — the
  lower-third model lives outside `Model/`. The walk stays as a test, so a section added later
  with a `List<T>` fails before it ships.
- **Kept:** `LookData` (a look's capture, made and consumed in one call) keeps a plain
  `ObservableCollection`; it is never published.
- **A cost, measured at nothing:** the guard is one boolean read per write on a list that is
  never frozen; the live show's lists pay it on every edit and no publish, thumbnail or sink
  writes a list at all.

### H4 — the kernel, and the capabilities the desk provides

- **Done.** `ServiceKernel` as the composition root's first stage; the assistant client, the
  beacon, the nodes registry and the arcade written against it; the slots (`Air`, `Link`,
  `Notifier`, `Facts`) with null objects; the contracts (`ITwinHost`, `IWireHost`,
  `IStageHost`, `IPlayHost`) the desk-facing services take instead of the desk; the tests.
  `docs/PLAN.md` §57.4.
- **Found on the way:** the assistant client was reading eleven desk services to write its
  brief (`Gather`). That is the desk's knowledge, not the client's — it moved to the desk
  (`GatherFacts`) and the client reads it through the kernel's slot; a node's assistant, when
  one asks, now says truthfully that nothing is on air.
- **Honest about the size of the step:** this is the type boundary and the composition order,
  not a node built from the kernel alone. Every role still constructs the desk; what changed is
  that a kernel service *cannot* reach past the kernel, and a desk-facing service's reach is a
  contract the compiler reads. The critique's "growing god" is now a façade over a kernel and a
  set of contracts, and the next kernel-only service costs one constructor.
- **Left:** `CommandRouter` on the desk (the dispatcher is the desk's by nature); `MainViewModel`
  and the pages assume the whole desk; the kernel's beacon owns a `DispatcherTimer` (the
  kernel is UI-free in what it needs, not yet in what it holds).

### H5 — no worker mints the UI dispatcher

- **Done.** `UiThread`, the captured dispatcher every service posts through; the audience
  handler throwing on cancellation after its wait. `docs/PLAN.md` §57.5.
- **Found by the build machine:** the H1 push failed there on an unrelated test — the between-
  tests dispatcher mint from round 30, back through the audience port's long-polls. A rule
  written in a review and kept by nobody is not a rule; it is one class now, and the grep for
  `Dispatcher.UIThread` in the services is the audit (two files keep it on purpose: the fault
  handler installed at start, and the thumbnail queue that already captures its own).
- **Left:** the view models and the lazy page still post through the static from the UI thread,
  where it is the right thing; a worker added there would have the same problem, and the same
  answer.

### H6 — the audience port's parser bounded, timed and fuzzed

- **Done.** `HttpLimits`, `HttpHead`, the byte-level bounded read with a clock on the head and
  on the body, the tests. `docs/PLAN.md` §57.6.
- **Found by the fuzz, before the fuzz:** the body read as characters against a length in
  bytes — a join with an accent in the name hung until the browser gave up. Not a fuzz finding
  strictly; the first thing writing the fuzz made obvious. The load test's two hundred phones
  were all called "Phone N".
- **Answered, not swallowed:** the parser used to cap a body silently at 4 KB and read on; now
  a body past the limit is a 413 with the number, and a head past its bytes a 431. A phone's
  page never sends either; a thing that does is not a phone.
- **Then the lines:** the TCP wire, the twin link's first line and the nodes' replies were the
  same shape of gap, and `BoundedLineReader` closes all three — the twin's ceiling rises from
  a JOIN's size to a show's once the key is right, which is the right order for a fence.
- **Left:** a slow client on the control port holds a slot for ten seconds rather than five;
  the twin link's lines after the key are bounded at sixty-four megabytes, which is a bound
  against a broken peer, not a hostile one — a hostile one with the key is the operator's
  problem. The Spotify loopback (one localhost request in a five-second window) keeps its
  reader.
- **A flake taken while here:** the H5 suite on the build here failed one Spotify cue test once
  — a cue GOne at the test's clock (a day in 2026) settled to Done by the desk's own one-second
  poll reading the wall's clock, whenever that poll happened to land inside the test's
  `RunJobs`. `CueStackService.NowUtc` is a clock now, as the Spotify service's already was, and
  the rig sets both. The final run of the round failed the dashboard test once the same way —
  the desk's live sampler read this machine over the numbers the test feeds in, between the
  poll and the page's render; `SystemMetricsService.Live` existed for exactly that and the
  test now switches it off.

### Considered and left, this round

- **`PreviewSource` on the program snapshot** (the critique's medium). The program snapshot
  carries a window onto the live preview so a multiview's PVW tile can draw what the operator
  is editing; the tile is the one reader. It is an intentional seam and it stays one: the clean
  alternative is a sink that composes two snapshots — the program's and the preview's — which
  is a render-pipeline change (every tile that can show the preview takes a second input), not
  a hardening one. Noted for a render round; nothing in this round made it worse.
- **`ShowState` as one broad document, `ShowActions` as partials** (the critique's low). Agreed,
  and agreed low: the schema version already protects migration, the one action vocabulary is a
  strength the wire, the cues, Companion and the assistant all lean on, and a split by domain is
  a rewrite's worth of churn for no behaviour. The kernel (H4) is the cut that mattered — the
  services' reach — and the next one, when a section grows past its page, is a sub-document
  root under the same JSON.
- **The Companion wire's slow client** holds a slot for ten seconds (the control port's
  head seconds); the audience port's is five. The control port is the production network's.

## Round 40 review — the next steps

*The hardening round's three next steps, taken under the platform charter (`docs/NODES.md`
§13): broad edge, narrow core.*

### 40.1 — the preview seam retired

- **Done.** `RenderContext.Preview`, handed in by every sink that has a bus; the snapshot's
  `PreviewSource` gone; the engine, the words and the tally reading the frame's.
  `docs/PLAN.md` §58.1.
- **What it cost:** one parameter through the tally's five readers and the NDI frame's render,
  and a `null` in the tests that never had a preview. Nothing drew differently.
- **What it bought:** the last runtime reach out of a published snapshot is closed; a snapshot
  can now be handed to another process (a node, a recorder) with nothing in it that points
  back at the desk.

### 40.2 — rig day's moments

- **Done.** `Celebration` and `CelebrationTrack` (pure), the service's hooks, the sweep over the
  lattice, the chip in the words and the status. `docs/PLAN.md` §58.2.
- **Where it draws, and where it does not:** over the lattice, which is up only while somebody is
  aligning that projector. The show-ready bar filling with the outputs live and a room watching
  draws nothing on the wall — a celebration on the programme is the one thing the games must
  never do. The desk gets the chip; the wall gets nothing it did not ask for.
- **Left:** a sound on the desk for the moment (the stinger voices are there); the multiview
  could carry the chip, which needs a runtime word on the snapshot like the review's.

### 40.3 — the arcade node from the kernel alone

- **Done.** `NodeHost`, `NodeActions`, `NodeRouter`, `NodeViewModel`, `NodeWindow`; `IActionLayer`
  and `IRouter`; the Arcade and Nodes pages bound to `IArcadePage` and `INodesPage`; the app
  booting the arcade node without a desk. `docs/PLAN.md` §58.3.
- **The proof the ecosystem conversation asked for:** a computational function on a machine the
  desk finds on the beacon, appearing as a source (its NDI send) and answering the one
  vocabulary, with nothing on it that could open a screen — and built from the kernel, not the
  desk with its hands tied.
- **Honest about the rest:** the caller and the timer still boot the desk. Moving them is the
  twin link, the cue stack and the countdown's display onto the kernel — a round of its own.
- **Found on the way:** the wire served the desk's remote page as any process's front door; a
  node's is its own now.

### 40.4 — found by the build machine: a verb lost to a moment's stall

- **What failed:** the arcade test's desk sent `ARCADE START` to the node it heard and the node
  never started — once, on the build machine, on a commit that touched nothing on that path,
  after seven green runs of the same test. `docs/PLAN.md` §58.4.
- **What it was:** a fire-and-forget line with one attempt and one 1.5 s budget for the
  connect, the line and the reply together. A stall of that length between the connect and the
  write — a build machine's neighbour, on a show network a switch relearning — and the line
  never leaves, the node never hears it, and the desk has only a status line to show for it.
- **Done:** connect, reply and retry budgets kept apart; a line that never left sent again
  once; a line that did leave never sent twice, its silence said as silence. The test carries
  the desk's status line into its timeout, so the next failure explains itself. Two tests on a
  raw-socket node pin the rule.
- **Not done, and why:** a re-run was not the fix — a failing test is never an infra flake
  until the path it walks has been read. The node-side handler still greets before it reads,
  which is what Companion expects; a line that reached the node's socket before the desk gave
  up is read past the desk's close and run, so the desk's second ask covers the one case that
  loses a verb — the line that never left.

## Round 41 review — the caller and the timer on the kernel

*The desk-as-caller retired; what a stack, a link and a stage ask of their host said in
contracts; the kernel narrowed by one engine and widened by the machine's own services.*

### 41.1 — the cue stack on a contract

- **Done.** `ICueHost`, `CueStackService(ServiceKernel, ICueHost)`, `CueRuntime` on the kernel,
  the paper runner on the node. `docs/PLAN.md` §59.1.
- **The trade:** a caller alone used to run cues for real — looks landed on a local air state
  nobody saw. Now it reads them. Honest, and cheaper: the caller never needed the engines it
  was carrying to do that.

### 41.2–41.4 — the follower's link, its action layer, the stage

- **Done.** The node host as the twin's, the stage's and the stack's host; a timer that joins as
  a follower; every verb forwarded while in step; `STAGE ACK` in the vocabulary; the stage pages
  served from the node. `docs/PLAN.md` §59.2–59.4.
- **What it bought:** a stage timer node that is a stage timer alone, and the desk's clock when
  linked; a caller whose window is the Run surface and the Cues page over the kernel, with the
  same XAML the desk binds.
- **Left:** the timer's verbs from its own `/timer` page while linked go to the desk and come
  back mirrored — right, and a second slower than the desk's own page; a node's window has no
  Machine or Help page yet.

### 41.5 — the pages on interfaces

- **Done.** `IRunPage`, `ICuesPage`, `IStagePage`, `IStageDisplay`; `RunViewModel` and
  `CueEditor` on `IRunHost`; `StageSection` and `StageDisplay`; the node window's tabs by kind;
  the boot. `docs/PLAN.md` §59.5.
- **The seam that showed:** the Run surface carried the wall; the wall is the desk's. `HasRunWall`
  and a code-behind that builds the wall only on a desk keep one XAML for both.

### 41.6 — on the kernel, or off it

- **Done.** The arcade off the kernel; the updates folder and the management check-in on it
  through `IMachineHost`. `docs/PLAN.md` §59.6.
- **The rule that fell out:** the kernel is what every role *needs*, not what every role *could
  use* — an engine is the second, an update channel the first.
- **Left, and said:** the show lock and the metrics stay the desk's for now, with the reasons in
  the plan.

