namespace Patterns.Core.Services;

/// <summary>Who is at the desk: the walkthroughs are filed by the job, not by the page.</summary>
public enum DeskRole
{
    ShowCaller,
    Technician,
    Operator,
    Programmer,
    Graphics,
}

/// <summary>
/// One step of a walkthrough: the page it happens on (a shell page header, so GO can open it),
/// what to do, and — when the app can tell — the name of a fact that ticks the step by itself
/// (<see cref="Walkthroughs.Checks"/>); a step without one is ticked by hand.
/// </summary>
public sealed record WalkStep(string Page, string Title, string Detail, string Check = "");

/// <summary>A scenario a person at the desk works through: a goal, and the steps in order.</summary>
public sealed record Walkthrough(string Id, DeskRole Role, string Title, string Goal, IReadOnlyList<WalkStep> Steps);

/// <summary>
/// The Help page's step-through scenarios, one list per role — pure data, so the desk, the
/// docs and the tests read the same steps. Every page named here is a shell page header; the
/// App's test pins the two together.
/// </summary>
public static class Walkthroughs
{
    /// <summary>The facts the app can answer for a step. Anything else on a step is ticked by hand.</summary>
    public static readonly IReadOnlyList<string> Checks = new[]
    {
        "mode-prep", "mode-show", "planned-screens", "planned-adopted", "screens-present", "canvas-joined",
        "wall-gaps", "blend-auto", "outputs-on", "looks-saved", "cues-present", "cues-timed", "stack-armed",
        "edit-safe", "remote-on", "osc-on", "ndi-on", "stream-armed", "vogs-present", "stingers-present",
        "lower-thirds-designed", "people-library", "web-source", "layers-on", "beacon-on", "multiview-present",
    };

    public static string RoleLabel(DeskRole role) => role switch
    {
        DeskRole.ShowCaller => "Show caller",
        DeskRole.Technician => "Technician",
        DeskRole.Operator => "Operator",
        DeskRole.Programmer => "Programmer",
        _ => "Graphics & video",
    };

    public static string RoleBlurb(DeskRole role) => role switch
    {
        DeskRole.ShowCaller => "Runs the day from the cue sheet: the stack, GO, the clock, what is late.",
        DeskRole.Technician => "Builds the rig at the venue: screens, walls, projectors, feeds, the machine.",
        DeskRole.Operator => "Drives the show: looks, EDIT SAFE, TAKE, sounds, the panel and the remote.",
        DeskRole.Programmer => "Prepares everything before the venue: PREP, looks, cues, control surfaces.",
        _ => "Makes what is on screen: lower thirds, people, web pages, layers, feeds and the stream.",
    };

