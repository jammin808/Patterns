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
transition in `patterns.log` with the reading that caused it.

**Pass.** `retiringMB` flat, `poolStarved` 0, `hungFrames` 0, `quarantinedMB` 0, `fenceFaults`
empty, *media* flat, `renderP95` within the budget the Machine page shows for the target rate,
the census back at the baseline read after the first output opened (desks 1, nodes 0,
openFrames 0, retiredFrames 0) once the outputs close, and no more than one pressure
transition per dwell if any.

| date | machine | build / manifest hash | retiringMB | poolStarved | hungFrames | quarantinedMB | renderP95 | census at close | result | notes |
|------|---------|-----------------------|------------|-------------|------------|---------------|-----------|-----------------|--------|-------|
|      |         |                       |            |             |            |               |           |                 |        |       |

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

## What a fail means

A fail is a row with the reading that failed beside it and the log's lines from that minute
(`patterns.log`, `patterns.metrics.csv`), attached to the round's review. The build is not
qualified until the matrix passes on the same machine; a pass on another machine is another row,
not a substitute.
