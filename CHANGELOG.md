# Changelog

Patterns is built in rounds: one request from the field, answered as a run of commits, each
round closed with its papers. This is the short form, newest first; the long form is
`docs/PLAN.md` (the design of each round, by section) and `docs/REVIEW.md` (what was found and
fixed). Every round from 15 on is a tag on its last commit — `round-15` … — and every tag from
`round-61` on has a Release with the built desk. The README's *Versions and rolling back* says
how to get any of them back. The count at each round is the test suite at its end; the module
is the Bitfocus Companion module, where its version moved.

## Round 62 — 2026-09-15 — the menus that opened nothing, OPEN on every hint, the pointer as the desk's, the Library whole, the RUN monitor, the caller's lower thirds

`round-62` · PLAN §80 · REVIEW round 62 · 2,013 tests

- The right-click menus opened nothing on the desk since round 60: with a `ContextFlyout` set,
  Avalonia's own handler showed the flyout before the desk built the menu. The desk opens its own
  flyout on the context request now, and a test presses the right button through the real input
  pipeline.
- OPEN <PAGE> beside every Build hint that names another page (Media, Reactive, Screens, Library,
  Overlays, Particles, Pattern, Audio, Arcade), kept by a test over the XAML.
- The web page's pointer is the desk's own switch, off by default, kept with the show — no look,
  preset, target switch or TAKE re-arms it.
- The Library holds the saved web pages (YouTube, Vimeo, slides, a page) as tiles with the
  service's colours, and the decks have their chip.
- The RUN surface's monitor: one screen drawn large between the wall and the history, the main
  screen by default; right-click for any screen, a canvas, the programme, or hide; `RUN MONITOR`
  on the wire and OSC, `MENU MONITOR`, `runMonitor` in STATE; a cue may carry it.
- The show caller calls up lower thirds from the Run surface — on the desk and on a caller node,
  where the press goes to the desk.
- The App suite's host, killed three times at ~500 tests: a heap dump named the RUN monitor
  tile's pipeline, made off the tree for a Run layout not yet shown and keeping every closed desk
  alive through the frame budget registry. A tile off the surface has no pipeline now, two tests
  keep it, and the test harness measures memory per boot on request.

## Round 61 — 2026-09-15 — the changelog, a roll-back on GitHub, ASIO and DirectX answered

`round-61` · PLAN §79 · REVIEW round 61 · 1,996 tests

- This file, kept by a test: every round has its entry, in order, with no gap.
- Rolling back on GitHub: a tag per round (`tag-rounds.sh` makes and pushes them, once per
  closed round); a Release per tag with the portable exe, the full
  bundle with libVLC and the Companion module package, the changelog's entry as its notes; the
  **rollback** workflow (Actions → rollback → Run workflow) puts any tag, commit or branch back
  on a branch as a new commit and starts the build on it — nothing rewritten, ever.
- ASIO and DirectX: assessed, not built. ASIO is for a rig that needs stems on an interface's
  channels at a few milliseconds, and WASAPI exclusive mode with a channel map comes first;
  DirectX is already under every frame (Direct3D 11 through ANGLE, DirectComposition, the DXGI
  flip-model chain, D3D11VA decoding), and the one direct use worth building is a decoded frame
  that never leaves the GPU. PLAN §79.4–§79.5.

## Round 60 — 2026-09-15 — right-click menus: the preview first, the operator has the final say

`round-60` · PLAN §78 · REVIEW round 60 · 1,994 tests · module 3.3.0

- Staged verbs: `SCREEN n PVW LOOK|PRESET|PATTERN|PROGRAM|RESET` and `PVW …` land in the
  preview and nowhere else, opening EDIT SAFE by themselves; `SCREEN n PATTERN` live.
- A menu of the desk's own on every tile, screen row, look, lower third, cue row, layer, overlay
  and the PROGRAM and PREVIEW strips, in the desk's colours — amber preview, red air, blue stack,
  mint asks the assistant; a line that cannot be chosen says why; every line shows its wire line.
- `MENU …` answers the same menu as JSON; the Companion module stages on a key.

## Round 59 — 2026-09-15 — the modules: a pure show core, the edges as assemblies

`round-59` · PLAN §77 · REVIEW round 59 · 1,975 tests

