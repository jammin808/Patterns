namespace Patterns.Core.Services;

/// <summary>The Help catalogue's sections, in the order a show happens.</summary>
public enum HelpGroup
{
    /// <summary>The map: how a show flows through Patterns, the shell, the modes, the keys, the workflow, PREP.</summary>
    StartHere,
    /// <summary>Show time: the panel, the switcher, the cue sheet, sounds, lower thirds, the emergency keys.</summary>
    RunningTheShow,
    /// <summary>What goes on the screens: inputs, crops, web pages, decks, layers, parts, the multiview, the stream.</summary>
    Content,
    /// <summary>The venue: screen roles, edge blend, walls with bezels and gaps, frame rates and formats.</summary>
    TheRig,
    /// <summary>Everything that drives the show from outside: the phone, Companion, OSC, devices, the install's clock.</summary>
    Control,
    /// <summary>The computer: performance, the watchdog, the files on the stick, the small things.</summary>
    TheMachine,
}

/// <summary>
/// One topic of the Help catalogue: what it is called, where it sits in the workflow, how it
/// works (the long explanation), what to do in order, the words on the wire, the pages it lives
/// on (shell page headers, so a topic can open its page) and the words a search finds it by.
/// </summary>
public sealed record HelpTopic(
    string Id,
    HelpGroup Group,
    string Title,
    string Where,
    string Body,
    IReadOnlyList<string> Steps,
    string Wire,
    IReadOnlyList<string> Pages,
    IReadOnlyList<string> Keywords)
{
    public bool HasSteps => Steps.Count > 0;
    public bool HasWire => Wire.Length > 0;
}

/// <summary>A search hit: the topic, how strongly it matched, and the words around the first match.</summary>
public sealed record HelpHit(HelpTopic Topic, int Score, string Snippet);

/// <summary>
/// The Help page's catalogue — pure data, so the desk, the ? TIPS flyout, the docs and the tests
/// read the same guide. Every page named on a topic is a shell page header; the App's test pins
/// the two together. The long explanations live in <see cref="HelpBodies"/>.
/// </summary>
public static class HelpTopics
{
    public static IReadOnlyList<HelpGroup> Groups { get; } = Enum.GetValues<HelpGroup>();

    public static string GroupLabel(HelpGroup group) => group switch
    {
        HelpGroup.StartHere => "START HERE",
        HelpGroup.RunningTheShow => "RUNNING THE SHOW",
        HelpGroup.Content => "CONTENT",
        HelpGroup.TheRig => "THE RIG",
        HelpGroup.Control => "CONTROL",
        _ => "THE MACHINE",
    };

    public static string GroupBlurb(HelpGroup group) => group switch
    {
        HelpGroup.StartHere => "the map of a show day: how it flows, the shell, the modes, the keys, what to do first.",
        HelpGroup.RunningTheShow => "show time: the panel, the switcher, the cue sheet, sounds, names on screen, the emergency keys.",
        HelpGroup.Content => "what goes on the screens: inputs, crops, web pages, decks, layers, show parts, the multiview, the stream.",
        HelpGroup.TheRig => "the venue: what each screen is for, projectors that blend, walls with bezels and gaps, rates and formats.",
        HelpGroup.Control => "driving the show from outside: the phone, Companion, OSC, an Arduino, the install's clock.",
        _ => "the computer: performance, the watchdog, what is on the stick, the small things.",
    };

    private const string KeysBody =
        "F1–F12 — apply saved looks (Looks page).\n" +
        "⇧F5 — OUTPUTS ON: open the output windows.   ⇧F6 — OUTPUTS OFF: close them (on an output window: Esc twice within a second).\n" +
        "⇧F7 — IDENTIFY: flash screen numbers.   ⇧F8 or Space — BLACKOUT toggle.\n" +
        "Page Down / Page Up — the clicker list next / back (armed on the Cues page or the Show panel); with a deck on air its pages turn first.\n" +
        "Enter (RUN, armed) — GO on the cue stack; ↑ ↓ move the standby; Esc cancels a confirm, Esc twice is STOP ALL.\n" +
        "On the output windows: Esc ×2 close · Space or B blackout · I identify · F1–F12 looks · PgDn / PgUp presenter.\n" +
        "Every key acts once per press — holding a key down never repeats it.";

    private const string FlowBody =
        "Patterns is one program that does five jobs, and the rail on the left puts them in the order a show happens.\n\n" +
        "BUILD is where the pictures come from: test patterns and walls, media (files, folders, NDI, capture cards, web pages, PDF and PowerPoint decks), overlays (message, clock, countdown, ticker, logo), lower thirds, particles and effects, branding, the library. Everything here edits the PREVIEW while EDIT SAFE is open — the audience keeps seeing the program until you TAKE.\n\n" +
        "PLAN turns pictures into a show. A look is a snapshot of the whole picture — the program and every screen's own picture, the overlays, the countdown, the lower third on air — saved under a name and an F-key. A cue is one line of the running order: a look plus any actions (a stinger, a lower third, a screen on its own, a web key, a line to a device), with a planned time and, if you like, an auto-follow. The caller's stack is the show in order; the clicker list is what a presenter's clicker steps through. The Install page is the same idea for a site nobody sits at: looks on a rota, adverts, announcements.\n\n" +
        "SETUP is the rig: screens (real ones the machine sees, or planned ones you draw at home and adopt at the venue), canvases joined across displays, walls with bezels and gaps, edge blend across projectors, audio devices and the master clock, NDI sends, the stream, remote control (the phone, Companion, OSC) and the Interactive area (an Arduino, devices over IP).\n\n" +
        "SHOW is the day: the Show panel beside the switcher — the cue strip, the looks, each screen on its own, VOGs and stingers, lower thirds — and the Run page for a caller reading from a metre. ADMIN is the machine: health, the watchdog, this help.\n\n" +
        "Three ideas hold it together. The show file: one file with everything in it, portable on the stick beside the exe. The action layer: every change — from the desk, a cue, the wire, OSC, Companion, a device or the schedule — is the same action with its origin, so the journal reads what happened and who did it, a remote can never do something the desk could not, and recovery after a crash puts the show back where it was. Program and preview: what the audience sees is the program; EDIT SAFE opens a sandbox where the next picture is built and checked (on the PREVIEW pane, on a multiview with REVIEW), and TAKE swaps it to air — a screen sent on its own, or locked, is left alone by the swap.";

