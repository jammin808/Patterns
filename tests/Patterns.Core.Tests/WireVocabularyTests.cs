using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 16: the wire has no vocabulary of its own. A line parses straight into a show action —
/// the same kind, target and value the desk's keys and a cue's steps carry — or into one of the
/// five things a wire says that is not an action. There is no map between the wire and the
/// executor; this table is where a verb's spelling meets its action, and the door that a verb
/// added to the wire must come through.
/// </summary>
public class WireVocabularyTests
{
    private static readonly (string Line, ShowAction Action)[] Verbs =
    {
        ("GO", new(ShowActionKind.OutputsOn)),
        ("OUTPUTS OFF", new(ShowActionKind.OutputsOff)),
        ("IDENTIFY", new(ShowActionKind.Identify)),
        ("NEXT", new(ShowActionKind.PresenterNext)),
        ("BACK", new(ShowActionKind.PresenterPrev)),
        ("STOPALL", new(ShowActionKind.StopAll)),
        ("CUE GO 0123abcd", new(ShowActionKind.CueGo, "0123abcd")),
        ("CUE STANDBY NEXT", new(ShowActionKind.CueStandby, "next")),
        ("CUE STANDBY BACK", new(ShowActionKind.CueStandby, "prev")),
        ("CUE STANDBY 03.020", new(ShowActionKind.CueStandby, "03.020")),
        ("CUE HOLD ON", new(ShowActionKind.CueHoldOn)),
        ("CUE HOLD OFF", new(ShowActionKind.CueHoldOff)),
        ("CUE ARM ON", new(ShowActionKind.ListArm, "caller")),
        ("CUE ARM OFF", new(ShowActionKind.ListDisarm, "caller")),
        ("MUSIC PLAY 3", new(ShowActionKind.SpotifyPlay, "3")),
        ("SPOTIFY Interval bed", new(ShowActionKind.SpotifyPlay, "Interval bed")),
        ("MUSIC PAUSE", new(ShowActionKind.SpotifyPause)),
        ("MUSIC SKIP", new(ShowActionKind.SpotifyNext)),
        ("MUSIC VOL 40", new(ShowActionKind.SpotifyVolume, "", "40")),
        ("BLACKOUT ON", new(ShowActionKind.BlackoutOn)),
        ("BLACKOUT OFF", new(ShowActionKind.BlackoutOff)),
        ("BLACKOUT", new(ShowActionKind.BlackoutToggle)),
        ("LOOK 7", new(ShowActionKind.ApplyLookHotkey, "7")),
        ("LOOK Walk-in", new(ShowActionKind.ApplyLook, "Walk-in")),
        ("LOOK #3", new(ShowActionKind.ApplyLook, "#3")),
        ("SCREEN 2 ON", new(ShowActionKind.ScreenOn, "2")),
        ("SCREEN 2 OFF", new(ShowActionKind.ScreenOff, "2")),
        ("SCREEN 2", new(ShowActionKind.ScreenToggle, "2")),
        ("SCREEN 2 LOOK Sponsor", new(ShowActionKind.ScreenLook, "2", "Sponsor")),
        ("SCREEN 2 PGM", new(ShowActionKind.ScreenProgram, "2")),
        ("SCREEN 2 ROLE confidence", new(ShowActionKind.ScreenRole, "2", "confidence")),
        ("SCREEN 2 GROUP info", new(ShowActionKind.ScreenRole, "2", "info")),                  // round 67.7: the desk's word for the role
        ("SCREEN 2 LABEL Stage left", new(ShowActionKind.ScreenLabel, "2", "Stage left")),
        ("SCREEN 2 AUDIO Info HDMI", new(ShowActionKind.ScreenAudio, "2", "Info HDMI")),           // round 69: the output the screen's sound leaves by
        ("SCREEN 2 SOUND OFF", new(ShowActionKind.ScreenAudio, "2", "OFF")),
        ("AUDIO FOLLOW ON", new(ShowActionKind.AudioFollow, "", "on")),                            // round 69: the sound follows the picture
        ("SCREEN 2 LABEL", new(ShowActionKind.ScreenLabel, "2")),
        ("SCREEN 2 SIGNAL 3840x2160 50 RGB 8 SDR", new(ShowActionKind.ScreenSignal, "2", "3840x2160 50 RGB 8 SDR")),
        ("RIG SAVE first show", new(ShowActionKind.RigSaveKnownGood, "", "first show")),
        ("EYE FOCUS screen 2", new(ShowActionKind.EyeFocus, "", "screen 2")),
        ("TAKE NEXT wipe left 800", new(ShowActionKind.NextTransition, "", "wipe left 800")),
        ("TAKE NEXT STING Whoosh", new(ShowActionKind.NextTransition, "", "STING Whoosh")),
        ("TAKE NEXT", new(ShowActionKind.NextTransition, "", "CLEAR")),
        ("EYE NEXT", new(ShowActionKind.EyeNext)),
        ("EYE PREV", new(ShowActionKind.EyePrev)),
        ("EYE LENS control", new(ShowActionKind.EyeLens, "", "control")),
        ("EYE RESET", new(ShowActionKind.EyeReset)),
        ("SCREEN 2 TESTROUTE ON", new(ShowActionKind.ScreenTestRoute, "2", "ON")),
        ("SCREEN 2 TEST ROUTE", new(ShowActionKind.ScreenTestRoute, "2")),
        ("SCREEN 2 RECEIVED 3840x2160 50 RGB 8", new(ShowActionKind.ScreenReceived, "2", "3840x2160 50 RGB 8")),
        ("SCREEN 2 PATTERN LED wall", new(ShowActionKind.ScreenPattern, "2", "LED wall")),
        ("SCREEN 2 PVW LOOK Walk-in", new(ShowActionKind.ScreenStageLook, "2", "Walk-in")),
        ("SCREEN 2 PREVIEW PRESET Grid", new(ShowActionKind.ScreenStagePreset, "2", "Grid")),
        ("SCREEN 2 PVW PATTERN Grid", new(ShowActionKind.ScreenStagePattern, "2", "Grid")),
        ("SCREEN 2 PVW PROGRAM", new(ShowActionKind.ScreenStageProgram, "2")),
        ("SCREEN 2 PVW RESET", new(ShowActionKind.ScreenStageReset, "2")),
        ("SCREEN 2 PVW", new(ShowActionKind.ScreenToPreview, "2")),
        ("PVW PATTERN Grid", new(ShowActionKind.ScreenStagePattern, "", "Grid")),
        ("PVW PRESET Walk-in", new(ShowActionKind.ScreenStagePreset, "", "Walk-in")),
        ("PVW LOOK Walk-in", new(ShowActionKind.ApplyLookToPreview, "Walk-in")),
        ("PVW RESET", new(ShowActionKind.ScreenStageReset, "")),
        ("PVW PROGRAM", new(ShowActionKind.ScreenToPreview, "")),
        ("PREVIEW", new(ShowActionKind.ScreenToPreview, "")),
        ("LOCK 1 ON", new(ShowActionKind.ScreenLock, "1")),
        ("LOCK 1 OFF", new(ShowActionKind.ScreenUnlock, "1")),
        ("LOCK 1", new(ShowActionKind.ScreenLockToggle, "1")),
        ("GROUP a ON", new(ShowActionKind.CanvasOn, "A")),
        ("GROUP a OFF", new(ShowActionKind.CanvasOff, "A")),
        ("AUDIO PLAY", new(ShowActionKind.AudioPlay)),
        ("AUDIO PLAY 2", new(ShowActionKind.AudioPlay, "2")),
        ("AUDIO STOP", new(ShowActionKind.AudioStop)),
        ("AUDIO NEXT", new(ShowActionKind.AudioNext)),
        ("AUDIO PREV", new(ShowActionKind.AudioPrev)),
        ("AUDIO VOL 80", new(ShowActionKind.AudioVolume, "", "80")),
        ("TONE ON", new(ShowActionKind.ToneOn)),
        ("TONE OFF", new(ShowActionKind.ToneOff)),
        ("DUCK ON", new(ShowActionKind.DuckOn)),
        ("DUCK OFF", new(ShowActionKind.DuckOff)),
        ("DUCK", new(ShowActionKind.DuckToggle)),
        ("LT 2", new(ShowActionKind.LowerThirdShow, "2")),
        ("LT Neon WITH Jane", new(ShowActionKind.LowerThirdShow, "Neon", "Jane")),
        ("PERSON Jane", new(ShowActionKind.LowerThirdShow, "", "Jane")),
        ("LT OFF", new(ShowActionKind.LowerThirdHide)),
        ("LT PREVIEW Neon WITH 3", new(ShowActionKind.LowerThirdPreview, "Neon", "3")),
        ("LT PREVIEW OFF", new(ShowActionKind.LowerThirdPreviewOff)),
        ("LT TAKE", new(ShowActionKind.LowerThirdTake)),
        ("LT UPDATE", new(ShowActionKind.LowerThirdUpdate)),
        ("WEB KEY ArrowRight ON slides", new(ShowActionKind.WebKey, "slides", "ArrowRight")),
        ("WEB CLICK 50 50", new(ShowActionKind.WebClick, "", "50 50")),
        ("WEB TYPE hello", new(ShowActionKind.WebType, "", "hello")),
        ("WEB RELOAD slides", new(ShowActionKind.WebReload, "slides")),
        ("WEB OPEN https://x.org ON slides", new(ShowActionKind.WebOpen, "slides", "https://x.org")),
        ("DECK NEXT", new(ShowActionKind.DeckNext)),
        ("DECK PREV", new(ShowActionKind.DeckPrev)),
        ("DECK PAGE 5", new(ShowActionKind.DeckPage, "", "5")),
        ("DECK LAST", new(ShowActionKind.DeckPage, "", "last")),
        ("VIDEO END 5", new(ShowActionKind.VideoToEnd, "", "5")),
        ("VIDEO RESTART", new(ShowActionKind.VideoRestart)),
        ("DEVICE Arduino RELAY 1", new(ShowActionKind.DeviceSend, "Arduino", "RELAY 1")),
        ("ANNOUNCE Doors close", new(ShowActionKind.Announce, "", "Doors close")),
        ("ANNOUNCE OFF", new(ShowActionKind.AnnounceOff)),
        ("ADVERT 2", new(ShowActionKind.AdvertPlay, "2")),
        ("ADVERT OFF", new(ShowActionKind.AdvertOff)),
        ("SCHEDULE ON", new(ShowActionKind.ScheduleOn)),
        ("SCHEDULE OFF", new(ShowActionKind.ScheduleOff)),
        ("UPDATE APPLY 1234", new(ShowActionKind.UpdateApply, "1234")),
        ("RESTART 1234", new(ShowActionKind.Restart, "1234")),
        ("CLOCK ON", new(ShowActionKind.ClockOn)),
        ("CLOCK OFF", new(ShowActionKind.ClockOff)),
        ("CLOCK", new(ShowActionKind.ClockToggle)),
        ("CLOCK 24", new(ShowActionKind.ClockFormat, "", "24")),
        ("CLOCK SECONDS ON", new(ShowActionKind.ClockSeconds, "", "on")),
        ("CLOCK DATE OFF", new(ShowActionKind.ClockDate, "", "off")),
        ("MESSAGE ON Welcome", new(ShowActionKind.MessageOn, "", "Welcome")),
        ("MESSAGE OFF", new(ShowActionKind.MessageOff)),
        ("MESSAGE", new(ShowActionKind.MessageToggle)),
        ("MESSAGE SCROLL ON", new(ShowActionKind.MessageScroll, "", "on")),
        ("COUNTDOWN 5", new(ShowActionKind.CountdownStart, "", "5")),
        ("COUNTDOWN TO 14:00", new(ShowActionKind.CountdownTo, "", "14:00")),
        ("COUNTDOWN STOP", new(ShowActionKind.CountdownStop)),
        ("COUNTDOWN LABEL Back at", new(ShowActionKind.CountdownLabel, "", "Back at")),
        ("LOGO ON", new(ShowActionKind.LogoOn)),
        ("LOGO OFF", new(ShowActionKind.LogoOff)),
        ("LOGO", new(ShowActionKind.LogoToggle)),
        ("PIP ON", new(ShowActionKind.PipOn)),
        ("PIP OFF", new(ShowActionKind.PipOff)),
        ("PIP", new(ShowActionKind.PipToggle)),
        ("OVERLAYS OFF", new(ShowActionKind.OverlaysOff)),
        ("PATTERN LED wall", new(ShowActionKind.PatternKind, "", "LED wall")),
        ("WEATHER ON", new(ShowActionKind.WeatherOn)),
        ("WEATHER OFF", new(ShowActionKind.WeatherOff)),
        ("WEATHER", new(ShowActionKind.WeatherToggle)),
        ("WEATHER TOMORROW", new(ShowActionKind.WeatherView, "", "tomorrow")),
        ("REVIEW ON", new(ShowActionKind.ReviewOn)),
        ("TWIN TAKEOVER", new(ShowActionKind.TwinTakeOver)),
        ("TWIN TAKEOVER FORCE", new(ShowActionKind.TwinTakeOver, "", "force")),
        ("TWIN STANDBY", new(ShowActionKind.TwinStandBy)),
        ("TWIN TAKEBACK", new(ShowActionKind.TwinTakeBack)),
        ("SHOWLOCK ON", new(ShowActionKind.ShowLockOn)),
        ("SHOWLOCK OFF", new(ShowActionKind.ShowLockOff)),
        ("CALIBRATE RUN Phone (NDI Camera)", new(ShowActionKind.CalibrateRun, "", "Phone (NDI Camera)")),
        ("CALIBRATE CANCEL", new(ShowActionKind.CalibrateCancel)),
        ("CALIBRATE DEMO", new(ShowActionKind.CalibrateDemo)),
        ("CALIBRATE APPLY", new(ShowActionKind.CalibrateApply)),
        ("CAL UNDO", new(ShowActionKind.CalibrateUndo)),
        ("TIMER PAUSE", new(ShowActionKind.TimerPause)),
        ("TIMER RESUME", new(ShowActionKind.TimerResume)),
        ("TIMER ADD 60", new(ShowActionKind.TimerAdd, "", "+60")),
        ("TIMER MINUS 30", new(ShowActionKind.TimerAdd, "", "-30")),
        ("TIMER +1m", new(ShowActionKind.TimerAdd, "", "+60")),
        ("TIMER -30", new(ShowActionKind.TimerAdd, "", "-30")),
        ("TIMER FLASH", new(ShowActionKind.TimerFlash)),
        ("STAGE MESSAGE Wrap up", new(ShowActionKind.StageMessage, "speaker", "Wrap up")),
        ("STAGE CREW Mic 2 is live", new(ShowActionKind.StageMessage, "crew", "Mic 2 is live")),
        ("STAGE Five minutes", new(ShowActionKind.StageMessage, "speaker", "Five minutes")),
        ("STAGE CLEAR", new(ShowActionKind.StageClear)),
        ("STAGE ACK m-42", new(ShowActionKind.StageAck, "", "m-42")),
        ("ARCADE START pong 2", new(ShowActionKind.ArcadeStart, "", "pong 2")),
        ("ARCADE snake", new(ShowActionKind.ArcadeStart, "", "snake")),
        ("ARCADE STOP", new(ShowActionKind.ArcadeStop)),
        ("ARCADE PAUSE", new(ShowActionKind.ArcadePause)),
        ("ARCADE RESUME", new(ShowActionKind.ArcadeResume)),
        ("ARCADE ATTRACT breakout", new(ShowActionKind.ArcadeAttract, "", "breakout")),
        ("ARCADE KEY 1 UP TAP", new(ShowActionKind.ArcadeKey, "", "1 UP TAP")),
        ("ARCADE SIZE 3840x1080", new(ShowActionKind.ArcadeSize, "", "3840x1080")),
        ("ARCADE NDI ON", new(ShowActionKind.ArcadeNdi, "", "ON")),
        ("ARCADE NAME ABC", new(ShowActionKind.ArcadeName, "", "ABC")),
        ("ARCADE WINDOW", new(ShowActionKind.ArcadeWindow, "", "on")),
        ("ARCADE WINDOW FULL 2", new(ShowActionKind.ArcadeWindow, "", "FULL 2")),
        ("ARCADE FULLSCREEN", new(ShowActionKind.ArcadeWindow, "", "full")),
        ("ARCADE WINDOW OFF", new(ShowActionKind.ArcadeWindow, "", "OFF")),
        ("PLAY ADD quiz Which hall? | A | B | correct=2 time=15", new(ShowActionKind.PlayAdd, "", "quiz Which hall? | A | B | correct=2 time=15")),
        ("PLAY OPEN", new(ShowActionKind.PlayOpen, "", "")),
        ("PLAY NEXT", new(ShowActionKind.PlayOpen, "", "next")),
        ("PLAY CLOSE", new(ShowActionKind.PlayClose)),
        ("PLAY REVEAL", new(ShowActionKind.PlayReveal)),
        ("PLAY SHOW leaderboard", new(ShowActionKind.PlayShow, "", "leaderboard")),
        ("PLAY HIDE", new(ShowActionKind.PlayShow, "", "off")),
        ("PLAY MESSAGE group:Table 4 you won", new(ShowActionKind.PlayMessage, "", "group:Table 4 you won")),
        ("PLAY APPROVE", new(ShowActionKind.PlayApprove, "", "all")),
        ("PLAY REJECT abc123", new(ShowActionKind.PlayReject, "", "abc123")),
        ("PLAY AUTO OFF", new(ShowActionKind.PlayAuto, "", "OFF")),
        ("PLAY PATH CLOSE", new(ShowActionKind.PlayPath, "", "CLOSE")),
        ("PLAY DRAUGHTS", new(ShowActionKind.PlayDraughts, "", "reset")),
        ("PLAY NEW", new(ShowActionKind.PlayRoom, "", "new")),
        ("PLAY EXPORT", new(ShowActionKind.PlayExport)),
        ("RIGDAY ON", new(ShowActionKind.RigDayOn)),
        ("RIGDAY OFF", new(ShowActionKind.RigDayOff)),
        ("ALIGN START p1", new(ShowActionKind.AlignStart, "", "p1")),
        ("ALIGN STOP", new(ShowActionKind.AlignStop)),
        ("ALIGN NEXT", new(ShowActionKind.AlignNext)),
        ("ALIGN PREV", new(ShowActionKind.AlignPrev)),
        ("ALIGN NUDGE 2 -1", new(ShowActionKind.AlignNudge, "", "2 -1")),
        ("ALIGN SNAP", new(ShowActionKind.AlignSnap)),
        ("AUDIENCE ON 9701", new(ShowActionKind.AudienceOn, "", "9701")),
        ("AUDIENCE ON", new(ShowActionKind.AudienceOn, "", "")),
        ("AUDIENCE OFF", new(ShowActionKind.AudienceOff)),
        ("REVIEW OFF", new(ShowActionKind.ReviewOff)),
        ("REVIEW", new(ShowActionKind.ReviewToggle)),
        ("FREEZE ON", new(ShowActionKind.FreezeOn)),
        ("FREEZE OFF", new(ShowActionKind.FreezeOff)),
        ("FREEZE", new(ShowActionKind.FreezeToggle)),
        ("FADE 2 SCREEN 2", new(ShowActionKind.FadeToBlack, "SCREEN 2", "2")),
        ("FADE UP 1.5", new(ShowActionKind.FadeUp, "", "1.5")),
        ("LOOKBACK cut", new(ShowActionKind.LookBack, "", "cut")),
        ("STREAM ON", new(ShowActionKind.StreamStart)),
        ("STREAM OFF", new(ShowActionKind.StreamStop)),
        ("SECTION Main", new(ShowActionKind.PlaylistPart, "Main")),
        ("STINGER 2", new(ShowActionKind.StingerFire, "2")),
        ("VOG 2", new(ShowActionKind.StingerFire, "2", "vog")),
        ("STING Whoosh", new(ShowActionKind.StingerFire, "Whoosh", "sting")),
        ("STINGER STOP", new(ShowActionKind.StingerStop)),
    };

