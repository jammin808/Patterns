# Remote control — protocol and integrations

Patterns runs two remote interfaces while **Remote → Remote control** is on:

- **Web remote** — `http://<machine-ip>:9696/` (port configurable). One page for a phone or
  tablet with a menu across SHOW (presenter, transport, blackout, duck, STOP ALL, what is on
  now), CUES (the standby cue with its plan, ▲ ▼ GO HOLD, ARM, the day's timing, the next and
  the last), LOOKS, SCREENS (a switch and a padlock per screen, show parts), AUDIO (the audio
  track, break music, VOGs, stingers, tone), LOWER THIRDS (designs and people) and SETUP (the
  health line, the machine, the stream, the main machine's beacon, links to `/run` and
  `/multiview`); a sticky header names what is on air with its chips and a connection dot, the
  tab you were on is remembered, and the page waits on `GET /api/state?since=<rev>` so it
  changes the moment the show does. Works in any browser on the same network.
- **TCP line protocol** — port 9697 (configurable). One command per line (UTF-8, `\n`);
  every command answers `OK`, `OK <json>` or `ERR <reason>`. On connect — and on every
  change — the server pushes `STATE <json>` so controllers can show live feedback.
- **OSC** — UDP port 9698 (configurable; off by default — tick **OSC in** on the Remote page).
  Every address starts `/patterns/` and means exactly the TCP line it maps to; a refused command
  answers `/patterns/error` to the sender, and with a feedback host set every change sends one
  bundle of `/patterns/state/…` messages. See **OSC** below.

> There is no password on the show's commands. Anyone on the network can control the show while
> remote control is enabled — that's the same trust model as most stage-control protocols. Turn it
> off on the Remote page (SETUP) when it isn't needed. Administration — `RESTART`, `UPDATE APPLY`
> and the `/admin` page — sits behind the Install page's passcode (`docs/INSTALLS.md`).

## Commands