    public static readonly IReadOnlyList<HelpTopic> All = new[]
    {
        // ---- START HERE ------------------------------------------------------------------
        new HelpTopic("how-a-show-flows", HelpGroup.StartHere,
            "How a show flows through Patterns",
            "Read this first. Every other topic is one stop on this road, and the groups on the rail are its stages in order.",
            FlowBody,
            new[]
            {
                "BUILD the pictures (Pattern, Media, Overlays, Lower thirds) with EDIT SAFE open; TAKE what should be on air.",
                "Save each picture as a look (PLAN → Looks), with an F-key for the ones you press most.",
                "Write the running order as cues (PLAN → Cues): a look, its actions, a planned time; import the sheet from CSV or Excel.",
                "Set up the rig (SETUP → Screens…): at home with planned screens, at the venue adopt them; then audio, NDI, the stream, the remote.",
                "Run it from the Show panel (SHOW → Panel), or hand the caller the Run page.",
                "Before doors, read the Machine page (ADMIN) and run the super-check.",
                "Short of time? BUILD → Assistant drafts the screens, the looks, the cues and the lower thirds from a description of the day; APPLY what you want, then finish by hand.",
            },
            "Every verb a remote can send is in docs/REMOTE.md. STATE (the STATUS verb) carries the whole show as JSON, so a controller can read what a key should say.",
            new[] { "Panel", "Looks", "Cues", "Screens", "Machine", "Assistant" },
            new[] { "overview", "map", "start", "begin", "first", "stages", "groups", "action layer", "journal", "show file", "program", "preview", "edit safe", "take", "look", "cue", "rig", "how it works" }),

        new HelpTopic("shell", HelpGroup.StartHere,
            "The shell: five groups, the strip, ? TIPS, the resizable desk",
            "The rail is the map of a show day; the strip is the map of a group. Learn these two and every page is two clicks away.",
            HelpBodies.Shell,
            new[]
            {
                "Pick the group on the rail (SHOW · PLAN · BUILD · SETUP · ADMIN); the strip lists its pages and remembers the one you were on.",
                "Press ? TIPS on the strip for this page's explanations — or tick Show hints on the pages to keep them inline.",
                "Drag the divider between the page and the screens, or the handle between PROGRAM and PREVIEW; ◧ WIDE folds the screens to a strip whose width the divider sets too — on the Machine and Help pages as well.",
                "SHOW CONTROLS under the wall: the message, the clock, the countdown and the audio track's level, each behind SEND.",
            },
            "",
            new[] { "Help" },
            new[] { "rail", "groups", "strip", "tabs", "pages", "tips", "hints", "divider", "splitter", "wide", "layout", "show controls", "send", "resize", "settings column", "pop-out", "pop out", "column", "selected cue", "selected screen", "selected element", "close", "strip", "admin", "machine page", "drag" }),

        new HelpTopic("modes", HelpGroup.StartHere,
            "PREP · SHOW · RUN: what may leave the machine, and the caller's layout",
            "Set the mode when the machine arrives; check it first when nothing appears on a screen. RUN is a layout for the caller, not a mode.",
            HelpBodies.Modes,
            new[]
            {
                "PREP at the desk or at home: the output windows are refused, the sends stop, the stream is held — everything else works.",
                "SHOW at the venue: OUTPUTS ON (⇧F5) opens the windows, the sends run, the stream starts when it is armed.",
                "RUN for the caller: the LIVE strip, the wall, the stack and GO take the window; POP OUT for a second monitor, /run on a tablet.",
                "The Run area's room: drag the divider between the wall and the stack (the stack starts at about a third; the show remembers it); ▸ COLLAPSE TILES turns the wall's tiles into vertical title bars — the tally and the name on their side; a pause over any tile pops it up large, PGM and PVW side by side.",
                "Leaving RUN is refused while the stack is armed — disarm first.",
            },
            "OUTPUTS ON / OFF (⇧F5 / ⇧F6 on the desk). The mode itself is a desk choice, saved in the show.",
            new[] { "Panel", "Run", "Screens" },
            new[] { "mode", "prep", "show", "run", "outputs", "held", "ndi", "stream", "rehearsal", "layout", "pop out", "tablet", "/run", "audience" }),

        new HelpTopic("keys", HelpGroup.StartHere,
            "Keys on the desk and on the output windows",
            "One press, one action — a held key never repeats. The Run page adds Enter, ↑ ↓ and Esc.",
            KeysBody,
            Array.Empty<string>(),
            "A USB clicker is Page Down / Page Up. A Stream Deck or a phone sends the same verbs: LOOK <n>, NEXT, PREV, BLACKOUT TOGGLE, CUE GO.",
            new[] { "Panel", "Run", "Looks" },
            new[] { "keyboard", "shortcut", "hotkey", "f-key", "f1", "f5", "f6", "f7", "f8", "f12", "space", "blackout", "page down", "page up", "clicker", "enter", "esc", "escape", "identify", "repeat" }),

        new HelpTopic("workflow", HelpGroup.StartHere,
            "The workflow in one page: from a blank machine to a show that runs",
            "The order of a day, group by group. When something feels out of place, this is the list to check against.",
            HelpBodies.Workflow,
            new[]
            {
                "Build the pictures (BUILD) and save them as looks (PLAN → Looks).",
                "Write the cues (PLAN → Cues): import the sheet, give each cue its look and its time.",
                "Rig the venue (SETUP → Screens, Audio, NDI, Stream, Remote): OUTPUTS ON, IDENTIFY, walk the room.",
                "Rehearse from the Show panel with EDIT SAFE open: TAKE, GO, the VOGs.",
                "Run: arm the stack and GO from the Run page; the journal and the recovery place keep the show safe.",
            },
            "",
            new[] { "Pattern", "Looks", "Cues", "Screens", "Panel" },
            new[] { "workflow", "order", "day", "checklist", "first", "then", "rehearsal", "plan", "build", "rig", "steps" }),

        new HelpTopic("prep", HelpGroup.StartHere,
            "Before the show: PREP mode and planned screens",
            "Programming before the rig exists — at home, in the office, on the train. The show arrives at the venue finished; the venue only adopts the screens.",
            HelpBodies.Prep,
            new[]
            {
                "MODE → PREP: outputs are refused, the sends and the stream stay quiet.",
                "Screens page: + PLANNED SCREEN for each display or wall with its size and label; join canvases, set bezels, gaps and blend.",
                "Build looks and cues against the planned screens; the switcher tiles and the multiview show them.",
                "At the venue: ADOPT each planned screen onto the real display; MODE → SHOW; OUTPUTS ON.",
            },
            "",
            new[] { "Screens", "Panel", "Looks" },
            new[] { "prep", "planned", "adopt", "home", "office", "before", "rehearsal", "without hardware", "virtual", "plan the rig", "pre-programming" }),

        // ---- RUNNING THE SHOW ------------------------------------------------------------
        new HelpTopic("show-panel", HelpGroup.RunningTheShow,
            "The Show panel as the control surface",
            "Show time: the one page the operator stays on, beside the switcher. Everything built on PLAN and BUILD is pressed from here.",
            HelpBodies.ShowPanel,
            new[]
            {
                "ARM the stack. STANDBY names the cue GO fires, NEXT the one after; ▲ ▼ move the standby, HOLD stops everything firing.",
                "A LOOK tile puts the look on air (into the preview while EDIT SAFE is open); PVW loads it into the preview whatever the mode.",
                "SCREENS — EACH ON ITS OWN: pick a look in a row and → THIS SCREEN puts its picture on that screen alone; PROGRAM puts the screen back; LOCK keeps it.",
                "PROGRESSION: the VT clock while a clip plays (what is left, red for the last ten seconds, ⏭ LAST 10 s for a rehearsal, ⟲ RESTART); NEXT / BACK step the clicker list or a deck; the line also reads a counting auto-follow and the playlist's part.",
                "Then the VOG and STINGER chips with ■ Stop and the STING HOLD banner right under them, LOWER THIRDS and PEOPLE — three to a row, each lit red while it is on air or green while it is in the preview, with a line under the name that reads the tally — the audio track, break music, FREEZE / FADE / LOOK BACK and REVIEW.",
            },
            "CUE GO · CUE STANDBY NEXT / PREV · CUE HOLD ON / OFF · CUE ARM ON / OFF · LOOK <name> · SCREEN <n> LOOK <name> · SCREEN <n> PROGRAM · LOCK <n> ON · NEXT / PREV · VIDEO END · VIDEO RESTART · STINGER <name> · LOWERTHIRD <name> · STOPALL",
            new[] { "Panel" },
            new[] { "panel", "show panel", "cues", "go", "hold", "arm", "standby", "next", "looks", "pvw", "screens", "own", "program", "progression", "clicker", "control surface", "operator", "chips", "lit", "tally", "three to a row" }),

        new HelpTopic("video-clock", HelpGroup.RunningTheShow,
            "The VT clock: what is left of the clip on air, the ten-second out, the rehearsal's skip",
            "Show time, whenever a video plays: the caller reads how long it is and what is left from the panel, the Run strip or the phone, and a rehearsal jumps to the end of it without waiting.",
            HelpBodies.VideoClock,
            new[]
            {
                "Put a clip on air — the program's media, a playlist video, a video stinger — and read the row under PROGRESSION on the panel: VT name · 1:02 / 3:30 · 2:28 left.",
                "The Run strip's chip reads VT 2:28; the phone's SHOW tab, a Companion key ($(patterns:video_chip)) and OSC (/patterns/state/video/remaining) read the same seconds.",
                "In the last ten seconds the row, the chip and the key go red with OUT IN 7 — the caller's word.",
                "Rehearsal: ⏭ LAST 10 s (VIDEO END on the wire, VIDEO END 30 for thirty) jumps to the end; the out is heard and what follows the clip happens for real.",
                "⟲ RESTART (VIDEO RESTART) plays the clip from the top; a cue can do both (Video — jump to its last seconds, Video — restart).",
            },
            "VIDEO END [seconds] · VIDEO RESTART (VT and CLIP are aliases) · /patterns/video/end [seconds] · /patterns/video/restart · STATE video{…}",
            new[] { "Panel", "Run", "Media" },
            new[] { "vt", "video", "clip", "clock", "remaining", "left", "length", "duration", "how long", "ten seconds", "out", "rehearsal", "skip", "end", "restart", "top", "countdown", "caller" }),

        new HelpTopic("switcher", HelpGroup.RunningTheShow,
            "The switcher: PROGRAM, PREVIEW, EDIT SAFE, TAKE and the wall",
            "Under every page in SHOW and BUILD: the audience's picture on the left, the one you are building on the right, and a tile per screen between them.",
            HelpBodies.Switcher,
            new[]
            {
                "Open EDIT SAFE: the editors now change the PREVIEW; the program is frozen for the audience.",
                "Build the next picture; check it on the PREVIEW pane, or with REVIEW on a multiview.",
                "TAKE swaps it to air with the show's transition; CUT does it at once — where the picker beside them says: ALL ARMED, FOCUSED (the tile you clicked; the PGM tile means every armed screen), TICKED or TICKED GROUPS; everything outside the choice keeps its picture like an un-armed tile, and the next full TAKE lifts it.",
                "ARM off on a tile keeps that target through the next TAKE; LOCK keeps it through looks, cues and stingers too.",
                "SEND on a tile STAGES the preview there: that tile's PVW shows it, the room does not, and the tile is focused — so CUT or TAKE with FOCUSED puts it up on that tile and nowhere else. SEND TO TICKED is the live one.",
                "→ PVW on a tile pulls what that target is showing on air back into the preview to change; on the PGM tile it pulls the programme itself. The tile stays focused, so SEND stages it straight back where it came from.",
                "OWN gives a tile its own editable picture.",
                "The tick on a tile is there with or without EDIT SAFE: it joins SEND TO TICKED, and the Show panel's FADE TO BLACK on THE TICKED SCREENS or THE TICKED GROUPS.",
            },
            "LOOK <name> · SCREEN <n> LOOK <name> · SCREEN <n> PROGRAM · LOCK <n> ON / OFF · BLACKOUT ON / OFF. TAKE and CUT are desk keys; the phone remote has them too.",
            new[] { "Panel", "Pattern", "Screens" },
            new[] { "switcher", "program", "preview", "pgm", "pvw", "edit safe", "sandbox", "take", "cut", "arm", "lock", "send", "own", "tile", "wall", "transition", "tally", "screen" }),

        new HelpTopic("cue-sheet", HelpGroup.RunningTheShow,
            "The caller's home: a running order, the clock, auto-follow",
            "PLAN → Cues is where the day is written; the Run page and the panel's cue strip are where it is read. The sheet you were given becomes the stack.",
            HelpBodies.CueSheet,
            new[]
            {
                "IMPORT the running order from CSV or Excel (TEMPLATE gives the columns) — or + CUE by hand.",
                "Give each cue its look, any actions, a planned start or length, and notes for the caller.",
                "Mark breaks, lunch and the end: the clock then says what is early or late as the day runs.",
                "AUTO on a cue fires the next one after its seconds; CANCEL on the strip stops one counting.",
                "ARM the stack and GO — from the Run page, the panel, the phone, Companion or a device.",
                "A standby cue whose look carries a clip is pre-rolled: the decoder opens it and holds its first frame, silent, so GO lands on the picture — PRE-ROLLED on the Run strip; PRE-ROLLING… while it opens; CLIP NOT OPEN when the decoder limit is taken.",
                "Drag a step by its grip (⠿) to reorder it; the waits stay where they are, so the cue keeps the timing you built.",
                "After N s on a step waits that long after the step above it. All zero — the default — is one change on the screens, as it always was.",
                "The next GO on a list drops whatever the cue before it left waiting, and so do STOP ALL, disarming the list and resetting it.",
            },
            "CUE GO [id] · CUE STANDBY NEXT / PREV / <number> / <name> · CUE HOLD ON / OFF · CUE ARM ON / OFF · CUE LIST",
            new[] { "Cues", "Run", "Panel" },
            new[] { "cue", "cues", "stack", "sheet", "running order", "import", "csv", "excel", "planned", "time", "clock", "late", "early", "auto-follow", "follow", "break", "lunch", "go", "standby", "caller", "notes", "pre-roll", "preroll", "pre-rolled", "first frame", "clip not open", "decoder", "settings column", "pop-out", "selected cue", "delay", "wait", "after", "offset", "stagger", "reorder", "drag", "running order", "timing", "cue shape", "steps to come" }),

        new HelpTopic("vog-stingers", HelpGroup.RunningTheShow,
            "VOGs, stingers and staying up: sounds, clips and what happens after",
            "The Audio page holds the library; the Show panel fires it. A VOG plays over the show; a stinger takes the screens and then goes where you said.",
            HelpBodies.VogStingers,
            new[]
            {
                "Audio page: add a VOG (a sound over everything — the music ducks) or a STINGER (a clip, an effect or a held frame that takes the screens).",
                "Set what happens after a stinger: back, held for your TAKE, on to the next cue, or a look.",
                "Fire from the panel's chips, a cue, the phone, Companion, OSC or a device; STOP puts a held one back.",
                "STOP always works: a clip on the screens that nothing owns any more goes and the last show that was on comes back — the status line says which; a clip that stops moving is put back by itself after fifteen seconds.",
                "DUCK for an announcement from the room; STOP ALL stops every sound and never the outputs.",
            },
            "STINGER <n|name> · VOG <n|name> · STING <n|name> · STINGER STOP · DUCK ON / OFF · STOPALL · AUDIO PLAY / STOP",
            new[] { "Audio", "Panel" },
            new[] { "vog", "stinger", "sting", "clip", "sound", "duck", "ducking", "hold", "put it back", "stop all", "effect", "particles", "fractal", "audio track", "voice of god", "stuck", "stalled", "cannot stop", "orphan", "chip", "lit", "on air" }),

        new HelpTopic("lower-thirds-flow", HelpGroup.RunningTheShow,
            "Lower thirds: preview, sign-off, air, update, the show's default",
            "Names on screen during the show: the panel's chips, PVW FIRST for a sign-off, TAKE TO AIR, UPDATE ON AIR.",
            HelpBodies.LowerThirdsFlow,
            new[]
            {
                "Build designs on the Lower thirds page; ★ one as the show's default.",
                "On the panel a chip puts the design on air; with PVW FIRST it goes to the preview for a sign-off and TAKE TO AIR puts it on.",
                "A design's chip and a person's light red on air and green in the preview, the line under the name reading the phase and, for a person, the design that carries the name.",
                "EDITED means the design changed after it went on: UPDATE ON AIR pushes the change in place.",
                "■ Hide takes it off the way it was designed to leave.",
            },
            "LOWERTHIRD <n|name> · LOWERTHIRD OFF · LOWERTHIRD PREVIEW <n|name> · LOWERTHIRD TAKE · LOWERTHIRD UPDATE · PERSON <n|name>",
            new[] { "Panel", "Lower thirds" },
            new[] { "lower third", "lower thirds", "name strap", "caption", "preview", "sign-off", "take", "update", "default", "hide", "air", "edited", "lit", "chip", "person on air", "tally" }),

        new HelpTopic("people-library", HelpGroup.RunningTheShow,
            "The lower-thirds library: people ready to go",
            "Before the show, every speaker's name, role, company and photo typed once; during it, one press per person.",
            HelpBodies.PeopleLibrary,
            new[]
            {
                "Lower thirds page → LIBRARY: + PERSON with the name, role, company, photo and a note.",
                "A person's row and card show the name and the role; More — the company, the photo, the note — folds away, its header saying what is inside.",
                "On the panel, the PEOPLE chips put a person into the design on air (else the ★ default).",
                "A cue's Lower third — show with a person names the entry; the wire and Companion have the same.",
            },
            "PERSON <n|name> · LOWERTHIRD <design> WITH <person> · LOWERTHIRD PREVIEW WITH <person>",
            new[] { "Lower thirds", "Panel", "Cues" },
            new[] { "people", "person", "library", "speaker", "name", "role", "company", "photo", "headshot", "entry", "guest", "more", "drop-down", "fold" }),

        new HelpTopic("lower-thirds", HelpGroup.RunningTheShow,
            "Lower thirds: the designer — elements, keyframes, styles, media",
            "BUILD → Lower thirds is where a design is made; the flow above is how it is used at show time.",
            HelpBodies.LowerThirds,
            new[]
            {
                "Pick a preset or start blank; add text, shapes, a photo or a clip; drag the elements on the preview.",
                "+ PICTURE and + CLIP ask for the file as you press them, offering only what that element can draw.",
                "The file is imported — copied into media/ beside the show — so copying the show folder takes the pictures with it; one too big to carry is pointed at where it is and the desk says so.",
                "A file that did not travel is said in red on the element and warned about by the cue checks before doors; a clip on the designer's stage shows its name — PVW the design to watch it play.",
                "The preview and its timeline stay at the top of the page while the rest scrolls under them; the line beside PREVIEW names the design and says when it is on air.",
                "Keyframes give the way in and out; styles give the type, the colours and the edges.",
                "SAVE the design; EXPORT to share it as a file; ★ makes it the show's default.",
            },
            "",
            new[] { "Lower thirds" },
            new[] { "lower third", "designer", "design", "keyframe", "animation", "element", "text", "photo", "style", "preset", "export", "import", "graphics", "preview", "pinned", "scroll", "settings column", "pop-out", "selected element", "picture", "image", "clip", "video", "media", "file", "headshot", "missing", "not found", "travelled", "usb", "stick", "copy", "media folder" }),

        new HelpTopic("break-music", HelpGroup.RunningTheShow,
            "Break music (Spotify): the room between sessions",
            "The Audio page connects and picks; the Show panel plays, pauses and skips; a look can start or pause it.",
            HelpBodies.BreakMusic,
            new[]
            {
                "Audio page: CONNECT Spotify, choose the device in the room, add playlists or tracks (BROWSE, SEARCH).",
                "Panel: a chip plays an entry; ▶ ❚❚ ⏭ and the level; a stinger ducks it, STOP ALL pauses it.",
                "A look can carry 'play this' or 'pause', so a walk-in look starts the music by itself.",
            },
            "MUSIC PLAY [n|name] · MUSIC PAUSE · MUSIC NEXT · MUSIC VOL <0–100>",
            new[] { "Audio", "Panel", "Looks" },
            new[] { "spotify", "music", "break", "walk-in", "playlist", "track", "pause", "skip", "volume", "level", "duck", "room" }),

        new HelpTopic("audio-playlist", HelpGroup.RunningTheShow,
            "The audio playlist: a bed of tracks and folders, in order or shuffled",
            "The Audio page builds the list; the Show panel, a cue, the phone and a Stream Deck play, skip and stop it while the show runs.",
            HelpBodies.AudioPlaylist,
            new[]
            {
                "Audio page → AUDIO PLAYLIST: + FILES for tracks, + FOLDER for a whole folder of them (its files play after the rows, in name order, and a file dropped in live is seen within half a minute).",
                "Name a row if the file's name is not what the desk should read; ▲ ▼ order them; tick Shuffle (RESHUFFLE deals a new order) and Loop the list.",
                "▶ PLAY starts the list where it stopped (or the first row); ⏭ NEXT and ⏮ PREV step it; ▶ on a row plays that track now; ■ STOP.",
                "The Show panel's AUDIO block has the same keys with the line: 3/12 walk-in — 1:02 / 3:30 · next: intro.",
                "A cue's Play audio names a track (or none for the list), Audio — next / previous track step it; a VOG ducks it, a stinger fades it, STOP ALL stops it.",
            },
            "AUDIO PLAY [n|name] · AUDIO NEXT · AUDIO PREV · AUDIO STOP · AUDIO VOL <0–125> · /patterns/audio/play [n|name] · /patterns/audio/next · /patterns/audio/prev · STATE audio{…}",
            new[] { "Audio", "Panel" },
            new[] { "audio", "playlist", "track", "tracks", "folder", "music bed", "walk-in", "shuffle", "loop", "next", "previous", "skip", "volume", "duck", "outputs", "hdmi" }),

        new HelpTopic("transitions", HelpGroup.RunningTheShow,
            "Transitions: dissolve, dip, wipe, push, the brand stinger, a reactive matte",
            "How every recall the desk makes arrives on the screens — chosen once for the show, or named by one cue for itself.",
            HelpBodies.Transitions,
            new[]
            {
                "SETUP → Screens → TRANSITIONS: the tick and the time, then HOW — Dissolve, Dip, Wipe, Push, Brand stinger, Reactive.",
                "A wipe and a push ask which way they travel; a wipe and a reactive matte have an edge to soften.",
                "A dip takes the brand's background colour unless you untick and name one; a bright dip too soon after the last flash is drawn as a dissolve instead.",
                "The brand stinger is made from BUILD → Branding — the primary, the secondary, the background and the logo — so there is no clip to prepare or lose.",
                "One recall can carry its own: a cue's look transition box takes 'cut', a fade in milliseconds, or a name — wipe left 600, dip, stinger, reactive vortex 1200.",
            },
            "LOOK [name] [cut | ms | dissolve | dip | wipe | push | stinger | reactive [scene] | left | right | up | down]",
            new[] { "Screens", "Cues" },
            new[] { "transition", "dissolve", "crossfade", "dip", "dip to black", "wipe", "push", "stinger", "brand stinger", "reactive", "matte", "take", "edge", "softness" }),

        new HelpTopic("freeze-fade", HelpGroup.RunningTheShow,
            "Freeze, the timed fade, the previous look, earlier versions",
            "The emergency and finesse keys of a show operator, on the panel and on the wire.",
            HelpBodies.FreezeFade,
            new[]
            {
                "FREEZE holds every output's picture while you change anything behind it; press again to release.",
                "FADE TO BLACK / FADE UP over the seconds typed beside them, where the picker says: EVERY SCREEN is a blackout with a fade of its own time; THE FOCUSED SCREEN, THE TICKED SCREENS and THE TICKED GROUPS fade that part of the rig alone while the rest keeps its picture (the ticks are on every wall tile, with or without EDIT SAFE).",
                "WITH THE SOUND (on by default, saved with the show): a fade that leaves the whole rig dark takes the music and a clip's soundtrack down with it over the same seconds, and the fade up brings them back; a VOG and a stinger's own sound play through; BLACKOUT covers everything and never touches the sound.",
                "A cue's Fade to black / Fade up land in the same places (every screen, the focus, the ticks, Screen 2, Group A) for their seconds; the wire and Companion have them per screen and per group.",
                "LOOK BACK puts the previous look back on air; again swaps the two.",
                "An earlier build is a folder on the stick: run it and the show file opens as it was.",
            },
            "FREEZE ON / OFF / TOGGLE · FADE [seconds] [SCREEN n | GROUP A | FOCUSED | TICKED | GROUPS] · FADE UP [seconds] [where] · LOOKBACK [cut|ms] · BLACKOUT ON / OFF / TOGGLE · STATE black{count,text,audio}",
            new[] { "Panel" },
            new[] { "freeze", "hold frame", "fade", "black", "fade to black", "fade up", "fade a screen", "fade a group", "audio fade", "with the sound", "look back", "previous", "undo", "version", "roll back", "emergency" }),

        new HelpTopic("review", HelpGroup.RunningTheShow,
            "Review on the multiview: the next picture on the monitor wall",
            "A sign-off step between EDIT SAFE and TAKE: the preview full-frame on every multiview, the audience's screens untouched.",
            HelpBodies.Review,
            new[]
            {
                "Build the next picture with EDIT SAFE open.",
                "THE PREVIEW ON EVERY MULTIVIEW (the panel or the Pattern page): every multiview draws it with a REVIEW chip.",
                "TAKE when it is signed off; switch REVIEW off.",
            },
            "REVIEW ON / OFF / TOGGLE",
            new[] { "Panel", "Pattern" },
            new[] { "review", "multiview", "preview", "sign-off", "monitor wall", "check", "approve" }),

        new HelpTopic("multiview-tally", HelpGroup.RunningTheShow,
            "The multiview's tally: PROGRAM, the next TAKE, and which screen",
            "On a monitor wall the multiview says what is on air, what the next TAKE brings and which screen a tile is — the operator's second pair of eyes.",
            HelpBodies.MultiviewTally,
            new[]
            {
                "SETUP → Multiview: tiles for the programme, the preview, screens and canvases, inputs.",
                "The PROGRAM / PREVIEW badges follow the switcher; a screen's tile names the screen and its outputs.",
                "Tick where the wall shows — a screen, an NDI sender, the stream — or watch it at /multiview on the phone.",
            },
            "",
            new[] { "Multiview", "Screens", "NDI" },
            new[] { "multiview", "tally", "badge", "program", "preview", "tile", "monitor wall", "which screen", "outputs" }),

        new HelpTopic("audio-monitor", HelpGroup.RunningTheShow,
            "What the desk is listening to: one picture's sound, not all of them",
            "Audio page: a clip on the programme, one on a confidence screen and one in the preview are three soundtracks — the desk plays one of them, and by default it is the programme.",
            HelpBodies.AudioMonitor,
            new[]
            {
                "Audio page → PROGRAMME OUT names the outputs the room hears: the USB or dedicated interface feeding the PA, the HDMI screens, the machine's own. An interface that is not plugged in falls back and the page says so in red.",
                "MONITOR — WHAT THE OPERATOR HEARS names a second output that is not one of those: headphones, a spare card, the machine's own speakers. That is what makes auditioning possible at all.",
                "WHAT THE DESK IS LISTENING TO: the programme (the default), the preview, one output on its own, or nothing.",
                "The programme is never taken away to make room: a clip on air plays on the programme's outputs whatever you are listening to. With no monitor output named there is nowhere to audition that is not the room, so nothing else plays and the line says so.",
                "An output that is following the show sounds like the show; one with its own clip plays its own.",
                "The audio playlist, VOGs, stingers and the soundcheck tone are the show's own sound and always go to the programme's outputs.",
            },
            "",
            new[] { "Audio", "Media", "Panel" },
            new[] { "monitor", "listening", "audio", "sound", "overlapping", "mix", "preview sound", "clip audio", "headphones", "silent", "too loud", "usb audio", "sound card", "interface", "pgm audio", "programme out", "device", "output device", "not plugged in", "scarlett", "dante", "audition", "cans" }),

        new HelpTopic("multiview-walls", HelpGroup.RunningTheShow,
            "The monitor walls: two of them, arranged how you want, on any output",
            "SETUP → Multiview: up to two monitor walls the show holds, each shown on however many outputs you tick — a spare display, an NDI send, the stream.",
            HelpBodies.MultiviewWalls,
            new[]
            {
                "SETUP → Multiview → + ADD A MULTIVIEW: it arrives filled from the rig — the programme, the preview, every screen and a clock.",
                "Layout: programme and preview large with the rest beneath (the default), one large, one large down the left, or an even grid.",
                "The large tiles are the first in the list — drag a tile to the top with its grip to watch it.",
                "WHERE IT SHOWS: tick a screen, an NDI sender or the stream. A feed is pointed at its own picture for you.",
                "The programme stays the programme: a wall on a spare screen does not stop the show being a pattern, a clip or a page.",
            },
            "",
            new[] { "Multiview", "Screens", "NDI", "Stream" },
            new[] { "multiview", "monitor wall", "layout", "two multiviews", "second multiview", "where is multiview", "confidence monitor", "gallery", "tiles", "arrange", "large" }),

        // ---- CONTENT ----------------------------------------------------------------------
        new HelpTopic("inputs", HelpGroup.Content,
            "Many inputs at once: every source mounted, a pool to distribute",
            "BUILD → Media: the sources the show draws on — files, NDI, capture cards, web pages, decks — each mounted once and sent anywhere.",
            HelpBodies.Inputs,
            new[]
            {
                "Media page: add each source; it mounts and stays mounted while the show runs.",
                "Put a source on the program, on a screen's own picture, in a layer, or on a multiview tile.",
                "PiP: a second live input over the picture, cropped from any side.",
            },
            "SECTION <n|name> for playlist parts; the DECK and WEB verbs for decks and pages.",
            new[] { "Media", "Pattern" },
            new[] { "input", "inputs", "source", "media", "ndi", "capture", "hdmi", "sdi", "camera", "pool", "pip", "mount", "file", "folder", "playlist", "video" }),

        new HelpTopic("crop", HelpGroup.Content,
            "The area of interest: crop, mirror and turn any input",
            "Between the source and the screen: a Teams window without its furniture, a slide without the notes, a camera the right way up.",
            HelpBodies.Crop,
            new[]
            {
                "Media page → the source's AREA OF INTEREST: drag the box on the preview or type the percentages.",
                "Mirror and rotate as the room needs; the crop rides with the source into every look.",
                "The same box on a layer or a PiP.",
            },
            "",
            new[] { "Media", "Pattern" },
            new[] { "crop", "area of interest", "aoi", "cut", "trim", "mirror", "flip", "rotate", "turn", "teams", "slides", "furniture", "zoom" }),

        new HelpTopic("web-pages", HelpGroup.Content,
            "Web pages: YouTube, Google Slides, PowerPoint online, keys, clicks and cues",
            "A page in the engine like any other source — driven from the preview, a cue, the wire or a clicker, never a browser window the audience can see.",
            HelpBodies.WebPages,
            new[]
            {
                "Media page: + WEB PAGE with the address; presets for YouTube, Google Slides and Office 365.",
                "Click and type on the PREVIEW pane to drive it; show or hide the cursor on the outputs.",
                "A cue's Web — key or action (next, present, play…), the WEB verbs on the wire, Companion's keys.",
                "The clicker's NEXT / PREV drive the page on air when it is a deck.",
            },
            "WEB KEY <key|action> [ON <page>] · WEB NEXT / PREV / PRESENT / PLAY / PAUSE… · WEB CLICK <x> <y> · WEB TYPE <text> · WEB RELOAD · WEB OPEN <address>",
            new[] { "Media", "Cues", "Panel" },
            new[] { "web", "page", "browser", "youtube", "google slides", "office", "powerpoint online", "key", "click", "type", "cursor", "url", "address", "present", "webview" }),

        new HelpTopic("pdf-decks", HelpGroup.Content,
            "PDF decks: full frame, the click-through, the cue stack resumes",
            "A presentation as the pages of a PDF: on air at its own aspect, turned by the clicker, handing back to the stack at the end.",
            HelpBodies.PdfDecks,
            new[]
            {
                "Media page: + DECK with the PDF; it renders at the screen's size.",
                "Put it on air (a look or a cue); NEXT / PREV turn its pages, the panel and the phone show the page.",
                "Past the last page the caller's stack resumes with GO on the standby cue when the deck asks for it.",
            },
            "DECK NEXT / PREV / FIRST / LAST / PAGE <n> · NEXT / PREV",
            new[] { "Media", "Panel", "Cues" },
            new[] { "pdf", "deck", "presentation", "slides", "page", "click-through", "clicker", "aspect", "letterbox", "resume" }),

        new HelpTopic("powerpoint", HelpGroup.Content,
            "PowerPoint decks through LibreOffice Impress",
            "A .pptx becomes a deck like a PDF: converted once by LibreOffice on this machine, then the same click-through.",
            HelpBodies.PowerPoint,
            new[]
            {
                "Install LibreOffice (the portable build is fine); the Media page says where it found it.",
                "+ DECK with the .pptx, .key or .odp; the conversion runs and the pages appear.",
                "Then exactly as a PDF deck: on air, NEXT / PREV, the stack resumes.",
            },
            "DECK NEXT / PREV / FIRST / LAST / PAGE <n>",
            new[] { "Media" },
            new[] { "powerpoint", "pptx", "keynote", "impress", "libreoffice", "convert", "deck", "slides", "odp" }),

        new HelpTopic("layers", HelpGroup.Content,
            "Layers and dragging: two media layers over any picture",
            "BUILD → Layers: a logo, a camera, a page or a screen over the picture — sized, cropped, edged and dragged on the preview.",
            HelpBodies.Layers,
            new[]
            {
                "Layers page (BUILD, before the library and the assistant): pick the source for layer 1 and layer 2 (an image, a video, NDI, capture, a web page, a screen); EDITING TARGET says whose layers.",
                "Size, crop, border, corners and opacity; drag the layer on the PREVIEW pane.",
                "The overlays (message, clock, countdown, ticker) drag the same way.",
                "A drop is told from the nearest anchor: nothing moves, but the Nudge sliders come back to counting from a corner or an edge that is still there on a wall of another shape and at another size.",
                "Place X / Y (px) under the Nudge sliders reads where the box is on the canvas the PREVIEW pane shows — type in it to place it exactly.",
                "Position means that position: picking one from the dropdown puts the element there and drops the nudge; RESET TO POSITION does the same without changing the position."
            },
            "",
            new[] { "Layers", "Overlays" },
            new[] { "layer", "layers", "overlay", "drag", "move", "position", "border", "corner", "opacity", "logo", "pip", "crop", "anchor", "nudge", "pixels", "px", "place", "sliders", "re-anchor", "centre point", "center point" }),

        new HelpTopic("parts-multiview-stream", HelpGroup.Content,
            "Show parts, the multiview and streaming",
            "The playlist's sections are the show's parts; the multiview is the monitor wall; the stream is the same picture on the network.",
            HelpBodies.PartsMultiviewStream,
            new[]
            {
                "Media page: sections for the playlist — a part per session; SECTION on the wire or a cue puts one on air.",
                "Pattern page → Multiview: the wall's tiles; send it to a screen or an NDI send.",
                "Stream page: up to two destinations; ARM, and it starts with the outputs.",
                "The stream's light sits at the foot of the rail on every page: OFF, UP…, LIVE, SLOW, FAULT — click it to open the Stream page.",
                "Start and stop it from the Show panel's SHOW CONTROLS, a cue, a look, the phone, OSC or a Stream Deck — not only from its own page.",
                "SLOW is the one to watch: the stream is up but not taking frames at the rate asked for. The wall looks perfect; the online audience does not.",
            },
            "SECTION <n|name> · STREAM ON / OFF",
            new[] { "Media", "Pattern", "Stream" },
            new[] { "playlist", "part", "section", "multiview", "stream", "streaming", "rtmp", "srt", "destination", "arm", "stream health", "slow stream", "dropped frames", "stream light", "rail", "buffering", "stream fault", "encoder" }),

        new HelpTopic("test-card", HelpGroup.Content,
            "The Patterns test card: one card for a rig day's first four questions",
            "BUILD → Pattern → Patterns test card: which screen this is, whether the pixels arrive one to one, where the edges went and what the processor has done to black, white and grey — on one picture, read from the ladder. What a brand-new install comes up on.",
            HelpBodies.TestCard,
            new[]
            {
                "Rig is the whole card and the one a new install shows; Pixel is the mapping half at twice the size for somebody up a ladder; Levels is the measurement half full frame.",
                "Read the edges first: both borders on all four sides means the signal arrives whole, and the ticks count the overscan in pixels.",
                "The five one-to-one fields are each exactly half lit — even textures at native resolution, moiré through any scaler, and which one breaks says whether the scaling is horizontal, vertical or both.",
                "The staircase and the clipping patches say what the chain threw away; the gamma solid that vanishes into the line field beside it is this display's gamma.",
                "Both of those only mean anything unscaled and unsharpened — through a scaler you are measuring the scaler.",
                "The card carries the Patterns mark itself, so the badge overlay stays off this one kind: two logos on a card is a mistake.",
            },
            "PATTERN TestCard",
            new[] { "Pattern", "Screens" },
            new[] { "test card", "testcard", "test pattern", "rig", "rig day", "one to one", "1:1", "pixel mapping", "overscan", "edges", "scaling", "scaler", "moire", "gamma", "greyscale", "grayscale", "staircase", "clipping", "crush", "black level", "white level", "which screen", "identify", "default", "first run", "startup", "processor", "led" }),

        new HelpTopic("badge", HelpGroup.Content,
            "The Patterns badge: a branded test card",
            "Branding → PATTERNS BADGE: the app's own mark — the test-card icon, PATTERNS and a line under it, in its neon colours — drawn by the engine over every test pattern, on by default in the middle of the lower third, so a test card names its maker at an expo or on a rig day; it travels with looks and keeps off a client's media unless asked.",
            HelpBodies.Badge,
            new[]
            {
                "Leave it on: every test pattern — the grid, the bars, the walls, the ramps, particles and fractals too — carries the badge in the middle of the lower third on every output, NDI send and the stream.",
                "The Patterns test card is the exception that needs no setting: it carries the mark inside itself, as part of the picture, so the badge stays off it rather than making a second one.",
                "Branding → PATTERNS BADGE: move it (nine anchors and a nudge, or drag it on the PREVIEW pane), size it as a share of the screen, set its opacity, put a venue's address or a stand number on the line under the name, or turn the line off.",
                "Tick 'On media too' only when a client's video, image, deck or web page should carry it; the multiview never does.",
                "A look saves the badge as it is (on, off, where): recall the look and the badge comes with it; OVERLAYS OFF from the Show panel, a cue or the wire takes it with the other overlays.",
            },
            "OVERLAYS OFF (takes the badge with the rest)",
            new[] { "Branding", "Overlays" },
            new[] { "badge", "patterns badge", "branding", "brand", "test card", "test-card", "logo", "watermark", "expo", "rig day", "advert", "maker", "wordmark" }),

        new HelpTopic("fractals", HelpGroup.Content,
            "The fractal studio: scenes by family, the view, the palette, the sound",
            "BUILD → Fractals: a living picture from pure maths as a pattern of its own, designed on its own page like the particles — scenes filed by family (Mandelbrot, Julia, Burning ship, Newton, Domain warp) and your own under Custom, the view, the palette (or the brand kit's colours), the sound it breathes with and the stings that surge through it.",
            HelpBodies.Fractals,
            new[]
            {
                "BUILD → Fractals: press USE IT so the Fractal is the editing target's pattern type and the page shows live in the preview; pick a scene from a family.",
                "Shape it: the family and a Julia's c, the zoom, the centre and the detail, the motion; a palette of two to five colours, or BRAND KIT for the client's.",
                "SOUND: This computer or an input, and how much — the level pulses the zoom, the lows drift the colours, the highs brighten (Windows only for the listening).",
                "Save it as a preset on the Pattern page and it comes back as a chip under Custom; STINGS adds an effect pulse to fire from a cue, an F-key or Companion.",
            },
            "PATTERN Fractal · STING <n> (an effect pulse)",
            new[] { "Fractals", "Pattern", "Audio" },
            new[] { "fractal", "fractals", "fractal studio", "mandelbrot", "julia", "burning ship", "newton", "domain warp", "sound-reactive", "sound reactive", "scenes", "scene", "effect sting", "brand palette", "generative", "living picture", "microphone", "capture card", "audio interface", "default input" }),

        new HelpTopic("reactive", HelpGroup.Content,
            "Reactive scenes: six curated pictures that move with the sound",
            "BUILD → Reactive: a tunnel, a kaleidoscope, plasma, rings, a vortex or a star field as a pattern of its own — drawn on the graphics card for the screens and on the CPU for NDI and the stream, in the client's brand colours, with whole-screen flashes limited for the room.",
            HelpBodies.Reactive,
            new[]
            {
                "BUILD → Reactive: press USE IT so a scene is the editing target's pattern type and the page shows live in the preview; press a chip for a scene.",
                "Shape it: speed, depth or warp, symmetry, rotation and brightness — a chip is a starting point and leaves your colours and sound settings alone.",
                "COLOUR: the show's brand kit by default, or two to five colours of your own.",
                "SOUND: This computer or an input, and how much — the scene still moves with no sound at all (the listening is Windows only).",
                "QUALITY names the sinks that draw on the CPU — NDI, the stream, the thumbnails; the outputs and the preview are unaffected.",
                "Whole-screen flashes are limited to three a second on every screen, in the engine, and cannot be turned off.",
            },
            "PATTERN Reactive · STING <n> (an effect pulse)",
            new[] { "Reactive", "Pattern", "Branding", "Audio" },
            new[] { "reactive", "visualiser", "visualizer", "scene", "scenes", "tunnel", "kaleidoscope", "plasma", "vortex", "star warp", "pulse", "rings", "sound-reactive", "sound reactive", "walk-in", "walk in", "ambient", "milkdrop", "avs", "winamp", "flash", "strobe", "photosensitive", "epilepsy", "seizure", "microphone", "mic", "line in", "capture card", "usb capture", "audio interface", "input", "default input" }),

        // ---- THE RIG ----------------------------------------------------------------------
        new HelpTopic("weather", HelpGroup.Content,
            "The weather chip: now, the rest of today, tomorrow",
            "Overlays → WEATHER: search for the venue, pick the view, and the engine draws the forecast on every screen like the clock — from MET Norway (free, credited) or Open-Meteo; the Show panel, a cue, the wire and Companion switch it live.",
            HelpBodies.Weather,
            new[]
            {
                "Overlays → WEATHER: type a town and a country, press SEARCH, pick the place — the name and the coordinates fill in (or type the coordinates for a venue the search does not know).",
                "Choose the view — Now, Rest of today, Tomorrow — the units, and where the chip sits; drag it on the PREVIEW pane like the clock.",
                "Leave the source on MET Norway unless the show needs Open-Meteo's commercial plan (paste its key); put a contact in — the sources ask who is calling.",
                "Show time: the Show panel's drawer (SHOW / HIDE, NOW / TODAY / TOMORROW), a cue's Weather on / off / view, WEATHER on the wire and OSC, a Companion key that reads the figure.",
            },
            "A look carries the chip and its view; the place and the source are the show's.",
            new[] { "Overlays" },
            new[] { "weather", "forecast", "temperature", "rain", "sun", "cloud", "tomorrow", "today", "place", "location", "venue", "met norway", "open-meteo", "degrees", "celsius", "fahrenheit", "overlay", "chip" }),

        new HelpTopic("assistant", HelpGroup.Content,
            "The assistant: a show drafted with you, applied by you",
            "BUILD → Assistant: say what the show is — the screens, the day, the overlays you want — and it proposes planned screens, looks, cues, lower thirds and the brand; APPLY puts a proposal into the show, nothing changes until you do. Optional: it needs the internet and your own Anthropic API key.",
            HelpBodies.Assistant,
            new[]
            {
                "Paste an Anthropic API key from your own account and press SAVE KEY — it is kept beside the settings file on this machine, never in a show file; FORGET takes it off.",
                "Say what you have and what you want (\"two screens, a walk-in at nine, a keynote at ten, a break at eleven — plan it\"), or press one of the starters; answer its questions.",
                "Read each proposal — a look, a cue, a lower third, a show plan — and press APPLY on the ones you want: planned screens land on the Screens page, looks on the Looks page, cues on the caller's stack, designs on the Lower thirds page.",
                "Then finish by hand what only you can do: pick the media files, the logo, the web addresses, adopt the planned screens onto the real displays, and run the Cues page's checks.",
            },
            "",
            new[] { "Assistant" },
            new[] { "assistant", "ai", "chat", "chatbot", "claude", "anthropic", "api key", "key", "plan", "draft", "propose", "proposal", "brief", "show plan", "help me build", "apply", "attach", "attachment", "screenshot", "photo", "spreadsheet", "running order", "notes", "pdf", "word", "powerpoint", "import a plan" }),

        new HelpTopic("screen-roles", HelpGroup.TheRig,
            "Screen roles — the groups of screens — locks and repeaters",
            "SETUP → Screens: the group each screen is in by what it is for — main, confidence, info, repeater, an NDI or stream feed — and whether looks and cues may touch it. Every wall tile's foot line reads the group, and a click there opens the page on that screen.",
            HelpBodies.ScreenRoles,
            new[]
            {
                "Read a screen's group on its wall tile's foot line: MAIN, CONF, INFO, REP with what it repeats, NDI or STREAM for a feed screen.",
                "Click the foot line: SETUP → Screens opens on that screen — give it a role: MAIN follows the show; CONFIDENCE and INFO keep their own picture; REPEATER copies the target under Mirror of.",
                "LOCK a screen (the wall, the panel, the wire) to keep its picture through looks, cues, TAKE ALL and stingers.",
                "NDI sends and the stream are feed screens of their own, made on the NDI and Stream pages; what they show is chosen there.",
            },
            "LOCK <n> ON / OFF / TOGGLE · SCREEN <n> ON / OFF · GROUP <letter> ON / OFF",
            new[] { "Screens", "Panel" },
            new[] { "role", "group", "groups", "main", "confidence", "info", "info desk", "infodesk", "lock", "locked", "repeater", "repeaters", "mirror", "follow", "independent", "stage monitor", "foyer", "feed", "ndi feed", "feed screen", "allocate", "settings column", "pop-out", "selected screen" }),

        new HelpTopic("edge-blend", HelpGroup.TheRig,
            "Edge blend beyond two projectors: rows, grids, corners, the audit",
            "SETUP → Screens → Edge blend: one wide picture across projectors that overlap; the audit says which joins are wrong.",
            HelpBodies.EdgeBlend,
            new[]
            {
                "Place the projectors' screens so they overlap by the real overlap.",
                "Edge blend: widths, curve and gamma per edge — a middle projector fades both sides, a grid's a side and a top or bottom.",
                "Read the audit: an overlap nobody fades, a join fading one side only, widths or curves that differ.",
            },
            "",
            new[] { "Screens", "Pattern" },
            new[] { "edge blend", "blend", "projector", "projectors", "overlap", "gamma", "curve", "feather", "soft edge", "grid", "corner", "audit" }),

        new HelpTopic("bezels-gaps", HelpGroup.TheRig,
            "Bezels and gaps: the wall the content spans",
            "A video wall's bezels and an LED wall's dead strips are part of the surface: the content spans them so a line stays straight.",
            HelpBodies.BezelsGaps,
            new[]
            {
                "Screens page → the wall: bezel or gap sizes in pixels; an LED wall's gaps as positions and sizes.",
                "The content lays out on the grown surface; the multiview and the tiles take the same shape.",
            },
            "",
            new[] { "Screens", "Pattern" },
            new[] { "bezel", "bezels", "gap", "gaps", "video wall", "led wall", "dead strip", "mullion", "span", "compensate" }),

        new HelpTopic("framerate", HelpGroup.TheRig,
            "Frame rate, display modes and capture formats",
            "SETUP → Screens and Media: the rate the outputs run at, the mode a display is set to, the format a capture card delivers.",
            HelpBodies.FrameRate,
            new[]
            {
                "Screens page: the output's display mode and rate; DIRECT output on a suitable card.",
                "Media page: the capture device's format and rate.",
                "Machine page: the rendered rate and the drops.",
            },
            "",
            new[] { "Screens", "Media", "Machine" },
            new[] { "frame rate", "fps", "hz", "display mode", "resolution", "refresh", "capture format", "direct output", "vsync", "drops", "stutter" }),

        // ---- CONTROL ----------------------------------------------------------------------
        new HelpTopic("remote", HelpGroup.Control,
            "Remote control: the phone, the wire, the tablet",
            "SETUP → Remote: switch it on and every device on the network has the show — the phone remote, /run for a caller, a TCP line for anything else.",
            HelpBodies.Remote,
            new[]
            {
                "Remote page: ON; open the address it shows on a phone.",
                "The phone's pages: Show, Cues, Looks, Screens, Audio, Lower thirds, Overlays, Setup — and ADMIN with a passcode.",
                "OVERLAYS: the clock (hours, seconds, date), the message's words, a countdown of minutes or to a time with its label, the logo, the PiP, the weather, every overlay off in one press.",
                "Allow remotes to arm only if you mean it; HELLO names a connection in the journal.",
            },
            "Every verb is in docs/REMOTE.md. STATUS · PING · HELLO <name> · CUE LIST · CLOCK 24 · MESSAGE <text> · COUNTDOWN <min> · COUNTDOWN TO <HH:mm> · OVERLAYS OFF",
            new[] { "Remote" },
            new[] { "remote", "phone", "tablet", "web remote", "tcp", "port", "network", "address", "url", "/run", "admin", "verb", "protocol", "wire", "overlays", "clock", "message", "countdown", "logo" }),

        new HelpTopic("companion-banks", HelpGroup.Control,
            "Companion and OSC: keys that fill themselves from the show",
            "A Stream Deck through Bitfocus Companion, or any OSC controller: drag the bank presets once and every look, person, VOG, part and cue you make afterwards labels its own key.",
            HelpBodies.CompanionBanks,
            new[]
            {
                "Install the module (integrations/companion-module-patterns); point it at the machine's address and port.",
                "In the connection's settings tick the groups of keys this desk uses — all looks, all patterns, clock, countdown, message, overlays, VOGs, stingers, lower thirds, people, screens, audio, presenter, install — and the preset list holds only those.",
                "Drag the bank presets — Looks, People, VOGs, Stingers, Parts, Screens, Upcoming cues: the keys label themselves and light on air.",
                "The Clock, Countdown, Message and Overlays groups drive the overlays from keys; the countdown key counts down, green while it runs and red when over.",
                "Feedbacks and variables for anything else; OSC gets the same addresses and the same feedback.",
            },
            "LOOK #<n>, PATTERN <kind> and the module's actions and feedbacks; /patterns/look/index/<n> and /patterns/state/… over OSC.",
            new[] { "Remote" },
            new[] { "companion", "stream deck", "bitfocus", "preset", "bank", "feedback", "variable", "osc", "touchosc", "key", "label", "module", "groups", "tick", "clock", "countdown", "message", "pattern" }),

        new HelpTopic("look-state", HelpGroup.Control,
            "Is the look still what is on the screens — and which screen is not",
            "A look key that says only \"on air\" cannot tell you somebody has changed the picture since. The desk now says both, on its own page, on the wire, over OSC and on a Stream Deck.",
            HelpBodies.LookState,
            new[]
            {
                "Three states, not two: the look is up and untouched, the look is up but the picture has moved since it was recalled, or it is not up.",
                "The Looks page and the Show panel have said PROGRAM · EDITED since round 17; the wire, OSC and Companion say it too now, off the same reading — two surfaces disagreeing about a fact is worse than neither having it.",
                "Per screen, which is the reading a rig with eight of them actually needs: the look is up, and screen 3 has gone its own way. Companion's screen_off_look lights the key that puts it back.",
                "A screen the look itself gave its own picture is NOT a screen that has gone its own way, and a locked screen never counts — keeping its picture through a recall is what LOCK means.",
                "Every screen also says what kind of picture it is drawing, so a key can light for \"screen 2 is on the test card\" where before only the programme's kind was on the wire.",
            },
            "STATE lookEdited · lookScreensOff · screens[].pattern · screens[].off · /patterns/state/look/edited · /patterns/state/look/screensoff",
            new[] { "Looks", "Panel", "Remote" },
            new[] { "look", "edited", "changed", "modified", "live", "on air", "tally", "companion", "stream deck", "streamdeck", "feedback", "highlight", "screen", "drifted", "off look", "per screen", "pattern per screen" }),

        new HelpTopic("osc", HelpGroup.Control,
            "OSC in and out",
            "SETUP → Remote: an OSC port in, feedback out to a host — lighting desks, TouchOSC, any controller that speaks addresses.",
            HelpBodies.Osc,
            new[]
            {
                "Remote page: OSC ON and the port; the feedback host and port.",
                "Send /patterns/… addresses (the table is in docs/REMOTE.md); read /patterns/state/… for every change.",
            },
            "/patterns/look <n|name> · /patterns/cue/go · /patterns/screen/<n>/look \"<name>\" · /patterns/blackout · /patterns/clock · /patterns/message \"text\" · /patterns/countdown 5 · /patterns/status",
            new[] { "Remote" },
            new[] { "osc", "udp", "address", "feedback", "touchosc", "lighting desk", "port", "qlab", "clock", "countdown", "message" }),

        new HelpTopic("interactive", HelpGroup.Control,
            "The Interactive area: Arduino, Raspberry Pi and devices over IP",
            "SETUP → Interactive: buttons, sensors and lights in the room join the show through the same action layer as everything else.",
            HelpBodies.Interactive,
            new[]
            {
                "Interactive ON; + ARDUINO (SERIAL) or + DEVICE OVER IP with the port or the address.",
                "Trigger rows: a device's line → a protocol line (BTN1 → CUE GO); or let the device speak the protocol as it is.",
                "The show speaks back as KEY VALUE lines; a cue's Device — send a line, DEVICE on the wire.",
            },
            "DEVICE <name|*> <text> (SEND is an alias). What a device hears and can say is in docs/ARDUINO.md.",
            new[] { "Interactive", "Cues" },
            new[] { "arduino", "raspberry pi", "serial", "usb", "tcp", "udp", "device", "button", "sensor", "relay", "gpio", "trigger", "interactive", "lamp" }),

        new HelpTopic("installs", HelpGroup.Control,
            "Permanent installs: the clock runs the site",
            "PLAN → Install: a shop window, a hotel lobby, a museum wall — the machine nobody sits at, looked after from somewhere else.",
            HelpBodies.Installs,
            new[]
            {
                "Build the looks; + PROGRAMME rows on the rota, + ADVERT rows at their times, + ANNOUNCEMENT rows by the clock or by hand.",
                "TODAY shows the day as the clock will run it; tick Schedule on.",
                "Set the admin passcode: the phone's ADMIN page, RESTART, updates, the support bundle, a management server's check-in.",
            },
            "ANNOUNCE <name or words> · ANNOUNCE OFF · ADVERT <name|n> · ADVERT OFF · SCHEDULE ON / OFF · RESTART <passcode> · UPDATE APPLY <passcode>",
            new[] { "Install", "Remote" },
            new[] { "install", "permanent", "digital signage", "schedule", "rota", "programme", "advert", "announcement", "clock", "timed", "dated", "admin", "passcode", "update", "restart", "support bundle", "management", "check-in", "hotel", "retail", "shop" }),

        // ---- THE MACHINE ------------------------------------------------------------------
        new HelpTopic("machine", HelpGroup.TheMachine,
            "The Machine page: health at a glance, the GPU, the super-check",
            "ADMIN → Machine before doors and whenever something feels slow: one headline over twelve lit tiles says what needs attention, the cards under it say what to do, the lines show the last three minutes and the day.",
            HelpBodies.Machine,
            new[]
            {
                "HEALTH AT A GLANCE: green is fine, amber wants a look, red needs fixing now — outputs, render, CPU, memory, GPU, NDI, stream, audio, remote, watchdog, power, disk; the headline names the tiles that set it.",
                "WARNINGS AND RECOMMENDATIONS: each card is what is wrong, why it matters mid-show and the next thing to do, worst first.",
                "The lines: CPU, memory, rendering, the GPU's busy share and its video memory in use (against the card's total) — the last three minutes beside the day so far; a memory line that only climbs is a leak; run SUPER-CHECK for the graded report.",
                "Pick the GPU the outputs render on; set the frame rate the machine can hold; copy the report when asking for help.",
                "STABILITY: the desk's tick — what the once-a-second poll costs on the UI thread, its worst minute and the area that took it; the super-check's Desk tick row and the RENDER tile say the same.",
                "STABILITY: the engine's frame budget — the worst frame of the last minute with the stage that took it and the sink it was on (Render frame worst 31 ms (the lower third) on Output 1 (Main)); the super-check's Render frame row says what to lower; the start-up line under it names the phase a slow start waited on.",
                "QUALITY LADDER: Auto steps particles and fractal iterations down a level (70%, 50%, 35%) when an output's frames run past 25 ms for three seconds, on every sink at once, and back up after thirty clean seconds; Full never steps; Balanced and Economy lock a level for a small machine. MEMORY CEILINGS: the app against a quarter of the machine, ten pictures cached, four decoders, frames held 400 ms for a fade.",
            },
            "STATUS carries the health line.",
            new[] { "Machine" },
            new[] { "machine", "performance", "gpu", "cpu", "memory", "fps", "drops", "super-check", "health", "report", "suggestion", "recommendation", "slow", "desk tick", "stutter", "ui thread", "lag", "frame budget", "render frame", "worst frame", "slow frame", "hitch", "stage", "start-up", "startup", "slow start", "boot", "quality ladder", "quality", "adaptive", "dynamic resolution", "particles slow", "fractal slow", "step down", "economy", "balanced", "memory ceiling", "ceiling", "leak", "working set" }),

        new HelpTopic("watchdog", HelpGroup.TheMachine,
            "The watchdog, and a beacon for a second machine",
            "Behind the app: a supervisor that restarts it and puts the show back, and a heartbeat a backup machine can listen for.",
            HelpBodies.Watchdog,
            new[]
            {
                "Run Patterns through the watchdog (the default from the stick): a crash is a restart with the show live within seconds.",
                "Machine page: the beacon on; a second machine shows 'main machine seen'.",
                "RESTART on the wire (with the passcode) is a clean restart under the watchdog.",
                "A restart of any kind comes back with the audience's picture on the program and the half-built one still in the preview.",
                "After a crash: the health line names the exit code in words; a native fault (an access violation) makes the next run decode clips in software — Machine → VIDEO DECODING — and leaves a mini-dump in the crashes folder for the support bundle.",
                "A fault in the desk (a page, a button, a timer) is contained: the status line and the health line say what threw and where, patterns.log has the stack, and the show carries on; after a restart the health line names the exception's type, message and place.",
                "Render windows the last run left playing are taken back on the next start — the room keeps its picture and the new desk can stop it; Machine → 'Take the screens back from a run that is still playing' turns that off.",
            },
            "RESTART <passcode>",
            new[] { "Machine", "Install" },
            new[] { "watchdog", "supervisor", "crash", "restart", "recovery", "beacon", "heartbeat", "backup", "second machine", "failover", "resilience", "access violation", "0xc0000005", "native fault", "mini-dump", "crash dump", "createdump", "safe run", "video decoding", "software decoding", "hardware decoding", "exit code", "fault contained", "ui fault", "exception", "unhandled", "0xe0434352", "crash between menus", "page crash", "menu crash", "status line", "stuck outputs", "orphan windows", "windows still playing", "cannot stop the screens", "two copies", "second instance", "take the screens back", "re-own", "ownership", "hung", "edit safe after a restart", "preview went to program", "wrong picture after restart", "restart", "put the show back" }),

        new HelpTopic("portable-files", HelpGroup.TheMachine,
            "Portable files: what is on the stick",
            "One exe, one settings file, the media beside them — the whole show travels.",
            HelpBodies.PortableFiles,
            Array.Empty<string>(),
            "",
            new[] { "Library", "Machine" },
            new[] { "portable", "stick", "usb", "files", "settings", "json", "folder", "exe", "media path", "show file" }),

        new HelpTopic("extras", HelpGroup.TheMachine,
            "Extras: the small things",
            "Everything that did not need a topic of its own.",
            HelpBodies.Extras,
            Array.Empty<string>(),
            "",
            new[] { "Pattern", "Overlays" },
            new[] { "extras", "tone", "ident", "font", "colour", "ticker", "feed", "rss", "countdown", "particles", "misc" }),
    };

