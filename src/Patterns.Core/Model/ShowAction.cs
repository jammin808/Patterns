namespace Patterns.Core.Model;

/// <summary>
/// Every verb the show understands, in one vocabulary: the desk's keys and buttons, the cue
/// stack's steps, the remote protocol, OSC, the Companion module, the install's schedule and a
/// device of the Interactive area all speak it, and one executor runs it. A cue's step is a
/// <see cref="ShowAction"/> kept in the show file (<see cref="CueActionConfig"/>); the one table
/// <see cref="Services.ActionSpec"/> says, per kind, what its target and value are and which few
/// kinds are the desk's alone. Typed on purpose: a kind can be validated, journaled and displayed;
/// a free-form string cannot.
/// </summary>
public enum ShowActionKind
{
    /// <summary>A kind this build does not know (written by a newer build). Never executes.</summary>
    Unknown,
    Note,
    OutputsOn,
    OutputsOff,
    Identify,
    BlackoutOn,
    BlackoutOff,
    BlackoutToggle,
    /// <summary>Target = look name or id; Value = "cut", a fade in ms, or empty for the show default.</summary>
    ApplyLook,
    /// <summary>Target = look name or id; loads the look into the editors (the sandboxed preview).</summary>
    ApplyLookToPreview,
    /// <summary>Target = F-key slot 1–12.</summary>
    ApplyLookHotkey,
    PresenterNext,
    PresenterPrev,
    /// <summary>Target = screen number (arrangement order, 1-based) or a placement screen id.</summary>
    ScreenOn,
    ScreenOff,
    ScreenToggle,
    /// <summary>Target = canvas letter (A, B…) or a canvas member key.</summary>
    CanvasOn,
    CanvasOff,
    /// <summary>The audio playlist plays: Target = a track by number (its place in the order), a row's id or name, or a file's name; empty resumes, or starts the list.</summary>
    AudioPlay,
    AudioStop,
    /// <summary>The audio playlist moves to the next track (the list wraps for a key that must never dead-end).</summary>
    AudioNext,
    /// <summary>The audio playlist moves back a track.</summary>
    AudioPrev,
    ToneOn,
    ToneOff,
    /// <summary>
    /// Target = library number (1-based, Audio-page order), name or id; Value = "" (either kind),
    /// "vog" or "sting" — a button that says VOG must never fire a stinger.
    /// </summary>
    StingerFire,
    StingerStop,
    /// <summary>Target = playlist part number (1-based) or name.</summary>
    PlaylistPart,
    StreamStart,
    StreamStop,
    /// <summary>The sandbox becomes the program on every screen, crossfading.</summary>
    Take,
    /// <summary>The sandbox becomes the program on every screen, instantly.</summary>
    Cut,
    /// <summary>Audio track, break music, stingers (a clip reverts) and the tone stop. Never outputs, never blackout, never the stream.</summary>
    StopAll,
    /// <summary>Value = minutes; a duration countdown starts now on air.</summary>
    CountdownStart,
    CountdownStop,
    /// <summary>Value = the text (empty keeps the current text).</summary>
    MessageOn,
    MessageOff,
    ClockOn,
    ClockOff,
    /// <summary>The weather chip on air; WeatherView's Value names the view: now, day (the rest of today) or tomorrow.</summary>
    WeatherOn,
    WeatherOff,
    WeatherView,
    WeatherToggle,
    /// <summary>Target = a cue list (stack id or name): the clicker list or the caller's stack.</summary>
    ListArm,
    ListDisarm,
    ListGo,
    ListBack,
    ListReset,
    /// <summary>Target = cue id: run that cue's actions now, in order, stopping at the first failure.</summary>
    CueFire,
    /// <summary>GO on the caller's stack through the gate; Target = the standby id the sender saw ("" skips the fence).</summary>
    CueGo,
    /// <summary>The audio track's volume in percent (0–125), live — the drawer's SEND and a cue.</summary>
    AudioVolume,
    /// <summary>The routing matrix on, off or toggled (Value = on / off / toggle); switching it on with nothing in it seeds audio-follows-video.</summary>
    AudioRouting,
    /// <summary>A source put on a destination: Target = the source (programme, screen 2, music, vog…), Value = the destination, with " AT &lt;dB&gt;" for a level other than 0.</summary>
    AudioRoute,
    /// <summary>A source taken off a destination: Target = the source, Value = the destination.</summary>
    AudioUnroute,
    /// <summary>What a VOG does on a destination: Target = the destination, Value = duck / replace / leave.</summary>
    AudioVogMode,
    /// <summary>Round 69: the sound follows the picture — the matrix derives each screen's route from what it shows (Value = on / off / toggle).</summary>
    AudioFollow,
    /// <summary>Round 69: where a screen's sound leaves: Target = the screen (a number, an id or a canvas key), Value = a destination's words (an output's name, NDI &lt;send&gt;, computer) or OFF for none.</summary>
    ScreenAudio,
    /// <summary>Break music (Spotify): Target = library entry number (1-based, Audio-page order),
    /// name or id; empty resumes, or plays the first saved entry.</summary>
    SpotifyPlay,
    /// <summary>Break music pauses (Spotify has no "stop"; the position is kept so play resumes).</summary>
    SpotifyPause,
    /// <summary>Break music skips to the next track in whatever is playing.</summary>
    SpotifyNext,
    /// <summary>The break-music level in percent (0–100 — the Spotify device's own volume), live.</summary>
    SpotifyVolume,
    /// <summary>The live duck: everything but a VOG makes way for an announcement from the room. STOP ALL leaves it.</summary>
    DuckOn,
    DuckOff,
    DuckToggle,
    /// <summary>
    /// Target = a lower third's id, name or 1-based number (Lower thirds page order), or empty for
    /// the one on air (else the first); Value = a library entry (id, name or number) recalled into it
    /// first — its name, role, company and photo — or empty for the design as it is. It goes on air
    /// (again restarts its way in).
    /// </summary>
    LowerThirdShow,
    /// <summary>The lower third on air leaves the way it was designed to.</summary>
    LowerThirdHide,
    /// <summary>
    /// Target = screen number (arrangement order, 1-based), a placement screen id or a canvas
    /// key: locked, it keeps its picture through looks, cues, TAKE ALL and a stinger; unlocked,
    /// it follows them again.
    /// </summary>
    ScreenLock,
    ScreenUnlock,
    ScreenLockToggle,
    /// <summary>
    /// Target = a screen (number, id); Value = its role — main, confidence, info or repeater. A
    /// confidence or info screen is locked as it takes the role, the way the Screens page does
    /// it; a main screen or a repeater follows again.
    /// </summary>
    ScreenRole,
    /// <summary>Target = a screen (number, id) or a canvas key; Value = the name it goes by on the desk, the wall and the remotes (empty clears a screen's label).</summary>
    ScreenLabel,
    /// <summary>Every multiview draws the sandboxed preview full-frame — a review before the TAKE — or its tiles again. A runtime flag, never saved.</summary>
    ReviewOn,
    ReviewOff,
    ReviewToggle,
    /// <summary>FREEZE: every output holds the frame it shows until released (a runtime flag, never saved); the desk keeps moving.</summary>
    FreezeOn,
    FreezeOff,
    FreezeToggle,
    /// <summary>
    /// Target = where the fade lands (empty = the rig, as a blackout with a fade; FOCUSED, TICKED,
    /// GROUPS, SCREEN n, GROUP A, or ID &lt;id&gt; — see <see cref="Services.FadeScope"/>); Value =
    /// seconds ("2", "1.5", "1500ms"; empty = the show's transition time) — the one convention the
    /// desk, the wire, OSC and a cue share.
    /// </summary>
    FadeToBlack,
    /// <summary>The same places, faded up again (empty = the rig: the blackout lifted and every screen black on its own brought back).</summary>
    FadeUp,
    /// <summary>The look that was on air before the current one, back on air (the value: cut, a fade in ms, or the show default).</summary>
    LookBack,
    /// <summary>
    /// Target = a lower third (as LowerThirdShow), Value = a library entry: into the preview — the
    /// PREVIEW pane, the multiview's Preview tile and REVIEW show it — for a sign-off before it goes
    /// to air. Needs EDIT SAFE (without it the preview is the air). An empty target is the design in
    /// the preview, else the one on air, else the show's default.
    /// </summary>
    LowerThirdPreview,
    /// <summary>The lower third in the preview leaves it (the way it was designed to).</summary>
    LowerThirdPreviewOff,
    /// <summary>The lower third in the preview goes to air afresh — it arrives the way it was designed to — and the preview clears.</summary>
    LowerThirdTake,
    /// <summary>The design on air is replaced by the design as it is now — every edit, the words too — without leaving and arriving again.</summary>
    LowerThirdUpdate,
    /// <summary>
    /// Round 73: the design on air for this run's hold — Target = a design (number or name; empty
    /// = the default), Value = seconds ("0" or STAY = until hidden this run). The design's own hold
    /// is untouched; a timed design leaves by itself after the hold, the way it was designed to.
    /// </summary>
    LowerThirdShowFor,
    /// <summary>
    /// Round 73: the design's own hold, saved with the show — Target = a design, Value = seconds
    /// (a design that leaves by itself after them) or STAY (stays until hidden; the number is
    /// kept). The design on air retimes with it.
    /// </summary>
    LowerThirdHold,
    /// <summary>
    /// The RUN surface's monitor (round 62): one screen drawn large between the wall and the
    /// history, for the caller's eye. Target = a screen (its number on the wire, or its id), a
    /// canvas key, PGM for the programme, or MAIN / blank for the main screen. Changes where the
    /// desk looks, never the air; a cue may carry it ("watch the IMAG screen from here").
    /// </summary>
    RunMonitor,
    /// <summary>The RUN surface's monitor hidden; the history takes the room.</summary>
    RunMonitorOff,
    /// <summary>
    /// Target = a web page in the show ("" = the page the program shows; else its nickname, its
    /// address or a word of it); Value = a key chord ("ArrowRight", "Ctrl+Shift+F5") or a page
    /// action ("next", "play", "present"…) the page's service maps to its key or its script.
    /// </summary>
    WebKey,
    /// <summary>A click on the page: Value = "x y" in percent of the page.</summary>
    WebClick,
    /// <summary>Value typed into the field that has the page's focus.</summary>
    WebType,
    /// <summary>The page reloaded.</summary>
    WebReload,
    /// <summary>The page's browser sent to another address (Value); the pattern keeps its own address.</summary>
    WebOpen,
    /// <summary>
    /// The armed web VT: the page's video put at a point and held there, to play from it the moment
    /// the page goes to air. Value = the point ("1:23", "83", empty for the mark set or the player's
    /// own place, "off" to disarm).
    /// </summary>
    WebArm,
    /// <summary>The mark moved without arming: Value = the point, or empty for where the player is now.</summary>
    WebMark,
    /// <summary>The deck on air turns to its next page (the click-through's NEXT does this first while a deck is on).</summary>
    DeckNext,
    /// <summary>The deck on air turns back a page.</summary>
    DeckPrev,
    /// <summary>The deck on air turns to a page: Value = a number (1-based), first, last, next or prev.</summary>
    DeckPage,
    /// <summary>A line to a device of the Interactive area: Target = the device's name (or "" / * for the first), Value = the text.</summary>
    DeviceSend,
    /// <summary>
    /// An announcement now: Target = an announcement of the Install page by name (or ""), Value = the
    /// words when no announcement is named (a name in the value finds the announcement too) — for its
    /// seconds, over the programme; the programme comes back by itself.
    /// </summary>
    Announce,
    /// <summary>The announcement on ends now.</summary>
    AnnounceOff,
    /// <summary>An advert of the Install page (Target: by name or number) plays now for its seconds, on its screens; the programme comes back.</summary>
    AdvertPlay,
    /// <summary>The advert on ends now.</summary>
    AdvertOff,
    /// <summary>The install's schedule runs: programmes by the clock, adverts and announcements at their times.</summary>
    ScheduleOn,
    /// <summary>The schedule stops moving the picture; what is on stays.</summary>
    ScheduleOff,
    /// <summary>Target = the admin passcode: the staged update is applied by the watchdog — the app exits, the files are swapped, the show comes back.</summary>
    UpdateApply,
    /// <summary>Target = the admin passcode: the app restarts under the watchdog with the show put back.</summary>
    Restart,
    /// <summary>
    /// Target = a screen (number, placement id or canvas key), Value = a look (name or id): the look's
    /// picture for that target lands on it alone as its own pattern — every other target stays.
    /// </summary>
    ScreenLook,
    /// <summary>
    /// Value = a saved preset by name: the picture being edited becomes it. A preset is a PATTERN,
    /// not a look — it carries no overlays, no countdown, no per-screen arrangement — so recalling
    /// one changes what the picture IS and leaves everything the show has dressed it with alone.
    /// It lands in the editors like any other change, so EDIT SAFE holds it for the next TAKE.
    /// </summary>
    PatternPreset,
    /// <summary>
    /// Target = a screen (number, placement id or canvas key), Value = a saved preset by name: that
    /// preset becomes that screen's own picture, live — the twin of <see cref="ScreenLook"/>, and
    /// the reason a preset saved on the Pattern page is usable from the Show panel without
    /// building a whole look around it.
    /// </summary>
    ScreenPreset,
    /// <summary>Target = a screen or canvas: its own pattern is dropped and it shows the program again.</summary>
    ScreenProgram,
    /// <summary>
    /// Target = a screen or canvas: the picture it shows on air — its own pattern, its source's when
    /// it repeats one, else the program — is loaded into the sandboxed preview to edit, then SEND or
    /// TAKE. The air is untouched (EDIT SAFE opens first when it was off). The desk's own.
    /// </summary>
    ScreenToPreview,
    /// <summary>
    /// Target = a screen or canvas; Value = a kind of picture (Grid, ColorBars, LedWall…): that kind
    /// on the target alone, live, as its own pattern — PATTERN kind for one screen; every other
    /// screen stays.
    /// </summary>
    ScreenPattern,
    /// <summary>The tile's own TAKE (round 63): the preview to this one screen alone, with the transition, as its own picture — OWN lights up; every other screen and the programme stay.</summary>
    ScreenTake,
    /// <summary>The tile's own CUT (round 63): the same, instantly.</summary>
    ScreenCut,
    /// <summary>
    /// Target = a screen; Value = the contract in words ("3840x2160 50 RGB 8 SDR", "CLEAR"): what
    /// the screen's link is meant to carry (round 65) — each word sets its property, the rest stay.
    /// </summary>
    ScreenSignal,
    /// <summary>Round 65.10: the diagnostic profile stands in for the screen's contract (ON), the contract holds again (OFF), or toggle ("").</summary>
    ScreenTestRoute,
    /// <summary>Round 65.11: what the far end says it receives on the screen's link, in the contract's words — the engineer's reading of the box's panel; CLEAR forgets it.</summary>
    ScreenReceived,
    /// <summary>
    /// The staged verbs (round 60): Target = a screen, a canvas, or empty / PGM for the programme;
    /// the picture lands on that target's PVW in the sandboxed preview and nowhere else — EDIT
    /// SAFE opens first when it was off, the air is never touched, and the next CUT or TAKE puts
    /// it up (FOCUSED for that tile alone). The right-click menus speak these, so a menu can never
    /// change what the audience sees. Value = the look's name or id.
    /// </summary>
    ScreenStageLook,
    /// <summary>Staged (see <see cref="ScreenStageLook"/>): Value = a preset's name.</summary>
    ScreenStagePreset,
    /// <summary>Staged: Value = a kind of picture.</summary>
    ScreenStagePattern,
    /// <summary>Staged: the target follows the programme again in the preview; on the programme target, what is on air comes into the preview to edit.</summary>
    ScreenStageProgram,
    /// <summary>Staged: the look on air's own picture for that target back on its PVW; the programme target loads the whole look into the preview. Refused when no look is on air.</summary>
    ScreenStageReset,
    /// <summary>
    /// Round 73: a Library tile staged on a target's PVW — a factory pattern, a media file, a saved
    /// web page, a preset or a brand kit, by name or id (Value). The picture it makes lands in the
    /// preview like the other staged verbs, so a deck's key does exactly what a click on the Library
    /// page does. Target as <see cref="ScreenStageLook"/>, or FOCUSED for the desk's editing target
    /// (the programme when no tile is focused). A brand kit is the show's colours, not a picture:
    /// it applies to the show, as its tile does.
    /// </summary>
    ScreenStageLibrary,
    /// <summary>
    /// The clip on air jumps to its last seconds (Value = how many; empty = ten) — a rehearsal
    /// skips the body of a video and still sees its end, hears the out and lets whatever follows
    /// it (a playlist's next item, a stinger's ending) happen for real.
    /// </summary>
    VideoToEnd,
    /// <summary>The clip on air plays again from its start; an ended clip comes back.</summary>
    VideoRestart,
    /// <summary>The clock overlay flips.</summary>
    ClockToggle,
    /// <summary>Value = 12 or 24: the clock's hours.</summary>
    ClockFormat,
    /// <summary>Value = on / off / toggle: the clock's seconds.</summary>
    ClockSeconds,
    /// <summary>Value = on / off / toggle: the clock's date line.</summary>
    ClockDate,
    /// <summary>The message overlay flips, its words kept.</summary>
    MessageToggle,
    /// <summary>Value = on / off / toggle: the message scrolls as a ticker, or stands still.</summary>
    MessageScroll,
    /// <summary>Value = HH:mm (24 h, local): a countdown to that time of day, on air.</summary>
    CountdownTo,
    /// <summary>Value = the words over the countdown's digits.</summary>
    CountdownLabel,
    /// <summary>
    /// The countdown flips: off when it is on air, else started as the desk has it set up (the
    /// time of day it points at, else its duration from now) — one key, like every other overlay.
    /// </summary>
    CountdownToggle,
    /// <summary>The brand logo overlay.</summary>
    LogoOn,
    LogoOff,
    LogoToggle,
    /// <summary>The picture-in-picture inset.</summary>
    PipOn,
    PipOff,
    PipToggle,
    /// <summary>The clock, the message, the countdown, the logo, the PiP and the weather chip all off: a clean picture.</summary>
    OverlaysOff,
    /// <summary>Value = a kind of picture (Grid, ColorBars, LedWall, Particles, Fractal…): the pattern on air changes kind, its settings kept.</summary>
    PatternKind,
    /// <summary>
    /// The caller's stack's standby moves: Target = next, prev, or a cue by its number, its name or
    /// its id. A selection, not a change to the screens — the one action the journal does not keep.
    /// </summary>
    CueStandby,
    /// <summary>HOLD on the caller's stack: GO is refused until released.</summary>
    CueHoldOn,
    CueHoldOff,
    /// <summary>The standby twin runs the show from here: its hold on the outputs lifts and the air it mirrored goes on.</summary>
    TwinTakeOver,
    /// <summary>A twin that took over goes back to standing by: the link is dialled again and the outputs are held.</summary>
    TwinStandBy,
    /// <summary>The main takes the show back from a standby that ran it: the standby's show and air land here, the outputs open, the standby is told to follow again.</summary>
    TwinTakeBack,
    /// <summary>The show lock on: notifications, system sounds, other apps' audio, the shortcut keys, sleep and the Windows key held off for the show.</summary>
    ShowLockOn,
    /// <summary>The show lock off: everything put back as it was.</summary>
    ShowLockOff,
    /// <summary>CALIBRATE RUN &lt;camera&gt;: the projectors measured through a camera — the structured light out, the frames in, the rig solved. Nothing is applied until CALIBRATE APPLY.</summary>
    CalibrateRun,
    /// <summary>CALIBRATE CANCEL: a run stopped; the outputs show the show again.</summary>
    CalibrateCancel,
    /// <summary>CALIBRATE DEMO: a solve against a room that is not there — the report's words without a projector.</summary>
    CalibrateDemo,
    /// <summary>CALIBRATE APPLY: each projector into its solved place with its mesh and its blend mask.</summary>
    CalibrateApply,
    /// <summary>CALIBRATE UNDO: the placements as they were before APPLY.</summary>
    CalibrateUndo,
    /// <summary>TIMER PAUSE: the stage timer stops with what is left kept.</summary>
    TimerPause,
    /// <summary>TIMER RESUME: what was left runs again from now.</summary>
    TimerResume,
    /// <summary>TIMER ADD +60 / -30: seconds onto what is left.</summary>
    TimerAdd,
    /// <summary>TIMER FLASH: the stage displays flash for a moment — "look up".</summary>
    TimerFlash,
    /// <summary>STAGE MESSAGE &lt;text&gt;: words to the speaker's display (or the crew's, with a channel), with a receipt when seen.</summary>
    StageMessage,
    /// <summary>STAGE CLEAR: the message off the displays.</summary>
    StageClear,
    /// <summary>STAGE ACK &lt;id&gt;: a stage display's receipt of a message — the desk shows it as "seen at"; a timer node forwards it to the desk.</summary>
    StageAck,
    /// <summary>
    /// The arcade node's verbs — run there when this process is the arcade, sent to every arcade node
    /// the beacon hears when it is the desk. Value carries the words: "pong 2" (the game and its
    /// players), "1 UP TAP" (a pad's key), "1280x720" (the picture's size), "on" (NDI), initials.
    /// </summary>
    ArcadeStart,
    ArcadeStop,
    ArcadePause,
    ArcadeResume,
    ArcadeAttract,
    ArcadeKey,
    ArcadeSize,
    ArcadeNdi,
    ArcadeName,
    /// <summary>The game's own window on this machine: on, off, or filling a display (FULL [display]).</summary>
    ArcadeWindow,
    /// <summary>
    /// The audience room on the hub — run on the arcade node, sent there from a desk that hears
    /// one, run on the desk itself with none heard. Value carries the words: a question line
    /// ("quiz Which hall? | A | B | correct=2 time=15"), a question id or "next", what to show
    /// ("join", "results", "leaderboard", "message", "draughts", "path", "off"), a message ("room
    /// The poll closes in 30 s", "group:Table 4 you won", "phone:Sam your answer was right"), a
    /// queue item's id or "all", "on"/"off", "open"/"close"/"reset", "new".
    /// </summary>
    PlayAdd,
    PlayOpen,
    PlayClose,
    PlayReveal,
    PlayShow,
    PlayMessage,
    PlayApprove,
    PlayReject,
    PlayAuto,
    PlayPath,
    PlayDraughts,
    PlayRoom,
    PlayExport,
    /// <summary>
    /// Rig day, gamified and opt-in: the games' switch, and the alignment game — a projector's
    /// lattice driven node by node onto the solver's targets. Value: a screen for START, "dx dy"
    /// for NUDGE. The desk's alone; never in a cue.
    /// </summary>
    /// <summary>RIG SAVE [note] (round 65.9): the rig of the moment saved as the commissioned one — the machine, the displays and their EDIDs, the contracts, the audio, the network, the clock; every boot compares against it. Value = the note.</summary>
    RigSaveKnownGood,
    /// <summary>The God's Eye (round 66) — the operator's own view of the whole show: EYE FOCUS &lt;words&gt; (Value: an id, "screen 2", a label), EYE NEXT / EYE PREV (the problems queue), EYE LENS &lt;all|video|control|audio|room|problems&gt; (Value), EYE RESET. Desk-only: a running order never moves what the desk is looking at.</summary>
    EyeFocus,
    EyeNext,
    EyePrev,
    EyeLens,
    EyeReset,
    /// <summary>
    /// Round 73: MIDI learn armed for a wire line (Value) — the next control moved on any open
    /// surface is bound to it, into the Interactive area's own table, saved with the show.
    /// Desk-only: a running order never binds a control.
    /// </summary>
    MidiLearn,
    /// <summary>Round 73: MIDI learn disarmed; nothing is bound.</summary>
    MidiLearnOff,
    /// <summary>Round 73: every control bound to a wire line (Value) forgotten, on every surface.</summary>
    MidiForget,
    /// <summary>
    /// Round 74: the desk to a page (Target = a page header, or a rail's label for its page) and,
    /// with a Value, the item selected there through the same route the menus' GO TO entries use —
    /// a cue, a look, a design, a person, a screen. Desk-only: a running order never turns the pages.
    /// </summary>
    NavPage,
    /// <summary>Round 74: the page before.</summary>
    NavBack,
    /// <summary>Round 74: the panel — the desk's home.</summary>
    NavHome,
    /// <summary>Round 74: the settings column beside the page — Value ON / OFF / TOGGLE.</summary>
    NavSettings,
    /// <summary>Round 74: the preview saved as a look — Value = the name; a look of that name is updated instead.</summary>
    LookSave,
    /// <summary>Round 74: a look updated from the preview — Value = the name, or "" for the look on air.</summary>
    LookUpdate,
    /// <summary>Round 74: a look removed — Value = the name (or #n, or an id).</summary>
    LookDelete,
    /// <summary>Round 74: a cue added after the standby (or at the end of the caller's stack) — Value = its name.</summary>
    CueAdd,
    /// <summary>Round 74: a cue removed — Value = its number, name or id.</summary>
    CueDelete,
    /// <summary>Round 74: the editing target's picture saved as a preset of the Library — Value = the name.</summary>
    PresetSave,
    /// <summary>Round 74: a lower-third design made from a preset — Value = the name, Target = the preset ("" for the first).</summary>
    LowerThirdNew,
    /// <summary>Round 67.6: the transition or video sting the next TAKE alone arrives by — Value the words (a transition, STING name, CLEAR); one shot, the show's own transition untouched.</summary>
    NextTransition,
    RigDayOn,
    RigDayOff,
    AlignStart,
    AlignStop,
    AlignNext,
    AlignPrev,
    AlignNudge,
    AlignSnap,
    /// <summary>The audience listener on (Value: a port, or empty for the one set) or off — a control setting, the desk's own.</summary>
    AudienceOn,
    AudienceOff,
    /// <summary>The plan slips: every planned start from the standby cue on moves by the Value ("+2:00", "-0:30", "+90").</summary>
    PlanShift,
    /// <summary>"We resume now": the standby cue's planned start becomes the clock and the day moves with it.</summary>
    PlanResume,
    /// <summary>The lateness made up before the next mark, the planned lengths squeezed in proportion.</summary>
    PlanCatchUp,
    /// <summary>The countdown follows the running order — its target is the standby cue's planned start (Value: on / off).</summary>
    CountdownFollow,
}

