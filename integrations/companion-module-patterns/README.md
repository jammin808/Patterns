# Patterns — Bitfocus Companion module

Stream Deck / Companion control for the Patterns show display suite: fast look recall,
presenter next/back, transport, blackout with live feedback, individual screens and
canvas groups, VOGs and stingers (one-press sounds and clips), break music (Spotify) and the audio track.

## Setup

1. In Patterns: **Remote** page (SETUP) → make sure remote control is on (Companion TCP port,
   default **9697**).
2. In Companion (v3.x): `Connections → Add`, import this module as a dev module
   (`Settings → Developer modules path` pointing at this folder after `yarn && yarn package`,
   or copy the folder into your dev modules directory), then add the **Patterns** connection
   with the machine's IP and port.
3. Drag presets onto keys: Cue stack (GO, standby ▲ ▼, HOLD, ARM, STOP ALL), Transport,
   Presenter, Looks (F1–F12), Screens, VOG, Stingers, Audio, Break music.

## The cue stack (module 1.1.0)

The **GO** preset fires the cue on standby and shows its number and name; it is green while
the stack is armed, amber on HOLD or while a cue waits for confirmation (press GO again
within four seconds), red when the last cue failed or was refused. Every GO sends the
standby id the module last saw, so a GO that races a standby move is refused with
`ERR standby moved` rather than firing the wrong cue; `$(patterns:last_error)` carries the
last ERR line. **ARM** needs "Remotes may ARM the cue stack" on the Remote page. **STOP ALL**
stops the audio track, break music, any VOG or stinger (a clip or a held frame reverts, and a
stinger's ending is cancelled) and the tone — never the outputs, blackout or the stream. Variables: `cue_standby_number/name`, `cue_next_number/name`,
`cue_previous_number/name`, `cue_last_outcome`, `cue_confirm`, `cue_armed`, `cue_hold`,
`cue_seq`, and `program` (what is on air, by name). The module says `HELLO <label>` on
connect, so the caller's history reads "GO from FOH deck".

## VOG and stingers (module 1.2.0)

One library, one numbering. The **Stingers** presets (`stinger_1..8`, `STINGER n`) fire whatever
item *n* is; the **VOG** category (`vog_1..8`, `VOG n`) and the kind-checked stinger keys
(`STING n`, `sting`, `sting_name`) refuse the other kind — Patterns answers `ERR … is a VOG, not a
stinger` — so a key that says VOG never fires a transition. Feedbacks: `vog_playing` (blue — a VOG
clip, or a VOG sound, including one playing over a stinger it ducks),
`sting_playing` (amber) and `sting_hold` (hold amber) — a stinger that landed and is holding the
screens lights every sting key until the caller TAKEs, GOes, or presses the *Held stinger — put
it back* key (`stinger_stop`). `$(patterns:sting_hold)` names the held stinger. Note that the
`program` variable's prefix while something plays is now `VOG:`, `STING:` or `STING HOLD:` —
a trigger matching `STING:` should match `VOG:` too.

## Review on the multiview (module 1.7.0)

The **Transport** category gains **REVIEW** (`review` action: toggle / on / off, `REVIEW …` on the
wire): every multiview — a screen's own multiview pattern, an NDI send of it, `/multiview` — draws
the desk's sandboxed preview full-frame with a REVIEW chip until it is switched off, so the next
look is checked on the monitor wall before the TAKE; the audience's screens do not change.
Feedback `review_on` (green) and `$(patterns:review)` (`ON` / `off`). Every existing action,
feedback and variable id is unchanged.

## The people library (module 1.6.0)

The **Lower thirds** category gains the library from the Lower thirds page: `lower_third_person`
(by number, page order: `PERSON n` on the wire, or `LT <design> WITH n` when a design is named)
and `lower_third_person_name` (by name) put a person — name, role, company and photo — into the
lower third on air (else the last shown, else the first) and show it again: the next speaker in
one press. A name that is not in the library is refused (`ERR … not in the lower-thirds library`),
so a wrong name never reaches the screen. Feedback `lower_third_person_is` (red while that name is
on screen), `$(patterns:lower_third_person)` (the name the lower third on screen carries, or empty),
and the state's `people[{n,name,role}]`. Presets: PERSON 1–6. Every existing action, feedback and
variable id is unchanged.

## Screen locks (module 1.5.0)

The **Screens** category gains `screen_lock` (toggle / lock / unlock, `LOCK n …` on the wire): a
locked screen keeps its picture through every look, cue, TAKE ALL and stinger — a confidence
monitor or an info screen that must not change on a cue — and follows again when unlocked. The
`screen_locked` feedback lights amber while it is locked, and the state's `screens[]` rows carry
`locked` and `role`. Every existing action, feedback and variable id is unchanged.

## Lower thirds (module 1.4.0)

The **Lower thirds** category puts a design from the Lower thirds page on air over whatever is
showing — on every screen, NDI send and the stream at once — and takes it off the way it was
designed to. Actions `lower_third` (by number, Lower thirds page order: `LT n` on the wire),
`lower_third_name` (by name) and `lower_third_off` (`LT OFF`); pressing a design's key again
restarts its way in. Feedback `lower_third_on` (red; a blank name means any design, a name means
that one) and `$(patterns:lower_third)` (the design on screen, or empty). Presets: keys 1–6 and
OFF. Every existing action, feedback and variable id is unchanged.

## Live duck (module 1.3.0)

The **DUCK** key (`duck` action: toggle / on / off, `DUCK …` on the wire) makes way for an
announcement from the room: the music track, break music, a playing stinger's sound and a clip's
soundtrack drop to the Audio page's live-duck level, ramping, and come back when the key is
pressed again — a VOG never ducks. It is a latch, not a programme source: STOP ALL and look
recalls leave it. Feedback `duck_on` (hold amber) and `$(patterns:duck)` (`DUCK` / `off`). Every
existing action, feedback and variable id is unchanged.

## Break music (module 1.2.0)

The **Break music** category drives Spotify through Patterns: play / resume, pause, skip and
keys for entries 1–6 of the Audio page's break-music library (actions `music`, `music_item`,
`music_name` and `music_level` for anything else). Every key lights green while music plays
(`music_playing` feedback); `$(patterns:music)` is the track Spotify reports,
`$(patterns:music_state)` PLAYING / paused, `$(patterns:music_level)` the device level and
`$(patterns:music_device)` the Spotify device. With break music switched off in Patterns the
keys answer OK and do nothing, so a saved page never breaks a cue.

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
