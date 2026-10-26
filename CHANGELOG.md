# Changelog

Patterns is built in rounds: one request from the maintainer's desk or rig — and from round 76
on, the show laptop's own files — answered as a run of commits, each round closed with its papers. This is the short form, newest first; the long form is
`docs/PLAN.md` (the design of each round, by section) and `docs/REVIEW.md` (what was found and
fixed). Every round from 15 on is tagged on its last commit — `round-15` … — by
`.github/scripts/tag-rounds.sh` once the maintainer has run it, and every tag from `round-61` on gets
a Release with the built desk. The README's *Versions and rolling back* says how to get any of them
back. From round 79 each entry names its evidence class: the maintainer's list, a handed-in critique,
a verbal report from the rig, the show laptop's files, web research. The count at each round is the test suite at its end; the module
is the Bitfocus Companion module, where its version moved.

## Round 91 — 2026-10-26 — Show Bus 1.3 from Patterns' side: the Stagewatch link dials as a node and reads its verdict, the wire shows its state only to a paired connection, the context says what a peer may propose, and the certificate and the hello at 1.3's numbers

`round-91` · PLAN §109 · REVIEW round 91 · 2,942 tests · module 3.19.1 · evidence: a handed-in critique (the Show Bus 1.3 designs — the contract, peers that are not screens, cross-app proposals — with two items of the Eye's critique, and the plan revised against its critique)

- **The Stagewatch link dials as a node and reads its verdict (91.1).** It dials `/ws?node=<this desk's show network
  id>&t=patterns&cid=<id>`, the key in the `Authorization` header only, and sends nothing keyed until it has read
  Stagewatch's verdict. A refused key holds every keyed call and the redial for the larger of 60 s and the Stagewatch's
  `retry` (an hour at most) while the timer follows the public frame; a rate limit holds them for its `Retry-After`; a
  key that speaks for no node of this desk's holds only the context this desk posts and its proposals; a fresh verdict
  lifts the hold. STATE `bus.stagewatch.key`, the Nodes card, the Eye, the context's peers, the brief and the module's
  `stagewatch_key` (3.19.0) say the standing, one log line each change; a result a hold stopped is sent once the routes
  open, never run twice. Against a Stagewatch before 2.18, which sends no verdict, the key is judged by its HTTP answers
  and labelled amber (P-legacy, L66).
- **The wire shows its state only to a paired connection (91.2, ADR-016).** On a desk with a token a connection hears
  `NONCE` first and no STATE — no greeting, push or `ACTION` feed — until `OK paired`; before that it may ask the
  handshake and five reads the maintainer left open by name (`NAV`, `MENU`, `CUE LIST`, the arcade's and the room's
  status, `ASSISTANT`), and `STATUS`, `EYE`, `CONTEXT` and every other read answer `ERR not paired — this desk shows its
  state only to a paired connection …`, counted on the Super Check's REMOTE row *Wire reads* and logged once per
  address. NEW TOKEN tells a paired connection why and closes it. On a paired desk OSC's `/patterns/status` answers this
  machine and OSC ALLOW only — an OSC poller off the list loses the show's state until it is listed (ADR-017 amended).
  The module (3.19.1) pairs before it reads.
- **The context says what a peer may propose (91.3).** Nineteen rows after the fourteen: `APPLY` (review), `VIDEO STOP`,
  the Eye's moves, `CUE STANDBY`, the staged and per-screen takes, the Library forms (high: a brand kit reaches the air)
  and six web rows — `WEB OPEN` and `WEB TYPE` declined (L70); a fence replays today's Stagewatch rater and 1.3's so
  no line that changes the air reads lower. `APPLY` runs only the proposal this desk made, under its own id, once. A
  peer's context is read to 256 commands, verbs whole to 512 characters, a missing risk high; the brief lists 32.
- **The certificate and the hello at 1.3's numbers (91.4).** Certificates span 825 days, and a 1,826-day one is replaced
  once at a start (pinned clients see CERTIFICATE CHANGED once, the log names the old fingerprint). The hello says
  `proto` 3 within 1,200 bytes; the reader keeps one peer per id with up to four addresses and a dial that stays, marks
  an id two running machines say amber in every reader and dials no new link to it, and holds at most 128 ids, counting
  what it put out (`bus.dropped`).
- **Opened (91.0).** Nothing in `docs/field/`; the requester and the evidence class; the owner's answers the round reads
  (P-legacy, scope A, X-declines); the other trees at the opening; scope A read against this wire's grammar, every query
  kind put on one side; each unit read against the standing brief at the line it stands on (PLAN §109.1).
- **The papers (91.6).** 91.5, the Show Bus 1.3 copy and its kit, moved under the plan's rule R3: 1.3 is not final
  (Stagewatch's SW19.12 has not landed), so it takes the number of the first round after it lands — round 92 if 1.3 is
  final before 92.1, which reads its vectors from the copy; otherwise 92.1 moves with it. 91.7, the twin's new key kept
  out of the log, did not land: it waited on nothing, so R3 does not cover it, and the round closed without it by
  choice; round 92's opening numbers it. Until then a first run that makes a twin key before the window exists writes
  it to `patterns.log` in clear (L65, low: the settings beside the exe hold the same key). OPEN: L59 closed; L66 to L68
  and L70 to L72 kept as their units added them, L69 closed by 91.3; L73 to L76 added (the hello table's eviction
  against §2's words, this desk's own id said elsewhere, the 9700 table unbounded, a forged hello lighting the Eye's
  problems); L47, L50, L56, L64 (run 278's census failure seen here under load), L65 and L66 (the dial's
  `page=/patterns`) annotated. QUALIFICATION §26 and §27 gain a note each. Round 90's row in the tag table. Walk: none
  asked. The closing row: an unpaired `STATUS` refused, beside its one log line and *Wire reads*.

## Round 90 — 2026-10-26 — the bench host: the real desk headless off Windows, for Stagewatch's interop lane

`round-90` · PLAN §108 · REVIEW round 90 · 2,891 tests · module 3.18.1 (unchanged) · evidence: a handed-in critique (the ecosystem-lane design written on Stagewatch's side, its critique and the plan revised against it)

- **The bench host (90.1).** `tools/Patterns.Bench` boots the real desk — its services, view model and main window —
  headless off Windows through `HeadlessDesk`, the boot the App suite's `TestApp.Boot` now uses too, so Stagewatch's
  interop lane can run Patterns beside Stagewatch, PlayMap and AGMessage on one Linux kernel. Its settings are written
  before the boot, with the token and a Stagewatch key read from files and never from the command line, and no caller
  node may link; its ready line says the ports, the show network id and the build, and no secret; its clips are
  synthetic (`BenchClip`; libVLC is off in its process alone); `clip`, `propose look` (the approval desk's road only,
  never applied here) and `exit` are its control lines, and anything else is refused without being repeated. Fenced as
  `src/` is, in the solution, in no publish. No wire verb, STATE field, menu entry, deck action or Eye, Run strip or
  Super Check row moves, so there is no journal row, log line or Super Check row to read for one.
- **Opened (90.0).** Nothing in `docs/field/`; the requester and the evidence class; run 278 read for the ledger;
  Stagewatch 2.18's release and round 89's red test job read against the rule that a round's papers close only after
  the train before has closed; each unit read against the standing brief at the line it stands on (PLAN §108.1).
- **The papers (90.2).** OPEN: L25 closed (the tags `round-15` to `round-88` and round 88's Release on the remote);
  L62 closed on WebView2's probe and the lifecycle job, both green in run 278, its test half moved to L64; L63 (the
  bench's synthetic clips, libVLC on Linux unmeasured), L64 (run 278's test job red on round 89's code — four of the
  five App tests the Stagewatch link's, which pass on Linux here, cause not known) and L65 (the twin's new key in the
  log on a desk's first run, for round 91) added; L40, L47, L50 and L56 annotated. The train before read again by the
  plan's four measures, with the evidence for each; Stagewatch 2.18 is not released, and the papers read its release
  as the maintainer's PC work, named and not run — their own reading, not the plan's words — and the order of the
  releases stays the maintainer's (§108.6). Round 89's row in the tag table. Walk: none asked.

## Round 89 — 2026-10-25 — the harms stopped: a stuck Stagewatch shows no time, a page's pass never leaves the machine, no key or token in clear, the twin keeps its secrets home, loopback trust wants this desk's name, a proof grants nothing to an address

`round-89` · PLAN §107 · REVIEW round 89 · 2,884 tests · module 3.18.1 · evidence: a handed-in critique (the gap analysis across Stagewatch, Patterns, PlayMap and AGMessage, its designs and their review)

- **A stuck Stagewatch shows no time.** Its stuck beat marks the show stuck until its next state frame: the link stays
  live and amber, every counting value is red with no time — the stage pages, STATE `bus.stagewatch.stuck`, the
  twin's relay to the nodes, the Eye, the Nodes card, the brief and the module's `stagewatch_stuck` — and one log line
  says each change.
- **A page's pass never leaves the machine.** An address that carries a pass (a Stagewatch window's `/w/<token>`, a
  `pin=` or `token=` word, a user) is kept on this machine and the show holds `kept://host:port/<id>` (60 random bits);
  shows already on the disk are scrubbed on load with one journal row; the browser alone resolves it, and a window is
  black until Stagewatch says what it shows. STATE, CONTEXT, the Eye, the log, the journal and the bundle say it in
  public; the Super Check's Private pages row says what opens nothing here. `--verify-runtime` probes WebView2
  (`--require-webview2` on the release's smoke lane).
- **No key or token in clear.** The Stagewatch link takes TLS by the pin and a probe, never by a hello, and reads a
  Stagewatch that offers no TLS without its key; the module (3.18.1) sends plain `AUTH` to a desk on its own machine
  only; a token that still arrives in clear from another machine is marked "rotate it: NEW TOKEN" on the Super Check,
  the Nodes page and STATE `control`, refusing nothing.
- **The twin keeps the show's secrets home.** SEND SECRETS is off by default and refused to a standby on another
  machine, both desks say what a standby is without, and the twin listens on the bind address (and on 127.0.0.1 beside
  a bound one). Found on the way: the AIR line carried a whole show's secrets to every standby while EDIT SAFE was
  open — it carries the mirrored sections only now. A settings file an older build saved keeps its old tick.
- **Loopback trust wants this desk's name.** A request is this machine's only with a Host that names the desk, so a
  rebound name is an unpaired machine; a browser's POST to the wire's port is closed with nothing run.
- **A proof grants nothing to an address** (ADR-017). On a paired desk OSC comes from this machine and OSC ALLOW only;
  a controller that relied on a paired machine's twelve hours is refused, named once in the log and counted on the
  Remote page — add its address to OSC ALLOW.
- Unwalked (`docs/QUALIFICATION.md` §22 steps 12–14, §28). No CI run has built the round yet: the one on its pushed
  commits stopped at the .NET download before any build, so the probe's first run is the next push's (L62). OPEN: L26,
  L57 and L58 closed; L59 to L62 added.

## Round 88 — 2026-10-11 — Show Bus 1.2: a timer node follows its desk's Stagewatch, nodes say what they are, TLS on the web remote with pinned certificates, the shared approval queue

`round-88` · PLAN §106 · REVIEW round 88 · 2,702 tests · module 3.18.0 (unchanged) · evidence: the maintainer's list, the contract `docs/SHOWBUS.md` (1.2)

- **One room, one timer, on every node.** A desk relays the Stagewatch it follows to its timer and caller nodes over
  the twin — the frame and its age, never a key — so a node's stage follows with no link of its own.
- **Nodes say what they are.** `CONTEXT` and `/api/context` on a node give its role, its desk link, the Stagewatch it
  follows and what its stage shows or where its stack stands; the desk's Eye draws each node from it.
- **HTTPS on the web remote, same port.** A certificate made at the first start (its key sealed to the Windows user),
  TLS when the connection opens with a handshake, plain HTTP as before unless the Remote page requires HTTPS; `/tls`
  says how to trust it; the hello names its fingerprint.
- **The Stagewatch link over TLS, pinned.** Trusted on first use, a changed certificate refused (CERTIFICATE CHANGED on
  the Nodes page and the Eye) until ACCEPT.
- **The shared approval queue.** With a Stagewatch approval desk linked (a node key), the assistant's proposals go there
  and land here once approved; another app's approved wire line runs through the wire's own fences.
- Unwalked (`docs/QUALIFICATION.md` §27).
- **88.1, after the push (the build machine's lifecycle job):** a closed desk stayed alive — round 87's watchdog hook
  (`WatchdogBeat.Live`, a static) held a closure over the desk; the desk now lets go of it at shutdown, and the
  lifecycle census is back at nought over 100 boots. The arcade test waits (bounded) for the pause START causes at the
  engine's next step instead of assuming it is instant — the build machine answered one status before the step.

## Round 87 — 2026-10-11 — Show Bus 1.1: the show network heard, the Stagewatch link, receipts, the clock exchange, pairing by proof; the watchdog quicker while live

`round-87` · PLAN §105 · REVIEW round 87 · 2,688 tests · module 3.18.0 · evidence: the maintainer's list (given across the show network's three apps), a handed-in assessment (two read-only surveys of this tree and of the apps' links), the contract `docs/SHOWBUS.md`

- **The show network, heard.** Every other show app's hello on UDP 9928 — Stagewatch, its Slides agents, AGMessage —
  on the Nodes page (THE SHOW NETWORK) and the Eye's CONTROL plane: heard grey, quiet amber, gone red. The hello gains
  `host`, `ports`, `clock` and `inst`, and its `id` is now the install's.
- **One room, one timer.** Link to a Stagewatch (Nodes page; off by default; the key in a header, never a URL): the
  stage and timer pages follow its time and colours and say so; pause and add-time are its; a Stagewatch gone quiet
  shows no time rather than a frozen one. The Eye draws its departments and timer from the context it publishes, and
  the assistant's brief reads them — by the Eye's rule, so all of it works with no key and no internet.
- **The wire, for other apps.** Any line may carry `@id` and its answer names it; `PING <t1>` is a clock exchange;
  `CONTEXT` / `GET /api/context` give this desk in the shared shape (the Eye's picture, the last lines, the verbs with
  their risk, the apps it hears). STATE ends with a `bus` row; the module reads six new variables.
- **Strengthened.** The token never crosses the network (`NONCE` / `AUTH-PROOF`; plain AUTH from this machine only;
  the module 3.18.0 answers the nonce); a new desk is paired from its first run; OSC's verbs on a paired desk come only
  from paired machines or the OSC ALLOW list; the watchdog ends a hung desk in ten seconds while the outputs are live;
  a STATE push that would repeat the last is skipped.
- Unwalked: no real Stagewatch on a real network this round (`docs/QUALIFICATION.md` §26).

## Round 86 — 2026-10-10 — the clip's transport for another app: VIDEO PLAY / PAUSE / SEEK / STOP, the precise clock in STATE, a hello on 9928

`round-86` · PLAN §104 · REVIEW round 86 · 2,637 tests · module 3.17.0 · evidence: the maintainer's list (relayed from Stagewatch's design paper), a read of the code

- **The clip's transport on the wire, OSC, the cue editor, the panel, the phone and the module.** `VIDEO PLAY`,
  `VIDEO PAUSE`, `VIDEO STOP` (back to its top, held) and `VIDEO SEEK <83 | 1:23 | END 5>`. `VIDEO STOP` is the
  clip's — bare `STOP` is still OUTPUTS OFF. A seek from the end of a clip whose length is not known is refused, a
  playlist item's clock moves with a seek, and a playlist item or a stinger is not paused (its own clock would run on).
- **A clock another app can count down by itself.** STATE's `video` row ends with `positionMs`, `lengthMs` and
  `remainingMs` (null where there is no honest number), `paused`, `rate`, `seekable` and `seq` — the clip's count of
  discontinuities; `videos[]` lists every mounted clip. OSC adds `/video/paused` and `/video/seq`.
- **The show network's hello.** UDP 9928, every two seconds while the wire is on: who, where the wire is, what it can
  do — never the show. The 9700 beacon is unchanged.
- Unwalked: no real decoder was paused this round.

## Round 85 — 2026-09-25 — the handed-in plan's Phase 0: nine reported defects read against the head, seven fixed, two designed

`round-85` · PLAN §103 · REVIEW round 85 · 2,592 tests · module unchanged (3.16.0) · evidence: a handed-in plan document ("Patterns: Show Brain Development Plan", 24 September 2026, against `f4f9d3c`), web research at the maintainer's ask (`flybrain.online` and `eonsystemspbc/fly-brain` — `docs/CONNECTOMICS.md` §6), and the maintainer's "go ahead"

- **The replay reads its counts as written, and reads its files on a worker (P1-07, P1-08).** A render
  fault or a missed slot in a sample's own minute paints the machine red — four faults after four faults
  no longer read as none — and the pool-starved count alone keeps its rise. `EYE REPLAY` answers
  `OK Reading the record on a worker — the picture relights when it is read` and the strip says so until the
  read lands; the read is bounded and stopped by NOW or a second open; the record is the files as they
  stood at the press, so ON opens on what happened before it; `EYE AT` waits for a read in flight.
- **The recovery record's ladder holds (P1-03).** A record that parses to nothing reads the backup, and a
  torn record is never rotated over the whole one before it: the backup is kept only when it is a record.
- **Saving stays off through the handover (P1-04).** A second desk that comes to own a folder whose show
  file a newer build wrote becomes the primary — the record, the music — with autosave off and the words
  saying so; the newer file is never written over.
- **A bind that is not an address opens nothing (P1-06, wider than asked).** The control ports, the OSC port
  and the audience socket refuse to open on a bind that is not an IP address — never every interface — and
  say why on the status line, the Remote line, the Super Check (three red rows with the FIX) and the Eye's
  OSC node; the audience socket fails closed on its own bind with the control ports untouched.
- **The pages carry the token on every read, and the web has one policy (P1-01, P1-02).** The remote,
  admin and multiview pages send the token on their state and picture fetches and ask for it once on a
  403; on a paired desk every `/api/cmd` line and `/api/cues` want it from another machine, as `/api/state`
  has since round 83; the wire's queries stay open, and `docs/REMOTE.md` says why in one paragraph.
- **Two designed, not built.** L45: the stream's rate held through a display change, a restart deliberate
  (P1-05). L46: one set of output facts — requested, active, observed as separate fields with a source and
  an age each, read by every reader of "what is live" (P1-09).
- **The second look at the connectomics transfer.** `docs/CONNECTOMICS.md` §6: what `flybrain.online` and
  Eon's `fly-brain` are, the five transfers named for a show controller (labels on facts, the narrator's
  number fence, rails for autonomy, a ledger before an outcome, parity across origins), and what does not.

## Round 84 — 2026-09-24 — the connectomics transfer: the Eye replayed from the record, and synthetic shows as a fence

`round-84` · PLAN §102 · REVIEW round 84 · 2,575 tests · module unchanged (3.16.0) · evidence: web research at the maintainer's ask — the Google Research post on the complete fruit-fly brain map, read as method (`docs/CONNECTOMICS.md`) — and the maintainer's "sounds good, go ahead" on the assessment

- **The Eye replayed.** REPLAY on the Eye page relights the structure of now from what the desk recorded —
  the journal's rows and the metrics file's samples — at an instant: a screen shows the outcome of the rows
  that named it in the thirty seconds before (green Done, amber Refused, red Failed), a device its receipts,
  the desk the machine's sample and the cues and looks nothing else claims, and a thing the record does not
  mention is grey and says so; the links are grey, because the record holds things, not links. A scrub bar
  and two 30 s steps move the instant; the strip says the instant, the rows in its window and how the
  machine was doing; the card lists the rows newest first; NOW puts the picture of now back. The rail,
  STATE's counts and `EYE` stay on now. On the wire `EYE REPLAY [ON|OFF|<time>]` (a desk-only verb that
  answers with the moment's words) and `EYE AT <time>` (the record at an instant as JSON, the page
  unmoved); STATE's eye row carries `replay` and `replayAt`; the Eye menu, the help and the assistant's
  brief carry it. The Companion module is unchanged: a scrub is not a key.
- **Synthetic shows as a fence.** A fixed-seed generator draws rigs the hand-written tests never did — two
  to seven screens, canvases, repeaters, locks, screens switched off, own pictures, ticks, un-armed tiles, a
  focus — and runs five hundred and twenty of them through the take resolver under every scope, the show
  file's round trip, the frozen clone and the shown-picture rule, with the doctrine's invariants asserted
  on each; eight more take once on the live desk with the desk's own plan as the oracle for the air. It
  found no defect; its first catch was its author's write on the frozen clone, which H3's fence refused.
- **The research note.** `docs/CONNECTOMICS.md`: the post as method, the four transfers, the two taken, the
  two left (the problems queue's memory — L44; the Eye in a browser), what does not transfer.

## Round 83 — 2026-09-24 — the ledger's rows that are code: a fault behind the wire is answered, a page from another origin cannot run the show, a sidecar reaches the disk, whole words on the word list, the feed capped and moderated, the NDI receiver's copy guarded

`round-83` · PLAN §101 · REVIEW round 83 · 2,540 tests · module unchanged (3.16.0) · evidence: the ledger's six code-sized rows from the handed-in review (round 82), each re-read against the head; the maintainer's "continue to round 83"

- **A fault behind the wire is answered, never a silent disconnect (L31).** The TCP loop parses and routes each
  line under its own catch and answers `ERR the desk faulted on this line (<type>) — fault #n, logged`; the HTTP
  handler answers `500` with the same `ERR`; a socket's end is still routine and anything else leaves its trace,
  the first with its stack and then one a minute with the count, which the Super Check's REMOTE row carries.
- **A page from another origin cannot run the show (L32).** The HTTP head reads `Origin`, `Host` and
  `Sec-Fetch-Site`; a POST from a browser page of another origin is refused without `X-Patterns-Client`, paired,
  loopback or not, while a curl or a device sends neither header and is not touched. With a token set, `/api/state`,
  `/pgm.jpg` and `/mv.jpg` want the token too — the header, or the pages' own cookie for their img tags; never a
  query string.
- **A sidecar reaches the disk before its move, and the recovery record keeps the one before it (L34).** `AtomicFile`
  flushes the temp file to the disk before the move (the per-second playhead opts out); the recovery record is
  written keeping `.bak`, a torn record is read from it, and the Super Check says so; no readable backup either
  is said, not read as "no crash".
- **The word list matches whole words (L37).** "Scunthorpe" passes; an entry's own wildcards — `fuck*`, `*fuck`,
  `*fuck*` — say when a word that starts, ends or holds it is meant, and an entry with a space is a phrase; the
  defaults are written in that grammar.
- **The feed is capped, its failure is one line, and the ticker meets the word list (L39).** 2 MB on the client and
  the file; the status line and the log get the exception's type and first line, never the document; items with
  a listed word are held back and counted on the status.
- **The NDI receiver refuses a frame described wrongly (L41).** A stride below a row or a colour format it never
  asked for is refused before the copy, counted and named on the source's status.
- **Found on the way.** The support-bundle fence read the pages' new cookie `patterns.token` as a sidecar's
  name; the cookie is `patterns-token` and the fence stands (83.7). The ledger: six rows closed, two added (L42 the network defaults and a first-run
  token, the design half of L32; L43 a feed over `http://`). No verb an output can show landed. Unwalked: 83.3
  touches the record a restart reads; QUALIFICATION §24.

## Round 82 — 2026-09-24 — a handed-in review answered: time on the wire is finite, every number the show writes is finite, every documented OSC address routes, the OSC port survives a fault, the wall's names meet the word list, the parsers under a fixed-seed fuzz

`round-82` · PLAN §100 · REVIEW round 82 · 2,525 tests · module unchanged (3.16.0) · evidence: a handed-in review of the code, the architecture and the resiliency with a fuzz harness of its own, its patch set written against round 80 and merged here, and the maintainer's "continue to round 82"

- **A span of time on the wire is finite and at most a week.** `PLAN SHIFT +99999999999999999999` threw out of the
  parser and closed the connection unlogged; `PLAN SHIFT +1193047:00:00` wrapped to +31:44; `TIMER ADD +Infinity`
  was accepted. `StageTimer.MaxWireSeconds` is the one ceiling, and `ParseSeconds` and `ParseDelta` refuse NaN,
  the infinities and anything past it.
- **Every number the show writes is finite.** An infinity on a paused stage timer made every save, recovery
  record, twin mirror and publish of the Stage section throw; 108 of the model's 119 serialised numbers kept NaN.
  `Observable.Set` refuses a number that is not finite, with a reflection fence over the whole model.
- **Every documented OSC address routes.** `/patterns/plan/shift`, `/resume`, `/catchup` and
  `/patterns/countdown/follow` were in the reference and refused by the map; a fence now holds every row of the
  reference to a line the wire parses.
- **The OSC port survives a fault.** A message whose handling throws is answered `/patterns/error`, counted and
  logged once per hundred; the port carries on where it used to close for the rest of the show.
- **The wall's names meet the word list.** A phone's nickname and group pass the room's word list before the
  leaderboard or a draughts seat draws them; `NamesRefused` counts the refusals.
- **The parsers under a fixed-seed fuzz.** The wire's line, every module line with a hostile number, an HTTP head,
  the twin's lines, a management reply, an OSC datagram, an mDNS packet, a beacon, an EDID and 20,000 hostile OSC
  messages — no throw, from a seed that names the same input everywhere.
- **Made to fit.** The patch set's seven code units are 82.1–82.7 with their author's reasoning in each commit
  and the subjects renumbered; its own papers were written under the switcher wall's numbers and are replaced by
  this round's. The OSC reference's canvas row now says CANVAS, as the map and the wire have since round 81 (82.8).
- **Found on the way.** A residency test that held only on 8 GB or more steps the grace it reads (L38 for the
  twenty-six others). Eleven rows to the ledger, L31–L41: the handlers' swallowed faults, the network defaults, the
  management channel, the sidecars' write-through, the log, the twin's transport, the word list's substring match,
  the host-dependent tests, the feed, the stored credentials, the NDI receiver's stride; none closed. No verb an
  output can show landed. Unwalked: no platform path touched.

## Round 81 — 2026-09-23 — the switcher wall's workflow: groups are kinds, a scoped take lands alone, an OWN tile's preview is its own, focus and selection are one

`round-81` · PLAN §99 · REVIEW round 81 · 2,486 tests · module 3.16.0 · evidence: the maintainer's list from the rig (a verbal report; no files)

- **Groups are kinds.** GROUPS takes to every screen of the ticked tiles' groups — a group is what a
  screen is for: Main, Confidence, Info — and MAIN / CONFIDENCE / INFO SCREENS take to a group by name with
  no tick; a joined canvas is a canvas (`CANVAS A` on the wire, `GROUP A` still read), never a group. The
  wall's and the fade's pickers have seven choices; the cue editor, the OSC map, the cue validator and the
  Companion module's canvas key follow.
- **A scoped take lands alone.** FOCUSED, TICKED, TICKED GROUPS, a group by kind, SCREEN n and CANVAS A
  land on those screens alone, as their own pictures (OWN lights up); the programme, its preview and every
  other screen are untouched, nothing is pinned, EDIT SAFE stays open. Only ALL ARMED (and FOCUSED on the
  PGM tile) moves the programme. LOCK means lock on every path — the tile's own keys, the wire, every
  scope — with the reason named; unlocking never moves a picture.
- **An OWN tile's preview is its own.** A tile on its own picture keeps its PVW to itself: the programme's
  preview never reaches it until SEND copies it there, or the tile is selected and edited (its first edit
  makes a following tile its own); its CUT / TAKE put up what its PVW shows, and a press with nothing new
  says "already shows its own picture — SEND the programme's preview to its tile". The round-78 "settled"
  rule and its plumbing are gone. PROGRAM puts a tile back on the programme.
- **Focus and selection are one.** The EDITING TARGET picker, the tile's title and the wall's highlight are
  one selection, and FOCUSED means it; STATE's take row ends with `focused`, `focusedLabel` and `groups`
  (the ticked tiles' groups); Companion module 3.16.0 reads them (`take_focused`, `take_groups`); the
  desk's tooltips, the help, the assistant's and the cue sheet's place words say round 81's model.
- **The round's verb, read beside an output.** A FOCUSED TAKE with the outputs open leaves the journal's
  `Take` row — Done · Changed · OutputsLive, the words naming the tile alone — read on the headless desk;
  unwalked on the rig.

## Round 80 — 2026-09-21 — the ledger's rows that need no rig: the master rate follows the displays, the plan's OWN words, the module reads the descriptor version, one atomic write

`round-80` · PLAN §98 · REVIEW round 80 · 2,481 tests · module 3.15.0 · evidence: the maintainer's "continue" and the ledger's rows that need no rig

- **The master rate follows the displays.** The show never asks a display for more frames than it
  refreshes: with FOLLOW THE DISPLAYS on (the default, so a show saved before this round follows), a
  display slower than the master rate behind a screen that is here leads — a 50 Hz TV under a 60 fps
  show runs the show at 50 — and every reader reads the rate in force: the outputs' pacing, the NDI
  "master" rate, the stream, a screen's signal report, the tech info chip, the facts' target. The
  operator is told: the Super Check's *Master rate* row (green following, amber over-asked with the follow
  off, grey unlimited), the Screens page's switch and words, a log line once per change, STATE's
  `masterFps` / `masterFollows` / `masterFpsEffective` / `masterRate`, the Eye's desk words, the assistant's
  Desk line, the module's `master_fps` and `master_rate`. `OutputRate.Master`, `OutputConfig.FollowDisplays`.
- **The plan's OWN words.** The take plan says before the press which armed tile keeps its own picture —
  *2 · Lobby keeps its own picture (OWN)* — instead of listing it as taken; `TakeTarget.KeepsOwn`.
- **The module reads the descriptor version.** Companion module 3.15.0 reads `protocol` on every NAV and
  MENU reply, warns on the connection when the desk speaks a newer descriptor than it knows, and carries
  `desk_protocol`; a Core contract test holds the module's number to the desk's.
- **One atomic write.** `AtomicFile` in Core for every sidecar's temp-then-move (nine writers; the
  ownership record's committed write and the management service's streamed file named as the exemptions),
  with a source fence.
- **Found on the way.** An on-demand poll drops the dashboard's five-tick facts memo, so the dashboard test
  reads the numbers it injects and not the agent's cold first frames. Unwalked: no rig this round.

## Round 79 — 2026-09-21 — the retrospective answered: truth as fields, the last fail-open edges, the recorder's oracle, the monitors paced, an hour of hygiene, the latent nits, the process

`round-79` · PLAN §97 · REVIEW round 79 · 2,461 tests · module 3.14.0 · evidence: a handed-in retrospective checked claim by claim, and the maintainer's "run 1 to 7"

- **Truth as fields.** Every action result carries what the room could see (`Visibility`: outputs live,
  off, blackout) and, for the take family, what changed (`Effect`: changed, own only, nothing, not
  measured), stamped once at the executor from a before-and-after comparison of the air; the journal row,
  STATE's `take.last` and the Eye's desk words carry them; a wall TAKE a hand presses over an air that
  already is the preview is refused with the way out, the show's own automation never is; the take tests
  open an output and press twice.
- **The last fail-open edges.** The four overlay switch sites and the OSC map refuse an unknown word
  instead of toggling; OSC binds the desk's bind address and is amber in the Super Check and the Eye while
  it is open on every interface without pairing; the token stays local by design and the papers say so.
- **The recorder's oracle.** The nine wire verbs the writer could not say are written; the module's own
  `lines.txt` round-trips parse → write → parse; an enum fence names the eight kinds the wire cannot say;
  a dropped line is logged once per kind.
- **The monitors paced.** The wall's miniatures, the PROGRAM pane and the RUN monitor present at the desk's
  own monitor rate (25 by default, the Screens page's Desk monitors picker, STATE `monitorFps`); LIMITED
  BY RENDER CLOCK names its cause — the clock following a slower display, or the desk not keeping up.
- **An hour of hygiene.** The build says which build it is (`0.<round>.<run>+round-NN.<sha>` on every
  surface; the manifest's round from the changelog's header); the Release gated on the Windows smoke lane;
  `createdump.exe` beside the exe from one post-publish target; the stale module package untracked; a show
  file from a newer build runs as read with autosave off and the reason on the desk; the recovery record's
  air migrated before it goes back on air; the support bundle carries the playhead and the quality profile,
  with a fence over every sidecar the core names.
- **The latent nits.** The programme and the sandbox published as one pair a frame reads once; the
  sandbox's picture identities memoised on the trackers' change counters; `SectionsPublished` after the
  assignment; the wire peer's dead catch and its flush under a close; the stale sentences made true, with
  a fence that every "RAIL → Page" names a real page.
- **The process.** `CLAUDE.md` (the agent brief), `docs/field/` (the show laptop's four files per report),
  `docs/OPEN.md` (the cumulative still-open ledger: seventeen rows closed by this round, eleven open),
  QUALIFICATION §22 (the scripted walk). Unwalked: no rig this round.

## Round 78 — 2026-09-18 — the wall's CUT/TAKE truth, the show folder's lease asked again, NDI and the stream as outputs of what they carry; the assessment answered

`round-78` · PLAN §96 · REVIEW round 78 · 2,429 tests · module 3.14.0

- **The wall's CUT/TAKE truth.** A tile's PVW holds one picture — staged or edited and not yet seen, its
  own while the editors are on it, else the programme's preview — and a CUT / TAKE on the tile lands
  exactly that: a second take lands the new preview instead of copying the tile's own picture over itself;
  a take that would change nothing is refused with the way out and spends no one-shot; every take says when
  the outputs are off or the blackout is up; the wall's take says how many screens already showed their
  picture. STATE's take row (`outputsLive`, `pending`) and screen rows (`staged`), the Eye, a PVW badge on
  the tile. `SandboxService.PvwPicture`, `ShowSnapshot.SettledOwn`.
- **The show folder's lease asked again.** A desk that started as the second on its folder — a handover's
  replacement, a second window — owns the folder the moment the first has gone: saving on and the show
  written at once, the break music its to run, STATE `continuity.primary`, the Eye's desk words; a second
  desk's exit never clears the first desk's recovery record. `IInstanceLease`, `AppServices.PollPrimary`.
- **NDI and the stream as outputs of what they carry.** Each running sender and the stream is a live output
  of its source — the programme, a screen through its mirror chain, a canvas, its own screen — never a
  blanket "the programme leaves the machine"; a sender on a screen's own picture keeps that picture heard
  and the programme's clip silent.
- **The beacon's log.** A held port is asked again every five seconds and logged once, then a line a minute.
- **The assessment answered.** PLAN §96.6: what was correct (P1-1, P2-1, P2-2, the persistence questions),
  good and done another way (the two-process regression as a lease seam), wrong or short (the wall fault
  missing, the audio policy already stated, the ask bound to the handover), deferred with the reason (the
  phase model, mute-first, the scheduler).

## Round 77 — 2026-09-17 — the field's second report: the room hears what it sees, the lock spares the desk's own browser, a refused screencast asked again, the handover's side-effects, the mirror made visible

`round-77` · PLAN §95 · REVIEW round 77 · 2,419 tests · module 3.14.0

- **The room hears what it sees.** A picture's sound plays while some live output shows that picture and
  fades over the transition when none does — a programme clip falls silent when the only live screen shows
  its own picture, a screen's own picture is heard on its screen, the desk still hears the programme with no
  output open, a monitor device keeps the operator's ear; the same for a web page (its elements ramp down and
  come back at their level) and for the matrix's taps. `LiveOutputs`, `AudioMonitorRule.Where(…, live)`.
- **The lock spares the desk's own browser.** WebView2's processes descend from the desk and are never muted;
  the sessions the lock did mute are in the receipt with their originals, so a crash or a handover puts them
  back.
- **A refused screencast is asked again.** The ask climbs a ladder (size, quality, format), a browser that
  refuses every rung is asked again on a backoff from the capture tick, the status line says where the capture
  stands, and the browser composes its video itself so no capture path sees it black.
- **The handover's side-effects.** No false SCREEN UNPLUGGED at any boot; the lock handed over rather than
  toggled twice; the wire, the twin port and the beacon bound again once the old desk has left.
- **The mirror made visible.** "The picture is mirrored, turned 90°" on a recall and a tile take, in STATE
  and the Eye, a warning on the Media page, and RESET ADJUSTMENTS.

## Round 76 — 2026-09-17 — real-world tests and faults: a restart the room never sees, a hot-plug that blacks nothing, a picture's sound on its own screen, YouTube's frames and its sound, the notes closed

`round-76` · PLAN §94 · REVIEW round 76 · 2,407 tests · module 3.14.0

- **A restart the room never sees.** A restart asked for with the outputs live is a handover: the desk asks the
  watchdog for a replacement through its heartbeat and keeps its windows and its sound up; the replacement
  boots with the record, opens its own windows over the old ones with the show put back, then asks for the
  screens; the old desk leaves with exit code 84 and the watchdog starts nothing. A start that finds a run
  still playing (a hung desk) opens its own picture first and takes the screens after. A deliberate restart
  puts the show back whatever AutoRestore says. Every clip's position and the music's track and bar are written
  each second (`patterns.playhead.json`) and resumed where they would be by now. STATE's `continuity` row.
- **A hot-plug blacks nothing.** The output windows of every display the unplugged one had nothing to do with
  are carried over to their re-identified ids rather than closed and reopened; and the rig editor's "primary
  off when other screens exist" default no longer turns a live output off (or a desk monitor on) when Windows
  promotes another display to primary.
- **A picture's sound on its own screen alone.** A clip is heard on the outputs of the screens that show it and
  on a monitor row's output, never on every routed output; a picture leaving the programme fades its sound over
  the transition and closes it.
- **A page's sound answered, YouTube's frames.** The page's audio route is asked for up to ten seconds until it
  answers, never read once; YouTube pages are steered to H.264 (`WebConfig.PreferH264`), which every card
  decodes in hardware.
- **The notes closed.** One safe representation of an action (`Restart [redacted]`), legacy secret rows scrubbed
  on load, the support bundle masking known secret values; one landing validator for the wall, the tile and the
  canvas with a structural shape key apart from the words.

## Round 75 — 2026-09-17 — the small round: the recorder behind pairing and no secret on any feed, a landing that never takes what the press did not see, a lock that never pins the transition, the descriptor versioned

`round-75` · PLAN §93 · REVIEW round 75 · 2,376 tests · module 3.14.0

- **The roadmap assessed.** A cross-industry generalisation roadmap read against the code: its P0/P1 items
  real, its phases mostly already true of the code, its renames and project split declined with reasons,
  render nodes and a tablet Navigator postponed. `docs/GENERALISATION.md`.
- **The recorder behind pairing.** `RECORD ON` and `NAV DECK` wait for the pairing token like a verb; the
  wire writer, the action feed and the journal never carry an admin passcode (UPDATE APPLY, RESTART); MIDI
  learn refuses to bind a line that carries one.
- **A landing never takes what the press did not see.** A screen made a repeater, a canvas whose members
  moved or a screen that joined a canvas between the press and the landing is held, with what it is now:
  "changed since the press — now a repeater of 1 · Left".
- **A lock never pins the transition.** LOCK during a running clip pins the picture beneath the clip in the
  edited state, and the sting's end puts it under the lock on the air; the screen ends locked on the show,
  never on a frozen frame of the clip. Closes 72.3's open item.
- **The descriptor versioned.** `protocol` on every NAV and MENU reply; REMOTE.md names what a client lays out.

## Round 74 — 2026-09-17 — the deck as a programming surface: the desk's menus on the keys, the desk turned from the deck and the deck from the desk, the recorder

`round-74` · PLAN §92 · REVIEW round 74 · 2,371 tests · module 3.14.0

- **The question answered.** Does Patterns use Companion's dynamic key generation? Yes — keys that
  label themselves, presets built from the show, layered keys, and now the Navigator: slot keys
  whose words, colours, ticks and presses are generated live from the desk's own menus. The
  research and the design: `docs/COMPANION-NAVIGATOR.md`.
- **NAV on the wire.** `NAV` answers the desk's own table of rails and pages as JSON;
  `NAV <page> [item]` turns the desk's page and selects the thing (a cue, a screen or an element
  opens its settings column); `NAV BACK`, `NAV HOME`, `NAV SETTINGS ON|OFF|TOGGLE`;
  `NAV DECK <words>` — the deck says where it is, on the Remote page, in STATE's `nav` row and in
  the Eye.
- **A page's own menu and the build verbs.** `MENU PAGE <name>` — the page's things with their
  lines, ticks and menu words, and its build verbs with `*` for the deck's text; `LOOK SAVE`,
  `LOOK UPDATE`, `LOOK DELETE`, `CUE ADD`, `CUE DELETE`, `PRESET SAVE`, `LT NEW … FROM <preset>`
  through the action layer, answered with what happened; Delete on the cue and look menus.
- **The recorder.** `RECORD ON` — the desk's actions reach the deck as `ACTION <line>` in the
  operator's words (`ACTION LOOK Walk-in`), written by a wire writer that round-trips the whole
  vocabulary; automation and the deck's own presses are never fed.
- **The module 3.14.0.** The Navigator on 24 slot keys (rails → pages → a page's menu → a thing's
  menu → drawers; HOME, BACK, PREV / NEXT, RUN / MENU, FOLLOW both ways, TEXT), every line matched
  to its reply, the rails and pages as presets from the desk's table, Companion's action recorder
  and a key's LEARN fed by the desk, Stream Deck + knobs for the levels, `nav_*` and `desk_*`
  variables and feedbacks.

## Round 73 — 2026-09-17 — the operator's list: the badge above the band, a tile one press from its editor, the chip's intent, lower thirds that leave by themselves, MIDI learn from any button, the column that drags

`round-73` · PLAN §91 · REVIEW round 73 · 2,371 tests · module 3.13.0

- **The badge** sits centred just above the lower thirds, a size up — its bottom at 72 % of the
  height, clear of the band a design comes into; a settings migration moves an untouched badge once.
- **A Library tile** is the editing target's preview at once and one press from its editor: the
  strip with OPEN <editor>, CUT and TAKE; the press a journaled verb — `LIBRARY <name>` on the wire,
  OSC and the cue sheet; STATE's `editing` row, the Eye; Companion 3.12.0.
- **The tech info chip** says what Patterns is trying to push down the link — the raster, its shape,
  the rate asked, the show's master rate — beside what the display answers.
- **Timed lower thirds.** A design comes on, holds (5 s by default, editable on its row) and leaves
  by itself; `LT n FOR 8`, `STAY`, `LT n HOLD 8`; the countdown on the desk, the Run strip, STATE,
  the Eye and the deck.
- **MIDI learn from any button.** Right-click a look, a cue, a tile, a lower third, a layer, an
  overlay or the RUN surface's GO, choose MIDI LEARN ▸ and the line, press the control on any open
  surface, and it is bound — saved with the show; the field's practice in `docs/MIDI-LEARN.md`;
  `MIDI`, `MIDI LEARN <line>`, `MIDI LEARN OFF`, `MIDI FORGET <line>`; STATE, the Eye, Companion 3.13.0.
- **The settings column drags** to a width the show remembers (300–720 px, 420 by default), and the
  Machine and Audio pages keep their settings groups in it, so the pages stay their health lines
  and lists.

## Round 72 — 2026-09-16 — the critique answered: attempts are not facts, in the audio, the take, the session, the wire and the signal

`round-72` · PLAN §90 · REVIEW round 72 · 2,332 tests · module 3.11.0

- **The critique assessed.** The maintainer's critique and priority roadmap read claim by claim against the
  code — every claim real, at a named line — with the peers' practice and the decisions: `docs/ROADMAP-ASSESSMENT.md`.
- **Audio truth.** The routes the picture makes read the picture the audience has (`AirState`), never the
  preview's edit: `AudioRouting` takes the configuration and the picture apart, every reader passes both. A
  screen given its own picture in the preview moves no sound until it is taken. Found on the way: the audio
  graph's 50 ms tick forced a plan rebuild — twenty a second while the matrix was on; it polls now.
- **A press is a transaction.** A TAKE under a video sting freezes a ticket at the press — the targets, the
  scope's words, where, under which sting, when — and lands that, less only a screen locked since or gone
  from the rig, never more; a landing with nothing left says why. The one-shot is spent by a press that
  fired, kept by one that failed, cleared when its sting is gone from the library — with words each way.
  STATE's `take.landing`, the Eye's "Landing → …", the deck's `take_landing`. ADR-015.
- **A replaced show starts as a show opens.** The one-shot, a sting's waiting ticket, the ticks, the focus,
  the arming and the edit watchers are the last show's and go; with EDIT SAFE open the loaded show is
  frozen as the programme too, so the audience has it and a discard keeps it. A canvas's role changes as one
  edit of the state and one of the programme.
- **The wire fails closed.** BLACKOUT, SCREEN n, LOCK n, DUCK, REVIEW and FREEZE toggle on the bare verb or
  TOGGLE alone; any other word is refused as unknown — a misspelt ON never flips a latch the other way.
- **Signal truth's PARTIAL.** Everything the path states agrees and a property the contract names was stated
  by nobody: PARTIAL, naming the property, never a pass; the far end's word settles the properties Windows
  never states (dynamic range, colour space, transport, audio); the test route's profile drops its audio
  word. The commissioning flow, the Eye, the journal, STATE, the Screens page and Companion 3.11.0 carry it.
- **Three engineering items.** The frame smoother allocates nothing on a measured arrival; the display query
  loops when the topology moves under it; the display evidence (observation, EDIDs, inventory) is forgotten
  as one when the topology changes; the EDID parser fuzzed — never throws.
- **The papers.** QUALIFICATION §10–§12 (the take under a sting with the wall changing, the sound following
  the room's picture, a topology change under the evidence), PLAN §90, REVIEW round 72, ADR-015, the tag
  table to round 71.

## Round 71 — 2026-09-16 — the desk tick and the audio devices: a catalogue Windows keeps current, and no COM on the desk's thread

`round-71` · PLAN §89 · REVIEW round 71 · 2,315 tests · module 3.10.0

- **The stutter named.** The desk tick asked Windows for its audio input devices every second while a
  Fractal or Reactive was on the desk — a WASAPI enumeration with each endpoint's name read from its
  property store, 200–400 ms on the UI thread, the "(audio)" of the super-check's red tick row — and
  for its outputs from every right-click menu, the health facts every fifth tick, the pickers, the
  routing rows, the screens' choices, the cue editor and the verbs.
- **The catalogue.** `AudioEndpointCatalogue`: one read on a worker at start, Windows' own
  notifications thereafter, a debounced read on a worker, an immutable snapshot with a version
  published only when the endpoints changed. The tick compares one number; every menu, picker, table,
  fact and decoder id reads the snapshot; a press opens a known device by its id. `IRunHost.OutputDeviceNames`;
  the super-check's Output devices row carries the catalogue's words. ADR-014.

## Round 70 — 2026-09-16 — the analyzers as fences: Sonar's rules and Roslyn's in every build, each stop with its reason, and one culture on every desk

`round-70` · PLAN §88 · REVIEW round 70 · 2,309 tests · module 3.10.0

- **Analyzers as fences.** The SDK's analyzers, Sonar's rules (`SonarAnalyzer.CSharp` — the rules a
  SonarQube server runs, inside the compiler) and the threading analyzers run on every build, with a
  generated `.editorconfig` that names each rule that stops the build and why, by family — culture,
  disposal, threading, security, correctness, performance — and turns the style rules off by name with
  the reason; a banned-API list (`Environment.TickCount`, `Thread.Sleep`, `GC.Collect`, `Console.Write`,
  `Task.Wait`) with a reason per symbol. StyleCop measured (58,459 findings, none a defect) and left out.
  `docs/ANALYSIS.md`, ADR-013.
- **One culture.** 145 places formatted a number or compared a verb by the machine's locale; every one
  names its provider now, and `CultureGuard` puts the process on the invariant culture from Main's first
  line — a Hamburg desk writes `0.5` on the wire, not `0,5`.
- **The fences' catch.** Thirty-nine undisposed fields disposed (the render pipeline's paints among
  them — a leak per sink, per re-attach); every continuation scheduled; the async-void appliers made
  tasks; fifty-five P/Invokes given a DLL search path so a library planted beside the portable exe is
  never loaded; twenty-six fire-and-forget tasks carry their service's token; the keep-awake and the
  registry writes observe their results; twenty-nine regular expressions over text the desk did not
  write carry a timeout; DateTimes carry their Kind; the NDI sender, the arcade loop, the stream
  renderer and the audio lane wait on their stop instead of sleeping through it; the desk waits for a
  quitting host's exit instead of polling it; dead members and empty catches are gone or explained.

## Round 69 — 2026-09-16 — memory with a reason, the GPU cache governed, the composition root answered, audio that follows the picture

`round-69` · PLAN §87 · REVIEW round 69 · 2,308 tests · module 3.10.0

- **The residency ledger.** Every held thing — a clip, a web page, a deck, an NDI receiver, a
  picture — carries the reason it stays (on air, named by the show, preview, armed, pre-rolled,
  retiring) or an idle clock with a grace by machine class (20 / 60 / 180 s, shortened as memory
  presses); idle pictures go on their clock rather than waiting for pressure, never one drawn in the
  last second and a half, never one the show names. An armed web page — the marker on the video —
  is kept past its want and let go only at critical pressure. STATE's `memory.residency`, the Media
  page's line, the Eye's *held: …* words and its desk node's memory line, the assistant's inputs,
  Companion's `machine_memory_held`.
- **The GPU cache governed.** Skia's resource cache is bounded from the card (an eighth of its
  dedicated memory, no more than the class's 64 / 128 / 256 MB), shrunk as either memory presses —
  the media ladder's rung or the card's own — purged at high and critical, applied through the
  sinks' Skia lease once a second and read back rather than assumed; the collector's facts (gen 2,
  the large-object heap, the last pause) are in the sample, STATE's `machine.gpuCache` and
  `machine.gc`, the Machine page, the Eye's desk node and Companion's `machine_gpu_cache`, and the
  large-object heap is compacted once each time the outputs go off air. Swapping textures between the card and the computer is declined with the
  reason written down (`docs/MEMORY-RESEARCH.md` §12).
- **The composition root, fenced.** ADR-012: no dependency-injection container — the services are
  built once in the kernel's constructor order; MVVM as it stands, with `ArchitectureFenceTests`
  naming the seams by file and count (the ambient service's reaches, the code-behind handlers, the
  engines the kernel alone builds) and forbidding heavy work in code-behind.
- **Audio follows the picture.** Each screen names its sound output; the matrix derives the routes
  from what each screen shows — a repeater takes its source's sound, a canvas member its canvas's,
  a screen on its own picture its own, the rest the programme — and the operator's own rows always
  win. `SCREEN n AUDIO <output>`, `AUDIO FOLLOW`, the `AudioFollow` cue, OSC, Companion 3.10.0's
  `screen_audio` and `audio_follow`, the Screens and Audio pages, the Eye's *(follows the picture)*
  edges, STATE's `followed` routes; `docs/AUDIO-RESEARCH.md` §7.

## Round 68 — 2026-09-16 — web video smooth and cheap: a buffer on a locked clock, pooled decode, capture by policy, the browser out of the chain

`round-68` · PLAN §86 · REVIEW round 68 · 2,283 tests · module 3.9.0

- **The smoothing buffer.** A web page's frames are queued in a jitter buffer that gives each one
  a regular time from a phase-locked clock and shows it a few frames late, so a YouTube clip that
  arrived late, early and in bursts comes out even; the depth is sized by the machine and the
  measured lateness within bounds; every output shows the same frame in the same slot. Frames —
  Auto (a video smoothed, a live page at once), Smooth, Low latency — on the Media page and a web
  layer, carried by the look. A page leaving Program fades its sound over the transition, then its
  buffer is cut and its capture stops.
- **The cost cut.** Each frame decodes straight into a pooled buffer (no allocation per frame), a
  repeated picture is skipped before any decode, and the browser is asked for no more pixels than
  the rig shows — 720p on a small machine once the quality ladder has stepped, every second frame of
  a 60 fps page at Economy — at a JPEG quality by machine; the browser is asked for Chromium's D3D11
  video decoder when the desk decodes on the GPU.
- **The browser out of the chain.** Play via → Native player hands a YouTube or Vimeo page's stream
  (found by yt-dlp, the operator's tool, never bundled) to libVLC under the page's own key — GPU
  decode, the routing matrix's sound, no browser; the browser stands in until the stream is found
  and whenever it cannot be, and the words say plainly whose call the site's terms are.
- **Everything follows.** STATE's `web.path`, `via` and `native`; Companion 3.9.0's `web_path`,
  `web_smoothing`, `web_latency`, `web_underruns`, `web_capture`, `web_smoothed`, `web_stalled`;
  the PAGE CONTROLS line and the Eye read the buffer's words; the help and `docs/WEB-VIDEO.md`
  (the research: how OBS, vMix, CEF and WebView2 do it; GPU capture and the process-loopback audio
  tap recorded as the next bench).

## Round 67 — 2026-09-16 — the switcher's rules, the tile's isolation, the next take and the group

`round-67` · PLAN §85 · REVIEW round 67 · 2,242 tests · module 3.8.0

- **The take rules enforced.** One plan behind CUT and TAKE: LOCKED is never taken — not by ALL
  ARMED, not by the tile's own TAKE; ARM counts inside every scope; FOCUSED is the edited tile alone
  (the PGM tile means every armed screen); TICKED and TICKED GROUPS the ticked alone; a repeater
  never; everything outside the scope keeps its picture. A take that would move nothing is a
  refusal that says why. The wall shows the plan under the picker before the press; the multiview's
  NEXT TAKE line and the NEXT / HELD badges read the same plan; a tick set is spent only by a take
  that read it.
- **The tile's isolation.** A click on a tile makes it the editing target, both ways with BUILD →
  PATTERN; editing, the preview, the right-click menus and CUT / TAKE act on that tile alone; the
  first edit of a screen's preview switches it to its own picture at once (OWN lights; the
  programme and every other screen are untouched); a library tile lands in the target's preview
  under EDIT SAFE and the pattern type changes at once.
- **The next transition.** Right-click any TAKE — a tile's, the wall's keys, the PGM or PREVIEW
  strip — for NEXT TRANSITION: a kind, a rate or a video sting for that one take; the TAKE key reads
  what is pending; the show's own transition never moves; a CUT keeps it. A video sting covers the
  take's screens alone and the preview lands when the clip ends. `TAKE NEXT <words | STING name |
  CLEAR>` on the wire; STATE's `take` row.
- **The group in the tile's menu.** THIS TILE → Group (Main, Confidence, Info, Repeater) on the same
  verb as the Screens page and `SCREEN n GROUP`; the Screens page's arrangement answers a
  right-click with the screen's menu; a canvas sets every screen in it; one refresh whoever changed
  it.
- **Everything follows.** The Eye's screen nodes say LOCKED, held, ticked and the canvas; the desk
  node says what the next TAKE will do; STATE's rows carry `ticked`. Companion 3.8.0: `take_next`,
  `screen_group`, the one-shot and the group as feedbacks and variables, a Take page.

## Round 66 — 2026-09-16 — the God's Eye: the whole show as one picture, its problems worst first, the eye moved from the desk, a key or the wire

`round-66` · PLAN §84 · REVIEW round 66 · 2,219 tests · module 3.7.0

- **EYE** in the rail, above NODES, wears the worst light in the show and its headline (*Main LED:
  MISMATCH — Rate: 50 Hz asked · 59.94 Hz observed*, or ALL GREEN). The **Eye page** is the whole
  show as one picture — every display, screen (with its signal truth), source, device, deck (paired
  or merely connected), Companion heard, node, the twin, the audience room, the stream, the NDI
  sends, the audio sources, outputs and routes, the cue stack and the assistant — each a thing with
  a light by the evidence rule (never a guess), every link a line with a light of its own, in four
  bands: CONTROL, VIDEO, AUDIO, ROOM.
- The method borrowed from `bilawalsidhu/gods-eye-view` and written up in `docs/EYE.md`: **click to
  track** (a double-click puts the camera on the thing on a critically damped spring and dims the
  rest by hop distance), the card with what it is, its words and what it links to, **NEXT PROBLEM**
  through everything red or amber worst first (reds, then ambers; Video before Control before Audio
  before Room; the cause before the symptom), lenses that keep one band, a headline that regenerates
  with the view, **RESET** restoring the exact view from before the focus, and `EYE FOCUS <thing>`
  on the wire as the handoff.
- Right-click on the picture opens the thing's own desk menu — a screen its wall tile's, a cue its
  cue's — or the Eye's: FOCUS, the whole picture, each contact, OPEN the page that shows it, ASK the
  assistant with its facts and links in the question. The layout is a deterministic grid (no
  simulation, nothing jitters); the picture is rebuilt only when the facts moved; the canvas draws
  only on a change or while the camera moves.
- On the wire: `EYE` (the picture as JSON), `EYE FOCUS <screen n | label | id>`, `EYE NEXT`,
  `EYE PREV`, `EYE LENS <name>`, `EYE RESET` — desk verbs a cue never carries; STATE's `eye` row.
  Companion module 3.7.0: the Eye preset page (the headline key lit by the worst light, NEXT / PREV
  PROBLEM, a LENS key per lens, RESET), `eye_*` actions, feedbacks and variables. The assistant's
  brief carries the picture in words under a rule — "what is wrong?" worst first, "what does X
  depend on?" from its links. A `gods-eye` help topic.
- Left with reasons (PLAN §84.6): the Eye is the desk's alone; the lights are now, not a trail;
  the assistant reads the picture and does not act on it; no URL form of the handoff.

## Round 65 — 2026-09-16 — the rig's truth: what the machine is, what each link carries, what the far end receives, a rig commissioned on evidence

`round-65` · PLAN §83 · REVIEW round 65 · 2,206 tests · module 3.6.0

- Every screen's link has a **signal contract** — raster, an exact rational rate (59.94 is not
  60), encoding, depth, range, SDR/HDR, colour space, audio, connector — and the Screens page's
  technical view holds it against three witnesses: what the display's **EDID advertises** (read
  from Windows, parsed — base, CTA-861, DisplayID — hashed, never judged), what Windows
  **observes** it sends (QueryDisplayConfig and the advanced colour; unknown stays unknown), and
  what the **far end receives** (a processor's input status through an adapter — PJLink class 2
  published, others typed from the manual — or the engineer's own reading, `SCREEN n RECEIVED`).
  MATCH, MISMATCH, UNVERIFIED; one journal line per change; `SCREEN n SIGNAL`, STATE, Super
  Check's SIGNAL rows, the assistant's brief, Companion's feedbacks.
- The **planned EDID** built from the contract for a processor input or a PC's port — valid,
  re-parsed, exported as `.bin`/`.hex` with a summary, served over HTTP and the wire — and the
  view says when the display presents that very one.
- The **machine fully exposed**: the Windows build, the CPU and memory, every adapter with its
  driver, every display with its mode and EDID, every audio endpoint with its format, the power
  plan, GPU scheduling, Game DVR — read on a worker, never on the desk's thread; on the Machine
  page, in STATE, the bundle and the assistant's facts. **SAVE KNOWN GOOD** keeps the commissioned
  rig beside the settings and every reading says what drifted (`RIG SAVE`, `RIG STATUS`).
- **Commissioning** as seven stages judged from evidence — discover, assign, contract, capability,
  output test, verify, known good — each with its next step; **TEST ROUTE** (1080p50 RGB 8-bit
  stands in while a path is proven); `COMMISSION STATUS`; the Technician's walkthrough ticked by
  the same evidence; Companion module 3.6.0 with the signal and rig actions, feedbacks, variables
  and a Rig preset page.
- The control network's **trust** made explicit: a bind address, a per-show pairing token for
  every mutating verb (`AUTH` on the wire, `X-Patterns-Token` on the web; reads and loopback
  open), the passcode out of every URL, five wrong tokens close the peer; **one writer per remote
  peer** (replies in order, STATE latest-wins, a slow peer closed, never a torn line).
- Hardening from the round-64 review: the arcade's tab built only on an arcade node; the
  watchdog's **startup deadline** (a child that never beats is a startup hang, restarted under the
  crash policy); the render fence **fails closed** (no seat, no pooled frame — black and a red row
  naming the output); the **shutdown in eight named phases** with a fault injected into every step
  in turn; the final save on the file lane with a bounded wait; no DNS on the desk's thread.
- Architecture: `Patterns.Platform.Windows` — the Windows probes as an assembly of their own, the
  desk's geometry crossing at one seam; the show's files on one lane in the core
  (`PersistenceRuntime`); every secret in one list, the bundle's redaction JSON-aware (a box's
  password and the twin's key masked now); `docs/ADR.md` with eleven decision records, two of them
  designs deferred with their revisit conditions (renderer isolation; lifecycle and clocks).
- The Windows lane proves the P/Invoke layouts on a real Windows on every push
  (`--signal-report`); the processors' own APIs stay typed from their manuals; a real rig with a
  discrete GPU and a processor has not run this code — `docs/QUALIFICATION.md` gains the rows.

## Round 64 — 2026-09-15 — the frame's lifetime said outright, the census, a release that can be rebuilt, the rig's record

`round-64` · PLAN §82 · REVIEW round 64 · 2,122 tests · module 3.4.0

- The render fence holds the frame's life as a fact: a frame is opened and closed, nothing open
  is reused or freed on time, every retired frame has an owner, a stalled sink's pictures wait in
  quarantine and the stall is a recorded fault (`hungFrames`, `quarantinedMB`, `fenceFaults` on
  STATE, the Machine page and the super-check). The forced free is gone.
- The pacer counts drops inside an epoch — a change of rate, cadence or display starts a new one
  — and an output whose display wants more than the render clock gives says so: *LIMITED BY
  RENDER CLOCK* on the chip, the Machine page, STATE and the super-check.
- A lifetime census (desks, nodes, timers, seats, frames, pools, pictures, owners, mounts) on
  STATE and in the support ticket; a lifecycle test boots and closes the desk a hundred times in
  CI with the census back at zero; every dispatcher timer is named by its maker; a closed desk
  does nothing — no publish, no timer armed, no twin beat.
- Every appearance (overlays, layers, PiP, countdown) proved by a matrix over Fade, Cut and
  Slide at five moments; the PiP's arrival, which never showed, does; layers are told apart by
  what they show, not a hash.
- A release that can be rebuilt: the libVLC payload from the exact restored package, a manifest
  of component versions beside the exe and on the Release, `--verify-runtime` on the built
  bundle; a Windows lane that builds, tests and launches the exe; the roll-back script validated
  and tested against a scratch remote in CI.
- The pressure ladder steps down with hysteresis and a five-second dwell, a rung at a time.
- A node builds only its modules: a timer and a caller have no audience room and no arcade, and
  their process never loads them; the wire answers with *not on this node*.
- `docs/QUALIFICATION.md`: the rig's record to fill — Companion 5, IMAG, the soaks, a
  mixed-refresh desk, four hours — with the STATE keys, the rows, the columns and the pass marks.

## Round 63 — 2026-09-15 — CUT and TAKE on a tile, the output on its display's clock, the Preview's own menus, overlays and layers that arrive

`round-63` · PLAN §81 · REVIEW round 63 · 2,053 tests · module 3.4.0

- CUT and TAKE on each screen tile: the preview to that screen alone, as its own picture (OWN
  lights up), the programme and every other screen untouched, the preview kept; `SCREEN n TAKE`
  / `CUT` on the wire and OSC, in the tile's menu, on a Companion key; the look tally reads the
  screen as gone its own way.
- An output paces to its own display when the display is slower than the render clock or than
  what was asked — a 50 Hz screen no longer drawn at 60 — and the tech info chip says the sink,
  its pixels and shape, the kind, and the frames drawn against the rate presented at and the
  display's refresh.
- Right-click on the PREVIEW picture: the thing under the pointer — an overlay, the countdown, a
  layer, the PiP — gets its own menu; the picture gets the preview's, leading with SOURCE (a
  still, a clip, the playlist, a feed, a capture device, a web page, a deck, the arcade, or the
  library).
- Overlays and layers arrive and leave with a transition — a fade by default, a cut or a slide
  by choice, over the show's transition time or their own — when switched on or off live or by
  a TAKE that changes them alone; a whole-picture TAKE carries them in its own.

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
