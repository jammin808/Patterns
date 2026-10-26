using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 88 (Show Bus 1.2): the certificate and its fingerprint, pins and their verdicts, the first byte that tells TLS
/// from plain, the Stagewatch relay to a node and the choice of sight, a node's own context, and the approval queue's
/// shapes — every rule the App's sockets lean on, without a socket.
/// </summary>
public class ShowBus12Tests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ACertificateIsMadeOnceWithTheMachinesNamesAndItsFingerprintSurvivesKeeping()
    {
        using var cert = TlsIdentity.Create("FOH Desk", new[] { "FOH-PC", "localhost", "bad name!" }, new[] { IPAddress.Loopback, IPAddress.Parse("10.0.0.5") }, T0);
        var fp = TlsFingerprint.Of(cert);
        Assert.True(TlsFingerprint.IsValid(fp));
        Assert.Equal("CN=FOH Desk", cert.Subject);
        Assert.True(cert.HasPrivateKey);
        Assert.InRange((cert.NotAfter.ToUniversalTime() - T0).TotalDays, 5 * 365 - 1, 5 * 365 + 1);
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("FOH-PC", san.EnumerateDnsNames());
        Assert.DoesNotContain("bad name!", san.EnumerateDnsNames());
        Assert.Contains(IPAddress.Parse("10.0.0.5"), san.EnumerateIPAddresses());

        using var kept = TlsIdentity.Import(TlsIdentity.Export(cert));                  // kept and read back: the same certificate, with its key
        Assert.Equal(fp, TlsFingerprint.Of(kept));
        Assert.True(kept.HasPrivateKey);
        using var publicOnly = X509CertificateLoader.LoadCertificate(TlsIdentity.PublicDer(cert));
        Assert.False(publicOnly.HasPrivateKey);                                          // /tls.cer carries no key
        Assert.Equal(fp, TlsFingerprint.Of(publicOnly));

        Assert.False(TlsIdentity.NeedsRenewal(cert, T0));
        Assert.True(TlsIdentity.NeedsRenewal(cert, T0 + TlsIdentity.Life - TimeSpan.FromDays(10)));
    }

    [Fact]
    public void AFingerprintIsSha256InLowercaseHexAndAnythingElseIsNone()
    {
        var good = TlsFingerprint.Prefix + new string('a', 64);
        Assert.True(TlsFingerprint.IsValid(good));
        Assert.Equal(good, TlsFingerprint.Normal(" SHA256:" + new string('A', 64) + " "));
        Assert.Equal("", TlsFingerprint.Normal("sha1:abc"));
        Assert.Equal("", TlsFingerprint.Normal(TlsFingerprint.Prefix + new string('g', 64)));
        Assert.Equal("aaaa aaaa aaaa aaaa", TlsFingerprint.Short(good));
    }

    [Fact]
    public void APinIsTheTrustTheHelloIsOnlyAHint()
    {
        var a = TlsFingerprint.Prefix + new string('a', 64);
        var b = TlsFingerprint.Prefix + new string('b', 64);
        Assert.Equal(PinVerdict.FirstUse, CertificatePins.Check("", a, a));            // first time, as the hello said
        Assert.Equal(PinVerdict.FirstUse, CertificatePins.Check("", a, ""));           // a typed address: trust on first use
        Assert.Equal(PinVerdict.Mismatch, CertificatePins.Check("", a, b));            // the hello named another certificate
        Assert.Equal(PinVerdict.Pinned, CertificatePins.Check(a, a, b));               // the pin wins over a hello that changed
        Assert.Equal(PinVerdict.Changed, CertificatePins.Check(a, b, b));              // a new certificate: refused until accepted
        Assert.Equal(PinVerdict.Mismatch, CertificatePins.Check(a, "not one", a));
        Assert.True(CertificatePins.Accepts(PinVerdict.Pinned) && CertificatePins.Accepts(PinVerdict.FirstUse));
        Assert.False(CertificatePins.Accepts(PinVerdict.Changed) || CertificatePins.Accepts(PinVerdict.Mismatch));
        Assert.StartsWith("CERTIFICATE CHANGED", CertificatePins.Words(PinVerdict.Changed, "Stagewatch Hall A", b));
    }

    [Fact]
    public void TheFirstByteTellsTlsFromPlainAndRequireHttpsKeepsThisMachinePlain()
    {
        Assert.True(TlsSniff.IsTls(0x16));
        Assert.False(TlsSniff.IsTls((byte)'G'));
        Assert.True(TlsSniff.RefusePlain(requireHttps: true, isTls: false, loopback: false));
        Assert.False(TlsSniff.RefusePlain(requireHttps: true, isTls: false, loopback: true));
        Assert.False(TlsSniff.RefusePlain(requireHttps: true, isTls: true, loopback: false));
        Assert.False(TlsSniff.RefusePlain(requireHttps: false, isTls: false, loopback: false));
    }

    [Fact]
    public void AHelloCarriesItsCertificateAndAPeerReadsIt()
    {
        var fp = TlsFingerprint.Prefix + new string('c', 64);
        var json = JsonSerializer.Serialize(new { t = "main", v = 1, id = "a1b2c3d4e5f6", name = "Hall A", ports = new { http = 9924, ws = 9924, https = 9924 }, caps = new[] { "timer", "approvals" }, tls = fp.ToUpperInvariant() });
        var peer = ShowBusPeers.Parse(System.Text.Encoding.UTF8.GetBytes(json), "10.0.0.9", T0, out _)!;
        Assert.Equal(fp, peer.Tls);
        Assert.True(peer.OffersTls);
        Assert.Equal(9924, peer.Ports.Https);
        Assert.Contains("approvals", peer.Caps);
        var noTls = ShowBusPeers.Parse(System.Text.Encoding.UTF8.GetBytes("{\"t\":\"main\",\"id\":\"x1\",\"tls\":\"sha256:nothex\"}"), "10.0.0.9", T0, out _)!;
        Assert.False(noTls.OffersTls);
        var hello = new ShowBusHello { Id = "abcd1234" };
        Assert.DoesNotContain("\"tls\"", hello.ToJson());                              // no certificate, no word
        Assert.DoesNotContain("\"https\"", hello.ToJson());
        Assert.Contains($"\"tls\":\"{fp}\"", (hello with { Tls = fp }).ToJson());
    }

    [Fact]
    public void TheDesksStagewatchReachesItsNodesAsAFrameAndAnAgeNeverAKey()
    {
        var frame = StagewatchFrame.Parse("{\"type\":\"state\",\"seq\":7,\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":600,\"total\":900,\"text\":\"10:00\",\"title\":\"Keynote\"}")!;
        var sight = new StagewatchSight("Hall A", "10.0.0.9:9924", ShowBusState.Live, frame, 100, null, "");
        var relay = StagewatchRelay.Of(sight, nowSeconds: 101.5, seq: 3);
        var json = relay.ToJson();
        Assert.DoesNotContain("10.0.0.9", json);                                         // where to dial stays on the desk
        Assert.DoesNotContain("swk_", json);
        var back = StagewatchRelay.Parse(json)!;
        Assert.Equal(1.5, back.FrameAgeSeconds, 3);

        var onNode = back.ToSight(receivedAtSeconds: 500, nowSeconds: 501);
        Assert.Equal(ShowBusState.Live, onNode.State);
        Assert.Equal(498.5, onNode.FrameAtSeconds, 3);                                   // placed where it would have arrived
        var time = StageFollow.Time(onNode.Frame!, onNode.FrameAtSeconds, 501);
        Assert.InRange(time.RemainingSeconds, 597.4, 597.6);                              // the node counts on as the desk does

        Assert.Equal(ShowBusState.Stale, back.ToSight(500, 507).State);                  // the relay stopped: quiet here too
        Assert.Equal(ShowBusState.Gone, back.ToSight(500, 513).State);
        Assert.Equal(ShowBusState.Stale, (back with { State = "stale" }).ToSight(500, 500).State);
        Assert.Same(StagewatchSight.None, StagewatchRelay.Of(StagewatchSight.None, 1, 1).ToSight(1, 1));
        Assert.Null(StagewatchRelay.Parse("not json"));
    }

    [Fact]
    public void AStuckShowReachesTheNodesAsTheDeskHoldsItAndTheirStageShowsNoTime()
    {
        var frame = StagewatchFrame.Parse("{\"type\":\"state\",\"seq\":7,\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":600,\"total\":900,\"text\":\"10:00\",\"title\":\"Keynote\"}")!;
        // As the desk's link folds a stuck beat in: the held frame not live, the mark beside it.
        var stuck = new StagewatchSight("Hall A", "10.0.0.9:9924", ShowBusState.Live, frame with { Live = false }, 100, null, "") { Stuck = true };
        var json = StagewatchRelay.Of(stuck, 101.5, 4).ToJson();
        Assert.Contains("\"seq\":4,\"stuck\":true", json, StringComparison.Ordinal);           // the new field after the relay's last
        var onNode = StagewatchRelay.Parse(json)!.ToSight(500, 501);
        var held = onNode.Frame!;
        Assert.True(onNode.Stuck);
        Assert.False(held.Live);
        Assert.Equal(7, held.Seq);                                                            // no seq moved: the mark is what changed
        Assert.True(onNode.Following);
        Assert.Equal(StageTimerPhase.Idle, StageFollow.Time(held, onNode.FrameAtSeconds, 501).Phase);

        // Stuck before any frame: the mark alone reaches the node, which keeps its own timer.
        var bare = StagewatchRelay.Parse(StagewatchRelay.Of(stuck with { Frame = null }, 101.5, 5).ToJson())!.ToSight(500, 501);
        Assert.True(bare.Stuck);
        Assert.False(bare.Following);
        // A frame that says live:false is the same stall, mark or not.
        Assert.True(StagewatchRelay.Parse(StagewatchRelay.Of(stuck with { Stuck = false }, 101.5, 6).ToJson())!.ToSight(500, 501).Stuck);
        // The next frame clears both.
        var running = StagewatchRelay.Parse(StagewatchRelay.Of(stuck with { Frame = frame, Stuck = false }, 101.5, 7).ToJson())!.ToSight(500, 501);
        Assert.False(running.Stuck);
        Assert.True(running.Frame!.Live);

        // The node's own context: its Stagewatch amber (the link live), its stage red with no time.
        var facts = new NodeContextFacts { Role = "timer", DeskName = "FOH", DeskLight = CheckLight.Green, Linked = true, Stagewatch = onNode, StagewatchViaDesk = true, StageWords = "idle (Stagewatch Hall A's time)", StageScreens = 2 };
        var c = NodeContextBuilder.Build("1a2b3c4d", "Stage Left", T0, facts, Array.Empty<ShowLogEntry>());
        Assert.Contains(c.Items, i => i.Id == "stagewatch" && i.Light == "amber" && i.State.Contains("says its show is stuck", StringComparison.Ordinal));
        Assert.Contains(c.Items, i => i.Id == "stage" && i.Light == "red" && i.State == "no time shown — Stagewatch Hall A says its show is stuck · 2 screens watching");
        Assert.Equal("red", c.Light);
        Assert.Contains("Stage display: no time shown", c.Headline, StringComparison.Ordinal);
        var waiting = NodeContextBuilder.Build("1a2b3c4d", "Stage Left", T0, facts with { Stagewatch = bare, StageWords = "05:00 — running" }, Array.Empty<ShowLogEntry>());
        Assert.Contains(waiting.Items, i => i.Id == "stagewatch" && i.Light == "amber" && i.State.Contains("own stage timer runs until its first frame", StringComparison.Ordinal));
        Assert.Contains(waiting.Items, i => i.Id == "stage" && i.Light == "green" && i.State.StartsWith("05:00", StringComparison.Ordinal));
    }

    [Fact]
    public void ANodesOwnLinkWinsWhileHeardElseTheDesksRelay()
    {
        var own = new StagewatchSight("Mine", "10.0.0.2:9924", ShowBusState.Live, null, 1, null, "");
        var relayed = new StagewatchSight("Desk's", "via the desk", ShowBusState.Live, null, 1, null, "");
        Assert.Same(own, StageSightChoice.Choose(true, own, relayed));
        Assert.Same(relayed, StageSightChoice.Choose(true, own with { State = ShowBusState.Gone }, relayed));
        Assert.Same(relayed, StageSightChoice.Choose(false, own, relayed));
        Assert.Same(StagewatchSight.None, StageSightChoice.Choose(false, own, StagewatchSight.None));
    }

    [Fact]
    public void ANodeSaysWhatItIsInTheSharedShape()
    {
        var frame = StagewatchFrame.Parse("{\"type\":\"state\",\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":120,\"text\":\"02:00\"}")!;
        var facts = new NodeContextFacts
        {
            Role = "timer",
            DeskName = "FOH",
            DeskWords = "in step with FOH",
            DeskLight = CheckLight.Green,
            Linked = true,
            Stagewatch = new StagewatchSight("Hall A", "via the desk", ShowBusState.Stale, frame, 1, null, ""),
            StagewatchViaDesk = true,
            StageWords = "02:00 — running (Stagewatch Hall A's time)",
        };
        var journal = new[] { new ShowLogEntry(T0, "desk", "StageMessage", "", "Done", "wrap up") };
        var c = NodeContextBuilder.Build("1a2b3c4d", "Stage Left", T0, facts, journal);
        var round = ShowContext.Parse(c.ToJson())!;                                      // what another app reads back
        Assert.Equal("patterns", round.Node.Kind);
        Assert.Equal("1a2b3c4d", round.Node.Id);
        Assert.Equal("amber", round.Light);                                               // a quiet Stagewatch is the worst thing it knows
        Assert.Contains(round.Items, i => i.Id == "desk" && i.Light == "green");
        Assert.Contains(round.Items, i => i.Id == "stagewatch" && i.Light == "amber" && i.State.Contains("relayed by the desk", StringComparison.Ordinal));
        Assert.Contains(round.Items, i => i.Id == "stage" && i.State.StartsWith("02:00", StringComparison.Ordinal));
        Assert.Contains("stage timer node", round.Headline, StringComparison.Ordinal);
        Assert.Contains(round.Commands, x => x.Verb == "STAGE STATUS" && x.Risk == "low");
        Assert.Single(round.Recent);

        var caller = NodeContextBuilder.Build("9f9f9f9f", "SM", T0, new NodeContextFacts { Role = "caller", StackWords = "standby 14 · armed", StackLight = CheckLight.Green }, Array.Empty<ShowLogEntry>());
        Assert.Contains(caller.Items, i => i.Id == "stack" && i.State == "standby 14 · armed");
        Assert.Contains(caller.Items, i => i.Id == "desk" && i.State == "not linked to a desk");
        Assert.Contains(caller.Commands, x => x.Verb == "CUE GO" && x.Risk == "high");
    }

    [Fact]
    public void TheApprovalQueueShapesAreReadTolerantlyAndAnApplyLineNamesItsProposal()
    {
        using (var doc = JsonDocument.Parse(DeskProposals.Request("1a2b3c4d", "FOH", "1a2b3c4d", "APPLY a1: Look \"Blue\"", "the keynote look", "nonsense")))
        {
            var r = doc.RootElement;
            Assert.Equal("patterns", r.GetProperty("from").GetProperty("t").GetString());
            Assert.Equal("1a2b3c4d", r.GetProperty("target").GetProperty("id").GetString());
            Assert.Equal("high", r.GetProperty("risk").GetString());                     // unknown risk is high
        }
        var line = DeskProposals.Line("a3f9c2d1", "Look", "Keynote \"blue\"");
        Assert.Equal("APPLY a3f9c2d1: Look \"Keynote 'blue'\"", line);
        Assert.Equal("a3f9c2d1", DeskProposals.LocalId(line));
        Assert.Null(DeskProposals.LocalId("EYE FOCUS screen 2"));
        Assert.Null(DeskProposals.LocalId("APPLY ../x: y"));

        var list = DeskProposals.ParseList("{\"proposals\":[{\"id\":\"p17\",\"state\":\"Approved\",\"line\":\"EYE FOCUS screen 2\",\"target\":{\"t\":\"patterns\",\"id\":\"1a2b3c4d\"},\"from\":{\"id\":\"sw\"},\"decidedBy\":\"Sam\"},{\"nope\":1}]}");
        var p = Assert.Single(list);
        Assert.Equal(("p17", "approved", "1a2b3c4d", "Sam"), (p.Id, p.State, p.TargetId, p.DecidedBy));
        Assert.Single(DeskProposals.ParseList("[{\"id\":\"p1\",\"state\":\"pending\"}]"));
        Assert.Empty(DeskProposals.ParseList("not json"));
        Assert.Equal("p9", DeskProposals.ParseOne("{\"id\":\"p9\",\"state\":\"pending\"}")!.Id);
        Assert.True(new DeskProposal { State = "done" }.Final);
        using (var res = JsonDocument.Parse(DeskProposals.Result(false, "refused: the stack is armed")))
            Assert.False(res.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("waiting for approval on Stagewatch Hall A", DeskProposals.StateWords("pending", "Stagewatch Hall A"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnApplyRunsOnlyAsTheProposalThisDeskMadeUnderItsOwnId()
    {
        // Round 91.3: the desk posted APPLY a3f9c2d1 and the approval desk answered p17; only p17, from this desk, runs it, once.
        const string self = "1a2b3c4d";
        var line = DeskProposals.Line("a3f9c2d1", "Look", "Keynote blue");
        var own = new DeskProposal { Id = "p17", State = "approved", Line = line, FromId = self, TargetId = self };
        Assert.Null(DeskProposals.ApplyRefusal(own, "a3f9c2d1", self, held: true, postedAs: "p17", ran: false));

        // Another node re-proposed the same words to this desk: refused, whatever this desk holds.
        var copied = own with { Id = "p18", FromId = "pm-a1b2" };
        Assert.Equal(DeskProposals.OnlyOwnApply + " — another node proposed this one", DeskProposals.ApplyRefusal(copied, "a3f9c2d1", self, held: true, postedAs: "p17", ran: false));
        Assert.Equal(DeskProposals.OnlyOwnApply + " — another node proposed this one", DeskProposals.ApplyRefusal(copied with { Id = own.Id }, "a3f9c2d1", self, held: true, postedAs: "p17", ran: false));
        Assert.StartsWith(DeskProposals.OnlyOwnApply, DeskProposals.ApplyRefusal(copied with { FromId = "" }, "a3f9c2d1", self, held: true, postedAs: "p17", ran: false), StringComparison.Ordinal);

        // This desk's id on another proposal than the one it posted the named proposal as: a copy under another id, or one
        // named before the approval desk answered this desk's post at all.
        var other = own with { Id = "p19" };
        Assert.Equal(DeskProposals.OnlyOwnApply + " — this desk posted a3f9c2d1 under another id", DeskProposals.ApplyRefusal(other, "a3f9c2d1", self, held: true, postedAs: "p17", ran: false));
        Assert.Equal(DeskProposals.OnlyOwnApply + " — this desk posted a3f9c2d1 under another id", DeskProposals.ApplyRefusal(own, "a3f9c2d1", self, held: true, postedAs: "", ran: false));

        // Once it ran here, the same approval again is refused: a proposal runs once, whatever remembers the ids collected.
        Assert.Equal(DeskProposals.AlreadyApplied, DeskProposals.ApplyRefusal(own, "a3f9c2d1", self, held: true, postedAs: "p17", ran: true));

        // A proposal this desk no longer holds (restarted since): said as that, never applied.
        Assert.Equal(DeskProposals.NoLongerHeld, DeskProposals.ApplyRefusal(own, "a3f9c2d1", self, held: false, postedAs: "", ran: false));

        // A desk with no id of its own yet runs no APPLY at all.
        Assert.StartsWith(DeskProposals.OnlyOwnApply, DeskProposals.ApplyRefusal(own with { FromId = "" }, "a3f9c2d1", "", held: true, postedAs: "p17", ran: false), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwinsNewWordsParse()
    {
        Assert.Equal(TwinWord.Stagewatch, TwinMessage.Parse("STAGEWATCH {\"name\":\"Hall A\"}").Word);
        Assert.Equal(TwinWord.Context, TwinMessage.Parse("CONTEXT {\"v\":1}").Word);
        Assert.Equal("STAGEWATCH {}", TwinMessage.Format(TwinWord.Stagewatch, "{}"));
    }
}
