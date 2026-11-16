using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.Devices.Tests;

/// <summary>
/// A device link made of connections, as a test drives it: a connection moves (on the link's thread, before it can be written on),
/// then says it came up, then the node's lines — or a test stops between the two, as the desk's thread may lag the link's. Like
/// every link it raises nothing before <see cref="Start"/>: a raise before it is recorded in <see cref="Faults"/> and goes to nobody,
/// as a real link's would have gone before its caller's handlers were on. What it does as it starts (<see cref="OnStart"/>) is the
/// test's: a loopback connect may come up inside Start.
/// </summary>
public sealed class FakeConnectionLink : IDeviceLink, IConnectionLink
{
    private readonly object _gate = new();
    private readonly List<string> _written = new();
    private readonly List<string> _faults = new();
    public IReadOnlyList<string> Written { get { lock (_gate) return _written.ToList(); } }
    public IReadOnlyList<string> Faults { get { lock (_gate) return _faults.ToList(); } }
    public bool Disposed { get; private set; }
    public int Starts { get; private set; }
    /// <summary>Whether each handler (lines, Moving, Opened) was on when the link was first started.</summary>
    public (bool Lines, bool Moving, bool Opened) HandlersAtStart { get; private set; }
    public Action<FakeConnectionLink>? OnStart { get; init; }
    public string Status => Disposed ? "closed" : Starts == 0 ? "connecting…" : "open (fake)";
    public bool IsOpen => !Disposed && Starts > 0;
    public event Action<string>? LineReceived;
    public event Action? Moving;
    public event Action? Opened;
    public void Start()
    {
        if (Disposed || ++Starts > 1) return;
        HandlersAtStart = (LineReceived is not null, Moving is not null, Opened is not null);
        OnStart?.Invoke(this);
    }
    public void Write(string framedLine)
    {
        lock (_gate) _written.Add(framedLine);
    }
    /// <summary>The connections published so far: one more at each <see cref="Connect"/> and <see cref="Publish"/>, before its start is said.</summary>
    public long Connection { get; private set; }
    public void Connect()
    {
        Publish();
        Open();
    }
    /// <summary>A new connection published, as the link's thread does before it says its start: moved, then counted.</summary>
    public void Publish()
    {
        Move();
        if (Starts > 0) Connection++;
    }
    public void Move()
    {
        if (!Unstarted("Moving")) Moving?.Invoke();
    }
    public void Open()
    {
        if (!Unstarted("Opened")) Opened?.Invoke();
    }
    public void Say(string line)
    {
        if (!Unstarted("a line")) LineReceived?.Invoke(line);
    }
    public void Dispose() => Disposed = true;

