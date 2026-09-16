# The round-69.7 critique, assessed — and what round 72 does about it

The maintainer's *Developer Critique and Prioritised Roadmap* (reviewed tip `5c63180`, round 69.7)
names thirteen work items and a strategy: close the correctness items before another feature round.
This paper is the assessment — every claim checked against the code, not taken on trust — with the
peers' practice where it bears on the decision, and the plan that follows. Round 72 is the round it
asks for, under this repository's numbering (its "Round 70" was taken by the analyzers, 71 by the
desk tick).

## 1. The claims, checked

| # | The critique says | In the code | Verdict | Round 72 |
|--:|---|---|---|---|
| 1 | Sound-follow-picture derives the picture from the editable `State`, not the on-air `AirState`: an EDIT SAFE edit moves the live sound before TAKE | `AudioGraphService.Apply` reads `_services.State` and `AudioRouting.Resolve(state, …)`; `SourceOfScreen` reads the placements' own-picture flags; round 67.5's *own on edit* sets that flag on the editable state at the first edit while the air is a frozen clone (`SandboxService._program`). The Eye, STATE and the Audio page's words read the same editable state. | **Real.** | 72.2: the picture-derived functions take the picture state (`AirState`) beside the configuration state (`State`); every reader passes both; EDIT SAFE regressions. |
| 2 | A sting-delayed TAKE re-plans at the clip's end: targets armed or ticked during the clip land, though the press never promised them | `StingTakeScope` keeps the scope *words* (FOCUSED alone is pinned); `RunAfter` executes `TAKE <words>` at the end, and `PlanTake` reads arming and ticks then. The code says so in its own comment ("arming and ticks are visible state, read as the take lands"). | **Real, and a design choice the critique is right to reverse.** A production switcher latches a transition at the press; Event Master's program changes only by the Mix or Cut the operator took, on the destinations armed then. | 72.3: `TakeTicket` — the plan frozen at the press with a generation; the landing commits the ticket; a target may leave the ticket by a LOCK applied since (landing ⊆ press), never join it. |
| 3 | `NextTake.Consume()` runs before the sting is found or fired: a missing clip or a refused start loses the one-shot | `ShowActions.Switcher`: `Consume()` then `StingerLibrary.Find` (refused) then `Stingers.Fire` (failed) — both after the consumption. | **Real.** | 72.3: peek, prepare, start, then consume; a failed take keeps the one-shot and says so. |
| 4 | A show replaced leaves the last show's runtime residue: arming, the one-shot, ticks, focus, edit watchers, staging | `RefreshAfterShowReplaced` resets the tail, the hooks, the wall, the cues and the pages; it does not touch `NextTake`, the ticks, the focus, the arming or `_editWatches`. | **Real.** | 72.4: one explicit session reset with a documented policy per fact; tests. |
| 5 | `SCREEN n <unknown word>` falls through to `ScreenToggle` — a typo switches an output | `ControlProtocol` line 434: `_ => Act(ScreenToggle, n)`. The same fall-through on BLACKOUT, LOCK n, DUCK, REVIEW, FREEZE (`_ => …Toggle`). | **Real, five times.** PJLink answers an undefined command with `ERR1` and does nothing; that is the rule for a wire. | 72.5: bare and TOGGLE toggle; every other word is refused as unknown; one regression over the families. |
| 6 | MATCH reduces to "no mismatch + raster known + rate known" and overstates confidence when the contract names encoding, depth, range, colour or transport that nothing observed | `SignalTruth.Compare` line 366, exactly so; each unobservable contracted property adds a grey "observed unknown" line and nothing else. | **Real.** | 72.6: a PARTIAL verdict — everything observed agrees, one or more contracted properties unobserved; Companion, STATE, the super-check and the commissioning flow carry it. |
| 7 | `FrameSmoother.Adapt()` allocates a sorted copy of the jitter window on every measured arrival | Lines 290–292: `new double[_jitterCount]` per adapt, on every frame once measured. | **Real.** | 72.7: a scratch window; an allocation test over the arrival path. |
| 8 | `QueryDisplayConfig` can fail with `ERROR_INSUFFICIENT_BUFFER` when the topology grows between the size query and the query; the display, EDID and machine caches are not invalidated together | `DisplayObservation.Query` returns empty on any non-zero result. Microsoft's own documentation says to loop on `ERROR_INSUFFICIENT_BUFFER` and re-read the sizes. The three caches each have a `Forget()`; nothing calls the EDID's or the display's on a topology change. | **Real, both halves.** | 72.7: the bounded loop; `DisplayEvidence.InvalidateTopology()` called from the screen service's topology change. |
| 9 | A canvas role change mutates member screens one at a time, publishing each | `ShowActions.Rig`: `SetRole` per member, each in its own `BulkEdit` and `EditAir`. | **Real, minor.** | 72.4: one edit, one publish, one result. |
| 10 | Edit-session watchers from round 67 survive a show replacement | `_editWatches` is cleared only per target; not on replacement. | **Real** (part of 4). | 72.4. |
| 11 | EDID: a corpus, parser fuzzing, generated-EDID validation, robust cache identity | The parser records problems and never throws on short input; it has samples, not a fuzz; the cache is keyed by device path. | **Partly open.** | 72.7: a seeded fuzz over the samples (truncation, checksums, garbage extensions) as a test; the corpus is field work recorded in QUALIFICATION. |
| 12 | Bench the web pipeline on Windows and prototype the GPU-surface path | Recorded as a design in `docs/WEB-VIDEO.md` §4.7 since round 68; no Windows bench exists in this environment. | **Agreed, not here.** | Stays a Windows bench item; the qualification matrix names its measurements. |
| 13 | Qualify on a real multi-output rig before another feature round | `docs/QUALIFICATION.md` has nine matrices; none for TAKE scopes under stings, the audio follow matrix, or display/EDID changes while live. | **Agreed.** | 72.8: three matrices and the soak's tracked figures added as a record to fill. |

