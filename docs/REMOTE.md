# Remote control — protocol and integrations

Patterns runs two remote interfaces while **Remote → Remote control** is on:

- **Web remote** — `http://<machine-ip>:9696/` (port configurable). Big-button page for a
  phone or tablet: presenter next/back, looks, OUTPUTS ON/OFF, IDENTIFY, blackout, per-screen
  toggles, audio track. Works in any browser on the same network.
- **TCP line protocol** — port 9697 (configurable). One command per line (UTF-8, `\n`);
  every command answers `OK`, `OK <json>` or `ERR <reason>`. On connect — and on every
  change — the server pushes `STATE <json>` so controllers can show live feedback.

> There is no password. Anyone on the network can control the show while remote control
> is enabled — that's the same trust model as most stage-control protocols. Turn it off
> on the Remote page (SETUP) when it isn't needed.

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
| `NEXT` / `PREV` | Clicker list forward / back (the presenter click-through) |
| `SCREEN <n> ON` / `OFF` / `TOGGLE` | Enable/disable screen *n* (overview numbering) |
| `GROUP <letter> ON` / `OFF` | All screens of joined canvas A/B/… at once |
| `AUDIO PLAY` / `STOP` | The independent audio track |
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
| `SECTION <n>` / `SECTION <name>` | Put playlist show part *n* (Media-page order) on air |
| `STREAM ON` / `OFF` | Start/stop the streaming output (Stream page config) |
| `CUE GO [<id>]` | GO on the caller's cue stack through the gate. Send the standby id you last saw (from STATE) and a GO that races a standby move answers `ERR standby moved`; `OK <json>` carries the execution record (`outcome`, `last`, `standby`) or `{"outcome":"Confirm"}` when the cue asks for a second GO within four seconds |
| `CUE STANDBY NEXT` / `PREV` / `<number>` / `<name>` | Put a cue on standby — changes nothing on air |
| `CUE HOLD ON` / `OFF` | A latched GO inhibit and nothing else |
| `CUE ARM ON` / `OFF` | Arm / disarm the stack — accepted only when the Remote page allows remotes to arm |
| `CUE LIST` | `OK <json>` — the whole list with notes, summaries and broken reasons (`listRev` changes when the list does) |
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
`cuestack{armed,hold,seq,listRev,confirm,program{label},previous{id,number,name},standby{id,number,name,requireConfirm,notes},next[6]{id,number,name},last{id,number,name,outcome,error,at,origin,actionsDone,actionsTotal},history[8]}`
(the stack's runtime is pushed on its own event, throttled like everything else), `blackout`, `live`, `looks[{name,slot}]`, `presenter{armed,index,count,steps[]}`,
`screens[{n,label,enabled,group}]` (labels honour operator names), `audio{playing,track}`, `tone`,
`stingers[{n,name,kind,source}]` (`kind` is `vog` or `sting`; `source` is `file`, or `pulse` for an effect pulse — a surge through the particles and fractals on screen that owns nothing), `stingerPlaying` (whatever owns the show), `stingerKind`
(`vog` / `sting` / empty), `vogSound` (a VOG sound playing over the show — over a stinger too, which it ducks
rather than stops; empty when none), `stingHold` (the name of a stinger holding the screens, or empty), `duck` (the live duck is on),
`sections[{n,name,active}]`, `playlist`, `nextCue`,
`music{on,playing,level,now,device,status,items[{n,name}]}` (break music — `now` is the track
Spotify reports, `status` the same sentence the Audio page shows),
Remote commands always drive **what the audience sees**: looks, cues, playlist parts, stingers
and transport apply to the program even while the operator is building the next look in the
sandboxed preview.

State JSON also carries `stream{active,status}`, `health`, `machine{cpu,ram,fps,battery,advice}` — machine load
(percent, -1 = unknown), output frame rate, whether the computer is on battery, and how
many Machine-page suggestions currently need attention.

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

Use the **Patterns module** in `integrations/companion-module-patterns/` (1.2.0: cue stack
GO / standby / HOLD / ARM / STOP ALL with feedbacks and variables, a **Break music** category —
play / pause / skip and entries 1–6, lit while music plays — a **VOG** category and kind-checked
stinger keys with a STING HOLD feedback and a *put it back* key, plus presets for
transport/looks/screens/groups/presenter/audio — see its README for install), or the
built-in **Generic TCP** connection sending the raw commands above (no feedback).

## HTTP API (anything else)

- `GET /api/state` → the state JSON; `GET /api/state?since=<rev>` waits (up to 25 s) for the next change.
- `GET /api/cues` → the caller's cue list with notes, summaries and broken reasons.
- `GET /pgm.jpg` → the program as a JPEG thumbnail.
- `POST /api/cmd` with a command line as the body → `{"ok":true|false,"msg":"…"}`. Cue commands
  (`CUE …`, `STOPALL`) need an `X-Patterns-Client: <anything>` header, so a page from another
  origin cannot fire cues; everything else works without it.

`curl -d "LOOK Walk-in" http://<ip>:9696/api/cmd`
`curl -H "X-Patterns-Client: curl" -d "CUE STANDBY NEXT" http://<ip>:9696/api/cmd`
