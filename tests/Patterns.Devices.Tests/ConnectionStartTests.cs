using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.Devices.Tests;

/// <summary>
/// The desk's thread as the app has it: a post waits in the queue until the test runs the jobs, so a connection's start said on
/// the link's thread is not taken until then. The inline runner the rest of this suite keeps runs a post where it is made, which
/// hides every order this collection is about. A timer fires only when the test fires it.
/// </summary>
public sealed class QueuedDispatch : IDispatchProvider
{
    private readonly Queue<Action> _jobs = new();

    public int Waiting
    {
        get { lock (_jobs) return _jobs.Count; }
    }

    public bool CheckAccess() => true;

    public void Post(Action action)
    {
        lock (_jobs) _jobs.Enqueue(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> function) => Task.FromResult(function());

    public IDispatchTimer Timer(TimeSpan interval) => InlineDispatch.Instance.Timer(interval);

    /// <summary>Runs every job posted, those posted while running included, in the order they came.</summary>
    public void RunJobs()
    {
        while (true)
        {
            Action job;
            lock (_jobs)
            {
                if (_jobs.Count == 0) return;
                job = _jobs.Dequeue();
            }
            job();
        }
    }
}

/// <summary>The collection that swaps the process's dispatcher: run alone, after every collection that runs beside another.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QueuedDispatchCollection
{
    public const string Name = "The desk's thread queued, alone";
}

/// <summary>
/// A Pixera on a link made of connections, with the desk's thread queued as the app's is: a line sent between a connection's
/// publication and its start's turn on the desk's thread is encoded into a session that has taken that connection's start, so
/// the look-up it registers survives the start's turn and the handle plays; a look-up of a connection that ended plays nothing.
/// </summary>
[Collection(QueuedDispatchCollection.Name)]
public sealed class ConnectionStartTests
{
    private const string Reply = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":4711}";
    private const string EndedNote = DeviceService.EndedNote;

    private static void Queued(Action<QueuedDispatch, FakeConnectionLink, DeviceService, DeviceConfig, List<DeviceReceipt>> body)
    {
        var dispatch = new QueuedDispatch();
        var before = Dispatch.Provider;
        Dispatch.Provider = dispatch;
        var host = new NodeHost();
        var device = DeviceProfiles.Preset(DeviceProfile.Pixera, 1);
        device.Port = "10.0.0.30";
        device.ConfirmTimeoutMs = 30000;   // no leg rests on the clock: a line ends by what the test does, or at the area's close
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var link = new FakeConnectionLink();
        var devices = new DeviceService(host) { LinkFactory = _ => link };
        var receipts = new List<DeviceReceipt>();
        devices.Receipt += receipts.Add;
        try
        {
            devices.Reconcile();
            body(dispatch, link, devices, device, receipts);
            Assert.Empty(link.Faults);
        }
        finally
        {
            devices.Dispose();
            dispatch.RunJobs();
            Dispatch.Provider = before;
        }
    }

    private static string Said(FakeConnectionLink link, DeviceService devices, DeviceConfig device) =>
        $"written [{string.Join(" | ", link.Written)}]; traffic {devices.Traffic(device.Id)}; status '{device.Status}'";

    [Fact]
    public void ALineSentBeforeTheConnectionsStartIsTakenOnTheDesksThreadIsEncodedAfterThatStartAndItsHandlePlays()
    {
        Queued((dispatch, link, devices, device, receipts) =>
        {
            link.Publish();                                                                           // published and open; its start not yet said
            Assert.Equal(ActionStatus.Requested, devices.Send(device.Name, "TIMELINE Main PLAY").Status);
            link.Open();                                                                              // its start, queued behind the send
            link.Say(Reply);
            dispatch.RunJobs();
            Assert.True(link.Written.Count == 2, Said(link, devices, device));
            Assert.Contains("getTimelineFromName", link.Written[0], StringComparison.Ordinal);
            Assert.Contains("Pixera.Timelines.Timeline.play", link.Written[1], StringComparison.Ordinal);
            Assert.Contains("\"handle\":4711", link.Written[1], StringComparison.Ordinal);
            Assert.Contains("found — sending on", device.Status, StringComparison.Ordinal);
            Assert.Empty(receipts);                                                                   // the look-up's answer is not the line's yes
        });
    }

    [Fact]
    public void ALineSentWhileTheConnectionsStartWaitsInTheQueueIsEncodedAfterThatStartAndItsHandlePlays()
    {
        Queued((dispatch, link, devices, device, receipts) =>
        {
            link.Connect();                                                                           // published and its start queued, not run
            Assert.Equal(1, dispatch.Waiting);
            Assert.Equal(ActionStatus.Requested, devices.Send(device.Name, "TIMELINE Main PLAY").Status);
            link.Say(Reply);
            dispatch.RunJobs();
            Assert.True(link.Written.Count == 2, Said(link, devices, device));
            Assert.Contains("\"handle\":4711", link.Written[1], StringComparison.Ordinal);
            Assert.Contains("found — sending on", device.Status, StringComparison.Ordinal);
            Assert.Empty(receipts);

            link.Say("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":true}");                               // the play's own result is the yes
            dispatch.RunJobs();
            var receipt = Assert.Single(receipts);
            Assert.Equal((true, "result True"), (receipt.Ok, receipt.Answer));
        });
    }

