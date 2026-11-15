using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Show Bus 1.3 §13.12.4–13.12.6 at this desk's wire, the scripted peer dialling it as Stagewatch would and reading every answer by
/// 13.12.3 as a connector does: the verdict names <c>final</c> and <c>receipts</c> and never <c>stuck</c> or <c>expire</c>; a CUE GO a
/// box still owes its receipt is answered Requested, then <c>OK @id final …</c> once the box said yes and the row is Done — never
/// from the send; an id the record holds runs nothing again, answered accepted while it runs and with its final receipt after, a
/// refusal's included; RECEIPTS answers a link's own ids with this desk's inst and refuses a connection LINK did not verify; a
/// command the desk's thread took more than receipt.late.ms after its read says lateMs, writes its journal row and is counted in
/// the Super Check's stall distribution.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    /// <summary>A box behind a fake link that owes each line a receipt, and a cue that sends it one line.</summary>
    private static FakeDeviceLink CueOnABox(AcceptorDesk desk, int timeoutMs = 20_000)
    {
        var fake = new FakeDeviceLink();
        var services = desk.Services;
        services.Devices.LinkFactory = _ => fake;
        desk.Booted.Vm.State.Interactive.Devices.Add(new DeviceConfig
        {
            Name = "Proj", Link = DeviceLink.Tcp, Profile = DeviceProfile.PjLink, Port = "10.0.0.7", NetPort = 4352, Confirm = ConfirmLevel.Accepted, ConfirmTimeoutMs = timeoutMs, HearsShow = false,
        });
        desk.Booted.Vm.State.Interactive.Enabled = true;
        services.Devices.Reconcile();
        var cue = new RunCueConfig { Number = "01.010", Name = "Wall to main" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = "Proj", Value = "INPUT HDMI 1" });
        CueStacks.Caller(desk.Booted.Vm.State).Cues.Add(cue);
        desk.Booted.Vm.IsSandboxActive = false;
        services.CueStack.SetArmed(true, ActionOrigin.Desk);
        services.CueStack.StandbyFirst();
        Dispatcher.UIThread.RunJobs();
        return fake;
    }

    private static string Line(AcceptorDesk desk, int conn, Func<string, bool> match) => Str(ReadOn(desk, conn, match), "line")!;

    [AvaloniaFact]
    public void ALinksCueGoIsFollowedByItsFinalOnceTheBoxSaysAndAKeptIdRunsNothingAgain()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var fake = CueOnABox(desk);
        var (conn, linked) = Dialled(desk, DialDesk(desk, tls: false, files.Write("code", desk.Token), "code"));
        Assert.Equal(["final", "receipts"], linked["can"]!.AsArray().Select(n => n!.GetValue<string>()));
        ReadOn(desk, conn, l => l.StartsWith("STATE {", StringComparison.Ordinal));

        // The GO: Requested — accepted, the box still owes its receipt.
        const string go = "s1-11aa22bb";
        Send(desk, conn, "@" + go + " CUE GO");
        var first = Line(desk, conn, l => l.StartsWith("OK @" + go + " {", StringComparison.Ordinal) && !l.Contains("\"repeat\"", StringComparison.Ordinal));
        var read = PeerLink.ReadLine(first)!.Value;
        Assert.Equal((go, LinkOutcome.Accepted, false), (read.Id, read.Reading.Outcome, read.Reading.Final));
        // The answer is written before the wire hands the owed final to the desk's thread: wait for both, the box's line and the debt.
        PumpWhile(() => fake.Written.Count == 0 || desk.Control.FinalsOwed == 0, 5000);
        var execution = desk.Services.CueStack.History[0].ExecutionId;
        Assert.Contains("\"execution\":\"" + execution + "\"", first, StringComparison.Ordinal);
        Assert.Equal(1, desk.Control.FinalsOwed);

        // The same id again while it runs: answered accepted from the record, and the box hears nothing more.
        Send(desk, conn, "@" + go + " CUE GO");
        Assert.Equal("OK @" + go + " " + ReceiptBook.Repeat(ReceiptBook.Running), Line(desk, conn, l => l.StartsWith("OK @" + go + " {\"ok\"", StringComparison.Ordinal)));
        Assert.DoesNotContain(desk.Peer.Events, e => Str(e, "ev") == "read" && (Str(e, "line") ?? "").StartsWith("OK @" + go + " final", StringComparison.Ordinal));

        // The box says yes: the row is Done, and only then the final line — applied, final, the row's words.
        fake.Say("%1INPT=OK");
        var final = Line(desk, conn, l => l.StartsWith("OK @" + go + " final ", StringComparison.Ordinal));
        Assert.Equal("OK @" + go + " final {\"ok\":true,\"stage\":\"applied\",\"effect\":\"01.010 Wall to main — Done\"}", final);
        var settled = PeerLink.ReadLine(final)!.Value.Reading;
        Assert.Equal((LinkOutcome.Applied, true), (settled.Outcome, settled.Final));
        Assert.Equal(CueOutcome.Done, desk.Services.CueStack.History[0].Outcome);
        Assert.Equal(0, desk.Control.FinalsOwed);

        // After the final, a repeat is answered with the final receipt, repeat appended, and runs nothing.
        Send(desk, conn, "@" + go + " CUE GO");
        var repeat = Line(desk, conn, l => l.StartsWith("OK @" + go + " {\"ok\":true,\"stage\":\"applied\"", StringComparison.Ordinal));
        Assert.Equal((LinkOutcome.Applied, true), (PeerLink.ReadLine(repeat)!.Value.Reading.Outcome, PeerLink.ReadLine(repeat)!.Value.Reading.Repeat));
        Assert.Single(fake.Written);
        Assert.Single(desk.Services.CueStack.History, r => r.Number == "01.010" && r.Outcome != CueOutcome.Refused);

        // A refused verb is kept refused: nothing on standby now, and its repeat says so without a second GO.
        Send(desk, conn, "@s2-11aa22bb CUE GO");
        var refused = Line(desk, conn, l => l.StartsWith("ERR @s2-11aa22bb ", StringComparison.Ordinal));
        Send(desk, conn, "@s2-11aa22bb CUE GO");
        var again = PeerLink.ReadLine(Line(desk, conn, l => l.StartsWith("OK @s2-11aa22bb {", StringComparison.Ordinal)))!.Value.Reading;
        Assert.Equal((LinkOutcome.Refused, true), (again.Outcome, again.Repeat));
        Assert.StartsWith(refused["ERR @s2-11aa22bb ".Length..], again.Words, StringComparison.Ordinal);
        Assert.Single(desk.Services.CueStack.History, r => r.Outcome == CueOutcome.Refused);

        // RECEIPTS: the link's own ids, with this desk's inst; one it never read is unknown.
        Send(desk, conn, "@rq1 RECEIPTS " + go + " s2-11aa22bb s9-11aa22bb");
        var answer = JsonNode.Parse(Line(desk, conn, l => l.StartsWith("OK @rq1 ", StringComparison.Ordinal))["OK @rq1 ".Length..])!.AsObject();
        Assert.Equal(desk.Services.Kernel.Beacon.Instance, answer["inst"]!.GetValue<string>());
        Assert.Equal("01.010 Wall to main — Done", answer["receipts"]![go]!["effect"]!.GetValue<string>());
        Assert.False(answer["receipts"]!["s2-11aa22bb"]!["ok"]!.GetValue<bool>());
        Assert.Equal("unknown", answer["receipts"]!["s9-11aa22bb"]!.GetValue<string>());
        Send(desk, conn, "@rq2 RECEIPTS");
        Assert.Equal("ERR @rq2 " + AcceptorReceipts.AskWords, Line(desk, conn, l => l.StartsWith("ERR @rq2", StringComparison.Ordinal)));
        Send(desk, conn, "RECEIPTS " + go);
        Assert.Equal("ERR " + AcceptorReceipts.NeedIdWords, Line(desk, conn, l => l == "ERR " + AcceptorReceipts.NeedIdWords));

        // A connection LINK did not verify keeps no receipts: its ask is refused, before and after any pairing.
        using var deck = TestApp.Pump(Lines.Dial(desk.Port, tls: false));
        TestApp.Pump(deck.Send("@rq1 RECEIPTS " + go).ContinueWith(_ => true, TaskScheduler.Default));
        Assert.Equal("ERR @rq1 " + AcceptorReceipts.NotLinkedWords, TestApp.Pump(deck.NextMatching(l => l.StartsWith("ERR @rq1", StringComparison.Ordinal))));
    }

    [AvaloniaFact]
    public void ACommandTheDesksThreadTookLateSaysLateMsAndIsCountedAndOneInTimeIsNot()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var (conn, _) = Dialled(desk, DialDesk(desk, tls: false, files.Write("code", desk.Token), "code"));
        ReadOn(desk, conn, l => l.StartsWith("STATE {", StringComparison.Ordinal));

        Send(desk, conn, "@s1-11aa22bb BLACKOUT ON");
        Assert.Equal("OK @s1-11aa22bb", Line(desk, conn, l => l.StartsWith("OK @s1-11aa22bb", StringComparison.Ordinal)));
        var before = desk.Control.DeskStall;
        Assert.True(before.Total >= 1);
        Assert.Equal(0, before.Late);

        // The desk's thread is this test's: it is held while the wire's own thread reads the line, and the monotonic clock a wait is
        // counted on steps 1500 ms past that read before the thread is let go — so the wait is exactly that, whatever the runner's load.
        long now = 1_000_000;
        desk.Control.ReceiptsNowMs = () => Interlocked.Read(ref now);
        using var read = new ManualResetEventSlim();
        desk.Control.LineRead = rid => { if (rid == "s2-11aa22bb") read.Set(); };
        var sent = Task.Run(() => desk.Peer.Ask(new JsonObject { ["do"] = "send", ["conn"] = conn, ["line"] = "@s2-11aa22bb BLACKOUT OFF" }));
        Assert.True(read.Wait(TimeSpan.FromSeconds(30)), "the wire never read the line");
        Interlocked.Add(ref now, 1500);
        TestApp.Pump(sent);
        var late = Line(desk, conn, l => l.StartsWith("OK @s2-11aa22bb", StringComparison.Ordinal));
        Assert.Equal("OK @s2-11aa22bb ran 1500 ms after it was read (lateMs)", late);
        const long lateMs = 1500;
        Assert.Equal(1, desk.Control.DeskStall.Late);
        Assert.Equal(lateMs, desk.Control.DeskStall.MaxMs);
        PumpWhile(() => Rows(desk.Services, "LateCommand").Count == 0, 5000);
        var row = Assert.Single(Rows(desk.Services, "LateCommand"));
        Assert.Contains("(@s2-11aa22bb)", row.Message, StringComparison.Ordinal);
        Assert.Contains("lateMs", row.Message, StringComparison.Ordinal);

        // The kept receipt carries it too, read back by RECEIPTS as a connector would after a relink.
        Send(desk, conn, "@rq1 RECEIPTS s2-11aa22bb");
        var kept = JsonNode.Parse(Line(desk, conn, l => l.StartsWith("OK @rq1 ", StringComparison.Ordinal))["OK @rq1 ".Length..])!["receipts"]!["s2-11aa22bb"]!;
        Assert.Equal(lateMs, kept["lateMs"]!.GetValue<long>());

        // The Super Check's row says the distribution and that this desk offers no stuck or expire on it.
        var check = SuperCheck.Run(desk.Services.Metrics.GatherFacts()).Rows.Single(r => r.Section == "REMOTE" && r.Item == "Desk thread");
        Assert.Equal(CheckLight.Amber, check.Light);
        Assert.Contains("1 ran more than 1000 ms late", check.Value, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AFinalPastReceiptFinalMsGoesToTheRecordAloneAndAGoThatAskedForASecondGoIsFinallyNotRun()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var fake = CueOnABox(desk);
        long now = 1_000_000;
        desk.Control.ReceiptsNowMs = () => Interlocked.Read(ref now);
        var (conn, _) = Dialled(desk, DialDesk(desk, tls: false, files.Write("code", desk.Token), "code"));
        ReadOn(desk, conn, l => l.StartsWith("STATE {", StringComparison.Ordinal));

        // Answered Requested, then the box says yes 30 001 ms after the read: the connector has called it unconfirmed by then, so
        // nothing more is said on the line — the record takes the final, and RECEIPTS after a relink reads it.
        Send(desk, conn, "@s1-11aa22bb CUE GO");
        Line(desk, conn, l => l.StartsWith("OK @s1-11aa22bb {", StringComparison.Ordinal));
        PumpWhile(() => fake.Written.Count == 0 || desk.Control.FinalsOwed == 0, 5000);
        Interlocked.Add(ref now, (long)PeerLink.ReceiptFinal.TotalMilliseconds + 1);
        fake.Say("%1INPT=OK");
        PumpWhile(() => desk.Control.FinalsOwed > 0, 5000);
        Send(desk, conn, "@rq1 RECEIPTS s1-11aa22bb");
        var kept = JsonNode.Parse(Line(desk, conn, l => l.StartsWith("OK @rq1 ", StringComparison.Ordinal))["OK @rq1 ".Length..])!["receipts"]!["s1-11aa22bb"]!;
        Assert.Equal(("applied", "01.010 Wall to main — Done"), (kept["stage"]!.GetValue<string>(), kept["effect"]!.GetValue<string>()));
        Assert.DoesNotContain(desk.Peer.Events, e => Str(e, "ev") == "read" && (Str(e, "line") ?? "").StartsWith("OK @s1-11aa22bb final", StringComparison.Ordinal));

        // A cue that asks for confirmation: the first GO runs nothing, so its final follows at once, refused, and the GO that confirms
        // it is a command of its own, with its own id.
        var cue = new RunCueConfig { Number = "02.000", Name = "Confirmed", RequireConfirm = true };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.Note, Value = "n" });
        CueStacks.Caller(desk.Booted.Vm.State).Cues.Add(cue);
        desk.Services.CueStack.Standby(cue.Id);
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(GoGate.Lockout + TimeSpan.FromMilliseconds(100));                             // past the double-press lockout of the first GO
        Send(desk, conn, "@s2-11aa22bb CUE GO");
        var asked = PeerLink.ReadLine(Line(desk, conn, l => l.StartsWith("OK @s2-11aa22bb {\"outcome\"", StringComparison.Ordinal)))!.Value.Reading;
        Assert.Equal(LinkOutcome.Accepted, asked.Outcome);
        var final = PeerLink.ReadLine(Line(desk, conn, l => l.StartsWith("ERR @s2-11aa22bb final ", StringComparison.Ordinal)))!.Value.Reading;
        Assert.Equal((LinkOutcome.Refused, true, ControlService.ConfirmWords), (final.Outcome, final.Final, final.Words));
        Send(desk, conn, "@s3-11aa22bb CUE GO");
        Assert.StartsWith("OK @s3-11aa22bb {\"outcome\":\"Done\"", Line(desk, conn, l => l.StartsWith("OK @s3-11aa22bb", StringComparison.Ordinal) || l.StartsWith("ERR @s3-11aa22bb", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(0, desk.Control.FinalsOwed);
    }
    [AvaloniaFact]
    public void ACueGoWhoseBoxSaysNoOrNothingRanSoItsFinalIsAppliedWithTheWordsNeverRefused()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var fake = CueOnABox(desk, timeoutMs: 1000);
        var (conn, _) = Dialled(desk, DialDesk(desk, tls: false, files.Write("code", desk.Token), "code"));
        ReadOn(desk, conn, l => l.StartsWith("STATE {", StringComparison.Ordinal));

        // The box says no after the GO was answered Requested: the cue ran — its line went out — so the final is applied, its words
        // naming the box's refusal, and a connector never reads it as a cue to fire again (13.12.2: refused is "it did not run").
        Send(desk, conn, "@s1-11aa22bb CUE GO");
        Line(desk, conn, l => l.StartsWith("OK @s1-11aa22bb {", StringComparison.Ordinal));
        PumpWhile(() => fake.Written.Count == 0 || desk.Control.FinalsOwed == 0, 5000);
        fake.Say("%1INPT=ERR2");
        var no = PeerLink.ReadLine(Line(desk, conn, l => l.StartsWith("OK @s1-11aa22bb final ", StringComparison.Ordinal) || l.StartsWith("ERR @s1-11aa22bb final ", StringComparison.Ordinal)))!.Value.Reading;
        Assert.Equal((LinkOutcome.Applied, true), (no.Outcome, no.Final));
        Assert.StartsWith("01.010 Wall to main — Failed late: ", no.Words, StringComparison.Ordinal);
        Assert.Equal(CueOutcome.FailedLate, desk.Services.CueStack.History[0].Outcome);

        // The box says nothing in its time: the same — it ran, the words say the box did not answer.
        desk.Services.CueStack.StandbyFirst();
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(GoGate.Lockout + TimeSpan.FromMilliseconds(100));                             // past the double-press lockout of the first GO
        Send(desk, conn, "@s2-11aa22bb CUE GO");
        Line(desk, conn, l => l.StartsWith("OK @s2-11aa22bb {", StringComparison.Ordinal));
        var silent = PeerLink.ReadLine(Line(desk, conn, l => l.StartsWith("OK @s2-11aa22bb final ", StringComparison.Ordinal) || l.StartsWith("ERR @s2-11aa22bb final ", StringComparison.Ordinal)))!.Value.Reading;
        Assert.Equal((LinkOutcome.Applied, true), (silent.Outcome, silent.Final));
        Assert.StartsWith("01.010 Wall to main — Failed late: ", silent.Words, StringComparison.Ordinal);
        Assert.Contains("no answer", silent.Words, StringComparison.Ordinal);
        Assert.Equal(0, desk.Control.FinalsOwed);
    }

    [AvaloniaFact]
    public void TimeAWorkerOrAModelTakesIsNoWaitOfTheDesksThreadAndACommandTheThreadNeverTookIsNotCounted()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var (conn, _) = Dialled(desk, DialDesk(desk, tls: false, files.Write("code", desk.Token), "code"));
        ReadOn(desk, conn, l => l.StartsWith("STATE {", StringComparison.Ordinal));

        // Each read of the clock on the desk's thread jumps it 5 s on: whatever is measured after the thread took a command — the
        // worker's read of the record, the answer — is 5 s later. Only the take counts, so neither command is late.
        long now = 1_000_000;
        desk.Control.ReceiptsNowMs = () =>
        {
            var at = Interlocked.Read(ref now);
            if (Dispatcher.UIThread.CheckAccess()) Interlocked.Add(ref now, 5000);
            return at;
        };
        var before = desk.Control.DeskStall.Total;
        Send(desk, conn, "@e1-11aa22bb EYE AT yesterday");
        var eye = Line(desk, conn, l => l.StartsWith("ERR @e1-11aa22bb", StringComparison.Ordinal) || l.StartsWith("OK @e1-11aa22bb", StringComparison.Ordinal));
        Assert.StartsWith("ERR @e1-11aa22bb 'yesterday' is not a time", eye, StringComparison.Ordinal);
        Assert.DoesNotContain("lateMs", eye, StringComparison.Ordinal);
        Assert.Equal((before + 1, 0L), (desk.Control.DeskStall.Total, desk.Control.DeskStall.Late));

        // An ASSISTANT ASK refused before the desk's thread is asked anything: no take, so no measurement of that thread at all.
        desk.Booted.Vm.State.Control.AssistantOnWire = false;
        desk.Control.LineRead = rid => { if (rid == "a1-11aa22bb") Interlocked.Add(ref now, 5000); };
        Send(desk, conn, "@a1-11aa22bb ASSISTANT ASK what is on air");
        var refused = Line(desk, conn, l => l.StartsWith("ERR @a1-11aa22bb", StringComparison.Ordinal));
        Assert.DoesNotContain("lateMs", refused, StringComparison.Ordinal);
        Assert.Equal((before + 1, 0L), (desk.Control.DeskStall.Total, desk.Control.DeskStall.Late));
        Assert.Empty(Rows(desk.Services, "LateCommand"));
    }
}
