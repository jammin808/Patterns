# Patterns — Bitfocus Companion module

Stream Deck / Companion control for the Patterns show display suite, version **3.20.0** — a
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
<label> module=3.20.0` on connect, so the desk's Remote page can list every deck and its module — and, when the
connection's **Pairing token** field is filled, it proves the token against the desk's `NONCE` with `AUTH-PROOF`: a
desk with a token set (Remote page, TRUST) runs a verb only from a connection that proved it, and answers `ERR not
paired` otherwise; from round 91 it shows its state only to a paired connection too — it says `NONCE` first and no
`STATE` until the proof is answered `OK paired`, so the module pairs before it reads (`NAV` and the recorder wait for
that answer, and `NAV` goes at once when the desk greets with `STATE` first). The token itself never crosses the network: it goes plain (`AUTH`) only to a desk on this machine too
old to give a nonce. The module shows the wrong or missing token as a bad-config status with the words — with the
field empty on a desk that says `NONCE`, *Pairing token needed — this desk asks for its pairing token and shows its
state only to a paired connection*.

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

- **3.20.0** — the Stagewatch link's own record (round 93, Show Bus 1.3 §13.16): `stagewatch_link_state` reads STATE's
  `bus.stagewatch.link.state` — off, finding, connecting, live, stale, down, held or refused — `stagewatch_link_light` its
  light (red, amber, green or grey) and `stagewatch_link_flags` its flags, comma-separated (`legacy` while the Stagewatch
  cannot prove itself to the desk, `keyless` while the desk reads its public view, `first` for a certificate pinned at first
  use, and the rest); the `stagewatch_link_light_is` feedback colours a key by the record's light. A desk before round 93
  sends no record: the three read empty and the feedback lights for no light — the module says nothing of a link the desk did
  not describe. `stagewatch_light` stays the linked app's light (no better than the record's while the link is live); a link
  that is held, down or refused while the show needs it is red on `stagewatch_link_light`, whatever the app's light.
- **3.19.1** — the module pairs before it reads (round 91, the desk's ADR-016): a desk with a token says `NONCE` first
  and shows no `STATE` to a connection that has not paired, and refuses `STATUS`, `EYE`, `CONTEXT` and the other reads
  with `ERR not paired …`. With the **Pairing token** field filled the proof is asked and its answer read: `NAV` (the
  Navigator's rails and pages) and `RECORD ON` go once it is `OK paired` (or `OK open`), and the desk's greeting `STATE`
  follows that answer; a desk that greets with `STATE` before any `NONCE` gets `NAV` at once, and a proof that is never
  answered, refused, or not possible (no proof offered, a nonce that is not a desk's) settles the pairing too. With the
  field empty the reads go at once — `NAV` stays open on the desk — and a `NONCE` says the token is needed in the status
  (*Pairing token needed — …*), not the wrong-token words. Only a connection the desk paired reads a later `ERR not
  paired` as a token the desk no longer takes (*The desk did not take this connection's token — …*): NEW TOKEN on the
  desk says so and closes the connection, its keys go dark, and on reconnecting the old token's proof is refused once
  and not sent again on that connection. A 3.19.0 deck given the token keeps working on a round-91 desk; one without it
  lights nothing until the token is typed.
- **3.19.0** — what the linked Stagewatch made of the desk's key (round 91): the `stagewatch_key` variable reads STATE's
  `bus.stagewatch.key` — `key` or `operator` (the key speaks for this desk), `none` (Stagewatch takes it and lets it speak
  for no node of the desk's, or the link carries no key), `refused`, or empty while Stagewatch has said neither (a
  Stagewatch older than 2.18 sends no verdict). A desk before round 91 has no such field, and the variable reads empty.
- **3.18.1** — the token never in clear (round 89): plain `AUTH <token>` goes only to a desk on this machine (the
  address the socket reached, 127.0.0.0/8 or ::1). With no `NONCE` by the wait's end the module asks the desk for one
  (`NONCE` → `OK <hex>`) and proves it; a desk that answers `ERR` (before round 87) on another machine keeps the token
  here, and the connection says *The desk asks for no proof — update Patterns; the token stays here*. `AUTH-PROOF`
  answers only a nonce of 32 lowercase hex, and a connection that heard a `NONCE`, or any `OK` to its own `NONCE`
  ask, never sends plain `AUTH`; the `ERR` an older desk gives that ask is its answer, not a fault (no `last_error`). The
  `stagewatch_stuck` variable and feedback (round 89: a Stagewatch that says its show is stuck) ride in this version too.
- **3.18.0** — pairing without the token on the wire (round 87): a desk with a pairing token says `NONCE <hex>` on
  connect and the module answers `AUTH-PROOF <hex>` (HMAC-SHA256 of the token over the nonce and this connection's
  name) — the token itself never crosses the network. A desk before round 87 sends no nonce, and plain `AUTH` follows
  after a moment, as before. Round 87 desks take plain `AUTH` from their own machine only.
- **3.17.0** — the clip's transport (round 86): *Video — pause, play on or stop the clip on air* sends `VIDEO PAUSE`,
  `VIDEO PLAY` or `VIDEO STOP` (the clip back to its top, held — never the outputs, which bare `STOP` is), and *Video —
  move the clip on air to a point* sends `VIDEO SEEK 1:23` or `VIDEO SEEK END 5`; two presets under Presenter (VT ⏸ PAUSE,
  VT ▶ PLAY). A desk before round 86 answers ERR to them — update the desk.
- **3.16.0** — groups are kinds (round 81): the canvas key sends `CANVAS <letter> ON / OFF` (a desk before round 81
  read `GROUP <letter>` for it and answers ERR to CANVAS — update the desk); a fade or take to GROUPS means every
  screen of the ticked tiles' groups (main, confidence, info), a joined canvas being a canvas, not a group; a scoped
  take lands on its screens alone, as their own pictures. Two variables beside the take plan's: `take_focused` (the
  tile FOCUSED means, by its label; empty for the PGM tile) and `take_groups` (the ticked tiles' groups, what TICKED
  GROUPS takes to), read from STATE's `take.focusedLabel` and `take.groups`; `take_scope` can read the main /
  confidence / info screens.
- **3.15.0** — the desk's descriptor version read (round 80): every NAV and MENU reply's `protocol` is read into
  `desk_protocol`; a desk that speaks a newer descriptor than this module knows (1) shows a warning on the
  connection and the keys keep working on what they understand. Two variables beside the machine's:
  `master_fps` (the master rate in force — the setting, or the slowest display it follows; 0 unlimited) and
  `master_rate` (the words: *50 fps — following Lobby (50 Hz); set 60*), from STATE's `masterFpsEffective`
  and `masterRate`.
- **3.14.0** — the Navigator (round 74): the desk's rails, pages, menus and drawers on 24 slot keys (`nav_slot`, `nav_home`, `nav_back`, `nav_prev`, `nav_next`, `nav_rail`, `nav_page`, `nav_menu`, `nav_mode`, `nav_follow`, `nav_text`, `nav_refresh`), the desk's own pages turned (`nav_desk`, `nav_desk_page`), every line sent matched to its reply so a key can ask the desk a question (NAV, MENU PAGE Looks, MENU LOOK Walk-in), the build verbs (a look saved, a cue added, a preset saved, a design made) from the keys with `nav_text` for the words, Companion's Action Recorder fed by the desk (RECORD ON / OFF — what the desk does comes back as the line that reproduces it), LEARN on the raw line (the desk's next action becomes the key's line), Stream Deck + knobs stepping the audio and break-music levels (`audio_level_step`, `music_level_step`), `nav_*` and `desk_*` variables and feedbacks, a Navigator section with the rails and pages from the desk's own table.
- **3.13.0** — MIDI learn on the deck (round 73): `midi_learn` arms the desk for a wire line (the next control moved on any open surface is bound to it, saved with the show), `midi_learn_off` cancels, `midi_forget` unbinds every control bound to a line; `midi_learning`, `midi_bindings`, `midi_surfaces` and `midi_words` variables; a `midi_learning` feedback (amber while a learn waits, for any line or one carrying a word).
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
