using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 87 (Show Bus 1.1, docs/SHOWBUS.md): the hello heard and kept, liveness, receipts, the clock exchange, the proof
/// that keeps the token off the wire, the context any app's assistant reads, the Stagewatch the stage timer follows, the
/// OSC gate, the faster hang rule while live, a desk paired from its first run, and the STATE push that is not repeated.
/// </summary>
public class ShowBusTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc);

    private static byte[] Hello(string t, string id, string name, int http = 0, int wire = 0, string inst = "", string extra = "")
        => Encoding.UTF8.GetBytes($"{{\"t\":\"{t}\",\"v\":1,\"proto\":1,\"id\":\"{id}\",\"name\":\"{name}\",\"host\":\"PC-{name}\",\"ports\":{{\"http\":{http},\"wire\":{wire}}},\"caps\":[\"timer\",\"context\"],\"clock\":1760101234567{(inst.Length > 0 ? $",\"inst\":\"{inst}\"" : "")}{extra}}}");

    // ---- the hello heard ------------------------------------------------------------------------

    [Fact]
    public void AHelloIsKeptByIdItsOwnComesBackIgnoredAndJunkIsNotAPeer()
    {
        var peers = new ShowBusPeers();
        Assert.True(peers.Hear(Hello("main", "a1b2c3d4", "Hall A", http: 9924), "10.0.0.9", Now, "self"));
        Assert.False(peers.Hear(Hello("patterns", "ffff0000", "FOH", http: 9696, wire: 9697, inst: "5e1f5e1f"), "10.0.0.5", Now, "5e1f5e1f"));   // our own, back
        Assert.False(peers.Hear(Encoding.UTF8.GetBytes("GET / HTTP/1.1"), "10.0.0.9", Now, "self"));
        Assert.False(peers.Hear(Encoding.UTF8.GetBytes("{\"t\":\"main\"}"), "10.0.0.9", Now, "self"));                                    // no id
        Assert.False(peers.Hear(new byte[] { 0xFF, 0xFE, 0x00 }, "10.0.0.9", Now, "self"));
        Assert.False(peers.Hear(new byte[5000], "10.0.0.9", Now, "self"));                                                               // too big for a hello
        Assert.True(peers.Hear(Hello("messenger", "77aa77aa", "AGM", http: 3023), "10.0.0.20", Now, "self"));

        var all = peers.Snapshot(Now);
        Assert.Equal(new[] { "main", "messenger" }, all.Select(p => p.Kind));
        var sw = all[0];
        Assert.Equal("Stagewatch", sw.KindWords);
        Assert.Equal("Hall A", sw.Name);
        Assert.Equal("PC-Hall A", sw.Host);
        Assert.Equal(9924, sw.Ports.Http);
        Assert.Equal("http://10.0.0.9:9924", sw.HttpBase);
        Assert.Contains("context", sw.Caps);
        Assert.Equal(1760101234567, sw.ClockMs);
        Assert.Equal("AGMessage", all[1].KindWords);

        // Show Bus 1.3 §2: one id is one peer — another door or another name from the same id replaces what was kept.
        Assert.True(peers.Hear(Hello("main", "a1b2c3d4", "Hall A (2)", http: 9925), "10.0.0.9", Now, "self"));
        Assert.True(peers.Hear(Hello("main", "a1b2c3d4", "Hall A renamed", http: 9924), "10.0.0.9", Now, "self"));
        var main = Assert.Single(peers.Snapshot(Now), p => p.Kind == "main");
        Assert.Equal(("Hall A renamed", 9924), (main.Name, main.Ports.Http));
        Assert.Equal(4, peers.Heard);
    }

    [Fact]
    public void AHelloFromBefore11NamesItsDoorsAtTheTopAndANewerFieldIsIgnored()
    {
        var old = ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"patterns\",\"v\":1,\"id\":\"ab12cd34\",\"name\":\"FOH\",\"machine\":\"FOH-PC\",\"web\":9696,\"wire\":9697,\"future\":{\"x\":1}}"), "10.0.0.5", Now, out var inst);
        Assert.NotNull(old);
        Assert.Equal(9696, old!.Ports.Http);
        Assert.Equal(9697, old.Ports.Wire);
        Assert.Equal("FOH-PC", old.Host);
        Assert.Equal("", inst);
        var stagewatch20 = ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"id\":\"x1x1x1\",\"port\":9924}"), "10.0.0.9", Now, out _);
        Assert.Equal(9924, stagewatch20!.Ports.Http);
    }

    [Fact]
    public void AHellosNameIsOneLineSoNoHelloWritesALineOfItsOwn()
    {
        // Round 89: a hello is unauthenticated and its name reaches patterns.log with every stall — a line break never passes.
        var forged = ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"id\":\"x1x1x1\",\"name\":\"Hall A\\n2026-10-25 12:00:00.000 [WARN] forged\\u2028line\",\"host\":\"PC\\r\\nB\\t\",\"port\":9924}"), "10.0.0.9", Now, out _);
        Assert.Equal("Hall A 2026-10-25 12:00:00.000 [WARN] forged line", forged!.Name);
        Assert.Equal("PC  B", forged.Host);
        Assert.DoesNotContain(new[] { forged.Name, forged.Host }, w => w.Any(c => char.IsControl(c) || c == '\u2028'));
    }

    [Fact]
    public void APeerIsLiveForSixSecondsStaleToTwelveGoneAfterAndForgottenAfterAMinute()
    {
        var peers = new ShowBusPeers();
        peers.Hear(Hello("main", "a1b2c3d4", "Hall A", http: 9924), "10.0.0.9", Now, "self");
        var p = peers.Snapshot(Now).Single();
        Assert.Equal(ShowBusState.Live, p.StateAt(Now.AddSeconds(6)));
        Assert.Equal(ShowBusState.Stale, p.StateAt(Now.AddSeconds(7)));
        Assert.Equal(ShowBusState.Stale, p.StateAt(Now.AddSeconds(12)));
        Assert.Equal(ShowBusState.Gone, p.StateAt(Now.AddSeconds(13)));
        Assert.Single(peers.Snapshot(Now.AddSeconds(60)));
        Assert.Empty(peers.Snapshot(Now.AddSeconds(61)));
    }

    [Fact]
    public void TheRedialBacksOffFromHalfASecondToFiveWithJitter()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), ShowBusLiveness.Redial(0, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), ShowBusLiveness.Redial(1, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(5000), ShowBusLiveness.Redial(10, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(5000), ShowBusLiveness.Redial(1000, 0.5));
        Assert.InRange(ShowBusLiveness.Redial(3, 0).TotalMilliseconds, 3200, 3200);       // 4 s less a fifth
        Assert.InRange(ShowBusLiveness.Redial(3, 1).TotalMilliseconds, 4800, 4800);       // 4 s plus a fifth
        Assert.Equal(TimeSpan.FromSeconds(2), ShowBusLiveness.Beat);
        Assert.Equal(TimeSpan.FromSeconds(6), ShowBusLiveness.StaleAfter);
        Assert.Equal(TimeSpan.FromSeconds(12), ShowBusLiveness.DropAfter);
    }

    [Fact]
    public void TheHelloSaysTheDoorsAndTheClockByShowBusNamesAndTheInstanceItIgnores()
    {
        var hello = new ShowBusHello { Id = "ab12cd34", Instance = "0a1b2c3d", Name = "FOH", Machine = "FOH-PC", Host = "FOH-PC", Web = 9696, Wire = 9697, Ports = new ShowBusPorts { Http = 9696, Wire = 9697, Osc = 9698 }, Clock = 1760101234567 };
        using var doc = JsonDocument.Parse(hello.ToJson());
        var r = doc.RootElement;
        Assert.Equal(9696, r.GetProperty("ports").GetProperty("http").GetInt32());
        Assert.Equal(9698, r.GetProperty("ports").GetProperty("osc").GetInt32());
        Assert.Equal(1760101234567, r.GetProperty("clock").GetInt64());
        Assert.Equal("0a1b2c3d", r.GetProperty("inst").GetString());
        foreach (var cap in new[] { "video", "eye", "context", "receipts", "clock" }) Assert.Contains(cap, r.GetProperty("caps").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("token", hello.ToJson(), StringComparison.OrdinalIgnoreCase);
        // A hello parses back into the peer another desk keeps.
        var back = ShowBusPeers.Parse(hello.ToBytes(), "10.0.0.5", Now, out var inst);
        Assert.Equal("0a1b2c3d", inst);
        Assert.Equal(9697, back!.Ports.Wire);
        Assert.Equal("0a1b2c3d", back.Instance);
    }

    [Fact]
    public void AnInstallKeepsItsIdAcrossRestarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-busid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = ShowBusIdentity.LoadOrCreate(dir);
            Assert.True(ShowBusIdentity.IsId(first));
            Assert.Equal(first, ShowBusIdentity.LoadOrCreate(dir));
            File.WriteAllText(Path.Combine(dir, ShowBusIdentity.FileName), "not an id");
            var fresh = ShowBusIdentity.LoadOrCreate(dir);
            Assert.True(ShowBusIdentity.IsId(fresh));
            Assert.Equal(fresh, ShowBusIdentity.LoadOrCreate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- receipts, the clock, the proof ---------------------------------------------------------

    [Theory]
    [InlineData("@a1 VIDEO END 10", "a1", "VIDEO END 10", false)]
    [InlineData("  @req-7_x STATUS", "req-7_x", "STATUS", false)]
    [InlineData("VIDEO END 10", "", "VIDEO END 10", false)]
    [InlineData("@ STATUS", "", "STATUS", true)]
    [InlineData("@bad!id STATUS", "", "STATUS", true)]
    [InlineData("@a1", "", "", true)]
    [InlineData("@abcdefghijabcdefghijabcdefghijabc STATUS", "", "STATUS", true)]   // 33 characters
    public void ARequestIdIsSplitFromItsLineAndAMalformedOneIsRefused(string line, string id, string rest, bool malformed)
    {
        var (gotId, gotLine) = WireReceipt.Split(line, out var bad);
        Assert.Equal(malformed, bad);
        Assert.Equal(id, gotId);
        Assert.Equal(rest, gotLine.Trim());
    }

    [Fact]
    public void AnAnswerNamesTheIdOfTheLineItAnswersAndAPushNever()
    {
        Assert.Equal("OK @a1", WireReceipt.Tag("OK", "a1"));
        Assert.Equal("OK @a1 paired", WireReceipt.Tag("OK paired", "a1"));
        Assert.Equal("OK @a1 {\"x\":1}", WireReceipt.Tag("OK {\"x\":1}", "a1"));
        Assert.Equal("ERR @a1 not paired", WireReceipt.Tag("ERR not paired", "a1"));
        Assert.Equal("STATE {}", WireReceipt.Tag("STATE {}", "a1"));
        Assert.Equal("OK paired", WireReceipt.Tag("OK paired", ""));
    }

    [Fact]
    public void ThePingCarriesStampsAndThePongGivesNtpItsFourAndTheOffsetFollows()
    {
        Assert.Null(ClockPing.Stamp(""));
        Assert.Null(ClockPing.Stamp("abc"));
        Assert.Null(ClockPing.Stamp("-5"));
        Assert.Equal(1760101234567, ClockPing.Stamp(" 1760101234567 "));
        Assert.Equal("OK PONG 1 2 3", ClockPing.Pong(1, 2, 3));
        Assert.Equal((1L, 2L, 3L), ClockPing.ParsePong("PONG 1 2 3"));
        Assert.Null(ClockPing.ParsePong("PONG"));
        Assert.Equal(0, ClockPing.Ms(DateTime.UnixEpoch));

        // The peer is 1.5 s ahead; the path takes 20 ms each way; it holds the ping 5 ms.
        var clock = new LinkClock();
        long t1 = 1_000_000, t2 = t1 + 20 + 1500, t3 = t2 + 5, t4 = t1 + 45;
        clock.Sample(t1 * TimeSpan.TicksPerMillisecond, t2 * TimeSpan.TicksPerMillisecond, t3 * TimeSpan.TicksPerMillisecond, t4 * TimeSpan.TicksPerMillisecond);
        Assert.True(clock.Known);
        Assert.Equal(1500, clock.Offset.TotalMilliseconds, 1);
        Assert.Equal(40, clock.Delay.TotalMilliseconds, 1);
    }

    [Fact]
    public void TheProofAnswersOneNonceForOneTokenAndNameAndTheTokenNeverTravels()
    {
        const string token = "K7QM-3XWD-P9RA";
        var nonce = WireProof.NewNonce();
        Assert.Matches("^[0-9a-f]{32}$", nonce);
        Assert.NotEqual(nonce, WireProof.NewNonce());
        var proof = WireProof.Proof(token, nonce, "Stagewatch");
        Assert.Matches("^[0-9a-f]{64}$", proof);
        Assert.DoesNotContain("K7QM", proof, StringComparison.OrdinalIgnoreCase);
        // The arithmetic another app does: HMAC-SHA256, the token as compared for the key, the nonce then the name.
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("K7QM3XWDP9RA"), Encoding.UTF8.GetBytes(nonce + "Stagewatch"))).ToLowerInvariant();
        Assert.Equal(expected, proof);
        Assert.True(WireProof.Verify(token, nonce, "Stagewatch", proof));
        Assert.True(WireProof.Verify("k7qm 3xwd p9ra", nonce, "Stagewatch", proof.ToUpperInvariant()));   // the token as typed, the proof in any case
        Assert.False(WireProof.Verify(token, WireProof.NewNonce(), "Stagewatch", proof));                  // another nonce
        Assert.False(WireProof.Verify(token, nonce, "FOH deck", proof));                                    // another name
        Assert.False(WireProof.Verify("ABCD-EFGH-JKMN", nonce, "Stagewatch", proof));                       // another token
        Assert.False(WireProof.Verify("", nonce, "Stagewatch", proof));                                     // an open desk has nothing to prove
        Assert.False(WireProof.Verify(token, nonce, "Stagewatch", "zz"));
    }

    [Theory]
    [InlineData("PING", RemoteCommandKind.Ping, "")]
    [InlineData("PING 1760101234567", RemoteCommandKind.Ping, "1760101234567")]
    [InlineData("NONCE", RemoteCommandKind.Nonce, "")]
    [InlineData("AUTH-PROOF abc123", RemoteCommandKind.AuthProof, "abc123")]
    [InlineData("CONTEXT", RemoteCommandKind.Context, "")]
    [InlineData("context status", RemoteCommandKind.Context, "")]
    [InlineData("AUTH-PROOF", RemoteCommandKind.Unknown, "AUTH-PROOF")]
    [InlineData("NONCE again", RemoteCommandKind.Unknown, "NONCE again")]
    public void TheNewWordsAreQueriesNeverActions(string line, RemoteCommandKind kind, string text)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(kind, cmd.Kind);
        Assert.Equal(text, cmd.Text);
        Assert.True(ControlProtocol.IsQuery(cmd));
    }

    // ---- the context ------------------------------------------------------------------------------

    private static EyeGraph Graph(params EyeShowApp[] apps) => EyeGraph.Build(new EyeFacts
    {
        MachineName = "FOH",
        Health = CheckLight.Green,
        Devices = new[] { new EyeDevice("d1", "Projector", "PjLink", "10.0.0.30", true, true, "answering", CheckLight.Green, "", ""), new EyeDevice("d2", "Lights", "Osc", "10.0.0.31", true, false, "closed", CheckLight.Red, "") },
        ShowApps = apps,
    });

    private static ShowContext StagewatchContext(string light = "amber") => new()
    {
        Node = new ShowContextNode { Kind = "main", Id = "a1b2c3d4", Name = "Hall A" },
        At = 1760101234567,
        Light = light,
        Headline = "Keynote — Ana · 12:30 left · Lighting standing by",
        Items = new[]
        {
            new ShowContextItem { Id = "timer", Kind = "timer", Label = "Keynote — Ana", Light = "green", State = "running, 12:30 left" },
            new ShowContextItem { Id = "cue:lighting", Kind = "department", Label = "Lighting", Light = "amber", State = "standing by: LX 12" },
        },
        Links = new[] { new ShowContextLink { From = "timer", To = "cue:lighting", Kind = "drives", Light = "amber" } },
        Recent = new[] { new ShowContextLine { At = 1760101230000, Text = "STANDBY Lighting: LX 12" } },
        Commands = new[] { new ShowContextCommand { Verb = "signal go all", Risk = "high", Words = "GO every department standing by" } },
        Peers = new[] { new ShowContextPeer { Kind = "messenger", Id = "77aa77aa", Name = "AGM", Light = "green", Headline = "12 in the room" } },
    };

    [Fact]
    public void TheContextIsTheEyesPictureInTheSharedShapeWithoutAnotherAppsThings()
    {
        var graph = Graph(new EyeShowApp("a1b2c3d4", "Stagewatch", "Hall A", ShowBusState.Live, true, "") { Context = StagewatchContext() });
        var journal = new[]
        {
            new ShowLogEntry(Now.AddSeconds(-5), "tcp FOH deck", "LookRecall", "Walk-in", "Done", "on air"),
            new ShowLogEntry(Now.AddSeconds(-1), "desk", "BlackoutOn", "", "Done", ""),
        };
        var peers = new[] { new ShowContextPeer { Kind = "main", Id = "a1b2c3d4", Name = "Hall A", Light = "amber", Headline = "Keynote" } };
        var c = ShowContextBuilder.FromEye(graph, "ab12cd34", "FOH", Now, journal, peers);

        Assert.Equal(1, c.V);
        Assert.Equal("patterns", c.Node.Kind);
        Assert.Equal("ab12cd34", c.Node.Id);
        Assert.Equal(ClockPing.Ms(Now), c.At);
        Assert.Equal(graph.Headline, c.Headline);
        Assert.Equal("red", c.Light);                                                            // the lights' closed device is the desk's own worst
        Assert.Contains(c.Items, i => i.Id == "desk");
        Assert.Contains(c.Items, i => i.Label == "Lights" && i.Light == "red");
        Assert.DoesNotContain(c.Items, i => i.Id.StartsWith("app:", StringComparison.Ordinal));  // Stagewatch's things are Stagewatch's to say
        Assert.All(c.Links, l => Assert.True(c.Items.Any(i => i.Id == l.From) && c.Items.Any(i => i.Id == l.To)));
        Assert.Equal(new[] { "LookRecall Walk-in — Done: on air (tcp FOH deck)", "BlackoutOn — Done (desk)" }, c.Recent.Select(r => r.Text));
        Assert.Contains(c.Commands, x => x.Verb == "VIDEO END <seconds>" && x.Risk == "high");
        Assert.Contains(c.Commands, x => x.Verb == "EYE" && x.Risk == "low");
        Assert.Equal("Hall A", c.Peers.Single().Name);

        // It reads back as another app would send it, and keeps to the shape's names.
        var json = c.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "v", "node", "at", "light", "headline", "items", "links", "recent", "commands", "peers" }, doc.RootElement.EnumerateObject().Select(p => p.Name));
        var back = ShowContext.Parse(json);
        Assert.NotNull(back);
        Assert.Equal(c.Items.Count, back!.Items.Count);
        Assert.Equal(c.Headline, back.Headline);
    }

    [Fact]
    public void AContextFromAnotherAppIsBoundedAndUnknownLightsAreGrey()
    {
        Assert.Null(ShowContext.Parse(""));
        Assert.Null(ShowContext.Parse("[]"));
        Assert.Null(ShowContext.Parse("{\"v\":1}"));                                             // no node kind
        var many = "{\"v\":1,\"node\":{\"t\":\"main\",\"id\":\"x\",\"name\":\"X\"},\"light\":\"purple\",\"items\":["
                   + string.Join(",", Enumerable.Range(0, 500).Select(i => $"{{\"id\":\"i{i}\",\"light\":\"Yellow\"}}")) + "]}";
        var c = ShowContext.Parse(many)!;
        Assert.Equal("grey", c.Light);
        Assert.Equal(ShowContext.MaxItems, c.Items.Count);
        Assert.All(c.Items, i => Assert.Equal("amber", i.Light));
        Assert.Equal(CheckLight.Red, ShowContext.Worst(new[] { CheckLight.Green, CheckLight.Red, CheckLight.Amber }));
        Assert.Equal(CheckLight.Green, ShowContext.Worst(new[] { CheckLight.Grey, CheckLight.Green }));
        Assert.Equal(CheckLight.Grey, ShowContext.Worst(Array.Empty<CheckLight>()));
    }

    // ---- what a peer may propose (round 91.3) ----------------------------------------------------

    /// <summary>The fourteen rows the context listed before round 91, in their order: rows are only ever appended.</summary>
    private static readonly string[] FirstFourteen =
    {
        "STATUS", "EYE", "EYE FOCUS <thing>", "STAGE MESSAGE <words>", "STAGE CREW <words>", "VIDEO END <seconds>", "VIDEO PLAY", "VIDEO PAUSE",
        "VIDEO SEEK <m:ss>", "VIDEO RESTART", "LOOK <name>", "CUE GO", "BLACKOUT ON", "BLACKOUT OFF",
    };

    /// <summary>
    /// Every line a §6 form stands for, with a value in each slot: each A|B word one of its words, each […] with and without.
    /// A form's slots are this table's names, so a row naming a slot the table has not met fails here.
    /// </summary>
    private static IReadOnlyList<string> Lines(string form)
    {
        var slots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["<n>"] = "2", ["<name>"] = "Walk-in", ["<cue>"] = "12", ["<key name>"] = "Ctrl+Shift+F5", ["<x>"] = "50", ["<y>"] = "40", ["<page>"] = "slides",
            ["<thing>"] = "screen-2", ["<words>"] = "Five minutes", ["<seconds>"] = "10", ["<m:ss>"] = "1:23", ["<proposal>"] = "a3f9c2d1",
        };
        foreach (var (slot, value) in slots) form = form.Replace(slot, value, StringComparison.Ordinal);
        Assert.DoesNotContain('<', form);
        var lines = new List<string> { form };
        while (lines.Exists(l => l.Contains('[', StringComparison.Ordinal)))
        {
            lines = lines.SelectMany(l =>
            {
                var a = l.IndexOf('[', StringComparison.Ordinal);
                var b = l.IndexOf(']', a);
                return new[] { l[..a] + l[(a + 1)..b] + l[(b + 1)..], (l[..a].TrimEnd() + l[(b + 1)..]).Trim() };
            }).ToList();
        }
        return lines.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Aggregate(new List<string> { "" }, (made, word) => made.SelectMany(m => word.Split('|').Select(w => (m + " " + w).Trim())).ToList()))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void TheContextListsWhatAPeerMayProposeAppendedAfterTheFourteenAndEveryFormIsAVerbOfThisDesk()
    {
        var commands = ShowContextBuilder.Commands;
        Assert.Equal(FirstFourteen, commands.Take(14).Select(c => c.Verb));
        var appended = commands.Skip(14).Select(c => (c.Verb, c.Risk)).ToArray();
        Assert.Equal(new[]
        {
            ("APPLY <proposal>", "review"),
            ("VIDEO STOP", "high"),
            ("EYE NEXT|PREV|RESET", "low"),
            ("EYE LENS <name>", "low"),
            ("CUE STANDBY NEXT|PREV|<cue>", "review"),
            ("PVW LOOK|PRESET|PATTERN <name>", "review"),
            ("PVW PROGRAM|RESET", "review"),
            ("SCREEN <n> TAKE|CUT", "high"),
            ("SCREEN <n> PVW LOOK|PRESET|PATTERN <name>", "review"),
            ("SCREEN <n> PVW PROGRAM|RESET", "review"),
            ("LIBRARY <name>", "high"),
            ("PVW LIBRARY <name>", "high"),
            ("SCREEN <n> PVW LIBRARY <name>", "high"),
            ("WEB NEXT|PREV|FIRST|LAST|PRESENT|EXIT [ON <page>]", "high"),
            ("WEB PLAY|PAUSE|MUTE|RESTART|FORWARD|REWIND [ON <page>]", "high"),
            ("WEB BLACK|WHITE|CAPTIONS|FULLSCREEN [ON <page>]", "high"),
            ("WEB KEY <key name> [ON <page>]", "high"),
            ("WEB CLICK <x> <y> [ON <page>]", "high"),
            ("WEB RELOAD [<page>]", "high"),
        }, appended);
        Assert.All(commands, c => Assert.True(c.Words.Length is > 0 and <= ShowContext.MaxWordsChars && c.Verb.Length <= ShowContextBuilder.OwnVerbMostChars, c.Verb));
        Assert.All(commands, c => Assert.Contains(c.Risk, new[] { "low", "review", "high" }));                    // every row says its risk, none left to a default
        Assert.True(ShowContextBuilder.OwnVerbMostChars <= ShowContext.MaxVerbChars);
        Assert.Equal(commands.Count, commands.Select(c => c.Verb).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("only the one this desk proposed (any other is refused)", commands.Single(c => c.Verb == "APPLY <proposal>").Words, StringComparison.Ordinal);

        // Each form's every line parses to the verbs it says, through the wire's own parser; none is WEB OPEN or WEB TYPE.
        var kinds = new Dictionary<string, ShowActionKind[]>(StringComparer.Ordinal)
        {
            ["VIDEO STOP"] = new[] { ShowActionKind.VideoStop },
            ["EYE NEXT|PREV|RESET"] = new[] { ShowActionKind.EyeNext, ShowActionKind.EyePrev, ShowActionKind.EyeReset },
            ["EYE LENS <name>"] = new[] { ShowActionKind.EyeLens },
            ["CUE STANDBY NEXT|PREV|<cue>"] = new[] { ShowActionKind.CueStandby },
            ["PVW LOOK|PRESET|PATTERN <name>"] = new[] { ShowActionKind.ApplyLookToPreview, ShowActionKind.ScreenStagePreset, ShowActionKind.ScreenStagePattern },
            ["PVW PROGRAM|RESET"] = new[] { ShowActionKind.ScreenToPreview, ShowActionKind.ScreenStageReset },
            ["SCREEN <n> TAKE|CUT"] = new[] { ShowActionKind.ScreenTake, ShowActionKind.ScreenCut },
            ["SCREEN <n> PVW LOOK|PRESET|PATTERN <name>"] = new[] { ShowActionKind.ScreenStageLook, ShowActionKind.ScreenStagePreset, ShowActionKind.ScreenStagePattern },
            ["SCREEN <n> PVW PROGRAM|RESET"] = new[] { ShowActionKind.ScreenStageProgram, ShowActionKind.ScreenStageReset },
            ["LIBRARY <name>"] = new[] { ShowActionKind.ScreenStageLibrary },
            ["PVW LIBRARY <name>"] = new[] { ShowActionKind.ScreenStageLibrary },
            ["SCREEN <n> PVW LIBRARY <name>"] = new[] { ShowActionKind.ScreenStageLibrary },
            ["WEB NEXT|PREV|FIRST|LAST|PRESENT|EXIT [ON <page>]"] = new[] { ShowActionKind.WebKey },
            ["WEB PLAY|PAUSE|MUTE|RESTART|FORWARD|REWIND [ON <page>]"] = new[] { ShowActionKind.WebKey },
            ["WEB BLACK|WHITE|CAPTIONS|FULLSCREEN [ON <page>]"] = new[] { ShowActionKind.WebKey },
            ["WEB KEY <key name> [ON <page>]"] = new[] { ShowActionKind.WebKey },
            ["WEB CLICK <x> <y> [ON <page>]"] = new[] { ShowActionKind.WebClick },
            ["WEB RELOAD [<page>]"] = new[] { ShowActionKind.WebReload },
        };
        Assert.Equal(appended.Skip(1).Select(a => a.Verb), kinds.Keys);
        foreach (var (form, allowed) in kinds)
        {
            var lines = Lines(form);
            var seen = new HashSet<ShowActionKind>();
            foreach (var line in lines)
            {
                var parsed = ControlProtocol.Parse(line);
                Assert.True(parsed.IsAction, $"{form}: '{line}' is not a verb of this desk");
                Assert.Contains(parsed.Action.Kind, allowed);
                seen.Add(parsed.Action.Kind);
            }
            Assert.Equal(allowed.OrderBy(k => k), seen.OrderBy(k => k));                           // and every verb it says is reached by one of its lines
        }
        Assert.Equal(new[] { 6 * 2, 6 * 2, 4 * 2 }, kinds.Keys.Where(k => k.StartsWith("WEB ", StringComparison.Ordinal) && k.Contains('|', StringComparison.Ordinal)).Select(k => Lines(k).Count));
        Assert.Equal(new ShowAction(ShowActionKind.ScreenStageLibrary, "", "Walk-in"), ControlProtocol.Parse("PVW LIBRARY Walk-in").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenStageLibrary, "2", "Walk-in"), ControlProtocol.Parse("SCREEN 2 PVW LIBRARY Walk-in").Action);
        Assert.Equal(new ShowAction(ShowActionKind.WebKey, "slides", "PRESENT"), ControlProtocol.Parse("WEB PRESENT ON slides").Action);
        Assert.Equal(new ShowAction(ShowActionKind.WebClick, "slides", "50 40"), ControlProtocol.Parse("WEB CLICK 50 40 ON slides").Action);
        Assert.Equal(new ShowAction(ShowActionKind.CueStandby, "12"), ControlProtocol.Parse("CUE STANDBY 12").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenStageLibrary, "FOCUSED", "Walk-in"), ControlProtocol.Parse("LIBRARY Walk-in").Action);
        Assert.DoesNotContain(commands, c => c.Verb.StartsWith("WEB OPEN", StringComparison.Ordinal) || c.Verb.StartsWith("WEB TYPE", StringComparison.Ordinal) || c.Verb.StartsWith("RESTART", StringComparison.Ordinal) || c.Verb.StartsWith("UPDATE", StringComparison.Ordinal));

        // APPLY is no wire verb: it reaches this desk only as the approval desk's line for one of this desk's own proposals.
        Assert.False(ControlProtocol.Parse("APPLY a3f9c2d1").IsAction);
        Assert.Equal("a3f9c2d1", DeskProposals.LocalId(DeskProposals.Line("a3f9c2d1", "Look", "Walk-in")));
        Assert.Equal("a3f9c2d1", DeskProposals.LocalId("APPLY a3f9c2d1"));

        // The context's own bytes: the rows follow BLACKOUT OFF, and the last closes the list — a reader matching text keeps its matches.
        var json = ShowContextBuilder.FromEye(Graph(), "ab12cd34", "FOH", Now, Array.Empty<ShowLogEntry>(), Array.Empty<ShowContextPeer>()).ToJson();
        Assert.Contains("{\"verb\":\"BLACKOUT OFF\",\"risk\":\"high\",\"words\":\"the pictures back\"},{\"verb\":\"APPLY \\u003Cproposal\\u003E\",\"risk\":\"review\",", json, StringComparison.Ordinal);
        Assert.Contains("{\"verb\":\"WEB RELOAD [\\u003Cpage\\u003E]\",\"risk\":\"high\",\"words\":\"reload the page on air\"}],\"peers\":", json, StringComparison.Ordinal);
        Assert.Equal(commands.Select(c => (c.Verb, c.Risk, c.Words)), ShowContext.Parse(json)!.Commands.Select(c => (c.Verb, c.Risk, c.Words)));
    }

    private static int RiskRank(string risk) => risk switch { "low" => 0, "review" => 1, _ => 2 };

    /// <summary>
    /// Stagewatch's reader and rater as committed for 2.18 to 2.20, until its whole-form matcher: <c>PeerContexts.Parse</c>
    /// keeps a context's first 64 commands, each verb trimmed and cut at 80 and its risk at 8; <c>ProposalDesk.RiskFromCommands</c>
    /// rates a line by the rows whose leading literal words — up to the first word that opens with '&lt;' or holds '/' — the
    /// line starts with, in any case: the longest run wins, the first row a tie, and a line no row starts is high.
    /// <paramref name="rows"/> reads only that many of the context's rows.
    /// </summary>
    private static string TodaysRisk(string contextJson, string line, int rows = 64)
    {
        static string Cut(string s, int most) => s.Trim().Length > most ? s.Trim()[..most] : s.Trim();
        using var doc = JsonDocument.Parse(contextJson);
        var table = doc.RootElement.GetProperty("commands").EnumerateArray().Take(Math.Min(64, rows))
            .Select(c => (Verb: Cut(c.GetProperty("verb").GetString()!, 80), Risk: Cut(c.GetProperty("risk").GetString()!, 8)))
            .Where(c => c.Verb.Length > 0).ToList();
        var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var best = (Len: 0, Risk: "high");
        foreach (var (verb, risk) in table)
        {
            var leading = verb.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(w => !w.StartsWith('<') && !w.Contains('/', StringComparison.Ordinal)).ToArray();
            if (leading.Length == 0 || leading.Length > words.Length) continue;
            if (!leading.Select((w, i) => w.Equals(words[i], StringComparison.OrdinalIgnoreCase)).All(x => x)) continue;
            if (leading.Length > best.Len) best = (leading.Length, risk);
        }
        return best.Risk is "low" or "review" or "high" ? best.Risk : "high";
    }

    /// <summary>
    /// Show Bus 1.3's rule (§6's notation, §11's rating): the most specific whole form a line matches — the most literal words,
    /// an exact tie the higher risk — and a line no form matches is high. A literal matches in any case; A|B is one word, either;
    /// […] is optional; a &lt;…&gt; value is one word, but a form's last element and a value named words, text or line take one or more.
    /// </summary>
    private static string WholeFormRisk(IEnumerable<ShowContextCommand> rows, string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var best = (Literals: -1, Risk: "high");
        foreach (var row in rows)
        {
            var literals = FormMatch(FormTokens(row.Verb), 0, words, 0);
            if (literals < 0) continue;
            if (literals > best.Literals || (literals == best.Literals && RiskRank(row.Risk) > RiskRank(best.Risk))) best = (literals, row.Risk);
        }
        return best.Risk;
    }

    private static List<string> FormTokens(string form)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var inValue = false;
        foreach (var ch in form)
        {
            if (inValue || ch == '<')
            {
                word.Append(ch);
                inValue = ch != '>';
                continue;
            }
            if (ch is ' ' or '[' or ']')
            {
                if (word.Length > 0) tokens.Add(word.ToString());
                word.Clear();
                if (ch != ' ') tokens.Add(ch.ToString());
                continue;
            }
            word.Append(ch);
        }
        if (word.Length > 0) tokens.Add(word.ToString());
        return tokens;
    }

    /// <summary>The most literal words a match of the form's tokens from <paramref name="i"/> over the line's words from <paramref name="j"/> takes, or -1 when none matches.</summary>
    private static int FormMatch(List<string> t, int i, string[] w, int j)
    {
        if (i == t.Count) return j == w.Length ? 0 : -1;
        if (t[i] == "]") return FormMatch(t, i + 1, w, j);
        if (t[i] == "[")
        {
            var depth = 0;
            var close = i;
            do depth += t[close] == "[" ? 1 : t[close] == "]" ? -1 : 0;
            while (depth > 0 && ++close < t.Count);
            return Math.Max(FormMatch(t, i + 1, w, j), FormMatch(t, close + 1, w, j));
        }
        var best = -1;
        foreach (var alt in t[i].Split('|'))
        {
            if (alt.StartsWith('<'))
            {
                var name = alt.Trim('<', '>');
                var many = t.Skip(i + 1).All(x => x == "]") || name is "words" or "text" or "line" || name.EndsWith(" words", StringComparison.Ordinal) || name.EndsWith(" line", StringComparison.Ordinal);
                for (var take = 1; j + take <= w.Length && (many || take == 1); take++) best = Math.Max(best, FormMatch(t, i + 1, w, j + take));
            }
            else if (j < w.Length && alt.Equals(w[j], StringComparison.OrdinalIgnoreCase) && FormMatch(t, i + 1, w, j + 1) is var rest and >= 0)
            {
                best = Math.Max(best, rest + 1);
            }
        }
        return best;
    }

    [Fact]
    public void NoReaderRatesALineLowerThanTheAirItChangesTodaysStagewatchOr13sWholeForms()
    {
        // Round 91.3. Stagewatch up to its whole-form matcher rates a line by a row's leading literal words, the first row
        // winning a tie, so a row appended ahead of a riskier one with the same leading words lowers every line that starts
        // with them (SCREEN <n> PVW … at review ahead of SCREEN <n> TAKE|CUT read SCREEN 2 TAKE as review). Show Bus 1.3's
        // whole-form rule reads the table as written. Every line — each listed form's, the screen verbs that act live and the
        // verbs the table leaves out — is rated by both, against the fourteen rows as they stood and the table as it is now.
        var json = ShowContextBuilder.FromEye(Graph(), "ab12cd34", "FOH", Now, Array.Empty<ShowLogEntry>(), Array.Empty<ShowContextPeer>()).ToJson();
        var rows = ShowContextBuilder.Commands;
        Assert.True(rows.Count <= 64, "Stagewatch's reader keeps a context's first 64 commands");

        // Where rows share their leading literal words, the first of them is the riskiest: the tie today's rater breaks by order.
        foreach (var group in rows.Select((r, at) => (Key: string.Join(' ', r.Verb.Split(' ').TakeWhile(w => !w.StartsWith('<') && !w.Contains('/', StringComparison.Ordinal))).ToUpperInvariant(), r.Risk, At: at))
                     .GroupBy(r => r.Key))
        {
            Assert.Equal(group.Max(r => RiskRank(r.Risk)), RiskRank(group.OrderBy(r => r.At).First().Risk));
        }

        var live = new[]
        {
            "SCREEN 2 LOOK Walk-in", "SCREEN 2 PRESET Walk-in", "SCREEN 2 PATTERN Grid", "SCREEN 2 ON", "SCREEN 2 OFF", "SCREEN 2 PROGRAM", "SCREEN 2 TAKE", "SCREEN 2 CUT",
            "SCREEN 2 ROLE confidence", "SCREEN 2 LABEL Stage left", "SCREEN 2 AUDIO HDMI 1", "SCREEN 2 TESTROUTE ON", "SCREEN 2 SIGNAL 3840x2160 50 RGB 8 SDR", "SCREEN 2 PVW",
            "LIBRARY Brand", "PVW LIBRARY Brand", "SCREEN 2 PVW LIBRARY Brand", "PVW", "LOOK #3", "CUE GO 12", "VIDEO STOP", "EYE LENS links",
            "WEB OPEN https://example.com/deck", "WEB TYPE hello", "WEB Ctrl+Shift+F5", "WEB ARM 1:23", "WEB GO https://example.com", "APPLY a3f9c2d1",
        };
        var lines = rows.SelectMany(r => Lines(r.Verb)).Concat(live).Distinct(StringComparer.Ordinal).ToList();

        // What may read below high: a read, the desk's own view, a cue on standby, words to a display with a receipt (the
        // fourteen's own call) and the preview alone. A Library tile is not among them: a brand kit's tile is the show's colours.
        var belowHigh = new HashSet<ShowActionKind>
        {
            ShowActionKind.EyeFocus, ShowActionKind.EyeNext, ShowActionKind.EyePrev, ShowActionKind.EyeReset, ShowActionKind.EyeLens,
            ShowActionKind.CueStandby, ShowActionKind.StageMessage,
            ShowActionKind.ApplyLookToPreview, ShowActionKind.ScreenToPreview, ShowActionKind.ScreenStageLook, ShowActionKind.ScreenStagePreset,
            ShowActionKind.ScreenStagePattern, ShowActionKind.ScreenStageProgram, ShowActionKind.ScreenStageReset,
        };
        var fourteen = rows.Take(14).ToList();
        var lowerToday = new List<string>();
        foreach (var line in lines)
        {
            var today = TodaysRisk(json, line);
            var whole = WholeFormRisk(rows, line);
            var parsed = ControlProtocol.Parse(line);
            var changesAir = parsed.IsAction && !belowHigh.Contains(parsed.Action.Kind);
            if (changesAir)
            {
                Assert.True(today == "high", $"'{line}' ({parsed.Action.Kind}) changes the air and reads {today} on today's Stagewatch");
                Assert.True(whole == "high", $"'{line}' ({parsed.Action.Kind}) changes the air and reads {whole} under 1.3's rule");
            }
            if (RiskRank(today) < RiskRank(TodaysRisk(json, line, rows: 14))) lowerToday.Add(line);
            if (RiskRank(whole) < RiskRank(WholeFormRisk(fourteen, line))) Assert.False(changesAir, $"'{line}' reads {whole} under 1.3's rule, lower than before the rows were appended");
        }
        // The one line today's Stagewatch reads lower than before is this desk's own APPLY, at the review its row says.
        Assert.Equal(new[] { "APPLY a3f9c2d1" }, lowerToday);

        // Under 1.3's rule each form's lines read its own risk: the staged verbs at review, the brand-kit forms high.
        foreach (var row in rows) Assert.All(Lines(row.Verb), line => Assert.Equal((line, row.Risk), (line, WholeFormRisk(rows, line))));

        // Today: every SCREEN line reads its first row's risk, high; the staged verbs read high until the whole-form matcher
        // (over-rated, never lowered — docs/OPEN.md L71); APPLY reads review and the Eye's moves low, as the rows say.
        Assert.All(lines.Where(l => l.StartsWith("SCREEN ", StringComparison.Ordinal)), l => Assert.Equal((l, "high"), (l, TodaysRisk(json, l))));
        Assert.Equal(("high", "review"), (TodaysRisk(json, "PVW LOOK Walk-in"), WholeFormRisk(rows, "PVW LOOK Walk-in")));
        Assert.Equal(("high", "review"), (TodaysRisk(json, "SCREEN 2 PVW PATTERN Grid"), WholeFormRisk(rows, "SCREEN 2 PVW PATTERN Grid")));
        Assert.Equal(("review", "review"), (TodaysRisk(json, "APPLY a3f9c2d1"), WholeFormRisk(rows, "APPLY a3f9c2d1")));
        Assert.Equal(("low", "low"), (TodaysRisk(json, "EYE LENS links"), WholeFormRisk(rows, "EYE LENS links")));
        Assert.Equal(("high", "high"), (TodaysRisk(json, "WEB OPEN https://example.com/deck"), WholeFormRisk(rows, "WEB OPEN https://example.com/deck")));
    }

    [Fact]
    public void AReaderKeeps256OfAPeersCommandsEachVerbWholeOrDroppedAndCountsWhatItDidNotKeep()
    {
        static string Context(IEnumerable<string> commands) => "{\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"pm-a1b2\",\"name\":\"Stage left\"},\"commands\":[" + string.Join(",", commands) + "]}";
        static string Row(string verb, string risk = "review", string words = "w") => JsonSerializer.Serialize(new { verb, risk, words });

        // PlayMap's busy context: 199 rows, verbs to 307 characters and words to 403 — every one kept whole (64 kept before).
        var busy = Enumerable.Range(0, 199).Select(i => Row($"V{i} " + new string('x', i == 7 ? 304 : 20), i % 3 == 0 ? "high" : "review", new string('w', i == 9 ? 403 : 40))).ToList();
        var c = ShowContext.Parse(Context(busy))!;
        Assert.Equal(199, c.Commands.Count);
        Assert.Equal(0, c.CommandsDropped);
        Assert.Equal(307, c.Commands[7].Verb.Length);
        Assert.Equal(403, c.Commands[9].Words.Length);
        Assert.Equal("V198 " + new string('x', 20), c.Commands[^1].Verb);

        // 300 rows: the first 256 kept in the order sent, 44 counted.
        c = ShowContext.Parse(Context(Enumerable.Range(0, 300).Select(i => Row($"V{i}"))))!;
        Assert.Equal(ShowContext.MaxCommands, c.Commands.Count);
        Assert.Equal(Enumerable.Range(0, 256).Select(i => $"V{i}"), c.Commands.Select(x => x.Verb));
        Assert.Equal(44, c.CommandsDropped);

        // A verb at 512 is kept; at 513 it is dropped whole, never cut, and counted; so is an empty one. Words cut at 512.
        c = ShowContext.Parse(Context(new[]
        {
            Row(new string('a', 512), "low", new string('w', 600)),
            Row(new string('b', 513)),
            Row(""),
            "{\"risk\":\"low\"}",
            Row("PANIC", "HIGH!"),
            Row("NOTE <words>", "Low"),
            "{\"verb\":\"NO RISK\"}",
            "null",
        }))!;
        Assert.Equal(new[] { new string('a', 512), "PANIC", "NOTE <words>", "NO RISK" }, c.Commands.Select(x => x.Verb));
        Assert.Equal(3, c.CommandsDropped);
        Assert.Equal(ShowContext.MaxWordsChars, c.Commands[0].Words.Length);
        Assert.Equal(new[] { "low", "high", "low", "high" }, c.Commands.Select(x => x.Risk));    // an unknown risk is high, never lower — and so is none
        Assert.DoesNotContain(c.Commands, x => x.Verb.StartsWith('b'));

        // 256 kept after the dropped ones: what is dropped leaves room, never a gap in what is kept.
        c = ShowContext.Parse(Context(new[] { Row(new string('b', 600)) }.Concat(Enumerable.Range(0, 260).Select(i => Row($"V{i}")))))!;
        Assert.Equal(256, c.Commands.Count);
        Assert.Equal("V0", c.Commands[0].Verb);
        Assert.Equal(5, c.CommandsDropped);

        // What this desk read is never written back: its own context's shape is unchanged.
        Assert.DoesNotContain("CommandsDropped", c.ToJson(), StringComparison.Ordinal);
        Assert.Equal(ShowContext.MaxCommands, ShowContext.KeptCommands(Enumerable.Range(0, 1000).Select(i => new ShowContextCommand { Verb = $"V{i}" })).Kept.Count);
    }

    [Fact]
    public void TheBriefListsThirtyTwoOfAPeersVerbsInItsOrderAndSaysHowManyItLeftOut()
    {
        static EyeShowApp App(ShowContext context) => new("pm-a1b2", "PlayMap", "Stage left", ShowBusState.Live, true, "") { Context = context };
        static string Proposed(ShowContext context) => ShowBusView.BriefLines(new[] { App(context) }).Single(l => l.StartsWith("  may be proposed to it: ", StringComparison.Ordinal));

        var rows = Enumerable.Range(0, 199).Select(i => new ShowContextCommand { Verb = $"V{i}", Risk = i < 3 ? "high" : "review" }).ToList();
        var line = Proposed(StagewatchContext() with { Commands = rows });
        Assert.StartsWith("  may be proposed to it: V0 (high); V1 (high); V2 (high); V3 (review);", line, StringComparison.Ordinal);
        Assert.Contains("; V31 (review) — 167 more left out here", line, StringComparison.Ordinal);
        Assert.DoesNotContain("V32 ", line, StringComparison.Ordinal);
        Assert.EndsWith("— 167 more left out here", line, StringComparison.Ordinal);

        // A context this desk read short: the rows it did not keep are counted among those left out, and said.
        var read = ShowContext.Parse("{\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"pm-a1b2\",\"name\":\"Stage left\"},\"commands\":["
                                     + string.Join(",", Enumerable.Range(0, 300).Select(i => $"{{\"verb\":\"V{i}\",\"risk\":\"low\"}}")) + "]}")!;
        Assert.EndsWith("V31 (low) — 268 more left out here (44 not kept by this desk: a verb empty or over 512 characters, or past the first 256)", Proposed(read), StringComparison.Ordinal);

        // Thirty-two or fewer: listed whole, nothing said about what was left out.
        Assert.Equal("  may be proposed to it: " + string.Join("; ", rows.Take(32).Select(r => $"{r.Verb} ({r.Risk})")), Proposed(StagewatchContext() with { Commands = rows.Take(32).ToList() }));
        Assert.Equal(32, ShowBusView.BriefCommands);
    }

    // ---- the Eye draws the other apps -------------------------------------------------------------

    [Fact]
    public void TheEyeDrawsALinkedStagewatchWithItsOwnLightAndThingsAndHeardAppsGrey()
    {
        var graph = Graph(
            new EyeShowApp("a1b2c3d4", "Stagewatch", "Hall A", ShowBusState.Live, true, "") { Context = StagewatchContext() },
            new EyeShowApp("77aa77aa", "AGMessage", "AGM", ShowBusState.Live, false, "10.0.0.20"),
            new EyeShowApp("55bb55bb", "Slides agent", "Lectern", ShowBusState.Stale, false, ""),
            new EyeShowApp("66cc66cc", "Slides agent", "Side", ShowBusState.Gone, false, ""));
        var sw = graph.Nodes.Single(n => n.Id == "app:a1b2c3d4");
        Assert.Equal(EyeKind.ShowApp, sw.Kind);
        Assert.Equal(EyePlane.Control, sw.Plane);
        Assert.Equal(CheckLight.Amber, sw.Light);                                                // the context's own light
        Assert.Equal("Keynote — Ana · 12:30 left · Lighting standing by", sw.Sub);
        Assert.Contains(sw.Words, w => w.Contains("hears AGMessage AGM", StringComparison.Ordinal));
        Assert.Equal(CheckLight.Amber, graph.Nodes.Single(n => n.Id == "app:a1b2c3d4:cue:lighting").Light);
        Assert.Contains(graph.Edges, e => e.From == "app:a1b2c3d4:timer" && e.To == "app:a1b2c3d4:cue:lighting" && e.Kind == EyeEdgeKind.Drives);
        Assert.Contains(graph.Edges, e => e.From == "app:a1b2c3d4" && e.To == EyeGraph.DeskId && e.Kind == EyeEdgeKind.Follows);
        Assert.Equal(CheckLight.Grey, graph.Nodes.Single(n => n.Id == "app:77aa77aa").Light);
        Assert.Equal(CheckLight.Amber, graph.Nodes.Single(n => n.Id == "app:55bb55bb").Light);
        Assert.Equal(CheckLight.Red, graph.Nodes.Single(n => n.Id == "app:66cc66cc").Light);
        Assert.Contains(graph.Lines(), l => l.Contains("Hall A", StringComparison.Ordinal));
    }

    [Fact]
    public void TheShowNetworkIsOneProjectionForTheEyeTheNodesPageTheContextAndTheBrief()
    {
        var peers = new ShowBusPeers();
        peers.Hear(Hello("main", "a1b2c3d4", "Hall A", http: 9924), "10.0.0.9", Now, "self");
        peers.Hear(Hello("patterns", "ffff0000", "Backup", http: 9696, wire: 9697), "10.0.0.6", Now, "self");     // another Patterns: the beacon draws it
        peers.Hear(Hello("messenger", "77aa77aa", "AGM", http: 3023), "10.0.0.20", Now, "self");
        var sight = new StagewatchSight("Hall A", "10.0.0.9:9924", ShowBusState.Live, null, 0, StagewatchContext(), "");
        var apps = ShowBusView.Apps(peers.Snapshot(Now), Now, sight);
        Assert.Equal(new[] { "Stagewatch", "AGMessage" }, apps.Select(a => a.KindWords));
        Assert.True(apps[0].Linked);
        Assert.Equal(0, apps[0].Context!.At);                                                     // a context's moment does not rebuild the Eye
        Assert.False(apps[1].Linked);

        var ctxPeers = ShowBusView.Peers(apps);
        Assert.Equal(("main", "amber", "Keynote — Ana · 12:30 left · Lighting standing by"), (ctxPeers[0].Kind, ctxPeers[0].Light, ctxPeers[0].Headline));
        Assert.Equal(("messenger", "grey", "heard on the show network"), (ctxPeers[1].Kind, ctxPeers[1].Light, ctxPeers[1].Headline));

        var brief = ShowBusView.BriefLines(apps);
        Assert.Contains("Stagewatch Hall A — linked — amber: Keynote — Ana · 12:30 left · Lighting standing by", brief);
        Assert.Contains("  · Lighting (department) — amber: standing by: LX 12", brief);
        Assert.Contains("  may be proposed to it: signal go all (high)", brief);
        Assert.Contains("  it hears AGMessage AGM — green: 12 in the room", brief);
        Assert.Contains("AGMessage AGM — heard, not linked", brief);

        var card = ShowAppCard.Of(apps[0]);
        Assert.Equal("#35E0D0", card.Hue);
        Assert.StartsWith("linked · Keynote", card.Line, StringComparison.Ordinal);
        Assert.Equal("2 apps on the show network. Stagewatch link: following Hall A.", ShowAppCard.NetworkLine(2, true, "Stagewatch link: following Hall A."));
        Assert.StartsWith("Not listening", ShowAppCard.NetworkLine(0, false, ""), StringComparison.Ordinal);

        // A Stagewatch reached by a typed address that never said hello is still drawn.
        var typed = ShowBusView.Apps(Array.Empty<ShowBusPeer>(), Now, sight);
        Assert.Equal("a1b2c3d4", typed.Single().Id);
        Assert.True(typed.Single().Linked);
    }

    // ---- the Stagewatch the stage timer follows ---------------------------------------------------

    [Fact]
    public void AStagewatchFrameIsReadAndABeatOrAPongIsNotOne()
    {
        var f = StagewatchFrame.Parse("{\"type\":\"state\",\"v\":1,\"seq\":42,\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":750,\"total\":1800,\"text\":\"12:30\",\"phase\":\"warning\",\"color\":\"#FFB000\",\"title\":\"Keynote — Ana\",\"next\":\"Panel\",\"message\":\"Wrap up\",\"messageId\":7,\"messageAck\":\"seen\"}");
        Assert.NotNull(f);
        Assert.Equal((1, 42L, "countdown", "running", 750.0, "Keynote — Ana", 7L, "seen"), (f!.V, f.Seq, f.Mode, f.State, f.Seconds, f.Title, f.MessageId, f.MessageAck));
        Assert.True(f.Live);
        Assert.False(StagewatchFrame.Parse("{\"type\":\"state\",\"seconds\":5,\"live\":false}")!.Live);
        Assert.Null(StagewatchFrame.Parse("{\"type\":\"beat\"}"));
        Assert.Null(StagewatchFrame.Parse("{\"type\":\"pong\",\"t\":1,\"t2\":2,\"t3\":3}"));
        Assert.Null(StagewatchFrame.Parse("not json"));
        Assert.Equal("beat", StagewatchFrame.TypeOf("{\"type\":\"beat\"}"));
        Assert.Equal((1L, 2L, 3L), StagewatchFrame.Pong("{\"type\":\"pong\",\"t\":1,\"t2\":2,\"t3\":3}"));
        Assert.Null(StagewatchFrame.Pong("{\"type\":\"pong\",\"t\":1}"));
        Assert.Equal("{\"cmd\":\"ping\",\"t\":1760101234567}", StagewatchFrame.Ping(1760101234567));

        // Round 89: the stuck beat, byte for byte as Stagewatch sends it (WebServer.cs StuckBeat) — and nothing else is one.
        Assert.True(StagewatchFrame.IsStuckBeat("{\"type\":\"beat\",\"live\":false}"));
        Assert.True(StagewatchFrame.IsStuckBeat("{ \"live\" : false, \"type\" : \"beat\" }"));
        Assert.Null(StagewatchFrame.Parse("{\"type\":\"beat\",\"live\":false}"));                // a stuck beat is not a frame
        Assert.False(StagewatchFrame.IsStuckBeat("{\"type\":\"beat\"}"));                         // the plain beat
        Assert.False(StagewatchFrame.IsStuckBeat("{\"type\":\"beat\",\"live\":true}"));
        Assert.False(StagewatchFrame.IsStuckBeat("{\"type\":\"beat\",\"live\":\"false\"}"));      // only the literal false
        Assert.False(StagewatchFrame.IsStuckBeat("{\"type\":\"beat\",\"live\":0}"));
        Assert.False(StagewatchFrame.IsStuckBeat("{\"type\":\"state\",\"live\":false}"));
        Assert.False(StagewatchFrame.IsStuckBeat("{\"type\":\"pong\",\"live\":false}"));
        Assert.False(StagewatchFrame.IsStuckBeat("[{\"type\":\"beat\",\"live\":false}]"));
        Assert.False(StagewatchFrame.IsStuckBeat("{\"type\":\"beat\",\"live\":false"));           // malformed
        Assert.False(StagewatchFrame.IsStuckBeat(""));
        Assert.False(StagewatchFrame.IsStuckBeat(null));
    }

    [Fact]
    public void AStuckStagewatchShowsNoTimeInEveryReaderWhileItsLinkStaysLiveAndAmber()
    {
        var peers = new ShowBusPeers();
        peers.Hear(Hello("main", "a1b2c3d4", "Hall A", http: 9924), "10.0.0.9", Now, "self");
        var frame = new StagewatchFrame { Seq = 3, Mode = "countdown", State = "running", Seconds = 750, Total = 1800, Text = "12:30", Title = "Keynote — Ana" };
        // As the link's sight folds its stuck mark in: the held frame no longer live, the mark beside it.
        var sight = new StagewatchSight("Hall A", "10.0.0.9:9924", ShowBusState.Live, frame with { Live = false }, 100, StagewatchContext("green"), "") { Stuck = true };

        // The stage: no time from it, and still following — its verbs stay the Stagewatch's.
        Assert.True(sight.Following);
        Assert.True(sight.StuckLive);
        Assert.Equal(StageTimerPhase.Idle, StageFollow.Time(sight.Frame!, sight.FrameAtSeconds, 101).Phase);
        Assert.Equal("following Stagewatch Hall A — it says its show is stuck; the time is not shown", StageFollow.Words(sight));

        // The projection: the link live, its context no better than amber, its counting things with no time.
        var app = Assert.Single(ShowBusView.Apps(peers.Snapshot(Now), Now, sight));
        Assert.True(app.Stuck);
        Assert.Equal(ShowBusState.Live, app.State);
        Assert.Equal("amber", app.Context!.Light);                                                   // the worse of its own green and amber
        Assert.Equal(ShowBusView.StuckWords, app.Context.Headline);                                  // a headline holds a count: not shown
        var timer = app.Context.Items.Single(i => i.Id == "timer");
        Assert.Equal(("red", ShowBusView.NoTimeWords), (timer.Light, timer.State));
        var lighting = app.Context.Items.Single(i => i.Id == "cue:lighting");
        Assert.Equal(("amber", "standing by: LX 12"), (lighting.Light, lighting.State));              // its other things keep their words
        Assert.Equal(app.Context.ToJson(), ShowBusView.Apps(peers.Snapshot(Now), Now, sight).Single().Context!.ToJson());   // the same in, the same out: no rebuild

        // The context's peers row, the brief, the Nodes card and the Eye give one verdict.
        var row = Assert.Single(ShowBusView.Peers(new[] { app }));
        Assert.Equal(("amber", ShowBusView.StuckWords), (row.Light, row.Headline));
        var brief = ShowBusView.BriefLines(new[] { app });
        Assert.Contains("Stagewatch Hall A — linked — amber: says its show is stuck — no time shown", brief);
        Assert.Contains("  · Keynote — Ana (timer) — red: no time shown — its show is stuck", brief);
        var card = ShowAppCard.Of(app);
        Assert.Equal(("#FFB020", "linked · says its show is stuck — no time shown"), (card.Hue, card.Line));
        var graph = Graph(app);
        var node = graph.Nodes.Single(n => n.Id == "app:a1b2c3d4");
        Assert.Equal((CheckLight.Amber, ShowBusView.StuckWords), (node.Light, node.Sub));
        var edge = graph.Edges.Single(e => e.From == "app:a1b2c3d4" && e.To == EyeGraph.DeskId);
        Assert.Equal((EyeEdgeKind.Follows, CheckLight.Amber), (edge.Kind, edge.Light));                // the link: live, amber
        var count = graph.Nodes.Single(n => n.Id == "app:a1b2c3d4:timer");
        Assert.Equal((CheckLight.Red, ShowBusView.NoTimeWords), (count.Light, count.Sub));

        // Never red for the stall alone — red only when its own light is red.
        var red = Assert.Single(ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { Context = StagewatchContext("red") }));
        Assert.Equal("red", red.Context!.Light);
        Assert.Equal("#B03030", ShowAppCard.Of(red).Hue);
        Assert.Equal(CheckLight.Red, Graph(red).Nodes.Single(n => n.Id == "app:a1b2c3d4").Light);

        // Before its context is read: amber and said all the same.
        var bare = Assert.Single(ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { Context = null }));
        Assert.Equal(("amber", ShowBusView.StuckWords), (ShowBusView.Peers(new[] { bare })[0].Light, ShowBusView.Peers(new[] { bare })[0].Headline));
        Assert.Contains("Stagewatch Hall A — linked — amber: says its show is stuck — no time shown", ShowBusView.BriefLines(new[] { bare }));
        Assert.Equal(CheckLight.Amber, Graph(bare).Nodes.Single(n => n.Id == "app:a1b2c3d4").Light);
        Assert.Equal("#FFB020", ShowAppCard.Of(bare).Hue);

        // A typed address that never said hello carries the same verdict.
        var typed = Assert.Single(ShowBusView.Apps(Array.Empty<ShowBusPeer>(), Now, sight));
        Assert.True(typed.Stuck);
        Assert.Equal("amber", typed.Context!.Light);

        // The link gone quiet says that instead; the stuck mark adds nothing to a link that is not live.
        var stale = Assert.Single(ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { State = ShowBusState.Stale }));
        Assert.False(stale.Stuck);
        Assert.Equal("#FFB020", ShowAppCard.Of(stale).Hue);
        Assert.Equal("quiet", ShowAppCard.Of(stale).Line[..5]);

        // Its next frame: the mark cleared, the time and its own light back.
        var back = Assert.Single(ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { Frame = frame, Stuck = false }));
        Assert.False(back.Stuck);
        Assert.Equal(("green", "Keynote — Ana · 12:30 left · Lighting standing by"), (back.Context!.Light, back.Context.Headline));
        Assert.Equal("#35E0D0", ShowAppCard.Of(back).Hue);
        Assert.Equal(CheckLight.Green, Graph(back).Edges.Single(e => e.From == "app:a1b2c3d4" && e.To == EyeGraph.DeskId).Light);
    }

    [Fact]
    public void AStagewatchStuckBeforeItsFirstFrameLeavesTheDesksOwnTimer()
    {
        var sight = new StagewatchSight("Hall A", "10.0.0.9:9924", ShowBusState.Live, null, 0, null, "") { Stuck = true };
        Assert.False(sight.Following);                                                              // no frame: the desk's timer and its verbs
        Assert.True(sight.StuckLive);
        Assert.Equal("Stagewatch Hall A says its show is stuck — this desk's own stage timer runs until its first frame", StageFollow.Words(sight));
        var app = Assert.Single(ShowBusView.Apps(Array.Empty<ShowBusPeer>(), Now, sight));
        Assert.True(app.Stuck);
        Assert.Equal("#FFB020", ShowAppCard.Of(app).Hue);
        // A stuck mark on a link that is not live says only what the link says.
        Assert.Contains("not answering", StageFollow.Words(sight with { State = ShowBusState.Gone }), StringComparison.Ordinal);
        Assert.False((sight with { State = ShowBusState.Stale }).StuckLive);
    }

    [Fact]
    public void OnlyTheCountingThingsOfAStuckAppLoseTheirTime()
    {
        foreach (var kind in new[] { "timer", "schedule", "playlist", "chase", "timecode", "clock", " Timer " })
            Assert.True(ShowBusView.IsCounting(kind), kind);
        foreach (var kind in new[] { "department", "cue", "message", "screen", "", null })
            Assert.False(ShowBusView.IsCounting(kind), kind ?? "null");
        Assert.Equal("amber", ShowBusView.StuckLight("green"));
        Assert.Equal("amber", ShowBusView.StuckLight("grey"));
        Assert.Equal("amber", ShowBusView.StuckLight(null));
        Assert.Equal("amber", ShowBusView.StuckLight("amber"));
        Assert.Equal("red", ShowBusView.StuckLight("red"));
        var once = ShowBusView.NoTime(StagewatchContext("green"));
        Assert.Equal(once.ToJson(), ShowBusView.NoTime(once).ToJson());                              // idempotent: a reader may apply it again
        Assert.Equal(StagewatchContext("green").Links.Single().Light, once.Links.Single().Light);     // the links between its things keep their lights
    }

    [Fact]
    public void TheStageTimerCountsOnFromTheFrameAndShowsNothingFromAStuckStagewatch()
    {
        var running = new StagewatchFrame { Mode = "countdown", State = "running", Seconds = 90, Total = 600, Phase = "warning" };
        var t = StageFollow.Time(running, frameAtSeconds: 100, nowSeconds: 101.5);
        Assert.Equal(StageTimerPhase.Running, t.Phase);
        Assert.Equal(88.5, t.RemainingSeconds, 3);
        Assert.Equal("amber", t.Colour);
        Assert.Equal(1 - 88.5 / 600, t.Progress01, 3);

        var over = StageFollow.Time(running with { Seconds = 1, Phase = "critical" }, 100, 103);
        Assert.Equal(StageTimerPhase.Over, over.Phase);
        Assert.Equal("red", over.Colour);

        var paused = StageFollow.Time(running with { State = "paused" }, 100, 160);
        Assert.Equal((StageTimerPhase.Paused, 90.0), (paused.Phase, paused.RemainingSeconds));

        var countup = StageFollow.Time(new StagewatchFrame { Mode = "countup", State = "running", Seconds = 30 }, 100, 102);
        Assert.Equal(32, countup.RemainingSeconds, 3);

        Assert.Equal(StageTimerPhase.Idle, StageFollow.Time(running with { Live = false }, 100, 100).Phase);
        Assert.Equal(StageTimerPhase.Idle, StageFollow.Time(running with { State = "idle" }, 100, 100).Phase);
        Assert.Equal(StageTimerPhase.Over, StageFollow.Time(running with { State = "expired", Seconds = -12 }, 100, 100).Phase);
        Assert.Equal("green", StageFollow.Colour(running with { Phase = "normal" }, StageTimerPhase.Running));
    }

    [Fact]
    public void ThePageSaysWhomItFollowsOrWhyNot()
    {
        var frame = new StagewatchFrame { State = "running", Seconds = 60 };
        Assert.Equal("", StageFollow.Words(StagewatchSight.None));
        Assert.Equal("following Stagewatch Hall A", StageFollow.Words(new StagewatchSight("Hall A", "10.0.0.9:9924", ShowBusState.Live, frame, 0, null, "")));
        Assert.Contains("stuck", StageFollow.Words(new StagewatchSight("Hall A", "x:1", ShowBusState.Live, frame with { Live = false }, 0, null, "")), StringComparison.Ordinal);
        Assert.Contains("held back", StageFollow.Words(new StagewatchSight("Hall A", "x:1", ShowBusState.Stale, frame, 0, null, "")), StringComparison.Ordinal);
        Assert.Contains("own stage timer runs", StageFollow.Words(new StagewatchSight("Hall A", "x:1", ShowBusState.Gone, frame, 0, null, "")), StringComparison.Ordinal);
        Assert.True(new StagewatchSight("Hall A", "x:1", ShowBusState.Live, frame, 0, null, "").Following);
        Assert.False(new StagewatchSight("Hall A", "x:1", ShowBusState.Stale, frame, 0, null, "").Following);
    }

    [Theory]
    [InlineData("10.0.0.9:9924", "10.0.0.9", 9924)]
    [InlineData("10.0.0.9", "10.0.0.9", 9924)]
    [InlineData("hall-a.local:8080", "hall-a.local", 8080)]
    [InlineData("hall-a", "hall-a", 9924)]
    [InlineData("[fe80::1]:9924", "fe80::1", 9924)]
    [InlineData("fe80::1", "fe80::1", 9924)]
    public void ATypedAddressIsAHostAndAPort(string typed, string host, int port)
        => Assert.Equal((host, port), StageFollow.ParseAddress(typed));

    [Theory]
    [InlineData("")]
    [InlineData("http://10.0.0.9:9924")]
    [InlineData("10.0.0.9:0")]
    [InlineData("10.0.0.9:99999")]
    [InlineData("a b")]
    [InlineData("user@host")]
    public void ATypedAddressThatIsNotOneIsRefused(string typed) => Assert.Null(StageFollow.ParseAddress(typed));

    // ---- strengthening: OSC, the watchdog, the first run, the push ---------------------------------

    /// <summary>
    /// OSC cannot pair, and a proof grants nothing to an address (ADR-017): on a paired desk its verbs come from this
    /// machine (when the desk trusts its own) and the operator's OSC ALLOW list, and from nowhere else — the gate is
    /// handed no record of who proved the token on the wire or the web, so none can widen it.
    /// </summary>
    [Fact]
    public void OnAPairedDeskOscMovesTheShowOnlyFromThisMachineOrTheAllowList()
    {
        const string token = "K7QM-3XWD-P9RA";
        var from = System.Net.IPAddress.Parse("10.0.0.9");
        Assert.True(OscGate.Allows("", from, false, ""));                                                          // an open desk: as before
        Assert.False(OscGate.Allows(token, from, false, ""));                                                      // paired, unlisted: refused
        Assert.False(OscGate.Allows(token, from, true, ""));                                                       // trusting loopback trusts no other machine
        Assert.True(OscGate.Allows(token, from, false, "10.0.0.8, 10.0.0.9;bogus"));
        Assert.True(OscGate.Allows(token, System.Net.IPAddress.Parse("::ffff:10.0.0.9"), false, "10.0.0.9"));      // mapped
        Assert.True(OscGate.Allows(token, System.Net.IPAddress.Parse("10.0.0.9"), false, "::ffff:10.0.0.9"));      // listed mapped
        Assert.False(OscGate.Allows(token, System.Net.IPAddress.Parse("10.0.0.90"), false, "10.0.0.9"));           // an address, never a prefix
        Assert.True(OscGate.Allows(token, System.Net.IPAddress.Loopback, true, ""));
        Assert.True(OscGate.Allows(token, System.Net.IPAddress.IPv6Loopback, true, ""));
        Assert.False(OscGate.Allows(token, System.Net.IPAddress.Loopback, false, ""));
        Assert.Equal(new HashSet<string> { "10.0.0.8", "10.0.0.9" }, OscGate.AllowList("10.0.0.8, 10.0.0.9;bogus"));

        // The words every reader says it with: the refusal names the list and the rule; the list's count reads plainly.
        Assert.Contains("OSC ALLOW", OscGate.NotListed, StringComparison.Ordinal);
        Assert.Contains("grants nothing to an address", OscGate.NotListed, StringComparison.Ordinal);
        Assert.DoesNotContain("paired on the wire or the web, or", OscGate.NotListed, StringComparison.Ordinal);
        Assert.Contains("grants nothing to an address", OscGate.PairedRule, StringComparison.Ordinal);
        Assert.Equal("OSC ALLOW names no address, so a controller on another machine is refused until its address is listed", OscGate.Listed(0));
        Assert.Equal("OSC ALLOW names 1 address", OscGate.Listed(1));
        Assert.Equal("OSC ALLOW names 3 addresses", OscGate.Listed(3));
        Assert.StartsWith("not paired — ", OscGate.NotListed, StringComparison.Ordinal);                        // the walk's words, and a controller's search

        // ADR-016: the show's state answers whom the verbs come from — its own words and log line, the same bound.
        Assert.StartsWith("not paired — ", OscGate.ReadNotListed, StringComparison.Ordinal);
        Assert.Contains("STATUS", OscGate.ReadNotListed, StringComparison.Ordinal);
        Assert.Contains("OSC ALLOW", OscGate.ReadNotListed, StringComparison.Ordinal);
        Assert.Contains("grants nothing to an address", OscGate.ReadNotListed, StringComparison.Ordinal);
        Assert.Contains("STATUS answers those addresses alone", OscGate.StateRule, StringComparison.Ordinal);
        Assert.Contains("OSC: STATUS from 10.0.0.9 refused", OscGate.ReadRefusedLine(SaidOnceAnswer.Say, "10.0.0.9"), StringComparison.Ordinal);
        Assert.Contains("no more addresses are named", OscGate.ReadRefusedLine(SaidOnceAnswer.Full, "10.0.0.9"), StringComparison.Ordinal);
        Assert.Null(OscGate.ReadRefusedLine(SaidOnceAnswer.Quiet, "10.0.0.9"));
    }

    /// <summary>
    /// The allow-list is read as the bind address is (<see cref="BindAddress.TryAddress"/>): one whole address per entry.
    /// The parser's shorthand would take '192.168.1' as 192.168.0.1 and '10' as 0.0.0.10, and its octal '010' as 8, so a
    /// typo would read as a listed address on every reader while the controller it meant stayed refused. Whatever is not
    /// an address is left out, named and never counted.
    /// </summary>
    [Fact]
    public void TheOscAllowListTakesWholeAddressesAndNamesWhatItLeftOut()
    {
        const string token = "K7QM-3XWD-P9RA";
        var list = OscGate.Read("192.168.1, 10; studio-mac.local 10.0.0.9, 0x0A.0.0.7, 192.168.001.010, 10, ::ffff:10.0.0.12");
        Assert.Equal(new HashSet<string> { "10.0.0.9", "192.168.1.10", "10.0.0.12" }, list.Addresses);
        Assert.Equal(new[] { "192.168.1", "10", "studio-mac.local", "0x0A.0.0.7" }, list.LeftOut);           // each once, in the order written
        Assert.Equal(list.Addresses, OscGate.AllowList("192.168.1, 10; studio-mac.local 10.0.0.9, 0x0A.0.0.7, 192.168.001.010, 10, ::ffff:10.0.0.12"));

        Assert.False(OscGate.Allows(token, System.Net.IPAddress.Parse("192.168.0.1"), false, "192.168.1"));     // the shorthand never stands for an address
        Assert.False(OscGate.Allows(token, System.Net.IPAddress.Parse("0.0.0.10"), false, "10"));
        Assert.False(OscGate.Allows(token, System.Net.IPAddress.Parse("8.0.0.9"), false, "010.0.0.9"));          // never octal
        Assert.True(OscGate.Allows(token, System.Net.IPAddress.Parse("10.0.0.9"), false, "010.0.0.9"));          // a leading zero is decimal

        Assert.Equal("OSC ALLOW names 3 addresses; '192.168.1' is not an address — left out; '10' is not an address — left out; 'studio-mac.local' is not an address — left out; and 1 more",
            OscGate.Listed(list.Addresses.Count, list.LeftOut));
        var none = OscGate.Read("192.168.1");
        Assert.Empty(none.Addresses);
        Assert.Equal(OscGate.Listed(0) + "; '192.168.1' is not an address — left out", OscGate.Listed(0, none.LeftOut));
        Assert.Empty(OscGate.Read("").LeftOut);
        Assert.Empty(OscGate.Read(null).Addresses);
    }

    /// <summary>
    /// The paired desk's OSC note, the one sentence the Super Check's row and the Eye's node share: the rule, its questions
    /// open, what the list names — and, while the port listens on every interface with someone listed, that a listed
    /// address is not a proof (a datagram's source is whatever its sender writes; only the bind narrows who can write it).
    /// </summary>
    [Fact]
    public void APairedDesksOscNoteSaysAListedAddressIsNotAProofUntilThePortIsBound()
    {
        var unboundListed = OscGate.PairedWords(1, Array.Empty<string>(), bound: false);
        Assert.StartsWith(OscGate.PairedRule + "; " + OscGate.StateRule + ". OSC ALLOW names 1 address", unboundListed, StringComparison.Ordinal);
        Assert.Contains("A listed address is not a proof", unboundListed, StringComparison.Ordinal);
        Assert.Contains("bind the control ports", unboundListed, StringComparison.Ordinal);

        Assert.DoesNotContain("not a proof", OscGate.PairedWords(1, Array.Empty<string>(), bound: true), StringComparison.Ordinal);    // the bind keeps it to the control network
        Assert.DoesNotContain("not a proof", OscGate.PairedWords(0, Array.Empty<string>(), bound: false), StringComparison.Ordinal);   // nobody listed: nobody to forge
        Assert.Contains("'192.168.1' is not an address — left out", OscGate.PairedWords(0, new[] { "192.168.1" }, bound: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused verb names its address in the log once, for at most <see cref="OscGate.RefusedNamedMost"/> addresses; the
    /// first new address past the bound writes one line that says no more are named, and nothing after it. A forged
    /// flood of sources is a bounded set and a bounded log, and the log never seems to have named them all.
    /// </summary>
    [Fact]
    public void RefusedVerbsNameEachAddressOnceAtMostTheBoundThenOneLineSaysNoMore()
    {
        var said = new SaidOnce(OscGate.RefusedNamedMost);
        var lines = new List<string>();
        for (var round = 0; round < 2; round++)
        {
            for (var i = 1; i <= 100; i++)
            {
                var address = $"10.0.0.{i}";
                if (OscGate.RefusedLine(said.Note(address), address) is { } line) lines.Add(line);
            }
        }
        Assert.Equal(OscGate.RefusedNamedMost + 1, lines.Count);
        Assert.Equal(64, lines.Count(l => l.StartsWith("OSC: a verb from 10.0.", StringComparison.Ordinal) && l.EndsWith("Said once for this address.", StringComparison.Ordinal)));
        Assert.Equal(64, lines.Take(64).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("OSC: verbs refused from more than 64 addresses — no more addresses are named in this log; the Remote page's OSC line counts every refusal.", lines[^1]);
        Assert.Contains("OSC: a verb from 10.0.0.1 refused", lines[0], StringComparison.Ordinal);
        Assert.Null(OscGate.RefusedLine(SaidOnceAnswer.Quiet, "10.0.0.1"));
    }

    [Fact]
    public void ALiveDeskIsJudgedHungAfterTenSecondsAnIdleOneAfterThirty()
    {
        var started = Now.AddMinutes(-5);
        var beat = Now.AddSeconds(-11);
        Assert.Equal(SupervisorPolicy.ChildPhase.Hung, SupervisorPolicy.Phase(started, beat, Now, live: true));
        Assert.Equal(SupervisorPolicy.ChildPhase.Beating, SupervisorPolicy.Phase(started, beat, Now, live: false));
        Assert.Equal(SupervisorPolicy.ChildPhase.Beating, SupervisorPolicy.Phase(started, Now.AddSeconds(-9), Now, live: true));
        Assert.True(SupervisorPolicy.IsHung(beat, Now, live: true));
        Assert.False(SupervisorPolicy.IsHung(beat, Now));
        Assert.Equal(TimeSpan.FromSeconds(10), SupervisorPolicy.HangTimeoutFor(true));
        Assert.Equal(TimeSpan.FromSeconds(30), SupervisorPolicy.HangTimeoutFor(false));
        Assert.Equal(SupervisorPolicy.LiveBeat, SupervisorPolicy.BeatFor(SupervisorPolicy.AliveBeat, live: true));
        Assert.Equal(SupervisorPolicy.AliveBeat, SupervisorPolicy.BeatFor(SupervisorPolicy.AliveBeat, live: false));
        Assert.Equal(SupervisorPolicy.HandoverBeat, SupervisorPolicy.BeatFor(SupervisorPolicy.HandoverBeat, live: true));   // the handover ask wins
        Assert.Equal(new byte[] { 1, 2, 3 }, new[] { SupervisorPolicy.AliveBeat, SupervisorPolicy.HandoverBeat, SupervisorPolicy.LiveBeat });
    }

    [Fact]
    public void ADeskIsPairedFromItsFirstRunAndNothingElseIsTouched()
    {
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", PairingToken.ForFirstRun(firstRun: true, isDesk: true));
        Assert.Equal("", PairingToken.ForFirstRun(firstRun: false, isDesk: true));      // a settings file keeps its choice, open included
        Assert.Equal("", PairingToken.ForFirstRun(firstRun: true, isDesk: false));      // a node's pages stay open to its phones
    }

    [Fact]
    public void AStatePushThatWouldRepeatTheLastIsSkippedAndCounted()
    {
        var gate = new StatePushGate();
        Assert.True(gate.ShouldPush("{\"a\":1}"));
        Assert.False(gate.ShouldPush("{\"a\":1}"));
        Assert.False(gate.ShouldPush("{\"a\":1}"));
        Assert.True(gate.ShouldPush("{\"a\":2}"));
        Assert.True(gate.ShouldPush("{\"a\":1}"));
        Assert.Equal(2, gate.Skipped);
    }

    // ---- round 91: the Stagewatch link as a node, and its verdict ------------------------------------------------------

    [Fact]
    public void TheLinkDialsAsANodeNamingThisDeskAndAnIdThatIsNoNodeIdDialsAsAScreen()
    {
        Assert.Equal("/ws?node=9f8e7d6c&t=patterns&cid=9f8e7d6c&page=/patterns", StagewatchFrame.SocketQuery("9f8e7d6c"));
        // An older Stagewatch keeps a cid only at 6–32 characters: a node id outside that sends today's cid beside it.
        Assert.Equal("/ws?node=ab12&t=patterns&cid=patterns&page=/patterns", StagewatchFrame.SocketQuery("ab12"));
        Assert.Equal($"/ws?node={new string('a', 40)}&t=patterns&cid=patterns&page=/patterns", StagewatchFrame.SocketQuery(new string('a', 40)));
        foreach (var notAnId in new[] { "", null, new string('a', 41), "9f8e 7d6c", "9f8e7d6c&t=x", "ünï" })
            Assert.Equal("/ws?cid=patterns&page=/patterns", StagewatchFrame.SocketQuery(notAnId));
        Assert.True(StagewatchFrame.IsNodeId("a_b-C9"));
        Assert.False(StagewatchFrame.IsNodeId("a.b"));
    }

    [Fact]
    public void AVerdictIsReadInTheBytesStagewatchWritesAndNothingElseIsOne()
    {
        // Stagewatch 2.18's NodeLinks.Verdict, Refusal and ViewRefusal: its writer escapes a dash and an apostrophe.
        var key = StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"verified\":\"key\"}}")!;
        Assert.Equal((true, "node", "patterns", "9f8e7d6c", "key", "", "", 0L, false, ""), (key.Ok, key.Scope, key.Kind, key.Node, key.Verified, key.Words, key.Error, key.Retry, key.View, key.Refused));
        var bound = StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"patterns\",\"id\":\"5e6f7a8b\",\"verified\":\"none\"},\"words\":\"this key speaks for patterns 9f8e7d6c \\u2014 make a key for patterns 5e6f7a8b\"}")!;
        Assert.Equal(("none", "this key speaks for patterns 9f8e7d6c — make a key for patterns 5e6f7a8b"), (bound.Verified, bound.Words));
        var claim = StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":true,\"scope\":\"none\",\"node\":{\"t\":\"patterns\",\"id\":\"5e6f7a8b\",\"verified\":\"none\"},\"words\":\"no key \\u2014 the id is only claimed\"}")!;
        Assert.Equal(("none", "none", "no key — the id is only claimed"), (claim.Scope, claim.Verified, claim.Words));
        var refused = StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":false,\"error\":\"unknown or revoked API key\",\"retry\":60}")!;
        Assert.Equal((false, "unknown or revoked API key", 60L, false), (refused.Ok, refused.Error, refused.Retry, refused.View));
        var view = StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":false,\"view\":true,\"error\":\"This screen needs the viewing code\",\"retry\":60,\"refused\":\"unknown or revoked API key\"}")!;
        Assert.Equal((false, true, "This screen needs the viewing code", 60L, "unknown or revoked API key"), (view.Ok, view.View, view.Error, view.Retry, view.Refused));
        var control = StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":true}")!;                  // a control page's: names no node
        Assert.Equal((true, ""), (control.Ok, control.Node));

        Assert.Null(StagewatchFrame.Auth("{\"type\":\"auth\",\"scope\":\"node\"}"));               // no ok: not a verdict
        Assert.Null(StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":\"true\"}"));
        Assert.Null(StagewatchFrame.Auth("{\"type\":\"state\",\"ok\":true}"));
        Assert.Null(StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":true"));
        Assert.Null(StagewatchFrame.Auth(""));
        Assert.Null(StagewatchFrame.Auth(null));
        Assert.Null(StagewatchFrame.Parse("{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"verified\":\"key\"}}"));   // a verdict is no frame
        Assert.Equal("auth", StagewatchFrame.TypeOf("{\"type\":\"auth\",\"ok\":false,\"error\":\"x\"}"));

        // The far end's words reach the log: one line, bounded.
        var long1 = StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":false,\"error\":\"line one\\nline two\\u2028" + new string('x', 400) + "\"}")!;
        Assert.DoesNotContain('\n', long1.Error);
        Assert.DoesNotContain('\u2028', long1.Error);
        Assert.Equal(StagewatchFrame.MaxWords, long1.Error.Length);
        Assert.StartsWith("line one line two ", long1.Error, StringComparison.Ordinal);

        // A far end that echoes the bearer: this desk's key, and anything shaped as a Stagewatch key, masked before any reader.
        var echoed = StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":false,\"error\":\"unknown key swk_abc234xyz (and swk_other9)\",\"refused\":\"Bearer s3cret-desk-key\"}", "s3cret-desk-key")!;
        Assert.Equal(("unknown key ••• (and •••)", "Bearer •••"), (echoed.Error, echoed.Refused));
        Assert.Equal("a key ••• that ends here", StagewatchFrame.Words("a key swk_" + new string('k', 300) + " that ends here"));   // masked whole, before the bound
        Assert.Equal("abc unchanged", StagewatchFrame.Words("abc unchanged", "abc"));               // a value too short to mask free text with
        Assert.Equal("swkey, swk and sw_k stay", StagewatchFrame.Words("swkey, swk and sw_k stay"));
    }

    [Fact]
    public void AHoldIsTheLargerOfSixtySecondsAndTheFarEndsNumberBoundedAtAnHour()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), StagewatchFrame.RefusedHold);
        Assert.Equal(TimeSpan.FromSeconds(60), StagewatchFrame.Hold(401, 0));
        Assert.Equal(TimeSpan.FromSeconds(60), StagewatchFrame.Hold(401, 5));
        Assert.Equal(TimeSpan.FromSeconds(120), StagewatchFrame.Hold(401, 120));
        Assert.Equal(TimeSpan.FromSeconds(60), StagewatchFrame.Hold(403, 0));
        Assert.Equal(TimeSpan.FromSeconds(4), StagewatchFrame.Hold(429, 4));                           // a rate limit: its Retry-After
        Assert.Equal(TimeSpan.FromSeconds(60), StagewatchFrame.Hold(429, 0));
        Assert.Equal(TimeSpan.FromHours(1), StagewatchFrame.Hold(401, 1_000_000_000));                // bounded here
        Assert.Equal(TimeSpan.FromHours(1), StagewatchFrame.Hold(429, long.MaxValue));
        Assert.Equal(TimeSpan.Zero, StagewatchFrame.Hold(200, 30));
        Assert.Equal(TimeSpan.Zero, StagewatchFrame.Hold(500, 30));
        Assert.Equal(TimeSpan.FromSeconds(3), StagewatchFrame.Hold(401, 3, TimeSpan.FromSeconds(1)));  // a test's shorter least
        Assert.Equal(TimeSpan.FromSeconds(1), StagewatchFrame.Hold(401, 0, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void EachVerdictSaysTheKeysStandingAndWhatItHolds()
    {
        var least = StagewatchFrame.RefusedHold;
        StagewatchKeyStep? Of(string json, bool keyed = true) => StagewatchKey.OfVerdict(StagewatchFrame.Auth(json)!, "9f8e7d6c", keyed, least);

        // Taken: the key speaks for this desk; the hold lifts; an authenticated answer may pin.
        var key = Of("{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"verified\":\"key\"}}")!;
        Assert.Equal(("key", StagewatchHoldKind.None, true, true), (key.Key!.Word, key.Hold, key.Takes, key.Pins));
        var op = Of("{\"type\":\"auth\",\"ok\":true,\"scope\":\"control\",\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"verified\":\"operator\"}}")!;
        Assert.Equal(("operator", true), (op.Key!.Word, op.Takes));

        // Refused: held for the larger of 60 s and its retry, said in its words; the socket keeps the public frame.
        var refused = Of("{\"type\":\"auth\",\"ok\":false,\"error\":\"rate limit: too many requests with this key\",\"retry\":75}")!;
        Assert.Equal(("refused", "rate limit: too many requests with this key", StagewatchHoldKind.Refused, TimeSpan.FromSeconds(75), false),
            (refused.Key!.Word, refused.Key.Said, refused.Hold, refused.For, refused.Takes));
        Assert.Equal("KEY REFUSED — rate limit: too many requests with this key", refused.Key.Line);

        // Bound to another node, on a link that carries the key: its node routes held (§13.15.3), its reads and frames kept —
        // the binding governs contexts and proposals, not reading (Stagewatch §7.1).
        var bound = Of("{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"verified\":\"none\"},\"words\":\"this key speaks for patterns 1a2b3c4d \\u2014 make a key for patterns 9f8e7d6c\"}")!;
        Assert.Equal(("none", StagewatchHoldKind.Node, TimeSpan.FromSeconds(60), false), (bound.Key!.Word, bound.Hold, bound.For, bound.Takes));
        Assert.Equal("key not accepted for this desk — this key speaks for patterns 1a2b3c4d — make a key for patterns 9f8e7d6c", bound.Key.Line);
        Assert.True(bound.Key.Fault);
        var read = Of("{\"type\":\"auth\",\"ok\":true,\"scope\":\"read\",\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"verified\":\"none\"},\"words\":\"a read key reads; only a node key speaks for a node\"}")!;
        Assert.Equal(("none", StagewatchHoldKind.Node), (read.Key!.Word, read.Hold));

        // A link that carries no key: what Stagewatch says of a claim holds nothing and is no fault.
        var claim = Of("{\"type\":\"auth\",\"ok\":true,\"scope\":\"none\",\"node\":{\"t\":\"patterns\",\"id\":\"9f8e7d6c\",\"verified\":\"none\"},\"words\":\"no key \\u2014 the id is only claimed\"}", keyed: false)!;
        Assert.Equal(("none", "no key — the id is only claimed", StagewatchHoldKind.None, false, false, ""), (claim.Key!.Word, claim.Key.Said, claim.Hold, claim.Key.Fault, claim.Key.Amber, claim.Key.Line));

        // The viewing code: the socket closes after it. With the key refused too, the refusal; with no key, the redial held and said.
        var viewKeyed = Of("{\"type\":\"auth\",\"ok\":false,\"view\":true,\"error\":\"This screen needs the viewing code\",\"retry\":60,\"refused\":\"unknown or revoked API key\"}")!;
        Assert.Equal(("refused", "unknown or revoked API key", StagewatchHoldKind.Refused), (viewKeyed.Key!.Word, viewKeyed.Key.Said, viewKeyed.Hold));
        var viewKeyless = Of("{\"type\":\"auth\",\"ok\":false,\"view\":true,\"error\":\"This screen needs the viewing code\"}", keyed: false)!;
        Assert.Null(viewKeyless.Key);
        Assert.Equal((StagewatchHoldKind.Refused, TimeSpan.FromSeconds(60)), (viewKeyless.Hold, viewKeyless.For));
        Assert.Equal("it asks for its viewing code — This screen needs the viewing code; this desk reads it only with a key", viewKeyless.Words);

        // Those bytes on a link that carried the key are an older Stagewatch's (2.16): a 2.18 lets a key it takes past the
        // code and says why it refused one. Read as the older one's — the key not let past, refused and held.
        const string bareView = "{\"type\":\"auth\",\"ok\":false,\"view\":true,\"error\":\"This screen needs the viewing code\"}";
        Assert.True(StagewatchKey.OlderFirst("auth", StagewatchFrame.Auth(bareView), keyed: true));
        Assert.False(StagewatchKey.OlderFirst("auth", StagewatchFrame.Auth(bareView), keyed: false));                  // both say it to a keyless link
        Assert.False(StagewatchKey.OlderFirst("auth", StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":false,\"view\":true,\"error\":\"This screen needs the viewing code\",\"retry\":60,\"refused\":\"unknown or revoked API key\"}"), keyed: true));
        Assert.False(StagewatchKey.OlderFirst("auth", StagewatchFrame.Auth("{\"type\":\"auth\",\"ok\":false,\"error\":\"unknown or revoked API key\",\"retry\":60}"), keyed: true));
        Assert.True(StagewatchKey.OlderFirst("state", null, keyed: false));
        var olderView = Of(bareView)!;
        Assert.Equal(("refused", StagewatchKey.ViewWords + " — This screen needs the viewing code", StagewatchHoldKind.Refused, TimeSpan.FromSeconds(60)),
            (olderView.Key!.Word, olderView.Key.Said, olderView.Hold, olderView.For));

        // Not this link's: another node's verdict, or a control page's.
        Assert.Null(Of("{\"type\":\"auth\",\"ok\":true,\"scope\":\"node\",\"node\":{\"t\":\"patterns\",\"id\":\"00000000\",\"verified\":\"key\"}}"));
        Assert.Null(Of("{\"type\":\"auth\",\"ok\":true}"));
    }

    [Fact]
    public void TheAnswersToKeyedCallsAreTheEvidenceAndAnOlderStagewatchIsSaidInAmber()
    {
        var least = StagewatchFrame.RefusedHold;
        var unauthorised = StagewatchKey.OfAnswer(401, "unknown or revoked API key", "", 0, context: false, legacy: false, least)!;
        Assert.Equal(("refused", "unknown or revoked API key", StagewatchHoldKind.Refused, TimeSpan.FromSeconds(60)), (unauthorised.Key!.Word, unauthorised.Key.Said, unauthorised.Hold, unauthorised.For));
        // A 403 is a key the Stagewatch knows that may not use this route: its node routes held, its reads not.
        var forbidden = StagewatchKey.OfAnswer(403, "patterns 9f8e7d6c speaks with its own key", "", 90, context: false, legacy: false, least)!;
        Assert.Equal(("none", StagewatchHoldKind.Node, TimeSpan.FromSeconds(90)), (forbidden.Key!.Word, forbidden.Hold, forbidden.For));
        var busy = StagewatchKey.OfAnswer(429, "rate limit: too many requests with this key", "", 7, context: false, legacy: false, least)!;
        Assert.Equal(((StagewatchKey?)null, StagewatchHoldKind.Busy, TimeSpan.FromSeconds(7), false), (busy.Key, busy.Hold, busy.For, busy.Takes));
        Assert.Equal("HTTP 401", StagewatchKey.OfAnswer(401, "", "", 0, false, false, least)!.Key!.Said);   // a body with no words says its status

        // This desk's context taken: the verified it names; none holds the node routes.
        Assert.Equal(("key", true), (StagewatchKey.OfAnswer(200, "", "key", 0, context: true, legacy: false, least)!.Key!.Word, StagewatchKey.OfAnswer(200, "", "key", 0, true, false, least)!.Takes));
        var unverified = StagewatchKey.OfAnswer(200, "", "none", 0, context: true, legacy: false, least)!;
        Assert.Equal(("none", StagewatchKey.UnverifiedWords, StagewatchHoldKind.Node), (unverified.Key!.Word, unverified.Key.Said, unverified.Hold));
        // A Stagewatch before 2.18 names nothing, and on a link that heard no verdict that takes the key with no word (P-legacy).
        var older = StagewatchKey.OfAnswer(200, "", "", 0, context: true, legacy: true, least)!;
        Assert.Equal(("", true, StagewatchHoldKind.None), (older.Key!.Word, older.Takes, older.Hold));
        // On a link that heard a verdict, a missing or unknown word is no evidence: refused, nothing changed, no hold lifted.
        Assert.Null(StagewatchKey.OfAnswer(200, "", "", 0, context: true, legacy: false, least));
        Assert.Null(StagewatchKey.OfAnswer(200, "", "admin", 0, context: true, legacy: false, least));
        Assert.Null(StagewatchKey.OfAnswer(200, "", "admin", 0, context: true, legacy: true, least));
        Assert.Null(StagewatchKey.OfAnswer(200, "", "key", 0, context: false, legacy: false, least));       // a read is no evidence of the binding
        Assert.Null(StagewatchKey.OfAnswer(404, "not here", "", 0, context: true, legacy: false, least));
        Assert.Null(StagewatchKey.OfAnswer(500, "boom", "", 0, context: true, legacy: true, least));

        // P-legacy: a link that carried the key and heard no verdict is labelled in amber; a fault keeps the label beside it.
        var legacy = new StagewatchKey { Keyed = true, Legacy = true };
        Assert.Equal((StagewatchKey.LegacyWords, true, false), (legacy.Line, legacy.Amber, legacy.Fault));
        var legacyRefused = unauthorised.Key with { Legacy = true };
        Assert.Equal("KEY REFUSED — unknown or revoked API key · " + StagewatchKey.LegacyWords, legacyRefused.Line);
        Assert.False(new StagewatchKey { Legacy = true }.Amber);                                    // no key carried: no evidence read, nothing labelled
        Assert.Equal("", StagewatchKey.Unknown.Line);
        Assert.False(StagewatchKey.Unknown.Amber);
    }

    [Fact]
    public void ARefusedKeyReachesEveryReaderAsOneVerdictAmberWhileTheLinkIsLive()
    {
        var peers = new ShowBusPeers();
        peers.Hear(Hello("main", "a1b2c3d4", "Hall A", http: 9924), "10.0.0.9", Now, "self");
        peers.Hear(Hello("messenger", "77aa77aa", "AGM", http: 3023), "10.0.0.20", Now, "self");
        var refused = new StagewatchKey { Word = "refused", Said = "unknown or revoked API key", Keyed = true };
        var sight = new StagewatchSight("Hall A", "10.0.0.9:9924", ShowBusState.Live, null, 0, StagewatchContext("green"), "") { Key = refused };
        var apps = ShowBusView.Apps(peers.Snapshot(Now), Now, sight);
        var app = apps.Single(a => a.Linked);
        Assert.Equal(refused, app.Key);
        Assert.Equal(StagewatchKey.Unknown, apps.Single(a => !a.Linked).Key);                       // only the linked one's verdict
        Assert.True(ShowBusView.KeyAmber(app));
        const string line = "KEY REFUSED — unknown or revoked API key";

        var row = ShowBusView.Peers(apps)[0];
        Assert.Equal(("amber", line), (row.Light, row.Headline));                                    // the worse of its own green and amber
        Assert.Contains("Stagewatch Hall A — linked — amber: Keynote — Ana · 12:30 left · Lighting standing by", ShowBusView.BriefLines(apps));
        Assert.Contains("  this desk's key: " + line, ShowBusView.BriefLines(apps));
        var card = ShowAppCard.Of(app);
        Assert.Equal(("#FFB020", "linked · Keynote — Ana · 12:30 left · Lighting standing by · " + line), (card.Hue, card.Line));
        var graph = Graph(app);
        var node = graph.Nodes.Single(n => n.Id == "app:a1b2c3d4");
        Assert.Equal((CheckLight.Amber, line), (node.Light, node.Sub));
        Assert.Contains("this desk's key: " + line, node.Words);
        var edge = graph.Edges.Single(e => e.From == "app:a1b2c3d4" && e.To == EyeGraph.DeskId);
        Assert.Equal((CheckLight.Amber, "linked · this desk's key refused"), (edge.Light, edge.Words));
        Assert.Equal(CheckLight.Green, graph.Nodes.Single(n => n.Id == "app:a1b2c3d4:timer").Light);   // its things keep their own lights

        // Never red for the key alone; red when its own light is.
        var red = ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { Context = StagewatchContext("red") }).Single(a => a.Linked);
        Assert.Equal("#B03030", ShowAppCard.Of(red).Hue);
        Assert.Equal(CheckLight.Red, Graph(red).Nodes.Single(n => n.Id == "app:a1b2c3d4").Light);

        Assert.True(ShowBusView.KeyLineLeads(app));

        // A link gone quiet, or gone, says that — in every reader with one headline; the key's words stay on the card's line.
        foreach (var (state, light, sub) in new[] { (ShowBusState.Stale, "amber", "quiet"), (ShowBusState.Gone, "red", "gone quiet") })
        {
            var off = ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { State = state }).Single(a => a.Linked);
            Assert.False(ShowBusView.KeyAmber(off));
            Assert.False(ShowBusView.KeyLineLeads(off));
            var offRow = ShowBusView.Peers(new[] { off })[0];
            Assert.Equal(light, offRow.Light);
            Assert.DoesNotContain("KEY REFUSED", offRow.Headline, StringComparison.Ordinal);
            var offNode = Graph(off).Nodes.Single(n => n.Id == "app:a1b2c3d4");
            Assert.StartsWith(sub, offNode.Sub, StringComparison.Ordinal);
            Assert.DoesNotContain("KEY REFUSED", offNode.Sub, StringComparison.Ordinal);
            Assert.EndsWith(line, ShowAppCard.Of(off).Line, StringComparison.Ordinal);
        }

        // A stuck show says so first; the key's line is still the context's and the card's detail.
        var stuckApp = ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { Stuck = true }).Single(a => a.Linked);
        Assert.False(ShowBusView.KeyLineLeads(stuckApp));
        Assert.Equal(ShowBusView.StuckWords, Graph(stuckApp).Nodes.Single(n => n.Id == "app:a1b2c3d4").Sub);

        // P-legacy's label: amber, the headline kept, said beside it.
        var legacy = ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { Key = new StagewatchKey { Keyed = true, Legacy = true } }).Single(a => a.Linked);
        Assert.True(ShowBusView.KeyAmber(legacy));
        Assert.Equal(("amber", "Keynote — Ana · 12:30 left · Lighting standing by"), (ShowBusView.Peers(new[] { legacy })[0].Light, ShowBusView.Peers(new[] { legacy })[0].Headline));
        var legacyNode = Graph(legacy).Nodes.Single(n => n.Id == "app:a1b2c3d4");
        Assert.Equal((CheckLight.Amber, "Keynote — Ana · 12:30 left · Lighting standing by"), (legacyNode.Light, legacyNode.Sub));
        Assert.Equal("#FFB020", ShowAppCard.Of(legacy).Hue);

        // Taken, or a keyless link's claim: nothing said, its own colour.
        foreach (var quiet in new[] { new StagewatchKey { Word = "key", Keyed = true }, new StagewatchKey { Word = "none", Said = "no key — the id is only claimed" } })
        {
            var taken = ShowBusView.Apps(peers.Snapshot(Now), Now, sight with { Key = quiet }).Single(a => a.Linked);
            Assert.False(ShowBusView.KeyAmber(taken));
            Assert.Equal(("#35E0D0", "linked · Keynote — Ana · 12:30 left · Lighting standing by"), (ShowAppCard.Of(taken).Hue, ShowAppCard.Of(taken).Line));
            Assert.Equal(CheckLight.Green, Graph(taken).Nodes.Single(n => n.Id == "app:a1b2c3d4").Light);
        }

        // A typed address that never said hello carries the same verdict.
        Assert.Equal(refused, ShowBusView.Apps(Array.Empty<ShowBusPeer>(), Now, sight).Single().Key);
    }

    [Fact]
    public void AnIdThatCannotBeKeptIsSaidWithWhatItCosts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-busid91-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = ShowBusIdentity.LoadOrCreate(dir, out var made);
            Assert.True(made);                                                                       // written now
            Assert.Equal(id, ShowBusIdentity.LoadOrCreate(dir, out var read));
            Assert.True(read);                                                                       // read back
            var blocker = Path.Combine(dir, "a-file");
            File.WriteAllText(blocker, "not a folder");
            var fresh = ShowBusIdentity.LoadOrCreate(Path.Combine(blocker, "settings"), out var kept);   // its folder cannot be made
            Assert.False(kept);
            Assert.True(ShowBusIdentity.IsId(fresh));
            Assert.Equal(" · show network id 9f8e7d6c", ShowAppCard.IdLine("9f8e7d6c", kept: true));
            Assert.Equal(" · show network id 9f8e7d6c — not kept (its folder is not writable): a Stagewatch key bound to it refuses this desk after a restart", ShowAppCard.IdLine("9f8e7d6c", kept: false));
            Assert.Equal("", ShowAppCard.IdLine("", kept: true));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
