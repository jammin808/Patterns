# Remote control — protocol and integrations

Patterns runs two remote interfaces while **Remote → Remote control** is on:

- **Web remote** — `http://<machine-ip>:9696/` (port configurable). One page for a phone or
  tablet with a menu across SHOW (presenter, transport, blackout, duck, STOP ALL, what is on
  now), CUES (the standby cue with its plan, ▲ ▼ GO HOLD, ARM, the day's timing, the next and
  the last), LOOKS, SCREENS (a switch and a padlock per screen, show parts), AUDIO (the audio
  track, break music, VOGs, stingers, tone), LOWER THIRDS (designs and people) and SETUP (the
  health line, the machine, the stream, the main machine's beacon, links to `/run` and
  `/multiview`, `/timer`, `/stage`, `/pad`, `/play` and `/host`); a sticky header names what is on air with its chips and a connection dot, the
  tab you were on is remembered, and the page waits on `GET /api/state?since=<rev>` so it
  changes the moment the show does. Works in any browser on the same network.
- **TCP line protocol** — port 9697 (configurable). One command per line (UTF-8, `\n`);
  every command answers `OK`, `OK <json>` or `ERR <reason>`. On connect — and on every
  change — the server pushes `STATE <json>` so controllers can show live feedback. Each
  connection has one writer on the desk's side (round 65): replies come back in the order the
  lines were sent, `STATE` pushes are latest-wins behind them — a slow deck gets the newest
  state, never a backlog — and a connection that stops reading its answers is closed rather
  than kept.
- **OSC** — UDP port 9698 (configurable; off by default — tick **OSC in** on the Remote page).
  Every address starts `/patterns/` and means exactly the TCP line it maps to; a refused command
  answers `/patterns/error` to the sender, and with a feedback host set every change sends one
  bundle of `/patterns/state/…` messages. See **OSC** below.

> **Trust.** The Remote page's TRUST block decides who may run the show (round 65). With no
> pairing token set, anyone on the network can control the show while remote control is enabled —
> the trust model of most stage-control protocols — and Super Check's Remote row reads amber for
> it. With a token set, a connection presents it before the desk runs a mutating verb:
> `AUTH <token>` on the wire (the Companion module sends it after `HELLO` from its **Pairing
> token** field), the `X-Patterns-Token` header on the web (the phone, run, stage and pad pages
> ask for it once and keep it in the browser). Reading — `STATE`, `STATUS`, `PING`, `HELLO`,
> `CUE LIST`, the `MENU` queries, the pages themselves — never needs it, and connections from the
> desk's own machine never do. A mutating verb without it answers `ERR not paired …` and runs
> nothing; NEW TOKEN cuts every paired remote off until the new token is typed into it. The token
> is the show's: saved with it and mirrored to the twin, so a standby that takes over answers the
> same remotes. **Bind to** puts the web remote and the wire on one address — the control
> network's, on a desk with two — so the audience network never sees the control ports. OSC has
> no session to pair: leave it off, or keep it behind the control network. Turn remote control off
> on the Remote page (SETUP) when it isn't needed. Administration — `RESTART`, `UPDATE APPLY` and
> the `/admin` page — sits behind the Install page's passcode (`docs/INSTALLS.md`), which rides in
> the `X-Patterns-Pass` header and never in a URL.

## Commands

