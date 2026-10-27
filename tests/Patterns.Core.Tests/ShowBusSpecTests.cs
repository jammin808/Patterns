using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// This tree's copy of Show Bus 1.3 (<c>docs/SHOWBUS.md</c>) and its scripted peer (<c>tools/showbus/peerlink-peer.mjs</c>), read
/// from this tree alone and held to the two numbers the master pins (§14.5): the body's SHA-256, which the header's Body line
/// prints, and the peer's, §14's <c>peerlink.peer.sha256</c>, each hashed as its bytes. Inside the hashed body every row of the
/// <c>```showbus</c> block is placed — held to the code of this desk that keeps it, or named: what this desk does without keeping
/// the row yet (said as it is today, and checked so), or a part of the contract this desk does not speak yet — so a copy that
/// brings a row nobody placed fails here. The <c>```peerlink</c> vectors the code beside the core answers are held to it here — the
/// hello's liveness and redial, the wire's legacy proof, the one <see cref="LinkClock"/> — and every other row is counted as the
/// PeerLink core's, which <c>PeerLinkVectorsTests</c> runs whole. Nothing here reads another tree.
/// </summary>
public sealed class ShowBusSpecTests
{
    /// <summary>
    /// The body's SHA-256 the master's header prints (Stagewatch's <c>docs/SHOWBUS.md</c>, Show Bus 1.3 final). A new master is
    /// copied whole, byte for byte, with its scripted peer, and both numbers here follow it.
    /// </summary>
    public const string BodySha256 = "9ca83889a3735eba9dd948a53be3045b9cd1855c68a3682d5e734adf8f901fd5";

    /// <summary>The scripted peer's SHA-256 the master's §14 pins (<c>peerlink.peer.sha256</c>).</summary>
    public const string PeerSha256 = "f36847cfebeee05e9e76fa4f2071a4c205a544fddb0231c247c6975893dabc28";

    private const string Start = "## 1. Rules every node follows";
    private const string BodyLine = "*Body SHA-256 (§14): ";
    private const string LandedLine = "*Landed (§14): ";
    private const string PeerRow = "peerlink.peer.sha256";
    private const string Follow = " — copy Stagewatch's docs/SHOWBUS.md and tools/showbus/peerlink-peer.mjs byte for byte, and pin their numbers here";

    /// <summary>The trees §14.5's Landed line may name, the master first.</summary>
    private static readonly string[] Trees = ["Stagewatch", "Patterns", "Mapper", "AGMessage"];

    /// <summary>The rows that take a row per word (§14.1).</summary>
    private static readonly string[] ManyRows = ["kind", "port", "cap", "reserved", "peerlink.feature"];

    /// <summary>The rows whose value is a word, not a whole number (§14.1, §14.2).</summary>
    private static readonly string[] WordRows = ["revision", "peerlink.label", "window.token.alphabet", PeerRow];

    private enum Relation
    {
        /// <summary>The code's number is the row's.</summary>
        Same,

        /// <summary>A limit a reader takes at least (§14.3): the code takes the row's number or more.</summary>
        TakesAtLeast,

        /// <summary>A limit on what a sender sends (§14.3): the code sends the row's number or less.</summary>
        SendsAtMost,

        /// <summary>A least interval (a <c>.min.ms</c> row): the code waits the row's number or more between two.</summary>
        WaitsAtLeast,

        /// <summary>A most interval or a deadline: the code's wait, or its bound on one, is the row's number or less.</summary>
        WaitsAtMost,
    }

