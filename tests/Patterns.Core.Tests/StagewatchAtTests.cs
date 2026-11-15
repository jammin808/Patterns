using System.Text;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 99.7: the one rule for who is at a typed Stagewatch address (<see cref="StagewatchAt"/>) — the link's dial and its evaluator's
/// look again at a live address connection read it alike — on a scripted hello table and injected time. A hello names the address
/// only from that address (the datagram's source) on the typed port while it is live; an id in conflict names nothing; of two the
/// pinned one; a node-target, found or already-retargeted connection is never looked at again; only a link whose node's dial would
/// prove the key there is moved; and the node's own dial that would be refused now keeps the address's public view with words that say
/// why.
/// </summary>
public class StagewatchAtTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 19, 0, 0, DateTimeKind.Utc);
    private const string Id = "3f9c2a1b7d0e";
    private const string Typed = "addr:10.0.0.9:9924";
    private static readonly string Pin = TlsFingerprint.Prefix + new string('a', 64);
    private static readonly string Other = TlsFingerprint.Prefix + new string('b', 64);

    private static byte[] Hello(string id, string inst = "0a1b2c3d", string host = "STAGE-PC", int port = 9924, string name = "Hall A")
        => Encoding.UTF8.GetBytes($"{{\"t\":\"main\",\"v\":1,\"proto\":3,\"id\":\"{id}\",\"inst\":\"{inst}\",\"name\":\"{name}\",\"host\":\"{host}\",\"ports\":{{\"http\":{port},\"ws\":{port}}},\"caps\":[\"context\"]}}");

    private static IReadOnlyList<ShowBusPeer> Live(ShowBusPeers table, DateTime at) => StagewatchAt.Dialable(table.Snapshot(at), at);

    private static ShowBusPeer? Look(ShowBusPeers table, DateTime at, string target = Typed, string typed = Typed, bool already = false, Func<string, bool>? pinned = null)
        => StagewatchAt.Retarget(target, typed, already, Live(table, at), "10.0.0.9", 9924, pinned ?? (_ => false));

    [Fact]
    public void ALiveHelloFromTheTypedAddressOnItsPortNamesTheNodeThere()
    {
        var table = new ShowBusPeers();
        Assert.Null(Look(table, T0));                                                                    // no hello yet: the address is its own record
        table.Hear(Hello(Id), "10.0.0.9", T0.AddSeconds(1), "self");
        Assert.Equal(Id, Look(table, T0.AddSeconds(1))?.Id);
        Assert.Equal(Id, Look(table, T0.AddSeconds(7))?.Id);                                              // within the 6 s liveness window
        Assert.Null(Look(table, T0.AddSeconds(7.5)));                                                     // past it: nothing new to dial
    }

    [Fact]
    public void AHelloFromAnotherAddressOrPortOrOnlySayingThatHostNamesNothing()
    {
        var table = new ShowBusPeers();
        table.Hear(Hello(Id, host: "10.0.0.9"), "10.0.0.66", T0, "self");                                // says it is that host: a word, not a source
        table.Hear(Hello("b0b0b0b0b0b0", port: 9925), "10.0.0.9", T0, "self");                            // heard there, on another door
        Assert.Null(Look(table, T0));
        Assert.Null(StagewatchAt.Named(Live(table, T0), "10.0.0.9", 9924, _ => false, out var conflict));
        Assert.Null(conflict);
    }

    [Fact]
    public void AnIdInConflictNamesNothingAndSaysWhichMarkHoldsIt()
    {
        var table = new ShowBusPeers();
        table.Hear(Hello(Id, inst: "aaaaaaaa", host: "STAGE-PC"), "10.0.0.9", T0, "self");
        table.Hear(Hello(Id, inst: "bbbbbbbb", host: "STAGE-PC2"), "10.0.0.7", T0.AddSeconds(1), "self");
        table.Hear(Hello(Id, inst: "aaaaaaaa", host: "STAGE-PC"), "10.0.0.9", T0.AddSeconds(2), "self");
        var at = T0.AddSeconds(2);
        Assert.NotNull(Assert.Single(table.Snapshot(at)).Conflict);
        Assert.Null(Look(table, at));
        Assert.Null(StagewatchAt.Named(Live(table, at), "10.0.0.9", 9924, _ => true, out var conflict));   // pinned or not
        Assert.Equal(Id, conflict?.Id);
    }

    [Fact]
    public void OfTwoNodesHeardAtTheAddressOnlyThePinnedOneIsNamed()
    {
        var table = new ShowBusPeers();
        table.Hear(Hello(Id, inst: "aaaaaaaa", name: "Hall A"), "10.0.0.9", T0, "self");
        table.Hear(Hello("b0b0b0b0b0b0", inst: "cccccccc", name: "Hall B"), "10.0.0.9", T0, "self");
        Assert.Null(Look(table, T0));                                                                     // two, neither pinned: nothing new
        Assert.Equal("b0b0b0b0b0b0", Look(table, T0, pinned: id => id == "b0b0b0b0b0b0")?.Id);
    }

    [Fact]
    public void ANodeTargetAFoundTargetAndAConnectionLookedAtOnceAreNeverLookedAtAgain()
    {
        var table = new ShowBusPeers();
        table.Hear(Hello(Id), "10.0.0.9", T0, "self");
        Assert.Null(Look(table, T0, target: "node:" + Id));                                              // hello-named, typed: its target is the node
        Assert.Null(Look(table, T0, target: "node:" + Id, typed: ""));                                    // found, or named by id
        Assert.Null(Look(table, T0, already: true));                                                     // a second hello after the retarget
        Assert.Equal(Id, Look(table, T0)?.Id);
    }

    [Fact]
    public void TheDialAndTheEvaluatorNameWhatEachTableSays()
    {
        // Each table with the node it names, written out: what the dial's Named gives and what the evaluator's Retarget gives on an
        // address connection not yet looked at — each against the expectation, not against the other.
        var cases = new List<(string Why, ShowBusPeers Table, Func<string, bool> Pinned, string? Named)>();
        ShowBusPeers T(Action<ShowBusPeers> hear)
        {
            var t = new ShowBusPeers();
            hear(t);
            return t;
        }
        cases.Add(("no hello", new ShowBusPeers(), _ => false, null));
        cases.Add(("one live from the address", T(t => t.Hear(Hello(Id), "10.0.0.9", T0, "self")), _ => false, Id));
        cases.Add(("one that only says the host", T(t => t.Hear(Hello(Id, host: "10.0.0.9"), "10.0.0.66", T0, "self")), _ => false, null));
        cases.Add(("two, neither pinned", T(t =>
        {
            t.Hear(Hello(Id), "10.0.0.9", T0, "self");
            t.Hear(Hello("b0b0b0b0b0b0", inst: "cccccccc"), "10.0.0.9", T0, "self");
        }), _ => false, null));
        cases.Add(("two, one pinned", T(t =>
        {
            t.Hear(Hello(Id), "10.0.0.9", T0, "self");
            t.Hear(Hello("b0b0b0b0b0b0", inst: "cccccccc"), "10.0.0.9", T0, "self");
        }), id => id == Id, Id));
        cases.Add(("one gone quiet", T(t => t.Hear(Hello(Id), "10.0.0.9", T0.AddSeconds(-7), "self")), _ => false, null));
        cases.Add(("one on another door", T(t => t.Hear(Hello(Id, port: 9925), "10.0.0.9", T0, "self")), _ => false, null));
        foreach (var (why, table, pinned, expected) in cases)
        {
            Assert.True(expected == StagewatchAt.Named(Live(table, T0), "10.0.0.9", 9924, pinned, out _)?.Id, "the dial, " + why);
            Assert.True(expected == Look(table, T0, pinned: pinned)?.Id, "the evaluator, " + why);
        }
    }

    [Fact]
    public void OnlyALinkWhoseNodesDialWouldProveTheKeyThereIsMoved()
    {
        const string node = "node:" + Id;
        Assert.True(StagewatchAt.ProvesThere(keyedHere: false, hasKey: true, node, node, Typed, level0: false, tlsOrLocal: true));        // bound to the node, held back here
        Assert.True(StagewatchAt.ProvesThere(false, true, "", node, Typed, false, true));                                                 // unbound: it goes where it is dialled
        Assert.False(StagewatchAt.ProvesThere(true, true, node, node, Typed, false, true));                                                // carried here already: never ended for it
        Assert.False(StagewatchAt.ProvesThere(false, false, node, node, Typed, false, true));                                              // no key: nothing would be verified
        Assert.False(StagewatchAt.ProvesThere(false, true, "node:b0b0b0b0b0b0", node, Typed, false, true));                                 // bound to another: held back there too
        Assert.False(StagewatchAt.ProvesThere(false, true, StagewatchKeyFor.None, node, Typed, false, true));                              // bound to nothing
        Assert.False(StagewatchAt.ProvesThere(false, true, node, node, Typed, level0: true, true));                                        // older than LINK: nothing proved
        Assert.False(StagewatchAt.ProvesThere(false, true, node, node, Typed, false, tlsOrLocal: false));                                  // plain off this machine: the key stays here
    }

    [Fact]
    public void TheNodesOwnDialThatWouldBeRefusedKeepsThePublicViewWithWordsThatSayWhy()
    {
        Assert.Null(StagewatchAt.Stays("", "", 0));                                                       // no pin, no hold: dial it again as the node
        Assert.Null(StagewatchAt.Stays(Pin, Pin, 0));                                                     // the pinned certificate shown here
        var held = StagewatchAt.Stays("", Pin, 12_400);
        Assert.Equal("this desk's key is held for it — the link dials it as that Stagewatch once the hold ends, or at LINK AGAIN", held);
        Assert.Equal(held, StagewatchAt.Stays("", Pin, 1));                                              // the words move only when why moves
        var changed = StagewatchAt.Stays(Pin, Other, 0)!;
        Assert.Contains("it shows " + TlsFingerprint.Short(Other) + ", not " + TlsFingerprint.Short(Pin) + " pinned for it", changed, StringComparison.Ordinal);
        Assert.Contains("ACCEPT it on the Nodes page", changed, StringComparison.Ordinal);
        Assert.Contains("answers without TLS", StagewatchAt.Stays(Pin, "", 0), StringComparison.Ordinal);
        Assert.Null(StagewatchAt.Stays("sha256:not-a-pin", Other, 0));                                    // nothing that is not a pin refuses
        var refused = StagewatchAt.Stays(Pin, Pin, 0, "other-id");
        Assert.Equal("its own dial from here was refused (other-id) — this address's public view is read until another Stagewatch process is heard here", refused);
        Assert.Equal(refused, StagewatchAt.Stays("", "", 12_400, "other-id"));                         // a refused dial says so before a hold or a pin
        foreach (var words in new[] { held, changed, refused })
            Assert.DoesNotContain("BIND KEY", words, StringComparison.Ordinal);                            // never the way out: it would bind the key to the address
    }
}
