# The God's Eye — one picture of the whole show

*Round 66. What is what, what is attached to what, what talks to what, and how it all links —
in one view an operator can take in at a glance or dive into, with or without the assistant.
This paper says where the idea came from, what the field does, what the method is when it is
applied to Patterns, and how it is built so that it is instant, honest and never a second
source of truth.*

## 1. Where the method comes from

The reference the round was asked to study is *God's Eye View* (bilawalsidhu/gods-eye-view):
a browser globe that puts public live signals — aircraft, ships, satellites, seismographs,
public cameras, traffic — into one explorable picture, with a voice agent over it. Read as a
method rather than a product, it does eight things, and each has a direct counterpart on a
show desk:

| The globe does | The method | On the desk |
|---|---|---|
| Layers of live feeds on one globe, each a module | **One picture, many layers.** Nothing is a separate page you have to remember to open. | Every plane of the show — video, control, audio, the room — on one canvas; each plane is a lens you can isolate. |
| Click-to-track: the camera locks on, a trail draws, the metadata surfaces; contacts near the target dim | **Focus with de-emphasis.** The thing you chose is protected by dimming everything competing with it, never by hiding it; the dim has an attack and a release so the picture never snaps. | Click a screen and its display, its sources and the box on the far end stay bright; the rest fades to a floor over 300 ms and comes back over 600 ms. |
| Contacts: a roster of everything within 250 km of the target, step-through | **The neighbours, as a list you can walk.** | The selected thing's contacts — what feeds it, what it feeds, who controls it — with FOCUS on each; arrow keys step them. |
| Global Context: the full situational picture with one switch, and your exact view back when you leave | **A view is a value.** Leaving a focus restores the view you had, exactly. | RESET is one key; the camera the operator had before a focus comes back when the focus ends. |
| AI HUD summary: a terse readout of the current view that regenerates as you move | **A headline that is always true for what is on screen.** | One line over the canvas — *14 things · 1 red · 2 amber — Projector 1: carries 1080p60 against a 50 contract* — computed from the graph, no model needed; the assistant reads the same words. |
| Entity Q&A grounded in the object's live telemetry; "instructed never to hallucinate labels" | **Ask about a thing with its facts in the question.** | ASK on any node puts its facts into the question; the brief carries the rule that unknown stays unknown. |
| Share links: camera, layers and one tracked target serialise into a URL — "a live target is a handoff, not a bookmark" | **The view travels as words.** | `EYE FOCUS screen 2`, `EYE LENS control`, `EYE RESET` on the wire — a Companion key recalls a view; the JSON carries what the operator sees. |
| Reset Globe: one control back to the whole Earth | **One way home.** | RESET, `EYE RESET`, Esc. |

Two further habits of the reference carry over as rules. Its focus de-emphasis samples every
80 ms and moves emphasis with hysteresis so a contact does not flicker at the edge of the focus
rectangle; the Eye's emphasis is a smoothed value per node, never a step. And its agent "only
confirms actions that succeeded": the Eye's ASK puts the facts in the question, and every verb
it offers runs through the action layer and answers with a receipt, as every key does.

## 2. What the field does, and what goes wrong on show days

Every serious system has an overview, and every overview is of its own box. Barco's Event
Master GUI puts a diagram of the system, the screens and the multiviewer in the middle of the
window with a dashboard of card status beside it; Q-SYS Designer is a schematic of the design
with the physical I/O at its edges; disguise's Designer has the stage and the feed map for the
outputs it drives; Pixera has its control overview for the devices it talks to; Dante
Controller shows the routing matrix, the clock and the latency of the audio network; NDI's
tools list what is on the network; Kiloview's Media HUB draws the IP topology of connected
devices and their ports. None of them draws the whole show: the processor does not know what the
media server is showing, the audio network does not know which screen the picture came from, and
the control surface knows only what it was told.

The show-day problems the trade's own troubleshooting guides list are the same everywhere: a
display on the wrong input after a handshake dropped; a source on the wrong screen; "no signal"
somewhere in a chain nobody can see end to end; a cable re-seated three times before the actual
fault (a mode the far end cannot take) is found; a device that stopped answering an hour ago and
nobody noticed because its page was not open; a network that is congested by a stream nobody
meant to leave running; a network deck that is connected but not paired, so its keys do nothing;
an operator with eleven windows open and the one that matters behind them. The common thread is
not a missing tool — every box has a status light — but that the lights are in eleven places and
the operator is the only thing joining them up.

