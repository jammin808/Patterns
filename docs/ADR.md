# Architecture decision records

One page per decision that shaped Patterns and would surprise a newcomer if it were only in the
code. Each says what was decided, why, what it cost, and what would make us revisit it. The rounds
that made them are in `docs/PLAN.md`; the tests that hold them are named. Numbered in the order
they were written down (round 65), not the order they were made.

---

## ADR-001 — Evidence over inference (the unknown rule)

**Decision.** A fact the desk did not observe reads *unknown*. A number is never derived from a
capability, a default or a plan and shown as if measured: an EDID that offers RGB is not a signal
that is RGB; a display's refresh the path never stated is *not available from this Windows path*;
a render clock not yet measured is -1, never 60. Verdicts (MATCH, MISMATCH, commissioned, known
good) move on observation alone.

**Why.** On a rig the cost of a confident wrong number is an hour lost on the wrong cable. An
honest *unknown* sends the engineer to look; a guess sends them the wrong way.

**Cost.** More grey lights than a marketing screenshot would like; a Hyper-V runner that reports a
1 Hz refresh is shown as 1 Hz.

**Held by.** `SignalTruthTests`, `RateAndAspectTests` (the evidence rule of round 64), the
`NotAvailable` note in `SignalTruth`, `CommissioningTests` (UNVERIFIED is never a pass).

## ADR-002 — One writer per remote peer, latest-wins state

**Decision.** Every TCP control peer has exactly one writer (`WirePeer`): replies go in the order
their lines came, STATE pushes are latest-wins behind them, the queue is capped (256 replies), a
write past its deadline (10 s) closes the peer, and a closed peer clears its queue.

**Why.** Two writers interleaving on one socket corrupted lines under load; a slow reader must get
the newest state, not a backlog; a dead reader must not hold the desk's memory.

**Cost.** A reply can be dropped for a peer that stopped reading — it is closed, which is the
truthful outcome. The Companion module reconnects as it always did.

**Held by.** `WirePeerTests`, `RemoteTrustTests`.

## ADR-003 — Explicit control-network trust: a bind address and a pairing token

**Decision.** The control listeners bind to the address the show names (every interface only when
it says so); a per-show pairing token guards every mutating verb over TCP and HTTP (`AUTH <token>`
after HELLO; `X-Patterns-Token` on HTTP); reads need no token; loopback is trusted; the passcode
never rides a query string. Five wrong tokens close the peer.

**Why.** A venue network is shared; a verb from any laptop that found the port must not move the
wall. Reads staying open keeps a phone's tally page, a deck's feedbacks and a support engineer's
look working without pairing.

**Cost.** One more field in every controller's config; NEW TOKEN cuts every paired deck off until
retyped — deliberately.

**Held by.** `PairingTokenTests`, `RemoteTrustTests`, the module's token test, Super Check's REMOTE
row.

## ADR-004 — Signal truth has three witnesses, and none of them is the picture

**Decision.** A screen's link is judged by three witnesses held against the engineer's contract:
what the display's EDID *advertises* (capability, 65.7), what Windows *observes* it sends (the
path, 65.6) and what the far end says it *receives* (the processor's input status or the
engineer's reading of its panel, 65.11). ADVERTISED never moves the verdict; OBSERVED and RECEIVED
do; a RECEIVED disagreement is red because the box itself says the link carries something else.
The planned EDID (65.8) is the desk's offer to the far end, and a match tells the processor loaded
it.

**Why.** Each witness lies in its own way — an EDID can be an emulator's, Windows reports what it
asked for, a processor may scale silently — and only together do they name where a soft picture
comes from.

**Cost.** Three columns where a lesser tool shows one; a RECEIVED that is typed by hand is only as
good as the engineer's eyes, and says so with the name and the time.

**Held by.** `SignalTruthTests`, `EdidTests`, `EdidWriterTests`, `InputStatusTests`,
`SignalTruthAppTests`, `InputStatusAppTests`.

## ADR-005 — The Known Good Rig is a saved fact, compared, never enforced

**Decision.** SAVE KNOWN GOOD writes the rig as it stands — machine, drivers, displays with EDID
hashes, contracts, audio, senders, bindings in words, render clock — beside the settings; every
reading compares the rig of the day against it and says what moved (amber a driver, a mode, a
contract or a plan; red a display or an audio output gone). Nothing is reverted, blocked or
"fixed" by the comparison.

**Why.** "It worked yesterday" needs an answer, not an action: the engineer decides whether a new
driver is meant. A tool that reverts drivers on its own is the tool nobody installs on a show
machine.

**Cost.** The comparison is only as complete as the snapshot's fields; a change outside them (a
BIOS setting) is not seen.

