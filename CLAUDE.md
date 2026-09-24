# Patterns — the agent brief

Read this before any round. It is the short memory: the constraints, the standing brief, the doctrine,
where each artefact lives and how a round opens and closes. The papers people read are elsewhere
(`README.md`, `CHANGELOG.md`, `docs/`); this file is for whoever works the tree.

## What this is

A single portable Windows exe (`Patterns.exe`, .NET 10, Avalonia, SkiaSharp) that puts pixel-exact
test patterns and show content on every screen, wall and NDI receiver in a room, and runs the show
from a desk, a phone, a Stream Deck (the Bitfocus Companion module in
`integrations/companion-module-patterns`) or the wire (`docs/REMOTE.md`). One maintainer with one
show laptop; every ask so far came from that desk or that rig (`docs/field/` holds the machine's files
when a report arrives).

## The standing brief (the maintainer's, applies to every round)

Stability, resilience, efficiency, user experience, modularity, abstraction, performance adaptability
across system specs, durability, rapid show construction and an easy show workflow are paramount.
Every selection change, view update or UX update is instant, or as fast as it can be. AI integration
is considered at each step. Cutting-edge interaction with Bitfocus Companion and external controllers
is always considered. Right-click menus and the desk are always in step with each other. No legacy
or previous version is considered. Anything done is followed down every chain it affects — the
core, the desk, the UX, the menu sections, STATE, the Eye, the wire, the deck, the assistant — and
kept in sync. The closing sentence names the rig: a round with a verb an output can show closes with
one journal row, one log line or one Super Check row for that verb, read beside the output it names,
and a round without one says so in its papers.

## The doctrine

- **Attempts are not facts.** Sent → delivered → accepted → observed. `Done` is a claim until the
  fields say what the room could see (`ActionResult.Visibility`) and what changed
  (`ActionResult.Effect`); a take that would change nothing is refused with the reason. Tests assert
  fields and the published snapshot first, words never alone.
- **One air.** What the audience sees is the programme (`AirState`, the frozen clone while EDIT SAFE
  is open); the edited state is the preview. Every picture-derived reader takes the air, not the edit.
- **Every reader.** A fact that changes reaches the desk, the Run strip, the right-click menus,
  STATE, the Eye's God's Eye view, the wire's reply, the Companion module and the assistant's facts
  in the same round, or the paper says which it did not reach and why.
- **Fail closed on the wire.** An unknown word is refused, never defaulted; secrets ride in no line,
  no journal row and no feed; every listener honours the bind address or is named as an exemption;
  the health lights say what pairing does not cover.
- **Measured, then governed.** No ladder, governor or fence lands without a measured cause and a
  deterministic bounded test; removing the cost beats governing it.

## Constraints that bite

- **One `dotnet build` at a time.** Never start a build while another build or a test run that
  is not `--no-build` is in progress. A `--no-build` test run is safe beside source edits; a build
  is not.
- **The analyzers are fences** (`docs/ANALYSIS.md`, `.editorconfig`, `BannedSymbols.txt`) and they
  apply to tests too: no empty catch (S108), no dead stores (S1481, S1854), no `System.Random`
  (S2245), culture-invariant parsing and formatting, `Thread.Sleep`, `Environment.TickCount`,
  `DateTime.Now`, `GC.Collect` and `Console.Write` banned in `src/` (tests may sleep). The build is
  the run: a fenced rule fails `dotnet build`.
- **The wire's shape.** Replies are bare `OK …` / `ERR …` lines. STATE's JSON is matched by some
  tests as raw substrings, so a new field goes at the end of its row. Bare `TAKE` and `CUT` are
  deliberately not wire verbs (a wire cannot take a half-built preview). A test that connects from
  loopback sets `ControlService.TrustLoopback = false` and restores it in `finally`.
- **Comments state the invariant**; the round number goes in the commit message and the papers.
  No model or tool identifiers in anything pushed.
- **Never** skip, disable or quarantine a test to get green; never rewrite pushed history; never
  push to a branch you were not given.

## The loop

```
dotnet build Patterns.sln -c Debug -v q                       # the fences run here
dotnet test tests/Patterns.Core.Tests -c Debug --no-build --filter "FullyQualifiedName~X"
# the seven suites: Core, Rendering, Devices, Audio, Assistant, Audience, App (headless Avalonia)
cd integrations/companion-module-patterns && npm test         # the module's node tests
bash .github/scripts/tag-rounds.sh                            # the maintainer's one command: the tags
build/publish-win-x64.cmd | build/publish-win-x64-full.cmd    # the portable exe (with libVLC)
```

CI (`.github/workflows/build.yml`): `test` and `lifecycle` on Ubuntu, `companion-module`,
`portable-exe` and `windows-smoke` on Windows (the suites that can run there, `--verify-runtime`,
the exe launched), `rollback-script`, and `release` on a tag once the smoke lane is green. The
Windows runner has no GPU, no audio endpoint and one display; what it cannot see is the rig's.

## Where things live

