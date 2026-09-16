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
  answer. *(Corrected in round 45: the view models no longer use the static at all — zero uses
  in `ViewModels/`; the services carry it, `ControlService` and `TwinService` most, and the
  arcade window once.)*

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


## Round 42 review — the game on the wall with nothing between

*An input the desk already knew how to show, fed from the loop's own buffers; the NDI send off
the game's thread; a window of the game's own.*

### 42.1 — the frame ring

- **Done.** `FrameRing` in Core: one writer, any readers, pinned buffers, a skip instead of a
  wait, a sequence for lanes to wait on, a drain before the buffers go. `docs/PLAN.md` §60.1.
- **What it bought:** the loop's three buffers and two indices served one reader; the ring
  serves the page, the pop-out, the copy and the send, and the loop's `Frame` never takes a
  lock but its own.
- **The test that matters:** a writer and two readers flat out for a third of a second, and no
  buffer ever read while written.

### 42.2 — the lane

- **Done.** The NDI send and the bus copy on `arcade-lane`, the sender unclocked, the copy only
  while wanted. `docs/PLAN.md` §60.2.
- **The seam that showed:** the sender's clock was the loop's clock — a network library pacing a
  game. Unclocked and on its own thread, a slow send drops a frame on the network and none in
  the world.

### 42.3 — the ARCADE source

- **Done.** `arcade:local` on the bus; `Arcade` in the four source enums; the locator, the
  renderers, the pickers, the engine with the fade-out hold. `docs/PLAN.md` §60.3.
- **What it bought:** the operator's question — "is there a faster way than NDI to get the game
  on screen?" — answered with the fastest way there is: none between.
- **Left, and said:** the source is this machine's game; a node's picture still comes over NDI.

### 42.4 — the window

- **Done.** `ArcadeWindow`, `ArcadeWindowHost`, POP OUT and FULLSCREEN on the page, `ARCADE
  WINDOW` and `ARCADE FULLSCREEN` on the wire and in cues. `docs/PLAN.md` §60.4.
- **The rule kept:** the service knows a host function, not a window — a headless run refuses
  the verb in words.

## Round 43 review — attempts are not facts

*The triage's three P0s, taken in its order: the authority sidecar fails closed, the hand-back
releases the standby last, a box's answer confirms a switch.*

### 43.1 — the record fails closed

- **Done.** `SidecarRead` and `SidecarWrite`, `OutputClaim.Unknown`, a start and a restart that
  open nothing on an unreadable record, a beat that holds only what committed, an ask that ends
  nothing unless it reached the disk, the files as a seam. `docs/PLAN.md` §61.1.
- **What it bought:** the rule the process probe already kept — unreadable is a fence — under
  the file that decides who may put a picture on the wall. Eight fault cases on CI.
- **The one the triage did not have:** a folder that is not there reads as free through
  `File.Exists`; now it is unreadable.

### 43.2 — the hand-back as a transaction

- **Done.** `TwinTransaction`; TAKE BACK across machines opens here, routes, confirms, commits,
  releases, in that order; one machine releases first; the trail in the log and TWIN STATUS.
  `docs/PLAN.md` §61.2.
- **The seam that showed:** the comment above the old cue call described the order the code
  did not have. The transaction makes the order a contract a stage cannot skip.
- **Kept on purpose:** a takeover routes before it opens — the main is silent and a refusal
  must leave the standby untouched.

### 43.3 — sent, delivered, accepted, observed

- **Done.** `ConfirmLevel` on the device, `DeviceConfirmation`, receipts on the card, in the
  journal and to the twin's fence; the HTTP link awaitable; profiles that say which reply
  answers which command; an unanswerable box not a fence. `docs/PLAN.md` §61.3.
- **What it bought:** a hand-back through a projector input that waits for `%1INPT=OK` and
  stops on `ERR3`; a takeover by itself that refuses on silence and goes ahead on a yes.
- **Left, and said:** one expected answer per device; the cue stack's late-failure watch does
  not read receipts yet.

## Round 44 review — what travels, and how the key is known

*The key proved over nonces on both sides; the machine's own sections off the wire; the show's
credentials sent by choice; the drill written down.*

### 44.1 — the handshake

- **Done.** JOIN with a nonce and no key, CHALLENGE with the main's proof, PROOF with the
  joiner's; a main that cannot prove the key gets no proof and no standby. `docs/PLAN.md` §62.1.
- **What it bought:** a line read off the wire holds no key, and a rogue main is found out
  before it can hand a standby a show. Both sides prove; the triage asked for one.

### 44.2 — the wire's scope

- **Done.** Mirrored sections only; credentials sent by default and blanked by choice; a landing
  keeps the standby's own where the wire is blank. `docs/PLAN.md` §62.2.
- **The honest limit:** the credentials the standby needs to run the show are the ones that
  travel; the switch exists for a link that is not on the show's own network, and encryption is
  named as the next thing, not claimed.

### 44.3 — the drill

- **Written.** `docs/DRILL.md`, eighteen scenarios, to be run in a real room and filmed. `docs/PLAN.md` §62.3.
- **Not done here:** it cannot be. It is the gate the next release in a real room passes or does not.

## Round 45 review — the hand-back answered, and a fence that answers

*Two outside reviews of `95c0fc7`, checked line by line against the code before anything was
changed. Their authority findings were real; one consequence they missed was worse than the
findings. Their P1 list is kept for the rounds after this one.*

### 45.1 — the hand-back

- **Done.** HANDBACK carries the handover's id; RELEASED answers by it after the outputs closed
  and the marker went; the main's "old owner released" is that answer, the marker gone, the
  process gone, or a fresh join that claims nothing — never the line having been written. Overdue
  is said and the hold kept on one machine; the next press and the re-dial tell it again; a
  stopped handover resumes and the trail keeps both. `docs/PLAN.md` §63.1.
- **The one they missed, closed:** a join claiming the show under a takeover this desk took back
  is answered with the hand-back, not a hold. Before, one lost line could route the room to the
  main and close the main's outputs.
- **What it bought:** the same discipline a game's netcode has for a message that must not be
  lost — id, ack, resend, idempotent receive — on the one message the twin cannot afford to lose.

### 45.2 — the fence

- **Done.** A fence is a box that answers at Accepted or better; a cue that is not there, one that
  only recalls a look, a box not on the page or one merely heard is not a fence, and says why. A
  receipt at Delivered does not confirm a route. An open link reads the page's current device
  words after a show has landed. `docs/PLAN.md` §63.2.
- **The honest limit:** Accepted is the box saying yes to the command, not the room having moved;
  Observed with a query is the stronger fence where the profile has one, and the drill's
  scenarios 7 and 14 are where a switcher's Confirm level is found out.

### 45.3 — the operator's route

- **Done.** A take-back across machines with no cue, or a cue nobody's box vouches for, is two
  presses with the room switched by hand between them; nothing infers the switch. A takeover by a
  press with no cue says so. TWIN STATUS says what the next press does. `docs/PLAN.md` §63.3.
- **What was not done:** a verb of its own for the second press, and a TAKE OVER ANYWAY for a
  standby that will not let go on one machine — both considered and left, §63.5.

### 45.4 — the reviews themselves

- **Where they were stale:** the first list's P0 asked for the confirmation levels Round 43 built
  and the confidentiality decision §62.4 had made; the second review caught both.
- **Where they were wrong:** "move lunch does not exist" (it does: −1 MIN, +1 MIN, RESUME NOW,
  CATCH UP on the Run surface; what is missing is a wire verb and the countdown following the
  plan); the control port's limits are the audience port's, looser, not absent (what is absent is
  a read timeout and a cap on the wire); a NodeHost guard test exists; UiThread is out of the view
  models. Each is recorded so the next reviewer starts from the code.
- **Kept for the rounds after:** caller PLAN and SECTION queued while armed; a live policy that
  refuses calibration and other non-show work while armed and live; the glance line with true
  presentation drops; PLAN SHIFT on the wire and the countdown following the plan; the node
  Machine strip; a venue NAT profile; the wire's timeout and caps; the compact soak beside the drill.

## Round 46 review — the armed desk

*Arm and live together, as a table; a caller's plan that waits; its notes that do not.*

### 46.1 — the live policy

- **Done.** `LivePolicy` names six verbs and refuses them only while the stack is armed and the
  outputs are live, at the one door every verb comes through, and through the update window.
  Content is never on the table. `docs/PLAN.md` §64.1.
- **What it is not:** a Show Mode. Two states that already existed, read together; nothing new for
  the operator to switch on, and the refusal says what lifts it.
- **The honest limit:** the table is short on purpose; a verb missing from it runs. Loading a
  show and the assistant's APPLY are named as considered and left.

### 46.2 — the caller while armed

- **Done.** APPLY waits on the page while the desk is armed and lands on DISARM; a cue added,
  removed or moved by a caller waits too; a note, the pad and a cue's own words land at once; a
  desk edit meanwhile sets the waiting one aside and says so. `docs/PLAN.md` §64.2.
- **Found on the way:** a desk edit within the flush window after a caller's edit landed was never
  mirrored back to that caller — the echo origin was decided at the flush. Decided at the publish
  now, with a test that would have failed before.
- **Also found:** the desk builds its cue stack after its twin, so a hook wired in the twin's
  constructor sees no stack; the earlier hook on the hosting tick worked by timing. The landing is
  idempotent and hooked wherever the stack first is or something is first queued.

## Round 47 review — the glance, and one clock for the day

*True drops from the pacer, a p95 from bins, one line on the Run surface, and the day's slip as a
verb every clock follows.*

### 47.1 — the numbers

- **Done.** The pacer counts the slots the room did not get; the budget keeps a histogram per
  second and reads a p95 in one walk; the CSV carries both and rotates on a changed header.
  `docs/PLAN.md` §65.1.
- **The honest limit:** a p95 from half-millisecond bins is exact to the bin's upper edge; a
  frame past 64 ms reads as "over 64 ms". Good enough to see a night, not a profiler.

### 47.2 — the line

- **Done.** `Glance` is pure and tested; the two hosts give their words through one member;
  the desk, the pop-out and the caller's window show the same line. The last box that said no is
  on the health line too. `docs/PLAN.md` §65.2.
- **What it is not:** a dashboard. One line, facts only, in the order a caller reads them.

### 47.3 — the clock

- **Done.** PLAN SHIFT, RESUME and CATCHUP on the wire and from a caller node; the countdown
  follows the standby cue's planned start when told to. `docs/PLAN.md` §65.3.
- **Kept small on purpose:** no scheduling engine — the plan is the cues' planned starts, the
  slip moves them, and the countdown reads the one that is next.
- **Found on the way:** a calibration run outlived its desk and blackened every output sink in the
  process behind it; shutdown ends the run now. In the suite it read as rendering tests failing at
  random under load — a flake with a cause, found by asking what paints black.

## Round 48 review — the node's own Machine tab, the room behind one address, and the wire's ceilings

*The P1 list's last three, and the soak before the drill.*

### 48.1 — the Machine tab

- **Done.** Every node has the tab; the words that sent its operator to a page it never had now
  send them there; RESTART from the tab and from the wire go through one door. `docs/PLAN.md` §66.1.
- **The honest limit:** the tab is the node's own settings, not the desk's page — no super-check,
  no tiles. A node has nothing to render, and its health is its link's words.

### 48.2 — the room behind one address

- **Done.** A profile, not a wider default: a flat room keeps the per-address budgets that tell a
  runaway phone from the room; a venue NAT opens them to the room and keeps the per-phone ones.
  The sign is on the room's line with the fix named. `docs/PLAN.md` §66.2.
- **Kept deliberate:** the profile is never switched by itself. A budget that widens itself is
  one an attacker widens.

### 48.3 — the wire's ceilings

- **Done.** Connections in all and from one address on the wire and on the web remote, a started
  line's seconds, one ledger for three doors, one log line a minute. `docs/PLAN.md` §66.3.
- **What it is not:** a rate limit on lines. A Companion that hammers is answered line by line;
  the ceilings are on what holds a slot.

### 48.4 — the soak

- **Done.** Seven steps, four hours, the faults at the hours, what decides. `docs/SOAK.md`.
- **The honest limit:** it is a script to run, not a result. Nothing is claimed until a row is
  filled in on the real rig.

## Round 49 review — one clock across the machines

*The beats measure the clocks; the followers read the desk's; the lines say when they are apart.*

### 49.1 — the measurement

- **Done.** NTP's arithmetic over the beats both sides already send, the shortest round trip
  believed, the arrival stamped at the read. Nothing new on the wire but four numbers, and an old
  beat still reads. `docs/PLAN.md` §67.1.
