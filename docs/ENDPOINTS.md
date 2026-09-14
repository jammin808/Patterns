# Endpoints — media servers, projectors and the room's other boxes

A show is more than the screens Patterns draws. The projector has to be on and on the right
input, the media server has to be told to play, the lighting desk wants a cue, the streaming
encoder has a web API. All of them are **devices of the Interactive area** (SETUP → Interactive),
because everything the area already has is what they need: a link that opens and reopens by
itself, a name a cue can say, the `DEVICE` verb on the wire and in OSC, the `device_send` action
in Companion, the journal, and the assistant's catalogue. What a box adds is a **profile** — its
own vocabulary — so a cue can say `POWER ON` to a projector and `PLAY` to a media server in the
words a person would use, and the profile turns them into the bytes the box expects.

`docs/ARDUINO.md` covers the area itself (links, triggers, what a device hears). This page is
the boxes.

## The profiles

| Speaks | The box | Link and port | The words |
|---|---|---|---|
| **Projector (PJLink)** | Any venue projector on the network — Panasonic, Epson, Christie, NEC, Sony, Barco all speak PJLink class 1 | TCP 4352 | `POWER ON` · `POWER OFF` · `INPUT HDMI 1` (also `RGB`, `VIDEO`, `DIGITAL`, `STORAGE`, `NETWORK` *n*, or a two-digit PJLink code such as `31`) · `SHUTTER ON` / `OFF` (picture and sound) · `MUTE ON` / `OFF` (sound) · `POWER ?` · `LAMP ?` · `ERRORS ?` · `NAME ?` · `RAW %1POWR ?` |
| **Disguise d3 (OSC)** | Disguise's show control, through the OSC device on the d3 machine | UDP, the OSC device's *receive* port (7401 as shipped) | `PLAY` · `PLAY SECTION` · `LOOP` · `STOP` (d3's stop is a pause) · `NEXT` · `PREV` · `START` (return to start) · `NEXT TRACK` · `PREV TRACK` · `TRACK <name>` · `TRACK #<n>` · `CUE <tag>` (`1.5` as a number, `intro` as a name) · `FADE UP` · `FADE DOWN` · `HOLD` · `VOLUME <0–100>` · `BRIGHTNESS <0–100>` · `RAW /d3/showcontrol/<address> [args]` for anything else on d3's list |
| **Pixera (JSON-RPC)** | Pixera's API over TCP, every frame ending in `0xPX` | TCP 1400 | `TIMELINE <name> PLAY` / `PAUSE` / `STOP` · `CUE <timeline> <cue>` · `API <method> [json params]` (`API Pixera.Utility.getApiRevision`) · `RAW {json-rpc}` |
| **OSC device** | Anything that speaks OSC over UDP — QLab, Resolume, TouchDesigner, a lighting desk, a MIDI-to-OSC bridge | UDP, the box's port (QLab 53000) | `/address` and its arguments, typed by their shape: `3` is a whole number, `0.5` a decimal, `true`/`false` a bool, a word a string, `"quoted words"` one string |
| **Companion (its TCP API)** | Bitfocus Companion itself — the desk driving the Stream Deck's pages and buttons, so the deck follows the show | TCP 16759 (Companion's TCP remote control; its address filled in from the Companion heard announcing itself on the network) | `PAGE <n> [surface]` (the surface id from Companion's Surfaces page — typed once in the device's Surface box for a bare `PAGE`) · `PAGE UP` / `DOWN` · `PRESS <page/row/column>` · `DOWN` / `UP <page/row/column>` · `ROTATE LEFT` / `RIGHT <page/row/column>` · `STEP <page/row/column> <n>` · `TEXT <page/row/column> <words>` · `COLOR` / `BGCOLOR <page/row/column> <#hex>` · `VAR <name> <value>` · `GET <name>` · `RESCAN` · `RAW <Companion's own line>` |
| **Plain lines** | A board, a script, a show controller with a text protocol — the area as it always was | Serial, TCP, UDP | The line as typed, with the line ending chosen |
| **HTTP endpoint** | A box with a web API — Companion's own API, a Q-SYS core, a Crestron or Extron processor, an encoder | HTTP, the box's address (`http://10.0.0.9:8080`) | Each line is one request: `GET /api/play` · `POST /cue/3` · `POST /go {"cue":3}` (a `{…}` body is sent as JSON); a bare line is a GET of that path; the answer's status and first line come back as a line |

