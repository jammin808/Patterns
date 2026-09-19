> Interim version, placed on branch `claude/retrospective-hindsight` on 2026-09-19. The final verified version will replace this file on the same branch and will state what changed.

# Patterns, rebuilt with hindsight

**Repository:** `jammin808/Patterns`, branch `claude/nice-hopper-vf1paa`, HEAD `08dcaaa` (round 78, 2026-09-18).
**Question:** given the trajectory, would a from-scratch rebuild do anything differently?
**Status:** interim, verified subset. See section 9 for exactly what has and has not been checked.

---

## How to read this

Twenty analysis lenses (scope, architecture, state model, testing, process and docs, platform hygiene, defect archaeology, integrations and Companion, the assistant, security, performance, persistence, the distributed layer, the operator surface, release operations, peers and prior art, the AI-collaboration process, a code-quality sample, the original product core, portability) each produced observations and up to six recommendations. Every recommendation then faced three independent skeptics whose job was to refute it: one on evidence (does the repo say what the recommendation says?), one on counterfactual (would the alternative really have been better for this project, or is it hindsight or generic advice?), one on cost (was it worth doing when the recommendation says, under the real constraints: a Linux box, no Windows machine, no rig, an AI agent writing the code, three weeks?). A recommendation survives if at most one of the three refutes it, and the skeptics' corrections are applied to what you read here.

Each item below is tagged:

- **Verified**: survived at least two of three skeptics; corrections applied.
- **Fact checked, recommendation pending**: the facts were confirmed by a skeptic or directly by me, but the recommendation itself has fewer than two verdicts yet.
- **Observation**: from a lens report; not yet checked. Presented as something to look at, not as a conclusion.

Where a skeptic downgraded an impact rating I say so, even where I think the item still belongs in the list.

---

## 1. Short answer

Yes, but mostly not in the code. The technology choices, the headless whole-desk test strategy, the single action layer, the atomic show file and the feature-detected natives all held up under 78 rounds of growth, and a rebuild would keep them. What I would change is the **order in which evidence, scope and fences arrived**:

1. **Evidence.** The show laptop's own files (Super Check, `patterns.log`, `showlog.jsonl`, the recovery record) first reached the repository at round 71 of 78, and a full set at rounds 76 to 78. Each read found a class of defect the 2,429 headless tests structurally could not see: a 200 to 400 ms COM enumeration on the UI thread every second, present since round 9; eleven TAKEs journaled `Done` with the outputs off; a 50 Hz TV under a 60 fps show; a screencast refused at every ask. Nineteen rig checklists and 21 qualification matrices were written and none was ever filled in.
2. **Scope.** Roughly 13,400 source lines of twin failover, follower and timer nodes, arcade games and audience phones were built between 09-12 and 09-14, before one machine had a passing qualification row. None of it has run on two real machines; the drill document says so itself.
3. **Two truths.** The edited show and the on-air programme are the same class, told apart by one null-coalescing property. Readers of the wrong state recurred from day 4 to round 72, and the field's first real bug (round 78) was a rule that needed both states and the editing target.
4. **Receipts.** `ActionResult` is a status plus words. The doctrine "attempts are not facts" had to be re-applied at rounds 43, 72, 75 and 78 because the type never carried what was observed to change.
5. **Fences.** Analyzers, the banned-API list, module rules and the lifecycle census arrived at rounds 59 to 70. The retrofit itself was cheap in hours, but the classes they catch accumulated unseen for twelve days.
6. **The record.** Every build ever made reports version 1.0.0. PLAN.md is a 1 MB journal whose sections are numbered out of order. The CHANGELOG was back-filled at round 61 and encodes a false belief that history begins on 2026-09-06.

The single largest change is a process one: **fewer rounds, each closed by the desk's files, with breadth capped at what the rig had proven.** The maintainer's own round-56 brief said it ("leave any work on the twin machine until later; we need to concentrate on getting a single machine rock solid"), and that round immediately found the show being serialised three times per GO on the UI thread.

---

## 2. The trajectory

| Dates | Rounds | What happened | Size signal |
|---|---|---|---|
| 08-29 | day 1 | Brief: a portable test-pattern generator with an explicit out-of-scope list (PLAN §1 to §7). Avalonia 11.3 + SkiaSharp, Core/App/Tests, TreatWarningsAsErrors, CI with a windows-latest publish job, 87 tests (5 of them headless app boots). | ~9.7K lines |
| 08-30 to 09-01 | days 2 to 4 | Becomes a playout switcher: looks, cues on F-keys, a sandbox PGM/PVW (`d2d1fd9`), the TCP wire and a Companion module (`e43e28b`), NDI receive, capture, web pages, streaming. First "field-feedback round" (`0d9ccfb`) on day 2. First reader-of-the-wrong-state fix on 09-01 (`35b0945`). | ten commits breaking §7 |
| 09-03 | Run mode | Six Run-mode phases in one day: the one action layer with an origin and a journal (`0c2c219`), `TestApp.Boot`, the wall, the cue stack. Two action vocabularies born the same day. | |
| 09-04 to 09-06 | 9 to 17 | Fractals (and a per-tick WASAPI enumeration, `c59d659`), Spotify, stingers, edge blend, particles, lower thirds, permanent installs, Arduino, OSC, PDF and PowerPoint decks, WebView2 pages (`e775842`), the LLM assistant (`7ad2166`), weather. MainViewModel split into partials at 6.8K lines (`a308218`). Crash hardening and the crash-note kit (`496120e`), one vocabulary (`1a0eb49`, `82e4ac2`), .NET 10, the out-of-process encoder host (`ef67e6f`), a rig day (round 17). | 09-05 alone: +58K lines in 42 commits |
| 09-07 to 09-11 | 18 to 28 | Feature rounds from the maintainer's lists: relative cue waits, two audio wires, phone remote, decks. Verbal field notes at rounds 25 and 28. | |
| 09-12 | 29 to 34 | Review round "what a publish costs" (`3aabd81`), ShowActions split by area, the twin (`88c8c77`), PJLink endpoints to spec, mesh warp, camera calibration, the show lock (`8cf45d9`). | |
| 09-13 | 35 to 52 | Nodes A to D, arcade, audience phones and rig-day games: 11,180 lines in one day from one quoted idea. Hardening H1 to H6 with the kernel (`677d554`). "Attempts are not facts" (`90d6a9b`, round 43). Twin HMAC keys. | 33 of 89 commits on 09-12 to 09-14 and 45% of insertions were the distributed layer |
| 09-14 | 53 to 58 | Companion module rewritten on module base 2.x (`aebd42e`), audio research and routing matrix, "single machine rock solid" (`8044340`: three serialisations per GO removed), memory pools. | |
| 09-15 | 59 to 64 | Assembly split into ten modules (`d3b8e1c`, 324 files), a menus regression the maintainer found by running the build, CHANGELOG, tags and rollback tooling (round 61), the App test host killed three times at ~500 tests (round 62), the Windows smoke lane and the lifecycle census (`1c895ee`, round 64). | |
| 09-16 | 65 to 73 | ADRs, platform assembly, pairing token (65), the God's Eye (66), the pure take resolver (67, `066aaf3`), web video (68), the no-container ADR and fences (69), analyzers (70: 145 culture sites, 39 undisposed fields, 55 P/Invokes without a search path), the audio tick fixed from the first Super Check (71, `8794153`), a 13-item critique all found real (72), MIDI learn (73). | 9 to 11 rounds a day |
| 09-17 to 09-18 | 74 to 78 | The desk's files arrive. Five faults from a real show (76), the render clock and the 50 Hz TV (77), eleven TAKEs journaled Done with outputs off and a second desk clearing the first's recovery record (78, `e175928`, `c531909`). | |

