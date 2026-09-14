# Capture to screen — the latency chain, the direct path, and the player question (round 57)

*The brief: "Could latency between capture from HDMI card to output screen be reduced? This would
improve IMAG. (Direct to screen bypassing Windows is already a feature — could this be per screen?)
If it currently uses VLClib, could you create an ultra-flexible, multi-codec, fast, accurate,
custom stable player specifically for Patterns and its architecture (if it is worth it)? This could
also be a linked, 'intelligent' node player… that takes virtually all codecs (or at least as good as
VLC)."* This note is the chain as built, what round 57.3 changed in it, what "per screen" means,
the honest verdict on a player of Patterns' own, and the phased plan. The memory half of the same
story is `docs/MEMORY-RESEARCH.md`.

## 1. The chain, stage by stage

A frame from a camera reaches the glass through eleven hands. Estimates are for 60 Hz sources and
displays; a frame is 16.7 ms.

| Stage | What holds the frame | Typical | Who owns it |
|---|---|---|---|
| The camera and its HDMI out | the sensor's readout and the encoder | 1 frame | the camera |
| The capture card | its own frame buffer; USB cards one to two frames, a PCIe card under one | 1–2 frames | the card |
| The DirectShow driver | the graph's sample queue; MJPEG cards decode a frame first | 0.5–1 frame | the driver |
| libVLC's input | `live-caching` — a fixed buffer before the clock starts, **80 ms** as set | 80 ms | Patterns (an option) |
| libVLC's decoder and output | raw YUY2/NV12 converted to BGRA; the vout's clock (`clock-jitter`, `clock-synchro`) may hold a picture a frame | 0.5–1 frame | libVLC |
| The display callback | into the frame pool — since 57.2 no copy, no allocation | 0 | Patterns |
| The output frame | the picture arrives between vsyncs; the next frame that draws is 0–1 frame away, half on average | 0.5 frame | Patterns (the pacer) |
| Skia and Avalonia | record, flush, present — inside the frame | 0 | Avalonia |
| The compositor | composed: DWM adds a frame; direct output (independent flip): none | 0–1 frame | Windows |
| The display | a monitor's own processing: a gaming monitor a millisecond, a projector one to two frames, a TV 20–80 ms | 1–5 frames | the display |
| The cable and the wall | nothing | 0 | — |

Adding up what Patterns can touch, today on a USB card, composed: 80 (caching) + ~12 (VLC's
output) + 8 (the pacer) + 17 (DWM) ≈ **117 ms of Patterns' and Windows' making**, on top of the
card's and the display's. With direct output and the low-latency capture profile (57.3):
0 + ~8 + 8 + 0 ≈ **16 ms**. With a capture path of Patterns' own that hands the frame to the GPU
(phase 1 and 2 below): ≈ **10 ms**. Past that the card and the display are the latency, and
a PCIe card with a monitor made for it is a two-frame glass-to-glass — as good as IMAG gets on a
computer.

## 2. What round 57.3 changed

**The live age, measured.** Nothing in the app stamped an input frame: the GO clock and the
frame budgets start at a snapshot publish, so they measure the desk to the glass, never a camera
to the glass. A live source now stamps the show clock when a frame is handed over
(`IVideoFrameSource.FrameClock`; `IsLive` says which sources count — a capture device and an NDI
feed; a clip is not live, and a web page or the arcade times its frames in its slot but is not
IMAG), every draw of a live frame notes the oldest one on the frame's stages, and the sink's
budget records the age from that frame's arrival to the end of the frame that drew it. It reads on the glance line (*live 24 ms*), the Machine page's
render line, the super-check's *Live input* row (green under 40 ms, amber to 80, red past), the
CSV (`liveAgeWorstMs`), STATE's machine row (`liveAgeMs`) and Companion (`machine_live_age`). The
words claim what was measured: *the app's share* — from the decoder's hand-over to the frame drawn,
not the card and not the display. SYNC CHECK still measures the whole.