    /// <summary>
    /// The rows a number of this desk's Core keeps, each with that number and how it stands to the row. The Stagewatch link's
    /// intervals are its beat loop's waits: a ping, the collect and the context's read and post ride a beat, and the beat's side
    /// calls run in its path rather than on 13.7.2's lane of their own, so a slow one stretches the beat it runs in
    /// (<c>ping.max.ms</c>, not kept yet). Then §13's numbers the PeerLink core keeps (<see cref="PeerLink"/>,
    /// <see cref="LinkClock"/>), each where it lives — the ones this desk kept before the core are read where they were.
    /// </summary>
    private static readonly (string Row, Relation How, Func<long> Code)[] Kept =
    [
        ("proto", Relation.Same, () => ShowBusHello.Revision),
        ("hello.port", Relation.Same, () => ShowBusHello.Port),
        ("hello.every.ms", Relation.Same, () => Ms(ShowBusHello.Every)),
        ("hello.bytes", Relation.Same, () => ShowBusHello.MaxBytes),
        ("hello.read.bytes", Relation.TakesAtLeast, () => ShowBusPeers.ReadBytes),
        ("hello.caps", Relation.Same, () => ShowBusPeers.KeptCaps),
        ("hello.cap.chars", Relation.Same, () => ShowBusPeers.CapChars),
        ("hello.ports", Relation.SendsAtMost, () => Doors().Count),
        ("hello.name.chars", Relation.Same, () => ShowBusHello.NameChars),
        ("hello.kind.chars", Relation.Same, () => ShowBusPeers.KindChars),
        ("hello.id.min", Relation.Same, () => ShowBusPeers.IdMin),
        ("hello.id.max", Relation.Same, () => ShowBusPeers.IdMax),
        ("hello.inst.chars", Relation.Same, () => ShowBusPeers.InstChars),
        ("peers.max", Relation.Same, () => ShowBusPeers.MaxPeers),
        ("peer.addresses", Relation.Same, () => ShowBusPeers.MaxAddresses),
        ("peer.stale.ms", Relation.Same, () => Ms(ShowBusLiveness.StaleAfter)),
        ("peer.gone.ms", Relation.Same, () => Ms(ShowBusLiveness.DropAfter)),
        ("peer.forget.ms", Relation.Same, () => Ms(ShowBusLiveness.ForgetAfter)),
        ("peer.conflict.clear.ms", Relation.Same, () => Ms(ShowBusPeers.ConflictClearsAfter)),
        ("link.beat.ms", Relation.Same, () => Ms(ShowBusLiveness.Beat)),
        ("link.stale.ms", Relation.Same, () => Ms(ShowBusLiveness.StaleAfter)),
        ("link.drop.ms", Relation.Same, () => Ms(ShowBusLiveness.DropAfter)),
        ("link.redial.first.ms", Relation.Same, () => Ms(ShowBusLiveness.RedialFirst)),
        ("link.redial.max.ms", Relation.Same, () => Ms(ShowBusLiveness.RedialMost)),
        ("link.connect.timeout.ms", Relation.Same, () => Ms(StagewatchFrame.DialWithin)),
        ("link.sidecall.timeout.ms", Relation.Same, () => Ms(StagewatchFrame.SideCallWithin)),
        ("ping.min.ms", Relation.WaitsAtLeast, () => Ms(ShowBusLiveness.Beat)),
        ("context.post.min.ms", Relation.WaitsAtLeast, () => Ms(StagewatchFrame.ContextEvery)),
        ("context.post.max.ms", Relation.WaitsAtMost, () => Ms(StagewatchFrame.ContextEvery + ShowBusLiveness.Beat)),
        ("proposal.collect.ms", Relation.WaitsAtMost, () => Ms(ShowBusLiveness.Beat)),
        ("clock.apart.ms", Relation.Same, () => Ms(LinkClock.ApartAfter)),
        ("clock.window", Relation.Same, () => LinkClock.Kept),
        ("request.id.chars", Relation.Same, () => WireReceipt.MaxId),
        ("context.items", Relation.Same, () => ShowContext.MaxItems),
        ("context.recent", Relation.Same, () => ShowContext.MaxRecent),
        ("context.commands", Relation.Same, () => ShowContext.MaxCommands),
        ("context.verb.chars", Relation.Same, () => ShowContext.MaxVerbChars),
        ("context.words.chars", Relation.Same, () => ShowContext.MaxWordsChars),
        ("context.words.read.chars", Relation.TakesAtLeast, () => ShowContext.MaxWordsChars),
        ("cert.days", Relation.Same, () => (long)TlsIdentity.Life.TotalDays),
        ("cert.backdate.hours", Relation.Same, () => (long)TlsIdentity.Backdate.TotalHours),
        ("cert.renew.days", Relation.Same, () => (long)TlsIdentity.RenewBefore.TotalDays),
        ("peerlink.hold.refused.ms", Relation.Same, () => Ms(StagewatchFrame.RefusedHold)),
        ("peerlink.version", Relation.Same, () => PeerLink.Version),
        ("peerlink.can.words", Relation.Same, () => PeerLink.CanWords),
        ("peerlink.nonce.bytes", Relation.Same, () => PeerLink.NonceBytes),
        ("peerlink.kid.bytes", Relation.Same, () => PeerLink.KidBytes),
        ("peerlink.lid.bytes", Relation.Same, () => PeerLink.LidBytes),
        ("peerlink.handshake.ms", Relation.Same, () => Ms(PeerLink.Handshake)),
        ("peerlink.stable.ms", Relation.Same, () => Ms(PeerLink.Stable)),
        ("peerlink.pings.matched", Relation.Same, () => PeerLink.PingsMatched),
        ("link.selfgap.ms", Relation.Same, () => Ms(PeerLink.SelfGap)),
        ("link.inflight.max", Relation.Same, () => PeerLink.InFlightMost),
        ("clock.rtt.max.ms", Relation.Same, () => Ms(LinkClock.RoundTripMost)),
        ("clock.fresh.ms", Relation.Same, () => Ms(LinkClock.FreshFor)),
        ("receipt.deadline.ms", Relation.Same, () => Ms(PeerLink.ReceiptDeadline)),
        ("receipt.final.ms", Relation.Same, () => Ms(PeerLink.ReceiptFinal)),
        ("receipts.kept", Relation.Same, () => PeerLink.ReceiptsKept),
        ("receipts.kept.ms", Relation.Same, () => Ms(PeerLink.ReceiptsKeptFor)),
        ("receipts.ask.max", Relation.Same, () => PeerLink.ReceiptsAskMost),
    ];

    /// <summary>The rows held by what this desk's code does rather than a number of it, each by a test below that names it.</summary>
    private static readonly string[] HeldByWhatItDoes =
    [
        "revision",                    // TheBlockIsWellFormed
        PeerRow,                       // TheScriptedPeerIsTheOneSection14Pins
        "peerlink.label",              // TheNumbersThisDesksCoreKeepsAreTheBlocks (the core's label; its features beside it)
        "peerlink.redial.jitter.pct",  // TheNumbersThisDesksCoreKeepsAreTheBlocks
        "context.links",               // TheContextsThisDeskReadsAndSendsKeepSection6sSizes (the reader)
        "context.peers",               // TheContextsThisDeskReadsAndSendsKeepSection6sSizes
        "context.bytes",               // TheContextsThisDeskReadsAndSendsKeepSection6sSizes (the reader)
        "proposal.line.chars",         // TheContextsThisDeskReadsAndSendsKeepSection6sSizes (this desk's own proposal line)
    ];

