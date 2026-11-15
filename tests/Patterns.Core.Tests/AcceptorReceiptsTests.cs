using System.Globalization;
using System.Text.Json.Nodes;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 §13.12.4–13.12.6 at this desk's wire as an acceptor: the record keeps 13.12.1's ids only, per connector, the last
/// receipts.kept for receipts.kept.ms (both bounds at their edge), as many connectors as the wire takes connections; a repeat is
/// answered as kept with <c>repeat</c> before <c>lateMs</c>; RECEIPTS asks name 1 to receipts.ask.max ids; a command more than
/// receipt.late.ms late says so in its receipt line; CUE GO's final is the cue row's outcome; the desk thread's stall is measured.
/// </summary>
public class AcceptorReceiptsTests
{
    private const string Conn = "main:3f9c2a1b7d0e";

    [Theory]
    [InlineData("s17-11aa22bb", true)]
    [InlineData("pm1-a41f09c2", true)]
    [InlineData("abcd9-x", true)]
    [InlineData("rq1", false)]                 // the link's own RECEIPTS ask, never a command
    [InlineData("l2", false)]                  // LINK's own lines
    [InlineData("a1", false)]                  // an id with no inst: a sender whose ids restart with its process
    [InlineData("s0-11aa22bb", false)]         // a counter from 1
    [InlineData("s01-11aa22bb", false)]
    [InlineData("abcde1-11aa22bb", false)]     // 1–4 letters
    [InlineData("s1-", false)]
    [InlineData("s1-11aa_22bb", false)]
    [InlineData("", false)]
    public void OnlyAnIdOf13121sShapeIsKept(string id, bool kept)
    {
        Assert.Equal(kept, AcceptorReceipts.Keeps(id));
        var r = new AcceptorReceipts();
        Assert.Null(r.Begin(Conn, id, 0));
        Assert.Equal(kept, r.Begin(Conn, id, 1) is not null);
        Assert.False(AcceptorReceipts.Keeps("s1-" + new string('a', WireReceipt.MaxId - 2)));      // past request.id.chars
        Assert.True(AcceptorReceipts.Keeps("s1-" + new string('a', WireReceipt.MaxId - 3)));
    }

    [Fact]
    public void ARepeatIsAnsweredAsKeptAndNeverRunsAndTheBoundsHoldAtTheirEdges()
    {
        var r = new AcceptorReceipts();
        Assert.Null(r.Begin(Conn, "s1-11aa22bb", 0));
        // While it runs: accepted, from the record.
        Assert.Equal("{\"ok\":true,\"stage\":\"accepted\",\"effect\":\"\",\"repeat\":true}", r.Begin(Conn, "s1-11aa22bb", 1));
        r.Keep(Conn, "s1-11aa22bb", AcceptorReceipts.Applied("blackout on"), 2);
        Assert.Equal("{\"ok\":true,\"stage\":\"applied\",\"effect\":\"blackout on\",\"repeat\":true}", r.Begin(Conn, "s1-11aa22bb", 3));
        // A refused one repeats refused, with its words.
        Assert.Null(r.Begin(Conn, "s2-11aa22bb", 3));
        r.Keep(Conn, "s2-11aa22bb", AcceptorReceipts.Refused("not armed"), 3);
        Assert.Equal("{\"ok\":false,\"error\":\"not armed\",\"repeat\":true}", r.Begin(Conn, "s2-11aa22bb", 4));

        // receipts.kept.ms: held at the edge, forgotten one millisecond past it.
        var keptMs = (long)PeerLink.ReceiptsKeptFor.TotalMilliseconds;
        Assert.NotNull(r.Begin(Conn, "s1-11aa22bb", 2 + keptMs));
        Assert.Null(r.Begin(Conn, "s1-11aa22bb", 3 + keptMs));

        // receipts.kept: the last 256 ids held, the 257th back pushes the oldest out.
        var b = new AcceptorReceipts();
        for (var i = 1; i <= PeerLink.ReceiptsKept; i++) b.Keep(Conn, Id(i), AcceptorReceipts.Applied(""), 10);
        Assert.NotNull(b.Begin(Conn, Id(1), 11));
        Assert.NotNull(b.Begin(Conn, Id(PeerLink.ReceiptsKept), 11));
        b.Keep(Conn, Id(PeerLink.ReceiptsKept + 1), AcceptorReceipts.Applied(""), 12);
        Assert.Null(b.Begin(Conn, Id(1), 13));                                                       // the oldest gave way; it runs, held as running
        Assert.Null(b.Begin(Conn, Id(2), 13));                                                       // and its place pushed out the next oldest
        Assert.NotNull(b.Begin(Conn, Id(4), 13));
    }