Totals at HEAD: 364 commits on one branch, no merges, 78 rounds in 21 days; ~139K source lines and ~88K test lines; 2,429 tests of which 661 boot the whole desk headless; ~2.1 MB of prose in docs; `ShowState.cs` 2,952 lines with 499 properties across 69 types; `ShowActionKind` 220 members; `ControlProtocol.cs` 1,443 lines of hand-written grammar; `AppServices.cs` 2,459 lines; the Companion module bumped 36 times in 19 days; about 20 sidecar files written beside the exe.

The kind of thing being built changed four times (pattern generator, switcher, show-control desk, multi-machine platform) and the first two changes happened within 48 hours of the brief. The record shows the shape was fixed in those two days and afterwards only fenced, never revised.

---

## 3. What I would keep

These are the decisions that demonstrably paid. A rebuild that drops any of them loses the project's actual advantages.

**Verified**

- **Avalonia 11.3 + SkiaSharp with a headless whole-desk suite on Linux.** This is the reason a Windows-only show tool could be built at all from a Linux box. 661 tests boot the real desk with real Skia; the suite reproduced the round-17 crash, the round-62 leak once memory was measured, and the round-64 roots. Exact pins; the .NET 8 to 10 move cost 21 lines behind `PatternsTfm`. Corrections from the skeptics: the tag, Release and rollback mechanism of round 61 has never been exercised (the remote holds zero tags, the tag push answered 403 five times, README line 1792 is untrue), so call it "script-tested". Withdraw the idea of adding `global.json` and a lock file: the build box could not install the .NET 10 SDK, which is why `PatternsTfm` exists. Do make the assistant's model id a setting with a compiled default; today it is a compile-time constant and a model retirement would need a release.
- **One action layer, one executor with an origin, one vocabulary, a journal.** From `0c2c219` (09-03) every origin (desk, wire, OSC, Companion, cue, node, assistant) is one `ShowAction` through `ShowActions.Execute` with an `ActionOrigin`. This is what let the round-78 show log pin eleven bad takes to the second and ship the fix with a 266-line regression test in one 12-file commit. Correction: the day-1 suite had 5 of 87 tests booting the app headless, not 87; `TestApp.Boot` and the executor date from 09-03; one vocabulary from round 16.
- **Architecture rules as tests, not as a container or a framework.** `ModuleRulesTests` reads compiled references, `ArchitectureFenceTests` scans source, `CompanionModuleContractTests` parses every line the module can emit. "A folder is a promise; an assembly is a build error." The composition-root-without-container decision (ADR-012) held: `AppServices.Instance` is reached from 7 files after the round-69 fence. Correction: the parts of the system with a table and a test behind them stayed correct as verbs were added; the parts without one (the parser's spellings, REMOTE.md, help wire lines) are where the round-72 bugs were. Cost to state honestly: serial suites, 8m54s for 685 App tests, and a host that died at 13.5 GB at round 62; the offsetting benefit is that 683 real boots per run exposed product leaks.
- **The show file contract.** Atomic write with `.bak`, quarantine that never blocks startup, tolerant enum reads, version-gated idempotent in-place migrations with the reasoning written where the gate is, a `backups/` folder twenty deep. No operator's show was ever lost to a format or migration fault across ten schema bumps and 102 commits to `ShowState.cs`. Corrections: keep the round-78 contract, not the day-1 one (day 1 had only atomic write and quarantine; the version arrived 08-30, tolerant enums 09-03, backups 09-05, the single write gate 09-12); and twice a review found the quarantine too eager, each fixed before any field use.
- **Feature-detected optional natives behind one candidate-folder table each, failing with words.** NDI, libVLC and WebView2 are looked up in an ordered table, gated by a probe, and their absence becomes a sentence on the page and in the log; the libVLC table is shared by the desk, the encoder host and the runtime check. Corrections: say "never exercised on real hardware" rather than "no field failure" (the evidence the promise held is the Linux suite and the round-64 Windows lane running without NDI or libVLC); the NDI shim has 7 commits with `--follow`, not 3; WebView2 had the same presence probe and its field failure was behavioural, so the shape does not address it.
- **The encoder-host kit and the crash-note kit, as they are.** A three-slot shared-memory ring, a stdin/stdout word protocol, a Windows job object so a dead desk never leaves an encoder streaming, backoff and a stand-down rule, health words the operator can read; and from round 14 an exit code turned into a sentence, a mini-dump when `createdump` is beside the exe, a software-decode safe run. Corrections: do not extend this into "host every native edge" (ADR-010 and PLAN §14.2 declined that with reasons the field never overturned; see section 6); the Windows-only parts have no execution record because no CI lane runs `dotnet test` on Windows and `--verify-runtime` does not start a host; "proven by real-process tests" means proven headless on Linux with the null plan.
- **Published protocols implemented to the byte against a fake peer.** PJLink follows the JBMIA spec with MD5 auth and error words, was extended once and never reworked (6 commits since round 31). The Companion module's `lines.txt` contract makes the desk's parser accept every action the module can emit. Correction: do not claim the research-first integrations "have the lowest churn in the repo"; the Companion module files sit in the top decile of churn after round 54, but the churn is additive (each desk verb ships a module minor), which is a different and weaker point.
- **The evidence doctrine and the critique method.** "Attempts are not facts" (round 43, ADR-001, ADR-015), the per-round REVIEW with its "found on the way" list and honest limits, and the claim-by-claim assessment of handed-in critiques at a named line (round 72 found all thirteen real). Corrections: item-by-item answers to the maintainer's reports exist from rounds 9 and 10; round 56 is only the first named `.md` brief and the named-line verdict table is round 72. And note the receipt ladder (`Sent/Delivered/Accepted/Observed`) exists only for device sends and twin handover; `ShowActions` journals a four-value status with no observed rung (see section 4, item 4).

**Observation (lens reports, not yet through the skeptics)**

- The pure-record core of the take path (`TakePlan`, `TakeTicket` in Core, testable without a desk), the published snapshot that throws on any setter, the fail-visible render path (black without a fence seat, last-good redraw with a rate-limited log), and "every exception to a rule carries its reason" (all 27 pragma sites do; `async void` is at zero).
- The measurement-first fixes that worked: publish-cost section sharing, `FrameInput` once per frame, the tight-bound allocation test, the census with a heap-slope gate, "-1 means not measured", the audio endpoint catalogue.
- The fail-closed authority doctrine of the twin and its pure-Core types; one restore path shared by restart, takeover and handover; one outputs hold every role honours.
- The audience-port pattern: a separate listener, a route allowlist that 404s both ways, per-address budgets, a fuzz test with hostile paths. The twin's key lifecycle (desk-generated, proved by HMAC over nonces, never on the wire) as the template for every credential.
- The assistant's "no hands" fence: proposals the desk applies through the same Core paths, a secret-free brief with negative tests, the catalogue and schema generated from the desk's own tables.

---

## 4. What I would do differently, ranked

Ranking weighs verified impact after the skeptics' downgrades, then how early it was knowable, then cost. Where the cost skeptic downgraded an item, the downgrade is stated.

### 1. Put the show laptop in the loop from round 9, and make its files the gate

**Verified** (four lenses converged on this: scope, testing, platform hygiene, AI-collaboration; all four forms survived, each with the same rescoping).

*What happened.* CI had a `windows-latest` publish job from the first commit, but no Windows machine ever ran a test until round 64, and that lane runs on a Hyper-V runner with no GPU, no audio endpoint and one 1024x768 path reported at 1 Hz. The maintainer had the show laptop from 08-30 and ran builds on it: a field-feedback round on day 2, a 70-minute watchdog log with two access violations at round 14, a rig day at round 17, verbal notes at rounds 25, 28, 55, 62 and 63. What never happened until round 71 was the desk's own files coming back with the report, and what never happened at all was a scripted walk with a result column: 19 `CHECKLIST-roundNN.md` files are Do/Expect tables with no result column, `QUALIFICATION.md` has 21 matrices and 0 filled rows, `SOAK.md` and `DRILL.md` open with "nothing below is a claim". When the files did arrive, each read found five to eleven real faults: the per-second WASAPI enumeration on the UI thread (introduced `c59d659`, round 9, 09-04; removed `8794153`, round 71, 09-16: twelve days and about sixty rounds, invisible because `CaptureDevices()` is empty off Windows), the eleven TAKEs journaled Done with the outputs off (rule from round 67, found round 78), the render clock at 16.5 Hz on a 50 Hz TV, the WebView2 screencast refused with `E_INVALIDARG` at every rung, a policy registry key the managed laptop denied (first seen in a round-71 log and left).

*What instead.* Not a walk per round: at 7 to 11 rounds a day (09-13 to 09-16) a per-round gate is 30 to 40 maintainer hours and blocks the agent. The skeptics' rescoping, which I adopt:

- The near-zero-cost non-negotiable: **every report from the desk attaches `patterns.supercheck.txt`, `patterns.log`, `showlog.jsonl` and the recovery record**, committed under `docs/field/round-NN/`, and the agent opens the next round with "what the files said" before any feature ask. Round 71 shows the yield of one such read.
- **One scripted 15 to 30 minute walk per calendar day the maintainer picks up a build** (about 14 walks from 09-05 to 09-18, 7 to 10 hours in total), plus a mandatory walk after any round that touches a platform path: display topology or hot-plug, audio endpoints, WebView2, process, watchdog or twin, registry, CUT/TAKE semantics. The script: outputs off then on; a Fractal on the desk; a web page and a YouTube page on the TV; TAKE the same tile three times; RESTART with outputs live; unplug and replug the TV. Delete the checklists that script replaces; write matrices only for what it cannot reach (two machines, a capture card), and only once a row can be filled.
- **A rule that no new rail item, node or module starts while the newest qualification row is blank.** Where no rig is available for a week, the round is a hardening or docs round.
- Build `--verify-runtime` at the first native, not round 64, and put the Windows CI lane in on day 1 as what it can honestly be: compile, the non-App suites, the runtime check. Do not expect it to catch rig truths; its documented catch at round 64 was one assembly-load regression.

*Why it matters.* Every time real files arrived they found a class of defect the suite could not represent. The intended effect is a sharply lower round count and breadth capped at what the rig had proven, and that price should be stated as the point. The counter-evidence is also on the record: the round-60 menus regression fell on the day the maintainer ran the build, and rounds 60 to 62 followed in one day.

*What the skeptics corrected.* The tick defect dates from round 9 (`c59d659`), not round 21 or `a308218`. Field evidence was not absent between 09-10 and 09-17; it was verbal and unscripted. The round-62 leak would not have been caught on the laptop (an idle pipeline the operator would not see). The Windows lane "from day 1" existed as a publish job; the missing thing was the test and smoke lane.

### 2. Defer the twin, nodes, arcade and audience phones until one machine had a qualified row

**Verified** (scope-trajectory R2; the cost skeptic rated it medium, and I keep it at 2 because it is the scope half of item 1).

*What happened.* One quoted idea ("consider simple old-school mini games, like Pong") became a 346-line assessment and four rounds totalling 11,180 lines on 2026-09-13. Twin failover was built across nine rounds (31, 32, 34, 39.H2, 43, 44, 45, 49, 51) with a 22-scenario drill whose record template is empty and which ends by saying its redundancy claims are about the simulated room the tests build. Servicing this layer then consumed rounds 39 (audience listener budgets, parser fuzzing), 40.3, 41, 42, 48, 64.7 and a P0 at 65.1 (the arcade tab built on every node). About 13,400 source lines (twin 4,356; nodes 3,326; arcade 3,257; audience 2,472) and 158 test methods sit behind paths no second real machine has run; no automated test starts a second Patterns process (one real side against a fake peer on loopback, a fake child process, a fake lease). The maintainer's round-56 brief conceded the ordering.

*What instead.* Answer the round-31 and round-35 asks with the assessment documents alone and a stated gate: no second machine or node until `QUALIFICATION` §1 to §6 have a passing row on one machine. The maintainer's own preferred shape for games was "a separate exe" (PLAN §52); taken literally, as a separate repository over the wire, the desk would have carried none of the audience listener, the arcade frame ring, the `NodeHost` or the role-specific composition. Twin failover should have waited for the first single-machine soak.

*What the skeptics corrected.* Drop mesh warp and blend from the deferred scope: they were in the v1 brief's blended-projection scope and are single-machine features. Camera calibration was a direct maintainer instruction at rounds 32 and 33; whether it belongs in the deferral is arguable. Do **not** claim the round-62 test-host death was the cost of composing these modules: PLAN §80.10 names the root as the RUN monitor tile's pipeline held by `FrameBudgets`, a round-62 feature. The true and weaker point is that `AppServices.cs` lines 606 to 614 build `TwinService`, `ArcadeService`, `PlayService` and the nodes on every desk boot. The correct figure for the days is 33 of 89 commits and 45% of insertions on 09-12 to 09-14, not all 89.

### 3. Give the on-air programme its own nominal type by day 3 or 4

**Verified** (architecture R1; the cost skeptic refuted the "high" rating and dated the class to two incidents; it survives on evidence and counterfactual, both of which rescoped it as below).

*What happened.* The sandbox arrived on day 2 (`d2d1fd9`) as `_program = JsonUtil.Clone(_services.State)`: a second mutable `ShowState`. The air is `AirState => Sandbox.ProgramState ?? State` (`AppServices.cs:885`). Every service, page, menu, wire row and rule that reads `State` compiles whether it meant the edited show or the programme. The readers-of-the-wrong-state class: watchdog recovery restoring the air look into the preview (`35b0945`, 09-01, 48 lines) and round 72.2, where every reader of audio-follow (graph, STATE, Eye, pages, menus, brief, verbs) was passing the editable state so an EDIT SAFE edit moved the room's sound (219 lines, one sub-round). Round 72.4 (the frozen programme staying the previous show's after a load) is a lifecycle omission, and round 78.1 (eleven TAKEs) is a semantic PVW rule that needs both states plus the editing target; no parameter type catches those on its own, though the type makes the rule's inputs explicit.

