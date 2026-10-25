using System.Net;
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
    public void TheHeadCarriesOriginHostAndFetchSiteAndTheCookiesTokenWhenNoHeaderHasOne()
    {
        var head = HttpHead.Parse("POST /api/cmd HTTP/1.1\r\nHost: desk:9696\r\nOrigin: http://evil.example\r\nSec-Fetch-Site: cross-site\r\nCookie: a=1; patterns-token=K7QM-3XWD-P9RA; b=2\r\n", HttpLimits.Control);
        Assert.True(head.Ok);
        Assert.Equal("desk:9696", head.Host);
        Assert.Equal("http://evil.example", head.Origin);
        Assert.Equal("cross-site", head.SecFetchSite);
        Assert.Equal("K7QM-3XWD-P9RA", head.Token);
        Assert.True(head.CrossSite);

        var both = HttpHead.Parse("GET /api/state HTTP/1.1\r\nHost: desk\r\nX-Patterns-Token: FROM-HEADER\r\nCookie: patterns-token=FROM-COOKIE\r\n", HttpLimits.Control);
        Assert.Equal("FROM-HEADER", both.Token);                                    // the header first, the cookie is the img tags' way
        Assert.False(both.CrossSite);

        var bare = HttpHead.Parse("GET / HTTP/1.1\r\nHost: desk\r\n", HttpLimits.Control);
        Assert.Equal("", bare.Token);
        Assert.Equal("", bare.Origin);
        Assert.False(bare.CrossSite);

        Assert.Equal("K7", HttpHead.CookieToken("x=1; patterns-token = K7 ; y=2"));
        Assert.Equal("", HttpHead.CookieToken("patterns-tokenx=1; =2; nothing"));

        // Round 89: the head says when the token is the cookie's — one the browser attached by itself — and when a header carried it.
        Assert.True(head.TokenFromCookie);
        Assert.False(both.TokenFromCookie);
        Assert.False(bare.TokenFromCookie);
        Assert.False(HttpHead.Parse("GET / HTTP/1.1\r\nHost: desk\r\nX-Patterns-Token: T\r\n", HttpLimits.Control).TokenFromCookie);
    }

    [Fact]
    public void ASecondHostHeaderMakesTheHeadNameNothing()
    {
        Assert.Equal("localhost:9696", HttpHead.Parse("GET / HTTP/1.1\r\nHost: localhost:9696\r\n", HttpLimits.Control).Host);
        var twice = HttpHead.Parse("GET / HTTP/1.1\r\nHost: localhost:9696\r\nhost: evil.example:9696\r\n", HttpLimits.Control);
        Assert.True(twice.Ok);
        Assert.Equal("", twice.Host);
        var thrice = HttpHead.Parse("GET / HTTP/1.1\r\nHost: evil.example\r\nHost: localhost:9696\r\nHost: localhost:9696\r\n", HttpLimits.Control);
        Assert.Equal("", thrice.Host);
        Assert.False(DeskNames.Named(twice.Host, new IPEndPoint(IPAddress.Loopback, 9696), tls: false, Array.Empty<string>()));
    }

    private static readonly IPEndPoint OverLoopback = new(IPAddress.Loopback, 9696);
    private static readonly IPEndPoint OnTheLan = new(IPAddress.Parse("10.0.0.5"), 9696);
    private static readonly IReadOnlyList<string> Machine = DeskNames.Of("FOH-PC", "foh-pc", new[] { "venue.lan" }, "FOH PC");

    [Fact]
    public void LoopbackNamesAndLoopbackAddressesNameThisDeskOnlyOverLoopbackAndOnlyWithTheirPort()
    {
        foreach (var host in new[] { "localhost:9696", "LOCALHOST:9696", "127.0.0.1:9696", "127.0.0.2:9696", "[::1]:9696", "[::ffff:127.0.0.1]:9696", " localhost:9696 " })
        {
            Assert.True(DeskNames.Named(host, OverLoopback, tls: false, Machine), host);
            Assert.False(DeskNames.Named(host, OnTheLan, tls: false, Machine), host);              // another machine's tunnel says localhost too
        }
        Assert.True(DeskNames.Named("[::1]:9696", new IPEndPoint(IPAddress.IPv6Loopback, 9696), tls: false, Machine));
        Assert.True(DeskNames.Named("localhost:9696", new IPEndPoint(IPAddress.Parse("::ffff:127.0.0.1"), 9696), tls: false, Machine));
        foreach (var port in new[] { "localhost:9697", "localhost:80", "localhost", "localhost:", "localhost:09696", "localhost:+9696", "localhost:96960", "localhost:9696:1" })
            Assert.False(DeskNames.Named(port, OverLoopback, tls: false, Machine), port);
        // No port is the scheme's: 80 in plain, 443 over TLS.
        Assert.True(DeskNames.Named("localhost", new IPEndPoint(IPAddress.Loopback, 80), tls: false, Machine));
        Assert.False(DeskNames.Named("localhost", new IPEndPoint(IPAddress.Loopback, 80), tls: true, Machine));
        Assert.True(DeskNames.Named("localhost", new IPEndPoint(IPAddress.Loopback, 443), tls: true, Machine));
        Assert.True(DeskNames.Named("localhost:443", new IPEndPoint(IPAddress.Loopback, 443), tls: true, Machine));
    }

    [Fact]
    public void ARequestNamesThisDeskByTheAddressItArrivedOnOrTheMachinesOwnNamesAndNothingElse()
    {
        Assert.True(DeskNames.Named("10.0.0.5:9696", OnTheLan, tls: false, Machine));
        Assert.True(DeskNames.Named("10.0.0.5:9696", new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.5"), 9696), tls: false, Machine));
        Assert.False(DeskNames.Named("10.0.0.6:9696", OnTheLan, tls: false, Machine));            // an address this connection did not arrive on: a forwarder's
        Assert.False(DeskNames.Named("10.0.0.5:9696", OverLoopback, tls: false, Machine));        // carried in over loopback from somewhere else
        var v6 = new IPEndPoint(IPAddress.Parse("fe80::1%3"), 9696);
        Assert.True(DeskNames.Named("[fe80::1]:9696", v6, tls: false, Machine));
        Assert.False(DeskNames.Named("[fe80::2]:9696", v6, tls: false, Machine));

        foreach (var host in new[] { "foh-pc:9696", "FOH-PC:9696", "foh-pc.local:9696", "foh-pc.venue.lan:9696", "foh-pc-patterns.local:9696" })
        {
            Assert.True(DeskNames.Named(host, OnTheLan, tls: false, Machine), host);
            Assert.True(DeskNames.Named(host, OverLoopback, tls: false, Machine), host);
        }

        // A rebound name, a forwarder's name, an absent Host, and the forms no browser sends: none names this desk.
        foreach (var host in new[] { "evil.example:9696", "foh-pc.evil.example:9696", "localhost.:9696", "foh-pc.:9696", "", "   ", ":9696", "[::1", "[::1]x", "[10.0.0.5]:9696",
                                     "127.1:9696", "2130706433:9696", "0x7f000001:9696", "127.0.0.01:9696", "::1:9696", "10.0.0.5.:9696", "foh-pc:9696/path" })
        {
            Assert.False(DeskNames.Named(host, OverLoopback, tls: false, Machine), host);
            Assert.False(DeskNames.Named(host, OnTheLan, tls: false, Machine), host);
        }
        Assert.False(DeskNames.Named("localhost:9696", null, tls: false, Machine));                // no endpoint to compare with: nothing is named
    }

    [Fact]
    public void TheMachinesNamesAreItsNameAndHostNameUnderLocalAndItsSuffixAndTheResponderName()
    {
        Assert.Equal(new[] { "foh-pc", "foh-pc.local", "foh-pc.venue.lan", "foh-pc-patterns.local" }, DeskNames.Of("FOH-PC", "foh-pc", new[] { ".Venue.LAN." }, "FOH PC"));
        Assert.Equal(new[] { "foh-pc", "foh-pc.local", "stage-laptop", "stage-laptop.local", "booth-patterns.local" }, DeskNames.Of("FOH-PC", "stage-laptop", new[] { "" }, "Booth"));
        Assert.Equal(new[] { "foh-pc", "foh-pc.local" }, DeskNames.Of("FOH-PC", "", new[] { " " }, ""));
        Assert.DoesNotContain("localhost", DeskNames.Of("FOH-PC", "foh-pc", Array.Empty<string>(), "FOH-PC"));       // localhost names the desk only over loopback, never as a machine name
        Assert.Equal(DnsSd.Dotted(new MdnsAdvert("desk", "FOH PC", "", 0, 0, 0, "", "", Array.Empty<IPAddress>()).HostName), DeskNames.Of("", "", Array.Empty<string>(), "FOH PC")[0]);
    }

    [Fact]
    public void EachDnsSuffixTheMachineHasNamesItTheOneItsRouterHandsOutIncluded()
    {
        // The primary suffix and each live interface's own (a venue's .lan, a home router's .fritz.box): each once, in order,
        // ".local" and empty ones skipped — the machine's names under each.
        var names = DeskNames.Of("FOH-PC", "foh-pc", new[] { "corp.example", "", "LAN", ".fritz.box.", "lan", "local", " " }, "");
        Assert.Equal(new[] { "foh-pc", "foh-pc.local", "foh-pc.corp.example", "foh-pc.lan", "foh-pc.fritz.box" }, names);

        // A tablet that opened the desk by its router's name names it, on the LAN and over loopback alike; another suffix does not.
        foreach (var host in new[] { "foh-pc.lan:9696", "FOH-PC.Fritz.Box:9696", "foh-pc.corp.example:9696" })
            Assert.True(DeskNames.Named(host, OnTheLan, tls: false, names), host);
        Assert.False(DeskNames.Named("foh-pc.home:9696", OnTheLan, tls: false, names));
        Assert.False(DeskNames.Named("foh-pc.lan.evil.example:9696", OnTheLan, tls: false, names));
    }

    [Fact]
    public void AnHttpRequestLineIsNeverALineOfTheWire()
    {
        // What a browser's page sends first when it posts to the wire's port: closed on, before its body is read as a line.
        foreach (var line in new[] { "POST / HTTP/1.1", "GET /api/state HTTP/1.0", "OPTIONS * HTTP/1.1", "PRI * HTTP/2.0", " POST /x?y=1 HTTP/1.1 ", "PUT / HTTP/2" })
            Assert.True(ControlProtocol.IsHttpRequestLine(line), line);
        // Every line of the wire, and the shapes that only look like one, are not.
        foreach (var line in new[] { "BLACKOUT ON", "AUTH K7QM-3XWD-P9RA", "HELLO FOH deck module=3.18.1", "@a1 STATUS", "TEXT POST / HTTP", "post / HTTP/1.1",
                                     "POST / HTTP/1.1 extra", "POST /", "POST / HTTP/", "POST / HTTP/1.", "POST / HTTP/11", "POST / HTTPS/1.1", "LOOK SET Opening HTTP/1.1 x", "" })
            Assert.False(ControlProtocol.IsHttpRequestLine(line), line);
        Assert.StartsWith("an HTTP request is not a line of the wire", ControlProtocol.HttpOnWire, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalAsksForTheTokenOnlyWhereATokenWouldPairIt()
    {
        // The pages ask for the token on "not paired": a foreign Host or page on a paired desk can still pair with it in its header.
        Assert.StartsWith("not paired", ControlProtocol.ForeignPage, StringComparison.Ordinal);
        Assert.Contains("X-Patterns-Token", ControlProtocol.ForeignPage, StringComparison.Ordinal);
        // An open desk has no token to give: its words for a browser under another name say what to do instead, and never ask.
        Assert.DoesNotContain("not paired", ControlProtocol.ForeignHostOpen, StringComparison.Ordinal);
        Assert.Contains("open the desk as localhost", ControlProtocol.ForeignHostOpen, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostIsShownInTheLogAsPrintableTextOfABoundedLength()
    {
        Assert.Equal("'evil.example:9696'", DeskNames.Shown(" evil.example:9696 "));
        Assert.Equal("no Host", DeskNames.Shown(""));
        Assert.Equal("'a?b'", DeskNames.Shown("a\u0007b"));
        var shown = DeskNames.Shown(new string('x', 500));
        Assert.Equal(64 + 3, shown.Length);
        Assert.EndsWith("…'", shown);
        // The answer such a request hears starts as every refusal for want of the token does, so a page that asks for it on "not paired" still asks.
        Assert.StartsWith("not paired", ControlProtocol.ForeignHost, StringComparison.Ordinal);
        Assert.Contains("X-Patterns-Token", ControlProtocol.ForeignHost, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestIsCrossSiteByItsFetchSiteOrAnOriginThatIsNotThisHostAndNeverWithoutEither()
    {
        Assert.False(HttpHead.IsCrossSite("", "desk:9696", ""));                          // a curl, a device, a script
        Assert.False(HttpHead.IsCrossSite("http://desk:9696", "desk:9696", ""));           // the desk's own page
        Assert.False(HttpHead.IsCrossSite("http://DESK:9696/", "desk:9696", "same-origin"));
        Assert.True(HttpHead.IsCrossSite("http://desk:8080", "desk:9696", ""));            // another app on this host
        Assert.True(HttpHead.IsCrossSite("http://evil.example", "desk:9696", ""));
        Assert.True(HttpHead.IsCrossSite("null", "desk:9696", ""));                       // a file: or sandboxed page
        Assert.True(HttpHead.IsCrossSite("", "desk:9696", "cross-site"));
        Assert.True(HttpHead.IsCrossSite("", "desk:9696", "same-site"));
        Assert.False(HttpHead.IsCrossSite("", "desk:9696", "none"));                      // a typed address, a bookmark

        // Round 89: a browser is known by either header, whatever it says; a curl, a device or a Companion sends neither.
        Assert.True(HttpHead.Parse("GET / HTTP/1.1\r\nHost: desk\r\nSec-Fetch-Site: none\r\n", HttpLimits.Control).FromBrowser);
        Assert.True(HttpHead.Parse("GET / HTTP/1.1\r\nHost: desk\r\nSec-Fetch-Site: same-origin\r\n", HttpLimits.Control).FromBrowser);
        Assert.True(HttpHead.Parse("POST / HTTP/1.1\r\nHost: desk\r\nOrigin: http://desk\r\n", HttpLimits.Control).FromBrowser);
        Assert.False(HttpHead.Parse("POST / HTTP/1.1\r\nHost: desk\r\nX-Patterns-Client: tool\r\n", HttpLimits.Control).FromBrowser);
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