    [Fact]
    public void EachConnectorHasItsOwnRecordAndTheOneHeardLongestAgoGivesWayAtTheWiresCeiling()
    {
        var r = new AcceptorReceipts();
        r.Keep(Conn, "s1-11aa22bb", AcceptorReceipts.Applied("x"), 0);
        Assert.Null(r.Begin("patterns:9f8e7d6c", "s1-11aa22bb", 1));                                  // another connector's id is its own
        for (var i = 0; i < AcceptorReceipts.Connectors - 2; i++) r.Keep("main:c" + i.ToString(CultureInfo.InvariantCulture), "s1-11aa22bb", AcceptorReceipts.Applied(""), 2 + i);
        Assert.Equal(AcceptorReceipts.Connectors, r.ConnectorsHeld);
        Assert.NotNull(r.Begin(Conn, "s1-11aa22bb", 100));                                            // heard again: newest now
        r.Keep("main:one-more", "s1-11aa22bb", AcceptorReceipts.Applied(""), 101);
        Assert.Equal(AcceptorReceipts.Connectors, r.ConnectorsHeld);
        Assert.NotNull(r.Begin(Conn, "s1-11aa22bb", 102));                                            // kept
        Assert.Null(r.Begin("patterns:9f8e7d6c", "s1-11aa22bb", 103));                                // forgotten whole: heard longest ago
        Assert.Equal(AcceptorReceipts.Connectors, r.ConnectorsHeld);
    }

    [Fact]
    public void AReceiptsAnswerNamesTheInstAndEachIdsReceiptOrUnknownAndAnAskIsBounded()
    {
        var r = new AcceptorReceipts();
        Assert.Null(r.Begin(Conn, "s1-11aa22bb", 0));
        r.Keep(Conn, "s1-11aa22bb", AcceptorReceipts.Applied("GO 3 Walk-in — Done", 1200), 1);
        Assert.Null(r.Begin(Conn, "s2-11aa22bb", 1));
        var answer = JsonNode.Parse(r.Answer(Conn, ["s1-11aa22bb", "s2-11aa22bb", "s3-11aa22bb"], "a41f09c2", 2))!.AsObject();
        Assert.Equal("a41f09c2", answer["inst"]!.GetValue<string>());
        var receipts = answer["receipts"]!.AsObject();
        Assert.Equal(("applied", 1200), (receipts["s1-11aa22bb"]!["stage"]!.GetValue<string>(), receipts["s1-11aa22bb"]!["lateMs"]!.GetValue<int>()));
        Assert.Equal("accepted", receipts["s2-11aa22bb"]!["stage"]!.GetValue<string>());        // still running
        Assert.Equal("unknown", receipts["s3-11aa22bb"]!.GetValue<string>());
        Assert.Equal("unknown", JsonNode.Parse(r.Answer("main:other", ["s1-11aa22bb"], "a41f09c2", 2))!["receipts"]!["s1-11aa22bb"]!.GetValue<string>());

        var most = Enumerable.Range(1, PeerLink.ReceiptsAskMost).Select(Id).ToList();
        Assert.Equal(PeerLink.ReceiptsAskMost, AcceptorReceipts.AskOf(most)!.Count);
        Assert.Null(AcceptorReceipts.AskOf([.. most, Id(PeerLink.ReceiptsAskMost + 1)]));
        Assert.Null(AcceptorReceipts.AskOf([]));
        Assert.Null(AcceptorReceipts.AskOf(["s1-11aa22bb", "not an id!"]));
        Assert.Single(AcceptorReceipts.AskOf(["s1-11aa22bb", "s1-11aa22bb"])!);
        Assert.True(AcceptorReceipts.IsAsk("RECEIPTS s1-11aa22bb"));
        Assert.True(AcceptorReceipts.IsAsk("receipts"));
        Assert.False(AcceptorReceipts.IsAsk("RECEIPTSX s1"));
        Assert.False(AcceptorReceipts.IsAsk("CUE GO"));
    }

