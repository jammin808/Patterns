using Patterns.Devices;
using System.Net;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// DNS-SD as bytes and rules: a packet round-trips its wire form, compression pointers are followed
/// on the way in, the advert is the records a Companion's browser expects, a query about this
/// process is answered and one about another is not, and the browser turns a Companion's own
/// announcement into a peer — and forgets it on a goodbye.
/// </summary>
public class MdnsTests
{
    private static MdnsAdvert Advert() => new("desk", "FOH-PC", "Gala", 9697, 9696, 9700, "a1b2c3d4", "1.9.0", new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Parse("192.168.7.2") });

    [Fact]
    public void ThePacketRoundTripsItsWireForm()
    {
        var advert = Advert();
        var bytes = advert.Announcement().ToBytes();
        var back = DnsPacket.Parse(bytes);
        Assert.NotNull(back);
        Assert.True(back!.IsResponse);
        Assert.Empty(back.Questions);
        Assert.Equal(6, back.Answers.Count);
        var ptr = back.Answers[0];
        Assert.Equal(DnsSd.TypePtr, ptr.Type);
        Assert.Equal("_patterns._tcp.local", DnsSd.Dotted(ptr.Name));
        Assert.Equal("Patterns desk FOH-PC._patterns._tcp.local", DnsSd.Dotted(ptr.Target!));
        Assert.False(ptr.Flush, "a PTR is shared");
        var srv = back.Answers[1];
        Assert.Equal(DnsSd.TypeSrv, srv.Type);
        Assert.Equal(9697, srv.Port);
        Assert.Equal("foh-pc-patterns.local", DnsSd.Dotted(srv.Target!));
        Assert.True(srv.Flush, "an SRV is this responder's alone");
        Assert.Equal(120u, srv.Ttl);
        var txt = back.Answers[2].TextPairs();
        Assert.Equal("desk", txt["kind"]);
        Assert.Equal("FOH-PC", txt["name"]);
        Assert.Equal("Gala", txt["show"]);
        Assert.Equal("9697", txt["wire"]);
        Assert.Equal("9696", txt["http"]);
        Assert.Equal("a1b2c3d4", txt["instance"]);
        Assert.Equal("1.9.0", txt["version"]);
        Assert.Equal(new[] { "10.0.0.5", "192.168.7.2" }, back.Answers.Where(r => r.Type == DnsSd.TypeA).Select(r => r.Address!.ToString()));
        Assert.Equal("_services._dns-sd._udp.local", DnsSd.Dotted(back.Answers[5].Name));
        // Goodbye: the same, with nothing to keep.
        Assert.All(advert.Goodbye().Answers, r => Assert.Equal(0u, r.Ttl));
        // A query round-trips too, and a broken datagram is nobody's packet.
        var q = DnsPacket.Parse(DnsPacket.Query(MdnsAdvert.ServiceName, DnsSd.TypePtr).ToBytes())!;
        Assert.False(q.IsResponse);
        Assert.Equal("_patterns._tcp.local", DnsSd.Dotted(Assert.Single(q.Questions).Name));
        Assert.Null(DnsPacket.Parse(new byte[] { 1, 2, 3 }));
        Assert.Null(DnsPacket.Parse(bytes.AsSpan(0, 40)));
    }

    [Fact]
    public void CompressionPointersAreFollowedAndLoopsRefused()
    {
        // A response as a compressing responder writes it: the PTR target repeats the question's name by a pointer.
        var packet = new List<byte>();
        void U16(int v) { packet.Add((byte)(v >> 8)); packet.Add((byte)v); }
        void Label(string l) { packet.Add((byte)l.Length); packet.AddRange(System.Text.Encoding.ASCII.GetBytes(l)); }
        U16(0); U16(0x8400); U16(0); U16(1); U16(0); U16(0);
        var nameAt = packet.Count;
        Label("_patterns"); Label("_tcp"); Label("local"); packet.Add(0);
        U16(DnsSd.TypePtr); U16(DnsSd.ClassIn); packet.AddRange(new byte[] { 0, 0, 0, 120 });
        var data = new List<byte>();
        data.Add(4); data.AddRange(System.Text.Encoding.ASCII.GetBytes("Desk"));
        data.Add((byte)(0xC0 | (nameAt >> 8))); data.Add((byte)nameAt);   // "Desk" + pointer to _patterns._tcp.local
        U16(data.Count); packet.AddRange(data);
        var parsed = DnsPacket.Parse(packet.ToArray());
        Assert.NotNull(parsed);
        Assert.Equal("Desk._patterns._tcp.local", DnsSd.Dotted(Assert.Single(parsed!.Answers).Target!));

        // A pointer to itself is a loop, and the packet is refused rather than spun on.
        var loop = packet.ToArray();
        var pointerAt = loop.Length - 2;
        loop[pointerAt] = (byte)(0xC0 | (pointerAt >> 8));
        loop[pointerAt + 1] = (byte)pointerAt;
        Assert.Null(DnsPacket.Parse(loop));
    }

    [Fact]
    public void AQueryAboutThisProcessIsAnsweredAndOneAboutAnotherIsNot()
    {
        var advert = Advert();
        var browse = MdnsResponder.Answer(DnsPacket.Query(MdnsAdvert.ServiceName, DnsSd.TypePtr), advert);
        Assert.NotNull(browse);
        Assert.Equal(DnsSd.TypePtr, Assert.Single(browse!.Answers).Type);
        Assert.Equal(new[] { DnsSd.TypeSrv, DnsSd.TypeTxt, DnsSd.TypeA, DnsSd.TypeA }, browse.Additionals.Select(r => r.Type));

        var enumeration = MdnsResponder.Answer(DnsPacket.Query(DnsSd.ServiceEnumeration, DnsSd.TypePtr), advert);
        Assert.Equal("_patterns._tcp.local", DnsSd.Dotted(Assert.Single(enumeration!.Answers).Target!));

        var resolve = MdnsResponder.Answer(DnsPacket.Query(advert.InstanceName, DnsSd.TypeSrv), advert);
        Assert.Equal(9697, Assert.Single(resolve!.Answers).Port);
        Assert.Equal(2, resolve.Additionals.Count);

        var any = MdnsResponder.Answer(DnsPacket.Query(new[] { "patterns DESK foh-pc", "_PATTERNS", "_tcp", "LOCAL" }, DnsSd.TypeAny), advert);
        Assert.Equal(new[] { DnsSd.TypeSrv, DnsSd.TypeTxt }, any!.Answers.Select(r => r.Type));

        var host = MdnsResponder.Answer(DnsPacket.Query(advert.HostName, DnsSd.TypeA), advert);
        Assert.Equal(2, host!.Answers.Count);

        Assert.Null(MdnsResponder.Answer(DnsPacket.Query(DnsSd.Labels("_companion-satellite-tcp._tcp.local"), DnsSd.TypePtr), advert));
        Assert.Null(MdnsResponder.Answer(DnsPacket.Query(DnsSd.Labels("other._patterns._tcp.local"), DnsSd.TypeSrv), advert));
        Assert.True(MdnsResponder.Answer(advert.Announcement(), advert) is null, "a response is never answered");
    }

    [Fact]
    public void TheInstanceNameSaysTheKindAndTheHostLabelIsSafe()
    {
        Assert.Equal("Patterns stage timer STAGE-PI", (Advert() with { Kind = "timer", Machine = "STAGE-PI" }).InstanceLabel);
        Assert.Equal("Patterns caller FOH-CALL", (Advert() with { Kind = "caller", Machine = "FOH-CALL" }).InstanceLabel);
        Assert.Equal("Patterns arcade HUB", (Advert() with { Kind = "arcade", Machine = "HUB" }).InstanceLabel);
        Assert.Equal("foh-pc-patterns.local", DnsSd.Dotted(Advert().HostName));
        Assert.Equal("show-desk-2", DnsSd.HostLabel("Show Desk #2"));
        Assert.Equal("patterns", DnsSd.HostLabel("###"));
        Assert.NotEqual(Advert().Signature, (Advert() with { Show = "Awards" }).Signature);
    }

    [Fact]
    public void TheBrowserTurnsACompanionsAnnouncementIntoAPeerAndForgetsItOnGoodbye()
    {
        var browser = new MdnsBrowser(CompanionModule.SatelliteServiceType);
        Assert.Equal("_companion-satellite-tcp._tcp.local", DnsSd.Dotted(browser.ServiceName));
        var service = browser.ServiceName;
        var instance = new[] { "Companion (FOH-PC)", "_companion-satellite-tcp", "_tcp", "local" };
        var host = new[] { "foh-pc", "local" };
        var now = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        var announced = DnsPacket.Response(new[]
        {
            DnsRecord.Ptr(service, instance, 4500),
            DnsRecord.Srv(instance, host, 16622, 120),
            DnsRecord.Txt(instance, new[] { "id=abc", "version=5.0.3", "protocolVersion=1.14.0" }, 4500),
            DnsRecord.A(host, IPAddress.Parse("10.0.0.7"), 120),
        });
        Assert.True(browser.Hear(DnsPacket.Parse(announced.ToBytes())!, IPAddress.Parse("10.0.0.7"), now));
        var peer = Assert.Single(browser.Peers(now));
        Assert.Equal("Companion (FOH-PC)", peer.Instance);
        Assert.Equal("10.0.0.7", peer.Address!.ToString());
        Assert.Equal(16622, peer.Port);
        Assert.Equal("5.0.3", peer.Txt["version"]);
        Assert.Equal("Companion (FOH-PC) at 10.0.0.7:16622 · v5.0.3", peer.Line);

        // Another type's announcement is not a peer; a PTR alone is a peer at the sender's address until the SRV comes.
        Assert.False(browser.Hear(Advert().Announcement(), IPAddress.Parse("10.0.0.5"), now));
        var ptrOnly = DnsPacket.Response(new[] { DnsRecord.Ptr(service, new[] { "Companion (STAGE)", "_companion-satellite-tcp", "_tcp", "local" }, 4500) });
        Assert.True(browser.Hear(ptrOnly, IPAddress.Parse("10.0.0.9"), now));
        var stage = browser.Peers(now).Single(p => p.Instance == "Companion (STAGE)");
        Assert.Equal("10.0.0.9", stage.Address!.ToString());
        Assert.Equal(0, stage.Port);
        var srvLater = DnsPacket.Response(new[] { DnsRecord.Srv(new[] { "Companion (STAGE)", "_companion-satellite-tcp", "_tcp", "local" }, new[] { "stage", "local" }, 16622, 120) });
        browser.Hear(srvLater, IPAddress.Parse("10.0.0.9"), now.AddSeconds(1));
        Assert.Equal(16622, browser.Peers(now).Single(p => p.Instance == "Companion (STAGE)").Port);

        // The TTL runs out; a goodbye is at once.
        Assert.Equal(2, browser.Peers(now.AddSeconds(4000)).Count);
        Assert.Empty(browser.Peers(now.AddSeconds(5000)));
        browser.Hear(announced, IPAddress.Parse("10.0.0.7"), now.AddSeconds(6000));
        Assert.Single(browser.Peers(now.AddSeconds(6000)));
        var goodbye = DnsPacket.Response(new[] { DnsRecord.Ptr(service, instance, 0) });
        browser.Hear(goodbye, IPAddress.Parse("10.0.0.7"), now.AddSeconds(6001));
        Assert.Empty(browser.Peers(now.AddSeconds(6001)));
    }
}