**Held by.** `RigSnapshotTests`, `KnownGoodRigAppTests`, Super Check's RIG rows.

## ADR-006 — The commissioning flow is judged from evidence, and a stage says its next step

**Decision.** Seven stages — DISCOVER, ASSIGN, CONTRACT, CAPABILITY, OUTPUT TEST, VERIFY, KNOWN
GOOD — each green only on the desk's own evidence, each amber or grey line carrying the next verb
in the desk's words. A screen on TEST ROUTE (the diagnostic profile standing in for its contract)
holds the flow at CONTRACT: a proven route is not a commissioned design. The same report feeds the
Machine page, STATE, COMMISSION STATUS, the Technician's walkthrough and the assistant.

**Why.** A checklist ticked by hand records intent; a flow judged from evidence records the rig.
One report in five places means one truth.

**Cost.** A stage can only be green for what the desk can see: the output test is "the outputs
opened this run", not "the engineer looked at the wall".

**Held by.** `CommissioningTests`, `CommissioningAppTests`, `WalkthroughAppTests`.

## ADR-007 — Patterns.Platform.Windows: what Windows says lives in one assembly

**Decision.** The Windows probes — display modes and the observed signal (QueryDisplayConfig,
advanced colour), the EDID reader (the registry), the adapters and the driver keys (DXGI, the
display class key), the audio endpoints (WASAPI), the power plan (powrprof), the CPU and memory
counters — are one assembly referencing the core and its native libraries only, with no UI. The
desk's UI geometry crosses to the core's `RasterRect` at one seam. Every probe is best-effort,
cached, never on the desk's thread when it can be avoided, and answers through a `Source` hook
for a test.

**Why.** The desk's project was where Windows and the UI met; a probe that needed Avalonia's
`PixelRect` could not be tested without the desk, and the platform rules (round 59) said the core
never learns Windows. An assembly makes the boundary a compile error, and its footprint measurable.

**Cost.** One more project; a role that never reads the machine still loads the assembly when the
desk sets the build version on it (cheap; measured by the footprint step).

**Held by.** `ModuleRulesTests` (the rule row and the source scan), the module map test, the
Windows CI lane's signal-report step (the P/Invoke layouts on a real Windows).

## ADR-008 — One persistence lane, generation-coalesced, bounded at exit

**Decision.** Every write of the show's files — autosaves, the recovery record's writes and clears,
the final save — goes through one ordered lane on a worker (`PersistenceRuntime`): a save a newer
save overtakes is skipped; a clear never races a write; the exit waits a bounded time and, when the
show could not be written, leaves the recovery record and a marker for the next start.

**Why.** Three serialisations of a whole show on the desk's thread per GO was the frame budget's
worst enemy (round 56); two writers on one file is a torn show file on a USB stick.

**Cost.** A save is at most one debounce late (900 ms) and never observable on the desk's thread;
the recovery record is one generation behind the very latest edit while the lane is busy.

**Held by.** `ShutdownPhaseTests` (round 65.4's fault injection), `PersistenceRuntimeTests`, the
lifecycle census.

## ADR-009 — Secrets are one list, and everything that leaves the machine reads it

**Decision.** `Secrets.Names` names every property that holds a credential (the admin passcode,
the management token, the pairing token, a box's password, the weather key, Spotify's tokens) and
`Secrets.Placed` the one that hides under a plain name (Twin.Key). The support bundle masks them
(JSON-aware, so a value that merely looks like one is untouched); the twin's wire blanks the ones
in travelling sections and the standby keeps its own. A bare "Key" is an identity, never a secret.

**Why.** A regex over a few names missed the projector's password and the twin's key; the next
credential would have been missed too. One list read by every exit door is the only design that
stays correct as fields are added.

**Cost.** A property named like a secret that is not one (none today) would be masked in the bundle
— visible, and cheap to exempt.

**Held by.** `SecretsTests`, the bundle tests, the twin wire-secrecy tests.

## ADR-010 — Renderer isolation is deferred; the fence fails closed instead

**Decision.** The renderer stays in the desk's process. A separate render host (DXGI swap chains
owned by a child process, frames shared) was designed and measured in outline (rounds 57.3 and
61.3) and is not built. Instead the render fence (round 64) makes frame lifetime explicit and
fails closed (65.3): no seat means no pooled rendering and a red row, never a torn frame; a hung
render thread is quarantined and named.

