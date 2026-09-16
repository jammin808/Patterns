# Audio routing — what vMix, QLab, Ableton Live and Aquilon do, and what Patterns takes from them

Round 55's field note. The ask: a video or a web page on an info screen with its own soundtrack over
that screen's HDMI (or an NDI send), while the main outputs show a presentation and the room hears
the audio desk through a sound card; an operator choosing which soundtrack goes where; a VOG able to
take over any channel. Patterns is not trying to be any of the four products below, but each has
spent years on the same question and their answers are worth reading before writing a line.
Facts as read on 14 September 2026; sources at the end.

## 1. vMix (Windows; help v29)

- **Buses.** Eight stereo mixes: Master (M) and A–G. Master feeds recording, streaming and the
  external output; A–G go to the device and channel pair chosen per bus in *Settings → Audio
  Outputs*. Headphones is a separate output — Master, except while Solo is on. A sound card's
  pairs are picked as 1+2, 3+4, 5+6; a mono output gets an automatic mono downmix.
- **Per-input assignment.** M / A / B buttons under every fader (C–G on right-click); each input has a
  bus mixer (level and mute per bus); the input's own mute takes it off every bus. The *Audio Bus
  Manager* is one inputs × buses grid of click-to-toggle squares.
- **Channel matrix.** A 16 × 16 router per input (source channels → buses), so three embedded
  language pairs go to Master, A and B from one input.
- **Audio Follow Video (AFV).** A per-input "Automatically Mix Audio" arrow mutes and unmutes the input
  as it enters and leaves the output; a global default in Settings → Audio.
- **Solo.** Headphones only, non-destructive: AFL by default, PFL per input, multi-solo on right-click.
- **Mix-minus.** Built from buses: a call returns Master, Headphones or a bus, always minus the
  guest's own microphone.
- **Delay.** Per-input delay in ms; a global default; recording and external output accept a
  negative delay (about −80 ms at 30p when looping through a desk). No plugin delay compensation —
  "vMix is live".
- **NDI / SRT.** Outputs 1–4 each choose which bus (or channel set — Master 2, MA 4, MAB 6 … up to 16
  channels) rides that NDI or SRT output; Master, Headphones, A and B can be published as audio-only
  NDI streams.
- **UI.** The Audio Mixer docks at the right of the main window (pin to detach, minimise to re-dock):
  an outputs strip (Master, A, B, the headphone knob) then one strip per input with L/R meters and a
  cog to its settings; a title collapses on right-click.
- **Clock and latency.** One master sample rate (44.1 or 48 kHz) for every mix; *Audio Gap Handling*
  — Drop (insert silence; the default) or Resample (stretch) for a drifting source; ASIO buffers up to
  20 ms; audio delayed to video "usually under 80 ms"; per-output latency read under Master's "i".

## 2. QLab 5 (macOS)

- **Chain.** File channels (rows, up to 24) → the cue's matrix → cue outputs (columns) → the patch's
  matrix (cue outputs → device outputs) → the device. Every stage has a level in dBFS and the levels
  add along the path (−3 in, +2 at the crosspoint = −1). 1–128 cue outputs per patch, up to 128
  device outputs.
- **The Levels tab.** The main level top-left, output levels along row 0, input levels down column 0,
  crosspoints inside. Blank = −∞; dragging stops at 0 dB, typing allows up to the workspace maximum
  (+12 by default); an unsigned number reads as negative. *Set Default Levels*, *Set all silent*,
  *Gangs* (type the same word into several fields and they move together), a *Trim* tab for
  post-fader offsets immune to fades.
- **Audio Output Patch.** A name, a cue-output count, a device (Core Audio, Dante, AVB, NDI, or the
  system output); cue outputs and device outputs each with mute and solo; effects and a sample delay
  per device output. A missing device keeps its name and raises "Audio output device missing".
- **Mic cues.** Live inputs through separate input patches; cues on one patch stay sample-locked,
  across devices they drift unless word-clocked (measured 0–3 ms over two hours).
- **Fades.** A fade cue targets a cue or (5.x) a whole patch; absolute or relative; curves; "stop
  target when done".
- **Ducking.** None automatic. The standard recipe: a Group — a Fade (music −20 dB relative in
  0.5 s) with the announcement — then an auto-follow Fade restoring over about five seconds; the fade
  can target a whole cue list or a patch, so everything on those outputs ducks.

## 3. Ableton Live 12

