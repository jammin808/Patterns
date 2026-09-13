# Nodes: the arcade, the hub, the caller, the timer — separate processes around Patterns

*An assessment, not a build. What was asked: old-school mini games (Pong, Space Invaders, Snake,
a racer, a duck shoot, an adventure, Draughts, something new) as a separate exe that Patterns
picks up, links and folds into its UX — or at least a separate process; low CPU and GPU, stable,
abstracted, using what game engines know; started and stopped from the left rail; keyboard,
hand-held controller or remote; the same engine running audience polls and quizzes with messages
back to each phone; discoverable so a PC out front can be the games hub; a professional stage
timer and messages-to-stage; and a show caller's own install — cues planned at home, imported or
discovered at the venue, kept in step during the show as an external control node.*

*Status — Rounds A, B and C are built: round 35 (`docs/PLAN.md` §53) landed the node launch
and profile, `--node caller` over the twin's link with the plan offer, the stage timer and
messages with their pages, and the NODES rail; round 36 (§54) the arcade — the engine, Pong,
Snake and Breakout, the pads, NDI out, the ARCADE verbs; round 37 (§55) audience play — the room,
five kinds of question, the wall, messages back, the queue with the assistant's second look,
draughts and the path; round 38 (§56) rig day gamified — the show-ready bar, the alignment
game, Blend Quest, the on-time streak, all behind one opt-in switch. The four rounds are built;
what is left of the plan is in each round's "left for later".*

## 0. The verdict, first

