using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The caller's VT clock in the pure layer: the clip on air read from a snapshot (which file, its
/// role), the words for a metre, the ten-second out and a loop that never comes out, the wire and
/// OSC verbs, the feedback, the cue actions and their checks, the sequencer's clock wound forward.
/// </summary>
public class VideoClockTests
{
    /// <summary>A decoder that never decodes: a timeline a test sets.</summary>
    private sealed class Clip : IVideoFrameSource
    {
        public double Length = 210;
        public double Position = 62;
        public bool Ended;
        public bool Seekable = true;
        public double? SeekedTo;

        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
        public SKSizeI? FrameSize => null;
        public bool IsPlaying => !Ended;
        public bool IsEnded => Ended;
        public double DurationSeconds => Length;
        public string StatusText => "clip";
        public double PositionSeconds => Position;
        public bool CanSeek => Seekable;

        public bool Seek(double seconds)
        {
            if (!Seekable) return false;
            SeekedTo = seconds;
            return true;
        }
    }

    private static ShowSnapshot ProgramClip(string path, bool loop = false, string layerPath = "")
    {
        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Media.Source = MediaSource.Video;
        s.Pattern.Media.VideoPath = path;
        s.Pattern.Media.Loop = loop;
        if (layerPath.Length > 0)
        {
            s.Pattern.Layer1.Enabled = true;
            s.Pattern.Layer1.Source = LayerSource.Video;
            s.Pattern.Layer1.VideoPath = layerPath;
        }
        return new ShowSnapshot { State = s, Version = 1 };
    }

    [Fact]
    public void TheWordsReadLikeAMetre()
    {
        Assert.Equal("0:00", VideoClock.Format(0));
        Assert.Equal("0:07", VideoClock.Format(7.4));
        Assert.Equal("3:30", VideoClock.Format(210));
        Assert.Equal("1:02:05", VideoClock.Format(3725));
        Assert.Equal("0:00", VideoClock.Format(-3));

        Assert.True(VideoClock.TryParseBeforeEnd("", out var d));
        Assert.Equal(10, d);
        Assert.True(VideoClock.TryParseBeforeEnd("2.5", out var half));
        Assert.Equal(2.5, half);
        Assert.True(VideoClock.TryParseBeforeEnd("30s", out var thirty));
        Assert.Equal(30, thirty);
        Assert.False(VideoClock.TryParseBeforeEnd("soon", out _));
        Assert.False(VideoClock.TryParseBeforeEnd("-1", out _));
    }

    [Fact]
    public void TheReadingIsTheProgramsClipFirstAndNamesWhereItCameFrom()
    {
        var program = new Clip();
        var layer = new Clip { Length = 30, Position = 1 };
        IVideoFrameSource? Resolve(string key) => key == InputKeys.Video("C:/show/sponsor.mp4") ? program : key == InputKeys.Video("C:/show/broll.mp4") ? layer : null;

        var reading = VideoClock.Read(ProgramClip("C:/show/sponsor.mp4", layerPath: "C:/show/broll.mp4"), Resolve);
        Assert.NotNull(reading);
        Assert.Equal(VideoRole.Program, reading!.Role);
        Assert.Equal("sponsor.mp4", reading.Name);
        Assert.Equal(62, reading.PositionSeconds);
        Assert.Equal(210, reading.LengthSeconds);
        Assert.Equal(148, reading.RemainingSeconds);
        Assert.True(reading.CanSeek);
        Assert.Equal("VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left", VideoClock.Describe(reading));
        Assert.Equal("VT 2:28", VideoClock.Chip(reading));
        Assert.Equal("", VideoClock.Call(reading));

        // A stinger's clip owns the screens: the app says so, the clock names it.
        var sting = VideoClock.Read(ProgramClip("C:/show/sponsor.mp4"), Resolve, stingerClip: true)!;
        Assert.Equal(VideoRole.Stinger, sting.Role);
        Assert.Equal("STINGER CLIP", VideoClock.Tag(sting));

        // The program is a picture of its own: the layer's clip is the one on air, and reads as such.
        var grid = ProgramClip("", layerPath: "C:/show/broll.mp4");
        grid.State.Pattern.Kind = PatternKind.Grid;
        var over = VideoClock.Read(grid, Resolve)!;
        Assert.Equal(VideoRole.Layer, over.Role);
        Assert.Equal("broll.mp4", over.Name);

        // A playlist's video item.
        var list = new ShowState();
        list.Pattern.Kind = PatternKind.Media;
        list.Pattern.Media.Source = MediaSource.Playlist;
        var snap = new ShowSnapshot { State = list, Version = 2, PlaylistNow = new PlaylistNow("C:/show/sponsor.mp4", true, 0, 3, DateTime.UtcNow, 210) };
        var item = VideoClock.Read(snap, Resolve)!;
        Assert.Equal(VideoRole.Playlist, item.Role);
        Assert.Equal("PLAYLIST", VideoClock.Tag(item));

        // Nothing on air is a file, or its decoder is not open: no clock.
        Assert.Null(VideoClock.Read(ProgramClip(""), Resolve));
        Assert.Null(VideoClock.Read(ProgramClip("C:/show/other.mp4"), Resolve));
        Assert.Equal("", VideoClock.Describe(null));
    }