*What instead.* A sealed wrapper over the frozen `ShowState` exposing read-only views, introduced on day 3 or 4 when `527b66a` and `35b0945` first showed readers reading the wrong state. Every picture-derived function (`LookService.Shown`, `AudioRouting.SourceOfScreen`, `ContentTargets.UsesOwnPattern`, the PVW rule, the recovery record, the Eye, STATE) takes the programme type, so passing the edited document is a compile error rather than a round-72 audit. `IsStaged`, `Pending` and `EffectOf` become pure functions of (document, programme), which is what round 78 eventually wrote.

*Not* an immutable record graph: 72 `EditAir` sites, `PublishBoth`'s blackout write, cues, stingers, lower thirds, SEND and recovery all mutate the programme by design, and round 34's `ChangeTracker`-based recovery record depends on it. The render path already holds the immutable copy. (The full "immutable model" version of this idea was refuted; see section 6.)

*Why it matters.* Fences could not catch this class because it is a semantic error, not a dependency error; only the type system could. Under this project's constraints it was free: the agent writes a second type as cheaply as a clone call, and no Windows machine was needed.

### 4. A receipt, not a status, from the first action layer

**Fact checked, recommendation pending.** The fact is verified: `ActionResult` is `(Status, string Message)` (`ShowAction.cs:588-599`); the journal writes `Status.ToString()` and words; after the round-78 fix a wall TAKE still returns `Done` with the outputs off (`TakeTruthAppTests.cs:245`) while the tile path refuses the same no-op; 21 of the 70 asserts in `TakeTruthAppTests` read message substrings; the `Sent/Delivered/Accepted/Observed` ladder exists only for device sends and twin handover (7 files). Five lenses (state model, defect archaeology, code-quality sample, release operations, original product core) independently recommended an observed-effect field on `ActionResult` from `0c2c219` (09-03), and the skeptics of the two verified "keep" items named it as the one substantive delta a rebuild should make. Those five direct recommendations have not yet had two verdicts each.

