using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>What an action's Target names.</summary>
public enum TargetKind
{
    None,
    Look,
    Stinger,
    Part,
    Screen,
    Canvas,
    Stack,
    /// <summary>A break-music library entry (blank = resume what is loaded).</summary>
    Music,
    /// <summary>A lower third design.</summary>
    LowerThird,
    /// <summary>A web page in the show, by nickname, address or a word of it (blank = the page on air).</summary>
    Page,
    /// <summary>A device of the Interactive area, by name or place (blank = the first enabled one).</summary>
    Device,
    /// <summary>An announcement or an advert of the Install page, by name (blank: the words in the value are the announcement).</summary>
    Slot,
    /// <summary>A track of the audio playlist — a row's id, its name, a file's name or its number in the order (blank = play or resume the list).</summary>
    Track,
    /// <summary>Where a fade lands: blank = every screen; FOCUSED, TICKED, GROUPS, SCREEN n, GROUP A, or ID &lt;screen id&gt; (see <see cref="FadeScope"/>).</summary>
    Place,
    /// <summary>A sound of the routing matrix: programme, a screen's own picture, the preview, music, vog, sting, tone.</summary>
    AudioSource,
    /// <summary>A destination of the routing matrix: a Windows output by name or an NDI send.</summary>
    AudioDestination,
    /// <summary>Where a picture is staged on the preview: a screen, a canvas, or blank / PGM for the programme.</summary>
    Stage,
    /// <summary>What the RUN surface's monitor shows: a screen, a canvas, PGM for the programme, or MAIN / blank for the main screen.</summary>
    Monitor,
}

/// <summary>What an action's Value holds (nothing else takes free text).</summary>
public enum ValueKind
{
    None,
    /// <summary>"cut", a fade in milliseconds, or empty for the show default.</summary>
    Transition,
    Minutes,
    Text,
    /// <summary>A whole number of percent, 0–125.</summary>
    Percent,
    /// <summary>A whole number of percent, 0–100 (a Spotify device's own range).</summary>
    Level,
    /// <summary>A lower-thirds library entry (id, name or number) to fill the design with first; empty = as designed.</summary>
    Person,
    /// <summary>A key chord ("ArrowRight", "Space", "Ctrl+Shift+F5") or a page action ("next", "play", "present"…).</summary>
    WebKey,
    /// <summary>"x y" in percent of a page.</summary>
    Point,
    /// <summary>A point in a page's video: "1:23", "83", "1m23s"; empty for the mark or the player's own place; "off" to disarm.</summary>
    VideoTime,
    /// <summary>A destination of the routing matrix, with " AT &lt;dB&gt;" for a level other than 0 ("Info HDMI AT -6").</summary>
    AudioRouteTo,
    /// <summary>What a VOG does on a destination: duck, replace or leave.</summary>
    VogMode,
    /// <summary>A deck page: a number (1-based), first or last.</summary>
    DeckPage,
    /// <summary>A look of the show (id or name) — a picker, not a text box.</summary>
    Look,
    /// <summary>A pattern saved as a preset, by name — a picker filled from the presets folder beside the show.</summary>
    Preset,
    /// <summary>A number of seconds (decimals allowed); empty means the action's own default.</summary>
    Seconds,
    /// <summary>The weather chip's view: now, day (the rest of today) or tomorrow.</summary>
    WeatherView,
    /// <summary>on, off or toggle (also 1 / 0, true / false, show / hide, yes / no).</summary>
    Switch,
    /// <summary>The clock's hours: 12 or 24.</summary>
    Hours,
    /// <summary>A time of day, HH:mm, 24-hour.</summary>
    ClockTime,
    /// <summary>A kind of picture — a <see cref="Model.PatternKind"/> by name (Grid, ColorBars, Media, Particles, Fractal…).</summary>
    PatternKind,
    /// <summary>A web address (https://…) or a local HTML file.</summary>
    Address,
    /// <summary>A screen's role: main, confidence, info or repeater.</summary>
    Role,
}

