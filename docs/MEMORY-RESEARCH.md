# Memory in Patterns — the research and the design (round 57)

*The brief: "Can memory be handled more efficiently? Do full research, and be creative and novel if
you can do something brilliant and customised for Patterns."* This note is the research, the design
it led to, what round 57.2 built of it, and what is left. The code's own words are in
`MemoryBudget`, `FramePool`, `RenderFence`, `MemoryLedger` and `ImageCache`; PLAN §75.2 lists the
contracts and REVIEW round 57 the honest limits.

## 1. The answer, first

Yes — in three ways that were open, and one that was not.

1. **Bytes, not counts.** Every cache was bounded by a count: ten pictures, four decoders, twelve
   held frames. Ten 8K photographs are not ten icons. Now the pictures have a byte budget the
   machine's class sets, the least recently drawn go first past it, and the graveyard behind it is
   bounded too.
2. **No allocation in a steady second of video.** Every decoded frame — file, capture card, NDI —
   was its own native allocation, filled, wrapped, drawn for a frame or two and freed a fade
   later: at 1080p60 that is roughly half a gigabyte of allocator traffic a second per source, and
   a source's memory a function of its rate. Now a source owns four to eight buffers sized once
   and cycles them behind a render fence; libVLC decodes straight into them (no copy at all), an
   NDI frame is copied once (the SDK owns its buffer). A source's memory is a fixed number of
   frames whatever it plays.
3. **The memory placed, not just measured.** The Machine page said one number (the working set)
   against a ceiling. Now a ledger says where the memory is by owner — pictures, frame pools, the
   frames held for fades, the decks' pages, the managed heap — beside the private bytes and the
   heap, on the page, in STATE, in the CSV and in the assistant's brief. A climb the ledger cannot
   place is a leak in what it does not see, which is the sentence an operator needs.
4. **Not the collector.** The two GC settings the app carries are the right two (§4). The one
   change made there is Skia's GPU resource cache, sized to the machine instead of one number for
   every machine.

## 2. What a game engine does, and what carries over

A game engine's memory is a set of budgets in bytes per system — textures, meshes, audio, the
streaming pool — decided before the first frame from the platform's class; arenas and pools are
sized once and recycled, never allocated in the frame; a frame's resources stay alive until the
GPU has consumed the command list that references them (a fence per frame in flight); streaming
keeps the resident set under the budget by evicting what is least recently needed; a memory screen
shows every pool's fill; and a pressure ladder (drop mip levels, shrink the streaming window) runs
before the platform's allocator says no.

For an AV desk the systems are pictures, live frames, decks, the effects' rasters, the GPU cache
and the managed heap; the frames in flight are the sinks' frames with a deferred GPU read at
flush; the platform classes are the machines a show lands on (a laptop under 8 GB, a desk-class
machine, a media server past 32 GB). Everything above maps, and the fence is the part that had no
equivalent here: `RetiredFrames` held every replaced frame for 400 ms because *nothing knew when a
draw was over*. The fence knows.

## 3. What Patterns had

- `MemoryBudget`: the app's ceiling (a quarter of the machine, 512 MB to 3 GB), counts for the
  rest; "it measures and says; it does not evict".
- `ImageCache`: ten pictures, LRU by a linear scan, a graveyard bounded by time (5 s) alone.
- `VideoEngine.OnDisplay` / `NdiReceiver.PublishFrame`: `new SKBitmap` + row copy + `SetImmutable`
  + `SKImage.FromBitmap` per frame; the previous frame into `RetiredFrames` (400 ms, twelve at
  most across every source).
- `MetricSample.RamAppMB` = `Process.WorkingSet64`: the one memory number, inflated by `RetainVM`
  (segments kept between collections, by design) and by mapped images (the 88 MB ReadyToRun exe,
  ICU, libVLC). Private bytes and the managed heap were never sampled.
- `runtimeconfig`: `System.GC.Concurrent = true`, `System.GC.RetainVM = true`; sustained low
  latency while the outputs are live. No Skia GPU cache limit set; no `GRContext` in reach.
