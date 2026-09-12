using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The device profiles, pure: the bytes each box expects for the words a cue says, and what the bytes back mean.</summary>
public class DeviceProfileTests
{
    private static string Text(byte[] frame) => Encoding.UTF8.GetString(frame);

    [Fact]
    public void PjLinkWordsBecomeTheProjectorsCommandsWithACarriageReturn()
    {
        var s = new PjLinkSession("");
        Assert.Equal("%1POWR 1\r", Text(Assert.Single(s.Encode("POWER ON", out var problem))));
        Assert.Equal("", problem);
        Assert.Equal("%1POWR 0\r", Text(Assert.Single(s.Encode("power off", out _))));
        Assert.Equal("%1POWR ?\r", Text(Assert.Single(s.Encode("POWER ?", out _))));
        Assert.Equal("%1INPT 31\r", Text(Assert.Single(s.Encode("INPUT HDMI 1", out _))));
        Assert.Equal("%1INPT 32\r", Text(Assert.Single(s.Encode("INPUT HDMI 2", out _))));
        Assert.Equal("%1INPT 11\r", Text(Assert.Single(s.Encode("INPUT RGB", out _))));
        Assert.Equal("%1INPT 21\r", Text(Assert.Single(s.Encode("INPUT VIDEO 1", out _))));
        Assert.Equal("%1INPT 51\r", Text(Assert.Single(s.Encode("INPUT NETWORK 1", out _))));
        Assert.Equal("%1INPT 33\r", Text(Assert.Single(s.Encode("INPUT 33", out _))));
        Assert.Equal("%1AVMT 31\r", Text(Assert.Single(s.Encode("SHUTTER ON", out _))));
        Assert.Equal("%1AVMT 30\r", Text(Assert.Single(s.Encode("SHUTTER OFF", out _))));
        Assert.Equal("%1AVMT 21\r", Text(Assert.Single(s.Encode("MUTE ON", out _))));
        Assert.Equal("%1LAMP ?\r", Text(Assert.Single(s.Encode("LAMP ?", out _))));
        Assert.Equal("%1ERST ?\r", Text(Assert.Single(s.Encode("ERRORS ?", out _))));
        Assert.Equal("%1NAME ?\r", Text(Assert.Single(s.Encode("NAME ?", out _))));
        Assert.Equal("%1CLSS ?\r", Text(Assert.Single(s.Encode("RAW %1CLSS ?", out _))));
        Assert.Equal("%1POWR ?\r", Text(Assert.Single(s.Encode("%1POWR ?", out _))));
        Assert.Empty(s.Encode("DANCE", out problem));
        Assert.Contains("not a PJLink command", problem);
        Assert.Empty(s.Encode("INPUT LASER 1", out _));
        Assert.Equal("POWER ?", s.PollWords);
    }