    /// <summary>
    /// What this desk does without keeping the row yet, said as it is today. A row whose today is checked below is checked both
    /// ways: when the code keeps the row, the check fails and the row moves to the held ones.
    /// </summary>
    private static readonly Dictionary<string, string> NotKeptYet = new(StringComparer.Ordinal)
    {
        ["context.bytes"] = "this desk's own context is not trimmed to it in §6's order (the reader half is held)",
        ["context.links"] = "this desk's own context sends every link among its items (the reader half is held)",
        ["context.items.first"] = "this desk's own context lists its items in the Eye's order, not its reds, ambers and link ends first",
        ["context.headline.chars"] = "this desk's own headline is the Eye's, not cut to it",
        ["context.item.id.chars"] = "this desk's own items carry the Eye's node ids, not cut to it",
        ["context.item.kind.chars"] = "this desk's own items carry the Eye's kinds, not cut to it",
        ["context.item.label.chars"] = "this desk's own items carry the Eye's labels, not cut to it",
        ["context.item.state.chars"] = "this desk's own items carry the Eye's sub-lines, not cut to it",
        ["context.link.end.chars"] = "this desk's own links name its items' ids, not cut to it",
        ["context.recent.chars"] = "this desk's own recent lines are cut at 240 characters (checked)",
        ["ping.max.ms"] = "the Stagewatch link pings once a beat and waits the whole beat, this row's number, after the beat's side calls — the context's read and post, the approval queue's calls, each up to link.sidecall.timeout.ms — which run in its path, not on 13.7.2's lane of their own: two pings are a beat and that work apart (checked)",
        ["report.retry.first.ms"] = "an owed §11 result is sent again on each two-second turn of the approval queue until an answer settles it",
        ["report.retry.max.ms"] = "an owed §11 result is sent again on each two-second turn of the approval queue until an answer settles it",
        ["report.retry.for.ms"] = "an owed §11 result is kept until an answer settles it (at most a hundred owed), with no span of its own",
    };

    /// <summary>Rows of what this desk does not do yet, or of another node's part.</summary>
    private static readonly Dictionary<string, string> NotThisDesks = new(StringComparer.Ordinal)
    {
        ["window.token.chars"] = Windows,
        ["window.token.alphabet"] = Windows,
        ["window.max"] = Windows,
        ["view.wrong.per.minute"] = Windows,
        ["window.grants"] = Windows,
        ["context.kept.ms"] = "a node that takes CONTEXT POST keeps one this long; this desk takes none yet (§8.2)",
        ["proposal.undelivered.ms"] = "the approval desk's (§11): Stagewatch keeps the queue; this desk proposes and collects",
        ["proposal.result.ms"] = "the approval desk's (§11): Stagewatch keeps the queue; this desk proposes and collects",
        ["peerlink.key.bits.min"] = Acceptor,
        ["peerlink.unpaired.per.address"] = Acceptor,
        ["peerlink.wrong.proofs.per.minute"] = Acceptor,
        ["peerlink.hold.disconnect.ms"] = "the connector's hold after an acceptor operator's disconnect, which no verdict or message of 1.3 names: not scheduled",
        ["peerlink.cert.probe.ms"] = "13.8.3's probe of a changed certificate, a MAY: the Stagewatch link's carrier, which takes the core at 93.2 (S6's first half)",
        ["link.check.max.ms"] = "the evaluator's own timer: the Stagewatch link's carrier, which takes the core at 93.2",
        ["link.replies.max"] = Acceptor,
        ["link.write.deadline.max.ms"] = "the link's writer lane: the Stagewatch link's carrier, which takes the core at 93.2",
        ["link.command.bytes"] = Acceptor,
        ["link.read.min.bytes"] = "what the link's reader takes whole: the Stagewatch link's carrier, which takes the core at 93.2",
        ["link.ws.in.bytes"] = "a message the link sends to Stagewatch's /ws: the Stagewatch link's carrier, which takes the core at 93.2",
        ["link.sidecall.inflight"] = "the side calls' lane of their own: the Stagewatch link's carrier, which takes the core at 93.2 (L84)",
        ["show.stuck.min.ms"] = Acceptor,
        ["show.stuck.max.ms"] = Acceptor,
        ["receipt.late.ms"] = Acceptor,
        ["cmd.expire.max.ms"] = Acceptor,
        ["log.repeat.summary.ms"] = "the link's log while a state repeats: the link's record in every reader, round 93's 93.3",
    };

    private const string Windows = "§12's windows: this desk holds no granted window yet";
    private const string Acceptor = "a LINK acceptor's: this desk accepts no LINK before round 98 (the plan's 97), and its wire offers none";

    /// <summary>
    /// §2's words this desk does not keep yet (docs/OPEN.md L73), as the copy says them: checked both ways, the words still the
    /// copy's and the desk's table still making room (<see cref="TheFullTablesWordsAreTheCopysAndThisDeskStillMakesRoom"/>).
    /// </summary>
    private const string FullTableWords = "keeps at most `peers.max` peers: a new id beyond that is dropped.";

    // ---- the copy and the kit ----------------------------------------------------------------------------------------

    /// <summary>
    /// §14.5: the body is hashed as its bytes, from its first line to the end — UTF-8 without a BOM, LF line ends and one final
    /// LF — as the peer is, never read as text first: a copy saved with CRLF, or with a BOM, is not the master's.
    /// </summary>
    [Fact]
    public void TheCopyIsTheMastersBodyAndItsHeaderPrintsTheMastersHash()
    {
        var bytes = File.ReadAllBytes(CopyPath());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "docs/SHOWBUS.md starts with a byte order mark");
        var body = BodyBytes(bytes);