- **The honest limit:** an offset is only as symmetric as the path; a Wi-Fi hop that is slow one
  way skews it by half the difference. On a show LAN that is milliseconds, and a stage timer
  reads seconds.

### 49.2 — the room clock

- **Done.** One clock on the kernel, two host contracts, every absolute read on a follower on the
  desk's frame; a deadband, a kept frame on a drop, a reset when alone by choice.
  `docs/PLAN.md` §67.2.
- **Kept deliberate:** a standby twin does not follow. After a takeover it is a desk of its own,
  and one frame per machine — outputs included — beats a stage timer that disagrees with the wall.

### 49.3 — the words

- **Done.** Each peer's clock on the main's line, the desk's on the follower's, the warning on
  the health line on both sides past two seconds, with the fix named. `docs/PLAN.md` §67.3.
- **What it is not:** a time server. The venue's is the venue's; the link measures and says.

## Round 50 review — the switch on the clock

*The press timed to the frame; the arrival work below it.*

### 50.1 — the measurement

- **Done.** Press, handler, build, frame, on a budget beside the tick's; the words say where the
  time went; the Machine page, the super-check and the CSV read it. `docs/PLAN.md` §68.1–68.2.
- **The honest limit:** the frame is the window's first frame after the tab moved, not the
  compositor's presentation; on a desk it is the same to a millisecond, and a headless test sees
  none and says so.

### 50.2 — below the frame

- **Done.** The rig's read and the presets folder run below input and rendering. `docs/PLAN.md`
  §68.3.
- **What it is not:** a rewrite of the pages. Two things were on the switch's path; the budget
  will name the next.

## Round 51 review — the twin service in parts

*Five files, one class, nothing moved across a seam.*

### 51.1 — the split

- **Done.** Cut along the regions the file already had, by line ranges with the seams asserted;
  the suites ran the same before and after. `docs/PLAN.md` §69.1.
- **The honest limit:** a partial class is a table of contents, not an architecture. The link,
  the mirror, the handover and the followers still share one object's fields; the cut names
  where a later extraction would go and does not pretend to be it.

### 51.2 — the guard

- **Done.** Each part on a page, no sixth part unnoticed. `docs/PLAN.md` §69.3.

## Round 52 review — the GO on the clock

*The press to the frame, cue by cue; the lag of every sink behind the publish.*

### 52.1 — the measurement

- **Done.** The publish's clock was already on the snapshot; the pipeline now says which one each
  frame drew, so the lag costs nothing per frame but a comparison, and the GO's clock reads the
  sinks' rings on the poll. `docs/PLAN.md` §70.1–70.2.
- **The honest limit:** the frame drawn, not the frame presented — a vsync short of the glass, and
  the same short on every desk. And the stack's Go is the press as far as this code can see.

### 52.2 — the words

- **Done.** GO→frame on the glance line, the line on the Machine page, the row on the super-check
  with the publish told from the frame, the CSV's two columns. `docs/PLAN.md` §70.3.
- **Kept deliberate:** the sinks that count are the outputs when any is open, the preview
  otherwise — a monitor pane never holds a GO open.

## Round 53 review — the desk tells the assistant how it is doing

*Six rounds of self-measurement, put where the operator's questions go.*

### 53.1 — the brief

- **Done.** The desk's own lines and the check's warnings in the brief, gathered on every ask;
  the fence says to answer from them and never to guess. `docs/PLAN.md` §71.
- **The honest limit:** the assistant is optional and needs a key; without it the same words are
  on the Machine page and the glance line, where they were.
- **Kept deliberate:** nothing that names the machine, a path or an address goes in the brief,
  as before; the measurements are numbers and page names.

## Round 54 review — Companion, second generation

*The module could not be loaded by the Companion a venue installs today. Now it can, it finds the
desk by itself, the desk drives it back, and both sides speak one colour.*

### 54.1 — the module on Companion 5

- **Done.** Base 2.x, node22, sections, the default export; every id kept; the stage, nodes, twin,
  plan and arcade keys; a node test suite on the real base with Companion's own preset sanitiser;
  the lines file the desk parses; a CI job that packages the `.tgz`. `docs/PLAN.md` §72.1.
- **The honest limit:** no Companion ran here. The module boots against the real module base and
  its presets pass Companion's own sanitiser, but a layered key drawn on a Stream Deck, a bonjour
  pick in the connection dialog and an import of the `.tgz` are unverified until a Companion 5 is
  in the room. The lines it sends are parsed by the desk's suite, so a key that is wrong is wrong
  in its rendering, not its verb.
- **Kept deliberate:** Companion 3 and 4 are not served; the last 2.x module stays in history.

### 54.2 — found on the network, both ways

- **Done.** DNS-SD in Core, the responder and the browser in the App, the HELLO token, the
  Remote page's strip, `version` and `decks` in STATE. §72.2.
- **The honest limit:** a real one-shot query is answered on the loopback in the test; a multicast
  browse across a venue's switches is the network's to allow, and the words say when port 5353
  would not open. IPv4 only.
- **Kept deliberate:** the advert's host label is the machine's with `-patterns`, so the system's
  own mDNS record for the machine is never argued with.

### 54.3 — the desk drives the deck

- **Done.** The Companion profile with its words, `+OK` / `-ERR` as receipts, the chip with the
  heard address, the assistant's words. §72.3.
- **The honest limit:** Companion's TCP API lines are the documented ones; a Companion that
  changed them answers `-ERR`, which the card shows word for word.

### 54.4 — the colour language and the room on the deck

- **Done.** The palette both ways with its test, the Nodes cards in the deck's hues, `nodes`,
  `twin` and `stage` in STATE with the deck signature push. §72.4.
- **Kept deliberate:** the desk's own pages were not repainted in the palette beyond the Nodes
  cards; the palette is the deck's language and the desk borrows it where the same fact is shown.

## Round 55 review — two fixes from field testing

### 55.1 — the page's picture at the browser's rate

- **Done.** `Page.startScreencast` with immediate acks and a latest-wins decode off the UI thread,
  the frame sliced from the event without a second copy, JPEG 70, the screenshot poll as the
  fallback, the rate on the status line, in STATE and in Companion. §73.1.
- **The honest limit:** no browser ran here; the rate the room gets is the field's to read off
  the line. The research's next step (capture from the composition visual) is recorded, not built.
- **Kept deliberate:** the page's `IsPlaying` means "up with a picture", not "a frame arrived
  lately" — the screencast sends nothing for a still page.

### 55.2 — the armed web VT

- **Done.** `WebVt` in Core; the arm per page in the engine with the fire on the way to air, the
  look's own arm, the pre-roll of the cues ahead with a grace, the advert skipped; the verbs, the
  cue actions, STATE, the phone, Companion, the desk's PAGE CONTROLS row. §73.2.
- **Found and fixed on the way:** the clicker's next cue was looked up with a call that *makes*
  the clicker list when a show has none — inside the show's own change event and, on a standby, in
  a section the main owns; five twin and load tests said so. It is found, never made, now.
- **Kept deliberate:** a page on air refuses an ARM; the look's start point is what arms a page
  by itself.

### 55.3 — the routing matrix in Core

- **Done.** The model, the plan with each VOG mode, the seed, the players' lists, a clip's path,
  the envelope, the words, the verbs on the wire, OSC and in cues with the checks. §73.3.
- **Kept deliberate:** the matrix is off by default and every show before it behaves as it did;
  the monitor stays outside the matrix (its rule from round 26 stands).

### 55.4 — the routing matrix in the App

- **Done.** The players following the matrix, the decoder's tap, the ring, the graph's lanes,
  NDI embedded audio, the page's picker, the Audio page's ROUTING area, STATE, Companion. §73.4.
- **The honest limit:** the lanes, the tap, the NDI send and the page's picker need Windows, a
  decoder and a runtime — none here. The App tests use fakes; the field decides the rest. The
  clip taps have no sample-rate lock yet (a counted snap, not a growing delay).
- **Seen, and fixed after the round:** `TwinAppTests.TheWireCarriesNoMachineSectionsAndTheShowsCredentialsOnlyWhenTold`
  looked for the digits `9876` in the wire's payload to prove the admin passcode never travels, and
  a generated id can carry them by chance — one run here did (`…f9a99ff2987695…`). The secret is
  now a word no hex id can spell (`zq9876`), and the machine's own sections are checked as JSON
  properties over `TwinSync.LocalSections` — all eight, not three named in text. The Core twin
  auth test had the same shape (`1234`) and got the same treatment.

Counts at the end of the round: Core 1,233, App 616 — both suites green here, the module's
thirteen beside them.

## Round 56 review — a single machine rock solid

*The triage document's P0 and P1 (the twin's items left for later at the brief's direction), one
unit a commit, each proven by both suites before the next.*

### 56.1 — one captured snapshot per frame

- **Done.** `FrameInput` captured once at the top of the frame with its kind; everything below
  draws from it. §74.1.
- **The honest limit:** proven headless on the raster backend; a GPU device-lost mid-frame is not
  simulated here.

### 56.2 — cue execution ids; device receipts settle the cue

- **Done.** The id through the actions, the device lines and the receipts; the row `Requested`
  until settled; STATE and Companion carry the pending count. §74.2.
- **Kept deliberate:** a datagram is `Done` at once — a UDP line has no receipt to wait for; the
  GO lockout stands, so the settlement test sleeps between two GOs.
- **The honest limit:** the boxes here are fakes; a real projector's timing is the field's.

### 56.3 — the warp geometry cache

- **Done, with numbers.** A plain frame 1,616 B → 32 B, a 17 × 17 mesh frame ~100 KB → 331 B
  (235 B are two Skia wrappers); the allocation test holds both under a bound. §74.3.
- **Found on the way:** the overlay's badge shaped its letters one `DrawText(string)` at a time
  (SkiaSharp 3 shapes a blob per call) and the transition key's `GetOrAdd` closure — both gone.

### 56.4 — render faults, the last good frame, the canvas, the words

- **Done.** Faults per sink with the run and the last good frame drawn in their place; the
  canvas quiet while detached; *slots missed* and *first drawn frame* in every line; the CSV's
  columns renamed. §74.4.
- **Kept deliberate:** a fault is logged once every ten seconds, not every frame — the log is
  for the stack, the line for the count.

### 56.5 — side effects by dirty domain

- **Done.** The domains, the dispatch on the mask, the reconcile budget on the page, the
  super-check and the brief. §74.5.
- **The honest limit:** the domains' reads are hand-kept lists — a system that starts reading a
  new section must be added to its list or an edit there will not reach it. The test
  `EverySectionASystemReadsIsARealSectionOfTheShow` catches a misspelt section, not a missing one;
  the boot and `RepublishNow` still run everything.

### 56.6 — the show's files off the desk's thread

- **Done.** The autosave and the recovery record serialised and written on one ordered lane from
  the frozen show, coalesced; a show, a save and a sheet on workers; the files budget. §74.6.
- **Found on the way:** three tests read the recovery sidecar right after an action and now flush
  the lane first — the sidecar is written a moment later, on purpose.
- **Kept deliberate:** a show saved by hand is still serialised on the desk (the operator asked,
  and the file must be the show as they see it); only its write moved.

### 56.7 — the desk's second in lanes; the pages warmed with headroom

- **Done.** Three lanes, the housekeeping budget with carry-over, the pages in a show's order with
  the headroom rule and the small-machine rule, the pages line. §74.7.
- **Kept deliberate:** the test harness pins the housekeeping budget wide — a test that reads the
  Install page or the machine's lines after one poll wants the lane whole whatever the test
  machine is doing; the lanes' own test sets the desk's real budget back. The machine class
  (8 GB) is a chosen line, to be tuned from the field.
- **Found on the way:** the first pages line walked the window's logical tree, and a cold walk
  under load tripped the dashboard's desk-tick caution in a test — the pages now register by
  their root as they attach, and nothing walks. `StartupTests` also assumed a desk-class machine:
  the harness pins one.

### 56.8 — the quality ladder from the sink's rate, its p95 and a profile

- **Done.** The budget from the rate, the second's verdict on p95, slots and stutter, the sink
  pressing hardest judged, the profile read at the start and written at the end, the untouchables
  proven pixel for pixel. §74.8.
- **Kept deliberate:** the safety margin (85 %) and the three-slot line are chosen numbers with a
  reason each, not measurements; the profile's start is not a step (nothing counted, no time).
- **The honest limit:** on Linux the machine key has no CPU name; the render loop that would feed
  real p95s ran nowhere here — the App tests record frames by hand.

### 56.9 — what a box did lately; the venue NAT through the socket

