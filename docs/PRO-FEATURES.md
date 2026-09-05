# What the big rigs do, and what Patterns should

An honest look at eight products a show tech meets — vMix, Barco Event Master (E2/S3), Analog
Way (Aquilon/Picturall), QLab, Millumin, Resolume Arena, Disguise, Pixera — against Patterns as it
is after round 11: what each is for, what of it Patterns already does, what was worth building now
(built in this commit), what is realistic next, and what would cost the one thing Patterns is for:
a show display that does not fall over. Written from the products' documented feature sets and
from the desk's experience of them, not from a spec sheet race.

## The eight, in one line each

| Product | What it is | The thing it does that matters here |
| --- | --- | --- |
| **vMix** | A software vision mixer for Windows: inputs, a program/preview switcher, overlays, titles, recording and streaming. | Everything is an *input* with a preview and a program, and every input has a place on a multiview. |
| **Barco Event Master (E2/S3)** | The hardware screen-management system of large events: inputs → layers → destinations (screens, auxes), presets, cues, a multiviewer. | *Destinations with layers*, preset recall with transition times, freeze, a preview that is a real destination, redundancy. |
| **Analog Way (Aquilon, Picturall)** | Hardware presentation switchers and media servers of the same trade: screens, layers, edge blending, warping, presets, a media library. | Rock-solid *live* picture management, warps and blends, fast preset recall, backup units. |
| **QLab** | Mac cue playback for theatre: audio, video, lighting and network cues in a list with GO, follows, fades and targets. | *The cue list as the show*: pre-waits, follows, fade curves, group cues, network cues to other systems. |
| **Millumin** | Mac media server for theatre and installations: a timeline, dashboards, mapping, a board of layers with cues. | A *board* of layers with columns as cues, live mapping, OSC everywhere. |
| **Resolume Arena** | VJ and media-server software: layers of clips, effects, an advanced output mapper (slices, warps, blends, LED maps), Arena's "advanced output". | *Advanced Output*: slices of the composition onto physical outputs with masks, bezels and LED maps. |
| **Disguise** | The stage visualiser and media server of large productions: a 3D model of the stage, feeds, timeline, understudy machines. | *Feeds and understudies*: the physical mapping of content to LED processors, and hot backup machines. |
| **Pixera** | A media server system with a 3D stage, a compositing timeline, projection mapping, and a control layer. | Timeline compositing, mapping onto a modelled stage, a control API. |

## What Patterns already does (and where it is stronger)

Patterns is not a media server for a 3D stage and not a vision mixer for cameras; it is the show
display machine of the corporate and event floor — the walls, the confidence monitors, the lobby
screens, the feeds — with the test patterns to set them up and a desk to run them. Against the
list above it already has:

- **Destinations with layers** (E2, Analog Way): every screen or joined canvas is a content
  target; a pattern, media or web page fills it, two layers sit over it, overlays and a lower
  third on top; screens have roles, locks and mirrors; presets are looks with fades and cuts.
- **Preview as a real destination** (E2's preview): the PGM/PVW panes, EDIT SAFE, a Preview tile on
  the multiview, and REVIEW — the preview full-frame on every multiview.
- **A cue list as the show** (QLab): the cue stack with follows, planned times, confirm, hold, the
  caller's surface, a sheet in and out of Excel, actions on cues (looks, screens, lower thirds,
  sounds), and a journal of every GO.
- **Advanced output** (Resolume): outputs with rotation, trims, warp, edge blend, spans, a
  multiview per output, and — from this round — bezels and gaps so content spans the physical wall.
- **Feeds** (Disguise): NDI senders and the stream as virtual screens with their own look, the same
  engine for every sink, the master clock and the sync check.
- **Control surfaces** (all of them): a TCP protocol, a web remote, Companion, OSC in and out, F-keys.
- **Resilience** the others buy with hardware: a supervisor that restarts a crash or a hang, a
  health line with advice, a super-check, a beacon a backup machine listens for.

Where it is *stronger* than the software peers for its job: it starts in a second, runs on any
Windows machine with a portable exe, has no licence server, and every feature is guarded by a test
that runs on every push.

## Built now — the easy ones that fit

Four things the big rigs have that were cheap, safe and useful:

1. **FREEZE** (E2, Analog Way, Aquilon "freeze"): every output — the windows, the NDI sends, the
   stream — holds the frame it shows until released, while the desk keeps moving. A runtime flag
   on the snapshot bus (like REVIEW), read by the engine at the top of every sink's frame: the
   frame is drawn once onto the sink's own surface and put up unchanged. A blackout still takes a
   frozen output; the fade that runs when the freeze lifts starts from the frame the room saw.
   On the Show panel, the phone remote, Companion (`freeze`), the wire (`FREEZE`), OSC
   (`/patterns/freeze`).