**Why.** The isolation's benefit — a driver fault not taking the desk down — is real but rare
after the safe-run rule (a native fault restarts into software decode), and its cost is a second
process with a shared-memory frame protocol, a second clock and a second failure mode on every
show. The fence bought most of the stability at a fraction of the surface.

**Revisit when.** A field failure the watchdog cannot contain is traced to the renderer, or a
GPU-affine node (an output-only machine) is wanted — then the render host is the node.

## ADR-011 — Lifecycle and clocks stay as they are, and are written down

**Decision.** The service lifecycle is the kernel's constructor order and the shutdown's named
phases (audience, authority, machine, workers, persist, recovery, ownership, process), not a
container or an `IHostedService` scheme. Time is three clocks with known owners: `ShowClock`
(monotonic seconds for animation and budgets), `RoomClock` (the twin-corrected wall clock the
stage and the nodes read), and the platform's render clock as *measured* by `FrameBudgets` (never
assumed). A test replaces a clock by handing in a reading, not by mocking an interface.

**Why.** The phases already give ordering, fault isolation (a step that throws is recorded and the
next still taken) and a fault-injection test; an interface per clock would add ceremony without a
second implementation to justify it.

**Revisit when.** A second host (a headless render node, a service install) needs the same
services in a different order — then the phases become a contract, not a method.

## ADR-012 — A composition root, not a container; MVVM with a fence, not a framework

**Decision.** Services are built once, in `AppServices`' constructor order — the kernel's
composition root (ADR-011) — and handed to the view models as one object; there is no dependency
injection container, no registration by convention, no property injection. The desk is MVVM as it
stands: the pages are view models the sections bind to, every action goes through
`ShowActions.Execute` with an origin, and a view's code-behind is layout and input plumbing — the
divider drags, the windows' key latches, the Media list's row drag, two copy buttons, two `Loaded`
hooks. The seams that remain are *named*, by file and by count, in a test
(`ArchitectureFenceTests`): the ambient `AppServices.Instance`, reached only where Avalonia
constructs the object itself (a tile's pipeline, the lazy page's warm-up, a XAML converter, the
crash note before the desk exists); the code-behind handlers there are; and the engines, which the
kernel alone constructs. The test fails when a file reaches for the ambient service, gains a
handler, does heavy work in code-behind (files, threads, the network, serialisation, an engine
driven directly) or builds an engine of its own — so each of those becomes a decision made in the
fence's list with a reason beside it, as the module rules are for the assemblies.

