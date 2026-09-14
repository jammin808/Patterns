using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The routing matrix, pure: dB, the sources and destinations, the seed, the plan with each VOG mode, the players' lists, a clip's path, the envelope, the words, the verbs.</summary>
public class AudioRoutingTests
{
    private static ShowState Show()
    {
        var state = new ShowState();
        state.AudioPlayer.Devices.Add("Scarlett 2i2");
        state.AudioPlayer.SetDelay("Scarlett 2i2", 40);
        state.Ndi.Senders.Add(new NdiSenderConfig { Id = "ndi1", Name = "Stream" });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "INFO", CustomLabel = "Info screen", UseCustomPattern = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "MAIN", CustomLabel = "Main", UseCustomPattern = false });
        state.Stingers.DuckPct = 25;   // −12 dB
        return state;
    }

    [Fact]
    public void DecibelsReadLikeADesk()
    {
        Assert.Equal(1.0, Db.ToGain(0), 6);
        Assert.Equal(0.5, Db.ToGain(-6.0206), 3);
        Assert.Equal(0, Db.ToGain(-60));
        Assert.Equal(0, Db.ToGain(-80));
        Assert.Equal(Db.ToGain(12), Db.ToGain(30), 6);      // the ceiling
        Assert.Equal(-6, Db.FromGain(0.5012), 1);
        Assert.Equal(Db.Floor, Db.FromGain(0));
        Assert.Equal(-12, Db.FromPercent(25), 0);
        Assert.Equal("0 dB", Db.Text(0));
        Assert.Equal("−6 dB", Db.Text(-6));
        Assert.Equal("+3 dB", Db.Text(3));
        Assert.Equal("off", Db.Text(-60));
        Assert.Equal(0, Db.ClampLevel(double.NaN));
    }

    [Fact]
    public void TheSourcesAreTheShowsAndTheDestinationsAreTheMachinesAndTheShows()
    {
        var state = Show();
        var sources = AudioRouting.Sources(state);
        Assert.Equal(new[] { "programme", "screen:INFO", "preview", "music", "vog", "sting", "tone" }, sources.Select(s => s.Id));
        Assert.Equal("Info screen", sources[1].Label);
        Assert.Equal(RoutedSourceKind.Screen, sources[1].Kind);

        var destinations = AudioRouting.Destinations(state, new[] { "Scarlett 2i2", "NVIDIA HDMI 3" });
        Assert.Contains(destinations, d => d.Key == "dev:(computer output)" && !d.Configured);
        Assert.Contains(destinations, d => d.Key == "dev:NVIDIA HDMI 3" && d.Kind == AudioDestinationKind.Device && d.Present);
        Assert.Contains(destinations, d => d.Key == "ndi:ndi1" && d.Kind == AudioDestinationKind.Ndi && d.Label == "NDI Stream");

        // A configured row comes first with its label, and one the machine has not got is kept and marked absent.
        state.AudioRouting.Destinations.Add(new AudioDestinationConfig { Key = "dev:Dante Virtual Soundcard", Label = "Room desk" });
        var again = AudioRouting.Destinations(state, new[] { "Scarlett 2i2" });
        Assert.Equal("dev:Dante Virtual Soundcard", again[0].Key);
        Assert.Equal("Room desk", again[0].Label);
        Assert.True(again[0].Configured);
        Assert.False(again[0].Present);
    }

    [Fact]
    public void SwitchedOnEmptyTheMatrixSeedsAudioFollowsVideo()
    {
        var state = Show();
        var made = AudioRouting.SeedDefaults(state);
        Assert.Equal(8, made);   // five on the Scarlett, three on the NDI send
        var scarlett = AudioRouting.Row(state, "dev:Scarlett 2i2");
        Assert.NotNull(scarlett);
        Assert.Equal(40, scarlett!.DelayMs);   // the classic delay table carried over
        Assert.NotNull(AudioRouting.Route(state, "programme", "dev:Scarlett 2i2"));
        Assert.NotNull(AudioRouting.Route(state, "tone", "dev:Scarlett 2i2"));
        Assert.NotNull(AudioRouting.Route(state, "vog", "ndi:ndi1"));
        Assert.Null(AudioRouting.Route(state, "sting", "ndi:ndi1"));
        Assert.Null(AudioRouting.Route(state, "screen:INFO", "dev:Scarlett 2i2"));   // a screen's own picture starts unrouted
        Assert.Equal(0, AudioRouting.SeedDefaults(state));                              // never over a table that has something in it
    }

    [Fact]
    public void ThePlanAppliesLevelsTrimMuteAndEachVogModeOnlyWhileAVogPlays()
    {
        var state = Show();
        state.AudioRouting.Enabled = true;
        AudioRouting.SetRoute(state, "music", "dev:Scarlett 2i2", -6);
        AudioRouting.SetRoute(state, "vog", "dev:Scarlett 2i2");
        AudioRouting.SetRoute(state, "programme", "dev:Scarlett 2i2");
        AudioRouting.SetRoute(state, "screen:INFO", "dev:HDMI 3");
        AudioRouting.SetRoute(state, "vog", "dev:HDMI 3");
        AudioRouting.SetRoute(state, "programme", "ndi:ndi1");
        AudioRouting.SetRoute(state, "vog", "ndi:ndi1");
        AudioRouting.Row(state, "dev:HDMI 3")!.VogMode = AudioVogMode.Replace;
        AudioRouting.Row(state, "ndi:ndi1")!.VogMode = AudioVogMode.Leave;
        AudioRouting.Row(state, "dev:Scarlett 2i2")!.TrimDb = -3;

        var quiet = AudioRouting.Resolve(state, vogPlaying: false);
        var scarlett = quiet.Single(p => p.Key == "dev:Scarlett 2i2");
        Assert.Equal(Db.ToGain(-9), scarlett.GainFor("music"), 6);       // −6 route, −3 trim
        Assert.Equal(Db.ToGain(-3), scarlett.GainFor("programme"), 6);
        Assert.Equal(0, scarlett.GainFor("tone"));                        // not routed
        Assert.True(scarlett.Carries("vog"));

        var loud = AudioRouting.Resolve(state, vogPlaying: true);
        var ducked = loud.Single(p => p.Key == "dev:Scarlett 2i2");
        Assert.Equal(Db.ToGain(-9) * Db.ToGain(Db.FromPercent(25)), ducked.GainFor("music"), 6);   // the show's duck level under the VOG
        Assert.Equal(Db.ToGain(-3), ducked.GainFor("vog"), 6);                        // the VOG itself never ducks
        var replaced = loud.Single(p => p.Key == "dev:HDMI 3");
        Assert.Equal(0, replaced.GainFor("screen:INFO"));                             // replaced: silence under the VOG
        Assert.Equal(1, replaced.GainFor("vog"), 6);
        var left = loud.Single(p => p.Key == "ndi:ndi1");
        Assert.Equal(1, left.GainFor("programme"), 6);                                // left alone
        Assert.Equal(0, left.GainFor("vog"));                                         // and the VOG never reaches it

        // A destination's own duck level and a mute.
        AudioRouting.Row(state, "dev:Scarlett 2i2")!.VogDuckDb = -20;
        Assert.Equal(Db.ToGain(-9) * Db.ToGain(-20), AudioRouting.PlanFor(state, "dev:Scarlett 2i2", true)!.GainFor("music"), 6);
        AudioRouting.Row(state, "dev:Scarlett 2i2")!.Mute = true;
        Assert.Equal(0, AudioRouting.PlanFor(state, "dev:Scarlett 2i2", false)!.GainFor("programme"));

        // Off, there is no plan at all: the two wires as before.
        state.AudioRouting.Enabled = false;
        Assert.Empty(AudioRouting.Resolve(state, true));
    }

    [Fact]
    public void ThePlayersOpenWhereTheMatrixSaysOrWhereTheyAlwaysDid()
    {
        var state = Show();
        // Off: the programme's devices with their delays, at unity.
        var classic = AudioRouting.OutputsFor(state, "music");
        var pick = Assert.Single(classic);
        Assert.Equal(("Scarlett 2i2", 1.0, 40), (pick.Device, pick.Gain, pick.DelayMs));
        Assert.Equal(40, AudioRouting.DelayFor(state, "Scarlett 2i2"));

        // On: the device destinations routed for the source, at the crosspoint's gain and the destination's delay; NDI is the mixer's.
        state.AudioRouting.Enabled = true;
        AudioRouting.SetRoute(state, "music", "dev:Scarlett 2i2", -6);
        AudioRouting.SetRoute(state, "music", "dev:HDMI 3");
        AudioRouting.SetRoute(state, "music", "ndi:ndi1");
        AudioRouting.SetRoute(state, "vog", "dev:HDMI 3");
        AudioRouting.Row(state, "dev:HDMI 3")!.DelayMs = 120;
        AudioRouting.Row(state, "dev:HDMI 3")!.VogMode = AudioVogMode.Leave;
        var picks = AudioRouting.OutputsFor(state, "music");
        Assert.Equal(2, picks.Count);
        Assert.Contains(picks, p => p.Device == "Scarlett 2i2" && Math.Abs(p.Gain - Db.ToGain(-6)) < 1e-6 && p.DelayMs == 0);
        Assert.Contains(picks, p => p.Device == "HDMI 3" && p.DelayMs == 120);
        Assert.Equal(new[] { "Scarlett 2i2", "HDMI 3" }, AudioRouting.DeviceNamesFor(state, "music"));
        Assert.Equal(120, AudioRouting.DelayFor(state, "HDMI 3"));
        // A Leave destination never opens a VOG at all.
        Assert.Empty(AudioRouting.OutputsFor(state, "vog"));
        // A source nobody routed opens nowhere.
        Assert.Empty(AudioRouting.OutputsFor(state, "tone"));
    }

    [Fact]
    public void AClipsSoundtrackTakesTheDecodersOwnDeviceForOneDestinationAndTheMixerForMore()
    {
        var state = Show();
        var programme = new[] { MediaBus.Program, MediaBus.Sandbox };
        var info = new[] { MediaBus.Output("INFO") };
        Assert.Equal(ClipAudioPath.Classic, AudioRouting.ClipRoute(state, programme).Path);
        Assert.Equal("programme", AudioRouting.SourceForBuses(programme));
        Assert.Equal("screen:INFO", AudioRouting.SourceForBuses(info));
        Assert.Equal("preview", AudioRouting.SourceForBuses(new[] { MediaBus.Sandbox }));
        Assert.Equal("programme", AudioRouting.SourceForBuses(null));

        state.AudioRouting.Enabled = true;
        Assert.Equal(ClipAudioPath.Silent, AudioRouting.ClipRoute(state, info).Path);
        AudioRouting.SetRoute(state, "screen:INFO", "dev:HDMI 3", -3);
        var one = AudioRouting.ClipRoute(state, info);
        Assert.Equal(ClipAudioPath.Device, one.Path);
        Assert.Equal("HDMI 3", one.Device);
        Assert.Equal(Db.ToGain(-3), one.Gain, 6);
        AudioRouting.SetRoute(state, "screen:INFO", "ndi:ndi1");
        var mixed = AudioRouting.ClipRoute(state, info);
        Assert.Equal(ClipAudioPath.Mixer, mixed.Path);
        Assert.Equal(2, mixed.Lanes.Count);
        Assert.Contains(mixed.Lanes, l => l.Source == "ndi:ndi1");   // the lanes keyed by destination for the mixer
    }

    [Fact]
    public void TheDuckEnvelopeLandsFastAndComesBackSlowWithoutOvershoot()
    {
        var env = new DuckEnvelope();
        Assert.Equal(1.0, env.Value);
        // A 40 ms attack: three time constants in, it has landed; never below the target.
        var v = env.Advance(0.25, 0.010, 40, 600);
        Assert.True(v is < 1.0 and > 0.25, $"{v}");
        for (var i = 0; i < 20; i++) v = env.Advance(0.25, 0.010, 40, 600);
        Assert.Equal(0.25, v, 3);
        // The release takes its time: after 100 ms of a 600 ms release it is nowhere near back.
        v = env.Advance(1.0, 0.100, 40, 600);
        Assert.True(v is > 0.25 and < 0.6, $"{v}");
        for (var i = 0; i < 30; i++) v = env.Advance(1.0, 0.100, 40, 600);
        Assert.Equal(1.0, v, 3);
        // No time passed, nothing moved; a target outside the range is clamped.
        Assert.Equal(1.0, env.Advance(0.0, 0, 40, 600));
        env.Reset(2);
        Assert.Equal(1.0, env.Value);
    }

    [Fact]
    public void TheWordsSayOffOrWhatIsRoutedAndHowTheVogBehaves()
    {
        var state = Show();
        Assert.StartsWith("Routing off", AudioRouting.Words(state));
        state.AudioRouting.Enabled = true;
        Assert.Contains("nothing routed yet", AudioRouting.Words(state));
        AudioRouting.SeedDefaults(state);
        AudioRouting.Row(state, "ndi:ndi1")!.VogMode = AudioVogMode.Leave;
        var words = AudioRouting.Words(state);
        Assert.Contains("8 routes on 2 destinations", words);
        Assert.Contains("ducks 1", words);
        Assert.Contains("stays off NDI Stream", words);
        Assert.Contains("Silent: Info screen, Preview", words);
        var row = AudioRouting.DestinationWords(state, AudioRouting.Row(state, "dev:Scarlett 2i2")!);
        Assert.StartsWith("Scarlett 2i2 · 40 ms · VOG ducks the rest to −12 dB · Programme 0 dB, Music (playlist) 0 dB", row);
    }

    [Fact]
    public void SourcesAndDestinationsAreFoundByTheirWords()
    {
        var state = Show();
        Assert.Equal("programme", AudioRouting.FindSource(state, "PGM"));
        Assert.Equal("music", AudioRouting.FindSource(state, "playlist"));
        Assert.Equal("screen:INFO", AudioRouting.FindSource(state, "screen INFO"));
        Assert.Equal("screen:INFO", AudioRouting.FindSource(state, "Info screen"));
        Assert.Equal("screen:INFO", AudioRouting.FindSource(state, "info"));
        Assert.Null(AudioRouting.FindSource(state, "bagpipes"));

        var devices = new[] { "Scarlett 2i2", "NVIDIA High Definition Audio (HDMI 3)" };
        Assert.Equal("dev:Scarlett 2i2", AudioRouting.FindDestination(state, devices, "scarlett 2i2"));
        Assert.Equal("dev:NVIDIA High Definition Audio (HDMI 3)", AudioRouting.FindDestination(state, devices, "hdmi 3"));
        Assert.Equal("ndi:ndi1", AudioRouting.FindDestination(state, devices, "NDI Stream"));
        Assert.Equal("dev:(computer output)", AudioRouting.FindDestination(state, devices, "computer"));
        state.AudioRouting.Destinations.Add(new AudioDestinationConfig { Key = "dev:Scarlett 2i2", Label = "Room desk" });
        Assert.Equal("dev:Scarlett 2i2", AudioRouting.FindDestination(state, devices, "room desk"));
        Assert.Null(AudioRouting.FindDestination(state, devices, "moon"));

        Assert.Equal(("Info HDMI", -6.0), AudioRouting.ParseRouteValue("Info HDMI AT -6"));
        Assert.Equal(("Info HDMI", 0.0), AudioRouting.ParseRouteValue("Info HDMI"));
        Assert.Equal(("Info HDMI", -3.0), AudioRouting.ParseRouteValue("Info HDMI at −3 dB"));
        Assert.True(double.IsNaN(AudioRouting.ParseRouteValue("Info HDMI AT loud").LevelDb));
        Assert.True(AudioRouting.TryParseVogMode("REPLACE", out var mode) && mode == AudioVogMode.Replace);
        Assert.True(AudioRouting.TryParseVogMode("off", out mode) && mode == AudioVogMode.Leave);
        Assert.False(AudioRouting.TryParseVogMode("loudly", out _));
    }

    [Fact]
    public void TheWireOscTheSummaryAndTheChecksSpellTheVerbs()
    {
        var route = ControlProtocol.Parse("AUDIO ROUTE music TO Info HDMI AT -6");
        Assert.Equal(ShowActionKind.AudioRoute, route.Action.Kind);
        Assert.Equal("music", route.Action.Target);
        Assert.Equal("Info HDMI AT -6", route.Action.Value);
        Assert.Equal(ShowActionKind.AudioUnroute, ControlProtocol.Parse("AUDIO UNROUTE screen INFO FROM NDI Stream").Action.Kind);
        Assert.Equal("NDI Stream", ControlProtocol.Parse("AUDIO UNROUTE screen INFO FROM NDI Stream").Action.Value);
        var vog = ControlProtocol.Parse("AUDIO VOG Info HDMI REPLACE");
        Assert.Equal(ShowActionKind.AudioVogMode, vog.Action.Kind);
        Assert.Equal("Info HDMI", vog.Action.Target);
        Assert.Equal("replace", vog.Action.Value);
        Assert.Equal("on", ControlProtocol.Parse("AUDIO ROUTING ON").Action.Value);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUDIO ROUTING LOUD").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUDIO ROUTE music").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUDIO ROUTE music TO desk AT loud").Kind);

        Assert.Equal("AUDIO ROUTING ON", OscMap.ToLine(OscMessage.Of("/patterns/audio/routing", "on")));
        Assert.Equal("AUDIO ROUTE music TO Info HDMI AT -6", OscMap.ToLine(OscMessage.Of("/patterns/audio/route", "music", "Info HDMI", -6.0)));
        Assert.Equal("AUDIO ROUTE music TO Info HDMI", OscMap.ToLine(OscMessage.Of("/patterns/audio/route", "music", "Info HDMI")));
        Assert.Equal("AUDIO UNROUTE music FROM Info HDMI", OscMap.ToLine(OscMessage.Of("/patterns/audio/unroute", "music", "Info HDMI")));
        Assert.Equal("AUDIO VOG Info HDMI LEAVE", OscMap.ToLine(OscMessage.Of("/patterns/audio/vog", "Info HDMI", "leave")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/audio/vog", "Info HDMI", "loudly")));

        var state = Show();
        Assert.Equal("Route Music (playlist) → Info HDMI at −6 dB", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.AudioRoute, Target = "music", Value = "Info HDMI AT -6" }));
        Assert.Equal("VOG on Info HDMI: replace", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.AudioVogMode, Target = "Info HDMI", Value = "replace" }));
        Assert.Equal((TargetKind.AudioSource, ValueKind.AudioRouteTo), ActionSpec.For(ShowActionKind.AudioRoute));

        var cue = new RunCueConfig { Name = "Sound" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.AudioRoute, Target = "bagpipes", Value = "Scarlett 2i2" });
        Assert.Equal(1, CueValidator.ValidateOne(state, cue, null).BrokenCount);
        cue.Actions[0].Target = "music";
        Assert.Equal(0, CueValidator.ValidateOne(state, cue, null).BrokenCount);     // an unknown destination is a soft note, looked up when the cue fires
        cue.Actions[0].Value = "Scarlett 2i2 AT loud";
        Assert.Equal(1, CueValidator.ValidateOne(state, cue, null).BrokenCount);
        cue.Actions.Clear();
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.AudioVogMode, Target = "Scarlett 2i2", Value = "loudly" });
        Assert.Equal(1, CueValidator.ValidateOne(state, cue, null).BrokenCount);
    }
}
