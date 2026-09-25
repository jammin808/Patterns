# The connectomics transfer — what Patterns took from a brain map

*Round 84. Requester: the maintainer — "Could Patterns take anything useful from this?", with the Google
Research post "A connectomics milestone: mapping the complete male fruit fly brain". Evidence class: web
research at the maintainer's ask, assessed in the session that opened the round, and the maintainer's
"sounds good, go ahead" on the assessment's proposal.*

A note on the source: the post was read for the assessment; when this note was written the build
environment's network policy denied `research.google`, so the post could not be re-read beside the text.
The method below is therefore stated in the shape the assessment drew from it and does not repeat the
post's figures. The reader who wants the numbers has the post; this note is about what transferred.

## 1. The post, as method

Strip the neuroscience away and the post describes a way of working that Patterns already half-shares
and half-lacked:

1. **Structure first, as a complete map.** The work's object is the wiring — every cell and every
   connection of a whole nervous system — reconstructed from imaging into one graph that can be
   browsed, queried and cited. Nothing is left "off the map" because it was hard.
2. **Activity apart from structure.** The map carries no time: it says what connects to what, not what
   fired when. Recordings and simulations are laid *over* the structure as a separate layer, and the
   two are never confused — a question about the map and a question about a moment are two questions.
3. **Verification as a stage of its own.** Automated reconstruction proposes; human proofreading
   corrects; the release counts proofread work as work. "Segmented" is not "correct", and the pipeline
   says which parts have been checked.
4. **Synthetic exercise of the map.** Once the wiring is known, models run on it — simulated stimuli
   through the real graph — to predict behaviour before an experiment and to find where the map or the
   model must be wrong.
5. **Release with the tools to read it.** The map ships with a viewer and with the record of how it was
   made, so many readers see one map, and a view can be handed to a colleague.

Patterns' God's Eye (round 66) is point 1 for a show: every display, screen, source, device, deck, node,
audio path, the room, the stream and the stack, as one graph with lights, built from the desk's own facts
once a second. Points 2 to 5 were the assessment's candidates.

## 2. The transfers considered

| The post's idea | In Patterns' words | Decision | Where |
|---|---|---|---|
| Activity laid over a static structure | The Eye's structure of now, relit from what the desk *recorded* — the journal's rows and the metrics file's samples — at an instant the operator picks | **Taken** | 84.1 (core), 84.2 (the desk, the wire, the menus, the help) |
| Synthetic exercise of the map | Rigs the hand-written tests never drew, generated from fixed seeds and run through the take resolver, the show file's round trip, the frozen clone and the shown-picture rule, with the doctrine's invariants asserted | **Taken** | 84.3 (`SyntheticShowTests`, `SyntheticTakeAppTests`) |
| Proofreading as a queue with a memory | The Eye's problems queue walks red and amber things but has no memory: a problem seen and understood is not dismissable, and the same amber greets the operator every walk | **Left — a ledger row** | `docs/OPEN.md` L44 |
| A viewer with views in URLs | An Eye page in the browser (the wire already answers `EYE` as JSON) with the focus and the lens in the URL, so a view is a link | **Left — said in the plan** | PLAN §102 "what the round did not do" |
| The scale and the reconstruction | Tens of thousands of cells segmented from imaging by a model, corrected by many hands over months | **Not transferable** | The Eye has tens of things, typed and named by the desk, read live; nothing is inferred from an image and nothing is offline |

## 3. What shipped

**The replay (84.1, 84.2).** A `ReplayRecord` is the journal's rows and the metrics file's samples, sorted
once; `EyeReplay.At` gives the moment at an instant — the sample in force (the last one written at or
before it) with the sample before it, the rows of the thirty seconds before the instant, and the last row
before it whatever the window; `EyeReplay.Apply` keeps every node and link of the Eye of now, in order,
and replaces the lights and the words from the record alone. A screen shows the outcome of the rows that
named it (by id, by its wire number, by a canvas key's members, by label); a device its receipts; the desk
the machine's sample (grey without one or with a stale one, red for a render fault, a starved pool or a
missed slot since the sample before or a p95 frame past 50 ms, amber for a slow frame, a p95 past the
hitch line or a battery) and the rows nothing else claims — a cue, a look, a lower third; a thing the
record does not mention in the window is grey and says so; the links are grey, because the record holds
things, not links; an outcome word the replay does not know is grey, never green. On the desk: REPLAY on
the Eye page opens the record at its last stamp, a scrub bar and two 30 s steps move the instant, the
strip says the instant, the rows in its window and how the machine was doing, the card lists the rows
newest first, and NOW puts the picture of now back. On the wire: `EYE REPLAY [ON|OFF|<time>]` and
`EYE AT <time>`; STATE's eye row says `replay` and `replayAt`; the Eye menu offers both lines; the help
topic carries it; the assistant's brief says when the page is replaying and keeps the picture of now.
The rail, STATE's counts and `EYE` stay on now: the replay is the operator's reading, never the room's truth.