        Assert.True(Array.IndexOf(body, (byte)'\r') < 0, "the body holds a CR: §14.5's body has LF line ends");
        Assert.True(body[^1] == (byte)'\n', "the body does not end with a line end");
        Assert.False(body.Length >= 2 && body[^2] == (byte)'\n', "the body ends with more than one line end");
        var hash = Sha(body);
        Assert.True(hash == BodySha256, $"the body hashes to {hash}, the master's to {BodySha256}{Follow}");

        var (header, words) = Contract();
        var headerLines = header.Split('\n');
        var printed = headerLines.Where(l => l.StartsWith("*Body SHA-256", StringComparison.Ordinal)).ToList();
        Assert.True(printed.Count == 1 && Printed(printed[0]) == BodySha256,
            $"the header prints [{string.Join(" | ", printed)}]: once, and only '{BodyLine}{BodySha256}.*' (§14.5)");

        var landed = headerLines.Where(l => l.StartsWith("*Landed", StringComparison.Ordinal)).ToList();
        Assert.True(landed.Count == 1, $"the header names the landed trees on {landed.Count} lines, not one (§14.5)");
        var trees = Landed(landed[0]);
        Assert.True(trees is not null && trees.Count > 0 && trees[0] == Trees[0] && trees.All(Trees.Contains) && trees.Distinct(StringComparer.Ordinal).Count() == trees.Count,
            $"'{landed[0]}' is not '{LandedLine}<trees>.*': from {string.Join(", ", Trees)}, each once, Stagewatch first (§14.5)");