- **I/O section.** *Audio From* / *Audio To* per track: the type (Ext. In / Ext. Out, Main, Sends
  Only, another track, Resampling) and the channel; channels come from Audio Settings → Channel
  Configuration, where each mono channel and stereo pair is enabled and named. A mono output sums
  L+R at −6 dB. A track target exposes Track In, a device's sidechain input, Pre FX / Post FX / Post
  Mixer taps.
- **Sends and returns.** A send knob per track to each Return track (up to twelve, A–L), Pre or Post
  fader; a *Pre* return is an independent aux that "can be routed to a separate output" — a monitor
  mix; *Sends Only* takes a track off Main.
- **Cue.** The Main track has *Main Out* and *Cue Out* choosers (different outputs, four channels or
  more); the Solo/Cue switch turns Solo into a headphone cue; the browser's preview goes to Cue Out.
- **Delay.** Track Delay in ms per track (for "human, acoustic, hardware" compensation, not to be
  moved on stage — it clicks); automatic plugin delay compensation; External Audio Effect and
  Instrument carry a *Hardware Latency* setting.
- **Ducking.** The Compressor's Sidechain: any internal routing point as the key, with gain, dry/wet,
  a sidechain EQ, a listen button, Attack and Release (or Auto), Lookahead 0 / 1 / 10 ms. The manual's
  own example is the compressor on the music keyed from the narration.
- **Engine.** 32-bit float; clips only at the physical outputs or on export.

## 4. Analog Way LivePremier / Aquilon

- Audio is de-embedded on every input and re-embedded on every output; Dante 64 × 64 at 48 kHz
  (32 × 32 at 96 kHz), redundant ports, AES67; HDMI carries up to four channels per input.
- **The model.** *Receivers* (input channels, Dante in) → *transmitters* (outputs, multiviewer
  outputs, Dante out). Allowed: inputs → outputs and multiviewers, inputs → Dante, Dante → outputs
  and multiviewers, Dante → Dante. The Web RCS Audio → Routing page shows receivers on the left,
  transmitters on the right; dragging a receiver onto a transmitter is the default routing (all
  channels mapped); *Advanced Routing Mode* maps single channels. Per transmitter: mute, a test tone,
  delete the route, the number of channels to send; per receiver: mute, highlight the transmitters
  using it.
- **Control.** REST: per output channel `source = none | input | dante` with the source and its
  channel; mute per channel or all. A Q-SYS plugin with simple (input → output) and advanced
  (channel) routing.