| Command | Effect |
|---|---|
| `OUTPUTS ON` | Open the output windows on the enabled screens |
| `OUTPUTS OFF` | Close all output windows |
| `GO` / `STOP` | Frozen aliases for `OUTPUTS ON` / `OFF` (older buttons keep working; `GO` never fires a cue) |
| `BLACKOUT ON` / `OFF` / `TOGGLE` | Instant black on every sink. Bare `BLACKOUT` toggles; any other word after the verb is refused as unknown (round 72) — a misspelt ON or OFF never flips the latch the other way |
| `IDENTIFY` | Flash screen numbers |
| `LOOK <1–12>` | Apply the look on that F-key slot |
| `LOOK <name>` | Apply a look by name (case-insensitive) |
| `LOOK #<n>` | Apply the *n*th look in the show's order, whatever its name or F-key — a bank key that follows the list as looks are made (`ERR no look #7 — the show has 4`) |
| `NEXT` / `PREV` | The presenter click-through: a deck (PDF) on air turns its pages first — past the last page the caller's stack resumes with GO on the standby cue when the deck asks for it — else the clicker list forward / back |
| `DECK NEXT` / `PREV` / `FIRST` / `LAST` / `PAGE <n>` / `<n>` | The deck on air turns a page (`PDF` and `SLIDES` are aliases; `ERR` with no deck on air or a page that is not a number, first or last) |
| `VIDEO END [seconds]` | The clip on air — the program's video, a playlist's, a stinger's, an audio file — jumps to its last seconds (none = ten): the rehearsal's skip. Its end still plays, the out is still heard, and whatever follows it (the playlist's next item, a stinger's ending) happens for real; a timed playlist item's own clock is wound forward with it. `VT` and `CLIP` are aliases, `LAST` / `OUT` / `TAIL` mean `END`; `ERR` with no clip on air, a live source, or a clip whose length is not known yet |
| `VIDEO RESTART` | The clip on air plays again from its start (`START` / `TOP` / `REWIND` are the same); an ended clip comes back |
| `DEVICE <name\|*> <text>` | A line to a device of the Interactive area — an Arduino's relay, a Pi's script, or the device's own words through its profile: `DEVICE Projector POWER ON`, `DEVICE Disguise CUE 1.5`, `DEVICE Pixera TIMELINE Main PLAY`, `DEVICE Encoder GET /api/start` (`SEND` is an alias; `*` is the first device; `ERR` while the area or the device is off, the name is unknown, or the words are not the profile's). `OK` means dispatched — "sent; awaiting accepted" — and the receipt follows: the box's yes, its no with its own reason, or its silence with how far the line got (sent, delivered, accepted, observed), on the device's card and in the journal as a `DeviceReceipt` entry. Each device's Confirm level on the Interactive page says what the show wants of it, capped at what its link can give — a UDP datagram or a MIDI note is sent only. What devices send back and hear is in `docs/ARDUINO.md`; the boxes and their words in `docs/ENDPOINTS.md` |
| `ANNOUNCE <name or words>` | An announcement of the Install page by name — its words on the message overlay, its VOG, its look, for its seconds — else the words themselves for the site's announcement seconds; the programme comes back by itself. `ANNOUNCE OFF` (`STOP` / `END`) ends it (`ERR` while an advert is on instead — `ADVERT OFF` ends that). Works with the schedule on or off; refused while the caller's stack is armed or a stinger holds the screens |
| `ADVERT <name\|n>` | An advert of the Install page plays now for its seconds, on its screens (`AD` is an alias; `ERR` for an unknown name, a row that is not an advert, or one with no look). `ADVERT OFF` (`STOP` / `SKIP` / `END`) ends it and the programme comes back |
| `SCHEDULE ON` / `OFF` | The install's clock runs the site — programmes by the clock, adverts and announcements at their times — or stops with the picture where it is. `docs/INSTALLS.md` has the rules |
| `RESTART <passcode>` | Not while the stack is armed and the outputs are live (`ERR Not while…`: DISARM first, or OUTPUTS OFF). The app restarts under the watchdog with the show put back — the Install page's admin passcode; `ERR` with none set, a wrong one (five wrong tries lock the gate for a minute), or without the watchdog |
| `UPDATE APPLY <passcode>` | The staged update (`updates/*.zip`) applied by the watchdog between two starts of the app, rolled back if the new build does not stay up; the same gate as `RESTART`, and the same refusal while the stack is armed and the outputs are live — the update window keeps it too: a window that comes round mid-show is not taken, and the package waits for the next |
| `SCREEN <n> ON` / `OFF` / `TOGGLE` | Enable/disable screen *n* (overview numbering). Bare `SCREEN <n>` toggles; any other word after the number that is not a verb of the table below is refused as unknown (round 72) — a misspelt ON or OFF never flips the screen the other way |
| `SCREEN <n> LOOK <name or id>` | The look's picture for screen *n* lands on it alone as its own pattern — every other screen stays; a whole-look recall or a cue later replaces it, TAKE leaves it, a lock keeps it |
| `SCREEN <n> PROGRAM` (`PGM`, `FOLLOW`) | Screen *n* drops its own picture and shows the program again |
| `SCREEN <n> ROLE <main\|confidence\|info\|repeater>` | What screen *n* is for; a confidence or info screen is locked as it takes the role, a main screen or a repeater follows again (the Screens page's picker is the same verb, and a cue may carry it) |
| `SCREEN <n> GROUP <main\|confidence\|info\|repeater>` | Round 67: the same verb by the desk's word for it — every wall tile's right-click menu has Group under THIS TILE, and the Screens page's arrangement answers a right-click with the same menu. Bare `SCREEN <n> GROUP` is unknown |
| `TAKE NEXT <transition \| STING <name or number> \| CLEAR>` | Round 67: the transition or video sting the next TAKE alone arrives by — the words a cue's recall takes (`wipe left 800`, `dip`, `cut`, `reactive vortex 1200`, a bare `800` for the show's kind at that rate), `STING Whoosh` for a video sting of the library (a VOG is not a sting), `CLEAR` (or `DEFAULT`, `NONE`, `OFF`, or nothing) for the show's own again. One shot: the TAKE that spends it — the wall's or a tile's — arrives by it and says so; a CUT is a cut and keeps it; the show's transition on the Outputs page never moves. A sting covers the take's screens alone and the preview lands when the clip ends (the press answers `Requested`; STATE's `take.next` reads what is pending). Desk-only: a cue names the transition it arrives by on the recall itself. Bare `TAKE` and `CUT` stay unknown — a wire cannot take a half-built preview |
| `SCREEN <n> LABEL <name>` | Screen *n*'s name on the desk, the wall and the remotes; bare `SCREEN <n> LABEL` clears it. A canvas key as the target names the canvas |
| `SCREEN <n> AUDIO <output>` / `SCREEN <n> SOUND <output>` | Round 69: the output screen *n*'s sound leaves by — an output's name as Windows shows it (or its label on the Audio page), `NDI <send>`, `computer`; `OFF` (or `NONE`) names none. The route itself follows the picture: the programme's sound while the screen shows the programme, its own picture's soundtrack while it shows one of its own, the repeated screen's while it repeats, the canvas's while it is a member of one with a picture of its own — moving with every take, no row touched. Naming an output makes it a row of the matrix and switches the matrix on (seeded, as `AUDIO ROUTING ON` would). A canvas key as the target sets every screen of it. Desk-only: a cue never has to move the route. Bare `SCREEN <n> AUDIO` is unknown |
| `SCREEN <n> SIGNAL <words>` | Round 65: what screen *n*'s link is meant to carry — `3840x2160 50 RGB 8 SDR`, each word setting its property (WxH, a rate as `50`, `59.94` or `60000/1001`, `RGB` / `444` / `422` / `420`, `8` / `10` / `12` bit, `FULL` / `LIMITED`, `SDR` / `HDR10` / `HLG`, `709` / `P3` / `2020`, `STEREO` / `8CH` / `NOAUDIO`, `HDMI` / `DP` / `SDI` / `DVI` / `VGA` / `USBC`, `DIAGNOSTIC`), `CLEAR` for none; the answer carries the verdict. Bare `SCREEN <n> SIGNAL` reads the signal truth as JSON: `design`, `advertised` (what the display's EDID offers — capability, never the signal), `requested`, `observed` (what Windows reports it sends — each property unknown until the path states it), `result` (`MATCH`, `MISMATCH`, `UNVERIFIED`), `edid` (identity, name, serial, version, preferred timing, extensions, blocks, checksums, problems, SHA-256) and the `lines` Super Check shows under SIGNAL; STATE's screen rows carry `signal:{design,observed,received,result}` |
| `SCREEN <n> EDID` | Round 65.8: the EDID screen *n* is planned to present — built from its contract, its own size where the contract is silent — as JSON: `plan` (the contract's words), `timing`, `length`, `hash` (SHA-256), `bytes` (base64), `hex`, `file`, `url`, `presented` (`identity`, `hash`, `matches`: whether the display presents this very EDID) and `summary`. `GET /api/screens/<n>/edid.bin` (`.hex`, `.txt`) serves the same for a processor input, an EDID emulator or a PC to load as its custom EDID |
| `RIG SAVE [note]` | Round 65.9: the rig of the moment saved as the commissioned one — the machine as Windows describes it (the Windows build, the CPU, every GPU with its driver version and date, every display with its mode, connector, encoding and EDID hash, every audio endpoint), each screen's signal contract, the NDI senders that are on, the control bindings in words (never the token) and the render clock — with a note (`first show`); `RIG KNOWNGOOD` and `RIG COMMISSION` say the same. The Machine page's SAVE KNOWN GOOD. Desk-only: a running order never declares the rig commissioned. Every boot then compares the machine against it: Super Check's RIG rows, STATE's `machine.rig`, one journal line per change of the comparison |
| `RIG STATUS` (or bare `RIG`) | Round 65.9: the machine as Windows describes it, the commissioned rig and the drift from it, as JSON: `machine` (`build`, `windows`, `cpu`, `cores`, `ramGB`, `gpus` with `driver`, `driverFriendly`, `driverDate`, `vramMB`, `displays` with `rate`, `connector`, `encoding`, `bits`, `hdr`, `edid`, `edidHash`, `audio` with `flow`, `rateHz`, `bits`, `channels`, `power`, `onBattery`, `gpuScheduling`, `gameDvr`, `overlayPlanes`, `summary`, `lines`), `known` (the saved snapshot), `drift` (`same`, `changes`, `headline`, `lines` each `same`, `item`, `words`, `severe`) and `words`. STATE's `machine` row carries `inventory` (one line: the card, its driver, the displays, the audio outputs, the power plan) and `rig` (`not saved`, or the drift's headline) |
| `SCREEN <n> TESTROUTE [ON\|OFF]` (or `TEST ROUTE`) | Round 65.10: the diagnostic profile — 1080p50, RGB, 8-bit, SDR; no audio word since round 72, because nothing on the machine observes the audio a link carries — stands in for screen *n*'s contract while a path is proven (ON), the contract holds again (OFF), bare toggles; the contract itself is never touched. A soft or wrong picture that clears on the test route is a capability problem, one that stays is a path problem. The screen's signal view carries a Test route line first; the commissioning flow holds at CONTRACT while any screen is on the route. Desk-only: a running order never puts a screen on the test route |
| `SCREEN <n> RECEIVED <words>` | Round 65.11: what the far end — the processor, the scaler, the projector — says its input receives on screen *n*'s link, in the contract's words (`3840x2160 50 RGB 8`): the engineer's own reading of the box's panel, held against the contract as the third witness beside Windows' observation; a disagreement is red and the result MISMATCH, whatever Windows believes it sends. `SCREEN <n> RECEIVED CLEAR` forgets it. A device with an input-status adapter (Interactive page: *Input carries*, *Ask*, *Reads*) says it by itself — a PJLink class 2 projector's `IRES ?` out of the box; a box with its own API through the words and the pattern typed. The signal view carries RECEIVED, STATE's screen rows `signal.received`, the journal one line per change. Desk-only: a running order never claims what a box receives |
| `COMMISSION` / `COMMISSION STATUS` (or `COMMISSIONING`) | Round 65.10: the commissioning flow as JSON — `complete`, `done`, `total`, `percent`, `headline`, `next`, `overall` and `stages` (each `stage`, `title`, `light`, `value`, `next`): DISCOVER (Windows shows every display, none lost), ASSIGN (every planned screen adopted, screens enabled), CONTRACT (every screen says what its link carries; none on the test route), CAPABILITY (every display's EDID offers its contract), OUTPUT TEST (the outputs have opened this run), VERIFY (MATCH on every contracted screen — UNVERIFIED is not a pass) and KNOWN GOOD (saved and unchanged). STATE's `commissioning{complete,done,total,stage,headline,next}` row carries the same |
| `EYE` / `EYE STATUS` (or `EYE GRAPH`) | Round 66: the God's Eye — the whole show as one picture, as JSON: `headline` (the worst thing and what is wrong, or *all green*), `worst`, `worstLight`, `worstWords`, `counts{green,amber,red,grey,things,links}`, `problems` (ids, reds first then ambers, Video before Control before Audio before Room), `focus`, `lens`, `nodes` (each `id`, `kind`, `plane`, `tier`, `label`, `sub`, `light`, its place `x`, `y`, `w`, `h`, `words`, the desk `page` and `item` that show it, and its own `wire` line) and `edges` (`from`, `to`, `kind` — the link's verb, lower-case — `light`, `words`). STATE's `eye{headline,worst,worstLight,worstWords,red,amber,green,grey,things,problems,focus,lens}` row carries the headline, the counts and the view |
| `EYE FOCUS <words>` | Round 66: the operator's eye on one thing — a screen's number (`EYE FOCUS screen 2`, or `EYE FOCUS 2`), a label (`EYE FOCUS Main LED`), `desk`, or an id from `EYE` (`screen:…`, `display:…`, `source:…`, `device:…`, `deck:…`, `companion:…`, `stack`, `assistant`); the Eye page's camera moves to it and the rest dims by distance. `ERR` names a thing the picture has not got. Desk-only: a running order never moves the operator's eye |
| `EYE NEXT` / `EYE PREV` (or `PREVIOUS`, `BACK`) | Round 66: the eye steps through the problems — reds first, then ambers — and wraps; `OK` with nothing red or amber |
| `EYE LENS <name>` | Round 66: one lens over the picture — `all`, `video`, `control`, `audio`, `room` or `problems` (the things that are red or amber, their neighbours and the desk); `ERR` for any other word |
| `EYE RESET` (or `EYE HOME` / `EYE ALL`) | Round 66: the whole picture — no focus, every band, the view from before the focus |
| `LOCK <n> ON` / `OFF` / `TOGGLE` | Lock screen *n*: it keeps its picture through looks, cues, TAKE ALL and stingers (a confidence monitor, an info screen); unlock lets it follow again. Bare `LOCK <n>` or `LOCK <n> TOGGLE` toggles; any other word is refused as unknown (round 72) |
| `GROUP <letter> ON` / `OFF` | All screens of joined canvas A/B/… at once |
| `AUDIO PLAY` | The audio playlist plays — from where it stopped, or its first track (`TRACK` is an alias of `AUDIO`; `ERR` with an empty list) |
| `AUDIO PLAY <n>` / `AUDIO PLAY <name>` | A track by its place in the order (the rows first, then the folders' files in name order), a row's name, or a file's name with or without its extension (`ERR` for a track that is not there) |
| `AUDIO NEXT` / `AUDIO PREV` | The next or the previous track (`SKIP` / `BACK` are the same); they wrap, so a key never dead-ends |
| `AUDIO STOP` | The list stops (it resumes at the same track) |
| `AUDIO VOL <0–125>` | The list's level (`VOLUME` / `LEVEL` are the same; out of range is `ERR`) |
| `AUDIO ROUTING ON` / `OFF` / `TOGGLE` | The routing matrix in charge of which soundtrack goes where (Audio page → ROUTING), or the two wires as before: the programme's outputs and the monitor. Switched on with nothing in it, the matrix seeds audio-follows-video: the programme, the music, VOGs, stingers and the tone on every programme output, the programme with the music and VOGs on every NDI send |
| `AUDIO ROUTE <source> TO <destination> [AT <dB>]` | A source on a destination at a level (0 dB without `AT`; −60 is off, +12 the ceiling). Sources: `programme`, `screen <name or id>` (a screen with its own picture), `preview`, `music`, `vog`, `sting`, `tone`. Destinations: an output's name as Windows shows it (or the label given on the Audio page), `NDI <send>`, `computer`. A destination named for the first time becomes a row |
| `AUDIO UNROUTE <source> FROM <destination>` | The source off that destination |
| `AUDIO VOG <destination> DUCK` / `REPLACE` / `LEAVE` | What a VOG does to everything else on that destination: steps it down to the duck level (the show's, or the destination's own), replaces it (the announcement alone), or never reaches it (a stream that stays clean, a screen with its own soundtrack) |
| `AUDIO FOLLOW ON` / `OFF` / `TOGGLE` (`FOLLOWS` the same) | Round 69: the sound follows the picture. On (the default), every screen that names a sound output (`SCREEN <n> AUDIO`) gets the route its picture makes, at 0 dB plus the row's trim; a row of the operator's for the same crosspoint wins, on or off. Off, the matrix is the rows alone. A cue may carry it. STATE's `audioRouting` row carries `follow`, `followWords` and `followed` (screen, source, destination, label, what), each destination's `row` (false for an output the picture alone routes to — the defaults: no trim, no delay, a VOG ducks) and each lane's `followed`; each screen's row carries `audioOut`, `audioOutLabel` and `audioSource` |
| `MUSIC PLAY` | Break music (Spotify): resume, or start the library's first entry |
| `MUSIC PLAY <n>` / `MUSIC PLAY <name>` | Play break-music entry *n* (Audio-page order) or by name (`MUSIC <n>` / `MUSIC <name>` do the same) |
| `MUSIC PAUSE` | Pause break music (alias `MUSIC STOP`) |
| `MUSIC NEXT` | Skip to the next track (alias `MUSIC SKIP`) |
| `MUSIC VOL <0–100>` | The Spotify device's own level (alias `VOLUME`; out of range answers `ERR … 0 to 100`) |
| `TONE ON` / `OFF` | Soundcheck tone generator |
| `DUCK ON` / `OFF` / `TOGGLE` | The live duck: the music track, break music, a playing stinger's sound and a clip's soundtrack drop to the Audio page's live-duck level for an announcement from the room and come back when lifted; a VOG never ducks. A latch (bare `DUCK` or `DUCK TOGGLE` toggles; any other word is refused as unknown, round 72): STOP ALL and look recalls leave it |
| `STINGER <n>` / `STINGER <name>` | Fire library item *n* (Audio-page order) or by name — a VOG or a stinger, whichever it is |
| `VOG <n>` / `VOG <name>` | The same, refused if that item is a stinger — a key that says VOG never fires one |
| `STING <n>` / `STING <name>` | The same, refused if that item is a VOG |
| `STINGER STOP` | Stop whatever is on air: a clip or a held frame reverts, and a stinger's ending is cancelled, never run (`VOG STOP` / `STING STOP` are aliases) |
| `LOWERTHIRD <n>` / `LOWERTHIRD <name>` | Put lower third *n* (Lower thirds page order) or the named design on air over whatever is showing; again restarts its way in (`LT` is an alias) |
| `LOWERTHIRD OFF` | The lower third on air leaves the way it was designed to (`LT HIDE` does the same) |
| `LOWERTHIRD <design> WITH <person>` | Fill design *n* / the named design from the library first — the person's name, role, company and photo (Lower thirds page, LIBRARY) — then put it on air; *person* is a number (library order) or a name (`LT … WITH …` is the same) |
| `LOWERTHIRD <design> FOR <seconds>` / `LOWERTHIRD <design> STAY` / `LOWERTHIRD <design> HOLD <seconds\|STAY>` | Timed lower thirds (round 73). A new design leaves by itself five seconds after it has arrived (its DESIGN row: *Leaves by itself after* and the seconds; unticked, it stays until hidden and keeps the number). `FOR` puts the design on for this run's hold, then it leaves the way it was designed to — `FOR 0` or `STAY` keeps this run until hidden; the design's own hold is untouched. `HOLD` sets the design's own hold, saved with the show, and retimes the one on air; `HOLD STAY` makes it stay. Decimals are fine (`FOR 7.5`); a stranger answers `ERR`. STATE's `lowerThirdTimed`, `lowerThirdLeavesIn` (seconds to a tenth, `null` when it stays) and `lowerThirdHoldMs` read the run; the chip on the panel and the Run surface counts it down; the Eye's desk node says when it goes |
| `PERSON <n>` / `PERSON <name>` | The same into the lower third on air, else the show's ★ default design (Lower thirds page, the star on a design): the next speaker in one command. A name that is not in the library answers `ERR … not in the lower-thirds library` — a wrong name never reaches the screen |
| `LOWERTHIRD PREVIEW <n\|name> [WITH <person>]` / `LOWERTHIRD PREVIEW WITH <person>` | The design — with a library entry — into the preview for a sign-off: the desk's PREVIEW pane, the multiview's Preview tile and REVIEW show it while the audience sees nothing new. With no design named, the design already in the preview, else the one on air, else the ★ default. Needs EDIT SAFE on the desk (answers `ERR` without it); `LT PVW …` is the same |
| `LOWERTHIRD PREVIEW OFF` | The preview's lower third leaves (also `CLEAR`); nothing changes on air |
| `LOWERTHIRD TAKE` | The lower third in the preview goes to air afresh — it arrives the way it was designed to — and the preview clears for the next name |
| `LOWERTHIRD UPDATE` | With EDIT SAFE open the audience sees a copy of the design: this replaces the copy on air by the design as it is now, in place — every edit, the words too — without it leaving and arriving again (`AIR` / `LT n` again restarts it instead) |
| `WEB KEY <key\|action> [ON <page>]` | A key chord (`ArrowRight`, `Space`, `k`, `Shift+N`, `Ctrl+Shift+F5`) or a page action to the web page on air — the one the program shows — or to the page `ON` names (its nickname, its address or a word of it). A page action is what the page's service makes of it: `next` / `prev` / `first` / `last` are the slide keys of a Google Slides deck or a PowerPoint for the web, `present` starts the deck (Ctrl+Shift+F5 on Slides, F5 on PowerPoint), `black` / `white` blank it, `exit` leaves; on YouTube `play` / `pause` / `mute` / `restart` / `forward` / `rewind` / `next` / `prev` drive the player itself; on any other page the arrows, page keys, Enter and Escape. `PAGE` is an alias of `WEB`; a key nobody knows or a page not on the desk answers `ERR` |
| `WEB NEXT` / `PREV` / `FIRST` / `LAST` / `PRESENT` / `EXIT` / `PLAY` / `PAUSE` / `MUTE` / `RESTART` / `FORWARD` / `REWIND` / `BLACK` / `WHITE` / `CAPTIONS` / `FULLSCREEN` `[ON <page>]` | The page actions as verbs of their own — `WEB NEXT` is `WEB KEY next` |
| `WEB CLICK <x> <y> [ON <page>]` | A click at a point in percent of the page (`WEB CLICK 50 50` is its centre) |
| `WEB TYPE <text>` | The text — spaces and all — typed into the field that has the page's focus (click it first); a character no US key types is inserted as text |
| `WEB RELOAD [<page>]` | The page reloaded (also `WEB KEY reload`) |
| `WEB OPEN <address> [ON <page>]` | The page's browser sent to another address; the pattern keeps its own, so a look recall brings the first page back |
| `WEB ARM [<time>] [ON <page>]` | The armed web VT: the page's video (YouTube, Vimeo, any page with a video element) held at a point — `1:23`, `83`, `1m23s`; no time = the mark set, else where the player is now — and played from it the moment the page goes to air (a TAKE, a cue, the clicker's NEXT). With no page on air the page in the preview (or one opened early for a cue ahead) is the one armed; a page on air answers `ERR` — arm it from the preview, or give the look a start point (Media page → Play the video from) and it arms itself. `WEB ARM OFF` disarms |
| `WEB MARK [<time>] [ON <page>]` | The start point set without arming: a time, or where the player is now |
| `WEB DISARM [ON <page>]` | The arm cleared — the video plays as the page does |
| `FREEZE ON` / `OFF` / `TOGGLE` | Every output — the windows, the NDI sends, the stream — holds the frame it shows until released; the desk's own views keep moving, a blackout still takes a frozen output. A latch (bare `FREEZE` or `FREEZE TOGGLE` toggles; any other word is refused as unknown, round 72), never saved |
| `FADE [seconds] [where]` / `FADE UP [seconds] [where]` (also `FADEUP`) | A fade to black over the seconds given (`FADE 2`, `FADE 2.5`, `FADE 1500ms`; none = the show's transition time), or up again the same way. With no place it is the rig — a blackout with a fade of its own, and `FADE UP` lifts the blackout and brings back every screen that was black on its own. With a place, that part alone fades while the rest of the rig keeps its picture: `SCREEN 2` (a screen by its wall number; one that joined a canvas takes its canvas with it), `GROUP A` (a joined canvas by its wall letter; `CANVAS A` the same), `FOCUSED` (the wall tile the desk has clicked — the PGM tile means the rig), `TICKED` (the tiles ticked on the desk's wall), `GROUPS` (the ticked tiles that are canvases), `ID <screen id>` or a bare canvas key (`a+b`). The seconds and the place go in either order (`FADE 2 SCREEN 2`, `FADE SCREEN 2 2`). A fade that leaves the whole rig dark takes the programme's sound (the music, a clip's soundtrack) with it when the show's WITH THE SOUND setting is on (the default), and a fade up brings it back; `BLACKOUT` on its own never touches the sound. Refused with `ERR` when the place is already there, when it names nothing on this rig (`No screen 9`, `Tick the wall tiles to fade first.`), or when the words mean nothing (`FADE slowly`) |
| `LOOKBACK [cut\|ms]` | The look that was on air before the current one, back on air with the show's transition (or a cut, or a fade in ms); a second `LOOKBACK` swaps back. `ERR` when there is none yet |
| `WEATHER ON` / `OFF` / `TOGGLE` | The weather chip on air or gone, as the Overlays page set it up (the place, the source, the size, where it sits); `FORECAST` is an alias and bare `WEATHER` toggles. With no place set the chip goes on and says so |
| `WEATHER NOW` / `DAY` / `TOMORROW` | The chip's view: this hour's degrees and sky, the rest of today (the range, the day's sky, the hours), or tomorrow (`TODAY` means `DAY`) |
| `CLOCK ON` / `OFF` / `TOGGLE` | The clock overlay on air or gone, as the Overlays page set it up; bare `CLOCK` toggles (`SHOW` / `HIDE` are the same) |
| `CLOCK 12` / `CLOCK 24` | The clock's hours |
| `CLOCK SECONDS [ON\|OFF]` · `CLOCK DATE [ON\|OFF]` | The seconds and the date line shown or not; no word toggles |
| `MESSAGE <text>` | The words onto the message overlay, and on (`MSG` is an alias; `MESSAGE ON <text>` and `MESSAGE TEXT <text>` are the same) |
| `MESSAGE ON` / `OFF` / `TOGGLE` | The message overlay on with its current words, off (the words kept), or flipped; bare `MESSAGE` toggles |
| `MESSAGE SCROLL [ON\|OFF]` | The message as a ticker across the screen, or a still line (`TICKER [ON\|OFF]` is the same); no word toggles |
| `COUNTDOWN <minutes>` / `COUNTDOWN START <minutes\|m:ss>` | A countdown of that long from now, on air — `5`, `2.5`, `2:30`, `90s`; bare `COUNTDOWN START` runs the countdown as it is set up (its time of day, else its duration again). `TIMER` is an alias |
| `COUNTDOWN TO <HH:mm>` | A countdown to a time of day, 24-hour, local (`AT` / `UNTIL` are the same); a time that passed less than twelve hours ago reads as over, one further back means tomorrow |
| `COUNTDOWN` / `COUNTDOWN TOGGLE` | The countdown flips — off when it is on air, else started as the desk has it set up (its time of day, else its duration from now). One key on a phone or a Stream Deck, like a bare `CLOCK`, `MESSAGE`, `LOGO` or `PIP`; `TIMER` is the same |
| `COUNTDOWN STOP` | The countdown leaves (`OFF` / `HIDE` / `CLEAR` are the same) |
| `COUNTDOWN LABEL <text>` | The words over the digits — BACK FROM LUNCH IN |
| `COUNTDOWN FOLLOW ON` / `OFF` | The countdown follows the running order: its target is the standby cue's planned start, as a time of day, and it moves on every GO, standby move, slip, resume or catch-up — so the speaker timer, the stage display and the info screen keep the caller's one clock (`TIMER FOLLOW` is the same; a standby cue with no planned start leaves it where it was) |
| `LOGO ON` / `OFF` / `TOGGLE` | The brand logo overlay (the file is the Branding page's); bare `LOGO` toggles |
| `PIP ON` / `OFF` / `TOGGLE` | The picture-in-picture inset, its source as the Overlays page set it; bare `PIP` toggles |
| `OVERLAYS OFF` | The clock, the message, the countdown, the logo, the PiP and the weather chip all off: a clean picture in one press |
| `PRESET <name>` | A pattern saved on the Pattern page, back into the picture being edited (with EDIT SAFE open it lands in the preview and waits for the TAKE). Presets are files in the presets folder beside the exe, so a machine that has not got one answers `ERR` naming the ones it does have |
| `SCREEN <n> PRESET <name>` | That preset becomes screen *n*'s own picture, live — every other screen stays, exactly like `SCREEN n LOOK` |
| `SCREEN <n> PATTERN <kind>` | That kind of picture on screen *n* alone, live, as its own pattern — `PATTERN kind` for one screen; every other screen stays |
| `SCREEN <n> TAKE` / `SCREEN <n> CUT` | The desk's preview to screen *n* alone (round 63), as its own picture — OWN lights up; the programme and every other screen stay, the preview keeps the picture. TAKE with the transition, CUT at once. EDIT SAFE must be open; the tile's own keys |
| `SCREEN <n> PVW LOOK <name>` / `PVW PRESET <name>` / `PVW PATTERN <kind>` / `PVW PROGRAM` / `PVW RESET` | The staged verbs (round 60): the picture lands on screen *n*'s PVW in the desk's preview and nowhere else — EDIT SAFE opens by itself, the audience sees nothing until the desk's CUT or TAKE (FOCUSED puts it up on that screen alone). LOOK is the look's own picture for that screen, PRESET a saved pattern, PATTERN a kind of picture (its settings kept), PROGRAM the programme again, RESET the look on air's own picture for that screen back (`ERR` when no look is on air). Bare `SCREEN <n> PVW` is → PVW: the screen's air picture into the preview to edit. `PREVIEW` is the same word |
| `PVW LOOK <name>` / `PVW PRESET <name>` / `PVW PATTERN <kind>` / `PVW RESET` / `PVW PROGRAM` | The programme's picture in the preview, never on air: the whole look, a preset, a kind of picture, the look on air back whole (RESET), what is on air into the preview to edit (PROGRAM, and bare `PVW`). What the right-click menus of the desk speak; CUT or TAKE puts it up |
| `LIBRARY <name>` / `SCREEN <n> PVW LIBRARY <name>` / `PVW LIBRARY <name>` | A Library tile (round 73) — a factory pattern, one of the show's images, videos or audio files, a saved web page, a preset, a brand kit — onto a preview, exactly what a click on the Library page does: bare `LIBRARY` lands on the desk's editing target (the tile selected on the wall, or the programme), the other two name the target. Staged like the PVW verbs: EDIT SAFE opens by itself and nothing reaches the audience until CUT or TAKE; a brand kit applies its colours to the show and stages no picture. The tile is named as the page lists it (a file by its display name, a page by its title, `Section/name` to be exact; case-blind); a name the library lacks answers `ERR No Library tile …`. STATE's `editing` row says what the desk's editors are on — the target, `own`, `kind`, the `editor` page and the `library` tile — and the Eye's desk node carries the same words. `LIB` is the short form |
| `PATTERN <kind>` | The kind of picture on air — `Grid`, `ColorBars`, `LedWall`, `Particles`, `Fractal`… (spaces ignored: `PATTERN LED wall`); the pattern's settings are kept, so a look's grid comes back a grid. STATE's `patternKinds` lists every kind; a stranger answers `ERR` with the list |
| `REVIEW ON` / `OFF` / `TOGGLE` | Every multiview (a screen's own multiview pattern, an NDI send of it, `/multiview`) draws the desk's sandboxed preview full-frame with a REVIEW chip until switched off — the next look checked on the monitor wall before the TAKE; the audience's screens do not change. A latch (bare `REVIEW` or `REVIEW TOGGLE` toggles; any other word is refused as unknown, round 72), never saved |
| `RUN MONITOR <n>` / `RUN MONITOR PGM` / `RUN MONITOR MAIN` (bare `RUN MONITOR`) / `RUN MONITOR OFF` | The RUN surface's monitor (round 62): one screen drawn large between the wall and the history, for the caller's eye — a screen by its number or id, a canvas by its key, the programme, the main screen (the first whose role is Main, as the canvas it belongs to when joined), or hidden. The desk's own eye — nothing changes on air; the show remembers it; `STATE` carries it as `runMonitor` (`MAIN`, `PGM`, `OFF`, a number or a key). Also `/patterns/run/monitor <n|pgm|main|off>` |
| `SECTION <n>` / `SECTION <name>` | Put playlist show part *n* (Media-page order) on air |
| `STREAM ON` / `OFF` | Start/stop the streaming output (Stream page config) |
| `CUE GO [<id>]` | GO on the caller's cue stack through the gate. Send the standby id you last saw (from STATE) and a GO that races a standby move answers `ERR standby moved`; `OK <json>` carries the execution record (`outcome`, `last`, `standby`) or `{"outcome":"Confirm"}` when the cue asks for a second GO within four seconds |
| `CUE STANDBY NEXT` / `PREV` / `<number>` / `<name>` | Put a cue on standby — changes nothing on air |
| `CUE HOLD ON` / `OFF` | A latched GO inhibit and nothing else |
| `CUE ARM ON` / `OFF` | Arm / disarm the stack — accepted only when the Remote page allows remotes to arm |
| `PLAN SHIFT <±m:ss>` | The day slips: every planned start from the standby cue on moves by the delta — `+2:00`, `-0:30`, `+90`, `-2m` (`SLIP` / `MOVE` are the same, and `PLAN +2:00` alone) — the Run surface's −1 MIN / +1 MIN as any delta; journaled as PlanShift; a caller node sends it to the desk it follows |
| `PLAN RESUME` | "We resume now": the standby cue's planned start becomes the clock and the rest of the day moves with it (RESUME NOW on the Run surface) |
| `PLAN CATCHUP` | The lateness made up before the next break, lunch or end by squeezing the planned lengths in proportion, never below 30 s a cue (CATCH UP on the Run surface) |
| `CUE LIST` | `OK <json>` — the whole list with notes, summaries, broken reasons and each cue's plan (`plannedStart`, `plannedSeconds`, `followSeconds`, `mark`); `listRev` changes when the list does |
| `MENU SCREEN <n>` / `MENU PGM` / `MENU PREVIEW` / `MENU CUE <number\|name\|standby>` / `MENU LOOK <name>` / `MENU LT <n\|name>` / `MENU PERSON <name>` / `MENU LAYER 1\|2` / `MENU CLOCK` (`LOGO`, `MESSAGE`, `PIP`, `WEATHER`, `BADGE`, `INFO`, `COUNTDOWN`) / `MENU MONITOR` | `OK <json>` — the right-click menu the desk would show for that thing, as the desk builds it now: `kind`, `subject`, `title`, `subtitle`, `tone`, `hue`, then `groups[]` (`heading`, `tone`, `note`) of `entries[]` — each with `id`, `text`, `detail`, `scope` (preview, live, stack, go, ask), `tone`, `wire` (the line that does it, "" when the wire has no word), `because` (why it cannot be chosen now; `enabled` false), `on` (the tick), `page` and `item` for a go-to, `question` for an ask, and `children[]` for a drawer. A tablet or a script offers the desk's own choices and sends the entry's `wire` back; the desk's own edits (a cue's look, a layer's source, a tile's ARM) have no wire line yet and are the desk's to press. Bare `MENU` is the programme's; a stranger answers `ERR` with what MENU takes |
| `STOPALL` | Stops the audio track, break music, any VOG or stinger (a clip or a held frame reverts, no ending runs) and the tone — never outputs, blackout or the stream (one token: an older build reads `STOP ALL` as `STOP`) |
| `HELLO <name>` | Names this connection: history and the journal read "GO from tcp FOH deck" |
| `AUTH <token>` | Presents the show's pairing token (Remote page, TRUST): `OK paired`, `ERR wrong token …`, or `OK open …` on a desk that asks for none. Five wrong tokens close the connection; a mutating verb before it answers `ERR not paired …` |
| `STATUS` | `OK <json>` — same payload as the STATE pushes |
| `PING` | `OK PONG` |
| `TWIN STATUS` | `OK <json>` — the twin link: `role` (off, main, standby), `phase` (listening, connecting, inStep, mainSilent, tookOver, refused), `words` (the Machine page's line), `main` (the main's name as a standby knows it), `standbys` (the names a main has in step), `holder` (the standby that has the show, or empty), `launcher` (what the standby process the main runs is doing, or empty), `takeOverCue` and `takeBackCue` (the wall switch cues, or empty), `clock` (the peer's clock against this desk's as the beats measured it — `offsetMs`, `roundTripMs`, `samples`, `apart` past two seconds, `followed` on a caller or a timer whose room clock reads the desk's frame, and its `roomOffsetMs` — or null before an exchange closed), `clocks` (on a main: each peer's `name`, `offsetMs` and `apart`), `handover` (the last handover run here — `id`, `kind` takeOver or takeBack, `shape` sameMachine or acrossMachines, `stage`, `complete`, `stopped` with the reason, `awaiting` — the standby's answer to the hand-back, overdue or not, or the operator's switch, or empty — and the `trail` of stages with its stops, resumptions and notes — or null). The hand-back is answered: HANDBACK carries the handover's id, the standby answers RELEASED by it after closing its outputs, and the main counts the standby released on that answer (or, on one machine, its marker gone or its process gone), never on the line having been written; a claim made under a takeover the main already took back is answered with the hand-back again, not a hold. A takeover by itself needs a fence that answers: the wall-switch cue must send to a box that confirms at Accepted or better; a take-back whose route nobody's box vouches for is two presses, the room switched by hand between them. A main needs a key: one with none is given one before its port opens, and a join that cannot prove it is refused whatever it claims. Every BEAT carries its sender's wall clock and the echo of the last beat heard — its stamp and the arrival — so each side measures the other's clock as NTP does, believes the exchange with the shortest round trip, and says on its line when the clocks are two seconds apart; a caller or a stage timer node reads the desk's absolute times on the desk's clock. The key is never on the wire (link version 2): a joiner's JOIN carries a nonce, the main answers CHALLENGE with a nonce of its own and its proof of the key over the joiner's, and the joiner answers PROOF over the main's — a main that cannot prove the key gets no proof and no standby. The SHOW and SECTION lines carry the mirrored sections only — the twin's key, the admin passcode, the management token, the remote's ports and the machine's other own sections never travel — and the show's credentials (a projector's password, the weather key) travel unless the main's TWIN block says not to send them, in which case the standby keeps the ones typed on it |
| `TWIN TAKEOVER` | The standby twin runs the show from here: the takeover is marked on disk, a hung main on this machine is ended and confirmed gone, its hold on the outputs lifts, what the main had on air goes on with the caller's place, and the wall switch cue (Machine page, TWIN) fires — `ERR` on a desk that is not a standby, and `ERR` with the reason when the hung main cannot be ended or the takeover cannot be marked on disk (the outputs stay held). A cue that sends to a box that answers is confirmed by the box's receipt, not by having fired: the outputs open once it has said yes (the verb answers `OK` as requested and the words follow on the status line); by itself, a box that said no or nothing refuses the takeover and the hold stays, and a box that cannot answer at all (a UDP datagram) is not a fence for taking over by itself |
| `TWIN TAKEOVER FORCE` | The same over a refusal: taken over anyway, the reason in the words — by hand only; nothing takes over by itself this way |
| `TWIN STANDBY` | A twin that took over follows the main again: the link is dialled and the outputs are held closed |
| `TWIN TAKEBACK` | The main takes the show back from a standby that ran it, in the order the room needs: across machines the standby's show and air land here and the outputs open here first, then the take-back cue points the room at this desk and its boxes answer, and only then is the standby told to close its own and follow again; a switch that could not fire, said no or said nothing stops the hand-back with the standby still up and the room still on it — `ERR` with the words, and TAKE BACK again finishes it once the wall is switched. On one machine the standby lets go first and this desk's picture goes up after, never two sets. `ERR` on a desk that is not the main, or when no standby has the show, or when the one that has it is not on the link, or while a hand-back is waiting on its switch |
| `SHOWLOCK ON` / `OFF` | The show lock: Windows notifications, system sounds, other apps' audio, the shortcut keys, sleep and the Windows key held off for the show — and put back. `SHOW-LOCK` and `MACHINELOCK` are aliases |
| `SHOWLOCK STATUS` | `OK <json>` — what the lock holds, item by item, and whether Windows Update has a restart pending |
| `CALIBRATE RUN <camera>` | The camera calibration through an NDI source: every projector shows the structured light in turn while the camera is read, then the rig is solved — `OK` at once, `CALIBRATE STATUS` follows it; nothing moves until APPLY. `ERR` with no projector or a camera that cannot be opened |
| `CALIBRATE CANCEL` | Stops a run; the outputs show the show again |
| `CALIBRATE DEMO` | A solve against a room that is not there — the report's words without a projector or a camera. RUN, DEMO, APPLY and UNDO are refused while the stack is armed and the outputs are live (`ERR Not while…`): a calibration takes the room's screens; DISARM first, or OUTPUTS OFF |
| `CALIBRATE APPLY` / `UNDO` | Each projector into its solved place with its mesh and its blend mask, its blend zones off; and back as it was before APPLY |
| `CALIBRATE STATUS` | `OK <json>` — `running`, `progress` (0–1), `status`, `solved`, `applied`, `canvas` (`width`, `height`), `projectors` (each `id`, `name`, `x`, `y`, `mesh`, `coverage`, `residualPx`, `words`) and the `report`. `CALIBRATION` and `CAL` are aliases of the verb |
| `NODES` | `OK <json>` — the nodes the beacon hears: `me` (this instance's `kind`, `machine`, `instance`, `link`), `linked` (callers on this desk's link), `nodes` (each `instance`, `kind` — desk, caller, timer, arcade — `name`, `address`, `show`, `live`, `fresh`, `link`, `http`, `heardSecondsAgo`) |
| `TIMER PAUSE` / `RESUME` | The stage timer (the countdown overlay's clock) holds its remaining seconds, and runs on again from them |
| `TIMER ADD <seconds>` / `TIMER MINUS <seconds>` | The clock nudged — `TIMER ADD 90`, `TIMER ADD 1:30`, `TIMER MINUS 30`; `TIMER +60` and `TIMER -30` are the same |
| `TIMER FLASH` | The stage pages blink for three seconds — the speaker's eye to the clock |
| `STAGE <words>` / `STAGE MESSAGE <words>` | A message to the speaker's stage page, kept until the page's ACK; the receipt reaches the desk's status line |
| `STAGE CREW <words>` | The same to the crew's page |
| `STAGE CLEAR` | Every pending message marked seen |
| `STAGE ACK <id>` | A stage display's receipt of one message, by the id the payload carries — what the page's ACK sends; a timer node forwards it to the desk it follows |
| `STAGE FLASH` | As `TIMER FLASH` |
| `STAGE STATUS` | `OK <json>` — `rev`, `timer` (`phase` idle/running/paused/over, `remaining`, `text`, `colour`, `progress`, `label`, `paused`, `amber`, `red`, `flashUntilUtc`), `segment` (the running order's current cue and the next), `messages` (each `id`, `text`, `channel`, `sentUtc`, `ackUtc`, `flash`, `from`, `seen`) |
| `ARCADE START <game> [players]` | A match on the arcade — `pong`, `snake` or `breakout` (or its number), for that many people (P1 first; the rest is the house); `ARCADE pong 2` is the same. On a desk the verb goes to the arcade nodes the beacon hears, and with none heard runs the game on the desk's own Arcade page |
| `ARCADE STOP` / `PAUSE` / `RESUME` | The title card; a match held; on again |
| `ARCADE ATTRACT [game]` | The house plays itself — the attract screen with the board; START on a pad joins |
| `ARCADE KEY <pad> <button> [DOWN\|UP\|TAP]` | A pad's key from a Stream Deck or any client: the pad 1–4, UP DOWN LEFT RIGHT A B START; TAP (the default) is held a tenth of a second |
| `ARCADE SIZE <w>x<h>` | The picture's size — `3840x1080` for a joined canvas of two projectors |
| `ARCADE NDI ON` / `OFF` | The picture on the network as `PATTERNS ARCADE (<machine>)` for a game on another machine; the desk puts it on a screen or a canvas like any NDI source. Sent from a lane of its own, unclocked, so the send never costs the game a frame. On the machine the game runs on, no NDI is needed: Arcade is a source a pattern's media, a layer, the PiP and a wall tile can pick, straight from the loop |
| `ARCADE WINDOW [ON\|OFF\|FULL [display]]` | The game's own window on this machine: `ARCADE WINDOW` opens it, `ARCADE WINDOW FULL 2` fills display 2 (its number on the Screens page; no number, the display the window is on), `ARCADE FULLSCREEN` is the same, `ARCADE WINDOW OFF` closes it. In the window the keys are the pads, F11 fills or brings the window back, Esc brings it back and then closes it |
| `ARCADE NAME <initials>` | Signs the last score on the board |
| `ARCADE STATUS` | `OK <json>` — `phase` (idle, attract, joining, playing, paused, over), `game`, `title`, `seats`, `scores`, `humans`, `winner`, `step`, `seed`, `difficulty`, `words`, `running`, `fps`, `size`, `ndi` (`on`, `name`, `status`, `receivers`), `source` (`wanted` — a picture on this machine's show is taking the frames — `key`, `copies`), `window` (off, on, full), `skipped` (frames the loop drew nowhere because every buffer was being read — 0 on a healthy machine), `board` (the best five), `games`. Through a desk that hears arcade nodes: a list, one entry per node with its `status` |
| `ARCADE GAMES` / `ARCADE SCORES [game]` | The catalogue; the board's best ten for a game |
| `PLAY ADD <kind> <text> \| option \| option [\| correct=N time=S scale=A-B]` | A question for the room: `choice`, `multi`, `scale`, `words` or `quiz` — `PLAY ADD quiz Which hall is the keynote in? \| A \| B \| C \| correct=2 time=15`. Added as a draft |
| `PLAY OPEN [id]` / `PLAY NEXT` | Opens a question (the next draft when none is named); the one that was open closes; the wall shows the results |
| `PLAY CLOSE` / `PLAY REVEAL` | Closes the open question; shows the answer and the results (a quiz's right option lit) |
| `PLAY SHOW join \| results \| leaderboard \| message [words] \| draughts \| path \| off` | The wall's picture, on the arcade's lane (the window and NDI); `PLAY HIDE` is `off` |
| `PLAY MESSAGE [room \| group:<name> \| phone:<nick>] <words>` | A message back — to every phone, one table, or one phone by its nickname |
| `PLAY APPROVE <id> \| all` / `PLAY REJECT <id>` | The queue: a word for the cloud or a shout lands on the wall, or does not |
| `PLAY AUTO ON` / `OFF` | Words straight to the wall without a press (a quiz night), or waiting for the host |
| `PLAY PATH OPEN` / `CLOSE` / `RESET` / `RELOAD` | The story's vote opened; closed (the winning option takes it on); back to the start; the file read again |
| `PLAY DRAUGHTS` | A new board; two phones take the sides on `/play` |
| `PLAY RESET` / `PLAY NEW` | The night cleared (questions kept as drafts); a new room code — every phone joins again |
| `PLAY EXPORT` | Everything to a file in the hub's folder — nicknames, never tokens |
| `PLAY STATUS` / `PLAY RESULTS [id]` / `PLAY QUEUE` | `OK <json>` — the room; a question's results; the queue. Through a desk that hears a hub: a list, one entry per hub |
| `ASSISTANT ASK <words>` / `ASSISTANT MODERATE <text>` | The desk's assistant from a node — only when the desk allows it (Remote page, *Nodes may ask the assistant*; off, `ERR`): one ask, `OK <json>` with `sent`, `status`, `inScope`, `reply` — a hub's queue asks MODERATE and reads one word: FINE, DOUBTFUL or OUT |
| `AUDIENCE ON [port]` / `AUDIENCE OFF` | The audience listener — the phones' own port, the play pages and nothing else — opened (on the port given, or the one set) or closed; a control setting, never a cue |
| `AUDIENCE STATUS` | `OK <json>` — `enabled`, `listening`, `port`, `bind`, `urls`, `joinUrl`, `players`, `maxPlayers`, `connections`, `longPolls`, `longPollsPeak`, `network` (`flat` or `venue-nat`) and its `networkWords`, `joinsRefused` this minute and `refusedFrom` (the address most came from), the `budget` as the profile reads it, `assistantOnWire` |
| `RIGDAY ON` / `OFF` | Rig day's games, opt-in: the show-ready bar on the health line, the alignment game and Blend Quest on the Screens page, the on-time streak on the Run surface — off, nothing of them shows |
| `RIGDAY STATUS` | `OK <json>` — `enabled`, `ready` (`done`, `total`, `bar`, `words`, `next`, `steps`), `align` (the game, or null), `quest` (`words`, `levels`), `streak`, `celebration` (the moment being marked: `kind`, `words`, `chip`, `phase`; null when none) |
| `ALIGN START <screen>` | The alignment game on a projector with a calibration (the solver's mesh as the targets, ringed on the projector's lattice); `ALIGN NEXT` / `PREV` walk the open nodes, `ALIGN NUDGE <dx> <dy>` moves the lit node in the output's pixels, `ALIGN SNAP` lands it on its target, `ALIGN STOP` ends it; `ALIGN STATUS` is the game as JSON. The desk's own — never a cue |

One library, one numbering: `STINGER 3`, `VOG 3` and `STING 3` all mean library item 3 in
Audio-page order — there is deliberately no per-kind numbering, because two numbering schemes on a
live desk is how the wrong button gets fired. Like `STINGER`, the `VOG` and `STING` verbs need no
client header over HTTP. `SPOTIFY …` is accepted as an alias for every `MUSIC …` verb. With break music switched off on the
Audio page the `MUSIC` verbs answer `OK` and do nothing — a saved button never breaks a cue — while a
name that resolves to no entry is an `ERR` on or off. Patterns drives the Spotify app (Premium and
your own Client ID, set up on the Audio page); it never plays the audio itself.

While the caller's stack is armed, `LOOK` and the other content commands still work and are
journaled with your name; the daily schedule, playlist part start times and plain F-keys on the
desk wait. A refused GO always says why: `ERR GO 03.020 refused — not armed`, `held`, `blackout
is on — lift it first`, `standby moved`, `too soon after the last GO`, or the cue's broken reason.

State JSON carries: `rev` (bumps on every change — long-poll on it), `airLabel` (what is on air, by name),
`cuestack{armed,hold,seq,listRev,confirm,program{label},previous{id,number,name},standby{id,number,name,requireConfirm,notes,plannedStart,followSeconds},next[6]{id,number,name},last{id,number,name,outcome,error,at,origin,actionsDone,actionsTotal,execution,pending},history[8],timing{offsetSeconds,offset,nextBreak{number,name,expected,planned,deltaSeconds,atLeast,text},lunch{…},end{…},follow}}`
(`last.execution` is the cue run's id — eight hex characters, the one its device lines and receipts carry — and `last.pending` the device receipts it still waits for: the outcome reads `Requested` until they land, then `Done`, `Done, with warnings` or `FailedLate`. `timing` is the caller's clock: `offset` reads "ON TIME", "3 MIN LATE" or "2 MIN EARLY" from the last GO against its planned start; `nextBreak`, `lunch` and `end` say when the marked cues are expected — `atLeast` when a cue has overrun or has no planned length; `follow` reads "AUTO 01.030 in 0:07" while the next cue is going to fire by itself)
(the stack's runtime is pushed on its own event, throttled like everything else), `blackout`, `black{count,text,audio,targets[]}` (the screens faded to black on their own — how many, their names in one line (*Screen 2 · A · Main wall*), whether the programme's sound is down with the picture, the names as a list; the blackout is separate and covers everything), `live`, `review` (the preview fills every multiview), `frozen` (every output holds its frame), `previousLook` (the name `LOOKBACK` returns to, or empty), `airLook` (the look on air, by name — empty when none was recorded or the picture moved on), `previewLook` (the look loaded in the preview while EDIT SAFE is open), `pattern` (the kind of picture on air: `Media`, `LedWall`, `ProjectionBlend`…), `patternKinds` (every kind a `PATTERN` key can ask for, in the desk's order), `looks[{n,name,slot,air,preview}]` (the show's looks in order — `n` is the place `LOOK #n` uses, `slot` the F-key or 0, `air` / `preview` where it is), `lookEdited` (the look on air has been changed since it was recalled — the difference between a key that says "this look is up" and one that says "this look is up and somebody has been at it since"; only the row with `air` can be edited, so the fact rides once rather than on all sixteen rows), `lookScreensOff` (how many screens have gone their own way inside it — a locked screen never counts, because keeping its picture through a recall is what LOCK means), `presenter{armed,index,count,steps[]}`,
`screens[{n,label,enabled,group,locked,role,armed,ticked,own,black,pattern,off}]` (labels honour operator names; `role` is main, confidence, info or repeater — the screen's group on the desk; `armed` = the next CUT / TAKE changes it; `ticked` = the tick at the top of its wall tile, which a TICKED take, a fade or SEND TO TICKED reads (round 67); `own` = it shows a picture of its own, not the program's; `black` = faded to black on its own by `FADE … SCREEN n` and the like; `pattern` = the kind of picture this screen is actually drawing, which is not the same as the programme's once a screen has its own; `off` = it is not showing what the look on air asked of it — `own` cannot answer that, because a look very often gives a screen its own picture on purpose), `editSafe` (EDIT SAFE is open: there is a preview and a TAKE to come), `take{scope,scopeLabel,words,where,taken[],held[{id,label,reason}],outside,refusal,next{set,words,wire,sting},landing{sting,scope,targets[],where,words,pressedUtc}|null}` (round 67: the wall's take plan as the desk's picker has it — the scope's words and label, what the next TAKE will change and where, the targets it takes and the ones it holds with the reason (locked, not armed, a repeater), how many are outside the scope, the refusal when it would move nothing, and the one-shot pending on the next TAKE; round 72: `landing` is the ticket a TAKE under a video sting froze at the press — the sting, the scope words, the targets it promised, where, the words and when — while the clip runs, null otherwise: the landing runs that ticket, less only a screen locked since or gone from the rig, never more),
`audio{playing,track,n,count,next,position,length,remaining,positionText,lengthText,remainingText,shuffle,loop,level,status,items[{n,name}]}` (the audio playlist: `track` is the track on — or, stopped, the one PLAY would start — `n` its place (0 with nothing on) of `count`, `next` the one after it, the clock in whole seconds and as `m:ss`, the list's flags and level, its status line, and the rows by place — `AUDIO PLAY <n>`), `tone`,
`stingers[{n,name,kind,source}]` (`kind` is `vog` or `sting`; `source` is `file`, or `pulse` for an effect pulse — a surge through the particles and fractals on screen that owns nothing), `stingerPlaying` (whatever owns the show), `stingerKind`
(`vog` / `sting` / empty), `vogSound` (a VOG sound playing over the show — over a stinger too, which it ducks
rather than stops; empty when none), `stingHold` (the name of a stinger holding the screens, or empty), `duck` (the live duck is on),
`lowerThirds[{n,name}]` (the designs, Lower thirds page order), `lowerThird` (the design on screen — arriving, holding or leaving — or empty),
`people[{n,name,role}]` (the library, page order — `PERSON n`), `lowerThirdPerson` (the name the lower third on screen carries, or empty),
`lowerThirdPreview` / `lowerThirdPreviewPerson` (the design and the name in the preview for a sign-off, or empty), `lowerThirdDefault` (the show's ★ design),
`lowerThirdEdited` (true while the design on air differs from the edited one — `LOWERTHIRD UPDATE` pushes the edit),
`web{page,url,title,service,fps,actions[{id,label}],player{pos,dur,text,paused,ad,muted}|null,arm{armed,at,atText,byLook,played,phase,words}|null,path{words,smoothing,depth,latencyMs,jitterMs,decodeMs,deliveredFps,presentedFps,underruns,dropped,duplicates,held,poolStarved,poolBytes,capture}|null,via,native{phase,words,stream,separateAudio}|null}` (the web page the program shows — its nickname or host, its address and title, its service when Patterns knows it (YouTube, Vimeo, Google Slides, PowerPoint for the web), the frames it delivered in the last second (a video's rate; 0 for a still page), the actions `WEB KEY <id>` takes on it, what its video player last said (its place and length in seconds and as `1:23 / 4:56`, paused, an advert showing over it) and the arm on it (armed, the mark in seconds and as text, whether the look put it there, whether it has played, `phase` — round 58: what the page itself reported, `Unprepared`, `PrepareRequested`, `PreparedObserved`, `FireRequested`, `PlayingObserved` or `Failed`, never what the desk sent — and the words, which end with the phase), and `path` — round 68: how its picture reaches the glass — the smoothing in words (`smooth 2 (67 ms)`, `low latency`, `auto → smooth 2 (67 ms)`, `auto · measuring`), the buffer's depth in frames (0 when the newest frame is shown at once), the delay it adds in ms, the measured jitter (the p95 lateness of a frame against the page's cadence) in ms, the decode into the pool in ms, the frames delivered and presented in the last second, the stalls (times the buffer ran dry while a video played), the frames dropped (a full ring, or due together with a newer one) and skipped as duplicates (the browser re-sent an unchanged picture), the frames waiting, the pool's starvations and bytes, and `capture` — what the browser is asked to hand over (`captured at 1280×720 · q60 · every 2nd frame`); `null` for a source with no pipeline; `via` — round 68.6: who plays the page's video, `browser` or `native player` (the page's stream, found by yt-dlp, playing through libVLC under the page's own key), and `native` — the native player's story for the page: `phase` (`resolving`, `ready`, `failed`, `notool`), its `words`, the stream's host and whether its sound comes apart; `null` for a page whose look never asked; `null` with no page on air),
`webArmed{page,key,at,atText,byLook,preRolled,phase,words,short}|null` (the armed web VT anywhere on the desk — a page in the preview, one opened early for a cue ahead (`preRolled`), the page on air — its name, its mark, its observed `phase` and its words, `short` being `VT armed at 1:23`; `null` with nothing armed),
`video{file,role,tag,position,length,remaining,positionText,lengthText,remainingText,text,chip,playing,ended,loops,out,call}` (the caller's VT clock — the clip on air: its file, its `role` (`program`, `playlist`, `stinger`, `layer`) and `tag` (`VT`, `AUDIO`, `STINGER CLIP`, `PLAYLIST`), where it is, how long it is and what is left in whole seconds and as `m:ss`, `text` as the desk reads it (*VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left*), `chip` (*VT 2:28*), `loops` when it never comes out, `out` for its last ten seconds with `call` the caller's word (*OUT IN 7*); `null` with no clip on air. Pushed every second while a clip runs — only then, and only while a controller listens),
`weather{on,view,place,text,figure,sky,source,status}` (the weather chip: on air, its view — `now`, `day` or `tomorrow` — the place, the line the desk reads (*Manchester · 18° · Light rain · wind 12 km/h*), the figure alone (*18°* or *14–19°*), the sky's glyph name, the source and the fetch status),
`overlays{clock{on,hours,seconds,date,text},message{on,text,scroll},countdown{on,phase,label,target,remaining,text},logo{on,file},pip{on},text}` (the overlays a remote drives: the clock's switch, its hours (12 or 24), its seconds and date line and what it reads now; the message's switch, words and scroll; the countdown's switch, its `phase` — `running`, `over` or `off` — its label, its target (*19:30* or *15 min*), what is left in whole seconds and as the desk reads it (*12:34 · DOORS IN*, *OVER · STARTING NOW*); the logo's switch and whether a file is set; the PiP's switch; and one line — *Clock 24 h with seconds · Message: WELCOME · Countdown 12:34 to 19:30 · Logo*, or *No overlays on.* Pushed every second while a countdown runs, like the VT clock),
`deck{file,kind,page,count,ended,endsWithGo,converting,status}` (the deck the program shows: its file and its kind — `PDF`, `PowerPoint`, `Keynote`, `Impress` — the page on show and the count, `ended` on its last page, `endsWithGo` when the next click there GOes the standby cue, `converting` while LibreOffice is still making the PDF of a PowerPoint (the count is 0 and `status` reads *Converting…*, or why it could not); `null` with no deck on air),
`interactive` (the Interactive area is on) and `devices[{n,name,link,address,enabled,open,status,lastIn,lastOut,lastCommand,lastCommandUtc,lastReply,lastReplyUtc,confirmed,confirmedUtc,observed,observedUtc,lastFailure,lastFailureUtc,failing,history}]` (every device of the Interactive page — its link is `serial`, `tcp` or `udp`, `open` while its wire is up, `status` the page's words, the last line each way, and what the box did lately with its times: the last line sent to it, its last reply, the level its last good receipt reached (`sent`, `delivered`, `accepted`, `observed`), the state it was last observed in, the last failure — a no, a silence, a port that would not open — `failing` while that failure is its last word, and `history` the card's own line with the ages, *Last sent POWER ON (3 s ago) · reply POWR: OK — accepted (3 s ago)*),
`install{on,site,programme,idle,over,overKind,overUntil,next,status,slots[{n,name,kind,enabled,status}],problems,update{staged,version,ok,running,supervised,status,last},management}` (the Install page: the schedule's switch, the programme on, the announcement or advert on and until when, the next change, every row and its state, the staged update, the check-in's line — see `docs/INSTALLS.md`),
`sections[{n,name,active}]`, `playlist`, `nextCue`,
`music{on,playing,level,now,device,status,items[{n,name}]}` (break music — `now` is the track
Spotify reports, `status` the same sentence the Audio page shows),
Remote commands always drive **what the audience sees**: looks, cues, playlist parts, stingers
and transport apply to the program even while the operator is building the next look in the
sandboxed preview.

State JSON also carries `version` (this build), `decks[{name,module,address}]` (every deck that said
HELLO on the wire and what module it runs), `linked` (the caller nodes linked to this desk), `nodes[{n,instance,kind,name,show,live,fresh,words,address,http,link,heardSecondsAgo}]`
(every other Patterns heard on the beacon, in the Nodes page's order — `fresh` false once one stops being heard),
`twin{role,phase,words,main,standbys,holder,clocks,apart}` (the twin as the Machine page reads it) and
`stage{timer{phase,remaining,text,colour,progress,label,paused},segment,next,pendingSpeaker,pendingCrew,flash}`
(the speaker's timer in its own colour word — green, amber, red — the running order's cue and the next, the
messages waiting for their ACK, the flash); a node appearing, the twin's phase moving or a message to the stage
waiting is a push of its own. And `stream{active,status}`, `health`, `quality{mode,level,factor,text}` (the effects' quality ladder: Auto / Full / Balanced / Economy, the level 0–3, its factor, the Machine page's line — why the level is what it is, where Auto started, and the last second judged against its sink's own budget at its rate), `memory{appMB,ceilingMB,text,privateMB,managedMB,pictureMB,framePoolMB,retiringMB,starved,pendingFree,fenceOldestMs,liveSinks,openFrames,hungNow,hungFrames,quarantinedMB,fenceFaults,mediaMB,mediaBudgetMB,pressure,pressureSteps,placed,residency{grace,words,letGo,holds[{key,kind,label,reason,bytesMB,idleS,status}]}}` (the app's working set against its ceiling and the MEMORY CEILINGS line; round 57 adds the private bytes, the managed heap, the picture cache and the frame pools in MB, and `placed` — the ledger's line, the app's memory by owner, largest first; round 58 adds what waits behind the render fence in MB (`retiringMB`: a pool's replaced buffers, scratch frames and evicted pictures, freed only once every sink that drew them has started a frame since), the frames a starved pool sent the old way, the buffers waiting to be freed, the oldest retired frame's age in ms, the sinks the fence counts live; round 64 replaces the forced frees with the frames open right now (`openFrames`), the sinks whose frame is hung past two seconds (`hungNow`), the frames that hung this session (`hungFrames` — the gate: should stay 0; a number there is a render thread that stalled, named in `fenceFaults` with how long), the bytes those frames hold in quarantine (`quarantinedMB` — never freed under them) and the fault record's lines (`fenceFaults`), the media memory as one figure against its budget (`mediaMB`, `mediaBudgetMB` — pictures, frame pools, retiring frames and decks against six tenths of the app's ceiling), and the pressure ladder's rung (`pressure`: `none` / `elevated` / `high` / `critical`) with the steps it is taking (`pressureSteps`: *retired swept, pictures trimmed, pre-roll held back, decks narrowed, no new preview-only source opened* — the source on air is never touched); round 69 adds `residency` — the residency ledger: the grace an idle thing gets on this machine at this rung in seconds (20 small, 60 standard, 180 big; halved at elevated, quartered at high, none at critical), its words, the idle pictures let go so far on their clock, and every held thing (`holds`: its key, kind and label, the reason it stays — `on air`, `named` by the show, `preview`, `armed`, `pre-rolled`, `retiring`, or `idle` on the clock — its MB, its idle seconds and its status)), `inputs{mounted,retiring,limitNote,pendingNote,pending[{key,target,what,words}]}` (round 58: the decoders mounted and fading out, the limit's words, and the reopens staged under a source on air — a capture mode, low-latency profile, loop or routing change made while the source is on the programme with the outputs live waits until it leaves the air or the outputs go off; `pendingNote` is the earliest's words, *Low latency change pending — Cam Link 4K is on air; applies when it leaves the air or the outputs go off air.*, or empty; each row names the input, the target, the change (`Format`, `Low latency`, `Loop`, `Audio routing`) and its words), `modules[{name,version,native,loaded}]` (round 59: the build's assemblies — Core, Rendering, Ndi, Arcade, Devices, Audio, Assistant, Audience, App — what is native in each, and which this process has loaded, with the version it loaded at; a timer node that never drew has not loaded the render module, and says so here as it does in a support ticket), `machine{cpu,ram,fps,battery,advice,renderFaults,faulting,liveAgeMs,renderClockHz,clockLimited,inventory,rig,gpuCache{hasContext,limitMB,usedMB,resources,purges,rung,words},gc{mode,gen0,gen1,gen2,lohMB,pohMB,lastPauseMs,pausePct,compactions,words}}` — machine load; `commissioning{complete,done,total,stage,headline,next}` (round 65.10) — where the commissioning flow stands, the stage it is at and the next step in words
(percent, -1 = unknown), output frame rate, whether the computer is on battery, and how
many Machine-page suggestions currently need attention, the render faults of the last minute across the outputs (frames whose draw threw; the last good picture was drawn in their place) and whether an output is faulting right now — and `beacon{sending,listening,main}`:
whether this machine sends its heartbeat beacon, whether it listens for a main machine's, and
what it makes of it ("Main machine MAIN seen 1 s ago: live · Walk-in", "MAIN MACHINE MAIN SILENT
for 6 s — … Take over?", or empty when not listening). `liveAgeMs` (round 57) is the oldest camera or feed picture an output drew in the last minute, from its arrival in the decoder to the end of the frame that drew it, in ms — the IMAG number; -1 with no live picture drawn; the card's own delay and the screen's are not in it. `renderClockHz` (round 64) is the platform's render clock as measured, -1 unmeasured, and `clockLimited` names the outputs it holds under their rate. `inventory` (round 65.9) is the machine as Windows describes it in one line — the card, its driver, the displays, the audio outputs, the power plan — and `rig` is the known-good rig's verdict: `not saved`, or the drift's headline (*unchanged since …*, *2 changes since …*); `RIG STATUS` has the whole of both. `gpuCache` (round 69) is Skia's GPU resource cache as governed: whether a sink's draw has held a GPU context (false under software rendering, and the words say so), the limit in MB (the machine class's number — 64 / 128 / 256 — bounded by an eighth of the card's dedicated memory, three quarters at elevated pressure, half at high, a quarter at critical, never under 32), the fill and the resources read back from Skia, the purges asked at high and critical, the rung the governor stands on (the worse of the media ladder's and the card's own use of the video memory the OS grants) and the words; `gc` is the collector: its mode (*sustained low latency (outputs live)* / *interactive (off air)*), collections by generation, the large-object and pinned heaps in MB after the last collection, the last collection's pause in ms, the pause share, the large-object compactions asked (one each time the outputs go off air) and the words. `eye{headline,worst,worstLight,worstWords,red,amber,green,grey,things,problems,focus,lens}` (round 66) is the God's Eye's row — the headline (the worst thing and what is wrong, or *all green*), the worst thing's id, light and words, how many things are red, amber, green and grey, how many are problems, and the operator's view (the id the eye is on, or empty, and the lens); `EYE` has the whole picture.

## Remote multiview

`http://<machine-ip>:9696/multiview` shows the configured multiview (Pattern page →
Multiview) as a live picture refreshing about once a second — program, screens, inputs and
clock with labels and on-air tally. Each tile is drawn at its target's real shape — a joined
canvas is one wide tile, a screen inside one shows its own half — the same picture the wall
shows; a target with no display attached falls back to 16:9. `GET /mv.jpg` returns the current
frame for anything else (tally lights, dashboards); `GET /mv.jpg?w=1280` renders at that width
(320–1920; default 1024).

## The cue stack on a tablet

`http://<machine-ip>:9696/run` is the caller's page: the LIVE strip, the standby cue with its
notes, the next six, the program thumbnail (`GET /pgm.jpg`), the history, and GO / HOLD /
standby ▲ ▼ — GO and HOLD only while the stack is armed, and GO always sent with the standby
id the page last saw. It waits on `GET /api/state?since=<rev>`, a long-poll the server holds
for up to 25 seconds, so it updates within the push throttle instead of polling.

## Bitfocus Companion

Use the **Patterns module** in `integrations/companion-module-patterns/` — **3.0.0, a Companion 5
module** (module base 2.x, node22; Companion 3 and 4 cannot load it, and the last 2.x module is in
the repository's history for them). Import the package this repository's CI builds (the
`companion-module-patterns` artifact, a `.tgz`) under *Modules*, or `npm ci && npm run package` in
the folder; then *Connections → Add → Patterns* and pick the desk under **Desk on the network** —
every Patterns process announces itself over mDNS as `_patterns._tcp` ("Patterns desk FOH-PC",
"Patterns stage timer STAGE-PI"), so nobody types an address; the typed host and port are there
for a network that blocks multicast. The connection says `HELLO <label> module=3.0.0`, and the
desk's Remote page lists every deck connected with the module it runs (and says when one is behind).

What the keys are: the cue stack (GO, standby, HOLD, ARM, STOP ALL, the day's timing, PLAN ±1 MIN,
RESUME NOW, CATCH UP) and a **cue bank** that reads the standby cue and the six after it; the
**look bank** (sixteen keys by place, three states: up, changed since, not up), F1–F12, one key per
look of the show; screens (toggle, lock, back to the program, the picture it is showing, fades) and
canvases; the presenter's keys, decks and web pages, the VT clock; lower thirds with the sign-off
flow, people; VOGs and kind-checked stingers; the audio playlist, break music, parts; the clock,
countdown, message and overlays; **the stage** — the speaker's timer in the timer's own colour with
a progress ring on Companion 5's layered keys, pause / resume, ±1 min, FLASH, WRAP UP and a crew
message that stay amber until the stage page ACKs; **the nodes** — eight bank keys, each in its
kind's colour (desk, caller, arcade, stage timer), dark once a node stops being heard; **the
twin** — its role and phase, two-press TAKE OVER / TAKE BACK, STAND BY; the install; and `raw`, a
line of your own from the table above. Tick the **preset groups** a desk uses and the list holds
only those; the actions, feedbacks and variables are always all there.

One colour language: green on air, armed or running; amber a preview, a hold, *changed since*, a
message waiting; orange late or a screen gone its own way; red a lower third on screen, the stream
live, a failed cue, black on its own; sky blue the overlays; steel blue the presenter's things; a
colour per node kind; dim for a bank key with nothing behind it. The module's `src/palette.js` and
the desk's `CompanionPalette` are held equal by a test on each side, and the Nodes page's cards
wear the same hues.

Tested both ways: the module's own suite boots it against the real module base with a fake host
(every preset through Companion's own preset sanitiser); every line it can send is written to
`test/lines.txt` and parsed by the desk's suite. The built-in **Generic TCP** connection still
works with the raw commands above (no feedback).

**The desk drives the deck too.** Companion's own TCP API (port 16759) is a device profile on the
Interactive page — **+ COMPANION**, the address filled in from the Companion heard announcing
itself on the network — so a cue, `DEVICE Companion PAGE 3` on the wire, OSC or the assistant
turns the Stream Deck to a page, `PRESS 2/0/1` fires one of its buttons, `VAR speaker Jane Doe`
fills a custom variable; Companion answers `+OK` or `-ERR`, so the receipt reads Accepted or the
refusal by name. `docs/ENDPOINTS.md` has the words; `docs/PLAN.md` §72 the round.

The wire keeps ceilings: 64 connections open in all and 16 from one address — the next reads
`ERR busy — 16 connections already open from this address; close one first` and the door closes
— and a line that has started has ten seconds to end (`ERR a line that did not end within 10 s —
the wire's lines are commands, and this one was not; closed`); a connection that sits idle between
presses is never cut. The web remote's port keeps 256 and 64 the same way, answering `503 busy`
with a Retry-After. Each refusal is logged once a minute per address. `docs/PLAN.md` §66.3.

## OSC

Tick **OSC in** on the Remote page (SETUP) and Patterns listens on UDP port 9698 (configurable)
while remote control is on — for QLab's network cues, TouchOSC, a lighting desk with OSC out, or
Companion's generic OSC module. Every address starts `/patterns/` and maps onto the one-line
protocol above, so a message means exactly what its line means, with the same checks, the same
journal entry (the origin reads `osc 10.0.0.5:53001`) and the same answers. A number, a name or a
switch rides as the next address segment or as the first argument — `/patterns/look/3`,
`/patterns/look 3` and `/patterns/look "Walk-in"` are the same — and a switch is `1` / `0`, a
float above 0.5, a bool, or the words `on` / `off` / `toggle`. Bundles are read in order.

| Address | Means |
|---|---|
| `/patterns/outputs 1\|0` | OUTPUTS ON / OFF (also `/patterns/outputs/on`, `/off`) |
| `/patterns/blackout [1\|0]` | BLACKOUT ON / OFF; no argument toggles (also `/on`, `/off`, `/toggle`) |
| `/patterns/identify` | IDENTIFY |
| `/patterns/look <n\|name>` | LOOK n / LOOK name (also `/patterns/look/<n>`) |
| `/patterns/look/index <n>` | LOOK #n — the *n*th look in the show's order, whatever its name or F-key (also `/patterns/look/index/<n>`, `/patterns/look/bank/<n>`) |
| `/patterns/next`, `/patterns/prev` | NEXT / PREV — the clicker list |
| `/patterns/screen/<n> [1\|0]` | SCREEN n ON / OFF; no argument toggles |
| `/patterns/screen/<n>/look "<name>"` (or `/screen/<n>/look/<name>`) | SCREEN n LOOK name — that look's picture on screen n alone |
| `/patterns/screen/<n>/program` (or `/pgm`, `/follow`) | SCREEN n PROGRAM — screen n back to the program |
| `/patterns/screen/<n>/pattern "<kind>"` (or `/screen/<n>/pattern/<kind>`) | SCREEN n PATTERN kind — that kind of picture on the screen alone, live |
| `/patterns/screen/<n>/take` · `/patterns/screen/<n>/cut` | SCREEN n TAKE / CUT — the desk's preview to that screen alone, as its own picture (round 63) |
| `/patterns/screen/<n>/pvw/look "<name>"` · `/pvw/preset/<name>` · `/pvw/pattern/<kind>` · `/pvw/program` · `/pvw/reset` · `/pvw` | SCREEN n PVW … — staged on that screen's PVW in the preview; nothing changes on air until CUT or TAKE (`/preview/…` is the same) |
| `/patterns/screen/<n>/audio "<output>"` (or `/screen/<n>/audio/<output>`, `/sound`) | SCREEN n AUDIO output — the output that screen's sound leaves by; `"off"` for none (round 69) |
| `/patterns/pvw/pattern "<kind>"` · `/pvw/preset "<name>"` · `/pvw/look "<name>"` · `/pvw/reset` · `/pvw/program` · `/patterns/pvw` | PVW … — the programme's picture in the preview, never on air |
| `/patterns/lock/<n> [1\|0]` | LOCK n ON / OFF; no argument toggles |
| `/patterns/group/<letter> 1\|0` | GROUP A ON / OFF — a joined canvas |
| `/patterns/audio/play [n\|name]` | AUDIO PLAY — the audio playlist plays: a track by its place or its name, or the list resumes (also `/patterns/audio/play/<n>`, `/patterns/track/…`) |
| `/patterns/audio/stop`, `/patterns/audio/next`, `/patterns/audio/prev` | AUDIO STOP / NEXT / PREV |
| `/patterns/audio/volume <level>` | AUDIO VOL: an integer is percent (0–125), a float from 0.0 to 1.0 is a fader (× 100) |
| `/patterns/audio/routing on\|off\|toggle` | AUDIO ROUTING |
| `/patterns/audio/route "source" "destination" [dB]` | AUDIO ROUTE — a source on a destination at a level |
| `/patterns/audio/unroute "source" "destination"` | AUDIO UNROUTE |
| `/patterns/audio/vog "destination" duck\|replace\|leave` | AUDIO VOG |
| `/patterns/audio/follow on\|off\|toggle` | AUDIO FOLLOW — the sound follows the picture (round 69) |
| `/patterns/music/play [n\|name]` | MUSIC PLAY — break music (Spotify), an entry by number or name |
| `/patterns/music/pause`, `/patterns/music/next` | MUSIC PAUSE / NEXT |
| `/patterns/music/volume <level>` | MUSIC VOL: an integer is percent, a float from 0.0 to 1.0 is a fader (× 100) |
| `/patterns/tone 1\|0` | TONE ON / OFF |
| `/patterns/duck [1\|0]` | DUCK ON / OFF; no argument toggles |
| `/patterns/stinger <n\|name>` | STINGER n / name (also `/patterns/stinger/<n>`); `/patterns/stinger/stop` |
| `/patterns/vog <n\|name>`, `/patterns/sting <n\|name>` | VOG / STING — kind-checked, like the TCP verbs |
| `/patterns/lowerthird <n\|name> [person]` | LOWERTHIRD n / name, with a library entry when a second argument names one (also `/patterns/lt`; `/patterns/lowerthird/2/3` is design 2 with person 3) |
| `/patterns/lowerthird/off` | LOWERTHIRD OFF |
| `/patterns/lowerthird/preview <n\|name> [person]` | LOWERTHIRD PREVIEW — the design (with a library entry) into the preview for a sign-off (also `/patterns/lowerthird/preview/<n>/<person>`, `/patterns/lt/pvw …`) |
| `/patterns/lowerthird/preview/off` | LOWERTHIRD PREVIEW OFF |
| `/patterns/lowerthird/take` | LOWERTHIRD TAKE — the lower third in the preview to air |
| `/patterns/lowerthird/update` | LOWERTHIRD UPDATE — the design on air replaced by the design as it is now, in place |
| `/patterns/person <n\|name>` | PERSON — a library entry into the lower third on air (else the show's ★ default design) |
| `/patterns/web/key <key\|action> [page]` | WEB KEY — a key chord or a page action to the web page on air, or to the page a second argument names (also `/patterns/web/key/ArrowRight`, `/patterns/page/…`) |
| `/patterns/web/next`, `/prev`, `/first`, `/last`, `/present`, `/exit`, `/play`, `/pause`, `/mute`, `/restart`, `/black`, `/white`… `[page]` | WEB <action> — the page actions as addresses of their own |
| `/patterns/web/click <x> <y>` | WEB CLICK — a click at a point in percent of the page (also `/patterns/web/click/50/50`; floats up to 1.0 are fractions) |
| `/patterns/web/type "text"` | WEB TYPE — text into the field that has the page's focus |
| `/patterns/web/reload [page]` | WEB RELOAD |
| `/patterns/web/open "address" [page]` | WEB OPEN — the page's browser sent to another address |
| `/patterns/web/arm [time] [page]` | WEB ARM — the page's video armed to play from a point when the page goes to air (also `/patterns/web/arm/1:23`); no time = the mark set, else where the player is now |
| `/patterns/web/mark [time] [page]` | WEB MARK — the start point set without arming |
| `/patterns/web/disarm [page]` | WEB DISARM |
| `/patterns/deck/next`, `/prev`, `/first`, `/last` | DECK NEXT / PREV / FIRST / LAST — the deck (PDF) on air turns a page |
| `/patterns/deck/page <n>` | DECK PAGE n (also `/patterns/deck/page/5`, `/patterns/deck 5`) |
| `/patterns/video/end [seconds]` | VIDEO END — the clip on air jumps to its last seconds (none: ten), the rehearsal's skip (also `/patterns/video/end/5`, `/patterns/vt/end`) |
| `/patterns/video/restart` | VIDEO RESTART — the clip on air from the top (also `/patterns/video/start`, `/patterns/vt/restart`) |
| `/patterns/section <n\|name>` | SECTION — a playlist part |
| `/patterns/device/<name> "text"` | DEVICE name text — a line to a device of the Interactive area (also `/patterns/device "name" "text"`, `/patterns/device/Arduino/RELAY 1`, `/patterns/send/…`; `*` is the first device) |
| `/patterns/announce "name or words"` | ANNOUNCE — an announcement of the Install page by name, else the words (also `/patterns/announce/<name>`); `/patterns/announce/off` ends it |
| `/patterns/advert "name"` | ADVERT — an advert of the Install page now (also `/patterns/advert/<name>`, `/patterns/advert/<n>`); `/patterns/advert/off` ends it |
| `/patterns/schedule 1\|0` | SCHEDULE ON / OFF (also `/patterns/schedule/on`, `/off`) |
| `/patterns/stream 1\|0` | STREAM ON / OFF |
| `/patterns/cue/go [id]` | CUE GO — the standby id you last saw, or none |
| `/patterns/cue/standby/next`, `/patterns/cue/standby/prev` | CUE STANDBY NEXT / PREV |
| `/patterns/cue/standby <number\|name>` | CUE STANDBY — a cue by number or name |
| `/patterns/cue/hold 1\|0` | CUE HOLD ON / OFF |
| `/patterns/cue/arm 1\|0` | CUE ARM ON / OFF — only while the Remote page allows remotes to arm |
| `/patterns/review [1\|0]` | REVIEW ON / OFF — the preview full-frame on every multiview; no argument toggles |
| `/patterns/weather [1\|0\|now\|day\|tomorrow]` | WEATHER ON / OFF — the weather chip on air; no argument toggles; a view word picks what it shows (also `/patterns/weather/tomorrow`) |
| `/patterns/clock [1\|0\|12\|24]` | CLOCK ON / OFF — the clock overlay; no argument toggles; 12 or 24 sets the hours (also `/patterns/clock/24`, `/patterns/clock/on`); `/patterns/clock/seconds [1\|0]` and `/patterns/clock/date [1\|0]` the seconds and the date line |
| `/patterns/message [1\|0\|"text"]` | MESSAGE ON / OFF — the message overlay; no argument toggles; a text puts the words on (also `/patterns/message/text "…"`, `/patterns/msg/Doors/open`); `/patterns/message/scroll [1\|0]` (or `/patterns/ticker`) makes it a ticker |
| `/patterns/countdown <minutes>` | COUNTDOWN START — a duration from now (also `/patterns/countdown/start 5`, `/patterns/countdown/start/2:30`, `/patterns/timer/10`); `/patterns/countdown` with nothing to say, `/patterns/countdown/toggle` or `/patterns/countdown "toggle"` flips it; `/patterns/countdown/to "19:30"` (or `/to/19:30`) a time of day; `/patterns/countdown/stop`; `/patterns/countdown/label "DOORS IN"` |
| `/patterns/logo [1\|0]` · `/patterns/pip [1\|0]` | LOGO / PIP ON / OFF; no argument toggles |
| `/patterns/overlays/off` | OVERLAYS OFF — every overlay off in one message |
| `/patterns/pattern <kind>` | PATTERN — the kind of picture on air (also `/patterns/pattern/Grid`) |
| `/patterns/freeze [1\|0]` | FREEZE ON / OFF — every output holds its frame; no argument toggles |
| `/patterns/fade [seconds]` · `/patterns/fade/up [seconds]` | FADE / FADE UP — a blackout with a fade of that many seconds (none: the show's transition time); the seconds as the argument or the next segment (`/patterns/fade/down/2`) |
| `/patterns/fade/screen/<n> [seconds]` · `/patterns/fade/group/<A>` · `/patterns/fade/focused` · `/patterns/fade/ticked` · `/patterns/fade/groups` · `/patterns/fade "SCREEN 2" [seconds]` | FADE … SCREEN n / GROUP A / FOCUSED / TICKED / GROUPS — that part of the rig alone to black; `/patterns/fade/up/…` brings it back; the place as segments or a string argument, the seconds as a number argument or a trailing segment (`/patterns/fade/screen/2/1.5`) |
| `/patterns/lookback` | LOOKBACK — the look that was on air before the current one, back on air |
| `/patterns/stopall` | STOPALL |
| `/patterns/ping` | PING — answered with `/patterns/pong` to the sender |
| `/patterns/status` | STATUS — answered with `/patterns/status <json>` to the sender |

Answers go to whoever sent the message, from the same port: `/patterns/pong` for a ping,
`/patterns/status <json>` for a status, `/patterns/error <text>` when a command is refused (the
same `ERR …` sentence the TCP port would write) or an address is not one Patterns knows. With
**Feedback to** set to a host or address and a port (default 9699), every change sends one bundle
there — throttled to 200 ms like the STATE pushes — carrying `/patterns/state/live i`,
`/blackout i`, `/program s`, `/duck i`, `/tone i`, `/audio i`, `/audio/track s`, `/audio/next s`, `/audio/n i`, `/audio/count i`, `/audio/remaining i`, `/audio/items/<n> s` (1…8), `/music i`, `/music/now s`,
`/music/level i`, `/stinger s`, `/stinger/hold s`, `/lowerthird s`, `/lowerthird/person s`, `/lowerthird/preview s`,
`/lowerthird/preview/person s`, `/lowerthird/default s`, `/lowerthird/edited i`,
`/stream i`, `/playlist s`, `/health s`, `/review i`, `/freeze i`, `/clock i`, `/clock/hours i`, `/clock/seconds i`, `/clock/date i`, `/message i`, `/message/text s`, `/message/scroll i`, `/countdown i`, `/countdown/phase s`, `/countdown/label s`, `/countdown/target s`, `/countdown/remaining i` (whole seconds, sent every second while it runs), `/countdown/text s`, `/logo i`, `/pip i`, `/overlays/text s`, `/editsafe i`, `/look/previous s`, `/look/air s`, `/look/preview s`, `/pattern s`, `/rev i`, `/screen/<n> i`, `/lock/<n> i`, `/armed/<n> i`, `/screen/<n>/name s`, `/cue/armed i`,
`/cue/hold i`, `/cue/confirm s`, `/cue/standby s s` (number, name), `/cue/previous s s`,
`/cue/next s s`, `/cue/next/<k> s s` (the cues after the standby, k = 1…6), `/cue/last s s` (number, outcome), `/cue/offset s`, `/cue/follow s`,
and the show's lists by place for a bank of keys on the controller — `/looks/<n> s` (n = 1…16, `""` past the list) with `/looks/<n>/air i`,
`/lowerthirds/<n> s` (1…8), `/people/<n> s` (1…8), `/stingers/<n> s` (1…8), `/sections/<n> s` (1…6), `/music/items/<n> s` (1…6) —
plus `/deck/page i`, `/deck/count i`, `/deck/ended i`, `/deck/file s`, `/web/page s`, `/web/service s` (zeros and empty strings with none on air),
and the caller's VT clock — `/video/file s`, `/video/position i`, `/video/length i`, `/video/remaining i` (whole seconds), `/video/text s`, `/video/out i` (1 for the clip's last ten seconds) — sent every second while a clip runs. TouchOSC:
send to the machine on 9698, receive on 9699 with the tablet's address as the feedback host — a label bound to
`/patterns/state/looks/3` and a button sending `/patterns/look/index/3` make a look key that names itself.
QLab: a Network cue with an OSC message per line above. Companion: the generic OSC module for a
key or two; the Patterns module (TCP) for the full feedback.

## HTTP API (anything else)

- `GET /api/state` → the state JSON; `GET /api/state?since=<rev>` waits (up to 25 s) for the next change.
- `GET /api/cues` → the caller's cue list with notes, summaries, broken reasons and each cue's plan (planned start and length, follow delay, mark).
- `GET /api/stage?since=<rev>` → the stage payload (`STAGE STATUS`), waiting up to the long-poll's limit for a change past `rev`; the `/stage` and `/timer` pages live on it.
- `POST /api/stage/ack` with the message id as the body → `{"ok":true}` once; `{"ok":false,"reason":…}` for a message already seen or unknown.
- `GET /api/arcade` → the arcade's status (`ARCADE STATUS`) — on a desk that hears arcade nodes, their list; `POST /api/arcade/key` with `<pad> <button> DOWN|UP|TAP` as the body → `{"ok":…,"msg":…}`; the phone pad at `/pad` is built on both.
- **The audience port.** The phones' calls below answer only on the audience listener (Remote page → AUDIENCE; `AUDIENCE ON [port]`, default 9701, off by default, bindable to one address) — a socket that carries the play pages and nothing else: `/`, `/play`, `GET /api/play/state`, `POST /api/play/join|answer|say|vote|draughts`. Everything else — `/api/cmd`, the state, the pictures, `/host`, `/api/admin` — answers 404 there, and the control port answers 404 for the phones' paths. Put the audience port on the audience network and never the control port. Budgets: the seats (`AudienceMaxPlayers`), joins per address per minute, answers, messages, votes and moves per phone per minute, phones waiting at once, connections in all and per address; past one, a phone is told to slow down or that the room is full, and the show is untouched. A venue whose Wi-Fi puts every phone behind one address (a NAT gateway, a captive portal's proxy) would meet the per-address budgets by the twentieth join: the Remote page's Network picker has a **venue NAT** profile that opens them to the room and leaves the per-phone ones standing, and the room's line says when joins were refused from one address, with the fix named. `AUDIENCE STATUS` reads the listener, the seats, the connections, the waiting phones, the network profile, the refusals and the budgets.
- `POST /api/play/join` `{"nick","group","token","room"}` → `{"ok","token","nick","group","room","show"}`; `GET /api/play/state?token=&since=<seq>&rev=<rev>` → what one phone sees (the question and its own answer, its messages past `since`, the leaderboard, the path, the draughts board), waiting on the room's revision; `POST /api/play/answer` `{"token","question","choices":[],"scale","words"}`; `POST /api/play/say` `{"token","text"}`; `POST /api/play/vote` `{"token","option"}`; `POST /api/play/draughts` `{"token","action":"seat|move|leave","side","from","to"}`; `POST /api/play/host` with the admin passcode as the body → the host's JSON; `GET /api/play` → `PLAY STATUS`; `GET /api/play/feed.csv` → the room as lines for the message overlay's feed.
- `GET /pgm.jpg` → the program as a JPEG thumbnail.
- `GET /api/screens/<n>/edid.bin` → the planned screen's EDID (round 65.8) as bytes — an E-EDID 1.4 base block, a CTA-861 extension, a DisplayID 2.0 extension when the raster is past 4095 pixels; `edid.hex` the same as hex pairs, `edid.txt` the summary (the plan, the timing, what it advertises, its SHA-256, how to use it). Reading: no token. Load it as the custom EDID of the processor input or the PC's port the screen's link lands on, and the desk's signal view says whether the display presents it.
- `POST /api/cmd` with a command line as the body → `{"ok":true|false,"msg":"…"}`. Cue commands
  (`CUE …`, `STOPALL`) need an `X-Patterns-Client: <anything>` header, so a page from another
  origin cannot fire cues; everything else works without it. With a pairing token set (Remote
  page, TRUST) every mutating command needs `X-Patterns-Token: <token>` as well — without it the
  answer is `403` with `{"ok":false,"msg":"ERR not paired …"}` and nothing runs; the queries
  (`STATUS`, `CUE LIST`, `MENU …`) never need it, and neither does a browser on the desk's own
  machine. `POST /api/stage/ack` and `POST /api/arcade/key` want the same header. A token in the
  URL is not read.

`curl -d "LOOK Walk-in" http://<ip>:9696/api/cmd`
`curl -H "X-Patterns-Client: curl" -d "CUE STANDBY NEXT" http://<ip>:9696/api/cmd`
`curl -H "X-Patterns-Token: K7QM-3XWD-P9RA" -d "LOOK Walk-in" http://<ip>:9696/api/cmd` — on a paired desk

Behind the Install page's admin passcode (`docs/INSTALLS.md`):

- `GET /admin` → the ADMIN page.
- `POST /api/admin` with `<passcode>\n<command line>` as the body → `{"ok":…,"msg":"…"}`; a body with the
  passcode alone checks it (the page unlocking). A wrong passcode answers 403 and, after five wrong
  tries, a minute's lock.
- `GET /api/admin/log` with the passcode in an `X-Patterns-Pass: <passcode>` header → the last eighty lines of `patterns.log`.
- `GET /support-bundle.zip` with the same header → the support bundle, written beside the settings and sent.
  A `?pass=` in the URL is not read (round 65): a browser's history, a proxy's log and a screenshot keep URLs, and never headers.

`curl -d $'open-sesame\nANNOUNCE Closing time' http://<ip>:9696/api/admin`
`curl -H "X-Patterns-Pass: open-sesame" http://<ip>:9696/api/admin/log`
