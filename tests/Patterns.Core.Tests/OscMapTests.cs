using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>OSC onto the protocol: every address, a switch in every spelling, a fader, and the state out as messages.</summary>
public class OscMapTests
{
    private static OscMessage Of(string address, params object?[] args) => OscMessage.Of(address, args);

    [Fact]
    public void EveryAddressBecomesTheLineItMeans()
    {
        var cases = new (OscMessage Message, string? Line)[]
        {
            (Of("/patterns/outputs", 1), "OUTPUTS ON"), (Of("/patterns/outputs", 0), "OUTPUTS OFF"), (Of("/patterns/outputs/off"), "OUTPUTS OFF"), (Of("/patterns/outputs"), "OUTPUTS ON"),
            (Of("/patterns/blackout"), "BLACKOUT TOGGLE"), (Of("/patterns/blackout", 1f), "BLACKOUT ON"), (Of("/patterns/blackout", 0.2f), "BLACKOUT OFF"),
            (Of("/patterns/blackout", "off"), "BLACKOUT OFF"), (Of("/patterns/blackout/toggle"), "BLACKOUT TOGGLE"), (Of("/patterns/blackout", true), "BLACKOUT ON"),
            (Of("/patterns/identify"), "IDENTIFY"),
            (Of("/patterns/look", 3), "LOOK 3"), (Of("/patterns/look", "Walk-in"), "LOOK Walk-in"), (Of("/patterns/look/2"), "LOOK 2"), (Of("/patterns/look"), null),
            (Of("/patterns/look/index/3"), "LOOK #3"), (Of("/patterns/look/index", 2), "LOOK #2"), (Of("/patterns/look/bank/4"), "LOOK #4"), (Of("/patterns/look/index"), null), (Of("/patterns/look/index/x"), null),
            (Of("/patterns/next"), "NEXT"), (Of("/patterns/prev"), "PREV"), (Of("/patterns/back"), "PREV"),
            (Of("/patterns/screen/2", 1), "SCREEN 2 ON"), (Of("/patterns/screen/2"), "SCREEN 2 TOGGLE"), (Of("/patterns/screen/2/off"), "SCREEN 2 OFF"), (Of("/patterns/screen/x"), null), (Of("/patterns/screen"), null),
            (Of("/patterns/lock/1", 0), "LOCK 1 OFF"), (Of("/patterns/lock/1"), "LOCK 1 TOGGLE"), (Of("/patterns/lock/1/on"), "LOCK 1 ON"),
            (Of("/patterns/group/a", 1), "GROUP A ON"), (Of("/patterns/group/B/off"), "GROUP B OFF"), (Of("/patterns/group"), null),
            (Of("/patterns/audio/play"), "AUDIO PLAY"), (Of("/patterns/audio", "stop"), "AUDIO STOP"), (Of("/patterns/audio/x"), null),
            (Of("/patterns/music/play"), "MUSIC PLAY"), (Of("/patterns/music/play", 2), "MUSIC PLAY 2"), (Of("/patterns/music/play", "Interval bed"), "MUSIC PLAY Interval bed"),
            (Of("/patterns/music/play/3"), "MUSIC PLAY 3"), (Of("/patterns/music/pause"), "MUSIC PAUSE"), (Of("/patterns/music/next"), "MUSIC NEXT"), (Of("/patterns/music/2"), "MUSIC PLAY 2"),
            (Of("/patterns/music/volume", 40), "MUSIC VOL 40"), (Of("/patterns/music/volume", 0.5f), "MUSIC VOL 50"), (Of("/patterns/music/volume", 1.0f), "MUSIC VOL 100"),
            (Of("/patterns/music/volume", 75.0), "MUSIC VOL 75"), (Of("/patterns/music/vol/30"), "MUSIC VOL 30"), (Of("/patterns/music/volume"), null), (Of("/patterns/music/volume", "loud"), null),
            (Of("/patterns/tone", 1), "TONE ON"), (Of("/patterns/tone/off"), "TONE OFF"), (Of("/patterns/duck"), "DUCK TOGGLE"), (Of("/patterns/duck", 0), "DUCK OFF"),
            (Of("/patterns/stinger", 3), "STINGER 3"), (Of("/patterns/stinger/2"), "STINGER 2"), (Of("/patterns/stinger/stop"), "STINGER STOP"), (Of("/patterns/stinger"), null),
            (Of("/patterns/vog", "Welcome"), "VOG Welcome"), (Of("/patterns/sting/stop"), "STING STOP"), (Of("/patterns/sting/1"), "STING 1"),
            (Of("/patterns/lowerthird", 2), "LOWERTHIRD 2"), (Of("/patterns/lt/3"), "LOWERTHIRD 3"), (Of("/patterns/lowerthird", "Neon", "Jane Doe"), "LOWERTHIRD Neon WITH Jane Doe"),
            (Of("/patterns/lowerthird/2", "Jane Doe"), "LOWERTHIRD 2 WITH Jane Doe"), (Of("/patterns/lowerthird/2/3"), "LOWERTHIRD 2 WITH 3"),
            (Of("/patterns/lowerthird/off"), "LOWERTHIRD OFF"), (Of("/patterns/lowerthird", "off"), "LOWERTHIRD OFF"), (Of("/patterns/lowerthird"), null),
            (Of("/patterns/person", 1), "PERSON 1"), (Of("/patterns/person/Jane"), "PERSON Jane"), (Of("/patterns/person"), null),
            (Of("/patterns/section", 2), "SECTION 2"), (Of("/patterns/stream", 1), "STREAM ON"), (Of("/patterns/stream/off"), "STREAM OFF"),
            (Of("/patterns/cue/go"), "CUE GO"), (Of("/patterns/cue/go", "abc"), "CUE GO abc"), (Of("/patterns/cue/go/abc"), "CUE GO abc"),
            (Of("/patterns/cue/standby/next"), "CUE STANDBY NEXT"), (Of("/patterns/cue/standby", "prev"), "CUE STANDBY PREV"), (Of("/patterns/cue/standby", "03.020"), "CUE STANDBY 03.020"),
            (Of("/patterns/cue/standby/03.020"), "CUE STANDBY 03.020"), (Of("/patterns/cue/standby"), null),
            (Of("/patterns/cue/hold", 1), "CUE HOLD ON"), (Of("/patterns/cue/hold"), "CUE HOLD ON"), (Of("/patterns/cue/arm/off"), "CUE ARM OFF"), (Of("/patterns/cue/list"), "CUE LIST"), (Of("/patterns/cue/x"), null), (Of("/patterns/cue"), null),
            (Of("/patterns/stopall"), "STOPALL"), (Of("/patterns/ping"), "PING"), (Of("/patterns/status"), "STATUS"),
            (Of("/Patterns/Blackout/ON"), "BLACKOUT ON"), (Of("/other/blackout", 1), null), (Of("/patterns"), null), (Of("/patterns/"), null), (Of("/patterns/nonsense"), null),
        };
        foreach (var (message, line) in cases)
        {
            Assert.True(line == OscMap.ToLine(message), $"{message}: expected '{line ?? "null"}', got '{OscMap.ToLine(message) ?? "null"}'");
            if (line is not null)
            {
                Assert.True(ControlProtocol.Parse(line).Kind != RemoteCommandKind.Unknown, $"{line} is not a command the protocol knows");
            }
        }
        Assert.NotEmpty(OscMap.Reference);
        Assert.All(OscMap.Reference, r => Assert.StartsWith(OscMap.Prefix, r.Address));
    }