**Why.** The risk the question names — tight coupling between classes, UI logic leaking into
business logic — is real, and a container does not answer it: a container answers *registration*.
With the desk's services in one constructor the graph is explicit and its order is the lifecycle
(ADR-011); a test builds the whole desk headless in under a second and 700-odd of them do; the
node compositions (round 64.7's role allowlists) are constructors, not configuration; the start-up
budget (round 56.7) has no reflection in it. What actually leaks is measured rather than argued:
at round 69, 18 code-behind handlers in 7 of 44 code-behind files, none doing heavy work; 11 reaches
for `AppServices.Instance` in 6 files, all at Avalonia's own construction seams; every engine built
in one file. A container would hide the order behind a registry and move those numbers nowhere. The
static registries that remain by design — `InputBus`, `ImageCache`, `FramePools`,
`QualityLadder.Shared`, `UiThread`, `MediaLocator.WebResolver`, `MachineProbe.Source`, `ShowGc`,
`GpuCacheGovernor` — were audited in round 64.3 and each has a reason (a frame's path that must not
allocate or look anything up; a seam Avalonia's own objects reach; a runtime setting that is one per
process); a test replaces one by handing in a reading or a factory, not by mocking an interface.

**Revisit when.** A second host needs the same services in a different graph (ADR-011's trigger),
or a static seam acquires a second implementation worth swapping at run time — then an interface
at that seam, not a container, is the first step; the fence's list says where.

## ADR-013 — Analyzers are fences with reasons, not a style; and the desk speaks one culture

**Decision.** Every build runs the .NET SDK's analyzers, Sonar's rules (`SonarAnalyzer.CSharp` — the
rules a SonarQube server runs, here inside the compiler), the threading analyzers and a banned-API
list. A named set of rules stops the build — the fences — and each is written in `.editorconfig` with
the reason it stops, generated from `tools/gen_editorconfig.py` where the plan lives by family:
culture, disposal, threading, security, correctness, performance, banned. Everything else is a
suggestion the IDE shows, or off with its reason written. `TreatWarningsAsErrors` makes a fence a stop
locally and in CI alike. A new instance of a fenced rule is a decision: fix it, or carry the reason
beside the code (`#pragma warning disable RULE // why`, or `[SuppressMessage]` with a Justification);
a blanket suppression is never the answer. StyleCop is not used. The process runs on the invariant
culture from Main's first line (`CultureGuard`), and every formatting and parsing call names its
provider anyway: the guard for the machine, the provider for the reader.

**Why.** An analyzer earns its place by the bug class it stops, and a rule that stops the build has to
be one the team would fix every time. Measured on this tree (round 70, `docs/ANALYSIS.md` §3) the
rules that describe a show's failure modes found 145 sites formatting or comparing by the machine's
locale — a Hamburg desk writing `0,5` on the wire, a Swedish one a Unicode minus — 39 disposable fields
never disposed, 55 P/Invokes with no DLL search path on an exe meant to run from a stick, 29 regular
expressions with no timeout on text from the wire and pages, 7 results ignored (a keep-awake and a
registry write among them), blocking waits and unscheduled continuations that could land on the UI
thread. Those are the fences. The rules about naming and API shape — 7,000 of the SDK's 7,359
findings at every rule on, all 58,459 of StyleCop's — describe a library style that is not this desk's;
they are switched off by name with the reason, rather than left to shout or half-enabled. Sonar's
server was not adopted here because it cannot be proven here; its rules were, because they can.

**Revisit when.** A SonarCloud dashboard is wanted (a token and one job; `docs/ANALYSIS.md` §7 has the
shape); a fenced rule's written exceptions come to outnumber the sites it corrected (then the rule is
wrong for this code and goes to suggestion with the reason written — CA1867 went that way in round 70,
with nothing to fix and five sites it would have made wrong; CA2213 stays, with thirty-nine fixed and
ten explained); or a failure in the field turns out to be a class a rule would have caught — then that
rule joins the fences with the field's example as its reason.

## ADR-014 — A device list is a catalogue the platform keeps current, never an enumeration on the desk's thread

**Decision.** Any list of the machine's devices the desk shows, searches or resolves — the audio
endpoints today; video capture devices and NDI sources by the same rule when their line ever shows in
the tick; the displays already so since round 32 — is a catalogue: read once on a worker when the
desk starts, kept current by the platform's own notification (WASAPI's `IMMNotificationClient` for
audio), published as an immutable snapshot with a version, and read by everything on the desk's thread
in the time of a field read. A page rebuilds its list when the version moved, never on a schedule; a
refresh asked for by name is a nudge to the worker, never a wait; a press opens a device by the id the
catalogue holds. The desk's tick, its menus, its pickers, its facts and its verbs never call an
enumeration. Where a platform has no notification, the catalogue reads on a slow worker cadence and
says so in its words — still never on the tick.

**Why.** Round 71's report: a WASAPI enumeration with each endpoint's name read from its property
store costs 200–400 ms on an ordinary machine, and the desk ran one on its own thread every second
while a sound-reactive pattern was on the desk, on every right-click menu of a screen, every fifth tick
for the health facts, and after every rig change for the screens' choices — sixty ticks of sixty past
the budget, every moving pattern stuttering, on a laptop and a 4 GB desk alike. The list changes when a
cable moves, which Windows reports; asking for it on a schedule was an attempt at knowing, and the
notification is the fact. The same shape already serves the displays (the hot-plug of round 32) and
the NDI sources (discovery is push-based).

**Revisit when.** A platform's notification proves unreliable in the field (a device change the
catalogue missed): then that platform's catalogue gains the slow worker cadence as a safety net, with
the evidence in the round's paper; or a list is wanted that no platform reports at all.
