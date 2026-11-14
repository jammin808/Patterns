using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.Devices.Tests;

/// <summary>
/// A device link made of connections, as a test drives it: a connection moves (on the link's thread, before it can be written on),
/// then says it came up, then the node's lines — or a test stops between the two, as the desk's thread may lag the link's.
/// </summary>
public sealed class FakeConnectionLink : IDeviceLink, IConnectionLink
{
    private readonly object _gate = new();
    private readonly List<string> _written = new();
    public IReadOnlyList<string> Written { get { lock (_gate) return _written.ToList(); } }
    public bool Disposed { get; private set; }
    public string Status => Disposed ? "closed" : "open (fake)";
    public bool IsOpen => !Disposed;
    public event Action<string>? LineReceived;
    public event Action? Moving;
    public event Action? Opened;
    public void Write(string framedLine)
    {
        lock (_gate) _written.Add(framedLine);
    }
    public void Connect()
    {
        Move();
        Open();
    }
    public void Move() => Moving?.Invoke();
    public void Open() => Opened?.Invoke();
    public void Say(string line) => LineReceived?.Invoke(line);
    public void Dispose() => Disposed = true;
}

/// <summary>The desk as a node device sees it: a show, a router, this desk's name and this process's instance.</summary>
public sealed class NodeHost : IDeviceHost
{
    public ShowState State { get; } = new();
    public FakeRouter Wire { get; } = new();
    public IRouter Router => Wire;
    public string ExecutionInHand => "";
    public string NodeDeskName => "FOH";
    public string NodeInst => "0badc0de";
    public event Action? SnapshotPublished;
    public event Action? RuntimeChanged;
    public void Publish() => SnapshotPublished?.Invoke();
    public void MoveRuntime() => RuntimeChanged?.Invoke();
}

/// <summary>
/// Round 97: a Show Bus node device on the device service, with no desk: the connection opens with HELLO, the node's NONCE is
/// proved, nothing but HELLO and the proof goes before <c>OK paired</c>, the show's facts follow untagged once it is, a command's
/// receipt is its own, and a link that drops leaves what it had in flight unconfirmed (Show Bus 13.12.3).
/// </summary>
public class NodeDeviceTests
{
    private const string Nonce = "00112233445566778899aabbccddeeff";

    [Fact]
    public void ANodeDevicePairsByProofBeforeAnyFactOrCommandGoesAndEachCommandHasItsOwnReceipt()
    {
        var host = new NodeHost();
        host.Wire.State = "{\"blackout\":true,\"live\":false}";
        var link = new FakeConnectionLink();
        var device = DeviceProfiles.Preset(DeviceProfile.ShowBusNode, 1);
        device.Port = "10.0.0.9";
        device.Secret = "K7QM-3XWD-P9RA";
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var devices = new DeviceService(host) { LinkFactory = _ => link };
        var receipts = new List<DeviceReceipt>();
        devices.Receipt += receipts.Add;
        try
        {
            devices.Reconcile();
            link.Connect();
            link.Say("NONCE " + Nonce);
            Assert.Equal(new[] { "@dh HELLO Patterns FOH\n", "@dp AUTH-PROOF " + PeerLink.LegacyProof(device.Secret, Nonce, "Patterns FOH") + "\n" }, link.Written);

            devices.Resend(device);                                                                // facts asked for before the proof: none go
            Assert.False(devices.Send(device.Name, "PLAYLIST GO show").Ok);
            Assert.Equal(2, link.Written.Count);

            link.Say("OK @dh");
            link.Say("OK @dp paired");
            Assert.Equal("@do SUBSCRIBE OFF\n", link.Written[2]);
            Assert.Equal("@dc SUBSCRIBE CONTEXT\n", link.Written[3]);
            devices.Resend(device);
            Assert.Contains("BLACKOUT 1\n", link.Written);
            Assert.Contains("LIVE 0\n", link.Written);
            Assert.DoesNotContain(link.Written, w => w.StartsWith("OPEN", StringComparison.Ordinal));

            var sent = devices.Send(device.Name, "PLAYLIST GO show #3");
            Assert.Equal(ActionStatus.Requested, sent.Status);
            Assert.Equal("@d1-0badc0de PLAYLIST GO show #3\n", link.Written[^1]);
            link.Say("OK @d1-0badc0de playing show #3");
            var receipt = Assert.Single(receipts);
            Assert.True(receipt.Ok);
            Assert.Equal("PlayMap: PLAYLIST GO show #3 — accepted (applied — playing show #3)", receipt.Line);

            var sight = Assert.Single(devices.NodeSights);
            Assert.True(sight.Up && sight.Paired);
            Assert.Equal(("legacy", "live", "amber"), (sight.Record.Proof, sight.Record.State, sight.Record.Light));
            Assert.Equal("applied — playing show #3", sight.LastOutcome);

            Assert.Equal(LinkOutcome.Applied, receipt.Outcome);
            Assert.Equal("Done", receipt.JournalOutcome);

            Assert.True(devices.Send(device.Name, "PLAYLIST GO show #4").Ok);
            link.Connect();                                                                         // the connection dropped and came back before the answer
            Assert.Equal(2, receipts.Count);
            Assert.False(receipts[1].Ok);
            Assert.Equal("unconfirmed — the link dropped before PlayMap answered", receipts[1].Answer);
            Assert.Equal((LinkOutcome.Unconfirmed, "Unconfirmed"), (receipts[1].Outcome, receipts[1].JournalOutcome));
            Assert.True(device.Runtime.Unsure);                                                     // it may have run: amber, not a failure
            Assert.False(device.Runtime.Failing);
            Assert.Null(devices.LastFailed);
            Assert.Equal("@dh HELLO Patterns FOH\n", link.Written[^1]);
            Assert.False(devices.NodeSights.Single().Up);
            Assert.DoesNotContain(link.Written, w => w.Contains("K7QM", StringComparison.Ordinal));
        }
        finally
        {
            devices.Dispose();
        }
    }

