using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The audio playlist's rules in the pure layer: the order (rows, then folders, no file twice, the
/// old single track as a fallback, a shuffle that repeats by its seed), the folders read through an
/// enumerator, stepping and wrapping, finding a track four ways, the words, the migration, the
/// verbs on the wire and over OSC, the feedback, and the cue actions through spec, sheet, summary
/// and checks.
/// </summary>
public class AudioPlaylistTests
{
    private static AudioPlayerConfig List(params string[] paths)
    {
        var cfg = new AudioPlayerConfig();
        foreach (var p in paths) cfg.Items.Add(new AudioTrackConfig { Path = p });
        return cfg;
    }

    [Fact]
    public void TheOrderIsTheRowsThenTheFoldersOnceEachWithTheOldTrackAsAFallback()
    {
        var cfg = List("C:/bed/a.mp3", "C:/bed/B.wav", "c:/bed/A.MP3");
        var folder = new[] { "C:/more/z.flac", "C:/more/y.mp3", "C:/bed/b.wav" };
        var order = AudioPlaylist.BuildOrder(cfg, folder);
        Assert.Equal(new[] { "C:/bed/a.mp3", "C:/bed/B.wav", "C:/more/z.flac", "C:/more/y.mp3" }, order);

        var old = new AudioPlayerConfig { Path = "C:/old/track.mp3" };
        Assert.Equal(new[] { "C:/old/track.mp3" }, AudioPlaylist.BuildOrder(old, Array.Empty<string>()));
        Assert.True(AudioPlaylist.HasTracks(old));
        Assert.False(AudioPlaylist.HasTracks(new AudioPlayerConfig()));
        var folderOnly = new AudioPlayerConfig();
        folderOnly.Folders.Add("C:/bed");
        Assert.True(AudioPlaylist.HasTracks(folderOnly));
        Assert.Empty(AudioPlaylist.BuildOrder(new AudioPlayerConfig(), Array.Empty<string>()));
    }

    [Fact]
    public void AShuffleRepeatsByItsSeedAndANewSeedDealsAgain()
    {
        var cfg = List("C:/1.mp3", "C:/2.mp3", "C:/3.mp3", "C:/4.mp3", "C:/5.mp3", "C:/6.mp3", "C:/7.mp3", "C:/8.mp3");
        cfg.Shuffle = true;
        cfg.ShuffleSeed = 7;
        var first = AudioPlaylist.BuildOrder(cfg, Array.Empty<string>());
        var again = AudioPlaylist.BuildOrder(cfg, Array.Empty<string>());
        Assert.Equal(first, again);
        Assert.Equal(8, first.Distinct().Count());
        Assert.NotEqual(cfg.Items.Select(i => i.Path).ToList(), first);
        cfg.ShuffleSeed = 8;
        Assert.NotEqual(first, AudioPlaylist.BuildOrder(cfg, Array.Empty<string>()));
        Assert.NotEqual(AudioPlaylist.OrderKey(cfg, Array.Empty<string>()), AudioPlaylist.OrderKey(List("C:/1.mp3"), Array.Empty<string>()));
    }

    [Fact]
    public void TheFoldersAreReadInNameOrderThroughTheEnumeratorAndCapped()
    {
        IEnumerable<string> Enumerate(string folder) => folder switch
        {
            "C:/bed" => new[] { "C:/bed/zeta.mp3", "C:/bed/notes.txt", "C:/bed/Alpha.wav", "C:/bed/cover.jpg" },
            "C:/broken" => throw new IOException("no such folder"),
            _ => Array.Empty<string>(),
        };
        var files = AudioPlaylist.AudioFilesIn(new[] { "C:/bed", "", "C:/broken", "C:/empty" }, Enumerate);
        Assert.Equal(new[] { "C:/bed/Alpha.wav", "C:/bed/zeta.mp3" }, files);

        var many = AudioPlaylist.AudioFilesIn(new[] { "C:/big" }, _ => Enumerable.Range(0, 5000).Select(i => $"C:/big/{i:0000}.mp3"));
        Assert.Equal(AudioPlaylist.MaxFolderFiles, many.Count);
    }

