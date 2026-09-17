# Companion — what the research found, and what Patterns does about it

*Round 54 (`docs/PLAN.md` §72). The brief asked for Patterns and Bitfocus Companion to interact in
new ways — speed of setup, total control including the nodes around the desk, live feedback in
useful colours — and for the latest Companion development documents to be read first. This page is
the reading, dated September 2026, and where each finding landed. Sources at the end.*

## 1. Where Companion is

- **Companion 5.0** is the current release (5.0.5 at the time of reading). Modules are installable
  plugins since 4.0 — from the module store, or an offline bundle imported under *Modules* — and
  since 4.3 surfaces are modules too. 5.0's button graphics are a stack of elements (text, box,
  image, gauge, line, circle, groups) with an image library and fonts; actions can return a result
  into a variable; expressions have loops and control statements; **Companion announces its
  satellite ports over mDNS**.
- **The module base is 2.x** (`@companion-module/base` 2.1.3, June–August 2026; 2.0 in March).
  Breaking from 1.x: the entrypoint is the module's default export (no `runEntrypoint`), with
  `UpgradeScripts` as a named export; variable definitions are an object, not an array;
  `setPresetDefinitions(structure, presets)` takes sections with ids; `checkAllFeedbacks` is its
  own call; feedbacks lost their subscribe callbacks; `learn` returns only the learned values;
  `parseVariablesInString` is gone; the manifest needs `type: "connection"` and a `node22` or
  `node26` runtime. New: preset *sections* and *groups* (including template groups), *layered*
  presets built from graphics elements with **gauges**, *alternatives* (several variants of one
  preset, the host picks the first it can draw), *text* presets, feedback-based local variables,
  abort signals on actions and feedbacks, `secret-text` config fields, `isVisibleExpression` on
  fields, **`bonjour-device` config fields with `bonjourQueries` in the manifest** (a `type` and
  `protocol`, optional `port`, `txt` filters and address family; Companion browses with
  `@julusian/bonjour-service` and hands the module `address:port`, or null for *Manual*).
- **The host's checks**: `@companion-module/host` sanitises every preset — unknown action or
  feedback ids, unknown option keys, layered elements against a schema (coordinates 0–100,
  colours as integers), style overrides against declared element ids — and drops what fails with
  a warning. Patterns' module suite runs its presets through that same sanitiser.
- **Companion's own APIs for being driven**: the TCP remote-control API (port 16759, lines
  answered `+OK` / `-ERR`) with `SURFACE <id> PAGE-SET <n>` / `PAGE-UP` / `PAGE-DOWN`, `LOCATION
  <page>/<row>/<column> PRESS` / `DOWN` / `UP` / `ROTATE-LEFT` / `ROTATE-RIGHT` / `SET-STEP` /
  `STYLE TEXT|COLOR|BGCOLOR`, `SURFACES RESCAN`, `CUSTOM-VARIABLE <name> SET-VALUE|GET-VALUE`; the
  same over UDP, HTTP (`/api/location/<p>/<r>/<c>/press`, `/api/custom-variable/<name>/value`),
  OSC, Ember+ and Art-Net.
- **The Satellite API** (TCP 16622, WebSocket 16623; announced over mDNS as
  `_companion-satellite-tcp._tcp` and `_companion-satellite-ws._tcp` with the version in the TXT
  record): a client says `ADD-DEVICE DEVICEID=… PRODUCT_NAME=… KEYS_TOTAL=… KEYS_PER_ROW=… BITMAPS=…
  COLORS=… TEXT=…`, receives `KEY-STATE` with bitmaps, colours and text per key, sends `KEY-PRESS`
  and `KEY-ROTATE`, and since 4.2 may describe a complex layout; 5.0 adds compressed images.

## 2. What that meant for Patterns

1. **The module as it stood (2.8.0, base 1.11, node18) could not be loaded by Companion 5.** That
   came first: the port to base 2.x is 54.1, with every id kept so saved pages keep working, and a
   test suite that boots it on the real base with a fake host, so the next base change is a failing
   test rather than a venue.
