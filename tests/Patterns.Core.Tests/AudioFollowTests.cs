using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 69: the sound follows the picture. Each screen names the output its sound leaves by; the
/// matrix derives the route from what the screen shows now — the programme, its own picture, the
/// screen it repeats, the canvas it belongs to — and the operator's rows stand beside them and win.
/// </summary>
public class AudioFollowTests
{
    /// <summary>A rig: a main wall, a repeater of it, an info screen with its own picture, a repeater of the info screen, and a room desk.</summary>
    private static ShowState Show()
    {
        var state = new ShowState();
        state.AudioPlayer.Devices.Add("Scarlett 2i2");
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "MAIN", CustomLabel = "Main wall", AudioOutput = "dev:Main HDMI" });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "REP", CustomLabel = "Stage left", MirrorOf = "MAIN", Role = ScreenRole.Repeater, AudioOutput = "dev:Stage HDMI" });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "INFO", CustomLabel = "Info", UseCustomPattern = true, Role = ScreenRole.Info, AudioOutput = "dev:Info HDMI" });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "INFO2", CustomLabel = "Foyer", MirrorOf = "INFO", Role = ScreenRole.Repeater, AudioOutput = "dev:Foyer HDMI" });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "SPARE", CustomLabel = "Spare" });          // names no output: nothing follows
        state.Stingers.DuckPct = 25;   // −12 dB
        return state;
    }

    [Fact]
    public void TheSourceOfAScreenIsWhatItsPictureIs()
    {
        var state = Show();
        Assert.Equal("programme", AudioRouting.SourceOfScreen(state, "MAIN"));
        Assert.Equal("programme", AudioRouting.SourceOfScreen(state, "REP"));          // repeats the main: the main's sound
        Assert.Equal("screen:INFO", AudioRouting.SourceOfScreen(state, "INFO"));       // its own picture
        Assert.Equal("screen:INFO", AudioRouting.SourceOfScreen(state, "INFO2"));      // repeats the info screen: its picture's sound
        Assert.Null(AudioRouting.SourceOfScreen(state, "GHOST"));
        Assert.Null(AudioRouting.SourceOfScreen(state, ""));

        // A take moves the picture and the source moves with it: the info screen back on the programme, the main given its own.
        state.Output.Placements.First(p => p.ScreenId == "INFO").UseCustomPattern = false;
        Assert.Equal("programme", AudioRouting.SourceOfScreen(state, "INFO"));
        Assert.Equal("programme", AudioRouting.SourceOfScreen(state, "INFO2"));
        state.Output.Placements.First(p => p.ScreenId == "MAIN").UseCustomPattern = true;
        Assert.Equal("screen:MAIN", AudioRouting.SourceOfScreen(state, "MAIN"));
        Assert.Equal("screen:MAIN", AudioRouting.SourceOfScreen(state, "REP"));

        // A member of a joined canvas with a picture of its own shows the canvas's picture — and a repeater of the canvas the same.
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "A" });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "B" });
        state.Output.CanvasNames.Add(new CanvasNameConfig { MemberKey = "A+B", Name = "Wall", UseCustomPattern = true });
        Assert.Equal("screen:A+B", AudioRouting.SourceOfScreen(state, "A"));
        state.Output.Placements.First(p => p.ScreenId == "SPARE").MirrorOf = "A+B";
        Assert.Equal("screen:A+B", AudioRouting.SourceOfScreen(state, "SPARE"));
        state.Output.CanvasNames[0].UseCustomPattern = false;
        Assert.Equal("programme", AudioRouting.SourceOfScreen(state, "A"));

        Assert.Equal("the programme", AudioRouting.SourceOfScreenWords(state, "A"));
        Assert.Equal("its own picture", AudioRouting.SourceOfScreenWords(state, "MAIN"));
        Assert.Equal("Main wall's picture (repeated)", AudioRouting.SourceOfScreenWords(state, "REP"));
        Assert.Equal("no such screen", AudioRouting.SourceOfScreenWords(state, "GHOST"));
    }

    [Fact]
    public void TheRoutesThePictureMakesAreOneAnEnabledScreenWithAnOutputAndNoneWhenFollowIsOff()
    {
        var state = Show();
        var followed = AudioRouting.FollowedRoutes(state);
        Assert.Equal(4, followed.Count);
        Assert.Contains(followed, f => f.ScreenId == "MAIN" && f.Source == "programme" && f.Destination == "dev:Main HDMI");
        Assert.Contains(followed, f => f.ScreenId == "REP" && f.Source == "programme" && f.Destination == "dev:Stage HDMI");
        Assert.Contains(followed, f => f.ScreenId == "INFO" && f.Source == "screen:INFO" && f.Destination == "dev:Info HDMI");
        Assert.Contains(followed, f => f.ScreenId == "INFO2" && f.Source == "screen:INFO" && f.Destination == "dev:Foyer HDMI");
        Assert.DoesNotContain(followed, f => f.ScreenId == "SPARE");

        // A screen switched off carries nothing; follow off, the rows alone.
        state.Output.Placements.First(p => p.ScreenId == "REP").Enabled = false;
        Assert.Equal(3, AudioRouting.FollowedRoutes(state).Count);
        var before = AudioRouting.FollowSignature(state);
        state.Output.Placements.First(p => p.ScreenId == "INFO").UseCustomPattern = false;      // a take: the signature moves, the graph rebuilds
        Assert.NotEqual(before, AudioRouting.FollowSignature(state));
        state.AudioRouting.FollowPicture = false;
        Assert.Empty(AudioRouting.FollowedRoutes(state));
        Assert.Null(AudioRouting.FollowedRoute(state, "programme", "dev:Main HDMI"));
    }

    [Fact]
    public void TheOperatorsRowWinsWhereBothNameTheCrosspoint()
    {
        var state = Show();
        AudioRouting.SetRoute(state, "programme", "dev:Main HDMI", -6);                  // the operator's level on the main's output
        AudioRouting.SetRoute(state, "music", "dev:Info HDMI");                           // music on the info screen's output beside its own picture
        var off = AudioRouting.SetRoute(state, "screen:INFO", "dev:Foyer HDMI");
        off.Enabled = false;                                                              // a row switched off: the operator's word, the picture does not overrule it

        var effective = AudioRouting.EffectiveRoutes(state);
        var main = effective.Single(r => r.Source == "programme" && r.Destination == "dev:Main HDMI");
        Assert.False(main.Followed);
        Assert.Equal(-6, main.LevelDb);
        Assert.Single(effective, r => r.Destination == "dev:Foyer HDMI");                 // the disabled row stands alone; no followed twin
        Assert.False(effective.Single(r => r.Destination == "dev:Foyer HDMI").Enabled);
        var info = effective.Where(r => r.Destination == "dev:Info HDMI").ToList();
        Assert.Equal(2, info.Count);                                                       // the row's music and the picture's own sound
        Assert.Contains(info, r => r.Source == "music" && !r.Followed);
        Assert.Contains(info, r => r.Source == "screen:INFO" && r.Followed && r.ScreenId == "INFO" && r.LevelDb == 0);
        Assert.NotNull(AudioRouting.FollowedRoute(state, "screen:INFO", "dev:Info HDMI"));
    }

    [Fact]
    public void ThePlanCarriesTheFollowedLanesWithTheDefaultsForADestinationWithoutARowAndMovesWithATake()
    {
        var state = Show();
        state.AudioRouting.Enabled = true;
        AudioRouting.SetRoute(state, "programme", "dev:Scarlett 2i2");
        AudioRouting.SetRoute(state, "music", "dev:Scarlett 2i2", -6);
        AudioRouting.SetRoute(state, "vog", "dev:Scarlett 2i2");
        AudioRouting.Row(state, "dev:Scarlett 2i2")!.TrimDb = -3;

        var plan = AudioRouting.Resolve(state, vogPlaying: false);
        Assert.Equal(new[] { "dev:Scarlett 2i2", "dev:Main HDMI", "dev:Stage HDMI", "dev:Info HDMI", "dev:Foyer HDMI" }, plan.Select(p => p.Key));
        var mainHdmi = plan.Single(p => p.Key == "dev:Main HDMI");
        Assert.Equal(1, mainHdmi.GainFor("programme"), 6);
        Assert.True(mainHdmi.Lanes.Single().Followed);
        Assert.Equal(0, mainHdmi.DelayMs);
        Assert.Equal(AudioVogMode.Duck, mainHdmi.VogMode);
        var infoHdmi = plan.Single(p => p.Key == "dev:Info HDMI");
        Assert.True(infoHdmi.Carries("screen:INFO"));
        Assert.False(infoHdmi.Carries("programme"));
        Assert.DoesNotContain(plan.Single(p => p.Key == "dev:Scarlett 2i2").Lanes, l => l.Followed);   // the room desk is the operator's rows alone

        // A followed-only destination ducks to the show's level under a VOG, like any Duck row.
        var loud = AudioRouting.Resolve(state, vogPlaying: true);
        Assert.Equal(Db.ToGain(Db.FromPercent(25)), loud.Single(p => p.Key == "dev:Info HDMI").GainFor("screen:INFO"), 6);

        // The take: the info screens go to the programme too — their outputs carry the programme's sound, no row touched.
        state.Output.Placements.First(p => p.ScreenId == "INFO").UseCustomPattern = false;
        var after = AudioRouting.Resolve(state, vogPlaying: false);
        Assert.True(after.Single(p => p.Key == "dev:Info HDMI").Carries("programme"));
        Assert.True(after.Single(p => p.Key == "dev:Foyer HDMI").Carries("programme"));
        Assert.False(after.Single(p => p.Key == "dev:Info HDMI").Carries("screen:INFO"));
        Assert.DoesNotContain(state.AudioRouting.Routes, r => r.Destination.Contains("HDMI"));

        // A row made for a followed destination gives it its trim, delay and VOG mode.
        var row = AudioRouting.EnsureRow(state, "dev:Main HDMI");
        row.DelayMs = 80;
        row.TrimDb = -2;
        row.VogMode = AudioVogMode.Leave;
        var trimmed = AudioRouting.PlanFor(state, "dev:Main HDMI", false)!;
        Assert.Equal(80, trimmed.DelayMs);
        Assert.Equal(Db.ToGain(-2), trimmed.GainFor("programme"), 6);
        Assert.Equal(AudioVogMode.Leave, trimmed.VogMode);

        // Off, there is no plan and nothing follows.
        state.AudioRouting.Enabled = false;
        Assert.Empty(AudioRouting.Resolve(state, false));
    }

    [Fact]
    public void AClipOnTheMainAndItsRepeaterTakesTheMixerAndTheInfoScreensPlaylistItsOwnOutput()
    {
        var state = Show();
        state.AudioRouting.Enabled = true;
        AudioRouting.SetRoute(state, "programme", "dev:Scarlett 2i2");

        // The video on the programme: the room desk (a row), the main's HDMI and the repeater's HDMI (followed) — three devices, the mixer.
        var video = AudioRouting.ClipRoute(state, new[] { MediaBus.Program });
        Assert.Equal(ClipAudioPath.Mixer, video.Path);
        Assert.Equal(new[] { "dev:Scarlett 2i2", "dev:Main HDMI", "dev:Stage HDMI" }, video.Lanes.Select(l => l.Source));

        // The playlist with a soundtrack of its own on the info screens: its own output alone — the decoder plays straight to it.
        var playlist = AudioRouting.ClipRoute(state, new[] { MediaBus.Output("INFO") });
        Assert.Equal(ClipAudioPath.Mixer, playlist.Path);                                   // the info screen and its repeater: two outputs
        Assert.Equal(new[] { "dev:Info HDMI", "dev:Foyer HDMI" }, playlist.Lanes.Select(l => l.Source));
        state.Output.Placements.First(p => p.ScreenId == "INFO2").AudioOutput = "";
        var alone = AudioRouting.ClipRoute(state, new[] { MediaBus.Output("INFO") });
        Assert.Equal(ClipAudioPath.Device, alone.Path);
        Assert.Equal("Info HDMI", alone.Device);
        Assert.Equal(1.0, alone.Gain, 6);

        // The players' lists read the followed destinations too, and the room desk hears nothing of the info screen's own picture.
        Assert.Equal(new[] { "Scarlett 2i2", "Main HDMI", "Stage HDMI" }, AudioRouting.DeviceNamesFor(state, "programme"));
        Assert.Equal(new[] { "Info HDMI" }, AudioRouting.DeviceNamesFor(state, "screen:INFO"));
    }

    [Fact]
    public void TheWordsSayWhatFollowsWhereAndWhy()
    {
        var state = Show();
        state.AudioRouting.Enabled = true;
        Assert.Equal("Sound follows the picture on 4 screens: Main wall → Main HDMI (the programme), Stage left → Stage HDMI (the programme), Info → Info HDMI (its own picture), Foyer → Foyer HDMI (Info's picture (repeated)).", AudioRouting.FollowWords(state));
        Assert.Contains("The picture makes 4 more.", AudioRouting.Words(state));
        Assert.DoesNotContain("nothing routed yet", AudioRouting.Words(state));            // the picture routes, with no row at all
        Assert.Contains("Silent: Preview, Music (playlist), VOG, Stingers, Tone.", AudioRouting.Words(state));

        var row = AudioRouting.EnsureRow(state, "dev:Info HDMI");
        Assert.Contains("Info 0 dB (follows the picture)", AudioRouting.DestinationWords(state, row));
        Assert.Contains("Info HDMI", AudioRouting.DestinationWords(state, row));

        state.AudioRouting.FollowPicture = false;
        Assert.Equal("Sound follows the picture: off — the matrix is the rows alone.", AudioRouting.FollowWords(state));
        state.AudioRouting.FollowPicture = true;
        foreach (var p in state.Output.Placements) p.AudioOutput = "";
        Assert.StartsWith("Sound follows the picture — no screen names a sound output yet", AudioRouting.FollowWords(state));
    }

    [Fact]
    public void TheWireOscTheSummaryAndTheChecksSpellTheVerbs()
    {
        var sound = ControlProtocol.Parse("SCREEN 2 AUDIO Info HDMI");
        Assert.Equal(new ShowAction(ShowActionKind.ScreenAudio, "2", "Info HDMI"), sound.Action);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenAudio, "2", "OFF"), ControlProtocol.Parse("SCREEN 2 SOUND OFF").Action);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 AUDIO").Kind);        // an output has a name
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 SOUND").Kind);
        Assert.Equal(new ShowAction(ShowActionKind.AudioFollow, "", "on"), ControlProtocol.Parse("AUDIO FOLLOW ON").Action);
        Assert.Equal(new ShowAction(ShowActionKind.AudioFollow, "", "toggle"), ControlProtocol.Parse("AUDIO FOLLOWS TOGGLE").Action);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUDIO FOLLOW LOUD").Kind);

        Assert.Equal("AUDIO FOLLOW OFF", OscMap.ToLine(OscMessage.Of("/patterns/audio/follow", "off")));
        Assert.Equal("AUDIO FOLLOW ON", OscMap.ToLine(OscMessage.Of("/patterns/audio/follow/on")));
        Assert.Equal("SCREEN 2 AUDIO Info HDMI", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/audio", "Info HDMI")));
        Assert.Equal("SCREEN 2 AUDIO off", OscMap.ToLine(OscMessage.Of("/patterns/screen/2/sound/off")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/screen/2/audio")));

        // The vocabulary: a cue may switch the follow; the wiring is the desk's alone, with a reason.
        Assert.Contains(ShowActionKind.AudioFollow, ActionSpec.CueKinds);
        Assert.Null(ActionSpec.DeskOnly(ShowActionKind.AudioFollow));
        Assert.DoesNotContain(ShowActionKind.ScreenAudio, ActionSpec.CueKinds);
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.ScreenAudio));
        Assert.Equal((TargetKind.None, ValueKind.Switch), ActionSpec.For(ShowActionKind.AudioFollow));
        Assert.Equal((TargetKind.Screen, ValueKind.Text), ActionSpec.For(ShowActionKind.ScreenAudio));

        var state = Show();
        Assert.Equal("Sound follows the picture on", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.AudioFollow, Value = "on" }));
        Assert.Equal("Screen 'Info' sound out → Info HDMI", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.ScreenAudio, Target = "INFO", Value = "Info HDMI" }));
        Assert.Equal("Screen 'Info' sound out: none", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.ScreenAudio, Target = "INFO", Value = "OFF" }));
        Assert.Equal(ShowActionKind.AudioFollow, CueSheet.ParseKind("AudioFollow"));

        var cue = new RunCueConfig { Name = "Sound" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.AudioFollow, Value = "loud" });
        Assert.Equal(1, CueValidator.ValidateOne(state, cue, null).BrokenCount);
        cue.Actions[0].Value = "off";
        Assert.Equal(0, CueValidator.ValidateOne(state, cue, null).BrokenCount);

        // The help knows the words.
        Assert.Contains(HelpTopics.All, t => t.Id == "audio-routing" && t.Wire.Contains("AUDIO FOLLOW") && t.Wire.Contains("SCREEN n AUDIO") && t.Body.Contains("follows the picture"));
    }
}
