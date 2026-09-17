# Patterns — Bitfocus Companion module

Stream Deck / Companion control for the Patterns show display suite, version **3.12.0** — a
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
show a deck is on; `$(patterns:last_error)` carries the last refusal. `$(patterns:machine_faulting)`,
`$(patterns:devices_failing)` and `$(patterns:device_last_failure)` say when an output or a box is in
trouble, and the `render_faulting` and `device_failing` feedbacks light a key red for it;
`$(patterns:cue_last_pending)` counts the device receipts the last cue still waits for. From 3.6.0 the
rig is on the keys too: `$(patterns:screen_n_signal)` is what Windows reports a screen's link carrying
against its contract (MATCH / MISMATCH / PARTIAL / UNVERIFIED — MATCH alone is a pass), `$(patterns:machine_rig)`
is the known-good rig's verdict and `$(patterns:commissioning)` the flow's headline; the `screen_signal_is`,
`signal_mismatch_any`, `rig_known_good`, `rig_drift` and `commissioned` feedbacks colour them. The connection says `HELLO
<label> module=3.12.0` on connect, so the desk's Remote page can list every deck and its module — and, when the
connection's **Pairing token** field is filled, `AUTH <token>` straight after it: a desk with a token set (Remote
page, TRUST) runs a verb only from a connection that presented it, and answers `ERR not paired` otherwise. The
module shows the wrong or missing token as a bad-config status with the words.

## Installing a build into Companion 5

Companion 5.0 and later takes a module package from the Modules page: **Modules → Import module
package**, then pick the `.tgz`. The package is what `npm run package` (`companion-module-build`)
writes beside this folder — `jammin808-patterns-<version>.tgz` — and what the repository's CI
uploads as the *companion-module-patterns* artifact; GitHub wraps every artifact in a `.zip`,
so unzip that first and import the `.tgz` inside it, never the zip ("Failed to decompress data").
The manifest declares the `node22` runtime, which Companion 5.0.x bundles beside `node18` and
`node26`, and module API 2.1, which Companion 5.0.5 hosts.

**Companion refuses a package whose id and version it already has** — *Module jammin808-patterns
v3.0.0 already exists* — so a rebuilt module with the same version never lands: every change here
moves the version (3.0.0 → 3.1.0 for the round-56 variables and feedbacks, 3.2.0 for round 58's, 3.3.0 for round 60's staged verbs), and an older version
already installed can stay beside the new one or be removed from the Modules page first. The
package test (`test/package.test.mjs`) builds the `.tgz` and checks it the way Companion does on
import and start: one root folder, `companion/manifest.json` valid under the module base's own
schema, the runtime one Companion 5 bundles, the API version the host carries, the entrypoint
present and loading under Node, the package's `type: module`, a licence that is not empty, and
the version equal in the manifest, the package and the `HELLO` the desk reads.

## Versions

- **3.12.0** — the Library on the deck (round 73): a `library` action puts a tile (a factory pattern, a file, a
  saved page, a preset, a brand kit) on the desk's editing target's preview, the programme's, or a screen's —
  `LIBRARY <name>`, `PVW LIBRARY <name>`, `SCREEN n PVW LIBRARY <name>` — exactly what a click on the Library
  page does; `screen_stage` and `pvw` gain the LIBRARY choice; `editing_target`, `editing_kind`,
  `editing_editor` and `editing_library` say what the desk's editors are on; `library_selected` lights amber
  while a tile sits on the editing target. Timed lower thirds: `lower_third_for` puts a design on for this
  run's hold (`LT n FOR 8`; 0 or STAY = until hidden), `lower_third_hold` sets the design's own (`LT n HOLD 8`);
  `lower_third_timed` and `lower_third_leaves_in` read the countdown.
- **3.11.0** — signal truth's PARTIAL and the take ticket (round 72): `screen_n_signal` reads PARTIAL when
  everything the path states agrees with the contract and a contracted property was never stated (a
  colour space Windows never reports, a bit depth the driver did not say) — amber on the `rig_signal_n`
  keys and a choice of `screen_signal_is`; the `signal_partial_any` feedback; `$(patterns:take_landing)`
  is the ticket a TAKE under a video sting froze at the press ("→ 1 · Left, 2 · Right when 'Whoosh' ends")
  while the clip runs, with the `take_landing_pending` feedback. A 3.10.0 deck reads PARTIAL as its words.
