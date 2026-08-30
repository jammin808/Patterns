# Remote control — protocol and integrations

Patterns runs two remote interfaces while **Remote → Remote control** is on:

- **Web remote** — `http://<machine-ip>:9696/` (port configurable). Big-button page for a
  phone or tablet: presenter next/back, looks, GO/STOP/IDENTIFY, blackout, per-screen
  toggles, audio track. Works in any browser on the same network.
- **TCP line protocol** — port 9697 (configurable). One command per line (UTF-8, `\n`);
  every command answers `OK`, `OK <json>` or `ERR <reason>`. On connect — and on every
  change — the server pushes `STATE <json>` so controllers can show live feedback.

> There is no password. Anyone on the network can control the show while remote control
> is enabled — that's the same trust model as most stage-control protocols. Turn it off
> in the Remote tab when it isn't needed.

## Commands

| Command | Effect |
|---|---|
| `GO` | Open outputs on the enabled screens |
| `STOP` | Close all outputs |
| `BLACKOUT ON` / `OFF` / `TOGGLE` | Instant black on every sink |
| `IDENTIFY` | Flash screen numbers |
| `LOOK <1–12>` | Apply the look on that F-key slot |
| `LOOK <name>` | Apply a look by name (case-insensitive) |
| `NEXT` / `PREV` | Presenter click-through forward / back |
| `SCREEN <n> ON` / `OFF` / `TOGGLE` | Enable/disable screen *n* (overview numbering) |
| `GROUP <letter> ON` / `OFF` | All screens of joined canvas A/B/… at once |
| `AUDIO PLAY` / `STOP` | The independent audio track |
| `TONE ON` / `OFF` | Soundcheck tone generator |
| `STATUS` | `OK <json>` — same payload as the STATE pushes |
| `PING` | `OK PONG` |

State JSON carries: `blackout`, `live`, `looks[{name,slot}]`, `presenter{armed,index,count,steps[]}`,
`screens[{n,label,enabled,group}]`, `audio{playing,track}`, `tone`, `playlist`, `nextCue`.

## Bitfocus Companion

Use the **Patterns module** in `integrations/companion-module-patterns/` (actions,
presets for transport/looks/screens/groups/presenter/audio, live feedback and variables —
see its README for install), or the built-in **Generic TCP** connection sending the raw
commands above (no feedback).

## HTTP API (anything else)

- `GET /api/state` → the state JSON.
- `POST /api/cmd` with a command line as the body → `{"ok":true|false,"msg":"…"}`.

`curl -d "LOOK Walk-in" http://<ip>:9696/api/cmd`
