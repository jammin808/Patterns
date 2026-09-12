# The Interactive area — Arduino, Raspberry Pi and devices over IP

Buttons, sensors and lights in the room, wired to the show. A device speaks short text lines to
Patterns and Patterns speaks short text lines back; nothing else is needed on either side. The
page is **SETUP → Interactive**.

## The idea in one minute

- A device sends a line — `BTN1`, `SENSOR 42`, or a protocol line like `CUE GO` — and Patterns
  turns it into a show command through the same action layer the desk, the cues, the phone and
  Companion use. The journal reads *CUE GO from device Arduino*.
- Patterns answers each command with `OK` or `ERR` and the reason (switch *Answers go back* off
  for a device that would rather not hear them).
- Patterns tells the device what the show is doing, one `KEY VALUE` line per fact and only when
  it changes: `BLACKOUT 1`, `LOOK Walk-in`, `CUE 01.020`, `ARMED 1`, `DECK 3 12`… A lamp on the
  lectern can follow the cue stack; a relay can fire when a look comes on.
- A cue can send a line of its own (**Device — send a line**: target the device, value the text),
  and so can the wire (`DEVICE Arduino RELAY 1`), OSC (`/patterns/device/Arduino "RELAY 1"`) and
  Companion (`device_send`).

The area is **off by default** — opening a serial port or a network connection is a deliberate
act — and every device can be switched off on its own.

## Links

| Link | For | Address |
|---|---|---|
| Serial (USB) | Arduino, Teensy, an RS-232 controller | `COM3` on Windows, `/dev/ttyUSB0` or `/dev/ttyACM0` on Linux, `/dev/tty.usbmodem…` on macOS; the baud the sketch opens (115200 is the usual) |
| TCP | Raspberry Pi, ESP32, a show controller with a TCP port | `192.168.1.50` with the IP port beside it, or `pi.local:7000`; Patterns connects, and reconnects every two seconds after a drop |
| UDP | Datagrams, no connection | The same address form; every line goes as one datagram, and lines come back from the same socket |

A serial device is opened with DTR and RTS raised, which is what an Arduino wants; most boards
reset when the port opens and say nothing for a second or two — Patterns sends every fact again
the moment a device connects, and **RESEND ALL** on the page does it by hand.

## Triggers — what a device's lines mean

Each device carries trigger rows: *the device says* → *Patterns does*. The match is the whole
line, case-blind (`BTN1`); ending in `*` it matches the start, and the rest of the line rides into
a command that ends in `*` (`SENSOR *` → `MESSAGE Room at *` turns `SENSOR 42` into
`MESSAGE Room at 42`). With **Its lines may be show commands as they are** on, a line that matches
no row and is a protocol line (`NEXT`, `LOOK 3`, `BLACKOUT ON`, `STINGER 2`, `CUE GO`…) runs as it
is — the whole vocabulary of `docs/REMOTE.md`. Anything else answers `ERR no trigger for '…'`.

## What a device hears

`KEY VALUE` lines, sent when the value changes (all of them once, when the device connects):

| Line | Meaning |
|---|---|
| `BLACKOUT 1` / `0` | Blackout on or off |
| `LIVE 1` / `0` | Output windows open |
| `DUCK 1` / `0`, `FROZEN 1` / `0`, `REVIEW 1` / `0` | The live duck, the freeze, review on the multiview |
| `LOOK Walk-in` | The look on air (a bare `LOOK` when none) |
| `PROGRAM Walk-in` | What is on air, by name — a look, a cue, `VOG: …`, `STING: …` |
| `STINGER Whoosh`, `LOWERTHIRD Neon` | The stinger and the lower third on air (bare when none) |
| `ARMED 1` / `0`, `HOLD 1` / `0` | The cue stack's state |
| `CUE 01.020` | The standby cue's number |
| `DECK 3 12` | The deck on air: page and count (`0 0` with none) |
| `STEP 2 5` | The presenter step and count |