**The synthetic shows (84.3).** A fixed-seed generator lays out two to seven screens of mixed sizes, some
dragged flush into canvases, repeaters, locks, screens switched off, own pictures on screens and canvases,
ticks, un-armed tiles and a focus. Every rig is run through the take resolver under every scope — the
whole rig, FOCUSED, TICKED, GROUPS, CANVASES, the three groups by kind, every target by id, an id the rig
has not got — and the invariants hold on each: a locked target is never taken, a repeater is never taken,
an un-armed one is never taken, a plan that would change nothing is a refusal that names why, the taken,
the held and the outside partition the rig in wall order, a scoped take has an outside and the whole rig
has none, the same rig makes the same plan twice, and the words a key shows name every held target with
its reason. The model holds too: the show file round-trips, the frozen clone is equal and apart, the
shown-picture rule finds a repeater's source and an own picture's assignment, and a lock never moves a
picture. On the live desk eight rigs take once each under a scoped press, with the desk's own plan read
before the press as the oracle for the air after it. Five hundred and twenty rigs in the core, eight on
the desk, all from seeds, so a failure names its rig.

## 4. What the round did not do, and why

- **The Companion module is unchanged.** A scrub is a hand on a slider, not a key; the wire has the verbs
  for any controller that wants them, and STATE's `replay` and `replayAt` are there for a module that
  chooses to show them.
- **The assistant does not drive the replay.** It answers from the picture of now, and its brief says when
  the page is replaying so it never mistakes then for now; `EYE AT <time>` is the question an agent asks.
- **No measurement of the record's load.** The journal's newest rows and the metrics file are read on the
  REPLAY press, on the desk's thread, the way the history page already reads them at boot; the cost was
  not measured this round and the papers say so rather than claim a number.
- **The problems queue's memory** is a ledger row (L44), not a unit: a dismiss needs a place to live (the
  show? the machine? the session?) and a rule for when a dismissed problem returns, and that is a design
  before it is code.
- **The Eye in the browser** waits for a round with a bench: the desk's pages are the desk's, and a second
  reader of the same JSON should be walked on a real phone beside the wall before it is called done.

## 5. Reading the replay honestly

The doctrine's first line — attempts are not facts — is the replay's whole rule. A row that says
`Refused` paints amber, not "nothing happened"; a row that says `Failed` paints red at the thing it named
or at the desk that tried; a `Done` paints green only because the executor stamped it so, and the row's
`Visibility` and `Effect` ride into the words. The picture of then is what the desk *wrote*, no more:
where nothing was written, the replay is grey and says "no record in the last 30 s", and where the metrics
file has no sample within ninety seconds of the instant the desk is grey too. The brain map's proofreaders
would recognise the stance: the map shows what was checked, and says where it was not.

## 6. The second look (round 85): `flybrain.online`, Eon's `fly-brain`, and what a show controller takes from them

The maintainer asked, beside the handed-in plan, for a look at `flybrain.online` and at the repository
`github.com/eonsystemspbc/fly-brain`. Both were read in round 85; this section records what they are and
what transfers, the way §1–§5 did for the Google Research post. The site could not be fetched from the
build environment (its network policy denies the host); its text was read through a search index's fetch,
and the repository it points at was cloned and read whole. Eon's repository was cloned and read whole.

### 6.1 What `flybrain.online` is