**Viable, and the preferred shape is the right one — with one correction.** Not a separate exe:
a separate *process of the same exe*. Patterns already runs itself four ways from one build — the
watchdog, the desk under it, a native host (`Patterns.exe --host encoder`, the stream encoder in
its own process speaking lines on stdin/stdout), and the standby twin (`--home`, `--standby-of`,
`--key`, started, restarted, adopted and ended by the main's launcher). A **node** is the fifth:
`Patterns.exe --node arcade`, `--node caller`, `--node timer` — a small process with its own
folder, its own crash domain, its own window, that boots a fraction of the desk's services,
announces itself on the beacon, is driven by the wire Patterns already speaks, and puts its
picture on the wall through the source pipeline Patterns already has (NDI from another machine,
the shared frame ring on the same one). Nothing new has to be invented to link it; the work is a
composition root that boots only what a node needs, and then the nodes themselves.

Order of worth, each roughly a round:

| Round | What lands | Why first |
| --- | --- | --- |
| A | **The node kernel and the caller.** `--node caller`: the Show pages alone, no outputs, no generators, works at home on a `.patshow.json`; at the venue the beacon finds the desk (or the desk finds it), the cues are offered as an import with a diff, and during the show the cue sections and the caller's verbs travel both ways over the twin's own link. The **stage timer and messages** as pages the desk serves (`/stage`, `/timer`) and the caller shows. The Nodes rail item. | The workflow win: a caller plans at home and walks in ready. The kernel is what every other node stands on. |
| B | **The arcade.** `--node arcade`: the engine (fixed-step simulation, interpolated render, Skia, input from keyboard, XInput pads, Companion keys and a phone pad), three games (Pong, Snake, Breakout), attract mode, a leaderboard; its picture to the wall over NDI or the ring; `ARCADE …` verbs and cue actions; the Nodes page's cards. | The technicians' rig-day toy, and the base for audience play. |
| C | **Audience play.** The arcade node hosts `/play`: a room code and QR on the wall, phones join with a nickname, polls and quizzes with speed points, word clouds, results on the wall and in the desk's overlays, a message back to each phone, a moderation queue the assistant helps with, Draughts and a choose-the-path adventure the room votes on. | The "mini Slido with gamification", on the hub PC out front. |
| D | **Rig day, gamified.** The alignment game on the mesh (drive the picked point onto the calibration's target with a pad; the residual is the score), the show-ready score on the health line, Blend Quest across a rig's joins. | Small, and it makes the boring hour better. |

What not to do, and why, is §11.

## 1. Why one exe, and what a node is

Patterns ships as one portable folder with one exe; a second exe means a second build, a second
signing and update path, a second copy of the runtime and of Core, and a second thing to be out
of date at the venue. The process boundary is what matters — a game that hits a bug must never
take a show down — and a process boundary costs a flag, not a project. `Program.Main` already
branches on the first argument (`--host` before anything else touches Avalonia; `--standby-of`
before the desk is built); `--node <role>` is a branch beside them.

What a node has, and does not have:

- **Its own folder** (`--home`, as the standby has), so settings, logs and crash notes are its own.
- **Its own window** — the arcade's game surface, the caller's desk, the timer's display — or none.
- **A kernel** of the desk's services, not the desk: the store, the log and crash note, the
  beacon (sending and hearing), the control service (the wire: TCP lines, HTTP, OSC, the same
  `CommandRouter`), the journal, the assistant client, the help. Not the outputs, not the render
  engines, not libVLC, not NDI receive, not the audio engine unless the role asks. This is the one
  real refactor: `AppServices` is a single constructor that builds everything; a node needs a
  composition root that builds a subset. The clean move is a `NodeServices` that owns the kernel
  and an `AppServices` that adds the desk's services to it — the desk keeps its shape, the node
  gets a kernel, and the shared pieces stop knowing which they are in.
- **A vocabulary on the wire** of its own kind, in the one `ShowActionKind` table the wire, the
  cues, Companion and the assistant all read (`ArcadeStart`, `PollOpen`…), so a cue in the desk's
  stack can start a game in the interval exactly as it starts a stinger.
- **A place on the beacon.** The packet the desk already broadcasts once a second carries the
  machine, the instance and the twin port; it gains a `Kind` (desk, standby, arcade, caller,
  timer) and the node's own ports. Discovery is then free in both directions: a node hears the
  desk and dials it; the desk hears nodes and lists them.
- **Supervision** from the desk when the desk started it — the `TwinLauncher` already starts,
  restarts with a growing pause, adopts one the previous desk left and ends on a clean exit —
  and none when it was started by hand on another machine (the desk only lists it).
- **The desk's memory discipline**: a node's working set is bounded like the desk's
  (`MemoryBudget`), its frames retire through `RetiredFrames`, and its quality steps down the same
  ladder when its frames run long.

Budget for a node: under 150 MB working set idle, under 5% of a core idle, a game under 15% of a
core at 1080p60 drawn by Skia on the CPU (simple 2D at that size costs two to four milliseconds a
frame; the GPU backend Avalonia already uses is there when a machine has one).

## 2. The picture to the wall: three lanes, one already open

The desk shows pictures from sources — NDI feeds, web pages, clips, decks — and places them on
any screen or canvas, under the warp, the blend and the overlays. A node's picture is a source.

1. **NDI, another machine.** The node runs the `NdiSender` Core already has, named
   `PATTERNS ARCADE (FOH-PC)`; the desk sees it in the source list like any NDI feed, a cue puts
   it on the wall. Nothing to build on the desk side. Cost: an encode on the node and a decode on
   the desk — fine on a wired LAN at 1080p60 (about 100 Mbit), not for Wi-Fi.
2. **The frame ring, this machine.** `SharedFrameRing` — the memory-mapped ring the desk draws
   into for the encoder host — reversed: the node writes frames, the desk reads them as a source
   named `ring:<node>`. No encode, no network, one copy; the right lane for a rig-day game on the
   show machine and for a local standby-style hub. A day's work on the source side.
3. **The web lane, for results and boards.** A leaderboard, a poll's bars, a word cloud are HTML
   the node serves; the desk's web source renders any page. Lowest effort for the flat pictures
   audience play produces; not for a game (input latency, no controller, Chromium is heavier than
   a Skia loop).

Games render at the wall's size the desk tells them (`ARCADE SIZE 3840x1080` for a joined
canvas), so a racer's track can span two projectors and the blend is invisible under it — the
mesh and the blend are the desk's, applied to the source like any other.

## 3. Control: the wire the node already speaks

A node runs the desk's `ControlService`, so it is driven the way the desk is: TCP lines,
`/api/cmd`, OSC, the Companion module, the phone remote. The desk adds a client side — it dials
a node's wire — so a cue on the desk, a Stream Deck key, the assistant or the Nodes page can say
`ARCADE START snake`, `ARCADE STOP`, `POLL OPEN q3`, `POLL CLOSE`, `POLL SHOW`, `TIMER START 10`,
`TIMER MESSAGE Wrap up`, and every node answers `STATUS` as JSON for feedback and tallies.

Input to a game, from cheapest to richest:

- **Keyboard** on the node's window — arrows, WASD, space; two players on one board.
- **Hand-held controllers**: XInput on Windows through one P/Invoke (`xinput1_4.dll`,
  `XInputGetState`) — every Xbox pad and most USB pads, four at once, polled at the simulation
  tick, no dependency. Not Avalonia's job; the node's.
- **Companion / Stream Deck** as buttons: `ARCADE KEY LEFT` and friends are verbs, so the Interval
  page of a Stream Deck is a controller.
- **The phone pad**: `/pad` on the node — a touch d-pad and two buttons. The remote server's
  long-poll is fine for polls; a d-pad wants a WebSocket (a small RFC 6455 upgrade on the raw
  server the control service already is — the desk's HTTP is a `TcpListener` on purpose, no URL
  ACLs, portable). Budget: touch to wall under 100 ms feels right for Pong at a party; it is not
  a fighting game.

## 4. The engine: what game engines know, brought over

The engine is small and deliberate — not Unity, not Godot, not a browser (§11):

- **A fixed simulation step with an accumulator and an interpolated render.** The world advances
  at 120 Hz in exact steps whatever the frame rate; the frame draws the world interpolated
  between the last two steps at the display's own rate (the desk already measures each output's
  Hz). Determinism follows: the same inputs give the same game.
