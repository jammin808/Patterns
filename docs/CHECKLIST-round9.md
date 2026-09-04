# Round 9 — Windows checklist

The headless suite proves the logic on every push. These are the things only a real rig shows:
one line per item, what to do, and what you should see. Tick them on a Windows machine with the
full build, two displays, an audio interface and, where named, a capture card, an NDI receiver
and a Spotify Premium account.

| # | Item | Do | Expect |
| --- | --- | --- | --- |
| 1 | Stop fade | Fire a sound stinger, press STOP (■) after a second | The sound fades out over the Stop fade (default 200 ms); no click, no cut |
| 2 | No bleed-back | Fire a sound stinger, STOP it, fire another within a second | Only the new sound is heard; the stopped one never re-sounds |
| 3 | Stopped clip | Fire a video stinger, STOP it mid-clip | The picture dissolves back; the clip's sound is silent within the dissolve |
| 4 | VOG over a sting | Fire a long sound stinger, then a VOG sound | The stinger ducks under the VOG and returns when the VOG ends; nothing stops |
| 5 | VOG over a sting clip | Fire a video stinger, then a VOG sound | The clip keeps playing, its sound ducks and returns; the label keeps the stinger |
| 6 | Live DUCK | Play the track and Spotify, fire a stinger, press D | Everything but a VOG drops to the live-duck level and ramps back on D again; STOP ALL leaves it |
| 7 | Ticker | Scroll a long message for a minute | The train never jumps; a Fade background darkens toward the anchored edge only |
| 8 | Edge blend | Two projectors overlapped on the Screens page, Automatic ticked, Projection blend pattern with the grey check | The overlap reads flat grey at the tuned gamma; spans and NDI show no fade |
| 9 | PiP crop | Crop the PiP inset from each side on an NDI feed and a capture | The inset keeps the cropped shape; nothing stretches |
| 10 | Master rate | Master 30 fps, outputs on, Machine page open | Outputs report 30 fps while the desktop stays 60 Hz; no stutter, even spacing |
| 11 | Screen override | One screen at 60 in a 30 fps show | That screen draws at 60, the rest at 30; a span stays in step |
| 12 | NDI master | An NDI sender set to "master" | The receiver reports the master rate; change the master and it follows |
| 13 | Stream follows | Stream with "Follow the show's master rate" ticked | The encode restarts at the master rate; the status line says so |
| 14 | Display mode | Pick a lower mode on the selected screen, Apply, wait 15 s | The display changes and reverts by itself; KEEP within 15 s makes it stick; placements, canvas names, tiles and looks survive the rename |
| 15 | Capture format | A capture card with several modes, Format picker on the Media page | The card's modes list; the chosen one opens (the picture changes size or rate); Device default leaves the driver alone |
| 16 | Spotify browse | CONNECT, Refresh my playlists, BROWSE SONGS | The songs list within a second or two; a chosen song becomes a button named "Artist · Song" |
| 17 | Spotify search | Search for an artist | Songs, albums, playlists and artists list; ADD makes a button; a free account can do this but is refused on play |
| 18 | Music on a look | A look with a Music entry, put ON AIR | The picture lands, then Spotify starts the entry; → PVW leaves the music alone |
| 19 | Library | Images / Videos / Audio chips, a search, ✕ on a media tile | The chips file the tiles; two files of one name show two thumbnails; the file stays on disk |
| 20 | Particle coverage | Snow with wind 40 on a full-screen output for a minute | No bare side; particles enter from the upwind edge as well as the top |
| 21 | Packs | Every chip of every pack | Each scene renders smoothly; Starcloth stays put; Fireworks bursts from the centre |
| 22 | Fractal on the GPU | Fractal pattern on a 4K output, Machine page open | The output holds the master rate; changing the family or zoom never stalls |
| 23 | Fractal on NDI | The same fractal on an NDI sender | The receiver shows the same picture at a softer resolution, in step with the output |
| 24 | Internal sound | Fractal with "This computer's sound", Spotify playing on this machine | The picture pulses with the music; the SOUND line reads "Listening to this computer's sound." |
| 25 | External sound | Fractal with an input, a microphone or an interface channel | The picture follows the input; the line names the input |
| 26 | Effect pulse | An Explosion pulse fired over Fireworks, then a Rush over a fractal, then a Flash from a cue beside a look | A burst / a dive / a white hit, each settling back within its length; nothing owns the screens; the label and the music are untouched |
| 27 | Pulse on a span | The same pulse on a two-output span and an NDI sender | Seam-identical; every sink surges on the same frame |
