using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.Devices.Tests;

/// <summary>
/// The end of a connection, with the desk's thread queued as the app's is: a step of a line read after its connection ended is
/// never written and never counted, and the line ends not-sent whether or not the next connection is published yet; a line that
/// times out lets its session's steps go, so nothing of it plays behind its receipt; a link that is no longer the device's says
/// nothing to its successor; a box whose answers carry no key is never answered across connections. The fakes write nothing
/// while closed, and no leg rests on the clock but the timed-out one, whose bound runs from its own send.
/// </summary>
[Collection(QueuedDispatchCollection.Name)]
public sealed class ConnectionEndTests
{
    private const string Reply = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":4711}";
    private const string Nonce = "00112233445566778899aabbccddeeff";

    /// <summary>A device on the queued desk's thread, its links made by <paramref name="links"/> in turn, its receipts kept.</summary>
    private static void Queued(DeviceProfile profile, Action<DeviceConfig> configure, FakeConnectionLink[] links,
        Action<QueuedDispatch, DeviceService, DeviceConfig, List<DeviceReceipt>, NodeHost> body)
    {
        var dispatch = new QueuedDispatch();
        var before = Dispatch.Provider;
        Dispatch.Provider = dispatch;
        var host = new NodeHost();
        var device = DeviceProfiles.Preset(profile, 1);
        device.Port = "10.0.0.30";
        device.ConfirmTimeoutMs = 30000;   // a line ends by what the test does, or at the area's close
        configure(device);
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var made = 0;
        var devices = new DeviceService(host) { LinkFactory = _ => links[Math.Min(made++, links.Length - 1)] };
        var receipts = new List<DeviceReceipt>();
        devices.Receipt += r =>
        {
            lock (receipts) receipts.Add(r);
        };
        try
        {
            devices.Reconcile();
            body(dispatch, devices, device, receipts, host);
            foreach (var link in links) Assert.Empty(link.Faults);
        }
        finally
        {
            devices.Dispose();
            dispatch.RunJobs();
            Dispatch.Provider = before;
        }
    }

    private static string Said(FakeConnectionLink link, DeviceService devices, DeviceConfig device, List<DeviceReceipt> receipts) =>
        $"written [{string.Join(" | ", link.Written)}]; lost [{string.Join(" | ", link.Lost)}]; traffic {devices.Traffic(device.Id)}; status '{device.Status}'; receipts [{string.Join(" | ", receipts.Select(r => r.Line))}]";