2. **Speed of setup is discovery.** `bonjour-device` in the connection plus a desk that announces
   `_patterns._tcp` means the connection dialog lists "Patterns desk FOH-PC" and nobody types an
   address (54.2). Every node announces too, by kind, so a Companion can drive an arcade or a stage
   timer node directly. The desk browses back for `_companion-satellite-tcp` and names the
   Companion in the room.
3. **Total control, both ways.** The wire already had the nodes', the twin's, the stage's, the
   plan's and the arcade's verbs; the module gained their keys (54.1) and STATE gained the room
   around the desk so the keys light (54.4). The other direction — the desk turning the deck's
   pages — is Companion's TCP API as a device profile (54.3): a cue's `DEVICE Companion PAGE 3`.
4. **Live feedback in useful colours.** One palette on both sides, a hue per kind and a treatment
   per state, held equal by a test (54.4); the stage timer's key wears the timer's own colour and,
   on Companion 5, a ring of how far through — the first use of layered presets here, offered as
   an *alternative* with the plain key as the fallback.

## 3. Considered and left

- **Patterns as a Satellite surface** — the desk, a node or a phone page showing Companion's
  buttons. The protocol is simple enough (above) and it would put a Stream Deck on any screen
  Patterns has; but Companion's own emulator page already does that in any browser, and the desk's
  pages are the desk's. Left until somebody at a desk asks for it.
- **The module store.** Publishing the module to Companion's store is a submission, not code;
  until then the CI's `.tgz` is imported under *Modules*.
- **Template preset groups** (one template, many values) could replace the per-item *… — this
  show* presets; the per-item presets are simpler to read in Companion's list and cost nothing.
- **Companion's HTTP API** as the driving path instead of TCP: the TCP API answers every line,
  which is what a receipt needs; the HTTP profile is there for anything else.

## 4. Unverified here

No Companion ran in this environment. What is verified: the module boots on the real module base
(2.1.3), its manifest validates, its presets pass Companion's own sanitiser, every line it sends
parses on the desk, the desk answers a real mDNS query on the loopback, and the Companion profile's
lines are the documented ones. What a Companion 5 in the room will confirm: the `.tgz` importing,
the bonjour pick listing the desk, a layered key drawn, the TCP API answering `+OK`.

## 5. Round 55 additions

Two field fixes reached the deck. The armed web VT: a `web_vt` action (ARM at the mark or at a
time, MARK, DISARM, on the page on air or a named page), `web_armed` and `web_advert` feedbacks,
`web_vt` / `web_armed` / `web_armed_page` variables and three keys in the Web page category
(ARM VT, MARK VT, DISARM VT, lit steel blue while a page is armed), plus `web_fps` — the frames
the page delivered in the last second. The routing matrix: `audio_routing` (on, off, toggle),
`audio_route` (a source on a destination at a level, or off it) and `audio_vog` (duck, replace,
leave) actions, an `audio_routing_on` feedback, `audio_routing` / `audio_routing_words` variables
and a ROUTING key in the Audio category. Every line they send is in `test/lines.txt` and parsed by
the desk's own test. The module's version stays 3.0.0: the ids added are new, none changed.

## 6. Round 56 additions