- **Done.** `DeviceRuntime` on the card with the ages, in STATE and on the deck; two hundred
  phones through the real socket under the profile, refused on flat, seated again. §74.9.
- **The honest limit:** the loopback is one address by nature; the connection ledger's per-address
  cap is not asserted (two hundred parallel connections may or may not overlap on a test machine),
  the join budget is.

### Seen, and noted

- `DeckStateAppTests.StateCarriesTheNodes…` failed once in a full run with two nodes where three
  were expected and passed alone and in every run since — a beacon's arrival order, not a fault
  found; watched.
- `AdminTests.TheDashboardReadsTheMachineAtAGlance…` tripped once on the desk tick's 50 ms
  caution while another suite ran beside it: the cause was 56.7's first pages line (fixed above),
  but a loaded test machine can still make a cold tick slow — the test reads a caution, not a
  crash.
- CI's Windows job and the portable exe were not run here; the branch is pushed for it.

Counts at the end of the round: Core 1,257, App 647 — both suites green here, the module's
thirteen beside them.

## Round 57 review — Companion 5, memory, the live picture's age

*Three asks in one brief, each a unit and a commit, each proven by both suites before the next;
the twin's items left for later at the brief's direction.*

### 57.1 — the Companion module imports into Companion 5.0.5

- **Found.** Not a schema or runtime fault: Companion 5 refuses a module whose id and version it
  already has, and the module had shipped three times as 3.0.0; the CI artifact is a zip round
  the tgz, which the file import does not unpack. §75.1.
- **Done.** 3.1.0 everywhere, a real `LICENSE`, install words in the README and the HELP, a
  packaging test that builds the package and asserts what Companion's installer, scanner and
  process manager check, and CI running it and `companion-module-check`.
- **The honest limit:** Companion itself was not run here; the test mirrors its checks from its
  source at v5.0.5, read on GitHub.

### 57.2 — memory placed, bounded in bytes, and a steady second of video that allocates nothing

- **Done.** The render fence, the frame pool under libVLC and NDI, byte budgets by machine
  class, the picture cache in bytes, the ledger and the new samples. §75.2 and
  `docs/MEMORY-RESEARCH.md`.
- **Fixed in the round:** the first fence held every pool for any sink that had drawn once in
  the last two seconds — a headless boot's previews, and on a desk every click that redrew a
  preview; the pool's table of who drew it (`Touch`) is the fix, and the App test now draws a
  pooled frame through a real source on the bus rather than assuming the boot's sinks are quiet.
- **The honest limit:** proven headless on the raster backend; libVLC's lock/unlock/display
  into the pool is written to libVLC's contract and not run here; the fallback is time; the
  ledger sees what registers.

### 57.3 — the live picture's age, the low-latency profile, the player question

- **Done.** A live source stamps its frames; the frame keeps the oldest live picture it drew;
  the budget ages it at the frame's end; the glance, the render line, the super-check's *Live
  input* row, the CSV, STATE and Companion carry it. The low-latency profile per capture device
  beside the mode, on the Media page and the PiP's, reopening the decoder. §75.3 and
  `docs/PLAYER-RESEARCH.md` — the chain, the verdict, the phased plan.
- **Kept deliberate:** a web page and the arcade time their frames but are not live — their age
  is not IMAG; the words say *decoder to frame*, never the card or the screen.
- **The honest limit:** no capture card, no NDI runtime, no GPU and no Windows here; the age is
  proven with a timed fake source on an output; the profile's options are asserted, not opened;
  the chain's other links carry the research's numbers, not measurements from this machine.

### Seen, and noted

- The App suite's `MemoryAppTests` fence test failed once in a full run and passed alone: the
  boot's own sinks had drawn within two seconds. A real flaw in the fence's rule, fixed above,
  not a test wobble.
- CI's Windows job, the portable exe and Companion's own import were not run here; the branch is
  pushed for them.

Counts at the end of the round: Core 1,270, App 652 — both suites green here, the module's
seventeen beside them.

## Round 58 review — the critique's P0 and P1

*One brief, a developer's critique in priority order; five units, each a commit, each proven by
both suites before the next; the hardware qualification recorded as the next field work.*

### 58.1 — the lease

- **Found.** The race the critique named was real: a draw fetched the latest image under the
  pool's lock and touched the fence outside it; and the live age noted the source's newest
  clock, not the drawn frame's. §76.1.
- **Done.** `TryLease` under the pool's own gate; `DrawnFrame` from every draw; the stages note
  the drawn clock; the budget keeps the frame's generation and clocks in words.
- **The honest limit:** the race is closed by construction and by a test that publishes after a
  lease and finds it stale; there is no thread-sanitiser for .NET here.

### 58.2 — retirement

- **Done.** Monotonic ticks; reuse on evidence or a dead sink only; one list for frames,
  scratch and pictures; the fence's health on the desk. §76.2.
- **Kept deliberate:** the ten-second abandon is a disposal-only backstop, counted as a forced
  free and shown; it should read zero in a soak, and a number there is a finding, not a feature.
- **The honest limit:** the dead-sink rule (two seconds without a frame started) is the one
  timing assumption left, chosen because a sink asleep holds no frame mid-draw; a sink stalled
  mid-draw for two seconds is a fault the render faults row already shows.

### 58.3 — memory truth

- **Done.** Honest pool bytes, one media view against six tenths of the app's ceiling, the
  ladder with three rungs and named steps, the retired sources bounded to two. §76.3.
- **Kept deliberate:** the ladder never touches the source on air, and its steps are the cheap
  ones first; the critical rung refuses only a preview-only open.
- **The honest limit:** the rungs are proven by driving the reading, not by filling a machine;
  the budget's share is a judgement for the soak to move.

### 58.4 — live-change safety

- **Done.** The policy table; reopens staged while on air and applied when the source leaves it
  or the outputs go off; the monitor rule in the inputs' domain; the domains audited by
  behaviour. §76.4.
- **Found on the way:** a monitor-rule edit did not reach the decoders — the inputs' domain
  listed the routing matrix but not the rule that decides which bus a mount's sound belongs on.

### 58.5 — the browser and the audio

- **Done.** Observed VT phases with timeouts; ring epochs from the VLC flush; the screencast judge
  with bounded restarts and awaited acks; routes that fail closed with the permission gated to
  the page's origin; latest-wins generations; the audio graph on a signature and its own domain.
  §76.5.
- **The honest limit:** the browser side (`WebFrameSource`) compiles here and runs on Windows
  only — WebView2, the permission request, the screencast and the output-device routing are
  written to the CDP and WebView2 contracts and were not run; the judge, the phases, the ring
  and the route rule are pure and tested.

### Seen, and noted

- `DeckConversionAppTests.APowerPointIsConvertedOnceAndBecomesTheDeckOnAir` failed once in a full
  App run (two sources expected, one seen) and passed alone and in every full run since — a
  reload-gate timing race in the test's own wait, not the deck; watched, not fixed this round.
- No WebView2, Windows, libVLC, NDI runtime, GPU, capture card or Companion here; the branch is
  pushed for CI's Windows job, and the field list is in `docs/SOAK.md`.

Counts at the end of the round: Core 1,288, App 660 — both suites green here, the module's
seventeen beside them.

## Round 59 review — the modules

*One ask, widened in the round at the user's direction: the assistant, NDI, the arcade and the
audience as assemblies too; eight units, each a commit, each proven by every suite before the
next.*

### 59.1 — a pure show core

- **Done.** Geometry and colour of the core's own, the playhead contract, the render side's
  hooks. §77.1.
- **Found on the way:** the names first chosen (`PixelSize`, `PixelRect`) collided with
  Avalonia's in every App file that names both; renamed to the raster words the code already
  used.
- **Kept deliberate:** the canvas rectangle's semantics kept to the letter (the all-zero
  empty, the plain union), so no plan changed when the type did.

### 59.2 / 59.3 — the render side out, the tests split

- **Done.** Three assemblies above the core; the core's project file names no package; the
  tests split along the seam. §77.2, §77.3.
- **Found on the way:** a rewrite of relative namespace qualifiers damaged the moved files'
  namespace lines once and the tests' using lines once (a lookahead that excluded `Rendering.`
  but not `Rendering;`); both repaired in the same unit, the second by a whole-tree check of
  every namespace declaration. Recorded because it is the kind of mistake a mass move invites.
- **The honest limit:** the fonts moved with the renderer under their old resource names;
  proven by the manifest and the text tests, not by eye.

### 59.4 — the devices

- **Done.** The dispatch seam, the contracts, the transports as an assembly, a Companion driven
  with no desk. §77.4.
- **Found on the way:** the kernel test asserted every kernel service takes the kernel itself;
  it now accepts a contract the kernel provides, which is the stronger rule.

### 59.5 — the audio

- **Done.** The DSP and the providers as an assembly; the desk's services compositions. §77.5.
- **Found on the way:** the stinger voice took the whole graph for a yes-or-no; a rule wrote
  a missing device into a static of the desk's from inside itself. Both undone by the move.

### 59.6 / 59.7 — the assistant, the audience

- **Done.** The assistant's brain over the core's vocabulary with the SDK out of the App; the
  room on two contracts with the port on the wire. §77.6, §77.7.
- **Kept deliberate:** the assistant's tests reference the render side for real pictures
  through the codec; the assembly does not.

### 59.8 — the rules as a test, the modules on the desk

- **Done.** The module rules from the compiled references and the source; the modules map in
  STATE, the ticket, the brief and the log; a node's footprint measured. §77.8.
- **The honest limit:** the suite's process is shared, so the footprint test asserts the one
  claim that holds in any order (a timer node never pulls NDI in) and records the rest; the
  measurement alone says the arcade and the room load on every node — the next cut, named.

### Seen, and noted

- The temp filesystem filled with the round's test-run copies mid-round; cleared. Nothing of
  the tree was touched.
- No Windows, WebView2, libVLC, NDI runtime, GPU, capture card or Companion here; the branch is
  pushed for CI's Windows job.

Counts at the end of the round: Core 655, Rendering 602, Devices 7, Audio 9, Assistant 36,
Audience 2, App 664 — 1,975 in all, every suite green here; the Companion module's seventeen
beside them.

## Round 60 review — right-click menus

*One ask, in the user's words: right-click on a screen in the preview to recall any pattern or
reset it to its look; on a cue for its look, its transition, its overlays, a lower third in and
out on a clock; on a layer, the countdown, a lower third for source, pattern, preset, library and
a way to the page; contextual assistant features; the preview only, unless it is the cue stack;
clear, instinctive, instructive, in the desk's colours. Five units, each a commit, each proven by
every suite before the next.*

### 60.1 — the staged verbs

- **Done.** Five kinds that land on a screen's PVW in the preview and nowhere else, the live
  twin `SCREEN n PATTERN`, the words on the wire, OSC and in a cue. §78.3.
- **Found on the way:** `SCREEN n LOOK` and `SCREEN n PRESET` write the frozen program too — the
  right thing for a cue and a remote, the wrong thing for a menu that promised the preview; the
  staged kinds are a family beside them rather than a flag on them, so the promise is in the
  kind's name. `ParsePatternKind` did not read the desk's own label ("Colour bars"); it does now.
- **Kept deliberate:** RESET refuses with no look on air rather than resetting to the programme —
  "the picture the room is watching was never a look" is a fact the operator should hear.

### 60.2 — the menu as a model

- **Done.** `Patterns.Core.Menus`: the menu as data, one builder per thing, the cue and preview
  edits by key, the JSON. §78.4.
- **Found on the way:** the follow words read "0:05 later" through the caller's duration
  formatter; under a minute the menu says "5 s".
- **The honest limit:** a drawer is one level deep by design (§78.9); the lower third's design and
  its timing are two drawers, and the person for a cue's lower third stays with the editor.

### 60.3 — the menus on the desk

- **Done.** The flyout, the attachments on every surface the ask named and the strips beside
  them, one runner on the desk, the cue menu shared with a caller node. §78.5.
- **Found on the way:** `FlyoutBase.Opening` carries no cancel in this Avalonia; the right-click
  is caught on `ContextRequested` instead, where a thing with no menu marks the request handled
  and nothing opens. A test that held a tile across a TAKE read a stale object once the wall
  rebuilt; the facts are the state's, and the test re-reads the tile as an operator's eye would.
- **Kept deliberate:** the PREVIEW pane's canvas has no menu — its pointer belongs to the crop
  band and the web page; the PREVIEW strip above it carries the menu.

### 60.4 — the wire and the deck

- **Done.** `MENU …` answers the desk's own menu as JSON; the module at 3.3.0 with the staged
  verbs on a key. §78.6.