- `MetricsHistory`: lists shifted down by one each second (600 records moved every tick).
- The machine's size read two ways (`GC.GetGCMemoryInfo` for the warm-up and the ladder,
  `GlobalMemoryStatus` for the ceiling).

## 4. The .NET 10 collector — every knob considered

| Knob | Decision | Why |
|---|---|---|
| Workstation, concurrent (`GC.Concurrent`) | keep | background collections, no stop-the-world gen 2 on air; server GC would multiply heaps by cores for a desk that allocates little |
| DATAS (dynamic adaptation) | n/a | server GC only |
| `RetainVM` | keep | the show's working set is not released and re-committed between collections — a commit stall mid-show is worse than a larger number; the ceiling's words say the number includes it |
| `SustainedLowLatency` on air | keep | `ShowGc` sets it when the outputs open, back to interactive off air |
| `GCConserveMemory` | no | it fights LOH fragmentation; the app's large buffers are native (pictures, frames), the LOH is small |
| `GCHeapHardLimit` / `HardLimitPercent` | no | an OutOfMemory at a limit is the worst failure a show can have; a soft budget with eviction is the right shape, and the native pixels are outside the GC's limit anyway |
| `GC.AddMemoryPressure` for native pixels | no | every pixel buffer is disposed explicitly (pools, caches, graveyards); pressure would only hurry finalizers nothing relies on |
| `TieredPGO`, ReadyToRun | as they are | on by default; the exe is R2R for the start-up time |
| Trimming, NativeAOT | closed (PLAN §18) | WebView2/NAudio COM interop, the SDK's reflection |

What was worth changing outside the collector: **the numbers read**. `PrivateMemorySize64` (what
the process holds that nothing else shares — the number that climbs in a leak), `GC.GetTotalMemory`
(the managed heap: objects, never pictures) and the heap's committed bytes now ride the sample,
the CSV (`privateMB`, `managedMB`) and the Machine page; the working set stays the ceiling's
number because it is what the operating system pages, and the words say what each is.

## 5. Skia, Avalonia and the GPU

- **A raster image over external pixels.** `SKImage.FromPixels(info, pointer, rowBytes)` wraps a
  buffer without copying: the image reads the memory. That is the frame pool's whole trick — one
  image per buffer, made once — and its whole hazard: the buffer must not be written while a draw
  can still read the image.
