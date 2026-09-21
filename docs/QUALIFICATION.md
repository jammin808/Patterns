# The qualification record

Nothing here is a claim until the row has been filled in on a real rig. This is the record the
round-63.5 review asked for (P0.2), written so a rig day can fill it without the papers open:
each matrix says what to set up, what to read — the STATE keys (`STATUS` on the wire, or the
Remote page's STATE), the Machine page's rows, `patterns.metrics.csv`'s columns — and what
passes. `docs/SOAK.md` has the seven steps and the four-hour soak's script; this is the record
of what they read. A release is qualified when every matrix has a row with a pass and the
build's manifest hash; a fail is a finding with the reading beside it, never a retry without one.

The header of every row: the date, the machine (CPU, GPU, RAM, the displays and their refresh),
the build (`round-<n>` and the commit from `Patterns-<tag>-manifest.json`), the manifest's libVLC
hash, and who ran it. `Patterns.exe --verify-runtime --report runtime.txt` first — its lines go
in the record before anything is switched on.

## 1. Companion 5 imports the module

**Set up.** Companion 5.0.5 or later on a machine on the desk's network; the desk's wire on
(Remote page: TCP and HTTP ports); the module's tgz from the release
(`jammin808-patterns-<version>.tgz`).

**Do.** Remove any `jammin808-patterns` already installed; import the tgz; add the connection
with the desk's address and port; open a page with `go`, `take`, `screen_take`, `pvw` and the
`machine_memory_pressure` feedback.

**Read.** The Remote page's decks line — *FOH deck (module <version>, …)*; STATE's `decks` row
(the name, the module version, the address); `machine_memory_pressure` reads *none* on an idle
desk; a `go` on a key moves the running order and its receipt lands on the key.

**Pass.** The import needs no second attempt; the decks line names the module's version; every
action on the page answers within a second and the feedbacks follow within two.

| date | machine | build / manifest hash | Companion | result | notes |
|------|---------|-----------------------|-----------|--------|-------|
|      |         |                       |           |        |       |

## 2. IMAG on a capture card, the profile off and on

**Set up.** A camera into a capture card (a Cam Link 4K or an HDMI card); a screen the room
sees beside the speaker; the card as a source on that screen; a clap at the lens.

**Do.** Record with Low latency off, then on (Inputs → the device → Low latency). Tick the box
while the camera is on air and watch the room: the reopen is staged, the picture never blanks.

**Read.** The glance line's *live* number (the app's share, decoder to frame); SYNC CHECK's
whole-chain figure; STATE `inputs.pending` while the reopen is staged; the super-check's *Live
input* row; frames dropped on a hitch (`drops` per sink on the glance line).

**Pass.** The whole chain falls by about the decoder's 80 ms confidence buffer with the profile
on while *live* barely moves; the *Live input* row is green (to 40 ms) or amber (to 80), never
red; no blank frame in the room on the staged reopen.

| date | machine | build / manifest hash | card | live off / on (ms) | chain off / on (ms) | result | notes |
|------|---------|-----------------------|------|--------------------|---------------------|--------|-------|
|      |         |                       |      |                    |                     |        |       |

## 3. The thirty-minute soak

**Set up.** A capture card on air, a clip in the preview, three looks; the CSV on (Machine →
Metrics).

**Do.** Recall a look every minute for thirty minutes; a TAKE and a CUT each five minutes;
EDIT SAFE opened and closed each ten.

**Read.** CSV: `retiringMB`, `poolStarved`, `fenceOldestMs`, `renderP95`; STATE `memory`:
`hungFrames`, `quarantinedMB`, `fenceFaults`, `pressure`; STATE `census` at the end.

**Pass.** `retiringMB` returns to its floor between edits; `poolStarved` 0; `hungFrames` 0 and
`quarantinedMB` 0 throughout (a number there is a finding, named in `fenceFaults`);
`fenceOldestMs` under a second; the pressure rung never leaves *none* on a Standard machine
with one source; `census.openFrames` 0 and `census.hungFrames` 0 at the end.

| date | machine | build / manifest hash | retiringMB floor / peak | poolStarved | hungFrames | quarantinedMB | pressure rungs | result | notes |
|------|---------|-----------------------|-------------------------|-------------|------------|---------------|----------------|--------|-------|
|      |         |                       |                         |             |            |               |                |        |       |

## 4. The sixty-minute soak

**Set up.** As 3, with the web page as a second source and a deck on a third screen.

**Do.** As 3, for sixty minutes; the deck's pages turned each minute; the web page's VT armed
and taken once.

