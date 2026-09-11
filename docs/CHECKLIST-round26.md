# Round 26 — Windows checklist

The headless suite proves the rules: that a transition arms on a take and on nothing else, that a
FOCUSED take moves one tile, which device each mount is handed, and where an imported picture ends
up. These are the things only a real rig shows — a wall the room can see crossfading, a second audio
interface, a pair of headphones, and a show carried to another machine on a stick.

## The transition waits for a take

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | Editing does not fade the wall | EDIT SAFE **off** (Admin → Switcher, or leave it), a 2-second dissolve on (SETUP → Screens → TRANSITIONS). Open the Pattern page and drag a colour slider slowly for ten seconds | The wall follows the slider immediately and continuously. Not one crossfade. Before this round every keystroke started one |
| 2 | Choosing a screen to edit does not fade | With several screens, click each switcher tile in turn, then pick targets in EDITING TARGET | The PREVIEW pane changes shape and picture at once. Nothing on air moves |
| 3 | A take still crossfades | EDIT SAFE on, build a different picture, press TAKE | The wall dissolves over the set time, on every screen, NDI send and the stream at the same instant |
| 4 | And a cut still cuts | CUT | Instant, everywhere. No half-dissolve left behind |
| 5 | A transition in flight is never abandoned | Set an 8-second dissolve. Press TAKE, and while it is crossfading type in a text overlay, open another page and let an autosave land | The dissolve runs to the end, smoothly. This is the one this round nearly broke: an edit mid-transition must not tear it down |
| 6 | Unless the picture is cut out from under it | Press TAKE (8 s), then press CUT halfway | The cut lands at once and the dissolve is gone — no ghost of the old picture |
| 7 | A look, a cue and a stinger still arrive as designed | Fire an F-key look, GO a cue with a look action, fire a brand stinger | Each arrives with the show's transition, exactly as before this round |

## The switcher's round trip

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 8 | The programme comes back into the preview | With EDIT SAFE **off** and a picture on air, press **→ PVW** on the **PGM** tile | EDIT SAFE opens, the preview holds what the room is watching, and the status line says so. The room is untouched |
| 9 | And it is a copy | Change the picture in the editors | The preview changes; the wall does not |
| 10 | One screen, pulled and put back | → PVW on screen 2's tile. Change the picture. Press **SEND** on screen 2's tile | Screen 2's **PVW miniature** shows the new picture. The room still sees the old one on every screen, screen 2 included |
| 11 | The take lands on that tile alone | With the picker on **FOCUSED**, press TAKE | Screen 2 changes. Every other screen keeps its picture, exactly as an un-armed tile does |
| 12 | The focus is where you left it | Watch the tile border through steps 10–11 | Screen 2 stays focused from the SEND onward — you never have to re-click it before the take |
| 13 | A staging you do not take leaves nothing behind | SEND on screen 3, then click screen 1's tile and TAKE (FOCUSED) | Screen 1 changes; screen 3 keeps the audience's picture. Then recall a look: **screen 3 follows it**. Before this round screen 3 had quietly stopped following the show |
| 14 | SEND TO TICKED is still the live one | Tick two tiles, press SEND TO TICKED | Those two change on air at once, as before |
| 15 | It costs one dissolve, not two | An 8-second dissolve, EDIT SAFE on. SEND on a tile, wait, then TAKE (FOCUSED) | The SEND is instant and silent on air; the TAKE is the only crossfade the room sees |

## Two audio wires

You need a second output for this block: a USB interface or a spare card for the programme, and the
machine's own output (or headphones) for the monitor. One interface is the case round 25 got wrong.

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 16 | The programme goes where you sent it | Audio → PROGRAMME OUT: tick the USB interface only. Play a clip with sound on the programme | The room hears it **on the interface**. Nothing comes out of the machine's own speakers |
| 17 | A missing interface is said out loud | Unplug the interface and play a track | The sound falls back to the machine's output **and a red line under PROGRAMME OUT names the interface**. Plug it back in, press Refresh devices, play again — the line clears |
| 18 | The monitor is a second wire | Audio → MONITOR: pick the machine's own output (or headphones). Set WHAT THE DESK IS LISTENING TO → the preview. With EDIT SAFE open, load a clip with sound into the preview | The preview's clip is in the **headphones**; the programme's clip is still on the **interface**, at full level. Both at once |
| 19 | The room is never silenced to audition | Repeat 18 while someone listens in the room | They hear no change at all when you switch what you are listening to. This is the fault this round fixes |
| 20 | With no monitor output, the desk refuses rather than pretends | Set MONITOR to none, keep listening to the preview | The programme carries on; the preview's clip is silent; and the line reads *Pick a monitor output below to hear anything else* |
| 21 | The same clip in both goes to the room | Put the same file on the programme and in the preview, listening to the preview | It plays on the **interface**, not the headphones. One decoder has one device, and the room's claim wins |
| 22 | One output on its own | Give a confidence screen its own clip. Listen to that output | Its soundtrack in the headphones, the programme still on the interface |
| 23 | The soundcheck tone is on the PA | Audio → TONE ON with the interface ticked under PROGRAMME OUT | The tone comes out of the **interface**, not the laptop's speakers. Change the first ticked output while the tone is up — the tone moves with it |
| 24 | The monitor picker keeps what you chose | Pick a monitor output. Plug a display in, load a show, change a screen's label, wait a minute | The picker still reads what you chose. It must never quietly become "none" |
| 25 | A show carries the choice | Save the show, restart, load it | PROGRAMME OUT and MONITOR are as you left them. A device named but absent stays named, with the red line explaining |

## The badge on every picture

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 26 | Reactive carries it | BUILD → Reactive, pick each of the six scenes with the badge on (BUILD → Branding) | The Patterns badge is in the lower third on every one, on the outputs and on the preview |
| 27 | And every other kind | Step through every Pattern Type | The badge is there on all of them, except a Multiview wall (never) and Media (only with *on media too* ticked) |
| 28 | On every sink | Check an NDI send, the stream and a multiview tile of the programme | Same badge, same place |

## Importing a picture or a short clip

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 29 | One gesture | BUILD → Lower thirds, select a design, press **+ PICTURE** | The file picker opens straight away, offering **pictures only** — no audio, no PowerPoint |
| 30 | It comes into the show | Choose a headshot from the Desktop | It draws on the stage; the element is named after the file; the status line says it is in the show's media folder. Look in `media/` beside Patterns.exe — the file is there |
| 31 | And the original is untouched | Check the Desktop | The file is still there. Nothing is moved out from under the operator |
| 32 | A clip too | **+ CLIP**, choose a short b-roll file | The stage shows its name and *plays on PVW and on air*. Press **PVW** on the design — now it plays, and (with a monitor output named) is heard in the headphones only |
| 33 | The show travels | Copy the whole Patterns folder and the show file to a second machine — or just to another drive letter — and open it | The headshot and the clip draw. This is the case that used to be a blank rectangle in front of the room |
| 34 | What did not travel is said | Point an element at a file that does not exist (type a path) | A red line under the file box names it. Run the cue checks — a warning names the design, the file and the element; the cue is **not** refused |
| 35 | A big file is not doubled onto the stick | Choose a clip over 512 MB | The design points at it where it is and the status line says why. `media/` does not grow by a gigabyte |
| 36 | The same file twice | Choose the same headshot for a second element | One copy in `media/`, not two. A *different* file with the same name gets `name (2).jpg` and the first design is untouched |