- **When a draw reads.** A CPU canvas reads at `DrawImage`; Avalonia's GPU-backed lease canvas
  records the draw and uploads the pixels when the frame flushes, at the end of the compositor's
  render pass. So "the draw is over" means "that sink started its next frame". `RenderFence`
  is exactly that: every `RenderPipeline` registers, advances at each frame's start, and a buffer
  retired at a mark is free once every sink that drew from its pool advanced past it — or half a
  second passed, and a sink that has not started a frame in two seconds is asleep, not mid-frame.
  *That drew from its pool* matters: a source's draw notes the pool on the sink whose frame is
  running (`FramePool.Touch`, a thread-static sink id set by the frame's start), so a preview that
  drew a static page once and stopped holds nothing of a camera's pool. Without it every desk click
  that redrew a preview would have held every pool for half a second — at 60 fps that is thirty
  frames, more than the pool has, and the pool would have starved to sixteen pooled frames a second
  for two seconds after each click. A sink that registered and never started a frame holds nothing
  either.
- **Skia's GPU resource cache.** Avalonia owns the `GRContext` and sets one cache limit for every
  machine; `SkiaOptions.MaxGpuResourceSizeBytes` is now sized by class (64 / 128 / 256 MB). The
  app cannot read the cache's fill through the lease today; the ledger names it as a budget, not
  a reading.
- **What the ledger does not see.** Avalonia's own render surfaces, ANGLE's device memory, the
  browser's process (out of process anyway), libVLC's decoder pictures, the NDI SDK's buffers.
  The private-bytes number holds all of it; the ledger's total is what the app placed; the gap is
  the rest, and a gap that grows is where to look.

## 6. libVLC and NDI at the buffer

libVLC's `vmem` output asks for a picture buffer (`lock`), fills it, says it finished (`unlock`)
and says which picture to show (`display`), keeping two or three pictures ahead of the display. The
pool hands `lock` a free buffer and returns the slot as the picture's id; `unlock` marks it decoded;
`display` publishes it; a picture decoded but never shown (a late frame VLC skipped) is dropped when
a later one is shown. That is why a pool has at least four buffers: one on show, one being written,
one or two under the fence or ahead in VLC's hands. When every buffer is spoken for, `lock` hands
VLC the scratch buffer and that one frame goes the old way — copied into an image of its own — and
`FramePools.Starved` counts it. The NDI SDK owns its frame until `recv_free_video`, so an NDI frame
is copied once into a pooled buffer; the same starvation path stands behind it.

## 7. The design as built (round 57.2)

| Piece | What it is | Bounds |
|---|---|---|
| `MachineClass` | small under 8 GB, standard to 32, big past it | one reading (`MemoryBudget.MachineMB`, the GC's view, taken at the start) |
| `ImageCache` | pictures bounded in bytes, LRU, the graveyard bounded by time and by half the budget | 128 / 256 / 512 MB; 32 at most |
| `FramePool` | a source's frames in fixed buffers, images made once, decoded into, cycled behind the fence | 48 / 64 / 96 MB a source: eight 1080p frames, four at 4K |
| `RenderFence` | per-sink frame starts; a pool's mark clears when every live sink that drew from it advanced, or after 500 ms | 256 seats, the longest-idle reused; a pool's table of who drew it |
| `MemoryLedger` | owners' bytes with a detail, largest first | pictures, frame pools, frames held, deck pages, managed heap (+ committed) |
| the sample | private bytes, managed heap, committed | CSV `privateMB`, `managedMB` |
| Skia GPU cache | sized by class at start | 64 / 128 / 256 MB |
| `MetricsHistory` | rings, not shifted lists | 600 + 2,880 records as before |

Per source at 1080p60, before: a 7.9 MB allocation and free sixty times a second and up to
twelve retired frames across the desk (≈100 MB of pixels in flight at the worst, churning).
After: 64 MB fixed, nothing allocated while it plays, and the starved count on the page if the
pool ever was. The steady frame's own allocation stayed at nought (the round-56 fence stands).

## 8. How to read it in the field

- The Machine page's MEMORY CEILINGS line: the ceiling words, then *placed:* the owners. The
  `AdminMemText` line beside it: the working set, the private bytes, the managed heap.
- The CSV: `ramAppMB` (working set), `privateMB`, `managedMB` every thirty seconds. A soak
  (`docs/SOAK.md`) reads private bytes flat within ten per cent; a slope in `privateMB` with the
  ledger's owners flat is the runtime's or a driver's; a slope in `managedMB` is an object leak;
  a slope in an owner is that owner's.
- STATE's `memory{appMB,ceilingMB,privateMB,managedMB,pictureMB,framePoolMB,placed,text}` for a
  deck's key or the assistant.

## 9. What is next, in order

1. **A pressure ladder.** Past 90 % of the ceiling, evict in order what is cheap to make again:
   the graveyard, the pictures beyond the ones on air, the decks' pages beyond the window, the
   thumbnails; the same shape as the quality ladder, with the same words. The ledger makes it
   possible; the eviction hooks are the next round's.
2. **Thumbnails and web frames in the ledger and the pools.** The Library's tiles are Avalonia
   bitmaps (count them by pixels); the web page's JPEG frames could decode into pooled pixels
   through `SKCodec.GetPixels`; the arcade's ring could publish its buffers wrapped rather than
   copied.
3. **Frames that never leave the GPU.** A hardware decoder writing NV12 into a D3D11 texture the
   sink samples directly is the memory *and* latency answer for video and capture — the player
   research (`docs/PLAYER-RESEARCH.md`) has the path; it needs a device the app owns or a share
   with Avalonia's.
4. **The GPU cache read back.** Reaching the `GRContext` through Avalonia's Skia lease would let
   the ledger read Skia's fill instead of naming its limit.

## 10. Honest limits

Everything here was proven headless on Linux with the raster backend: the fence, the pools, the
cache, the ledger, the rings. libVLC's `lock`/`unlock`/`display` path into the pool is written to
libVLC's contract and not run here; the NDI path is exercised through the same `PublishInto` the
receiver uses, with pixels of the test's own. The fence's fallback is time (500 ms), the same
assumption the 400 ms hold made before; a sink that takes longer than that over one frame is a
hung sink. A pool is held only by the sinks that told it they drew it — the two decoders and any
source written the same way; a source that hands a pooled image to a canvas without `Touch` is
covered by the fallback alone. The ledger sees what registers.

## 11. Round 69 — residency: a reason for everything held, and a clock for what has none

*The brief: "When something isn't needed or running it can be discarded from memory unless it is
set up and adjusted like a marker on a YouTube video, or pre-loading media for the next cue … it
needs to dispose of old redundant memory better."*

**What was already true.** The engines never kept a source nobody wanted: a decoder or a browser
page leaves the moment its picture does (kept a few hundred milliseconds for the crossfade, then
disposed behind the render fence), a receiver the same, a deck drops the pages outside its window,
and the standby cue's clips and pages are opened ahead and let go four seconds after the cue stops
being next. What the desk lacked was two things. First, the picture cache kept a decoded still
until the byte budget or the count was passed: on a big machine a 33 MB photograph shown once at
09:00 was still resident at 17:00 with nothing drawing it — not a leak, but a drawer full of things
nobody had a reason for. Second, nothing said *why* a thing was in memory: STATE listed the mounts
and the bytes, not the holds.

**The residency policy (`Residency`, Core).** Every held thing has one of seven reasons: on air (a
frame drew it within the last second and a half), named by the show (a pattern, a layer, the logo or
the standby cue's look references it — kept decoded whether or not a frame drew it this second, so
the picture is there the moment it is asked for), in the preview, armed at its mark (a web page whose
video the operator set up — the one thing kept past its want), pre-rolled for the standby cue,
retiring, or idle. An idle thing gets a grace by the machine's class — 20 s small, 60 s standard,
180 s big — halved at the ladder's elevated rung, a quarter at high, none at critical, then goes on
its own clock, not only once the budget is passed. `Residency.Grace`, `ForBuses`, `ForPicture`,
`NamedPictures` and `Summary` are pure and tested.

**The sweep (`ImageCache.SweepIdle`, Rendering).** Once a second the residency service reads every
resident picture's idle age from the cache's tick clock, lets the ones past the grace go — behind the
fence, like any other picture, never one drawn within the window and never one the show names — and
counts what went (`IdleSwept`). The ladder's `TrimTo` at elevated stands beside it: the sweep is the
clock, the trim the emergency.

**The armed page (`WebEngine.KeepArmed`).** A page whose video the operator armed in the preview
used to be retired the moment the preview moved on — the browser, the mark and the advert skipped all
gone, to be done again. Now the page stays mounted, off air, until it is disarmed or plays; the memory
pressure ladder clears the keep at critical, and the page leaves like any other. A look's own arm
(*Play the video from*) is not kept this way: the look brings it back.

**The ledger (`ResidencyService`, App).** The rows — pictures, clips, pages, receivers, decks, each
with its reason, its bytes and its idle age — are gathered once a second after the pressure ladder and
read by STATE (`memory.residency`: the grace, the words, what was let go, every hold), the Media page's
line under the live inputs (*In memory: 7 held (412 MB): 3 on air, 1 pre-rolled for the standby cue, 1
named by the show, 2 idle — the first lets go in 12 s · idle pictures let go after 60 s on this
machine*), the Eye's source nodes (*held: pre-rolled for the standby cue*) and the assistant's brief.

**Balance between graphics memory and system memory — as it really is.** The split is by design, not
by a swapper: decoded frames and pictures live in system memory (the pools, the cache) and the GPU
holds only what Skia uploads to draw a frame, in its resource cache. Nothing moves between them at run
time because nothing would gain: a texture is an upload of pixels the pool already has. What the round
adds on the GPU side is §12's governor — the cache bounded by the card's dedicated memory and shrunk
under either pressure — and the honest numbers beside it.