    [Fact]
    public void EveryVerbOfTheWireIsAShowActionAndTheWireHasNoVocabularyOfItsOwn()
    {
        // Six things a wire says are not actions; everything else the parser produces is one.
        Assert.Equal(
            new[] { RemoteCommandKind.Unknown, RemoteCommandKind.Action, RemoteCommandKind.Ping, RemoteCommandKind.Status, RemoteCommandKind.Hello, RemoteCommandKind.Auth, RemoteCommandKind.CueList, RemoteCommandKind.TwinStatus, RemoteCommandKind.ShowLockStatus, RemoteCommandKind.CalibrationStatus, RemoteCommandKind.NodesStatus, RemoteCommandKind.StageStatus, RemoteCommandKind.ArcadeStatus, RemoteCommandKind.PlayStatus, RemoteCommandKind.AssistantAsk, RemoteCommandKind.RigDayStatus, RemoteCommandKind.Menu, RemoteCommandKind.ScreenSignal, RemoteCommandKind.ScreenEdid, RemoteCommandKind.RigStatus, RemoteCommandKind.CommissionStatus, RemoteCommandKind.EyeStatus },
            Enum.GetValues<RemoteCommandKind>());

        foreach (var (line, action) in Verbs)
        {
            var cmd = ControlProtocol.Parse(line);
            Assert.True(cmd.IsAction, $"{line} is an action of the show");
            Assert.Equal(action, cmd.Action);
            Assert.NotEqual(ShowActionKind.Unknown, cmd.Action.Kind);
        }
        // Every kind the wire can reach is a kind of the one vocabulary the table classifies (a cue's or the desk's alone) — never unclassified.
        foreach (var kind in Verbs.Select(v => v.Action.Kind).Distinct())
        {
            Assert.True(ActionSpec.CueKinds.Contains(kind) || ActionSpec.DeskOnly(kind) is not null, $"{kind} is classified");
        }

        // The wire's own words.
        Assert.Equal(RemoteCommandKind.Ping, ControlProtocol.Parse("PING").Kind);
        Assert.Equal(RemoteCommandKind.Status, ControlProtocol.Parse("STATUS").Kind);
        Assert.Equal(RemoteCommandKind.CueList, ControlProtocol.Parse("CUE LIST").Kind);
        Assert.Equal(RemoteCommandKind.TwinStatus, ControlProtocol.Parse("TWIN STATUS").Kind);
        Assert.Equal(RemoteCommandKind.CalibrationStatus, ControlProtocol.Parse("CALIBRATE STATUS").Kind);
        Assert.Equal(RemoteCommandKind.CalibrationStatus, ControlProtocol.Parse("CALIBRATE").Kind);
        Assert.Equal(RemoteCommandKind.NodesStatus, ControlProtocol.Parse("NODES").Kind);
        Assert.Equal(RemoteCommandKind.StageStatus, ControlProtocol.Parse("STAGE STATUS").Kind);
        Assert.Equal(RemoteCommandKind.StageStatus, ControlProtocol.Parse("STAGE").Kind);
        Assert.Equal(RemoteCommandKind.ArcadeStatus, ControlProtocol.Parse("ARCADE STATUS").Kind);
        Assert.Equal(RemoteCommandKind.ArcadeStatus, ControlProtocol.Parse("ARCADE").Kind);
        Assert.Equal("games", ControlProtocol.Parse("ARCADE GAMES").Text);
        Assert.Equal("scores pong", ControlProtocol.Parse("ARCADE SCORES pong").Text);
        Assert.False(ControlProtocol.Parse("ARCADE START").IsAction);
        Assert.False(ControlProtocol.Parse("ARCADE tetris").IsAction);
        Assert.Equal(RemoteCommandKind.PlayStatus, ControlProtocol.Parse("PLAY").Kind);
        Assert.Equal(RemoteCommandKind.Menu, ControlProtocol.Parse("MENU SCREEN 2").Kind);
        Assert.Equal("SCREEN 2", ControlProtocol.Parse("MENU SCREEN 2").Text);
        Assert.Equal("", ControlProtocol.Parse("MENU").Text);
        Assert.False(ControlProtocol.Parse("MENU CUE 03.020").IsAction);
        Assert.Equal("results abc", ControlProtocol.Parse("PLAY RESULTS abc").Text);
        Assert.Equal("queue", ControlProtocol.Parse("PLAY QUEUE").Text);
        Assert.Equal(RemoteCommandKind.AssistantAsk, ControlProtocol.Parse("ASSISTANT ASK ten questions about today").Kind);
        Assert.Equal("moderate hello there", ControlProtocol.Parse("ASSISTANT MODERATE hello there").Text);
        Assert.False(ControlProtocol.Parse("ASSISTANT").IsAction);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("ASSISTANT ASK").Kind);
        Assert.Equal(RemoteCommandKind.RigDayStatus, ControlProtocol.Parse("RIGDAY").Kind);
        Assert.Equal("audience", ControlProtocol.Parse("AUDIENCE STATUS").Text);
        Assert.Equal(RemoteCommandKind.PlayStatus, ControlProtocol.Parse("AUDIENCE").Kind);
        Assert.Equal("align", ControlProtocol.Parse("ALIGN STATUS").Text);
        Assert.False(ControlProtocol.Parse("ALIGN NUDGE").IsAction);
        Assert.Equal(RemoteCommandKind.EyeStatus, ControlProtocol.Parse("EYE").Kind);
        Assert.Equal(RemoteCommandKind.EyeStatus, ControlProtocol.Parse("EYE STATUS").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("EYE FOCUS").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("EYE LENS").Kind);
        var hello = ControlProtocol.Parse("HELLO FOH deck");
        Assert.Equal(RemoteCommandKind.Hello, hello.Kind);
        Assert.Equal("FOH deck", hello.Text);
        Assert.Equal(ShowAction.None, hello.Action);
        var junk = ControlProtocol.Parse("FROBNICATE 12");
        Assert.Equal(RemoteCommandKind.Unknown, junk.Kind);
        Assert.Equal("FROBNICATE 12", junk.Text);
        Assert.False(junk.IsAction);

