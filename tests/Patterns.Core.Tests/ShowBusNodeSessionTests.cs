using System.Security.Cryptography;
using System.Text;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 97: a Show Bus node driven as a device — PlayMap's line wire at level 0 (Show Bus 1.3 §13.6.8). The session is read
/// against PlayMap's own grammar (Mapper's <c>WireSession</c>: <c>NONCE</c> at connect, <c>OK @id</c> to HELLO, <c>OK @id paired</c>
/// to a right proof, <c>ERR @id wrong token — …</c> to a wrong one) in both orders of its greeting STATE: before the proof (an older
/// PlayMap, discarded and counted, never taken) and after <c>OK paired</c> (a newer one, taken). Its fields first, words second.
/// </summary>
public class ShowBusNodeSessionTests
{
    private const string Token = "K7QM-3XWD-P9RA";
    private const string Inst = "a1b2c3d4";
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static string Text(byte[] frame) => Encoding.UTF8.GetString(frame);

    private static IReadOnlyList<string> Lines(IReadOnlyList<byte[]> frames) => frames.Select(Text).ToList();

    private static ShowBusNodeSession Session(string secret = Token) => new(secret, "FOH", Inst, "PlayMap Hall B");

    /// <summary>Mapper's own proof, written apart from Patterns' (its <c>Pairing.Proof</c>): HMAC-SHA256 keyed by the token with its dashes gone, upper-cased, over nonce + the HELLO name.</summary>
    private static string MapperProof(string token, string nonce, string name)
        => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant()), Encoding.UTF8.GetBytes(nonce + name)));

    /// <summary>The session from connect to <c>OK paired</c>, the greeting STATE where <paramref name="olderOrder"/> puts it; what it wrote, in order.</summary>
    private static List<string> Pair(ShowBusNodeSession s, bool olderOrder)
    {
        var written = new List<string>(Lines(s.OnOpened()));
        written.AddRange(Lines(s.OnReceived("NONCE " + Nonce).SendNext));
        written.AddRange(Lines(s.OnReceived("OK @dh").SendNext));
        if (olderOrder) Assert.Empty(s.OnReceived("STATE {\"blackout\":false,\"name\":\"Hall B\"}").SendNext);
        written.AddRange(Lines(s.OnReceived("OK @dp paired").SendNext));
        if (!olderOrder) Assert.Empty(s.OnReceived("STATE {\"blackout\":false,\"name\":\"Hall B\"}").SendNext);
        return written;
    }

    [Fact]
    public void AConnectionSaysHelloProvesOnTheNodesNonceAndSubscribesToTheContextAlone()
    {
        var s = Session();
        var written = Pair(s, olderOrder: false);
        Assert.Equal("Patterns FOH", s.HelloName);
        Assert.Equal(new[]
        {
            "@dh HELLO Patterns FOH\n",
            "@dp AUTH-PROOF " + MapperProof(Token, Nonce, "Patterns FOH") + "\n",
            "@do SUBSCRIBE OFF\n",
            "@dc SUBSCRIBE CONTEXT\n",
        }, written);
        Assert.Equal(PeerLink.LegacyProof(Token, Nonce, "Patterns FOH"), MapperProof(Token, Nonce, "Patterns FOH"));
        Assert.True(s.Paired);
        Assert.True(s.Up);
        Assert.Equal(ShowBusNodeSession.LevelWords, s.Words);
        Assert.DoesNotContain(written, w => w.Contains(Token, StringComparison.Ordinal) || w.Contains(Token.Replace("-", "", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    [Fact]
    public void AnOlderPlayMapsStateBeforeTheProofIsDiscardedAndCountedAndNeverTaken()
    {
        var s = Session();
        Pair(s, olderOrder: true);
        Assert.Equal((1, 0), (s.StateDiscarded, s.StateTaken));
        Assert.Null(s.LastState);
        var row = new ShowBusNodeSight("d", "PlayMap Hall B", "10.0.0.9", 9941, true, true, s.Paired, s.Open, s.Stopped, s.Context, s.Record(true, true), s.Words, s.StateDiscarded, s.StateTaken, s.Discarded, s.LastOutcome,
            DiscardedWhy: s.DiscardedWhy);
        Assert.Contains("discarded (Show Bus 13.6.3)", row.DiscardedLine);
        Assert.Contains("greets with STATE before it pairs", row.DiscardedLine);
        // Said in the record's own words and counts, never as the line's fault: no malformed count, no flag but level 0's.
        var record = s.Record(true, true);
        Assert.Equal(0, record.Counts.Malformed);
        Assert.Equal(new[] { "legacy" }, record.Flags);
    }

    [Fact]
    public void ANewerPlayMapsStateAfterOkPairedIsTaken()
    {
        var s = Session();
        Pair(s, olderOrder: false);
        Assert.Equal((0, 1), (s.StateDiscarded, s.StateTaken));
        Assert.Equal("{\"blackout\":false,\"name\":\"Hall B\"}", s.LastState);
    }

    [Fact]
    public void NothingIsTakenOrSentBeforeTheProof()
    {
        var s = Session();
        s.OnOpened();
        s.OnReceived("NONCE " + Nonce);
        Assert.Empty(s.Encode("PLAYLIST GO show", out var problem));
        Assert.StartsWith("not-sent — the link is not live — waiting for its proof (PlayMap Hall B: ", problem);
        Assert.Empty(s.EncodeFact("BLACKOUT 1", out _));
        Assert.False(s.TakesFacts);
        s.OnReceived("CONTEXT {\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"pm-1234\",\"name\":\"Hall B\"},\"light\":\"green\",\"headline\":\"ok\",\"items\":[]}");
        Assert.Null(s.Context);
        Assert.Equal(1, s.Discarded);
    }

    [Fact]
    public void CommandsCarryTheirOwnIdsAndAreReadByTheReceiptTable()
    {
        var s = Session();
        Pair(s, olderOrder: false);
        Assert.Equal("@d1-a1b2c3d4 PLAYLIST GO show #3\n", Text(Assert.Single(s.Encode("PLAYLIST GO show #3", out _))));
        Assert.Equal("d1-a1b2c3d4", s.SentKey("PLAYLIST GO show #3"));
        Assert.Equal("@d2-a1b2c3d4 BLACKOUT ON\n", Text(Assert.Single(s.Encode("BLACKOUT ON", out _))));
        Assert.Equal("d2-a1b2c3d4", s.SentKey("BLACKOUT ON"));

        var applied = s.OnReceived("OK @d1-a1b2c3d4 playing show #3");
        Assert.Equal((true, false, "d1-a1b2c3d4", "applied — playing show #3"), (applied.Ack, applied.IsError, applied.AckKey, applied.Words));
        var accepted = s.OnReceived("OK @d2-a1b2c3d4 {\"stage\":\"accepted\",\"effect\":\"queued\"}");
        Assert.Equal((true, "d2-a1b2c3d4", "accepted, not run yet — queued"), (accepted.Ack, accepted.AckKey, accepted.Words));
        Assert.Equal("accepted — queued", s.LastOutcome);
        var refused = s.OnReceived("ERR @d3-a1b2c3d4 no playlist named 'x'");
        Assert.Equal((false, true, "d3-a1b2c3d4"), (refused.Ack, refused.IsError, refused.AckKey));
        Assert.Equal("refused — no playlist named 'x'", refused.Words);
        // An answer that names no id (the scripted peer's OK to a fact) settles no command.
        Assert.Equal(ShowBusNodeSession.NoId, s.OnReceived("OK").AckKey);
        Assert.Equal(ShowBusNodeSession.NoId, s.OnReceived("ERR a quote that does not close").AckKey);
        // A line the device does not tag itself, or one with a line break, is refused before it is written.
        Assert.Empty(s.Encode("@x9 PING", out var tagged));
        Assert.StartsWith("not-sent", tagged);
    }

    /// <summary>The fact keys: Mapper's <c>FactLines.Keys</c> at cb9be004 (<c>src/PlayMap.Net/Commands/CommandRunner.cs:255-256</c>), the list its <c>docs/WIRE.md</c> item 6 names.</summary>
    [Fact]
    public void TheFactsGoUntaggedAndOnlyThoseTheNodeTakesAsFacts()
    {
        var mapper = new[] { "BLACKOUT", "LIVE", "DUCK", "FROZEN", "REVIEW", "LOOK", "PROGRAM", "STINGER", "LOWERTHIRD", "VOL", "MUSICVOL", "LOOKEDITED", "ARMED", "HOLD", "CUE", "DECK", "STEP" };
        Assert.Equal(mapper, ShowBusNodeSession.FactKeys);
        var s = Session();
        Pair(s, olderOrder: false);
        Assert.True(s.TakesFacts);
        Assert.Equal("BLACKOUT 1\n", Text(Assert.Single(s.EncodeFact("BLACKOUT 1", out _))));
        Assert.Equal("LOOK Keynote Main\n", Text(Assert.Single(s.EncodeFact("LOOK Keynote Main", out _))));
        Assert.Equal("CUE\n", Text(Assert.Single(s.EncodeFact("CUE", out _))));
        Assert.Empty(s.EncodeFact("OPEN 1", out var open));                                        // the arrival's word: not a node's fact
        Assert.Equal("", open);
        Assert.Empty(s.EncodeFact("BLACKOUT ON", out _));                                          // the node's verb, never a fact
        Assert.Empty(s.EncodeFact("LOOK \"Walk in", out _));                                       // its grammar would answer ERR
        Assert.All(DeviceFeedback.Changes(DeviceFeedback.Facts("{\"blackout\":true,\"live\":false,\"airLook\":\"Keynote\"}"), null),
            line => Assert.True(ShowBusNodeSession.IsFact(line), line));
    }

    [Fact]
    public void AWrongTokenStopsEverySendUntilTheSecretIsTypedAgain()
    {
        var s = Session("WRNG-WRNG-WRNG");
        s.OnOpened();
        s.OnReceived("NONCE " + Nonce);
        var wrong = s.OnReceived("ERR @dp wrong token — the pairing token is in PlayMap's Network drawer");
        Assert.True(wrong.IsError);
        Assert.True(s.Stopped);
        Assert.Equal(ShowBusNodeSession.WrongTokenWords, s.Words);
        Assert.Empty(s.OnReceived("NONCE fedcba9876543210fedcba9876543210").SendNext);           // no second proof toward the five-wrong close
        Assert.Empty(s.Encode("PING", out var problem));
        Assert.StartsWith("not-sent — wrong pairing token", problem);
        Assert.Equal(new[] { "@dh HELLO Patterns FOH\n" }, Lines(s.OnOpened()));                  // a new connection says HELLO and proves nothing
        Assert.Empty(s.OnReceived("NONCE fedcba9876543210fedcba9876543210").SendNext);
        Assert.Equal("refused", s.Record(true, true).State);
    }

    [Fact]
    public void OnlyAThirtyTwoHexNonceIsProvedAndANodeThatSendsNoneIsAskedForOne()
    {
        var odd = Session();
        odd.OnOpened();
        Assert.Empty(odd.OnReceived("NONCE 0123").SendNext);
        Assert.True(odd.Stopped);

        var none = Session("");
        none.OnOpened();
        Assert.Empty(none.OnReceived("NONCE " + Nonce).SendNext);
        Assert.Equal(ShowBusNodeSession.NoTokenWords, none.Words);

        // A trusted address hears no NONCE at connect: the session asks for one and proves on it.
        var trusted = Session();
        trusted.OnOpened();
        Assert.Equal(new[] { "@dn NONCE\n" }, Lines(trusted.OnReceived("OK @dh").SendNext));
        Assert.Equal(new[] { "@dp AUTH-PROOF " + MapperProof(Token, Nonce, "Patterns FOH") + "\n" }, Lines(trusted.OnReceived("OK @dn " + Nonce).SendNext));

        // No Secret and no NONCE: the node asks this desk for nothing — up, open, and every reader says nothing was proved.
        var open = Session("");
        open.OnOpened();
        Assert.Equal(new[] { "@do SUBSCRIBE OFF\n", "@dc SUBSCRIBE CONTEXT\n" }, Lines(open.OnReceived("OK @dh").SendNext));
        Assert.True(open.Open);
        Assert.Equal(new[] { "open" }, open.Record(true, true).Flags);
    }

    [Fact]
    public void TheContextIsKeptLatestWinsWithinItsBoundAndALongLineIsReadWhole()
    {
        var s = Session();
        Pair(s, olderOrder: false);
        var context = "{\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"pm-1234\",\"name\":\"Hall B\"},\"light\":\"amber\",\"headline\":\"output 2 dark\",\"items\":[{\"id\":\"out2\",\"kind\":\"output\",\"label\":\"Output 2\",\"light\":\"amber\",\"state\":\"dark\"}],\"pad\":\"" + new string('x', 8000) + "\"}";
        var buffer = new StringBuilder();
        buffer.Append("CONTEXT ").Append(context[..3000]);
        Assert.Empty(s.Split(buffer));
        buffer.Append(context[3000..]).Append('\n');
        var line = Assert.Single(s.Split(buffer));
        s.OnReceived(line);
        Assert.Equal("pm-1234", s.Context!.Context.Node.Id);
        Assert.Equal("output 2 dark", s.Context.Context.Headline);
        var rev = s.Context.Rev;
        s.OnReceived("CONTEXT " + context);
        Assert.Equal(rev, s.Context.Rev);                                                       // the same context: the same revision

        var huge = new StringBuilder("CONTEXT {").Append('x', ShowBusNodeSession.MaxLineChars + 10);
        Assert.Empty(s.Split(huge));
        huge.Append("tail\nPING\n");
        Assert.Equal(new[] { "PING" }, s.Split(huge));                                          // the long line skipped whole, the next one read
        Assert.Equal(1, s.Oversize);
    }

    [Fact]
    public void TheLinkRecordIsLevelZeroLegacyAndAmberOnceItIsUp()
    {
        var s = Session();
        Assert.Equal("connecting", s.Record(true, false).State);
        Assert.Equal("off", s.Record(false, false).State);
        Pair(s, olderOrder: false);
        var r = s.Record(true, true, "pm-1234");
        Assert.Equal(("line", 0, "plain", "legacy", "live", "amber"), (r.Carrier, r.Level, r.Tls, r.Proof, r.State, r.Light));
        Assert.Equal("playmap", r.PeerT);
        Assert.Contains("cannot prove itself yet", r.Words);
        Assert.Contains("it ends once PlayMap Hall B proves itself with PeerLink", r.Ends);
        Assert.Equal("", r.PeerRowLink);                                                         // level 0: nothing proved who answered
        Assert.DoesNotContain(Token, r.ToJson(), StringComparison.Ordinal);
        Assert.Equal("down", s.Record(true, false).State);                                       // its connection gone
    }

    [Fact]
    public void TheProfileIsNamedPresetAndReachesAcceptedOnTcp()
    {
        Assert.Equal("Show Bus node (PlayMap)", DeviceProfiles.Label(DeviceProfile.ShowBusNode));
        var d = DeviceProfiles.Preset(DeviceProfile.ShowBusNode, 1);
        Assert.Equal((DeviceLink.Tcp, 9941, true, false, false), (d.Link, d.NetPort, d.HearsShow, d.SpeaksProtocol, d.EchoReplies));
        Assert.Equal(30000, d.ConfirmTimeoutMs);
        Assert.Equal(ConfirmLevel.Accepted, DeviceConfirmation.Effective(d));
        d.Secret = Token;
        Assert.IsType<ShowBusNodeSession>(ProfileSession.For(d, "FOH", Inst));
        Assert.IsType<LinesSession>(ProfileSession.For(new DeviceConfig { Link = DeviceLink.Tcp }, "FOH", Inst));   // a generic Lines device keeps its own protocol
        Assert.Equal("Patterns FOH -2", ShowBusNodeSession.HelloNameFor("FOH  \"2\""));
        Assert.Equal("Patterns FOH module-x", ShowBusNodeSession.HelloNameFor("FOH module=x"));      // no " module=" for the node to cut off the name it proves over
        Assert.Equal("Patterns", ShowBusNodeSession.HelloNameFor(""));
    }

    [Fact]
    public void AStateBeforeThisDesksProofWithNoNonceFirstNamesNoOlderBuild()
    {
        // A node that trusts this address (or asks it for no token) counts it paired at connect and greets at once: this desk, holding
        // a Secret, still asks for a NONCE and proves — the STATE between is discarded, and no build is blamed for it.
        var s = Session();
        s.OnOpened();
        Assert.Equal(new[] { "@dn NONCE\n" }, Lines(s.OnReceived("OK @dh").SendNext));
        Assert.Equal(ShowBusNodeSession.DiscardedTrustedWords, s.OnReceived("STATE {\"blackout\":false}").Words);
        s.OnReceived("OK @dn " + Nonce);
        s.OnReceived("OK @dp paired");
        Assert.Equal((1, 0, ShowBusNodeSession.TrustedWhy), (s.StateDiscarded, s.StateTaken, s.DiscardedWhy));
        var row = new ShowBusNodeSight("d", "PlayMap Hall B", "10.0.0.9", 9941, true, true, s.Paired, s.Open, s.Stopped, s.Context, s.Record(true, true), s.Words,
            s.StateDiscarded, s.StateTaken, s.Discarded, s.LastOutcome, DiscardedWhy: s.DiscardedWhy);
        Assert.Contains("the node asks this address for no token, or trusts it", row.DiscardedLine);
        Assert.DoesNotContain("older build", row.DiscardedLine);

        // After a NONCE, the same STATE is an older PlayMap's greeting.
        var older = Session();
        Pair(older, olderOrder: true);
        Assert.Equal((1, ShowBusNodeSession.OlderWhy), (older.StateDiscarded, older.DiscardedWhy));

        // Before the node said anything of the pairing (the scripted acceptor greets first): counted, and no reason guessed.
        var early = Session();
        early.OnOpened();
        Assert.Equal(ShowBusNodeSession.DiscardedEarlyWords, early.OnReceived("STATE {}").Words);
        Assert.Equal((1, ""), (early.StateDiscarded, early.DiscardedWhy));
    }

    [Fact]
    public void AProofHoldsOnlyOnItsConnectionFromTheMomentTheLinkMoves()
    {
        var s = Session();
        var gen = s.LinkMoved();                                                                  // the link's thread: a connection is about to be published
        Assert.Equal(new[] { "@dh HELLO Patterns FOH\n" }, Lines(s.OnOpenedAt(gen)));
        s.OnReceived("NONCE " + Nonce);
        s.OnReceived("OK @dh");
        s.OnReceived("OK @dp paired");
        Assert.True(s.Up);
        Assert.Single(s.Encode("PING", out _));

        var next = s.LinkMoved();                                                                 // dropped and back, before the desk's thread heard
        Assert.False(s.Up);
        Assert.False(s.TakesFacts);
        Assert.Empty(s.Encode("PLAYLIST GO show", out var problem));
        Assert.StartsWith("not-sent — the link is not live — its connection moved", problem);
        Assert.Empty(s.EncodeFact("BLACKOUT 1", out _));
        // The gone connection's late lines: a command's answer still settles it; nothing of the link's own is read or sent.
        Assert.Empty(s.OnReceived("OK @dp paired").SendNext);
        Assert.Empty(s.OnReceived("NONCE " + Nonce).SendNext);
        s.OnReceived("CONTEXT {\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"pm-1\",\"name\":\"B\"},\"light\":\"green\",\"headline\":\"\",\"items\":[]}");
        Assert.Null(s.Context);
        Assert.Equal((true, "d1-a1b2c3d4"), (s.OnReceived("OK @d1-a1b2c3d4 pong").Ack, s.OnReceived("OK @d1-a1b2c3d4 pong").AckKey));

        Assert.Equal(new[] { "@dh HELLO Patterns FOH\n" }, Lines(s.OnOpenedAt(next)));        // the new connection opens: HELLO first, nothing proved
        Assert.False(s.Paired);
        Assert.Single(s.OnReceived("NONCE " + Nonce).SendNext);
        s.OnReceived("OK @dp paired");
        Assert.True(s.Up);
    }

    [Fact]
    public void AConnectionMovedInTheMiddleOfALongLineStartsTheNextOneClean()
    {
        var s = Session();
        var huge = new StringBuilder("CONTEXT {").Append('x', ShowBusNodeSession.MaxLineChars + 10);
        Assert.Empty(s.Split(huge));                                                             // skipping to the line's end
        s.LinkMoved();                                                                            // the connection dropped there
        var fresh = new StringBuilder("NONCE " + Nonce + "\n");
        Assert.Equal(new[] { "NONCE " + Nonce }, s.Split(fresh));                                // the next connection's first line is read
        Assert.Equal(1, s.Oversize);
    }

    [Fact]
    public void AHeardPlayMapWithADeviceSetAtItsAddressOffersThatDeviceNeverASecond()
    {
        // The device is set at the hello's address and wire port but not up yet: no fold, and no Add as a device beside it.
        var s = Session();
        var waiting = new ShowBusNodeSight("dev1", "PlayMap-Hall-B", "10.0.0.9", 9941, true, false, false, false, false, null, s.Record(true, false), s.Words, 0, 0, 0, "");
        var apps = ShowBusView.Apps(new[] { PlayMapHeard() }, DateTime.UtcNow, StagewatchSight.None, new[] { waiting });
        var heardApp = apps.Single(a => a.Id == "pm-1234");
        Assert.Equal(("", "dev1"), (heardApp.DeviceId, heardApp.DeviceAt));
        var card = ShowAppCard.Of(heardApp);
        Assert.Equal((false, "dev1"), (card.AddAsDevice, card.DeviceId));
        Assert.Contains("this desk's device PlayMap-Hall-B is set at its address", card.Line);
        var entry = DeskMenus.ShowApp(new DeskFacts(), MenuShowApp.Of(card)).Groups.Single(g => g.Heading == "DEVICE").Entries.Single();
        Assert.Equal("device.open", entry.Id);

        // A device elsewhere leaves the offer.
        var elsewhere = waiting with { Host = "10.0.0.50" };
        Assert.True(ShowAppCard.Of(ShowBusView.Apps(new[] { PlayMapHeard() }, DateTime.UtcNow, StagewatchSight.None, new[] { elsewhere }).Single(a => a.Id == "pm-1234")).AddAsDevice);

        // The unfolded device is this desk's own: never published as a peer heard.
        var published = ShowBusView.Published(apps);
        Assert.Equal(new[] { "pm-1234" }, published.Select(a => a.Id));
        Assert.DoesNotContain(ShowBusView.Peers(published), p => p.Id.StartsWith("device-", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEyeDrawsANodeDevicesLinkAsItsControlsEdgeAloneCountedAndLitByTheRecord()
    {
        var folded = Assert.Single(ShowBusView.Apps(new[] { PlayMapHeard() }, DateTime.UtcNow, StagewatchSight.None, new[] { Up("pm-1234") }));
        var graph = EyeGraph.Build(new EyeFacts
        {
            ShowApps = new[] { folded },
            Devices = new[] { new EyeDevice("dev1", "PlayMap Hall B", "ShowBusNode", "10.0.0.9:9941", true, true, "open", CheckLight.Green, "") },
        });
        var app = graph.Nodes.Single(n => n.Id == "app:pm-1234");
        Assert.Contains(app.Words, w => w.StartsWith("PlayMap on the show network — driven as a device", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Edges, e => e.From == "app:pm-1234" && e.Kind == EyeEdgeKind.Follows);
        var controls = Assert.Single(graph.Edges, e => e.To == "app:pm-1234" && e.Kind == EyeEdgeKind.Controls);
        Assert.Equal(("device:dev1", CheckLight.Amber, true), (controls.From, controls.Light, controls.Counted));
        Assert.Single(graph.Edges, e => e.From == "app:pm-1234" && e.Kind == EyeEdgeKind.Hears);       // heard, and said as heard

        // The device's app no hello names: its one edge is the device's.
        var own = ShowBusView.Apps(Array.Empty<ShowBusPeer>(), DateTime.UtcNow, StagewatchSight.None, new[] { Up("pm-1234") }).Single();
        var alone = EyeGraph.Build(new EyeFacts
        {
            ShowApps = new[] { own },
            Devices = new[] { new EyeDevice("dev1", "PlayMap Hall B", "ShowBusNode", "10.0.0.9:9941", true, true, "open", CheckLight.Green, "") },
        });
        Assert.DoesNotContain(alone.Edges, e => e.From == "app:device-dev1" && e.Kind is EyeEdgeKind.Hears or EyeEdgeKind.Follows);
        Assert.Single(alone.Edges, e => e.From == "device:dev1" && e.To == "app:device-dev1" && e.Kind == EyeEdgeKind.Controls);
    }

    private static ShowBusPeer PlayMapHeard(string id = "pm-1234", string address = "10.0.0.9", int wire = 9941)
        => new("playmap", id, "Hall B", "hallb", address, new ShowBusPorts { Http = 9940, Wire = wire }, Array.Empty<string>(), 3, 0, DateTime.UtcNow);

    private static ShowBusNodeSight Up(string contextId, string host = "10.0.0.9", int port = 9941)
    {
        var s = Session();
        Pair(s, olderOrder: false);
        s.OnReceived("CONTEXT {\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"" + contextId + "\",\"name\":\"Hall B\"},\"light\":\"green\",\"headline\":\"all outputs live\",\"items\":[{\"id\":\"out1\",\"kind\":\"output\",\"label\":\"Output 1\",\"light\":\"green\",\"state\":\"live\"}]}");
        return new ShowBusNodeSight("dev1", "PlayMap Hall B", host, port, true, true, s.Paired, s.Open, s.Stopped, s.Context, s.Record(true, true, contextId), s.Words, s.StateDiscarded, s.StateTaken, s.Discarded, s.LastOutcome);
    }

    [Fact]
    public void AHeardPlayMapAndItsDeviceFoldOnlyWhenTheAddressAndTheContextIdAgree()
    {
        var heard = new[] { PlayMapHeard() };
        var folded = Assert.Single(ShowBusView.Apps(heard, DateTime.UtcNow, StagewatchSight.None, new[] { Up("pm-1234") }));
        // Driven, not linked: the Stagewatch link's readers never take a node device for it.
        Assert.Equal(("pm-1234", "PlayMap", false, "dev1", "Hall B"), (folded.Id, folded.KindWords, folded.Linked, folded.DeviceId, folded.Name));
        Assert.Equal("all outputs live", folded.Context!.Headline);
        Assert.Equal(CheckLight.Amber, ShowBusView.CardLight(folded));                           // level 0's legacy amber, never its context's green
        Assert.Equal("legacy", folded.Link!.Flags.Single());

        // Another id at that address, or that id from another address: two apps, the heard one still only heard.
        foreach (var device in new[] { Up("pm-9999"), Up("pm-1234", host: "10.0.0.10"), Up("pm-1234", port: 9000) })
        {
            var apps = ShowBusView.Apps(heard, DateTime.UtcNow, StagewatchSight.None, new[] { device });
            Assert.Equal(2, apps.Count);
            Assert.False(apps.Single(a => a.Id == "pm-1234").Linked);
            Assert.Equal("device-dev1", apps.Single(a => a.DeviceId == "dev1").Id);
        }

        var peer = Assert.Single(ShowBusView.Peers(new[] { folded }));
        Assert.Equal(("playmap", "amber", "all outputs live", ""), (peer.Kind, peer.Light, peer.Headline, peer.Link));
        var brief = ShowBusView.BriefLines(new[] { folded });
        Assert.Contains(brief, l => l.StartsWith("PlayMap Hall B — driven as a device — amber: all outputs live", StringComparison.Ordinal));
        Assert.Contains(brief, l => l.StartsWith("  the link: live, amber", StringComparison.Ordinal));
        Assert.Contains(brief, l => l.Contains("DEVICE dev1 <its verb>", StringComparison.Ordinal));                // a name of more than one word: the wire names the device by its id
        Assert.Contains(brief, l => l.Contains("· Output 1 (output) — green", StringComparison.Ordinal));
        var card = ShowAppCard.Of(folded);
        Assert.Equal(("dev1", false), (card.DeviceId, card.AddAsDevice));
        Assert.Contains("driven as the device PlayMap Hall B", card.Line);
    }

    [Fact]
    public void AHeardPlayMapCardOffersAddAsADeviceAndOneDrivenOpensItsDevice()
    {
        var heard = ShowAppCard.Of(Assert.Single(ShowBusView.Apps(new[] { PlayMapHeard() }, DateTime.UtcNow, StagewatchSight.None)));
        Assert.Equal(("PlayMap", true), (heard.KindLabel, heard.AddAsDevice));
        var facts = new DeskFacts();
        var add = DeskMenus.ShowApp(facts, MenuShowApp.Of(heard)).Groups.Single(g => g.Heading == "DEVICE").Entries.Single();
        Assert.Equal(("device.add", DeskMenus.AddDeviceEdit + "pm-1234", true), (add.Id, add.Edit, add.IsEnabled));
        Assert.False(DeskMenus.ShowApp(facts with { IsNode = true }, MenuShowApp.Of(heard)).Groups.Single(g => g.Heading == "DEVICE").Entries.Single().IsEnabled);

        var driven = ShowAppCard.Of(Assert.Single(ShowBusView.Apps(new[] { PlayMapHeard() }, DateTime.UtcNow, StagewatchSight.None, new[] { Up("pm-1234") })));
        var open = DeskMenus.ShowApp(facts, MenuShowApp.Of(driven)).Groups.Single(g => g.Heading == "DEVICE").Entries.Single();
        Assert.Equal(("device.open", "Interactive", "dev1"), (open.Id, open.Route!.Page, open.Route.Item));
        Assert.DoesNotContain(DeskMenus.ShowApp(facts, new MenuShowApp("sw", "Stagewatch", "Hall A", "", false)).Groups, g => g.Heading == "DEVICE");
    }
}