    [Fact]
    public void ARepeatPutsItsWordBeforeLateMsAndFrameAndLeavesTheirLookalikesInsideAStringAlone()
    {
        Assert.Equal("{\"ok\":true,\"stage\":\"applied\",\"effect\":\"x\",\"repeat\":true,\"lateMs\":1200}", ReceiptBook.Repeat(AcceptorReceipts.Applied("x", 1200)));
        Assert.Equal("{\"ok\":true,\"repeat\":true,\"lateMs\":5,\"frame\":9}", ReceiptBook.Repeat("{\"ok\":true,\"lateMs\":5,\"frame\":9}"));
        const string inside = "{\"ok\":true,\"effect\":\"said ,\\\"lateMs\\\":7 here\"}";
        Assert.Equal(inside[..^1] + ",\"repeat\":true}", ReceiptBook.Repeat(inside));
        Assert.NotNull(JsonNode.Parse(ReceiptBook.Repeat(inside)));
    }

    [Fact]
    public void ACommandRunMoreThanReceiptLateMsAfterItsReadSaysSoAndOneAtTheEdgeRanInTime()
    {
        var edge = (long)PeerLink.ReceiptLate.TotalMilliseconds;
        Assert.Null(AcceptorReceipts.Late(1000, 1000 + edge));
        Assert.Equal(edge + 1, AcceptorReceipts.Late(1000, 1001 + edge));
        Assert.Equal("OK", AcceptorReceipts.WithLate("OK", null));
        Assert.Equal("OK ran 1500 ms after it was read (lateMs)", AcceptorReceipts.WithLate("OK", 1500));
        Assert.Equal("OK paused — ran 1500 ms after it was read (lateMs)", AcceptorReceipts.WithLate("OK paused", 1500));
        Assert.Equal("ERR not armed — ran 1500 ms after it was read (lateMs)", AcceptorReceipts.WithLate("ERR not armed", 1500));
        Assert.Equal("OK {\"outcome\":\"Done\",\"lateMs\":1500}", AcceptorReceipts.WithLate("OK {\"outcome\":\"Done\"}", 1500));
        Assert.Equal("OK {\"lateMs\":1500}", AcceptorReceipts.WithLate("OK {}", 1500));
        // A line answer as the connector's 13.12.3 reads it: lateMs from the JSON.
        var read = PeerLink.ReadLine("OK @s1-11aa22bb {\"outcome\":\"Done\",\"lateMs\":1500}")!.Value.Reading;
        Assert.Equal((LinkOutcome.Applied, 1500L), (read.Outcome, read.LateMs));
    }

