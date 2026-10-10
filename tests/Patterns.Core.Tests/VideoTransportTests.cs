using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 86: the clip's transport for another app — VIDEO PLAY / PAUSE / SEEK / STOP on the wire
/// and OSC (and bare STOP still OUTPUTS OFF), where a seek lands, the words while paused, the clip's
/// count of discontinuities (STATE video.seq), the feedback, and the show network's hello on 9928.
/// </summary>
public class VideoTransportTests
{
    [Theory]
    [InlineData("VIDEO PLAY", ShowActionKind.VideoPlay, "")]
    [InlineData("VT RESUME", ShowActionKind.VideoPlay, "")]
    [InlineData("VIDEO PAUSE", ShowActionKind.VideoPause, "")]
    [InlineData("CLIP HOLD", ShowActionKind.VideoPause, "")]
    [InlineData("VIDEO STOP", ShowActionKind.VideoStop, "")]
    [InlineData("VIDEO SEEK 83", ShowActionKind.VideoSeek, "83")]
    [InlineData("VIDEO SEEK 1:23", ShowActionKind.VideoSeek, "1:23")]
    [InlineData("VT SEEK 1:02:03", ShowActionKind.VideoSeek, "1:02:03")]
    [InlineData("VIDEO SEEK END", ShowActionKind.VideoSeek, "END")]
    [InlineData("VIDEO SEEK END 5", ShowActionKind.VideoSeek, "END 5")]
    [InlineData("video goto 2.5s", ShowActionKind.VideoSeek, "2.5s")]
    public void TheTransportParses(string line, ShowActionKind kind, string value)
    {
        var parsed = ControlProtocol.Parse(line);
        Assert.Equal(RemoteCommandKind.Action, parsed.Kind);
        Assert.Equal(kind, parsed.Action.Kind);
        Assert.Equal(value, parsed.Action.Value);
    }

