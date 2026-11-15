using System.Text.Json;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 §13.6.1–13.6.4 at this desk's line acceptor, pure: the offer, the one proof's decision and the verdict, held to the
/// contract's own vectors (the <c>line</c> rows of the <c>peerlink</c> block: a code over TLS, a key over plain); an unknown kid
/// refused in the same words as a wrong proof and never counted; a locked pair refused before its proof; a key bound to another
/// connector refused for its binding and never counted; and 13.5.6's table, per (address, kid), apart from everything else.
/// </summary>
public class LinkAcceptorTests
{
    // The contract's first proof row: Patterns 9f8e7d6c, Stagewatch 3f9c2a1b7d0e, the code k7qm-3xwd p9ra, over TLS.
    private const string Server = "9f8e7d6c";
    private const string Client = "3f9c2a1b7d0e";
    private const string SNonce = "00112233445566778899aabbccddeeff";
    private const string CNonce = "ffeeddccbbaa99887766554433221100";
    private const string TlsBind = "tls:sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04";
    private const string CodeClientProof = "a0622441e11e24be7577abcff6599b5b8c5031a06739a2774e94307d70b5c09f";
    private const string CodeServerProof = "a628a0aa5691349ced0d2e1d8b9b13203b55bbd3d994a9e45925b74fc1369e02";
    private const string CodeLid = "c1bb791379aa366c";

    // The contract's key row over plain: the ptk_ key, kid bb3f97d01c274e15.
    private const string Key = "ptk_3vq8xk2m9tz7w4hc6r5n2b8d7f4g9j3k";
    private const string KeyKid = "bb3f97d01c274e15";
    private const string KeyClientProof = "d64f0760b67d30c1ae6e98e337a6990767a5382a851950063e3735ad88797ba6";
    private const string KeyServerProof = "386af6ffb6c7e13e94481111eb56fde13df8993bdeadcd96411900a82ffd012d";
    private const string KeyLid = "e7bb3c6328d0766c";

    private static LinkAsk Ask(string kid) => new(1, "main", Client, CNonce, kid);

    private static int Unlocked(string kid) => 0;

    [Fact]
    public void TheCodeVectorVerifiesAndTheVerdictCarriesTheServersProofAndTheLid()
    {
        var code = LinkAcceptor.Code("k7qm-3xwd p9ra")!;
        Assert.Equal(("code", "code", "operate", (string?)null), (code.Kid, code.Verified, code.Scope, code.Bound));
        Assert.Equal("5155f2dfc471eeab6498c03b18e3e3df5f8c9f7a2e5f85c8ed4526aaad49e5c8", Convert.ToHexStringLower(code.K));
        var d = LinkAcceptor.Decide(Ask("code"), CodeClientProof, Server, SNonce, TlsBind, [code], Unlocked);
        Assert.True(d.Ok);
        Assert.Equal((CodeServerProof, CodeLid, (string?)null), (d.Proof, d.Lid, d.CountKid));

        var verdict = LinkAcceptor.Verdict(code, d.Proof!, d.Lid!, ["final"], "a41f09c2", 2000);
        Assert.StartsWith("paired ", verdict, StringComparison.Ordinal);
        // 13.6.4's example verdict, field for field in its order, up to what this desk offers.
        Assert.Equal("paired {\"v\":1,\"verified\":\"code\",\"scope\":\"operate\",\"proof\":\"" + CodeServerProof + "\",\"lid\":\"" + CodeLid
            + "\",\"level\":1,\"can\":[\"final\"],\"inst\":\"a41f09c2\",\"beatMs\":2000}", verdict);
        using var json = JsonDocument.Parse(verdict["paired ".Length..]);
        Assert.Equal(CodeServerProof, json.RootElement.GetProperty("proof").GetString());
    }

