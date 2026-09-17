# Companion as a programming surface — the Navigator

*Round 74. The brief: "Companion buttons for each of the rails — when pressed, the Companion
surface changes to show the menus with the main options and features for each section so an
operator can rapidly program a show from an external surface … go directly to a menu within a
rail, or directly to a sub-menu that requires the collapsible right-side column … full feedback
states and the saved looks, cues, videos, web pages, arcade and audience programming … research
how peers handle this and be better. Make full use of Companion's advanced features." And the
question: does Patterns make use of dynamic key generation in Companion?*

## 0. The question, answered from the code

Yes — three ways, since round 54, and a fourth from this round:

1. **Keys that label themselves.** The look bank, the cue bank, the lower-third, person,
   stinger, track, part, screen and node keys carry `$(patterns:look_3)`-style variables: drag a
   row once, and every look or design made later appears on the next key
   (`src/presets.js`, `look_bank_n`, `cue_bank_k`, …; `src/state.js` `bankVariables`).
2. **Presets built from the show itself.** `buildShowPresets(state)` makes one preset per look,
   design, person, stinger, VOG, track, break-music item, playlist part, screen, upcoming cue and
   pattern kind, under the *… — this show* sections, and `refreshShowPresets()` rebuilds them
   whenever the show's lists change (a signature over the names, `showSignature`), so the preset
   list in Companion follows the show file (`src/main.js`).
3. **Layered and alternatives presets** — the stage timer is drawn with Companion 5's graphics
   (a ring gauge, digits, the ground in the timer's colour), with a plain variant for a
   Companion that has no layers.
4. **This round: the Navigator** — a fixed set of *slot* keys whose words, colours, ticks and
   presses are generated live from the desk's own menus (rails → pages → a page's menu → a thing's
   right-click menu → its drawers). The keys are never rebuilt; what they say is.

What the module did **not** use before this round: Companion's action recorder, `learn` on
actions, rotary (Stream Deck+) actions, pages that follow the desk, or any way for the deck to
say where it is. Each is below.

## 1. How the field hands a surface its navigation