The single-machine round reached the deck in three places. Trouble on the outputs:
`machine_render_faults` (frames of the last minute whose draw threw — the room saw the last good
picture instead) and `machine_faulting` (FAULT / ok), with a `render_faulting` feedback in the
screen colour `fault` (red). A cue settled by its boxes: `cue_last_pending` — the device receipts
the last cue still waits for, 0 once settled (its outcome reads *Requested* until then). The
boxes themselves: `devices_failing` (how many Interactive devices' last word was a failure),
`device_last_reply` (*Projector: POWR: OK — accepted*) and `device_last_failure` (*Projector:
INPUT HDMI 2 — no answer in 2 s*), with a `device_failing` feedback (any device, or a named one)
in the device colour `fault` (red) beside `device_open` (green). The palette rows are held equal
on both sides by the desk's test as before. The module's version stayed 3.0.0: the ids added were
new, none changed — and that is the rule round 57 learnt the hard way (§7).

## 7. Round 57 additions

**The version rule.** Companion 5 refuses a module whose id and version it already has (*Module
patterns v3.0.0 already exists*), and its file import wants the tgz, not the zip GitHub wraps
round a CI artifact. Every build that changes anything now moves the version — 3.1.0 this round —
in every file that carries it (the package, the manifest, `MODULE_VERSION`, the desk's
`CompanionModule.Version`, the HELLO), and a packaging test builds the module the way
`companion-module-build` does and checks what Companion's installer, scanner and process manager
check: the root directory, the manifest, a runtime Companion 5 bundles, the api version, the
entrypoint, the version everywhere. Installing a build: unzip the artifact, remove the old version
in Companion, import the tgz; the module's README says it in full.

**One variable, one feedback.** `machine_live_age`: the oldest camera or feed picture an output
drew in the last minute, from its arrival in the decoder to the end of the frame that drew it —
*33 ms*, or *n/a* with no live picture drawn. The IMAG number, for a button that shows the room's
delay is where it should be; the card's own delay and the screen's are not in it, and the desk's
super-check says the same. `live_age_over` lights a button when that age passes a limit (80 ms by
default — the super-check's red line) in the screen colour `fault`, so the IMAG operator's deck
goes red before the room notices. The palette rows are unchanged: the feedback wears a colour the
desk already holds.

## 8. Round 58 additions

**Two variables, two feedbacks, version 3.2.0.** `machine_memory_pressure`: the rung the desk's
media memory ladder stands on — *none*, *elevated*, *high* or *critical* — pictures, frame pools,
retiring frames and decks against their budget (six tenths of the app's ceiling for the machine's
class). The steps at each rung are the desk's own (the retired swept and the pictures trimmed,
then the pre-roll held back and the decks narrowed, then no preview-only source opened) and the
source on air is never touched, so a key that shows the rung tells the operator what the desk is
already doing about it. `memory_pressure_at_least` lights a button at a rung or past it (a
dropdown — elevated, high, critical; high by default) in the screen colour `fault`.
`inputs_pending`: a reopen staged under a source on air — a capture mode, a low-latency profile,
a clip's loop or the routing matrix's mode changed while the source is on the programme with the
outputs live waits until it leaves the air or the outputs go off — as the desk's words (*Low
latency change pending — Cam Link 4K is on air; applies when it leaves the air or the outputs go
off air.*) or empty; `inputs_change_pending` lights a button while one waits, in the screen
colour `offLook`: amber, a change since, not a fault. The palette rows are unchanged. By the rule
of §7 the version moves to 3.2.0 in every file that carries it; a deck with 3.1.0 installed
removes it first or keeps it beside the new one.

**The web VT's phase on the deck.** The `web_vt` words now end with what the page itself reported
— *at its mark (observed)*, *PLAYING (observed)*, *FAILED — the player did not answer* — never
what the desk sent; STATE's `web.arm` and `webArmed` rows carry `phase` for a page that wants the
word alone.

## 9. Round 60 additions

**The staged verbs on a key, version 3.3.0.** The desk's right-click menus (round 60) speak five
new lines, and a deck can speak them too: `screen_stage` — a look, a preset, a kind of picture,
the programme, or the look on air back (RESET) on one screen's PVW — and `pvw` — the same for
the programme's picture — put the next picture in the desk's preview and nowhere else. EDIT SAFE
opens by itself; the audience sees nothing until the desk's CUT or TAKE. A key at front of house
can therefore build the next look on the operator's preview without ever going live, which is
the one thing a bank of "put this on screen 2" keys could never promise before. `screen_pattern`
is the live twin: `SCREEN n PATTERN kind`, one screen's kind of picture, like `SCREEN n LOOK`.

