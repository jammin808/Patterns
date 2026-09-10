# Round 25 — Windows checklist

The headless suite proves the rules: that the Cues page changes nothing when nothing changed, where
a monitor wall's tiles land, and which clip the desk plays. These are the things only a real rig
shows — a pointer in a real dropdown, a second display, an NDI receiver, and a pair of speakers.

## The Cues page under the pointer

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | The row under the pointer holds still | PLAN → Cues with a dozen cues and the show running (a clock overlay on, so the desk is publishing). Rest the pointer on a cue for thirty seconds | The hover colour stays put. No flash, no blink, not once |
| 2 | A dropdown can be reached | Select a cue, open SETTINGS ▸, and open an action's **kind** picker. Leave it open and move the pointer down the list slowly | The list stays open and a row can be clicked. Repeat with the target picker and the person picker |
| 3 | A box keeps what you type | Type a long value into an action's text box, slowly, pausing mid-word | Nothing is lost, nothing is reselected, and the caret does not jump |
| 4 | And the After box | Type `12.5` into an action's **After**, pausing between characters, then Tab out | It reads 12.5. Change the step above it and watch this row's **+N s** move without its own number changing |
| 5 | The list still keeps up | Rename a cue, break one (point a look action at nothing), add one, delete one, drag one | Every change appears within a beat, in place, without the list jumping |

## The monitor walls

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 6 | It is findable | Open SETUP → Multiview on a desk that has never had one | The page is there beside Screens, with + ADD A MULTIVIEW and a line saying what a wall arrives with |
| 7 | A wall arrives ready | Press + ADD A MULTIVIEW on a rig with two screens | Tiles for the programme, the preview, both screens and a clock — and the preview arrangement shows the programme and preview large with the rest along the bottom |
| 8 | On a spare display, with the show still the show | Set the programme to a test pattern. Tick a second screen under WHERE IT SHOWS | The spare display shows the wall; the audience's screens still show the pattern; the PROGRAM tile on the wall shows that pattern. This is the case the old build could not do |
| 9 | On NDI, in one tick | Tick an NDI sender | An NDI receiver sees the wall within a second, and the NDI page shows that sender on its own screen — nothing to set there by hand |
| 10 | On the stream | Tick the stream, then start it | The stream carries the wall. Untick it and the stream is back on the programme |
| 11 | Two walls at once | Add a second wall, give it different tiles, and tick a different output for it | Both draw, each its own; the line beside each output says which wall it is showing |
| 12 | Dragging chooses what is watched | Drag a screen tile to the top of the list with its grip | It becomes one of the large ones as you drag, on the page and on the wall itself |
| 13 | Every layout on a real monitor | Try each: programme and preview large, one large, one large down the left, the even grid — with 3, 6 and 12 tiles | Nothing overlaps, nothing leaves the wall, every tile keeps its target's real shape, and the labels stay readable in the small strip |
| 14 | An older show opens as it was | Open a show saved before this round that had a multiview on a screen | The wall is on SETUP → Multiview, laid out as the even grid it always was, and the screen still shows it. Two screens that had the same tiles now share one wall |
| 15 | Adopting a screen keeps the wall | In PREP, build a wall with planned screens, then adopt them at the venue | Every tile follows onto the real screen |
| 16 | Removing is safe | Tick three outputs for a wall, then remove the wall | All three go straight back to the programme; nothing is left drawing a wall that no longer exists |
| 17 | Nothing costs a frame | Two walls on two outputs with a 4K programme, NDI and the stream live, for ten minutes | Machine → STABILITY: no change in the worst frame |

## What the desk is listening to

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 18 | The default is one soundtrack | Put a clip with sound on the programme, a different clip on a confidence screen's own picture, and load a third into the preview with EDIT SAFE open | You hear **only the programme's**. This is the whole point — before this round you heard all three |
| 19 | The room is untouched | With the desk on the programme, listen at a speaker fed from an HDMI screen | The screen's own clip is still heard in the room. The monitor is the desk's speakers, never the outputs |
| 20 | Hearing the next one | Audio → WHAT THE DESK IS LISTENING TO → the preview | The preview's clip, alone. TAKE it and it carries on — now as the programme |
| 21 | One output on its own | Pick *one output on its own* → the confidence screen | That screen's clip, alone. Pick a screen that is simply following the show and you get the show, not silence |
| 22 | Silence | Pick *nothing* | Every clip silent at the desk; the audio playlist, a VOG and a stinger all still play |
| 23 | Your own mute still wins | Mute a clip on the Media page, then listen to whatever it is on | Silent. The monitor only ever takes sound away |
| 24 | A web page counts | Put a YouTube page on the programme and another in the preview | Only the programme's plays |
| 25 | It is not content | Save a look with the monitor on the preview, recall it on another machine | The look does not carry the monitor choice, and recalling it never crossfades a screen |