- **Absent from the manual.** No level or gain, no per-output delay, no audio-follows-video (the
  routing is static per output, whatever the screen's layers show), no prelisten beyond the test
  tone and a VU meter per multiviewer output.

## 5. What Patterns takes from them

1. **Two stages, not one.** A source → destination crosspoint matrix (QLab's Levels, vMix's Bus
   Manager) in front of a destination → device binding (QLab's patches, vMix's Audio Outputs). The
   operator routes to a *destination* — "the room's desk", "the info screen's HDMI", "NDI 1", "the
   monitor" — and the device behind it is a setting, so a show file travels to a desk with a
   different card.
2. **dB discipline.** 0 = unity, blank = off, a drag stops at 0, a typed number goes to +12, the
   floor is −60 → −∞; levels add along the path; *set all silent* and *set default* for bulk work.
3. **Audio follows video as the default.** A picture's sound goes where that picture goes (vMix's
   auto-mix, Patterns' programme rule) unless the operator says otherwise per destination; a
   destination with nothing said follows the programme.
4. **VOG as a priority ducker per destination.** Sidechain semantics (Ableton) — an attack of tens of
   milliseconds, a release of hundreds — over QLab's "−20 dB in 0.5 s, back over 5 s" recipe; plus a
   *replace* mode (everything else off under the VOG) and a *leave* mode (the VOG never reaches that
   destination — the stream stays clean). Each destination chooses.
5. **A delay per destination in ms** for lip-sync over HDMI and NDI (QLab's device-output delay,
   vMix's per-input delay), never re-timed on air.
6. **A monitor bus apart from the programme** (Ableton's Cue Out, vMix's Headphones), with the
   programme never taken away to make room for it — Patterns' own round 26 rule.
7. **Hot-plug by name.** A destination bound to a device by its name keeps the show running with a
   "device missing" warning (QLab), and "the computer's output" is a pseudo-device.
8. **One clock.** One engine rate (48 kHz), files resampled on the way in, drift handled explicitly
   (vMix's drop or resample; Patterns' own sample-rate lock to the master clock), about 20 ms buffers,
   the latency per output shown.
9. **Multi-channel envelopes are a later step.** vMix's MA / MAB channel sets on SDI, NDI and SRT
   show where a stereo-only matrix goes next; Patterns' NDI audio starts stereo.
10. **Aquilon's static per-output selection is the floor** — it lacks level, delay and AFV, exactly
    the gaps a software desk fills.

## 6. What the round built from it

- `AudioRouting` in Core: sources (the programme's sound, each screen's own picture, the preview,
  the music, VOGs, stingers, the tone) × destinations (each Windows output by name, each NDI send,
  the monitor), crosspoints in dB, a trim, a delay and a VOG mode per destination, resolved to a
  plan of linear gains with the duck's attack and release as an exponential approach — pure and
  tested. Off, the desk behaves exactly as before (the two wires of round 26).
- The App applies the plan where Windows and the decoders allow: the playlist, VOGs, stingers and the
  tone open on the destinations routed for them with the crosspoint's gain and the destination's
  delay; a clip's soundtrack goes through the decoder's own device when one destination wants it and
  through a mixer lane when several do (the App's audio graph), NDI sends carry embedded audio, and a
  web page's sound is steered to a device through the page's own output picker where the browser
  allows it — with what could not be routed said on the Audio page rather than assumed.

## 7. Round 69 — the sound follows the picture

The gap the matrix left: a crosspoint is a fact about a source and a destination, and a screen's picture
is not a fact — it moves on every take. A show where the main wall and its repeaters carry a video while
the info screens run a playlist of their own, and the next cue puts the video everywhere, needs rows
re-ticked on the cue, or an operator who never lets the info screens change. vMix answers it with *audio
follows video* per input (an input's sound is live while the input is in the output), QLab with a patch
per cue, a broadcast desk with a follow on the bus. Patterns has a fact the others lack: every screen
knows what it shows — the programme, a picture of its own, the target it repeats, the canvas it belongs
to (`ContentTargets`, `ScreenRoles.ResolveMirror`) — so the route can be derived rather than stored.

What the round built: `ScreenPlacement.AudioOutput` names the output a screen's sound leaves by, once.
`AudioRouting.SourceOfScreen` gives the source its picture makes now; `FollowedRoutes` one derived route
per enabled screen with an output; `EffectiveRoutes` the rows first and the derived routes where no row
names the crosspoint (a row wins, on or off — the operator's word); `Resolve` plans over the rows'
destinations and the outputs the picture alone routes to (no row: trim 0, delay 0, a VOG ducks at the
show's level), each lane carrying `Followed`; `FollowSignature` lets the audio graph rebuild its lanes on
a take and not on a quiet tick (the rig's `Output` joined the graph's dirty domains for the same reason);
`AudioRoutingConfig.FollowPicture` (on by default) turns the derivation off. The verbs: `SCREEN n AUDIO
<output>|OFF` (desk-only — the rig's wiring; naming an output makes it a row and switches the matrix on),
`AUDIO FOLLOW ON|OFF|TOGGLE` (a cue may carry it), both over OSC. The desk: Sound out on the Screens page,
a Sound out drawer in every tile's menu, a FOLLOW switch and "follows the picture" cells on the Audio
page's matrix, the Eye's followed edges, STATE's rows, Companion 3.10.0.

What it does not do: a followed destination's own trim, delay and VOG mode need a row (naming an output
makes one); a web page in the browser is still steered through its own output picker to the first device
its picture is routed to (round 55's limit) — the native-player path follows fully, as any clip does; a
screen inside a joined canvas without a picture of its own follows the programme, as the canvas does.

## Sources

vMix: help29/Mixer.html, AudioOutputs.html, AudioBusManager.html, AudioSettings.html, Audio.html,
OutputAudioChannels.html, ExternalOutput.html, vMixCallAudio.html, help28/NDINetworkDeviceInterface1.html,
SRT.html, knowledgebase article 211 (audio delay), help24/vMixUserGuide.pdf, forum threads 22729, 23946, 24222.
QLab 5: docs/v5/audio/introduction-to-audio, audio-cues, audio-output-patch-editor, mic-cues,
fading-audio, fundamentals/workspace-settings, general/new-in-qlab-5, the QLab cookbook and users' group.
Ableton Live 12: the manual's routing-and-i-o, mixing, live-audio-effect-reference and first-steps chapters.
Analog Way: the LivePremier user manual v4.2 §14.1 (pp. 98–99), the Aquilon C+ product page, the
Dante product listing, the REST driver notes and the Q-SYS plugin help file.