| Command | Effect |
|---|---|
| `OUTPUTS ON` | Open the output windows on the enabled screens |
| `OUTPUTS OFF` | Close all output windows |
| `GO` / `STOP` | Frozen aliases for `OUTPUTS ON` / `OFF` (older buttons keep working; `GO` never fires a cue) |
| `BLACKOUT ON` / `OFF` / `TOGGLE` | Instant black on every sink |
| `IDENTIFY` | Flash screen numbers |
| `LOOK <1–12>` | Apply the look on that F-key slot |
| `LOOK <name>` | Apply a look by name (case-insensitive) |
| `LOOK #<n>` | Apply the *n*th look in the show's order, whatever its name or F-key — a bank key that follows the list as looks are made (`ERR no look #7 — the show has 4`) |
| `NEXT` / `PREV` | The presenter click-through: a deck (PDF) on air turns its pages first — past the last page the caller's stack resumes with GO on the standby cue when the deck asks for it — else the clicker list forward / back |
| `DECK NEXT` / `PREV` / `FIRST` / `LAST` / `PAGE <n>` / `<n>` | The deck on air turns a page (`PDF` and `SLIDES` are aliases; `ERR` with no deck on air or a page that is not a number, first or last) |
| `VIDEO END [seconds]` | The clip on air — the program's video, a playlist's, a stinger's, an audio file — jumps to its last seconds (none = ten): the rehearsal's skip. Its end still plays, the out is still heard, and whatever follows it (the playlist's next item, a stinger's ending) happens for real; a timed playlist item's own clock is wound forward with it. `VT` and `CLIP` are aliases, `LAST` / `OUT` / `TAIL` mean `END`; `ERR` with no clip on air, a live source, or a clip whose length is not known yet |
| `VIDEO RESTART` | The clip on air plays again from its start (`START` / `TOP` / `REWIND` are the same); an ended clip comes back |
| `DEVICE <name\|*> <text>` | A line to a device of the Interactive area — an Arduino's relay, a Pi's script (`SEND` is an alias; `*` is the first device; `ERR` while the area or the device is off, or the name is unknown). What devices send back and hear is in `docs/ARDUINO.md` |
| `ANNOUNCE <name or words>` | An announcement of the Install page by name — its words on the message overlay, its VOG, its look, for its seconds — else the words themselves for the site's announcement seconds; the programme comes back by itself. `ANNOUNCE OFF` (`STOP` / `END`) ends it (`ERR` while an advert is on instead — `ADVERT OFF` ends that). Works with the schedule on or off; refused while the caller's stack is armed or a stinger holds the screens |
| `ADVERT <name\|n>` | An advert of the Install page plays now for its seconds, on its screens (`AD` is an alias; `ERR` for an unknown name, a row that is not an advert, or one with no look). `ADVERT OFF` (`STOP` / `SKIP` / `END`) ends it and the programme comes back |
| `SCHEDULE ON` / `OFF` | The install's clock runs the site — programmes by the clock, adverts and announcements at their times — or stops with the picture where it is. `docs/INSTALLS.md` has the rules |
| `RESTART <passcode>` | The app restarts under the watchdog with the show put back — the Install page's admin passcode; `ERR` with none set, a wrong one (five wrong tries lock the gate for a minute), or without the watchdog |
| `UPDATE APPLY <passcode>` | The staged update (`updates/*.zip`) applied by the watchdog between two starts of the app, rolled back if the new build does not stay up; the same gate as `RESTART` |
| `SCREEN <n> ON` / `OFF` / `TOGGLE` | Enable/disable screen *n* (overview numbering) |
| `SCREEN <n> LOOK <name or id>` | The look's picture for screen *n* lands on it alone as its own pattern — every other screen stays; a whole-look recall or a cue later replaces it, TAKE leaves it, a lock keeps it |
| `SCREEN <n> PROGRAM` (`PGM`, `FOLLOW`) | Screen *n* drops its own picture and shows the program again |
| `LOCK <n> ON` / `OFF` / `TOGGLE` | Lock screen *n*: it keeps its picture through looks, cues, TAKE ALL and stingers (a confidence monitor, an info screen); unlock lets it follow again. Bare `LOCK <n>` toggles |
| `GROUP <letter> ON` / `OFF` | All screens of joined canvas A/B/… at once |
| `AUDIO PLAY` | The audio playlist plays — from where it stopped, or its first track (`TRACK` is an alias of `AUDIO`; `ERR` with an empty list) |
| `AUDIO PLAY <n>` / `AUDIO PLAY <name>` | A track by its place in the order (the rows first, then the folders' files in name order), a row's name, or a file's name with or without its extension (`ERR` for a track that is not there) |
| `AUDIO NEXT` / `AUDIO PREV` | The next or the previous track (`SKIP` / `BACK` are the same); they wrap, so a key never dead-ends |
| `AUDIO STOP` | The list stops (it resumes at the same track) |
| `AUDIO VOL <0–125>` | The list's level (`VOLUME` / `LEVEL` are the same; out of range is `ERR`) |
| `MUSIC PLAY` | Break music (Spotify): resume, or start the library's first entry |
| `MUSIC PLAY <n>` / `MUSIC PLAY <name>` | Play break-music entry *n* (Audio-page order) or by name (`MUSIC <n>` / `MUSIC <name>` do the same) |
| `MUSIC PAUSE` | Pause break music (alias `MUSIC STOP`) |
| `MUSIC NEXT` | Skip to the next track (alias `MUSIC SKIP`) |
| `MUSIC VOL <0–100>` | The Spotify device's own level (alias `VOLUME`; out of range answers `ERR … 0 to 100`) |
| `TONE ON` / `OFF` | Soundcheck tone generator |
| `DUCK ON` / `OFF` / `TOGGLE` | The live duck: the music track, break music, a playing stinger's sound and a clip's soundtrack drop to the Audio page's live-duck level for an announcement from the room and come back when lifted; a VOG never ducks. A latch (bare `DUCK` toggles): STOP ALL and look recalls leave it |
| `STINGER <n>` / `STINGER <name>` | Fire library item *n* (Audio-page order) or by name — a VOG or a stinger, whichever it is |
| `VOG <n>` / `VOG <name>` | The same, refused if that item is a stinger — a key that says VOG never fires one |
| `STING <n>` / `STING <name>` | The same, refused if that item is a VOG |
| `STINGER STOP` | Stop whatever is on air: a clip or a held frame reverts, and a stinger's ending is cancelled, never run (`VOG STOP` / `STING STOP` are aliases) |
| `LOWERTHIRD <n>` / `LOWERTHIRD <name>` | Put lower third *n* (Lower thirds page order) or the named design on air over whatever is showing; again restarts its way in (`LT` is an alias) |
| `LOWERTHIRD OFF` | The lower third on air leaves the way it was designed to (`LT HIDE` does the same) |
| `LOWERTHIRD <design> WITH <person>` | Fill design *n* / the named design from the library first — the person's name, role, company and photo (Lower thirds page, LIBRARY) — then put it on air; *person* is a number (library order) or a name (`LT … WITH …` is the same) |
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
| `FREEZE ON` / `OFF` / `TOGGLE` | Every output — the windows, the NDI sends, the stream — holds the frame it shows until released; the desk's own views keep moving, a blackout still takes a frozen output. A latch (bare `FREEZE` toggles), never saved |
| `FADE [seconds]` / `FADE UP [seconds]` (also `FADEUP`) | A blackout with a fade of its own: down over the seconds given (`FADE 2`, `FADE 2.5`, `FADE 1500ms`; none = the show's transition time), or up again the same way. Refused with `ERR` when the show is already there |
| `LOOKBACK [cut\|ms]` | The look that was on air before the current one, back on air with the show's transition (or a cut, or a fade in ms); a second `LOOKBACK` swaps back. `ERR` when there is none yet |
| `REVIEW ON` / `OFF` / `TOGGLE` | Every multiview (a screen's own multiview pattern, an NDI send of it, `/multiview`) draws the desk's sandboxed preview full-frame with a REVIEW chip until switched off — the next look checked on the monitor wall before the TAKE; the audience's screens do not change. A latch (bare `REVIEW` toggles), never saved |
| `SECTION <n>` / `SECTION <name>` | Put playlist show part *n* (Media-page order) on air |
| `STREAM ON` / `OFF` | Start/stop the streaming output (Stream page config) |
| `CUE GO [<id>]` | GO on the caller's cue stack through the gate. Send the standby id you last saw (from STATE) and a GO that races a standby move answers `ERR standby moved`; `OK <json>` carries the execution record (`outcome`, `last`, `standby`) or `{"outcome":"Confirm"}` when the cue asks for a second GO within four seconds |
| `CUE STANDBY NEXT` / `PREV` / `<number>` / `<name>` | Put a cue on standby — changes nothing on air |
| `CUE HOLD ON` / `OFF` | A latched GO inhibit and nothing else |
| `CUE ARM ON` / `OFF` | Arm / disarm the stack — accepted only when the Remote page allows remotes to arm |
| `CUE LIST` | `OK <json>` — the whole list with notes, summaries, broken reasons and each cue's plan (`plannedStart`, `plannedSeconds`, `followSeconds`, `mark`); `listRev` changes when the list does |
| `STOPALL` | Stops the audio track, break music, any VOG or stinger (a clip or a held frame reverts, no ending runs) and the tone — never outputs, blackout or the stream (one token: an older build reads `STOP ALL` as `STOP`) |
| `HELLO <name>` | Names this connection: history and the journal read "GO from tcp FOH deck" |
| `STATUS` | `OK <json>` — same payload as the STATE pushes |
| `PING` | `OK PONG` |

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
`cuestack{armed,hold,seq,listRev,confirm,program{label},previous{id,number,name},standby{id,number,name,requireConfirm,notes,plannedStart,followSeconds},next[6]{id,number,name},last{id,number,name,outcome,error,at,origin,actionsDone,actionsTotal},history[8],timing{offsetSeconds,offset,nextBreak{number,name,expected,planned,deltaSeconds,atLeast,text},lunch{…},end{…},follow}}`
(`timing` is the caller's clock: `offset` reads "ON TIME", "3 MIN LATE" or "2 MIN EARLY" from the last GO against its planned start; `nextBreak`, `lunch` and `end` say when the marked cues are expected — `atLeast` when a cue has overrun or has no planned length; `follow` reads "AUTO 01.030 in 0:07" while the next cue is going to fire by itself)
(the stack's runtime is pushed on its own event, throttled like everything else), `blackout`, `live`, `review` (the preview fills every multiview), `frozen` (every output holds its frame), `previousLook` (the name `LOOKBACK` returns to, or empty), `airLook` (the look on air, by name — empty when none was recorded or the picture moved on), `previewLook` (the look loaded in the preview while EDIT SAFE is open), `pattern` (the kind of picture on air: `Media`, `LedWall`, `ProjectionBlend`…), `looks[{n,name,slot,air,preview}]` (the show's looks in order — `n` is the place `LOOK #n` uses, `slot` the F-key or 0, `air` / `preview` where it is), `presenter{armed,index,count,steps[]}`,
`screens[{n,label,enabled,group,locked,role,armed,own}]` (labels honour operator names; `role` is main, confidence, info or repeater; `armed` = the next CUT / TAKE changes it; `own` = it shows a picture of its own, not the program's), `editSafe` (EDIT SAFE is open: there is a preview and a TAKE to come),
`audio{playing,track,n,count,next,position,length,remaining,positionText,lengthText,remainingText,shuffle,loop,level,status,items[{n,name}]}` (the audio playlist: `track` is the track on — or, stopped, the one PLAY would start — `n` its place (0 with nothing on) of `count`, `next` the one after it, the clock in whole seconds and as `m:ss`, the list's flags and level, its status line, and the rows by place — `AUDIO PLAY <n>`), `tone`,
`stingers[{n,name,kind,source}]` (`kind` is `vog` or `sting`; `source` is `file`, or `pulse` for an effect pulse — a surge through the particles and fractals on screen that owns nothing), `stingerPlaying` (whatever owns the show), `stingerKind`
(`vog` / `sting` / empty), `vogSound` (a VOG sound playing over the show — over a stinger too, which it ducks
rather than stops; empty when none), `stingHold` (the name of a stinger holding the screens, or empty), `duck` (the live duck is on),
`lowerThirds[{n,name}]` (the designs, Lower thirds page order), `lowerThird` (the design on screen — arriving, holding or leaving — or empty),
`people[{n,name,role}]` (the library, page order — `PERSON n`), `lowerThirdPerson` (the name the lower third on screen carries, or empty),
`lowerThirdPreview` / `lowerThirdPreviewPerson` (the design and the name in the preview for a sign-off, or empty), `lowerThirdDefault` (the show's ★ design),
`lowerThirdEdited` (true while the design on air differs from the edited one — `LOWERTHIRD UPDATE` pushes the edit),
`web{page,url,title,service,actions[{id,label}]}` (the web page the program shows — its nickname or host, its address and title, its service when Patterns knows it (YouTube, Vimeo, Google Slides, PowerPoint for the web) and the actions `WEB KEY <id>` takes on it; `null` with no page on air),
`video{file,role,tag,position,length,remaining,positionText,lengthText,remainingText,text,chip,playing,ended,loops,out,call}` (the caller's VT clock — the clip on air: its file, its `role` (`program`, `playlist`, `stinger`, `layer`) and `tag` (`VT`, `AUDIO`, `STINGER CLIP`, `PLAYLIST`), where it is, how long it is and what is left in whole seconds and as `m:ss`, `text` as the desk reads it (*VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left*), `chip` (*VT 2:28*), `loops` when it never comes out, `out` for its last ten seconds with `call` the caller's word (*OUT IN 7*); `null` with no clip on air. Pushed every second while a clip runs — only then, and only while a controller listens),
`deck{file,kind,page,count,ended,endsWithGo,converting,status}` (the deck the program shows: its file and its kind — `PDF`, `PowerPoint`, `Keynote`, `Impress` — the page on show and the count, `ended` on its last page, `endsWithGo` when the next click there GOes the standby cue, `converting` while LibreOffice is still making the PDF of a PowerPoint (the count is 0 and `status` reads *Converting…*, or why it could not); `null` with no deck on air),
`interactive` (the Interactive area is on) and `devices[{n,name,link,address,enabled,open,status,lastIn,lastOut}]` (every device of the Interactive page — its link is `serial`, `tcp` or `udp`, `open` while its wire is up, `status` the page's words, the last line each way),
`install{on,site,programme,idle,over,overKind,overUntil,next,status,slots[{n,name,kind,enabled,status}],problems,update{staged,version,ok,running,supervised,status,last},management}` (the Install page: the schedule's switch, the programme on, the announcement or advert on and until when, the next change, every row and its state, the staged update, the check-in's line — see `docs/INSTALLS.md`),
`sections[{n,name,active}]`, `playlist`, `nextCue`,
`music{on,playing,level,now,device,status,items[{n,name}]}` (break music — `now` is the track
Spotify reports, `status` the same sentence the Audio page shows),
Remote commands always drive **what the audience sees**: looks, cues, playlist parts, stingers
and transport apply to the program even while the operator is building the next look in the
sandboxed preview.

State JSON also carries `stream{active,status}`, `health`, `machine{cpu,ram,fps,battery,advice}` — machine load
(percent, -1 = unknown), output frame rate, whether the computer is on battery, and how
many Machine-page suggestions currently need attention — and `beacon{sending,listening,main}`:
whether this machine sends its heartbeat beacon, whether it listens for a main machine's, and
what it makes of it ("Main machine MAIN seen 1 s ago: live · Walk-in", "MAIN MACHINE MAIN SILENT
for 6 s — … Take over?", or empty when not listening).

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

Use the **Patterns module** in `integrations/companion-module-patterns/` (2.0.0: **banks** —
keys that label themselves from the show through variables Patterns keeps fresh, so a row of
sixteen look keys, seven cue keys, eight lower-third / people / stinger / screen keys and six
music / part keys fills itself as the show is built, each key firing the item at its place
(`LOOK #n`, `LT n`, `PERSON n`, `STINGER n`, `MUSIC PLAY n`, `SECTION n`, `SCREEN n`, the cue
bank's standby or GO), lit while its item is on air and dim while empty; a preset per item
under *… — this show* categories rebuilt when the lists change; the cue stack GO / standby /
HOLD / ARM / STOP ALL with feedbacks and variables; Break music, VOG, kind-checked stingers,
lower thirds with the sign-off flow, web pages, decks, review, freeze, the timed fade, the
previous look, screen locks and arming — see its README for install), or the built-in
**Generic TCP** connection sending the raw commands above (no feedback).

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
| `/patterns/lock/<n> [1\|0]` | LOCK n ON / OFF; no argument toggles |
| `/patterns/group/<letter> 1\|0` | GROUP A ON / OFF — a joined canvas |
| `/patterns/audio/play [n\|name]` | AUDIO PLAY — the audio playlist plays: a track by its place or its name, or the list resumes (also `/patterns/audio/play/<n>`, `/patterns/track/…`) |
| `/patterns/audio/stop`, `/patterns/audio/next`, `/patterns/audio/prev` | AUDIO STOP / NEXT / PREV |
| `/patterns/audio/volume <level>` | AUDIO VOL: an integer is percent (0–125), a float from 0.0 to 1.0 is a fader (× 100) |
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
| `/patterns/freeze [1\|0]` | FREEZE ON / OFF — every output holds its frame; no argument toggles |
| `/patterns/fade [seconds]` · `/patterns/fade/up [seconds]` | FADE / FADE UP — a blackout with a fade of that many seconds (none: the show's transition time); the seconds as the argument or the next segment (`/patterns/fade/down/2`) |
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
`/stream i`, `/playlist s`, `/health s`, `/review i`, `/freeze i`, `/editsafe i`, `/look/previous s`, `/look/air s`, `/look/preview s`, `/pattern s`, `/rev i`, `/screen/<n> i`, `/lock/<n> i`, `/armed/<n> i`, `/screen/<n>/name s`, `/cue/armed i`,
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
- `GET /pgm.jpg` → the program as a JPEG thumbnail.
- `POST /api/cmd` with a command line as the body → `{"ok":true|false,"msg":"…"}`. Cue commands
  (`CUE …`, `STOPALL`) need an `X-Patterns-Client: <anything>` header, so a page from another
  origin cannot fire cues; everything else works without it.

`curl -d "LOOK Walk-in" http://<ip>:9696/api/cmd`
`curl -H "X-Patterns-Client: curl" -d "CUE STANDBY NEXT" http://<ip>:9696/api/cmd`

Behind the Install page's admin passcode (`docs/INSTALLS.md`):

- `GET /admin` → the ADMIN page.
- `POST /api/admin` with `<passcode>\n<command line>` as the body → `{"ok":…,"msg":"…"}`; a body with the
  passcode alone checks it (the page unlocking). A wrong passcode answers 403 and, after five wrong
  tries, a minute's lock.
- `GET /api/admin/log?pass=<passcode>` → the last eighty lines of `patterns.log`.
- `GET /support-bundle.zip?pass=<passcode>` → the support bundle, written beside the settings and sent.

`curl -d $'open-sesame\nANNOUNCE Closing time' http://<ip>:9696/api/admin`
