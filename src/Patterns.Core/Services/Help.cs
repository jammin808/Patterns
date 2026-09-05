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
            },
            "Every verb a remote can send is in docs/REMOTE.md. STATE (the STATUS verb) carries the whole show as JSON, so a controller can read what a key should say.",
            new[] { "Panel", "Looks", "Cues", "Screens", "Machine" },
            new[] { "overview", "map", "start", "begin", "first", "stages", "groups", "action layer", "journal", "show file", "program", "preview", "edit safe", "take", "look", "cue", "rig", "how it works" }),

        new HelpTopic("shell", HelpGroup.StartHere,
            "The shell: five groups, the strip, ? TIPS, the resizable desk",
            "The rail is the map of a show day; the strip is the map of a group. Learn these two and every page is two clicks away.",
            HelpBodies.Shell,
            new[]
            {
                "Pick the group on the rail (SHOW · PLAN · BUILD · SETUP · ADMIN); the strip lists its pages and remembers the one you were on.",
                "Press ? TIPS on the strip for this page's explanations — or tick Show hints on the pages to keep them inline.",
                "Drag the divider between the page and the screens, or the handle between PROGRAM and PREVIEW; ◧ WIDE folds the screens to a strip.",
                "SHOW CONTROLS under the wall: the message, the clock, the countdown and the audio track's level, each behind SEND.",
            },
            "",
            new[] { "Help" },
            new[] { "rail", "groups", "strip", "tabs", "pages", "tips", "hints", "divider", "splitter", "wide", "layout", "show controls", "send", "resize" }),

        new HelpTopic("modes", HelpGroup.StartHere,
            "PREP · SHOW · RUN: what may leave the machine, and the caller's layout",
            "Set the mode when the machine arrives; check it first when nothing appears on a screen. RUN is a layout for the caller, not a mode.",
            HelpBodies.Modes,
            new[]
            {
                "PREP at the desk or at home: the output windows are refused, the sends stop, the stream is held — everything else works.",
                "SHOW at the venue: OUTPUTS ON (⇧F5) opens the windows, the sends run, the stream starts when it is armed.",
                "RUN for the caller: the LIVE strip, the wall, the stack and GO take the window; POP OUT for a second monitor, /run on a tablet.",
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
                "PROGRESSION: NEXT / BACK step the clicker list or a deck; the line also reads a counting auto-follow and the playlist's part.",
                "Then the VOG and STINGER chips, LOWER THIRDS and PEOPLE, the audio track, break music, FREEZE / FADE / LOOK BACK and REVIEW.",
            },
            "CUE GO · CUE STANDBY NEXT / PREV · CUE HOLD ON / OFF · CUE ARM ON / OFF · LOOK <name> · SCREEN <n> LOOK <name> · SCREEN <n> PROGRAM · LOCK <n> ON · NEXT / PREV · STINGER <name> · LOWERTHIRD <name> · STOPALL",
            new[] { "Panel" },
            new[] { "panel", "show panel", "cues", "go", "hold", "arm", "standby", "next", "looks", "pvw", "screens", "own", "program", "progression", "clicker", "control surface", "operator" }),

        new HelpTopic("switcher", HelpGroup.RunningTheShow,
            "The switcher: PROGRAM, PREVIEW, EDIT SAFE, TAKE and the wall",
            "Under every page in SHOW and BUILD: the audience's picture on the left, the one you are building on the right, and a tile per screen between them.",
            HelpBodies.Switcher,
            new[]
            {
                "Open EDIT SAFE: the editors now change the PREVIEW; the program is frozen for the audience.",
                "Build the next picture; check it on the PREVIEW pane, or with REVIEW on a multiview.",
                "TAKE swaps it to air with the show's transition; CUT does it at once.",
                "ARM off on a tile keeps that target through the next TAKE; LOCK keeps it through looks, cues and stingers too.",
                "SEND puts the preview on one tile alone as its own picture; OWN gives a tile its own editable picture.",
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
            },
            "CUE GO [id] · CUE STANDBY NEXT / PREV / <number> / <name> · CUE HOLD ON / OFF · CUE ARM ON / OFF · CUE LIST",
            new[] { "Cues", "Run", "Panel" },
            new[] { "cue", "cues", "stack", "sheet", "running order", "import", "csv", "excel", "planned", "time", "clock", "late", "early", "auto-follow", "follow", "break", "lunch", "go", "standby", "caller", "notes" }),

        new HelpTopic("vog-stingers", HelpGroup.RunningTheShow,
            "VOGs, stingers and staying up: sounds, clips and what happens after",
            "The Audio page holds the library; the Show panel fires it. A VOG plays over the show; a stinger takes the screens and then goes where you said.",
            HelpBodies.VogStingers,
            new[]
            {
                "Audio page: add a VOG (a sound over everything — the music ducks) or a STINGER (a clip, an effect or a held frame that takes the screens).",
                "Set what happens after a stinger: back, held for your TAKE, on to the next cue, or a look.",
                "Fire from the panel's chips, a cue, the phone, Companion, OSC or a device; STOP puts a held one back.",
                "DUCK for an announcement from the room; STOP ALL stops every sound and never the outputs.",
            },
            "STINGER <n|name> · VOG <n|name> · STING <n|name> · STINGER STOP · DUCK ON / OFF · STOPALL · AUDIO PLAY / STOP",
            new[] { "Audio", "Panel" },
            new[] { "vog", "stinger", "sting", "clip", "sound", "duck", "ducking", "hold", "put it back", "stop all", "effect", "particles", "fractal", "audio track", "voice of god" }),

        new HelpTopic("lower-thirds-flow", HelpGroup.RunningTheShow,
            "Lower thirds: preview, sign-off, air, update, the show's default",
            "Names on screen during the show: the panel's chips, PVW FIRST for a sign-off, TAKE TO AIR, UPDATE ON AIR.",
            HelpBodies.LowerThirdsFlow,
            new[]
            {
                "Build designs on the Lower thirds page; ★ one as the show's default.",
                "On the panel a chip puts the design on air; with PVW FIRST it goes to the preview for a sign-off and TAKE TO AIR puts it on.",
                "EDITED means the design changed after it went on: UPDATE ON AIR pushes the change in place.",
                "■ Hide takes it off the way it was designed to leave.",
            },
            "LOWERTHIRD <n|name> · LOWERTHIRD OFF · LOWERTHIRD PREVIEW <n|name> · LOWERTHIRD TAKE · LOWERTHIRD UPDATE · PERSON <n|name>",
            new[] { "Panel", "Lower thirds" },
            new[] { "lower third", "lower thirds", "name strap", "caption", "preview", "sign-off", "take", "update", "default", "hide", "air", "edited" }),

        new HelpTopic("people-library", HelpGroup.RunningTheShow,
            "The lower-thirds library: people ready to go",
            "Before the show, every speaker's name, role, company and photo typed once; during it, one press per person.",
            HelpBodies.PeopleLibrary,
            new[]
            {
                "Lower thirds page → LIBRARY: + PERSON with the name, role, company, photo and a note.",
                "On the panel, the PEOPLE chips put a person into the design on air (else the ★ default).",
                "A cue's Lower third — show with a person names the entry; the wire and Companion have the same.",
            },
            "PERSON <n|name> · LOWERTHIRD <design> WITH <person> · LOWERTHIRD PREVIEW WITH <person>",
            new[] { "Lower thirds", "Panel", "Cues" },
            new[] { "people", "person", "library", "speaker", "name", "role", "company", "photo", "headshot", "entry", "guest" }),

        new HelpTopic("lower-thirds", HelpGroup.RunningTheShow,
            "Lower thirds: the designer — elements, keyframes, styles, media",
            "BUILD → Lower thirds is where a design is made; the flow above is how it is used at show time.",
            HelpBodies.LowerThirds,
            new[]
            {
                "Pick a preset or start blank; add text, shapes, a photo or a clip; drag the elements on the preview.",
                "Keyframes give the way in and out; styles give the type, the colours and the edges.",
                "SAVE the design; EXPORT to share it as a file; ★ makes it the show's default.",
            },
            "",
            new[] { "Lower thirds" },
            new[] { "lower third", "designer", "design", "keyframe", "animation", "element", "text", "photo", "style", "preset", "export", "import", "graphics" }),

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

        new HelpTopic("freeze-fade", HelpGroup.RunningTheShow,
            "Freeze, the timed fade, the previous look, earlier versions",
            "The emergency and finesse keys of a show operator, on the panel and on the wire.",
            HelpBodies.FreezeFade,
            new[]
            {
                "FREEZE holds every output's picture while you change anything behind it; press again to release.",
                "FADE TO BLACK / FADE UP over the seconds typed beside them — a blackout with a fade of its own time.",
                "LOOK BACK puts the previous look back on air; again swaps the two.",
                "An earlier build is a folder on the stick: run it and the show file opens as it was.",
            },
            "FREEZE ON / OFF / TOGGLE · FADE [seconds] · FADE UP [seconds] · LOOKBACK [cut|ms] · BLACKOUT ON / OFF / TOGGLE",
            new[] { "Panel" },
            new[] { "freeze", "hold frame", "fade", "black", "fade to black", "fade up", "look back", "previous", "undo", "version", "roll back", "emergency" }),

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
                "Pattern page → Multiview: tiles for the program, the preview, screens and canvases, inputs.",
                "The PROGRAM / PREVIEW badges follow the switcher; a screen's tile names the screen and its outputs.",
                "Send the multiview to a screen, an NDI send, or /multiview on the phone.",
            },
            "",
            new[] { "Pattern", "Screens", "NDI" },
            new[] { "multiview", "tally", "badge", "program", "preview", "tile", "monitor wall", "which screen", "outputs" }),

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
            "BUILD → Pattern: a logo, a camera, a page or a screen over the picture — sized, cropped, edged and dragged on the preview.",
            HelpBodies.Layers,
            new[]
            {
                "Pattern page → LAYERS: pick the source for layer 1 and layer 2 (an image, a video, NDI, capture, a web page, a screen).",
                "Size, crop, border, corners and opacity; drag the layer on the PREVIEW pane.",
                "The overlays (message, clock, countdown, ticker) drag the same way.",
            },
            "",
            new[] { "Pattern", "Overlays" },
            new[] { "layer", "layers", "overlay", "drag", "move", "position", "border", "corner", "opacity", "logo", "pip", "crop" }),

        new HelpTopic("parts-multiview-stream", HelpGroup.Content,
            "Show parts, the multiview and streaming",
            "The playlist's sections are the show's parts; the multiview is the monitor wall; the stream is the same picture on the network.",
            HelpBodies.PartsMultiviewStream,
            new[]
            {
                "Media page: sections for the playlist — a part per session; SECTION on the wire or a cue puts one on air.",
                "Pattern page → Multiview: the wall's tiles; send it to a screen or an NDI send.",
                "Stream page: up to two destinations; ARM, and it starts with the outputs.",
            },
            "SECTION <n|name> · STREAM ON / OFF",
            new[] { "Media", "Pattern", "Stream" },
            new[] { "playlist", "part", "section", "multiview", "stream", "streaming", "rtmp", "srt", "destination", "arm" }),

        // ---- THE RIG ----------------------------------------------------------------------
        new HelpTopic("screen-roles", HelpGroup.TheRig,
            "Screen roles, locks and repeaters",
            "SETUP → Screens: what each screen is for — main, confidence, info — and whether looks and cues may touch it.",
            HelpBodies.ScreenRoles,
            new[]
            {
                "Give a screen a role: MAIN follows the show; CONFIDENCE and INFO keep their own picture.",
                "LOCK a screen (the wall, the panel, the wire) to keep its picture through looks, cues, TAKE ALL and stingers.",
                "REPEATER: a screen that copies another target.",
            },
            "LOCK <n> ON / OFF / TOGGLE · SCREEN <n> ON / OFF · GROUP <letter> ON / OFF",
            new[] { "Screens", "Panel" },
            new[] { "role", "main", "confidence", "info", "lock", "locked", "repeater", "mirror", "follow", "independent", "stage monitor", "foyer" }),

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
                "The phone's pages: Show, Cues, Looks, Screens, Audio, Lower thirds, Setup — and ADMIN with a passcode.",
                "Allow remotes to arm only if you mean it; HELLO names a connection in the journal.",
            },
            "Every verb is in docs/REMOTE.md. STATUS · PING · HELLO <name> · CUE LIST",
            new[] { "Remote" },
            new[] { "remote", "phone", "tablet", "web remote", "tcp", "port", "network", "address", "url", "/run", "admin", "verb", "protocol", "wire" }),

        new HelpTopic("companion-banks", HelpGroup.Control,
            "Companion and OSC: keys that fill themselves from the show",
            "A Stream Deck through Bitfocus Companion, or any OSC controller: drag the bank presets once and every look, person, VOG, part and cue you make afterwards labels its own key.",
            HelpBodies.CompanionBanks,
            new[]
            {
                "Install the module (integrations/companion-module-patterns); point it at the machine's address and port.",
                "Drag the bank presets — Looks, People, VOGs, Stingers, Parts, Screens, Upcoming cues: the keys label themselves and light on air.",
                "Feedbacks and variables for anything else; OSC gets the same addresses and the same feedback.",
            },
            "LOOK #<n> and the module's actions and feedbacks; /patterns/look/index/<n> and /patterns/state/… over OSC.",
            new[] { "Remote" },
            new[] { "companion", "stream deck", "bitfocus", "preset", "bank", "feedback", "variable", "osc", "touchosc", "key", "label", "module" }),

        new HelpTopic("osc", HelpGroup.Control,
            "OSC in and out",
            "SETUP → Remote: an OSC port in, feedback out to a host — lighting desks, TouchOSC, any controller that speaks addresses.",
            HelpBodies.Osc,
            new[]
            {
                "Remote page: OSC ON and the port; the feedback host and port.",
                "Send /patterns/… addresses (the table is in docs/REMOTE.md); read /patterns/state/… for every change.",
            },
            "/patterns/look <n|name> · /patterns/cue/go · /patterns/screen/<n>/look \"<name>\" · /patterns/blackout · /patterns/status",
            new[] { "Remote" },
            new[] { "osc", "udp", "address", "feedback", "touchosc", "lighting desk", "port", "qlab" }),

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
            "The Machine page: performance, the GPU, the super-check",
            "ADMIN → Machine before doors and whenever something feels slow: the rate, the GPU, the memory, and what to change.",
            HelpBodies.Machine,
            new[]
            {
                "Read the health line and the suggestions; run SUPER-CHECK for a graded report.",
                "Pick the GPU the outputs render on; set the frame rate the machine can hold.",
                "Save or copy the report when asking for help.",
            },
            "STATUS carries the health line.",
            new[] { "Machine" },
            new[] { "machine", "performance", "gpu", "cpu", "memory", "fps", "drops", "super-check", "health", "report", "suggestion", "recommendation", "slow" }),

        new HelpTopic("watchdog", HelpGroup.TheMachine,
            "The watchdog, and a beacon for a second machine",
            "Behind the app: a supervisor that restarts it and puts the show back, and a heartbeat a backup machine can listen for.",
            HelpBodies.Watchdog,
            new[]
            {
                "Run Patterns through the watchdog (the default from the stick): a crash is a restart with the show live within seconds.",
                "Machine page: the beacon on; a second machine shows 'main machine seen'.",
                "RESTART on the wire (with the passcode) is a clean restart under the watchdog.",
            },
            "RESTART <passcode>",
            new[] { "Machine", "Install" },
            new[] { "watchdog", "supervisor", "crash", "restart", "recovery", "beacon", "heartbeat", "backup", "second machine", "failover", "resilience" }),

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
