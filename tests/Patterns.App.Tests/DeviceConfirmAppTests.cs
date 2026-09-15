using Patterns.Devices;
using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 43: a line to a box is dispatched at once and its receipt follows — the box's yes, its
/// no, or its silence — on the card, in the journal and to whoever waits for it; the HTTP link
/// answers with the response itself; the receipts of what a cue sent can be waited for.
/// </summary>
public class DeviceConfirmAppTests
{
    private static bool WaitFor(Func<bool> done, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return true;
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
        return done();
    }

    private static (DeviceConfig Device, FakeDeviceLink Fake) Projector(TestApp.Booted b, ConfirmLevel confirm, int timeoutMs = 400)
    {
        var fake = new FakeDeviceLink();
        b.Services.Devices.LinkFactory = _ => fake;
        var device = new DeviceConfig { Name = "Proj", Link = DeviceLink.Tcp, Profile = DeviceProfile.PjLink, Port = "10.0.0.7", Confirm = confirm, ConfirmTimeoutMs = timeoutMs, HearsShow = false };
        b.Vm.State.Interactive.Devices.Add(device);
        b.Vm.State.Interactive.Enabled = true;
        b.Services.Devices.Reconcile();
        Assert.Same(fake, b.Services.Devices.LinkFor(device.Id));
        return (device, fake);
    }

    [AvaloniaFact]
    public void TheCardKeepsWhatTheBoxDidLatelyAndStateAndTheDeckReadIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var (device, fake) = Projector(b, ConfirmLevel.Accepted);
            Assert.Same(DeviceRuntime.None, device.Runtime);
            services.Devices.Poll();
            Assert.Equal("No line sent yet.", device.HistoryText);

            // A line sent and answered: the last line, the reply and the level it reached, on the card with their ages.
            Assert.True(services.Devices.Send("Proj", "POWER ON").Ok);
            Assert.Equal("POWER ON", device.Runtime.LastCommand);
            Assert.NotNull(device.Runtime.LastCommandUtc);
            fake.Say("%1POWR=OK");
            Assert.True(WaitFor(() => device.Runtime.LastConfirmed == ConfirmLevel.Accepted), "the yes is kept");
            Assert.Equal("POWR: OK", device.Runtime.LastReply);
            Assert.False(device.Runtime.Failing);
            services.Devices.Poll();
            Assert.StartsWith("Last sent POWER ON (just now) · reply POWR: OK — accepted (just now)", device.HistoryText);

