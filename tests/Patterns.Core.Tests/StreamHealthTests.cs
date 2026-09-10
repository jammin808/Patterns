using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The stream's health, read the same way everywhere it is shown. The rule that matters: a stream
/// that is up but not keeping up must not read as live — the wall looks perfect while the online
/// audience watches a slideshow, and that is the failure an operator cannot see from the desk.
/// </summary>
public class StreamHealthTests
{
    private static StreamFacts Live(double fps = 30, int target = 30, double up = 60, int restarts = 0) => new(
        Wanted: true, Configured: true, Destinations: 2, Encoding: true, Starting: false,
        Frames: 1800, Fps: fps, TargetFps: target, Restarts: restarts, Trouble: "", UpSeconds: up);

    [Fact]
    public void NobodyAskedForItSoNothingIsWrong()
    {
        var off = StreamHealth.Read(StreamFacts.None);
        Assert.Equal(StreamLight.Off, off.Light);
        Assert.Equal("OFF", off.Word);
        Assert.False(off.IsTrouble);
        Assert.False(off.IsOnAir);
        Assert.Contains("no destination", off.Line);

        var ready = StreamHealth.Read(StreamFacts.None with { Configured = true, Destinations = 1 });
        Assert.Equal(StreamLight.Off, ready.Light);
        Assert.Contains("1 destination ready", ready.Line);
    }

    [Fact]
    public void AskedForWithNowhereToSendItReadsAsAFault()
    {
        var h = StreamHealth.Read(StreamFacts.None with { Wanted = true });
        Assert.Equal(StreamLight.Failed, h.Light);
        Assert.Equal("NO DEST", h.Word);
        Assert.True(h.IsTrouble);
        Assert.Contains("nowhere to send it", h.Line);
    }

    [Fact]
    public void ComingUpIsNotAFaultAndTheEncodersWordsAre()
    {
        var starting = StreamHealth.Read(Live() with { Starting = true, Frames = 0, UpSeconds = 1 });
        Assert.Equal(StreamLight.Starting, starting.Light);
        Assert.True(starting.IsOnAir);
        Assert.False(starting.IsTrouble);

        var failed = StreamHealth.Read(Live() with { Trouble = "the encoder failed 3 times in a short window" });
        Assert.Equal(StreamLight.Failed, failed.Light);
        Assert.Equal("FAULT", failed.Word);
        Assert.Contains("failed 3 times", failed.Line);
        Assert.False(failed.IsOnAir);
    }

    [Fact]
    public void UpButNotKeepingUpIsNotLive()
    {
        // Half the rate asked for: the wall is fine, the stream is not.
        var slow = StreamHealth.Read(Live(fps: 15));
        Assert.Equal(StreamLight.Strained, slow.Light);
        Assert.Equal("SLOW", slow.Word);
        Assert.True(slow.IsTrouble);
        Assert.True(slow.IsOnAir);
        Assert.Contains("15 of 30 fps", slow.Line);
        Assert.Contains("The wall is fine", slow.Line);

        // A little under is still live: an encoder is never exactly on the number.
        Assert.Equal(StreamLight.Live, StreamHealth.Read(Live(fps: 29)).Light);
        Assert.Equal(StreamLight.Live, StreamHealth.Read(Live(fps: 30)).Light);

        // And in the first seconds a low rate is a stream settling, not a stream failing.
        Assert.Equal(StreamLight.Live, StreamHealth.Read(Live(fps: 8, up: 2)).Light);
    }

    [Fact]
    public void AnEncoderThatRestartedSaysSoEvenWhileItIsKeepingUp()
    {
        var h = StreamHealth.Read(Live(restarts: 2));
        Assert.Equal(StreamLight.Strained, h.Light);
        Assert.Equal("LIVE*", h.Word);
        Assert.Contains("restarted 2 times", h.Line);

        var clean = StreamHealth.Read(Live());
        Assert.Equal(StreamLight.Live, clean.Light);
        Assert.Equal("LIVE", clean.Word);
        Assert.False(clean.IsTrouble);
        Assert.Contains("Live to 2 destinations", clean.Line);
    }

    [Fact]
    public void EveryLightHasAColourAndAnUptimeAnOperatorCanRead()
    {
        foreach (var light in Enum.GetValues<StreamLight>())
        {
            var facts = light switch
            {
                StreamLight.Off => StreamFacts.None,
                StreamLight.Failed => Live() with { Trouble = "no" },
                StreamLight.Starting => Live() with { Frames = 0 },
                StreamLight.Strained => Live(fps: 1),
                _ => Live(),
            };
            var h = StreamHealth.Read(facts);
            Assert.Equal(light, h.Light);
            Assert.StartsWith("#", h.Hue);
            Assert.NotEqual("", h.Word);
        }

        Assert.Equal("", StreamHealth.Duration(0.4));
        Assert.Equal("18 s", StreamHealth.Duration(18));
        Assert.Equal("3 m 12 s", StreamHealth.Duration(192));
        Assert.Equal("1 h 04 m", StreamHealth.Duration(3840));
    }
}
