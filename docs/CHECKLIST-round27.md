# Round 27 — Windows checklist

The headless suite proves the rules: the card's numbers are the numbers it claims, a look's three
states reach the wire, and a MIDI message becomes the line an operator sees. These are the things
only a real rig shows — a wall at native resolution, a Stream Deck, and a controller out of a
flight case.

## The maker's line

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | It changed | Open a settings file written by an older build (or just start the app after upgrading) | The badge under PATTERNS reads **rig · playback · show control**. Before this round it would have kept the old words for ever, because the file carries them |
| 2 | A line you typed is yours | Put a venue's address on the line (BUILD → Branding → PATTERNS BADGE), restart | Your words are still there |
| 3 | And they stay yours | Type the OLD words in on purpose — "Show display · test cards · playback" — and restart twice | They stay. The migration runs once, at schema 10, not on every load |
| 4 | A saved look keeps what it saved | Recall a look saved before the change | That look's badge shows the words it was saved with. Re-save it to bring it up to date |

## The Patterns test card

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 5 | A new install opens on it | Run the app in a folder with no settings file | The Rig card, with the screen's own name on it |
| 6 | Which screen is this | Put it on every screen of a rig | Each names itself, large enough to read from the floor, with its pixel size under it |
| 7 | The edges are all there | Look at all four edges on a real wall | Both the outer border and the one three pixels in, on all four sides. Missing = overscan |
| 8 | And how much is missing | Count the corner ticks | Ten pixels a tick, longer at fifty and a hundred. This is the overscan as a number |
| 9 | One to one | Look at the five small fields from two metres | Three even textures of the same brightness. If they crawl, moiré or shimmer, something in the chain is scaling — and which of the three breaks says whether it is horizontal, vertical or both |
| 10 | Prove it the other way | Turn on the processor's scaler, or send 1080p to a 4K wall | They break. That is the test working |
| 11 | The circle is round | Photograph it square-on and measure | Round. Oval = a pixel-aspect or stretch fault |
| 12 | Black is black | Look at the four patches on the black ground | 2, 4, 6 and 8 all visible. Any you cannot see is a code value the chain threw away — and the number says which |
| 13 | White is white | The four on the white ground | 253, 251, 249 and 247 all visible |
| 14 | Gamma | Step back until the line field blurs, then read which solid vanishes into it | 1.9, 2.2 or 2.6 — that is this display's gamma. Only true at native size with no sharpening |
| 15 | It says when it cannot measure | Put the card on a multiview tile, or set a 1920 canvas on a 4K output | It turns orange and says SCALED — 1:1 AND GAMMA READ ONLY AT NATIVE SIZE. This is the one that matters: before this it looked identical either way |
| 16 | Three densities | Switch between Rig, Pixel and Levels | Pixel is readable from the back of the hall; Levels is what you photograph for the LED supplier |
| 17 | Every shape | A 32:9 joined canvas, a portrait screen, a 96-pixel monitor-wall tile | Nothing falls off the side, nothing is blank |
| 18 | It costs nothing | Leave it on eight screens for an hour | Machine → STABILITY: no change in the worst frame. Nothing on the card moves |
| 19 | One mark, not two | Look at the badge setting | The badge overlay is off on this kind and cannot be turned on for it — the card carries the mark itself |

## Three states for a look (needs a Stream Deck)

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 20 | Update the module | Companion → the Patterns connection → module 2.8.0; re-drag the **Look bank** and *Looks — this show* keys | Three states on one key with no feedback stacking by hand |
| 21 | Green | Recall a look | The key is green |
| 22 | Amber — the one that did not exist | Now change the picture (any editor, EDIT SAFE off) | **The key turns amber.** Before this round it stayed solid green over a picture nobody had checked |
| 23 | And back | Recall the look again | Green |
| 24 | Which screen | Give one screen its own picture after the look went up | `look_screens_off` lights, and **Screen n back to the program** goes amber for that screen alone |
| 25 | A look's own instruction does not cry wolf | Save a look in which a confidence screen has its own picture, recall it | Nothing is amber. That screen is doing as it was told |
| 26 | A locked screen never counts | Lock a screen, recall a look | Not amber, ever. Keeping its picture through a recall is what LOCK means |
| 27 | What each screen is drawing | Drag **Screen n — the picture it is showing** | It prints the kind, and goes blue for a screen on its own picture |
| 28 | The facts that were already there | Drag the stream, EDIT SAFE, outputs and timing keys | The stream key goes red live and amber in trouble; the timing key says how late the day is |
| 29 | Two old bugs | Look at the Audio track keys 7 and 8, and any two-line key | They show track names, not `$(patterns:track_7)`; and the labels break onto two lines instead of showing a literal `\n` |

## A MIDI control surface

Bring a controller. An APC40 mkII if you have one; a nanoKONTROL2 is the useful worst case.

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 30 | It is offered | SETUP → Interactive → **+ MIDI CONTROL SURFACE** | A card with a port picker listing this machine's MIDI inputs |
| 31 | Learn | Press **LEARN**, then press a pad | The row fills in with that pad's own line (NOTE 1 53 *). Type `CUE GO` beside it |
| 32 | It fires once | Press the pad | One GO. Press and hold, then release — still one. A release is its own word, so a pad cannot fire twice |
| 33 | A fader reaches both ends | Learn a fader onto `AUDIO LEVEL *`, sweep it | 0 at the bottom, **100 at the top**, smooth between. Before this a raw fader would have died about four fifths of the way up |
| 34 | A sweep does not stall the desk | Hold a fader and sweep it up and down for ten seconds while the show runs | The picture never stutters. Machine → STABILITY: the desk tick stays inside its budget |
| 35 | And does not flood the journal | Look at the journal after that sweep | About one row a second with the value the fader reached — not fifty a second |
| 36 | Lamps | Add `BLACKOUT 1 → LAMP 1 82 5` and `BLACKOUT 0 → LAMP 1 82 0`, press blackout | The pad lights and goes out. On an APC40 the number after the pad is a colour in its own palette — try a few |
| 37 | A ring of light | Add `VOL * → CC 1 48 %` on a surface with LED rings, move the audio level at the desk | The ring follows the show |
| 38 | No fader fight | On a motorised surface, hold a fader while the show changes that level | It does not pull against you, and lands on the show's value the moment you let go |
| 39 | Starter rows | Pick a controller under **Starter rows**, press **+ STARTER ROWS** | Its published numbers land in your table where you can read and edit them. The status line says they have not been run against hardware |
| 40 | Unplug and back | Pull the USB mid-show, plug it back in | It reopens by itself within a couple of seconds **and the lamps come back** — that was the bug: it used to come back dark |
| 41 | Another application has it | Open the surface in Ableton or the vendor's utility first, then start Patterns | The card says the port would not open and that another application may be holding it. Close the other one — it opens on the next retry |
| 42 | The fence | With "remotes may not arm" set, learn a pad onto `CUE ARM ON` and press it | Refused, exactly as TCP, OSC and Companion are. A surface is a device and the rule already covered it |
| 43 | The journal names it | Run anything from the surface, read the journal | "from device Surface" |
| 44 | The limits are true | Read the card's second tip, then try them | Endless encoders do behave oddly (they send nudges); a nanoKONTROL2 does light nothing until its own editor is used; nothing answers back to the surface |