**The menus on the wire.** `MENU SCREEN 2`, `MENU CUE 03.020`, `MENU LOOK Walk-in`, `MENU LT
Neon`, `MENU LAYER 1`, `MENU CLOCK`, `MENU PGM`, `MENU PREVIEW` answer the very menu the desk
would show, as JSON, each entry with its wire line and — when it cannot be chosen — why. Companion
cannot render a menu, so this is not a module feature; it is the road for a tablet page or a
script that offers the desk's own choices and sends the desk's own words back (docs/REMOTE.md).

**Considered and left.** A `menu` action that types a MENU query and shows the answer on a key:
a Stream Deck key has no room for a menu, and a variable holding the JSON would be a variable
nobody reads. Feedbacks for "a picture is staged on screen n's PVW": STATE does not carry the
staged flag yet; when it does, the feedback is one line in the module and one in the palette.
The module's version moves to 3.3.0 in every file that carries it; a deck with 3.2.0 keeps
working, its keys unchanged, and the three new actions appear when it updates.

## 10. Round 62 additions

- `RUN MONITOR <n|PGM|MAIN|OFF>` — the RUN surface's monitor, one screen drawn large for the
  caller's eye. The module's generic line action sends it as any line; a `run_monitor` action
  with the screens as a dropdown (from STATE's `screens`) is the next module round's line, with
  a feedback on STATE's `runMonitor` (`MAIN`, `PGM`, `OFF`, a screen's number or a canvas key).
  No version bump this round: nothing in the module changed.
- `MENU MONITOR` answers the monitor's menu as JSON like the other menus (round 60).
- The lower thirds a caller presses on the Run surface are the same `LOWERTHIRD` verbs the
  module already has; nothing new on the wire.

## 11. Round 63 additions

**CUT and TAKE on one screen, version 3.4.0.** The wall's tiles carry CUT and TAKE that put the
desk's preview on that screen alone, as its own picture — OWN lights up, the programme and every
other screen stay — and a deck can press them too: `screen_take` (screen number, TAKE with the
transition or CUT at once) sends `SCREEN n TAKE` / `SCREEN n CUT`. A TAKE preset per screen
(`screen_<n>_take`) sits in the Screens category, green while the screen shows its own picture.
EDIT SAFE must be open on the desk; the desk answers `ERR` otherwise, as it does for TAKE and
CUT. The look tally reads the screen as gone its own way, so a key lit for the look on air dims
the way it does after any per-screen send.

The module's version moves to 3.4.0 in every file that carries it; a deck with 3.3.0 keeps
working and lacks the one action.

## 12. Round 65 additions

**The pairing token, version 3.5.0.** A desk with a pairing token set (Remote page, TRUST) runs a
verb only from a connection that presented it. The connection's config gains a **Pairing token**
field; when it is filled the module sends `AUTH <token>` straight after `HELLO`, and the desk
answers `OK paired`. Without one nothing more is said, and an open desk needs nothing. A refusal
over trust — `ERR not paired` when the desk asks for a token the config lacks, `ERR wrong token`
when it is not this desk's — sets the connection's status to bad config with the words, so the
Companion UI says what to type rather than a key going quietly dead; `$(patterns:last_error)`
carries the line as it does for every ERR. Dashes, spaces and case in the token do not matter:
the desk normalises before a constant-time compare. NEW TOKEN on the desk cuts every paired deck
off until the new token is typed into its connection.

**One writer per connection on the desk.** The desk's replies come back in the order the lines
were sent, and `STATE` pushes are latest-wins behind them — a deck that reads slowly gets the
newest state, never a backlog — and a connection that stops reading is closed rather than kept;
the module reconnects as it always has. Nothing changes in what the module parses.

The module's version moves to 3.5.0 in every file that carries it; a deck with 3.4.0 keeps
working on an open desk and lacks the token field.