    [Fact]
    public void TheKeptReceiptOfAnAnswerIsWhatTheConnectorReadsFromTheLine()
    {
        Assert.Equal("{\"ok\":true,\"stage\":\"applied\",\"effect\":\"\"}", AcceptorReceipts.FromAnswer("OK"));
        Assert.Equal("{\"ok\":true,\"stage\":\"applied\",\"effect\":\"'opener.mp4' paused\"}", AcceptorReceipts.FromAnswer("OK 'opener.mp4' paused"));
        Assert.Equal("{\"ok\":false,\"error\":\"GO refused — not armed.\",\"lateMs\":1500}", AcceptorReceipts.FromAnswer("ERR GO refused — not armed.", 1500));
        Assert.Equal("{\"ok\":true,\"stage\":\"applied\",\"effect\":\"3 Walk-in — Done\"}",
            AcceptorReceipts.FromAnswer("OK {\"outcome\":\"Done\",\"last\":{\"number\":\"3\",\"name\":\"Walk-in\",\"outcome\":\"Done\"},\"standby\":null}"));
        Assert.Equal("{\"ok\":true,\"stage\":\"applied\",\"effect\":\"standby 4 Talk\"}", AcceptorReceipts.FromAnswer("OK {\"standby\":{\"number\":\"4\",\"name\":\"Talk\"}}"));
        // A long answer is cut: a receipt says what came of a command, never a state's whole text.
        var kept = JsonNode.Parse(AcceptorReceipts.FromAnswer("OK " + new string('x', 4000)))!["effect"]!.GetValue<string>();
        Assert.Equal(AcceptorReceipts.WordsMost + 1, kept.Length);
        // Each reads back through 13.12.3 as the outcome it keeps.
        Assert.Equal(LinkOutcome.Refused, PeerLink.ReadLine("OK @s1-11aa22bb " + ReceiptBook.Repeat(AcceptorReceipts.Refused("no")))!.Value.Reading.Outcome);
        var repeat = PeerLink.ReadLine("OK @s1-11aa22bb " + ReceiptBook.Repeat(AcceptorReceipts.Applied("cut")))!.Value.Reading;
        Assert.Equal((LinkOutcome.Applied, true), (repeat.Outcome, repeat.Repeat));
    }

    [Fact]
    public void ACueGosFinalIsItsRowsOutcomeAppliedWhenItRanRefusedOnlyWhenNothingRanAndNoneWhileItSettles()
    {
        CueExecutionRecord Row(CueOutcome o, string detail = "", int done = 2) => new(DateTime.UnixEpoch, "c3", "3", "Walk-in", o, "tcp", done, 2, detail, "e1");
        Assert.Null(CueFinal.Of(Row(CueOutcome.Requested)));
        Assert.Equal((true, "3 Walk-in — Done"), CueFinal.Of(Row(CueOutcome.Done)));
        Assert.Equal((true, "3 Walk-in — Done, with warnings"), CueFinal.Of(Row(CueOutcome.DoneWithWarnings)));
        // It ran: its actions are on the air, so a box that said no — or said nothing in its time — is named, never read as "not run".
        Assert.Equal((true, "3 Walk-in — Failed late: later: projector 2 said no"), CueFinal.Of(Row(CueOutcome.FailedLate, "later: projector 2 said no")));
        Assert.Equal((true, "3 Walk-in — Failed: the clip did not open"), CueFinal.Of(Row(CueOutcome.Failed, "the clip did not open", done: 1)));
        // Nothing ran: refused (13.12.2), and only then.
        Assert.Equal((false, "3 Walk-in — Failed: the clip did not open"), CueFinal.Of(Row(CueOutcome.Failed, "the clip did not open", done: 0)));
        Assert.Equal((false, "3 Walk-in — Refused"), CueFinal.Of(Row(CueOutcome.Refused, done: 0)));
        Assert.Equal((false, "3 Walk-in — Skipped"), CueFinal.Of(Row(CueOutcome.Skipped, done: 0)));
        // A run no reader settles any more is kept as its first answer read: accepted, unconfirmed in words, never applied.
        var givenUp = PeerLink.ReadLine("OK @s9-11aa22bb " + AcceptorReceipts.Accepted(AcceptorReceipts.GivenUpWords))!.Value.Reading;
        Assert.Equal((LinkOutcome.Accepted, AcceptorReceipts.GivenUpWords), (givenUp.Outcome, givenUp.Words));
        Assert.Equal("{\"ok\":true,\"stage\":\"accepted\",\"effect\":\"x\",\"lateMs\":1200}", AcceptorReceipts.Accepted("x", 1200));

        var (ok, keptOk) = CueFinal.Line("s9-11aa22bb", true, "3 Walk-in — Done", null);
        Assert.Equal("OK @s9-11aa22bb final {\"ok\":true,\"stage\":\"applied\",\"effect\":\"3 Walk-in — Done\"}", ok);
        Assert.Equal(AcceptorReceipts.Applied("3 Walk-in — Done"), keptOk);
        var read = PeerLink.ReadLine(ok)!.Value;
        Assert.Equal(("s9-11aa22bb", LinkOutcome.Applied, true, "3 Walk-in — Done"), (read.Id, read.Reading.Outcome, read.Reading.Final, read.Reading.Words));
        var (no, keptNo) = CueFinal.Line("s9-11aa22bb", false, "3 Walk-in — Failed late", 1300);
        Assert.Equal("ERR @s9-11aa22bb final 3 Walk-in — Failed late — ran 1300 ms after it was read (lateMs)", no);
        Assert.Equal("{\"ok\":false,\"error\":\"3 Walk-in — Failed late\",\"lateMs\":1300}", keptNo);
        var refused = PeerLink.ReadLine(no)!.Value.Reading;
        Assert.Equal((LinkOutcome.Refused, true), (refused.Outcome, refused.Final));
    }

