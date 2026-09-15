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
}