    [Fact]
    public void TheKeyVectorVerifiesForTheConnectorItIsBoundToAndIsRefusedForAnyOtherUncounted()
    {
        var key = LinkAcceptor.Key(Key, "main:" + Client, "drive")!;
        Assert.Equal((KeyKid, "key", "drive"), (key.Kid, key.Verified, key.Scope));
        var d = LinkAcceptor.Decide(Ask(KeyKid), KeyClientProof, Server, SNonce, "plain", [key], Unlocked);
        Assert.True(d.Ok);
        Assert.Equal((KeyServerProof, KeyLid), (d.Proof, d.Lid));

        // 13.5.2: a key speaks for the one connector it is bound to; its binding refusal is never counted.
        var other = LinkAcceptor.Key(Key, "playmap:0a1b2c3d", "drive")!;
        var bound = LinkAcceptor.Decide(Ask(KeyKid), KeyClientProof, Server, SNonce, "plain", [other], Unlocked);
        Assert.Equal(("binding", "this key speaks for PlayMap 0a1b2c3d", (string?)null), (bound.Class, bound.Words, bound.CountKid));
    }

    [Fact]
    public void AnUnknownKidIsRefusedInAWrongProofsWordsByteForByteAndOnlyTheKnownKidIsCounted()
    {
        var code = LinkAcceptor.Code("k7qm-3xwd p9ra")!;
        var asked = new List<string>();
        int Locked(string kid)
        {
            asked.Add(kid);
            return 0;
        }

        var wrong = LinkAcceptor.Decide(Ask("code"), new string('0', 64), Server, SNonce, TlsBind, [code], Locked);
        var unknown = LinkAcceptor.Decide(Ask(KeyKid), KeyClientProof, Server, SNonce, TlsBind, [code], Locked);
        Assert.Equal(("refused", PeerLink.ProofWords), (wrong.Class, wrong.Words));
        Assert.Equal((wrong.Ok, wrong.Class, wrong.Words, wrong.RetrySeconds), (unknown.Ok, unknown.Class, unknown.Words, unknown.RetrySeconds));
        Assert.Equal(("code", (string?)null), (wrong.CountKid, unknown.CountKid));
        Assert.Equal(new[] { "code", KeyKid }, asked);                                            // the table is asked of every kid, known or not

        // A proof of another shape is a wrong proof, counted for a known kid; the contract's refuse rows: another bind, a server proof.
        Assert.Equal("code", LinkAcceptor.Decide(Ask("code"), null, Server, SNonce, TlsBind, [code], Unlocked).CountKid);
        var otherBind = "tls:sha256:8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc";
        Assert.Equal("refused", LinkAcceptor.Decide(Ask("code"), CodeClientProof, Server, SNonce, otherBind, [code], Unlocked).Class);
        Assert.Equal("refused", LinkAcceptor.Decide(Ask("code"), CodeServerProof, Server, SNonce, TlsBind, [code], Unlocked).Class);
        Assert.Equal("refused", LinkAcceptor.Decide(Ask("code"), CodeClientProof, Server, "ffffffffffffffffffffffffffffffff", TlsBind, [code], Unlocked).Class);
    }

    [Fact]
    public void ALockedPairIsRefusedBeforeItsProofIsCheckedAndARightProofThenWaitsForTheMinute()
    {
        var code = LinkAcceptor.Code("k7qm-3xwd p9ra")!;
        var d = LinkAcceptor.Decide(Ask("code"), CodeClientProof, Server, SNonce, TlsBind, [code], _ => 37);
        Assert.Equal((false, "locked", LinkAcceptor.ProofsLocked, 37, (string?)null), (d.Ok, d.Class, d.Words, d.RetrySeconds, d.CountKid));
        Assert.Null(d.Proof);
    }