| What | Where |
|---|---|
| The show model, the vocabulary, the wire grammar, the menus, the Eye, the checks | `src/Patterns.Core` (no package references; `docs/MODULES.md` and `ModuleRulesTests` hold the module rules) |
| The Skia edge, the frame input, the render fence | `src/Patterns.Rendering` |
| The desk, its services, the action layer, the sandbox, the pages | `src/Patterns.App` (`Services/ShowActions*.cs` is the executor; `Services/SandboxService.cs` the air/preview split; `Services/CommandRouter.cs` STATE and the wire) |
| Devices, audio, NDI, the assistant, the audience room, the Windows probes | `src/Patterns.Devices`, `Patterns.Audio`, `Patterns.Ndi`, `Patterns.Assistant`, `Patterns.Audience`, `Patterns.Platform.Windows` |
| The design of each round | `docs/PLAN.md` §NN (appended; the newest section is the round's design and its "what the round did not do") |
| What each round found and fixed | `docs/REVIEW.md` (a section per round; "The review's items, answered") |
| The short history and the tag table | `CHANGELOG.md` (fenced by `ChangelogTests`; `## Round NN — date — words` headers, newest first) and `.github/scripts/tag-rounds.sh` (each round's last commit) |
| The product, as it is now | `README.md` (the ledger of what it does, the Quick start, the keys) |
| The wire, the module, the deck's Navigator | `docs/REMOTE.md`, `docs/COMPANION.md`, `docs/COMPANION-NAVIGATOR.md`; the module's own `lines.txt` is the writer's oracle |
| Decisions | `docs/ADR.md` |
| The rig record | `docs/QUALIFICATION.md` (the matrices, and §22 the walk), `docs/SOAK.md`, `docs/DRILL.md` (two-machine scenarios, unrun until a second machine is on the bench) |
| The still-open ledger | `docs/OPEN.md` — cumulative; nothing leaves it until a row names the unit that closed it |
| The machine's files per report | `docs/field/round-NN/` (`docs/field/README.md` says which four and how) |
| Research and assessments | `docs/EYE.md`, `docs/CONNECTOMICS.md`, `docs/WEB-VIDEO.md`, `docs/MEMORY-RESEARCH.md`, `docs/ROADMAP-ASSESSMENT.md`, `docs/GENERALISATION.md`, `docs/NODES.md` |

## How a round opens

1. Read the newest `docs/field/round-NN/` and write "what the files said" (the PLAN §96.1 shape:
   each row or line, what it means, what it asks) before any feature ask.
2. Read `docs/OPEN.md`; a round closes rows or adds them, and says which.
3. Name the requester and the evidence class in the PLAN section's first lines: the maintainer's
   list, a handed-in critique, a verbal report from the rig, the show laptop's files, web research.
4. Read the ask against the standing brief; items with a fence or a measurement get one, and
   "no measurement this round" is written where there is none rather than the item dropped.

## How a round closes

- **One commit per unit**, subject `Round NN.k: <what landed>` with the reasoning in the body; the
  maintainer's authorship; every unit validated (build, the suites it touches) before its commit.
- **The papers commit**: `docs/PLAN.md` §NN (the design, the proof, what the round did not do),
  `docs/REVIEW.md` round NN (found, fixed, the items answered, "Walk: …" — a walk row or
  `unwalked`), a `CHANGELOG.md` entry naming the evidence class, `README.md` where the product
  changed, `docs/REMOTE.md` / `docs/COMPANION.md` where the wire or the module changed,
  `docs/QUALIFICATION.md` rows where a platform path was touched, `docs/OPEN.md` rows closed and
  added, and the tag table row for the round before (its last commit).
- **All seven suites and the module's tests green**, then push. The maintainer runs
  `tag-rounds.sh`; CI builds and releases from the tag.
- A platform path touched (display topology, hot-plug, audio endpoints, WebView2, the process or
  watchdog, the twin, the registry, CUT/TAKE) asks for a walk (`docs/QUALIFICATION.md` §22) on the
  next build the maintainer picks up; until its row is filled the round is `unwalked`, which is a
  fact in the papers, not a block.

## House words

- **the air / the programme** — what the audience sees; **the preview / the sandbox / EDIT SAFE** —
  the edited state held apart from the air; **a take** — the preview moved to the air, on the wall
  or on one tile; **OWN** — a target with a picture of its own instead of the programme's.
- **a fence** — a test or analyzer rule that fails the build when an invariant breaks; **a lane** —
  a single-writer path (the persistence lane, the NDI sender lane); **a seam** — the interface where
  a platform or heavy implementation is swapped (the Windows probes, the renderer).
- **STATE** — the wire's JSON of the desk; **the Eye** — the God's Eye rail and page; **the wire** —
  the TCP/HTTP control protocol; **the deck** — a Companion surface; **the journal** —
  `patterns.showlog.jsonl`; **the Super Check** — `patterns.supercheck.txt`; **the recovery
  record** — `patterns.recovery.json`, what a restart puts back.
- **a round** — one request answered as a run of commits and closed with its papers; **the
  papers** — PLAN, REVIEW, CHANGELOG, README and the wire's documents.