2. **Timed fade to black and fade up** (QLab's fade cues, E2's transition time on a preset):
   `FADE 2` / `FADE UP 2` — a blackout with a fade of its own seconds, using the bus's
   per-publish fade override that cues already had, so a two-second fade needs no change to the
   transition setting. Show panel, phone, Companion (`fade`), OSC (`/patterns/fade`).
3. **Previous look** (E2's "recall previous preset", Analog Way's preset toggle): `LOOKBACK` puts
   the look that was on air before the current one back on air; pressing it again swaps the two.
   The app already knew the look on air; it now keeps the one before. Show panel, phone LOOKS
   tab, Companion (`look_back`), OSC (`/patterns/lookback`).
4. **Earlier versions of the show** (every media server's show-file versioning): every save keeps
   the previous file; the first save that changes something after five quiet minutes keeps the
   file as it was, twenty deep; the Machine page lists them and RESTORE puts one back. Local,
   offline, no network — the roll-back a desk needs at 19:55. (The software's own versions live in
   GitHub: README → Versions and rolling back.)

## Realistic next — worth a round each

Things the big rigs do that Patterns could do well without changing what it is:

- **Group cues and pre-waits** (QLab): a cue that fires several actions with their own delays.
  The stack has follows and actions; a per-action delay is a small model change and a timer.
- **Preset transition per destination** (E2): a look that fades on the main wall and cuts on the
  confidence monitors. The look carries one transition; per-target overrides are a table on the look.
- **Layer key and mask** (E2, Resolume): a luma/chroma key on a layer, a shaped mask. Skia can do
  both in the layer renderer; a key on a camera feed is the common ask.
- **Slices of one canvas onto several outputs with independent scale** (Resolume's advanced
  output): today a span is one canvas at 1:1; a "slice" that scales a region of the canvas onto
  an output would let one 4K canvas feed a 1080p LED processor and a 4K wall at once.
- **Understudy that takes over by itself** (Disguise): the beacon already tells the backup when
  the main is silent; automatic take-over needs a rule the two machines agree on (the main
  releases its outputs when it hears the backup went live) — a protocol, not a button. Worth a
  careful design; a wrong one is worse than a person deciding.
- **Timecode** (LTC/MTC in, as QLab and Pixera read it): a cue that fires at a timecode. NAudio
  can read an audio input; decoding LTC is a known algorithm. A round of work with a hardware test.
- **A stage view** (Disguise, Pixera): a picture of the room with the screens in it, driven from
  the arrangement. Nice for the desk; not a render feature.
- **Recording** (vMix): recording the program to disk through the same libVLC path the stream
  uses. Small, but disk speed on a show machine is a risk to measure first.

## Not now — what would cost the stability

- **A 3D stage with projection mapping onto geometry** (Disguise, Pixera): a different product
  (a GPU scene graph, calibration tooling, a content pipeline), and a different failure surface.
  Patterns' 4-corner warp, blend and gaps cover the flat walls and projector pairs it is for.
- **Camera switching with transitions between inputs, keys and DSKs at broadcast quality** (vMix):
  possible in Skia, but the frame-accurate audio-follow-video, the tally, and the input formats of
  a broadcast mixer are a product in themselves. Patterns switches *looks*, and puts a camera in a
  layer or a PiP.
- **Effects stacks on every layer** (Resolume): a plug-in effect chain per layer invites the one
  thing the supervisor cannot fix — a shader that takes the render thread down mid-show. The
  particle and fractal effects are built in and bounded for that reason.
- **Plug-ins and scripting inside the process**: the same reason. Control comes in over TCP, OSC
  and HTTP, where a bad message is an `ERR`, not a crash.
- **A dependency on a hardware key or a licence server**: a machine that refuses to start at the
  venue because a server is unreachable is the failure Patterns exists to avoid.

## The architecture question, answered

Should anything be pulled out of the core into its own process? See docs/PLAN.md → *12. The
core: keep it, extract at the seams*. Short version: the single process is the reason the show
survives a fault — one snapshot, one clock, one supervisor — and the three candidates worth a
separate process (the stream encoder, the web renderer, video decoding) are already behind their
own seams (an engine source, a frame source, an input mount) with a fake for tests, so each can
move out when a real fault on a real rig says so, without the desk noticing.