- **Found on the way:** the module's packaging test reads the desk's `CompanionWords.Version`
  too, so a version bump is four files or none; it caught the fourth.

### Seen, and noted

- The menus are built on the right-click, from the services, in well under a millisecond on the
  facts a desk has; nothing is cached and nothing needs to be. A cue menu with two hundred
  looks would be the first to feel it, and the drawer scrolls.
- The cue menu on a caller node edits the mirrored stack as the row's own verbs do; whether the
  desk sees the edit is the link's business (the diff offer), unchanged by this round.
- One flake seen once in the round's four full App runs: the twin's "standby taking over by
  itself waits for the switcher" test failed while the module's node tests and a core build ran
  beside the suite, and passed alone and in the runs after; a timing test under load, not this
  round's.

Counts at the end of the round: Core 665, Rendering 602, Devices 7, Audio 9, Assistant 36,
Audience 2, App 673 — 1,994 in seven suites, every one green here, the module's seventeen beside
them.

## Round 61 review — the changelog, a roll-back on GitHub, ASIO and DirectX

### 61.1 — the changelog

- **Done.** `CHANGELOG.md`: rounds 61 to 15 from the history, 14 to 1 from the plan, one
  entry each with its tag, its sections, its count and the module's version where it moved;
  `ChangelogTests` keeps it. §79.1.
- **Found on the way:** a third of the commits before round 39 do not name their round; the
  boundaries were read from the plan's sections and the dates (round 16 ends with the encoder
  process, 17 with the Fractals page, 26 with the show's files off the frame budget, 27 with the
  MIDI device), and the tags record that reading.

### 61.2 — the roll-back

- **Done.** The tags as a script (`tag-rounds.sh`: the table of each round's last commit,
  the newest at HEAD, idempotent), the tag trigger and the release job in build.yml, the
  rollback workflow and its script, the release-notes script, the README's section. §79.2.
- **Found on the way:** this session's push reaches the working branch and nothing else — the
  push of the tags answered 403 five times while the branch's push went through — so the tags
  are one command for a maintainer rather than a push from here, and the script that makes them
  is in the tree, proved against a bare scratch remote: a first run makes forty-seven and pushes
  them, a second finds them all on the remote, a tag at another commit is said and left, a round
  missing from the table stops it before anything is made.
- **Found on the way:** a push made with the workflow's token starts no build, so a roll-back
  that only pushed would leave the branch unbuilt; the workflow starts the build itself, and
  build.yml gained `workflow_dispatch` for it. A roll-back that took the workflows with it would
  take that trigger away; the workflows stay on the branch unless named. The release job takes
  the run's own artifacts through `gh run download` — the same API as the download action, with
  no action version to pin from a machine that cannot look one up.
- **Tested by hand** in a scratch clone with `DRY_RUN=1`: everything back to round 58 by tag
  (the tree equal to the tag's outside `.github`, `.github` kept); the docs and the module alone
  by commit (the rest untouched); nothing to do; an unknown version refused; a path missing at
  the version refused before anything is touched; a new branch made from the branch the run was
  started from. The notes script on a round tag, a missing round and a show tag.
- **The honest limit:** `main` is at round 28 and the working branch at 61; a
  `workflow_dispatch` workflow is a button on the Actions page only once its file is on the
  default branch, so the roll-back button waits for the merge. The tags and the Releases do not
  wait for the merge, only for the one command: a tag push runs the workflow at the tag, and
  `round-61`'s is the first to reach the release job — it has not run from here.
- **Kept deliberate:** no Release for the historical tags (forty-six builds of old trees); the
  first Release is `round-61`'s, and every tag after it gets one the same way.

### 61.3 — the assessments

- **Done.** ASIO: not as a blanket change and not now — WASAPI exclusive mode with a latency
  setting and a channel map per destination first, ASIO as an optional destination kind after
  them, behind the qualification list (§79.3). DirectX: already under every frame through
  ANGLE's Direct3D 11, DirectComposition, the DXGI flip-model chain, D3D11VA and the DXGI
  adapter list; the one direct use worth building is the decoded frame that never leaves the GPU,
  as an optional sink path with the copy path kept (§79.4).

### Seen, and noted

- The changelog's counts before round 32 are the README's own line at each round's last commit,
  which the rounds updated at their close; where a round did not move it (28 to 31 read 1,447)
  the line is repeated rather than guessed.
- `round-61`'s tag run will be the first through the release job, and it runs when the tag is
  pushed; this session's check-in reads the branch's run. A failure in the release job is fixed
  forward with a new commit and the next tag, never a moved one.

Counts at the end of the round: Core 667, Rendering 602, Devices 7, Audio 9, Assistant 36,
Audience 2, App 673 — 1,996 in seven suites, every one green here, the module's seventeen beside
them.

## Round 62 review — the menus that opened nothing, and the caller's eye and hands

### 62.1 — the right-click menus

- **Done.** The desk opens its own flyout on the context request; `DeskMenuPointerTests` drive
  the right button and the menu key through the headless input pipeline. §80.1.
- **Found on the way:** the cause was the platform's handler order — `ContextFlyout` subscribes
  `PopupFlyoutBase`'s handler first, which showed the flyout empty and marked the request handled
  before the desk's builder ran; the round-60 test never drove the pointer. Confirmed by
  decompiling the exact Avalonia the desk runs (11.3.20) rather than by memory. The headless
  `KeyPress` sends only the key's press; the context key acts on `KeyRelease`, which the test
  sends too.
- **The honest limit:** proved in the headless window, not on a Windows desk; the mechanism now
  matches what Avalonia's own `ContextMenu` does, which worked on Cues before round 60.

### 62.2 — OPEN on every hint

- **Done.** Fourteen buttons across seven sections, one command; the XAML rule as a test. §80.2.
- **Found on the way:** the test found a hint the ask had not — Fractals' effect stings name the
  Audio page — and it has its OPEN AUDIO.

### 62.3 — the pointer

- **Done.** One desk switch, off by default, in the show's Web section; the per-picture flags
  gone; the renderers read the snapshot's state. §80.3.
- **Found on the way:** two Rendering tests and one Core test had set the picture's flag; they
  set the desk's now, and the media-pattern test turns the switch on before it expects the arrow.

### 62.4 — the Library

- **Done.** The Web section with the service's colours, APPLY and ✕, the Decks chip, the rebuild
  on the Web section. §80.4.
- **Kept deliberate:** a swatch rather than a live thumbnail per saved page.
- **Found by CI:** two round-30 tests pin the chip list to its nine names; they name eleven now.

### 62.5 — the RUN monitor

- **Done.** The verb in the one vocabulary (wire, OSC, cue, checks, summary), the desk layout's
  setting, the view between the wall and the history, the menu, MENU MONITOR, STATE. §80.5.
- **Found on the way:** Main is every screen's default role, so "the first screen whose role is
  Main" is the first screen on an untouched rig and the main screen the moment a confidence or
  info screen is named — which is the right rule and the test now says so with a confidence
  screen in its rig. A main screen joined into a canvas is shown as the canvas.
- **The honest limit:** the monitor is the desk's; a caller node has none (§80.9).

### 62.6 — the caller's lower thirds

- **Done.** The strip on the Run surface through `IRunPage`, on the desk and on a caller node;
  `CallerLowerThirdsTests` on both. §80.6.
- **The honest limit:** a node's chips carry no air state of their own — `IsOnAir` and
  `IsInPreview` are the desk's tallies on the desk's objects — so on a node the chips read the
  names and the press answers with the desk's words; the LIVE strip says what is on.

### 62.7 — the test host that died, and the tile it named

- **What happened.** Three local runs of the App suite alone ended with "Test host process
  crashed" — at 559, 475 and 504 of 683 tests — with the host near 13.5 GB and no dump left (a
  kill by the OS for memory leaves none). CI ran all 683 green on the same commits. With the fix,
  the suite alone ran all 685 green in 8 m 54 s: the managed heap after every closed boot between
  41 and 299 MB and 92 MB at the last, the host's working set never past 1.3 GB where it had
  reached 13.5, and no budget left attached after any boot.
- **How it was read.** A memory line per closed boot, after a forced full collection: the managed
  figure climbed ~40 MB a boot, so every closed desk was rooted. A heap dump of the host at 49
  boots held 75 desks and 229 pipelines; `gcroot` from the oldest desk ran through one static —
  the frame budget registry the glance line and the GO's clock read — to a budget, its bus, and
  the desk behind it. The budget's label: "the main screen — …", the RUN monitor's tile of 62.5.
- **The cause.** The tile made its pipeline on every viewport change whatever its attachment. Its
  surface is the Run layout, whose content is laid out — and so joins the visual tree — only when
  the layout is first shown; the tests' desks never showed it, the viewport's binding arrived at
  boot, and the tile, off the tree with no detach in its future, made a pipeline nothing would
  ever dispose. Its budget kept the desk: one a boot, 683 boots.
- **The fix.** A tile off the surface has no pipeline: made on attach, disposed on detach, and a
  viewport that changes in between makes nothing; opening the Run layout lays its surface out and
  the tile makes its pipeline then. `MonitorTileLifetimeTests`: a tile through attach, retitle,
  detach, a change while detached and a return; and a desk whose monitor has no pipeline while
  the Run layout is closed, one once it opens, and nothing of its own left in the registry once
  closed. `FrameBudgets.Attached` shows the registry. The test harness measures on request
  (`PATTERNS_TEST_MEMLOG`) — the managed bytes after a full collection, the working set and
  every budget still attached, per boot — and collects when the host has grown heavy.
- **Found on the way.** `DeckConversionAppTests` counted the stand-in LibreOffice's starts the
  moment after a reload; the converter runs on its own lane, and a Settle pumps the desk without
  waiting for a lane. It read one where two were coming, twice in a day here; the test waits for
  the start now.
- **Honest notes.** The round-60 note that blamed load beside the suite was wrong: the suite
  alone died. CI passed all 683 with the leak because its runner had the room — a green CI is not
  a clean host, and the measure is now a column, not a guess. On the desk itself this was one
  idle pipeline made at start for a Run layout not yet opened, nothing an operator would see; the
  rule closes the class, since any tile given a viewport off the tree — a wall rebuilt while its
  page is being replaced — was the same leak waiting.

### Seen, and noted

- Every unit of this round is on the desk's own paths: a menu opens through the input pipeline,
  a verb runs through the action layer, a setting lives in the show. Nothing was patched in a
  view.
- The right-click fix was the first item because a desk that shows nothing on a right-click is
  a broken desk; the regression test is the one that would have caught round 60.
- The App suite's host crashes were first put down to load beside the suite, as in round 60. They
  were the desk's own leak (62.7), found the moment it was measured rather than explained.

Counts at the end of the round: Core 672, Rendering 602, Devices 7, Audio 9, Assistant 36,
Audience 2, App 685 — 2,013 in seven suites, the module's seventeen beside them. The App suite
alone, measured boot by boot, holds at ~45 MB managed after every boot where it climbed 40 MB a
boot, and ran to its end green (62.7).


## Round 63 review — CUT and TAKE on a tile, the output on its display's clock, the Preview's menus, overlays that arrive

### 63.1 — CUT and TAKE on each screen tile

- **Done.** `ScreenTake` / `ScreenCut` across the vocabulary; the executor through the sandbox's
  send with a cut; the tile's keys and its menu's TO AIR group; the desk's refresh; the module's
  `screen_take` and presets, version 3.4.0. §81.1.
- **Found on the way:** the tile's menu had no TO AIR group at all — the programme's had TAKE and
  CUT, a screen's had only the staged verbs. It has one now, with the same reasons.
- **Found by CI:** two tests looked the wall's big TAKE key up by its word alone and found a
  tile's own TAKE first — hidden until EDIT SAFE opens, so "not visible". `ShellTests` and
  `DeskLayoutTests` name the big key by its class now.
- **The honest limit:** the look tally reads the result by fingerprint, so the words follow the
  next tally refresh — the desk refreshes it on the press, the wire's STATE reads it on the poll.

### 63.2 — the output on its display's clock, the chip's truth

- **Done.** `OutputRate.Present`, the display's refresh on the viewport, the pipeline counting the
  beats it is offered, the budget's target following; the chip with the pixels, the shape, the
  rate presented at and the display's refresh. §81.2.
- **Found on the way:** Avalonia's render clock is one output's vblank for every window — read in
  the decompiled Win32 platform, not guessed — which is why a 50 Hz screen beside a 60 Hz desk
  monitor was drawn at 60. The pacer serves a slower display; a faster one is the platform's to
  serve, and is said so in §81.6.
- **The honest limit:** the measured beat takes a second of continuous frames to know; a static
  picture never paces, and needs none.

