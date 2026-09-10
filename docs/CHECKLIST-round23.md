# Round 23 — Windows checklist

The headless suite proves the rules: what a page is treated as, what CLEAN takes off it, and how a
stream's health reads in every state. These are the things only a real rig, a real browser and a
real RTMP endpoint show. Tick them on a Windows machine with the full build, WebView2, two
displays and a test destination (a local nginx-rtmp, or a private YouTube stream key).

## The web page

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | A YouTube link goes on with nothing round it | Remote & web → paste a YouTube watch link, leave *Treat it as* on Auto, tick **CLEAN**, press SHOW THIS PAGE ON THE PATTERN | The video fills the frame with no black margin and no scrollbar. Move the mouse over the PREVIEW pane: **no media bar appears**, no title, no share buttons, no channel watermark. Pause it: no "more videos" panel, no end-screen cards. Let it run to the end: no related-video grid |
| 2 | And it is still yours to drive | With that page on air, use PAGE CONTROLS: PLAY / PAUSE, MUTE, RESTART, +10 s, −10 s | Every one works, with no furniture drawn at any point — the player is driven through its own API, not by pressing its buttons. The same from a cue, the phone's PAGE ON AIR block and a Stream Deck |
| 3 | The tick comes off live | Untick CLEAN with the page on air | The furniture comes back within a frame and the page does **not** reload — the video keeps playing from where it was |
| 4 | An address that does not say | Take a YouTube embed served through a corporate proxy or a short link (or fake one by putting a `youtube-nocookie.com/embed/…` address behind a redirect). Leave *Treat it as* on Auto, then set it to **YouTube** | On Auto the line says nothing and PAGE CONTROLS offers the plain keys. On YouTube the line names it, FULL FRAME appears, PLAY / RESTART / ±10 s appear, and CLEAN takes the media bar off |
| 5 | A dashboard nobody wrote for a wall | Put an internal dashboard on the pattern and tick CLEAN | The page loses its margin, its scrollbar and its pointer, and nothing else — no guessing at the site's own furniture |
| 6 | It travels | Save a look with a clean YouTube page on it, recall it from an F-key, send it to one screen, reopen the show | The service, the CLEAN tick and the address all come back; the page opens clean straight away rather than flashing its furniture first |
| 7 | It reaches everything | With the clean page on the program, look at an NDI receiver, the stream and a wall tile | The same clean picture on all of them — the strip is in the browser, so every sink gets it |

## The stream

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 8 | The rail's foot tells the truth from any page | With no destination set, go to BUILD → Particles and watch the foot of the rail. Then tick a destination and press START on the Stream page | Off: a grey dot and OFF. Asked for with nowhere to send it: red and NO DEST. Started: amber UP… then green LIVE with the uptime counting under it — all without leaving the Particles page. Click the foot: the Stream page opens |
| 9 | SLOW is visible before anyone complains | With the stream live, load the machine hard (a fractal at Fine on three outputs, a 4K clip, the multiview) until the encoder falls behind | The rail's foot and the Stream page turn amber and read **SLOW**, with "N of M fps reaching the encoder" and what to lower. The wall itself still looks perfect — that is the whole point of the light |
| 10 | A fault says what happened | With the stream live, kill the encoder process from Task Manager | The desk restarts it within seconds and the light reads LIVE* with "the encoder has restarted 1 time this session". Kill it repeatedly until the desk stands down: red, FAULT, and the reason |
| 11 | Press it where you are | Open the Show panel's SHOW CONTROLS drawer mid-show and press STREAM START, then STOP | The stream comes up and goes down without opening the Stream page, and the line beside the buttons carries the same word and colour as the rail |
| 12 | The phone sees it too | Open the web remote on a phone, SHOW tab | STREAM ON / STREAM OFF work, and the line under them carries the word, the uptime and the health sentence — in the same colours. The SETUP tab's stream line still reads as before |
| 13 | A look carries it | Put *Stream: START* on the "Doors open" look and *Stream: STOP* on the "Walk-out" look; give the first an F-key. Press the F-key, then → PVW the same look | The F-key puts the look up **and** starts the stream. → PVW puts the look in the preview and leaves the stream alone — nothing that has not gone to air reaches the internet. The same look in a clicker step and in an install's schedule does the same |
| 14 | A cue still names it | Add a cue action *Start stream* with no destination ticked | The cue reads Broken with "no enabled stream destination (Stream tab)" — the check that was always there. Tick a destination and it clears |
| 15 | A restart does not start it behind you | With the stream live, restart the desk from the Machine page | The show comes back on the wall; the stream does **not** start by itself, and the status strip says the stream was live and to press STREAM |
| 16 | Nothing costs a frame | With the stream live, two displays and the multiview up, sit for ten minutes | Machine → STABILITY shows no change in the worst frame; the health is read once a second on the desk's own poll and nowhere else |