| Where | The navigation | What is dynamic | What the device knows about the deck |
| --- | --- | --- | --- |
| **Companion itself** | Pages (a surface shows one page; the internal *Surface: set page / page up / page down* actions turn it; a key can jump to a page), button *steps* (one key, several states), custom variables and expressions in every text, *Triggers* (events → actions), the *Action Recorder* (Companion records a device's actions into a button while you work on the device), *Learn* on an action (the module reads the device's current value into the action's options), presets in sections, dynamic presets (a module may re-set them any time), `bonjour-device` discovery, Satellite (surfaces on other machines), the TCP/UDP/HTTP/OSC APIs to drive Companion from outside | Presets, variables, feedbacks, option choices; Companion 5's graphics elements | Nothing, unless the device drives Companion's API |
| **Elgato's Stream Deck app** | *Folders* (a key opens a folder — a page with a back key), profiles per application, Multi Actions; plugins render their own keys live (`setTitle`, `setImage`) | Everything a plugin draws | The plugin is the device's own code |
| **vMix** (Companion module; the vMix Stream Deck plugin) | Dynamic presets per input, input names as variables, tallies as feedbacks; vMix's *Shortcuts* with *Find* | Inputs, overlays, audio buses | No |
| **Blackmagic ATEM** | Input names as variables; presets per input; tally feedbacks; macros as keys | Inputs, macros | No |
| **OBS** | Scenes and sources as dropdown choices refreshed live; presets per scene; feedbacks for programme/preview | Scenes, sources, filters | No |
| **Resolume Arena** | Clips, layers and columns as keys through OSC/the module; a clip's name in a variable, its playing state a feedback | Composition | No |
| **QLab** | The cue list through OSC: cue numbers and names as variables, the standby cue on a key, GO | Cues | No |
| **ProPresenter** | Presentations and slides on keys, the current slide as a variable, clear keys | Playlists | No |
| **disguise / Watchout / Pixera** | Timelines, cues, layers through OSC or their own APIs; keys per cue or timeline | Cues, timelines | No |
| **Ontime (rundown)** | One preset per event of the rundown, the running event's clock as variables, feedbacks for running/late | The rundown | No |
| **Loupedeck / Razer Stream Controller** | Pages and *touch* screens per application, with dynamic icons from the app's plugin | The app's plugin | The plugin |
| **TouchOSC / Open Stage Control** | Layouts drawn by hand; a page per section; values by OSC | Values only | No |

What every peer has in common: **pages are the navigation, the device's lists become dynamic
presets or variables, and the device never knows where the deck is.** None of them turns the
device's own context menus into the deck's pages, and none lets the desk say "the deck is on the
Looks page" the way a desk says a screen is on air.

Where Patterns was already ahead: the desk drives Companion's TCP API as a device (round 54.3 —
a cue can turn a surface's page), the deck is found by mDNS and named on the desk, every key
lights from the one palette both sides share, and the desk's right-click menus are already on
the wire as JSON with each entry's line (round 60: `MENU LOOK Walk-in`).

## 2. Companion's advanced features against the module

| Feature (Companion 5 / module base 2.x) | Before | Now |
| --- | --- | --- |
| Actions with dropdowns, text, numbers; `raw` line | used | used |
| Boolean feedbacks with palette styles; `internal:buttonCurrentStep` | used | used |
| Variables that label banks; `$(patterns:…)` in presets | used | used + the Navigator's slots |
| Preset sections, groups ticked per desk | used | + *Navigator* section, rails and pages |
| Layered presets, alternatives, gauges | used (stage timer) | used |
| `bonjour-device` discovery | used | used |
| Dynamic presets (re-set when the show changes) | used | + rails/pages from `NAV` |
| **`learn` on an action** | not used | used: `nav_slot` and `raw` learn the line the deck is on |
| **Action recorder** (`handleStartStopRecordActions`, `recordAction`) | not used | used: while Companion records, what the operator does on the desk lands in the button as `raw` lines (`RECORD ON` on the wire; the desk pushes `ACTION <line>`) |
| **Rotary actions** (Stream Deck+ encoders) | not used | used: the audio level and the break-music level on encoders, a step from the level STATE reports |
| Button steps | used (two-press TAKE OVER) | used |
| Triggers, custom variables, expressions | Companion's own | the Navigator's variables are expression-ready (`nav_slot_n_on`, `nav_level`) |
| TCP/HTTP API driven by the desk | used (device profile) | used; the deck now tells the desk where it is (`NAV DECK`) so the Remote page and the Eye read it |
| `png64` / image feedbacks | not used | left (§6) |
| Satellite | left (round 54) | left |

## 3. The Navigator

**One Companion page, always the same keys; what they say comes from the desk.** A row of
fixed keys — **HOME**, **BACK**, **◀ PREV**, **NEXT ▶**, **RUN / MENU** mode, **DESK ⇄ DECK**
follow, **SETTINGS ▸** — and a grid of **slot keys** (24 by default; 8, 16 or 32 in the
connection's settings, whatever the surface has). The module keeps one small state machine
per connection:

```
level 0  RAILS      SHOW · PLAN · BUILD · SETUP · ADMIN                      (from NAV)
level 1  PAGES      the rail's pages: Cues · Looks · Install …               (from NAV)
level 2  PAGE MENU  the page's own menu: its things and its build verbs     (MENU PAGE Looks)
level 3  THING      a thing's right-click menu, exactly as the desk shows it (MENU LOOK Walk-in)
level 4  DRAWER     a drawer's choices (Look ▸, Pattern ▸, MIDI LEARN ▸ …)  (from level 3's JSON)
```

- A press on a slot **descends** when the entry opens something (a rail, a page, a drawer, a
  thing in MENU mode) and **runs** when it is a line (the entry's `wire`, sent as it is). The
  page's things run in **RUN** mode — the Looks page's "Walk-in" puts the look on air, the
  fastest path — and open their own menu in **MENU** mode, the map-mode idea from the MIDI
  research, so the same key builds or fires depending on one visible toggle.
- Every level renders into the slots with paging (PREV/NEXT) when it is longer than the grid;
  the title key reads the breadcrumbs (`SHOW › Looks › Walk-in`) and the page count.
- **Colours are the desk's**: a slot wears its entry's tone (amber preview, red live, blue
  cue stack, grey go-to, mint ask, violet MIDI, a rail's own hue on the rails and pages level),
  its tick (`on`) lights it, and a disabled entry (`because`) is dimmed with the reason in its
  variable — the same three promises the desk's menus make.
- **Follow, both ways.** With DESK ⇄ DECK on, a page chosen on the deck puts the desk on that
  page (`NAV <page>`), a thing chosen selects it there (`NAV <page> <item>`, which opens the
  settings column for a cue, a screen or an element — the "sub-menu that needs the collapsible
  column"); and when the desk's page or selection changes (STATE's `nav` row), the deck's
  navigator follows to the same level. The deck says where it is (`NAV DECK SHOW › Looks`), so
  the desk's Remote page and the Eye's deck node read it — the desk and the deck are never out
  of step, and a caller can see which page each deck is on.
- **Instant.** A level is one line and one reply (the desk answers a `MENU` in microseconds from
  the same builders the desk's own menus use); a re-render is a batch of variable writes and one
  feedback check; nothing polls. STATE pushes re-render the level in place when the show changes
  (a look saved on the desk appears on the deck's Looks level at once).
- **Resilient.** Replies are matched to requests in order (every line the module sends gets
  exactly one reply from the desk); a reply that does not come in time re-syncs the queue; a
  disconnect empties the navigator and its keys go dark; an `ERR` lands in `last_error` and the
  navigator stays where it was.
- **Attempts are not facts.** A slot's light is the desk's STATE, never the deck's press; the
  breadcrumb is what the desk answered, not what the deck asked for.

### The build verbs

Programming from the deck needs verbs the wire did not have. This round adds them, with the
same rule as everything on the wire — a line the desk journals and answers:

| Line | Does |
| --- | --- |
| `LOOK SAVE <name>` | The preview as a look — saved anew, or the look of that name updated (the desk's own SAVE LOOK) |
| `LOOK UPDATE [name]` | The look on air (or the named look) updated from the preview |
| `LOOK DELETE <name>` | The look removed (its cues keep their recall, which the validator then flags) |
| `CUE ADD [name]` | A cue after the standby (or at the end), numbered between its neighbours, named |
| `CUE DELETE <number\|name>` | The cue removed |
| `PRESET SAVE <name>` | The editing target's picture as a preset in the Library |
| `LT NEW <name> [FROM <preset>]` | A lower-third design from a preset (Neon, Clean …), named |

Text — a name — comes from a Companion text option or a variable; the operator types it once on
the deck, or on the desk. Every page's menu carries its build verbs as entries, so a deck can
save a look, add a cue or make a design without leaving the navigator.

### The recorder

Companion's *Action Recorder* asks a connection to start recording; the module answers by
telling the desk `RECORD ON`. From then the desk pushes `ACTION <line>` for every action the
operator performs on the desk itself (the desk, its keys, a MIDI surface, a menu) — the wire
line that reproduces it, written by a writer that round-trips the wire's vocabulary — and the
module records each as a `raw` action on the button being recorded. Press RECORD in Companion,
do the thing on the desk, stop: the button now does it. `RECORD OFF` ends it; a deck that
disconnects ends it. Presses from the wire itself are not fed back (a deck recording its own
presses would loop), and a cue's steps and the schedule are not the operator's hand.

### Encoders

A Stream Deck+ encoder turns a level: `audio_level_step` and `music_level_step` read the level
the desk reported in STATE and send the absolute level ± a step — never a relative nudge the
wire does not have — so the digits on the LCD strip are the desk's and a turn while
disconnected does nothing. The presets carry `rotate_left` / `rotate_right` and a press that
mutes to 0 / restores.

## 4. The desk side

- `NAV` — the rails and pages as JSON (`groups[]{id,label,hue,pages[]}`, `pages[]{header,group,hue,room}`), the desk's page, its rail, whether the Run surface is up, the settings column (`settings{open,key,title,identity}`), the last selection.
- `NAV <page> [item]` — the desk goes to a page (a rail's name goes to its first page) and, with an item, selects it there through the same route the menus' GO TO entries use — a cue by number, name or id, a look by name, a design or a person, a screen by number or id; the settings column opens for what has one. `NAV HOME` is the panel, `NAV BACK` the page before, `NAV SETTINGS ON|OFF|TOGGLE` the column. Desk-only — a running order never turns the desk's pages.
- `NAV DECK <words>` — the deck says where its navigator is; the Remote page's deck line and the Eye's deck node carry it.
- `MENU PAGE <name>` — the page's own menu: a group per kind of thing on it (looks, cues, designs, people, library tiles, screens, overlays, stingers, tracks, games, nodes, lenses…), each entry with its line, its tick and its GO TO route; a group of build verbs; a MIDI group like every menu.
- STATE's `nav` row: `{ page, group, hue, run, settings{open,key,title}, back, decks[]{name,where} }`; the Eye's desk node says "Desk on Looks · settings column: SELECTED CUE · 03.020"; a deck node says where its navigator is.
- `RECORD ON|OFF` — the action feed for the recorder; `WireWriter.Line(action)` is the writer (tested by round-tripping the wire vocabulary table).

## 5. AI

The assistant already reads the menus' JSON and the wire; the navigator gives it a surface to
describe: "which deck is on which page" is in its facts through the Eye, and a question like
"build me a Companion page for the keynote" answers with the lines a `raw` key takes, in the
order a page would carry them. The Eye's deck nodes carry the navigator's whereabouts, so the
picture of the show includes what each surface is looking at.

## 6. Left out, and why

- **A generated Companion page file.** Companion imports page exports, and the desk could serve
  one with the Navigator laid out; the export schema is versioned and no Companion ran here to
  confirm it, so the Navigator ships as presets (drag a section once) and the file is the next
  step once a Companion in the room has produced one to read.
- **Pictures on keys** (the programme or a look's thumbnail). Companion draws PNG from a
  feedback's `imageBuffer`/`png64`; the desk serves JPEG today and a PNG key-sized endpoint is a
  small addition, but the module would poll at a frame a second per key and the value on a
  72-pixel key is doubtful — left until asked for.
- **Satellite** — as round 54 left it.
- **Soft takeover on encoders** — the level is absolute from STATE, so a turn never jumps.

## 7. What a Companion in the room will confirm

The Navigator's slots re-render on a real surface within a STATE push; the recorder's `ACTION`
lines land in a button as `raw` actions; the encoders' `rotate_left`/`rotate_right` presets
draw on a Stream Deck+; `NAV DECK` shows on the Remote page while a deck is navigating.
Everything below that line is tested here: the state machine over a fake wire, the replies
matched to their requests, every line the module sends parsing on the desk, the desk's NAV
family, `MENU PAGE` for every page, the build verbs, the writer's round trip, the STATE row,
the Eye's words.

## 8. Sources read

Bitfocus Companion 5 user documentation (pages, surfaces and the *set page* actions, button
steps, triggers, custom variables and expressions, the Action Recorder, Learn, presets, the TCP
/ HTTP / OSC APIs, Satellite); `@companion-module/base` 2.x type definitions as installed in the
module's `node_modules` (`handleStartStopRecordActions`, `recordAction`, `learn` on actions,
rotary steps, preset sections); Elgato Stream Deck documentation (folders, profiles, Multi
Actions) and the Stream Deck SDK (`setTitle`, `setImage`); the vMix, ATEM, OBS, Resolume, QLab,
ProPresenter and Ontime Companion modules' documentation and their variable/feedback lists;
Loupedeck's page model; TouchOSC and Open Stage Control documentation. All read as their authors
publish them; nothing here claims a peer does what its documentation does not say.
