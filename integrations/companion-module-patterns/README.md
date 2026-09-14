# Patterns — Bitfocus Companion module

Stream Deck / Companion control for the Patterns show display suite, version **3.0.0** — a
Companion 5 module (module base 2.x): the desk found on the network by itself, one colour
language across every key, keys that label themselves from the show, and — new in 3.0 — the
speaker's stage timer in its own colour with a progress ring, messages to the stage, every other
Patterns on the network (the nodes) on keys, the twin's state with two-press TAKE OVER / TAKE
BACK, the plan's slip / resume / catch-up, the arcade and the audience's play.

## Setup

1. **Patterns**: the Remote page (SETUP) — remote control on; the wire is the *Companion (TCP)*
   port, 9697 unless you changed it. The desk announces itself on the network (`_patterns._tcp`),
   so the next step needs no typing.
2. **Companion 5**: *Modules → Import module* with the package this repository's CI builds
   (`companion-module-patterns` artifact, a `.tgz`), or `npm ci && npm run package` in this folder
   and import `pkg/*.tgz`. Then *Connections → Add → Patterns*: pick the desk under **Desk on the
   network** — every Patterns on the network is listed by kind and machine — or type its IP.
3. Tick the **preset groups** this desk uses and drag keys from the sections: the banks first (Look
   bank, Cue bank, Screens, Nodes — keys that label themselves and dim while empty), then Cue stack,
   Transport, Stage, and the *… — this show* sections with one key per item of the loaded show.

Developing: `npm ci`, `npm test` (the module boots against the real module base with a fake host
and a fake wire; every preset goes through Companion's own preset sanitiser), `npm run lines`
(rewrites `test/lines.txt`, every line the module can send — the desk's test suite parses it),
`npm run package`.

## What the keys are

| Section | Keys |
|---|---|
| Cue stack, Cue bank | GO (green armed, amber on HOLD or waiting for a confirm, red after a refusal), standby ▲ ▼, HOLD, ARM, STOP ALL, DUCK, the day's timing with PLAN −1 / +1 MIN, RESUME NOW, CATCH UP; a bank of seven keys that read the standby cue and the six after it |
| Transport, Stream | Outputs on / off, blackout, FREEZE, REVIEW, EDIT SAFE, fades (the rig, the focused or ticked tiles, a screen, a canvas), SHOW LOCK, the stream's health |
| Looks, Look bank | Sixteen bank keys by place, F1–F12, the look on air in three states (up, changed since, not up), PREVIOUS LOOK, one key per look of the show |
| Screens | Per screen: toggle, lock, back to the program (amber when it has gone its own way), the picture it is showing, fade down / up; canvases A–D |
| Presenter, Web page | NEXT / BACK, the deck's pages, the VT clock (red for the last ten seconds), the web page's actions; the armed web VT — ARM VT, MARK VT, DISARM VT (lit while a page's video is held at its mark to play when the page goes to air) |
| Lower thirds, People | Designs and people by number and by name, the sign-off flow (preview, TAKE, UPDATE) |
| Stingers, VOG | Kind-checked keys, the held stinger put back |
| Audio, Break music, Playlist parts | The playlist's transport and a bank of tracks, break music, the show's parts; ROUTING — the matrix of which soundtrack goes where, on or off (the `audio_route` and `audio_vog` actions put a source on a destination at a level and set what a VOG does there) |
| Clock, Countdown, Message, Overlays | Every overlay from a key that reads what is on; COUNTDOWN FOLLOW the plan |
| **Stage** | **STAGE TIMER** — what the speaker sees, in the timer's own colour (green, then amber and red at the desk's thresholds), with a ring for how far through on Companion 5's layered keys; pause / resume, ±1 min, FLASH, WRAP UP and a crew message (amber until the stage page ACKs), the pending message, the running order |
| **Nodes, Twin** | Eight node keys by place, each in its kind's colour (desk, caller, arcade, stage timer) and dark when a node stops being heard; the twin's role and state; TAKE OVER / STAND BY / TAKE BACK |
| Install | The schedule, announcements, adverts |

Actions the sections do not show as keys: `arcade` (start, stop, pause, resume, attract, NDI),
`arcade_key` (a pad's key), `play` (the audience's next question, open, close, reveal, the wall's
picture), `raw` (any line of `docs/REMOTE.md`).

## Colours

One hue per kind of thing, one treatment per state (`src/palette.js`, held equal to the desk's own
table by a test on each side): green on air / armed / running; amber a preview, a hold, *changed
since*, a message waiting; orange late or a screen gone its own way; red a lower third on screen,
the stream live, a failed cue, black on its own; sky blue the overlays; steel blue the presenter's
things; a colour per node kind; dim for a bank key with nothing behind it.

## Feedback

Patterns pushes `STATE {json}` on every change (throttled to 200 ms) and every second while a
countdown, a clip or the stage timer runs; the module turns it into the variables the keys read
and rechecks every feedback. `$(patterns:desk_version)` and `$(patterns:show)` say which desk and
show a deck is on; `$(patterns:last_error)` carries the last refusal. The connection says `HELLO
<label> module=3.0.0` on connect, so the desk's Remote page can list every deck and its module.

## From 2.x

Every action, feedback and variable id of 1.x and 2.x is kept, so saved pages keep working. The
preset categories became Companion 5 sections with the same names, `pattern_is` and the *Patterns
— every kind* keys are unchanged, and the *Preset groups* settings gained *Stage* and *Nodes*.
Companion 3.x and 4.x cannot load a module base 2 module; the last 2.x module (2.8.0) is in this
repository's history for them.

No module? The same protocol works with Companion's built-in **Generic TCP** connection — one
command per line as `docs/REMOTE.md` lists them — without feedback.