- **Input sampled at the step, not the frame**, so latency is one step plus one frame, and a
  dropped frame never drops a press.
- **A seeded random source per match**, so a game can be replayed from its inputs — the attract
  mode is a replay, and a bug report is a seed and a recording.
- **Data first**: entities as arrays of structs, systems as loops over them, no allocation in the
  frame — the desk's own rule for its renderers, and why a game at 60 fps costs the GC nothing.
- **Skia**: sprites as `SKPicture`s and atlases, text through the desk's font path, the one
  `SKCanvas` per frame, the retired-frame pool for the frames handed to NDI or the ring.
- **Audio** through the node's own device by default (the technician's laptop speaks; the wall
  does not have to), or through the desk's stinger bus when a cue says so — `ARCADE SOUND DESK`.
  The show lock lets a node's audio through: it is Patterns.
- **Adaptive difficulty as a rule, not a model**: the last three rounds' scores move the ball's
  speed, the invaders' rate, the racer's rivals.
- **Attract mode**: a game plays itself between rounds; a leaderboard with initials, arcade style,
  per show, in the node's folder, exportable.

The first three games are the ones whose physics is a page each: Pong (two players or the house),
Snake (four players on one board, by colour), Breakout. Then the racer (top-down, one to four
pads, a track that spans a joined canvas — the one that shows a blended wall off), the duck shoot
(pointer, pad or phone: where the phone points, the phone's crosshair goes — the audience's
phones as light guns), Draughts (turn-based, two phones on one board on the wall), Space Invaders.
The new one is in §5.

## 5. Audience play: the hub out front

The same node, on the FOH PC, with its HTTP server facing the audience Wi-Fi:

- **Joining**: a room code and a QR on the wall (drawn by the node — a QR is a small table, no
  library); phones open `/play`, pick a nickname, get a token in a cookie. No accounts, no PII,
  nothing kept past the show unless the host exports the results.
- **Questions**: single choice, several choices, a scale, a word cloud, a quiz with a timer and
  speed points (the first right answer scores most), a reveal. Results on the wall as the node's
  picture (bars that grow live, a cloud that blooms) and, for the desk, as data: a JSON feed the
  message overlay reads (`UseFeed` is there already), a lower third of the winner, a cue that
  puts the board up in the interval.
- **Messages back**: to the room ("the poll closes in 30 s"), to a group ("table 4, you won"), to
  one phone ("your answer: B — right"). A moderation queue for anything the room writes: a word
  list first, the assistant second (§9), the host's approval last; nothing reaches the wall
  without a press unless the host turns that off for a quiz.
- **Two games the room plays together**: Draughts on the wall, two phones the pieces; and **the
  path** — an adventure told on the wall in scenes, the room voting each fork on their phones,
  the branches written for the event (or proposed by the assistant within its fence), the story
  the show's own theme. That is the new one: no arcade cabinet ever had four hundred players.
