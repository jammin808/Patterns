using System.Text;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Companion as a box the desk drives: the words a cue says become Companion's own TCP API lines,
/// a bare PAGE turns the device's surface, +OK is a yes and -ERR a no with its reason, and the
/// profile's preset, label, words and confirm level are the ones the page and the fence read.
/// </summary>
public class CompanionProfileTests
{
    private static string Text(byte[] frame) => Encoding.UTF8.GetString(frame);

    [Fact]
    public void TheWordsBecomeCompanionsLines()
    {
        var s = new CompanionSession("streamdeck:abc123");
        string One(string words)
        {
            var frames = s.Encode(words, out var problem);
            Assert.True(problem.Length == 0, problem);
            return Text(Assert.Single(frames));
        }
        Assert.Equal("SURFACE streamdeck:abc123 PAGE-SET 3\n", One("PAGE 3"));
        Assert.Equal("SURFACE emulator:emulator PAGE-SET 12\n", One("PAGE 12 emulator:emulator"));
        Assert.Equal("SURFACE streamdeck:abc123 PAGE-UP\n", One("PAGE UP"));
        Assert.Equal("SURFACE other PAGE-DOWN\n", One("page down other"));
        Assert.Equal("LOCATION 2/0/1 PRESS\n", One("PRESS 2/0/1"));
        Assert.Equal("LOCATION 2/0/1 DOWN\n", One("DOWN 2/0/1"));
        Assert.Equal("LOCATION 2/0/1 UP\n", One("UP 2/0/1"));
        Assert.Equal("LOCATION 3/1/4 ROTATE-LEFT\n", One("ROTATE LEFT 3/1/4"));
        Assert.Equal("LOCATION 3/1/4 ROTATE-RIGHT\n", One("rotate right 3/1/4"));
        Assert.Equal("LOCATION 2/0/1 SET-STEP 2\n", One("STEP 2/0/1 2"));
        Assert.Equal("LOCATION 2/0/1 STYLE TEXT Q&A\nnow\n", One("TEXT 2/0/1 Q&A\nnow"));
        Assert.Equal("LOCATION 2/0/1 STYLE COLOR #ffffff\n", One("COLOR 2/0/1 #ffffff"));
        Assert.Equal("LOCATION 2/0/1 STYLE BGCOLOR #ff8800\n", One("BGCOLOUR 2/0/1 #ff8800"));
        Assert.Equal("CUSTOM-VARIABLE speaker SET-VALUE Jane Doe\n", One("VAR speaker Jane Doe"));
        Assert.Equal("CUSTOM-VARIABLE speaker GET-VALUE\n", One("GET speaker"));
        Assert.Equal("SURFACES RESCAN\n", One("RESCAN"));
        Assert.Equal("SURFACES RESCAN\n", One("RAW SURFACES RESCAN"));
    }

    [Fact]
    public void WordsThatAreNotCompanionsAreRefusedWithTheReason()
    {
        var s = new CompanionSession("");
        string Problem(string words)
        {
            var frames = s.Encode(words, out var problem);
            Assert.Empty(frames);
            return problem;
        }
        Assert.Contains("Surface box", Problem("PAGE 3"));
        Assert.Contains("not a page number", Problem("PAGE three streamdeck:x"));
        Assert.Contains("page/row/column", Problem("PRESS 2/0"));
        Assert.Contains("page/row/column", Problem("PRESS A/B/C"));
        Assert.Contains("location alone", Problem("PRESS 2/0/1 hard"));
        Assert.Contains("LEFT or RIGHT", Problem("ROTATE UP 2/0/1"));
        Assert.Contains("the step", Problem("STEP 2/0/1 two"));
        Assert.Contains("a colour", Problem("COLOR 2/0/1"));
        Assert.Contains("name and its value", Problem("VAR speaker"));
        Assert.Contains("is not a Companion command", Problem("DANCE"));
        Assert.Contains("Nothing to send", Problem(""));
        Assert.Contains("Companion's line itself", Problem("RAW"));
    }

    [Fact]
    public void CompanionsAnswersAreAYesOrANoWithItsReason()
    {
        var s = new CompanionSession("x");
        var ok = s.OnReceived("+OK");
        Assert.True(ok.Ack);
        Assert.False(ok.IsError);
        Assert.Equal("OK", ok.Words);
        var value = s.OnReceived("+OK \"Jane Doe\"");
        Assert.True(value.Ack);
        Assert.Equal("OK — \"Jane Doe\"", value.Words);
        var no = s.OnReceived("-ERR Surface not found");
        Assert.True(no.IsError);
        Assert.False(no.Ack);
        Assert.Equal("Companion refused: Surface not found", no.Words);
        var other = s.OnReceived("something else");
        Assert.False(other.Ack);
        Assert.False(other.IsError);
        Assert.Equal("something else", other.Words);
    }

    [Fact]
    public void ThePresetTheLabelTheWordsAndTheLevelAreCompanions()
    {
        var d = DeviceProfiles.Preset(DeviceProfile.Companion, 1);
        Assert.Equal("Companion", d.Name);
        Assert.Equal(DeviceLink.Tcp, d.Link);
        Assert.Equal(16759, d.NetPort);
        Assert.Equal("RESCAN", d.TestText);
        Assert.Equal(ConfirmLevel.Accepted, d.Confirm);
        Assert.False(d.HearsShow);
        Assert.False(d.EchoReplies);
        Assert.Equal("Companion 2", DeviceProfiles.Preset(DeviceProfile.Companion, 2).Name);
        Assert.Equal("Companion (its TCP API)", DeviceProfiles.Label(DeviceProfile.Companion));
        Assert.StartsWith("PAGE <n> [surface]", DeviceProfiles.Words(DeviceProfile.Companion));
        Assert.Equal(ConfirmLevel.Accepted, DeviceConfirmation.Attainable(DeviceLink.Tcp, DeviceProfile.Companion));
        Assert.Equal(ConfirmLevel.Accepted, DeviceConfirmation.Effective(d));
        var session = ProfileSession.For(new DeviceConfig { Profile = DeviceProfile.Companion, Surface = "streamdeck:abc" });
        Assert.Equal("SURFACE streamdeck:abc PAGE-SET 2\n", Text(Assert.Single(session.Encode("PAGE 2", out _))));
        Assert.Equal("10.0.0.5:16759 (TCP, Companion (its TCP API))", DeviceAddress.Describe(new DeviceConfig { Profile = DeviceProfile.Companion, Link = DeviceLink.Tcp, Port = "10.0.0.5", NetPort = 16759 }));
    }
}