    [Theory]
    [InlineData("VIDEO SEEK")]
    [InlineData("VIDEO SEEK soon")]
    [InlineData("VIDEO SEEK -3")]
    [InlineData("VIDEO SEEK END later")]
    [InlineData("VIDEO SEEK 99999999")]
    [InlineData("VIDEO STOP NOW")]
    [InlineData("VIDEO PLAY 3")]
    [InlineData("VIDEO PAUSE please")]
    [InlineData("VIDEO RESTART twice")]
    public void AnythingElseIsRefusedNeverDefaulted(string line)
        => Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse(line).Kind);

    [Fact]
    public void VideoStopIsTheClipsStopAndBareStopStaysTheOutputs()
    {
        Assert.Equal(ShowActionKind.OutputsOff, ControlProtocol.Parse("STOP").Action.Kind);
        Assert.Equal(ShowActionKind.VideoStop, ControlProtocol.Parse("VIDEO STOP").Action.Kind);
        Assert.NotEqual(ShowActionKind.OutputsOff, ControlProtocol.Parse("VT STOP").Action.Kind);
        Assert.Equal("VIDEO STOP", WireWriter.Line(new ShowAction(ShowActionKind.VideoStop)));
    }

    [Fact]
    public void TheWriterSpellsWhatTheParserReads()
    {
        foreach (var a in new[]
        {
            new ShowAction(ShowActionKind.VideoPlay), new ShowAction(ShowActionKind.VideoPause), new ShowAction(ShowActionKind.VideoStop),
            new ShowAction(ShowActionKind.VideoSeek, "", "1:23"), new ShowAction(ShowActionKind.VideoSeek, "", "END 5"),
        })
        {
            var back = ControlProtocol.Parse(WireWriter.Line(a)).Action;
            Assert.Equal((a.Kind, a.Value), (back.Kind, back.Value));
        }
    }

    [Fact]
    public void ASeekLandsOnTheClipAndNeverGuessesFromAnUnknownEnd()
    {
        Assert.True(VideoClock.TryParseSeek("1:23", out var s, out var fromEnd));
        Assert.Equal((83d, false), (s, fromEnd));
        Assert.True(VideoClock.TryParseSeek("end", out s, out fromEnd));
        Assert.Equal((10d, true), (s, fromEnd));
        Assert.True(VideoClock.TryParseSeek("END 2.5", out s, out fromEnd));
        Assert.Equal((2.5, true), (s, fromEnd));
        Assert.True(VideoClock.TryParseSeek("top", out s, out _));
        Assert.Equal(0, s);

        Assert.Equal(205, VideoClock.SeekTarget(5, fromEnd: true, lengthSeconds: 210));
        Assert.Equal(0, VideoClock.SeekTarget(10, fromEnd: true, lengthSeconds: 6));        // a clip shorter than the gap starts again
        Assert.Null(VideoClock.SeekTarget(10, fromEnd: true, lengthSeconds: 0));            // nothing to count back from: refused
        Assert.Equal(210, VideoClock.SeekTarget(500, fromEnd: false, lengthSeconds: 210));  // clamped to the clip
        Assert.Equal(30, VideoClock.SeekTarget(30, fromEnd: false, lengthSeconds: 0));      // from the top needs no length
    }

    [Fact]
    public void APausedClipSaysSoAndKeepsItsOutWordToItself()
    {
        var playing = new VideoReading("video:a", @"C:\clips\a.mp4", VideoRole.Program, 203, 210, true, false, false, true);
        Assert.Equal("OUT IN 7", VideoClock.Call(playing));
        var paused = playing with { Playing = false, Paused = true };
        Assert.Equal("", VideoClock.Call(paused));
        Assert.Equal("VT PAUSED 0:07", VideoClock.Chip(paused));
        Assert.Equal("3:23 / 3:30 · 0:07 left · paused", VideoClock.Times(paused));
        Assert.Equal("3:23 / 3:30 · 0:07 left", VideoClock.Times(playing));   // unchanged while it plays
        Assert.Equal(1, playing.Rate);
    }

    [Fact]
    public void TheSequenceMovesOnDiscontinuitiesAndNeverForTheClockRunningOn()
    {
        var seq = new VideoSequence();
        var r = new VideoReading("video:a", "a.mp4", VideoRole.Program, 10, 210, true, false, false, true);
        var first = seq.Next(r, 100);
        Assert.Equal(1, first);                                                         // a clip appeared
        Assert.Equal(first, seq.Next(r with { PositionSeconds = 11 }, 101));            // played on as expected
        Assert.Equal(first, seq.Next(r with { PositionSeconds = 12.2 }, 102));          // jitter
        var paused = seq.Next(r with { PositionSeconds = 12.2, Playing = false, Paused = true }, 103);
        Assert.Equal(first + 1, paused);
        Assert.Equal(paused, seq.Next(r with { PositionSeconds = 12.2, Playing = false, Paused = true }, 110));   // held: no movement
        var sought = seq.Next(r with { PositionSeconds = 150, Playing = false, Paused = true }, 111);
        Assert.Equal(paused + 1, sought);                                               // a seek while paused
        var other = seq.Next(r with { Key = "video:b", PositionSeconds = 0 }, 112);
        Assert.Equal(sought + 1, other);                                                // a new clip
        Assert.Equal(other + 1, seq.Next(null, 113));                                   // the clip left the air
        Assert.Equal(other + 1, seq.Next(null, 114));
        var fast = new VideoSequence();
        var x = fast.Next(r with { Rate = 2 }, 0);
        Assert.Equal(x, fast.Next(r with { Rate = 2, PositionSeconds = 14 }, 2));       // twice the speed is expected, not a jump
    }

    [Fact]
    public void FeedbackCarriesThePauseAndTheSequence()
    {
        var json = "{\"video\":{\"file\":\"a.mp4\",\"position\":62,\"length\":210,\"remaining\":148,\"text\":\"VT a.mp4\",\"out\":false,\"paused\":true,\"seq\":7}}";
        var msgs = OscFeedback.FromState(json);
        Assert.Contains(msgs, m => m.Address == "/patterns/state/video/paused" && Equals(m.Args[0], 1));
        Assert.Contains(msgs, m => m.Address == "/patterns/state/video/seq" && Equals(m.Args[0], 7));
        var none = OscFeedback.FromState("{\"video\":null}");
        Assert.Contains(none, m => m.Address == "/patterns/state/video/paused" && Equals(m.Args[0], 0));
    }

    [Theory]
    [InlineData("/patterns/video/play", "VIDEO PLAY")]
    [InlineData("/patterns/vt/pause", "VIDEO PAUSE")]
    [InlineData("/patterns/video/stop", "VIDEO STOP")]
    [InlineData("/patterns/video/seek/83", "VIDEO SEEK 83")]
    [InlineData("/patterns/video/seek/end", "VIDEO SEEK END")]
    [InlineData("/patterns/video/seek/end/5", "VIDEO SEEK END 5")]
    public void OscMirrorsTheTransport(string address, string line)
        => Assert.Equal(line, OscMap.ToLine(OscMessage.Of(address)));

    [Fact]
    public void OscSeekTakesItsPointAsAnArgumentAndRefusesAWord()
    {
        Assert.Equal("VIDEO SEEK 83", OscMap.ToLine(OscMessage.Of("/patterns/video/seek", 83)));
        Assert.Equal("VIDEO SEEK 1:23", OscMap.ToLine(OscMessage.Of("/patterns/video/seek", "1:23")));
        Assert.Equal("VIDEO SEEK END 5", OscMap.ToLine(OscMessage.Of("/patterns/video/seek/end", 5)));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/video/seek", "soon")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/video/seek")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/video/seek"));
    }

    [Fact]
    public void TheCueSideKnowsTheTransport()
    {
        Assert.Equal(ShowActionKind.VideoPause, CueSheet.ParseKind("pause video"));
        Assert.Equal(ShowActionKind.VideoStop, CueSheet.ParseKind("video stop"));
        Assert.Equal(ShowActionKind.VideoSeek, CueSheet.ParseKind("Video — move to a point"));
        Assert.Contains(ShowActionKind.VideoSeek, ActionSpec.CueKinds);
        Assert.Equal((TargetKind.None, ValueKind.ClipPoint), ActionSpec.For(ShowActionKind.VideoSeek));
        Assert.Equal("Video: to 1:23", CueSummary.DescribeAction(new ShowState(), new CueActionConfig { Kind = ShowActionKind.VideoSeek, Value = "1:23" }));
        Assert.Equal("Video: to 5 s before its end", CueSummary.DescribeAction(new ShowState(), new CueActionConfig { Kind = ShowActionKind.VideoSeek, Value = "END 5" }));
    }

    [Fact]
    public void TheHelloSaysWhoAndWhereNeverWhatTheShowIs()
    {
        var hello = new ShowBusHello { Id = "ab12cd34", Name = "FOH", Machine = "FOH-PC", Web = 9696, Wire = 9697, Role = "desk" };
        using var doc = JsonDocument.Parse(hello.ToJson());
        var root = doc.RootElement;
        Assert.Equal("patterns", root.GetProperty("t").GetString());
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("ab12cd34", root.GetProperty("id").GetString());
        Assert.Equal(9697, root.GetProperty("wire").GetInt32());
        Assert.Equal(9696, root.GetProperty("web").GetInt32());
        Assert.Equal(1, root.GetProperty("proto").GetInt32());
        Assert.Contains("video.seek", root.GetProperty("caps").EnumerateArray().Select(e => e.GetString()));
        var names = root.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "t", "v", "id", "name", "machine", "web", "wire", "role", "caps", "proto" }, names);
        Assert.True(hello.ToBytes().Length < 1024);
        Assert.Equal(9928, ShowBusHello.Port);
    }

    [Theory]
    [InlineData(true, "", true)]
    [InlineData(true, "0.0.0.0", true)]
    [InlineData(true, "192.168.10.5", true)]
    [InlineData(true, "127.0.0.1", false)]
    [InlineData(true, "::1", false)]
    [InlineData(true, "localhost", false)]
    [InlineData(false, "", false)]
    public void TheHelloGoesOutOnlyForAWireTheNetworkCanReach(bool enabled, string bind, bool expected)
        => Assert.Equal(expected, ShowBusHello.MayAnnounce(enabled, bind));
}