/// <summary>
/// The one table for the one vocabulary: for each <see cref="ShowActionKind"/>, what its target
/// is, what its value is, how it reads, and whether a cue may carry it. The cue editor, the
/// validator, the sheet, the assistant's catalogue and the executor all read it; a kind added to
/// the vocabulary must take a place here — as a cue's kind in <see cref="CueKinds"/> or as the
/// desk's alone in <see cref="DeskOnly"/> — and a test holds the door.
/// </summary>
public static class ActionSpec
{
    public static (TargetKind Target, ValueKind Value) For(ShowActionKind kind) => kind switch
    {
        ShowActionKind.ApplyLook => (TargetKind.Look, ValueKind.Transition),
        ShowActionKind.ApplyLookToPreview => (TargetKind.Look, ValueKind.None),
        ShowActionKind.LookBack => (TargetKind.None, ValueKind.Transition),
        ShowActionKind.AudioPlay => (TargetKind.Track, ValueKind.None),
        ShowActionKind.StingerFire => (TargetKind.Stinger, ValueKind.None),
        ShowActionKind.StingerStop => (TargetKind.None, ValueKind.None),
        ShowActionKind.PlaylistPart => (TargetKind.Part, ValueKind.None),
        ShowActionKind.ScreenOn or ShowActionKind.ScreenOff or ShowActionKind.ScreenToggle => (TargetKind.Screen, ValueKind.None),
        ShowActionKind.ScreenLock or ShowActionKind.ScreenUnlock or ShowActionKind.ScreenLockToggle => (TargetKind.Screen, ValueKind.None),
        ShowActionKind.ScreenRole => (TargetKind.Screen, ValueKind.Role),
        ShowActionKind.ScreenLabel => (TargetKind.Screen, ValueKind.Text),
        ShowActionKind.CanvasOn or ShowActionKind.CanvasOff => (TargetKind.Canvas, ValueKind.None),
        ShowActionKind.CountdownStart => (TargetKind.None, ValueKind.Minutes),
        ShowActionKind.TimerAdd => (TargetKind.None, ValueKind.Text),
        ShowActionKind.StageMessage or ShowActionKind.StageAck => (TargetKind.None, ValueKind.Text),
        ShowActionKind.ArcadeStart or ShowActionKind.ArcadeAttract or ShowActionKind.ArcadeKey or ShowActionKind.ArcadeSize or ShowActionKind.ArcadeNdi or ShowActionKind.ArcadeName or ShowActionKind.ArcadeWindow => (TargetKind.None, ValueKind.Text),
        ShowActionKind.PlayAdd or ShowActionKind.PlayOpen or ShowActionKind.PlayShow or ShowActionKind.PlayMessage or ShowActionKind.PlayApprove or ShowActionKind.PlayReject or ShowActionKind.PlayAuto or ShowActionKind.PlayPath or ShowActionKind.PlayDraughts or ShowActionKind.PlayRoom => (TargetKind.None, ValueKind.Text),
        ShowActionKind.AlignStart or ShowActionKind.AlignNudge => (TargetKind.None, ValueKind.Text),
        ShowActionKind.AudienceOn => (TargetKind.None, ValueKind.Text),
        ShowActionKind.CountdownTo => (TargetKind.None, ValueKind.ClockTime),
        ShowActionKind.CountdownLabel => (TargetKind.None, ValueKind.Text),
        ShowActionKind.CountdownFollow => (TargetKind.None, ValueKind.Switch),
        ShowActionKind.MessageOn => (TargetKind.None, ValueKind.Text),
        ShowActionKind.MessageScroll => (TargetKind.None, ValueKind.Switch),
        ShowActionKind.ClockFormat => (TargetKind.None, ValueKind.Hours),
        ShowActionKind.ClockSeconds or ShowActionKind.ClockDate => (TargetKind.None, ValueKind.Switch),
        ShowActionKind.PatternKind => (TargetKind.None, ValueKind.PatternKind),
        ShowActionKind.AudioVolume => (TargetKind.None, ValueKind.Percent),
        ShowActionKind.AudioRouting => (TargetKind.None, ValueKind.Switch),
        ShowActionKind.AudioRoute => (TargetKind.AudioSource, ValueKind.AudioRouteTo),
        ShowActionKind.AudioUnroute => (TargetKind.AudioSource, ValueKind.Text),
        ShowActionKind.AudioVogMode => (TargetKind.AudioDestination, ValueKind.VogMode),
        ShowActionKind.SpotifyPlay => (TargetKind.Music, ValueKind.None),
        ShowActionKind.SpotifyVolume => (TargetKind.None, ValueKind.Level),
        ShowActionKind.LowerThirdShow => (TargetKind.LowerThird, ValueKind.Person),
        ShowActionKind.LowerThirdPreview => (TargetKind.LowerThird, ValueKind.Person),
        ShowActionKind.ListArm or ShowActionKind.ListDisarm or ShowActionKind.ListGo
            or ShowActionKind.ListBack or ShowActionKind.ListReset => (TargetKind.Stack, ValueKind.None),
        ShowActionKind.WebKey => (TargetKind.Page, ValueKind.WebKey),
        ShowActionKind.WebClick => (TargetKind.Page, ValueKind.Point),
        ShowActionKind.WebType => (TargetKind.Page, ValueKind.Text),
        ShowActionKind.WebReload => (TargetKind.Page, ValueKind.None),
        ShowActionKind.WebOpen => (TargetKind.Page, ValueKind.Address),
        ShowActionKind.WebArm => (TargetKind.Page, ValueKind.VideoTime),
        ShowActionKind.WebMark => (TargetKind.Page, ValueKind.VideoTime),
        ShowActionKind.DeckPage => (TargetKind.None, ValueKind.DeckPage),
        ShowActionKind.DeviceSend => (TargetKind.Device, ValueKind.Text),
        ShowActionKind.Announce => (TargetKind.Slot, ValueKind.Text),
        ShowActionKind.AdvertPlay => (TargetKind.Slot, ValueKind.None),
        ShowActionKind.ScreenLook => (TargetKind.Screen, ValueKind.Look),
        ShowActionKind.PatternPreset => (TargetKind.None, ValueKind.Preset),
        ShowActionKind.ScreenPreset => (TargetKind.Screen, ValueKind.Preset),
        ShowActionKind.ScreenProgram => (TargetKind.Screen, ValueKind.None),
        ShowActionKind.ScreenToPreview => (TargetKind.Screen, ValueKind.None),
        ShowActionKind.ScreenPattern => (TargetKind.Screen, ValueKind.PatternKind),
        ShowActionKind.ScreenTake or ShowActionKind.ScreenCut => (TargetKind.Screen, ValueKind.None),
        ShowActionKind.ScreenStageLook => (TargetKind.Stage, ValueKind.Look),
        ShowActionKind.ScreenStagePreset => (TargetKind.Stage, ValueKind.Preset),
        ShowActionKind.ScreenStagePattern => (TargetKind.Stage, ValueKind.PatternKind),
        ShowActionKind.ScreenStageProgram or ShowActionKind.ScreenStageReset => (TargetKind.Stage, ValueKind.None),
        ShowActionKind.RunMonitor => (TargetKind.Monitor, ValueKind.None),
        ShowActionKind.RunMonitorOff => (TargetKind.None, ValueKind.None),
        ShowActionKind.VideoToEnd => (TargetKind.None, ValueKind.Seconds),
        ShowActionKind.FadeToBlack or ShowActionKind.FadeUp => (TargetKind.Place, ValueKind.Seconds),
        ShowActionKind.WeatherView => (TargetKind.None, ValueKind.WeatherView),
        _ => (TargetKind.None, ValueKind.None),
    };