### 63.3 — the Preview picture's own menus

- **Done.** `Menus.SubjectAt`, the pane's hit under the pointer, the routing to the overlay, layer
  and countdown menus, the preview menu's SOURCE group and its edits. §81.3.
- **Found on the way:** the frame's hit map, built for the drag in round 30, was exactly the
  answer to "what is under the pointer" — no second layout, no guess.
- **The honest limit:** a hit is where the last frame drew the thing; a right-click before the
  first frame of a pane opens the preview's own menu.

### 63.4 — overlays and layers that arrive and leave

- **Done.** `AppearanceConfig`, the tracker per sink, the arriving and leaving draws, the slide,
  the cadence, the pages' settings. §81.4.
- **Found on the way:** `FrameStageEngineTests` counted a layer stage on every frame once the
  layers were given every frame; a frame with no layer notes none now. `LayerTests`' nudge test
  rendered three snapshots into one sink and expected boxes, not arrivals — it switches the
  show's transitions off, as its question is the boxes.
- **Found by CI:** the two Preview menu tests of 63.3 drew one frame after switching the clock or a
  layer on and asked the hit map — empty: the first frame of an arrival drew nothing, at a
  presence of nothing, and the box went on the map a frame later. An arriving thing is drawn from
  its first frame now, through a veil at its presence (a layer's placeholder and border included,
  which had shown whole), so its box is on the map the frame the show says it is there; the
  Rendering test that asked for "nothing to take hold of" on that frame asks for the handle.
- **The honest limit:** the departure draws from the snapshot that last had the thing — a message
  whose words changed in the same publish that switched it off leaves with its old words, which
  is what a room saw.

### Seen, and noted

- Every unit of this round is on the desk's own paths: two verbs through the action layer, a
  rate rule in Core with the platform's clock measured rather than assumed, a menu built from
  the frame's own hit map, arrivals kept per sink where the frames are drawn.
- The chip's "60 fps on a 50 Hz display" was true and misleading at once: it counted frames
  drawn. The truth an operator needs is the frames the room gets, and the rate is now paced to
  give them.

Counts at the end of the round: Core 700, Rendering 608, Devices 7, Audio 9, Assistant 36,
Audience 2, App 691 — 2,053 in seven suites, the module's seventeen beside them.

## Round 64 review — the frame's lifetime, the pacer's epochs, the census, the appearance matrix, a release that can be rebuilt, the rig's record

### 64.1 — explicit frame lifetime

- **Done.** Seats as tickets (id with an epoch), `BeginFrame` / `EndFrame` on the pipeline with
  the canvas flushed before the close, `Touch` / `Mark` / `Check` reading Clear, Open or Hung,
  retirement that requires an owner, quarantine over any forced free, `FenceFault` records,
  `hungFrames` / `quarantinedMB` / `fenceFaults` on STATE, the Machine page and the super-check.
  §82.1.
- **Found on the way:** the old tests asserted the forced free and the ownerless retire; both
  were behaviours the review called defects, so the tests were rewritten to the new rules rather
  than kept.
- **The honest limit:** a hung sink's pictures wait in quarantine for as long as the frame is
  open — a render thread that never returns holds its memory until the pipeline is disposed,
  which is the truth the old code hid by freeing under it.

### 64.2 — pacing epochs and the render-clock warning

- **Done.** `FramePacerState` with epochs; `OutputRate.SameFamily` and `ClockLimit`; the chip's
  *LIMITED BY RENDER CLOCK*; the Machine reading, the *Render clock* super-check row, STATE's
  `renderClockHz` / `clockLimited`, the assistant's facts. §82.2.
- **Found on the way:** `RenderContext` is a struct, so its clock field could carry no
  initialiser — `-1` is set by the pipeline, and a zero reads as not measured, which is the
  evidence rule (P1.8) in the type.

### 64.3 — the lifetime census and the lifecycle test

- **Done.** `LifetimeCensus`, `DeskCensus`, `DeskTimers` (every dispatcher timer named by its
  maker), the static hooks cleared, `OnWindowClosed`, the view models registered with their desk,
  Shutdown on per-step guards, *a closed desk does nothing* (the publish path, the debounces, the
  wire's push, the pages, the twin), `LifecycleTests` (a dozen cycles in the suite, a hundred
  strict in CI), `AChangeAfterTheDeskClosedArmsNoTimer`. §82.3.
- **Found on the way — eight roots, one at a time, each by a heap dump:** two statics
  (`DragReorder.Moved`, `UiFaults.Listener`); the beacon's last continuation on the dispatcher;
  the view model's status timer; the kernel's mDNS timer; the save timer re-armed by the last
  publish; the pages' debounces re-armed after the close; and the twin's beat restarted on a
  closed desk by a queued key edit that republished — the one that would have held a port open
  in the product after an exit. Each is a rule now, not a patch.
- **Found in the suite:** the census only tells the truth when every test closes its desk, and
  forty-odd test files did not. The host closes a forgotten boot at the next boot and names the
  test in the memlog (`late_close`) rather than the product carrying the blame; the leaks that
  remained after that were the product's, and are fixed.
- **The honest limit:** the strict baseline (every count zero) holds in a process of its own;
  in the shared suite the test asserts the census returns to what the first cycle read, because
  earlier tests leave what they leave.

### 64.4 — the appearance matrix

- **Done.** `AppearanceMatrixTests` over every key by Fade, Cut and Slide at five moments;
  `LayerPicture` and `PipKey` as exact identities. §82.4.
- **Found by the matrix:** the PiP's arrival never showed — both copies were marked as the fade
  source. `DrawPipAt` marks the outgoing copy only.
- **The honest limit:** the layer swap rides the whole-picture crossfade (§82.9).

### 64.5 — release reproducibility and the Windows lane

- **Done.** The libVLC payload from the restored package's exact version, `manifest.json` and
  its copy on the Release, `--verify-runtime` on the built bundle, `windows-smoke`,
  `rollback-script`. §82.5.
- **The honest limit:** the Windows lane's first run is this push's; the desk's classes chosen
  for it are the ones expected to hold headless on Windows, and the list may need trimming or
  growing after CI reads.

### 64.6 — hardening

- **Done.** Hysteresis with dwell on the pressure ladder (a clock a test can hold), the
  roll-back script validated and tested in CI, the forced free retired as a gate in favour of
  the fence's own numbers, the evidence rule realised. §82.6.
- **Found on the way:** the ladder's App test assumed an instant step-down and was wrong under
  the new rule; it now walks the rungs one dwell at a time.

### 64.7 — role-specific node compositions

- **Done.** `NodeKinds.RunsRoom` / `RunsArcade` / `Composition`; the room and the arcade built
  only on the arcade node and the desk; nullable capabilities with a closed door on the wire, the
  audience port and the routes; `ARoleBuildsOnlyItsModules`; CI's own-process run; the Windows
  lane's `--footprint` process; MODULES.md §5 rewritten. §82.7.
- **The branch model:** recorded in §82.7 as a recommendation; the merge to `main` and the
  protection are the maintainer's to do.

### 64.8 — the qualification record

- **Done.** `docs/QUALIFICATION.md` with six matrices and a blank record; SOAK.md's gates
  extended. §82.8.
- **The honest limit:** nothing in it is a claim until the rig fills it in (P0.2).

### Found by CI (run 230)

- The footprint step had been inserted into the companion-module job rather than the lifecycle
  job (a step anchor that matched the wrong neighbour); it is in the lifecycle job now.
- The assistant's attachment text joined rows and slides with the platform's newline, so the
  Windows lane read `\r\n` where the tests expected `\n`; the text is the model's, not the
  machine's, and joins with `\n` on every platform.
- `MediaMemoryTests`' trim test held two pictures on both runners: a test earlier on the same
  thread had advanced a seat and moved on, leaving the thread's current seat set, so the trim
  test's picture fetches were recorded as draws of a frame nobody would close. The test now
  resets the fence as its premise, and `Advance` says so.
- The lifecycle job passed strictly at a hundred cycles on its first run; rollback-script passed;
  the Windows lane's desk classes, runtime check and footprint were skipped behind the newline
  failures and run for the first time on the next push.

### The review's items, answered

P0.1 done (64.1). P0.2 prepared, not run — the record is written. P1.1 done (64.2). P1.2 done
(64.2). P1.3 done (64.3). P1.4 done (64.5). P1.5 done, first run pending (64.5). P1.6 done
(64.4). P1.7 answered by removal: there is no forced free to record; `hungFrames` and
`quarantinedMB` are the gates. P1.8 realised in the types (64.2). P2.1 done (64.6). P2.2 done
(64.7). P2.3 done (64.6). P2.4 recorded for the maintainer (64.7). P2.5 done (64.4). P2.6 kept:
`ModuleRulesTests` still passes, and the node's footprint shrank rather than grew.

## Round 65 review — the rig's truth: the machine, each link's contract, the far end's word, and a rig commissioned on evidence

### 65.1 — the arcade's section built only on an arcade node

- **Done.** The node window builds the arcade's section in code, only when the host has an
  arcade; every `Arcade*` and `Play*` getter of the node view model asks `HasArcade` / `HasRoom`
  first and reaches the module through a method compiled only on a role that has it; the strict
  footprint test builds the view model and the window for the timer and the caller, polls once,
  and asserts the tab stays empty and the assemblies stay out. §83.1.
- **Found on the way:** the Linux footprint test had been green for a round while the exe on
  Windows loaded both assemblies — a XAML instantiation and a bound getter's type are loads the
  class-level test never saw. Run 231's Windows lane found it; run 232 read the exe clean.
- **The honest limit:** the separate-process claim is the Windows lane's (`--footprint` on the
  built exe); on Linux the class runs in its own process, which is the same claim short of the exe.

### 65.2 — the watchdog's startup deadline

- **Done.** `SupervisorPolicy.StartupDeadline` (120 s) and `Phase`; a child that never beats is
  a startup hang — killed, noted in the crash note, restarted under the crash back-off, counted
  against the loop cap. `WatchdogStartupTests`. §83.2.
- **Found on the way:** the supervisor judged the heartbeat only once a first beat had arrived,
  and the beat begins after the framework's own initialisation — the exact window a wedged
  graphics device or an output takeover falls in.
- **The honest limit:** 120 s is a policy number; a machine that legitimately takes longer to
  start is restarted once, and the loop cap still holds it.

### 65.3 — the render fence fails closed

- **Done.** `RenderLocked` takes the seat first; no seat means black, a flush and a return, whole
  again on the first frame after a seat frees; refusals listed and recorded as faults;
  `fenceRefused` on STATE; the red *Fence seats* row naming the output; the *Render clock* row's
  FIX words first. §83.3.
- **Found on the way:** a second hole beside the first — a seat reseated while its sink sat idle
  left the sink's later frames unfenced; the same fix (take the seat first) closes both.
- **The honest limit:** a sink without a seat shows black, on purpose: visible on the wall and
  named on the row, never a pooled frame outside the fence.

### 65.4 — the shutdown as phases, the final save bounded, no DNS on the desk thread

- **Done.** Eight named phases, every step on its own guard and on `ShutdownReport`;
  `FailShutdownStep`; `ShutdownPhaseTests` fails each step in turn and proves the rest ran; the
  final save serialised on the desk's thread and queued on the file lane behind the autosaves,
  waited `ExitSaveWait`; the recovery record kept when it did not land, with a note for the next
  start; `LocalAddresses` reads the interfaces. §83.4.
- **Found on the way:** the exit awaited the lane for ten seconds and then saved synchronously
  under the same lock — a dead share could hold the exit forever; and `Dns.GetHostAddresses` on
  the desk's thread stalled the status strip on a venue whose resolver did.
- **The honest limit:** an exit can end with the show unsaved — the truth is a record kept and a
  note, not a second save that never returns.

### 65.5 — one writer per remote client, explicit trust

- **Done.** `WirePeer`; `ControlConfig.Bind`; `PairingToken` and `ControlConfig.Token`; `AUTH`,
  `X-Patterns-Token`, `X-Patterns-Pass`; the TRUST block, the *Remote* row, module 3.5.0.
  `WirePeerTests` (eight threads, no line interleaved), `PairingTokenTests`, `RemoteTrustTests`.
  §83.5.
- **Found on the way:** the handler and `Broadcast` were two writers on one socket; `?pass=` in
  the pages' URLs was in every history and proxy log.