    public static readonly IReadOnlyList<Walkthrough> All = new[]
    {
        new Walkthrough("caller-sheet", DeskRole.ShowCaller, "Run the day from a cue sheet",
            "The sheet becomes the stack, every cue has its look, the clock says what is late — and GO is one key.",
            new[]
            {
                new WalkStep("Cues", "Get the sheet in", "Press Template CSV, fill it in Excel (numbers, names, starts, lengths, marks for coffee and lunch, a look by name), save as .xlsx and press IMPORT SHEET. Read the report: it names anything it could not find.", "cues-present"),
                new WalkStep("Cues", "Give each cue what it does", "Pick a look from the Quick pick on a cue, add an action with + (a lower third, a screen, a stinger), and set Follow 0 on a cue that must fire the next at once.", "looks-saved"),
                new WalkStep("Cues", "Plan the clock", "Type planned starts and lengths — the marks line reads when coffee, lunch and the end are expected.", "cues-timed"),
                new WalkStep("Run", "Take the Run surface", "RUN in the header: the LIVE strip, the wall, the stack. ARM the stack; Enter is GO, ↑ ↓ move standby, Esc twice is STOP ALL.", "stack-armed"),
                new WalkStep("Run", "When the day slips", "Late reads in amber. +1 MIN moves every later start, RESUME NOW plans the standby cue for now, CATCH UP takes the lateness off the lengths before the next break."),
                new WalkStep("Run", "Hold and recover", "HOLD lights amber and stops a follow's countdown; a second GO is asked for a cue that wants confirming; PREV puts the last cue back on standby."),
                new WalkStep("Remote", "Call from a tablet", "Switch remote control on and open the address on a tablet: /run is the same surface, and the SHOW tab of the phone remote has GO, HOLD and ARM.", "remote-on"),
            }),
        new Walkthrough("caller-late", DeskRole.ShowCaller, "The speaker overran — land the break on time",
            "Read the lateness, decide where the time comes from, and tell the room what is next.",
            new[]
            {
                new WalkStep("Run", "Read the strip", "The LIVE strip says n MIN LATE and the marks line says when coffee is now expected; every row shows planned → expected (a stack with planned times is what makes it read).", "cues-timed"),
                new WalkStep("Run", "Take the time off", "CATCH UP shortens the lengths before the next break by the lateness and says so — or +1 MIN / −1 MIN on single cues, or RESUME NOW to plan from the clock."),
                new WalkStep("Panel", "Tell the room", "The Show panel's SEND for the message overlay puts a line on every screen straight to air (Doors open 15:00); the countdown runs to a wall-clock time."),
                new WalkStep("Cues", "Fix the sheet for tomorrow", "Export CSV keeps the edited times, so tomorrow's sheet starts from what actually happened."),
            }),

        new Walkthrough("tech-venue", DeskRole.Technician, "Bring the rig up at the venue",
            "Every planned screen becomes a real display, the walls are right, and the outputs are on.",
            new[]
            {
                new WalkStep("Screens", "Leave PREP", "Switch the mode to SHOW in the header or on the Screens page: outputs may open now, sends and the stream come back by themselves.", "mode-show"),
                new WalkStep("Screens", "Adopt the planned screens", "For each planned screen, pick the display it turned out to be and press Adopt — placement, label, rotation, trims, warp, patterns and multiview tiles follow onto it.", "planned-adopted"),
                new WalkStep("Screens", "Arrange and join", "Drag displays flush to join them into one canvas (it glows green); drag apart to split. Rotate a portrait display, name the canvas.", "screens-present"),
                new WalkStep("Screens", "Tell it where the wall has no pixels", "Wall gaps: Bezel H / V for a canvas of joined displays; a gap per strip (or Set from grid) for an LED processor's packed pillars. Content spans them, the outputs cut them out.", "wall-gaps"),
                new WalkStep("Pattern", "Check the wall", "Put the LED wall or Video wall pattern on the target with the panels' real size: the tile numbers land on the panels, the ring reads round through the bezels."),
                new WalkStep("Screens", "Trim and warp", "Brightness, gamma and RGB per output for mismatched displays; corner warp for a casually placed projector; the display mode for a wall that wants 50 Hz."),
                new WalkStep("Screens", "Outputs on", "OUTPUTS ON opens a window on every enabled display. IDENTIFY flashes the numbers. Esc twice on an output closes them.", "outputs-on"),
                new WalkStep("Machine", "Read the health line", "SUPER-CHECK reads the whole machine and the rig in one list: the GPU, the displays, the frame rate, the sends, the stream, the beacon."),
            }),
        new Walkthrough("tech-blend", DeskRole.Technician, "Blend two projectors into one picture",
            "Two overlapping projectors read as one wide picture with a flat seam.",
            new[]
            {
                new WalkStep("Screens", "Automatic blend on both", "Select each projector's screen and tick Automatic under Edge blend — overlapping them joins them into one canvas instead of being a mistake.", "blend-auto"),
                new WalkStep("Screens", "Overlap them", "Drag one projector over the other by the measured overlap (in its pixels). The readback says what each output will fade.", "canvas-joined"),
                new WalkStep("Pattern", "The grey check", "Put the Projection blend pattern on the canvas and turn the blend gamma on the Screens page until the overlap reads as flat as the rest."),
                new WalkStep("Screens", "Keystone", "Corner warp pulls each corner by pixels; the blend follows the warp, so a keystoned projector's fade stays on the picture's own edge."),
                new WalkStep("Screens", "Outputs on", "OUTPUTS ON — the fade only exists on the real outputs; monitors, NDI and the preview never fade.", "outputs-on"),
            }),
        new Walkthrough("tech-backup", DeskRole.Technician, "A second machine that watches the first",
            "The backup hears the main machine's heartbeat and knows the moment it stops.",
            new[]
            {
                new WalkStep("Machine", "The same show on both", "Save the show file and open it on the backup: the same screens, looks, cues and inputs."),
                new WalkStep("Machine", "Send the beacon on the main", "Tick Send a heartbeat beacon (leave the broadcast address, or type the backup's). Every second: live, the program, the standby cue, the health line.", "beacon-on"),
                new WalkStep("Machine", "Listen on the backup", "Tick Listen on the backup: its health line reads MAIN MACHINE seen 1 s ago; after five silent seconds it reads SILENT — take over? Taking over stays a person's decision."),
                new WalkStep("Machine", "Read what the watchdog knows", "The supervisor restarts a crash or a hung desk; a frozen render path, a stream that stopped and a stand-down show on the health line and in SUPER-CHECK."),
            }),

        new Walkthrough("op-look", DeskRole.Operator, "Build a look safely and take it",
            "Nothing reaches the audience until you say so; then it goes in one fade.",
            new[]
            {
                new WalkStep("Panel", "Open EDIT SAFE", "EDIT SAFE on the Show panel (on by default): the PREVIEW detaches from air, and every editor changes the preview only.", "edit-safe"),
                new WalkStep("Pattern", "Build the picture", "Pick a pattern, a media source or a web page; drag the clock, the PiP and the layers on the PREVIEW pane. The PROGRAM pane keeps showing what the audience sees."),
                new WalkStep("Panel", "Review it on the wall", "REVIEW puts the preview over every multiview full-frame with a chip — a Preview tile beside the program does the same in a corner."),
                new WalkStep("Panel", "TAKE or CUT", "TAKE crossfades the preview to air on every armed target; CUT switches at once; SEND on one tile puts it on that screen alone. EDIT SAFE re-arms itself."),
                new WalkStep("Looks", "Keep it", "Save the state as a look, give it an F-key: F1–F12 recall it on air from any page, an output window, the remote and Companion.", "looks-saved"),
            }),
        new Walkthrough("op-sounds", DeskRole.Operator, "VOGs, stingers and a lower third at show time",
            "The voice of god, the sting and the name arrive on the press, from the desk or a Stream Deck.",
            new[]
            {
                new WalkStep("Audio", "Load the calls", "Add VOG files (an announcement, ducking the music) and stingers (a hit with a clip that takes the screens and puts them back).", "vogs-present"),
                new WalkStep("Panel", "Fire them", "The Show panel's VOG and STINGER chips light while they play; STOP puts the picture back; DUCK dips the break music by hand."),
                new WalkStep("Lower thirds", "The name", "Pick a design, press SHOW; PEOPLE puts the next person into the design on air; HIDE takes it off.", "lower-thirds-designed"),
                new WalkStep("Remote", "The same keys elsewhere", "Companion presets for VOGs, stingers, lower thirds and people light while they play; the phone remote's AUDIO and LOWER THIRDS tabs carry them too.", "remote-on"),
                new WalkStep("Panel", "STOP ALL", "STOP ALL on the panel (twice on the phone) stops every sound and clip but leaves the picture."),
            }),

        new Walkthrough("prog-prep", DeskRole.Programmer, "Pre-programme the whole show before the venue",
            "A show file that opens at the venue with every screen, look and cue ready.",
            new[]
            {
                new WalkStep("Screens", "PREP", "PREP in the header: outputs are held closed, sends stop, the stream is held — nothing can go live while you build.", "mode-prep"),
                new WalkStep("Screens", "Plan the screens", "Plan a screen at the size the venue will have — an LED processor's canvas, a projector's native size — arrange, name and join them.", "planned-screens"),
                new WalkStep("Media", "Name the inputs", "Type the NDI names and capture devices the rig will use before they exist; each source runs on its own mount."),
                new WalkStep("Looks", "Build the looks", "Every picture the show needs as a look with an F-key; a look can carry its own cut or fade time.", "looks-saved"),
                new WalkStep("Cues", "Build the stack", "The cue stack with looks, actions, planned times and follows — or import the caller's sheet.", "cues-present"),
                new WalkStep("Panel", "Save", "Save the show: the file carries the planned screens, so adopting them at the venue is a click each."),
            }),
        new Walkthrough("prog-control", DeskRole.Programmer, "Stream Deck, OSC and the phone",
            "Every control surface drives the same show through the same verbs.",
            new[]
            {
                new WalkStep("Remote", "Switch it on", "Remote control on: the TCP port for Companion and the web remote's address for phones and tablets. There is no password.", "remote-on"),
                new WalkStep("Remote", "Companion", "Load the module from integrations/companion-module-patterns: presets for transport, looks, screens, cues, stingers, people and review, with live feedback."),
                new WalkStep("Remote", "OSC", "Tick OSC in, set the feedback host: /patterns/look 3, /patterns/blackout 1, /patterns/cue/go and the rest map onto the same verbs; the state comes back as a bundle.", "osc-on"),
                new WalkStep("Remote", "The phone", "Open the address on a phone: SHOW · CUES · LOOKS · SCREENS · AUDIO · LOWER THIRDS · SETUP, live within a blink."),
                new WalkStep("Help", "The words on the wire", "docs/REMOTE.md lists every line, the OSC table and the STATE keys."),
            }),

        new Walkthrough("gfx-lower", DeskRole.Graphics, "Lower thirds with a people library",
            "One design, a list of speakers, and a name on screen from a cue, a key or a phone.",
            new[]
            {
                new WalkStep("Lower thirds", "Design", "Start from a preset (Clean, Glass, Headshot…) and edit it on the stage: drag elements, keyframes, fonts, a photo element.", "lower-thirds-designed"),
                new WalkStep("Lower thirds", "The library", "Template CSV → fill names, roles, companies and headshot paths in Excel → IMPORT LIST. Append a CSV to update.", "people-library"),
                new WalkStep("Lower thirds", "Use and show", "USE puts a person into the design's fields; SHOW puts it on every output; a wrong name never reaches the screen."),
                new WalkStep("Cues", "From a cue", "Lower third on with the design and the person: the cue reads the name. LT 2 WITH Jane Doe from the wire, PERSON keys on Companion.", "cues-present"),
                new WalkStep("Lower thirds", "Save the creations", "Export a design as a file to carry to another show; import it back."),
            }),
        new Walkthrough("gfx-web", DeskRole.Graphics, "A web page and layers on a screen",
            "A live dashboard on the wall, a feed in a box over it, all dragged into place.",
            new[]
            {
                new WalkStep("Media", "The page", "Source: Web page, the address, the size, the zoom; Show the pointer if the room should see clicks. Click, scroll and type on the PREVIEW pane.", "web-source"),
                new WalkStep("Media", "Layers", "LAYER 1 and 2: any media, an NDI feed, another screen; crop, border, corners, opacity. Drag the boxes on the PREVIEW pane (Alt-drag over a web page).", "layers-on"),
                new WalkStep("Overlays", "Overlays", "The clock, the message ticker, the logo, the PiP inset — dragged from their anchors on the pane."),
                new WalkStep("Looks", "Keep it as a look", "Save the look: the page, its size and zoom, the layers and the overlays come back with one fade.", "looks-saved"),
            }),
        new Walkthrough("gfx-feeds", DeskRole.Graphics, "Feed the stream and the network",
            "The show goes out as NDI and as a stream, each with its own picture if it wants one.",
            new[]
            {
                new WalkStep("NDI", "Senders", "Add an NDI sender per picture: a screen, a canvas or the program; a sender can be its own virtual screen with its own look.", "ndi-on"),
                new WalkStep("Stream", "Destinations", "Up to two destinations with their keys; the source can be the desktop, a screen or a virtual screen the engine renders.", "stream-armed"),
                new WalkStep("Pattern", "A multiview for the truck", "A Multiview pattern on a spare display or a sender: tiles of every screen, feed, the clock and the preview, with tally.", "multiview-present"),
                new WalkStep("Machine", "Watch it", "The Machine page reads dropped frames against the master rate, the stream's status and the sends; SUPER-CHECK grades the lot."),
            }),
    };

