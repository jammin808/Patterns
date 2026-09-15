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

## What a fail means

A fail is a row with the reading that failed beside it and the log's lines from that minute
(`patterns.log`, `patterns.metrics.csv`), attached to the round's review. The build is not
qualified until the matrix passes on the same machine; a pass on another machine is another row,
not a substitute.
