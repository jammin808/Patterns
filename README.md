<p align="center">
  <img src="src/Patterns.App/Assets/patterns-256.png" width="96" alt="Patterns icon" />
</p>

<h1 align="center">Patterns</h1>

<p align="center">
  <b>Portable Windows test-pattern suite for corporate events, shows and festivals.</b><br/>
  LED walls · video walls · blended projection · multi-screen · countdowns · branding · NDI®
</p>

---

<p align="center">
  <img src="docs/media/shot-ui.png" alt="Patterns main window — LED wall editing with live preview" width="820"/>
</p>

**Patterns** is a single portable `Patterns.exe` you keep on a USB stick. It opens instantly on
any Windows 10/11 x64 machine — no install, no admin rights, no registry — and puts accurate,
pixel-exact test patterns on every screen, wall and NDI receiver in the room. It is built to run
for hours without a hiccup: GPU-accelerated rendering, zero-allocation draw loops, per-frame
fault containment, and settings that can never brick startup.

## What it does

- **The Patterns test card, and a new install comes up on it** — one card for a rig day's first four
  questions: which screen this is, whether the pixels arrive one to one, where the edges went, and
  what the processor has done to black, white and grey. A one-pixel border and edge ticks that count
  overscan in pixels; five one-to-one fields, each exactly half lit, that moiré through any scaler —
  and which one breaks says whether the scaling is horizontal, vertical or both; an eleven-step
  staircase with its code values on it; clipping patches at 2/4/6/8 on black and 253/251/249/247 on
  white; a gamma match whose vanishing solid is the display's gamma; and the screen's own name,
  large. Every measurement is fixed in the code, so a reading taken in one venue can be quoted in
  another — and when the card is drawn on a monitor-wall tile or a non-native canvas it says so in
  orange, because a measurement that cannot say when it has stopped measuring is worse than none.
  Three densities (Rig, Pixel, Levels) and the mark drawn into the card rather than over it.
- **Three states for a look, and what each screen is actually drawing** — a look key used to carry
  one bit, and that bit stays true however much the picture moves after it is recalled. The desk has
  read PROGRAM · EDITED on its own Looks page since round 17 and never put it on the wire; it does
  now, off one shared reading, and per screen: *the look is up, and screen 3 has gone its own way*
  is a different thing to know from *the look is up*. A screen the look itself gave its own picture
  does not count, and a locked screen never does. Companion 2.8.0 adds `look_edited`,
  `look_screens_off`, `screen_off_look` and `screen_pattern_is` on the built-in keys, plus the
  stream's health, the outputs, EDIT SAFE, the tone and how late the day is running — facts the desk
  had been pushing that nothing read.
- **Save a picture as a preset, and get it back in one press** — the chips under the Save box on
  the Pattern page ARE the recall; before this round saving worked and recall lived on a different
  page with nothing saying so. With EDIT SAFE open a recall lands in the preview and waits for the
  TAKE, because a recall is something you do while building. On the Show panel each screen's row now
  offers your presets beside the show's looks in one picker, so `→ THIS SCREEN` puts a saved picture
  on that screen alone, live, with every other screen untouched — a preset is a pattern and not a
  look (no overlays, no countdown, no per-screen arrangement), which is exactly what makes that safe.
  A cue and the wire reach both: `PRESET Walk-in`, `SCREEN 2 PRESET Walk-in`.
- **A MIDI control surface is a device, and the table it already had is the map** — an APC40, a
  Launchpad, a nanoKONTROL, an X-Touch. Its messages arrive as the same text lines an Arduino sends,
  so the trigger table on the Interactive page IS the map and the action layer, the journal and the
  arm fence are the ones the desk already had; the whole model cost is one enum member. Press LEARN
  and press the control — nobody can state a controller's note numbers without the hardware in front
  of them, so the desk asks instead of shipping a table that is wrong at a venue. One table reads
  both ways: `NOTE 1 53 * → LOOK 3` is the pad firing the look, `LOOK Walk-in → LAMP 1 53 21` is the
  look lighting the pad, and `VOL * → CC 1 48 %` drives a ring of light from the show's own level.
  A release is its own word so a pad fires once; a fader reads 0–100 so it does not silently die in
  the top of its travel; presses are instant while faders are sampled fifty times a second, which is
  what keeps a sweep from becoming hundreds of journal writes on the thread that draws the desk.
  Starter rows for four researched surfaces land in your own table, editable, each saying it has not
  been run against hardware. Windows only, one program per port, and the page says so.
- **A picture or a short clip imported into a lower third** — `+ PICTURE` and `+ CLIP` ask for
  the file as you press them, offering only what that element can draw, and the file is copied
  into `media/` beside the show with the design pointing at the copy: the folder is the show, so
  copying the folder takes the pictures with it. A path that no longer resolves is found by name
  in `media/`, so a show opened from another drive letter finds its own pictures; a file too big
  to carry is pointed at where it is and the desk says so; what is genuinely missing is named in
  red on the element and warned about by the cue checks **before doors** rather than found as a
  blank rectangle in front of the room.
- **A transition arms on a take, and on nothing else** — the engine used to infer a take from its
  consequence, so with EDIT SAFE off every keystroke in the editors crossfaded the wall, and a
  pane switching from one screen to another dissolved between two pictures that were both already
  on air. The snapshot now carries whether it is a take, set around the action layer and the one
  seam that edits the air. A transition already running is never abandoned by an edit — only by a
  cut, a move, or transitions being switched off.
- **The switcher's round trip: pull it in, stage it back, take that tile** — `→ PVW` on the PGM
  tile brings what the room is watching into the preview to change (the air untouched, EDIT SAFE
  opened if it was off) and keeps the focus where it was, so a FOCUSED take afterwards no longer
  silently widens to the whole rig. `SEND` on a tile now **stages**: that tile's PVW holds the
  picture, the room does not, the tile is focused as you press, and CUT or TAKE with FOCUSED puts
  it up there and nowhere else. A staging the next take leaves alone is dropped rather than left
  behind as a screen that has quietly stopped following the show.
- **The room's outputs and the operator's own, on separate wires** — the Audio page is in two
  halves: PROGRAMME OUT is what the room hears (the USB or dedicated interface, the HDMI screens,
  the computer's own, each with its lip-sync delay), MONITOR is one output that is deliberately
  none of those. A mount on the programme bus goes to the programme's outputs whatever the
  operator is listening to; anything else goes to the monitor output when one is named, and when
  none is it stays silent and the line says why. The soundcheck tone follows the programme's
  interface rather than whatever Windows calls default, and a named interface that is not plugged
  in is said out loud in red instead of silently substituted.
- **The badge goes on every picture the desk draws** — the rule was an allow-list of pattern
  kinds written when there were eight, so every kind added since arrived without it (Reactive was
  simply the most recent). It now names its exceptions — never on a monitor wall, on media only
  when asked, and never on the Patterns test card, which carries the mark inside itself — so the
  next kind carries it the day it is added.
- **The divider drags on the Admin pages too** — the Machine and Help pages take the room with
  the screens as a strip on the right, and the strip's width now follows the divider the way the
  page's width does elsewhere: the show remembers it (◧ WIDE shares it), and a drag there never
  changes an ordinary page's width behind your back.
- **The settings beside the page** — the selected cue's, screen's or lower-third element's
  settings pop out into a column of their own between the page and the switcher, so the Cues
  list, the screen overview and the designer keep their room: ◀ CLOSE hides it for that
  selection, SETTINGS ▸ brings it back, the next selection opens it again, the column wears the
  page's colour and ? TIPS reads its explanations after the page's. Pages without a selection
  and the wide pages keep their layout.
- **Attach the material and the assistant works out a plan** — ATTACH… on the Assistant page
  takes a screenshot or a photo of the rig, a brief, notes, a running order as a spreadsheet or
  CSV, a PDF, a Word or PowerPoint file, or a mixture, up to ten per ask; each is read at once
  into a chip with a note (a picture reduced to what the model reads best, a sheet as a text
  table, a document as its words, a PDF as it is), rides with the next ask as its own block, and
  ASK with nothing typed asks for a plan from it — a running order becomes cues with times, a
  brief becomes screens, looks and lower thirds. What is inside a file is material, never an
  instruction, and nothing is sent until you ASK.
- **The assistant reads the desk** — its brief carries every state the desk has at the moment
  you ask, not just the show file: EDIT SAFE and which picture is on air and which is in the
  preview, the outputs, the editing target, the joined canvases and what every screen shows now,
  the cue on standby, the inputs open (by nickname, never an address), the library's names, the
  sound playing, the lower third on air, the sends and the stream — and it is told to read them
  before proposing, never to propose what already exists, and to say when what you ask for is
  already on air. How it is wired into the architecture, and what it still cannot do on purpose,
  is `docs/PLAN.md` §28.2.
- **The assistant: newest first, the preview only, screens apart** — the conversation reads
  newest first with the latest answer lit under the ask box, and the cards have room; APPLY of
  a proposal that draws opens EDIT SAFE when it is off and lands on Program's own pattern, so
  what is on air stays until TAKE or CUT; every planned screen it adds (and every one the desk's
  + PLANNED SCREEN adds) lands a gap from the rig, its own target, never one wide canvas by
  accident; the rules tell it a wall fed by several outputs is one screen of the wall's size.
- **What .NET 10 buys, applied where it pays** — the garbage collector runs in sustained low
  latency while the outputs are live (no full stop-the-world collection for the length of the
  show) and rests off air, and the Machine page's runtime line reads which, with the .NET in
  use; the clone every publish takes is compact; the spectrum keeps its buffers and the capture
  feed reads its bytes as samples; a blended output keeps its gradient stops; the 10-bit NDI
  conversion runs eight pixels at a time on 256-bit vectors, bit-equal to the scalar row it
  replaced; the runtime keeps its memory between collections. What is not open, and why
  (NativeAOT, trimming, the newer C# while the .NET 8 escape hatch exists), is `docs/PLAN.md`
  §26.4.
- **A faster start and restart** — the window builds the page on the rail and the rest in idle
  time after the first frame (a click in the first second builds its page, guarded); the show
  file is read once before the desk and handed to it; the Machine page's Start-up line names
  nine phases from the process's own start (*runtime · settings · graphics · avalonia · services
  · view model · pages · window · first frame*); RESTART APP comes back sooner — the watchdog is
  woken by the exit instead of polling, the show goes back on the moment the window opens
  instead of after a timer, the shutdown runs once and the NDI senders stop together; the
  published exe is no longer compressed, so it is larger and starts faster.
- **The particles, hardened like the fractals** — a sim per field on every sink, so a crossfade,
  a monitor wall or a layer never re-seeds the field in the hot path; a catch-up bounded per
  frame; the quality ladder thinning the draw and never the field, so every output shows the
  same particles; a late sink (an output at OUTPUTS ON, an NDI send mid-show, a display plugged
  in late) joining the running field from its first frame; a designer scrub that no longer
  freezes it; NaN and dispose fences. And the caller's plan across midnight: a cue planned for
  23:55 read at 00:04 is nine minutes late, not a day early.
- **The Fractals page** — BUILD → Fractals, a fractal studio built like the Particles page:
  scenes filed by family (Mandelbrot's coast, the Julia constants, the burning ship's armada,
  Newton's basins, five domain warps — twenty-four scenes), your saved fractal presets under
  Custom, the family and the view, a palette or the brand kit's colours at a press, the sound it
  listens to, and the stings that surge through it; USE IT makes it the pattern, and the Pattern
  page points to it with OPEN FRACTALS (OPEN PARTICLES for the particles). The Library files the
  scenes under Fractals by family.
- **Which group a screen is in** — main screen, confidence monitor, info desk, repeater, an NDI
  feed's or the stream's own screen: every wall tile's foot line reads it (MAIN · 1920×1080,
  REP ↳ A · Main wall, NDI · 1280×720), a pause says what it means, and a click opens SETUP →
  Screens on that screen, where Role and Mirror of are set. TICKED GROUPS still means the joined
  canvases, and the tooltips and Help say which is which.
- **The Patterns badge** — a branded test card: the app's own mark (the test-card icon, PATTERNS
  and a line under it, in its neon colours) drawn by the engine over every test pattern on every
  output, NDI send and the stream, on by default in the middle of the lower third, in every look
  like the clock. It keeps off a client's media unless asked, drags on the PREVIEW pane, and
  OVERLAYS OFF takes it with the rest. Branding → PATTERNS BADGE.
- **Any screen into the preview** — → PVW on a wall tile loads the picture that target shows on
  air (its own, its source's if it repeats one, else the program's) into the preview to edit; the
  air is untouched, and EDIT SAFE opens first if it was off. Then SEND it to a screen, or TAKE. The
  desk's own action, journaled like the rest.
- **A send keeps the preview** — SEND on a wall tile (and SEND TO TICKED) puts the preview on
  that target alone as its own pattern, live, and the preview keeps the picture: send it to the
  next screen, edit and send again, or TAKE it. It used to close the sandbox and open a fresh one
  that mirrored the program, so the picture just built was on one screen and nowhere to edit.
- **The switcher's tiles show one face** — a screen tile had shown its name on its side over its
  body with the OWN / MON / ARM row at mid-height: the two faces were chosen by a binding up the
  visual tree that a tile rebuilt after the wall could miss. Classes on the tree choose now, the
  body sits at the top, and every title row is PGM's height. ▸ on a tile's title row collapses that
  tile alone (▾ on the bar opens it; the show remembers), and a screen's group is a joined canvas
  made by dragging screens flush on SETUP → Screens — said on the tooltips and in Help.
- **The grid reads on the switcher tiles** — a tile or a pane is a true miniature, the target drawn
  at its own size through a scale to fit, and a one-pixel line at a twentieth of a device pixel
  drops out. Patterns now read the sink's device scale and widen their hairlines to the tile's own
  pixel (minor lines that would not resolve are left out); on an output nothing changes.