Each line ends with what the device expects — LF (`\n`, what `Serial.println` and most scripts
read), CR LF, CR, or nothing — chosen per device on the page.

## An Arduino sketch

Two buttons on pins 2 and 3 (to ground, internal pull-ups), two LEDs on pins 8 and 9. `BTN1` fires
the trigger row it has on the page (say `CUE GO`), `BTN2` another (say `NEXT`); the LEDs follow
`BLACKOUT` and `ARMED`.

```cpp
// Patterns — Interactive area. 115200 baud, LF line endings (the page's defaults).
const int BTN1 = 2, BTN2 = 3, LED_BLACKOUT = 8, LED_ARMED = 9;
bool was1 = false, was2 = false;
String line;

void setup() {
  Serial.begin(115200);
  pinMode(BTN1, INPUT_PULLUP);
  pinMode(BTN2, INPUT_PULLUP);
  pinMode(LED_BLACKOUT, OUTPUT);
  pinMode(LED_ARMED, OUTPUT);
}

void button(int pin, bool &was, const char *name) {
  bool down = digitalRead(pin) == LOW;
  if (down && !was) { Serial.println(name); delay(30); }   // one line per press, debounced
  was = down;
}

void heard(const String &l) {                               // "KEY VALUE" from Patterns
  if (l.startsWith("BLACKOUT ")) digitalWrite(LED_BLACKOUT, l.endsWith("1") ? HIGH : LOW);
  else if (l.startsWith("ARMED ")) digitalWrite(LED_ARMED, l.endsWith("1") ? HIGH : LOW);
  else if (l == "PING") Serial.println("PONG");            // the page's SEND box, answered
  // "OK" and "ERR …" are the answers to what this board sent; ignore or log them
}

void loop() {
  button(BTN1, was1, "BTN1");
  button(BTN2, was2, "BTN2");
  while (Serial.available()) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') { if (line.length()) heard(line); line = ""; }
    else if (line.length() < 120) line += c;
  }
}
```

A relay from a cue: add `else if (l == "RELAY 1") digitalWrite(7, HIGH); else if (l == "RELAY 0")
digitalWrite(7, LOW);` to `heard`, and give the cue **Device — send a line** with `RELAY 1`.

## A Raspberry Pi (or any computer) over TCP

Patterns connects to the device, so the device listens. A dozen lines of Python: a button on GPIO
17 sends `BTN1`, and a lamp on GPIO 27 follows the blackout.

```python
import socket, threading
from gpiozero import Button, LED          # pip install gpiozero (Raspberry Pi OS has it)

button, lamp = Button(17), LED(27)
server = socket.socket(); server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("", 7000)); server.listen(1)

while True:
    conn, _ = server.accept()                                   # Patterns connecting (it reconnects by itself)
    button.when_pressed = lambda: conn.sendall(b"BTN1\n")
    for raw in conn.makefile("r"):                              # "KEY VALUE" lines, one per change
        line = raw.strip()
        if line.startswith("BLACKOUT "): (lamp.on if line.endswith("1") else lamp.off)()
```

On the page: **+ DEVICE OVER IP**, the Pi's address, IP port 7000, Link TCP. An ESP32 running a
`WiFiServer` on 7000 reads the same lines with `client.readStringUntil('\n')`.

## Where it fits in the workflow

- **A show with a physical GO button**: the caller's stack armed, `BTN1 → CUE GO`. A GO from the
  button is fenced like every other — refused with the reason while the stack is not armed or on
  HOLD, and answered `ERR …` to the device.
- **A lamp that tells the presenter the deck is on**: `DECK 3 12` lines — light while the count is
  above zero.
- **A relay on a look**: a cue that applies the look and a second action *Device — send a line*
  `RELAY 1`; the reverse cue sends `RELAY 0`.
- **A sensor that changes the message**: `SENSOR *` → `MESSAGE Room at *`.

