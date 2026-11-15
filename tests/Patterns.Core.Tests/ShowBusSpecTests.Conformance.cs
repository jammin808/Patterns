using System.Text.RegularExpressions;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// 13.20 for this desk: "A node sends cap <c>peerlink</c>, and offers LINK, only when its tree passes the block below and the
/// scripted peer's scenarios for every carrier it uses, in each role it plays. Its §8 row lists anything it does not keep yet, and
/// what ends it." The table below is this desk's reading of the copy's "who passes what" for each of its roles — the acceptor on
/// its wire, the connector on <c>ws</c> to a Stagewatch, the connector on <c>line</c> to a Show Bus node device, and the twin —
/// each scenario run by a named test of this tree, not kept yet with what ends it, or not asked of that role and why (only where
/// 13.20 makes it conditional, or this desk's named reading says so). The cap follows the table: said only while every scenario
/// is run, so a tree that loses a scenario's test, or keeps one not yet, cannot say it; and once every one is run the cap must be
/// said. The offer does not follow it yet: the wire on 9697 accepts LINK and LINK PROOF, plain and over TLS, while rows below are
/// not kept — a departure from 13.20's first sentence, which the master's §8 row for Patterns does not list yet, and which
/// ends when the last row not kept here is run.
/// </summary>
public sealed partial class ShowBusSpecTests
{
    private enum Stands
    {
        /// <summary>Run by the tests named, against the scripted peer.</summary>
        Run,

        /// <summary>Asked of the role and not run whole yet: the words say what is missing and what ends it.</summary>
        NotKept,

        /// <summary>Asked only where the role does something it does not do here (side calls, enrolment, a carrier's own grammar).</summary>
        NotAsked,
    }

    /// <param name="Role">The role and carrier.</param>
    /// <param name="Id">The scenario (13.20's table) or the block's row kind (the twin's <c>clock</c>).</param>
    /// <param name="How">Run, not kept yet, or not asked of the role.</param>
    /// <param name="Tests">The tests that run it, <c>&lt;project&gt;/&lt;file&gt;:&lt;method&gt;</c>.</param>
    /// <param name="Words">What the tests assert, or what is missing and what ends it, or why it is not asked.</param>
    private sealed record Scenario(string Role, string Id, Stands How, string[] Tests, string Words);

    // The copy's lists of who passes what (13.20), as the table below was read from them: a new master that changes them fails here.
    private const string EveryConnector = "every connector: S1, S2, S4–S7, S11–S14, S17–S19 and S22, and S21 where it enrols;";
    private const string EveryAcceptor = "every acceptor: S1–S3, S8, S9, S12 (emitting), S14 (for the features it offers), S15–S20, S10 where it takes side calls, and S21 where it offers `enrol`;";
    private const string TheTwin = "Patterns' twin (13.18): the `clock` rows and S19.";

    private static readonly string[] ConnectorAsks = ["S1", "S2", "S4", "S5", "S6", "S7", "S11", "S12", "S13", "S14", "S17", "S18", "S19", "S22", "S21"];
    private static readonly string[] AcceptorAsks = ["S1", "S2", "S3", "S8", "S9", "S12", "S14", "S15", "S16", "S17", "S18", "S19", "S20", "S10", "S21"];
    private static readonly string[] TwinAsks = ["clock", "S19"];

    private const string Acceptor = "acceptor on line (9697, TCP and TLS)";
    private const string StagewatchLink = "connector on ws (the Stagewatch link)";
    private const string NodeDevice = "connector on line (a Show Bus node device, PlayMap's 9941)";
    private const string Twin = "the twin (13.18)";

    /// <summary>
    /// The scenarios each role may name as not asked: 13.20 makes S10 an acceptor's only where it takes side calls, and S21 any end's
    /// only where it enrols or offers <c>enrol</c>; nothing else is conditional. S22 on <c>ws</c> is this desk's reading, not 13.20's
    /// words: 13.20 asks S22 of every connector, and S22's acceptor speaks the line grammar (STATE, then NONCE), which a Stagewatch's
    /// <c>/ws</c> does not — so widening any set here is an edit a reviewer sees, never a relabelled row.
    /// </summary>
    private static readonly Dictionary<string, string[]> MayBeNotAsked = new(StringComparer.Ordinal)
    {
        [Acceptor] = ["S10", "S21"],
        [StagewatchLink] = ["S21", "S22"],
        [NodeDevice] = ["S21"],
        [Twin] = [],
    };

