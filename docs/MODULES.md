# The modules

*Round 59. The one assembly that held the show, the renderer, the transports, the audio maths
and the assistant is nine — ten from round 65.12, when what Windows says about the machine became
an assembly of its own. This is the map, the rules between them, the seams that let a rule hold,
and what the split found. The tests enforce it; this says why.*

## 0. The verdict, first

A folder is a promise; an assembly is a build error. Before round 59 `Patterns.Core` was one
assembly with SkiaSharp in it, and its `Services` folder reached the render folder as freely as
the render folder reached it — a review had to keep the seam, and every round the seam moved a
little. Now the show core links the runtime and nothing else, each edge links its own native
library and the core, and only the App names the UI. The compiler keeps the seam, a test reads
the compiled references and the source to prove it, and the runtime says which modules a
process actually loaded.

The split is worth what it enforces, and three things it makes possible: a pure core that any
role and any test host runs with no native library beside it (the core suite runs in four
seconds with `Patterns.Core.dll` alone in its folder); an edge that can be tested through its
contracts alone, with no desk, no UI thread and no headless platform (a Companion driven through
a fake wire, a room run for the phones, the audio providers over the mix format); and edges
whose contracts are narrow enough to be hosted in a child process of the supervisor when a real
fault says so — the plan the README has carried since round 12, which cannot start until the
edge is out of the assembly.

## 1. The map

```text
Patterns.Core        the show: the model, the snapshot bus, the clock, the cues, the looks, the
                     actions and the wire's vocabulary, the budgets and the health rules, the
                     geometry and colour of its own, the contracts every edge asks of a process,
                     the dispatch seam, the module map            → the runtime only
Patterns.Rendering   everything that paints: the engine, the sinks, the generators, the effects,
                     the particles, the frame pools, the picture cache, the render fence, the
                     retired frames, the video seam, the lower-third renderer, the rig-day games,
                     the fonts                                     → Core, SkiaSharp
Patterns.Ndi         the NDI runtime's interop, the receiver, the senders   → Core, Rendering
Patterns.Arcade      the arcade engine, the games, the audience's board    → Core, Rendering
Patterns.Devices     the device service and its serial, TCP, UDP and HTTP links with the
                     profiles' drivers (PJLink, Pixera, Companion…), the OSC port, the DNS-SD
                     responder, the beacon, the bounded line reader, the MIDI surface link
                                                                   → Core, System.IO.Ports, NAudio (MIDI)
Patterns.Audio       the fan-out ring, the sync maths, the gain, delay, asynchronous-rate, tap
                     and tee providers, the tone, the WASAPI stinger voice, the mix format, the
                     outputs' rules                                → Core, NAudio
Patterns.Assistant   the key store, the scope, the facts, the brief, the proposal and its parser,
                     the apply step, the attachment rules, the model client and its transport
                     seam                                          → Core, the Anthropic SDK
Patterns.Audience    the room's service: the door, the answers, the queue, the wall's modes, the
                     host's page, the feed                         → Core, Rendering, Arcade
Patterns.Platform.Windows
                     what Windows says about the machine: the display modes, the observed signal
                     (QueryDisplayConfig, advanced colour), the EDID reader (the registry), the
                     adapters and their drivers (DXGI, the display class key), the audio
                     endpoints (WASAPI), the power plan (powrprof), the CPU and memory counters —
                     best-effort, cached, a Source hook per probe for a test
                                                                   → Core, NAudio, PerformanceCounter
Patterns (App)       the desk, the nodes, the wire and the pages; the video engine (libVLC), the
                     web engine (WebView2), the decks (PDFtoImage), the audio services that
                     compose the graph; the compositions of every edge  → all of the above, Avalonia
```

The tests follow the map: `Patterns.Core.Tests` (no native library beside it),
`Patterns.Rendering.Tests` (the Skia natives, the render module registered once),
`Patterns.Devices.Tests`, `Patterns.Audio.Tests`, `Patterns.Assistant.Tests` (the render module
for the pictures), `Patterns.Audience.Tests`, and `Patterns.App.Tests` (the headless desk, every
composition, the module rules and the node's footprint — and the platform assembly through its
Source hooks, since its Windows paths only run on the Windows lane).