    [Fact]
    public void SteppingWrapsWithLoopAndStopsWithoutAndATrackIsFoundFourWays()
    {
        Assert.Equal(1, AudioPlaylist.Step(0, 3, +1, loop: false));
        Assert.Null(AudioPlaylist.Step(2, 3, +1, loop: false));
        Assert.Equal(0, AudioPlaylist.Step(2, 3, +1, loop: true));
        Assert.Equal(2, AudioPlaylist.Step(0, 3, -1, loop: true));
        Assert.Equal(0, AudioPlaylist.Step(-1, 3, +1, loop: false));
        Assert.Null(AudioPlaylist.Step(0, 0, +1, loop: true));

        var cfg = List("C:/bed/walk-in.mp3", "C:/bed/intro.wav");
        cfg.Items[1].Name = "Opening sting";
        var order = AudioPlaylist.BuildOrder(cfg, new[] { "C:/more/Closing Song.mp3" });
        Assert.Equal(0, AudioPlaylist.Find(cfg, order, "1"));
        Assert.Equal(2, AudioPlaylist.Find(cfg, order, "3"));
        Assert.Equal(-1, AudioPlaylist.Find(cfg, order, "4"));
        Assert.Equal(1, AudioPlaylist.Find(cfg, order, cfg.Items[1].Id));
        Assert.Equal(1, AudioPlaylist.Find(cfg, order, "opening sting"));
        Assert.Equal(1, AudioPlaylist.Find(cfg, order, "intro.wav"));
        Assert.Equal(0, AudioPlaylist.Find(cfg, order, "walk-in"));
        Assert.Equal(2, AudioPlaylist.Find(cfg, order, "closing song"));
        Assert.Equal(-1, AudioPlaylist.Find(cfg, order, "nope"));
        Assert.Equal(-1, AudioPlaylist.Find(cfg, order, ""));
        Assert.Equal("Opening sting", AudioPlaylist.NameOf(cfg, "C:/bed/intro.wav"));
        Assert.Equal("Closing Song", AudioPlaylist.NameOf(cfg, "C:/more/Closing Song.mp3"));
        Assert.Equal("", AudioPlaylist.NameOf(cfg, ""));
        Assert.Equal("Opening sting", AudioPlaylist.FindItem(cfg, "Opening sting")!.DisplayName);
        Assert.Null(AudioPlaylist.FindItem(cfg, "closing song"));
    }