    private const string NoLinkToANode =
        "a node device is sent no LINK: round 98.5 keeps it at level 0 whatever a node offers (ShowBusNodeSession.Level, held against the "
        + "scripted peer offering LINK), so this verified line link's scenario does not run on this connector — ends when PlayMap's 9941 answers "
        + "LINK (Mapper's PM-9b, its wp4-peerlink) and a unit here sends it through that seam (OPEN L95, L112)";

    private const string NoTlsToANode =
        "the node device dials its address over plain TCP with no pin, so no certificate is pinned or probed on it — ends when it dials "
        + "PlayMap's 9941 over TLS by a pin and runs this against the peer";

    private const string NoPingToANode =
        "the node device's level-0 session sends no PING and keeps no stale or drop clock of its own (the device's TCP reconnect only), so "
        + "silence, the stuck beat and pongs are never read on it — ends when the session pings on its own timer (13.11) and runs this against the peer";

    private static string AppTest(string file, string method) => "Patterns.App.Tests/" + file + ":" + method;

    private static string CoreTest(string file, string method) => "Patterns.Core.Tests/" + file + ":" + method;

    private static readonly string LinkOnTheWire = AppTest("PeerLinkWireTests.Acceptor.cs", "ALinkOnTheWireProvesTheTokenAndAKeyBothWaysPlainAndOverTlsAndRunsAVerbWithNoSecretInAnyReader");
    private static readonly string WrongProof = AppTest("PeerLinkWireTests.Acceptor.cs", "AWrongProofAndAnUnknownKidAreRefusedInTheSameWordsTheConnectionEndedAndOnlyTheKnownKidCounted");
    private static readonly string Replayed = AppTest("PeerLinkWireTests.Acceptor.cs", "AReplayedLinkAForgedKidAServerProofAsAClientsAndASecondProofAreRefused");
    private static readonly string CueGoFinal = AppTest("PeerLinkWireTests.Receipts.cs", "ALinksCueGoIsFollowedByItsFinalOnceTheBoxSaysAndAKeptIdRunsNothingAgain");
    private static readonly string KeyRevoked = AppTest("PeerLinkWireTests.Keys.cs", "AKeyMadeOnTheDeskLinksAtItsScopeAndRevokedWhileLiveIsRefusedAtOnceItsConnectionEnded");
    private static readonly string SwProves = AppTest("PeerLinkWireTests.StagewatchLink.cs", "TheStagewatchLinkProvesItsKeyWithLinkPinsByTheProofAndCarriesOnlyItsSession");
    private static readonly string SwRefused = AppTest("PeerLinkWireTests.StagewatchLink.cs", "ARefusedOrBoundKeyIsHeldAndTheSocketReadsThePublicViewWithNoSession");
    private static readonly string SwPongs = AppTest("PeerLinkWireTests.Stagewatch.cs", "TheStagewatchLinkMatchesTheScriptedPeersPongsAndDropsItsSilence");
    private static readonly string SwLevelOne = AppTest("PeerLinkWireTests.StagewatchLink.cs", "AtLevelOneOnlyAMatchedPongIsASampleAndTheStuckBeatHoldsUntilAFrame");
    private static readonly string NodeLevelZero = AppTest("PeerLinkWireTests.NodeDevice.cs", "ANodeDeviceIsProvedByAuthProofOnALevelZeroAcceptorAndDiscardsWhatCameBeforeTheProof");

