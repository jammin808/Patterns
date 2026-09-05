# Round 13 — Windows checklist

The headless suite proves the logic on every push. These are the things only a real rig shows:
one line per item, what to do, and what you should see. Tick them on a Windows machine with the
full build, two displays, an audio interface and, where named, a phone on the network, a Stream
Deck with Companion and an OSC controller.

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | The caller's VT clock — what is left, the ten-second out, the rehearsal's skip | Put a three-minute clip on the program (Media → Video) with OUTPUTS ON; open SHOW → Panel and the Run page (POP OUT); open the phone's SHOW tab, a Companion key with the *VT clock* preset and an OSC label on `/patterns/state/video/remaining`; watch a minute; press ⏭ LAST 10 s; press ⟲ RESTART; type `VIDEO END 30` in the console; fire a video stinger and press ⏭ LAST 10 s; put a playlist with a timed video item on air and press ⏭ LAST 10 s; put a looping clip on and watch it pass its end; open a camera and look for the row | The panel's row reads *VT name · 1:02 / 3:30 · 2:28 left* with the bar moving, the Run strip's chip *VT 2:28*, the phone's line, the Companion key and the OSC label all show the same seconds and step together every second; at 0:10 the row, the chip and the key go red with *OUT IN 10 … 1*; ⏭ LAST 10 s jumps the picture to its last ten seconds and the clip ends for real (the last frame stays, the row reads *ended*); ⟲ RESTART brings it back from the top; `VIDEO END 30` lands at 3:00; the stinger's clip reads *STINGER CLIP* and its ending runs (the show comes back or the after-policy fires) when the skip reaches the end; the playlist's item reads *PLAYLIST*, the skip moves both the picture and the item's own *left* and the next item comes up when it ends; the loop reads *LOOP 0:03* past its end and never calls an out; the camera shows no row at all; the journal reads *VideoToEnd from desk / tcp / cue* |