- **3.10.0** — the sound follows the picture (round 69): `screen_audio` names the output a screen's
  sound leaves by (`SCREEN n AUDIO <output>`; the route follows the picture — the programme, its own
  picture, the screen it repeats — moving with every take), `audio_follow` switches the following
  (`AUDIO FOLLOW ON|OFF|TOGGLE`); the `audio_follow_on` and `screen_sound_out` feedbacks; the
  `audio_follow`, `audio_follow_words` and `screen_n_audio` variables; a FOLLOW key on the Audio page.
  Two shades: the audio family's `follow` (sky), the screen family's `sound` (blue).
- **3.9.0** — the web page's picture on the deck (round 68): STATE's `web.path` row — the smoothing
  buffer and its depth, the delay it adds, the measured jitter, the decode time, the frames delivered
  and presented, the stalls, the frames dropped or skipped, the pool, and what the browser is asked to
  hand over — as the `web_path`, `web_smoothing`, `web_latency`, `web_underruns` and `web_capture`
  variables and the `web_smoothed` and `web_stalled` feedbacks. No new actions; a 3.8.0 deck ignores the row.
- **3.8.0** — the next take and the group on the deck (round 67): the `take_next` action (`TAKE NEXT
  <transition | STING name | CLEAR>` — the transition or video sting for the next TAKE alone; the show's
  own transition never moves) and `screen_group` (`SCREEN n GROUP main | confidence | info | repeater`);
  the `take_next_set`, `take_next_sting`, `screen_group_is` and `screen_ticked` feedbacks; the `take_next`,
  `take_scope`, `take_words` and `screen_n_group` variables; a Take preset page and MAIN / CONF group keys
  per screen; the `take` colours and `screen.group`; the module reads STATE's `take` row and `screens[].ticked`.
- **3.7.0** — the God's Eye on the deck (round 66): `eye_focus` / `eye_next` / `eye_prev` / `eye_lens` /
  `eye_reset` actions, the `eye_worst` and `eye_problems` feedbacks, the `eye_headline`, `eye_worst`,
  `eye_problems` and `eye_focus` variables, the Eye preset page, the `eye` colours; the module reads
  STATE's `eye` row.
- **3.6.0** — the round-65 rig words: `screen_signal` / `screen_testroute` / `rig_save` actions,
  the `screen_signal_is`, `signal_mismatch_any`, `rig_known_good`, `rig_drift` and `commissioned`
  feedbacks, the `screen_n_signal`, `machine_inventory`, `machine_rig`, `commissioning` and
  `commissioning_next` variables, the Rig preset page, the `signal` and `rig` colours.
- **3.5.0** — the pairing token: the connection's **Pairing token** field, `AUTH <token>` after
  `HELLO`, a trust refusal shown as a bad-config status.
- **3.2.0** — the round-58 deck words: `machine_memory_pressure` (the rung the desk's media
  memory ladder stands on) and `inputs_pending` (a reopen staged under a source on air); the
  `memory_pressure_at_least` and `inputs_change_pending` feedbacks.
- **3.1.0** — the round-56 deck words: `machine_render_faults`, `machine_faulting`,
  `cue_last_pending`, `devices_failing`, `device_last_reply`, `device_last_failure`; the
  `render_faulting` and `device_failing` feedbacks; the `fault` colours; the package test; and,
  under the same number, round 57's `machine_live_age` with the `live_age_over` feedback.
- **3.0.0** — the Companion 5 port (module base 2.x): discovery, the colour language, the stage,
  the nodes, the twin, the plan, the arcade, the audience, the armed web VT, the routing matrix.

## From 2.x

Every action, feedback and variable id of 1.x and 2.x is kept, so saved pages keep working. The
preset categories became Companion 5 sections with the same names, `pattern_is` and the *Patterns
— every kind* keys are unchanged, and the *Preset groups* settings gained *Stage* and *Nodes*.
Companion 3.x and 4.x cannot load a module base 2 module; the last 2.x module (2.8.0) is in this
repository's history for them.

No module? The same protocol works with Companion's built-in **Generic TCP** connection — one
command per line as `docs/REMOTE.md` lists them — without feedback.