    [Fact]
    public void PjLinkAuthenticationPutsTheDigestInFrontOfTheFirstCommandOnly()
    {
        var s = new PjLinkSession("JBMIAProjectorLink");
        s.OnConnected();
        var greeting = s.OnReceived("PJLINK 1 498e4a67");
        Assert.Equal("connected — authentication on", greeting.Words);
        var expected = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes("498e4a67JBMIAProjectorLink"))).ToLowerInvariant();
        Assert.Equal(expected, PjLinkSession.Digest("498e4a67", "JBMIAProjectorLink"));
        Assert.Equal(expected + "%1POWR 1\r", Text(Assert.Single(s.Encode("POWER ON", out _))));
        Assert.Equal("%1POWR ?\r", Text(Assert.Single(s.Encode("POWER ?", out _))));   // the second command rides the authenticated link
        s.OnConnected();                                                                 // the projector dropped the idle link: the next one authenticates again
        s.OnReceived("PJLINK 1 aabbccdd");
        Assert.StartsWith(PjLinkSession.Digest("aabbccdd", "JBMIAProjectorLink"), Text(Assert.Single(s.Encode("POWER ON", out _))));

        // No authentication asked: no digest, whatever the password box says.
        var open = new PjLinkSession("anything");
        open.OnReceived("PJLINK 0");
        Assert.True(open.AuthenticationOff);
        Assert.Equal("%1POWR 1\r", Text(Assert.Single(open.Encode("POWER ON", out _))));

        // Authentication asked and no password typed: refused with the reason, nothing sent.
        var blank = new PjLinkSession("");
        blank.OnReceived("PJLINK 1 498e4a67");
        Assert.Empty(blank.Encode("POWER ON", out var problem));
        Assert.Contains("Password box", problem);
    }

    [Fact]
    public void PjLinkRepliesReadAsWordsAndErrorsAsFaults()
    {
        var s = new PjLinkSession("");
        Assert.Equal("POWR: OK", s.OnReceived("%1POWR=OK").Words);
        Assert.False(s.OnReceived("%1POWR=OK").IsError);
        Assert.Equal("power on", s.OnReceived("%1POWR=1").Words);
        Assert.Equal("warming up", s.OnReceived("%1POWR=3").Words);
        Assert.Equal("cooling down", s.OnReceived("%1POWR=2").Words);
        Assert.Equal("power standby", s.OnReceived("%1POWR=0").Words);
        Assert.Equal("lamp 1234 h (on)", s.OnReceived("%1LAMP=1234 1").Words);
        Assert.Equal("no errors", s.OnReceived("%1ERST=000000").Words);
        Assert.Equal("shutter closed, sound muted", s.OnReceived("%1AVMT=31").Words);
        var err = s.OnReceived("%1INPT=ERR2");
        Assert.True(err.IsError);
        Assert.StartsWith("INPT: out of parameter", err.Words);
        Assert.True(s.OnReceived("%1POWR=ERR3").IsError);
        Assert.True(s.OnReceived("PJLINK ERRA").IsError);
        Assert.Equal("something else", s.OnReceived("something else").Words);
    }

    [Fact]
    public void DisguiseWordsBecomeShowControlMessagesUnderTheD3Prefix()
    {
        var s = new DisguiseSession();
        OscMessage One(string words)
        {
            var frames = s.Encode(words, out var problem);
            Assert.Equal("", problem);
            return Assert.Single(OscCodec.Decode(Assert.Single(frames)));
        }
        Assert.Equal("/d3/showcontrol/play", One("PLAY").Address);
        Assert.Empty(One("PLAY").Args);
        Assert.Equal("/d3/showcontrol/playsection", One("PLAY SECTION").Address);
        Assert.Equal("/d3/showcontrol/loopsection", One("LOOP").Address);
        Assert.Equal("/d3/showcontrol/stop", One("STOP").Address);
        Assert.Equal("/d3/showcontrol/stop", One("PAUSE").Address);
        Assert.Equal("/d3/showcontrol/nextsection", One("NEXT").Address);
        Assert.Equal("/d3/showcontrol/previoussection", One("PREV").Address);
        Assert.Equal("/d3/showcontrol/nexttrack", One("NEXT TRACK").Address);
        Assert.Equal("/d3/showcontrol/returntostart", One("START").Address);
        Assert.Equal("/d3/showcontrol/hold", One("HOLD").Address);
        Assert.Equal("/d3/showcontrol/fadeup", One("FADE UP").Address);
        Assert.Equal("/d3/showcontrol/fadedown", One("FADE DOWN").Address);
        var track = One("TRACK Main stage");
        Assert.Equal("/d3/showcontrol/trackname", track.Address);
        Assert.Equal("Main stage", Assert.Single(track.Args));
        var trackId = One("TRACK #2");
        Assert.Equal("/d3/showcontrol/trackid", trackId.Address);
        Assert.Equal(2, Assert.Single(trackId.Args));
        var cue = One("CUE 1.5");
        Assert.Equal("/d3/showcontrol/cue", cue.Address);
        Assert.Equal(1.5f, Assert.Single(cue.Args));
        Assert.Equal("intro", Assert.Single(One("CUE intro").Args));
        Assert.Equal(0.8f, (float)Assert.Single(One("VOLUME 80").Args)!, 3);
        Assert.Equal(0.5f, (float)Assert.Single(One("BRIGHTNESS 0.5").Args)!, 3);
        var raw = One("RAW /d3/showcontrol/volume 0.25");
        Assert.Equal("/d3/showcontrol/volume", raw.Address);
        Assert.Equal(0.25f, (float)Assert.Single(raw.Args)!, 3);
        Assert.Equal("/anything/else", One("/anything/else 1").Address);
        Assert.Empty(s.Encode("DANCE", out var problem));
        Assert.Contains("not a Disguise command", problem);

        // What d3 sends back reads as the address and its arguments — a line a trigger row can match.
        Assert.Equal("/d3/showcontrol/transport playing", s.Decode(OscCodec.Encode(OscMessage.Of("/d3/showcontrol/transport", "playing"))));
        Assert.Null(s.Decode(Encoding.UTF8.GetBytes("not osc")));
    }

    [Fact]
    public void OscWordsAreTypedAndReadBack()
    {
        var m = OscWords.Parse("/layer/1/opacity 0.5 3 true \"Main stage\" word");
        Assert.NotNull(m);
        Assert.Equal("/layer/1/opacity", m!.Address);
        Assert.Equal(new object?[] { 0.5f, 3, true, "Main stage", "word" }, m.Args);
        Assert.Equal("/layer/1/opacity 0.5 3 true \"Main stage\" word", OscWords.Format(m));
        Assert.Null(OscWords.Parse("no address"));
        var s = new OscSession();
        var frame = Assert.Single(s.Encode("/cue/1/start", out var problem));
        Assert.Equal("", problem);
        Assert.Equal("/cue/1/start", Assert.Single(OscCodec.Decode(frame)).Address);
        Assert.Empty(s.Encode("PLAY", out problem));
        Assert.Contains("not an OSC message", problem);
        Assert.Equal("/reply 1", s.Decode(OscCodec.Encode(OscMessage.Of("/reply", 1))));
    }

    [Fact]
    public void PixeraPlaysATimelineByLookingItUpFirstAndSplitsOnTheTerminator()
    {
        var s = new PixeraSession();
        var frames = s.Encode("TIMELINE Main PLAY", out var problem);
        Assert.Equal("", problem);
        var lookup = Text(Assert.Single(frames));
        Assert.EndsWith(PixeraSession.Terminator, lookup);
        using (var doc = JsonDocument.Parse(lookup[..^PixeraSession.Terminator.Length]))
        {
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal("Pixera.Timelines.getTimelineFromName", doc.RootElement.GetProperty("method").GetString());
            Assert.Equal("Main", doc.RootElement.GetProperty("params").GetProperty("name").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
        }
        Assert.Equal(1, s.PendingCount);

        // The reply carries the handle: the play goes out with it.
        var reply = s.OnReceived("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":12345}");
        Assert.False(reply.IsError);
        Assert.Equal("found — sending on", reply.Words);
        var play = Text(Assert.Single(reply.SendNext));
        using (var doc = JsonDocument.Parse(play[..^PixeraSession.Terminator.Length]))
        {
            Assert.Equal("Pixera.Timelines.Timeline.play", doc.RootElement.GetProperty("method").GetString());
            Assert.Equal(12345, doc.RootElement.GetProperty("params").GetProperty("handle").GetInt64());
        }
        Assert.Equal(0, s.PendingCount);

        // A cue: the timeline, then the cue, then the apply.
        s.Encode("CUE Main Intro", out problem);
        Assert.Equal("", problem);
        var cueLookup = s.OnReceived("{\"id\":3,\"result\":7}");
        var second = Text(Assert.Single(cueLookup.SendNext));
        Assert.Contains("Pixera.Timelines.Timeline.getCueFromName", second);
        Assert.Contains("\"name\":\"Intro\"", second);
        var apply = s.OnReceived("{\"id\":4,\"result\":99}");
        Assert.Contains("Pixera.Timelines.Cue.apply", Text(Assert.Single(apply.SendNext)));
        Assert.Contains("\"handle\":99", Text(apply.SendNext[0]));

        // An error from the box is a fault in its own words; a result with nothing pending is just read.
        var err = s.OnReceived("{\"id\":9,\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}");
        Assert.True(err.IsError);
        Assert.Equal("Pixera error: Method not found", err.Words);
        Assert.Equal("result 42", s.OnReceived("{\"id\":10,\"result\":42}").Words);
        Assert.Equal("plain text", s.OnReceived("plain text").Words);

        // API and RAW pass through; a stranger is refused.
        Assert.Contains("\"method\":\"Pixera.Utility.getApiRevision\"", Text(Assert.Single(s.Encode("API Pixera.Utility.getApiRevision", out _))));
        Assert.Contains("\"params\":{\"a\":1}", Text(Assert.Single(s.Encode("API Pixera.Test.echo {\"a\":1}", out _))));
        Assert.Equal("{\"x\":1}" + PixeraSession.Terminator, Text(Assert.Single(s.Encode("RAW {\"x\":1}", out _))));
        Assert.Empty(s.Encode("TIMELINE Main DANCE", out problem));
        Assert.Contains("PLAY, PAUSE or STOP", problem);

        // The stream is split on 0xPX, not on line breaks.
        var buffer = new StringBuilder("{\"id\":1}0xPX{\"id\":2,\"result\":\"a\nb\"}0xPX{\"id\":3");
        var split = s.Split(buffer);
        Assert.Equal(new[] { "{\"id\":1}", "{\"id\":2,\"result\":\"a\nb\"}" }, split);
        Assert.Equal("{\"id\":3", buffer.ToString());
    }

    [Fact]
    public void PresetsAndLabelsAndTheAddressWordsCarryTheProfile()
    {
        var projector = DeviceProfiles.Preset(DeviceProfile.PjLink, 1);
        Assert.Equal("Projector", projector.Name);
        Assert.Equal(DeviceLink.Tcp, projector.Link);
        Assert.Equal(4352, projector.NetPort);
        Assert.False(projector.EchoReplies);
        Assert.False(projector.HearsShow);
        Assert.False(projector.SpeaksProtocol);
        Assert.Equal("Disguise 2", DeviceProfiles.Preset(DeviceProfile.Disguise, 2).Name);
        Assert.Equal(DeviceLink.Udp, DeviceProfiles.Preset(DeviceProfile.Disguise, 1).Link);
        Assert.Equal(1400, DeviceProfiles.Preset(DeviceProfile.Pixera, 1).NetPort);
        Assert.Equal(DeviceLink.Http, DeviceProfiles.Preset(DeviceProfile.Lines, 1).Link);
        foreach (var p in Enum.GetValues<DeviceProfile>())
        {
            Assert.NotEqual(p.ToString(), DeviceProfiles.Label(p));
            Assert.True(DeviceProfiles.Words(p).Length > 20, p.ToString());
            Assert.Equal(p, ProfileSession.For(new DeviceConfig { Profile = p }).Profile);
        }
        projector.Port = "10.0.0.20";
        Assert.Equal("10.0.0.20:4352 (TCP, Projector (PJLink))", DeviceAddress.Describe(projector));
        Assert.Equal("http://10.0.0.9:8080 (HTTP)", DeviceAddress.Describe(new DeviceConfig { Link = DeviceLink.Http, Port = "10.0.0.9:8080" }));
        Assert.Equal(DeviceProfiles.Words(DeviceProfile.Pixera), new DeviceConfig { Profile = DeviceProfile.Pixera }.Words);
        // The plain profile frames as it always did; over HTTP the words go as they are.
        Assert.Equal("RELAY 1\n", Text(Assert.Single(new LinesSession(LineEnding.Lf).Encode("RELAY 1", out _))));
        Assert.Equal("GET /api/play", Text(Assert.Single(ProfileSession.For(new DeviceConfig { Link = DeviceLink.Http }).Encode("GET /api/play", out _))));
    }
}
