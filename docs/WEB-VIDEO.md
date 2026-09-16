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

The round built 1, 2, 3 and 5 in full with tests, 4 as a flag under the existing decoding
choice, and recorded 6 and 7 as designs with their exact shapes (§4.5, §4.7) rather than shipping
COM and WinRT interop no bench here could run; 8 is declined — the cost in bundle and upkeep is
the wrong trade while 7 is on the table.

## 4. The design, as built

### 4.1 The smoothing buffer (`FrameSmoother`, Core)

A jitter buffer for pictures — but not "arrival plus a constant": the arrivals *are* the jitter, and
a due time of `arrival + latency` would carry every stray straight through. Each frame gets an
**ideal** time from a phase-locked clock: the previous ideal plus the page's cadence, nudged a tenth
of the way to the real arrival (the rate is tracked, the jitter is not); the frame is due at
`ideal + depth × cadence`. The cadence is the least-squares slope of the arrivals over a window of
32 (a burst — a late frame and the catch-up after it — leaves the slope where a running mean of
intervals would swing) and is **locked** to a known rate when within 4 % of one (120, 60, 50, 48,
30, 25, 24, 20, 15, 12, 10 — not the broadcast fractions: the browser composites on vsync, so 29.97
reaches the screencast as 30 with a repeat now and then, and 29.97 in the list only made the lock
flap). A gap of more than 250 ms, or an interval past 200 ms, re-locks the clock on the arrival.

The depth starts from the machine class — small 3, standard 2, big 2 — and follows the p95
**lateness** (`max(0, arrival − predicted)`: an early frame waits, only a late one needs room):
`depth = clamp(ceil(p95 / cadence) + 1, min, max)` within the class's bounds (small 2–5, standard
2–4, big 1–3) and capped by the frame pool's room (`Count − 3`: the frame on show, the one retiring,
the one decoding), growing at once and shrinking one frame at a time after a full quiet window.
`Pick(now)` gives a sink the newest frame whose due time has passed, never one ahead of its time,
never one twice; the older frames due together with it are dropped (the sink was slower than the
cadence) and their slots handed back; nothing due while the frame on show is more than two cadences
past its time is a **stall**, counted once per stall; a full ring on arrival drops the oldest frame,
counted. Auto smooths at 24 fps or more and goes back to the newest frame at once below 20 (a band,
so a 24 fps page does not flap), and only after eight measured intervals — a static page is never
"smoothed". Every sink reads the show clock, so every output shows the same frame in the same slot.
The tests run a jittery 30 fps source against a 60 Hz sink: shown as they arrive the frames land at
1, 3, 2, 2, 3, 1 ticks; smoothed, every one lands two ticks after the one before, the whole way.

