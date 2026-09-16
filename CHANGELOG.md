# Changelog

Patterns is built in rounds: one request from the field, answered as a run of commits, each
round closed with its papers. This is the short form, newest first; the long form is
`docs/PLAN.md` (the design of each round, by section) and `docs/REVIEW.md` (what was found and
fixed). Every round from 15 on is a tag on its last commit — `round-15` … — and every tag from
`round-61` on has a Release with the built desk. The README's *Versions and rolling back* says
how to get any of them back. The count at each round is the test suite at its end; the module
is the Bitfocus Companion module, where its version moved.

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