    /// <summary>What the operator reads in the kind picker, the sheet and the assistant's catalogue.</summary>
    public static string Label(ShowActionKind kind) => kind switch
    {
        ShowActionKind.Unknown => "Unknown (newer build)",
        ShowActionKind.Note => "Note only",
        ShowActionKind.ApplyLook => "Apply look",
        ShowActionKind.ApplyLookToPreview => "Load a look into the preview",
        ShowActionKind.LookBack => "The look before (back)",
        ShowActionKind.AudioPlay => "Play audio (the list, or a track)",
        ShowActionKind.AudioStop => "Stop audio",
        ShowActionKind.AudioVolume => "Audio volume",
        ShowActionKind.AudioRouting => "Audio routing — on / off",
        ShowActionKind.AudioRoute => "Audio routing — put a source on a destination",
        ShowActionKind.AudioUnroute => "Audio routing — take a source off a destination",
        ShowActionKind.AudioVogMode => "Audio routing — what a VOG does on a destination",
        ShowActionKind.AudioNext => "Audio — next track",
        ShowActionKind.AudioPrev => "Audio — previous track",
        ShowActionKind.SpotifyPlay => "Break music — play",
        ShowActionKind.SpotifyPause => "Break music — pause",
        ShowActionKind.SpotifyNext => "Break music — skip track",
        ShowActionKind.SpotifyVolume => "Break music level",
        ShowActionKind.StingerFire => "Fire VOG / stinger",
        ShowActionKind.StingerStop => "Stop VOG / stinger",
        ShowActionKind.PlaylistPart => "Playlist part",
        ShowActionKind.StreamStart => "Start stream",
        ShowActionKind.StreamStop => "Stop stream",
        ShowActionKind.OutputsOn => "Outputs on",
        ShowActionKind.OutputsOff => "Outputs off",
        ShowActionKind.BlackoutOn => "Blackout on",
        ShowActionKind.BlackoutOff => "Blackout off",
        ShowActionKind.BlackoutToggle => "Blackout toggle",
        ShowActionKind.FadeToBlack => "Fade to black (every screen, or some)",
        ShowActionKind.FadeUp => "Fade up (every screen, or some)",
        ShowActionKind.FreezeOn => "Freeze every output",
        ShowActionKind.FreezeOff => "Release the freeze",
        ShowActionKind.FreezeToggle => "Freeze toggle",
        ShowActionKind.ScreenOn => "Screen on",
        ShowActionKind.ScreenOff => "Screen off",
        ShowActionKind.ScreenToggle => "Screen toggle",
        ShowActionKind.ScreenLock => "Screen lock (keeps its picture)",
        ShowActionKind.ScreenUnlock => "Screen unlock (follows cues)",
        ShowActionKind.ScreenLockToggle => "Screen lock toggle",
        ShowActionKind.ScreenRole => "Screen role (main, confidence, info, repeater)",
        ShowActionKind.ScreenLabel => "Screen label (its name on the desk)",
        ShowActionKind.CanvasOn => "Canvas on",
        ShowActionKind.CanvasOff => "Canvas off",
        ShowActionKind.PatternKind => "Pattern — change its kind",
        ShowActionKind.CountdownStart => "Start countdown",
        ShowActionKind.CountdownTo => "Countdown to a time of day",
        ShowActionKind.CountdownStop => "Stop countdown",
        ShowActionKind.CountdownToggle => "Countdown on / off",
        ShowActionKind.CountdownLabel => "Countdown label",
        ShowActionKind.TimerPause => "Stage timer — pause",
        ShowActionKind.TimerResume => "Stage timer — resume",
        ShowActionKind.TimerAdd => "Stage timer — add seconds (+60 / -30)",
        ShowActionKind.TimerFlash => "Stage displays — flash",
        ShowActionKind.StageMessage => "Message to stage",
        ShowActionKind.StageClear => "Message to stage — clear",
        ShowActionKind.StageAck => "Stage display — a message seen (ACK)",
        ShowActionKind.ArcadeStart => "Arcade — start a game (pong 2, snake, breakout)",
        ShowActionKind.ArcadeStop => "Arcade — stop",
        ShowActionKind.ArcadePause => "Arcade — pause",
        ShowActionKind.ArcadeResume => "Arcade — resume",
        ShowActionKind.ArcadeAttract => "Arcade — the house plays (attract mode)",
        ShowActionKind.ArcadeKey => "Arcade — a pad's key (1 UP TAP)",
        ShowActionKind.ArcadeSize => "Arcade — the picture's size (1920x1080)",
        ShowActionKind.ArcadeNdi => "Arcade — NDI out (on / off)",
        ShowActionKind.ArcadeName => "Arcade — initials for the last score",
        ShowActionKind.ArcadeWindow => "Arcade — the game's own window (on / off / full [display])",
        ShowActionKind.PlayAdd => "Audience — add a question (quiz Which hall? | A | B | correct=2 time=15)",
        ShowActionKind.PlayOpen => "Audience — open a question (next, or its id)",
        ShowActionKind.PlayClose => "Audience — close the question",
        ShowActionKind.PlayReveal => "Audience — reveal the answer / results",
        ShowActionKind.PlayShow => "Audience — the wall shows (join, results, leaderboard, message, draughts, path, off)",
        ShowActionKind.PlayMessage => "Audience — a message back (room …, group:Table 4 …, phone:Sam …)",
        ShowActionKind.PlayApprove => "Audience — approve from the queue (an id, or all)",
        ShowActionKind.PlayReject => "Audience — reject from the queue",
        ShowActionKind.PlayAuto => "Audience — words straight to the wall (on / off)",
        ShowActionKind.PlayPath => "Audience — the path (open, close, reset)",
        ShowActionKind.PlayDraughts => "Audience — draughts (reset)",
        ShowActionKind.PlayRoom => "Audience — the room (new code, or reset)",
        ShowActionKind.PlayExport => "Audience — export the room's results",
        ShowActionKind.RigDayOn => "Rig day games — on",
        ShowActionKind.RigDayOff => "Rig day games — off",
        ShowActionKind.AlignStart => "Alignment game — start on a projector",
        ShowActionKind.AlignStop => "Alignment game — stop",
        ShowActionKind.AlignNext => "Alignment game — next node",
        ShowActionKind.AlignPrev => "Alignment game — previous node",
        ShowActionKind.AlignNudge => "Alignment game — nudge the node (dx dy)",
        ShowActionKind.AlignSnap => "Alignment game — snap the node to its target",
        ShowActionKind.AudienceOn => "Audience listener — on (a port, or the one set)",
        ShowActionKind.AudienceOff => "Audience listener — off",
        ShowActionKind.MessageOn => "Message on",
        ShowActionKind.MessageOff => "Message off",
        ShowActionKind.MessageToggle => "Message toggle",
        ShowActionKind.MessageScroll => "Message — scroll on / off",
        ShowActionKind.ClockOn => "Clock on",
        ShowActionKind.ClockOff => "Clock off",
        ShowActionKind.ClockToggle => "Clock toggle",
        ShowActionKind.ClockFormat => "Clock — 12 or 24 hours",
        ShowActionKind.ClockSeconds => "Clock — seconds on / off",
        ShowActionKind.ClockDate => "Clock — date line on / off",
        ShowActionKind.LogoOn => "Logo on",
        ShowActionKind.LogoOff => "Logo off",
        ShowActionKind.LogoToggle => "Logo toggle",
        ShowActionKind.PipOn => "Picture-in-picture on",
        ShowActionKind.PipOff => "Picture-in-picture off",
        ShowActionKind.PipToggle => "Picture-in-picture toggle",
        ShowActionKind.WeatherOn => "Weather on",
        ShowActionKind.WeatherOff => "Weather off",
        ShowActionKind.WeatherToggle => "Weather toggle",
        ShowActionKind.WeatherView => "Weather — the view (now / day / tomorrow)",
        ShowActionKind.OverlaysOff => "Overlays off (a clean picture)",
        ShowActionKind.DuckOn => "Duck for an announcement",
        ShowActionKind.DuckOff => "Lift the duck",
        ShowActionKind.DuckToggle => "Duck toggle",
        ShowActionKind.ToneOn => "Line-up tone on",
        ShowActionKind.ToneOff => "Line-up tone off",
        ShowActionKind.StopAll => "Stop all (sound, stingers, tone)",
        ShowActionKind.LowerThirdShow => "Lower third on",
        ShowActionKind.LowerThirdHide => "Lower third off",
        ShowActionKind.LowerThirdPreview => "Lower third to preview",
        ShowActionKind.LowerThirdPreviewOff => "Lower third preview off",
        ShowActionKind.LowerThirdTake => "Lower third take (preview to air)",
        ShowActionKind.LowerThirdUpdate => "Lower third update (edits to air)",
        ShowActionKind.ListArm => "Arm a list",
        ShowActionKind.ListDisarm => "Disarm a list",
        ShowActionKind.ListGo => "GO on a list",
        ShowActionKind.ListBack => "Back on a list",
        ShowActionKind.ListReset => "Reset a list",
        ShowActionKind.WebKey => "Web page — key or action",
        ShowActionKind.WebClick => "Web page — click",
        ShowActionKind.WebType => "Web page — type text",
        ShowActionKind.WebReload => "Web page — reload",
        ShowActionKind.WebOpen => "Web page — open an address",
        ShowActionKind.WebArm => "Web page — arm the video (play from a point when it goes to air)",
        ShowActionKind.WebMark => "Web page — mark the start point",
        ShowActionKind.DeckNext => "Deck — next page",
        ShowActionKind.DeckPrev => "Deck — previous page",
        ShowActionKind.DeckPage => "Deck — go to page",
        ShowActionKind.DeviceSend => "Device — send a line",
        ShowActionKind.Announce => "Announcement on",
        ShowActionKind.AnnounceOff => "Announcement off",
        ShowActionKind.AdvertPlay => "Advert — play now",
        ShowActionKind.AdvertOff => "Advert — end now",
        ShowActionKind.ScheduleOn => "Install schedule on",
        ShowActionKind.ScheduleOff => "Install schedule off",
        ShowActionKind.ScreenLook => "Screen — its own look",
        ShowActionKind.PatternPreset => "Preset — recall a saved pattern",
        ShowActionKind.ScreenPreset => "Screen — a saved preset on it alone",
        ShowActionKind.ScreenProgram => "Screen — back to the program",
        ShowActionKind.ScreenPattern => "Screen — a kind of picture on it alone",
        ShowActionKind.ScreenTake => "Screen — TAKE the preview to it alone",
        ShowActionKind.ScreenCut => "Screen — CUT the preview to it alone",
        ShowActionKind.ScreenStageLook => "Preview — a look staged on a screen's PVW",
        ShowActionKind.ScreenStagePreset => "Preview — a preset staged on a screen's PVW",
        ShowActionKind.ScreenStagePattern => "Preview — a kind of picture staged on a screen's PVW",
        ShowActionKind.ScreenStageProgram => "Preview — the programme staged on a screen's PVW",
        ShowActionKind.ScreenStageReset => "Preview — the look's own picture back on a screen's PVW",
        ShowActionKind.RunMonitor => "Run monitor — a screen large on the RUN surface",
        ShowActionKind.RunMonitorOff => "Run monitor off",
        ShowActionKind.VideoToEnd => "Video — jump to its last seconds",
        ShowActionKind.VideoRestart => "Video — restart from the top",
        // The desk's own: named for the journal and a refusal, never offered to a cue.
        ShowActionKind.Take => "TAKE",
        ShowActionKind.Cut => "CUT",
        ShowActionKind.Identify => "Identify the screens",
        ShowActionKind.ApplyLookHotkey => "Look hotkey (F-key slot)",
        ShowActionKind.PresenterNext => "Clicker — next",
        ShowActionKind.PresenterPrev => "Clicker — previous",
        ShowActionKind.CueFire => "Fire a cue",
        ShowActionKind.CueGo => "GO on the caller's stack",
        ShowActionKind.CueStandby => "Standby on the caller's stack",
        ShowActionKind.CueHoldOn => "Hold the caller's stack",
        ShowActionKind.CueHoldOff => "Release the hold",
        ShowActionKind.PlanShift => "Plan — slip the planned starts from the standby cue on",
        ShowActionKind.PlanResume => "Plan — resume now: the standby cue's planned start is the clock",
        ShowActionKind.PlanCatchUp => "Plan — catch up before the next mark",
        ShowActionKind.CountdownFollow => "Countdown — follow the running order",
        ShowActionKind.ReviewOn => "Review on the multiview",
        ShowActionKind.ReviewOff => "Review off",
        ShowActionKind.ReviewToggle => "Review toggle",
        ShowActionKind.ScreenToPreview => "Screen — its picture into the preview",
        ShowActionKind.UpdateApply => "Apply the staged update",
        ShowActionKind.Restart => "Restart Patterns",
        ShowActionKind.TwinTakeOver => "Twin — take the show over",
        ShowActionKind.TwinStandBy => "Twin — stand by again",
        ShowActionKind.TwinTakeBack => "Twin — take the show back",
        ShowActionKind.ShowLockOn => "Show lock — hold the machine",
        ShowActionKind.ShowLockOff => "Show lock — release the machine",
        ShowActionKind.CalibrateRun => "Calibrate — measure the projectors through a camera",
        ShowActionKind.CalibrateCancel => "Calibrate — cancel the run",
        ShowActionKind.CalibrateDemo => "Calibrate — a run against a room that is not there",
        ShowActionKind.CalibrateApply => "Calibrate — apply the solution to the rig",
        ShowActionKind.CalibrateUndo => "Calibrate — undo the last apply",
        _ => kind.ToString(),
    };