    [Fact]
    public void TheDeskThreadsStallIsCountedInFixedBucketsAndSaidAsADistribution()
    {
        var stall = new DeskStall();
        Assert.Equal("no command from the wire measured yet", stall.Snapshot().Words);
        for (var i = 0; i < 98; i++) stall.Add(3);
        stall.Add(-5);                                                                                // a clock read out of order counts as no wait
        stall.Add(1001);
        var d = stall.Snapshot();
        Assert.Equal((100L, 1001L, 1L), (d.Total, d.MaxMs, d.Late));
        Assert.Equal(DeskStall.Bounds.Count + 1, d.Counts.Count);
        Assert.Equal((16L, 16L, 1001L), (d.Within(0.5), d.Within(0.99), d.Within(1.0)));
        Assert.Equal("100 wire commands taken: half within 16 ms, 99 in 100 within 16 ms, the longest 1001 ms after its read; 1 ran more than 1000 ms late (lateMs)", d.Words);
        stall.Add(1000);                                                                              // at receipt.late.ms: in time
        Assert.Equal(1L, stall.Snapshot().Late);
        stall.Add(60_000);
        Assert.Equal((60_000L, 60_000L), (stall.Snapshot().MaxMs, stall.Snapshot().Within(1.0)));
    }

    [Fact]
    public void TheSuperChecksDeskThreadRowIsARecordGreyBeforeAnyCommandGreenWithNoneLateAmberWithTheCount()
    {
        CheckRow Row(DeskStallFacts d) => SuperCheck.Run(new CheckFacts { RemoteEnabled = true, WirePort = 9697, DeskStall = d }).Rows.Single(r => r.Section == "REMOTE" && r.Item == "Desk thread");
        Assert.Equal(CheckLight.Grey, Row(DeskStallFacts.None).Light);
        Assert.Contains("'stuck' or 'expire'", Row(DeskStallFacts.None).Note, StringComparison.Ordinal);
        var stall = new DeskStall();
        stall.Add(4);
        Assert.Equal(CheckLight.Green, Row(stall.Snapshot()).Light);
        stall.Add(2500);
        var amber = Row(stall.Snapshot());
        Assert.Equal(CheckLight.Amber, amber.Light);
        Assert.StartsWith("FIX: the desk's thread kept 1 wire command waiting more than 1000 ms", amber.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts { RemoteEnabled = true, WirePort = 0 }).Rows, r => r.Item == "Desk thread");
    }

    private static string Id(int n) => "s" + n.ToString(CultureInfo.InvariantCulture) + "-11aa22bb";
}