**Signal truth and the rig on the deck, version 3.6.0 (round 65.10).** Three actions: `screen_signal`
(`SCREEN n SIGNAL <words>` — the link's contract), `screen_testroute` (`SCREEN n TESTROUTE ON / OFF`
or bare to toggle — the diagnostic profile standing in for the contract while a path is proven) and
`rig_save` (`RIG SAVE <note>` — the rig saved as known good). Five feedbacks, evidence only:
`screen_signal_is` (a screen's result MATCH / MISMATCH / UNVERIFIED — UNVERIFIED is never a pass),
`signal_mismatch_any`, `rig_known_good` (the rig is the one saved), `rig_drift` (it moved) and
`commissioned` (every stage of the flow green). Variables `screen_n_signal`, `machine_inventory`
(the card, its driver, the displays, the audio, the power plan in one line), `machine_rig` and
`commissioning` / `commissioning_next` (the flow's headline and next step). A **Rig** preset page: a
signal key per screen (green MATCH, red MISMATCH; press toggles the test route), KNOWN GOOD (green
saved and unchanged, amber moved; press saves), COMMISSIONING and THIS MACHINE. Colours: the
`signal` family (match green, mismatch red) and the `rig` family (same green, drift amber). The
module reads STATE's `screens[].signal`, `machine.inventory`, `machine.rig` and `commissioning`
rows as the desk has sent them since rounds 65.6–65.10; a 3.5.0 deck ignores them.

## 13. Round 66 additions

**The God's Eye on the deck, version 3.7.0.** The desk's picture of the whole show — every display,
screen, source, device, deck, Companion heard, node, audio path, the audience room, the stream and the
cue stack, each with its light and its links — reaches the deck as one STATE row,
`eye{headline,worst,worstLight,worstWords,red,amber,green,grey,things,problems,focus,lens}`, and five
actions move the operator's eye on the desk: `eye_focus` (`EYE FOCUS <words>` — a screen's number, a
label or an id from `EYE`), `eye_next` / `eye_prev` (`EYE NEXT` / `EYE PREV` — the problems stepped,
reds first, then ambers), `eye_lens` (`EYE LENS all | video | control | audio | room | problems`) and
`eye_reset` (`EYE RESET`). Two feedbacks: `eye_worst` (the worst light in the picture is red / amber /
green) and `eye_problems` (something is red or amber). Variables `eye_headline`, `eye_worst` (the worst
thing by name and what is wrong), `eye_problems` (the count) and `eye_focus` (the id the eye is on, or
empty). An **Eye** preset page: GOD'S EYE (the headline, lit by the worst light; press steps to the
next problem), NEXT / PREV PROBLEM, RESET, a LENS key per lens, and WORST. The `eye` colour family
(red, amber, green) is held equal on both sides like every other, and the group **God's Eye** in the
connection's config keeps or drops the page. A 3.6.0 deck ignores the row.

The point on a show: a key that is red before anyone looks at the desk, and NEXT PROBLEM under the
operator's thumb — the desk's picture is already on the thing that is wrong when they turn to it. The
module never reads the picture itself: the desk's row is the truth, latest-wins like every STATE push.

## 14. Round 67 additions