## 2. The rules

1. **The core references the runtime and nothing else.** No package, no other module. Its
   project file names none, and `ModuleRulesTests` asserts it from the compiled assembly.
2. **An edge references its own native library and the core**, and the render side where it
   paints (NDI, the arcade, the room). An edge never references another edge except those.
3. **Only the App names the UI.** No module outside it has `using Avalonia`; the source scan
   asserts it. An edge that needs the desk's thread goes through the core's dispatch seam.
4. **The rules live in the core; the edge holds what touches the world.** The audio *routing*
   rules (the matrix, the decibels, the duck envelope) are the core's because the wire's
   vocabulary and the cue validator read them; the audio *providers* are the edge's. The device
   *profiles* and the confirmation levels are the core's; the *links* are the edge's. The
   room's *rules* are the core's; the room's *service* is the edge's.
5. **An edge asks for a contract, never the desk.** `IShowHost`, `IDeviceHost`, `IOscHost`,
   `IBeaconHost`, `IMdnsHost`, `IAudienceHost`, `IPlayHost`, the assistant's `IAssistantHost`:
   what the edge needs of the process it runs in, and no more. The desk and the kernel
   implement them; a test hands in a fake. `KernelTests` asserts every constructor takes the
   kernel or a contract the kernel provides.
6. **The render side reports; the core never reads it.** The media memory view reads a
   registered source of the render side's bytes; the assistant's attachments shrink pictures
   through a registered codec; a per-snapshot render state hangs on the snapshot's typed
   attachment bag under the render side's own key. `RenderingModule.Register()` fills the hooks
   once, at the desk's composition and the arcade's; a role that never draws leaves them empty
   and the core's answers are honest for it.
7. **Nothing that draws reaches an integration.** The render-core boundary test walks the
   render, NDI, arcade and audio assemblies' IL: no assistant type, no Spotify, no socket.
8. **What Windows says lives in the platform assembly** (round 65.12). A probe of the operating
   system — a display path, an EDID, an adapter, an audio endpoint, a power plan — is
   `Patterns.Platform.Windows`'s, referencing the core and the native libraries alone; the desk's
   UI geometry crosses to the core's `RasterRect` at one seam (`PlatformSeams`). Every probe is
   best-effort and never throws into the desk; every probe answers through a `Source` hook so a
   test hands facts in; the readings that cost (the machine, the display paths) are kept and
   refreshed on a worker, never on the desk's thread. The Windows CI lane's signal-report step is
   where the P/Invoke layouts are proven — the Linux suites cannot.

## 3. The seams

- **Geometry and colour** (`Patterns.Core.Geometry`): `RasterPoint`, `RasterSize`,
  `RasterRect` and `Rgba`, with the semantics of the canvas types they replaced (edges,
  differences, the all-zero empty, the plain bounding-box union). The render side converts at
  the edge (`ToSk`, `ToRaster`, `ToSkRect`) by a copy of four numbers. The rig, the screen
  layout, the gap map, the edge-blend derivation, the decks' raster ceiling and the stream plan
  are argued in them. The names avoid Avalonia's `PixelSize` and `PixelRect`.
- **The playhead** (`IPlayheadSource`): what the video clock asks of a playing source without
  the canvas it draws on; the frame source extends it.
- **Dispatch** (`Patterns.Core.Services.Dispatch`): a post, a check, an awaitable invoke, a
  timer with the UI timer's three lines. The app installs Avalonia's dispatcher once, where the
  kernel captures the UI thread; a process with no UI and every test of an edge keeps the inline
  runner — a post runs at once, a timer fires when the test says so.
- **The host contracts** (`Patterns.Core.Services.EdgeHosts`, `Capabilities`): what each edge
  asks of the process; the desk and the kernel provide them.
- **The render side's hooks** (`MediaMemory.Source`, `Pictures.Shrinker`,
  `ShowSnapshot.Attachments`): the core defines the shape; the render module fills it.
- **The module map** (`Patterns.Core.Services.Modules`): name, native edge, purpose, and what
  this process loaded. STATE carries `modules[]`, the support ticket names them, the assistant's
  brief says what the process is running, the desk and every node log it when their services
  are up.

## 4. What the split found

