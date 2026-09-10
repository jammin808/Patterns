# Round 24 — Windows checklist

The headless suite proves the rules: a cue's shape in time, the drag that keeps it, and every
transition whole at both ends on a sink with no graphics card. These are the things only a real
rig shows — a wall of real pixels, a real GPU beside a real software encoder, a caller's hands on
a trackpad in a dark room. Tick them on a Windows machine with the full build, two displays, an
NDI receiver and a stream destination.

## A cue with a shape in time

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | The default is unchanged | Open a show built before this round and fire a few cues | Every step still goes on the GO. The After column reads 0 everywhere and the line under the list says nothing about a length |
| 2 | A wait lands where it was asked for | Build a cue: a look, then a lower third with **After 3**. Press GO with a stopwatch | The picture lands on the press; the name arrives three seconds later. The row reads **+3 s** and the line under the list says the cue runs for 3 s |
| 3 | The tail is named while it runs | Watch the status line and the journal through that cue | The status line names each step as it runs, and the journal has it with the cue's name and its place in it — not one entry for the whole cue |
| 4 | The next GO owns the list | Build a cue with a 10 s tail. Press GO, then press GO again after 2 s | The first cue's remaining steps **never land**. The second cue runs cleanly from its own press |
| 5 | So does everything that means "stop" | Repeat with STOP ALL, with disarming the list, with RESET, and with loading another show | The tail is gone in every case. Nothing from a cue two cues ago reaches the audience |
| 6 | The drag keeps the shape | Build 0 s / 3 s / 5 s across three steps. Drag the third step to the top with the grip (⠿) | The order changes; the timing does not — the cue still runs at 0 s, +3 s, +5 s, with the moved step now at 0 s. The ↑ ↓ buttons do the same |
| 7 | The grip is a grip, not a hair trigger | Click the grip without moving. Then drag it 2 px, then 10 px | A click selects and does not reorder. 2 px does nothing. 10 px picks the row up. On a trackpad, in gloves, in the dark |
| 8 | The sheet carries it | Export the cue list to CSV, open it in Excel, change an After, import it back | The After column is there, reads in any position by its header, and comes back with the cue. A template CSV has it too |
| 9 | A long cue does not drift | Build a cue with steps at 0, 5, 10, 30 and 60 s and run it against a stopwatch | Each step lands within a beat (50 ms) of its moment, and the last one is not late by the sum of the others — the moments are absolute from the press, not chained |

## How one picture becomes the next

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 10 | Every kind on a real wall | SETUP → Screens → TRANSITIONS, 1000 ms. Take each of Dissolve, Dip, Wipe, Push, Brand stinger and Reactive between two very different looks | Each one starts on the picture the room has and ends on the picture the show asked for, with no flash, no black frame and no jump at either end |
| 11 | The same on the stream and on NDI | Repeat with an NDI receiver and the stream recording | **Frame for frame the same transition.** This is the one that matters: the client's stream must not get a hard cut where the room got a wipe |
| 12 | And on the desk's own miniatures | Watch the switcher tiles and the preview through each kind | The tiles run the same transition, at their own size. A thumbnail never fades (it never has) |
| 13 | A wipe travels the way it says | Wipe, Travels *Left to right*, Edge 0.02 then 0.5 | The edge crosses left to right, hard at 0.02 and a slow bleed at 0.5. Change Travels to *Right to left* and it goes the other way |
| 14 | A push is one movement | Push, each of the four ways | The two pictures move together with no gap, no tearing at the seam and nothing showing through between them |
| 15 | A dip hides the cut | Dip, brand background unticked, colour #000000, 800 ms | The screen goes to black and back, and the change is **invisible** — no frame of either picture is seen through the dip |
| 16 | The stinger is the client's | BUILD → Branding: set a primary, a secondary, a background and a logo. Brand stinger, 900 ms, between two takes | Two bars sweep in in those colours, the background fills, the logo lands in the middle and the picture has changed underneath. Change a brand colour and take again: the stinger changed with it |
| 17 | A reactive wipe on a big wall | Reactive, Scene *Vortex*, on a 4K output with a fractal on both sides of the change | The change spirals in. Machine → STABILITY: **the worst frame does not move** — the matte is 256 px and is scaled up |
| 18 | And it costs nothing when idle | Sit for two minutes with no changes after a reactive transition | Memory does not hold the matte — the buffers go the moment the change is over |
| 19 | A bright dip is limited | Dip, brand background unticked, colour #FFFFFF. Fire four look changes in one second | The first dips through white. The ones too soon after it are drawn as **dissolves** — the picture still changes, it just does not flash to do it. No strobe |
| 20 | A setting changed mid-wipe does nothing | Start a 3000 ms wipe, and while it is crossing the screen change HOW to Push | The wipe finishes as a wipe. The **next** change is a push |
| 21 | The pickers are instant | Click through Dissolve → Dip → Wipe → Push → Brand stinger → Reactive | Travels, Scene, Edge and the dip colour appear and disappear on the click, with no lag and no flicker of the wrong row. The note under them changes with them |
| 22 | A cue can name its own | Put *wipe left 600* in a cue's look transition box; another with *stinger*; another with *reactive vortex 1200*. Switch the show's crossfades **off** | Each recall arrives its own way even with fades off, and the change after it is the show's own. The printed sheet reads `(wipe right to left, 600 ms)` |
| 23 | A typo is caught before the show | Put *wype* in a look transition box and run the checks | The cue reads Broken, naming the word and the ones that would have worked. Nothing reaches the wall |
| 24 | A look's own fade is not lost | Give a look a 1500 ms fade in a cue and fire it from an F-key, from the caller's list and from the phone | The change takes a second and a half every time — not the show's 400 ms. (This is the tally-publish fix; it was silently wrong before this round) |
| 25 | Nothing costs a frame | An hour of transitions between clips, decks, web pages and reactive scenes, with the stream and NDI live | Machine → STABILITY: no change in the worst frame, no growth in the working set, no contained render faults |