    [Fact]
    public void AHandleSaidOnAConnectionThatEndedPlaysNothingAndItsLineEndsNotSentWhenTheNextOnesStartIsTaken()
    {
        Queued((dispatch, link, devices, device, receipts) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            link.Say(Reply);                                                                          // said on connection 1, still queued
            link.Connect();                                                                           // 1 ended, 2 published and its start queued
            dispatch.RunJobs();
            Assert.True(link.Written.Count == 1, Said(link, devices, device));                         // the look-up and nothing after it
            Assert.Equal((1L, Reply + EndedNote), (devices.Traffic(device.Id)!.Value.In, devices.Traffic(device.Id)!.Value.LastIn));
            var receipt = Assert.Single(receipts);
            Assert.Equal((false, LinkOutcome.NotSent), (receipt.Ok, receipt.Outcome));
            Assert.Equal(PixeraSession.ForgottenWords, receipt.Answer);

            // The new connection's own look-up plays on it (id 2 went to the play that was never written).
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            link.Say("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":99}");
            dispatch.RunJobs();
            Assert.Contains("\"handle\":99", link.Written[^1], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AHandleSaidOnAConnectionWhoseSuccessorsStartAWriteTookFirstPlaysNothingAndItsLineEndsNotSent()
    {
        Queued((dispatch, link, devices, device, receipts) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            link.Say(Reply);                                                                          // said on connection 1, still queued
            link.Connect();                                                                           // 2 published, its start queued
            Assert.True(devices.Send(device.Name, "TIMELINE Other PLAY").Ok);                         // a write takes 2's start first
            dispatch.RunJobs();
            Assert.True(link.Written.Count == 2, Said(link, devices, device));                         // two look-ups, no play
            Assert.Equal((1L, Reply), (devices.Traffic(device.Id)!.Value.In, devices.Traffic(device.Id)!.Value.LastIn));
            var receipt = Assert.Single(receipts);                                                    // 1's look-up forgotten at that start; the late handle is nobody's yes
            Assert.Equal((false, LinkOutcome.NotSent, PixeraSession.ForgottenWords, "TIMELINE Main PLAY"), (receipt.Ok, receipt.Outcome, receipt.Answer, receipt.Words));
        });
    }

    [Fact]
    public void APlaysOwnResultSaidOnTheConnectionThatThenEndedIsStillTheLinesYes()
    {
        // The box answered the play on connection 1 and hung up; the desk's thread reads that answer only after connection 2 is
        // published. The answer is a fact of connection 1 the session still holds: the line ran, and its receipt says so.
        Queued((dispatch, link, devices, device, receipts) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            link.Say(Reply);
            dispatch.RunJobs();
            Assert.Contains("\"handle\":4711", link.Written[^1], StringComparison.Ordinal);
            link.Say("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":true}");                           // the play's result, on connection 1
            link.Connect();                                                                           // 1 ended, 2 published and its start queued
            dispatch.RunJobs();
            var receipt = Assert.Single(receipts);
            Assert.Equal((true, "result True", (LinkOutcome?)null), (receipt.Ok, receipt.Answer, receipt.Outcome));
        });
    }

    [Fact]
    public void APlaysOwnResultReadAfterTheNextConnectionsStartIsTakenStillSettlesItsOwnLineAndNoOther()
    {
        Queued((dispatch, link, devices, device, receipts) =>
        {
            link.Connect();
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "TIMELINE Main PLAY").Ok);
            link.Say(Reply);
            dispatch.RunJobs();
            link.Say("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":true}");                           // the play's result, on connection 1, queued
            link.Connect();
            Assert.True(devices.Send(device.Name, "TIMELINE Other PLAY").Ok);                         // a write takes 2's start; its look-up waits
            dispatch.RunJobs();
            var receipt = Assert.Single(receipts);                                                    // the first line's yes; the second still waits
            Assert.Equal((true, "result True", "TIMELINE Main PLAY"), (receipt.Ok, receipt.Answer, receipt.Words));
        });
    }

    [Fact]
    public void AProjectorsAnswerSaidOnTheConnectionThatThenEndedStillSettlesItsLine()
    {
        // PJLink: the projector says yes on connection 1 and closes; the desk's thread reads the yes only after connection 2 is
        // published. The session still holds connection 1 (no write and no start of 2 has been taken), so the yes is read and
        // the line ran.
        var dispatch = new QueuedDispatch();
        var before = Dispatch.Provider;
        Dispatch.Provider = dispatch;
        var host = new NodeHost();
        var device = DeviceProfiles.Preset(DeviceProfile.PjLink, 1);
        device.Port = "10.0.0.20";
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
            link.Say("PJLINK 0");
            dispatch.RunJobs();
            Assert.True(devices.Send(device.Name, "POWER ON").Ok);
            Assert.Equal("%1POWR 1\r", link.Written[^1]);
            link.Say("%1POWR=OK");                                                                    // the yes, on connection 1
            link.Connect();                                                                           // 1 ended, 2 published and its start queued
            dispatch.RunJobs();
            var receipt = Assert.Single(receipts);
            Assert.True(receipt.Ok, receipt.Answer);
            Assert.Empty(link.Faults);
        }
        finally
        {
            devices.Dispose();
            dispatch.RunJobs();
            Dispatch.Provider = before;
        }
    }
}
