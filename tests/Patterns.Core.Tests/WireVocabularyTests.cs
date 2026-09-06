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
        // Five things a wire says are not actions; everything else the parser produces is one.
        Assert.Equal(
            new[] { RemoteCommandKind.Unknown, RemoteCommandKind.Action, RemoteCommandKind.Ping, RemoteCommandKind.Status, RemoteCommandKind.Hello, RemoteCommandKind.CueList },
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
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("CUT").Kind);
    }
}