**A low-latency profile per capture device.** Beside the format picker, *Low latency (IMAG)*:
libVLC opens the device with `live-caching=0`, `clock-jitter=0` and `clock-synchro=0` — no input
buffer, no jitter allowance, the picture shown as it is decoded — instead of the 80 ms
confidence-monitoring buffer. The trade is honest: a card that delivers unevenly may stutter
where the buffer smoothed it; IMAG wants the frame now, a slide capture wants it smooth. Stored on
the show per device, so the same card opens the same way on any desk; a change reopens the decoder.

**Direct output per screen — what it already is.** The tick is per screen and always was: each
ticked window gets the desktop's transitions off, no peek, no non-client frame, square corners,
and Windows flips it straight to its display while it covers the display alone. What is per
process is the *kind* of swap chain Avalonia creates (`Win32PlatformOptions.CompositionMode`),
chosen at start; it is harmless for a window that does not cover a display — the desk's own
window composes as it always did. The one thing that is not per screen is the start-time choice,
and that is Avalonia's; making the kind per window means presenting through a swap chain of our
own (§4, phase 2). The Machine page's *Direct output* line and the super-check's row say how many
outputs ask and whether the swap chain is in force from this start; which window Windows actually
flips is PresentMon's to say (§7).

## 3. libVLC — what it gives and what it costs

libVLC gives every container and codec, hardware decode (D3D11VA), subtitles, DVDs, network
streams and a stable API, for nothing. What it costs Patterns:

- **A readback.** Hardware decode lands on the GPU; the `vmem` output downloads every frame to
  system memory as BGRA for the callback (since 57.2 straight into the pool, but still a download:
  a 4K60 clip is two gigabytes a second across the bus and back up as a Skia texture).
- **Its own clock.** VLC times pictures on its clock, not the show's; the pacer draws what is
  there when the slot comes.
- **Fixed buffers on live input.** `live-caching` is a number, not a policy; the low-latency
  profile is as far as the options go.
- **Seeks to a time, not a frame.** A VT cued to a mark lands on the nearest decodable picture;
  frame-stepping and an exact first frame need the decoder to be told.
- **No timestamps out.** The callback gets pixels, not the PTS; the live age above is measured
  from the hand-over, not the capture instant.

## 4. A player of Patterns' own — the honest verdict

**Not as a replacement; as the second engine.** "As good as VLC at everything" is VLC's twenty
years — the long tail of broken files, odd containers, discs and streams is where the effort goes,
and it buys IMAG nothing. What buys IMAG something is an engine that owns the frame from the
capture instant to the GPU texture the sink samples, on the show clock, with the PTS in hand — and
that engine is worth building for exactly the sources libVLC serves badly: capture cards, NDI is
already ours, frame-accurate VT. libVLC stays as the universal fallback that plays anything.

**The seam is already there.** Every source on the `InputBus` is an `IVideoFrameSource`; every
engine-owned one an `IMountedSource`; `VideoEngine.Reconcile` mounts what `MediaLocator` wants
and retires what it does not. A second engine is a second implementation of those two interfaces,
chosen by source kind (capture → the Patterns engine when the machine can; a file → libVLC unless
the show asks for frame-accurate playback), with the pool, the fence, the live age, the routing
matrix's audio tap and the pre-roll contract unchanged. Nothing above the bus knows which engine
drew.

**What it is made of.**

