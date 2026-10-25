using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public void AHelloIsKeptByIdAndDoorItsOwnComesBackIgnoredAndJunkIsNotAPeer()
    {
        var peers = new ShowBusPeers();
        Assert.True(peers.Hear(Hello("main", "a1b2c3d4", "Hall A", http: 9924), "10.0.0.9", Now, "self"));
        Assert.False(peers.Hear(Hello("patterns", "ffff0000", "FOH", http: 9696, wire: 9697, inst: "self"), "10.0.0.5", Now, "self"));   // our own, back
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

        // The same install on another door is another peer (two desks on one machine); the same door replaces.
        Assert.True(peers.Hear(Hello("main", "a1b2c3d4", "Hall A (2)", http: 9925), "10.0.0.9", Now, "self"));
        Assert.True(peers.Hear(Hello("main", "a1b2c3d4", "Hall A renamed", http: 9924), "10.0.0.9", Now, "self"));
        Assert.Equal(new[] { "Hall A (2)", "Hall A renamed" }, peers.Snapshot(Now).Where(p => p.Kind == "main").Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
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
        var stagewatch20 = ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"id\":\"x1\",\"port\":9924}"), "10.0.0.9", Now, out _);
        Assert.Equal(9924, stagewatch20!.Ports.Http);
    }

    [Fact]
    public void AHellosNameIsOneLineSoNoHelloWritesALineOfItsOwn()
    {
        // Round 89: a hello is unauthenticated and its name reaches patterns.log with every stall — a line break never passes.
        var forged = ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"id\":\"x1\",\"name\":\"Hall A\\n2026-10-25 12:00:00.000 [WARN] forged\\u2028line\",\"host\":\"PC\\r\\nB\\t\",\"port\":9924}"), "10.0.0.9", Now, out _);
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
        var hello = new ShowBusHello { Id = "ab12cd34", Instance = "p1", Name = "FOH", Machine = "FOH-PC", Host = "FOH-PC", Web = 9696, Wire = 9697, Ports = new ShowBusPorts { Http = 9696, Wire = 9697, Osc = 9698 }, Clock = 1760101234567 };
        using var doc = JsonDocument.Parse(hello.ToJson());
        var r = doc.RootElement;
        Assert.Equal(9696, r.GetProperty("ports").GetProperty("http").GetInt32());
        Assert.Equal(9698, r.GetProperty("ports").GetProperty("osc").GetInt32());
        Assert.Equal(1760101234567, r.GetProperty("clock").GetInt64());
        Assert.Equal("p1", r.GetProperty("inst").GetString());
        foreach (var cap in new[] { "video", "eye", "context", "receipts", "clock" }) Assert.Contains(cap, r.GetProperty("caps").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("token", hello.ToJson(), StringComparison.OrdinalIgnoreCase);
        // A hello parses back into the peer another desk keeps.
        var back = ShowBusPeers.Parse(hello.ToBytes(), "10.0.0.5", Now, out var inst);
        Assert.Equal("p1", inst);
        Assert.Equal(9697, back!.Ports.Wire);
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
        Assert.StartsWith(OscGate.PairedRule + "; its questions stay open. OSC ALLOW names 1 address", unboundListed, StringComparison.Ordinal);
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
}