Patterns already holds most of the evidence in one process: the display paths Windows reports,
the EDIDs, the signal contracts and their verdicts, the far end's input status, every device's
last receipt, every deck that said HELLO and whether it presented the token, every peer heard on
the beacon, the twin's phase, the audio routes and their lanes' errors, the room's phones, the
stream's health, the cue stack's state. The Eye is the picture those facts were always going to
make.

## 3. The method, applied

**One graph.** `EyeGraph.Build(EyeFacts)` in the core turns the facts the desk gathers into
nodes and edges. A node is a thing (the desk, a source, a screen, a display, an NDI send, the
box on the far end, a deck, a Companion heard, the wire's and the web's clients, OSC, the cue
stack, the assistant, the twin, another Patterns, an audio source, an audio output, the room,
the stream) with a stable id, a kind, a plane, a tier, a label, a state line, a light and its
facts as words. An edge is a link with a kind — feeds, shows, drives, carries, controls,
mirrors, follows, hears, routes, serves, sends — and a light of its own. The facts are a
record the App fills; the core never reaches for a service, so the graph is built and tested
without a desk.

**Planes and tiers.** The picture is four bands, top to bottom: CONTROL (what tells the desk
what to do: decks, Companions heard, wire and web clients, OSC, the cue stack, the assistant,
the twin and the other nodes), VIDEO (sources → the desk → screens → displays and NDI sends →
the far end), AUDIO (sources → outputs, the routes between them) and ROOM (the audience's phones,
the stream). Within a band the tiers are columns, left to right, in the direction the signal
travels. The layout is a deterministic grid — every (band, tier) cell stacks its nodes in a
stable order — so the same show always draws the same picture and a node keeps its place while
its neighbours change. A force layout was considered and rejected: it is prettier on a random
graph and worse on a show, where the operator wants the projector where it was a minute ago.

**Lights and the evidence rule.** A node's light is the worst thing known about it, never a
guess: a screen with a MISMATCH is red; on the test route, amber; MATCH, green; with a contract
and no observation, grey (*not verified*), because unverified is not a failure and is not a pass.
A display that is missing is red, planned and not yet plugged in amber. A source a screen uses
that is not mounted is red. A device that is enabled and closed is red; failing, red; open and
answering, green; disabled, grey. A deck that said HELLO and presented the token is green; one
that did not is amber (*connected, not paired — its keys do nothing*). A Companion heard on mDNS
but not connected is grey. The twin's light is its phase. A node that stopped being heard is red.
The same lights the Super Check, the tiles and the deck already use.

**Focus and de-emphasis.** Selecting a node focuses it: the camera fits the node and its
neighbours, and every node's emphasis becomes a function of its hop distance from the focus —
1.0 at zero and one hop, 0.55 at two, a floor of 0.25 beyond — smoothed by the canvas with a
300 ms attack and a 600 ms release. The graph, not the canvas, computes the hops; the canvas
only animates.

**The camera is maths.** A scale and an offset; fit-to-rectangle; zoom about the pointer's
world point (the point under the pointer stays under the pointer); pan; a critically damped
spring on the offset and on the logarithm of the scale, stepped with the frame's dt and
sub-stepped when a frame is long, so a focus glides and never overshoots and a machine at 30 Hz
gets the same motion as one at 144. The canvas runs its timer only while the spring is unsettled
or an emphasis is moving; a still picture costs nothing.

**Lenses.** ALL, VIDEO, CONTROL, AUDIO, ROOM, PROBLEMS. A plane lens shows that band and the
desk (the hub is on every lens); PROBLEMS shows every red and amber node with its neighbours
and nothing else — the operator's "what is wrong" view.

**The headline and the problems queue.** `EyeGraph.Headline` is one line computed from the
lights: the count of things, the reds and ambers, and the worst thing named with its state
words. `Problems` is the reds then the ambers in the picture's order; NEXT and PREV walk it, on
the page, on a key (`EYE NEXT`) and from the assistant. The headline is what the rail's EYE item
wears (its colour is the worst light), what STATE carries, what the module's variable reads.

