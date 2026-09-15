using Patterns.Devices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The room's other boxes on a live desk, each against a fake of itself on a socket: a projector
/// that asks for a password and answers PJLink, a Pixera that hands back a handle, a Disguise
/// listening for OSC, a web API — driven by the DEVICE verb and a cue's action through the one
/// action layer, the answers read onto the device's card.
/// </summary>
public class EndpointAppTests
{
    private static void PumpUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    private static string ReadUntil(NetworkStream stream, string terminator, int timeoutMs = 10000)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1024];
        var deadline = Environment.TickCount64 + timeoutMs;
        stream.ReadTimeout = 200;
        while (Environment.TickCount64 < deadline)
        {
            var text = sb.ToString();
            var at = text.IndexOf(terminator, StringComparison.Ordinal);
            if (at >= 0)
            {
                var frame = text[..at];
                var rest = text[(at + terminator.Length)..];
                sb.Clear();
                sb.Append(rest);
                return frame;
            }
            try
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) throw new EndOfStreamException();
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (IOException)
            {
                Dispatcher.UIThread.RunJobs();
            }
        }
        throw new TimeoutException("nothing arrived");
    }

    private static void Write(NetworkStream stream, string text) => stream.Write(Encoding.UTF8.GetBytes(text));

    [AvaloniaFact]
    public void AProjectorIsSwitchedOnThroughPjLinkWithItsPasswordAndReadOntoTheCard()
    {
        var b = TestApp.Boot();
        using var projector = new TcpListener(IPAddress.Loopback, 0);
        projector.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var device = DeviceProfiles.Preset(DeviceProfile.PjLink, 1);
            device.Port = "127.0.0.1:" + ((IPEndPoint)projector.LocalEndpoint).Port;
            device.Secret = "JBMIAProjectorLink";
            vm.State.Interactive.Devices.Add(device);
            vm.State.Interactive.Enabled = true;
            Dispatcher.UIThread.RunJobs();

            var accept = projector.AcceptTcpClientAsync();
            PumpUntil(() => accept.IsCompleted);
            using var client = accept.Result;
            using var stream = client.GetStream();
            Write(stream, "PJLINK 1 498e4a67\r");
            PumpUntil(() => services.Devices.LinkFor(device.Id)?.IsOpen == true && device.Status.Contains("authentication on"));

            // The cue's words become the projector's command, the digest in front of the first one.
            var result = services.Actions.Execute(new ShowAction(ShowActionKind.DeviceSend, "Projector", "POWER ON"), ActionOrigin.Desk);
            Assert.True(result.Ok, result.Message);
            var first = ReadUntil(stream, "\r");
            Assert.Equal(PjLinkSession.Digest("498e4a67", "JBMIAProjectorLink") + "%1POWR 1", first);
            Write(stream, "%1POWR=OK\r");
            PumpUntil(() => device.Status.Contains("POWR: OK"));

            // The second command rides the authenticated link; a reply reads as words; a fault is a fault.
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.DeviceSend, "Projector", "INPUT HDMI 1"), ActionOrigin.Desk).Ok);
            Assert.Equal("%1INPT 31", ReadUntil(stream, "\r"));
            Write(stream, "%1INPT=ERR2\r");
            PumpUntil(() => device.Status.Contains("INPT: out of parameter"));

            // Words that are not PJLink's are refused with the reason, and nothing goes out.
            var refused = services.Actions.Execute(new ShowAction(ShowActionKind.DeviceSend, "Projector", "DANCE"), ActionOrigin.Desk);
            Assert.False(refused.Ok);
            Assert.Contains("not a PJLink command", refused.Message);

            // The wire says the same thing.
            var router = new CommandRouter(services);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("DEVICE Projector SHUTTER ON"))));
            Assert.Equal("%1AVMT 31", ReadUntil(stream, "\r"));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("DEVICE Projector DANCE"))));
            Assert.Contains("\"profile\":\"pjlink\"", router.StateJson());

            // A poll asks how it is, on its own; the answer lands on the card and is no command to the show.
            services.Devices.Poll();
            var expectPoll = Environment.TickCount64 + 5000;
            string? polled = null;
            while (polled is null && Environment.TickCount64 < expectPoll)
            {
                Dispatcher.UIThread.RunJobs();
                services.Devices.Poll();
                try { polled = ReadUntil(stream, "\r", 300); } catch (TimeoutException) { }
            }
            Assert.Equal("%1POWR ?", polled);
            Write(stream, "%1POWR=3\r");
            PumpUntil(() => device.Status.Contains("warming up"));
        }
        finally
        {
            projector.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void PixeraPlaysATimelineByHandleAndDisguiseHearsItsOscAndAWebApiGetsItsRequest()
    {
        var b = TestApp.Boot();
        using var pixera = new TcpListener(IPAddress.Loopback, 0);
        pixera.Start();
        using var d3 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var web = new HttpListener();
        var webPort = FreeTcpPort();
        web.Prefixes.Add($"http://127.0.0.1:{webPort}/");
        web.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var px = DeviceProfiles.Preset(DeviceProfile.Pixera, 1);
            px.Port = "127.0.0.1:" + ((IPEndPoint)pixera.LocalEndpoint).Port;
            var dz = DeviceProfiles.Preset(DeviceProfile.Disguise, 1);
            dz.Port = "127.0.0.1:" + ((IPEndPoint)d3.Client.LocalEndPoint!).Port;
            var api = DeviceProfiles.Preset(DeviceProfile.Lines, 1);
            api.Name = "Encoder";
            api.Port = $"http://127.0.0.1:{webPort}";
            vm.State.Interactive.Devices.Add(px);
            vm.State.Interactive.Devices.Add(dz);
            vm.State.Interactive.Devices.Add(api);
            vm.State.Interactive.Enabled = true;
            Dispatcher.UIThread.RunJobs();

            // Pixera: the look-up goes out, the handle comes back, the play goes out with it.
            var accept = pixera.AcceptTcpClientAsync();
            PumpUntil(() => accept.IsCompleted);
            using var client = accept.Result;
            using var stream = client.GetStream();
            PumpUntil(() => services.Devices.LinkFor(px.Id)?.IsOpen == true);
            var cue = new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = "Pixera", Value = "TIMELINE Main PLAY" };
            var result = services.Actions.Execute(cue.ToAction(), ActionOrigin.Desk);
            Assert.True(result.Ok, result.Message);
            var lookup = ReadUntil(stream, PixeraSession.Terminator);
            Assert.Contains("\"method\":\"Pixera.Timelines.getTimelineFromName\"", lookup);
            Assert.Contains("\"name\":\"Main\"", lookup);
            Write(stream, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":4711}" + PixeraSession.Terminator);
            var play = ReadUntil(stream, PixeraSession.Terminator);
            Assert.Contains("\"method\":\"Pixera.Timelines.Timeline.play\"", play);
            Assert.Contains("\"handle\":4711", play);
            PumpUntil(() => px.Status.Contains("found — sending on"));
            Write(stream, "{\"jsonrpc\":\"2.0\",\"id\":2,\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}" + PixeraSession.Terminator);
            PumpUntil(() => px.Status.Contains("Pixera error: Method not found"));

            // Disguise: the words become the show-control message on d3's OSC device.
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.DeviceSend, "Disguise", "CUE 1.5"), ActionOrigin.Desk).Ok);
            var packet = ReceiveDatagram(d3);
            var message = Assert.Single(OscCodec.Decode(packet));
            Assert.Equal("/d3/showcontrol/cue", message.Address);
            Assert.Equal(1.5f, Assert.Single(message.Args));
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.DeviceSend, "Disguise", "TRACK Main stage"), ActionOrigin.Desk).Ok);
            var track = Assert.Single(OscCodec.Decode(ReceiveDatagram(d3)));
            Assert.Equal("/d3/showcontrol/trackname", track.Address);
            Assert.Equal("Main stage", Assert.Single(track.Args));
            // …and what d3 sends back is read as its address and arguments.
            var ours = ((UdpDeviceLink)services.Devices.LinkFor(dz.Id)!).LocalEndpoint!;
            d3.Send(OscCodec.Encode(OscMessage.Of("/d3/showcontrol/transport", "playing")), new IPEndPoint(IPAddress.Loopback, ours.Port));
            PumpUntil(() => dz.Status.Contains("/d3/showcontrol/transport playing"));

            // The web API gets the request the line describes, and its answer is a line on the card.
            var context = web.GetContextAsync();
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.DeviceSend, "Encoder", "POST /api/start {\"channel\":2}"), ActionOrigin.Desk).Ok);
            PumpUntil(() => context.IsCompleted);
            var request = context.Result.Request;
            Assert.Equal("POST", request.HttpMethod);
            Assert.Equal("/api/start", request.Url!.AbsolutePath);
            using (var reader = new StreamReader(request.InputStream)) Assert.Equal("{\"channel\":2}", reader.ReadToEnd());
            Assert.StartsWith("application/json", request.ContentType);
            var response = context.Result.Response;
            var body = Encoding.UTF8.GetBytes("started\nmore");
            response.StatusCode = 200;
            response.OutputStream.Write(body);
            response.Close();
            PumpUntil(() => api.Status.Contains("200 started"));
        }
        finally
        {
            web.Stop();
            pixera.Stop();
            b.Dispose();
        }
    }

    private static int FreeTcpPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static byte[] ReceiveDatagram(UdpClient socket, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (socket.Available > 0)
            {
                IPEndPoint? from = null;
                return socket.Receive(ref from!);
            }
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        throw new TimeoutException("the datagram never came");
    }
}