- **The assistant answers again** — the service had refused its first question ("the compiled
  grammar is too large"): the reply's schema had optional members in closed objects, which the
  service's grammar pays for with a state per subset. Every member is required now, null where it
  does not apply; and should the service refuse a schema again, the same ask goes once more in
  plain JSON with the shape in the prompt, the reply read all the same, the status saying so once.
- **No crash between pages** — the Lower Thirds page's designer preview kept one sink for its whole
  life, disposed it on the way out and drew with it on the way back, on the compositor's render
  thread with no gate; a fractal element's disposed shader made the freed handle an access
  violation. A guard (`SinkGuard`, the wall's own rule for a control that draws by hand) closes the
  sinks as a page leaves the tree, draws nothing after, and makes fresh ones on the way back; the
  Screens page's tiles keep the same guard.
- **The stream encoder in its own process** — libVLC's encode runs in a process of its own (this
  same exe with `--host encoder`), fed by the desk through a shared-memory frame ring and watched
  the way the watchdog watches the app: a fault in the encoder ends that process, not the desk,
  and it is started again with backoff while the show carries on; the Stream page reads LIVE with
  the process named, *Encoder restarting … the show is untouched*, or why it stopped; a crash
  loop stands down with words and START tries again. On Windows the encoder dies with the desk,
  never left streaming on its own. The host is built for the decoders to follow.
- **Fractals on the card everywhere** — a lower third's fractal element now draws through the
  same runtime shader as the Fractal pattern on the outputs, the preview and the monitors: full
  resolution at the display's rate, nothing rastered, nothing uploaded. And the CPU path — NDI,
  the stream, thumbnails — draws the same picture as the card at last: the domain-warp cloud
  used to be a different cloud there (a double against the shader's float in a hash that
  amplifies the difference); the noise is single precision now and a test holds every family
  on both paths within a few levels of each other.
- **A cue with a shape in time** — every step of a cue carries an **After**: seconds to wait after
  the step above it. 0 — the default — means it goes with the GO, so a cue built before this is
  exactly what it always was: one edit, one change on the screens. Put 3 on the lower third and the
  picture lands on the press while the name arrives three seconds later, from one GO instead of two.
  The row shows where the step falls in the cue (**+3 s**) and the line under the list says how long
  the whole cue runs for. Reorder with the grip (⠿) — dragged with pointer capture rather than the
  OS drag loop, or the ↑ ↓ buttons — and **the waits stay with the positions, not with the steps**:
  the cue keeps the timing you built and only the content moves, so the list order is always the
  running order. What is still to come is dropped, always, by the next GO on that list, by STOP ALL,
  by disarming, by resetting and by loading another show — a step from a cue two cues ago can never
  land on the audience. The status line and the journal name each step as it runs; the sheet's
  **After** column exports and imports with the rest.
- **One action vocabulary** — a cue's step is now the same show action the desk's keys, the wire,
  OSC, Companion, the schedule and a device send: the cue-only kind and the 65-row map to the
  executor are gone, and so is the wire's own — the parser hands back the show action itself
  (plus five words of its own: PING, STATUS, HELLO, CUE LIST and a line it cannot read) and the
  router runs it through the one executor; the stack's standby, hold and arm are show actions the
  Run surface, the keys, the phone, Companion and OSC all send the same way. One table classifies
  every verb (a cue's to pick, or the desk's alone with the
  reason), and thirty-four verbs a cue never had — the clock's format, seconds and date, the
  message's scroll, a countdown to a time and its label, the logo, the PiP, overlays off, the
  pattern's kind, freeze, tone, stop all, a look into the preview, the look before, a web page
  opened, and more — are in the cue editor, the checks, the sheet and the assistant's catalogue. A
  fade's length means seconds from every source. Show files load unchanged. The reasoning, and the
  one map that remains, are in `docs/PLAN.md` §22.
- **Cloud processing, considered** — asked whether rendering on AWS or similar could help a
  lower-spec machine: possible, and not worthwhile for the show's own path (the latency, the decode
  on the laptop anyway, the venue's internet as a single point of failure, the bill); worthwhile for
  work done ahead of the show, which is what the assistant already is and what baking heavy looks to
  clips would be. The reasoning is in `docs/PLAN.md` §20.10, the round read against the standing
  brief in §20.11, and what is established, what is still a guess and what comes next in §20.12.
- **The sections read apart** — every page wears its own neon, the hue of its chip on the rail:
  the title a step bigger in that colour, every section heading a bold band in the same neon on a
  subdued ground of the same hue with room inside it, the page's panels edged with a hairline of it,
  and the rail and the page strip underlining the current group and page in theirs; the pane
  captions step up with the headings.
- **Layers on their own page** — the two pictures over a target's pattern have left the Media page
  for a page of their own on the BUILD rail, between Branding and Library (before the library and the
  assistant): EDITING TARGET at its top says whose layers, LAYER 1 and LAYER 2 with everything they
  had — the source, the box, the fit, the crop, the corners, the border, the opacity — and a web
  layer's PAGE CONTROLS on the same page; the Media page keeps its page block and the same controls.
- **The Run area's room** — the cue stack takes about a third of the Run area by default and the
  divider between the wall and the stack drags, saved with the show and shared by the main window
  and the pop-out; ▸ COLLAPSE TILES turns the wall's tiles into vertical title bars — the tally and
  the name on their side, no miniatures drawn — so the stack has the room (the desk's own wall keeps
  its full tiles); a pause over any tile, full or bar, pops it up large with PGM and PVW side by
  side, drawn only while the popup is open.
- **CUT / TAKE with a scope** — a picker on the wall beside CUT and TAKE says where the next send
  lands: ALL ARMED (ARM and LOCK on the tiles decide, as before), FOCUSED (the
  tile you clicked; the PGM tile means every armed screen), TICKED or THE TICKED
  GROUPS. Everything outside the choice keeps its picture exactly as an un-armed tile does — pinned
  as its own, lifted by the next full TAKE — so a scoped send is the un-armed TAKE with the arming
  implied by the choice; the ticks are consumed by the send like SEND TO TICKED; the status line
  says where it landed and how many kept their picture. TAKE and CUT stay desk keys: the wire never
  takes a half-built preview to air.
- **Fade to black on one screen or a group, with the sound** — FADE TO BLACK and FADE UP on the
  Show panel land where a picker says: every screen (the blackout with a fade of its own), the
  focused wall tile, the ticked tiles or the ticked groups — that part of the rig fades on its own
  while the rest keeps its picture, drawn black by the engine with the sink's own crossfade as the
  fade, never in the show file. The wire has it per screen and per group (`FADE 2 SCREEN 2`,
  `FADE GROUP A`, `FADE FOCUSED`, `FADE TICKED`, `FADEUP … SCREEN 2`), OSC too
  (`/patterns/fade/screen/2`), a cue's *Fade to black* / *Fade up* pick the place from a list, the
  phone's SCREENS tab has ▼ ▲ per screen, and Companion 2.6.0 has the place on its `fade` action
  with per-screen and per-canvas keys. WITH THE SOUND (on by default, saved with the show): a fade
  that leaves the whole rig dark takes the music and a clip's soundtrack down over the same seconds
  and the fade up brings them back; a VOG and a stinger play through; BLACKOUT covers everything
  and never touches the sound. STATE carries `black{count,text,audio}` and a `black` flag per
  screen; the wall's ticks are there with or without EDIT SAFE.
- **The Machine page's card as lines, and bars that fit** — the GPU's busy share and its video
  memory in use are drawn like CPU and memory (the last three minutes beside the day so far, the
  memory against the card's total), and HEALTH AT A GLANCE's bars stay inside their pills; the
  reasoning is in `docs/PLAN.md` §20.4.
- **The Show panel's stop where the chips are, and a wall toggle that fits** — ■ Stop and the
  STING HOLD banner sit right under the VOG and STINGER chips instead of below the lower thirds
  and the people, and the wall tile's OUTPUT switch is a wall button on the title row (OUT, green
  while the screen's output is on) that stays inside its tile; the reasoning is in
  `docs/PLAN.md` §20.3.
- **The editing target is never blank** — the Pattern page's EDITING TARGET picker keeps its
  selection through every rebuild of the rig (a lock, a label, OWN on a tile, a display plugged
  in, a show loaded) and falls back to Program, never to an empty box; the chain is in
  `docs/PLAN.md` §20.2.
- **A fault on the desk never takes the show down** — an exception on the UI thread (a page that
  fails as it comes in, a button whose handler throws, a timer's tick) is caught at the dispatcher
  or at the button, logged with its stack, counted on the health line with the exception's own
  words and put on the status line, and the desk, the outputs and the remotes carry on; what
  cannot be contained (out of memory, a native fault) leaves a crash note that names what threw
  and where, so the next start's health line reads *… ended in an unhandled .NET exception —
  InvalidOperationException: … in MainWindow.ApplyDeskLayout — at 21:14:02 after 12 min* instead
  of an exit code alone. The reading of the round-15 crash between menus is in `docs/PLAN.md` §20.1.
- **Companion 2.5.0: tick the groups of keys this desk uses** — the connection's settings gain a
  checkbox per group (all looks, all patterns, clock functions, countdown functions, the message,
  the overlays, all stingers, all VOGs, all lower thirds, all people, screens, audio, presenter,
  install) and only the ticked groups reach the preset list, every key still labelling itself
  from the show and lighting from the air; new keys for the clock, the countdown (a key that
  counts down, green while it runs, red when over), the message, the logo, the PiP, every overlay
  off, and a key per kind of picture, with `PATTERN <kind>` on the wire behind the last.
- **The phone drives the clock, the message, the countdown and the overlays** — an OVERLAYS tab
  on the phone remote: the clock on or off with its hours, seconds and date; the message's words
  typed and shown, scrolling or still; a countdown of any minutes or to a time of day, with a
  label, counting on the phone every second; the logo, the PiP, the weather chip, and every
  overlay off in one press. Every key is a one-line verb any controller can send (`CLOCK 24`,
  `MESSAGE Doors open at 7`, `COUNTDOWN TO 19:30`, `OVERLAYS OFF`), the same over OSC, all of it
  read back in STATE.
- **The Show panel's chips three to a row, lit while live** — VOGs, stingers, lower thirds and
  people sit three to a row on the Show panel, each chip a name over a small line that says what
  it is (the kind, the after-choice, the person a design holds, the role) until it goes live,
  when the border lights red on air or green in the preview and the line reads the tally: *ON
  AIR · 12 s*, *HOLDING*, *ARRIVING*, *IN PREVIEW*, and for a person *ON AIR · Neon* — the design
  that carries the name, whichever way the person got there.
- **The Lower thirds page keeps its preview in view, and a person's card to the name and the
  role** — the stage and its timeline are pinned at the top of BUILD → Lower thirds with a line
  naming the design (and ON AIR / IN PREVIEW while it is up) while the designs, the library, the
  elements and the styles scroll under it, so every edit shows without scrolling back; a person
  in the library reads the name and the role, with the company, the photo and the note in a
  drop-down whose header says what is inside — *More — Acme Ltd · no photo · note*.
- **The effects step down before the room sees a stutter** — a quality ladder, a game engine's
  dynamic resolution for the particles and fractals: when an output's frames run past 25 ms for
  three seconds, particles and fractal iterations step down a level (70 %, 50 %, 35 %) on every
  sink at once and step back up after thirty clean seconds; Machine → QUALITY LADDER shows the
  level and why, and can lock one (Full never steps; Balanced and Economy for a small laptop from
  the first minute). Beside it MEMORY CEILINGS puts the numbers to the app's bounded memory —
  the working set against a quarter of the machine, ten pictures cached, four decoders, frames
  held 400 ms for a fade — so a climb reads as a leak before it becomes one.
- **The standby cue's clip is pre-rolled** — the moment a cue whose look carries a video clip goes
  on standby, the decoder opens the file, decodes its first frame and holds it there, silent, so GO
  cuts onto the picture rather than onto a decoder opening; the Run strip reads PRE-ROLLED beside
  STANDBY (PRE-ROLLING… while it opens, CLIP NOT OPEN when the four decoders are all live or the
  file will not open — GO still works, the clip just opens at the press). Standby moves on and the
  held clip goes; a clip already on the screens is never held; GO releases the same decoder, no
  reopen.
- **A slow frame says what to lower** — the engine times every frame it draws for the preview, the
  monitors and the outputs with the stage that took it (the pattern by its kind, the layers, the
  overlays, the lower third, a crossfade), and keeps each sink's last minute; the Machine page's
  STABILITY block, the super-check's *Render frame* row and the RENDER tile read *worst 31 ms (the
  lower third) on Output 1 (Main)* with advice by the stage — fewer particles, Fast fractal quality,
  the clip's decode — rather than a number. Beside it a start-up budget: the phases from Main to
  the first frame, so a machine that takes twenty seconds to become a desk names the phase that
  waited.
- **The next crash leaves a trace, and the run after it plays safe** — the watchdog puts the exit
  code in words on the health line and in the log (0xC0000005 is an access violation: native code —
  a decoder, a driver, a library — not Patterns' own), keeps a mini-dump of a native crash in a
  `crashes` folder beside the settings (with `createdump.exe` beside the exe, which the publish
  script now places; the note says when it is missing), and starts the run after a native fault
  with clips decoding in software, so one show rules the graphics driver's decoder — the first
  suspect on a laptop — in or out; Machine → VIDEO DECODING sets Auto / Hardware / Software outright
  and says what this run does, and the support bundle carries the note and the newest dump. The
  fractal raster takes half the cores and a lower third's fractal element draws 25 new frames a
  second, so a small machine keeps its breath for the audio and the decoders.
- **Stingers and VOGs that always come back** — a clip on the screens is never left with nothing
  owning it: a probe that throws mid-clip is carried past rather than dropping the session, STOP
  puts back a clip that nothing owns (the last show that was on, else the look on air) and says so,
  a press of a clip whose decoder is already open plays it from the top, a clip whose position stops
  moving is put back as stalled, and a crash mid-clip never restores the clip as the show. The chain
  the round-14 report described, read from the code, is in `docs/PLAN.md` §18.1.
- **The assistant** — an optional helper on the BUILD rail that drafts a show with you: say how
  many screens you have, what the day looks like and which overlays you want, and it proposes
  planned screens, looks with their overlays, a cue stack with planned times, lower thirds from
  the presets and the brand's colours — APPLY puts a proposal into the show exactly as you would
  have built it by hand, and nothing changes until you do; nothing it says goes on air. It talks
  about Patterns and live-event AV only and never about how Patterns works inside: a question
  about its instructions, Patterns' internals or any key is stopped on this side and never leaves
  the machine, the model is fenced by its instructions, and every reply says whether it stayed in
  scope. It sees names and counts (screens, looks, cues, designs, overlays, the brand), never a
  file, an address, a passcode or a key. Needs the internet and your own Anthropic API key, kept
  beside the settings file on this machine and never inside a show file. A cloud model on purpose:
  the desk's memory and graphics card belong to the outputs (the reasoning is in `docs/PLAN.md`
  §16.3).
- **The weather chip** — an overlay like the clock: the venue's forecast for this hour, the rest
  of today or tomorrow, drawn by the engine on every screen from one report — a glyph, the
  figure, the sky in words, the wind and the rain, small columns for the hours, a credit line.
  Search for the venue on the Overlays page (OpenStreetMap's search), pick it, choose the view
  and the units; the forecast is MET Norway's (free, no key, the world over) or Open-Meteo's (a
  key for commercial shows). A look carries the chip and its view while the place stays the
  show's; the Show panel's drawer, a cue, `WEATHER` on the wire and OSC, the phone and a
  Companion key that reads the figure switch it live; a failed fetch keeps the last forecast on
  screen and says so.
- **The desk's tick, budgeted and guarded** — the desk polls every service once a second on
  the same thread as every click, so that tick is now nineteen guarded areas: one that fails
  (a sound card gone, a serial device dying, a resolver that will not answer) is carried past,
  counted, and logged once a minute while the cues, the tallies and the clock keep moving —
  before, it took the app down to the watchdog. The tick is timed with the area that took the
  time and read on the Machine page's STABILITY block (*Desk tick 1.2 ms · worst 4.1 ms
  (tallies) in the last minute*), the super-check's *Desk tick* row (green under a desk frame,
  amber past 16 ms, red past 50 ms) and the RENDER tile. What it no longer does every second:
  ask the resolver for the machine's own addresses (kept for half a minute), serialise the show
  for the tallies (kept by snapshot version), or raise a chip that did not move. The view model
  behind the desk is nine partial files by area, one of them the tick.
- **The audio playlist** — the audio track was one file with a loop, which a stinger could do,
  so it became a playlist: tracks added one at a time and whole folders whose audio files play
  after the rows in name order (a file dropped in live is seen within half a minute), the same
  file never twice, shuffle by a seed the show keeps (RESHUFFLE deals again), loop the list or
  stop at its end; a missing file is skipped and the running order carries on. The Audio page
  builds it (▶ NOW marks the row, ▶ on a row plays it, ⏮ ⏭), the Show panel's AUDIO block reads
  *3/12 walk-in — 1:02 / 3:30 · next: intro* with the same keys; `AUDIO PLAY [n or name]`,
  `AUDIO NEXT` / `PREV` / `STOP` / `VOL` on the wire and over OSC, a cue's *Play audio* naming
  a track and *Audio — next / previous track*, Companion keys and a track bank that labels
  itself, the phone's AUDIO tab. A VOG ducks it, a stinger fades it, STOP ALL stops it; an older
  show's one track becomes the first row (schema 8).
- **The caller's VT clock** — while a clip is on air (the program's video, a playlist's, a
  stinger's clip, an audio file) the Show panel's PROGRESSION block, the Run strip, the phone,
  Companion and OSC all read one clock: the clip's name, where it is, how long it is and what is
  left, from the decoder itself; the last ten seconds go red with the caller's word (OUT IN 7), a
  loop reads LOOP and never calls an out. ⏭ LAST 10 s is the rehearsal's skip — the clip jumps to
  its last ten seconds and everything still happens for real (the end plays, the out is heard, the
  playlist moves on, a stinger runs its ending); ⟲ RESTART plays it from the top. `VIDEO END
  [seconds]` and `VIDEO RESTART` on the wire, `/patterns/video/end` over OSC, two cue actions,
  Companion 2.4.0's VT clock key that reads what is left and goes red for the out.
- **Field research, the operational review and the multi-core answer** — round 12 went to the
  places operators talk (ControlBooth, the vMix, Resolume and NewTek forums, the QLab list, AVS
  Forum, the Companion issue tracker, the production companies' own field guides) and wrote up the
  fourteen problems show callers, techs and operators keep reporting — freezes and reboots
  mid-show, double GO and skipping clickers, dead air, the wrong deck version, laptops that will
  not handshake, aspect mismatches, confidence monitors, last-minute running-order changes, NDI
  dropouts, sync drift, unseen machine health, control surfaces losing state, stale signage, the
  missing rehearsal — each with its sources, what Patterns does today, what this round built for
  it and what still stands, ranked ([`docs/FIELD-RESEARCH.md`](docs/FIELD-RESEARCH.md)).
  [`docs/PLAN.md`](docs/PLAN.md) §14 then says, area by area, where Patterns is strong and weak
  against its peers, answers the multi-core / orchestrator question from the code as it is (one
  show core, one render core with its sinks on their own threads, the supervisor already the
  orchestrator: keep one process, watch more, extract native edges at their seams when a fault
  says so, redundancy is a second machine), and writes down the instant-UX rules every page now
  follows.
- **The Machine page as a health dashboard** — ADMIN → Machine opens on HEALTH AT A GLANCE: one
  headline over twelve lit tiles (outputs, render, CPU, memory, GPU, NDI, stream, audio, remote,
  watchdog, power, disk), each a big value with a bar and a line under it, green / amber / red /
  grey with the super-check's thresholds; the headline is the worst of the tiles and the advice,
  naming the tiles that set it ("Attention needed — CPU, POWER") and counting what is below. The
  tiles update in place every second from the live sample; the rig's facts are read every five.
  WARNINGS AND RECOMMENDATIONS are cards worst first — what is wrong, why it matters mid-show, the
  next thing to do — and LIVE PERFORMANCE draws the last three minutes beside the day so far (the
  30-second averages: a memory line that only climbs is a leak). The super-check, the graphics
  card, the watchdog, the beacon and the earlier versions follow as before. Pure and tested:
  `HealthDashboard.Tiles`, `Overall`, `Verdict`.
- **Help reorganised: a searchable catalogue, in the order a show happens** — the Help page is now
  a catalogue of 37 topics filed in six sections (START HERE, RUNNING THE SHOW, CONTENT, THE RIG,
  CONTROL, THE MACHINE), each a card with *where it sits in the workflow*, *how it works* in depth,
  *do this* in order, *on the wire* (the verbs, the OSC addresses, the Companion keys) and *pages*
  (GO opens one). Search for a word, a key or a verb (`stinger`, `F5`, `SCREEN LOOK`, `spotify`,
  `arduino`): every word must be found, the title and the search words weigh most, the strongest
  topic comes first with the words around the match; section chips narrow the list; READ ALL opens
  everything for reading through. A new opening topic, *How a show flows through Patterns*, is the
  map the rest hangs on — the five groups as the stages of a day, and the three ideas that hold it
  together (the show file, the action layer, program and preview). ? TIPS on any page now lists the
  topics that page belongs to, one press away. The walkthroughs by role stay, as the same guide in
  checklist form. The catalogue is pure data (`HelpTopics`, `HelpSearch`), so the tests read it too.
- **The Show panel as the control surface** — one page runs the show beside the switcher. CUES is
  the caller's stack in a strip (STANDBY with its planned and expected time, NEXT, GO / HOLD / ARM,
  ▲ ▼, an auto-follow's CANCEL); LOOKS are one press each with a PVW key that loads the look into
  the sandboxed preview for a sign-off; SCREENS — EACH ON ITS OWN is a row per screen with a look
  picker — → THIS SCREEN puts that look's picture on that screen alone while every other screen
  stays, PROGRAM puts it back, LOCK and ON beside it; PROGRESSION reads the clicker's place, a
  deck's page, a counting auto-follow and the playlist's part in one line. The per-screen sends are
  actions everywhere: a cue's *Screen — its own look* / *back to the program*, `SCREEN 2 LOOK
  Sponsor` / `SCREEN 2 PROGRAM` on the wire, `/patterns/screen/2/look` over OSC, Companion 2.3.0's
  `screen_look` and `screen_program` keys.
- **Permanent installs: the clock runs the site** — a new PLAN page for the machine nobody sits
  at: a shop window, a hotel lobby, a museum wall. Programmes are looks on a rota (a start, an end,
  the days — `Mon–Fri`, `weekends` — the dates of a season, a window past midnight); adverts are a
  look for some seconds at their times, on every screen or only the screens they name (the others
  keep their picture); announcements are words on the message overlay, a VOG and a look of their
  own, by the clock or by hand — `ANNOUNCE Closing time`, `ANNOUNCE some words`, `ADVERT Offer`,
  a cue, a Companion 2.2.0 key, OSC, the phone. Announcements beat adverts, a firing waits while
  the caller's stack is armed, the idle look or black outside hours, TODAY lists the day as the
  clock will run it, and every change is journaled *from schedule*. Remote administration behind a
  passcode: the web remote's ADMIN page (health, the schedule, every announcement and advert as a
  key, RESTART, updates, a support bundle, the log, a console) and `RESTART` / `UPDATE APPLY` on the
  wire; a support bundle with the logs and the settings with every secret blanked; a management
  server the site checks in with (commands back, updates down, no inbound port); and updates the
  watchdog applies between two starts — the old files kept, the new build rolled back by itself if
  it does not stay up. Off by default. `docs/INSTALLS.md` has the rules and the contracts.
- **The Interactive area: Arduino over USB, Raspberry Pi and devices over IP** — a new SETUP
  page where buttons, sensors and lights in the room join the show. A device is a name, a link
  (serial at a baud, TCP, or UDP) and an address; it sends short text lines and each one fires a
  show command through the same action layer as the desk, the cues and Companion — a trigger
  row maps `BTN1` to `CUE GO` (a prefix `SENSOR *` carries its value into `MESSAGE Room at *`),
  or the line runs as the protocol it already is (`NEXT`, `LOOK 3`, `BLACKOUT ON`) — with `OK`
  or `ERR` and the reason written back and the journal reading *from device Arduino*. The show
  speaks back as `KEY VALUE` lines only when a value changes (`BLACKOUT 1`, `LOOK Walk-in`,
  `CUE 01.020`, `ARMED 1`, `DECK 3 12`…), so a lamp on the lectern follows the cue stack in six
  lines of sketch. A cue sends a line of its own (**Device — send a line**), and so do the wire
  (`DEVICE Arduino RELAY 1`), OSC (`/patterns/device/Arduino "RELAY 1"`) and Companion 2.1.0
  (`device_send`). Serial links reopen and TCP links reconnect by themselves; the page shows
  every device's state with the last line in and out; STATE carries the rows. Off by default —
  a port opens only when you say so. `docs/ARDUINO.md` has the sketch, a Raspberry Pi script and
  the whole line vocabulary.
- **Companion 2.0 and OSC: keys that fill themselves from the show** — drag a row of the new
  **bank** presets onto a Stream Deck once and every look, lower third, person, VOG, stinger,
  break-music entry, playlist part, screen and upcoming cue you make afterwards appears on the
  next spare key by itself: each bank key reads its name from a variable Patterns keeps fresh
  (`$(patterns:look_3)`, `$(patterns:lt_2)`, `$(patterns:cue_1)`…), fires the item at that
  place (`LOOK #3` is the third look in the show's order, whatever its name or F-key; the cue
  bank puts the cue at that place on standby, or GOes it), lights green while its look is on
  air or amber while it is in the preview, and dims while there is nothing behind it. The
  module also builds a preset per item — *Looks — this show*, *People — this show*, *Upcoming
  cues — this show*… — rebuilt the moment the show's lists change, so a named key is one drag
  away too. New feedbacks (`look_on_air`, `look_preview`, `look_bank_on_air`, `look_f_on_air`,
  `screen_armed`, `screen_own`, `slot_empty`), new variables (`air_look`, `preview_look`,
  `pattern` and the banks), a `stream` action, and STATE carrying `airLook`, `previewLook`,
  `pattern` and each look's place, F-key, on-air and in-preview flags. OSC gets the same:
  `/patterns/look/index/3` and feedback for every list (`/patterns/state/looks/3 "Walk-in"`,
  `/looks/3/air 1`, `/lowerthirds/n`, `/people/n`, `/stingers/n`, `/sections/n`,
  `/music/items/n`, `/screen/n/name`, `/cue/next/k`, `/deck/page`, `/deck/count`,
  `/deck/ended`, `/web/page`, `/web/service`, `/look/air`, `/look/preview`, `/pattern`), so
  a TouchOSC page or a lighting desk labels itself the same way. Companion cannot place keys on
  a page by itself — that is the one honest limit — so the banks are the answer: drag once,
  then never again.
- **Edge blend across three, four and a grid of projectors — proved and audited** — does the
  blending work independently with and across more than two projectors? Yes: every output fades
  its own edges from its own overlaps — a middle projector both sides, a grid's projector a side
  and a top or bottom — with its own curve, gamma and typed widths, and where two zones cross the
  fades multiply, so four projectors sharing a corner add up to one flat picture ((left + right)
  × (top + bottom) is one). Now it is proved on the real output pipeline (rows of three and four,
  a stack, a 3×2 grid, a 2×2 grid's shared corner summing to white) and audited on the desk:
  Screens → Edge blend reads every join of the selected screen — the neighbour must fade the
  facing edge by the same width with the same curve — and names an overlap nobody fades, a join
  fading on one side only, widths that differ, curves that differ and zones too wide to leave a
  picture, each with what to do. The Projection blend pattern draws a grid too (**Rows across**,
  **Overlap across**): zones both ways, P1…Pn hue-coded, ramps and the grey check through the
  corners; presets for 4× and 2×2 WUXGA.
- **PowerPoint decks through LibreOffice Impress — one smooth workflow** — the Deck on the Media
  page takes a PowerPoint (`.pptx`, `.ppt`, `.ppsx`…), a Keynote or an Impress file as it is:
  Patterns runs LibreOffice Impress headless in the background — its own profile, never a window,
  never a wait on one the operator has open — and keeps the PDF it makes under `decks/` beside
  the show, named for the file's size and last write, so an unchanged deck never converts twice
  and an edited one converts again by itself (RELOAD forces it). The card reads *Converting…*
  until the pages arrive, then the deck is the click-through like any PDF: on air through a cue,
  a look or by hand, the clicker turns its pages, the last page hands back to the cue stack.
  LibreOffice is found beside Patterns (a portable copy on the show drive), in Program Files or
  on the PATH, or at the path you give when it is nowhere else; without it the card says so and
  what to do. STATE's `deck` row gains `kind` and `converting`; the pickers and the Library take
  the files as decks. Animations inside a PowerPoint are not kept — a slide is a page — and a
  missing font is substituted, so check the pages once before the show.
- **PDF decks: a presentation full frame, the click-through, the cue stack resumes** — Media
  page → **Deck**: a PDF (export it from PowerPoint, Keynote or Slides) shown a page at a time,
  full frame at its own shape — a 4:3 deck gets bars, never a stretch — rendered by PDFium as
  sharp as the largest screen needs, on every output, NDI send and the stream, with the pages
  either side rendered ahead so a turn is instant. Once the deck is on air (through a cue, a
  look, or by hand) it is the click-through: the clicker's keys, NEXT / BACK on the phone,
  Companion's presenter keys and `NEXT` on the wire turn its pages, and at the last page the
  next click GOes the standby cue, so the cue stack resumes — one smooth workflow. A cue can
  turn pages (**Deck — next page / go to page**), the wire has `DECK NEXT / PREV / FIRST / LAST /
  PAGE n`, OSC `/patterns/deck/…`, Companion 1.11.0 a deck category with a last-page feedback,
  STATE a `deck` row; the start page and the end-of-deck GO are per pattern, PDFs join the
  library as decks, and the area of interest, flips and turns apply to a page like any picture.
- **Web pages that answer: YouTube, Google Slides, PowerPoint, keys, clicks and cues** — every
  click, drag, wheel step and key the desk sends now reaches the page the way a browser
  automation session sends them, as trusted events whichever window has the focus, so links,
  players, sliders and frames from other sites all respond. **KEYS → PAGE** (a chip on the
  PREVIEW pane and under PAGE CONTROLS, or Ctrl+Alt+K) hands a page the whole keyboard: F5
  starts a PowerPoint, the arrows move a deck, a sign-in takes a password. Paste a YouTube,
  Vimeo or Google Slides link and **FULL FRAME** shows the player or the deck alone (autoplay,
  no controls, no related videos; a published deck's embed with the bar hidden; your own deck in
  present mode); the page's own actions — NEXT, PREVIOUS, PRESENT, BLACK, PLAY, MUTE, RESTART —
  sit under PAGE CONTROLS, on the phone's SHOW tab, in Companion 1.10.0 and in cues (**Web page
  — key or action**: `page: present` on the cue that starts the deck, `page: next` on every
  slide), on the wire (`WEB NEXT`, `WEB KEY Ctrl+Shift+F5`, `WEB CLICK 50 50`, `WEB TYPE …`) and
  over OSC. Nothing opens outside Patterns any more: the browser-window buttons are gone and a
  link that wants a new window opens in the same page.
- **The area of interest: crop, mirror and turn any input** — Teams, Google Slides and a shared
  screen rarely fill the frame: a title bar, a participants strip, a chat panel or a toolbar ride
  along. Media page → AREA OF INTEREST: press **PICK ON PREVIEW**, drag a box on the PREVIEW pane,
  and that part of the picture is the picture — on every output, NDI send and the stream, cut
  before the fit — for a still, a video, an NDI feed, a capture card, a web page and every
  playlist item. A second box refines the first, four sliders cut up to 90 % from any side (never
  keeping less than a twentieth), presets take a top bar, a side panel, a bottom strip or keep the
  centre 80 %, and **Mirror**, **Upside down** and **Turn** (90°, 180°, 270°) put a portrait feed
  or a camera on its side the right way up. Saved with the pattern, so looks and cues carry it; a
  web page keeps its clicks through the crop while it is upright.
- **One picture's sound at the desk, not all of them at once** — a show can have a clip on the
  program, another on a confidence screen's own picture and a third loaded into the preview, and
  every one has a soundtrack. Nothing was choosing between them, so they all played: a mix nobody
  asked for over the top of whatever the operator was trying to hear — and with EDIT SAFE open it
  happened on **every** show, since the picture being built is nearly always mounted beside the one
  on air. Now the desk monitors one at a time (**Audio → WHAT THE DESK IS LISTENING TO**), and
  unless you say otherwise it is the **program** — the sound the room is hearing is the sound you
  are checking against. The others: the **preview**, to hear the next clip before it lands; **one
  output on its own**, so a confidence screen doing its own thing can be listened to without
  taking the program down; or **nothing**. What it does not do matters as much: it changes nothing
  on any screen, any NDI send or the stream — a clip silent at the desk is still heard in the room
  — it is not part of what a picture looks like, so choosing one never crossfades anything, and a
  look or a cue never carries it. Picking an output that simply follows the show gives you the
  show, not silence. The audio playlist, VOGs and stingers are the show's own sound and always play.
- **The monitor walls: two of them, arranged how you want, on any output** — a multiview used to be
  a *pattern type*, which put it in the one place it could not usefully be: to build one you had to
  make it the picture of whatever you were editing, so the program could not be a pattern **and** a
  wall at the same time, and a wall on a second screen was a second set of tiles to keep level by
  hand. Now they are the show's: **SETUP → Multiview** holds up to two, each with its own
  arrangement and tiles, and each drawn on however many outputs you tick — a spare display, any NDI
  sender, the stream. Ticking a sender or the stream points that feed at its own picture as part of
  the same tick, so nothing is left to find on another page; unticking hands the output straight
  back to the program, and the line beside each one says what it is doing instead. **Layouts**:
  program and preview large with the rest in a strip beneath (the default — a wall is read at a
  glance from across a room, and what is on air and what is next are the two pictures that decide
  anything), one large with the rest beneath, one large down the left with the rest in a column on
  the right, or an even grid. The large tiles are simply the first in the list, so dragging a tile
  to the top with its grip (⠿) is how you choose what the wall watches. A show made before this
  keeps exactly what it had — its tiles become a wall the show holds, laid out as the grid the old
  build drew, and every target that was showing them points at it. `/multiview` on the phone shows
  the first wall, `?n=2` the second.
- **The multiview's tally: program, the next TAKE, and which screen** — every tile now says what
  state its target is in and what it is. A red border and **PGM** while it is live to the audience
  (**OFF**, **OUTPUTS OFF** or **BLACK** when it is not, **FROZEN** while the outputs hold);
  **NEXT** in green when the next TAKE changes it and **HELD** when the wall un-armed it (both only
  while EDIT SAFE is open); **LOCKED**, **OWN** for a screen on its own picture, **REP** for a
  repeater; the Preview tile green with **PVW**. Under each tile its name and which output it is —
  *SCREEN 2 · 1920×1080*, *CANVAS A · 3840×1080 · 2 SCREENS*, *NDI SEND 4*, *STREAM* — while
  the PROGRAM tile lists the screens the program is on (*ON 1 · 2 · A*) and the PREVIEW tile the
  ones the next TAKE reaches (*NEXT TAKE → A*). The same words on a screen's own multiview, an
  NDI send of it and `/multiview`; the phone's SCREENS tab reads NEXT / HELD / OWN too, and STATE
  carries `editSafe` and each screen's `armed` and `own`.
- **Lower thirds triaged: the sign-off flow, UPDATE ON AIR, the show's default** — the bug: with
  EDIT SAFE open the audience sees a copy of a design, a SHOW again reused the stale copy (an edit
  reached the designer and never the outputs) and a picture TAKE dropped the lower third on air.
  Now **AIR** refreshes the copy every time, a TAKE carries the lower third across untouched, and an
  edit made while a design is on air lights **EDITED** with **UPDATE ON AIR** to push it in place. The
  new flow: **PVW** puts a design (and a person) into the preview — the PREVIEW pane, the
  multiview's Preview tile and REVIEW — where a caller or director signs it off, and **TAKE TO AIR**
  puts it on; the show's **★ default** design is where PERSON n and the PEOPLE chips go when none is
  on air. On the page, the Show panel (PVW FIRST), the phone, the wire (`LT PREVIEW`, `LT TAKE`,
  `LT UPDATE`), OSC, cues and Companion 1.9.0.
- **Freeze, the timed fade, the previous look, earlier versions** — the easy things the big
  rigs have, built after a look at vMix, Event Master, Analog Way, QLab, Millumin, Resolume,
  Disguise and Pixera (`docs/PRO-FEATURES.md`): **FREEZE** holds every output's frame while the
  desk keeps moving; **FADE TO BLACK / FADE UP** are a blackout with a fade of the seconds you
  type; **LOOK BACK** puts the look before the current one back on air (again to swap); and the
  Machine page lists **earlier versions of the show** (the previous save and up to twenty timed
  copies) with RESTORE. All on the Show panel, the phone remote, the wire, OSC and Companion 1.8.0.
- **Walkthroughs by role** — the Help page opens with **who you are** (show caller,
  technician, operator, programmer, graphics & video) and **what you are about to do** (run the
  day from a cue sheet, bring the rig up at the venue, blend two projectors, a second machine,
  build a look safely, VOGs and stingers, pre-programme in PREP, control surfaces, a people
  library, web pages and layers, the feeds): each scenario is its steps in order, each on the
  page it happens on — **GO** opens that page, a tick marks a step by hand, and a step the show
  already has (screens adopted, outputs on, looks saved, the stack armed…) ticks itself as you
  do the work, so the list is a live checklist rather than a leaflet.
- **Bezels and gaps: the wall the content spans** — tell a screen where its wall has no
  pixels (the Screens page's **Wall gaps**: a bezel width for a canvas of joined displays, or a
  list of strips — before which raster pixel, how wide — for one output that packs LED pillars
  or a wall controller's displays; **Set from grid** for an even wall). Every pattern, video,
  web page, layer, overlay and lower third is laid out across the strips and the output leaves
  them out, so a line across the wall is straight in the room and a thing that moves across a
  gap goes behind it. The LED wall and video wall patterns put their tiles on the real panels
  and draw the gaps black with their width; the PGM/PVW panes, the multiview tiles and the
  phone's thumbnail shade the strips; NDI senders carry the whole surface.
- **Review on the multiview** — a multiview tile can show the **Preview** (what the desk is
  building in EDIT SAFE) beside the program, and **REVIEW** — on the Show panel, the Pattern
  page, the phone remote, `REVIEW ON / OFF` on the wire, `/patterns/review` over OSC and a
  Companion key — puts the preview over every multiview full-frame with a REVIEW chip until you
  switch it off: the next look checked on the monitor wall (a screen's own multiview, an NDI send
  of it, `/multiview`) before the TAKE, while the audience's screens never change.
- **The phone remote, redesigned** — one page with a menu across SHOW · CUES · LOOKS · SCREENS ·
  AUDIO · LOWER THIRDS · SETUP (the tab you were on is remembered), a sticky header that names
  what is on air with the BLACKOUT / HOLD / ARMED / MUSIC / STING HOLD / DUCK chips and a
  connection dot, thumb-sized buttons, and the caller's long-poll so it changes the moment the
  show does. New on it: STOP ALL (press twice), ARM, a padlock per screen, the day's timing line
  and the standby cue's plan, the lower thirds with their people, tone, and a SETUP tab with the
  health line, the machine's numbers, the stream, the main machine's beacon and the links to the
  caller's page and the multiview. Every command is the TCP line it always was.
- **The watchdog reviewed, and a beacon for a second machine** — the supervisor's heartbeat
  proves the desk answers, so the two things it could not see now show on the Machine page's
  advice: outputs open but drawing nothing for 30 s while moving content is up, and a stream that
  switched itself off after an encoder error. A supervisor that stands down (a crash loop, an app
  it could not start) leaves a note the next start puts on the health line. Tick **Send a
  heartbeat beacon** and the main machine sends one small UDP datagram a second — who it is,
  what is on air, the cue on standby, the health line — to the backup or the whole network;
  tick **Listen** on the backup and its health line reads *Main machine seen 1 s ago: live ·
  Walk-in · standby 01.020*, *MAIN MACHINE SILENT for 6 s — take over?* when the main goes quiet,
  or *its watchdog gave up* when the supervisor sent its last word; the super-check shows the
  same as a row. Taking over stays your call, on purpose.
- **OSC in and out** — tick OSC in on the Remote page and Patterns listens on UDP (9698) for
  QLab, TouchOSC, a lighting desk or Companion's OSC: every address starts `/patterns/` and means
  exactly the TCP line it maps to (`/patterns/look 3`, `/patterns/cue/go`, `/patterns/lowerthird
  Neon "Jane Doe"`, `/patterns/screen/2 1` …), a refused command answers `/patterns/error` to the
  sender, and with a feedback host set every change sends one bundle of `/patterns/state/…`
  messages — live, blackout, the program, the cue on standby, the lower third and its person, each
  screen's switch and lock — so a fader page or a tally reads the show.
- **The lower-thirds library** — every person the show will name kept ready on the Lower thirds
  page (LIBRARY: a name, a role, a company, a photo, a note), imported from a speaker list in Excel
  or a CSV (Name or First / Last name, Role, Company, Photo, Note — by header; Append updates a name
  that is already there; Template and Export CSV) and recalled into any design in one press: USE
  fills the design's fields and its picture element, SHOW puts it on air; a cue names the design and
  the person, and a person who is not in the library is refused before the show — a wrong name never
  reaches the screen; PERSON n on the wire, the phone's PEOPLE buttons, the Show panel's chips and
  Companion keys put the next speaker into the lower third on air.
- **The show caller's home** — a running order comes in from Excel or a CSV in one go (IMPORT SHEET
  on the Cues page: numbers, names, tracks, start times, lengths, follow delays, break / lunch / end
  marks, confirm, a look by name, an action with its target, notes — by header, in any order;
  Template CSV to copy from, Export CSV back out); every cue gets its look in one pick and an action
  in one press, a planned start and length, a mark and an auto-follow; the Run surface reads
  ON TIME / 3 MIN LATE / 2 MIN EARLY from the last GO against the plan, says when the next break,
  lunch and the end are expected (≥ once a cue has overrun), shows each row's planned and expected
  start, and lets the caller push or pull the rest of the day a minute at a time, RESUME NOW, or
  CATCH UP before the next mark; a cue with a follow fires the next by itself through the same GO
  gate — HOLD, moving standby, disarming or STOP FOLLOW stops it.
- **Web pages inside the engine, driven from the desk** — a page is a pattern (Media → Web page) or a
  layer over any pattern, rendered through WebView2 (the browser engine Windows 10 and 11 ship) so it
  fits the canvas, joins spans and trims, reaches NDI and the stream, crossfades and rides with looks
  and cues; page size and zoom per use; click, drag and scroll it on the PREVIEW pane (Alt-drag moves
  a web layer's box), type into it with PAGE CONTROLS; the desk's pointer and a click ripple are drawn
  on the page for the room, or hidden per page; four pages at once, one browser per address.
- **A web page with nothing round it** — a YouTube link goes on as the player alone, and **CLEAN**
  takes off what the address cannot: the media bar, the channel watermark, the pause panel, the
  end-screen cards and the big centre play button. Vimeo loses its control bar and outro, Google
  Slides its viewer toolbar, and any other page its margin, scrollbar and pointer. It is a style
  sheet put into the page before the page's own scripts run, so a player that rebuilds its controls
  never gets to draw them; it goes on and off live, with no reload. It is one picture — what the
  room sees the operator sees — so a clean page is driven from the desk instead: PLAY, PAUSE, MUTE,
  RESTART and ±10 s reach the player's own API from PAGE CONTROLS, a cue, the phone or a Stream
  Deck, and answer whether or not its buttons are drawn. **Treat it as** names the service outright
  when the address does not say — a short link, a corporate proxy, an embed on the client's own
  hostname — and the same row adds the next service: its name, its address rewrite, its actions,
  its strip.
- **The stream, where you can see it and where you can press it** — a light at the foot of the rail
  on **every** page: OFF, UP…, LIVE, SLOW, FAULT, with the uptime under it; click it for the Stream
  page, which gains a health block with the frames encoded, the rate going in against the rate
  asked for, the destinations and the restarts. **SLOW** is the one that matters: the stream is up
  and taking frames at under four-fifths of the rate asked for, so the wall looks perfect while the
  online audience watches a slideshow — the failure the desk's own windows cannot show you. One
  reading, computed once a second and shown by the rail, the page, the Show panel, the phone, the
  wire and an OSC feedback bundle, so no two of them can disagree. And a command wherever an
  operator acts: the **SHOW CONTROLS** drawer, the phone's SHOW tab, a cue — and a **look**, which
  is what gives an F-key, the presenter's clicker list and a permanent install's schedule a stream
  command, since all three recall a look and none of them carries an action list. A look loaded into
  the preview never touches the stream.
- **A restart comes back to the show, not to the edit** — whatever restarted the desk, the wall
  comes back showing what the audience was watching, and the preview comes back holding the look
  that was being built. The recovery record beside the settings carries the **program itself** —
  the pattern, the brand kit, the lower third on air and where it is in its life, the NDI senders,
  every locked screen's own picture and any screen faded to black on its own — together with
  whether the desk was split (EDIT SAFE open) at the time, so the two halves go back where they
  were. It follows the air by construction: the desk watches the frozen program, so arming EDIT
  SAFE, a TAKE, a CUT, a per-screen SEND, a cue, a stinger and a lower third all move the record
  without any of them having to know it exists. The Machine page's **RESTART** now leaves exactly
  what a crash leaves — including the caller's standby, last GO and history — and a desk handing
  its screens to an incoming desk leaves the record for it to read instead of deleting it. The
  stream is the one thing a restart never puts back by itself: it pushes somewhere public, so the
  desk says it was live and leaves the press to the operator.
- **A picture change reaches every screen on the press** — a sink is asked for frames *before* the
  frame that starts a crossfade, not after it. The redraw cadence is decided on the desk's thread
  and the frame is drawn on the compositor's, so a sink used to arm a fade and then never draw
  another frame — and the first frame of a fade is the outgoing picture at full opacity. That is
  why picking a Pattern Type left every switcher miniature and both panes on the old picture until
  some unrelated edit, and why a look recall between two still pictures could leave an output on
  the outgoing one. One memoised comparison on a sink that is not already fading; nothing else on
  the render path moved.
- **Reactive scenes — a walk-in that moves with the room** — six curated sound-reactive pictures as
  a pattern of their own (BUILD → **Reactive**): *Ambient Plasma*, *Subtle Tunnel*, *Brand
  Kaleidoscope*, *Corporate Pulse*, *Vortex* and *Star Warp*. Each is one full-canvas pass on the
  graphics card for the outputs, the preview and the desk's monitors, and **the same picture drawn
  on the CPU** for NDI, the stream and the thumbnails — held together by a test, so what the client
  streams is what the wall shows. They take the show's **brand kit** by default, they surge with the
  **stings** exactly as the particles and the fractals do, and they lean into the sound without ever
  depending on it: with no capture at all — before the music starts, or on a machine that cannot
  listen — every scene still runs on the show clock and looks finished. What they listen to is the
  operator's: **this computer's own sound, or any input Windows captures from** — a microphone, a
  line, a USB capture card, one channel of an interface — picked on the page beside the response,
  the same picker the Fractals page has. **Whole-screen flashes are
  limited to three a second on every screen**, in the engine rather than in the presets: that is the
  rate broadcast and web guidance draw the line at, a beat at 128 BPM taken double-time is faster
  than it, and a flash that comes too soon is dropped rather than shortened. The limit covers the
  stings that shipped before the scenes did, and nothing in the desk turns it off.
- **A drop is told from the nearest anchor, and a place in pixels** — an overlay's place is an
  anchor and a nudge from it as a share of the canvas, which is what keeps it relative. A drag used
  to leave the anchor behind: a chip dragged to the bottom-right from the middle read **+42 / +43**,
  which is a displacement and not a place — it landed hundreds of pixels short of that corner on a
  wall of another shape, and slid as the box's own size changed. Now a **drop re-anchors**: nothing
  moves, the box is exactly where you let go, but the anchor and the **Nudge X / Y** sliders come
  back to counting from the corner, edge or centre the element is actually near, so *bottom-right,
  nudge nothing* stays in the corner on a 32:9 wall and at any size. Under the sliders, **Place X /
  Y (px)** reads where the box's top-left really is on the canvas the PREVIEW pane is showing (with
  the box's size and the canvas's beside it) and **types back** — a rig sheet's numbers go straight
  in. And **Position means that position**: picking one from the dropdown puts the element there
  outright, taking the nudge a drag or the sliders had built up with it, because *bottom right*
  means the bottom right of the screen and not the bottom right plus wherever it was last dragged.
  **RESET TO POSITION** beside the pixel fields does the same without changing the position —
  the way out of a drag that went wrong. The drag, the picker, the sliders and the pixels are four
  ways of saying one thing, and all four move together.
- **One key for the countdown, like every other overlay** — the clock, the message, the logo, the
  PiP and the weather chip have always answered a bare `CLOCK` / `MESSAGE` / `LOGO` / `PIP` /
  `WEATHER` by flipping. The countdown was the one that did not: a bare `COUNTDOWN` was refused, so
  a phone key, a Stream Deck key or an OSC address could start it and stop it but never *toggle* it.
  Now `COUNTDOWN` (and `COUNTDOWN TOGGLE`, `TIMER`, `/patterns/countdown`, `/patterns/countdown/toggle`)
  turns it on as the desk has it set up — the time of day it points at, else its duration armed from
  now — and off again. The phone's OVERLAYS tab gains a **COUNTDOWN** key that lights while it is on,
  Companion 2.7.0's countdown action gains a **Toggle** mode and its reading key uses it, and a cue
  can carry it (`Countdown on / off`).
- **Two layers on every target, and everything drags** — each screen or canvas's pattern carries
  **two layers** (the Layers page): any picture — a still, a clip, an NDI feed, a capture device, or
  another screen's picture — in a box with a fit, a crop from all four sides, rounded corners, a
  border and an opacity, drawn over the pattern and under the overlays on every span, NDI send and
  the stream, saved in looks and cues. Take hold of a layer, the logo, the clock, the countdown,
  the message or the PiP inset **on the PREVIEW pane and drag it** where it should be (the pane
  shows an empty layer's box so it can be placed before its picture exists; a drag never starts a
  crossfade), and drag a lower third's elements on the designer's stage.
- **Screens with a job** — give a screen a **role** on the Screens page: Main, **Confidence** (a
  stage monitor), **Info** (a foyer screen) or **Repeater**. Confidence and Info screens are
  **locked** as you choose them: they keep exactly the picture they have through every look, cue,
  clicker step, TAKE ALL and stinger, so a cue progression changes the main screens and leaves
  them alone — until you SEND the preview to their tile, OWN and edit, or unlock. LOCK sits on
  every wall tile (amber while a send is being built), a cue can lock or unlock a screen mid-show,
  and the remote (`LOCK n ON/OFF`) and Companion 1.5.0 do the same. **Mirror of** makes a screen
  a repeater of another screen or joined canvas — the same picture wherever it goes. **SEND** on a
  tile puts the preview on that one target alone.
- **A clearer desk** — the explanations that sat on every page now live behind **? TIPS** on the
  page strip: press it for the current page's tips under their headings, tick *Show hints on the
  pages* to have them inline again (the show remembers). The room goes to the controls, the type
  is a step larger, and the header says which is the **MODE** (PREP · SHOW — what may leave the
  machine) and which the **LAYOUT** (RUN — the caller's surface over either). PREP now holds the
  stream closed as well as the outputs and the NDI sends.
- **A graphical screen overview** — every detected screen is a live tile showing exactly what
  it outputs. Drag screens flush together and they join into one spanned canvas (seam-tested,
  viewport-exact); drag them apart and they split again. Any screen can be enabled/disabled or
  given its own pattern — and your main screen stays off by default when other outputs exist,
  so GO never covers the controls.
- **LED wall mode** — describe the wall the way LED techs do: panel pixel size (any custom size,
  presets for 64–256 px), then either columns × rows or a target canvas (edge panels go partial,
  like the real thing). Tile borders, row-column / linear / serpentine data-run numbering,
  pixel grid, center cross, dimension readouts. **Irregular walls too**: switch to the map
  editor and drag mixed-size panels, offset blocks and gaps into place (they snap flush) —
  or seed the map from the grid and edit the exceptions.
- **Video wall mode** — standard-resolution display elements (landscape or portrait), any grid,
  bezel-loss hatching, per-element numbering, diagonals and center circles.
- **Projection blend mode** — 2–12 projectors in a row or column, native resolution and overlap
  in pixels, continuous alignment grid through the zones, hue-coded projector frames, blend-curve
  ramps (linear / cosine / S-curve / gamma 2.2), zone markers with centerlines, and a flat 50%
  grey double-stack check.
- **Frame rate, display modes, capture formats** — a **master frame rate** for the show (every
  output paces to it on one clock, evenly — a 30 fps show on 60 Hz displays draws every other
  refresh — with a per-screen override; an NDI sender set to *master* sends at it; the stream
  can follow it); a **display-mode picker** per screen that lists what the display's EDID offers
  and applies it through Windows with KEEP / REVERT and a 15 s safety revert, everything
  programmed for the screen following it across the change; and a **Format** picker per capture
  device listing the sizes and rates the card's driver advertises, so the card opens in the
  mode the source sends.
- **Edge blend on the real outputs** — tick *Automatic* on two projectors and overlap them on the
  Screens page: the overlap becomes a joined canvas both draw, each output fading its shared
  edge to black along the chosen curve with a blend gamma you tune until the grey check reads
  flat. Manual widths per edge for a rig measured by hand; a keystoned projector's fade follows
  its keystone; NDI, monitors and the preview never fade.
- **A proper pattern library** — alignment grids, SMPTE RP 219-style / EBU / 75% / 100% bars
  (legal or full range), grey & RGB ramps, banding steps, focus charts (Siemens star, line pairs,
  type), geometry & safe areas, flat fields, 1-px checkerboards — parametric at any resolution up
  to 4K DCI and beyond, with a thumbnail preset gallery plus your own saved presets. The
  **Library** files everything under section chips — Patterns, Images, Videos, Audio, Particles,
  Presets, Brand kits — with a search box, a thumbnail per tile (two files of one name in two
  folders each get theirs) and a ✕ to take a file out of the library again.
- **Motion diagnostics** — moving bar with a px-per-frame judder mode, bouncing FPS box,
  frame-flash drop detector, animated zone plate, scrolling grid.
- **Particle mini-studio** — scenes in packs: Classic (snow, confetti, starfield, rain, bokeh,
  embers, fireflies), Awards (gold dust, champagne, red-carpet sparkle, ticker tape), Modern,
  Nature, Moods, Starcloth, Night sky and Feel-good (party confetti, bubbles, fireworks,
  sparkler, sunshine), plus your own saved scenes under Custom; emitter, physics, shapes
  (including your logo as a sprite), brand palettes, additive glow — thousands of particles,
  one draw call. A scene that drifts sideways keeps the whole screen covered: the upwind edge
  takes its share of the births, so wind never sweeps one side bare.
- **Fractals, sound-reactive** — a living picture from pure maths as a pattern of its own, with
  a studio page of its own (BUILD → Fractals): Mandelbrot, Julia, Burning ship, Newton and a
  flowing domain warp, with scenes by family to start from (Seahorse valley, Elephant valley,
  Douady's rabbit, the Siegel disk, The armada, Newton coast, lava, aurora…), zoom, centre,
  detail, motion and a palette. Outputs, the preview and the monitors draw it on the graphics card at full size;
  NDI and thumbnails draw the same view on the CPU at a modest size. It can listen — to this
  computer's own sound or to an input (a microphone, a line, a USB capture card, one channel of
  an interface) — and breathe with it: the level pulses the zoom, the lows drift the colours, the
  highs brighten. Windows only for the listening; the picture works everywhere.
- **An input the rig can be patched after** — *Default input* follows whatever this machine's own
  input is, so a show file travels between a desk with an interface and a laptop with a built-in
  microphone. A named device that is not plugged in yet is looked for again every few seconds
  rather than reported missing once and forgotten, one unplugged mid-show is picked up when it
  comes back, and a USB box moved to another socket — which Windows renames from
  `(2- USB Capture)` to `(3- USB Capture)` — is matched as the same device. Sixteen-bit,
  twenty-four-bit and float capture formats are all read, which is what interfaces and capture
  cards actually hand over.
- **Time, date & countdowns** — clock overlay (12/24 h, seconds, date) and a show countdown to a
  time of day or a duration (“BACK FROM LUNCH AT…”, “SHOW STARTS IN…”), with hold / flash /
  message endings and an optional progress bar. Overlays composite over *any* pattern.
- **Corporate branding** — brand kit (primary/secondary/accent/background/text + logo) that
  drives accents, checkerboards, colour cycles, particles and overlay text. Measurement lines
  stay neutral so patterns remain accurate. Kits save/load per client.
- **User media & playlists** — your images (PNG/JPEG/BMP/WebP) with fit modes; your videos
  *and audio files* (MP3/WAV/FLAC…) via libVLC (bundled in the *full* download, optional
  otherwise), with live mute and volume that never restart the media. Everything you load
  lands in the Library under *Images*, *Videos* or *Audio* for one-click recall — and the **playlist** source
  cycles files and whole folders (rescanned live): drag rows to re-order or use seeded
  shuffle, images on a dwell timer, videos/audio to their end, a ▶ NOW marker on the
  playing row, per-item overrides and daily *play at HH:mm for N seconds* scheduling.
  Media renders through the engine, so it reaches spans and NDI too.
- **Live inputs** — receive an **NDI® feed** off the network (sources auto-discovered), or an
  **HDMI/SDI capture device** (Elgato, Magewell, Blackmagic WDM, AVerMedia, webcams — anything
  DirectShow) — both composite through the engine like any pattern, so a camera or remote feed
  reaches spans, trims and NDI outputs.
- **Web pages inside the engine** — a session schedule, a dashboard, a YouTube video, a Google
  Slides deck or a PowerPoint on Office 365 as the pattern or a layer: rendered through WebView2
  inside Patterns, so it joins spans and trims, reaches NDI and the stream, rides with looks and
  cues, and takes clicks, keys and typing from the desk, the phone, a Stream Deck and cues; a
  saved-pages list for quick recall. Nothing opens outside Patterns.
- **Looks & cues** — save the entire content state (pattern, per-screen patterns, overlays,
  countdown, blackout) as a named look on `F1`–`F12`, then run the evening from a simple
  schedule: *Walk-in 18:00 · Countdown 18:45 · Blackout 19:00*. Screen arrangement and NDI
  infrastructure deliberately stay put when a look recalls.
- **Portrait & mismatched house displays** — per-screen output rotation (90°/180°/270°,
  content stays upright, the overview shows the rotated footprint) plus per-screen
  brightness / gamma / RGB trims as exact 256-entry LUTs — match that one warm hotel plasma
  without touching the rest of the rig.
- **Soundcheck audio** — a click-free tone generator (20 Hz–20 kHz, dBFS level) and a channel
  ident (one pip LEFT, two pips RIGHT) with a matching on-screen indicator on every output,
  so front-of-house can confirm routing at a glance. Never auto-starts with the app.
- **Ticker data feeds** — point the message ticker at an RSS/Atom feed, a CSV/text file of
  lines, or an ICS calendar (next 24 h as `HH:mm Event`) — session schedules and wayfinding
  straight onto the screens, refreshed on your interval. The ticker loops seamlessly at any
  speed and text length, every screen, span half and NDI sender shows the same train, and the
  message can sit on a soft fade (the classic lower third), a solid bar, a chip or nothing.
- **System fonts** — overlay text (clock, countdown, messages, chips) in any font installed
  on the machine, with the bundled Inter as a travelling fallback.
- **NDI® outputs** — any number of senders, each with its own name, resolution, frame rate,
  source (program or a specific screen) and bit depth — including **10-bit P216** with a
  BT.709 limited-range pipeline for serious ramp/banding checks. Feature-detected at runtime
  (nothing crashes without the NDI runtime).
- **Five groups, two levels** — the rail holds **SHOW · PLAN · BUILD · SETUP · ADMIN**, grouped
  by who is at the desk and when, and the page strip across the top shows the pages of the
  current group, so eighteen pages never crowd a laptop screen. **MODE** in the header is
  PREP or SHOW: PREP holds the outputs, the NDI sends and the stream closed while you
  pre-program (every editor, look and cue still works), SHOW lets them open; **LAYOUT** is
  RUN, the caller's surface over either mode — and leaving RUN is refused while the stack is
  armed. **? TIPS** on the page strip holds the current page's explanations.
  Under the wall, a **SHOW CONTROLS** drawer holds exactly four air-targeted controls —
  message, clock, countdown, audio volume — each behind an explicit **SEND** that goes to
  air whether or not the sandbox is open and is journaled as a desk action; the next look
  recall replaces it. A cue can do the same four things.
- **A Show panel** — once the rig is built, run the evening beside the switcher: every look
  as a big button, the clicker list, VOGs and stingers, the audio track and live status; the outputs
  transport lives in the header. The other pages stay out of the way.
- **Remote control** — a **web remote** for any phone or tablet on the network: one page with a
  menu across **SHOW · CUES · LOOKS · SCREENS · AUDIO · LOWER THIRDS · SETUP**, a sticky header
  that names what is on air with its chips and a connection dot, thumb-sized buttons, and the
  same long-poll the caller's page uses so it changes the moment the show does (the tab you were
  on is remembered); a one-command-per-line **TCP protocol** with live state feedback, and a ready-made
  **Bitfocus Companion module** (`integrations/companion-module-patterns/`) with presets for
  transport, looks `F1`–`F12`, individual screens and screen groups, presenter steps and
  audio — plus feedbacks and variables for Stream Deck keys. See [`docs/REMOTE.md`](docs/REMOTE.md).
- **A cue stack** — the Cues page (PLAN) holds two lists of the same kind: the **cue stack** a show
  caller runs in order, and the **clicker list** a speaker steps through. A cue is one or
  more typed actions run in order — apply a look (with its own cut or fade), play or stop the
  audio track, fire a VOG or stinger, switch a playlist part, start or stop the stream, blackout,
  screens and canvases on or off, start a countdown, a message or the clock, and hand the
  room to the other list. Numbers are labels (`03.020`, auto-assigned, editable, never used
  to sort); every reference is **checked as you build** by simulating the list in order, so a
  part named by cue 12 is checked against the playlist cue 9 puts on air. A cue that cannot
  run is marked **broken** with the reason, GO refuses it, and the rest of the list still
  runs — one deleted look never stops a show. A cue stops at its first failing action and
  says "failed at action 2 of 3"; blackout stays as it was unless the cue switches it.
- **Run mode** — press **RUN** in the header and the window becomes the show caller's
  surface: a **LIVE strip** that names what is on air (a look, `03.020 Five-minute call`,
  `VOG: …`, `STING: …`, `STING HOLD: …`, `PART: …`, or `MODIFIED — last …` after a send), the wall beside the **cue
  stack** with the last, standby and next cues marked, and a transport row — **ARM**,
  standby ▲ ▼, a big green **GO**, **HOLD**, **BLACKOUT** and a small guarded **STOP ALL**.
  Every GO passes one gate in order, whatever pressed it: armed, not held, blackout off,
  nothing executing, a cue on standby, the standby the sender saw still current, 300 ms
  since the last GO, confirmation satisfied — and a refused GO says why. A cue that asks
  for confirmation turns GO into `CONFIRM 03.020` for four seconds. While armed, the daily
  schedule, playlist part start times and plain F-keys wait (the desk's look buttons and a
  remote's LOOK stay live), so only the caller moves the picture. **Enter** is GO, ↑ ↓ move
  standby, Esc cancels a confirm and twice is STOP ALL (audio, break music, VOGs, stingers, tone —
  never the outputs, blackout or the stream). A watchdog relaunch reopens Run **disarmed** at the next
  cue with a banner, and fires nothing; the history reads from the journal. **POP OUT** puts
  the Run surface on the caller's own monitor as a second window with its own keys; the
  `/run` page on the web-remote address gives a tablet the same LIVE strip, standby, next
  six and GO / HOLD; Companion module 1.1.0 adds GO (green armed, amber on hold, red when
  the last cue failed), standby, HOLD, ARM and STOP ALL keys with feedbacks and variables;
  the protocol gains `CUE GO / STANDBY / HOLD / ARM / LIST`, `STOPALL` and `HELLO <name>`.
- **Presenter click-through** — the clicker list: hand the presenter a USB clicker,
  `Page Down` advances, `Page Up` goes back (exactly what presentation remotes send), and
  each click fires the next cue — a look, a message, a VOG, anything a cue can do. It
  answers only while armed (always off when the app opens); the remote and Companion
  `NEXT`/`PREV` drive the same list. Older shows' presenter steps move into it on load.
- **Transitions — six ways one picture becomes the next** — content changes glide instead of cut
  (100 ms–3 s, engine-level, so they work on spans, rotated outputs and NDI alike — even between
  videos and live inputs). **Dissolve** is what a desk has always done; **Dip** goes out through a
  colour and back with the change made underneath it; **Wipe** travels a soft edge across (which
  way, and how soft); **Push** brings the new picture in from the far side as the old one leaves;
  **Brand stinger** sweeps the show's own kit over the cut — two bars in the primary and secondary,
  the background behind them, the logo at the peak — so a stinger needs no clip to prepare or lose;
  **Reactive** uses a reactive scene's own picture as the matte, so a plasma dissolves in clouds and
  a vortex spirals in. Not one of them needs a graphics card: they are plain canvas work, so the
  wall, the preview, NDI, the stream and a thumbnail all get the same change, and the reactive matte
  is drawn once at 256 px and scaled up, so a 4K wall and a thumbnail cost the same. A bright dip
  goes past the same three-a-second flash limit a strobing sting does — too soon after the last one
  and that change dissolves instead. One recall can carry its own arrival (`wipe left 600`, `dip`,
  `stinger`, `reactive vortex 1200`) for that recall alone, even on a show whose crossfades are off.
  Turn them off and everything cuts clean like a test-pattern box should.
- **Picture-in-picture** — a second live input (another NDI feed or a capture device) as a
  corner overlay over the program on every output: anchor, size, opacity and border are live,
  and the feed **crops from any side** (a slate, a border, a black bar) with the inset taking
  the cropped shape. Confidence-monitor the camera while the walls show content.
- **Independent audio track** — play a music/VO file to **any set of audio outputs**: the
  **computer's own output** (the jack or interface feeding the venue sound system — a
  pinned, explicit choice), HDMI screen audio, a Dante/USB interface — several at once,
  with loop and live volume, regardless of what's on screen. Video sound and the tone
  generator stay separate.
- **Break music from Spotify** — your playlists, albums and songs as one-press buttons for
  the room between the show's own content: walk-in, the interval, the wrap. Patterns
  *drives* Spotify rather than playing it (the sound comes out of the Spotify app on the
  desk machine or any Spotify Connect device — Spotify's DRM allows nothing else), tells it
  what to play, how loud and when to stop, and reads back what is on. It ducks under a
  VOG sound and fades under a stinger like the music track, **STOP ALL pauses it**, and it is a cue action
  (play / pause / skip / level), a `MUSIC` remote verb and a Companion key. Needs Spotify
  Premium and your own free Client ID; the feature is off until you switch it on.
  **Browse and search** on the Audio page: the songs inside any of your playlists (or a
  pasted playlist, album or artist link) and a search across Spotify, each result one press
  from becoming a button. A **look can start a playlist or song, or pause the music**, as it
  goes on air, so a cue's picture and its music are one GO; loading a look into the preview
  leaves the music alone. Browsing and searching work on a free account; starting playback
  is Premium-only, by Spotify's rule.
- **4-corner warp** — nudge each output's corners (keystone/skew) so a slightly-off projector
  lands straight on the surface — composed with rotation and per-screen trims, applied to
  patterns, media and live inputs alike.
- **A switcher workspace** — the right side of the window works like a vision mixer:
  **PROGRAM on top** (always what the audience sees), **PREVIEW below**, and **the wall**
  between them — one tile per *content target*: the program, every joined canvas and every
  stand-alone screen, each with its **custom label**, a **PGM and a PVW miniature** (true
  pictures at the target's own shape, so a 3840×1080 wall looks like a wall) and a **tally**
  (red on air, amber held, grey off). Click a tile and the big panes take its shape and
  show it; its buttons are **OWN** (its own pattern instead of the program — a joined
  canvas can hold content of its own now), **MON** (draw the miniatures), **ARM** (the next
  CUT / TAKE changes it; un-armed, it keeps the picture the audience is seeing) and the
  live **OUTPUT** switch. A bold banner above the page always says what you're editing.
  Flip **EDIT SAFE (sandbox)** and the preview detaches from air: build the next look with
  every editor as normal, then **CUT** (instant) or **TAKE** (crossfade) it to every armed
  target, or send it to ticked tiles as their own pattern — or save it as a look, or
  discard. Blackout and OUTPUTS ON/OFF stay live through the freeze; what you *fire*
  (looks, cues, stingers, remote commands) still goes straight to air, only what you are
  editing is held back. Subtle neon hues mark every group and page, so the right page is
  one glance away.
- **Playlist show parts** — split the playlist into named parts of the show (*Walk-in ·
  Main · Break*): one part plays at a time, clicked on air from chips (or `SECTION 2`
  from the remote/Companion), or **starting daily at a set time**. Old flat playlists
  migrate into a first part untouched.
- **Streaming output** — send a chosen screen to the internet through the bundled libVLC:
  encoded **once** at your resolution/frame rate/bitrate, duplicated to **up to two
  destinations** (RTMP for YouTube/Twitch/Restream, SRT, UDP) at no extra encoding cost.
  Optional audio from a capture device; never auto-starts; an encoder or network failure
  changes a status line, never the show.
- **Customisable multiview** — a monitor wall as a pattern: program, any screen **or joined
  canvas** at its own real shape (a 3840×1080 wall is a long thin tile, a portrait screen a
  tall box), live inputs and a clock, with the same labels (your custom names) and **red
  on-air tally** the wall uses — so the multiview, the wall and the outputs never disagree;
  a target with no display attached is drawn 16:9. Being engine-rendered it goes anywhere —
  an operator screen, an NDI sender — and it's **available remotely** at `/multiview` on the
  web-remote address (live JPEG refresh).
- **VOG** (voice of God) — one-press sounds and clips over the show, no audio engineer
  needed: *"Take your seats, the show is about to begin."* A sound plays over everything on
  the audio-track outputs while the music ducks underneath (and comes back by itself) — and
  over a **playing stinger** it ducks the stinger too, sound or clip, rather than stopping it,
  so a long hit carries on under the announcement and comes back up after it; a
  **video clip takes over every screen and the previous content returns the moment it
  ends** — unless the operator changes content mid-clip, in which case their choice stands.
  Fired from the Show panel, a cue, the web remote, the TCP protocol or Companion.
- **Live DUCK** — someone needs to speak from the room, through the house desk or a mic on the
  Patterns machine: press **D** (or DUCK in the SHOW CONTROLS drawer, on the phone, on a Stream
  Deck, in a cue) and the music track, break music, a playing stinger and a clip's soundtrack
  drop to the level you set, ramping, until you lift it — a VOG never ducks. A latch, not a
  programme source: STOP ALL and look recalls leave it, and a restart never comes up ducked.
- **Lower thirds — a designer and an engine inside Patterns** — the Lower thirds page (BUILD):
  start from one of ten presets (Clean, Broadcast, Glass, Neon, Corporate, Tag, Headshot,
  Sparks, Fractal, Stamp), fill in the name, role, company, date and time, scrub or play the
  preview, and SHOW. A design is a box of elements anchored on the canvas: text, bars,
  pictures, the brand logo, a clip or still, a particle scene, a fractal — each with a fill or
  gradient, corners, a border, a shadow, a glow, a chaser running round its edge, and its own
  way in and out: a motion chip (fade, the four slides, pop, wipe, drop, rise, spin) writes
  keyframes you can then edit (offset, opacity, scale, rotation, a wipe, an ease), staggered
  one after another. Designed once at 1080 lines, it draws on every sink — displays, a joined
  canvas, NDI, the stream, the thumbnails — on the same frame of the master clock, and reacts
  to effect stings like the full-screen studio. Fire it from the Show panel, a cue (Lower third
  on / off), the phone remote and Companion (`LT n`, `LT OFF`); a look saved while one is on
  carries it. Designs live in the show file, and any one saves as a file of its own to carry
  to another show.
- **Direct output, per screen** — tick it on a screen and Windows hands that window straight to
  its display through the flip path (the "fullscreen optimisations" games ride on): no
  composition frame, none of the compositor's jitter, on a hardware card, while the window covers
  the display alone. The low-latency swap chain behind it is chosen when Patterns starts, from
  what the outputs asked for at the last save, and a fuse guards it: a start that never reaches
  the desk is held off next time and the screen's line says so. The status is honest per output
  (in force, restart to take effect, or why not), with a line on the Machine page and a row in
  the super-check.
- **One master clock, and every sound locked to it** — the pictures already ran on one
  monotonic clock; now every fade, duck and ramp reads the same clock (a wall-clock step can
  never stall one), and each audio output is pulled to it: a sample-rate converter in front of
  every device measures the device's own clock against the master and corrects it (±2000 ppm,
  a PI lock — no more 50–150 ms of drift over a two-hour set), a clip's soundtrack follows a
  **video sound delay**, the stream carries an **audio delay** inside its encode, and every
  output device has its own **delay** slider (0–2000 ms) for lip-sync against IMAG, streaming
  and incoming video. **SYNC CHECK** flashes every sink white on the master's two-second grid
  and clicks the computer output at the same instant — measure the gap at the far end, set the
  delay, done. The Machine page's super-check reads the lock and each output's lag.
- **Every NDI send owns a screen; so does the stream** — adding a send puts a screen of its
  own on the rig (Screens page: FEED SCREENS; the wall; every picker), sized to the send and
  named after it. What the send shows is its choice: the program, a **mirror** of any screen or
  joined canvas (kept at that target's shape, bars around it), or **its own screen** with a look
  of its own — OWN on the wall, → PVW and TAKE, a look, a cue, a multiview tile, exactly like a
  display, and never a window, never adopted, never joined to a display by touching. The stream
  gets the same: a display is still captured off the desktop (cheapest, everything on it), while
  **its own screen**, a joined canvas or a planned screen is rendered by the engine at the
  stream's size and rate and handed to libVLC as raw frames — the same encode, the same
  destinations, and nothing on the desktop can wander into it. An older show's senders get their
  screens on load, mirroring exactly what they mirrored before.
- **Super-check** — one button on the Machine page: the computer (CPU, memory, disk, power),
  the graphics cards and which one renders, every display with its mode and refresh rate, the
  outputs and their frame rate against the master, render faults and the watchdog, NDI sends
  and the runtime, the stream, audio devices, the remote, video playback and the advisor's
  advice — every row with a green, amber or red light, an overall headline, and the **level of
  show the hardware is good for** (Rehearsal · Small show · Full show · Big show, from the
  threads, the memory and the best card, minus a card left idle or a battery). The report is
  saved as `patterns.supercheck.txt` beside the exe and copies to the clipboard. The Machine
  and Help pages take the room automatically: the screens reduce to a strip while one is open,
  and the divider drags the strip's width there (the show remembers it, with ◧ WIDE).
- **A desk that resizes** — drag the divider between the page and the screens for more room
  either way, drag the handle between PROGRAM and PREVIEW to give one pane more of the column,
  or press **◧ WIDE** to reduce the screens to a strip on the right and give the page the room
  (the wall, PROGRAM and PREVIEW stay, small); with the strip showing — ◧ WIDE, or a Machine or
  Help page — the divider sets the strip's width instead, remembered on its own beside the
  page's. The layout travels with the show file, a wide page is held back rather than pushing
  the wall's TAKE off a small window, and the Run layout is untouched.
- **Tally on the desk** — the look in use lights on the Looks page and the Show panel: red
  **PROGRAM** for the picture on air (or **PROGRAM · EDITED** once it has been changed since the
  recall), green **PREVIEW** for the look loaded into the sandboxed preview with → PVW, and both
  when they are the same look. Nothing recalled yet — a fresh start — lights whichever look the
  picture matches. Every VOG, stinger and effect sting lights its row on the Audio page and its
  chip on the Show panel while it plays, with the seconds on air, **HOLDING** for a held frame,
  and a bar that runs down a sting's length.
- **Effect stings** — a stinger with no file, in twelve shapes. Four **pulses** (an explosion,
  a rush, a flash, a bloom: a rise and a settle) and eight **scored stings** whose settings change
  in phases over the length you set: a **shockwave** (a hit, then a ring rolls out through the
  field while the fractal punches out and back), a **vortex** (the field spins up into a whirl, the
  fractal turns, then it all lets go), a **strobe** (eight hits, the colours flipping between
  them), a **supernova** (a blast, everything falls upward, the colours sweep the wheel), a
  **freeze** (slow motion and a cold shift, then the release), a **gust** (a wind slams through one
  way and back), a **rainbow** (a full turn of the colours with a glow) and a **quake** (the
  picture shakes, a ripple runs). Every sting owns nothing — no session, no label, no revert, no
  change to the music — so it fires alone, over a clip, from a cue beside a look, or from
  `STINGER n` and a Stream Deck like any other; every output, a span's halves and NDI surge on
  the same frame, and the picture is back to itself by the end.
- **Stingers** — transition hits from the same library: the music **fades out** instead of
  ducking, a clip **dissolves in**, and when it lands the show goes where the stinger says —
  **back** to what was on, **held** on the last frame for your TAKE or GO (with an optional
  hold limit), **on to the caller's next cue** through the real GO gate, or to **a look or
  cue you name**. Anything that cannot run puts the show back and says so; STOP ALL always
  puts a held stinger back and never runs its ending. One library and one numbering for both
  kinds, so `STINGER 3`, a saved Companion key and a cue target never change meaning; `VOG n`
  and `STING n` refuse the other kind. A crash mid-sting comes back to the show, never the clip.
  A stopped sound or clip **fades out** over the stop fade (never a cut) and is silenced for
  good — nothing told to stop can be heard again under the next press, however soon it comes.
- **The screens are re-owned after a crash, never left playing for nobody** — a crash or a hang
  does not take the render windows down with the desk: the room keeps its picture, which is right.
  But those windows then answer to nobody — nothing can stop them, retarget them or put the next
  look on them, and OUTPUTS ON would open a second set behind the first. So **every start reads who
  has this show folder's screens**: a record beside the settings, beaten once a second by whoever
  owns them (a desk that has stopped answering stops beating while its windows play on — that
  silence is the signal). A record from a process that died is swept up; one from a process still
  up is **asked** for the screens, and a desk whose UI still answers closes its own windows within a
  second and says so on its status line; one that never answers within the grace is **ended** — but
  only once it is provably that same process (its start time, not just its id, which Windows hands
  out again) and provably Patterns. The show then goes back on those screens from the same recovery
  sidecar a watchdog restart reads, so the picture returns to what the audience was seeing, under
  this desk. The health line and Machine → STABILITY say what was found and done — *Took 2 screens
  (Main wall, Foyer) back from the last run (pid 4242, ended — it had stopped answering); the show
  is back on them* — and **Take the screens back from a run that is still playing** turns it off.
  Another computer's record (a show folder on a share) is never touched.
- **A watchdog that keeps the show up** — the app runs supervised: a crash, or a UI that
  stops responding for 30 s, gets the app restarted within seconds **with the same setup —
  outputs re-opened and the audio track resumed** (a sidecar file remembers what was live;
  a clean close never auto-restores). Restarts back off and stop if something genuinely
  crash-loops. Individual render faults never get that far: they're contained per frame and
  counted on a health line (uptime · restarts · faults caught) on the Show page and remotes.
- **Every input, on its own, anywhere you want it** — live sources are a pool, not a slot:
  a camera on screen 1, the graphics PC's NDI feed on screen 2 and a walk-in video on screen 3,
  all at once, each with its own decoder. Use the same feed in several places — two screens, the
  PiP inset, a multiview tile — and it still costs **one decode**; multiview tiles can name their
  own NDI source or capture device, so a monitor wall shows four different inputs. The Media page
  lists what is mounted and says so when the rig wants more than the limit (4 decoders, 6 NDI
  receivers).
- **The preview is sandboxed by default** — from the moment the app opens, touching any editor
  (screens, outputs, inputs, patterns, overlays) builds in the preview and **never reaches the
  audience** until you CUT or TAKE — and EDIT SAFE re-arms itself after every send, so you are
  always building in safety. What you *fire* still goes straight to air: F-key looks, scheduled
  cues, presenter steps, stingers and every remote command. `→ PVW` loads a look into the preview
  instead. Turn it off in Admin → Switcher if you prefer the preview to mirror the program.
- **Prep mode — programme the show before the rig exists** — switch to PREP and build the whole
  thing at your desk with nothing plugged in: **plan screens** at the sizes the venue will have,
  arrange them, name them, give each its pattern, join them into canvases, put them in the
  multiview, and type in the NDI and capture names the rig will use. The outputs, the NDI sends
  and the stream are held closed so nothing goes live by accident — on a cable or on the network.
  At the venue, switch to SHOW, say which detected display each planned
  screen turned out to be and press **Adopt** — position, label, rotation, trims, warp, per-screen
  pattern, canvas name, multiview tiles and the stream source all follow onto the hardware.
- **A Machine page (ADMIN) that watches the machine** — live **CPU / memory / GPU / frame-rate**
  with three-minute history charts, and **plain-language suggestions** when something needs
  attention: running on battery, memory climbing like a leak, frames dropping, disk
  filling, handle counts growing, the show stuck on the integrated GPU. A rolling
  `patterns.metrics.csv` (30-second samples, rotated at 1 MB) records the night for
  after-show reading; a computer overview with one-click **Copy support info** feeds
  tickets; **Restart app** relaunches through the watchdog with the show restored. The
  machine numbers also ride the remote protocol (`machine{cpu,ram,fps,battery,advice}`)
  and Companion variables — CPU and fps in a Stream Deck key corner.
- **It finds your best graphics card by default** — at startup Patterns enumerates the
  GPUs (DXGI), picks the strongest (most video memory, discrete first), renders on it, and
  registers the choice in Windows' per-app graphics preference so **video decoding follows
  the same card** — on laptops this is what stops the show landing on the battery-saver
  GPU. Selectable in Admin: best performance, power saving, a specific adapter, or let
  Windows decide.

<table>
  <tr>
    <td><img src="docs/media/shot-ledwall.png" alt="LED wall pattern, serpentine numbering"/></td>
    <td><img src="docs/media/shot-blend.png" alt="3-projector blend with S-curve ramps"/></td>
  </tr>
  <tr>
    <td><img src="docs/media/shot-smpte.png" alt="SMPTE RP 219-style bars with clock overlay"/></td>
    <td><img src="docs/media/shot-countdown.png" alt="Show countdown over branded bokeh particles"/></td>
  </tr>
  <tr>
    <td><img src="docs/media/shot-ledmap.png" alt="Irregular LED map editor with live preview"/></td>
    <td><img src="docs/media/shot-cues.png" alt="The Cues page — the caller's stack with typed actions, readable summaries and a broken cue flagged with its reason"/></td>
  </tr>
  <tr>
    <td><img src="docs/media/shot-run.png" alt="Run mode — the LIVE strip, the wall beside the cue stack with last / standby / next, and the transport row with GO"/></td>
    <td><img src="docs/media/shot-show.png" alt="The Show panel — looks, the clicker list, VOGs, stingers, break music and the audio track beside the switcher"/></td>
  </tr>
  <tr>
    <td><img src="docs/media/shot-playlist.png" alt="Playlist with drag-to-reorder and per-item timing"/></td>
  <tr>
    <td><img src="docs/media/shot-sandbox.png" alt="The switcher — program on air on top, the next look building in the sandboxed preview"/></td>
    <td><img src="docs/media/shot-audio.png" alt="Audio — track, break music, VOGs and stingers with their endings, and per-output device routing"/></td>
  </tr>
  <tr>
    <td><img src="docs/media/shot-multiview.png" alt="Multiview — program, screens, inputs and clock with tally"/></td>
    <td><img src="docs/media/shot-admin.png" alt="Machine — live performance charts, health suggestions and the GPU choice"/></td>
  </tr>
  <tr>
    <td><img src="docs/media/shot-prep.png" alt="Prep mode — planned screens built without hardware, sandboxed preview, adopt pickers"/></td>
    <td><img src="docs/media/shot-sandbox.png" alt="The switcher — program on air on top, the next look building in the sandboxed preview"/></td>
  </tr>
</table>

*Patterns rendered by the engine exactly as outputs and NDI receive them; the irregular-map
editor, looks/cues/presenter steps and the Show page drive them live.*

## Quick start

1. Grab `Patterns.exe` (build it with `build/publish-win-x64.cmd`, or take the CI artifact) and
   put it in a folder you can write to (USB stick, desktop — anywhere).
2. Run it. Arrange your screens on the **Screens** page (SETUP) — drag them together for one
   big canvas, click a tile to enable/disable it or give it its own pattern.
3. Choose a pattern (or click one in the **Library**), tune it, press **OUTPUTS ON** (`Shift+F5`).
4. Everything — wall geometry, blend overlap, colours, countdowns — updates live on the outputs.
5. Save the state as a **look** and put it on an F-key or the daily schedule (**Looks** page,
   PLAN); build the cue stack on the **Cues** page.
6. Run the evening from the **Show panel** or the **RUN** surface — or from a phone, tablet or
   Stream Deck via the **Remote** page (web remote, TCP protocol, Bitfocus Companion module).

| Key | Action |
|---|---|
| `F1`–`F12` | apply saved looks |
| `Shift+F5` | OUTPUTS ON — open the output windows |
| `Shift+F6` | OUTPUTS OFF — close them |
| `Shift+F7` | IDENTIFY — flash screen numbers |
| `Shift+F8` / `Space` | BLACKOUT toggle |
| `Page Down` / `Page Up` | clicker list next / back (when armed) |
| `Enter` (Run mode, armed) | GO on the cue stack |
| `↑` / `↓` (Run mode) | move standby — no output change |
| `Esc` (Run mode) | cancel a pending confirm; twice within a second = STOP ALL |
| on outputs: `Esc` twice within a second | close outputs (one Esc never blanks the room; the prompt shows on the desk) |
| on outputs: `Space` / `B`, `I`, `F1`–`F12`, `Page Down`/`Up` | blackout, identify, looks, presenter |

Settings autosave beside the exe (`patterns.settings.json`, atomic with backup); whole rigs save
as show files (`*.patshow.json`). Presets and brand kits are plain JSON folders next to the exe.
Every change to what the audience sees — a look recall, a scheduled cue, a VOG or stinger and its
revert, a playlist part, outputs on/off, blackout — is appended to `patterns.showlog.jsonl`
with the time and who caused it (desk, keyboard, clicker, a remote's address, the schedule),
so a show can be reconstructed afterwards and a caller can see what happened after a restart.
A show file saved by a newer build never quarantines an older build's settings: a setting the
older build does not know falls back to its plain default with a warning in the log.

## Optional integrations

- **NDI**: install the free [NDI runtime](https://ndi.video) *or* drop
  `Processing.NDI.Lib.x64.dll` next to `Patterns.exe`. The NDI page shows what was detected.
- **Video**: use the **full** build (`Patterns-portable-win-x64-full` CI artifact or
  `build\publish-win-x64-full.cmd`), which bundles libVLC — or with the lean exe, install
  64-bit [VLC](https://videolan.org) / place a `libvlc` folder next to `Patterns.exe`.
  Images work without any of this.
- **Remote control**: switch it on on the **Remote** page (SETUP) — the web remote and TCP protocol
  need nothing installed anywhere. For Stream Decks, load the Companion module from
  `integrations/companion-module-patterns/` (or use Companion's Generic TCP with the
  commands in [`docs/REMOTE.md`](docs/REMOTE.md)). *No password — anyone on the network can
  drive the show while it's enabled, so switch it off when you don't need it.*
- **Spotify break music**: needs a Spotify **Premium** account and your own free **Client ID**
  from the [Spotify developer dashboard](https://developer.spotify.com/dashboard) — create an
  app there and register all three redirect URIs the Audio page lists
  (`http://127.0.0.1:8724/callback`, `…:8725/…`, `…:8726/…`; Spotify no longer accepts
  `localhost`). A development-mode app allows up to five listed Premium accounts, which is
  plenty for a desk; a business can ask Spotify for more. Which sound output Spotify uses is
  chosen inside Spotify. CONNECT on the Audio page signs in through your browser; the sign-in
  is kept in `patterns.spotify.json` beside the settings file and **never travels inside a
  show file** — a show on another machine asks for its own CONNECT.

## Building

```bash
dotnet test                      # 1447 tests: a take told from an edit (the fade predicate — a take with transitions on, an edit that is not one, a cut, a fade override, a named transition, transitions off; the take scope nesting and closing, the action layer and the one seam that edits the air publishing takes and an ordinary edit publishing none; a transition already running never abandoned by an edit and abandoned by a cut, a move and transitions off; a sink that moves screens re-seeding rather than crossfading), the switcher's round trip (the programme itself into the preview from the PGM tile with the air untouched, EDIT SAFE opened and the focus cleared for the rig; a screen pulled in, changed and staged back on its own tile with the room still on the old picture; a FOCUSED take after a staged send touching that tile and nothing else; a take that leaves a staged tile alone dropping the staging rather than pinning a screen out of the show; staging costing no dissolve and the take costing exactly one), two audio wires (the destination for every bus — the programme's mount never silenced by a monitor pick, the operator's own when one is named, silence when none is, and the same clip on two buses going to the room; the engine handing each mount the device its bus belongs to; the soundcheck tone following the programme's first named output and not the machine's default; an interface that is not plugged in named in words rather than silently substituted; the monitor's pickers reconciled in place so a device chosen is still chosen after the list is rebuilt five times), presets recalled (saving one saying where it went and putting it one press away with the chip recalling it and ✕ forgetting the file, one picker offering the show's looks and this machine's presets together with a send landing on one screen alone and every other screen untouched, a recall waiting for the TAKE while a send to one screen is live, a preset that did not travel naming what this machine actually has, and a cue and the wire reaching both through the one action with the checks warning rather than refusing), the Patterns test card (a brand-new install coming up on it and the kind registered and appended so the tolerant reader's fallback still means Grid, the card carrying its own mark with the badge off it, the eleven steps at the values they claim with no step a pixel thinner than its neighbour, every clipping patch and gamma solid present so a missing one is the processor and not the card, the one-to-one fields exactly half lit through the real engine, a card that cannot measure saying so in orange rather than looking the same, every shape and sink from a 32:9 strip to a 96-pixel tile, and the card static so a wall of them costs nothing), three states for a look (a look still what it saved with no screen off it, one screen changed named and not the whole show, a screen the look itself gave its own picture not counting and a locked one never counting, the programme moving taking every screen that follows it, a setting that is not part of the picture counting for nothing, an unreadable look naming nobody; on a live desk the wire saying whether the look on air is still what is on the screens, every screen saying what it draws and whether it has drifted, the page and the wire off one reading, and every pattern kind in the picker with the first-run default and the tolerant fallback deliberately different), a MIDI control surface (every message read and written as a line an operator can see with a release as its own word so a pad fires once, a lamp line back out as the bytes the surface expects with fourteen bits for a motorised fader, a fader reading nought at the bottom and a hundred at the top monotonically so it cannot die in the top of its travel, a learn row matching its own control and no other through the trigger table the Arduinos already use, one table read both ways with a fact lighting a lamp and a lamp row never firing as a command, % stretching the show's own reading onto an LED ring, the two levels the show has reaching a device as facts, a press going through at once while a whole fader sweep costs one reading of where it ended up, a control that came back to where it started counting as unmoved, a surface unplugged and back forgetting where every control was, and every starter set being rows that read one way or the other and seed once; on a live desk a surface starting as pads in and lamps out, LEARN writing the row, the starter rows landing in the operator's own table, the same arm fence every other remote meets, a sweep not writing fifty journal rows a second, and the port saying so where there is no MIDI), the badge's rule (every kind in the enum carrying it, a monitor wall never, media only when asked, the test card never because it signs itself), a picture or clip imported into a lower third (the copy beside the show with the original left alone, the same file twice as one copy, a different file of the same name taking the next name rather than overwriting a design's picture, a file too big to carry pointed at with the reason, a show opened from another drive letter finding its pictures by name, the cue checks warning — never refusing — on the one that did not travel, the draw path reading the disk a twentieth as often as it draws while the checks read it now, and an import forgetting every reading; on a live desk the element named after its file with a typed name kept, the trouble line raised on the keystroke and cleared by the import, and the big clip's sentence), what the desk is listening to (every picture that wants a clip recorded against it and one clip on two buses staying one decoder, the programme heard by default with a confidence screen's clip out of the mix, the pair swapping over when the screen is monitored, silence meaning silence, a screen that follows the show sounding like the show along with one never picked and one that left the rig, the operator's own mute still winning, the choice kept out of what a picture looks like, a mount on several buses read as all of them, and the line that says what is being heard; on a live desk the preview's clip silent until the preview is what you are listening to, the same clip in both heard either way round, and the Audio page's row following the choice on the click), the monitor walls (every layout filling the wall with nothing overlapping or leaving it at every count, the large ones the first in the list and bigger than the strip, the strip wrapping rather than shrinking to nothing, the grid saying nothing is large; a wall the show's rather than a pattern's with the fallback for one that names none and one that names a wall the show lost, two the limit and the third press giving back what is there, the wall the remote gets; a tick setting up a screen, an NDI sender and the stream and untick handing each straight back, a removed wall taking every output with it, an older show's identical tiles becoming one wall both patterns point at with its grid kept and the pass idempotent, a wall's name and id kept out of what a picture looks like while which wall a pattern draws is exactly that, a screen adopted at the venue keeping its place on every wall, and the engine drawing the wall the layout planned; on a live desk the page on the rail under SETUP with a wall arriving filled from the rig, one tick doing the whole job with the programme untouched, and a tile dragged to the top becoming one of the large ones), the Cues page under the pointer (ten revalidates moving nothing and leaving every row instance where it was, a rename and a broken reference landing in place, a cue inserted, moved and removed reconciling around the rows that stay, the action pickers and the Quick look picker left alone with a new look still reaching both, and a wait typed into one step re-timing its neighbours without writing a number back into their boxes while a reorder does), a cue with a shape in time (the plan's moments and its immediate steps, the move that carries the step and leaves the waits, the words, the sheet's After column both ways; on a live desk a delayed step landing on the beat and named, the tail dropped by the next GO, by STOP ALL, by a disarm, by a reset and by another show), how one picture becomes the next (the curves and the cover's plateau, the push's four ways, the dip colour from the brand or the show with a bad hex refused, the matte's size and determinism and every scene's range, the ramp's two ends with every pixel premultiplied; every kind whole at both ends and busy in the middle on a sink with no graphics card, the wall and the stream drawing the same frame, the dip complete where the cut happens and the stinger's two brand bars at the peak, a wipe and a push travelling the way they were asked, a look settled once and deaf to a setting changed under it, a bright dip too soon drawn as a dissolve and a dark one never held back, a still picture holding no matte and a CUT throwing one away, the words on a sheet and in the checks, the override riding exactly one snapshot; on a live desk the pickers following the choice on the keystroke, a loaded show arriving with its pickers right, and a look's own arrival reaching the snapshot the sinks actually draw), a page with nothing round it (the service pick honoured by resolve, FULL FRAME and the actions, and a YouTube link told to be a plain page left alone; the strip's contents — the media bar, the watermark, the pause panel, the end-screen cards, the play button — off until asked, never touching the video element, one for every service and the reset alone for a plain page; the page the engine opens carrying its strip and a layer's own tick; on a live desk CLEAN reaching the browser and coming off live with no reload, and the service choice travelling from the staging block onto the pattern), the stream you can see (every light from every state, the slow threshold and its settling seconds, the restart note, the colours and the uptime words; on a live desk the rail's foot in three states and its way to the page by name, the Show panel's START and STOP, a look carrying the stream on air and never into the preview, the health on STATE and in an OSC bundle, and the cue vocabulary still naming it), the restart that comes back to the show (the record's shape and round trip with the program, the black targets and the stream in it, an older build's record still read, the staleness window; the fade predicate's truth table — the first frame, a key that moved, a CUT, transitions off, a thumbnail; on a live desk arming EDIT SAFE alone putting the air in the record, a TAKE and a per-screen SEND moving it, leaving EDIT SAFE handing it back to the settings file, RESTART keeping the air and the caller's place, a restart coming back split with the brand kit proving the record is the program and not a look of it, a screen faded out coming back dark, a stream named rather than started, a desk that was editing live coming back editing live, an older record leaving EDIT SAFE alone, the record surviving startup, the desk knowing what it calls the picture, a takeover with no record saying so, a takeover acting on its record however old, a takeover's words surviving the Run surface it opens, and a Pattern Type change reaching the switcher tile over more than one frame and settling back to drawing on change), the inputs a scene may listen to (nothing named meaning the machine's own input, the saved name winning, a USB box matched across the socket Windows stamps into it and only across that, the key; on a live desk a reactive scene's input read by the analyser where only a fractal's was before, the page's input picker, the machine's own input first in the list with a device this show names kept in it though it is not plugged in here, a scene that is not the pattern on screen opening nothing, a 24-bit stereo buffer read as sound), the reactive scenes (the flash limit — three a second let through and the fourth dropped, a dropped flash staying dropped for its whole pulse, the cap, a show clock that went backwards, the sound's bounded hold on the brightness; every scene moving in silence, the same clock drawing the same frame, the shader and the CPU twin inside a mean difference of 0.06 on all six, a scene whose shader will not compile falling to the twin and asked for once, the working size following the quality and the ladder, the palette's wrap and its five slots, the options' bounds and an older show at the defaults, the continuous cadence, the maker's badge off the client's background, six shader sources and the sink split, a sting's surge reaching the scene; on a live desk the page on the rail with its six chips and USE IT, a chip landing and leaving the colours and the sound alone, PATTERN Reactive on the wire and in STATE, the desk drawing one without a fault), the screens re-owned after a crash (the ownership record's every reading — free, ours, a dead owner, a pid handed out again, a live desk, a hung one, another machine, a record from last month — the grace, the ask obeyed once and never by its asker, the words, the disk round trip with junk on it; on a live desk the record written while the outputs are live, beaten by the poll and gone when they close, a hung run asked and then ended, a live one standing down un-ended, nothing but Patterns ever ended, the tick off leaving the screens alone, a desk standing down on an ask and never on its own or a stale one, the takeover on the health line and taken once), an overlay's place (the margin and the bases, re-anchoring exact from every anchor and every offset, the drop read as the corner it landed in, a corner still a corner on a 32:9 wall where the nudge from the middle misses by 400 px, a box growing keeping its anchor, typing pixels landing on those pixels, the degenerate canvas; on a live desk the drop re-anchored with nothing moved, a layer untouched, the pixel fields reading and writing and going quiet with nothing drawn, every overlay page carrying its row), the countdown's one key (the bare verb, TOGGLE and TIMER parsed, the OSC addresses, the cue sheet and the summary; on a live desk the key on and off again, the duration armed from now, STATE reading it), the divider on the wide pages (the strip following the divider with the page's width unmoved, a second release adding nothing, the clamps and the hold-back for the page's minimum, the Machine page dragging it and the Pattern page coming back as it was, Help showing it, the show file carrying both numbers and an older file at the default), the settings column (closed on the panel and on Cues with no cue, a cue added opening it with its panel and the page column wider by it with the show's width unmoved, the ACTIONS block in the column and not under the list, the bands in the page's neon, ◀ CLOSE and SETTINGS ▸, another cue on the same panel, another page closing it, the Screens page's panel in its own neon and the selection cleared closing it, the Lower thirds element, the Run layout keeping it out; ? TIPS reading the column's explanations under its title after the page's; the gap rows and the direct-output tick in the panel), the assistant's attachments (a picture reduced and still PNG, a PDF with its pages, text cut with its note, a sheet as a text table, a Word and a PowerPoint file as words, the refusals and the heading; on the desk two files attached and one refused, the plan ask carrying them as blocks, the chips cleared, the files sent again for two exchanges and words after), the assistant reading the desk (the brief with every state filled and with none, the fence's words; the request carrying EDIT SAFE, the outputs, the editing target, a joined canvas with what its members show, the stack and the sound, and after TAKE the air and the preview apart with the cue on standby), the assistant on the desk (three planned screens as three targets and no canvas, a real wall kept and the new screen apart, the rules' words; the rows newest first with the latest lit, APPLY with EDIT SAFE off opening it with the air untouched and the preview changed, the editing target on Program, TAKE putting it on air), the runtime (the clone compact and the same show, the spectrum's kept buffers giving fresh arrays' answer, the collector on air and off, the eight-pixel P216 row equal to the scalar row at every width, OUTPUTS ON and OFF moving the collector with the Machine page's line, the feed reading float and PCM buffers as samples), a faster start (the pages built when shown and the rest in idle time, the settings handed to the desk once with the phases in order, the way out once, the show back after a watchdog restart when the window opens, Main's marks first and the line naming every phase), the particles hardened (a sim per field found without a rebuild, the eviction; a late sink taking the leader's field and staying in step, a snapshot nobody drew anchoring alone, a disposed leader replaced; the catch-up's share per frame and the re-anchor; the ladder hiding and never stopping; the backwards clock; the poisoned field born again and the disposed sim inert; the random stream carried by a join; the plan across midnight), the Fractals page (the families in order with every scene under one, every scene applying with its name and rastering clean with the sound left alone; the chips by family, USE IT, the brand palette, a saved fractal preset as a Custom chip, the Library section, the page rendering, the rail order, OPEN FRACTALS and OPEN PARTICLES, the Help topic), which group a screen is in (the foot lines of a main screen, a confidence monitor, a repeater, an NDI feed screen and PGM; the click opening the Screens page on that screen; a role or a mirror changed there reading on the tile at once; the Help words), the Patterns badge (the defaults and the rule, an older file and look opening with it on, a look saved with it off recalling so; the pixels — the card centred in the lower third with the cyan and the white and its hit box, a clean field when off, moved by its anchor and nudge, grown by its height, media and the multiview kept clean; the Branding page, the drag, OVERLAYS OFF, the Help), any screen into the preview (a tile's own picture loaded with the air untouched, the editors on the program and the PGM tile selected, the copy edited and sent back to another screen; a screen on the program loading the program's picture and a repeater its source's; the action by wall number and a missing screen refused; EDIT SAFE off opened first with nothing live; → PVW on every screen tile and never PGM, SEND only with the sandbox open; the kind's words), a send keeping the preview (the program on air untouched and the target its own pattern in both states, the preview look and the editing target unmoved; a second send after an edit with the first screen keeping its picture; TAKE putting the preview everywhere but the screens with their own), the switcher's tiles (every tile with one face and its buttons at the top on a tall row level with PGM's, with MON on and off; a tile collapsed alone and kept through a rebuild and the show file, opened again; the Run wall's collapse over a tile's own choice; the Help's words), the grid on the tiles (the hairline rule — an output as asked, a tile widened to one device pixel, an upscale never thinned; the grid drawn into a 96-pixel tile keeping all twenty lines with the device scale and none without it), no crash between pages (the sink guard drawing nothing once closed, a close waiting for the frame in progress, fresh sinks after; the designer preview with a fractal element drawn, its page left and a late frame drawing nothing, the page back and four frames through a fresh stage; the Lower Thirds page left and re-entered on the real desk; the Screens page's guard with the tree), the assistant answering again (every member of the reply's schema required with the nullable shapes, the refusal known by its words and by nothing else, the plain prompt carrying the shape between the rules and the brief, nulls in every place read as nothing said; on a live desk a refused first request asked again in plain JSON as the same turn with the answer read and the status's note, the next ask plain from the start), the encoder in its own process (the ring whole and newest-wins and opened by address, a waiting reader woken by the owner's close, the words and their payloads, a scripted host started with its plan and read, dead and back with backoff, silent and ended, a cold start given its patience, a bring-up stuck past the start timeout ended and back, a host gone only when its process is, a crash loop stood down from, START again as a fresh count; the real host in a real process counting the ring frame by frame and back after a kill; the stream service around it — "Connecting" before LIVE naming the process, a settings change waiting for the old process to let go of the destination, restarting with the show untouched, no libVLC held, the encoder's error stopping the stream; the render thread keeping its ring through a late frame and letting it go after, and at once when stopped in time), fractals on the card (every family drawn by the shader and by the raster into the same pixels, a lower third's fractal element through the shader every frame on an output and at its cadence on NDI, the five-colour palette), one action vocabulary (every kind a cue's or the desk's alone and never both, words for every cue kind, the sheet reading every label, the show file round trip, the checks on the desk's own kinds and the new values; on a live desk a cue running eleven verbs a cue never had, a clean picture, TAKE and RESTART refused from a cue, the fade's seconds from the desk, the wire and a cue; the wire's 116 lines each parsed as the show action it is, the wire's own five words, TAKE and CUT with no verb, standby / hold / arm through the executor with the arm gate reading the origin, LOOK #n resolved by the executor), every page's neon on its title, its bands and its panels' edges with the rail's and the strip's underlines, the Layers page (its place on the rail, the editors bound to the editing target's own pattern, the page controls for a web layer, the Media page carrying no layers), the Run area's room (the stack at a third from the show's default, a drag to a half remembered, the show's limit, the tiles as bars and back with the desk's wall untouched, the popup on every tile), CUT / TAKE with a scope (the picker on the wall, a TAKE to the focused tile with the rest pinned, the next full TAKE lifting the pins, a CUT to the ticked tiles with the ticks consumed, the refusals with the air untouched, the PGM tile as every armed screen, ARM off inside a scope, SCREEN n from the action layer), a fade to black on its own (the scope words and the junk that means nothing, the verb with its seconds and place in either order, the OSC addresses and the feedback, the cue action through spec / sheet / summary / checks / export, the gain table taking the programme down with the black, a target black on its own drawn black with only its sink fading; on a live desk one screen faded while the rest keep their picture, the rig fade taking the sound down and the fade up bringing it back, the option off, a plain blackout leaving the sound, BLACKOUT OFF after a fade bringing it back, every screen black on its own counting as the rig dark, STATE, the show file clean, the picker and WITH THE SOUND, the ticks on every tile without EDIT SAFE, the focused tile, the ticked tiles, the groups refusal, the program tile as the rig, a cue), the Machine page's card as lines (the words and the lines from seventy samples, the day's lines, the page's rows bound to them, a card with no readings) and every health tile's bar inside its pill; the Show panel's stop under the stinger chips and above the lower thirds, there without a design or a person, the wall's OUT inside every tile with no switch left and switching the screen live; the editing target never blank (the picker keeping its selection through a lock, a label and a second OWN, Program as the fallback, the picker hidden with the target never null, an empty selection refused), UI faults contained (the crash note carrying the exception's words and an older note still loading, the words for a thrown, a wrapped, a never-thrown and a long-message exception; on a live desk a posted job, a command and a key that throw contained with the count, the words, the status line, the health line and the log's stack, every page forward and back at two sizes containing nothing, a fatal note read on the next start), the remote's overlays (every CLOCK, MESSAGE, COUNTDOWN, LOGO, PIP and OVERLAYS verb parsed and the malformed refused, the minutes parser, the OSC addresses and the feedback facts, the phone's words; on a live desk the verbs driving the air and STATE, the per-second rule, EDIT SAFE, the phone's OVERLAYS tab and its endpoint), the Show panel's chips (the carry by name with case, spaces, a hand edit and an empty library, the line's precedence and raises, a clone carrying no light; on a live desk the four groups three to a row, a person and the design lit with the lines, a VOG lit with its seconds, the preview green beside the air red, TAKE, HIDE, a typed-over name dark), the Lower thirds page (the preview pinned above a scrolling page and staying put, no design keeping the stage; a person's name and role with the rest folded until More opens, the header following the fields and the selection; the desk hosting the page without an outer scroll viewer), the quality ladder and the memory ceilings (the steps down and up, the modes, every level's factor and floors, the particle field's active share, the words, the ceilings for four machine sizes, the rows; on a live desk an output's slow seconds stepping the shared ladder, the poll's lines, the facts, Full and Economy, the page, the preview standing in), the standby cue's pre-roll (the look's clips as the pool wants them, live sources and blackout wanting nothing, the look by name and id, the words; on a live desk the hold, the retire, the fresh open, GO releasing the same decoder, CLIP ON AIR, the decoder cap), the engine's frame budget (a sink's last minute with the worst frame and its stage, the roll-out, the names, the stages and their words, the registry's readings and line, the super-check row and its advice per stage, the start-up row and budget, the RENDER tile, the engine noting four stages and a nested draw; on a live desk a preview pipeline feeding its budget and closing the start-up, the poll's lines, the facts, the page, dispose detaching, an output named by its screen, the fence), native-crash hardening (the exit-code words and the native-fault line, the crash note round-tripping once with junk ignored, the sentence with and without a dump and for a hang, the dump variables and the sweep keeping three, the decoding table against a safe run, the bundle carrying the note and the newest small dump and leaving a huge one with its path, the fractal raster the same at any parallelism, the lower-third fractal cadence; on a live desk a native-fault note making a safe run that Hardware overrides at once, a managed note keeping the card, a clean start and the Machine page block), the stinger lifecycle (the library's clip-on-air and clip-look reads; on a live desk a tick that throws keeping the session, STOP putting back a clip nothing owns, STOP with nothing known and with the look on air, a clip fired over a dead clip coming back to the show, the same clip pressed again from the top, a leftover ended decoder restarted and one that will not roll put back, a stalled clip put back, a recovery sidecar holding a clip refused), the assistant (the key store, the gate on probes and show talk, the brief with the names and without the secrets, the prompt and its catalogue, the schema closed at every object, the parser on a full reply and on junk, a show plan applied in order and read by the checks, applying again updating not doubling, the small proposals; on a live desk no key meaning nothing sent and the key beside the settings never in the show file, a probe stopped before the wire, a reply through a fake transport into rows and chips, APPLY building the look / the design / the cues, the conversation carried, a declined reply and a failing wire in words, the page), the weather chip (the symbol and WMO maps, MET and Open-Meteo replies into hours, days and the three cards, junk and the search, the addresses, the words, the verbs and OSC, the cue actions, the chip drawn and its hit; on a live desk the forecast through a fake transport onto the snapshot and STATE, the search, the wire / a cue / the drawer, a look carrying the chip, a failed fetch keeping the last forecast, the page and the drag), the desk's tick (the budget's window, worst, area, slow count, faults and words; the super-check row and the tile; a failing area carried past and told once a minute; the remote's addresses kept and following the port; the tick read on the page, the super-check and the dashboard; a quiet second raising only the clock; the benchmark's fence), the audio playlist (the order and its fallback, shuffle by seed, folders through the enumerator and the cap, stepping and finding, the migration, the verbs, OSC in and out, the cue actions; on a live desk rows and a folder in one order, PLAY with the marker, NEXT / PREV wrapping, a track by number, name and id, a cue and the panel, the natural end, a vanished file skipped, the empty list refused, the old single track), the caller's VT clock (the reading's priority and roles, the words, the ten-second out, the loop, the wire and OSC, the cue actions, the sequencer's clock; on a live desk the same seconds on the panel, the Run strip and STATE, the skip and the top from the wire, a cue and the keys), permanent installs (days and dates as people write them, windows past midnight, the programme that wins, firings and the next change, the day's timeline, the runtime's every rule — a programme once, an advert at its minute and the programme back, announcements beating adverts, a deferred advert fired or missed, the desk owning the screens, idle outside hours, the clock off — the verbs, the addresses, the cue actions, the passcode gate and its lock, the support bundle's redaction, an update package read, refused, applied and rolled back, the check-in contract; on a live desk the programme landing from the schedule, the advert and the announcement by the clock and by hand, idle black lifted by the morning, STATE, the page, RESTART and UPDATE APPLY behind the passcode with a staged package handed to the watchdog, and a real HTTP check-in whose reply runs commands from the server), and everything before it
build/publish-win-x64.sh         # → dist/win-x64/Patterns.exe  (single file, self-contained)
build/publish-win-x64-full.sh    # → dist/win-x64-full/  (exe + bundled libVLC; any host, .cmd on Windows)
```

Requires the .NET 10 SDK (LTS, supported to November 2028; .NET 8's support ends in November
2026). A machine that still has only the .NET 8 SDK builds and tests the same tree with
`-p:PatternsTfm=net8.0` on every `dotnet` command until .NET 10 is installed; CI and the published
exe are .NET 10. The exe is self-contained — end users need nothing installed — and it ships
precompiled (ReadyToRun): the first page, the first frame of a renderer and the first cue never wait
on the JIT. That is why it was about 88 MB rather than 50; the precompiled code is the difference.
Since round 18 the bundle is not compressed either — a compressed single-file exe is decompressed
on every start, precompiled code included — so it is larger again and starts faster; anyone who
wants the smaller download publishes with `-p:EnableCompressionInSingleFile=true`. While the
outputs are live the garbage collector runs in sustained low latency — collections in the
background, no full stop-the-world collection for the length of the show — and rests in the
interactive default off air; the Machine page's runtime line reads which, with the .NET in use.
The bundle asks the runtime to keep its memory between collections (`runtimeconfig.template.json`).

## Versions and rolling back

Yes — GitHub keeps every version. Every push to the branch is a commit with the whole tree as it
was, its message saying what changed and why, and a CI run that built it and ran the suite; the
Windows build job attaches the portable exe of that commit as an artifact, so any green commit is
a build you can download without building. Nothing is ever overwritten: a roll-back is a new
commit that puts an older tree back, and the history stays.

To go back to an earlier version of the software:

```bash
git log --oneline                      # the versions, newest first, one line each
git checkout <commit> -- .             # the working tree as it was at that commit (then commit it)
git revert <commit>                    # undo one commit, keeping everything after it
git checkout -b rollback <commit>      # or work from that point on a branch of its own
```

Tag a version you trust before a show (`git tag show-2026-09-12 && git push --tags`), and the
Releases page can carry the exe of that tag. The Actions page keeps the test results of every run,
so a version that was green is provable.

The show file has its own ladder, on the machine, with no network: every save keeps the previous
file (`patterns.settings.json.bak`), and the first save that changes something after five quiet
minutes keeps the file as it was in `backups/` under its time, twenty deep. The Machine page lists
them and RESTORE puts one back exactly as Load show would (Admin → Machine → Earlier versions of
the show). Export the show to a file of your own for a version you want to keep for good.

## Architecture (short version)

One UI-independent render engine (`Patterns.Core`, SkiaSharp) draws every sink — the preview,
each fullscreen output, preset thumbnails and NDI frames — from immutable show-state snapshots
published on change. Immutable by construction: every object in a published snapshot is marked
and a write to it throws; a publish copies only the sections of the show that moved and shares
the rest with the snapshot before it, so a drag copies one section, not the show
([`docs/PLAN.md`](docs/PLAN.md) §46). Output windows render at 1:1 device pixels (per-monitor-V2 DPI aware) with
antialiasing off for alignment content; spans use union-rect viewports and are covered by a
stitching test. Redraw is demand-driven: static patterns cost ~0 when idle, clocks tick once a
second, animation runs at vsync. A renderer that throws is contained to an on-screen error card —
the show keeps running. See [`docs/PLAN.md`](docs/PLAN.md) for the full design.

Should the core be split into several cores with an orchestrator? No — and the reasons are argued
from the code in [`docs/PLAN.md`](docs/PLAN.md) §12 and §14: one show core (the state, the action
layer, the cue stack — pure C#, no native surface) and one render core with its sinks on their own
threads share one snapshot, one clock and one journal; the supervisor is already the orchestrator
(a separate process that starts, watches, restarts, updates and rolls back the app) and should
watch more rather than run more; the native edges that can take a process down — the stream
encoder, the web renderer, video decoding — sit behind seams with fakes and move out to a child of
the same supervisor when a real fault says so; redundancy is a second machine listening for the
beacon, not a second process. The field research behind round 12's choices is in
[`docs/FIELD-RESEARCH.md`](docs/FIELD-RESEARCH.md).

The desk itself is one view model in nine partial files by area (the rig, the content, the show,
the sound, lower thirds, the machine and the install, help, the tick, and the head with the
constructor and the commands); its once-a-second tick is nineteen guarded, timed areas whose
budget the Machine page and the super-check read — the audit, the numbers and what was kept on
purpose are in [`docs/PLAN.md`](docs/PLAN.md) §16.1.

Is that a "game-play architecture with corporate stability"? The mapping — a fixed cadence with a
budget, state as immutable frames, systems that cannot take the frame down, instrumentation,
assets ready before the cut, scaling to the machine, back in seconds without a hand — and where
Patterns still falls short of a game engine (a render-side frame budget, pre-rolling the standby
cue's clip, an adaptive quality ladder, a start-up fence, memory ceilings in numbers) are argued in
[`docs/PLAN.md`](docs/PLAN.md) §16.4. The assistant's cloud-not-local decision and how it is fenced
are §16.3; the weather source's choice is §16.2.

## License

MIT — see [LICENSE](LICENSE). Third-party components: [THIRDPARTY-NOTICES.md](THIRDPARTY-NOTICES.md).
NDI® is a registered trademark of Vizrt NDI AB.