The site is the front of `fruitflydev/flycoinrh` (MIT; created 10 September 2026; one contributor): a
leaky-integrate-and-fire simulation of a whole fly brain over the MaleCNS v1.0 connectome released on
3 September 2026 by Janelia's FlyEM project with Cambridge and Google (CC-BY; 165,122 neurons and
10,228,000 signed connections). Its parameters are the paper's — a resting potential of −52 mV, a
threshold of −45 mV, a 20 ms membrane time constant, a 2.2 ms refractory period, a 0.2 ms step — with
per-cell-type gains trained by an evolution strategy. A 30×30 view is fed into 892 hexagonal columns of
the first visual layers, and four descending neurons (DNa02, DNa01, MDN, DNp09) are read as a cursor. The
project sells a token on a retail chain and lets the simulated fly "roam" the web and post; that is what
the site is for. It is an anonymous art project, not Eon's, and the two forks of it seen are the same.

What is worth reading in it is not the neuroscience but the rails the author built around an agent that
acts in public: `roam.py` (no wallet in the process; a veto regular expression; an allow-list of domains;
a hop budget; an environment flag that opens the roam), `voice.py` (a narrator whose every number must be
present in the packet it was given, no second draft, every post recorded before it is sent, a cap on
posts), `tradebook.py` (a write-ahead ledger: the intent recorded before the outcome), `mushroom.py` (a
learning rule with depression, so a reward does not grow without bound), a `disclosure.md`, and 27 test
files over the rails.

### 6.2 What Eon's `fly-brain` is

Eon Systems PBC is a San Francisco public-benefit company (co-founded by Alex Wissner-Gross; Philip Shiu
is on the repository). The repository (GPL-2.0; created 5 March 2026; head `a3db62f` of 29 August 2026;
341 stars) is a benchmark harness for the Shiu et al. 2024 leaky-integrate-and-fire model over the FlyWire
v783 connectome (about 138,000 neurons): the connectivity as a parquet file kept in git (about 101 MB),
six simulation backends behind one spike schema, a results grid with a manifest and checksums, a
ground-truth comparison by Jaccard overlap and rate correlation, and timings that leave the I/O out. The
paper's code is MIT; the harness carries no tests; the embodied fly in a physics simulator is described
and not released.

### 6.3 The five transfers, named for a show controller

Strip the biology away and the two repositories are two disciplines Patterns already claims, done with
more rigour than Patterns has in places.

1. **Labels on every fact — measured, chosen, invented.** The benchmark keeps the connectome (measured),
   the parameters (chosen) and the model's spikes (computed) apart and never lets one be read as another.
   Patterns' readers of "what is live" derive their facts by their own rules and do not say which is which:
   an NDI sender ticked (chosen), an encoder running (measured on this machine), a receiver connected
   (observed elsewhere, or not). The plan's P1-09 is this transfer; the ledger's L46 is its design.
2. **The narrator's number fence.** `voice.py` refuses a sentence whose numbers are not in the packet it
   was handed. The assistant's brief already rules "answer from the facts" (round 53); a fence that checks
   the reply's numbers against the facts it was given, and refuses a second draft, is the plan's P3
   "evidence-based explanations" made checkable. Not built this round; named for the maintainer's list.
3. **Rails for an agent that acts.** No wallet in the process, an allow-list, a budget, a flag that opens
   the roam: the shape of the plan's Output Guardian in advisory mode — a policy that may say, then a
   separate deliberate switch before it may do. Patterns' live policy (round 46) and the take ticket
   (round 72) are rails of the same family; the Guardian would be the third.
4. **A ledger before an outcome.** `tradebook.py` writes the intent, then the outcome, and never the
   outcome alone. Patterns' journal writes the outcome with the intent in one row after the executor
   returns (round 79); the plan's P2-02 and P2-03 — the expectation recorded before the send, with a
   deadline, and the receipt matched to it — is the same discipline on the wire to a device.
5. **Parity across origins.** The harness runs six backends against one schema and compares. Patterns has
   one executor for every origin — the desk, the wire, OSC, a cue, a deck — and asserts it path by path; a
   fence that runs one action from every origin and asserts one journal row and one air would say it once.
   Round 84's synthetic shows (§3) are the rig side of that fence; this would be the origin side.

### 6.4 What does not transfer

The neuroscience (a show has no membrane potential); the token; the GPL code (Patterns is Core with no
package references, and a GPL dependency would change what the exe is); the 30×30 vision and the cursor.
And the roam itself: a show controller does not act in public on its own, and nothing here proposes that
it should.

### 6.5 What this round took

None of the five as code. P1-09 is recorded as L46 with the labels of the first transfer; the other four
are named in §103.8 for the maintainer's list beside the plan's P2 and P3, where they belong.