- `Patterns.Core` with no package reference at all; `Patterns.Rendering`, `Patterns.Ndi`,
  `Patterns.Arcade`, `Patterns.Devices`, `Patterns.Audio`, `Patterns.Assistant` and
  `Patterns.Audience` as assemblies behind the core's contracts; the tests split into seven
  suites, the core's running with no native library beside it.
- The module rules as a test; the modules named in STATE, the support ticket, the brief and the
  log at every start. `docs/MODULES.md`.

## Round 58 — 2026-09-14 — the critique's P0 and P1

`round-58` · PLAN §76 · REVIEW round 58 · 1,948 tests · module 3.2.0

- Frame truth: one media frame is one thing — a lease binding the pixels, the arrival clock, the
  generation and the ownership; retirement behind the render fence on evidence, never time.
- Memory truth: one media view against one budget, a deterministic pressure ladder, honest pool
  bytes; live changes staged under a source on air (one topology policy); the browser and the
  audio hardened — states observed, never assumed, sound held closed until a route is proven.

## Round 57 — 2026-09-14 — Companion 5 imports the module; memory placed; the live picture's age

`round-57` · PLAN §75 · REVIEW round 57 · 1,922 tests · module 3.1.0

- The Companion module imports into Companion 5: the version moves, the package proven the way
  Companion opens it, one version number in four files or none.
- Memory placed and bounded in bytes, a steady second of video that allocates nothing; the live
  picture's age from the decoder to the frame on the glance, and a low-latency profile per
  capture device.

## Round 56 — 2026-09-14 — a single machine rock solid

`round-56` · PLAN §74 · REVIEW round 56 · 1,904 tests

- The render loop: one world per frame, the geometry built once, faults contained per sink with
  the last good frame kept; a cue's run named and settled by its boxes' receipts.
- The side effects follow the sections an edit touched and say what they cost; the show's files
  off the desk's thread; the desk's second in lanes and the pages warmed with headroom; the
  quality ladder from the sink's rate and p95 with a machine profile; what a box did lately on
  its card; the venue NAT through the socket.

## Round 55 — 2026-09-14 — two fixes from field testing, and which soundtrack goes where

`round-55` · PLAN §73 · REVIEW round 55 · 1,849 tests

- The page's picture at the browser's rate — the browser's own screencast, decoded off the UI
  thread, the rate on the PAGE CONTROLS line; the armed web VT that plays when it goes to air.
- Audio routing: sources × destinations with dB crosspoints, the VOG override, NDI audio, the
  tone; the graph in the App and the ROUTING area on the Audio page. `docs/AUDIO-RESEARCH.md`.

## Round 54 — 2026-09-14 — Companion, second generation

`round-54` · PLAN §72 · REVIEW round 54 · 1,794 tests · module 3.0.0

- The module on Companion 5 with node tests and a CI job; Patterns found on the network and
  Companions heard (mDNS, both ways); the desk drives the deck as a device (TCP 16759); one
  colour language for types and state; nodes, twin and stage on the deck. `docs/COMPANION.md`.

## Round 53 — 2026-09-14 — the desk tells the assistant how it is doing

`round-53` · PLAN §71 · REVIEW round 53 · 1,772 tests

- The health line, the tick, the switch, the GO, the render, the twin and the clocks in the
  assistant's brief, with the super-check's amber and red rows; the fence says to answer from them.

## Round 52 — 2026-09-14 — the GO on the clock

`round-52` · PLAN §70 · REVIEW round 52 · 1,770 tests

- Every GO timed from the press through its publish to the first frame every output drew with
  it; each sink's publish-to-frame lag on the glance line, the Machine page, the super-check
  and the CSV.

## Round 51 — 2026-09-14 — the twin service in parts

`round-51` · PLAN §69 · REVIEW round 51 · 1,765 tests

