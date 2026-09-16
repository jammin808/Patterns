# Web video in Patterns — the research and the design (round 68)

The request: YouTube and web video should be allowed a small but sensible buffer based on the
host machine, faded and cut when the page is taken off Program; the browser's video is poor on
lower-spec machines and WebView2 is CPU-heavy; how do OBS, vMix and the others play web video
smoothly; investigate every route to smooth, perfect playback. This paper is the reading, the
numbers where they could be had, the options ranked, and the design the round builds — with the
honest line between what is proven here (no Windows, no browser on this desk) and what waits
for the bench.

## 1. What Patterns does today, and where the cost is

A web page is one WebView2 browser per address (`WebFrameSource`), hosted in a window kept off
every screen, published on the input bus like an NDI receiver. The picture reaches the engine
through Chromium's own screencast: `Page.startScreencast` (round 55) makes the browser hand over
every frame its compositor draws, JPEG at quality 70 at the page's full viewport, as a
`Page.screencastFrame` DevTools event with the picture inside as base64 text; each frame is acked
on arrival, decoded on a worker, and published into a `FrameSlot` the sinks draw. A screenshot
poll stands in when a browser will not screencast (round 58 judges the stream's liveness). The
page's sound leaves by the browser's own output — steered to a Windows output through the page's
media elements (`setSinkId`) where the site allows — never through the desk's mixer.

Per 1080p frame at 30 frames a second, the chain costs:

| Stage | Where | Cost, roughly | Why it hurts on a small machine |
|---|---|---|---|
| Video decode in the browser | the browser's GPU process | hardware where Chromium allows; WebView2 is reported to pick `VDAVideoDecoder` where Chrome picks `D3D11VideoDecoder`, at about 1.5× the GPU cost and with stutter under desk activity ([WebView2Feedback #3751](https://github.com/MicrosoftEdge/WebView2Feedback/issues/3751), open) | the decode competes with the desk's own render for the same GPU and, on an iGPU, the same power budget |
| Compositor readback and JPEG encode | the browser's renderer | 4–8 ms per frame on a mid CPU | a whole core for one page |
| The event's text | the browser → our process, on the desk's UI thread | ~350 KB of base64 per frame becomes a ~700 KB .NET string; ~20 MB/s of large-object garbage at 30 fps | GC pauses on the thread that runs the desk |
| JPEG decode | one worker thread | 6–12 ms per 1080p frame (libjpeg-turbo through Skia) into a fresh 8 MB bitmap; the previous frame retires through the render fence | a second core, and 240 MB/s of native churn |
| Upload | every sink that draws the page | a raster image of 8 MB uploaded per sink per frame | three outputs, the preview and the multiview upload the same pixels five times |
| Presentation | each sink's vsync | the newest frame, whenever it arrived | the screencast's cadence is uneven (the encoder, the IPC, the decode), so a 30 fps video arrives 25–35 ms apart and the sink shows some frames twice and skips others: judder |

The last row is the one the operator sees as "poor": not a low rate, an uneven one. The others
are why a small machine cannot afford the page at all beside the show.

## 2. How the others do it

- **OBS** (Browser Source, `obs-browser`): the Chromium Embedded Framework rendered off-screen.
  On Windows with hardware acceleration the browser paints into a **shared D3D11 texture** and
  hands its handle over (`OnAcceleratedPaint`); OBS wraps the handle and draws it — no readback,
  no encode, no copy ([obs-browser PR #310](https://github.com/obsproject/obs-browser/pull/310),
  [obs-studio PR #3933](https://github.com/obsproject/obs-studio/pull/3933) for the macOS twin;
  [obsproject/cef PR #1](https://github.com/obsproject/cef/pull/1) on the per-frame request).
  Without acceleration, `OnPaint` hands a BGRA buffer that is copied to a texture. The page's
  audio comes through CEF's audio handler into OBS's mixer ("control audio via OBS"), so it fades,
  routes and meters like any source. The source has its own fps setting; the browser is asked for
  a frame per output frame.
- **vMix** (Web Browser input): CEF too, HTML5 video and audio, WebRTC. Its display modes are the
  answer to the request's buffer: **Smooth** "makes every effort to ensure video playback is as
  smooth as possible on the display, though it may add a couple of frames of delay", **Low
  Latency** shows a frame the moment it arrives; vMix picks Low Latency under 30 fps and Smooth
  otherwise ([vMix Performance](https://www.vmix.com/help28/Performance.html),
  [Web Browser](https://www.vmix.com/help29/WebBrowser.html)). Two frames of delay for a steady
  cadence is exactly the trade a room prefers.
- **A YouTube link as a clip**: OBS operators put a YouTube video through a Media Source by
  resolving the stream with `yt-dlp` — the video is then a file the media engine decodes with the
  card, at the cost of the site's player (no adverts, no chapters, no live chat) and of the site's
  terms, which the operator answers for. This is the cheapest and smoothest path there is, and it
  is not a browser at all.
- **WebView2's own off-screen path**: the `CoreWebView2CompositionController` draws into a
  Windows composition visual; `GraphicsCaptureItem.CreateFromVisual` and a
  `Direct3D11CaptureFramePool` hand over **GPU textures at the compositor's rate** with no encode
  ([the WPF composition control's spec](https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/WPF_WebView2CompositionControl.md),
  [a worked C++ example](https://gist.github.com/pabloko/5b5bfb71ac52d20dfad714c666a0c428)).
  Microsoft's own words on the cost: "there may be lower framerates compared to the standard
  WebView2 control" (the capture is a screen capture of the browser's processes) and "DRM videos
  will be unable to play"; the rate is the display's vsync, not the page's; the sound needs a
  route of its own.
- **The page's sound as a process**: Windows 10 build 20348 onwards can loopback-capture the
  render streams of one process and its children (`ActivateAudioInterfaceAsync` with
  `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`)
  — the browser's whole process family as one source, into the desk's mixer, whatever the site
  allows its player ([NAudio #878](https://github.com/naudio/NAudio/issues/878), Microsoft's
  application-loopback sample).
- **Chromium's screencast** is built for DevTools, not for a wall: the frames are JPEG, the rate
  is whatever the encoder and the consumer manage (the field reports ~30 fps as a practical ceiling
  and asks for a rate option that does not exist —
  [devtools-protocol #63](https://github.com/ChromeDevTools/devtools-protocol/issues/63)), and
  `everyNthFrame` is the only throttle.

The pattern across the field: **the browser decodes, the host never re-encodes.** A shared
texture (CEF, WebView2 composition) or nothing. Patterns' screencast is the one path in the list
that encodes and decodes every frame, and it does so because it is the only frame path the
windowed WebView2 offers without WinRT.

## 3. The options, ranked

| # | Option | What it buys | What it costs | Provable here |
|---|---|---|---|---|
| 1 | **A smoothing buffer** — timed frames presented on the show clock, two to four deep, sized by the machine and the measured jitter (vMix's Smooth) | judder gone; every output shows the same frame in the same slot; a fade-and-cut when the page leaves Program | 60–130 ms of delay on the page's video, which a room never notices on a web clip | yes — pure maths, tested |
| 2 | **Pooled decode** — JPEG decoded straight into fixed BGRA buffers behind the render fence, no allocation per frame | the 8 MB churn and its GC/allocator time gone; the frames the buffer holds cost nothing more | a pool of 5–8 buffers per page (40–65 MB at 1080p) | yes — Skia decodes here |
| 3 | **Capture by policy** — the screencast asked for the size the page is drawn at, never larger; JPEG quality and `everyNthFrame` by the machine class and the quality ladder | on a small machine a 1080p page drawn on a 720p screen costs 720p; a 60 fps page on Economy costs 30 | the picture is the drawn size (it was scaled to it anyway) | yes — pure, tested |
| 4 | **Chrome's decoder** — `--enable-features=D3D11VideoDecoder` for the browser, under the show's hardware-decoding choice | the reported 1.5× decode cost and the stutter | none when it works; a black video on a driver that cannot, which the safe run (software decoding) turns off | no — a Windows bench |
| 5 | **A YouTube link as a clip** — `yt-dlp` resolves the stream, libVLC plays it with the card | the smoothest path, no browser, the routing matrix's audio, pre-roll | the site's player and terms; `yt-dlp` on the machine; muxed formats stop at 720p (1080p is DASH, two streams) | the resolver's lines and rule, yes; playback, no |
| 6 | **The page's sound through the mixer** — process loopback of the browser | fade, route, meter, duck like a clip; the fade-and-cut the request asks for | Windows 10 21H2+; ~200 lines of COM interop with no bench here | no — compiled only |
| 7 | **GPU capture** — composition controller + Windows.Graphics.Capture | no encode, no decode, textures at the compositor's rate | WinRT in the App (a `-windows10.0` target or hand-written ABI), DRM video refused, the rate is vsync; the audio path of #6 is required | no — a Windows bench, and the App's target framework |
| 8 | **CEF beside WebView2** (CefSharp.OffScreen) | OBS's exact path: shared textures, an audio handler | ~200 MB of Chromium in the bundle, a second browser to keep current, a second set of flags | no |

The round builds 1, 2 and 3 in full with tests; 4 as a switch under the existing decoding choice;
5 as an optional path with the honest words; 6 as a compiled, off-by-default path behind a seam
that fails closed to today's; 7 as the seam and the recorded next step; 8 is declined — the cost
in bundle and upkeep is the wrong trade while 7 is on the table.

## 4. The design

### 4.1 The smoothing buffer (`FrameSmoother`, Core)

A jitter buffer for pictures, the same idea as an audio ring's latency: a frame arrives at
`arrival` on the show clock and is due at `arrival + latency`, where `latency = depth × cadence`.
`cadence` is estimated from the arrivals (an exponential mean of the intervals, clamped to a
sane range); `depth` starts from the machine class — small 3, standard 2, big 2 frames — and
adapts to the measured jitter (the p95 of `|interval − cadence|`): `depth = clamp(ceil(jitter /
cadence) + 1, min, max)` with the class's bounds (small 2–5, standard 2–4, big 1–3). A sink
asks `Pick(now)` and gets the newest frame whose due time has passed, never one ahead of its
time, never one twice; nothing due is an **underrun** (the last frame stays up, counted) and a
full ring on arrival is an **overrun** (the oldest waiting frame is dropped, counted). Every sink
reads the same clock, so every output shows the same frame in the same slot — the outputs of one
canvas cannot drift a frame apart, which the newest-frame slot let happen.

**Leaving Program.** When the page stops being wanted the engine keeps the source briefly for the
crossfade (as it does today). The smoother is told `Drain()`: it takes no new frames, keeps
presenting what it holds at cadence through the fade, and `Cut()` at the end clears the ring —
the buffered frames are shown, not leaked, and nothing of the page survives the cut. The sound
follows: with the page's own output, a volume ramp over the page's media elements then mute; with
the mixer tap (§4.5) the lane's own fade.

**Low latency.** A page whose picture is the point rather than its motion — a dashboard, a
scoreboard, a clock — can be set to Low latency (no buffer, the newest frame at once), the way
the capture devices have their IMAG profile; Auto picks Smooth when the page's cadence is 24 fps
or more and Low latency below, vMix's rule. Per pattern and per layer (`WebSmoothing`), carried
by the look and the cue.

### 4.2 Pooled decode (`WebFramePipeline`, Rendering)

The `FramePool` the clips use (round 58: fixed BGRA buffers behind the render fence, no
allocation per frame) takes the page's frames: the JPEG is decoded by `SKCodec` straight into a
pooled buffer, the slot is **queued** (a new slot state — a frame decoded and waiting for its due
time; publishing one queued frame no longer drops the ones queued behind it, as it does a
decoder's skipped frames), and `Pick` publishes it when its time comes: a pointer swap, the
sinks lease the latest as they do a clip's. The screenshot fallback decodes into the same pool.
The retire, the fence, the memory ledger and the census see one more pool per page and nothing
new.

### 4.3 Capture by policy (`WebCapturePolicy`, Core)

Pure: the machine class and cores, the quality ladder's level, the page's viewport and the
largest size any sink draws it at, the page's measured cadence → the screencast's `maxWidth` /
`maxHeight` (never above the drawn size; on a small machine at a ladder level below full, never
above 1280×720), the JPEG quality (60 small, 70 standard, 80 big), `everyNthFrame` (2 when the
ladder is at Economy and the page delivers more than 30 fps), the smoother's bounds, and the
default smoothing. The page lays itself out at its own viewport as before — the site still sees
1920×1080 and chooses its 1080p stream — only the picture handed over is the size it is drawn
at. The status line says what was asked: "captured at 1280×720 · q60 · smooth 3 (100 ms)".

### 4.4 The browser's decoder

`--enable-features=D3D11VideoDecoder` joins the browser arguments while the show's decoding
choice is hardware (the same choice the clips read: software in the run after a native fault
turns it off). The one `--enable-features` list is merged, since Chromium reads the last.

### 4.5 The page's sound through the mixer (`WebAudioTap`, Platform.Windows) — compiled, off by default

`ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK)` with the browser process's
id and `INCLUDE_TARGET_PROCESS_TREE`, initialised in shared mode with `LOOPBACK | EVENTCALLBACK |
AUTOCONVERTPCM` at the mixer's own 48 kHz stereo float (the virtual device has no mix format of
its own), read on a thread into an `AudioRing` the graph mixes as a clip's tap; the page's own
output muted while tapped. Windows 10 build 20348 or later; anything else, or a failure to
activate, keeps today's `setSinkId` route and says so. A switch on the Audio page, off until the
bench has heard it.

### 4.6 A YouTube link as a clip (`WebVideoResolver`, Core + App)

With `yt-dlp` on the machine (found on the path or named on the Media page) a YouTube or Vimeo
address can be played as a clip: `yt-dlp -g -f "best[height<=1080][ext=mp4]/best" --no-playlist`
resolves a direct stream URL, cached per address for the show, and the media engine mounts it as
a video file — the card decodes, the matrix carries the sound, pre-roll and the armed start point
apply as to any clip. The words are honest: the site's player, chapters, adverts and live chat are
not there; muxed formats stop at 720p; the operator answers for the site's terms. Off by default;
a per-item choice (`WebPlayVia`: Browser | Clip when available).

### 4.7 GPU capture (`IWebFrameCapture`) — the seam and the next step

The capture is peeled out of the source behind a seam with the screencast as its one
implementation. The Windows.Graphics.Capture path needs the App on a `-windows10.0.19041`
target framework (or hand-written WinRT ABI), a composition controller in place of the windowed
one, and a bench to prove the rate, the DRM refusal and the audio route (§4.5 is its
prerequisite). Recorded, not built blind.

## 5. What follows

STATE's `web` row carries the path (screencast, screenshot, clip), the buffer's depth and
latency, the jitter, the decode cost, the frames delivered and presented, underruns and drops;
the Eye's source node says the same in words; Companion 3.9.0 reads them as variables and a
Smooth / Low latency action; the Media page's PAGE CONTROLS show the path and the buffer with the
switch; the assistant's brief reads the web facts; REMOTE.md, COMPANION.md and the help say it.

## 6. Honest limits

No Windows, no WebView2 and no browser ran here. The smoother, the policy, the pool's queued
state and the pipeline are proven with synthetic frames through the real Skia decode; the
decoder flag, the process-loopback tap and the clip path are compiled and read on the Windows
lane, and wait for the bench. The numbers in §1 are the platform's documentation and the
round-55 measurements, not this desk's.