/// <summary>One thing to do to the show: a kind plus the target it acts on and an optional value.</summary>
public readonly record struct ShowAction(ShowActionKind Kind, string Target = "", string Value = "")
{
    /// <summary>No action — what a wire's handshake or query carries in the action's place.</summary>
    public static readonly ShowAction None = new(ShowActionKind.Unknown);

    /// <summary>
    /// The action's words for a log, a fault, a test's failure — every diagnostic line reads this
    /// one formatter (round 76). A kind whose target is the admin passcode
    /// (<see cref="Services.ActionSpec.CarriesSecret"/>) prints "Restart [redacted]": a secret may
    /// enter the authority path, and never leaves through a diagnostic.
    /// </summary>
    public override string ToString()
    {
        if (Target.Length == 0) return Kind.ToString();
        var target = Services.ActionSpec.CarriesSecret(Kind) ? Services.ActionSpec.Redacted : Target;
        return Value.Length == 0 ? $"{Kind} {target}" : $"{Kind} {target} {Value}";
    }
}

/// <summary>Where an action came from — the one fact a history row must never lose.</summary>
public enum OriginKind
{
    Desk,
    Keyboard,
    Clicker,
    Tcp,
    Http,
    Companion,
    Schedule,
    Playlist,
    Stinger,
    Recovery,
    Cue,
    /// <summary>A cue's auto-follow: the next cue GOing by itself after the delay the cue carries.</summary>
    Follow,
    /// <summary>An OSC message over UDP (QLab, TouchOSC, a lighting desk, Companion's OSC).</summary>
    Osc,
    /// <summary>A device of the Interactive area — an Arduino's button, a sensor, a controller over IP.</summary>
    Device,
    /// <summary>The management server an install checks in with: a command it sent back.</summary>
    Management,
    /// <summary>A caller node on the link: the show caller's own desk, calling from there.</summary>
    Caller,
}