The Interactive page shows every device's state — open, reconnecting, closed and why — with the
last line in and out and the counts, so a wiring problem is visible before doors. The STATE every
remote reads carries the same rows (`devices[{n,name,link,address,enabled,open,status,lastIn,lastOut}]`).

## A MIDI control surface

An APC40, a Launchpad, a nanoKONTROL, an X-Touch is a device on this page too, and for the same
reason everything above works: its messages arrive as the same short text lines a board sends, so the
trigger table IS the map.

| The surface sends | The line it arrives as |
| --- | --- |
| A pad or key down | `NOTE <ch> <note> <velocity>` — `NOTE 1 53 127` |
| A pad or key up (or a note-on at velocity 0) | `NOTEOFF <ch> <note>` — its own word, so a row learned from a press fires once and not again on the way up |
| A fader, knob or button reporting as a controller | `CC <ch> <controller> <0–100>` — a percentage, because the desk's level verbs refuse anything higher and a raw fader would silently die in the top of its travel |
| A wheel, or a motorised fader moved by hand | `BEND <ch> <0–100>` |
| A bank or patch button | `PROGRAM <ch> <n>` |
| Anything else | `MIDI <ch> <d1> <d2>` — carried so a row can still be written for it |

Channels are shown 1-based, as every controller's own manual numbers them.

### Mapping it

Press **LEARN** on the card and then press the control: the row writes itself. That is the fastest
way and the honest one — nobody can state a controller's note numbers with confidence without the
hardware in front of them, so the desk asks rather than shipping a table that is wrong at a venue.
**+ STARTER ROWS** is the other way in: a known controller's published numbers land in your own
table, editable and re-learnable, and the desk says on the button that they have not been run
against hardware.

### Lamps: the same table, read backwards

A surface cannot hear words — there is nowhere on a pad to put a sentence — but it can light. A row
whose left-hand side is one of the show's facts and whose right is a lamp reads the other way:

```
NOTE 1 53 *   →  LOOK 3           the pad fires the look
LOOK Walk-in  →  LAMP 1 53 21     the look lights the pad
BLACKOUT 1    →  LAMP 1 82 5
BLACKOUT 0    →  LAMP 1 82 0
VOL *         →  CC 1 48 %        the audio level drives the ring of light round a knob
OPEN 1        →  LAMP 1 98 3      the surface has just arrived (or come back)
```

Which way a row reads is not a setting: a left-hand side that is a surface line is a control doing
something, and anything else is the show. `LAMP ch note velocity` is a note-on — the velocity is a
colour index in that surface's own palette. `CC` and `BEND` write a controller and a 14-bit fader.
`*` puts the fact's own words in; `%` reads the fact as 0–100 and stretches it onto the wire.

### What the desk does for you

- **Presses are instant. Faders are sampled fifty times a second** — finer than a hand moves, and it
  turns a full sweep from several hundred actions into about a dozen. A control that has not moved
  produces nothing at all.
- **A level from a surface is journalled about once a second**, carrying where the fader reached.
  The level itself is instant; only the record is paced, because the journal writes to disk on the
  thread that draws the desk.
- **A lamp the desk lights is not read back as a press**, and a control with a hand on it is not
  written to for half a second — the value lands the moment they let go, so a motorised fader does
  not fight them.
- **A port that comes back is told everything again**, so a surface unplugged mid-show does not come
  back dark.

### Limits, so you find them here and not at a venue

Windows only. Windows' MIDI ports are single-client, so another application holding the surface
blocks it — the card says that is the likely reason and retries. Only two things in the whole show
take a level (the audio playlist and break music), so the other faders can fire and step but cannot
mix; there is no master fader here to give them. Endless encoders send a nudge rather than a position
and are not supported. SysEx is neither sent nor received, so a Launchpad needs putting into
programmer mode with its own tool, and there is still no MIDI Show Control and no timecode chase. A
Korg nanoKONTROL2 lights nothing until its own editor hands its lamps over. Nothing answers back to a
surface, so the card's status line is where to look.