    [Fact]
    public void AHandleReadAfterTheBoxHungUpAndBeforeAnyNextConnectionIsNeverWrittenAndItsLineEndsNotSent()
    {
        // The usual order on a real link: the handle is read, the box hangs up, and the desk's thread drains the handle while the
        // link waits to redial — no next connection is published. The play has no stream to go on: it is never written, never
        // counted, and the line's receipt says not-sent.
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.Pixera, _ => { }, new[] { link }, (dispatch, devices, device, receipts, _) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            link.Say(Reply);                                                                          // the handle, said on connection 1
            link.Drop();                                                                              // 1 is over; nothing published after it
            dispatch.RunJobs();

            Assert.True(link.Written.Count == 1 && link.Lost.Count == 0, Said(link, devices, device, receipts));
            Assert.Equal((1L, 1L, Reply + DeviceService.EndedNote), (devices.Traffic(device.Id)!.Value.In, devices.Traffic(device.Id)!.Value.Out, devices.Traffic(device.Id)!.Value.LastIn));
            var receipt = Assert.Single(receipts);
            Assert.Equal((false, LinkOutcome.NotSent, PixeraSession.ForgottenWords), (receipt.Ok, receipt.Outcome, receipt.Answer));
            Assert.Equal($"TIMELINE Main PLAY — {PixeraSession.ForgottenWords}", device.Runtime.LastFailure);
        });
    }

    [Fact]
    public void AStepWhoseWriteFindsTheLinkClosedIsNeverCountedAsSentAndItsLineEndsNotSent()
    {
        // The link closed under the write (a failed write; its read loop has not yet seen the end, so the end is not counted): the
        // write is judged by what became of its bytes, not by the counter.
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.Pixera, _ => { }, new[] { link }, (dispatch, devices, device, receipts, _) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            link.Say(Reply);
            link.Break();
            dispatch.RunJobs();

            Assert.True(link.Written.Count == 1, Said(link, devices, device, receipts));               // the look-up; the play reached nothing
            Assert.Equal((1L, Reply + DeviceService.EndedNote), (devices.Traffic(device.Id)!.Value.Out, devices.Traffic(device.Id)!.Value.LastIn));
            var receipt = Assert.Single(receipts);
            Assert.Equal((false, LinkOutcome.NotSent), (receipt.Ok, receipt.Outcome));
        });
    }

    [Fact]
    public void ALineWhoseOwnWriteFindsTheLinkClosedIsNeverCountedAsSentAndItsReceiptSaysNotDelivered()
    {
        // A tracked line (the DEVICE verb, a cue's step) written while the link is closed: its receipt says not delivered, and the
        // card's traffic says the same — no out, no last line out.
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.Pixera, _ => { }, new[] { link }, (dispatch, devices, device, receipts, _) =>
        {
            link.Connect();
            dispatch.RunJobs();
            link.Break();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            dispatch.RunJobs();

            Assert.True(link.Written.Count == 0, Said(link, devices, device, receipts));
            Assert.Equal((0L, ""), (devices.Traffic(device.Id)!.Value.Out, devices.Traffic(device.Id)!.Value.LastOut));
            var receipt = Assert.Single(receipts);
            Assert.False(receipt.Ok);
            Assert.StartsWith("not delivered", receipt.Answer, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ALineWhoseLookUpTimesOutEndsNotSentAndItsLateHandlePlaysNothing()
    {
        // A busy Pixera answers the look-up after the line's own timeout. The receipt said the line failed, the cue's row and the
        // health line with it: the handle that comes later must not play the timeline behind them.
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.Pixera, d => d.ConfirmTimeoutMs = 200, new[] { link }, (dispatch, devices, device, receipts, _) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            var sent = DateTime.UtcNow;                                                               // the bound runs from the line's own send
            while (DateTime.UtcNow - sent < TimeSpan.FromSeconds(10))
            {
                dispatch.RunJobs();
                lock (receipts) if (receipts.Count > 0) break;
                Thread.Sleep(10);
            }
            DeviceReceipt receipt;
            lock (receipts) receipt = Assert.Single(receipts);
            Assert.Equal((false, LinkOutcome.NotSent, "Failed"), (receipt.Ok, receipt.Outcome, receipt.JournalOutcome));
            Assert.Equal(PixeraSession.UnansweredLookUpWords(TimeSpan.FromMilliseconds(200)), receipt.Answer);
            Assert.Equal(receipt, devices.LastFailed);

            link.Say(Reply);                                                                          // the handle, late, on the live connection
            dispatch.RunJobs();
            Assert.True(link.Written.Count == 1, Said(link, devices, device, receipts));               // the look-up and no play
            lock (receipts) Assert.Single(receipts);
            Assert.StartsWith("result 4711 — the answer to a look-up let go", devices.Traffic(device.Id)!.Value.LastIn, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AnItemOfALinkThatIsNoLongerTheDevicesIsDroppedAndTheNewLinksCuePlays()
    {
        // Link A has reconnected twice; a line of its connection 2 waits in the queue when the device's password changes and one pass
        // closes A and opens B. The item is A's: read into B's session it would make B's connection 1 look gone, so B's cue would
        // never play. It is dropped and counted; B's cue plays.
        var a = new FakeConnectionLink();
        var b = new FakeConnectionLink();
        Queued(DeviceProfile.Pixera, _ => { }, new[] { a, b }, (dispatch, devices, device, receipts, _) =>
        {
            a.Connect();
            dispatch.RunJobs();
            a.Connect();
            dispatch.RunJobs();
            a.Say("{\"jsonrpc\":\"2.0\",\"id\":9,\"result\":true}");                                  // said on A's connection 2, still queued
            device.Secret = "changed";
            devices.Reconcile();                                                                      // A closed, B opened, in one pass
            Assert.Same(b, devices.LinkFor(device.Id));
            dispatch.RunJobs();
            Assert.Equal(1L, devices.StaleDropped);

            b.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            b.Say(Reply);
            dispatch.RunJobs();
            Assert.True(b.Written.Count == 2, Said(b, devices, device, receipts));
            Assert.Contains("\"handle\":4711", b.Written[1], StringComparison.Ordinal);
            Assert.Empty(receipts);
        });
    }

    [Fact]
    public void AStaleStartMarkerOfANodeWritesNoHelloOnTheConnectionAfterIt()
    {
        // Two connections published, each start queued behind the other: only the newest connection's own start opens it with HELLO.
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.ShowBusNode, d =>
        {
            d.Port = "10.0.0.9";
            d.Secret = "K7QM-3XWD-P9RA";
        }, new[] { link }, (dispatch, devices, device, receipts, host) =>
        {
            host.Wire.State = "{\"blackout\":true,\"live\":false}";
            link.Connect();
            dispatch.RunJobs();
            link.Say("NONCE " + Nonce);
            link.Say("OK @dh");
            link.Say("OK @dp paired");
            dispatch.RunJobs();
            Assert.True(devices.NodeSights.Single().Up);

            var before = link.Written.Count;
            link.Publish();
            link.Open();                                                                              // connection 2's start, queued
            link.Publish();
            link.Open();                                                                              // connection 3's start, queued behind it
            dispatch.RunJobs();
            var after = link.Written.Skip(before).ToList();
            Assert.True(after.Count(w => w.StartsWith("@dh HELLO", StringComparison.Ordinal)) == 1, string.Join(" | ", after));
        });
    }

    [Fact]
    public void ARawLineTakesOnlyAnAnswerWhoseIdTheDeskDidNotMakeAndARawIdTheDeskHoldsIsRefused()
    {
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.Pixera, _ => { }, new[] { link }, (dispatch, devices, device, receipts, _) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);                         // look-up id 1, waiting
            var held = devices.Send(device.Name, "RAW {\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Pixera.Utility.getApiRevision\"}");
            Assert.Equal(ActionStatus.Refused, held.Status);
            Assert.Contains("id 1 is one of this desk's own requests", held.Message, StringComparison.Ordinal);
            Assert.True(link.Written.Count == 1, Said(link, devices, device, receipts));

            Assert.True(devices.Send(device.Name, "RAW {\"jsonrpc\":\"2.0\",\"id\":77,\"method\":\"Pixera.Utility.getApiRevision\"}").Ok);
            Assert.True(devices.Send(device.Name, "RAW {\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Pixera.Utility.getApiRevision\"}").Ok);
            Assert.True(devices.Send(device.Name, "TIMELINE Other PLAY").Ok);                         // the desk's next id skips the typed 2
            Assert.Contains("\"id\":3", link.Written[^1], StringComparison.Ordinal);

            link.Say("{\"jsonrpc\":\"2.0\",\"id\":77,\"result\":\"7.1\"}");                           // an id the desk did not make
            dispatch.RunJobs();
            var receipt = Assert.Single(receipts);
            Assert.Equal((true, "RAW {\"jsonrpc\":\"2.0\",\"id\":77,\"method\":\"Pixera.Utility.getApiRevision\"}"), (receipt.Ok, receipt.Words));
            Assert.Equal(3, devices.PendingSince(0));                                                 // both timelines and the RAW id 2 still wait
        });
    }

    [Fact]
    public void AnObservationWaitsOnItsQuerysOwnAnswerAndNoOther()
    {
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.Pixera, d =>
        {
            d.Confirm = ConfirmLevel.Observed;
            d.ObserveQuery = "API Pixera.Utility.getApiRevision";
            d.ObserveExpect = "";
        }, new[] { link }, (dispatch, devices, device, receipts, _) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "API Pixera.Transport.goToStart").Ok);              // id 1
            link.Say("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":true}");                               // accepted: the query goes as id 2
            dispatch.RunJobs();
            Assert.True(link.Written.Count == 2, Said(link, devices, device, receipts));
            Assert.Contains("\"id\":2", link.Written[1], StringComparison.Ordinal);

            link.Say("{\"jsonrpc\":\"2.0\",\"id\":77,\"result\":\"someone else's\"}");                 // an answer that is not the query's
            dispatch.RunJobs();
            Assert.Empty(receipts);

            link.Say("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":\"7.1\"}");                             // the query's own
            dispatch.RunJobs();
            var receipt = Assert.Single(receipts);
            Assert.Equal((true, ConfirmLevel.Observed), (receipt.Ok, receipt.Reached));
        });
    }

    [Fact]
    public void AProjectorWithAPasswordWritesNothingOnAConnectionItHasNotBeenGreetedOn()
    {
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.PjLink, d => d.Secret = "JBMIAProjectorLink", new[] { link }, (dispatch, devices, device, receipts, _) =>
        {
            link.Connect();
            dispatch.RunJobs();
            var early = devices.Send(device.Name, "POWER ON");
            Assert.Equal(ActionStatus.Refused, early.Status);
            Assert.Contains(PjLinkSession.NotGreetedWords, early.Message, StringComparison.Ordinal);
            Assert.Empty(link.Written);

            link.Say("PJLINK 1 498e4a67");
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "POWER ON").Ok);
            Assert.Equal(PjLinkSession.Digest("498e4a67", device.Secret) + "%1POWR 1\r", Assert.Single(link.Written));
        });
    }

    [Fact]
    public void AProjectorsLineOfAConnectionThatEndedIsUnconfirmedAndNoAnswerOfTheNextOneSettlesIt()
    {
        // POWER ON written on connection 1; the projector drops the socket before it answers. The next connection's start ends the
        // line unconfirmed (written, no answer: it may have run), and the poll's answer on connection 2 — standby — is no yes to it.
        var link = new FakeConnectionLink();
        Queued(DeviceProfile.PjLink, _ => { }, new[] { link }, (dispatch, devices, device, receipts, _) =>
        {
            link.Connect();
            link.Say("PJLINK 0");
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "POWER ON").Ok);
            link.Drop();
            link.Connect();                                                                           // connection 2
            link.Say("PJLINK 0");
            link.Say("%1POWR=0");                                                                     // the poll's answer, on connection 2
            dispatch.RunJobs();
            var receipt = Assert.Single(receipts);
            Assert.Equal((false, LinkOutcome.Unconfirmed, "Unconfirmed"), (receipt.Ok, receipt.Outcome, receipt.JournalOutcome));
            Assert.Equal(DeviceService.UnansweredOnEndedWords, receipt.Answer);
            Assert.Equal($"POWER ON — {DeviceService.UnansweredOnEndedWords}", device.Runtime.LastUnconfirmed);   // the box spoke since: amber, then its reply
            Assert.False(device.Runtime.Failing);
            Assert.Null(devices.LastFailed);                                                          // amber, not the health line's
        });
    }
}