public sealed record ActionOrigin(OriginKind Kind, string Name = "", string Endpoint = "")
{
    public static readonly ActionOrigin Desk = new(OriginKind.Desk);
    public static readonly ActionOrigin Follow = new(OriginKind.Follow);
    public static readonly ActionOrigin Keyboard = new(OriginKind.Keyboard);
    public static readonly ActionOrigin Clicker = new(OriginKind.Clicker);
    public static readonly ActionOrigin Schedule = new(OriginKind.Schedule);
    public static readonly ActionOrigin Playlist = new(OriginKind.Playlist);
    public static readonly ActionOrigin Stinger = new(OriginKind.Stinger);
    public static readonly ActionOrigin Recovery = new(OriginKind.Recovery);

    /// <summary>"Companion FOH deck", "tcp 10.0.0.12:51234", "desk".</summary>
    public string Label
    {
        get
        {
            var kind = Kind.ToString().ToLowerInvariant();
            if (Name.Length > 0) return $"{kind} {Name}";
            if (Endpoint.Length > 0) return $"{kind} {Endpoint}";
            return kind;
        }
    }

    /// <summary>
    /// Round 79 (the rule the wire's recorder used since round 74, now the one place): the show's own automation —
    /// a cue, the schedule, the playlist, a sting's landing, the recovery, a follower's forward — against a hand on a
    /// key. Automation asserts an end state and is not refused when it already holds; a hand is told what its press
    /// did, and a press that would do nothing is refused with the way out.
    /// </summary>
    public bool IsAutomation => Kind is OriginKind.Cue or OriginKind.Follow or OriginKind.Schedule or OriginKind.Playlist or OriginKind.Stinger or OriginKind.Recovery;