    [Fact]
    public void TheStateGoesOutAsMessagesAndABundleRoundTrips()
    {
        const string json = "{\"show\":\"x\",\"rev\":12345678901,\"airLabel\":\"Walk-in\"," +
                            "\"cuestack\":{\"armed\":true,\"hold\":false,\"confirm\":\"\",\"standby\":{\"id\":\"a\",\"number\":\"01.020\",\"name\":\"Welcome\"},\"previous\":null," +
                            "\"next\":[{\"id\":\"b\",\"number\":\"01.030\",\"name\":\"Coffee\"}],\"last\":{\"number\":\"01.010\",\"outcome\":\"Done\"},\"timing\":{\"offset\":\"ON TIME\",\"follow\":\"\"}}," +
                            "\"blackout\":true,\"live\":false,\"screens\":[{\"n\":1,\"label\":\"A\",\"enabled\":true,\"locked\":false},{\"n\":2,\"label\":\"B\",\"enabled\":false,\"locked\":true}]," +
                            "\"audio\":{\"playing\":false},\"music\":{\"on\":true,\"playing\":true,\"level\":40,\"now\":\"Song\"},\"tone\":false,\"stingerPlaying\":\"\",\"stingHold\":\"\"," +
                            "\"lowerThird\":\"Neon\",\"lowerThirdPerson\":\"Jane Doe\",\"duck\":false,\"stream\":{\"active\":false},\"playlist\":\"\",\"health\":\"OK\"}";
        var messages = OscFeedback.FromState(json);
        OscMessage One(string address) => Assert.Single(messages, m => m.Address == OscFeedback.Prefix + address);
        Assert.Equal(1, One("blackout").Args[0]);
        Assert.Equal(0, One("live").Args[0]);
        Assert.Equal("Walk-in", One("program").Args[0]);
        Assert.Equal(1, One("screen/1").Args[0]);
        Assert.Equal(0, One("screen/2").Args[0]);
        Assert.Equal(0, One("lock/1").Args[0]);
        Assert.Equal(1, One("lock/2").Args[0]);
        Assert.Equal(1, One("cue/armed").Args[0]);
        Assert.Equal(0, One("cue/hold").Args[0]);
        Assert.Equal(new object?[] { "01.020", "Welcome" }, One("cue/standby").Args);
        Assert.Equal(new object?[] { "", "" }, One("cue/previous").Args);
        Assert.Equal(new object?[] { "01.030", "Coffee" }, One("cue/next").Args);
        Assert.Equal(new object?[] { "01.010", "Done" }, One("cue/last").Args);
        Assert.Equal("ON TIME", One("cue/offset").Args[0]);
        Assert.Equal("Neon", One("lowerthird").Args[0]);
        Assert.Equal("Jane Doe", One("lowerthird/person").Args[0]);
        Assert.Equal(1, One("music").Args[0]);
        Assert.Equal(40, One("music/level").Args[0]);
        Assert.Equal("Song", One("music/now").Args[0]);
        Assert.Equal(0, One("stream").Args[0]);
        Assert.Equal("OK", One("health").Args[0]);
        Assert.Equal((int)(12345678901L & 0x7FFFFFFF), One("rev").Args[0]);

        Assert.Empty(OscFeedback.FromState("not json"));
        Assert.Empty(OscFeedback.FromState("[]"));
        Assert.Empty(OscFeedback.FromState("{}"));

        // One datagram carries them all; the codec reads the bundle back in order.
        var bundle = OscCodec.EncodeBundle(messages);
        Assert.Equal(0, bundle.Length % 4);
        var back = OscCodec.Decode(bundle);
        Assert.Equal(messages.Select(m => m.Address), back.Select(m => m.Address));
        Assert.Equal(messages.Select(m => m.ToString()), back.Select(m => m.ToString()));
    }

