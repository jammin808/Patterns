# The generalisation roadmap, assessed — what round 75 takes, what waits, what is declined

A roadmap arrived after round 74 ("Patterns Orchestration Core and Cross-Industry Roadmap"): make the
strongest parts of Patterns — staged and live state, the selective commit, the take ticket, the walls, the
capability/request/observation evidence, the Eye, the Navigator protocol — reusable beyond live AV, in
eight phases, without weakening the AV product. This paper reads it against the code as it stands at round
74 and records the decisions. The method is the one round 72 used on the maintainer's critique: every claim
checked at a named line before anything is built; attempts are not facts.

## 1. The strategic advice — taken

The roadmap's first pages say: do not fork Patterns into industry products, do not rename the AV product
around enterprise terms, do not build a plugin framework or a generic transaction engine, and close the
current product's issues and its physical qualification before any generalisation round. All of that is
right and is the position of this paper. The AV product stays the proving ground.

## 2. The P0 / P1 list — checked in the code

| Item | Verdict | Where |
|---|---|---|
| `RECORD ON` should require pairing | **Real.** The pairing gate let every wire kind but an action through as a question, so an unpaired connection could subscribe to the desk's action feed and set a deck's whereabouts | `ControlProtocol.IsQuery` (any kind but `Action`); `ControlService`'s NAV DECK / RECORD block ran before the gate |
| Administrative secrets must not enter action feeds, journal rows, logs or exports | **Real, in two places.** The wire writer spelt `UPDATE APPLY <passcode>` and `RESTART <passcode>` (the passcode rides those actions' target), so a successful admin verb from the desk reached every recording deck as an ACTION line and Companion's recorder stored it in a button — round 74's feed. The journal's row wrote the same target to disk (`JournalTarget` fell through to `action.Target`) since the admin verbs exist — older than round 74 | `WireWriter.Line`, `ControlService.Feed`, `ShowActions.JournalTarget`, `ShowLog.Record` |
| Define the ticket's behaviour when a promised destination changes role or topology before landing | **Real gap.** `TakeTicket.Land` asked two questions of a promised target — still in the rig, locked since — and landed a screen made a repeater, a canvas whose members moved or a screen that joined a canvas as if nothing had changed, contradicting the hold the same rule applies at press time (a repeater is held at the press) | `TakePlan.cs`, `TakeTicket.Land` |
| Fix LOCK during an active sting | **Real, open since 72.3.** LOCK read the air's picture — the clip — and pinned it in the edited state while EDIT SAFE was open (reproduced by the new test with the fix off). A second hole suspected in the whole-cover restore was tested and not found: with EDIT SAFE closed the screen already came back to the show | `ShowActions.SetLock`, `StingerService.PictureUnder` |
| Complete the physical qualification matrices; run the long multi-output soak | **Needs the rig.** Nothing here can do it; `docs/QUALIFICATION.md` and `docs/SOAK.md` are the records to fill | — |

Round 75 closes the first four (PLAN §93.2–§93.4). The fifth waits for the maintainer's bench.

## 3. The eight phases — what is already true, what is not worth its churn

| Phase | Assessment |
|---|---|
| 1 — formalise the Destination concept; the Switcher's rules must not need an Avalonia output window; a fake destination in a TakePlan test | **Already true in substance.** `Patterns.Core` has no Avalonia reference at all; `TakeTarget`, `TakePlan` and `TakeTicket` are plain records over ids and flags (round 75 adds a shape in words); `TakePlanTests`, `TakeTicketTests` and `ScreenTakeTests` run with no renderer. What remains is a rename, which buys the operator nothing and would churn a suite of over two thousand tests. **Declined.** |
| 2 — staged/live state semantics, tested with no renderer | **Already true.** `AirState` versus the edited state (round 72.2), the sandbox's `SendAll` with kept targets, the lock, the take scope resolver (round 67.2) — all in Core or in services with Core tests. **Nothing to do.** |
| 3 — generalise `TakePlan` / `TakeTicket` (`CommitPlan`), a ticket that carries frozen identities, held and excluded destinations, and never gains a destination | **The rule is real and now complete** (round 75.2: the landing compares each promised target's shape). The rename to Commit* is **declined**. |
| 4 — Wall / Canvas as a generic Surface Group | Joined canvases already are logical surfaces over member screens, separate from the transport (the signal contract) and the physical output. An "Ops Wall" is a show file with four screens showing web pages; it needs no code. **Nothing to do.** |
| 5 — generalise capability / request / observation as generic evidence | The evidence exists twice already — Signal Truth's verdicts (MATCH, PARTIAL, MISMATCH) and the devices' receipt ladder (Sent, Delivered, Accepted, Observed). A common `Evidence` type would be abstraction noise until a third producer exists. **Postponed** until render nodes give one. |
| 6 — the Eye as a generic topology view | Already facts in, graph out, inside Core, with no Avalonia. The node kinds are the show's (Desk, Source, Screen, Display, NdiSend, FarEnd, Device, Deck, Companion, Twin, Node, AudioSource, AudioOut, Room, Stream…). Generic kinds would be a rename. **Declined.** |
| 7 — the Navigator protocol as a generic control-surface descriptor, versioned | The descriptor exists: the NAV reply (rails, pages, the desk) and every MENU reply (groups of entries with a line, a tick, a tone, a reason, a route, a menu, a drawer), documented in `docs/REMOTE.md`; Companion is one client. **Taken, minimally** (round 75.4): a `protocol` version on both replies and the descriptor named in words. A tablet or web client that reads the same replies is the natural next product step — **postponed**, not declined. |
| 8 — a generic endpoint adapter so the Switcher can commit state to something it does not render | `Patterns.Devices` already drives endpoints the desk does not render (PJLink, processors, HTTP, TCP, serial, MIDI, Companion's own API) with receipts. What does not exist is a *destination* Patterns does not render: a render node — a Patterns process on another machine that draws a screen for this desk, observed back. That is the "brain of the show" shape of this item and a real product feature. **Postponed** until the single-machine product has been qualified on a rig and a second machine is on the bench; the twin and follower links (rounds 15, 41, 49) are the ground it stands on. |

## 4. The work to avoid — agreed, and two more

The roadmap's own list is agreed: no second product now, no renaming of the AV UI, no plugin framework
first, no industry logic in Core. Two more from this reading: no interfaces ahead of a second
implementation (`IDestination`, `IEndpointAdapter` — add them when a render node needs them), and no
`Patterns.Domain` / `Runtime` / `Protocol` / `Topology` project split — the roadmap's own gate (a clear
boundary, two consumers, less coupling) is not met, and the module rules test already fences today's split
(`docs/MODULES.md`).

## 5. What this round does, and what waits

**Done in round 75:** the recorder behind pairing and no secret on any feed (75.1); a landing that holds a
target that is not what the press saw (75.2); a lock that pins the picture beneath a clip, never the clip
(75.3); the descriptor version (75.4).

**Waits for the rig:** the qualification matrices and the soak.

**Waits for a decision after the rig:** render nodes; a tablet Navigator reading NAV and MENU; a common
evidence type when a third producer exists.

**Declined:** the renames (Destination, Commit*, Surface, generic Eye kinds, ControlPage/ControlItem); the
project split; interfaces ahead of a second implementation; a second vertical; the Ops Wall as a product.

One caution stands. The roadmap says nothing about operator workflow, Companion, right-click menus or the
assistant, and following it in full would spend rounds without moving the show. The doctrine stream — the
operator's and the showcaller's — stays the main line.