    /// <summary>A node device paired on its first connection, its receipts kept.</summary>
    private static (NodeHost Host, FakeConnectionLink Link, DeviceConfig Device, DeviceService Devices, List<DeviceReceipt> Receipts) Paired(int timeoutMs = 30000)
    {
        var host = new NodeHost();
        host.Wire.State = "{\"blackout\":true,\"live\":false}";
        var link = new FakeConnectionLink();
        var device = DeviceProfiles.Preset(DeviceProfile.ShowBusNode, 1);
        device.Port = "10.0.0.9";
        device.Secret = "K7QM-3XWD-P9RA";
        device.ConfirmTimeoutMs = timeoutMs;
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var devices = new DeviceService(host) { LinkFactory = _ => link };
        var receipts = new List<DeviceReceipt>();
        devices.Receipt += r =>
        {
            lock (receipts) receipts.Add(r);
        };
        devices.Reconcile();
        link.Connect();
        link.Say("NONCE " + Nonce);
        link.Say("OK @dh");
        link.Say("OK @dp paired");
        Assert.True(devices.NodeSights.Single().Up);
        return (host, link, device, devices, receipts);
    }

    [Fact]
    public void NothingGoesOnANewConnectionBeforeItsHelloEvenWhileTheDesksThreadHasNotHeardItCameUp()
    {
        var (_, link, device, devices, receipts) = Paired();
        try
        {
            devices.Resend(device);
            var before = link.Written.Count;

            // The link's thread moved to a new connection; the desk's thread has not run its start yet.
            link.Move();
            var refused = devices.Send(device.Name, "PLAYLIST GO show");
            Assert.False(refused.Ok);
            Assert.Contains("not-sent — the link is not live", refused.Message);
            devices.Resend(device);                                                                 // a fact asked for in the gap: none goes
            link.Say("OK @dp paired");                                                              // the gone connection's late answer: not read
            Assert.Equal(before, link.Written.Count);

            link.Open();
            Assert.Equal("@dh HELLO Patterns FOH\n", link.Written[before]);
            Assert.Equal(before + 1, link.Written.Count);
            Assert.Empty(receipts);
        }
        finally
        {
            devices.Dispose();
        }
    }

    [Fact]
    public void ANodesAcceptedAnswerIsAcceptedNotDoneAndItsSilenceIsUnconfirmedNotFailed()
    {
        var (_, link, device, devices, receipts) = Paired(timeoutMs: 200);
        try
        {
            Assert.True(devices.Send(device.Name, "PLAYLIST LOAD show").Ok);
            link.Say("OK @d1-0badc0de {\"stage\":\"accepted\"}");
            DeviceReceipt accepted;
            lock (receipts) accepted = Assert.Single(receipts);
            Assert.True(accepted.Ok);
            Assert.Equal((LinkOutcome.Accepted, "Accepted"), (accepted.Outcome, accepted.JournalOutcome));
            Assert.Contains("accepted, not run yet", accepted.Line);

            Assert.True(devices.Send(device.Name, "PLAYLIST GO show").Ok);                         // and no answer comes
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (receipts) if (receipts.Count == 2) break;
                Thread.Sleep(20);
            }
            DeviceReceipt silent;
            lock (receipts) silent = receipts[^1];
            Assert.False(silent.Ok);
            Assert.Equal((LinkOutcome.Unconfirmed, "Unconfirmed"), (silent.Outcome, silent.JournalOutcome));
            Assert.StartsWith("unconfirmed — no answer in 0.2 s — it may have run", silent.Answer, StringComparison.Ordinal);
            Assert.True(device.Runtime.Unsure);
            Assert.False(device.Runtime.Failing);
            Assert.Null(devices.LastFailed);
            Assert.Contains("UNCONFIRMED PLAYLIST GO show", device.Runtime.Words(DateTime.UtcNow));

            Assert.True(devices.Send(device.Name, "PLAYLIST GO nothing").Ok);
            link.Say("ERR @d3-0badc0de no such playlist");
            DeviceReceipt refused;
            lock (receipts) refused = receipts[^1];
            Assert.Equal((false, LinkOutcome.Refused, "Failed"), (refused.Ok, refused.Outcome, refused.JournalOutcome));
            Assert.Equal(refused, devices.LastFailed);                                             // a no is the health line's
            Assert.DoesNotContain(link.Written, w => w.Contains("K7QM", StringComparison.Ordinal));
        }
        finally
        {
            devices.Dispose();
        }
    }

    [Fact]
    public void AnyOtherBoxsReceiptIsJournalledByItsYesOrNo()
    {
        Assert.Equal("Done", new DeviceReceipt("Box", "RELAY 1", ConfirmLevel.Accepted, ConfirmLevel.Accepted, true, "OK", DateTime.UtcNow).JournalOutcome);
        Assert.Equal("Failed", new DeviceReceipt("Box", "RELAY 1", ConfirmLevel.Accepted, ConfirmLevel.Delivered, false, "no answer in 2 s", DateTime.UtcNow).JournalOutcome);
    }
}
