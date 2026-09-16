## Patterns

Stream Deck keys for the Patterns show display suite: the cue stack (GO, standby, HOLD, ARM), looks, screens
and canvases, lower thirds with the sign-off flow, stingers and VOGs, the overlays (clock, message, countdown,
logo, PiP, weather), the audio playlist and break music, decks and web pages, the caller's VT clock, the speaker's
stage timer and messages to the stage, every other Patterns on the network (the nodes) and the twin — with live
feedback in one colour language, and keys that label themselves from the show that is loaded.

### Setup

1. In Patterns, open the **Remote** page (SETUP) and make sure remote control is on. The wire is the
   *Companion (TCP)* port, 9697 unless you changed it. The desk announces itself on the network.
2. In Companion, add a **Patterns** connection. Pick the desk from **Desk on the network** — every Patterns
   on this network is listed by its kind and machine — or type its IP and port by hand.
3. Tick the **preset groups** this desk uses (the rest stay out of the preset list) and drag keys from the
   sections: the banks first (Look bank, Cue bank, Screens, Nodes — keys that label themselves), then the cue
   stack, transport, the stage, and the *… — this show* sections that hold one key per item of the show.

### Colours

One hue per kind of thing, one treatment per state: green is on air, armed or running; amber is a preview, a
hold, or *changed since*; red is a lower third on screen, the stream live, a failed cue, or a screen black on
its own; the overlays are sky blue; the presenter's things (a web page, a deck, a clip) are steel blue; the nodes
are one colour each (desk, caller, arcade, stage timer) and go dark when a node stops being heard. A bank key
with nothing behind it dims.

### The stage timer key

*STAGE TIMER* reads what the speaker sees, in the timer's own colour — green, then amber and red at the
thresholds set on the desk's Stage page — with a ring for how far through the segment is on a Companion that
draws layered keys, and the plain key everywhere else. Press it to pause, press again to resume.

### The armed web VT and the routing matrix

*ARM VT* holds the web page's video at its mark (or where the player is now) so it plays from there the moment
the page goes to air — a take, a cue, the clicker's NEXT; *MARK VT* sets the point, *DISARM VT* clears it, and the
`web_vt` action takes a time of its own. *ROUTING* switches the desk's audio matrix — which soundtrack goes to which
output, HDMI screen or NDI send — on or off; `audio_route` puts a source (the programme, a screen's own picture,
the music, a VOG…) on a destination at a level in dB, `audio_vog` says whether a VOG ducks, replaces or stays off it.

### Two presses for the twin

*TAKE OVER* and *TAKE BACK* arm on the first press (the key reads SURE?) and send on the second; *STAND BY*
disarms a first press that was a mistake. The desk's own fences still apply — a takeover the wall switch
cannot confirm is refused and the key says so through the twin's state.

### The God's Eye page

The desk's picture of the whole show — every display, screen, source, device, deck, node and audio
path with its light — reaches the deck as one STATE row. The **GOD'S EYE** key shows the headline
(the worst thing and what is wrong, or all green) lit by the worst light: red is wrong now, amber
needs a look, green is all green; a press steps the desk's eye to the next problem. **NEXT / PREV
PROBLEM** walk everything red or amber, reds first; a **LENS** key keeps one band of the picture
(VIDEO, CONTROL, AUDIO, ROOM, PROBLEMS); **RESET** puts the whole picture back. `EYE FOCUS screen 2`
from any key puts the desk's eye on a screen. The keys move the picture on the desk's Eye page;
they change nothing on the wall.

No module? The same protocol works with Companion's built-in **Generic TCP** connection — one command per line,
as `docs/REMOTE.md` in the Patterns repository lists them — without feedback.

## Installing a build

Modules → *Import module package* → the `.tgz` (unzip the CI artifact first). Companion keeps one
copy per version: a rebuilt module needs a new version number, or the installed one removed first.