- **Scale and the network**: a few hundred phones on venue Wi-Fi; the server is the desk's async
  accept loop, one task per request, the results a long-poll every two seconds — the pattern the
  caller's page already uses. The risk is the venue's Wi-Fi, not the server: the QR carries the
  IP, a captive portal is the venue's to turn off, and the audience network should be a VLAN
  apart from the show LAN (say so in the doc, and on the page).

## 6. The stage timer and messages to stage, as professional tools

Read here as the two tools a caller runs beside the show software — a timer the presenter sees
and a way to put words in front of them. Patterns has the pieces: the countdown overlay (to a
time or for a duration, a label), the message overlay, the Confidence screen role (notes, the
clock, the next cue), the running order with planned starts and durations, the caller's page at
`/run`. What a dedicated tool has and Patterns does not yet:

- **A timer controller**: start, pause, +1 / −1 minute, next segment, flash, a preset for "wrap
  up" — on the caller's page, on Companion, as verbs (`TIMER START 12`, `TIMER ADD 60`,
  `TIMER NEXT`, `TIMER FLASH`).
- **Segments from the running order**: each cue's planned start and length is a segment; the timer
  follows the stack, and a GO that comes early or late moves the rest — the plan already knows.
- **A stage display in any browser**: `/stage` — a black page with the time, the colour at the
  thresholds (green, amber at two minutes, red over), the message in large type, a flash when the
  caller says so — so the confidence monitor can be a spare laptop, a tablet on a stand, a phone
  taped to the lectern, with no output card at all. The Confidence screen role keeps doing this
  for a real output.
- **Messages with a receipt**: the presenter's page has an ACK; the caller's page shows "seen at
  20:14:03"; unacknowledged after ten seconds, the message flashes.
- **Speaker view and crew view**: the same page with a switch — the speaker sees the time and
  the message, the crew sees the segment, the next cue and the drift.

These are pages and verbs on the desk (and on the caller node), not a process: a timer that ran in
its own process would be one more thing to keep in step with the stack that owns the time. The
`--node timer` role earns its place only on a machine with nothing else on it — a tablet's
kiosk, a small PC behind the lectern — and is then the stage page served locally.

## 7. The caller's own install

`Patterns.exe --node caller` is the desk with the Show pages alone: the cue stacks, the caller's
pad and the notes on cues, the looks and lower thirds by name (to be named in cues, not designed),
the presenter list, the timer, the help and the assistant — no outputs, no generators, no library
of media, no NDI, no audio engine. The same portable folder; the profile is the flag; the
working set is a fraction.

- **At home**: a show file (`.patshow.json`) or a cue sheet (CSV or the first sheet of an .xlsx,
  which the import already reads and the export already writes); the assistant builds cues from a
  brief the way it does at the desk.