*What happened.* The defect archaeology lens catalogued 183 distinct defects from the pre-release review to round 78.4; the two largest classes were "attempts reported as facts" (33) and "two copies of the truth" (25), both visible on days 2 to 4 and both recurring to the last commit. The doctrine was written at round 43 and re-applied at 72, 75 and 78 as appended sentences and per-round stories, because the type never carried the effect.

*What instead.* From the first action layer, an `ActionResult` that carries what changed (which sections, which outputs, whether the room could see it, the new version) alongside the status, journaled as fields, tested as fields. The "suite-wide invariant over preconditions" version of this was refuted (a TAKE with the outputs off is meant to stage and show at OUTPUTS ON, so `Version-or-Refused` is not the invariant); the receipt version is narrower and is what the take resolver's `Refusal` already half-does.

### 5. Analyzers, the banned-API list and a foreign-culture test run at round 9 to 12

**Verified** (scope R5 and testing R4 survived; the "day 1, all of it" version from platform hygiene was refuted; the cost skeptics put the honest impact at low to medium).

*What happened.* `TreatWarningsAsErrors` was on from day 1; nothing else about analysis was. Round 70 added SonarAnalyzer, BannedApiAnalyzers with `BannedSymbols.txt` (`Thread.Sleep`, `GC.Collect`, `Task.Wait`, `Environment.TickCount`) and the threading analyzers, generated a 361-line `.editorconfig` from a 188-line Python classifier over ~175 rules, and fixed in one pass 145 culture-sensitive sites, 39 undisposed fields, 55 P/Invokes without a DLL search path, 26 fire-and-forget tasks and 29 regexes without timeouts.

*What instead.* The same `Directory.Build.props` at round 9 to 12 (09-04 and 09-05), when Core's `Services` folder outgrew `Rendering`, the first P/Invokes entered with the fractals and the first permanent-install and wire code landed. Fix a handful of findings per round instead of 493 at once. Add a CI leg that runs the Core and App suites under `de_DE` with invariant globalisation off.

*What the skeptics corrected, and why the impact is modest.* The retrofit cost about 3.5 hours by commit timestamps (round 59 ~2 h, round 70 ~1.3 h), not two rounds. `ControlProtocol.cs` used `InvariantCulture` for every number from its first commit, so "the wire could format 0,5 on a German desk" is false; the 145 sites were diagnostic text, on-screen clock strings and culture-sensitive `StartsWith` in routing. The disposal findings are not the round-62 or round-64 leaks (those were a static registry root and unowned timers). And "all on, day 1" is not free either: ADR-013 records 7,359 SDK findings with every rule on. PLAN §9 explicitly considered and declined a separate assembly at round 9 ("one build, one truth"), so the split was a conscious decision revisited at round 59, not an oversight. The `Patterns.Core` to `Patterns.Rendering` seam belongs at the same round-9-to-12 point.

### 6. A lifecycle census in `TestApp` from the first shared boot helper (09-03)

**Verified** (architecture R5 survived on evidence and cost after both rescoped it hard; the counterfactual refuted the original "own every registry from the kernel" form).

*What happened.* Round 62: the App host was killed three times at ~500 of 683 tests near 13.5 GB, 40 MB rooted per closed desk, root the static `FrameBudgets` registry holding the RUN monitor tile's pipeline. Round 64: a heap-dump dig found more static roots, 28 unowned timers and a twin beat reopening a closed desk; the answers were `DeskTimers`, `DeskCensus`, late-close detection in `TestApp` and seven `ForTests` hooks.

*What instead.* From `0c2c219`, where `TestApp.Boot` was centralised: keep a weak list of desks built and assert, after a forced full collection, that a closed desk is collectable; name every `DispatcherTimer` at creation through an owned factory so the census is a listing; fail on a non-zero reading, so the first leak is a one-file diff rather than a 683-test heap dump.

*What the skeptics corrected.* Do not recommend removing process-wide registries: the round-59 precedent kept static slots, the instance design cannot reach Avalonia-constructed objects, and the accumulating `FrameBudgets` column is what named the round-62 leak. The twin-listener fault was an instance `_disposed` guard, not a static. The cost actually paid was about half a day by commit timestamps. "92 static mutable collections" is wrong: a fields-only census finds 2 non-readonly collection fields. Impact: low as a saving, kept in the list because it is a one-file habit with a known payoff.

### 7. A threading contract with a counted seam test, at round 14 and round 32

**Verified** (platform hygiene R2 survived on evidence and cost; the counterfactual refuted the day-1 "ADR-000 plus tick budget assertion" form).