The compiler is a better reviewer than a folder. Moving the render side out named every place a
rule of the core had been argued in a canvas type or reached a drawing class: the rig geometry
and the screen layout in Skia rectangles; the gap map and the edge-blend derivation in the
render folder though they are pure maths; the decks' raster and the stream plan in canvas
sizes; the snapshot carrying a particle sim; the frame-stage words and the ticker's maths on the
render side though the budget and the bus read them; the quest level and the input keys beside
the games and the video seam though they are rules; the assistant's picture codec inside the
core's attachment rules. Each moved the right way. On the edges: the stinger voice asked for the
whole audio graph and needed a yes-or-no; the delay-key and device-resolving rules lived in the
player service and belong with the outputs; a chosen-but-missing output was written into a
static of the desk's from inside the rule (now returned, for the desk to show); the graph's mix
constants were the module's; the room's moderation walked the desk's node registry from inside
the room (now one call on its contract, the kernel deciding whether to ask its own assistant or
a desk over the wire).

## 5. A node's footprint, measured

`NodeLightnessTests` boots a timer node host and records what it loaded. Alone in a process, the
timer loads the core, the devices (the beacon and DNS-SD are the kernel's), the assistant (the
kernel's) and the render side — and never the NDI runtime, and since round 64 never the room
(`Patterns.Audience`) nor the arcade (`Patterns.Arcade`): a role builds only its modules.
`NodeKinds.RunsRoom` and `RunsArcade` say which (the desk and the arcade node; a timer and a
caller neither), `NodeHost` builds each in a method of its own so compiling its constructor
touches neither type, its `Play` and `Arcade` are null on the roles that lack them, and the wire
answers their verbs with a closed door — `ARCADE STATUS` and `PLAY STATUS` with *not on this
node*, `/api/play/*` and `/api/arcade` with `{"ok":false,"msg":"no audience room on this node"}`,
the audience port with the door shut before a byte is read. `ARoleBuildsOnlyItsModules` boots a
timer, a caller and an arcade node in that order and asserts each one's composition; the suite's
process is shared, so the assembly claim is measured against what was loaded before the boot,
and CI's lifecycle job runs the class in a process of its own with `PATTERNS_FOOTPRINT_STRICT=1`,
where the claim is absolute. Every start of the app logs the same words (`Modules loaded: …`),
which is the record a support ticket reads, and `NodeKinds.Composition` says the role's
composition in words.

## 6. The process-boundary road

An edge behind a narrow contract can be hosted in a child of the supervisor; the pieces exist
(`ChildProcess`, `HostProtocol`, `SharedFrameRing` for pixels). What each would need, in order of
worth: the render side's pools and fence already speak in frames and generations, and a child
renderer would hand frames over the shared ring — the largest fault to isolate (a driver, Skia's
GPU backend) and the largest change; the NDI runtime is the easiest (frames in and out over the
ring, a runtime absent on many machines never loaded by the desk); the device transports are a
line protocol already (a child that speaks the wire's lines to the boxes); the audio providers
need a clocked stream across the boundary, which the ring gives. None is built this round: the
assemblies are the precondition, and the measurement that would justify each (the render faults
row, the NDI runtime's absence, a box driver's hang) is on the desk.

## 7. Considered and left

- **A libVLC edge** (the decoder, the capture, the encoder, the stream): the audio graph rides
  the decoder's audio taps through the App's composition; the tap contract this round's audio
  module starts is what a `Patterns.Vlc` cut needs first. The stream alone would leave the
  tangle in place.
- **The arcade built for every node**: measured above; the next cut, not this round's.
- **A Companion variable for the modules**: an operator's deck has no use for it; STATE carries
  `modules[]` for tooling and the support ticket names them.
- **Namespaces left as folders**: `Patterns.Rendering` holds the generators in its root so no
  namespace named `Patterns` sits under another (a relative `Patterns.X` inside it would have
  resolved wrongly); the core's `Media`, `LowerThirds`, `RigDay`, `Arcade` and `Play`
  namespaces keep the rules that stayed.
- **Splitting the App's audio services into the audio module**: they compose the graph over the
  desk's decoders, stingers and NDI sends; the module holds what has no desk in it.
