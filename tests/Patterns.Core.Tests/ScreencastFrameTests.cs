using System.Text;
using Patterns.Core.Media;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The browser's screencast as the frame source reads it: the parse, the ack, the bytes and the rate — no browser needed.</summary>
public class ScreencastFrameTests
{
    private static string Event(string base64, int sessionId = 7)
        => "{\"data\":\"" + base64 + "\",\"metadata\":{\"offsetTop\":0,\"pageScaleFactor\":1,\"deviceWidth\":1920,\"deviceHeight\":1080,\"scrollOffsetX\":0,\"scrollOffsetY\":0,\"timestamp\":1726000000.5},\"sessionId\":" + sessionId + "}";

    [Fact]
    public void AFrameEventParsesToItsSessionIdAndItsPicture()
    {
        var jpeg = Encoding.ASCII.GetBytes("not really a jpeg, but bytes all the same");
        var json = Event(Convert.ToBase64String(jpeg), sessionId: 42);

        Assert.True(ScreencastFrame.TryParse(json, out var id, out var range));
        Assert.Equal(42, id);

        var rented = ScreencastFrame.Rent(json, range, out var length);
        Assert.NotNull(rented);
        Assert.Equal(jpeg, rented!.AsSpan(0, length).ToArray());
        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
    }

    [Fact]
    public void TheAckNamesTheSessionAndTheStartAsksForJpegAtThePagesSize()
    {
        Assert.Equal("{\"sessionId\":42}", ScreencastFrame.AckParameters(42));
        var start = ScreencastFrame.StartParameters(1280, 720);
        Assert.Contains("\"format\":\"jpeg\"", start);
        Assert.Contains("\"quality\":70", start);
        Assert.Contains("\"maxWidth\":1280", start);
        Assert.Contains("\"maxHeight\":720", start);
        Assert.Contains("\"everyNthFrame\":1", start);
        // A quality outside the protocol's range is clamped, never sent as-is.
        Assert.Contains("\"quality\":100", ScreencastFrame.StartParameters(100, 100, quality: 250));
        // Round 68's capture plan: a smaller box and every second frame when the policy asks; the nth is clamped too.
        Assert.Contains("\"everyNthFrame\":2", ScreencastFrame.StartParameters(1280, 720, 60, everyNthFrame: 2));
        Assert.Contains("\"everyNthFrame\":1", ScreencastFrame.StartParameters(1280, 720, 60, everyNthFrame: 0));
        Assert.Contains("\"everyNthFrame\":10", ScreencastFrame.StartParameters(1280, 720, 60, everyNthFrame: 99));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"data\":\"AAAA\"}")]                        // no session id
    [InlineData("{\"sessionId\":3}")]                          // no picture
    [InlineData("{\"data\":\"AAAA\",\"sessionId\":\"three\"}")] // an id that is not a number
    public void AnythingElseIsNotAFrame(string? json)
    {
        Assert.False(ScreencastFrame.TryParse(json, out _, out _));
    }

    [Fact]
    public void TextThatIsNotBase64RentsNothing()
    {
        var json = Event("this is not base64 at all!!", sessionId: 1);
        Assert.True(ScreencastFrame.TryParse(json, out _, out var range));
        Assert.Null(ScreencastFrame.Rent(json, range, out var length));
        Assert.Equal(0, length);
    }

    [Fact]
    public void TheMeterCountsTheLastSecondOnly()
    {
        var meter = new FrameRateMeter();
        var t0 = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc).Ticks;
        Assert.Equal(0, meter.Rate(t0));
        Assert.Equal(0, meter.LastTicks);
        // Thirty frames over one second, then a pause: the rate reads thirty, then falls to nothing.
        for (var i = 0; i < 30; i++) meter.Tick(t0 + i * TimeSpan.TicksPerSecond / 30);
        Assert.Equal(30, meter.Rate(t0 + TimeSpan.TicksPerSecond - 1));
        Assert.Equal(t0 + 29 * TimeSpan.TicksPerSecond / 30, meter.LastTicks);
        Assert.Equal(15, meter.Rate(t0 + TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond / 2 - 1));
        Assert.Equal(0, meter.Rate(t0 + 3 * TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void TheMeterNeverOverflowsItsRing()
    {
        var meter = new FrameRateMeter();
        var t0 = DateTime.UtcNow.Ticks;
        for (var i = 0; i < 2000; i++) meter.Tick(t0 + i * TimeSpan.TicksPerMillisecond);   // 1000 fps for two seconds
        var rate = meter.Rate(t0 + 2000 * TimeSpan.TicksPerMillisecond);
        Assert.True(rate is > 200 and <= 256, $"rate {rate}");                           // the ring's capacity bounds it
    }

    // ---- round 77: a refused start climbs down a ladder and is asked again ---------------------

    [Fact]
    public void ARefusedStartClimbsDownToTheBareAskAndEveryRungIsDistinct()
    {
        var ladder = ScreencastFrame.StartLadder(1280, 720, quality: 60, everyNthFrame: 2);
        Assert.Equal(3, ladder.Count);
        Assert.Equal(ScreencastFrame.StartParameters(1280, 720, 60, 2), ladder[0]);
        Assert.Equal("{\"format\":\"jpeg\",\"quality\":60}", ladder[1]);
        Assert.Equal("{\"format\":\"jpeg\"}", ladder[2]);
        Assert.Equal(ladder.Count, ladder.Distinct().Count());
        Assert.Equal("{\"format\":\"jpeg\",\"quality\":70}", ScreencastFrame.MinimalParameters());
        Assert.Equal("{\"format\":\"jpeg\",\"quality\":100}", ScreencastFrame.MinimalParameters(250));
    }

    [Fact]
    public void TheRetryBacksOffToHalfAMinuteAndSaysWhereItStands()
    {
        Assert.Equal(1000, ScreencastRetry.DelayMs(0));
        Assert.Equal(1000, ScreencastRetry.DelayMs(1));
        Assert.Equal(2000, ScreencastRetry.DelayMs(2));
        Assert.Equal(4000, ScreencastRetry.DelayMs(3));
        Assert.Equal(8000, ScreencastRetry.DelayMs(4));
        Assert.Equal(15000, ScreencastRetry.DelayMs(5));
        Assert.Equal(30000, ScreencastRetry.DelayMs(6));
        Assert.Equal(30000, ScreencastRetry.DelayMs(600));

        var refused = new DateTime(2026, 9, 17, 21, 0, 0, DateTimeKind.Utc).Ticks;
        Assert.False(ScreencastRetry.Due(0, refused, refused + TimeSpan.TicksPerMinute), "never before a refusal");
        Assert.False(ScreencastRetry.Due(3, 0, refused), "a refusal without a clock is not due");
        Assert.False(ScreencastRetry.Due(3, refused, refused + 3999 * TimeSpan.TicksPerMillisecond));
        Assert.True(ScreencastRetry.Due(3, refused, refused + 4000 * TimeSpan.TicksPerMillisecond));
        Assert.True(ScreencastRetry.Due(9, refused, refused + 30 * TimeSpan.TicksPerSecond));

        Assert.Equal("", ScreencastRetry.StatusWords(screencastOn: true, refusals: 0));
        Assert.Equal("screenshot poll (the screencast was refused once; asking again)", ScreencastRetry.StatusWords(false, 1));
        Assert.Equal("screenshot poll (the screencast was refused ×3; asking again)", ScreencastRetry.StatusWords(false, 3));
        Assert.Equal("screencast (after 1 refusal)", ScreencastRetry.StatusWords(true, 1));
        Assert.Equal("screencast (after 4 refusals)", ScreencastRetry.StatusWords(true, 4));
    }
}