**Read.** As 3, plus `census.pictures`, `census.ledgerOwners` and the memory line's *media*
figure at the start and the end.

**Pass.** As 3; *media* flat within a picture budget's worth; `census.pictures` no higher at
the end than after the first ten minutes; the web VT reads *PLAYING (observed)* after the take.

| date | machine | build / manifest hash | media start / end (MB) | pictures 10 min / end | hungFrames | quarantinedMB | result | notes |
|------|---------|-----------------------|------------------------|-----------------------|------------|---------------|--------|-------|
|      |         |                       |                        |                       |            |               |        |       |

## 5. The mixed-refresh desk

**Set up.** Two outputs on one desk: a 50 Hz display and a 60 Hz display (or 59.94); the target
frame rate at 60; a clip with motion on both.

**Do.** Outputs on; change the 60 Hz display's refresh to 50 in the OS while the outputs are
live, then back; change the target frame rate to 50 and back.

**Read.** Each output's tech info chip (the sink, its pixels, the rate presented at, the
display's refresh, *render clock … Hz*); STATE `machine.renderClockHz` and
`machine.clockLimited`; the super-check's *Render clock* row; the glance line's drops per
sink before and after each change; `pacing.epochs` per sink on STATE's `outputs` rows.

**Pass.** The 50 Hz output presents at 50 in the 50 family, the 60 Hz output at 60; no drop is
counted on the frame of a change (a new epoch starts, the drops before it stay); *LIMITED BY
RENDER CLOCK* appears only when the render clock reads below what a display wants and is then a
finding about the machine, recorded; the *Render clock* row is green otherwise.

| date | machine | displays (Hz) | build / manifest hash | presented at | epochs | drops on change | clockLimited | result | notes |
|------|---------|---------------|-----------------------|--------------|--------|-----------------|--------------|--------|-------|
|      |         |               |                       |              |        |                 |              |        |       |

## 6. The four-hour soak

**Set up and do.** `docs/SOAK.md`'s seven steps, unchanged, for four hours; a Companion page
driving one GO a minute for the last hour.

**Read.** The CSV's columns at the end (`retiringMB`, `poolStarved`, `fenceOldestMs`,
`renderP95`, `switchP95`, `goP95`); STATE `memory` (`hungFrames`, `quarantinedMB`,
`fenceFaults`, `pressure`, the media figure); `census` after the outputs close; every ladder
transition in `patterns.log` with the reading that caused it. Round 69: STATE `machine.gpuCache`
(`hasContext` true on the rig, `usedMB` against `limitMB`, `purges`) and `machine.gc` (`gen2`,
`lohMB`, `lastPauseMs`) read every hour, and `memory.residency.letGo` at the end.

**Pass.** `retiringMB` flat, `poolStarved` 0, `hungFrames` 0, `quarantinedMB` 0, `fenceFaults`
empty, *media* flat, `renderP95` within the budget the Machine page shows for the target rate,
the census back at the baseline read after the first output opened (desks 1, nodes 0,
openFrames 0, retiredFrames 0) once the outputs close, and no more than one pressure
transition per dwell if any. Round 69: `gpuCache.usedMB` never above `limitMB`, `gpuCache.purges`
0 unless the rung left *none* (a purge on a quiet stage is a finding), `gc.lastPauseMs` under a
frame at the target rate while the outputs are live, `gc.lohMB` no higher after the outputs close
than after they first opened, and `residency.letGo` climbing only while the stage was still.

| date | machine | build / manifest hash | retiringMB | poolStarved | hungFrames | quarantinedMB | renderP95 | GPU cache used / limit · purges | LOH at open / close · worst pause | census at close | result | notes |
|------|---------|-----------------------|------------|-------------|------------|---------------|-----------|---------------------------------|-----------------------------------|-----------------|--------|-------|
|      |         |                       |            |             |            |               |           |                                 |                                   |                 |        |       |

## 7. The signal, the EDID and the far end on a real link

**Set up.** A screen on a real display or a processor input — an LED processor (Brompton,
Novastar), a projector on PJLink class 2, or a monitor on HDMI or DisplayPort — with the contract
the design asks for typed on the Screens page (`SCREEN n SIGNAL 3840x2160 50 RGB 8 SDR 709 HDMI`)
and the output opened on it.

**Do.** Read the technical view (DESIGN, ADVERTISED, REQUESTED, OBSERVED, RESULT). Load the
planned EDID (EXPORT EDID, or `GET /api/screens/<n>/edid.bin`) into the processor input or the
port's EDID emulator and reopen the output. For a processor with an input-status API, fill the
device card's *Input carries*, *Ask* and *Reads* and watch RECEIVED; for one without, read its
panel and type `SCREEN n RECEIVED <words>`. Then change one thing on purpose — the display's rate
to 60 on a 50 contract — and read the verdict.