    [Fact]
    public void TheOldTrackBecomesTheFirstRowOnceAndTheSchemaMovesOn()
    {
        var state = new ShowState { SchemaVersion = 7 };
        state.AudioPlayer.Path = "C:/old/walk-in.mp3";
        SettingsStore.Migrate(state);
        Assert.Equal(8, ShowState.CurrentSchemaVersion);
        Assert.Equal(ShowState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Single(state.AudioPlayer.Items);
        Assert.Equal("C:/old/walk-in.mp3", state.AudioPlayer.Items[0].Path);
        Assert.Equal("walk-in", state.AudioPlayer.Items[0].DisplayName);
        Assert.Equal("", state.AudioPlayer.Path);
        Assert.False(string.IsNullOrWhiteSpace(state.AudioPlayer.Items[0].Id));

        SettingsStore.Migrate(state);                                                    // idempotent
        Assert.Single(state.AudioPlayer.Items);

        var edited = new AudioPlayerConfig();
        edited.Items.Add(new AudioTrackConfig { Path = "C:/a.mp3", Id = "" });
        AudioPlaylist.Migrate(edited);
        Assert.False(string.IsNullOrWhiteSpace(edited.Items[0].Id));
    }

    [Fact]
    public void TheWireReadsTheListsVerbs()
    {
        Assert.Equal((RemoteCommandKind.AudioPlay, 0, ""), Parts(ControlProtocol.Parse("AUDIO PLAY")));
        Assert.Equal((RemoteCommandKind.AudioPlay, 3, ""), Parts(ControlProtocol.Parse("AUDIO PLAY 3")));
        Assert.Equal((RemoteCommandKind.AudioPlay, 0, "Walk-in music"), Parts(ControlProtocol.Parse("audio play Walk-in music")));
        Assert.Equal((RemoteCommandKind.AudioPlay, 2, ""), Parts(ControlProtocol.Parse("TRACK PLAY 2")));
        Assert.Equal(RemoteCommandKind.AudioStop, ControlProtocol.Parse("AUDIO STOP").Kind);
        Assert.Equal(RemoteCommandKind.AudioNext, ControlProtocol.Parse("AUDIO NEXT").Kind);
        Assert.Equal(RemoteCommandKind.AudioNext, ControlProtocol.Parse("audio skip").Kind);
        Assert.Equal(RemoteCommandKind.AudioPrev, ControlProtocol.Parse("AUDIO PREV").Kind);
        Assert.Equal(RemoteCommandKind.AudioPrev, ControlProtocol.Parse("AUDIO BACK").Kind);
        Assert.Equal((RemoteCommandKind.AudioVolume, 0, "80"), Parts(ControlProtocol.Parse("AUDIO VOL 80")));
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUDIO VOL 200").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUDIO VOL loud").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUDIO").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUDIO DANCE").Kind);
    }

    private static (RemoteCommandKind, int, string) Parts(RemoteCommand c) => (c.Kind, c.IntArg, c.TextArg);

    [Fact]
    public void OscAddressesTheListAndFeedsItBack()
    {
        Assert.Equal("AUDIO PLAY", OscMap.ToLine(OscMessage.Of("/patterns/audio/play")));
        Assert.Equal("AUDIO PLAY 3", OscMap.ToLine(OscMessage.Of("/patterns/audio/play", 3)));
        Assert.Equal("AUDIO PLAY 3", OscMap.ToLine(OscMessage.Of("/patterns/audio/play/3")));
        Assert.Equal("AUDIO PLAY Walk-in", OscMap.ToLine(OscMessage.Of("/patterns/audio/play", "Walk-in")));
        Assert.Equal("AUDIO STOP", OscMap.ToLine(OscMessage.Of("/patterns/audio/stop")));
        Assert.Equal("AUDIO NEXT", OscMap.ToLine(OscMessage.Of("/patterns/audio/next")));
        Assert.Equal("AUDIO PREV", OscMap.ToLine(OscMessage.Of("/patterns/track/back")));
        Assert.Equal("AUDIO VOL 80", OscMap.ToLine(OscMessage.Of("/patterns/audio/volume", 80)));
        Assert.Equal("AUDIO VOL 50", OscMap.ToLine(OscMessage.Of("/patterns/audio/volume", 0.5f)));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/audio/dance")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/audio/play"));
        Assert.Contains(OscMap.Reference, r => r.Address.Contains("/patterns/audio/next"));

        var fed = OscFeedback.FromState("{\"audio\":{\"playing\":true,\"track\":\"walk-in\",\"n\":2,\"count\":3,\"next\":\"intro\",\"remaining\":148,\"items\":[{\"n\":1,\"name\":\"opener\"},{\"n\":2,\"name\":\"walk-in\"},{\"n\":3,\"name\":\"intro\"}]}}");
        Assert.Contains(fed, m => m.Address == "/patterns/state/audio" && Equals(m.Args[0], 1));
        Assert.Contains(fed, m => m.Address == "/patterns/state/audio/track" && Equals(m.Args[0], "walk-in"));
        Assert.Contains(fed, m => m.Address == "/patterns/state/audio/next" && Equals(m.Args[0], "intro"));
        Assert.Contains(fed, m => m.Address == "/patterns/state/audio/n" && Equals(m.Args[0], 2));
        Assert.Contains(fed, m => m.Address == "/patterns/state/audio/remaining" && Equals(m.Args[0], 148));
        Assert.Contains(fed, m => m.Address == "/patterns/state/audio/items/3" && Equals(m.Args[0], "intro"));
        Assert.Contains(fed, m => m.Address == "/patterns/state/audio/items/4" && Equals(m.Args[0], ""));
    }

    [Fact]
    public void TheCueActionsAreSpecifiedSummarisedAndChecked()
    {
        Assert.Equal((TargetKind.Track, ValueKind.None), CueActionSpec.For(CueActionKind.AudioPlay));
        Assert.Contains(CueActionKind.AudioNext, CueActionSpec.Editable);
        Assert.Contains(CueActionKind.AudioPrev, CueActionSpec.Editable);
        Assert.Contains("next", CueActionSpec.Label(CueActionKind.AudioNext));
        Assert.Equal(CueActionKind.AudioNext, CueSheet.ParseKind("next track"));
        Assert.Equal(CueActionKind.AudioNext, CueSheet.ParseKind("Audio — next track"));
        Assert.Equal(CueActionKind.AudioPrev, CueSheet.ParseKind("previous track"));
        Assert.Equal(CueActionKind.AudioPlay, CueSheet.ParseKind("track"));

        var s = new ShowState();
        s.AudioPlayer.Items.Add(new AudioTrackConfig { Path = "C:/bed/walk-in.mp3", Name = "Walk-in" });
        s.AudioPlayer.Items.Add(new AudioTrackConfig { Path = "C:/bed/intro.wav" });
        var byName = new CueActionConfig { Kind = CueActionKind.AudioPlay, Target = "Walk-in" };
        var byId = new CueActionConfig { Kind = CueActionKind.AudioPlay, Target = s.AudioPlayer.Items[1].Id };
        var byNumber = new CueActionConfig { Kind = CueActionKind.AudioPlay, Target = "7" };
        var all = new CueActionConfig { Kind = CueActionKind.AudioPlay };
        var missing = new CueActionConfig { Kind = CueActionKind.AudioPlay, Target = "Nobody" };
        Assert.Equal("Play audio: Walk-in", CueSummary.DescribeAction(s, byName));
        Assert.Equal("Play audio: intro", CueSummary.DescribeAction(s, byId));
        Assert.Equal("Play audio track 7", CueSummary.DescribeAction(s, byNumber));
        Assert.Equal("Play audio", CueSummary.DescribeAction(s, all));
        Assert.Contains("not in the list", CueSummary.DescribeAction(s, missing));
        Assert.Equal("Audio: the next track", CueSummary.DescribeAction(s, new CueActionConfig { Kind = CueActionKind.AudioNext }));

        var stack = new CueStackConfig();
        var cue = new RunCueConfig { Number = "1", Name = "Music" };
        cue.Actions.Add(byName);
        stack.Cues.Add(cue);
        var exists = new CueValidationContext { FileExists = _ => true };
        Assert.False(CueValidator.Validate(s, stack, exists).IsBroken(cue.Id));
        cue.Actions[0] = missing;
        Assert.Contains("not in the list", CueValidator.Validate(s, stack, exists).ReasonFor(cue.Id));
        cue.Actions[0] = byNumber;
        Assert.False(CueValidator.Validate(s, stack, exists).IsBroken(cue.Id));            // a number: the folders can fill it at show time
        cue.Actions[0] = all;
        Assert.False(CueValidator.Validate(s, stack, exists).IsBroken(cue.Id));
        var gone = new CueValidationContext { FileExists = _ => false };
        Assert.Contains("on disk", CueValidator.Validate(s, stack, gone).ReasonFor(cue.Id));
        cue.Actions[0] = byName;
        Assert.Contains("missing", CueValidator.Validate(s, stack, gone).ReasonFor(cue.Id));

        var empty = new ShowState();
        Assert.Contains("empty", CueValidator.Validate(empty, stack, exists).ReasonFor(cue.Id));
        cue.Actions[0] = new CueActionConfig { Kind = CueActionKind.AudioNext };
        Assert.False(CueValidator.Validate(empty, stack, exists).IsBroken(cue.Id));      // a soft note only
    }
}
