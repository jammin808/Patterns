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