    [Fact]
    public void TheOfferNamesThisDeskItsNonceAndTheCertificateThisConnectionShows()
    {
        Assert.Equal("LINK 1 patterns " + Server + " " + SNonce + " " + TlsBind, LinkAcceptor.Offer(Server, SNonce, TlsBind));
        // Read back as a connector reads it (13.6.4's answer form).
        var offer = PeerLink.ReadOfferLine("OK @l1 " + LinkAcceptor.Offer(Server, SNonce, "plain"));
        Assert.NotNull(offer);
        Assert.Equal((1L, "patterns", Server, SNonce, "plain"), (offer.Speaks, offer.T, offer.Id, offer.SNonce, offer.Bind));
        Assert.True(PeerLink.IsHex(LinkAcceptor.NewNonce(), 32));
        Assert.NotEqual(LinkAcceptor.NewNonce(), LinkAcceptor.NewNonce());
        Assert.Equal("LINK busy — " + LinkAcceptor.UnfinishedWords, LinkAcceptor.Refusal("busy", LinkAcceptor.UnfinishedWords));
        Assert.True(LinkAcceptor.IsProofLine("LINK PROOF x"));
        Assert.False(LinkAcceptor.IsProofLine("LINK 1 main " + Client + " " + CNonce + " code"));
    }

    [Fact]
    public void ATokenIsACodeOnlyInTheCanonicalShapeAndAKeyOnlyInItsOwn()
    {
        Assert.Null(LinkAcceptor.Code(""));
        Assert.Null(LinkAcceptor.Code(null));
        Assert.Null(LinkAcceptor.Code("K7QM-3XWD-P9RÄ"));                                          // reaches only the legacy proof
        Assert.NotNull(LinkAcceptor.Code(PairingToken.New()));
        Assert.Null(LinkAcceptor.Key("ptk_short", "main:" + Client, "follow"));
        Assert.Null(LinkAcceptor.Key("ptk_3vq8xk2m9tz7w4hc6r5n2b8d7f4g9j3l", "main:" + Client, "follow"));   // 'l' is not of the alphabet
        Assert.Null(LinkAcceptor.Key(Key, "main", "follow"));
        Assert.Null(LinkAcceptor.Key(Key, "main:" + Client, "admin"));
        Assert.Equal(new[] { "follow", "post", "drive", "operate" }, LinkAcceptor.Scopes);
        Assert.Equal("this link's key is scope follow — Patterns FOH's operator can raise it on its Nodes page", LinkAcceptor.ScopeWords("follow", "FOH"));   // §6's example
        Assert.Equal((0, 3, -1), (LinkAcceptor.Rank("follow"), LinkAcceptor.Rank("operate"), LinkAcceptor.Rank("admin")));
    }