    [Fact]
    public void TheLastTenSecondsAreTheOutAndALoopNeverComesOut()
    {
        var clip = new Clip { Position = 203 };
        var near = VideoClock.Read(ProgramClip("C:/show/sponsor.mp4"), _ => clip)!;
        Assert.True(near.InLast(VideoClock.OutWarningSeconds));
        Assert.Equal("OUT IN 7", VideoClock.Call(near));
        Assert.Equal("VT 0:07", VideoClock.Chip(near));
        Assert.Equal("3:23 / 3:30 · 0:07 left", VideoClock.Times(near));

        var loop = VideoClock.Read(ProgramClip("C:/show/sponsor.mp4", loop: true), _ => clip)!;
        Assert.True(loop.Loops);
        Assert.False(loop.InLast(VideoClock.OutWarningSeconds));
        Assert.Equal("", VideoClock.Call(loop));
        Assert.Equal("3:23 / 3:30 · loop", VideoClock.Times(loop));
        Assert.Equal("VT LOOP 3:23", VideoClock.Chip(loop));

        clip.Ended = true;
        var ended = VideoClock.Read(ProgramClip("C:/show/sponsor.mp4"), _ => clip)!;
        Assert.Equal("ended", VideoClock.Times(ended));
        Assert.Equal("VT ENDED", VideoClock.Chip(ended));
        Assert.False(ended.InLast(VideoClock.OutWarningSeconds));

        var opening = new Clip { Length = 0, Position = 3 };
        var unknown = VideoClock.Read(ProgramClip("C:/show/sponsor.mp4"), _ => opening)!;
        Assert.False(unknown.HasLength);
        Assert.Equal("0:03", VideoClock.Times(unknown));
        Assert.Equal("VT 0:03", VideoClock.Chip(unknown));
        Assert.Equal(0, unknown.Fraction);

        var audio = VideoClock.Read(ProgramClip("C:/show/walk-in.mp3"), _ => new Clip())!;
        Assert.True(audio.IsAudioOnly);
        Assert.Equal("AUDIO", VideoClock.Tag(audio));
        Assert.StartsWith("AUDIO walk-in.mp3", VideoClock.Describe(audio));
    }

    [Fact]
    public void TheWireReadsVideoEndAndRestartWithTheirAliases()
    {
        var end = ControlProtocol.Parse("VIDEO END");
        Assert.Equal((RemoteCommandKind.VideoToEnd, 0), (end.Kind, end.IntArg));
        Assert.Equal(5000, ControlProtocol.Parse("VIDEO END 5").IntArg);
        Assert.Equal(2500, ControlProtocol.Parse("vt end 2.5").IntArg);
        Assert.Equal(30000, ControlProtocol.Parse("CLIP LAST 30").IntArg);
        Assert.Equal(RemoteCommandKind.VideoToEnd, ControlProtocol.Parse("VIDEO OUT").Kind);
        Assert.Equal(RemoteCommandKind.VideoRestart, ControlProtocol.Parse("VIDEO RESTART").Kind);
        Assert.Equal(RemoteCommandKind.VideoRestart, ControlProtocol.Parse("VT START").Kind);
        Assert.Equal(RemoteCommandKind.VideoRestart, ControlProtocol.Parse("clip top").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("VIDEO").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("VIDEO END soon").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("VIDEO PAUSE").Kind);
    }

    [Fact]
    public void OscAddressesTheClipAndFeedsTheClockBack()
    {
        Assert.Equal("VIDEO END", OscMap.ToLine(OscMessage.Of("/patterns/video/end")));
        Assert.Equal("VIDEO END 5", OscMap.ToLine(OscMessage.Of("/patterns/video/end", 5)));
        Assert.Equal("VIDEO END 5", OscMap.ToLine(OscMessage.Of("/patterns/video/end/5")));
        Assert.Equal("VIDEO END 2.5", OscMap.ToLine(OscMessage.Of("/patterns/vt/end", 2.5f)));
        Assert.Equal("VIDEO RESTART", OscMap.ToLine(OscMessage.Of("/patterns/video/restart")));
        Assert.Equal("VIDEO RESTART", OscMap.ToLine(OscMessage.Of("/patterns/clip/start")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/video/pause")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/video")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/video/end"));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/video/restart"));

        var on = OscFeedback.FromState("{\"video\":{\"file\":\"sponsor.mp4\",\"position\":62,\"length\":210,\"remaining\":148,\"text\":\"VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left\",\"out\":false}}");
        Assert.Contains(on, m => m.Address == "/patterns/state/video/file" && Equals(m.Args[0], "sponsor.mp4"));
        Assert.Contains(on, m => m.Address == "/patterns/state/video/remaining" && Equals(m.Args[0], 148));
        Assert.Contains(on, m => m.Address == "/patterns/state/video/length" && Equals(m.Args[0], 210));
        Assert.Contains(on, m => m.Address == "/patterns/state/video/out" && Equals(m.Args[0], 0));
        Assert.Contains(on, m => m.Address == "/patterns/state/video/text");