The critique's reading of the code was accurate on every item it made a claim about. Two things it
did not know: round 70 already fenced the analyzers and round 71 removed the desk tick's audio
enumeration, both after its reviewed tip.

## 2. What the peers do

- **A transition is latched at the press.** Barco's Event Master changes program only by the Mix or
  Cut the operator took, on the destinations armed at that moment; broadcast switchers run a
  transition with the parameters latched when it started. Nothing an operator touches during the
  transition joins it. Patterns' sting-covered TAKE is a transition that lasts a clip; the same rule
  applies: the ticket is the press.
- **An unknown command does nothing.** PJLink's class 1 and 2 specifications answer an unsupported
  command with `ERR1` (undefined command) and an out-of-range parameter with `ERR2`; the projector's
  state does not move. Companion, OSC and every control protocol that carries a typo must fail closed.
- **A broken cue cannot fire.** QLab marks a cue whose target is missing as broken and GO does not run
  it; the show keeps what it had. A one-shot whose sting is gone is that case: refuse, keep the
  one-shot, say why.
- **Windows' display query loops.** Microsoft's `QueryDisplayConfig` documentation: the configuration
  may change between `GetDisplayConfigBufferSizes` and the query; the query then fails with
  `ERROR_INSUFFICIENT_BUFFER` and the caller re-reads the sizes and tries again. Their sample is a
  `do … while (result == ERROR_INSUFFICIENT_BUFFER)`.
- **Green means verified.** A processor's input status names each property it measured; a property
  it cannot measure is shown as such, not folded into a pass. Signal Truth's PARTIAL is that
  distinction.

## 3. What round 72 does not take

- A container, a plugin system, or the six-layer restructure of §17: the layers already exist as
  assemblies and folders (MODULES.md); the critique itself asks for correctness first.
- QLab-style pre-wait, post-wait and continue modes, Resolume-style slicing, deeper Event Master or
  PIXERA control (§16, §19 options C and D): feature rounds, after the qualification the critique
  puts first.
- The GPU-surface browser path: a Windows bench first, as the paper says.

## 4. Acceptance, as tests

Each unit lands with the tests the critique asked for, in the App suite where the desk is the
subject and in Core where the rule is pure: EDIT SAFE never moves followed audio; TAKE moves it when
the picture lands; DISCARD leaves it; a sting-covered take lands what the press promised and no
more, less only by a lock applied since; a failed take keeps its one-shot; a replaced show carries
no residue; every unknown word on the wire is refused; PARTIAL is the verdict when a contracted
property is unobserved; the smoother allocates nothing on a steady stream; the display query loops on
the race; the EDID parser survives a thousand mutations.

## Closed — what the round did with each item

Written at the round's end, against the plan above.

- **72.2 audio truth** — done as designed: `AudioRouting` takes (configuration, picture) beside every
  single-state reading; every reader passes `AirState`. Found on the way and fixed: the audio graph's 50 ms
  tick forced a plan rebuild (twenty a second while the matrix was on); it polls now.
- **72.3 the take ticket** — done as designed, with one addition: the tile's own take under a sting is a ticket
  too, and a lock since holds it with words that say so. Seen, not done: a LOCK pressed during a running clip
  pins the clip's picture as the screen's own in the edited state (the lock reads the air, which is the clip);
  the next round should pin the picture under the clip. REVIEW round 72.
- **72.4 the session reset** — done as designed, and one thing more than planned: with EDIT SAFE open the
  loaded show is frozen as the programme too (`Sandbox.RestoreProgram` over a clone), because the old
  programme would otherwise have stayed on the outputs and a discard would have put its pictures into the
  loaded show. The sting's session is forgotten without a restore (`StingerService.ForgetSession`).
- **72.5 the wire** — done as designed; twenty stray words in the regression.
- **72.6 PARTIAL** — done as designed, with two decisions the plan did not name: a contract's audio policy
  counts as unobserved (nothing on the machine observes the audio a link carries; the EDID advertises
  capability), and the test route's diagnostic profile drops its audio word so a proven path still reads
  MATCH. The far end's word settles dynamic range, colour space, transport and audio, like its raster, rate,
  encoding and depth. Companion 3.11.0 carries PARTIAL and the take ticket together.
- **72.7 the engineering items** — done as designed; the topology invalidation counts itself
  (`DisplayEvidence.Invalidations`) for the Machine page and the qualification row.
- **Declined, unchanged** — the container, the six-layer restructure, QLab's cue modes, the GPU browser
  path without a Windows bench: the reasons above stand.