- **At the venue, before the show**: the caller node hears the desk's beacon (or the desk hears
  its beacon and lists it under NODES); either side presses LINK; the join uses the twin's key
  — mandatory for a main since round 34 — and the caller offers its cues: an import with a diff
  (added, changed, unchanged, the desk's own that the caller does not have), applied with one
  press, undone with one.
- **During the show**: the link is the twin protocol with a third role. The desk mirrors the show
  to the caller as it mirrors it to a standby (the whole show, then every section a publish names
  dirty), so the caller's LIVE strip, standby cue, tallies and notes are in step within a second;
  the caller sends back the sections it owns — the stacks, the pad, the notes — and its verbs (GO,
  STANDBY, HOLD, the timer's) as the wire's words, which the desk runs as it runs a Stream Deck's.
  Merge: per cue, the newer edit wins and the journal keeps both; the caller never holds outputs,
  never takes over, never mirrors the machine's own sections. The link dropping shows on the
  caller's page as the twin's silence shows on a standby's ("desk silent for 6 s") and changes
  nothing on air.
- **Export at any time**: the show file, the cue sheet, the journal of the night.

Why not a smaller separate app: the pages exist, peeled (`ShowPage`, the run list, the pad); the
protocol exists (the twin's mirror); the file formats exist. A second app would rebuild the pages
and then drift from them. The profile costs the kernel refactor of §1 and a page filter.

## 8. Where it sits on the desk

A **NODES** item in the left rail, above the stream's area as asked: a badge with the count of
nodes heard and a dot for each linked. The Nodes page: a card per node — its kind, machine,
name, what it is doing now (a game, a poll, a timer, a caller in step), START / STOP / LINK /
OPEN (its own page in the browser); the arcade card carries the game picker and the picture's
lane (NDI, ring); the poll card the questions and their state; the caller card the diff and
APPLY. Every card updates from the beacon (once a second) and from the node's STATE pushes, so
nothing on it is stale by more than a second and no press waits on a network round trip — the
press is sent, the card says "starting…", the STATE says started.

## 9. The AI at each step

The assistant runs on the desk with its key beside the settings; a node asks the desk over the
wire (`ASSISTANT ASK <words>`) rather than carrying a key of its own — one key, one fence, one
place that logs what was asked.

- **Quizzes and polls from the show**: the brief already summarises the show (screens, looks,
  cues, designs, the agenda in the cues' names); "ten questions about today's sessions" is a
  proposal of kind `poll` the host applies or edits — never posted by the model itself.
- **Moderation**: every word the room writes goes past a list and then the assistant, which marks
  it fine, doubtful or out; doubtful waits for the host. The fence stays: in scope, no
  instructions taken from the audience's text, nothing about how Patterns works.
- **The path's branches**: proposed within the event's theme, edited by the host, kept in the
  node's folder — the model writes options, the host owns the story.
- **The caller at home**: cues from a brief, as now; at the venue "what is next and how long" from
  the mirrored plan; after the show, the journal read back as a report.
- **Commentary**: leaderboard lines and round summaries as text for the wall, proposed and shown
  on a press, so the game master's voice is the host's.
- **Not the games' rules**: difficulty, physics and scoring are rules, not models — a model in the
  loop would be latency and unpredictability for nothing.

## 10. Gamification in the pro tool, with limits

Principles first: opt-in, per operator, quiet during a live show unless asked; no streaks that
nag, no confetti over a cue; a reward only for a real measure of quality; every one of them off
in one switch. Then the ones that earn their place:

- **The show-ready score** on the health line during a rig day: outputs on, every join's audit
  green, the calibration's residual under a pixel, the show lock held, the super-check all clear
  — each a step of a bar that fills, and a chime when it is full. The same facts the super-check
  already reads, told as progress.
- **The alignment game**: after a camera calibration, the projector shows the solver's target
  rings at each lattice node; the operator drives the picked node with a pad or the arrows; the
  residual in pixels is the score, a sound at "within one"; the lattice fills in as nodes lock.
  A real tool — assisted manual alignment where a hand beats a homography at the edges — that is
  more fun than a spreadsheet of residuals.
- **Blend Quest**: a rig's joins as levels; a level clears when its audit is green and the grey
  check reads flat; the 2×2's middle is the boss. Immersive where the calibration allows: the
  room as the camera sees it, the residual as heat over the wall, cooling as the operator works.
- **The caller's on-time streak**: GOs within the plan's drift, per session, on the caller's page
  — for callers who want it, and nowhere near the stack for those who do not.

## 11. What not to do

- **A separate engine runtime** (Unity, Godot, a JS game framework): another runtime to ship and
  update, seconds of start-up, another crash model, another input stack; everything §4 needs is a
  few hundred lines over Skia the build already has.
- **A browser as the game surface**: input latency and no controllers; fine for boards (§2), not
  for play.
- **A plugin system for third-party games**: the roster stays in-tree and tested; a game that can
  crash is a process, not a plugin, and that is already the design.
- **A timer in its own process** (§6): the stack owns the time.
- **Audience phones on the show LAN**: the hub faces the audience network; the show LAN stays the
  show's.
- **Gamification by default**: off, per operator, always.

## 12. Effort and the order

The kernel refactor is the cost of the first round and the enabler of the rest; the caller rides
it and returns the most to the workflow, so it goes first with the timer pages. The arcade is a
round of engine and three games. Audience play is a round on top of the arcade's server. The rig
day pieces are half a round on top of the calibration. Each round lands with its tests: a node
booted headless, discovered on a loopback beacon, driven by the wire, its picture read back from
the ring; a game's simulation stepped deterministically from recorded inputs; a poll's room of
fake phones voting; the caller's diff and merge against a show edited on both sides.
