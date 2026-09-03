# Patterns — Bitfocus Companion module

Stream Deck / Companion control for the Patterns show display suite: fast look recall,
presenter next/back, transport, blackout with live feedback, individual screens and
canvas groups, stingers (one-press sounds and clips), and the audio track.

## Setup

1. In Patterns: **Remote** page (SETUP) → make sure remote control is on (Companion TCP port,
   default **9697**).
2. In Companion (v3.x): `Connections → Add`, import this module as a dev module
   (`Settings → Developer modules path` pointing at this folder after `yarn && yarn package`,
   or copy the folder into your dev modules directory), then add the **Patterns** connection
   with the machine's IP and port.
3. Drag presets onto keys: Cue stack (GO, standby ▲ ▼, HOLD, ARM, STOP ALL), Transport,
   Presenter, Looks (F1–F12), Screens, Stingers, Audio.

## The cue stack (module 1.1.0)

The **GO** preset fires the cue on standby and shows its number and name; it is green while
the stack is armed, amber on HOLD or while a cue waits for confirmation (press GO again
within four seconds), red when the last cue failed or was refused. Every GO sends the
standby id the module last saw, so a GO that races a standby move is refused with
`ERR standby moved` rather than firing the wrong cue; `$(patterns:last_error)` carries the
last ERR line. **ARM** needs "Remotes may ARM the cue stack" on the Remote page. **STOP ALL**
stops the audio track, any stinger and the tone — never the outputs, blackout or the
stream. Variables: `cue_standby_number/name`, `cue_next_number/name`,
`cue_previous_number/name`, `cue_last_outcome`, `cue_confirm`, `cue_armed`, `cue_hold`,
`cue_seq`, and `program` (what is on air, by name). The module says `HELLO <label>` on
connect, so the caller's history reads "GO from FOH deck".

No module? The same protocol works with Companion's built-in **Generic TCP** connection —
send the commands listed in `docs/REMOTE.md` (e.g. `LOOK 2`, `NEXT`, `BLACKOUT TOGGLE`),
one per line. You lose feedback; keys still fire.

## Feedback

Patterns pushes `STATE {json}` lines whenever anything changes, so blackout, per-screen
enable, stinger-on-air and audio-playing states light keys live, and
`$(patterns:presenter_step)` / `$(patterns:presenter_count)` variables put the click
position on the NEXT key (`$(patterns:stinger)` and `$(patterns:health)` exist too).
Machine health lives in `$(patterns:machine_cpu)`, `$(patterns:machine_fps)`,
`$(patterns:machine_power)` (mains/BATTERY) and `$(patterns:machine_advice)` — put CPU
and fps in a key corner and you have a confidence monitor on the Stream Deck.
