# MIDI learn from any button — the field's practice, and what Patterns took from it

Round 73. The question asked: "Right click MIDI/Learn function on any button can be associated
from any button, Pattern, Look, Cue, Lower Thirds, action, etc that can be utilised by a MIDI
controller. Research how the industry applies this function."

Patterns already had a map (round 27): a MIDI control surface is a device of the Interactive
area, its messages arrive as text lines (`NOTE 1 53 127`, `CC 1 7 50`), and the surface's own
trigger table is the map — a row whose left is a surface line and whose right is a wire line
is a control doing something. LEARN on the surface's card wrote the left-hand side; the
operator typed the right. What was missing was the gesture every other tool in the field has:
start from the thing you want the control to do, not from the surface.

## 1. How the field does it

The tools below were read for one thing each: the gesture, the armed state, what a press
binds, what a sweep binds, what happens to a control mapped twice, where the map lives, and
what the operator can see of it.

| Tool | The gesture | Armed state and the way out | Press vs sweep | A control mapped twice | Where the map lives | The map as a whole |
| --- | --- | --- | --- | --- | --- | --- |
| **Ableton Live** (MIDI Map Mode, ⌘M / Ctrl+M) | Enter map mode, the mappable controls turn blue; click one, move the control; the mapping is made; leave map mode | The whole window changes colour; the mapping browser lists every mapping; Esc / the MIDI button leaves | A note binds a button; a CC binds a parameter as absolute by default, with a per-mapping choice of absolute / relative and a range; **Takeover** (None / Pickup / Value Scaling) for physical faders that are somewhere else than the value | The new mapping replaces the old one on that control, silently | With the Set (the project) | The MIDI Mappings browser — a table of control → parameter with min / max |
| **Resolume Arena** (Shortcuts → Edit MIDI) | Enter the MIDI shortcuts editor, click the thing on the screen, move the control | Every mapped control shows its shortcut as an overlay on the interface; an unmapped click shows the input waiting | Buttons: **Piano / Toggle / Toggle Inverted** behaviours; sliders: absolute by default, a **Relative** option for endless encoders; a value range | Warns that the control is in use and offers to replace | Application-wide by default, with **composition** shortcuts as a layer on top | The shortcuts overlay — the whole map drawn on the interface while editing |
| **QLab** (Settings → MIDI Controls; per-cue MIDI triggers) | Two layers: workspace-wide controls (GO, stop, panic…) and a per-cue **Triggers → MIDI** tab with **Capture** — click Capture and press the control | The Capture button is armed until a message arrives; one message ends it | A note-on trigger can require velocity match or **any velocity**; a controller value can be any | The cue keeps the last capture | With the workspace | The MIDI Controls settings page for the workspace; each cue's own trigger tab |
| **vMix** (Settings → Shortcuts) | Add a shortcut, choose the function, press **Find** and press the control (the Key box fills with the note / CC) | Find waits for the next MIDI message | A note is a key; a CC becomes the function's value input; **MIDI velocity → value** for faders | Duplicates allowed; the list shows them | With vMix's settings (or exported with the preset) | The shortcuts list, with a Find and a test |
| **OBS** (obs-midi / obs-midi-mg plugins) | Pick the action in the plugin's panel, press **Listen** and move the control | Listen is armed until a message | Note → button; CC → value for sliders | The list shows conflicts | In the plugin's settings | The plugin's binding list |
| **grandMA3** (MIDI remotes) | The remotes sheet: a row per control with **Learn** — press Learn, move the control, the row fills in | The row shows LEARN until a message | Note / CC per row; a fader row drives an executor's level | Each row is explicit; the sheet shows every row | With the show file | The remotes sheet |
| **ProPresenter** (MIDI settings) | A fixed table of functions with editable note numbers, plus per-cue MIDI triggers | No learn — the numbers are typed | Notes only for the fixed table; velocity ignored | The table refuses a note used twice | With the preferences | The MIDI settings table |
| **Bitfocus Companion** (button press / Learn on some modules) | On a button's action, **Learn** reads the current value from the device into the action's options — the other direction: the device tells the deck what it is | The Learn button waits for the module's read | — | — | With the Companion configuration | The button's own action list |

### What the field agrees on

1. **Target first, control second.** The operator names what the control should do — a
   clip, a parameter, a cue, a function — and then touches the control. Every tool with a learn
   gesture is built this way. The operator never has to know a note number.
2. **A visible armed state, and a way out.** Ableton recolours the window; Resolume overlays
   the map; QLab's Capture and vMix's Find sit lit until a message arrives; every one of them
   can be cancelled without binding anything.
3. **A press is a press.** A note-on binds a button; a note-off (or note-on at velocity 0)
   is a release and never binds. QLab's "any velocity" is the default the others take silently.
4. **A sweep binds a value.** A CC or a wheel binds a fader; its value rides through, scaled
   to the parameter's range. Absolute is the default; relative (endless encoders) is a choice
   the operator makes per mapping; **soft takeover** exists where a motor-less fader might be
   somewhere else than the value it drives.
5. **The last mapping of a control wins, and the tool says so** — Ableton replaces silently,
   Resolume warns, ProPresenter refuses. Warn-and-replace is the middle the field mostly sits at.
