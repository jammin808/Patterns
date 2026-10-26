using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 (§2, §10, §14): the hello this desk says — proto 3, its inst, names of one line within 64 characters, only
/// the doors it serves, within 1,200 bytes — and the table it keeps of what it hears: one id one peer with every address
/// and a dial that stays put while heard, two running nodes with one id marked in conflict and shown amber by every reader,
/// at most 128 ids under a flood of forged hellos with the ones this desk needs kept and the drops counted; and the
/// certificate, 825 days in all, with an over-long one replaced once. All on injected time.
/// </summary>
public class ShowBus13Tests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc);

    private static byte[] Hello(string id, string name = "Hall A", string host = "FOH-PC", string inst = "", string t = "main", int http = 9924, string caps = "\"timer\",\"context\"")
        => Encoding.UTF8.GetBytes($"{{\"t\":\"{t}\",\"v\":1,\"proto\":3,\"id\":\"{id}\",\"name\":\"{name}\",\"host\":\"{host}\",\"ports\":{{\"http\":{http},\"ws\":{http}}},\"caps\":[{caps}]{(inst.Length > 0 ? $",\"inst\":\"{inst}\"" : "")}}}");

    private static ShowBusPeer One(ShowBusPeers peers, DateTime at) => Assert.Single(peers.Snapshot(at));

    /// <summary>A forged sender's inst, new for every <paramref name="i"/> and the same at every run: eight lowercase hex.</summary>
    private static string Inst(int i) => unchecked((uint)i * 2654435761u).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A forged sender's address, from <paramref name="i"/>: the same at every run.</summary>
    private static string Lan(int net, int i) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"10.{net}.{(i >> 8) & 255}.{i & 255}");

    // ---- the hello this desk says ----------------------------------------------------------------

    [Fact]
    public void TheHelloSpeaks13AndNamesOnlyTheDoorsItServes()
    {
        var hello = new ShowBusHello { Id = "ab12cd34", Instance = "0a1b2c3d", Name = "FOH", Machine = "FOH-PC", Host = "FOH-PC", Web = 9696, Wire = 9697, Ports = new ShowBusPorts { Http = 9696, Wire = 9697 } };
        using var doc = JsonDocument.Parse(hello.ToBytes());
        var r = doc.RootElement;
        Assert.Equal(3, r.GetProperty("proto").GetInt32());
        Assert.Equal(ShowBusHello.Revision, hello.Proto);
        Assert.True(ShowBusPeers.IsInstance(r.GetProperty("inst").GetString()));
        var doors = r.GetProperty("ports").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "http", "wire" }, doors);                                              // no "ws":0, no "osc":0
        Assert.All(ShowBusHello.Capabilities, c => Assert.True(ShowBusPeers.IsCap(c), c));
        Assert.True(ShowBusHello.Capabilities.Length <= ShowBusPeers.KeptCaps);
    }

    [Fact]
    public void ANameIsOneLineOfAtMost64CharactersNeverCutInsideAPair()
    {
        Assert.Equal("Hall A \uFFFD B", ShowBusHello.Fit(" Hall A\n\uD800 B "));                  // a line break a space, a lone surrogate replaced
        var masks = string.Concat(Enumerable.Repeat("\U0001F3AD", 40));
        Assert.Equal(masks[..64], ShowBusHello.Fit(masks));                                            // 32 whole pairs
        var odd = ShowBusHello.Fit("a" + masks);
        Assert.Equal(63, odd.Length);                                                                  // the pair that would be split is left out
        Assert.False(char.IsHighSurrogate(odd[^1]));
        Assert.Equal("", ShowBusHello.Fit(null));
    }

    private static ShowBusHello Worst(string name) => new()
    {
        Id = new string('f', 40),
        Instance = "0a1b2c3d",
        Name = name,
        Machine = name,
        Host = name,
        Web = 65535,
        Wire = 65535,
        Role = "caller",
        Ports = new ShowBusPorts { Http = 65535, Wire = 65535, Ws = 65535, Osc = 65535, Https = 65535 },
        Clock = long.MaxValue,
        Tls = TlsFingerprint.Prefix + new string('a', 64),
    };

    [Fact]
    public void AHelloWithEveryFieldAtItsMostStaysWithin1200Bytes()
    {
        // Names of 64 three-byte characters go whole, and every reader keeps them whole.
        var euro = ShowBusHello.Fit(new string('\u20AC', 200));
        Assert.Equal(ShowBusHello.NameChars, euro.Length);
        var bytes = Worst(euro).ToBytes();
        Assert.InRange(bytes.Length, 1, ShowBusHello.MaxBytes);
        var back = ShowBusPeers.Parse(bytes, "10.0.0.5", T0, out _)!;
        Assert.Equal((euro, euro), (back.Name, back.Host));
        Assert.Equal(Encoding.UTF8.GetString(bytes), Worst(euro).ToJson());

        // Names of characters JSON escapes (six bytes each) are cut until the hello fits — never over.
        var escaped = ShowBusHello.Fit(new string('\u0378', 200));
        var cut = Worst(escaped).ToBytes();
        Assert.InRange(cut.Length, 1, ShowBusHello.MaxBytes);
        var shorter = ShowBusPeers.Parse(cut, "10.0.0.5", T0, out _)!;
        Assert.StartsWith(shorter.Name, escaped, StringComparison.Ordinal);
        Assert.InRange(shorter.Name.Length, 1, ShowBusHello.NameChars - 1);
    }

    [Fact]
    public void AReaderTakesOnlyHellosOfTheShapesSection2Names()
    {
        Assert.Null(ShowBusPeers.Parse(Hello("a1b2c"), "10.0.0.9", T0, out _));                       // an id under 6
        Assert.Null(ShowBusPeers.Parse(Hello(new string('a', 41)), "10.0.0.9", T0, out _));           // over 40
        Assert.Null(ShowBusPeers.Parse(Hello("a1b2c3d4", t: "a-kind-of-17-char"), "10.0.0.9", T0, out _));
        Assert.Null(ShowBusPeers.Parse(Hello("a1b2c3/4"), "10.0.0.9", T0, out _));
        Assert.NotNull(ShowBusPeers.Parse(Hello(new string('a', 40), t: "sixteen-chars-ok"), "10.0.0.9", T0, out _));

        // 40 caps, some not of §2's shape: the first 32 that are.
        var caps = string.Join(",", Enumerable.Range(0, 40).Select(i => i % 5 == 0 ? $"\"BAD{i}\"" : $"\"cap{i}\"").Append("\"" + new string('c', 25) + "\""));
        var peer = ShowBusPeers.Parse(Hello("a1b2c3d4", caps: caps), "10.0.0.9", T0, out _)!;
        Assert.Equal(ShowBusPeers.KeptCaps, peer.Caps.Count);
        Assert.All(peer.Caps, c => Assert.True(ShowBusPeers.IsCap(c)));
        Assert.Equal("cap1", peer.Caps[0]);

        // A format word this reader does not know is dropped (§2, fail closed); a hello naming none is an older app's.
        Assert.Null(ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"v\":2,\"id\":\"a1b2c3d4\",\"ports\":{\"http\":9924}}"), "10.0.0.9", T0, out _));
        Assert.Null(ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"v\":\"1\",\"id\":\"a1b2c3d4\",\"ports\":{\"http\":9924}}"), "10.0.0.9", T0, out _));
        Assert.Null(ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"v\":1.5,\"id\":\"a1b2c3d4\",\"ports\":{\"http\":9924}}"), "10.0.0.9", T0, out _));
        Assert.False(new ShowBusPeers().Hear(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"v\":2,\"id\":\"a1b2c3d4\",\"ports\":{\"http\":9924}}"), "10.0.0.9", T0, "self"));
        Assert.NotNull(ShowBusPeers.Parse(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"id\":\"a1b2c3d4\",\"ports\":{\"http\":9924}}"), "10.0.0.9", T0, out _));
        Assert.NotNull(ShowBusPeers.Parse(Hello("a1b2c3d4"), "10.0.0.9", T0, out _));

        // An inst not of §2's shape is no inst: the host stands for the sender.
        Assert.Equal("", ShowBusPeers.Parse(Hello("a1b2c3d4", inst: "XYZ"), "10.0.0.9", T0, out var raw)!.Instance);
        Assert.Equal("XYZ", raw);
        Assert.Equal("0a1b2c3d", ShowBusPeers.Parse(Hello("a1b2c3d4", inst: "0a1b2c3d"), "10.0.0.9", T0, out _)!.Instance);
    }

    // ---- one id, one peer ------------------------------------------------------------------------

    [Fact]
    public void ANodeHeardOnLoopbackAndTheLanIsOnePeerDialledAtOneAddressWhileItIsHeard()
    {
        var peers = new ShowBusPeers();
        // Its broadcast from the LAN and its copy to loopback, taking turns each second for 20 s.
        for (var s = 0; s < 20; s++) Assert.True(peers.Hear(Hello("a1b2c3d4", inst: "0a1b2c3d"), s % 2 == 0 ? "10.0.0.9" : "127.0.0.1", T0.AddSeconds(s), "self"));
        var p = One(peers, T0.AddSeconds(19));
        Assert.Equal("10.0.0.9", p.Address);                                                         // the first heard, kept while heard
        Assert.Equal(new[] { "10.0.0.9", "127.0.0.1" }, p.Addresses);
        Assert.Null(p.Conflict);                                                                      // one inst from two addresses is one node
        Assert.True(p.HeardAt("127.0.0.1") && p.HeardAt("10.0.0.9") && p.HeardAt("[10.0.0.9]"));   // a typed address matches any it was heard from
        Assert.False(p.HeardAt("10.0.0.10"));
        Assert.False(p.HeardAt("FOH-PC"));                                                           // never the host its hello says: a word, not a source

        // The dialled address unheard for more than 6 s: the newest is dialled, the stale one forgotten.
        for (var s = 20; s < 28; s++) peers.Hear(Hello("a1b2c3d4", inst: "0a1b2c3d"), "127.0.0.1", T0.AddSeconds(s), "self");
        p = One(peers, T0.AddSeconds(27));
        Assert.Equal("127.0.0.1", p.Address);
        Assert.Equal(new[] { "127.0.0.1" }, p.Addresses);
    }

    [Fact]
    public void AnIdKeepsAtMostFourAddressesAndNeverLosesTheOneItDials()
    {
        var peers = new ShowBusPeers();
        for (var i = 1; i <= 7; i++) peers.Hear(Hello("a1b2c3d4", inst: "0a1b2c3d"), $"10.0.0.{i}", T0.AddMilliseconds(i * 100), "self");
        var p = One(peers, T0.AddSeconds(1));
        Assert.Equal(ShowBusPeers.MaxAddresses, p.Addresses.Count);
        Assert.Equal("10.0.0.1", p.Address);                                                         // the dialled one stays
        Assert.Contains("10.0.0.7", p.Addresses);                                                    // the newest is kept
        Assert.DoesNotContain("10.0.0.2", p.Addresses);                                              // the oldest other went first
    }

    // ---- two nodes, one id -----------------------------------------------------------------------

    [Fact]
    public void TwoRunningNodesWithOneIdAreMarkedInConflictAndTheMarkClearsAMinuteAfterTheLastTurn()
    {
        var peers = new ShowBusPeers();
        peers.Hear(Hello("3f9c2a1b7d0e", host: "FOH-PC", inst: "aaaaaaaa"), "10.0.0.5", T0, "self");
        peers.Hear(Hello("3f9c2a1b7d0e", host: "FOH-PC2", inst: "bbbbbbbb"), "10.0.0.7", T0.AddSeconds(1), "self");
        Assert.Null(One(peers, T0.AddSeconds(1)).Conflict);                                          // A then B: a restart, so far
        peers.Hear(Hello("3f9c2a1b7d0e", host: "FOH-PC", inst: "aaaaaaaa"), "10.0.0.5", T0.AddSeconds(2), "self");
        var p = One(peers, T0.AddSeconds(2));
        Assert.NotNull(p.Conflict);
        Assert.Equal("two machines say they are 3f9c2a1b7d0e: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7", p.Conflict!.Words);
        Assert.Equal("10.0.0.5", p.Address);                                                         // the dial does not follow the turns

        // The words stay put while they take turns, and every turn moves the clearing on.
        var t = T0.AddSeconds(2);
        for (var s = 3; s < 20; s++)
        {
            t = T0.AddSeconds(s);
            peers.Hear(Hello("3f9c2a1b7d0e", host: s % 2 == 1 ? "FOH-PC2" : "FOH-PC", inst: s % 2 == 1 ? "bbbbbbbb" : "aaaaaaaa"), s % 2 == 1 ? "10.0.0.7" : "10.0.0.5", t, "self");
            Assert.Equal("two machines say they are 3f9c2a1b7d0e: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7", One(peers, t).Conflict!.Words);
        }

        // One stops: only B for a minute and the mark clears; nothing it says marks it again.
        for (var s = 21; s <= 77; s += 2) peers.Hear(Hello("3f9c2a1b7d0e", host: "FOH-PC2", inst: "bbbbbbbb"), "10.0.0.7", T0.AddSeconds(s), "self");
        Assert.NotNull(One(peers, t + ShowBusPeers.ConflictClearsAfter).Conflict);
        Assert.Null(One(peers, t + ShowBusPeers.ConflictClearsAfter + TimeSpan.FromSeconds(1)).Conflict);
        peers.Hear(Hello("3f9c2a1b7d0e", host: "FOH-PC2", inst: "bbbbbbbb"), "10.0.0.7", T0.AddSeconds(81), "self");
        var after = One(peers, T0.AddSeconds(81));
        Assert.Null(after.Conflict);
        Assert.Equal(("10.0.0.7", "FOH-PC2"), (after.Address, after.Host));                          // the one still heard is dialled
    }

    [Fact]
    public void ARestartIsNotAConflictNorIsAQuietReturn()
    {
        var peers = new ShowBusPeers();
        for (var s = 0; s < 10; s += 2) peers.Hear(Hello("a1b2c3d4", inst: "aaaaaaaa"), "10.0.0.5", T0.AddSeconds(s), "self");
        for (var s = 10; s < 30; s += 2) peers.Hear(Hello("a1b2c3d4", inst: "bbbbbbbb"), "10.0.0.5", T0.AddSeconds(s), "self");   // restarted: A, then only B
        Assert.Null(One(peers, T0.AddSeconds(30)).Conflict);
        // A sender that went quiet for more than 6 s before the next one spoke is no turn either.
        peers.Hear(Hello("a1b2c3d4", inst: "cccccccc"), "10.0.0.5", T0.AddSeconds(40), "self");
        peers.Hear(Hello("a1b2c3d4", inst: "bbbbbbbb"), "10.0.0.5", T0.AddSeconds(41), "self");
        Assert.Null(One(peers, T0.AddSeconds(41)).Conflict);
    }

    [Fact]
    public void ThreeMachinesTakingTurnsAndTwoHostsWithNoInstAreConflictsToo()
    {
        var peers = new ShowBusPeers();
        string[] insts = { "aaaaaaaa", "bbbbbbbb", "cccccccc" };
        for (var s = 0; s < 4; s++) peers.Hear(Hello("a1b2c3d4", host: "PC" + (s % 3), inst: insts[s % 3]), "10.0.0." + (5 + (s % 3)), T0.AddSeconds(s), "self");
        Assert.NotNull(One(peers, T0.AddSeconds(3)).Conflict);

        var old = new ShowBusPeers();                                                                // nodes that send no inst: two hosts within 6 s
        old.Hear(Hello("e5e5e5e5", host: "FOH-PC"), "10.0.0.5", T0, "self");
        old.Hear(Hello("e5e5e5e5", host: "FOH-PC"), "10.0.0.5", T0.AddSeconds(2), "self");
        Assert.Null(One(old, T0.AddSeconds(2)).Conflict);
        old.Hear(Hello("e5e5e5e5", host: "FOH-PC2"), "10.0.0.7", T0.AddSeconds(3), "self");
        Assert.Equal("two machines say they are e5e5e5e5: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7", One(old, T0.AddSeconds(3)).Conflict!.Words);

        var copies = new ShowBusPeers();                                                             // two copies on one machine
        copies.Hear(Hello("f6f6f6f6", inst: "aaaaaaaa"), "10.0.0.5", T0, "self");
        copies.Hear(Hello("f6f6f6f6", inst: "bbbbbbbb"), "10.0.0.5", T0.AddSeconds(1), "self");
        copies.Hear(Hello("f6f6f6f6", inst: "aaaaaaaa"), "10.0.0.5", T0.AddSeconds(2), "self");
        Assert.Equal("two copies running at 10.0.0.5 (FOH-PC) say they are f6f6f6f6", One(copies, T0.AddSeconds(2)).Conflict!.Words);
    }

    [Fact]
    public void AConflictReachesEveryReaderAmberWithBothMachinesNamed()
    {
        var peers = new ShowBusPeers();
        peers.Hear(Hello("3f9c2a1b7d0e", host: "FOH-PC", inst: "aaaaaaaa"), "10.0.0.5", T0, "self");
        peers.Hear(Hello("3f9c2a1b7d0e", host: "FOH-PC2", inst: "bbbbbbbb"), "10.0.0.7", T0.AddSeconds(1), "self");
        peers.Hear(Hello("3f9c2a1b7d0e", host: "FOH-PC", inst: "aaaaaaaa"), "10.0.0.5", T0.AddSeconds(2), "self");
        peers.Hear(Hello("77aa77aa", name: "AGM", host: "MSG-PC", t: "messenger", http: 3023), "10.0.0.20", T0.AddSeconds(2), "self");
        var now = T0.AddSeconds(2);
        var apps = ShowBusView.Apps(peers.Snapshot(now), now, StagewatchSight.None);
        var app = apps.Single(a => a.Id == "3f9c2a1b7d0e");
        const string words = "two machines say they are 3f9c2a1b7d0e: FOH-PC at 10.0.0.5 and FOH-PC2 at 10.0.0.7";
        Assert.Equal(words, app.Conflict);
        Assert.True(ShowBusView.ConflictAmber(app));
        Assert.Equal("", apps.Single(a => a.Id == "77aa77aa").Conflict);
        Assert.Contains("10.0.0.5, 10.0.0.7", app.Words, StringComparison.Ordinal);                 // every address, the dialled one first

        var row = ShowBusView.Peers(apps).Single(r => r.Id == "3f9c2a1b7d0e");
        Assert.Equal(("amber", words), (row.Light, row.Headline));                                   // the context's peers and STATE's bus.apps
        Assert.Equal("grey", ShowBusView.Peers(apps).Single(r => r.Id == "77aa77aa").Light);
        Assert.Contains($"  {words} — {ShowBusView.ConflictHoldWords}", ShowBusView.BriefLines(apps));   // the assistant's brief
        var card = ShowAppCard.Of(app);                                                               // the Nodes page
        Assert.Equal("#FFB020", card.Hue);
        Assert.EndsWith(words + " — " + ShowBusView.ConflictHoldWords, card.Line, StringComparison.Ordinal);

        var graph = EyeGraph.Build(new EyeFacts { MachineName = "FOH", Health = CheckLight.Green, ShowApps = apps });   // the Eye
        var node = graph.Nodes.Single(n => n.Id == "app:3f9c2a1b7d0e");
        Assert.Equal((CheckLight.Amber, words), (node.Light, node.Sub));
        var edge = graph.Edges.Single(e => e.From == "app:3f9c2a1b7d0e" && e.To == EyeGraph.DeskId);
        Assert.Equal((CheckLight.Amber, "heard · " + ShowBusView.ConflictEdgeWords), (edge.Light, edge.Words));
        Assert.Equal(CheckLight.Grey, graph.Nodes.Single(n => n.Id == "app:77aa77aa").Light);

        // Quiet, it says quiet; gone, gone — never red for the conflict alone.
        var later = T0.AddSeconds(10);
        var quiet = ShowBusView.Apps(peers.Snapshot(later), later, StagewatchSight.None).Single(a => a.Id == "3f9c2a1b7d0e");
        Assert.False(ShowBusView.ConflictAmber(quiet));
        Assert.Equal("quiet", ShowBusView.Peers(new[] { quiet })[0].Headline);
    }

    // ---- the table under a flood -----------------------------------------------------------------

    [Fact]
    public void AFloodOfForgedHellosKeeps128IdsTheNeededOnesAmongThemAndCountsTheDrops()
    {
        var peers = new ShowBusPeers();
        peers.Need(new[] { "5ca1ab1e0001" });
        // The Stagewatch this desk dials, heard every 2 s, and a messenger heard once before the flood.
        peers.Hear(Hello("5ca1ab1e0001", inst: "0a1b2c3d"), "10.0.0.9", T0, "self");
        peers.Hear(Hello("77aa77aa", name: "AGM", t: "messenger", http: 3023), "10.0.0.20", T0, "self");

        // Ten thousand forged ids in ten seconds, from addresses all over the LAN, each a hello of its own.
        var forged = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var at = T0.AddMilliseconds(i + 1);
            Assert.True(peers.Hear(Hello("f0" + i.ToString("x6", System.Globalization.CultureInfo.InvariantCulture), inst: Inst(i + 1)), Lan(0, i + 1), at, "self"));
            forged++;
            if (i % 2000 == 0) peers.Hear(Hello("5ca1ab1e0001", inst: "0a1b2c3d"), "10.0.0.9", at, "self");
        }
        var end = T0.AddSeconds(10);
        var held = peers.Snapshot(end);
        Assert.Equal(ShowBusPeers.MaxPeers, held.Count);                                              // bounded
        Assert.Contains(held, p => p.Id == "5ca1ab1e0001");                                           // the needed one kept through it all
        Assert.Equal(forged + 2 - ShowBusPeers.MaxPeers, peers.Dropped);                              // every id put out is counted
        Assert.DoesNotContain(held, p => p.Id == "77aa77aa");                                         // not needed, heard longest ago: out first

        // When the flood stops, the next real hello finds room at once — in place of the oldest forged id.
        Assert.True(peers.Hear(Hello("77aa77aa", name: "AGM", t: "messenger", http: 3023), "10.0.0.20", end, "self"));
        held = peers.Snapshot(end);
        Assert.Equal(ShowBusPeers.MaxPeers, held.Count);
        Assert.Contains(held, p => p.Id == "77aa77aa");
        Assert.Equal(forged + 3 - ShowBusPeers.MaxPeers, peers.Dropped);

        // And a minute later the forged ids are forgotten; the dialled one, heard again, stays.
        peers.Hear(Hello("5ca1ab1e0001", inst: "0a1b2c3d"), "10.0.0.9", end.AddSeconds(55), "self");
        Assert.Equal(new[] { "5ca1ab1e0001" }, peers.Snapshot(end.AddSeconds(61)).Select(p => p.Id));
    }

    [Fact]
    public void AFloodAimedAtOneIdKeepsItsAddressesBoundedItsDialAndItsPlaceButMarksItInConflict()
    {
        var peers = new ShowBusPeers();
        peers.Need(new[] { "5ca1ab1e0001" });
        peers.Hear(Hello("5ca1ab1e0001", inst: "0a1b2c3d"), "10.0.0.9", T0, "self");
        for (var i = 1; i <= 5_000; i++)
        {
            peers.Hear(Hello("5ca1ab1e0001", host: "FORGED", inst: Inst(i)), Lan(1, i), T0.AddMilliseconds(i), "self");
            if (i % 1000 == 0) peers.Hear(Hello("5ca1ab1e0001", inst: "0a1b2c3d"), "10.0.0.9", T0.AddMilliseconds(i), "self");
        }
        var p = One(peers, T0.AddSeconds(5));
        Assert.Equal("10.0.0.9", p.Address);                                                          // the dial stays on the real one while it is heard
        Assert.Equal(ShowBusPeers.MaxAddresses, p.Addresses.Count);
        Assert.NotNull(p.Conflict);                                                                    // ids are not authenticated: the forgery is shown, never merged
        Assert.Equal(0, peers.Dropped);

        // Junk is never a peer and never a drop.
        Assert.False(peers.Hear(new byte[ShowBusPeers.ReadBytes + 1], "10.0.0.66", T0, "self"));
        Assert.False(peers.Hear(Encoding.UTF8.GetBytes("{\"t\":\"main\",\"id\":\"x\"}"), "10.0.0.66", T0, "self"));
        Assert.Equal(0, peers.Dropped);
    }

    // ---- the log under a flood ---------------------------------------------------------------------

    [Fact]
    public void TheLogNamesAFewIdsAMinuteHoweverManyAFloodMarksInConflict()
    {
        var peers = new ShowBusPeers();
        var log = new ShowBusTableLog();
        var lines = new List<(int Minute, ShowBusLogLine Line)>();
        var n = 0;
        // Five minutes of a flood: every second a hundred new forged ids, each marked in conflict by three datagrams (A, B,
        // A from two addresses), and the desk's poll reading the table once.
        for (var s = 0; s < 300; s++)
        {
            for (var k = 0; k < 100; k++, n++)
            {
                var id = "f1" + n.ToString("x6", System.Globalization.CultureInfo.InvariantCulture);
                var at = T0.AddSeconds(s).AddMilliseconds(k * 3);
                peers.Hear(Hello(id, host: "PC-A", inst: Inst(2 * n + 1)), Lan(2, n), at, "self");
                peers.Hear(Hello(id, host: "PC-B", inst: Inst(2 * n + 2)), Lan(3, n), at.AddMilliseconds(1), "self");
                peers.Hear(Hello(id, host: "PC-A", inst: Inst(2 * n + 1)), Lan(2, n), at.AddMilliseconds(2), "self");
            }
            var now = T0.AddSeconds(s).AddMilliseconds(999);
            var held = peers.Snapshot(now);
            Assert.Contains(held, p => p.Conflict is not null);                                     // the flood does mark ids
            foreach (var line in log.Lines(held, peers.Dropped, now)) lines.Add((s / 60, line));
        }
        Assert.Equal(30_000 - ShowBusPeers.MaxPeers, peers.Dropped);

        // However many: at most the named few, the counted rest and the drops in any minute.
        Assert.Equal(5, lines.Select(l => l.Minute).Distinct().Count());
        foreach (var minute in lines.GroupBy(l => l.Minute)) Assert.InRange(minute.Count(), 1, ShowBusTableLog.NamedPerMinute + 2);
        Assert.Equal(ShowBusTableLog.NamedPerMinute, lines.Count(l => l.Minute == 0 && l.Line.Text.Contains("two machines say they are", StringComparison.Ordinal)));
        var counted = lines.Single(l => l.Minute == 1 && l.Line.Text.Contains("not named one by one", StringComparison.Ordinal)).Line;
        Assert.True(counted.Warn);
        Assert.StartsWith("Show network: 5996 more ids marked in conflict", counted.Text, StringComparison.Ordinal);   // minute 0's 6,000 marks, 4 of them named
        Assert.Equal(5, lines.Count(l => l.Line.Text.Contains("dropped from the hello table", StringComparison.Ordinal)));   // once a minute
    }

    [Fact]
    public void TheLogSaysWhetherAMarkWasClearedForgottenOrPutOutOfTheFullTable()
    {
        var peers = new ShowBusPeers();
        var log = new ShowBusTableLog();
        peers.Need(new[] { "c1c1c1c1", "c2c2c2c2" });
        foreach (var (id, n) in new[] { ("c1c1c1c1", 1), ("c2c2c2c2", 2), ("c3c3c3c3", 3) })
        {
            peers.Hear(Hello(id, host: "PC-A", inst: Inst(10 * n)), "10.0.0.5", T0, "self");
            peers.Hear(Hello(id, host: "PC-B", inst: Inst(10 * n + 1)), "10.0.0.7", T0.AddSeconds(1), "self");
            peers.Hear(Hello(id, host: "PC-A", inst: Inst(10 * n)), "10.0.0.5", T0.AddSeconds(2), "self");
        }
        var marked = log.Lines(peers.Snapshot(T0.AddSeconds(2)), peers.Dropped, T0.AddSeconds(2));
        Assert.Equal(3, marked.Count);
        Assert.All(marked, l => Assert.True(l.Warn));
        Assert.Contains(new ShowBusLogLine(true, $"Show network: two machines say they are c1c1c1c1: PC-A at 10.0.0.5 and PC-B at 10.0.0.7 — {ShowBusView.ConflictHoldWords}."), marked);
        Assert.Empty(log.Lines(peers.Snapshot(T0.AddSeconds(3)), peers.Dropped, T0.AddSeconds(3)));     // said once, not at every poll

        // A full table: the marked id this desk does not need is put out first — said so, not said cleared.
        for (var i = 0; i < ShowBusPeers.MaxPeers; i++) peers.Hear(Hello("f2" + i.ToString("x6", System.Globalization.CultureInfo.InvariantCulture), inst: Inst(1000 + i)), Lan(4, i), T0.AddSeconds(3), "self");
        var full = log.Lines(peers.Snapshot(T0.AddSeconds(4)), peers.Dropped, T0.AddSeconds(4));
        Assert.Equal(2, full.Count);
        Assert.Contains(new ShowBusLogLine(false, "Show network: c3c3c3c3 was put out of the full hello table to make room — its conflict mark goes with it; heard again, it is marked again only once two machines are heard taking turns."), full);
        Assert.Contains(full, l => l.Warn && l.Text.StartsWith("Show network: 3 more apps dropped from the hello table (3 since start)", StringComparison.Ordinal));

        // c1 heard from one machine alone: a minute after the last turn its mark clears. c2 not heard again: forgotten.
        for (var s = 4; s <= 64; s += 2) peers.Hear(Hello("c1c1c1c1", host: "PC-A", inst: Inst(10)), "10.0.0.5", T0.AddSeconds(s), "self");
        var later = log.Lines(peers.Snapshot(T0.AddSeconds(64)), peers.Dropped, T0.AddSeconds(64));
        Assert.Equal(new[]
        {
            new ShowBusLogLine(false, "Show network: c1c1c1c1 — no two machines heard taking turns to say it for a minute: its conflict mark is cleared."),
            new ShowBusLogLine(false, "Show network: c2c2c2c2 has not been heard for a minute — forgotten, and its conflict mark with it."),
        }, later.OrderBy(l => l.Text, StringComparer.Ordinal));
    }

    // ---- the link's own address ------------------------------------------------------------------

    [Fact]
    public void AHelloThatSaysTheLinksHostIsNeverDrawnAsTheLinkedStagewatch()
    {
        var peers = new ShowBusPeers();
        // A machine at 10.0.0.66 says hello naming the link's address as its host, with a name that sorts first.
        peers.Hear(Hello("e1110001", name: "A", host: "10.0.0.5", inst: Inst(1)), "10.0.0.66", T0, "self");
        var sight = new StagewatchSight("Hall A", "10.0.0.5:9924", ShowBusState.Live, null, 0, null, "");
        var apps = ShowBusView.Apps(peers.Snapshot(T0), T0, sight);
        Assert.False(apps.Single(a => a.Id == "e1110001").Linked);
        Assert.Equal("stagewatch", Assert.Single(apps, a => a.Linked).Id);                          // the link drawn as an app of its own

        // The real one says hello from the link's address: it is the linked one; the other stays only heard.
        peers.Hear(Hello("5ca1ab1e0001", name: "Hall A", host: "FOH-PC", inst: Inst(2)), "10.0.0.5", T0, "self");
        apps = ShowBusView.Apps(peers.Snapshot(T0), T0, sight);
        Assert.Equal("5ca1ab1e0001", Assert.Single(apps, a => a.Linked).Id);

        // Two ids heard from the link's address at its door (a source forged): the id the link's context names tells them
        // apart; with none, neither is taken.
        peers.Hear(Hello("d0d0d0d0", name: "Hall A", inst: Inst(3)), "10.0.0.5", T0, "self");
        apps = ShowBusView.Apps(peers.Snapshot(T0), T0, sight);
        Assert.Equal("stagewatch", Assert.Single(apps, a => a.Linked).Id);
        var context = ShowContext.Parse("{\"v\":1,\"node\":{\"t\":\"main\",\"id\":\"5ca1ab1e0001\",\"name\":\"Hall A\"},\"at\":1,\"light\":\"green\",\"headline\":\"Keynote\",\"items\":[],\"links\":[],\"recent\":[],\"commands\":[],\"peers\":[]}");
        Assert.NotNull(context);
        apps = ShowBusView.Apps(peers.Snapshot(T0), T0, sight with { Context = context });
        Assert.Equal("5ca1ab1e0001", Assert.Single(apps, a => a.Linked).Id);
    }

    // ---- the certificate -------------------------------------------------------------------------

    [Fact]
    public void ACertificateSpans825DaysInAllWhateverTheMomentItIsMade()
    {
        foreach (var now in new[] { T0, T0.AddTicks(1234567), T0.AddMilliseconds(999.9) })
        {
            using var cert = TlsIdentity.Create("FOH", new[] { "localhost" }, new[] { IPAddress.Loopback }, now);
            var notBefore = cert.NotBefore.ToUniversalTime();
            var notAfter = cert.NotAfter.ToUniversalTime();
            Assert.Equal(TlsIdentity.Life, notAfter - notBefore);
            Assert.Equal(TimeSpan.FromDays(825), TlsIdentity.Life);
            Assert.InRange(now - notBefore, TlsIdentity.Backdate, TlsIdentity.Backdate + TimeSpan.FromSeconds(1));
            Assert.False(TlsIdentity.NeedsRenewal(cert, now));                                        // a new one is never over-long
            Assert.Equal("", TlsIdentity.RenewalReason(cert, now));
        }
        var (nb, na) = TlsIdentity.Window(T0.AddTicks(1));
        Assert.Equal((T0 - TimeSpan.FromHours(24), T0 - TimeSpan.FromHours(24) + TimeSpan.FromDays(825)), (nb, na));
    }

    [Fact]
    public void ACertificateIsRenewedWithAMonthLeftAndAnOverLongOneIsReplacedOnce()
    {
        using var cert = TlsIdentity.Create("FOH", new[] { "localhost" }, new[] { IPAddress.Loopback }, T0);
        var notAfter = cert.NotAfter.ToUniversalTime();
        Assert.True(TlsIdentity.NeedsRenewal(cert, notAfter - TimeSpan.FromDays(29)));
        Assert.False(TlsIdentity.NeedsRenewal(cert, notAfter - TimeSpan.FromDays(31)));
        Assert.Equal("this desk's certificate runs out within a month", TlsIdentity.RenewalReason(cert, notAfter - TimeSpan.FromDays(29)));

        // Made before 1.3: 826 days (a day back and 825 on) and 1,826 days (Patterns' five years), 800 days left.
        foreach (var days in new[] { 826, 1826 })
        {
            using var old = SelfSigned(T0.AddDays(-1), T0.AddDays(days - 1));
            var now = old.NotAfter.ToUniversalTime() - TimeSpan.FromDays(800);
            Assert.True(TlsIdentity.NeedsRenewal(old, now), days.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains($"spans {days} days, more than the 825", TlsIdentity.RenewalReason(old, now), StringComparison.Ordinal);
            // Its replacement, made then, is within the span and is kept at the next start: the change is seen once.
            using var made = TlsIdentity.Create("FOH", new[] { "localhost" }, new[] { IPAddress.Loopback }, now);
            Assert.False(TlsIdentity.NeedsRenewal(made, now.AddDays(1)));
        }
    }

    private static X509Certificate2 SelfSigned(DateTime notBefore, DateTime notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Old", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(new DateTimeOffset(notBefore), new DateTimeOffset(notAfter));
    }
}