            // A silence: the failure is the box's last word, on the card as FAILED, and in STATE for the deck.
            Assert.True(services.Devices.Send("Proj", "INPUT HDMI 1").Ok);
            Assert.True(WaitFor(() => device.Runtime.LastFailureUtc is not null), "the silence is kept");
            Assert.True(device.Runtime.Failing);
            Assert.Equal("INPUT HDMI 1 — no answer in 0.4 s (delivered, not accepted)", device.Runtime.LastFailure);
            Assert.Equal("INPUT HDMI 1", device.Runtime.LastCommand);
            services.Devices.Poll();
            Assert.Contains("· FAILED INPUT HDMI 1 — no answer in 0.4 s", device.HistoryText);
            var row = System.Text.Json.JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("devices")[0];
            Assert.Equal("Proj", row.GetProperty("name").GetString());
            Assert.Equal("INPUT HDMI 1", row.GetProperty("lastCommand").GetString());
            Assert.Equal("POWR: OK", row.GetProperty("lastReply").GetString());
            Assert.Equal("accepted", row.GetProperty("confirmed").GetString());
            Assert.True(row.GetProperty("failing").GetBoolean());
            Assert.StartsWith("INPUT HDMI 1 — no answer", row.GetProperty("lastFailure").GetString());
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, row.GetProperty("lastFailureUtc").ValueKind);
            Assert.StartsWith("Last sent INPUT HDMI 1", row.GetProperty("history").GetString());

            // The box answers again: no longer failing, the failure kept for the record.
            Assert.True(services.Devices.Send("Proj", "POWER ON").Ok);
            fake.Say("%1POWR=OK");
            Assert.True(WaitFor(() => !device.Runtime.Failing), "the box answered again");
            services.Devices.Poll();
            Assert.Contains("· failed INPUT HDMI 1", device.HistoryText);
            Assert.False(System.Text.Json.JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("devices")[0].GetProperty("failing").GetBoolean());

            // Nothing of it is in the show file.
            var json = JsonUtil.Serialize(vm.State.Interactive);
            Assert.DoesNotContain("lastCommand", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HistoryText", json);
            Assert.DoesNotContain("Runtime", json);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALineIsDispatchedAtOnceAndItsReceiptFollowsTheBoxsYesItsNoOrItsSilence()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var (device, fake) = Projector(b, ConfirmLevel.Accepted);
            var receipts = new List<DeviceReceipt>();
            services.Devices.Receipt += r => receipts.Add(r);

            // Dispatched: the words say so, and say what is awaited.
            var sent = services.Devices.Send("Proj", "POWER ON");
            Assert.True(sent.Ok, sent.Message);
            Assert.Equal("Device Proj: POWER ON — sent; awaiting accepted", sent.Message);
            Assert.Contains("%1POWR 1\r", fake.Written);
            Assert.Equal(1, services.Devices.PendingSince(0));
            Assert.Empty(receipts);

            // The box says yes: the receipt lands, on the card and in the journal.
            fake.Say("%1POWR=OK");
            Assert.True(WaitFor(() => receipts.Count == 1), "the yes lands");
            var yes = receipts[0];
            Assert.True(yes.Ok);
            Assert.Equal(ConfirmLevel.Accepted, yes.Reached);
            Assert.Equal(ConfirmLevel.Accepted, yes.Wanted);
            Assert.Equal("Proj: POWER ON — accepted (POWR: OK)", yes.Line);
            Assert.Contains("· accepted", device.Status);
            Assert.Equal(0, services.Devices.PendingSince(0));
            Assert.Contains(services.Kernel.Journal.Tail(10), e => e.Kind == "DeviceReceipt" && e.Outcome == "Done" && e.Message.Contains("POWER ON — accepted"));

            // The box says no: a failure, with the projector's own reason.
            services.Devices.Send("Proj", "INPUT HDMI 1");
            fake.Say("%1INPT=ERR2");
            Assert.True(WaitFor(() => receipts.Count == 2), "the no lands");
            var no = receipts[1];
            Assert.False(no.Ok);
            Assert.Equal("Proj: INPUT HDMI 1 — rejected: INPT: out of parameter — that input or setting is not on this projector", no.Line);
            Assert.Contains(services.Kernel.Journal.Tail(10), e => e.Kind == "DeviceReceipt" && e.Outcome == "Failed" && e.Message.Contains("rejected"));

            // The box says nothing: the timeout is the answer, and it says how far the line got.
            services.Devices.Send("Proj", "SHUTTER ON");
            Assert.True(WaitFor(() => receipts.Count == 3, 2000), "the silence lands");
            var silence = receipts[2];
            Assert.False(silence.Ok);
            Assert.Equal(ConfirmLevel.Delivered, silence.Reached);
            Assert.Contains("no answer in 0.4 s (delivered, not accepted)", silence.Line);

            // An answer for another command does not close a waiting one: POWR's OK is not INPT's.
            services.Devices.Send("Proj", "INPUT HDMI 1");
            fake.Say("%1POWR=1");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, services.Devices.PendingSince(0));
            fake.Say("%1INPT=OK");
            Assert.True(WaitFor(() => receipts.Count == 4), "the right answer lands");
            Assert.True(receipts[3].Ok);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ObservedAsksTheBoxAfterwardsAndSentOnlyIsAllADatagramCanBe()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var (device, fake) = Projector(b, ConfirmLevel.Observed);
            device.ObserveQuery = "INPUT ?";
            device.ObserveExpect = "31";
            var receipts = new List<DeviceReceipt>();
            services.Devices.Receipt += r => receipts.Add(r);

            var sent = services.Devices.Send("Proj", "INPUT HDMI 1");
            Assert.Equal("Device Proj: INPUT HDMI 1 — sent; awaiting observed", sent.Message);
            fake.Say("%1INPT=OK");                                                      // accepted: the question goes out
            Assert.True(WaitFor(() => fake.Written.Contains("%1INPT ?\r")), string.Join("|", fake.Written));
            Assert.Empty(receipts);
            fake.Say("%1INPT=31");                                                      // and the answer is what was asked for
            Assert.True(WaitFor(() => receipts.Count == 1), "observed");
            Assert.True(receipts[0].Ok);
            Assert.Equal(ConfirmLevel.Observed, receipts[0].Reached);
            Assert.Equal("Proj: INPUT HDMI 1 — observed (input 31)", receipts[0].Line);

            // An answer that is not the expected one is not the observation; the timeout says accepted, not observed.
            services.Devices.Send("Proj", "INPUT HDMI 2");
            fake.Say("%1INPT=OK");
            fake.Say("%1INPT=32");
            Assert.True(WaitFor(() => receipts.Count == 2, 2000), "not observed");
            Assert.False(receipts[1].Ok);
            Assert.Contains("(accepted, not observed)", receipts[1].Line);

            // A datagram: sent, and the device's own level says that is all there is.
            var lights = new DeviceConfig { Name = "Lights", Link = DeviceLink.Udp, Profile = DeviceProfile.Osc, Port = "127.0.0.1", NetPort = 1, Confirm = ConfirmLevel.Accepted, HearsShow = false };
            services.Devices.LinkFactory = d => d.Id == device.Id ? fake : new UdpDeviceLink("127.0.0.1", 1);   // the real UDP link for the lights
            vm.State.Interactive.Devices.Add(lights);
            services.Devices.Reconcile();
            var udp = services.Devices.Send("Lights", "/wall/main 1");
            Assert.True(udp.Ok, udp.Message);
            Assert.Equal("Device Lights: /wall/main 1 — sent (sent only (a UDP datagram has no answer))", udp.Message);
            Assert.True(WaitFor(() => receipts.Count == 3), "sent only lands at once");
            Assert.True(receipts[2].Ok);
            Assert.Equal(ConfirmLevel.Sent, receipts[2].Reached);
            Assert.Equal(ConfirmLevel.Sent, receipts[2].Wanted);
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public async Task TheHttpLinkAnswersWithTheResponseItself()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var ctx = await listener.GetContextAsync();
                var ok = ctx.Request.Url!.AbsolutePath == "/route/main";
                ctx.Response.StatusCode = ok ? 200 : 503;
                var body = Encoding.UTF8.GetBytes(ok ? "routed\n" : "busy\n");
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });
        using var link = new HttpDeviceLink($"http://127.0.0.1:{port}");
        var heard = new List<string>();
        link.LineReceived += l => heard.Add(l);

        var accepted = await link.DeliverAsync(Encoding.UTF8.GetBytes("POST /route/main"));
        Assert.True(accepted.Ok);
        Assert.Equal(ConfirmLevel.Accepted, accepted.Reached);
        Assert.StartsWith("200 OK — routed", accepted.Words);

        var rejected = await link.DeliverAsync(Encoding.UTF8.GetBytes("POST /route/nowhere"));
        Assert.False(rejected.Ok);
        Assert.Equal(ConfirmLevel.Delivered, rejected.Reached);                         // it answered, and said no
        Assert.StartsWith("503", rejected.Words);
        await serve;
        listener.Stop();

        var gone = await new HttpDeviceLink($"http://127.0.0.1:{FreePort()}").DeliverAsync(Encoding.UTF8.GetBytes("GET /"));
        Assert.False(gone.Ok);
        Assert.Equal(ConfirmLevel.Sent, gone.Reached);                                  // nothing answered at all
        Assert.StartsWith("request failed", gone.Words);
        Assert.Contains(heard, l => l.StartsWith("200", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void TheReceiptsOfWhatACueSentCanBeWaitedFor()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var (_, fake) = Projector(b, ConfirmLevel.Accepted);
            var cue = new RunCueConfig { Name = "Wall to main" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = "Proj", Value = "INPUT HDMI 1" });
            CueStacks.Caller(vm.State).Cues.Add(cue);

            var mark = services.Devices.Mark();
            Assert.Equal(0, services.Devices.PendingSince(mark));
            var fired = services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, "Wall to main"), ActionOrigin.Desk);
            Assert.True(fired.Ok, fired.Message);
            Assert.Equal(1, services.Devices.PendingSince(mark));
            var wait = services.Devices.ConfirmSince(mark);
            Assert.False(wait.IsCompleted);
            fake.Say("%1INPT=OK");
            Assert.True(WaitFor(() => wait.IsCompleted), "the cue's line answered");
            var receipt = Assert.Single(wait.Result);
            Assert.True(receipt.Ok);
            Assert.Equal("Proj: INPUT HDMI 1 — accepted (INPT: OK)", receipt.Line);
            Assert.Equal(0, services.Devices.PendingSince(mark));
        }
        finally
        {
            b.Dispose();
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