- **The honest limit:** reads stay open by design (a tally page, a deck's feedbacks, a support
  engineer's look), and a twelve-symbol token is a venue LAN's guard, not the internet's — the
  bind address is what keeps the ports off the wrong network.

### 65.6 — signal truth: the contract and the observation

- **Done.** `SignalContract`, `SignalRate` (exact rationals, families), `SignalWords`,
  `DisplayObservation` (QueryDisplayConfig, advanced colour, kept two seconds, `Source`),
  `SignalTruth.Compare`, the technical view, `SCREEN n SIGNAL`, STATE, Super Check's SIGNAL rows,
  the journal, the brief. §83.6.
- **Found on the way:** the pacer's 2.5 % rate rule had been standing in for engineering truth —
  59.94 read as 60 — and a driver's 59940/1000 needed snapping onto the canonical rational, not
  rounding; a field the driver never fills had to stay *not available*, which is more grey than
  a screenshot likes and the only honest reading.
- **The honest limit:** the verdict is Windows' word against the contract; Windows reports what
  it asked the display for, which is why 65.7 and 65.11 add the other two witnesses.

### 65.7 — the EDID read and not judged

- **Done.** `Edid.Parse` (base, CTA-861, DisplayID 1.3 / 2.0; every checksum; SHA-256),
  `EdidInfo`, `EdidReader` (the registry, once per path, `Source`), ADVERTISED in the view, the
  EDID and advertised rows, the JSON's `edid`, the brief's rule, `--signal-report` on the
  Windows lane. `EdidTests` over a synthetic Patterns LED EDID with every checksum computed.
  §83.7.
- **Found on the way:** a 60 Hz VIC carries 59.94 too, as CTA-861 has it, so a capability list
  that said only 60 would have marked a 59.94 contract amber for nothing; the monitor's device
  path does not always lead to the instance key that holds the EDID, so the reader searches the
  display keys by manufacturer and product bytes when it does not.
- **The honest limit:** an EDID emulator's EDID reads as the display's — the reader cannot tell
  a processor's emulated EDID from a panel's, and does not try; ADVERTISED never moves the verdict.

### 65.8 — the planned screen's EDID

- **Done.** `EdidPlan.ForContract`, `EdidWriter.Build` / `Hex` / `Export`; `SCREEN n EDID`,
  `GET /api/screens/<n>/edid.bin|.hex|.txt`, the Planned EDID block and EXPORT EDID, the
  *Planned EDID* line green when the display presents it. `EdidWriterTests`,
  `PlannedEdidAppTests`. §83.8.
- **Found on the way:** 1920×1200 at 50 through CVT-RB lands on 49.98 unless the vertical total
  is walked onto the 10 kHz pixel-clock step — so the writer walks it, and the rate is exact; a
  raster past 4095 does not fit an 18-byte descriptor and needs DisplayID, with a 1080 line in
  the base for a source that reads none.
- **The honest limit:** the planned EDID is the desk's offer; loading it into the processor or
  the port's emulator is the engineer's step, and TEST ROUTE never changes a display's mode.

### 65.9 — the machine fully exposed, the Known Good Rig

- **Done.** `MachineProbe` → `MachineFacts` (the edition, the CPU and memory, DXGI's adapters
  with the display class key's drivers, every display with its EDID, WASAPI's endpoints,
  powrprof's plan, the scheduling switches), read on a worker and kept; `RigSnapshot`,
  `RigDrift`, `KnownGoodRig` on the kernel; `RIG SAVE`, `RIG STATUS`; the RIG rows, STATE's
  `machine.inventory` and `machine.rig`, the Machine page's blocks, the brief. `RigSnapshotTests`,
  `KnownGoodRigAppTests`. §83.9.
- **Found on the way:** a first draft blocked the desk's thread on the probe — `Read()` now
  answers the kept reading and refreshes on a worker, `ReadNow()` is for SAVE, the bundle and a
  test; an App test awaited a router answer on the UI thread and deadlocked (the headless
  dispatcher must be pumped, never `.GetResult()`); a `DisplayFact` already existed in the core,
  so the machine's is `MachineDisplayFact`.
- **The honest limit:** the Windows paths ran on the Windows lane's Hyper-V runner — one path at
  1024×768 reported as 1 Hz and shown as 1 Hz, one EDID, no discrete GPU, no audio endpoint; a
  real rig has not run this code (QUALIFICATION.md §8).

### 65.10 — the commissioning flow

- **Done.** `Commissioning.Build` over `CommissioningFacts`; TEST ROUTE and the diagnostic
  profile; `COMMISSION STATUS`; STATE's `commissioning`; the COMMISSIONING block; the
  *Commission the rig* walkthrough; the assistant's facts and rule; module 3.6.0.
  `CommissioningTests`, `CommissioningAppTests`. §83.10.
- **Found on the way:** the facts had to say what a lost display is regardless of Enabled, what
  a planned screen is excluding the lost, and skip disabled placements — the first App test
  showed a boot display counted as lost the moment a handed-in observation replaced it;
  `CompanionPalette.States` needed the `signal` and `rig` colour families on the desk's side
  before the module's test would pass.
- **Found in the suite:** one App run failed one test with the name swallowed by a quiet logger;
  the rerun with names was 717 of 717. It is recorded here as an unnamed flake, not dismissed —
  the next quiet run keeps the names.
- **The honest limit:** OUTPUT TEST is *the outputs opened this run*, not *the engineer looked at
  the wall*; a stage is green only for what the desk can see.

### 65.11 — what the far end receives

- **Done.** `InputStatus` (the query, the pattern with `w`, `h`, `hz`, `enc`, `bits`; PJLink
  class 2's `IRES ?` published, a class 1 `ERR1` as no answer), `DeviceConfig.Input*`,
  `DeviceService.InputReported`, `ScreenPlacement.Received / ReceivedBy / ReceivedAtUtc`,
  `SCREEN n RECEIVED`, the Received lines and MISMATCH on the box's word, RECEIVED and FORGET on
  the page, the journal, ENDPOINTS.md's *Input status*. `InputStatusTests`,
  `InputStatusAppTests`. §83.11.
- **Found on the way:** the received lines were first added in the no-contract branch, which
  cannot judge anything (a grey line is all it may say); the device's report was subscribed
  before the desk's actions existed; a bare `SCREEN n RECEIVED` parsed as the toggle it is not,
  so it is an unknown line now.
- **The honest limit:** the processors' own APIs (Brompton, Novastar, Barco, Christie) are typed
  from their manuals into the card, not shipped as presets — they differ by model and firmware
  and none was verified here.

### 65.12 — the architecture

- **Done.** `Patterns.Platform.Windows`; `PersistenceRuntime` in the core with the desk
  delegating; `Secrets` with the bundle and the twin reading it; `docs/ADR.md` (ADR-001 to
  ADR-011); MODULES.md rule 8; `ModuleRulesTests`' row and source scan; `PersistenceRuntimeTests`,
  `SecretsTests`, `PlatformSeamsTests`. §83.12.
- **Found on the way:** the bundle's redaction is `SupportBundle.Redact`, not `RemoteAdmin`'s
  as the plan had it; `AdminPasscode` lives under `Install`, which the first secrets test found
  by naming the wrong section; `PrepareRestart` still waited on the desk's old lane after the
  peel; the lane's every type is the core's, so it went to the core rather than the desk and is
  tested without one; xUnit's analyser refused a blocking `Wait` in a test — the lane tests await.
  The first full App run then failed three tests with a null reference: the constructor writes a
  migrated show back once (`SaveNow`) before it had built the lane — the lane is built right after
  the kernel now, before the first save can ask for it, and the startup, recovery and takeover
  tests that found it are green.
- **The honest limit:** every role loads the platform assembly (the desk sets the build version
  on it first thing); the footprint step measures the cost. The lane's behaviour is unchanged by
  design — the round moves code and adds tests, it does not change what lands on the disk.

### Found by CI (runs 231–236)

- Run 231's Windows lane launched the timer node and read the arcade's and the room's assemblies
  loaded — 65.1 came from it; run 232 read the exe clean.
- Run 233's signal-report step read the Hyper-V runner's one path — 1024×768 at 1 Hz, reported
  as 1 Hz, never rounded — and its EDID (MSH 062E), both checksums valid: the QueryDisplayConfig
  layouts, the advanced-colour call and the registry read proven on a real Windows.
- Runs 234, 235 and 236 (65.8 with 65.9; 65.10; 65.11) were green on every lane, the module's
  package test and the Windows lane's signal report included.

### The review's items, answered

P0 done (65.1). P1.1 done (65.2). P1.2 done (65.3). P1.3 done (65.4). P1.4 and P1.5 done (65.5).
P1.6 done (65.4, then peeled in 65.12). P1.7 done (65.4). The roadmap handed in during the round
is answered line by line in PLAN §83.14: its Phase 1 was these items; its Phase 3 is 65.6 to
65.11; its architecture items are 65.12 or a recorded decision (ADR-010, ADR-011); what was
deferred says why.

## Round 66 review — the God's Eye: the whole show as one picture, its problems worst first, the eye moved from the desk, a key or the wire

### 66.1 — the method, read and transferred

- **Done.** The referenced project read for its method — click-to-track focus with the rest dimmed
  by distance, a contacts roster, a global context that restores the exact view, a terse headline
  regenerated with the view, questions grounded in the thing's live facts, share links as
  handoffs, one reset — and each carried into a Patterns thing; the field's overview tools and
  their gaps surveyed; the design written before the code (`docs/EYE.md`). §84.1.

### 66.2 — the picture in the core

- **Done.** `EyeFacts` → `EyeGraph` (nodes with kinds, planes, tiers, lights by the evidence rule,
  words, routes, menu kinds; edges with kinds and lights), a deterministic grid layout by band and
  tier, `EyeCamera` on a critically damped spring, lenses, hop distance and emphasis, the headline
  and the problems queue, `Resolve`, `EyeJson`, `EyeMenus`, the wire grammar (`EYE`, `EYE FOCUS`,
  `EYE NEXT` / `PREV`, `EYE LENS`, `EYE RESET`) and its desk-only reasons; ten Core tests. §84.2.
- **Found on the way:** a breadth-first walk that expanded through the desk hub made everything
  two hops from everything, so a focus dimmed nothing — the walk now stops at the desk unless the
  desk is the focus. A planned display counted as present made a planned screen *showing*; the
  rule reads present as *found and not planned*. The problems queue's first expectation in the
  test named the red screen; the queue puts the red source that feeds it first — the cause before
  the symptom — and the test was corrected, not the rule. `IReadOnlyList` has no `IndexOf`: the
  graph answers `ProblemIndex` itself.

### 66.3 — the picture on the desk

- **Done.** `EyeService` (the facts gathered on the tick, hashed, the picture rebuilt only when
  they moved, the verbs through the action layer), EYE above NODES in the rail with the worst light
  as its hue and the headline as its line, the Eye page (the canvas with pan, zoom at the pointer,
  click, double-click, hover and the keys; the lens chips; PREV / NEXT PROBLEM / RESET; the card
  with the words, FOCUS / OPEN / ASK and the LINKED TO rows), right-click opening the thing's own
  desk menu or the Eye's, the router's `EYE` and verbs, STATE's `eye` row, `WireDeck.Paired`; two
  App tests. §84.3.
- **Found on the way:** a display the desk finds arrives as a placement that is disabled until the
  operator turns it on, so the test's mismatching screen came back grey *disabled* with the
  MISMATCH in its words — the rule was right and the test enables the screen as the operator does.
  The wire answers a verb with a bare `OK` and the STATE document is `STATUS`, as everywhere; the
  test had assumed the verb's message travelled. The shell test that pins the SHOW group's chips to
  Panel and Run gains Eye.

### 66.4 — the deck, the brief and the help

- **Done.** Companion module 3.7.0 (five actions, two feedbacks, four variables, the Eye preset
  page, the `eye` colour family, a config group; the fixture's `eye` row; a node test; the lines
  file with ten EYE lines the desk parses), `ShowFacts.Eye` with its rule in the brief and the desk
  gathering it, the `gods-eye` help topic, REMOTE.md and COMPANION.md; a Core test for the help, an
  Assistant test for the brief. §84.4.
- **Found on the way:** the module's palette families are read from `palette.js` by the desk's
  test and held equal to `CompanionPalette` — a new family lands on both sides or the Core suite
  says so.

### Found by CI

- Run 237 — the round-65 papers (65.13), the last push before this round — was green on every
  lane, the module's package test and the Windows lane's signal report included; round 65 closed
  with nothing found by CI after run 236.
- The round's commits went up together after every suite ran green here (Core 795, Rendering 648,
  Devices 7, Audio 9, Assistant 37, Audience 2, App 721; the module's nineteen). CI's verdict on
  them — the Linux lanes, the module's package test and the Windows lane — is read at the
  check-in after the push and, if anything is red, is the first item of the next round.

### The review's items, answered

The round-65 review left no open items; the roadmap's deferred rows (PLAN §83.14) stand. This
round answers the field's request for one picture of the show with the picture in the core, on
the desk, on the deck and in the brief, and records what it is not (§84.6): evidence, never
authority; the desk's alone; the lights of now.

## Round 67 review — the switcher's rules, the tile's isolation, the next take and the group

### 67.1 — research and design

- **Done.** The switcher's five scopes, the wall tile, the editing target, the library flow, OWN,
  the transitions and "group" read as they stood; the gaps named before the code (the keys read the
  arming alone, a LOCKED tile's own TAKE went through, a library tile ignored the editing target,
  OWN never followed an edit, the multiview's NEXT TAKE line never read the scope). §85.1.

### 67.2 — the take rules enforced

- **Done.** `TakePlan` is the one resolver behind CUT, TAKE and the wire; LOCKED never, ARM inside
  every scope, a repeater never, everything outside kept; a plan that would move nothing is a
  refusal with the reason. The wall shows the plan before the press; the snapshot's `TakeHeld` and
  the multiview read it; ticks are spent only by a take that read them. §85.2. Tests: TakePlanTests
  (7), TakeRulesAppTests, MultiviewTallyTests.

### 67.3–67.5 — the tile's isolation, the library and Own on edit

- **Done.** Every wall target is an editing target both ways with BUILD → PATTERN; an inert copy
  of the programme is the editor's picture until the first edit, when a `ChangeTracker` flips the
  target to its own picture at once; the tile's CUT / TAKE sends what its PVW shows; a library
  tile lands in the target's preview under EDIT SAFE. §85.3. Tests: TileIsolationAppTests,
  LibraryFlowAppTests, EditTargetAppTests.

### 67.6 — the next transition

- **Done.** `NextTransition`, `NextTakeService`, the drawer on every TAKE, the key face, `TAKE
  NEXT` on the wire, STATE's `take` row, the Eye's word; `StingerAfter.Take` with the scoped cover,
  the restore before the take, FOCUSED pinned at the press, the ticks spent as the take lands, the
  landing never spending a one-shot set meanwhile. §85.4. Tests: NextTransitionTests (4),
  NextTransitionAppTests (3), the stinger suites unchanged and green.

### 67.7 — the group in the tile's menu

- **Done.** THIS TILE → Group on the role verb with `SCREEN n GROUP` as its wire line; the Screens
  page's arrangement answers a right-click with the same menu; a canvas sets every screen in it;
  one refresh from the verb's hook whoever changed the group. §85.5. Tests: DeskMenuTests,
  WireVocabularyTests, GroupMenuAppTests.

### 67.8 — everything follows

- **Done.** The Eye's screen nodes carry LOCKED, held, ticked and the canvas; the desk node the next
  TAKE's plan; STATE's rows `ticked`. §85.6. Tests: EyeTests, TakeRulesAppTests.

### 67.9 — the deck and the papers of the wire

- **Done.** Companion 3.8.0 with the take and group actions, feedbacks, variables and a Take page;
  the palettes equal; lines.txt every line the desk parses; REMOTE.md, COMPANION.md §14, the
  module's README and HELP, the switcher's help. §85.7. Tests: the module's twenty,
  CompanionModuleContractTests, CompanionPaletteTests.

### Found on the way

- A tile's TAKE sent the programme's preview, not the tile's own staged picture — fixed with
  `SendToTargets(ownPicture: true)` reading `LookService.Shown`.
- The library had no EDIT SAFE guard: a tile applied went straight to air when EDIT SAFE was off —
  fixed; brand kits stay live by design.
- The multiview's NEXT TAKE line was scope-blind — it now reads the snapshot's `TakeHeld`.
- `ActionSpec` gave TAKE / CUT a bare shape; they carry a place and a transition now, and
  `NextTransition` is desk-only with its own row.
- Unlocking keeps OWN with the pinned picture (the lock pins the picture as the screen's own) — kept
  as it was and tested for what it is: a take never overwrites a screen's own picture; OWN off or
  Follow the programme does.
- A screen whose hot-plugged placement is disabled reads grey on the wall — a found display starts
  disabled by design; the tests enable it.
- Under a sting the ticks were spent at the press (`Requested` counted as `Ok`) — the desk spends
  them only on `Done` now; FOCUSED was re-read at the clip's end; the clip-end take consumed a
  one-shot set during the clip; a sting for one tile covered every screen and its dead frame was
  pinned on the screens outside the scope when the take landed — all four fixed in 67.6.
- The drawer's on-marks compared the whole wire line, so "wipe left 800" marked nothing — the kind
  and the rate are matched apart now.
- The Screens page's picker rebuilt the wall itself; the verb's hook does it for every origin.
- "is a info screen" — the article follows the word.
- `SCREEN n <unknown word>` toggles the screen's output (pre-existing); GROUP joined the bare words
  that are unknown, the default is recorded in §85.9.
- No round tag has ever reached the remote and the tag script's table stopped at round 60, so the
  maintainer's one command would have halted at round 62 ("neither in the table nor a tag"): rows 61
  to 66 added with each round's last commit — the parent of the next round's first — and round 67 is
  HEAD, as the script expects.

### Found by CI

- Run for 2e339e5 (the round-66 papers) was read at the check-in — see the next round's first item
  if anything was red; this round's commits go up together after every suite ran green here (Core
  808, Rendering 648, Devices 7, Audio 9, Assistant 37, Audience 2, App 731; the module's twenty).

### The review's items, answered

The round-66 review left no open items. This round answers the field's request that the desk's
words be the desk's facts: the plan under the picker is what the press does, a tile's menu and
its switches read one truth, and a transition chosen for one take is spent by that take alone.

## Round 68 review — web video smooth and cheap: a buffer on a locked clock, pooled decode, capture by policy, the browser out of the chain

### 68.1 — research and design

- **Done.** The chain and its per-stage costs read from the code; OBS, vMix, CEF, WebView2's
  capture paths and yt-dlp surveyed from their sources; eight options ranked by what they buy and
  what can be proven here; the design recorded and then realigned to what was built. §86.1,
  `docs/WEB-VIDEO.md`.

### 68.2 — the pipeline's cost cut

- **Done.** `WebFramePipeline` over the clips' `FramePool` with a Queued slot state; duplicate
  frames skipped before any decode; `WebCapturePolicy` by machine, ladder and the rig's largest
  surface; the plan applied live with a restart only on a change; the decoder flag under the
  desk's choice. §86.2. Tests: WebFramePipelineTests (11), WebCapturePolicyTests (4),
  ScreencastFrameTests, WebSmoothingAppTests.
- **Found and fixed on the way:** the first pool's creation reset the duplicate memory (the second
  identical frame decoded) — the reset now belongs to a remake alone; the smoother's cap follows
  the pool's room so a full buffer cannot starve the decoder; the App's plan lambda read `Screens`
  before the constructor assigned it (a nullable-flow error), moved below the assignment.

### 68.3 — the smoothing buffer

- **Done.** `FrameSmoother`: a phase-locked ideal clock, the cadence as the least-squares slope of
  the arrivals locked to a known rate, the depth from the p95 lateness within the class's bounds and
  the pool's room with hysteresis, Auto's band, a stall counted once, drain, cut and resume;
  `BeginLeaving` fades the page's sound over the transition and cuts at the end. §86.3. Tests:
  FrameSmootherTests (12), WebLeavingTests (2), WebSmoothingConfigTests, WebSmoothingAppTests.
- **Found and fixed on the way:** the first design's due time was `arrival + latency`, which
  carries every stray straight through — the regularity test (a jittery 30 fps source against a
  60 Hz sink) showed it, and the ideal clock replaced it; an exponential mean of the intervals
  wobbled the cadence by the very jitter the buffer removes and un-snapped the rate lock, hence the
  slope over a window and the lock; the broadcast fractions (29.97, 59.94, 23.976) made the lock
  flap between two rates a tenth of a per cent apart and left the list; the lateness measure is
  `max(0, error)` — an early frame waits, only a late one needs room; a sink that keeps asking after
  the source stops counts a stall, rightly, so the test ends a tick after the last frame's time.

### 68.4–68.5 — GPU capture and the page's sound through the mixer

- **Recorded, not built.** The exact shapes are in the paper (§4.5, §4.7) with the reasons; the
  browser path fades its own sound over the transition and the native-player path gives the
  matrix the sound outright. §86.4. The Windows bench is the open item.

### 68.6 — the native player

- **Done.** `WebVideoResolver` (pure: what applies, the argument list, the parse with the address's
  expiry, the complaint in one line, where the tool is looked for, the words) and `WebVideoService`
  (the tool off the UI thread, the answers kept, the locator's rewrite, the desk reconciled when
  an answer lands); the clip engine opens a network address with a slave; Play via on the look;
  STATE's `via` and `native`; the Media page's words and path box; the help's plain words on the
  site's terms. §86.6. Tests: WebVideoResolverTests (6), WebNativeAppTests (2).
- **Found on the way, kept by design:** on a cold take the browser stands in for the first pass
  and the clip takes over when the tool answers — the test first asserted no browser at all and
  was wrong; the paper and the help say to set the page up in the preview first.

### 68.7 — everything follows

- **Done.** STATE's `web.path`, `via` and `native`; Companion 3.9.0's variables and feedbacks with
  the module's tests; the Frames and Play via pickers; the help, the assistant's catalogue,
  REMOTE.md, COMPANION.md §15. §86.7. Tests: the module's round-68 case, WebVtAppTests' STATE row.
- **Found and fixed on the way:** the module's packaging test reads the version from the README's
  first line and its HELLO example, which a bulk replace of the version sites had missed.

### The review's items, answered

The round-67 review left no open items. This round answers the field's request that a web video
be smooth on a lower-spec machine and cheap on every machine: the buffer is sized by the machine
and the measured lateness, faded and cut when the page leaves Program; the pooled decode and the
capture plan take the cost out of the chain; and the native player takes the browser out of it
altogether where the operator chooses.

## Round 69 review — memory with a reason, the GPU cache governed, the composition root answered, audio that follows the picture

### 69.1 — research and design

- **Done.** Memory as it stood read from the code (what is held, by whom, until what); the GPU/CPU
  question answered from how the driver, Skia and Avalonia actually share the card; DI and MVVM
  measured (handlers, ambient reaches, constructors) rather than argued; the audio matrix's gap
  named. §87.1; `docs/MEMORY-RESEARCH.md` §11–12, `docs/AUDIO-RESEARCH.md` §7, `docs/ADR.md`
  ADR-012.

### 69.2 — the residency ledger

- **Done.** `Residency` (reasons, the grace by class and rung, the named pictures, the words),
  `ImageCache.SweepIdle` behind the fence with the drawn-within floor, `ResidencyService` on the
  tick with the engines' holds, the web engine's `KeepArmed`, the ladder letting armed pages go at
  critical; STATE's `memory.residency`, the Media page, the Eye, the assistant's inputs. §87.2.
  Tests: ResidencyTests (4), ImageCacheResidencyTests, ResidencyAppTests (2).
- **Found and fixed on the way:** the first sweep rule — *the newest picture is never swept* —
  conflicted with a fixed idle age and would have kept a stale picture for ever on a still stage;
  replaced by a floor of a second and a half since the last draw. The App test's first arm recipe
  called the engine's `Arm` with the wrong shape and was refused because the page was on air; the
  test now arms through the router as the desk does. `WebFrameSource.MemoryBytes` is a Windows
  reading, so the engine's hold reports it behind an operating-system guard (CA1416).

### 69.3 — the GPU cache governed, and the collector's facts

- **Done.** `GpuGovernor` (the limit table, the card's rung, the worse of two, the purge line, the
  words), `GpuCacheGovernor` through the sinks' Skia lease once a second, `ShowGc.Facts()` and the
  off-air LOH compaction, the sample's new fields, STATE's `machine.gpuCache` and `machine.gc`, the
  Machine page's lines, the qualification row and the soak gates. §87.3. Tests: GpuGovernorTests
  (4), GpuGovernorAppTests (2), RuntimeAppTests unchanged and green.
- **Found and fixed on the way:** the metric sample was taken before the governor ran, so the
  first sample of every tick carried the previous second's limit (-1 on the first) — the tick is
  now ordered the ladder, the governor, the sample, the ledger, and the App test asserts the sample
  carries the limit decided on the same tick. The memory paper's round-69 section had landed ahead
  of *Honest limits*; the sections are in order again.

### 69.4 — DI and MVVM answered

- **Done.** ADR-012 with the numbers; `ArchitectureFenceTests` — the ambient reaches by file and
  count, the code-behind handlers by file and count, no heavy work in code-behind, the engines built
  in the kernel alone. §87.4. Tests: ArchitectureFenceTests (3).
- **Found on the way, kept by design:** every reach for `AppServices.Instance` is at a seam Avalonia
  constructs itself (a tile's pipeline, the lazy page, a converter) or before the desk exists (the
  crash note, a second launch); none was worth an interface, and the fence keeps the count where
  it is.

### 69.5 — audio follows the picture

- **Done.** `AudioOutput` on the placement, `FollowPicture` on the matrix, `SourceOfScreen` /
  `FollowedRoutes` / `EffectiveRoutes` in the routing, the follow signature in the graph's topology,
  the `ScreenAudio` and `AudioFollow` actions, the wire and OSC verbs, the Screens and Audio pages,
  the Eye's edges, STATE, Companion 3.10.0, the help and the papers. §87.5. Tests: AudioFollowTests
  (7), AudioFollowAppTests, WireVocabularyTests, the module's round-69 cases.
- **Found and fixed on the way:** naming a screen's output while the matrix was off made the
  destination's row before seeding the defaults, and the seed — which fills an empty table only —
  then left the programme's crosspoint unmade; the executor now seeds first, enables, then makes
  sure of the row. A tooltip that named `<output>` in angle brackets did not compile as XAML.
  The xUnit analysers asked for `Assert.DoesNotContain` over a negated `Contains`.

### 69.6 — everything follows

- **Done.** The Eye's desk node reads the memory line — counts by reason and the cache's bound and rung,
  never the fill or the countdown, so a number that moves every second does not rebuild the picture every
  second; Companion's `machine_memory_held` and `machine_gpu_cache` with the module's fixture and its
  variables case; COMPANION.md §16. §87.6. Tests: EyeTests' desk-node case, EyeAppTests' desk node, the module's variables case.

### The review's items, answered

The round-68 review left no open items. This round answers the field's three questions with
mechanisms and their evidence: every held thing has a reason or a clock, and STATE says which; the
card's cache is bounded from the card and shrunk under pressure, with its fill read back rather than
assumed, and the swap that was asked about is declined with the reason written down; the
composition root stays, fenced by a test with the numbers in it; and a screen's sound follows its
picture from the source through the matrix to the output the screen names, with the operator's own
rows always winning.

## Round 70 review — the analyzers as fences, each stop with its reason, and one culture on every desk

### 70.1 — research and design

- **Done.** The tree measured under every SDK rule, Sonar's, StyleCop's, the threading analyzers' and a
  banned-API analyzer; the counts by rule and project; the bug classes behind the counts read in the
  code (the NDI ten-bit switch, the keep-awake result, the registry writes, the argument order in the
  transposes); the design as a generated configuration with a reason per stop. §88.1; `docs/ANALYSIS.md`;
  ADR-013.
- **Found on the way, kept by design:** CA1508's dead-condition findings are mostly the flow analysis
  wrong about volatile fields and loops (the web source's lock-free handoff, the NDI sender's ten-bit
  flag) — a suggestion, not a fence. S1244's floating-point equality flags sentinels compared exactly
  on purpose — reviewed by hand, not fenced.

### 70.2 — the culture family

- **Done.** CA1305, CA1310 and S6580 at error; 145 sites fixed by a fixer that reads the build's own
  log and edits the flagged call (a provider, a comparison, a parse's styles); `CultureGuard` at Main's
  first line; the configuration, the banned list and the generator. §88.2. Tests: CultureGuardTests;
  every suite green with the fence on.
- **Found and fixed on the way:** the first culture guard test blocked on a task and the xUnit analyzer
  (xUnit1031) refused it — the test awaits. The Core suite's result line was missing from the round's
  first suite run because that compile error was filtered out of the log; the filter now keeps every
  `error`.

### 70.3 — the disposal, threading and security families

- **Done.** Thirty-nine undisposed fields disposed or their reason written; every continuation
  scheduled; the async-void appliers and UI lambdas made tasks; twenty-six tasks and delays given their
  token; fifty-five P/Invokes given a search path; the commands, the ring's folder, the digest and the
  shuffles as §88.3 says. Tests: every suite green with the three families at error.
- **Found and fixed on the way:** the token fixer put `, ct` into an empty argument list
  (`ReadToEndAsync(, ct)`) — the fixer now checks the list; it also tripped on a token already inside an
  outer lambda's body — the check is now on the argument list's tail, and the outer call is edited before
  the inner one. A `SuppressMessage` for CA1001 on a field does nothing — the rule reports on the type,
  so the attribute moved to the class. A test's fake writer locked on its `MemoryStream`; it locks on a
  gate now (CA2002 found that one in the tests, where it was as wrong as anywhere).
- **Found by the suites and fixed:** forwarding the twin's cancellation token to its fire-and-forget
  writes broke five twin tests — a handover cancels the source and does not renew it at once, so the
  main's section and beat writes were tasks that never started ("no Section came"). The twin's
  fire-and-forget writes and its reconnect delay now opt out of cancellation explicitly
  (`CancellationToken.None`, which is what the rule asks for when a token is not wanted); the peers'
  and the device links' own tokens stay, since those objects cancel only when they are disposed. The
  changelog test failed once because the plan's round-70 section landed before the changelog's entry;
  the entry is in.

### 70.4 — the correctness, performance and banned families, CI and the loop

- **Done.** The three families at error across the source and the tests, as §88.4 says; the pacing
  loops on stop events; `IChildHandle.WaitForExit`; the encoder host's heartbeat on a timer;
  `Separators` and `SafeRegex` for the shared character sets and the regex timeout; `ReachableUrl`
  for the two places that picked the same address by hand; `FrameSmoother.OldestWaitingId` for the
  room-making that walked an iterator to its first element. CI needed no new step (§88.5). Tests:
  every suite green with all seven families at error.
- **Found on the way.** `_ = Task` inside a lambda whose only underscore is a parameter assigns the
  parameter (AppServices' NDI probe, the web source's navigation handler): named parameters now, and
  the rule that found it (S1854) is a fence. CA1867 proposes a char overload for a case-insensitive
  check — the analyzer's own source marks it the unsafe class — so the five sites stay and the rule is a
  suggestion with the reason; the `Ordinal` class (CA1865/CA1866) was rewritten mechanically across
  every project. Sonar's S3237 does not accept a comment as a use of `value`: the two default interface
  setters discard it explicitly. A `readonly record struct` gives a scope handle and a palette value
  equality in one word; a struct over an array does not get one (its equality would be the array's
  reference), so it says why instead. The FrameBudget bucket counted slow frames per second and nothing
  read the count — removed, the session's count is the one on the glance line.
- **Found by the suites.** Nothing this unit: the seven suites ran green on the first full run after
  the families landed.

### 70.5 — the papers

- **Done.** `docs/ANALYSIS.md` complete (§5's last three rows, §6 the bugs by family, §8 the limits);
  PLAN §88.4–88.5 and the counts; this review; CHANGELOG round 70; README; the tag table carried to
  round 69; ADR-013 as written in 70.1.

### The review's items, answered

The round-69 review left no open items. This round answers the maintainer's question with a
measurement and a mechanism: the tools were switched on over the whole tree and counted before any
was chosen; the rules with a show's failure mode behind them are fences that stop the build with their
reason written; the style rules are off by name with theirs; StyleCop is out with its numbers; the
SonarQube server is recorded as the maintainer's decision, not built blind. What the fences caught
is in ANALYSIS §6 — a keep-awake taken for a fact, a class of locale-dependent spelling, a leak per sink,
twenty-nine regexes with no timeout, a stop that waited two seconds for a sleep — and what they cannot
catch is in §8.

## Round 71 review — the desk tick and the audio devices

### 71.1 — research

- **Done.** The maintainer's super-check, log, settings and show log read; the "(audio)" of the tick
  row traced to `AudioPage.Poll` refreshing the sound-reactive input picker by a WASAPI enumeration
  every tick while a Fractal or Reactive was on the desk — the maintainer's programme was a Fractal —
  and the outputs' enumeration found on the desk's thread from the menus, the health facts (every
  fifth tick), the pickers, the routing rows, the screens' choices, the cue editor and the verbs.
  §89.1. The permissions question answered from the log: the registry line is the show lock's
  notifications item, once per lock, nothing to do with the tick.

### 71.2 — the catalogue

- **Done.** `AudioEndpointCatalogue` in `Patterns.Audio`, composed and started by the desk, disposed
  in the workers' shutdown step; every reader on it; the by-id resolution at a press; the facts row.
  §89.2. Tests: the catalogue's four, the desk's two; every suite green.
- **Found on the way.** `FractalAppTests` asserted on the analyser's static enumeration off Windows;
  it asserts on the catalogue's inputs now. The catalogue's default reader off Windows would have
  tried WASAPI and logged a warning at every test boot; it reads nothing, without a word. The App
  test measures the fix as a count — reads of the machine across sixty ticks: zero — rather than a
  time, which a container cannot promise.

### 71.3 — the papers

- **Done.** PLAN §89, this review, CHANGELOG round 71, README, ADR-014, the tag table carried to
  round 70.

### The review's items, answered

The round-70 review left no open items. This round answers a report from the rig with the cause
named in the code, the mechanism that removes it, and a test that counts the machine's reads on a
desk with a Fractal on it: zero.

## Round 72 review — the critique answered

### 72.1 — the assessment

- **Done.** Every claim of the maintainer's critique checked in the code and found real, with the file and the
  line; the peers' practice read; the decisions written, including the four things declined. §90.

### 72.2 — audio truth

- **Done.** The (configuration, picture) pair on every picture-derived reading of `AudioRouting`; every reader in
  the App and the assistant passes `AirState` as the picture. §90.2. Tests: a Core test over a configuration and
  a cloned air, an App test through EDIT SAFE.
- **Found on the way.** The audio graph's 50 ms timer called `Reconcile` — a forced rebuild twenty times a second
  while the matrix was on. It polls now. A first App test asserted on the rebuild counter across the publish's
  side effects (which force a reconcile by design); it asserts on the plan the rebuild makes and on the quiet
  ticks after it, which is the fact that matters.

### 72.3 — the take ticket

- **Done.** `TakeTicket` and its landing in Core; the sting carries it; `ShowActions.Land` runs it through the
  same verb the key runs; the one-shot spent only by a press that happened; STATE, the Eye and the deck show the
  ticket while it waits. §90.3, ADR-015.
- **Seen, not done.** A LOCK pressed during a running clip pins the clip's own picture as the screen's own in the
  edited state (`SetLock` reads the air's picture, which is the clip); the scoped restore puts the air right and
  the landing refuses the tile, but the edited state keeps a Media picture the operator never chose until the
  next send lifts it. The lock should pin the picture under the clip — the stinger's saved picture — when a clip
  covers the screen. Recorded for the next round; the qualification row in §10 will show it if it bites.

### 72.4 — the session on a replaced show

- **Done.** `ResetSession` with its policy in words; `StingerService.ForgetSession`; a canvas's role as one edit.
  §90.4. Tests: the whole session set up and a show loaded over it, EDIT SAFE open and closed; a canvas's role
  costs the publishes one screen's does (the verb's one edit, then the desk's own NEXT TAKE line through the
  snapshot — the second publish is the desk's, not a member's).

### 72.5 — the wire fails closed

- **Done.** The six latch families refuse any word that is not ON, OFF, TOGGLE or nothing. §90.5. The Companion
  module already sends the exact words; nothing on the deck changes.

### 72.6 — PARTIAL

- **Done.** The verdict, its words, the far end's settlement, the audio policy, the diagnostic profile's audio
  word dropped, the commissioning flow, the Eye, the journal, Companion 3.11.0. §90.6.
- **Found on the way.** Three tests pinned MATCH where a contracted property was never stated — the blind
  observation, a colour space with the connector agreeing, and an EDID test whose contract named stereo audio —
  and the diagnostic profile's words. Each moved to the new fact rather than the rule bending to the test.

### 72.7 — the engineering items

- **Done.** The smoother's scratch, the display query's loop, `DisplayEvidence.InvalidateTopology`, the EDID
  fuzz. §90.7. The invalidation is exercised on a real Windows only (the window's screens change) — the desk's
  hook is one line; the platform's Forgets are the ones the tests already drive.

### 72.8 — the papers

- **Done.** QUALIFICATION §10–§12, PLAN §90, this review, CHANGELOG round 72, README, ADR-015, the tag table to
  round 71.

### The review's items, answered

The round-71 review left no open items. This round answers a critique with every claim verified before
anything was built, and one item recorded as seen and not done (72.3's lock during a clip).

