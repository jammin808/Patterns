# Round 14 — Windows checklist

The headless suite proves the logic on every push. These are the things only a real rig shows:
one line per item, what to do, and what you should see. Tick them on a Windows machine with the
full build (libVLC bundled), two displays, an audio interface and, where named, a phone on the
network and a Stream Deck with Companion. The low-spec laptop the report came from is the right
machine for rows 1 and 2.

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | The stinger and VOG lifecycle — a clip that cannot be stopped, no more | SETUP → Audio: two video stingers (A and B) and a VOG sound; OUTPUTS ON on a look; fire A from the Show panel and let it end; fire A again and, while it plays, fire A a third time from the phone; fire A and press ■ Stop from the Audio page mid-clip; fire A, and while it plays pull the USB stick the clip is on (or rename the file) and wait twenty seconds; with EDIT SAFE off fire A, open EDIT SAFE mid-clip, let it end, close EDIT SAFE, fire A again; fire A, and while it plays fire the VOG sound and then B; type `STINGER STOP` in the console with nothing playing; open `patterns.log` and the journal | A ends and the look comes back every time; the third press restarts A from the top on the same decoder (the picture jumps to its start, no black); ■ Stop mid-clip puts the look back at once and the status reads *Ready.*; the pulled clip reads *Clip stalled — previous content back* (or *could not play*) within twenty seconds and the look is back, with a *Failed* line in the journal; after the EDIT SAFE dance the second fire of A plays from the top rather than fading straight back; the VOG ducks A and B replaces it, and after B the look is back, never a dead A; `STINGER STOP` with nothing up answers *Ready.*; the log has no *Stinger tick failed* line, and if it ever has one the clip still ends and the look still comes back |