    /// <summary>
    /// The kinds a cue may carry, in the picker's order — the whole vocabulary but the desk's own
    /// (<see cref="DeskOnly"/>) and Unknown. Every kind of <see cref="ShowActionKind"/> is in one
    /// of the two lists; a test holds the door.
    /// </summary>
    public static readonly IReadOnlyList<ShowActionKind> CueKinds = new[]
    {
        ShowActionKind.ApplyLook, ShowActionKind.Note, ShowActionKind.ApplyLookToPreview, ShowActionKind.LookBack,
        ShowActionKind.AudioPlay, ShowActionKind.AudioStop, ShowActionKind.AudioNext, ShowActionKind.AudioPrev, ShowActionKind.AudioVolume,
        ShowActionKind.AudioRouting, ShowActionKind.AudioRoute, ShowActionKind.AudioUnroute, ShowActionKind.AudioVogMode,
        ShowActionKind.SpotifyPlay, ShowActionKind.SpotifyPause, ShowActionKind.SpotifyNext, ShowActionKind.SpotifyVolume,
        ShowActionKind.StingerFire, ShowActionKind.StingerStop,
        ShowActionKind.PlaylistPart,
        ShowActionKind.StreamStart, ShowActionKind.StreamStop,
        ShowActionKind.BlackoutOn, ShowActionKind.BlackoutOff, ShowActionKind.BlackoutToggle,
        ShowActionKind.FadeToBlack, ShowActionKind.FadeUp,
        ShowActionKind.FreezeOn, ShowActionKind.FreezeOff, ShowActionKind.FreezeToggle,
        ShowActionKind.OutputsOn, ShowActionKind.OutputsOff,
        ShowActionKind.ScreenOn, ShowActionKind.ScreenOff, ShowActionKind.ScreenToggle,
        ShowActionKind.ScreenLock, ShowActionKind.ScreenUnlock, ShowActionKind.ScreenLockToggle, ShowActionKind.ScreenRole,
        ShowActionKind.ScreenLook, ShowActionKind.ScreenProgram,
        ShowActionKind.PatternPreset, ShowActionKind.ScreenPreset, ShowActionKind.ScreenPattern,
        ShowActionKind.ScreenStageLook, ShowActionKind.ScreenStagePreset, ShowActionKind.ScreenStagePattern, ShowActionKind.ScreenStageProgram, ShowActionKind.ScreenStageReset,
        ShowActionKind.RunMonitor, ShowActionKind.RunMonitorOff,
        ShowActionKind.CanvasOn, ShowActionKind.CanvasOff,
        ShowActionKind.PatternKind,
        ShowActionKind.CountdownStart, ShowActionKind.CountdownTo, ShowActionKind.CountdownStop, ShowActionKind.CountdownToggle, ShowActionKind.CountdownLabel, ShowActionKind.CountdownFollow,
        ShowActionKind.TimerPause, ShowActionKind.TimerResume, ShowActionKind.TimerAdd, ShowActionKind.TimerFlash, ShowActionKind.StageMessage, ShowActionKind.StageClear,
        ShowActionKind.ArcadeStart, ShowActionKind.ArcadeStop, ShowActionKind.ArcadePause, ShowActionKind.ArcadeResume, ShowActionKind.ArcadeAttract, ShowActionKind.ArcadeKey, ShowActionKind.ArcadeSize, ShowActionKind.ArcadeNdi, ShowActionKind.ArcadeName, ShowActionKind.ArcadeWindow,
        ShowActionKind.PlayAdd, ShowActionKind.PlayOpen, ShowActionKind.PlayClose, ShowActionKind.PlayReveal, ShowActionKind.PlayShow, ShowActionKind.PlayMessage, ShowActionKind.PlayApprove, ShowActionKind.PlayReject, ShowActionKind.PlayAuto, ShowActionKind.PlayPath, ShowActionKind.PlayDraughts, ShowActionKind.PlayRoom, ShowActionKind.PlayExport,
        ShowActionKind.MessageOn, ShowActionKind.MessageOff, ShowActionKind.MessageToggle, ShowActionKind.MessageScroll,
        ShowActionKind.ClockOn, ShowActionKind.ClockOff, ShowActionKind.ClockToggle, ShowActionKind.ClockFormat, ShowActionKind.ClockSeconds, ShowActionKind.ClockDate,
        ShowActionKind.LogoOn, ShowActionKind.LogoOff, ShowActionKind.LogoToggle,
        ShowActionKind.PipOn, ShowActionKind.PipOff, ShowActionKind.PipToggle,
        ShowActionKind.WeatherOn, ShowActionKind.WeatherOff, ShowActionKind.WeatherToggle, ShowActionKind.WeatherView,
        ShowActionKind.OverlaysOff,
        ShowActionKind.LowerThirdShow, ShowActionKind.LowerThirdHide, ShowActionKind.LowerThirdPreview, ShowActionKind.LowerThirdPreviewOff,
        ShowActionKind.LowerThirdTake, ShowActionKind.LowerThirdUpdate,
        ShowActionKind.WebKey, ShowActionKind.WebClick, ShowActionKind.WebType, ShowActionKind.WebReload, ShowActionKind.WebOpen,
        ShowActionKind.WebArm, ShowActionKind.WebMark,
        ShowActionKind.DeckNext, ShowActionKind.DeckPrev, ShowActionKind.DeckPage,
        ShowActionKind.VideoRestart, ShowActionKind.VideoToEnd,
        ShowActionKind.DeviceSend,
        ShowActionKind.Announce, ShowActionKind.AnnounceOff, ShowActionKind.AdvertPlay, ShowActionKind.AdvertOff,
        ShowActionKind.ScheduleOn, ShowActionKind.ScheduleOff,
        ShowActionKind.DuckOn, ShowActionKind.DuckOff, ShowActionKind.DuckToggle,
        ShowActionKind.ToneOn, ShowActionKind.ToneOff,
        ShowActionKind.StopAll,
        ShowActionKind.ListArm, ShowActionKind.ListDisarm, ShowActionKind.ListGo, ShowActionKind.ListBack, ShowActionKind.ListReset,
    };