        var off = OscFeedback.FromState("{\"video\":null}");
        Assert.Contains(off, m => m.Address == "/patterns/state/video/file" && Equals(m.Args[0], ""));
        Assert.Contains(off, m => m.Address == "/patterns/state/video/remaining" && Equals(m.Args[0], 0));
        Assert.Contains(off, m => m.Address == "/patterns/state/video/out" && Equals(m.Args[0], 0));
    }

    [Fact]
    public void TheCueActionsAreSpecifiedSummarisedAndChecked()
    {
        Assert.Equal((TargetKind.None, ValueKind.Seconds), CueActionSpec.For(CueActionKind.VideoToEnd));
        Assert.Equal((TargetKind.None, ValueKind.None), CueActionSpec.For(CueActionKind.VideoRestart));
        Assert.Contains(CueActionKind.VideoToEnd, CueActionSpec.Editable);
        Assert.Contains(CueActionKind.VideoRestart, CueActionSpec.Editable);
        Assert.False(CueActionSpec.ChangesContent(CueActionKind.VideoToEnd));
        Assert.Contains("last seconds", CueActionSpec.Label(CueActionKind.VideoToEnd));
        Assert.Contains("restart", CueActionSpec.Label(CueActionKind.VideoRestart));

        Assert.Equal(CueActionKind.VideoToEnd, CueSheet.ParseKind("Video — jump to its last seconds"));
        Assert.Equal(CueActionKind.VideoToEnd, CueSheet.ParseKind("vt end"));
        Assert.Equal(CueActionKind.VideoToEnd, CueSheet.ParseKind("skip to end"));
        Assert.Equal(CueActionKind.VideoRestart, CueSheet.ParseKind("Video — restart from the top"));
        Assert.Equal(CueActionKind.VideoRestart, CueSheet.ParseKind("rewind"));
        Assert.Equal(CueActionKind.VideoRestart, CueSheet.ParseKind("restart video"));

        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Grid;
        var skip = new RunCueConfig { Number = "1", Name = "Skip the walk-in" };
        skip.Actions.Add(new CueActionConfig { Kind = CueActionKind.VideoToEnd, Value = "5" });
        var top = new RunCueConfig { Number = "2", Name = "Loop again" };
        top.Actions.Add(new CueActionConfig { Kind = CueActionKind.VideoRestart });
        var stack = new CueStackConfig();
        stack.Cues.Add(skip);
        stack.Cues.Add(top);

        Assert.Equal("Video: to its last 5 s", CueSummary.DescribeAction(s, skip.Actions[0]));
        Assert.Equal("Video: restart from the top", CueSummary.DescribeAction(s, top.Actions[0]));
        skip.Actions[0].Value = "";
        Assert.Equal("Video: to its last 10 s", CueSummary.DescribeAction(s, skip.Actions[0]));

        // No clip on air as far as the checks can see is a warning, not a broken cue; a value that is not seconds is.
        var report = CueValidator.Validate(s, stack);
        Assert.False(report.IsBroken(skip.Id));
        Assert.False(report.IsBroken(top.Id));
        skip.Actions[0].Value = "soon";
        Assert.Contains("seconds", CueValidator.Validate(s, stack).ReasonFor(skip.Id));
        Assert.Contains("not seconds", CueSummary.DescribeAction(s, skip.Actions[0]));
    }

    [Fact]
    public void TheSequencersItemClockIsWoundForwardAndBack()
    {
        var t0 = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
        var options = new PlaylistOptions { ImageDwellSeconds = 30 };
        var seq = new PlaylistSequencer();
        seq.SetOrder(new List<PlaylistEntry>
        {
            new("C:/show/a.png", false, 0, "", 60),
            new("C:/show/b.png", false, 0, "", 60),
        }, t0);
        seq.Tick(options, t0, t0, false, 0);                                     // the first item is up
        Assert.Equal(0, seq.CurrentIndex);
        Assert.False(seq.Tick(options, t0, t0.AddSeconds(10), false, 0));

        // The item ends in five seconds from now: not due at four, due after five.
        Assert.True(seq.EndItemIn(5, t0.AddSeconds(10)));
        Assert.False(seq.Tick(options, t0, t0.AddSeconds(14), false, 0));
        Assert.Equal(0, seq.CurrentIndex);
        Assert.True(seq.Tick(options, t0, t0.AddSeconds(15.1), false, 0));
        Assert.Equal(1, seq.CurrentIndex);

        // The clock starts again: the second item runs its whole dwell from the restart, not from when it began.
        Assert.True(seq.RestartItem(t0.AddSeconds(40)));
        Assert.False(seq.Tick(options, t0, t0.AddSeconds(69), false, 0));
        Assert.Equal(1, seq.CurrentIndex);
        Assert.True(seq.Tick(options, t0, t0.AddSeconds(70.1), false, 0));
        Assert.Equal(0, seq.CurrentIndex);

        Assert.False(new PlaylistSequencer().EndItemIn(5, t0));
        Assert.False(new PlaylistSequencer().RestartItem(t0));
    }
}