    private static readonly Scenario[] Conformance =
    [
        new(Acceptor, "S1", Stands.Run, [LinkOnTheWire],
            "verified both ways, the token as a code on plain and a key over TLS against the desk's certificate, one lid at both ends (the journal row names the connector's), a verb applied, a ping answered"),
        new(Acceptor, "S2", Stands.Run, [WrongProof],
            "a wrong code refused in 13.6.3's words, the connection ended by the desk, its pair counted"),
        new(Acceptor, "S3", Stands.Run, [WrongProof],
            "an unknown kid refused in S2's words byte for byte, the connection ended, never counted"),
        new(Acceptor, "S8", Stands.Run, [Replayed],
            "a LINK replayed with its cnonce, kid and proof against a fresh snonce, and a forged kid, refused"),
        new(Acceptor, "S9", Stands.Run, [Replayed],
            "the connector's server proof offered as its client proof, refused"),
        new(Acceptor, "S12", Stands.Run, [LinkOnTheWire, CueGoFinal],
            "emitting, the half this acceptor has: no stuck offered (LinkAcceptor.Can, until a measured stall, LP-1), so none emitted — the verdict's can lists no stuck and a pong is t1, t2 and t3, never STUCK; the PONG … STUCK half is held back by TheCapWaitsForEveryScenarioThisDeskOwes, which fails once Can offers stuck"),
        new(Acceptor, "S14", Stands.Run,
            [CueGoFinal, AppTest("PeerLinkWireTests.Receipts.cs", "AFinalPastReceiptFinalMsGoesToTheRecordAloneAndAGoThatAskedForASecondGoIsFinallyNotRun"), AppTest("PeerLinkWireTests.Receipts.cs", "ARelinkReadsItsKeptReceiptsUnderTheSameInstAndADeskStartedAgainAnswersUnknownUnderItsOwn")],
            "for final and receipts, the features its verdict offers: applied, accepted then final, refused, a repeated id answered from the record and run once, a final past receipt.final.ms to the record alone, RECEIPTS under the same inst after a relink and unknown under a new one (the drop, the 30 s and the in-flight bound are the connector's half)"),
        new(Acceptor, "S15", Stands.NotKept,
            [AppTest("PeerLinkWireTests.Acceptor.cs", "AFinishedLinkKeepsTheWiresOwnBoundsALineTooLongEndsItAndASlowReaderIsNeverCut"), AppTest("WirePeerTests.cs", "APeerThatTakesNoRepliesIsClosedAtTheCeilingNotKept")],
            "a line past link.command.bytes ends its connection and a reader that stops is given the newest STATE, never cut for pushes, against the scripted peer; the cut past link.replies.max is held on the wire's writer over a stream that never drains, not over the peer's socket, whose buffers take 256 short replies without a block — ends with a test against the peer whose reader stops while more than link.replies.max STATE asks wait (each answer tens of KB, so the socket's buffers fill whatever the kernel's sizes and the queue reaches the cut deterministically, WirePeer.WriteDeadline set long enough to keep the dead-write close out of it)"),
        new(Acceptor, "S16", Stands.Run,
            [AppTest("PeerLinkWireTests.Acceptor.cs", "AnUnpairedFloodHoldsFourPlacesAnAddressALinkWithoutItsProofIsClosedAtTheHandshakeAndAppLinksKeepToTheirSubCap"), AppTest("PeerLinkWireTests.Flood.cs", "ASilentFloodIsTimedFromTheDesksOwnAcceptWithEveryIdleWorkerHeldAndWithTheDesksThreadBusy")],
            "at most peerlink.unpaired.per.address LINKs without a verdict and as many silent connections an address, each closed at its seconds, timed from the desk's own accept"),
        new(Acceptor, "S17", Stands.Run, [KeyRevoked],
            "a key revoked on the desk while its link is live refused at once, its connection ended"),
        new(Acceptor, "S18", Stands.Run, [AppTest("PeerLinkWireTests.Keys.cs", "AKeyBoundToAnotherConnectorIsABindingRefusalNeverCountedAndKeyBindLetsItSpeakForTheRightOne")],
            "a key bound to another connector refused for its binding, never counted"),
        new(Acceptor, "S19", Stands.Run, [LinkOnTheWire, KeyRevoked],
            "no form of the token, no key, K, proof or nonce in the log, the journal, STATE or the context"),
        new(Acceptor, "S20", Stands.Run, [AppTest("PeerLinkWireTests.Acceptor.cs", "LoopbackTrustNeverStandsForALinksProofAndABrowsersRequestToTheWireIsClosedOverTlsToo")],
            "a trusted loopback never stands for a proof; a page's request with a foreign Origin and Host closed, plain and inside TLS"),
        new(Acceptor, "S21", Stands.Run, [AppTest("PeerLinkWireTests.Keys.cs", "ACodeOverTlsEnrolsAKeyAtFollowOverPlainItDoesNotAndANewCodeRevokesItAndEndsItsLink")],
            "the code over TLS enrols a key, over plain it does not, and a new code revokes what the old one enrolled"),
        new(Acceptor, "S10", Stands.NotAsked, [],
            "it takes no side calls: the line carrier has none, and this desk's web remote takes no session credential"),

        new(StagewatchLink, "S1", Stands.NotKept, [SwProves],
            "run: LINK proved over the upgrade with no bearer, pinned by the proof, one lid at both ends, a matched pong a clock sample; not run: a command applied, which on ws is its side calls' and is read only against the suite's stand-in (S14) — ends when it sends a command with a receipt and runs it through the peer"),
        new(StagewatchLink, "S2", Stands.Run, [SwRefused],
            "a refused key held peerlink.hold.refused.ms, the socket reading the public view with no session"),
        new(StagewatchLink, "S4", Stands.Run, [AppTest("PeerLinkWireTests.StagewatchLink.cs", "AVerdictWhoseProofDoesNotVerifyIsNeitherTakenPinnedNorFloored")],
            "a machine between, whose verdict's proof is this desk's own: nothing taken, pinned or floored, no side call"),
        new(StagewatchLink, "S5", Stands.Run, [AppTest("PeerLinkWireTests.Stagewatch.cs", "APinnedStagewatchIsDialledOverTlsWhateverAForgedHelloSaysOfIt")],
            "a pinned Stagewatch dialled over TLS whatever a forged hello says"),
        new(StagewatchLink, "S6", Stands.Run,
            [AppTest("PeerLinkWireTests.StagewatchLink.cs", "AStagewatchPinnedByItsProofThatShowsAnotherCertificateGetsNothingOfThisDesks"), AppTest("PeerLinkWireTests.Stagewatch.cs", "ACertificateChangedAtTheScriptedPeerGetsNothingOfThisDesksAndIsLookedAtNoSoonerThanItsProbe"), AppTest("ShowBus12AppTests.cs", "OffThisMachineTheKeyGoesOnlyToACertificateAPersonTrustedAndAChangeOfItWaitsForAccept")],
            "CERTIFICATE CHANGED: nothing of this desk's sent, the public view, a probe no sooner than peerlink.cert.probe.ms, accepted only with 8 hex typed"),
        new(StagewatchLink, "S7", Stands.Run, [AppTest("PeerLinkWireTests.StagewatchLink.cs", "AStagewatchThatNowOffersLessThanItsFloorIsRefusedUntilAPersonLowersIt")],
            "LINK stripped on a floored id refused until a person lowers the floor, no bearer sent"),
        new(StagewatchLink, "S11", Stands.Run, [SwPongs],
            "silent: stale past link.stale.ms, dropped past link.drop.ms, dialled again within the first wait"),
        new(StagewatchLink, "S12", Stands.Run, [SwLevelOne],
            "the stuck beat makes the show stuck, a pong never clears it, a frame does"),
        new(StagewatchLink, "S13", Stands.Run, [SwLevelOne, SwPongs],
            "only a pong that answers a ping sent is a clock sample; an unmatched one counted"),
        new(StagewatchLink, "S14", Stands.NotKept, [AppTest("ShowBusAppTests.cs", "AContextPostTheStagewatchDidNotConfirmIsSaidUnconfirmedNeverFailedAndPostedAgain")],
            "its side calls' outcomes are read by 13.12.3 (unconfirmed, never failed) against the suite's Stagewatch stand-in, not the scripted peer, and the link sends no command on ws, so final, repeat, RECEIPTS and the in-flight bound never arise on it — ends when it sends a command with a receipt and runs them through the peer"),
        new(StagewatchLink, "S17", Stands.Run, [AppTest("PeerLinkWireTests.StagewatchLink.cs", "AKeyRevokedOnALiveLinkEndsItsSessionAtOnce")],
            "a key revoked on a live link refused at once, its session ended"),
        new(StagewatchLink, "S18", Stands.Run, [SwRefused],
            "a key bound to another connector refused for its binding and held"),
        new(StagewatchLink, "S19", Stands.Run, [SwProves, AppTest("PeerLinkWireTests.Stagewatch.cs", "TheStagewatchLinksKeyReachesTheScriptedPeerOnlyAsItsBearerAndNoReaderOfThisDesk")],
            "the key, K and S in no reader of this desk, and never on the wire in clear"),
        new(StagewatchLink, "S22", Stands.NotAsked, [],
            "S22's acceptor speaks Patterns 88.1's line grammar (STATE, then NONCE); a Stagewatch before LINK is S7's floor and P-legacy's level 0 on ws"),
        new(StagewatchLink, "S21", Stands.NotAsked, [],
            "it enrols no key: it proves the swk_ key a person gives it"),

        new(NodeDevice, "S22", Stands.Run, [NodeLevelZero],
            "a level-0 acceptor's STATE then NONCE: the STATE before the proof discarded, AUTH-PROOF only on the 32-hex NONCE, no plain AUTH, no form of the code on the wire"),
        new(NodeDevice, "S1", Stands.NotKept, [], NoLinkToANode),
        new(NodeDevice, "S2", Stands.NotKept, [], NoLinkToANode),
        new(NodeDevice, "S4", Stands.NotKept, [], NoLinkToANode),
        new(NodeDevice, "S5", Stands.NotKept, [], NoTlsToANode),
        new(NodeDevice, "S6", Stands.NotKept, [], NoTlsToANode),
        new(NodeDevice, "S7", Stands.NotKept, [], NoLinkToANode),
        new(NodeDevice, "S11", Stands.NotKept, [], NoPingToANode),
        new(NodeDevice, "S12", Stands.NotKept, [], NoPingToANode),
        new(NodeDevice, "S13", Stands.NotKept, [], NoPingToANode),
        new(NodeDevice, "S14", Stands.NotKept, [NodeLevelZero],
            "run, the level-0 half: each command answered by its own id and read by 13.12.3 against the peer; not run: final, a repeated id, RECEIPTS and the in-flight bound, features of a verified link — ends when PlayMap's 9941 answers LINK with them and a unit here sends LINK through ShowBusNodeSession.Level's seam"),
        new(NodeDevice, "S17", Stands.NotKept, [], NoLinkToANode),
        new(NodeDevice, "S18", Stands.NotKept, [], NoLinkToANode),
        new(NodeDevice, "S19", Stands.NotKept, [NodeLevelZero, AppTest("NodeDeviceAppTests.cs", "ANewerPlayMapThatHoldsItsStateUntilThePairingIsPairedDrivenAndDrawnAndItsStateIsTaken")],
            "run, the level-0 half: no form of the pairing code in any line the scripted peer read, and none in STATE or on the wire to the suite's PlayMap stand-in; not run: canaries in the log, the journal and the context, and the key, K and S of a verified link — ends when a canary test reads those readers too and LINK is sent to a node"),
        new(NodeDevice, "S21", Stands.NotAsked, [],
            "it enrols no key"),

        new(Twin, "clock", Stands.Run, [CoreTest("PeerLinkVectorsTests.cs", "EveryRowHoldsOnThisDesksCore")],
            "every clock row of the block on the one LinkClock the twin and the Stagewatch link measure with"),
        new(Twin, "S19", Stands.Run,
            [AppTest("TwinSecretsAppTests.cs", "NoLineToAStandbyCarriesTheMachinesOwnSecretsAndTheShowsRideOnlyToOneOnThisMachine"), AppTest("TwinAppTests.cs", "AFreshDeskIsGivenAKeyAndNoLineHoldsItTheLogAndTheStripSayWhereItIsRead")],
            "the twin's key and the machine's other secrets as canaries over a real join: no line a standby hears, on this machine or another, carries one; a key made on a fresh desk in no log line, journal row, STATE, context, twin status or strip"),
    ];