**Menus stay one.** A right-click on a node opens the menu the desk already has for it — a
screen's is the wall tile's menu, built from the same facts, so the Eye never has a stale
copy — and a node without a desk menu gets the Eye's own: FOCUS, its CONTACTS (one line each,
FOCUS), OPEN (its page of the rail, with the item selected), ASK (the question with the node's
facts). Every entry shows its wire line, as every menu does.

**With the assistant.** The brief carries the graph in words — every node with its plane,
light and state, every edge as *A → B (kind, light)* — and the rule that the picture is
evidence: answer *what feeds screen 2* or *why is the projector amber* from the edges and the
lights, say *unknown* where the graph says grey, and propose verbs, never run them. ASK on a node
sends the node's own facts with the question. Without a key the Eye is exactly as capable; ASK
types the question in on the Assistant page.

**On the wire and the deck.** `EYE` / `EYE STATUS` answers the graph as JSON (nodes with their
places, edges, the headline, the counts, the problems, the focus and the lens); `EYE FOCUS
<words>` (an id, *screen 2*, a label), `EYE NEXT`, `EYE PREV`, `EYE LENS <name>`, `EYE RESET` move
the operator's eye — desk-only actions: a running order never moves what the desk is looking at.
STATE gains an `eye` row (the headline, the worst light, the counts); the Companion module
(3.7.0) gains the actions, an `eye_worst` feedback and the `eye_headline`, `eye_worst`,
`eye_problems` variables, and an EYE preset page — so a Stream Deck's EYE key lights the worst
colour and NEXT walks the problems without a hand on the desk.

## 4. Performance

The facts are gathered on the desk's tick, once a second, and hashed; the graph is rebuilt only
when the hash moved. The layout is O(n) and rebuilt with the graph. The canvas caches every
label's text layout by string and scale step, hit-tests by rectangle maths, draws edges as
straight segments between tier columns, hides sub-lines below one zoom step and labels below
two, and invalidates only when the graph changed, the camera moved or the pointer crossed a
node. A selection is a field write and one invalidate — no rebuild, no query.

## 5. What it is not

It is not a second source of truth: every light is the light the Super Check, the tile, the
device card or the deck already shows, read from the same facts. It is not a network scanner:
it draws what Patterns knows — what said HELLO, what the beacon heard, what mDNS announced —
and says so where a thing is heard but not connected. It does not move the show by itself:
focus, lens and reset move the operator's view; everything else on its menus is the desk's own
verb, run through the action layer with a receipt. And it is not the assistant's: the model
reads the graph, it does not make it.

## 6. What shipped (round 66)

The design above was written before the code; this is the record of where the code landed and
where it differs.

- **Core** (`Eye.cs`, `EyeLayout.cs`, `EyeCamera.cs`, `EyeJson.cs`, `Menus/EyeMenus.cs`): the
  graph, the lights, the planes and tiers, the problems queue, `Resolve`, lenses, hops, the camera,
  the JSON, the menu and the wire grammar as in §3. The layout is a grid by band and tier —
  deterministic, O(n), nothing overlaps by construction — and the relaxation step the plan named
  was not needed. Hop distance does not travel through the desk hub, or a focus would dim nothing.
- **Desk** (`EyeService`, `EyeSection`, `EyeCanvas`, `MainViewModel.Eye`): the facts gathered once
  a second, hashed, the picture rebuilt only when they moved; EYE in the rail above NODES; the page
  with the canvas (pan, zoom at the pointer, click, double-click, hover, the keys), the lens chips,
  PREV / NEXT PROBLEM / RESET and the card with FOCUS / OPEN / ASK and the LINKED TO rows; right-
  click through the desk's menu host — a screen's tile menu, a cue's menu, else the Eye menu. The
  view from before a focus is kept and RESET restores it (§3's global context).
- **Wire, deck, brief, help**: `EYE` and the verbs in the router; STATE's `eye` row; the Companion
  module 3.7.0's Eye page, actions, feedbacks and variables; `ShowFacts.Eye` under its rule in the
  brief; the `gods-eye` help topic.
- **Differences from §3.** The handoff is `EYE FOCUS <thing>` on the wire; no URL form. The lights
  are now — no trail. The Eye is the desk's: a node has none. The assistant reads the picture and
  answers from it; it does not act on it — every fix is a desk verb. PLAN §84.6 has the reasons and
  the next steps.