**The next take and the groups on the deck, version 3.8.0.** STATE gains a `take` row — the wall's take
plan as the desk's picker has it (`scope`, `scopeLabel`, `words`, `where`, `taken`, `held` with reasons,
`outside`, `refusal`, and `next{set,words,wire,sting}`, the one-shot pending on the next TAKE) — and
`screens[].ticked`. Two actions: `take_next` (`TAKE NEXT <transition | STING name | CLEAR>` — a dropdown
of the ways to arrive, a rate in ms, a sting's name or number; the transition or video sting for the
next TAKE alone, the show's own transition never moved) and `screen_group` (`SCREEN n GROUP main |
confidence | info | repeater` — the screen's group, the same verb as the tile's right-click menu and the
Screens page's picker). Four feedbacks: `take_next_set` (a one-shot is pending), `take_next_sting` (it is
a video sting), `screen_group_is` (the screen is in the group named) and `screen_ticked`. Variables
`take_next` (the pending words, or empty), `take_scope` (the picker's label), `take_words` (the plan's
words, or the refusal) and `screen_n_group`. A **Take** preset page — NEXT TAKE (the plan), NEXT: CUT /
DISSOLVE / DIP / WIPE / PUSH / BRAND STINGER, NEXT: STING 1, CLEAR — and MAIN / CONF group keys per screen
on the Screens page. The `take` colour family (next amber, sting the stinger's brown) and `screen.group`
(sky) are held equal on both sides like every other. A 3.7.0 deck ignores the rows.

The point on a show: the operator arms a sting or a wipe for the next take from a key without leaving
the wall, the key stays amber until the TAKE spends it, and the plan key says what that TAKE will do —
and why it would do nothing — before the press. The module never plans a take itself: the desk's row is
the truth, latest-wins like every STATE push.

## 15. Round 68 additions

**The web page's picture on the deck, version 3.9.0.** STATE's `web` row gains `path` — how the page's
picture reaches the glass: `words` (*smooth 2 (67 ms) · decode 6.2 ms · 30 → 30 fps*), `smoothing`,
`depth`, `latencyMs`, `jitterMs`, `decodeMs`, `deliveredFps`, `presentedFps`, `underruns`, `dropped`,
`duplicates`, `held`, `poolStarved`, `poolBytes` and `capture` (what the browser is asked to hand over).
Variables `web_path`, `web_smoothing`, `web_latency`, `web_underruns` and `web_capture`; feedbacks
`web_smoothed` (the page on air is buffered) and `web_stalled` (its buffer has run dry). No new actions
and no new colours: the presenter family's shades serve. A 3.8.0 deck ignores the row.

The point on a show: a key that reads *smooth 2 (67 ms) · 30 → 30 fps* beside the page's name says the
video is smooth before the room does, and a stall lights a key rather than being noticed on the wall.

## 16. Round 69 additions

**The sound follows the picture, version 3.10.0.** Each screen names the output its sound leaves by
(`SCREEN n AUDIO <output>`; the new `screen_audio` action, blank or OFF for none), and the matrix derives
the route from what the screen shows now — the programme's sound while it shows the programme, its own
picture's while it shows one of its own, the repeated screen's while it repeats — moving with every take.
`AUDIO FOLLOW ON|OFF|TOGGLE` (the `audio_follow` action; a FOLLOW key beside ROUTING on the Audio page)
switches the derivation; the operator's own rows win where both name a crosspoint. STATE's `audioRouting`
row gains `follow`, `followWords` and `followed` (screen, source, destination, label, what), each
destination `row` (false for an output the picture alone routes to) and each lane `followed`; each
screen's row gains `audioOut`, `audioOutLabel` and `audioSource`. Feedbacks `audio_follow_on` and
`screen_sound_out` (screen *n* names an output); variables `audio_follow`, `audio_follow_words` and
`screen_n_audio`. Two shades: the audio family's `follow` (sky) and the screen family's `sound` (blue),
held equal on both sides by the palette test. A 3.9.0 deck ignores the rows.

**The memory on the deck, still 3.10.0.** Two variables beside `machine_memory_pressure`: `machine_memory_held`
reads STATE's `memory.residency.words` — what the desk holds in memory and why (*4 held (312 MB): 2 on air,
1 armed, 1 idle — the first lets go in 43 s*; the residency ledger of round 69) — and `machine_gpu_cache` reads
`machine.gpuCache.words` — Skia's GPU cache as governed (*GPU cache 48 MB of 128 MB (212 resources)*, or *no
GPU context (software rendering)*). A machine key can carry both under the pressure rung.

The point on a show: a video on the main screen and its repeaters and a playlist with a soundtrack of its
own on the info screens — the room hears the video through the main's and the repeaters' outputs, the
info screens' outputs carry the playlist, and the TAKE that puts the video on the info screens too moves
their sound with it. The deck's screen key reads the output; nobody re-routes on a cue.

## Sources

- github.com/bitfocus/companion-module-base — the monorepo's CHANGELOG (1.10 → 2.1.3), the
  manifest schema, and the typings read from the installed package (`base.d.ts`, `input.d.ts`,
  `feedback.d.ts`, `action.d.ts`, `preset/*.d.ts`, `graphics.d.ts`, `enums.d.ts`).
- github.com/bitfocus/companion-module-host — `instance.d.ts`, `internal/presets.js`,
  `schema/elements.js`.
- github.com/bitfocus/companion — `companion/lib/Service/TcpUdpApi.ts`, `Tcp.ts`,
  `MdnsAdvertise.ts`, `BonjourDiscovery.ts`, `Satellite/SatelliteApi.ts`,
  `Instance/Connection/Thread/Entrypoint.ts`, `Instance/Connection/ApiVersions.ts`,
  `webui/src/Components/BonjourDeviceInputField.tsx`, and CHANGELOG.md (4.0 → 5.0.5).
- github.com/bitfocus/companion-module-template-js — `companion/manifest.json`, `package.json`.
- RFC 6762 (multicast DNS) and RFC 6763 (DNS-based service discovery).

## 17. Round 72 additions

**Signal truth's PARTIAL, version 3.11.0.** A screen's result reads PARTIAL when everything the path
states agrees with its contract and a property the contract names was never stated by anyone — the
colour space Windows never reports, a bit depth or an encoding the driver left blank — and the far
end's own word (a processor's input status) is the witness that settles it: with it, MATCH; against
it, MISMATCH. `$(patterns:screen_n_signal)` carries the word; `screen_signal_is` gains the PARTIAL
choice; `signal_partial_any` lights amber while any contracted screen reads it; the `rig_signal_n`
keys are green MATCH, amber PARTIAL, red MISMATCH. MATCH alone is a pass — a key built on
`screen_signal_is` MATCH goes dark on PARTIAL, which is the point. The desk's Verify stage of the
commissioning flow reads amber with the property named; the Eye's screen node and its link to the
display read amber.

**The take ticket, still 3.11.0.** `$(patterns:take_landing)` is the ticket a TAKE under a video sting
froze at the press — "→ 1 · Left, 2 · Right when 'Whoosh' ends" — while the clip runs, empty
otherwise; `take_landing_pending` lights the sting's brown while one waits. What lands is what the
press promised, less only a screen locked since or gone from the rig, never more (ADR-015). A 3.10.0
deck reads PARTIAL as a word it does not colour and ignores the row.

## 18. Round 73 additions

**The Library on the deck, version 3.12.0.** A `library` action puts a Library tile — a factory
pattern, one of the show's images, videos or audio files, a saved web page, a preset, a brand kit —
on a preview, exactly what a click on the desk's Library page does: on the desk's editing target
(the tile selected on the wall, or the programme) with `LIBRARY <name>`, on the programme with `PVW
LIBRARY <name>`, on a screen by number with `SCREEN n PVW LIBRARY <name>`. `screen_stage` and
`pvw` gain the LIBRARY choice for the same words. The tile is named as the Library page lists it,
case-blind; a name the library lacks is refused with the words and nothing moves. Staged like every
PVW verb: EDIT SAFE opens by itself and the audience sees nothing until CUT or TAKE — a brand kit
applies its colours to the show and stages no picture.

`$(patterns:editing_target)`, `$(patterns:editing_kind)`, `$(patterns:editing_editor)` and
`$(patterns:editing_library)` read STATE's `editing` row — what the desk's editors are on ("Right",
"Fractal", "Fractals", "Mandelbrot") — so a key can label itself with the page a picture is edited
on; `library_selected` lights amber while a tile sits on the editing target (any, or a named one).
The Eye's desk node carries the same line. A 3.11.0 deck ignores the row and the choice.

**Timed lower thirds, still 3.12.0.** A new design leaves by itself five seconds after it has
arrived; `lower_third_for` puts a design on for this run's hold (`LT n FOR 8`; 0 or STAY keeps
it until hidden) and `lower_third_hold` sets the design's own hold, saved with the show (`LT n
HOLD 8`, `LT n HOLD STAY`). `$(patterns:lower_third_timed)` reads TIMED while the design on
screen will leave by itself and `$(patterns:lower_third_leaves_in)` counts the seconds down to a
tenth (empty when it stays); `lower_third_timed` lights amber for the same. The desk's chips
read "ON AIR · 3 s" and the Eye's desk node says when it goes.
