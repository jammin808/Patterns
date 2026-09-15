using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 65.8: the EDID a planned screen presents — built from its contract, read on the wire and
/// over HTTP as bytes, hex and summary, shown on the Screens page, and told apart from the EDID
/// the display presents.
/// </summary>
public class PlannedEdidAppTests
{
    private static readonly PixelRect Wall = new(0, 9000, 1920, 1080);
    private const string DevicePath = @"\\?\DISPLAY#PTN0001#5&2a3c8f1&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static T Pump<T>(Task<T> task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(Task task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        task.GetAwaiter().GetResult();
    }

    /// <summary>One GET on the control port: the status line and the body's bytes.</summary>
    private static (string Status, byte[] Body) Get(int port, string path)
    {
        using var client = new TcpClient();
        Pump(client.ConnectAsync(IPAddress.Loopback, port));
        using var stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: test\r\n\r\n"));
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var n = Pump(stream.ReadAsync(chunk, 0, chunk.Length));
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
        }
        var all = buffer.ToArray();
        var text = Encoding.ASCII.GetString(all);
        var cut = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var status = text[..text.IndexOf("\r\n", StringComparison.Ordinal)]["HTTP/1.1 ".Length..];
        return (status, cut < 0 ? Array.Empty<byte>() : all[(cut + 4)..]);
    }

    [AvaloniaFact]
    public void ThePlannedEdidIsBuiltFromTheContractReadOnTheWireAndOverHttpAndToldFromThePresentedOne()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        var vm = b.Vm;
        var observation = new SignalObservation(Wall.X, Wall.Y, Wall.Width, Wall.Height, Wall.Width, Wall.Height, SignalRate.Of(50, 1), PixelEncoding.RGB, 8, false, false, Monitor: "Test LED", Connector: "HDMI", DevicePath: DevicePath);
        DisplayObservation.Source = () => new[] { observation };
        byte[]? presented = null;
        EdidReader.Source = path => path == DevicePath ? presented : null;
        try
        {
            services.Screens.Source = () => new[] { new ScreenInfo("sig-a", "Test LED", Wall, 1.0, false, 0, Hz: 50) };
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var placement = services.State.Output.Placements.Single(p => p.ScreenId == "sig-a");
            var n = Rig.OrderedLivePlacements(services.State, services.Screens.All).FindIndex(x => x.Placement.ScreenId == "sig-a") + 1;

            // Without a contract the plan is the screen's own size and the conservative words; with one, the contract's.
            var open = services.Actions.PlannedEdid(n.ToString())!;
            Assert.Equal("1920x1080 60 RGB 8 SDR 709 STEREO HDMI", open.Plan.Words);
            Assert.True(services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} SIGNAL 1920x1080 50 RGB 8 SDR HDMI").Action, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            var planned = services.Actions.PlannedEdid(n.ToString())!;
            Assert.Equal("1920x1080 50 RGB 8 SDR 709 STEREO HDMI", planned.Plan.Words);
            Assert.Equal(256, planned.Bytes.Length);
            Assert.Equal(64, planned.Hash.Length);
            Assert.Null(planned.PresentedMatches);                                     // no EDID read yet
            var parsed = Edid.Parse(planned.Bytes);
            Assert.Empty(parsed.Problems);
            Assert.Equal(SignalRate.Of(50, 1), parsed.Preferred!.Rate);
            Assert.Equal(EdidPlan.ProductCodeFor("sig-a"), parsed.ProductCode);
            Assert.Null(services.Actions.PlannedEdid("99"));

            using (var json = JsonDocument.Parse(services.Actions.EdidJson(n.ToString())))
            {
                var root = json.RootElement;
                Assert.Equal(planned.Hash, root.GetProperty("hash").GetString());
                Assert.Equal("1920x1080 50 RGB 8 SDR 709 STEREO HDMI", root.GetProperty("plan").GetString());
                Assert.StartsWith("00 FF FF FF FF FF FF 00", root.GetProperty("hex").GetString());
                Assert.Equal(planned.Bytes, Convert.FromBase64String(root.GetProperty("bytes").GetString()!));
                Assert.Equal($"/api/screens/{n}/edid.bin", root.GetProperty("url").GetString());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("presented").ValueKind);
                Assert.Contains("use: load the .bin", root.GetProperty("summary").GetString());
            }
            Assert.Contains("No screen", services.Actions.EdidJson("99"));
            var wire = services.Control;
            _ = wire;

            // Over HTTP: the bytes, the hex, the summary; a screen that is not there is 404.
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var bin = Get(port, $"/api/screens/{n}/edid.bin");
            Assert.Equal("200 OK", bin.Status);
            Assert.Equal(planned.Bytes, bin.Body);
            var hex = Get(port, $"/api/screens/{n}/edid.hex");
            Assert.Equal("200 OK", hex.Status);
            Assert.StartsWith("00 FF FF FF FF FF FF 00", Encoding.UTF8.GetString(hex.Body));
            var txt = Get(port, $"/api/screens/{n}/edid.txt");
            Assert.Equal("200 OK", txt.Status);
            Assert.Contains("sha-256: " + planned.Hash, Encoding.UTF8.GetString(txt.Body));
            Assert.Equal("404 Not Found", Get(port, "/api/screens/99/edid.bin").Status);

            // The display presents its own EDID: the signal view says so, and how to change that.
            presented = Patterns.Core.Tests.EdidSamples.PatternsLed();
            using (var signal = JsonDocument.Parse(services.Actions.SignalJson(n.ToString())))
            {
                var line = signal.RootElement.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("item").GetString() == "Planned EDID");
                Assert.Equal("grey", line.GetProperty("light").GetString());
                Assert.Contains("presents its own EDID (PTN 0001 · PATTERNS LED)", line.GetProperty("value").GetString());
                Assert.Contains("export Patterns' EDID", line.GetProperty("note").GetString());
            }
            Assert.False(services.Actions.PlannedEdid(n.ToString())!.PresentedMatches);

            // The processor loaded Patterns' EDID: the line goes green.
            presented = planned.Bytes;
            using (var signal = JsonDocument.Parse(services.Actions.SignalJson(n.ToString())))
            {
                var line = signal.RootElement.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("item").GetString() == "Planned EDID");
                Assert.Equal("green", line.GetProperty("light").GetString());
                Assert.Contains("presents Patterns' EDID", line.GetProperty("value").GetString());
            }
            Assert.True(services.Actions.PlannedEdid(n.ToString())!.PresentedMatches);

            // The Screens page: the summary and the link.
            vm.Screens.SelectedPlacement = placement;
            Assert.Contains("1920x1080 50 RGB 8 SDR 709 STEREO HDMI", vm.Screens.SelectedEdidSummary);
            Assert.Contains("sha-256 " + planned.Hash[..16], vm.Screens.SelectedEdidSummary);
            Assert.Contains("presented: this EDID", vm.Screens.SelectedEdidSummary);
            Assert.Contains($"api/screens/{n}/edid.bin", vm.Screens.SelectedEdidLink);
            Assert.Contains($"SCREEN {n} EDID", vm.Screens.SelectedEdidLink);

            // A planned screen with no display has an EDID too: its planned size, its contract.
            var wall = vm.Screens.AddPlannedScreen(3840, 2160, "Main LED");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", SignalWords.Apply("50 RGB 10 HDR10 2020 DP", wall.Signal));
            var wallNumber = Rig.OrderedLivePlacements(services.State, services.Screens.All).FindIndex(x => x.Placement.ScreenId == wall.ScreenId) + 1;
            Assert.True(wallNumber >= 1);
            var wallEdid = services.Actions.PlannedEdid(wallNumber.ToString())!;
            Assert.Equal("3840x2160 50 RGB 10 HDR10 2020 STEREO DP", wallEdid.Plan.Words);
            var wallParsed = Edid.Parse(wallEdid.Bytes);
            Assert.Equal((3840, 2160), (wallParsed.Preferred!.HActive, wallParsed.Preferred.VActive));
            Assert.True(wallParsed.Cta!.Hdr!.Pq);
            Assert.Equal("DisplayPort", wallParsed.Interface);
        }
        finally
        {
            DisplayObservation.Source = null;
            EdidReader.Source = null;
            services.Screens.Source = null;
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
