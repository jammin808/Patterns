using System.Text;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The request head as the ports read it: bounded, and every shape a port can be sent answered
/// with a status rather than a wait — the audience port's half of the trust boundary.
/// </summary>
public class HttpHeadTests
{
    [Fact]
    public void TheBlankLineThatEndsAHeadIsFoundInEitherLineEnding()
    {
        Assert.Equal((17, 4), HttpHead.EndOfHead("GET / HTTP/1.1\r\nA\r\n\r\nbody"u8));
        Assert.Equal((0, 4), HttpHead.EndOfHead("\r\n\r\n"u8));
        Assert.Equal((5, 2), HttpHead.EndOfHead("GET /\n\nrest"u8));
        Assert.Equal((-1, 0), HttpHead.EndOfHead("GET / HTTP/1.1\r\nHost: a\r\n"u8));
        Assert.Equal((-1, 0), HttpHead.EndOfHead(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void AWellFormedHeadParsesWhateverItsLineEndings()
    {
        var head = HttpHead.Parse("POST /api/play/join HTTP/1.1\r\nHost: 10.0.0.1\r\ncontent-length: 12\r\nX-Patterns-Client: desk\r\n", HttpLimits.Audience);
        Assert.True(head.Ok);
        Assert.Equal("POST", head.Method);
        Assert.Equal("/api/play/join", head.Path);
        Assert.Equal(12, head.ContentLength);
        Assert.True(head.ClientHeader);
        Assert.Equal(3, head.HeaderCount);
        var lf = HttpHead.Parse("GET /play?x=1 HTTP/1.0\nHost: a\n", HttpLimits.Control);
        Assert.True(lf.Ok);
        Assert.Equal("/play?x=1", lf.Path);
        Assert.Equal(0, lf.ContentLength);
        Assert.False(lf.ClientHeader);
        var bytes = HttpHead.Parse(Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\n"), HttpLimits.Audience);
        Assert.True(bytes.Ok);
        Assert.Equal("/", bytes.Path);
    }

    [Fact]
    public void EveryShapeThatIsNotARequestIsAStatusNotAWait()
    {
        var limits = HttpLimits.Audience;
        foreach (var bad in new[] { "", "GET", "get / HTTP/1.1", "GET play HTTP/1.1", "\0ÿ garbage", "GET / HTTP/1.1\r\nno colon here\r\n", "GET / HTTP/1.1\r\n: empty name\r\n" })
        {
            var head = HttpHead.Parse(bad, limits);
            Assert.False(head.Ok, bad);
            Assert.Equal("400 Bad Request", head.Status);
            Assert.NotEqual("", head.Fault);
        }
        Assert.Equal("400 Bad Request", HttpHead.Parse("POST / HTTP/1.1\r\nContent-Length: abc\r\n", limits).Status);
        Assert.Equal("400 Bad Request", HttpHead.Parse("POST / HTTP/1.1\r\nContent-Length: -1\r\n", limits).Status);
        var big = HttpHead.Parse("POST / HTTP/1.1\r\nContent-Length: 999999999\r\n", limits);
        Assert.Equal("413 Content Too Large", big.Status);
        Assert.Contains("999999999", big.Fault);
        Assert.Contains(limits.MaxBodyBytes.ToString(), big.Fault);
        Assert.True(HttpHead.Parse($"POST / HTTP/1.1\r\nContent-Length: {limits.MaxBodyBytes}\r\n", limits).Ok);   // at the limit is fine
        var many = new StringBuilder("GET / HTTP/1.1\r\n");
        for (var i = 0; i <= limits.MaxHeaderLines; i++) many.Append($"H{i}: v\r\n");
        var crowded = HttpHead.Parse(many.ToString(), limits);
        Assert.Equal("431 Request Header Fields Too Large", crowded.Status);
        Assert.Equal("too many headers", crowded.Fault);
    }

    [Fact]
    public void TheAudiencePortsLimitsAreTighterThanTheControlPorts()
    {
        Assert.True(HttpLimits.Audience.MaxHeadBytes < HttpLimits.Control.MaxHeadBytes);
        Assert.True(HttpLimits.Audience.MaxBodyBytes < HttpLimits.Control.MaxBodyBytes);
        Assert.True(HttpLimits.Audience.MaxHeaderLines < HttpLimits.Control.MaxHeaderLines);
        Assert.True(HttpLimits.Audience.HeadSeconds < HttpLimits.Control.HeadSeconds);
        Assert.True(HttpLimits.Audience.BodySeconds <= HttpLimits.Control.BodySeconds);
        Assert.True(HttpLimits.Audience.MaxBodyBytes >= 4096);        // a phone's answer, with room to spare
    }
}