    private bool Unstarted(string what)
    {
        if (Starts > 0) return false;
        lock (_gate) _faults.Add(what + " raised before Start");
        return true;
    }
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
            Assert.Equal(1, link.Starts);
            Assert.Empty(link.Faults);
        }
        finally
        {
            devices.Dispose();
        }
    }

    /// <summary>A node device whose fake connects as it starts, the way a loopback connect may come up inside Start.</summary>
    private static (FakeConnectionLink Link, DeviceConfig Device, DeviceService Devices) ConnectingInsideStart(Func<FakeConnectionLink> make)
    {
        var host = new NodeHost();
        var device = DeviceProfiles.Preset(DeviceProfile.ShowBusNode, 1);
        device.Port = "10.0.0.9";
        device.Secret = "K7QM-3XWD-P9RA";
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        FakeConnectionLink? made = null;
        var devices = new DeviceService(host) { LinkFactory = _ => made = make() };
        devices.Reconcile();
        return (made ?? throw new InvalidOperationException("the service made no link"), device, devices);
    }

    [Fact]
    public void ALinkThatConnectsInsideItsStartIsHeardItsHelloGoesAndTheNodesNonceIsProved()
    {
        var (link, device, devices) = ConnectingInsideStart(() => new FakeConnectionLink { OnStart = l => l.Connect() });
        try
        {
            Assert.Equal(new[] { "@dh HELLO Patterns FOH\n" }, link.Written);                      // the connection's start was heard
            Assert.Equal(1, link.Starts);
            Assert.Equal((true, true, true), link.HandlersAtStart);                                 // every handler on before the link ran
            Assert.True(devices.NodeSights.Single().Connected);

            link.Say("NONCE " + Nonce);
            Assert.Equal(new[] { "@dh HELLO Patterns FOH\n", "@dp AUTH-PROOF " + PeerLink.LegacyProof(device.Secret, Nonce, "Patterns FOH") + "\n" }, link.Written);
            Assert.Empty(link.Faults);

            devices.Reconcile();                                                                    // the poll's pass: nothing starts twice
            Assert.Equal(1, link.Starts);
            Assert.Equal(2, link.Written.Count);
        }
        finally
        {
            devices.Dispose();
        }
    }

    [Fact]
    public void AConnectionThatComesUpInsideTheFactoryIsSaidToNobodyWhichIsWhyNoLinkStartsBeforeStart()
    {
        // The order a link kept before it had a Start: the connection up while the factory is still making it, before the service
        // has a handler on it. Its start goes to nobody, so the node's HELLO is never written and nothing ever drives the node. The
        // two faults, the empty Written and the sight not connected show the fake's own rule (it says nothing before Start); the
        // service's half is the last two lines, Start once and after every handler. That a real link's constructor can no longer raise
        // early is LinkStartTests' to hold.
        var (link, _, devices) = ConnectingInsideStart(() =>
        {
            var early = new FakeConnectionLink();
            early.Connect();
            return early;
        });
        try
        {
            Assert.Equal(new[] { "Moving raised before Start", "Opened raised before Start" }, link.Faults);
            Assert.Empty(link.Written);
            Assert.False(devices.NodeSights.Single().Connected);
            Assert.Equal(1, link.Starts);                                                           // started once, after its handlers
            Assert.Equal((true, true, true), link.HandlersAtStart);
        }
        finally
        {
            devices.Dispose();
        }
    }

    [Fact]
    public void ALinkWhoseStartThrowsIsPutAwayAndTheNextPassMakesAFreshOne()
    {
        // A link made and then refused at its Start is out of the table and disposed — whatever its constructor took (a UDP socket)
        // goes with it — and the device says it could not open; the next pass makes a new link rather than leaving one per second.
        var made = new List<FakeConnectionLink>();
        var (first, device, devices) = ConnectingInsideStart(() =>
        {
            var link = new FakeConnectionLink { OnStart = _ => throw new InvalidOperationException("the port is taken") };
            made.Add(link);
            return link;
        });
        try
        {
            Assert.True(first.Disposed);
            Assert.Null(devices.LinkFor(device.Id));
            Assert.Equal("could not open: the port is taken", device.Status);

            devices.Reconcile();
            Assert.Equal(2, made.Count);
            Assert.True(made[1].Disposed);
            Assert.Null(devices.LinkFor(device.Id));
        }
        finally
        {
            devices.Dispose();
        }
    }

    [Fact]
    public void ALinkThatFaultsBeforeItReachesTheTableIsStillDisposed()
    {
        // The fault comes while the service is still putting its handlers on, before the device is in its table: the link is
        // disposed all the same, and never started.
        var host = new NodeHost();
        var device = new DeviceConfig { Name = "Board", Link = DeviceLink.Serial, Port = "COM9", Profile = DeviceProfile.Lines, SpeaksProtocol = false, HearsShow = false, EchoReplies = false };
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var link = new RefusesHandlers();
        var devices = new DeviceService(host) { LinkFactory = _ => link };
        try
        {
            devices.Reconcile();
            Assert.True(link.Disposed);
            Assert.Equal(0, link.Starts);
            Assert.Null(devices.LinkFor(device.Id));
            Assert.Equal("could not open: no handler taken", device.Status);
        }
        finally
        {
            devices.Dispose();
        }
    }

    /// <summary>A link that will not take a handler, so the service faults before the device is in its table.</summary>
    private sealed class RefusesHandlers : IDeviceLink
    {
        public int Starts { get; private set; }
        public bool Disposed { get; private set; }
        public string Status => "open (fake)";
        public bool IsOpen => !Disposed;
        public event Action<string>? LineReceived
        {
            add => throw new InvalidOperationException("no handler taken");
            remove => throw new InvalidOperationException("no handler taken");
        }
        public void Start() => Starts++;
        public void Write(string framedLine)
        {
            // nothing is ever written: the link never opens
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void ALinkOfLinesThatSpeaksInsideItsStartIsHeard()
    {
        // Serial, UDP and MIDI raise lines and no connection: a board that speaks the moment its port opens is heard, because the
        // service starts the link only once its handler is on.
        var host = new NodeHost();
        var device = new DeviceConfig { Name = "Board", Link = DeviceLink.Serial, Port = "COM9", Profile = DeviceProfile.Lines, SpeaksProtocol = false, HearsShow = false, EchoReplies = false };
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var link = new LinesOnStart("READY");
        var devices = new DeviceService(host) { LinkFactory = _ => link };
        try
        {
            devices.Reconcile();
            Assert.Equal(1, link.Starts);
            Assert.Equal((1L, "READY"), (devices.Traffic(device.Id)!.Value.In, devices.Traffic(device.Id)!.Value.LastIn));
            Assert.Equal("READY", device.Runtime.LastReply);
        }
        finally
        {
            devices.Dispose();
        }
    }

    /// <summary>A link of lines that says its first as it starts, the way a board speaks the moment its port opens.</summary>
    private sealed class LinesOnStart(string first) : IDeviceLink
    {
        public int Starts { get; private set; }
        public string Status => "open (fake)";
        public bool IsOpen => true;
        public event Action<string>? LineReceived;
        public void Start()
        {
            if (++Starts == 1) LineReceived?.Invoke(first);
        }
        public void Write(string framedLine)
        {
            // what the service writes is not this test's
        }
        public void Dispose()
        {
            // nothing held
        }
    }

    [Theory]
    [InlineData(DeviceLink.Tcp, "10.0.0.20")]
    [InlineData(DeviceLink.Serial, "COM3")]
    public void AProjectorsGreetingHeardBeforeThePollSeesTheLinkOpenStillSignsTheFirstCommand(DeviceLink kind, string port)
    {
        // PJLink: the projector greets each connection with its seed, and the first command carries the digest of it. The greeting
        // is read on the desk's thread in order with the connection's start; the poll's pass that first sees the link open comes
        // later, and must not start the session over after the seed was taken. A serial port's each opening is a connection too: the
        // fake carries the service's path for a serial-configured device, and the real serial link is held to being a link made of
        // connections here, so the service takes that path for it.
        if (kind == DeviceLink.Serial)
        {
            using var serial = new SerialDeviceLink(port, 115200);
            Assert.IsAssignableFrom<IConnectionLink>(serial);
        }
        var host = new NodeHost();
        var device = DeviceProfiles.Preset(DeviceProfile.PjLink, 1);
        device.Link = kind;
        device.Port = port;
        device.Secret = "JBMIAProjectorLink";
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var link = new FakeConnectionLink();
        var devices = new DeviceService(host) { LinkFactory = _ => link };
        try
        {
            devices.Reconcile();
            link.Connect();
            link.Say("PJLINK 1 498e4a67");
            devices.Reconcile();                                                                    // the late pass: open, and not open before
            Assert.True(devices.Send(device.Name, "POWER ON").Ok);
            Assert.Equal(PjLinkSession.Digest("498e4a67", device.Secret) + "%1POWR 1\r", link.Written[^1]);

            link.Connect();                                                                         // a new connection, its own seed
            link.Say("PJLINK 1 5d8409bc");
            Assert.True(devices.Send(device.Name, "POWER OFF").Ok);
            Assert.Equal(PjLinkSession.Digest("5d8409bc", device.Secret) + "%1POWR 0\r", link.Written[^1]);
            Assert.Empty(link.Faults);
        }
        finally
        {
            devices.Dispose();
        }
    }

    [Fact]
    public void ALookUpOfAConnectionThatEndedIsForgottenWhenTheNextConnectionStartsHoweverSoonItComes()
    {
        // Pixera: a timeline is found by name and played by the handle its reply carries. The session starts over at each connection's
        // own start, so a reply to the gone connection's look-up plays nothing, and a pass that comes later (a poll that runs late, or
        // the next poll after a quick redial) can no longer wipe what the new connection has already sent or heard. Here the drop and
        // the return both fall between two polls.
        var host = new NodeHost();
        var device = DeviceProfiles.Preset(DeviceProfile.Pixera, 1);
        device.Port = "10.0.0.30";
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var link = new FakeConnectionLink();
        var devices = new DeviceService(host) { LinkFactory = _ => link };
        var receipts = new List<DeviceReceipt>();
        devices.Receipt += receipts.Add;
        try
        {
            devices.Reconcile();
            link.Connect();
            Assert.True(devices.Send(device.Name, "TIMELINE Show PLAY").Ok);
            var sent = link.Written.Count;
            Assert.Contains("getTimelineFromName", link.Written[^1], StringComparison.Ordinal);

            link.Connect();                                                                         // dropped and back before the reply
            var receipt = Assert.Single(receipts);                                                  // the play can never go: not-sent at the new start
            Assert.Equal((false, LinkOutcome.NotSent, "Failed"), (receipt.Ok, receipt.Outcome, receipt.JournalOutcome));
            Assert.Equal(PixeraSession.ForgottenWords, receipt.Answer);
            link.Say("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":42}");
            Assert.Equal(sent, link.Written.Count);                                                 // no play for a look-up the box no longer holds
            Assert.Single(receipts);                                                                // and the late handle is nobody's yes
            Assert.Empty(link.Faults);
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