6. **The map lives with the project**, and the exceptions (application-wide layers in
   Resolume, vMix's settings) exist for one reason — a house surface that is the same at every
   show — not because the project is the wrong place.
7. **The map is readable as a whole**: a browser, a sheet, an overlay, a list.
8. **Feedback goes back to the surface** where the surface lights (Ableton and Resolume light
   the APC40 / Launchpad grids; grandMA3 drives motorised faders).

## 2. What Patterns took

| The field's rule | Patterns, round 73 |
| --- | --- |
| Target first, control second | Every right-click menu ends with a **MIDI** group. **MIDI LEARN ▸** lists the menu's own wire lines — the entries and the drawers' choices (`LOOK Walk-in`, `PVW LOOK Keynote` on screen 2, `CUE GO 03.020`, `LT 1`, `SCREEN 2 LOCK`, `CUE GO`…) — and choosing one arms the desk for that line. The RUN surface's GO, HOLD, BLACKOUT and STOP ALL have a menu of their own (`transport`) so they are learnable too. |
| A visible armed state, a way out | The status line and the Interactive page's banner say which line waits; the menu's entry reads LEARNING and the drawer says "waiting for a control for …"; **Esc** anywhere, the banner's **CANCEL**, the menu's **Cancel MIDI learn** and `MIDI LEARN OFF` on the wire all stop it with nothing bound. |
| A press is a press | The learn waits past a NOTEOFF; the row written is the pad at any velocity (`NOTE 1 53 *`). |
| A sweep binds a value | A CC or a wheel is written anywhere in its travel (`CC 1 7 *`), and when the line has a trailing number (`AUDIO LEVEL 50`) the number becomes `*` so the fader sets the level; the same line learned on a pad stays `AUDIO LEVEL 50` and the pad sets it to fifty. |
| The last mapping wins, said out loud | A control learned twice keeps the last line; the words say what it replaced ("NOTE 1 53 on APC40 → LOOK Keynote (was LOOK Walk-in)"). Every menu that carries a bound line ticks it and names its control, and has a **Forget** line for it. |
| The map lives with the project | The bindings are the surface's own trigger rows in the show file — nothing new is stored, and a show carried to another desk carries its map. |
| The map readable as a whole | The Interactive page's **MAPPED CONTROLS** (every binding, a ✕ each), the surface's own table, STATE's `midi` row, `MIDI` on the wire, the Eye's desk node ("MIDI 12 controls bound on APC40") and a Companion key's variables. |
| Feedback to the surface | Already there since round 27 — the lamp rows (`LOOK Walk-in → LAMP 1 53 21`) — and untouched. |

### Left out, on purpose

- **Soft takeover / pickup.** The desk's level verbs are absolute and the surfaces the desk
  drives can set a motorised fader (`BEND`) — where a fader is not motorised the value jumps,
  as it does in Ableton with Takeover off. A pickup mode is a small addition to the trigger
  path once a surface asks for it; it is not here because nothing has asked.
- **Relative (endless) encoders.** Round 27's honest limit stands: a nudge is not a position.
- **A mapped-controls overlay on the picture.** Resolume draws its map over the interface;
  the desk's menus tick the bound lines instead, which reads the same for one thing at a time,
  and MAPPED CONTROLS reads the whole. An overlay is a drawing task for another round.
- **Learning the surface's lamps.** The lamp rows stay hand-written (with the starter sets):
  a lamp's colour index is the surface's palette, not something a press can teach.
- **A learn from a node.** A caller or a timer has no surface of its own; its menus carry no
  MIDI group.

## 3. The chain

```
right-click → MenuEntry (scope Learn, wire "MIDI LEARN <line>", Action MidiLearn)
    → ShowActions.RunMidi → MidiLearnService.Arm(line)
        → refused: not a wire verb / a question / no surface / none open
        → armed: DeviceService.LearnAny(...)   [status line · banner · menus tick · STATE midi.learning · Eye · deck]
surface press → MidiSurfaceLink (Windows, winmm) → DeviceService.Handle → LearnAny fires (not a NOTEOFF)
    → MidiBindings.Bind(device, line, wire)   [BulkEdit: the surface's trigger row, saved with the show]
    → Changed → the page's MAPPED CONTROLS, the status line "⌁ NOTE 1 53 on APC40 → LOOK Walk-in", the Eye
the press next time → DeviceMap.Resolve → the wire line → the action layer, journaled "from device APC40"
```

`MIDI` / `MIDI STATUS` on the wire answers the same facts as JSON (`learning`, `wire`, `label`,
`since`, `surfaces[]{name,port,open,bound}`, `bindings[]{device,control,match,command}`,
`count`, `last`, `words`); `MIDI LEARN <line>`, `MIDI LEARN OFF` and `MIDI FORGET <line>` are
the verbs; STATE carries the same row as `midi`. Companion 3.13.0: `midi_learn`,
`midi_learn_off`, `midi_forget`; `midi_learning`, `midi_bindings`, `midi_surfaces`,
`midi_words`; the `midi_learning` feedback.

## 4. Sources read

Ableton Live manual, "MIDI and Key Remote Control" (map mode, takeover modes, the mapping
browser); Resolume Arena manual, "Shortcuts" and "MIDI" (the editor, piano/toggle behaviours,
relative sliders, composition vs application shortcuts); QLab documentation, "MIDI Controls"
and "Triggers" (Capture, any-velocity); vMix documentation, "Shortcuts" (Find, MIDI value
inputs); the obs-midi and obs-midi-mg plugin READMEs; the grandMA3 user manual, "MIDI
Remotes"; ProPresenter's MIDI settings documentation; Bitfocus Companion's action Learn
documentation. All read as the vendor publishes them; none of the note tables were assumed —
the desk still learns the number from the pad.