    public static HelpTopic? Find(string id)
        => string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<HelpTopic> In(HelpGroup group) => All.Where(t => t.Group == group).ToList();

    /// <summary>The topics that live on a page (a shell page header), in catalogue order.</summary>
    public static IReadOnlyList<HelpTopic> ForPage(string pageHeader)
        => string.IsNullOrWhiteSpace(pageHeader)
            ? Array.Empty<HelpTopic>()
            : All.Where(t => t.Pages.Contains(pageHeader.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
}

/// <summary>
/// The Help page's search: every word typed must be found somewhere on a topic; a topic scores by
/// where the words hit — its title and its search words most, its place in the workflow next,
/// its steps and the wire after, the long explanation least — and the hits come strongest first.
/// </summary>
public static class HelpSearch
{
    private static readonly char[] Trim = { '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '“', '”', '‘', '’' };

    /// <summary>The words of a query: lower-case, punctuation trimmed, one letter dropped, duplicates dropped.</summary>
    public static IReadOnlyList<string> Tokens(string query)
    {
        var list = new List<string>();
        foreach (var raw in (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var word = raw.Trim(Trim).ToLowerInvariant();
            if (word.Length < 2 || list.Contains(word)) continue;
            list.Add(word);
        }
        return list;
    }

    public static IReadOnlyList<HelpHit> Find(string query, IEnumerable<HelpTopic>? topics = null)
    {
        var tokens = Tokens(query);
        if (tokens.Count == 0) return Array.Empty<HelpHit>();
        var hits = new List<(HelpHit Hit, int Order)>();
        var order = 0;
        foreach (var topic in topics ?? HelpTopics.All)
        {
            var score = Score(topic, tokens);
            if (score > 0) hits.Add((new HelpHit(topic, score, Snippet(topic, tokens)), order));
            order++;
        }
        return hits.OrderByDescending(h => h.Hit.Score).ThenBy(h => h.Order).Select(h => h.Hit).ToList();
    }

    /// <summary>0 when a word is found nowhere on the topic; else the sum of where every word hits.</summary>
    public static int Score(HelpTopic topic, IReadOnlyList<string> tokens)
    {
        var total = 0;
        foreach (var token in tokens)
        {
            var score = 0;
            if (Has(topic.Title, token)) score += 12;
            foreach (var keyword in topic.Keywords)
            {
                if (string.Equals(keyword, token, StringComparison.OrdinalIgnoreCase)) { score += 12; break; }
                if (Has(keyword, token)) { score += 8; break; }
            }
            if (Has(topic.Where, token)) score += 4;
            if (topic.Steps.Any(s => Has(s, token))) score += 2;
            if (Has(topic.Wire, token)) score += 2;
            var inBody = Count(topic.Body, token);
            if (inBody > 0) score += Math.Min(inBody, 4);
            if (score == 0) return 0;                                             // every word must be found somewhere
            total += score;
        }
        return total;
    }

    /// <summary>The words around the first match of the first word — from the workflow line, the steps, the wire, then the explanation; the workflow line when only the title or the search words matched.</summary>
    public static string Snippet(HelpTopic topic, IReadOnlyList<string> tokens, int radius = 110)
    {
        if (tokens.Count == 0) return "";
        foreach (var field in new[] { topic.Where, string.Join("  ", topic.Steps), topic.Wire, topic.Body })
        {
            foreach (var token in tokens)
            {
                var at = field.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var start = Math.Max(0, at - radius);
                var end = Math.Min(field.Length, at + token.Length + radius);
                if (start > 0)
                {
                    var space = field.IndexOf(' ', start);
                    if (space >= 0 && space < at) start = space + 1;
                }
                if (end < field.Length)
                {
                    var space = field.LastIndexOf(' ', end - 1);
                    if (space > at) end = space;
                }
                var text = field[start..end].Trim();
                return (start > 0 ? "…" : "") + text + (end < field.Length ? "…" : "");
            }
        }
        return topic.Where;
    }

    private static bool Has(string text, string token) => text.Length > 0 && text.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static int Count(string text, string token)
    {
        var count = 0;
        var at = 0;
        while (at < text.Length && (at = text.IndexOf(token, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            at += token.Length;
        }
        return count;
    }
}