    public static IEnumerable<Walkthrough> For(DeskRole role) => All.Where(w => w.Role == role);

    public static Walkthrough? Find(string id) => All.FirstOrDefault(w => w.Id == id);
}

/// <summary>
/// One person's place in one walkthrough: the current step, and which steps are done — by
/// hand, or because the app answered the step's check. Pure, so a test drives it without a desk.
/// </summary>
public sealed class WalkthroughProgress
{
    private readonly bool[] _byHand;
    private readonly bool[] _byApp;

    public WalkthroughProgress(Walkthrough walkthrough)
    {
        Walkthrough = walkthrough;
        _byHand = new bool[walkthrough.Steps.Count];
        _byApp = new bool[walkthrough.Steps.Count];
    }

    public Walkthrough Walkthrough { get; }

    public int Count => Walkthrough.Steps.Count;

    /// <summary>The step the desk is on (0-based).</summary>
    public int Current { get; private set; }

    public WalkStep CurrentStep => Walkthrough.Steps[Current];

    public bool IsDone(int index) => _byHand[index] || _byApp[index];

    /// <summary>The app's answer stands on its own: a step it ticks is done whether or not a hand tick was given.</summary>
    public bool IsDoneByApp(int index) => _byApp[index];

    public int DoneCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < Count; i++)
            {
                if (IsDone(i)) n++;
            }
            return n;
        }
    }

    public bool Finished => Count > 0 && DoneCount == Count;

    public double Fraction => Count == 0 ? 0 : DoneCount / (double)Count;

    /// <summary>A hand tick on a step; a step the app answered stays done.</summary>
    public void MarkDone(int index) => _byHand[index] = true;

    public void Unmark(int index) => _byHand[index] = false;

    /// <summary>The app's answer for a step's check: true ticks it, false only takes the app's own tick away.</summary>
    public void Observe(int index, bool met) => _byApp[index] = met;

    /// <summary>NEXT: the current step is done by hand, and the desk moves on.</summary>
    public void Next()
    {
        _byHand[Current] = true;
        if (Current < Count - 1) Current++;
    }

    public void Back()
    {
        if (Current > 0) Current--;
    }

    public void Go(int index)
    {
        if (index >= 0 && index < Count) Current = index;
    }

    /// <summary>Start again: the hand ticks go, the app's facts stand.</summary>
    public void Restart()
    {
        Array.Clear(_byHand);
        Current = 0;
    }

    /// <summary>"Step 3 of 7 · 2 done", "Finished — 7 of 7 done".</summary>
    public string Words => Finished
        ? $"Finished — {Count} of {Count} done"
        : $"Step {Current + 1} of {Count} · {DoneCount} done";
}