**Read.** `SCREEN n SIGNAL` (the JSON: `design`, `advertised`, `observed`, `received`,
`result`, the `edid` object's `sha256` and `problems`); STATE's screen rows (`signal`); Super
Check's SIGNAL rows; the journal's verdict lines; `patterns-signal-report.txt` from
`Patterns.exe --signal-report` for the raw paths and EDIDs.

**Pass.** OBSERVED names the raster, the rate as an exact rational, the encoding and the depth
for every active path on the real GPU (not *not available from this Windows path*); the EDID
parses with no problems and its identity matches the display's label; after the planned EDID is
loaded the *Planned EDID* line reads green and OBSERVED is the contract; RECEIVED agrees with
the contract on the processor; the deliberate rate change turns the verdict to MISMATCH within
two seconds with the rate line amber, and back to MATCH when undone, with exactly two journal
lines.

| date | machine / GPU / driver | build / manifest hash | display or processor | connector | observed (raster · rate · enc · bits) | EDID id · problems | planned EDID presented | received | verdict on the rate change | result | notes |
|------|------------------------|-----------------------|----------------------|-----------|---------------------------------------|--------------------|------------------------|----------|----------------------------|--------|-------|
|      |                        |                       |                      |           |                                       |                    |                        |          |                            |        |       |

## 8. The known-good rig and the commissioning flow

**Set up.** The show machine as it will run the show: its displays, its processor, its audio
output, the desk's bindings and the contracts typed. The Machine page open (Admin → Machine).

**Do.** Walk the Technician's *Commission the rig* walkthrough from DISCOVER to KNOWN GOOD,
reading the COMMISSIONING block's next step at each stage; TEST ROUTE a screen and clear it;
SAVE KNOWN GOOD with a note. Then change one thing on purpose — a display mode, or unplug an
audio output — and read the KNOWN GOOD RIG block, Super Check's RIG rows and the journal.

**Read.** `COMMISSION STATUS` (the seven lines, the headline, the next step, the percentage);
`RIG STATUS` (the saved rig's note and time, the drift lines); STATE's `commissioning`,
`machine.inventory` and `machine.rig`; Super Check's RIG rows; the machine's THIS COMPUTER
block against what Windows' own Settings say for the GPU driver, the displays and the audio
endpoints.

**Pass.** Every stage goes green on the rig's own evidence with no line left UNVERIFIED; TEST
ROUTE holds the flow at CONTRACT and its clearing frees it; the inventory names every GPU
(driver version and date as the vendor's control panel shows them), every display with its EDID
hash, every audio endpoint with its shared-mode format, the power plan and the scheduling
switches, and the reading never stalls the desk's tick (the Machine page's tick budget shows no
slow area); after SAVE KNOWN GOOD the RIG rows are green; the deliberate change turns the drift
line amber (a mode) or red (an output gone) within one reading and journals once; undoing it
journals once more and the rows are green again.

| date | machine | build / manifest hash | stages green | test route held CONTRACT | inventory complete (GPU · displays · audio · power) | drift on the change | result | notes |
|------|---------|-----------------------|--------------|--------------------------|-----------------------------------------------------|---------------------|--------|-------|
|      |         |                       |              |                          |                                                     |                     |        |       |

## 9. The God's Eye on a rig

**Set up.** The show machine with the rig of §7 and §8 — several displays, a processor, a deck
running Companion 3.7.0, a device the desk drives, an NDI feed or a capture card as a source, the
audio routing on — and the desk on the Eye page (EYE in the rail, above NODES).

**Do.** Read the picture cold: every display, screen, source, device and deck of the rig should
be there with the light the Screens, Interactive and Remote pages give it, and nothing else.
Then break one link at a time and watch the rail and the page: pull a display's cable (its screen
red, the display gone), set a contract Windows does not send (MISMATCH), power a driven device
down (red), connect a deck without its pairing token (amber, *connected, not paired*), stop the
source's feed (amber, *no frame yet*). Press NEXT PROBLEM through them, double-click one, RESET,
right-click a screen (its tile menu) and a device (the Eye menu, OPEN and ASK). On the deck: the
Eye page's headline key and NEXT PROBLEM; from a controller, `EYE FOCUS screen 2`.

**Read.** The rail's EYE word and hue against Super Check's overall light; `EYE` on the wire
(the counts, the problems' order); STATE's `eye` row against the deck's `$(patterns:eye_headline)`;
the Machine page's tick line for the *health* area while the picture is steady and while it
changes; the canvas's smoothness while the camera moves (no hitch on the outputs' frame budget).

**Pass.** Every thing of the rig is in the picture once, with the same light the page that owns
it shows and a link to what feeds, drives or shows it; each break turns its thing and its link the
expected colour within one tick and the headline names it; NEXT PROBLEM walks reds before ambers
and the wall before the deck; RESET restores the exact view from before the focus; the deck's
headline key follows the rail's light within a STATE push; the *health* area of the tick stays
inside its budget with the Eye's gather on it (a steady show costs no rebuild), and a camera move
never shows on the outputs' frame budget.

| date | machine | build / manifest hash | things in the picture = things on the rig | breaks seen (display · contract · device · deck · source) | NEXT PROBLEM order right | RESET exact | deck follows | health area within budget | result |
|------|---------|-----------------------|--------------------------------------------|------------------------------------------------------------|--------------------------|-------------|--------------|---------------------------|--------|
|      |         |                       |                                            |                                                            |                          |             |              |                           |        |

## 10. The take under a sting, with the wall changing under it (round 72)

**Set up.** The show machine with two or more outputs on the wall, EDIT SAFE open, a video sting in
the library (a two-second clip is enough), a deck running Companion 3.11.0 or a controller on the
wire, and STATE open on a second screen (`curl` in a loop, or the deck's `$(patterns:take_landing)`).

**Do.** Each row is one press. Set the one-shot (`TAKE NEXT STING <name>`, or right-click TAKE),
build a look in the preview, press TAKE with the scope named, and during the clip do the thing in
the row: tick another tile; click another tile (focus); ARM a tile that was not armed; LOCK a tile
the press promised; LOCK every tile the press promised; unplug a display the press promised; load
another show. Then let the clip end and read where the preview landed.

**Read.** STATE's `take.landing` while the clip runs (the sting, the scope words, the targets, when
it was pressed) and its absence after; the Eye's desk node ("Landing → …"); the journal's `Take` row
at the landing (its words name the sting and any screen held since the press with the reason);
the wall (which tiles moved, which kept their picture, OWN lights); the TAKE key's face and STATE's
`take.next` (the one-shot spent at the press that fired, kept by a press that failed).

**Pass.** The clip covers the press's targets alone; the landing changes exactly the press's targets
less any locked since the press or gone from the rig, and nothing that arrived during the clip
(a tick, a focus, an ARM, a screen) is taken; a LOCK since the press holds that screen and the
journal's words say `locked since the press`; every promised screen locked since gives
`Sting could not move the show on — previous content back.` with `Nothing lands — …` in the journal
and the pictures from before the clip back; a press whose sting cannot fire (a missing file) leaves
the one-shot on the key and says so; a sting gone from the library clears the one-shot and says so;
a show loaded during the clip (§12) never lands the old show's ticket.

| date | machine | build / manifest hash | scope pressed | change during the clip | landed exactly the press's targets | held since named | nothing arrived was taken | STATE landing row seen / gone | one-shot kept on a failed press | result |
|------|---------|-----------------------|---------------|------------------------|-------------------------------------|------------------|---------------------------|-------------------------------|----------------------------------|--------|
|      |         |                       |               |                        |                                     |                  |                           |                               |                                  |        |

## 11. The sound follows the picture the room has, not the preview (round 72)

**Set up.** Two outputs whose sound leaves by two devices (an HDMI display with speakers and a
USB interface do), each named on the Screens page (Sound out), the matrix on (AUDIO ROUTING ON),
follow on, a clip with a soundtrack on the programme, EDIT SAFE open.

**Do.** With the programme's clip audible on both outputs, give one screen its own picture in the
preview (click its tile, change the pattern) and leave it there for a minute without taking; then
TAKE; then edit that screen again in the preview and press EDIT SAFE off (discard); then load a
look that gives the other screen its own picture through a cue (the air moves without a press).

**Read.** The Audio page's ROUTING words ("Sound follows the picture on 2 screens: …") and each
output's meter; STATE's `audioRouting.followed[].what`; the tile's right-click menu ("Sound out … —
the programme / its own picture"); the Machine page's audio graph line (rebuilds) and the tick's
*audio* area.

**Pass.** During the minute of preview editing every output carries what it carried before — the
programme's sound stays on the edited screen's output, the meter does not move, the words say
"the programme", and the graph reports no lane rebuilt; at the TAKE the edited screen's output
switches to its own picture's sound within one tick, the words and STATE follow, and the graph
reports one rebuild; the discard changes nothing; the cue's look moves the other output's sound as
the take did, without any press on the Audio page. The tick's *audio* area stays inside its budget
throughout (no plan resolved on the 50 ms tick — the graph's quiet-tick counter climbs, its rebuild
counter does not).

| date | machine | build / manifest hash | outputs and devices | preview edit moved nothing (words · meter · rebuilds) | TAKE moved the sound (ticks to switch · rebuilds) | discard moved nothing | cue's look moved it | audio area within budget | result |
|------|---------|-----------------------|---------------------|--------------------------------------------------------|----------------------------------------------------|-----------------------|---------------------|--------------------------|--------|
|      |         |                       |                     |                                                        |                                                    |                       |                     |                          |        |

## 12. A display topology change under the evidence (round 72)

**Set up.** The show machine with two displays on the card, contracts set on both screens (Screens page,
SIGNAL CONTRACT), the EDIDs read (Super Check's SIGNAL block shows them), the machine's inventory taken
(the Machine page), and the outputs on.

**Do.** Pull one display's cable and plug it back; swap the two cables between the card's ports; plug a
third display in while the desk is reading the signal view (SCREEN n SIGNAL in a loop on the wire); change
one display's mode in Windows' settings. After each, read the screen's signal view and Super Check.

**Read.** The log's "Screen topology changed." line and the Machine page's display-evidence count
(`DisplayEvidence.Invalidations`) beside it; the signal view's OBSERVED and ADVERTISED blocks against what
is now plugged; the RESULT on each screen (MATCH, PARTIAL naming what nobody stated, MISMATCH, UNVERIFIED);
the inventory's display list.

**Pass.** Every topology change increments the evidence count once and the next signal view is the new
rig's — never the old cable's EDID under the new display, never a mode that was; a read that races the
plug reports the rig (the query's loop), not an empty observation; a swapped cable reads MISMATCH on the
transport line where the contract names the connector; a screen whose contract names a colour space reads
PARTIAL with `colour space never stated` and a processor's input status turns it to MATCH.

| date | machine | build / manifest hash | change made | evidence count moved once | signal view is the new rig's | raced read reported the rig | PARTIAL named the property | far end settled it | result |
|------|---------|-----------------------|-------------|---------------------------|------------------------------|-----------------------------|----------------------------|--------------------|--------|
|      |         |                       |             |                           |                              |                             |                            |                    |        |

## 13. A restart the room never sees (round 76)

**Set up.** The show machine under the watchdog (Machine → Stability, the default), two or more outputs on
with a clip playing on one screen (a VT with sound) and the music playlist playing; a phone on the wire
reading `STATE` every second; a camera on the room's screens.

**Do.** Press RESTART on the Machine page (or `RESTART <passcode>` on the wire). Then, in another run, end the
desk's process from Task Manager while a clip plays (a crash) and let the watchdog relaunch it. Then, in a
third, hang the desk (a debugger's pause on the UI thread, or a stress tool) and start a second `Patterns.exe`
on the same folder by hand.

**Read.** The camera's recording of the screens across each restart; `patterns.watchdog.log` ("asked to be
replaced", "handed its screens to the replacement", exit 84); the new desk's status line ("Restarted — the
show was put back on", the takeover's words); STATE's `continuity` row (`handingOver`, `claimDeferred`,
`pendingResumes` back to 0); the clip's clock on the Run strip against the wall clock; the playlist's track
and position; `patterns.outputs.json` naming the new desk's pid and no other.

**Pass.** The RESTART shows one picture become the next on every screen and never the desktop; the clip
carries on within a second of where it would have been (not from its first frame) and the music in the same
track; the sound stays on the same outputs; two desks may sound the clip together for under a second and no
longer; the old desk's exit is 84 and the watchdog logs no new start. The crash shows the desktop for the
relaunch's boot only, then the same show with the clip resumed where it would be by now — note the gap's
seconds. The hung desk's screens keep their picture until the second desk's own windows are up over them,
and the hung process is ended only after the grace.

| date | machine | build / manifest hash | case (RESTART / crash / hung) | desktop seen (s) | clip resumed at ≈ where it would be | music same track | sound on the same outputs | doubled sound (s) | exit 84 and no new start | result |
|------|---------|-----------------------|-------------------------------|------------------|--------------------------------------|------------------|---------------------------|-------------------|--------------------------|--------|
|      |         |                       |                               |                  |                                      |                  |                           |                   |                          |        |

## 14. A hot-plug that blacks nothing, with identical displays (round 76)

**Set up.** The desk's monitor as the primary display, two identical projectors (the same model and mode) and
one different display, all on, each showing its own picture with a clip on one.

**Do.** Pull the desk monitor's cable (Windows promotes another display to primary); plug it back. Pull one
projector; plug it back. Pull both identical projectors together; plug them back in the other order. After
each, read the Screens page, STATE's `continuity.carriedOver` and the log's "carried over" lines.

**Read.** Which outputs went black and for how long; the log's "Display re-identified", "carried over",
"SCREEN UNPLUGGED", "SCREEN BACK" lines; each screen's picture against its placement's name.

**Pass.** No output the unplugged display had nothing to do with goes black or shows a first frame again;
the promoted primary stays on with its picture; the returned display comes back on with its own picture; the
identical pair comes back with each picture on its own glass — or, if they came back swapped, the row says so
(the model matches identical displays by coinciding id first) and the operator's swap on the Screens page is
one drag.

| date | machine | build / manifest hash | change made | other outputs stayed lit | primary promotion left the output on | returned display back on | identical pair swapped? | carriedOver moved | result |
|------|---------|-----------------------|-------------|--------------------------|--------------------------------------|--------------------------|-------------------------|-------------------|--------|
|      |         |                       |             |                          |                                      |                          |                         |                   |        |

## 15. A picture's sound on its own screen's output, and the leaving fade (round 76)

**Set up.** The matrix on (Audio page, ROUTING), three screens each with a named output (a multichannel or
USB card), a clip with sound as one screen's own picture, the programme on the others; a monitor row from
that screen's sound to the desk's own output.

**Do.** Listen at each output. Take the clip's screen back to the programme with a 1 s fade, then with a cut;
take a fresh clip onto the same screen while the first still plays; add and remove the monitor row.

**Read.** Which outputs carry the clip's sound (the Audio page's input rows: their lane and source, "N inputs
playing, M fading out"); the level meters across the take; STATE's audio rows.

**Pass.** The clip is heard on its own screen's output and the monitor output only; the fade take fades the
sound over the second and the cut take fades it over 120 ms — no click, no sound left playing under the
new picture; the fresh clip's sound starts once and the old one leaves; the monitor row adds and removes its
output without moving the sound.

| date | machine | build / manifest hash | outputs carrying the clip | fade take (ms heard) | cut take (click?) | fresh clip once | monitor row add/remove | result |
|------|---------|-----------------------|---------------------------|----------------------|-------------------|-----------------|------------------------|--------|
|      |         |                       |                           |                      |                   |                 |                        |        |

## 16. The take under a sting on tiles and canvases, with a LOCK (round 76)

**Set up.** Three screens, two joined as a canvas, a sting clip of a few seconds set as the next transition;
a look different from the air on the preview.

**Do.** TAKE from the canvas's wall tile with the sting; during the clip, drag the third screen flush into the
canvas. Repeat with the wall's TAKE and, during the clip, rename a screen. Repeat and, during the clip, LOCK
one of the promised screens. Read the landing each time (the status line, the journal's TAKE row, STATE's
take row, the Eye's desk node).

**Pass.** The canvas take is held with "changed since the press — now Canvas … of 1, 2, 3"; the renamed
screen's take lands; the locked screen is left as it was with "locked since the press" and the others land;
the sting's end puts the show back on every screen and a locked screen ends locked on the show, never on a
frame of the clip.

| date | machine | build / manifest hash | case (canvas grew / renamed / locked) | landing words | journal row | Eye's words | screens after the clip | result |
|------|---------|-----------------------|---------------------------------------|---------------|-------------|-------------|------------------------|--------|
|      |         |                       |                                       |               |             |             |                        |        |

## 17. The room hears what it sees (round 77)

**Set up.** Two screens on their own outputs (the desk's monitor with a named sound output, a TV on HDMI);
a clip with an obvious soundtrack on the programme; a YouTube page as the TV's own picture; the transition
on at one second; the matrix off, then on.

**Do.** With both screens following the programme: hear the clip. TAKE the page onto the TV alone. Turn the
desk monitor's outputs off. Take the TV back to the programme. Close every output. Repeat with the matrix on
and a monitor device named with the pick on the programme.

**Pass.** With the TV on its own picture and the desk monitor off, the clip fades out over the transition
and stays silent — on every output — and the page's sound is heard from the TV; the clip comes back at once
when the TV returns to the programme; with every output closed the desk hears the clip; with a monitor
device named and the pick on the programme, the clip is in the operator's ear and nowhere else while it is
on no live output. STATE's `audio` words and the Audio page agree at each step.

| date | machine | build / manifest hash | matrix | step | what sounded where | fade heard | STATE agrees | result |
|------|---------|-----------------------|--------|------|--------------------|------------|--------------|--------|
|      |         |                       |        |      |                    |            |              |        |

## 18. The show lock and the desk's own browser (round 77)

**Set up.** A YouTube page on a live screen with sound; Spotify (or another app) playing; the lock's audio
item on with no allowed list.

**Do.** LOCK THE MACHINE. Read the audio item's words. Unlock. Lock again and kill the desk (Task Manager);
start it. Lock, RESTART with the outputs live (the handover), and watch the lock's words on the new desk;
end the show on the new desk.

**Pass.** The page's sound survives the lock and the other app is muted ("1 other app's audio muted"); the
crash's next start says "other apps' audio put back" and the app is unmuted; through the handover the machine
stays held (no SHOW LOCK OFF from the old desk, no "previous run ended with the show lock on" from the new),
and the new desk's unlock puts the other app back to how it was before the first lock.

| date | machine | build / manifest hash | page's sound under lock | other app muted / put back | crash start words | handover words (old / new) | result |
|------|---------|-----------------------|-------------------------|----------------------------|-------------------|----------------------------|--------|
|      |         |                       |                         |                            |                   |                            |        |

## 19. A refused screencast asked again (round 77)

**Set up.** A YouTube page in the Library; the Media page's status line in view; `patterns.log` tailed.

**Do.** Put the page on a live screen; read the status line every few seconds for a minute; note the log's
screencast lines (the rung that took, "started after N refusal(s)", or the refusals with their next ask).
Read the frame rate on the status line before and after the screencast takes. Take a screenshot of the page
from the Media page's preview while the video plays.

**Pass.** A refused start is asked again within the backoff and the status line moves from "screenshot poll
(the screencast was refused …; asking again)" to "… fps · screencast (after N refusals)" or to the plain
rate; the rate after the screencast takes is the page's (24–30 fps for a video), not 20; the screenshot
shows the video, not black. A page whose screencast is never accepted is a row with the log's lines.
Round 78 note: the field's log of 2026-09-18 answered the first question — every rung refused (1× … 20× over
seven minutes, always E_INVALIDARG) — so the next row should also record the WebView2 runtime version and
whether the control had a surface (shown, non-zero size) at the first ask (PLAN §96.8).

| date | machine | build / manifest hash | refusals | rung that took | fps before / after | screenshot shows video | result |
|------|---------|-----------------------|----------|----------------|--------------------|------------------------|--------|
|      |         |                       |          |                |                    |                        |        |

## 20. The wall's CUT/TAKE truth (round 78)

**Set up.** Two screens on the wall; EDIT SAFE open; the outputs OFF; the show log tailed
(`patterns.showlog.jsonl`).

**Do.** Build a picture in the preview and TAKE it on tile 2; read the status line and the journal row.
Change the preview and TAKE tile 2 again; read tile 2's PGM miniature. TAKE tile 2 a third time without
changing anything. Then OUTPUTS ON and repeat the three presses. Then click tile 2 (the editors on it),
change one property, and TAKE tile 2; then click PGM and TAKE tile 2 again.

**Pass.** With the outputs off every Done row ends "The outputs are off — nothing is on the screens until
OUTPUTS ON." and the screens show nothing. The second take lands the new preview on tile 2 (the PGM
miniature and, once the outputs are on, the TV change). The third press is refused: "2 · … already shows
this picture — nothing to take. Change the preview, or SEND a picture to this tile first." — nothing moves,
the journal row says Refused. With the editors on tile 2 its PVW shows its own picture, the edit lights
PVW on the tile, and the take lands the edit while the programme's preview is unchanged; with the editors
back on PGM the tile's PVW is the programme's preview again and the take lands it. STATE's `take.pending`
names the tile while a picture waits on it and `take.outputsLive` reads the outputs.

| date | machine | build / manifest hash | outputs-off words | second take landed the new preview | third press refused | edit on the tile pending and landed | result |
|------|---------|-----------------------|-------------------|-------------------------------------|---------------------|--------------------------------------|--------|
|      |         |                       |                   |                                     |                     |                                      |        |

## 21. The show folder's lease across a live restart (round 78)

**Set up.** The supervised desk with the outputs live on one screen; Spotify enabled and connected;
`patterns.log` tailed; STATE read on the wire (`continuity.primary`).

**Do.** Admin → RESTART (the handover). While the two desks overlap, read the replacement's STATE and its
Machine page's Spotify status. After the old desk has left, wait a poll (a second or two) and read them
again. Edit the show in the replacement, close it cleanly, start Patterns again and check the edit is there
and no recovery restore runs. Then the abandoned case: with the outputs live, kill the old desk's process
from Task Manager during the overlap.

**Pass.** During the overlap the replacement's `continuity.primary` is false, its Spotify status reads
"Break music is run by the first Patterns window.", and its log says autosave is off "until that one
leaves". Within a poll of the old desk's exit the log reads "This desk owns the show folder now …",
`continuity.primary` is true, the Spotify status is the ordinary one, and the show file's timestamp moved.
The edit survives the clean close and the next start puts nothing back from a recovery record. The killed
old desk leaves the mutex abandoned and the replacement is primary within a poll all the same.

| date | machine | build / manifest hash | primary false during overlap | promoted within a poll | edit survived close | abandoned owner taken | result |
|------|---------|-----------------------|------------------------------|------------------------|---------------------|-----------------------|--------|
|      |         |                       |                              |                        |                     |                       |        |

## 22. The walk (round 79)

The scripted 15-to-30-minute walk the retrospective asked for in place of the nineteen checklists that
never recorded a result: one row per calendar day the maintainer picks up a build, and one after any
round that touches a platform path (display topology or hot-plug, audio endpoints, WebView2, the
process or the watchdog, the twin, the registry, CUT/TAKE). Non-blocking: a round whose row is blank
is written `unwalked` in its REVIEW section, which is a fact, not a stop. Any step the operator cannot
complete becomes a headless test driven through the input pipeline (the `DeskMenuPointerTests` shape),
not a footnote. The files go to `docs/field/round-NN/` (`docs/field/README.md`).

**Set up.** The show laptop with the TV on its HDMI and the desk's own display; the outputs off;
`patterns.log` tailed; the Machine page open on the desk's display.

**Do, in order, ticking each.**

1. **Version.** Read the Admin page's version (or STATE's `version`): `0.<round>.<run>+round-NN.<sha>`
   from a CI build, `0.<round>.0+round-NN.<sha>` from a script — never `1.0.0`.
2. **Outputs off, then on** (`Shift+F6`, `Shift+F5`): the TV comes up on the programme, the desk's
   display stays the desk; the Super Check's Outputs rows read green.
3. **A Fractal on the desk.** BUILD → Fractals, a scene, TAKE; the Render frame line moves; nothing
   on the desk stutters while it runs.
4. **A web page and a YouTube page on the TV.** BUILD → Media, a web item and a YouTube item, each
   taken; the Media page's web row names the path (screencast, or the poll and why) and its fps.
5. **TAKE the same tile three times** with the preview unchanged: the first press lands (the
   journal row's `effect` is Changed or OwnOnly, its `visibility` OutputsLive), the second and third
   are refused with the reason under the picker and no journal row says Done.
6. **RESTART with the outputs live** (Admin → RESTART): the TV never shows the desktop; the log has
   the handover lines; the recovery record's picture is what came back.
7. **Unplug the TV and plug it back.** Every other output stays lit; the log's SCREEN lines name the
   TV alone; the Super Check's display rows come back green.
8. **A right-click menu** on a tile and on the PREVIEW picture: opens at once; its Group and Next
   take entries read what the desk reads.
9. **The three lines.** With a moving pattern up, the Machine page's Start-up, Desk tick and Render
   frame lines, copied into the report.
10. **Desk monitors** (round 79): the Screens page's Desk monitors picker at 25 and at 60 — the
    tiles' motion follows it, the outputs' Render frame line does not move; if LIMITED BY RENDER
    CLOCK shows, its words name the display it follows or say the desk is not keeping up.
11. **OSC** (if it is on): the Super Check's REMOTE / OSC row is amber with its FIX until a bind
    address is set, green once bound.

**Pass.** Every step ticked with the readings it asks for; the three lines posted; the four files
committed. A step that fails is a row of `docs/OPEN.md` with the reading beside it.

| date | machine | build | steps ticked (1–11) | Start-up / Desk tick / Render frame | result | files |
|------|---------|-------|---------------------|-------------------------------------|--------|-------|
|      |         |       |                     |                                     |        |       |

## What a fail means

A fail is a row with the reading that failed beside it and the log's lines from that minute
(`patterns.log`, `patterns.metrics.csv`), attached to the round's review. The build is not
qualified until the matrix passes on the same machine; a pass on another machine is another row,
not a substitute.