- One partial class per concern of the twin (the settings and the lines, the main's side, the
  handover, the standby's link, the followers), behaviour identical, both suites the same, a
  guard that keeps each part on its page.

## Round 50 — 2026-09-14 — the switch on the clock

`round-50` · PLAN §68 · REVIEW round 50 · 1,764 tests

- Every page switch timed from the press to the frame, with a budget beside the desk tick; the
  folder read and the wall refresh moved off the switch, so the switch is the frame and nothing
  else.

## Round 49 — 2026-09-14 — one clock across the machines

`round-49` · PLAN §67 · REVIEW round 49 · 1,760 tests

- The link measures the clock offset in NTP-style beats; followers and standbys read the main's
  frame; the offset on the lines, CLOCKS APART on the health line with the fix.

## Round 48 — 2026-09-14 — the node's own Machine tab, the room behind one address, the wire's ceilings

`round-48` · PLAN §66 · REVIEW round 48 · 1,753 tests

- A Machine strip on every node; a venue NAT profile; the wire's timeout and its caps; the
  compact soak script beside the drill. `docs/SOAK.md`.

## Round 47 — 2026-09-13 — the glance, and one clock for the day

`round-47` · PLAN §65 · REVIEW round 47 · 1,741 tests

- Drops and p95 per sink on one line of the Run surface and the caller's strip, a device answer
  on the health line, a CSV row a minute; PLAN SHIFT on the wire and every clock following the
  running order.

## Round 46 — 2026-09-13 — the armed desk

`round-46` · PLAN §64 · REVIEW round 46 · 1,722 tests

- The caller's PLAN and SECTION queued while the stack is armed; calibration and other
  non-show work refused while armed and live.

## Round 45 — 2026-09-13 — the hand-back answered, and a fence that answers

`round-45` · PLAN §63 · REVIEW round 45 · 1,717 tests

- Handover ids, RELEASED from the standby, a stale claim answered with the hand-back it missed;
  the fence needs a box that answers (Accepted or better — no receipts is no route); a route-less
  TAKE BACK across machines is two presses. Drill scenarios added.

## Round 44 — 2026-09-13 — what travels on the twin link, and how the key is known

`round-44` · PLAN §62 · REVIEW round 44 · 1,709 tests

- Mirrored sections only, secrets stripped; the key proven by HMAC challenge-response;
  `docs/DRILL.md` — twenty-two scenarios in a real room, the redundancy's release gate.

## Round 43 — 2026-09-13 — attempts are not facts

`round-43` · PLAN §61 · REVIEW round 43 · 1,702 tests

- Authority fails closed (a tri-state store, committed writes, no kill after an unconfirmed
  ask); the handover as a transaction — TAKE BACK closes the standby only after the wall has
  moved; endpoint confirmation levels Sent, Delivered, Accepted, Observed.

## Round 42 — 2026-09-13 — the game on the wall with nothing between

`round-42` · PLAN §60 · REVIEW round 42 · 1,670 tests

- The local arcade source on the input bus (a frame ring with pinned readers) in patterns,
  layers, the PiP, looks, cues and the wire; the NDI send off the game loop; the arcade window
  fullscreen on a chosen display.

## Round 41 — 2026-09-13 — the caller and the timer on the kernel

`round-41` · PLAN §59 · REVIEW round 41 · 1,662 tests

- The cue stack on a contract, the follower link on a node, the follower's action layer, the
  stage on a node with its pages served locally, the pages and windows on contracts; what else
  belongs on the kernel, and what stays off it.

## Round 40 — 2026-09-13 — the next steps, and the platform they serve

`round-40` · PLAN §58 · REVIEW round 40 · 1,662 tests

- The preview seam retired — the sink composes program and preview; rig day's moments; the
  arcade node built from the kernel alone; a line to a node's wire asked twice when it never
  left; the platform charter (`docs/NODES.md` §13).

## Round 39 — 2026-09-13 — the hardening round

`round-39` · PLAN §57 · REVIEW round 39 · 1,655 tests

- H1–H6: the audience listener of its own with budgets; the twin's fences closed; the published
  snapshot's lists frozen; the kernel as the composition root's first stage; no worker mints
  the UI dispatcher; the audience port's parser bounded, timed and fuzzed, every line reader
  given a ceiling.

## Round 38 — 2026-09-13 — Round D of the nodes: rig day, gamified

`round-38` · PLAN §56 · REVIEW round 38 · 1,633 tests

- The show-ready bar, the alignment game, Blend Quest, the on-time streak — behind one opt-in
  switch.

## Round 37 — 2026-09-13 — Round C of the nodes: audience play on the hub

`round-37` · PLAN §55 · REVIEW round 37 · 1,628 tests

- The room with its code and QR, five kinds of question, results on the wall and as a feed,
  messages back per phone, the queue, moderation, draughts.

## Round 36 — 2026-09-13 — Round B of the nodes: the arcade

`round-36` · PLAN §54 · REVIEW round 36 · 1,614 tests

- The engine (fixed step, interpolation, determinism), Pong, Snake and Breakout, the pads
  (keyboard, XInput, Companion, phone), attract mode, the leaderboard, NDI out, the verbs.

## Round 35 — 2026-09-13 — the nodes assessed; Round A: the launch, the caller, the stage

`round-35` · PLAN §52–§53 · REVIEW round 35 · 1,602 tests

- The assessment of the arcade, the hub, the caller and the timer (`docs/NODES.md`); the node
  launch (`--node`), the caller node linked over the twin protocol, the stage timer and its
  messages with receipts, the NODES rail.

## Round 34 — 2026-09-12 — the twin's ownership hardened

`round-34` · PLAN §51 · REVIEW round 34 · 1,565 tests

- A key made for a keyless main, a takeover that fails closed, the show back on when a local
  standby dies; the twin launcher's folder under the temp path in tests, a failed start says why.

## Round 33 — 2026-09-12 — the mesh, the camera, and a machine that keeps quiet

`round-33` · PLAN §50 · REVIEW round 33 · 1,565 tests

- The mesh warp (a lattice of points pulled into place, drawn as patches); the camera
  calibration (structured light out, a camera in, the rig solved into places, meshes and blend
  masks); the show lock — nothing interrupts the show. `docs/WARP-AND-BLEND.md`,
  `docs/SHOW-MACHINE.md`.

## Round 32 — 2026-09-12 — pages, a standby that runs itself, hot-plug, warp and black

`round-32` · PLAN §49 · REVIEW round 32 · 1,565 tests

- The Screens, Media and Show pages peeled out of the desk view-model; the twin as a second
  process on this machine with its own folder and the hand-back; output hot-plug (unplugged,
  back, new); projector warp and blend (edge bends, black-level matching, a blend grid); the
  caller's pad, a note on any cue, the cue row's own menu.

## Round 31 — 2026-09-12 — the desk peeled to the end, a twin, the room's other boxes

`round-31` · PLAN §48 · REVIEW round 31 · 1,447 tests

- The Assistant, break-music and Audio pages peeled; twin mode — a second Patterns kept in
  step, ready to take the show; endpoints — projectors, Disguise, Pixera, OSC boxes and web APIs
  as devices with a profile (`docs/ENDPOINTS.md`); the assistant's brief names the boxes and
  the twin.

## Round 30 — 2026-09-12 — the recommendations, taken

`round-30` · PLAN §47 · REVIEW round 30 · 1,447 tests

- The Library as a catalogue with one thumbnail queue; a frame per size, words once per
  snapshot, rows that stay, a page that lands from a worker; `ShowActions` by area; the
  lower-thirds designer's edits in a class with no desk in it; the rig's edits in a service.

## Round 29 — 2026-09-12 — the review: what a publish costs, and what the sweep found

`round-29` · PLAN §46 · REVIEW round 29 · 1,447 tests

- A snapshot immutable by construction and a publish that copies what moved; the sweep's
  findings fixed and folded.

## Round 28 — 2026-09-12 — getting a saved picture back

`round-28` · PLAN §45 · 1,447 tests

- Presets recalled where they are saved, and sent to one screen from the show panel.

## Round 27 — 2026-09-11 → 2026-09-12 — the maker's line, the desk's own test card, three states for a look, MIDI

`round-27` · PLAN §43–§44 · 1,442 tests · module 2.8.0

- The badge's line (rig · playback · show control); the Patterns test card, and a new install
  comes up on it; three states for a look, and what each screen is actually doing; MIDI as
  lines, so a control surface rides the area that already exists, then as a device with a map.

## Round 26 — 2026-09-11 — the take told from the edit, the switcher's round trip, two audio wires

`round-26` · PLAN §41–§42 · 1,398 tests

- The badge on a reactive scene, a transition that waits for a take; the switcher's round trip
  (pull a picture in, stage it back, take that tile); two wires — the room's outputs and the
  operator's own; a picture or short clip imported into a lower third; the show's own files off
  the frame budget.

## Round 25 — 2026-09-10 — the page that stopped rebuilding itself, the monitor walls, one pair of ears

`round-25` · PLAN §39–§40 · 1,373 tests

- The Cues page stops rebuilding itself under the operator's hand; the monitor walls become
  the show's, with layouts and any output; the desk listens to one picture at a time.

## Round 24 — 2026-09-10 — a cue with a shape in time, and how one picture becomes the next

`round-24` · PLAN §37–§38 · 1,349 tests

- A cue's steps with a shape in time, dragged into the order you want; six transitions, and a
  stinger the show already owns.

## Round 23 — 2026-09-10 — a page with nothing round it, and a stream you can see

`round-23` · PLAN §35–§36 · 1,318 tests

- A web page with nothing round it; a stream you can see from anywhere on the desk.

## Round 22 — 2026-09-10 — the restart that comes back to the show, and the frame after the fade

`round-22` · PLAN §33–§34 · 1,302 tests

- A restart comes back to the show and not to the edit; a picture change reaches every screen
  on the press.

## Round 21 — 2026-09-09 — the reactive scenes

`round-21` · PLAN §31–§32 · 1,283 tests

- Six curated sound-reactive pictures behind a flash limit that came first; a scene listens to a
  microphone, a capture card or an interface, and keeps listening when the rig is patched late.

## Round 20 — 2026-09-09 — the countdown's one key, the drop's anchor, the screens re-owned

`round-20` · PLAN §29–§30 · 1,258 tests · module 2.7.0

- One key for the countdown; a drop told from the nearest anchor with a place in pixels, and
  RESET puts it back; the screens re-owned when a run is still playing.

## Round 19 — 2026-09-07 — the assistant embedded, the pop-out column, the divider

`round-19` · PLAN §27–§28 · 1,222 tests

- The Assistant page newest first, APPLY landing in the preview only; the brief carries every
  state the desk has at the moment of the ask; a screenshot, a running order, a PDF or a deck
  attached becomes a plan; the pop-out settings column; the divider drags on the wide pages.

## Round 18 — 2026-09-07 — the studio, the sim, the start and the runtime

`round-18` · PLAN §25–§26 · 1,212 tests

- The particles given the fractals' treatments, a sim per field on every sink; a faster start
  and restart; what .NET 10 buys, applied — the collector in sustained low latency while the
  outputs are live.

## Round 17 — 2026-09-06 — the desk in use

`round-17` · PLAN §23–§24 · 1,193 tests

- The crash between pages fixed (a page's sink behind a gate, never disposed under a frame);
  the assistant answers again; the grid on the tiles as device pixels; tiles with one face; a
  send keeps the preview; → PVW from any wall tile; the Patterns badge; groups of screens; a
  Fractals page on the BUILD rail.

## Round 16 — 2026-09-06 — one action vocabulary

`round-16` · PLAN §21–§22 · 1,163 tests

- A cue's step is a `ShowAction`, every verb in one table, the wire returns show actions; the
  runtime on .NET 10 LTS; the lower-third fractal through the shader; ReadyToRun in every
  publish; the stream encoder in its own process behind a shared-memory frame ring and a
  watchdog.

## Round 15 — 2026-09-06 — the crash between menus, fade to black per screen, the desk's room

`round-15` · PLAN §19–§20 · 1,144 tests · module 2.6.0

- The first round in the repository's history: the round's papers, with the cloud question
  answered and the round read against the standing brief.

## Before the history — rounds 1 to 14

The tree before 2026-09-06 is not in the repository's history; these rounds live in
`docs/PLAN.md` and the checklists (`docs/CHECKLIST-round9.md` … `round14.md`).

- **Round 14** — the stinger triage, the crash, the next steps, the desk's surfaces. PLAN §17–§18.
- **Round 13** — the caller's clock, the audio playlist, the weather, the assistant, the desk.
  PLAN §15–§16.
- **Round 12** — the graphics op, the presenter, the installer and the wire; the operational
  review, the multi-core question, the instant-UX pass. PLAN §13–§14.
- **Round 11** — the desk, the caller and the network; the core kept, extracted at the seams.
  PLAN §11–§12.
- **Round 10** — the desk asks for more. PLAN §10.
- **Round 9** — heard from the desk: effect stings, sound-reactive fractals, and the rest of the
  list. PLAN §9.
- **Rounds 1–8** — the plan and the first desk: the engine and its sinks, the patterns, the
  shell, and run mode in its phases — the one action layer, content targets, the cue stack, the
  run layout, the CUE verbs, the shell's five groups, multiview, break music, the stinger
  library. PLAN §1–§8.