The Interactive page's chips add each box as a preset: the right link and port, a line to try,
and no chatter back at it (a projector must never be sent `OK`, so *Answers go back* and *Hears
the show* are off for these). Type the address. For a projector that asks for a password, type it
in the Password box — PJLink authentication is an MD5 of the projector's seed and the password in
front of the first command of every connection, and the profile does that; the status line says
*authentication failed — check the Password box* if it is wrong.

## Where the words come from

- **A cue**: *Device — send a line*, the device as the target, the words as the value. The checks
  refuse a cue whose device is not on the page and warn when the area or the device is off.
- **The wire**: `DEVICE Projector POWER ON`, `DEVICE Disguise CUE 1.5`, `DEVICE Pixera TIMELINE Main PLAY`
  (`SEND` is an alias; `*` is the first device). The reply is `OK` when the words were the
  profile's and the link was open, `ERR` with the reason otherwise — *'DANCE' is not a PJLink
  command — POWER ON · POWER OFF …*.
- **OSC in**: `/patterns/device/Projector "POWER ON"`.
- **Companion**: the `device_send` action.
- **The page**: *Send a line* on the device's card.
- **The assistant**: it knows the devices on the page and each one's words, so *"turn the
  projector on and take Disguise to cue 3 at the top of the show"* proposes the two steps.

## What comes back

A box's answers are read by the profile and shown on the device's card and in STATE's device
rows (`lastIn`): a projector's `%1POWR=1` reads *power on*, `%1POWR=ERR3` reads *POWR: unavailable
now — the projector is warming up, cooling down or in standby*; a Pixera error arrives in its own
words. A projector is asked `POWER ?` every ten seconds while the link is open, so the card reads
the box without a press. A box's own answers are never taken as commands to the show — unless a
trigger row on the device says so (d3's OSC device sends its transport state back; a row
`/d3/showcontrol/transport *` → `MESSAGE d3 *` would put it on screen).

## Honest notes

- Companion's TCP API answers every line with `+OK` (a value after it for `GET`) or `-ERR` and the
  reason, so a Companion device's Confirm level reaches Accepted with nothing to ask afterwards; the
  lines are the ones Companion 4 and 5 document (`SURFACE <id> PAGE-SET`, `LOCATION p/r/c PRESS`,
  `CUSTOM-VARIABLE <name> SET-VALUE`). A custom variable has to exist in Companion before `VAR`
  fills it. The desk found the Companion by the satellite port Companion 5 announces; the TCP API
  port is Companion's default and is typed if it was changed.
- PJLink is a published standard and the profile follows class 1 to the letter; the input family
  codes (1x RGB, 2x video, 3x digital, 4x storage, 5x network) are the standard's.
- Disguise's addresses are those of d3's published OSC show-control list. Versions differ at the
  edges; `RAW` sends any address as typed, and the OSC device's own log on the d3 machine shows
  what arrived.
- Pixera's API works on handles: `TIMELINE Main PLAY` is a look-up of the timeline by name whose
  reply sends the play with the handle. The method names follow Pixera's API reference for its
  timelines (`Pixera.Timelines.getTimelineFromName`, `Pixera.Timelines.Timeline.play`,
  `Pixera.Timelines.Timeline.getCueFromName`, `Pixera.Timelines.Cue.apply`); a box on another
  version answers with its own error, which the card shows word for word, and `API` and `RAW`
  reach any method the reference lists. Check it against your Pixera before the show.
- Passwords are kept with the show, as the rig's addresses are. A show file that travels carries
  them.
- A twin (a second Patterns in step — `docs/PLAN.md` §48) carries the devices with the show, but
  a standby opens no port until it takes over.