*What happened.* Day-1 PLAN §3 did name the UI thread, the render threads, the NDI thread and the snapshot-per-sink rule. What was never written was the worker post-back rule, the persistence lane, and "no I/O, serialisation, COM or device enumeration on the tick"; the section was never updated. The UI-dispatcher seam was touched at rounds 30, 31, 39, 59 and 64 (PLAN §57.5 records round 30's rule being written in a review "and five rounds of new workers did not" follow it), and synchronous work on the tick was found at round 29 (disk listing and autosave write), round 56 (three serialisations per GO) and round 71 (the COM enumeration).

*What instead.* Capture the UI dispatcher in one seam at round 14 rather than round 39 (about 40 lines; 21 `Dispatcher.UIThread` call sites at `a308218`). When displays became a notification-fed catalogue at round 32, apply the same rule to the audio endpoints whose per-tick enumeration had entered three days earlier: ADR-014 as the round-32 rule, not the round-71 answer for one device list. Hold it with the count-based seam test round 71 actually wrote ("N ticks touch the machine zero times"), not a wall-clock tick budget: the project's own round-16 note about flakes on a slow Windows runner and `DeskPollTests`' `WorstMs >= 0` assertion show why, and the Windows runner has no audio endpoints so a tick bound there would not have moved.

*What the skeptics corrected.* H5 took 11 minutes and 142 lines, not a day. Round 45 did not touch the seam. Impact: medium.

### 8. Show-file fixtures and a newer-schema rule

**Verified** (persistence P1 survived 3 of 3 with a rescoping; P5 survived 2 of 3 after heavy rescoping; P3 "keep" verified).

*What happened.* Ten schema bumps (v2 on 08-30 to v11 on 09-16) were written without one captured fixture: tests build `new ShowState { SchemaVersion = N }` from the current model, and one test asserts the literal current version so it was edited on every bump. `Migrate` stamps the build's version unconditionally and nothing checks `SchemaVersion > CurrentSchemaVersion`, so a rolled-back build opens a newer show, silently drops unknown members and rewrites the file on the first autosave. README lines 1737 to 1738 describe the load side of that rule, not the drop-on-save that PLAN line 1194 admits. The recovery record's `Air` (a whole `ShowState` since `63d9ae1`, round 22, 09-10) is restored without `Migrate`. Looks are an opaque `LookData` JSON string inside the show that migrations skip on purpose; no property inside it has been renamed yet, so nothing has aged, but a rename would silently drop values with no fixture to catch it.

*What instead.* Two parts of different priority. Must-do and cheap, at round 61 beside the rollback tooling: a `SchemaVersion > CurrentSchemaVersion` rule (open read-only with a banner, or refuse to autosave over it), about 30 lines and one test, plus one README sentence in the rollback section saying the folder is not covered by the exe rollback. Nice-to-have, from the first bump after a second real machine exists: commit the JSON the previous build actually wrote under `tests/fixtures/schema-N/` and load every fixture through `SettingsStore.LoadFrom` in one theory; call `SettingsStore.Migrate` on `RecoverySnapshot.Air` when read; one captured-fixture test that recalls a look saved by an early round on the current desk. Do not "refuse a newer recovery record" (it breaks the Supervisor's automatic rollback in the proving period) and do not store looks as typed objects in the frozen model (review H3 kept them out deliberately).

### 9. A build identity from git, and round-scoped commits and changelog from round 9

**Fact checked, recommendation pending** for the build identity; **verified** for the commit and changelog discipline (process P2, cost skeptic refuting at "low").

*What happened.* `Directory.Build.props` has carried `<Version>1.0.0</Version>` since `c584687` and no commit changed it. The log banner, the Admin page's "Build:" line, the STATE wire, the Super Check, the Eye, the mDNS beacon and the management check-in all read `Assembly.GetName().Version`, so every build ever made says 1.0.0, including the ones whose logs were read at rounds 71 to 78. Rounds became the real version scheme at round 61: a 63-row SHA table hand-extended in every later papers commit, round boundaries before 39 read from dates because about 180 of 364 subjects do not name their round, and a hard-coded belief (in `CHANGELOG.md:838`, `ChangelogTests.cs:53` and `tag-rounds.sh:2`) that history begins 2026-09-06, which this clone contradicts with 92 earlier commits from root `227f628`. Commit subjects average 199 characters, maximum 2,873; rounds 73 and 74 interleave.

*What instead.* Stamp the version from git (tag or `describe`) into the exe on day 1 and make every surface read it; a release-ops skeptic confirmed the facts and did not refute the recommendation, but it has one verdict so far. From round 9, when rounds were first numbered in the tree (`5f1b56b`, 09-04): `Round NN.k:` at the head of every subject with the narrative in the body (the agent self-adopted this at round 40, so the gap is rounds 16 to 39), a CHANGELOG entry at each round close with the `ChangelogTests` fence from then, and a CI-made tag per closed round rather than a session-made one (the session credential could not push tags). Drop the "branch per round merged `--no-ff`" idea: the default branch sat at round 28 through round 78, so nobody was positioned to merge per round.

### 10. Split the agent's working memory from the papers people read

**Verified** (process P1 3 of 3 with impact lowered to medium; scope R6 3 of 3 with impact lowered to low; both say the harm is readability, not cost).

*What happened.* There is no `CLAUDE.md`, `AGENTS.md`, glossary or docs index. `docs/PLAN.md` (10,386 lines, 1 MB, 185 commits, sections numbered 1 to 8, 39, 40, 37, 38, 35 ...) is simultaneously the design paper, the round-by-round record the agent re-reads, and a narrative that claims at line 7 to be "written before implementation" although the round sections are post-hoc (round 65 touched PLAN only in its 13th, "papers" commit). README's "What it does" is a 1,680-line newest-first ledger with 194 commits, the most-churned file in the repository, with the Quick start at line 1704. The dedicated "papers" commit convention began at round 56 and ran 159 to 774 insertions per round after that.

*What instead.* Day 1: a short repo-root agent brief (constraints, the doctrine, the glossary, where each artefact lives, how a round closes). Per round: one append-only `docs/rounds/NN.md` holding the ask, the design committed before the code, what landed and "found on the way". PLAN frozen as the v1 brief plus a two-to-three page `ARCHITECTURE.md` that is rewritten, not appended, whenever MODULES or ADR change. README limited to what the product does now, quick start and links; the ledger moves to CHANGELOG. ADRs from the first structural decision (day 1's "one engine, many sinks" and "Core has no UI" were already ADRs).

*What the skeptics corrected.* Do not count the round-61 back-fill or the false "history begins 09-06" belief here; they belong to item 9. The PLAN's `§` numbers do work as a stable index (304 references resolve). README's architecture section was rewritten for rounds 40, 41, 59 and 65; exactly one sentence from round 13 and one from round 61 are stale. The measured retrofit at rounds 61 and 65 is about 700 lines and an hour. Round 61 was ~43 minutes. The harm is that a newcomer cannot read the design document in order and that the product is hidden under the diary.

### 11. Windows runner tests and a per-native runtime probe from the first native commit

**Verified** (testing R1 survived on evidence and counterfactual with the cost skeptic refuting the "whole App suite on Windows" scope; portability R2 survived 3 of 3 with dates moved).

*What happened.* The `windows-latest` job restored and built from day 1 but executed no tests for 64 rounds; the first Windows test run (round 64) immediately found a platform-newline fault and a test-premise fault; at HEAD the smoke lane runs 32 of 779 App test methods. 72 `OperatingSystem.IsWindows()` branches and 12 P/Invoke files in `src` were never executed by any test. `RuntimeCheck` (round 64) probes only Skia, libVLC and WASAPI endpoints; no test ever constructs a `CoreWebView2`; the screencast added at round 55 was refused with `E_INVALIDARG` on the only field machine from its first ask to the last log, and three consecutive reviews (56, 57, 58) recorded that the Windows and WebView2 side had not run.

*What instead.* From `c59d659` (09-04), when WASAPI and 26 P/Invokes entered: `dotnet test` on the existing `windows-latest` job for the non-App suites plus a small App smoke set, and `--verify-runtime` on the built exe. From `e775842` (09-05, the first WebView2 and PDFtoImage commit): a probe per native in that check (libVLC makes an instance and decodes one generated frame; WebView2 creates an environment in a hidden HWND, navigates to a `data:` URL, calls `Page.startScreencast` and records the answer and the runtime version; PDFium renders a page; NDI reports present or absent), with the screencast probe landing in the same commit as the screencast (`0d1f3da`, 55.1). The report is the artefact the field is asked to attach.

*What the skeptics corrected.* Do not run the whole App suite on Windows (unproven headless there, 15 to 25 minutes per push, and it forces a "Windows without hardware" state into every Windows-only test). Do not claim it would have found the round-71 tick or the culture class: the runner has no audio endpoints and an en-US default. Say the `E_INVALIDARG` "could have been answered one way or the other" on 09-14, not "would have been visible": that a `windows-latest` runner reproduces the refusal is a hypothesis. The claim that 15 tests return early off Windows is false (zero do).

### 12. Ship the encoder host's lesson to the packaging: one post-publish target and a createdump check

**Verified** (portability R3 and R4 survived 2 of 3 each; every skeptic put them at low impact, and the counterfactual rescoped R4 to what follows).

*What happened.* README lines 18 and 19 promise "no install, no admin rights, no registry"; `GpuService` has written `HKCU\...\DirectX\UserGpuPreferences` since 08-31 and the show lock has written HKCU sound and notification keys since 09-12, one under `Software\Policies` that the field laptop denied (first seen at round 71 and left; scheduled for a fallback at round 78). Settings fall back silently to `%LOCALAPPDATA%\Patterns` on a read-only folder; the single-file host extracts natives to a per-version temp folder no document names; `createdump.exe` is copied beside the exe by `build/publish-win-x64.sh` only, not by the `.cmd`, the full scripts or CI, so Releases can ship without it and a native crash then leaves no dump.

*What instead.* Move the `WebView2Loader.dll` and `createdump.exe` placement out of four scripts into one post-publish target in `Patterns.App.csproj`; add a createdump-present check and the WebView2 probe to `--verify-runtime`; add a footprint line to the Super Check listing every path and registry key the build can touch, with whether it was permitted, held by a regex fence over `Registry.`, `GetFolderPath` and `GetTempPath` sites on the `ArchitectureFenceTests` pattern; record exe size and cold-start time in `manifest.json` per release. Defer the folder-versus-single-file question until a cold start has been measured on the show laptop.

### 13. Close the parser's fail-open tail, and generate only when a third mirror exists

**Verified in one form** (architecture R2, 2 of 3 with the cost skeptic refuting at "low"); **refuted in three others** (the round-16 and day-2 "generate everything from the table" forms; see section 6).

*What happened.* Round 16 unified the *kind* (`ShowActionKind`) and built `ActionSpec` as the one table, but the *spelling* stayed hand-maintained: 230 `case "..."` literals in `ControlProtocol.cs` (which references `ActionSpec` zero times), a 245-row hand table in `WireVocabularyTests`, 86 rows in `OscMap`, a 729-line Companion `actions.js`, 65 `Wire =` strings in `DeskMenus`, and a 109 KB REMOTE.md with 97 commits and no test. The round-16 wire commit `82e4ac2` introduced the `_ => ...Toggle` fall-through that round 72.5 later fixed in six places, and the fail-open default is still live at HEAD in `ControlProtocol.cs:838-860` (CLOCK SECONDS, MESSAGE SCROLL, TICKER) and `OscMap.cs:626-638` (any unknown word becomes TOGGLE for blackout, duck, review, freeze, clock, message, logo, pip and weather).

*What instead.* The defensible kernel is small: a table-driven tokenizer so an unknown trailing word is refused by construction, at round 16 when the parser was rewritten anyway. The full generator (parser, writer, OSC map, Companion definitions, help rows and REMOTE.md from one table) becomes worth it when a third hand-mirror exists, which is round 54 (the module's `lines.txt`), not day 2 or round 16: at round 16 most of what it would generate did not exist, the retrofit that did happen was two commits on one afternoon, and the only measured consequence of not generating is round 72.5 (+69/-14 across 3 files).

### 14. Smaller, verified items

- **Define "a live output" once in Core, read from the air state, beside the round-55 audio matrix.** Verified (peers R4, 2 of 3; counterfactual refuted the "adopt vMix's model whole" form). "What is heard where" was answered at rounds 26, 55, 69.5, 72.2, 76.3, 77.3 and 78.3; the round-55 research paper had the rule (audio follows video by default) and the code converged on it over five rounds. Round 26 is not a valid target (the brief, NDI audio and per-device outputs did not exist). Low impact.
- **Ship the screencast behind a probe and a bench at round 55, when a field tester was evidently available.** Verified (platform R5, 2 of 3; counterfactual refuted "make the native path the default"). Screencast-specific code is about 800 lines across rounds 55, 58, 68 and 77 on a path the field never saw work; the round-68 smoother and pooled decode also serve the poll path, so they were not wasted; the yt-dlp to libVLC path was itself unbenched and present unused in every build from round 68. Low impact.
- **A page-name lint at round 8.** Verified (operator UX R1, 2 of 3; cost refuted the "twelve-noun concept table on day 1" form). "Admin → Switcher" was coined 31 Aug, carried unchanged through the 5 Sep help rewrite two days after the page became Machine, and is live at `HelpBodies.cs:39` and `README.md:1616`; "the Outputs page" was written into right-click menus at round 67 for a header gone since round 8; README says eighteen pages against 29. The knowable-at-the-time nouns to fix were Screen vs Output vs Display, Canvas vs Group, Programme vs Program (57 and 58 spellings in the help today). The cheap part: put `Shell.Pages` in Core at `d0aa264` (3 Sep) and add a test that every page name in help, menu detail, README and REMOTE prose is a current header. Low impact.
- **One clause in README's on-disk paragraph** saying that an older build's next autosave drops what it does not know, and a one-page `FILES.md` for the nine sidecars no operator document names. Verified (persistence P6, 2 of 3), low impact; the round-78 recovery-record fault was found by reading code, not by an operator reading files.

---

## 5. Looks odd, but I would keep it

- **No dependency-injection container; a hand-built composition root with a static `Instance`.** ADR-012 held. The fence at round 69 brought `AppServices.Instance` down to 7 reaching files. The "kernel plus capabilities from day 1" alternative was refuted: the retrofits it would have avoided are about ten mechanical commits of 364, and the churn hotspots it blames on the shape have other causes.
- **JSON-clone snapshots and a mutable observable state model.** The immutable-record alternative was refuted on cost: 72 in-place mutation sites of the programme, `WatchAir`'s change tracker and the per-root memo all depend on the mutable model, and the render path already holds an immutable published copy that throws on any setter. Fix the two-truths problem with a nominal type (item 3), not with a new value model.
- **A serial, one-process, whole-desk headless suite.** It is slow (8m54s for the App suite) and it killed its host once, but the 683 real boots per run are what exposed product leaks, and the census made the cost visible. The "split desk-boot tests from service tests" idea is unverified.
- **The child-process host only for the encoder.** Hosting the libVLC decoders or WebView2 out of process "from the day they arrived" was refuted twice: the round-14 access violations were traced at round 17 to the desk's own Skia sink lifecycle with no decoder running, the decoder host's cost is under-counted by calling it "the same ring with the roles swapped", and ADR-010's trigger (wait for a stream fault on a real rig) was never met.
- **Help prose as C# string constants.** Odd, but it is fenced (page tables pinned by tests, laptop fit test) and the generated-skeleton alternative is unverified. The verified defect is only the stale page names (item 14).
- **A hand-written parser at round 16.** See item 13: at that point there was nothing to generate from and the retrofit was cheap.

---

## 6. Sounds right, but the record refutes it

Each of these was a recommendation from a lens that two or three skeptics refuted. They are the things a reader of the repo would be tempted to say, and should not.

| Recommendation | Why it does not hold |
|---|---|
| Make `ActionSpec` the single schema and generate wire, OSC, Companion, help and REMOTE.md from it at round 16 | Most of the generated surfaces did not exist at round 16; the two headline defects were small single-commit fixes; the churn arithmetic does not survive inspection. |
| Start with the kernel-plus-capabilities shape and page-owned view models from day 1 | The retrofits it would avoid are ~10 mechanical commits (~3%); `a308218` was a verbatim split; the causal claims about churn hotspots fail. |
| Model the show as immutable records with structural equality | The "free" premise is false: 72 in-place programme mutation sites, `ChangeTracker`-based recovery, the per-root memo; the published snapshot is already immutable. |
| One suite-wide "attempts are not facts" invariant test over preconditions | A TAKE with the outputs off is meant to stage; `Version-or-Refused` is not the invariant; it would not have failed `82134a3` on its day. |
| Ship round 70's analyzer props on day 1 | Generic and low impact: the retrofit was ~1.3 hours; 7,359 findings with every rule on; PLAN §9 declined a split with reasons. (The round-9-to-12 version survives, item 5.) |
| Host the libVLC decoders before the encoder at round 16 | The round-14 access violations were attributed at round 17 to the desk's own sink lifecycle, no decoder running; never recurred. |
| Model the switcher as Event Master-style armed destinations from the first CUT/TAKE | Everything the shape supplies was already in the build the field ran (`TakePlan` at round 67); the four convergence commits after it are about one round-equivalent. |
| One action table generating all spellings on day 2 | On day 2 the wire had 20 kinds in a 135-line parser and `ShowActionKind` did not exist; the round-16 merge was two commits on one afternoon. |
| Build the Companion module on the current module base before the first key | The premise ("Companion 5 could not load the base-1.x module") is false: Companion's own check at v5.0.5 accepts `1 - 1.x` and bundles node18. The round-54 rewrite was driven by an unverified claim. |
| Do the peer study for the four core models on day 1 | On 08-29 there was nothing to study against; the pivot to a desk came on day 2. The "papers before code predicts low churn" claim does not hold in git. |
| Host libVLC decode and WebView2 capture out of process from the day they arrived | Same round-17 diagnosis as above; the WebView2 refusal is behavioural, not a crash; cost materially understated. |
| One sidecar store type with result-returning reads from the second sidecar | The recovery record's lifecycle churn (re-cut at 22, 43, 56, 65, 76, 78) is about ownership semantics, which a shared store type does not decide. |
| Introduce the coalesced persistence lane at the first background write (08-30) | On 08-30 autosave was already a 900 ms debounce and there was no second writer to order; the dating and the attributed cost are wrong. |

---

## 7. Directing the agent next time

The repository is a record of one maintainer directing an AI agent through 78 rounds. The AI-collaboration lens classified the asks into five kinds and measured which paid best by what later rounds had to undo. Only its first recommendation has been verified so far (item 1 above); the rest is presented as observation.

**What paid best per round (observation).**

- The single-cause field report with the desk's files: round 71 removed a UI-thread COM enumeration that had survived every review since 09-04.
- The whole-of-system question with a measurable answer: round 70's "could analyzers improve Patterns?" fixed 145 + 39 + 55 + 26 + 29 sites in one round; rounds 59 and 69's module and fence questions produced tests that still stand.
- The handed-in critique checked claim by claim at a named line (rounds 56, 58, 65, 72, 75, 78).

**What produced the longest tails (observation).**

- The two big feature days. 09-03's six Run-mode phases created two action vocabularies the same day, merged by a 73-file round 16 after the maintainer wrote "the problem is still there", and left a GO record serialised three times on the UI thread until round 56. 09-05's +58K-line day was followed by rounds 14 to 16 of crash and refactor work and a view-model peel asked for four times.
- Numbered feature lists with no gate between items (rounds 9 to 13, 17, 19, 62, 63, 73).

**What I would ask for, in order (my synthesis of the verified items).**

1. Every desk report carries the four files; the round opens with what they said.
2. One subject per round, or at most five numbered items, and never a day that opens more than one subsystem. (Observation, unverified; consistent with the measured tails.)
3. Each review finding, the agent's own or a handed-in critique's, gets a reproducing failing test or a measurement before its fix. The repo did this at its best moments (round 72, round 78) and not at its worst (round 77's screencast retry ladder built on a parameters hypothesis).
4. The papers are for people; the agent's memory is a short brief and per-round files. Commit the ask and the design before the code and label the rest as the record.
5. No new subsystem while the newest qualification row is blank.

---

## 8. Day-1 checklist for a rebuild

Things that cost nearly nothing on day 1 and were paid for later. Each is either verified above or is a "keep" whose skeptics named the missing piece.

- [ ] Version stamped from git into the exe; every surface reads it. (item 9)
- [ ] `--verify-runtime` exists from the first native and grows a line per native; `createdump` placement in one msbuild target; a footprint line in the Super Check. (items 11, 12)
- [ ] The `windows-latest` job runs the non-App suites and the runtime check, and says in the workflow what it cannot see. (item 11)
- [ ] `docs/field/` exists and is empty until the first report; the report template names the four files. (item 1)
- [ ] A repo-root agent brief, `docs/rounds/`, `CHANGELOG.md` in keep-a-changelog shape with its fence, `docs/adr/` with the day-1 decisions. (items 9, 10)
- [ ] `Round NN.k:` subjects, narrative in the body, from the first numbered round. (item 9)
- [ ] `TestApp.Boot` with a census that fails on a non-zero reading, and a named-timer factory, from the first shared boot helper. (item 6)
- [ ] The UI dispatcher captured in one seam before the first worker. (item 7)
- [ ] The assistant's model id as a setting with a compiled default. (section 3)
- [ ] By day 3 or 4, when the sandbox appears: a sealed programme type, and every picture-derived function takes it. (item 3)
- [ ] With the first action layer: `ActionResult` carries the observed effect as fields; the journal and the tests read the fields. (item 4)
- [ ] At the first schema bump: a `SchemaVersion > Current` rule and the README sentence; fixtures from the first bump after a second machine exists. (item 8)
- [ ] Around round 9 to 12, when P/Invokes and services outgrow the renderer: analyzers, `BannedSymbols.txt`, the Core/Rendering seam, a `de_DE` CI leg. (item 5)
- [ ] A gate written down: no second machine, node or module until one machine has a passing qualification row. (item 2)

---

## 9. Coverage and method (interim)

| | Count |
|---|---|
| Lenses complete | 20 of 20 |
| Recommendations produced | 120 (six per lens) |
| Skeptic verdicts returned | 134 of ~360 |
| Recommendations verified (at most one refutation, at least two verdicts) | 31 |
| Recommendations refuted (two or more refutations) | 13 |
| Recommendations awaiting a second verdict | 76 |

Fully verified lenses: scope-trajectory, architecture, platform hygiene, peers and prior art, persistence, portability, testing (two pending). Partially verified: process and docs, operator surface, AI collaboration. Not yet verified: assistant, code-quality sample, defect archaeology, distributed layer, integrations and Companion, original product core, performance, release operations, security, state model. Their material appears above only as "fact checked" (where a skeptic of another item confirmed the fact) or "observation".

The unverified lenses hold several recommendations I expect to survive and would rank high if they do, in particular: the observed-effect field on `ActionResult` (five lenses), a pairing token and narrow bind from the first remote round (the control port was open on `IPAddress.Any` with no credential for 65 rounds, with the admin passcode in `?pass=` URLs until round 65, while the twin had mandatory keys from round 34), a protocol number in HELLO and STATE from the first wire commit, a byte budget and a recorded fixture for the assistant's brief (which is gathered from eleven services on the UI thread on every ask with no size bound and no test against a real reply), and "no governor without a measured cause" (about 40 budget, ladder, fence and census classes totalling ~6,900 lines, against exactly two allocation tests in the suite, the first of which found 1,616 bytes per plain frame sixteen days after the day-1 claim of an allocation-free frame path). These are stated here as observations pending verification.

The remaining skeptic checks, a second synthesis pass with independent judges, and a citation audit of every file, line and commit reference in this document are scheduled after the usage window resets. The final version will supersede this one; where it changes a conclusion, it will say so.

---

## 10. Evidence index

Commits named in this document (all in `jammin808/Patterns`):

| Commit | Date | What |
|---|---|---|
| `227f628` | 08-29 | root commit (the CHANGELOG's "history begins 09-06" is false) |
| `c584687` | 08-29 | scaffold; `Directory.Build.props` with `TreatWarningsAsErrors` and `Version 1.0.0` |
| `19e4944`, `fcf61ce`, `292029b` | 08-29 | `SettingsStore` contract; 87 tests; first CI with `windows-latest` publish |
| `0d9ccfb` | 08-30 | first "field-feedback round" |
| `d2d1fd9` | 08-30 | sandbox PGM/PVW (the frozen clone) |
| `e43e28b` | 08-30 | TCP wire, HTTP remote, Companion module (base 1.x) |
| `d9a0828` | 08-30 | schema v2 and `Migrate` |
| `35b0945` | 09-01 | first reader-of-the-wrong-state fix |
| `0c2c219` | 09-03 | Run mode phase 1: one action layer, `ActionOrigin`, journal, `TestApp.Boot`, tolerant enums |
| `d0aa264` | 09-03 | `Shell.Pages` and the page-pin test; Admin page renamed Machine |
| `c59d659` | 09-04 | fractals; per-tick `CaptureDevices()` enters the poll |
| `5f1b56b` | 09-04 | first numbered round (round 9) in PLAN |
| `e775842` | 09-05 | WebView2 pages and PDFtoImage |
| `a308218` | 09-05 | MainViewModel split verbatim into partials |
| `7ad2166` | 09-05 | the assistant (2,460 lines) |
| `26879db` | 09-05 | Super Check |
| `496120e` | 09-06 | crash note, createdump, safe run (round 14) |
| `1a0eb49`, `82e4ac2` | 09-06 | one vocabulary; parser rewrite with the `_ => Toggle` fall-through (round 16) |
| `ef67e6f` | 09-06 | encoder out of process |
| `63d9ae1` | 09-10 | recovery record carries a whole `ShowState` (round 22) |
| `3aabd81`, `40d4f9e` | 09-12 | "what a publish costs"; the round-29 sweep |
| `88c8c77` | 09-12 | first twin commit (round 31) |
| `8cf45d9` | 09-12 | show lock (HKCU writes) |
| `15d71d7`, `acdd578`, `c956c96`, `2902840`, `e737136` | 09-13 | nodes, arcade, audience, rig-day games |
| `677d554` | 09-13 | kernel and capabilities (round 39 H4) |
| `90d6a9b` | 09-13 | "attempts are not facts" (round 43) |
| `aebd42e` | 09-14 | Companion module rewrite on base 2.x |
| `8044340` | 09-14 | three serialisations per GO removed (56.6) |
| `61e4331` | 09-14 | first `ClearForTests` hook |
| `d3b8e1c` | 09-15 | assembly split (324 files) |
| `5961f46`, `cfc29ff`, `575f5b5` | 09-15 | rollback workflow, CHANGELOG back-fill, tag table (round 61) |
| `1c895ee`, `55239e1` | 09-15 | Windows smoke lane, `RuntimeCheck`, census (round 64) |
| `8c97004` | 09-16 | ADRs, one file lane (65.12) |
| `066aaf3` | 09-16 | pure take resolver (67.2) |
| `04ef8e9`, `f69b91f`, `c578ee3` | 09-16 | analyzers (round 70) |
| `8794153` | 09-16 | audio endpoint catalogue; tick enumeration removed (round 71) |
| `82134a3` | 09-16 | `SendToTargets` takes `LookService.Shown` (the round-78 fault's origin) |
| `f488ae7` | 09-17 | screencast retry ladder (77.2) |
| `e175928` | 09-18 | eleven TAKEs: the fix with `TakeTruthAppTests` (78.1) |
| `c531909` | 09-18 | second desk clearing the first's recovery record (78) |
| `08dcaaa` | 09-18 | HEAD, round 78 papers |

Files and documents most cited: `src/Patterns.App/Services/AppServices.cs` (line 885 `AirState`; 606 to 614 eager module construction), `src/Patterns.App/Services/SandboxService.cs`, `src/Patterns.Core/Model/ShowState.cs`, `src/Patterns.Core/Services/ControlProtocol.cs` (838 to 860), `src/Patterns.Core/Services/OscMap.cs` (626 to 638), `src/Patterns.Core/Services/SettingsStore.cs` (102 to 139, 143 to 274), `src/Patterns.Core/Services/Resilience.cs` (178 to 192), `src/Patterns.App/Services/WebFrameSource.cs` (184, 436), `src/Patterns.App/Services/RuntimeCheck.cs`, `tests/Patterns.App.Tests/TestApp.cs`, `tests/Patterns.App.Tests/TakeTruthAppTests.cs`, `docs/PLAN.md` (§3, §9, §12, §14.2, §22, §52, §57.5, §74.6, §79.1, §80.10, §82, §89.1, §94 to §96), `docs/REVIEW.md`, `docs/ADR.md` (001, 008, 010, 011, 012, 013, 014, 015), `docs/MODULES.md`, `docs/QUALIFICATION.md`, `docs/DRILL.md`, `docs/SOAK.md`, `docs/WEB-VIDEO.md`, `docs/AUDIO-RESEARCH.md`, `docs/ANALYSIS.md`, `CHANGELOG.md` (line 838), `README.md` (18 to 19, 1616, 1731 to 1738, 1785 to 1831), `.github/workflows/build.yml`, `.github/scripts/tag-rounds.sh`, `build/publish-win-x64.sh`.
