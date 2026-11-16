using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A link made of connections as the test drives it, with no socket: a connection is published (moved, then counted, as the link's
/// thread does before it can be written on), its start is said when the test says so, and its lines come when the test says them.
/// Like every link it raises nothing before Start.
/// </summary>
public sealed class ConnectionFakeLink : IDeviceLink, IConnectionLink
{
    private readonly object _gate = new();
    private readonly List<string> _written = new();
    private readonly List<string> _lost = new();
    private bool _published;

    /// <summary>What reached an open connection; a write while none is open records nothing, as on a real link whose stream is gone.</summary>
    public IReadOnlyList<string> Written
    {
        get { lock (_gate) return _written.ToList(); }
    }

    /// <summary>What the desk tried to write while no connection was open: a write the desk should never have tried.</summary>
    public IReadOnlyList<string> Lost
    {
        get { lock (_gate) return _lost.ToList(); }
    }

    public bool Disposed { get; private set; }
    public int Starts { get; private set; }
    public long Connection { get; private set; }
    public long Ended { get; private set; }
    public string Status => Disposed ? "closed" : _published ? "open (fake)" : "connecting…";
    public bool IsOpen => !Disposed && _published;
    public event Action<string>? LineReceived;
    public event Action? Moving;
    public event Action? Opened;

    public void Start() => Starts++;

    public void Write(string framedLine)
    {
        lock (_gate)
        {
            if (IsOpen) _written.Add(framedLine);
            else _lost.Add(framedLine);
        }
    }

    /// <summary>A new connection published and open; its start not said yet.</summary>
    public void Publish()
    {
        Started("a connection");
        Moving?.Invoke();
        Ended = Connection;   // a real link ends one connection before it publishes the next
        Connection++;
        _published = true;
    }

    /// <summary>The connection open now says its start.</summary>
    public void RaiseOpened()
    {
        Started("a start");
        Opened?.Invoke();
    }

    /// <summary>The connection open now ended.</summary>
    public void Drop()
    {
        Started("a drop");
        Ended = Connection;
        _published = false;
        Moving?.Invoke();
    }

    public void Say(string line)
    {
        Started($"'{line}'");
        LineReceived?.Invoke(line);
    }

    public void Dispose() => Disposed = true;

    private void Started(string what)
    {
        if (Starts == 0) throw new InvalidOperationException(what + " raised before the link was started: a link raises nothing before Start");
    }
}

/// <summary>
/// A Pixera's session takes each connection's start once, on the desk's thread, before anything is encoded for that connection:
/// a cue fired as the box connects — in the moment between the connection's publication and its start's turn on the desk's
/// thread — keeps its look-up, and the handle plays. A look-up of a connection that ended plays nothing, and its line ends
/// not-sent on every reader. No socket and no timing: the test says when each thing happens.
/// </summary>
public class DeviceConnectionStartAppTests
{
    // A frame as the link hands it on: the session's split has already cut it at its terminator.
    private const string Reply = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":4711}";

    private static (DeviceConfig Px, ConnectionFakeLink Fake, List<DeviceReceipt> Receipts) Pixera(TestApp.Booted b)
    {
        var fake = new ConnectionFakeLink();
        b.Services.Devices.LinkFactory = _ => fake;
        var receipts = new List<DeviceReceipt>();
        b.Services.Devices.Receipt += receipts.Add;
        var px = DeviceProfiles.Preset(DeviceProfile.Pixera, 1);
        px.Port = "10.0.0.30";
        px.HearsShow = false;
        px.ConfirmTimeoutMs = 30000;   // no leg rests on the clock: a line ends by what the test does, or at the desk's close
        b.Vm.State.Interactive.Devices.Add(px);
        b.Vm.State.Interactive.Enabled = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(fake, b.Services.Devices.LinkFor(px.Id));
        Assert.Equal(1, fake.Starts);
        return (px, fake, receipts);
    }

    private static ActionResult Timeline(TestApp.Booted b) =>
        b.Services.Actions.Execute(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = "Pixera", Value = "TIMELINE Main PLAY" }.ToAction(), ActionOrigin.Desk);

    private static string Said(TestApp.Booted b, DeviceConfig px, ConnectionFakeLink fake, List<DeviceReceipt> receipts) =>
        $"written [{string.Join(" | ", fake.Written)}]; lost [{string.Join(" | ", fake.Lost)}]; traffic {b.Services.Devices.Traffic(px.Id)}; status '{px.Status}'; receipts [{string.Join(" | ", receipts.Select(r => r.Line))}]";

    /// <summary>The look-up, then the play with the handle its answer carried; the card says the handle was found; no receipt yet.</summary>
    private static void AssertPlayed(TestApp.Booted b, DeviceConfig px, ConnectionFakeLink fake, List<DeviceReceipt> receipts)
    {
        var written = fake.Written;
        Assert.True(written.Count == 2, Said(b, px, fake, receipts));
        Assert.Contains("\"method\":\"Pixera.Timelines.getTimelineFromName\"", written[0], StringComparison.Ordinal);
        Assert.Contains("\"method\":\"Pixera.Timelines.Timeline.play\"", written[1], StringComparison.Ordinal);
        Assert.Contains("\"handle\":4711", written[1], StringComparison.Ordinal);
        Assert.True(px.Status.Contains("found — sending on", StringComparison.Ordinal), Said(b, px, fake, receipts));
        Assert.True(receipts.Count == 0, Said(b, px, fake, receipts));                                   // the look-up's answer is not the line's yes

        // The play's own result is the yes, and the receipt says that result, never the handle's.
        fake.Say("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":true}");
        Dispatcher.UIThread.RunJobs();
        var receipt = Assert.Single(receipts);
        Assert.Equal((true, "result True"), (receipt.Ok, receipt.Answer));
    }

    [AvaloniaFact]
    public void ACueFiredAfterTheConnectionIsPublishedAndBeforeItsStartIsSaidKeepsItsLookUpAndPlays()
    {
        var b = TestApp.Boot();
        try
        {
            var (px, fake, receipts) = Pixera(b);
            fake.Publish();                                                                              // the link's thread: published and open
            Assert.True(Timeline(b).Ok);                                                                 // the cue, before the start is even said
            fake.RaiseOpened();
            fake.Say(Reply);
            Dispatcher.UIThread.RunJobs();                                                               // the start's turn, then the handle
            AssertPlayed(b, px, fake, receipts);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACueFiredWhileTheConnectionsStartWaitsOnTheDesksThreadKeepsItsLookUpAndPlays()
    {
        var b = TestApp.Boot();
        try
        {
            var (px, fake, receipts) = Pixera(b);
            fake.Publish();
            fake.RaiseOpened();                                                                          // the start queued for the desk's thread, not run
            Assert.True(Timeline(b).Ok);
            fake.Say(Reply);
            Dispatcher.UIThread.RunJobs();
            AssertPlayed(b, px, fake, receipts);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AHandleOfAConnectionThatEndedPlaysNothingAndItsLineIsNotSentOnTheCardStateAndTheJournal()
    {
        var b = TestApp.Boot();
        try
        {
            var (px, fake, receipts) = Pixera(b);
            fake.Publish();
            fake.RaiseOpened();
            Dispatcher.UIThread.RunJobs();
            Assert.True(Timeline(b).Ok);                                                                 // the look-up, on connection 1
            fake.Say(Reply);                                                                             // its handle, said on connection 1, still queued
            fake.Drop();
            fake.Publish();                                                                              // connection 2, before the desk's thread ran
            fake.RaiseOpened();
            Dispatcher.UIThread.RunJobs();

            Assert.True(fake.Written.Count == 1, Said(b, px, fake, receipts));                            // the look-up and nothing after it
            Assert.Equal((1L, Reply + DeviceService.EndedNote), (b.Services.Devices.Traffic(px.Id)!.Value.In, b.Services.Devices.Traffic(px.Id)!.Value.LastIn));
            var receipt = Assert.Single(receipts);
            Assert.Equal((false, LinkOutcome.NotSent, "Failed"), (receipt.Ok, receipt.Outcome, receipt.JournalOutcome));
            Assert.Equal(PixeraSession.ForgottenWords, receipt.Answer);
            Assert.Equal($"TIMELINE Main PLAY — {PixeraSession.ForgottenWords}", px.Runtime.LastFailure);
            var row = JsonDocument.Parse(new CommandRouter(b.Services).StateJson()).RootElement.GetProperty("devices")[0];
            Assert.Equal(px.Runtime.LastFailure, row.GetProperty("lastFailure").GetString());
            Assert.True(row.GetProperty("failing").GetBoolean());
            Assert.Contains(b.Services.Journal.Tail(50), e => e.Kind == "DeviceReceipt" && e.Outcome == "Failed" && e.Message.Contains(PixeraSession.ForgottenWords, StringComparison.Ordinal));

            // The new connection's own look-up plays on it (id 2 went to the play that was never written).
            Assert.True(Timeline(b).Ok);
            fake.Say("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":99}");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("\"handle\":99", fake.Written[^1], StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AHandleReadAfterTheBoxHungUpWithNoNextConnectionIsNeverWrittenAndItsLineIsNotSentOnEveryReader()
    {
        // The usual order on a real link: the handle is read, the box hangs up, and the desk's thread drains the handle while the
        // link waits to redial. Nothing is written for the play, the traffic does not count it, and the line is not-sent on the
        // card, STATE, the journal and the health line.
        var b = TestApp.Boot();
        try
        {
            var (px, fake, receipts) = Pixera(b);
            fake.Publish();
            fake.RaiseOpened();
            Dispatcher.UIThread.RunJobs();
            Assert.True(Timeline(b).Ok);
            fake.Say(Reply);
            fake.Drop();                                                                                 // no connection published after it
            Dispatcher.UIThread.RunJobs();

            Assert.True(fake.Written.Count == 1, Said(b, px, fake, receipts));                            // the look-up and nothing after it
            Assert.True(fake.Lost.Count == 0, Said(b, px, fake, receipts));                               // and the play never even tried
            var traffic = b.Services.Devices.Traffic(px.Id)!.Value;
            Assert.Equal((1L, 1L, Reply + DeviceService.EndedNote), (traffic.In, traffic.Out, traffic.LastIn));
            var receipt = Assert.Single(receipts);
            Assert.Equal((false, LinkOutcome.NotSent, "Failed", PixeraSession.ForgottenWords), (receipt.Ok, receipt.Outcome, receipt.JournalOutcome, receipt.Answer));
            var row = JsonDocument.Parse(new CommandRouter(b.Services).StateJson()).RootElement.GetProperty("devices")[0];
            Assert.Equal($"TIMELINE Main PLAY — {PixeraSession.ForgottenWords}", row.GetProperty("lastFailure").GetString());
            Assert.True(row.GetProperty("failing").GetBoolean());
            Assert.Contains(b.Services.Journal.Tail(50), e => e.Kind == "DeviceReceipt" && e.Outcome == "Failed" && e.Message.Contains(PixeraSession.ForgottenWords, StringComparison.Ordinal));
            Assert.Contains(PixeraSession.ForgottenWords, b.Services.Devices.HealthWords, StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
        }
    }
}