    /// <summary>
    /// Why a kind is the desk's alone — null for the kinds a cue may carry. Few, and each with a
    /// reason: TAKE and CUT send the desk's half-built preview to air and a running order never
    /// does that by itself; the stack's own transport in a cue is a loop waiting to happen; the
    /// admin verbs sit behind the passcode; the rest are the desk looking at itself.
    /// </summary>
    public static string? DeskOnly(ShowActionKind kind) => kind switch
    {
        ShowActionKind.Take or ShowActionKind.Cut or ShowActionKind.ScreenTake or ShowActionKind.ScreenCut => "a desk key: it sends the preview to air, and a running order never takes a half-built preview by itself",
        ShowActionKind.CueFire or ShowActionKind.CueGo or ShowActionKind.CueStandby or ShowActionKind.CueHoldOn or ShowActionKind.CueHoldOff
            => "the stack's own transport — a cue firing, holding or re-aiming the stack it runs on is a loop waiting to happen (GO on a list names another list)",
        ShowActionKind.PresenterNext or ShowActionKind.PresenterPrev => "the clicker's own keys — Back / GO on a list name the list",
        ShowActionKind.PlanShift or ShowActionKind.PlanResume or ShowActionKind.PlanCatchUp => "the day's slip is the caller's press — a cue that moved the plan it is on would move itself",
        ShowActionKind.ApplyLookHotkey => "an F-key's slot — a cue names the look itself",
        ShowActionKind.Identify => "the rig's own identify pass, for the desk at set-up",
        ShowActionKind.ReviewOn or ShowActionKind.ReviewOff or ShowActionKind.ReviewToggle => "the desk's own check of its preview on the multiview",
        ShowActionKind.ScreenToPreview => "the desk loading its preview from a screen to edit — an edit, not a step of the show",
        ShowActionKind.UpdateApply or ShowActionKind.Restart => "an admin verb behind the passcode",
        ShowActionKind.ScreenLabel => "the rig's own naming, at set-up on the Screens page or from a remote — a running order never renames a screen",
        ShowActionKind.TwinTakeOver or ShowActionKind.TwinStandBy or ShowActionKind.TwinTakeBack => "the standby twin's own decision to run the show, or to follow again, or the main's to take it back — a cue never decides which machine is the main",
        ShowActionKind.ShowLockOn or ShowActionKind.ShowLockOff => "this machine's own hold on Windows — it goes on with the outputs and off with them, or from the Machine page and the wire; a cue never changes the machine's settings",
        ShowActionKind.CalibrateRun or ShowActionKind.CalibrateCancel or ShowActionKind.CalibrateDemo or ShowActionKind.CalibrateApply or ShowActionKind.CalibrateUndo
            => "the rig's own measuring at set-up — the outputs show structured light for a minute and the placements move; a running order never re-aims the projectors",
        ShowActionKind.RigDayOn or ShowActionKind.RigDayOff or ShowActionKind.AlignStart or ShowActionKind.AlignStop or ShowActionKind.AlignNext or ShowActionKind.AlignPrev or ShowActionKind.AlignNudge or ShowActionKind.AlignSnap
            => "rig day's games are the desk's own — the keys, the Screens page and the wire, never a cue",
        ShowActionKind.AudienceOn or ShowActionKind.AudienceOff
            => "the audience listener is a control setting — the Remote page and the wire, never a cue",
        ShowActionKind.StageAck => "a stage display's own receipt — the ACK on the stage page, forwarded by a timer node; never a cue",
        _ => null,
    };