| Part | Choice | Why |
|---|---|---|
| Capture | Media Foundation `IMFSourceReader` (WMF) with the D3D manager, NV12 out | the lowest-latency path Windows offers a capture card; no graph, no buffer policy but ours; DirectShow-only cards stay on libVLC |
| Demux and decode for files | FFmpeg (libavformat, libavcodec) through a thin native shim, D3D11VA for H.264/H.265/VP9/AV1, software for ProRes/DNxHD | the codec set a show uses, with the PTS and frame-exact seeks (decode from the keyframe to the frame) |
| Colour | NV12 → RGB on the GPU (a small shader, or the video processor) | no CPU conversion, no readback |
| Frames | the frame pool for CPU frames; a texture pool with the same fence for GPU frames | one lifecycle, one ledger line |
| Clock | the show clock is master: a frame is shown at its PTS on the show clock, dropped or repeated against it | the twin, the followers and the audio lock already ride that clock |
| Audio | decoded PCM into the routing matrix's ring, as the tap does today | the matrix already takes it |
| Presentation | phase 2: a D3D11 texture into Avalonia's composition (`CompositionDrawingSurface` with a shared texture), later a swap chain of our own per direct output | the GPU frame never leaves the GPU |

**What it costs.** Six to ten weeks for the formats a show uses (H.264/H.265/ProRes/DNxHD/VP9/AV1
in MP4/MOV/MKV/MXF, image sequences, AAC/PCM/MP3/FLAC audio), the hardware paths (one D3D11VA
path covers NVIDIA, AMD and Intel), the pre-roll and hold contracts, the audio lanes, and a soak
per format. Two weeks for the Media Foundation capture alone — the piece IMAG is waiting for.

## 5. The intelligent node player

The same engine as a node (`--node player`, beside the caller, the timer and the arcade): it
mounts the sources the desk assigns over the follower link, joins the show clock (round 49's
offset measurement) so its frames land on the main's frame, plays under the paper stack's cues,
sends NDI or drives a display of its own, and reports its live age and its pool's health on the
Nodes page like any node. Standalone, it is a media player with the same wire, so Companion drives
it directly and a cue on another desk can too. *Intelligent* means what the desk's engine already
means: it pre-rolls what the running order says is next, chooses the decode path by the machine's
class and the machine profile, tells the desk what it cannot play before the cue rather than at
it, and holds the first frame on a GO's mark. That is a profile and a page over the engine, not a
product of its own — the platform charter (§40) says nodes are the kernel with a role.

## 6. The plan, in phases

| Phase | Builds | Proves | Rough size |
|---|---|---|---|
| 0 (this round) | the live age; the low-latency capture profile; the pool with no copy; the direct-output words | headless: the age arithmetic, the options, the words, the pool | done |
| 1 | Media Foundation capture into the pool (CPU frames), the live age from the sample's own timestamp | on a Windows desk with a card: the age against SYNC CHECK; PresentMon for the flip | 2 weeks |
| 2 | GPU frames: NV12 textures shared into Avalonia's composition; a texture pool on the fence | the same, and the CPU and bus cost gone from the Machine page | 3 weeks |
| 3 | the Patterns engine for files over FFmpeg: PTS, frame-exact seeks and steps, the show clock as master, D3D11VA | the VT tests on real files; the pre-roll contract | 4–6 weeks |
| 4 | the node player | the follower link's tests, a node's soak | 2 weeks |
| later | a swap chain of our own per direct output (the kind per screen) | PresentMon: Independent Flip per window | 2 weeks |

## 7. How to measure in the field

- **SYNC CHECK** for the whole chain: every sink flashes on the master's two-second grid; a phone
  filming the source and the wall at 240 fps reads the gap.
- **The live age** for Patterns' share: the glance line and the CSV; the difference from SYNC
  CHECK is the card and the display.
- **PresentMon** for the flip (`docs/CHECKLIST-round10.md`): *Hardware: Independent Flip* on a
  direct output; *Composed: Flip* while a window sits on it.
- **The profile's trade:** the render line's *slots missed* and the frame budget's p95 with the
  low-latency profile on and off — a card that stutters at 0 ms of caching says so there.

## 8. Honest limits

No card, no libVLC, no GPU and no Windows ran here: the live age is proven with a source of the
tests' own through the real pipeline; the profile's options are proven as strings against libVLC's
documented ones; the direct-output words are proven pure. The stage estimates in §1 are from the
platform's documentation and the round-10 measurements, not from a card on this desk. Phase 1
onwards is Windows work with hardware on the bench.