    [Fact]
    public void ALookByItsPlaceInTheListIsAVerbOfItsOwn()
    {
        var byIndex = ControlProtocol.Parse("LOOK #3");
        Assert.Equal(RemoteCommandKind.Look, byIndex.Kind);
        Assert.Equal(3, byIndex.IntArg);
        Assert.Equal("#", byIndex.Extra);
        var bySlot = ControlProtocol.Parse("LOOK 3");
        Assert.Equal("", bySlot.Extra);
        Assert.Equal(3, bySlot.IntArg);
        var byName = ControlProtocol.Parse("LOOK #hashtag");     // not a number after the hash: a look named that
        Assert.Equal("#hashtag", byName.TextArg);
        Assert.Equal("", byName.Extra);
        Assert.Equal(RemoteCommandKind.Look, ControlProtocol.Parse("LOOK #0").Kind);
        Assert.Equal("#0", ControlProtocol.Parse("LOOK #0").TextArg);  // #0 is no place: a name, refused later as unknown
    }

    [Fact]
    public void TheShowsListsAndTheLookOnAirGoOutForABankOfKeys()
    {
        const string json = "{\"airLook\":\"Walk-in\",\"previewLook\":\"Awards\",\"pattern\":\"Media\"," +
                            "\"looks\":[{\"n\":1,\"name\":\"Walk-in\",\"slot\":1,\"air\":true,\"preview\":false},{\"n\":2,\"name\":\"Awards\",\"slot\":0,\"air\":false,\"preview\":true}]," +
                            "\"lowerThirds\":[{\"n\":1,\"name\":\"Neon\"}],\"people\":[{\"n\":1,\"name\":\"Jane Doe\",\"role\":\"CEO\"}]," +
                            "\"stingers\":[{\"n\":1,\"name\":\"Whoosh\",\"kind\":\"sting\"},{\"n\":2,\"name\":\"Welcome\",\"kind\":\"vog\"}]," +
                            "\"sections\":[{\"n\":1,\"name\":\"Walk-in\",\"active\":true}],\"music\":{\"playing\":false,\"items\":[{\"n\":1,\"name\":\"Interval bed\"}]}," +
                            "\"screens\":[{\"n\":1,\"label\":\"Main\",\"enabled\":true,\"locked\":false,\"armed\":true}]," +
                            "\"deck\":{\"file\":\"talk.pdf\",\"page\":3,\"count\":12,\"ended\":false},\"web\":{\"page\":\"youtube\",\"service\":\"YouTube\"}," +
                            "\"cuestack\":{\"armed\":true,\"hold\":false,\"standby\":{\"number\":\"01.020\",\"name\":\"Welcome\"},\"next\":[{\"number\":\"01.030\",\"name\":\"Coffee\"},{\"number\":\"01.040\",\"name\":\"Keynote\"}]}}";
        var messages = OscFeedback.FromState(json);
        OscMessage One(string address) => Assert.Single(messages, m => m.Address == OscFeedback.Prefix + address);
        Assert.Equal("Walk-in", One("look/air").Args[0]);
        Assert.Equal("Awards", One("look/preview").Args[0]);
        Assert.Equal("Media", One("pattern").Args[0]);
        Assert.Equal("Walk-in", One("looks/1").Args[0]);
        Assert.Equal(1, One("looks/1/air").Args[0]);
        Assert.Equal("Awards", One("looks/2").Args[0]);
        Assert.Equal(0, One("looks/2/air").Args[0]);
        Assert.Equal("", One("looks/3").Args[0]);                // a bank key past the list goes blank
        Assert.Equal("", One("looks/16").Args[0]);
        Assert.Equal("Neon", One("lowerthirds/1").Args[0]);
        Assert.Equal("Jane Doe", One("people/1").Args[0]);
        Assert.Equal("Whoosh", One("stingers/1").Args[0]);
        Assert.Equal("Welcome", One("stingers/2").Args[0]);
        Assert.Equal("Walk-in", One("sections/1").Args[0]);
        Assert.Equal("Interval bed", One("music/items/1").Args[0]);
        Assert.Equal("Main", One("screen/1/name").Args[0]);
        Assert.Equal(1, One("armed/1").Args[0]);
        Assert.Equal(3, One("deck/page").Args[0]);
        Assert.Equal(12, One("deck/count").Args[0]);
        Assert.Equal(0, One("deck/ended").Args[0]);
        Assert.Equal("talk.pdf", One("deck/file").Args[0]);
        Assert.Equal("youtube", One("web/page").Args[0]);
        Assert.Equal("YouTube", One("web/service").Args[0]);
        Assert.Equal(new object?[] { "01.030", "Coffee" }, One("cue/next").Args);
        Assert.Equal(new object?[] { "01.030", "Coffee" }, One("cue/next/1").Args);
        Assert.Equal(new object?[] { "01.040", "Keynote" }, One("cue/next/2").Args);
        Assert.DoesNotContain(messages, m => m.Address == OscFeedback.Prefix + "cue/next/3");

        // With no deck and no page the same addresses read empty, so a controller's display clears.
        var none = OscFeedback.FromState("{\"deck\":null,\"web\":null}");
        Assert.Equal(0, Assert.Single(none, m => m.Address == OscFeedback.Prefix + "deck/count").Args[0]);
        Assert.Equal("", Assert.Single(none, m => m.Address == OscFeedback.Prefix + "web/page").Args[0]);
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/look/index"));
    }
}