    [Fact]
    public void NoCredentialOrDecisionPrintsItsSecret()
    {
        var code = LinkAcceptor.Code("k7qm-3xwd p9ra")!;
        var d = LinkAcceptor.Decide(Ask("code"), CodeClientProof, Server, SNonce, TlsBind, [code], Unlocked);
        var printed = code + " " + d;
        Assert.DoesNotContain(Convert.ToHexStringLower(code.K), printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CodeServerProof, printed, StringComparison.Ordinal);
        Assert.Contains("Kid = code", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void FiveWrongProofsLockTheirPairForTheRestOfTheMinuteAndNoOtherPair()
    {
        var table = new WrongProofTable(LinkAcceptor.WrongProofsPerMinute);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(0, table.Reserve("10.0.0.7", "code", 1000 + i));
            table.Settle("10.0.0.7", "code", 1000 + i, wrong: true);
        }
        Assert.Equal(5, table.Of("10.0.0.7", "code", 2000));
        Assert.Equal(59, table.LockedFor("10.0.0.7", "code", 2000));
        Assert.Equal(59, table.Reserve("10.0.0.7", "code", 2000));                              // refused unchecked, the minute's rest
        Assert.Equal(0, table.LockedFor("10.0.0.7", KeyKid, 2000));                             // another kid from the address
        Assert.Equal(0, table.LockedFor("10.0.0.8", "code", 2000));                             // the code from another address
        Assert.Equal(0, table.LockedFor("10.0.0.7", "code", 61_000));                           // the minute over
        Assert.Equal(0, table.Reserve("10.0.0.7", "code", 61_000));
        table.Settle("10.0.0.7", "code", 61_000, wrong: false);
        Assert.Equal(0, table.Pairs);                                                           // a right proof leaves nothing behind
    }

    [Fact]
    public void ProofsCheckedTogetherNeverPassTheLimitBetweenThem()
    {
        var table = new WrongProofTable(LinkAcceptor.WrongProofsPerMinute);
        for (var i = 0; i < 5; i++) Assert.Equal(0, table.Reserve("10.0.0.7", "code", 1000));
        Assert.True(table.Reserve("10.0.0.7", "code", 1000) > 0);                               // five in flight: a sixth waits
        table.Settle("10.0.0.7", "code", 1000, wrong: false);
        Assert.Equal(0, table.Reserve("10.0.0.7", "code", 1000));
        for (var i = 0; i < 5; i++) table.Settle("10.0.0.7", "code", 1000, wrong: false);
        Assert.Equal(0, table.Pairs);
    }

    [Fact]
    public void TheTableStaysBoundedUnderAFloodOfAddresses()
    {
        var table = new WrongProofTable(LinkAcceptor.WrongProofsPerMinute);
        for (var i = 0; i < WrongProofTable.SweepAbove + 100; i++)
        {
            var at = "10.1." + (i / 256).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + (i % 256).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0, table.Reserve(at, "code", i));
            table.Settle(at, "code", i, wrong: true);
        }
        Assert.Equal(0, table.Reserve("10.9.9.9", "code", 200_000));
        table.Settle("10.9.9.9", "code", 200_000, wrong: true);                                 // the sweep runs: every older minute is over
        Assert.Equal(1, table.Pairs);
    }

    [Fact]
    public void LinkIsAWordBeforePairingAndTheWireLimitsKeepTheContractsNumbersWithTheDecksSlots()
    {
        var cmd = ControlProtocol.Parse("LINK 1 main " + Client + " " + CNonce + " code");
        Assert.Equal((RemoteCommandKind.Link, "1 main " + Client + " " + CNonce + " code"), (cmd.Kind, cmd.Text));
        Assert.True(ControlProtocol.OpenBeforePairing(cmd));
        Assert.True(ControlProtocol.OpenBeforePairing(ControlProtocol.Parse("LINK PROOF " + CodeClientProof)));
        Assert.Contains("LINK", ControlProtocol.OpenBeforePairingWords, StringComparison.Ordinal);

        var limits = WireLimits.Default;
        Assert.Equal((4, 10.0), (limits.UnfinishedPerAddress, limits.HandshakeSeconds));      // peerlink.unpaired.per.address, peerlink.handshake.ms
        Assert.Equal(PeerLink.Handshake.TotalSeconds, limits.HandshakeSeconds);
        Assert.Equal(5, LinkAcceptor.WrongProofsPerMinute);                                     // peerlink.wrong.proofs.per.minute
        Assert.True(limits.MaxAppLinks < limits.MaxClients && limits.MaxAppLinksPerAddress < limits.MaxClientsPerAddress);
        // With every app link, every LINK waiting for its proof and every silent place of one address held at once, the decks
        // still keep a quarter of that address's slots; of the port's, the app links take a quarter at most.
        Assert.True(limits.MaxClientsPerAddress - limits.MaxAppLinksPerAddress - 2 * limits.UnfinishedPerAddress >= limits.MaxClientsPerAddress / 4, "the decks keep a quarter of an address's slots");
        Assert.True(limits.MaxClients - limits.MaxAppLinks >= limits.MaxClients * 3 / 4, "the decks keep three quarters of the port");
        Assert.StartsWith("4 app links already open from this address", limits.AppLinksBusyWords(4), StringComparison.Ordinal);
        Assert.StartsWith("16 app links already open on this port", limits.AppLinksBusyWords(1), StringComparison.Ordinal);
    }
}
