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
    /// <summary>A deck page: a number (1-based), first or last.</summary>
    DeckPage,
    /// <summary>A look of the show (id or name) — a picker, not a text box.</summary>
    Look,
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
        ShowActionKind.CanvasOn or ShowActionKind.CanvasOff => (TargetKind.Canvas, ValueKind.None),
        ShowActionKind.CountdownStart => (TargetKind.None, ValueKind.Minutes),
        ShowActionKind.CountdownTo => (TargetKind.None, ValueKind.ClockTime),
        ShowActionKind.CountdownLabel => (TargetKind.None, ValueKind.Text),
        ShowActionKind.MessageOn => (TargetKind.None, ValueKind.Text),
        ShowActionKind.MessageScroll => (TargetKind.None, ValueKind.Switch),
        ShowActionKind.ClockFormat => (TargetKind.None, ValueKind.Hours),
        ShowActionKind.ClockSeconds or ShowActionKind.ClockDate => (TargetKind.None, ValueKind.Switch),
        ShowActionKind.PatternKind => (TargetKind.None, ValueKind.PatternKind),
        ShowActionKind.AudioVolume => (TargetKind.None, ValueKind.Percent),
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
        ShowActionKind.DeckPage => (TargetKind.None, ValueKind.DeckPage),
        ShowActionKind.DeviceSend => (TargetKind.Device, ValueKind.Text),
        ShowActionKind.Announce => (TargetKind.Slot, ValueKind.Text),
        ShowActionKind.AdvertPlay => (TargetKind.Slot, ValueKind.None),
        ShowActionKind.ScreenLook => (TargetKind.Screen, ValueKind.Look),
        ShowActionKind.ScreenProgram => (TargetKind.Screen, ValueKind.None),
        ShowActionKind.ScreenToPreview => (TargetKind.Screen, ValueKind.None),
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
        ShowActionKind.CanvasOn => "Canvas on",
        ShowActionKind.CanvasOff => "Canvas off",
        ShowActionKind.PatternKind => "Pattern — change its kind",
        ShowActionKind.CountdownStart => "Start countdown",
        ShowActionKind.CountdownTo => "Countdown to a time of day",
        ShowActionKind.CountdownStop => "Stop countdown",
        ShowActionKind.CountdownToggle => "Countdown on / off",
        ShowActionKind.CountdownLabel => "Countdown label",
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
        ShowActionKind.ScreenProgram => "Screen — back to the program",
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
        ShowActionKind.ReviewOn => "Review on the multiview",
        ShowActionKind.ReviewOff => "Review off",
        ShowActionKind.ReviewToggle => "Review toggle",
        ShowActionKind.ScreenToPreview => "Screen — its picture into the preview",
        ShowActionKind.UpdateApply => "Apply the staged update",
        ShowActionKind.Restart => "Restart Patterns",
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
        ShowActionKind.SpotifyPlay, ShowActionKind.SpotifyPause, ShowActionKind.SpotifyNext, ShowActionKind.SpotifyVolume,
        ShowActionKind.StingerFire, ShowActionKind.StingerStop,
        ShowActionKind.PlaylistPart,
        ShowActionKind.StreamStart, ShowActionKind.StreamStop,
        ShowActionKind.BlackoutOn, ShowActionKind.BlackoutOff, ShowActionKind.BlackoutToggle,
        ShowActionKind.FadeToBlack, ShowActionKind.FadeUp,
        ShowActionKind.FreezeOn, ShowActionKind.FreezeOff, ShowActionKind.FreezeToggle,
        ShowActionKind.OutputsOn, ShowActionKind.OutputsOff,
        ShowActionKind.ScreenOn, ShowActionKind.ScreenOff, ShowActionKind.ScreenToggle,
        ShowActionKind.ScreenLock, ShowActionKind.ScreenUnlock, ShowActionKind.ScreenLockToggle,
        ShowActionKind.ScreenLook, ShowActionKind.ScreenProgram,
        ShowActionKind.CanvasOn, ShowActionKind.CanvasOff,
        ShowActionKind.PatternKind,
        ShowActionKind.CountdownStart, ShowActionKind.CountdownTo, ShowActionKind.CountdownStop, ShowActionKind.CountdownToggle, ShowActionKind.CountdownLabel,
        ShowActionKind.MessageOn, ShowActionKind.MessageOff, ShowActionKind.MessageToggle, ShowActionKind.MessageScroll,
        ShowActionKind.ClockOn, ShowActionKind.ClockOff, ShowActionKind.ClockToggle, ShowActionKind.ClockFormat, ShowActionKind.ClockSeconds, ShowActionKind.ClockDate,
        ShowActionKind.LogoOn, ShowActionKind.LogoOff, ShowActionKind.LogoToggle,
        ShowActionKind.PipOn, ShowActionKind.PipOff, ShowActionKind.PipToggle,
        ShowActionKind.WeatherOn, ShowActionKind.WeatherOff, ShowActionKind.WeatherToggle, ShowActionKind.WeatherView,
        ShowActionKind.OverlaysOff,
        ShowActionKind.LowerThirdShow, ShowActionKind.LowerThirdHide, ShowActionKind.LowerThirdPreview, ShowActionKind.LowerThirdPreviewOff,
        ShowActionKind.LowerThirdTake, ShowActionKind.LowerThirdUpdate,
        ShowActionKind.WebKey, ShowActionKind.WebClick, ShowActionKind.WebType, ShowActionKind.WebReload, ShowActionKind.WebOpen,
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
        ShowActionKind.Take or ShowActionKind.Cut => "a desk key: it sends the preview to air, and a running order never takes a half-built preview by itself",
        ShowActionKind.CueFire or ShowActionKind.CueGo or ShowActionKind.CueStandby or ShowActionKind.CueHoldOn or ShowActionKind.CueHoldOff
            => "the stack's own transport — a cue firing, holding or re-aiming the stack it runs on is a loop waiting to happen (GO on a list names another list)",
        ShowActionKind.PresenterNext or ShowActionKind.PresenterPrev => "the clicker's own keys — Back / GO on a list name the list",
        ShowActionKind.ApplyLookHotkey => "an F-key's slot — a cue names the look itself",
        ShowActionKind.Identify => "the rig's own identify pass, for the desk at set-up",
        ShowActionKind.ReviewOn or ShowActionKind.ReviewOff or ShowActionKind.ReviewToggle => "the desk's own check of its preview on the multiview",
        ShowActionKind.ScreenToPreview => "the desk loading its preview from a screen to edit — an edit, not a step of the show",
        ShowActionKind.UpdateApply or ShowActionKind.Restart => "an admin verb behind the passcode",
        _ => null,
    };

    /// <summary>
    /// Content actions: a video stinger cannot share a cue with these (the clip owns every screen).
    /// Break music is sound only and is deliberately not here.
    /// </summary>
    public static bool ChangesContent(ShowActionKind kind) => kind is
        ShowActionKind.ApplyLook or ShowActionKind.PlaylistPart or ShowActionKind.PatternKind or
        ShowActionKind.ScreenOn or ShowActionKind.ScreenOff or ShowActionKind.ScreenToggle or ShowActionKind.CanvasOn or ShowActionKind.CanvasOff or
        ShowActionKind.ScreenLook or ShowActionKind.ScreenProgram;

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
    public static bool IsSwitchWord(string? value) => (value ?? "").Trim().ToLowerInvariant() is
        "on" or "1" or "true" or "show" or "yes" or "off" or "0" or "false" or "hide" or "no" or "toggle" or "flip";

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
        var word = (value ?? "").Replace(" ", "").Replace("-", "").Replace("_", "").Trim();
        return word.Length > 0 && Enum.TryParse<PatternKind>(word, true, out var kind) ? kind : null;
    }
}