    /// <summary>
    /// Content actions: a video stinger cannot share a cue with these (the clip owns every screen).
    /// Break music is sound only and is deliberately not here.
    /// </summary>
    public static bool ChangesContent(ShowActionKind kind) => kind is
        ShowActionKind.ApplyLook or ShowActionKind.PlaylistPart or ShowActionKind.PatternKind or
        ShowActionKind.ScreenOn or ShowActionKind.ScreenOff or ShowActionKind.ScreenToggle or ShowActionKind.CanvasOn or ShowActionKind.CanvasOff or
        ShowActionKind.ScreenLook or ShowActionKind.ScreenProgram
        or ShowActionKind.PatternPreset or ShowActionKind.ScreenPreset or ShowActionKind.ScreenPattern;

    /// <summary>The staged verbs: a picture on a target's PVW in the preview, never on air (see <see cref="ShowActionKind.ScreenStageLook"/>).</summary>
    public static bool IsStaged(ShowActionKind kind) => kind is
        ShowActionKind.ScreenStageLook or ShowActionKind.ScreenStagePreset or ShowActionKind.ScreenStagePattern
        or ShowActionKind.ScreenStageProgram or ShowActionKind.ScreenStageReset;

    /// <summary>A percent value: a number from 0 to 125 (the player's own ceiling, ≈ +2 dB).</summary>
    public static bool TryParsePercent(string? value, out double percent)
    {
        percent = 0;
        if (!double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        if (double.IsNaN(v) || v < 0 || v > 125) return false;
        percent = v;
        return true;
    }

    /// <summary>A level value: a number from 0 to 100 (a Spotify device's own volume range).</summary>
    public static bool TryParseLevel(string? value, out double level)
    {
        level = 0;
        if (!double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        if (double.IsNaN(v) || v < 0 || v > 100) return false;
        level = v;
        return true;
    }

    /// <summary>A transition value: empty, "cut", or a whole number of milliseconds.</summary>
    public static bool TryParseTransition(string? value, out bool cut, out int fadeMs)
        => TryParseTransition(value, out cut, out fadeMs, out _, out _, out _);

    /// <summary>
    /// What a recall says about how it should arrive: "cut", a fade in milliseconds, the name of a
    /// transition, a reactive scene to wipe with, the way it travels, or any of those together in
    /// any order — "wipe 800", "dip", "stinger", "wipe left 600", "reactive vortex 1200",
    /// "1200 reactive". Blank means the show's own. Anything it cannot read is refused rather than
    /// guessed at, so a typo in a cue is caught by the checks and not on the wall.
    /// </summary>
    public static bool TryParseTransition(string? value, out bool cut, out int fadeMs, out TransitionKind? kind, out ReactiveScene? scene, out TransitionDirection? direction)
    {
        cut = false;
        fadeMs = -1;
        kind = null;
        scene = null;
        direction = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        foreach (var word in value.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(word, "cut", StringComparison.OrdinalIgnoreCase))
            {
                cut = true;
                continue;
            }
            if (int.TryParse(word, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms >= 0)
            {
                fadeMs = ms;
                continue;
            }
            if (TransitionWord(word) is { } k)
            {
                kind = k;
                continue;
            }
            if (SceneWord(word) is { } sc)
            {
                scene = sc;
                kind ??= TransitionKind.Reactive; // naming a scene is asking for the scene to wipe with
                continue;
            }
            if (DirectionWord(word) is { } way)
            {
                direction = way; // a way on its own travels the show's own transition
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary>The words a transition answers to, as an operator would write them on a sheet.</summary>
    public static TransitionKind? TransitionWord(string word) => word.ToLowerInvariant() switch
    {
        "dissolve" or "fade" or "mix" or "crossfade" => TransitionKind.Dissolve,
        "dip" or "dipto" or "dtb" => TransitionKind.Dip,
        "wipe" => TransitionKind.Wipe,
        "push" or "slide" => TransitionKind.Push,
        "stinger" or "brand" or "brandstinger" => TransitionKind.BrandStinger,
        "reactive" or "scene" => TransitionKind.Reactive,
        _ => null,
    };

    /// <summary>The way a wipe or a push travels, as an operator would write it.</summary>
    public static TransitionDirection? DirectionWord(string word) => word.ToLowerInvariant() switch
    {
        "left" => TransitionDirection.Left,
        "right" => TransitionDirection.Right,
        "up" => TransitionDirection.Up,
        "down" => TransitionDirection.Down,
        _ => null,
    };

    /// <summary>A reactive scene by name, for a transition that wipes with one.</summary>
    public static ReactiveScene? SceneWord(string word)
    {
        foreach (var s in Enum.GetValues<ReactiveScene>())
        {
            if (string.Equals(s.ToString(), word, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return word.ToLowerInvariant() switch
        {
            "star" or "warp" or "starwarp" => ReactiveScene.StarWarp,
            "kaleido" => ReactiveScene.Kaleidoscope,
            _ => null,
        };
    }

    /// <summary>A switch value as the executor reads it (<see cref="OverlayControl.SwitchTo"/>): on, off or toggle, in any of their usual spellings.</summary>
    public static bool IsSwitchWord(string? value) => OverlayControl.IsSwitchWord(value);

    /// <summary>The clock's hours: 12 or 24.</summary>
    public static bool TryParseHours(string? value, out int hours)
    {
        hours = 0;
        var v = (value ?? "").Trim();
        if (v is not ("12" or "24")) return false;
        hours = v == "24" ? 24 : 12;
        return true;
    }

    /// <summary>A kind of picture by name, as the executor reads it: case, spaces, dashes and underscores ignored ("color bars", "LED wall", "grid").</summary>
    public static PatternKind? ParsePatternKind(string? value)
    {
        // Spaces, dashes and underscores are ignored, and the desk's own spelling on its labels
        // ("Colour bars") reads as the kind it names — a menu passes the enum's word, a person may not.
        var word = (value ?? "").Replace(" ", "").Replace("-", "").Replace("_", "").Trim()
            .Replace("olour", "olor", StringComparison.OrdinalIgnoreCase);
        return word.Length > 0 && Enum.TryParse<PatternKind>(word, true, out var kind) ? kind : null;
    }
}