**Leaving Program.** When the page stops being wanted the engine keeps the source for the crossfade
as before, and tells it `BeginLeaving(fade)` with the show's transition time: the page's own media
elements (a YouTube embed's player among them — the embed *is* the document) have their volume
ramped to nothing over the fade, the frames keep flowing so the crossfade gets real motion, and at
the end of the fade the buffer is **cut** (every waiting slot released, nothing more taken), the
capture and the poll stop and the browser is muted. Nothing of the page runs on after the take but
the browser the sweep closes a few seconds later. A cut (transition off) leaves at once.

**Low latency.** Frames on the Media page and a web layer: Auto, Smooth, Low latency — per pattern
and per layer (`WebSmoothing`), carried by the look, applied live to the buffer.

### 4.2 Pooled decode (`WebFramePipeline`, Rendering)

The `FramePool` the clips use takes the page's frames: the JPEG is decoded by `SKCodec.GetPixels`
straight into a pooled BGRA buffer, the slot is **queued** (a new slot state — decoded and waiting
for its time; a publish of another frame never drops it, unlike a decoder's skipped frame), and a
sink's draw picks the frame whose time has come, publishes it (a pointer swap) and leases it as it
does a clip's. A starved pool makes room by dropping the oldest waiting frame; a frame whose bytes
repeat the last (a still page the browser painted again) is skipped by a hash before any decode;
the screenshot fallback decodes into the same pool; a size change remakes the pool and drops what
waited at the old size. One decode at a time; a dispose waits for the decode in flight. The report
— smoothing words, depth, latency, jitter, decode ms, delivered and presented fps, stalls, drops,
duplicates, held, pool starvations and bytes — is STATE's `web.path`.

### 4.3 Capture by policy (`WebCapturePolicy`, Core)

Pure: the page's viewport, the largest surface in the rig (not the buses the page is on — a routing
change must never restart the capture), the machine class, the quality ladder's level and the page's
delivered rate → the screencast's `maxWidth` / `maxHeight` (the viewport's aspect fitted inside the
drawn size, never above the viewport; on a small machine at a ladder level below full, never above
1280×720), the JPEG quality (60 small, 70 standard, 80 big), `everyNthFrame` (2 when the ladder is at
Economy or lower and the page delivers 45 fps or more — a 30 fps video's wobble never trips it), and
the smoother's bounds. The plan is applied live: one the running screencast already follows changes
nothing, another restarts it, at most once every three seconds and never while the page is leaving.
The words: "captured at 1280×720 · q60 · every 2nd frame".

### 4.4 The browser's decoder

`--enable-features=D3D11VideoDecoder` joins the browser arguments while the show's decoding choice
is hardware (the same choice the clips read). A request the browser may decline, never a promise;
the Windows bench says whether it changed the decoder.

### 4.5 The page's sound through the mixer (`WebAudioTap`, Platform.Windows) — recorded, not built

The design: `ActivateAudioInterfaceAsync` on `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK` with an
`AUDIOCLIENT_ACTIVATION_PARAMS { AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK, { TargetProcessId =
the browser's, ProcessLoopbackMode = INCLUDE_TARGET_PROCESS_TREE } }` in the `PROPVARIANT` blob, the
`IActivateAudioInterfaceCompletionHandler` answered on an MTA thread, the `IAudioClient` initialised
in shared mode with `LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY` at the mixer's
own 48 kHz stereo float (the virtual device has no mix format of its own), the capture client read
on the event into an `AudioRing` the graph mixes as a clip's tap, the page's own output muted while
tapped; Windows 10 build 20348 or later, anything else or a failed activation keeping the `setSinkId`
route. Not built this round: it is two hundred lines of COM interop with no machine here to hear it
on, and an audio path that has never run is not something to put on a show machine behind a switch.
Meanwhile the native-player path (§4.6) gives a page's video the matrix's sound outright, and the
browser path fades its own sound over the transition (§4.1). Next: the Windows bench.

### 4.6 A YouTube or Vimeo page's video through the native player (`WebVideoResolver`, Core; `WebVideoService`, App) — built

Play via on the Media page and a web layer (`WebPlayVia`: Browser | Native player), carried by the
look. With Native player, a YouTube or Vimeo page (the services that are videos; a dashboard, a deck
or a plain page stays in the browser whatever the look asks) has its stream address found by yt-dlp:
`-g -f "best[height<=1080][ext=mp4][acodec!=none]/best[acodec!=none]/bestvideo[height<=1080]+bestaudio/best"
--no-playlist --no-warnings --no-progress -- <url>` — one argument at a time, the address after
`--`, one file with sound at 1080p or under first, else the best picture and the best sound apart.
The answer (one address per line, the picture then the sound; the expiry read from the address's
`expire=` stamp or assumed an hour) is kept per page, and `MediaLocator.WebResolver` — the desk's
hook on the locator — hands the page to the clip engine as the clip input for the stream **under the
page's own key**, with the sound as the player's `input-slave`, so every engine and renderer sees one
input: libVLC plays it (decoding on the GPU, `network-caching` 1.5 s), the routing matrix carries the
sound, and the browser is not opened for it. The browser stands in until the stream is found and
whenever it cannot be — no tool (the words say where to put it), the site refused (the tool's ERROR
line), a timeout (25 s) — a failure is not asked again for thirty seconds, an address is fetched again
when it lapses, and a mount already playing keeps its address until the page leaves. On a cold take
the browser shows for the second or so the tool takes and the clip then takes over; set the page up in
the preview first and the stream is ready before the take. The tool is looked for at the path the
operator named (Admin `YtDlpPath`, the box under Play via), beside Patterns.exe, then on PATH; it is
never bundled. The words are plain: the native player fetches the stream outside the site's own
player — the site's terms and the content owner's permission for the show are the operator's call;
the site's chapters, adverts and chat are not there; the armed VT (a start point) is the browser's
alone. STATE's `web` row says `via` (browser or native player) and `native` (phase, words, the
stream's host, whether its sound comes apart).

### 4.7 GPU capture (`IWebFrameCapture`) — the seam and the next step, recorded

The Windows.Graphics.Capture path needs the App on a `-windows10.0.19041` target framework (or a
hand-written WinRT ABI), a `CoreWebView2CompositionController` in place of the windowed one with a
`Visual` to capture (`GraphicsCaptureItem.CreateFromVisual`, `Direct3D11CaptureFramePool` at the
compositor's rate, the texture handed to the sinks' GPU context rather than read back), and a bench
to prove the rate, the DRM refusal (a protected video captures black) and the audio route (§4.5 is
its prerequisite, since the composition controller has no `setSinkId` story of its own). The seam
would sit where `WebFrameSource` offers bytes to the pipeline today: a capture that hands textures
publishes into the same pool's slot states with no decode. Recorded, not built blind.

## 5. What followed (68.7)

STATE's `web` row carries `path` (the smoothing words, depth, latency, jitter, decode ms, delivered
and presented fps, stalls, drops, duplicates, held, pool), `via` and `native`; the Eye's source node
and the PAGE CONTROLS line read the buffer's words through the page's status; Companion 3.9.0 reads
the path as `web_path`, `web_smoothing`, `web_latency`, `web_underruns` and `web_capture` and lights
`web_smoothed` and `web_stalled` (no new actions: the choices are the look's); the Media page has
Frames and Play via with the tool's words and its path box; the assistant's catalogue, the web page
help topic, REMOTE.md and COMPANION.md say the same.

## 6. Honest limits

No Windows, no WebView2, no browser and no yt-dlp ran here. The smoother, the policy, the pool's
queued state, the pipeline and the resolver are proven with synthetic frames through the real Skia
decode and with stand-ins for the browser and the tool; the decoder flag, the leaving fade's script,
the screencast restart on a plan change, the network open in libVLC and the tool's process are
compiled and read on the Windows lane, and wait for the bench. The buffer's depth is bounded by the
pool: at 1080p on a small machine (48 MB, five buffers) it is two frames; the 720p capture the policy
chooses there gives eight buffers and the full depth. The leaving fade reaches the media elements of
the page's own document — a video inside a cross-origin frame fades only by the browser's mute at the
end. The cue-ahead pre-roll opens the browser for a native-player page; the stream begins when the
page is wanted on air or in the preview. The numbers in §1 are the platform's documentation and the
round-55 measurements, not this desk's.