    public override string ToString() => Label;
}

public enum ActionStatus
{
    /// <summary>Applied; the screens (or the service) reflect it now.</summary>
    Done,
    /// <summary>Asked for; a service confirms or fails it later (stream start, a clip decode).</summary>
    Requested,
    /// <summary>Tried and failed (file missing, look unreadable).</summary>
    Failed,
    /// <summary>Not attempted: the show's state forbids it (prep mode, no sandbox open, unknown target).</summary>
    Refused,
}

/// <summary>
/// Round 79: whether the room could see an action land — read from the outputs and the blackout after it
/// ran, and stamped on the result by the executor for every origin. The words a result carries can say
/// "fades up"; this says whether anybody could have seen it. The journal keeps it as a field, so a report
/// is read by fields and not by parsing the words.
/// </summary>
public enum ActionVisibility
{
    /// <summary>Not stamped — a result built off the executor (a node's paper stack, a forwarded verb).</summary>
    Unknown,
    /// <summary>The outputs were live and the blackout down: the screens showed the result.</summary>
    OutputsLive,
    /// <summary>The outputs were closed: nothing reached a screen.</summary>
    OutputsOff,
    /// <summary>The blackout was up: the screens stayed black.</summary>
    Blackout,
}

/// <summary>
/// Round 79: what an action did to what the audience sees — the air's pictures compared before and after by
/// the executor, for the take family (TAKE, CUT, a tile's own). Attempts are not facts: a take journaled Done
/// says a take ran; this says whether it changed anything.
/// </summary>
public enum ActionEffect
{
    /// <summary>Not measured for this kind of action.</summary>
    NotMeasured,
    /// <summary>The pictures on the screens changed.</summary>
    Changed,
    /// <summary>The pictures stayed; only how they are held changed — a screen became its own (OWN), or a pin lifted.</summary>
    OwnOnly,
    /// <summary>Nothing on air changed.</summary>
    Nothing,
}

public sealed record ActionResult(ActionStatus Status, string Message = "")
{
    public bool Ok => Status is ActionStatus.Done or ActionStatus.Requested;

    /// <summary>A cue's run names itself here — its execution id and what it still waits for — so the stack's history row can be settled by the receipts that come later. Null for anything that is not a cue.</summary>
    public Services.CueExecution? Execution { get; init; }

    /// <summary>Round 79: whether the room could see this land — the executor's stamp, from the outputs and the blackout after the action ran.</summary>
    public ActionVisibility Visibility { get; init; }

    /// <summary>Round 79: what this did to what the audience sees — measured by the executor for the take family, <see cref="ActionEffect.NotMeasured"/> for the rest.</summary>
    public ActionEffect Effect { get; init; }

    public static ActionResult Done(string message = "") => new(ActionStatus.Done, message);
    public static ActionResult Requested(string message = "") => new(ActionStatus.Requested, message);
    public static ActionResult Failed(string message) => new(ActionStatus.Failed, message);
    public static ActionResult Refused(string message) => new(ActionStatus.Refused, message);
}