        // The desk's keys that never left the desk have no verb: a wire cannot TAKE or CUT a half-built preview.
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("TAKE").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("TAKE ALL").Kind);        // round 67: only TAKE NEXT is a wire verb
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("CUT").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 GROUP").Kind);  // a group needs its word
    }

    /// <summary>
    /// Round 72: a latch toggles on its bare verb or TOGGLE alone. Any other word after BLACKOUT, SCREEN n,
    /// LOCK n, DUCK, REVIEW or FREEZE is refused as unknown — a misspelt ON or OFF from a deck or a script
    /// used to fall through to the toggle and flip the latch the other way.
    /// </summary>
    [Fact]
    public void ALatchTogglesOnItsBareVerbOrToggleAloneAndRefusesAnyOtherWord()
    {
        var toggles = new (string Line, ShowActionKind Kind, string Target)[]
        {
            ("BLACKOUT", ShowActionKind.BlackoutToggle, ""), ("BLACKOUT TOGGLE", ShowActionKind.BlackoutToggle, ""), ("blackout toggle", ShowActionKind.BlackoutToggle, ""),
            ("SCREEN 1", ShowActionKind.ScreenToggle, "1"), ("SCREEN 1 TOGGLE", ShowActionKind.ScreenToggle, "1"),
            ("LOCK 1", ShowActionKind.ScreenLockToggle, "1"), ("LOCK 1 TOGGLE", ShowActionKind.ScreenLockToggle, "1"),
            ("DUCK", ShowActionKind.DuckToggle, ""), ("DUCK TOGGLE", ShowActionKind.DuckToggle, ""),
            ("REVIEW", ShowActionKind.ReviewToggle, ""), ("REVIEW TOGGLE", ShowActionKind.ReviewToggle, ""),
            ("FREEZE", ShowActionKind.FreezeToggle, ""), ("FREEZE TOGGLE", ShowActionKind.FreezeToggle, ""),
        };
        foreach (var (line, kind, target) in toggles)
        {
            var cmd = ControlProtocol.Parse(line);
            Assert.True(cmd.IsAction, line);
            Assert.Equal(kind, cmd.Action.Kind);
            Assert.Equal(target, cmd.Action.Target);
        }

        var refused = new[]
        {
            "BLACKOUT ONN", "BLACKOUT TOGLE", "BLACKOUT MAYBE", "BLACKOUT 1",
            "SCREEN 1 ONN", "SCREEN 1 TOGLE", "SCREEN 1 FLIP", "SCREEN 1 OF",
            "LOCK 1 ONN", "LOCK 1 TOGLE", "LOCK 1 PLEASE",
            "DUCK LOUD", "DUCK TOGLE", "DUCK 50",
            "REVIEW NOW", "REVIEW TOGLE", "REVIEW PGM",
            "FREEZE ALL", "FREEZE TOGLE", "FREEZE OFFF",
        };
        foreach (var line in refused)
        {
            var cmd = ControlProtocol.Parse(line);
            Assert.False(cmd.IsAction, $"{line} must not be an action");
            Assert.Equal(RemoteCommandKind.Unknown, cmd.Kind);
        }

        // ON and OFF stay explicit, whatever the case.
        Assert.Equal(ShowActionKind.BlackoutOn, ControlProtocol.Parse("blackout on").Action.Kind);
        Assert.Equal(ShowActionKind.ScreenUnlock, ControlProtocol.Parse("LOCK 2 off").Action.Kind);
        Assert.Equal(ShowActionKind.FreezeOff, ControlProtocol.Parse("FREEZE OFF").Action.Kind);
    }
}