        Assert.DoesNotContain(words.Split('\n'), l => l.StartsWith("*Body SHA-256", StringComparison.Ordinal) || l.StartsWith("*Landed", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScriptedPeerIsTheOneSection14Pins()
    {
        var pins = Rows().Where(r => r.StartsWith(PeerRow + " ", StringComparison.Ordinal)).Select(r => r[(PeerRow.Length + 1)..]).ToList();
        Assert.True(pins.Count == 1 && pins[0] == PeerSha256, $"§14 pins [{string.Join(", ", pins)}]: once, and only {PeerSha256}{Follow}");

        // Byte for byte: the file as it is on disk (.gitattributes keeps it LF on every checkout).
        var got = Sha(File.ReadAllBytes(PeerPath()));
        Assert.True(got == PeerSha256, $"tools/showbus/peerlink-peer.mjs hashes to {got}, §14 pins {PeerSha256}{Follow}");
    }

    /// <summary>13.20: each §14 number the scripted peer keeps names its row (<c>// 14: &lt;name&gt;</c>), held here to the block.</summary>
    [Fact]
    public void TheScriptedPeersNumbersAreTheBlocks()
    {
        var marked = File.ReadAllLines(PeerPath()).Where(l => l.StartsWith("const ", StringComparison.Ordinal) && l.Contains("; // 14: ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(marked);
        foreach (var line in marked)
        {
            var at = line.IndexOf("; // 14: ", StringComparison.Ordinal);
            var row = line[(at + "; // 14: ".Length)..].Trim();
            var value = line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..at].Trim();
            if (value.StartsWith('['))
            {
                var words = value.Trim('[', ']').Split(',').Select(w => w.Trim().Trim('\'')).ToList();
                Assert.True(ManyRows.Contains(row) && words.SequenceEqual(Many(row).Select(w => w[0])), $"the peer's {row} is [{string.Join(", ", words)}], not the block's rows");
            }
            else
            {
                Assert.True(value.Trim('\'') == Number(row), $"the peer's {row} is {value}, the block's {Number(row)}");
            }
        }
    }

    [Fact]
    public void TheBlockIsWellFormed()
    {
        var rows = Rows();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.True(row.Length > 0 && row.All(char.IsAscii) && !row.Contains("  ", StringComparison.Ordinal) && row == row.Trim(),
                $"the row '{row}' is not one line of ASCII words separated by one space (§14.1)");
            var words = row.Split(' ');
            Assert.True(words.Length >= 2 && IsName(words[0]), $"the row '{row}' names no row (§14.1)");
        }
        var single = rows.Select(r => r.Split(' ')[0]).Where(n => !ManyRows.Contains(n)).ToList();
        Assert.Equal(single.Count, single.Distinct(StringComparer.Ordinal).Count());
        foreach (var words in rows.Select(r => r.Split(' ')).Where(w => !ManyRows.Contains(w[0]) && !WordRows.Contains(w[0])))
            Assert.True(words.Length == 2 && long.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out _),
                $"the row '{string.Join(' ', words)}' is not a name and a whole number (§14.1)");

        // The revision this copy is, and the hello's proto its minor number (§2, §14.1).
        Assert.Equal("1.3", Number("revision"));
        Assert.Equal(Number("revision").Split('.')[1], ShowBusHello.Revision.ToString(CultureInfo.InvariantCulture));
    }

    // ---- the rows ----------------------------------------------------------------------------------------------------

    /// <summary>Every row of the block is held or named, and every name here is a row of the block: a new row is placed before it lands.</summary>
    [Fact]
    public void EveryRowOfTheBlockIsPlaced()
    {
        var rows = Rows().Select(r => r.Split(' ')[0]).Where(n => !ManyRows.Contains(n)).ToHashSet(StringComparer.Ordinal);
        var held = Kept.Select(k => k.Row).Concat(HeldByWhatItDoes).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Kept.Length + HeldByWhatItDoes.Length, held.Count);

        var placed = held.Concat(NotKeptYet.Keys).Concat(NotThisDesks.Keys).ToHashSet(StringComparer.Ordinal);
        var unplaced = rows.Where(r => !placed.Contains(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.True(unplaced.Count == 0, "rows of the block nobody placed: " + string.Join(", ", unplaced));
        var stale = placed.Where(p => !rows.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "names placed here that are no row of the block: " + string.Join(", ", stale));

        // A row is this desk's or not this desk's, never both. Only a held row's other half — a reader's limit held and the
        // sender's not kept yet, or a number held and the rule it is read by not kept yet — is named beside it.
        Assert.Empty(held.Intersect(NotThisDesks.Keys));
        Assert.Empty(NotKeptYet.Keys.Intersect(NotThisDesks.Keys));
    }

    [Fact]
    public void TheNumbersThisDesksCoreKeepsAreTheBlocks()
    {
        foreach (var (row, how, code) in Kept)
        {
            var want = Whole(row);
            var got = code();
            var holds = how switch
            {
                Relation.Same => got == want,
                Relation.TakesAtLeast or Relation.WaitsAtLeast => got >= want,
                _ => got <= want,
            };
            Assert.True(holds, string.Create(CultureInfo.InvariantCulture, $"{row}: the code keeps {got}, the block says {want} ({how})"));
        }

        // The redial's jitter moves a wait that far either way (13.15.1).
        var pct = Whole("peerlink.redial.jitter.pct") / 100.0;
        Assert.Equal(Math.Round(Whole("link.redial.first.ms") * (1 - pct)), ShowBusLiveness.Redial(0, 0).TotalMilliseconds);
        Assert.Equal(Math.Round(Whole("link.redial.first.ms") * (1 + pct)), ShowBusLiveness.Redial(0, 1).TotalMilliseconds);
        Assert.Equal(Math.Round(Whole("link.redial.max.ms") * (1 + pct)), ShowBusLiveness.Redial(30, 1).TotalMilliseconds);

        // The core's label and its features are the block's words (§14.1, 13.1).
        Assert.True(Number("peerlink.label") == PeerLink.Label, "the core's label is not §14's peerlink.label");
        Assert.Equal(Many("peerlink.feature").Select(w => w[0]), PeerLink.Features);

        // Not kept yet: the link's wait after a beat's work is the whole of ping.max.ms, so any work puts two pings past it.
        Assert.True(Ms(ShowBusLiveness.Beat) == Whole("ping.max.ms"), "the beat is no longer ping.max.ms: look again at where the row stands");
    }

    /// <summary>§2: the hello this desk says names only §14's doors, and its reader takes a datagram of <c>hello.read.bytes</c> and keeps the first <c>hello.caps</c> caps.</summary>
    [Fact]
    public void TheHelloThisDeskSaysAndReadsIsSection2s()
    {
        var doors = Many("port").Select(w => w[0]).ToHashSet(StringComparer.Ordinal);
        Assert.All(Doors(), d => Assert.True(doors.Contains(d), $"'{d}' is no port row"));

        var bare = Hello(Enumerable.Range(0, 40).Select(i => "cap" + i.ToString(CultureInfo.InvariantCulture)), "");
        var padded = Hello(Enumerable.Range(0, 40).Select(i => "cap" + i.ToString(CultureInfo.InvariantCulture)), new string('x', Whole("hello.read.bytes") - bare.Length));
        Assert.Equal(Whole("hello.read.bytes"), padded.Length);
        var heard = ShowBusPeers.Parse(padded, "10.0.0.5", new DateTime(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc), out _);
        Assert.NotNull(heard);
        Assert.Equal(Enumerable.Range(0, Whole("hello.caps")).Select(i => "cap" + i.ToString(CultureInfo.InvariantCulture)), heard.Caps);
    }

    /// <summary>
    /// §14's kinds as this desk names them — but two it does not name as the rows do yet — and its caps: every cap it says is a
    /// <c>cap</c> row for <c>patterns</c>, and the rows for <c>patterns</c> it does not say are the two whose parts it does not
    /// speak yet.
    /// </summary>
    [Fact]
    public void TheKindsAndCapsAreTheBlocksRows()
    {
        var kinds = Many("kind").ToDictionary(w => w[0], w => string.Join(' ', w.Skip(1)), StringComparer.Ordinal);
        Assert.True(kinds.ContainsKey(new ShowBusHello().Kind), "this desk's own kind is no kind row");
        foreach (var t in new[] { "main", "patterns", "messenger" })
            Assert.Equal(kinds[t], ShowBusPeers.KindWords(t));
        // Not kept yet: a Slides agent is named by this desk's own words for it, and a PlayMap by the reader's fallback for a
        // kind it does not know. Checked both ways: a name made the row's moves its kind to the loop above.
        foreach (var (t, today) in new[] { ("agent", "Slides agent"), ("playmap", "Playmap") })
        {
            Assert.Equal(today, ShowBusPeers.KindWords(t));
            Assert.NotEqual(kinds[t], today);
        }
        Assert.Equal(5, kinds.Count);

        var ours = Many("cap").Where(w => w.Skip(1).Contains("patterns")).Select(w => w[0]).ToHashSet(StringComparer.Ordinal);
        Assert.All(ShowBusHello.Capabilities, c => Assert.True(ours.Contains(c) && ShowBusPeers.IsCap(c), $"'{c}' is no cap row for patterns of §2's shape"));
        Assert.True(ShowBusHello.Capabilities.Length <= Whole("hello.caps"));
        // Not said yet: peerlink (this desk's wire offers no LINK) and pages (it opens no page granted to it).
        Assert.Equal(new[] { "pages", "peerlink" }, ours.Except(ShowBusHello.Capabilities).OrderBy(c => c, StringComparer.Ordinal));
        Assert.DoesNotContain(Many("reserved"), w => w.Skip(1).Contains("patterns"));
    }

    /// <summary>
    /// §6's sizes as this desk reads another app's context and sends its own: items, links, recent lines and peers bounded, a
    /// context of <c>context.bytes</c> read, this desk's own proposal line within <c>proposal.line.chars</c>; and its own recent
    /// lines cut at 240 today, not at <c>context.recent.chars</c>.
    /// </summary>
    [Fact]
    public void TheContextsThisDeskReadsAndSendsKeepSection6sSizes()
    {
        var items = string.Join(',', Enumerable.Range(0, 250).Select(i => $"{{\"id\":\"i{i.ToString(CultureInfo.InvariantCulture)}\",\"kind\":\"cue\",\"label\":\"x\"}}"));
        var links = string.Join(',', Enumerable.Range(0, 500).Select(_ => "{\"from\":\"i0\",\"to\":\"i1\",\"kind\":\"feeds\"}"));
        var recent = string.Join(',', Enumerable.Range(0, 30).Select(i => $"{{\"at\":{(1760101234567L + i).ToString(CultureInfo.InvariantCulture)},\"text\":\"r\"}}"));
        var peers = string.Join(',', Enumerable.Range(0, 40).Select(i => $"{{\"t\":\"main\",\"id\":\"p{i.ToString(CultureInfo.InvariantCulture)}aaaaa\",\"name\":\"n\"}}"));
        var read = ShowContext.Parse($"{{\"v\":1,\"node\":{{\"t\":\"main\",\"id\":\"a1b2c3d4\",\"name\":\"Hall A\"}},\"items\":[{items}],\"links\":[{links}],\"recent\":[{recent}],\"peers\":[{peers}]}}");
        Assert.NotNull(read);
        Assert.Equal(Whole("context.items"), read.Items.Count);
        Assert.Equal(Whole("context.links"), read.Links.Count);
        Assert.Equal(Whole("context.recent"), read.Recent.Count);
        Assert.Equal(Whole("context.peers"), read.Peers.Count);

        // A context of context.bytes as sent is read.
        var head = "{\"v\":1,\"node\":{\"t\":\"main\",\"id\":\"a1b2c3d4\",\"name\":\"Hall A\"},\"headline\":\"";
        var whole = head + new string('h', Whole("context.bytes") - head.Length - 2) + "\"}";
        Assert.Equal(Whole("context.bytes"), Encoding.UTF8.GetByteCount(whole));
        Assert.NotNull(ShowContext.Parse(whole));

        // This desk's own context: its items, recent lines and peers bounded.
        var graph = EyeGraph.Build(new EyeFacts
        {
            MachineName = "FOH",
            Devices = Enumerable.Range(0, 260).Select(i => new EyeDevice("d" + i.ToString(CultureInfo.InvariantCulture), "Projector", "PjLink", "10.0.0.30", true, true, "answering", CheckLight.Green, "")).ToList(),
        });
        var now = new DateTime(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc);
        var journal = Enumerable.Range(0, 30).Select(i => new ShowLogEntry(now.AddSeconds(i - 30), "desk", "LookRecall", "Walk-in", "Done", "")).ToList();
        var ownPeers = Enumerable.Range(0, 40).Select(i => new ShowContextPeer { Kind = "main", Id = "p" + i.ToString(CultureInfo.InvariantCulture) + "aaaaa", Name = "n" }).ToList();
        var own = ShowContextBuilder.FromEye(graph, "ab12cd34", "FOH", now, journal, ownPeers);
        Assert.Equal(Whole("context.items"), own.Items.Count);
        Assert.Equal(Whole("context.recent"), own.Recent.Count);
        Assert.Equal(Whole("context.peers"), own.Peers.Count);

        // This desk's own proposal line, whatever its title.
        Assert.InRange(DeskProposals.Line(new string('p', WireReceipt.MaxId), "a look", new string('t', 5000)).Length, 1, Whole("proposal.line.chars"));

        // Not kept yet: a recent line of this desk's own is cut at 240 characters, past context.recent.chars.
        var line = ShowContextBuilder.Line(new ShowLogEntry(now, "desk", "LookRecall", "Walk-in", "Done", new string('m', 400))).Text;
        Assert.Equal(240, line.Length);
        Assert.True(line.Length > Whole("context.recent.chars"), "a recent line now keeps context.recent.chars: move the row to the held ones");
    }

    /// <summary>
    /// §2's full table, which L73 (docs/OPEN.md) holds open: the copy says a new id past <c>peers.max</c> is dropped, and this
    /// desk's table puts out the id heard longest ago that it does not need to make room for it. Both are checked, so either
    /// moving is seen here.
    /// </summary>
    [Fact]
    public void TheFullTablesWordsAreTheCopysAndThisDeskStillMakesRoom()
    {
        Assert.Contains(FullTableWords, OneSpace(Contract().Body), StringComparison.Ordinal);

        var peers = new ShowBusPeers();
        var t0 = new DateTime(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < Whole("peers.max"); i++)
            peers.Hear(Bytes(Id(i)), Address(i), t0.AddMilliseconds(i), "self");
        var last = Id(Whole("peers.max"));
        peers.Hear(Bytes(last), "10.9.9.9", t0.AddSeconds(1), "self");
        var held = peers.Snapshot(t0.AddSeconds(1));
        Assert.Equal(Whole("peers.max"), held.Count);
        Assert.Contains(held, p => p.Id == last);          // the new id is heard
        Assert.DoesNotContain(held, p => p.Id == Id(0));   // the oldest one this desk does not need is put out
        Assert.Equal(1, peers.Dropped);

        static string Id(int i) => "aa" + i.ToString("x6", CultureInfo.InvariantCulture);
        static string Address(int i) => string.Create(CultureInfo.InvariantCulture, $"10.1.{i >> 8}.{i & 255}");
        static byte[] Bytes(string id) => Encoding.UTF8.GetBytes($"{{\"t\":\"main\",\"v\":1,\"proto\":3,\"id\":\"{id}\",\"name\":\"Hall\",\"host\":\"FOH\",\"ports\":{{\"http\":9924}},\"caps\":[\"timer\"]}}");
    }

    // ---- the vectors -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>```peerlink</c> vectors the code beside the PeerLink core answers, held to it here: liveness and the redial (the
    /// hello's own rule, which the core reads), §7's legacy proof as the wire's 9697 checks it, and the one <see cref="LinkClock"/>
    /// the twin, the Stagewatch link and the core measure — its admission (13.11.2: three samples a clock that checks only its
    /// stamps takes, and a stamp of 0, refused), its error and CLOCKS APART (13.11.5: 2010 ms with an error of 16 is not apart,
    /// 2020 is), and a best sample's freshness and fitness (13.11.4). Every other row — the four legacy rows that prove nothing among them — is
    /// the core's, which <c>PeerLinkVectorsTests</c> runs whole. Counted, so a vector nobody placed fails here.
    /// </summary>
    [Fact]
    public void TheVectorsThisDeskAnswersHold()
    {
        string[] core = ["canon", "key", "proof", "refuse", "legacy", "parse", "errms", "outcome", "machine"];
        var vectors = Block("peerlink");
        Assert.NotEmpty(vectors);
        var held = 0;
        var cores = 0;
        foreach (var text in vectors)
        {
            using var doc = JsonDocument.Parse(text);
            var v = doc.RootElement;
            var kind = v.GetProperty("case").GetString();
            switch (kind)
            {
                case "liveness":
                    var state = ShowBusLiveness.StateOf(TimeSpan.FromMilliseconds(v.GetProperty("silenceMs").GetInt64())) switch
                    {
                        ShowBusState.Live => "live",
                        ShowBusState.Stale => "stale",
                        _ => "down",
                    };
                    Assert.True(state == v.GetProperty("state").GetString(), text);
                    held++;
                    break;
                case "redial":
                    var ms = ShowBusLiveness.Redial(v.GetProperty("n").GetInt32(), v.GetProperty("j").GetDouble()).TotalMilliseconds;
                    var want = v.GetProperty("ms").GetInt32();
                    var tol = v.GetProperty("tol").GetInt32();
                    Assert.True(ms >= want - tol && ms <= want + tol, text);
                    held++;
                    break;
                case "legacy" when v.TryGetProperty("proof", out var proof):
                    var code = v.GetProperty("code").GetString()!;
                    var nonce = v.GetProperty("nonce").GetString()!;
                    var name = v.GetProperty("name").GetString()!;
                    Assert.True(WireProof.Proof(code, nonce, name) == proof.GetString(), text);
                    Assert.True(WireProof.Verify(code, nonce, name, proof.GetString()!), text);
                    held++;
                    break;
                case "clock" when v.TryGetProperty("samples", out var samples):
                    var clock = new LinkClock();
                    foreach (var s in samples.EnumerateArray())
                    {
                        var t = s.EnumerateArray().Select(x => x.GetInt64() * TimeSpan.TicksPerMillisecond).ToArray();
                        clock.Sample(t[0], t[1], t[2], t[3]);
                    }
                    if (v.TryGetProperty("refused", out var refused))
                    {
                        Assert.False(clock.Known, text);
                        Assert.Equal(refused.GetInt32(), clock.Refused);
                        held++;
                        break;
                    }
                    var clockTol = v.GetProperty("tol").GetDouble();
                    Assert.True(clock.Known, text);
                    Assert.InRange(clock.Offset.TotalMilliseconds, v.GetProperty("offsetMs").GetDouble() - clockTol, v.GetProperty("offsetMs").GetDouble() + clockTol);
                    Assert.InRange(clock.Delay.TotalMilliseconds, v.GetProperty("rttMs").GetDouble() - clockTol, v.GetProperty("rttMs").GetDouble() + clockTol);
                    Assert.InRange(clock.Error.TotalMilliseconds, v.GetProperty("errMs").GetDouble() - clockTol, v.GetProperty("errMs").GetDouble() + clockTol);
                    Assert.Equal(v.GetProperty("apart").GetBoolean(), clock.IsApart);
                    held++;
                    break;
                case "clock" when v.TryGetProperty("best", out var best):
                    var offset = TimeSpan.FromMilliseconds(best.GetProperty("offsetMs").GetDouble());
                    var error = TimeSpan.FromMilliseconds(best.GetProperty("errMs").GetDouble());
                    if (v.TryGetProperty("apart", out var apart)) Assert.True(LinkClock.Apart(offset, error) == apart.GetBoolean(), text);
                    if (best.TryGetProperty("ageMs", out var age))
                    {
                        var ageSpan = TimeSpan.FromMilliseconds(age.GetInt64());
                        Assert.Equal(v.GetProperty("fresh").GetBoolean(), LinkClock.Fresh(ageSpan));
                        Assert.Equal(v.GetProperty("fitFor300").GetBoolean(), LinkClock.FitFor(ageSpan, error, TimeSpan.FromMilliseconds(300)));
                    }
                    held++;
                    break;
                default:
                    Assert.True(core.Contains(kind), $"the vector case '{kind}' is placed nowhere: {text}");
                    cores++;
                    break;
            }
        }
        Assert.Equal(4 + 7 + 3 + 6 + 4, held);
        Assert.Equal(vectors.Count - held, cores);
        Assert.Equal(85, cores);
    }

    // ---- reading the copy --------------------------------------------------------------------------------------------

    private static long Ms(TimeSpan t) => (long)t.TotalMilliseconds;

    /// <summary>The doors this desk's hello can name, as it writes them.</summary>
    private static List<string> Doors()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new ShowBusPorts { Http = 1, Wire = 2, Ws = 3, Osc = 4, Https = 5 }));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    private static byte[] Hello(IEnumerable<string> caps, string pad)
        => Encoding.UTF8.GetBytes($"{{\"t\":\"main\",\"v\":1,\"proto\":3,\"id\":\"3f9c2a1b7d0e\",\"name\":\"Hall A\",\"host\":\"FOH-PC\",\"ports\":{{\"http\":9924}},\"caps\":[{string.Join(',', caps.Select(c => "\"" + c + "\""))}],\"zz\":\"{pad}\"}}");

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsLowerHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    private static bool IsName(string name) => name.Length > 0 && name.Split('.').All(p => p.Length > 0 && p.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')));

    /// <summary>The hash a Body line prints, or null when the line is not <c>*Body SHA-256 (§14): &lt;64 lowercase hex&gt;.*</c>.</summary>
    private static string? Printed(string line)
    {
        if (!line.StartsWith(BodyLine, StringComparison.Ordinal) || !line.EndsWith(".*", StringComparison.Ordinal)) return null;
        var hash = line[BodyLine.Length..^2];
        return hash.Length == 64 && hash.All(IsLowerHex) ? hash : null;
    }

    /// <summary>The trees a Landed line names, or null when the line is not <c>*Landed (§14): &lt;trees&gt;.*</c>.</summary>
    private static List<string>? Landed(string line)
        => line.StartsWith(LandedLine, StringComparison.Ordinal) && line.EndsWith(".*", StringComparison.Ordinal)
            ? line[LandedLine.Length..^2].Split(", ").ToList()
            : null;

    /// <summary>Every run of white space as one space: the copy's words read across its line breaks.</summary>
    private static string OneSpace(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The body's bytes as §14.5 hashes them: from the start of its first line to the end of the file.</summary>
    private static byte[] BodyBytes(byte[] file)
    {
        var lead = Encoding.UTF8.GetBytes("\n" + Start);
        if (file.AsSpan().StartsWith(lead.AsSpan(1))) return file;
        var at = file.AsSpan().IndexOf(lead);
        Assert.True(at >= 0, $"the line '{Start}' is not in docs/SHOWBUS.md");
        return file[(at + 1)..];
    }

    /// <summary>
    /// The copy's header and body as words, CRLF read as LF, for the tests that read what it says: its bytes are
    /// <see cref="TheCopyIsTheMastersBodyAndItsHeaderPrintsTheMastersHash"/>'s. The body runs from its first line to the end (§14.5).
    /// </summary>
    private static (string Header, string Body) Contract()
    {
        var text = File.ReadAllText(CopyPath(), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        Assert.True(lines.Count(l => l == Start) == 1, $"the line '{Start}' is not in docs/SHOWBUS.md once");
        var at = text.StartsWith(Start + "\n", StringComparison.Ordinal) ? 0 : text.IndexOf("\n" + Start + "\n", StringComparison.Ordinal) + 1;
        return (text[..at], text[at..]);
    }

    /// <summary>A fenced block of the body (<c>```name</c> to the next <c>```</c>), once.</summary>
    private static List<string> Block(string name)
    {
        var lines = Contract().Body.Split('\n');
        var opens = lines.Select((l, i) => (l, i)).Where(x => x.l == "```" + name).Select(x => x.i).ToList();
        Assert.True(opens.Count == 1, $"the body has {opens.Count} ```{name} blocks, not one");
        return lines.Skip(opens[0] + 1).TakeWhile(l => l != "```").ToList();
    }

    private static List<string> Rows() => Block("showbus");

    private static string Number(string name) => Rows().Select(r => r.Split(' ')).Single(w => w[0] == name)[1];

    private static int Whole(string name) => int.Parse(Number(name), NumberStyles.None, CultureInfo.InvariantCulture);

    private static List<string[]> Many(string name) => Rows().Select(r => r.Split(' ')).Where(w => w[0] == name).Select(w => w[1..]).ToList();

    private static string CopyPath() => Path.Combine(Root(), "docs", "SHOWBUS.md");

    private static string PeerPath() => Path.Combine(Root(), "tools", "showbus", "peerlink-peer.mjs");

    /// <summary>The tree this test was built from: the folder above the test binary that holds Patterns.sln.</summary>
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Patterns.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no Patterns.sln above " + AppContext.BaseDirectory + ": the copy is read from the tree");
    }
}