    /// <summary>Whether every scenario this desk's roles are asked is run: the one condition on which it says cap <c>peerlink</c>.</summary>
    private static bool EveryScenarioKept() => Conformance.All(s => s.How != Stands.NotKept);

    /// <summary>The copy's lists are the ones the table was read from, each role names every scenario its list asks once, and each is a row.</summary>
    [Fact]
    public void EachRoleOfThisDeskNamesEveryScenarioItsListAsks()
    {
        var body = OneSpace(Contract().Body);
        foreach (var list in new[] { EveryConnector, EveryAcceptor, TheTwin })
            Assert.True(body.Contains(list, StringComparison.Ordinal), $"13.20 no longer says '{list}': read the new lists into this table");

        var rows = Contract().Body.Split('\n').Select(l => Regex.Match(l, @"^\| (S\d+) \| ")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(22, rows.Count);
        Assert.True(Block("peerlink").Any(r => r.StartsWith("{\"case\":\"clock\"", StringComparison.Ordinal)), "the block holds no clock rows");

        foreach (var (role, asks) in new[] { (Acceptor, AcceptorAsks), (StagewatchLink, ConnectorAsks), (NodeDevice, ConnectorAsks), (Twin, TwinAsks) })
        {
            var named = Conformance.Where(s => s.Role == role).Select(s => s.Id).ToList();
            Assert.True(named.Count == named.Distinct(StringComparer.Ordinal).Count(), $"{role} names a scenario twice");
            Assert.Equal(asks.OrderBy(a => a, StringComparer.Ordinal), named.OrderBy(a => a, StringComparer.Ordinal));
            Assert.All(named.Where(id => id != "clock"), id => Assert.Contains(id, rows));
        }
        Assert.All(Conformance, s => Assert.Contains(s.Role, new[] { Acceptor, StagewatchLink, NodeDevice, Twin }));

        // Not asked only where 13.20 (or this desk's named reading above) makes the scenario conditional: a row cannot leave the cap's
        // count by being relabelled.
        foreach (var s in Conformance.Where(s => s.How == Stands.NotAsked))
            Assert.True(MayBeNotAsked[s.Role].Contains(s.Id, StringComparer.Ordinal), $"{s.Role} {s.Id} is marked not asked, which 13.20 does not let that role say");
    }

    /// <summary>Each test the table names is a test of this tree; each scenario not run says what ends it, each not asked says why.</summary>
    [Fact]
    public void EveryScenarioRunIsATestOfThisTreeAndEveryOneNotKeptSaysWhatEndsIt()
    {
        foreach (var s in Conformance)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Words), $"{s.Role} {s.Id} says nothing");
            if (s.How == Stands.Run) Assert.NotEmpty(s.Tests);
            if (s.How == Stands.NotKept) Assert.True(Regex.IsMatch(s.Words, @"\bends (when|with)\b"), $"{s.Role} {s.Id} is not kept and says nothing of what ends it");
            if (s.How == Stands.NotAsked) Assert.Empty(s.Tests);
            foreach (var test in s.Tests)
            {
                var at = test.IndexOf(':', StringComparison.Ordinal);
                var file = Path.Combine([Root(), "tests", .. test[..at].Split('/')]);
                Assert.True(File.Exists(file), $"{s.Role} {s.Id}: no test file {test[..at]}");
                var method = test[(at + 1)..];
                var source = File.ReadAllText(file);
                Assert.True(Regex.IsMatch(source, @"\[(Avalonia)?(Fact|Theory)\][^;{]*?\b(void|Task) " + Regex.Escape(method) + @"\("),
                    $"{s.Role} {s.Id}: {test} is no test of this tree");
            }
        }
    }

    /// <summary>
    /// 13.20: cap <c>peerlink</c> is said only while every scenario the table asks is run — never by a tree that lost one, or keeps
    /// one not yet — and is said once every one is. Today it is not: the rows not kept are named in the message.
    /// </summary>
    [Fact]
    public void TheCapWaitsForEveryScenarioThisDeskOwes()
    {
        // S12 (emitting) is run on its feature-less half only: the day this acceptor offers stuck, PONG … STUCK needs its test first.
        foreach (var (desk, overTls) in new[] { (true, true), (true, false), (false, true), (false, false) })
            Assert.False(LinkAcceptor.Can(desk, overTls).Contains("stuck", StringComparer.Ordinal),
                "the acceptor offers stuck: name the test that emits PONG … STUCK in its S12 row before it does");

        var missing = Conformance.Where(s => s.How == Stands.NotKept).Select(s => $"{s.Role} {s.Id}").ToList();
        var says = ShowBusHello.Capabilities.Contains("peerlink", StringComparer.Ordinal);
        Assert.True(says == (missing.Count == 0),
            says ? "the hello says cap peerlink while these scenarios are not kept: " + string.Join(", ", missing)
                 : "every scenario is run: the hello must say cap peerlink (13.20), and the papers with it");
    }
}
