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
/// Round 65.11: the far end's word on the desk — the engineer's RECEIVED from the wire flips the
/// verdict and is journaled; a device's input-status adapter asks on its clock, reads the answer
/// through the pattern and says it for its screen; STATE carries it; FORGET clears it.
/// </summary>
public class InputStatusAppTests
{
    private static readonly PixelRect Wall = new(0, 9000, 3840, 2160);
    private const string DevicePath = @"\\?\DISPLAY#PTN0001#5&2a3c8f1&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [AvaloniaFact]
    public void TheEngineerAndADeviceSayWhatTheFarEndReceives()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        DisplayObservation.Source = () => new[] { new SignalObservation(Wall.X, Wall.Y, Wall.Width, Wall.Height, Wall.Width, Wall.Height, SignalRate.Of(50, 1), PixelEncoding.RGB, 8, false, false, Monitor: "LED", Connector: "HDMI", DevicePath: DevicePath) };
        try
        {
            var existing = services.Screens.All.Where(s => !s.IsPlanned && !s.IsVirtual).ToList();
            services.Screens.Source = () => existing.Concat(new[] { new ScreenInfo("rx-a", "LED", Wall, 1.0, false, 0, Hz: 50) }).ToList();
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var placement = services.State.Output.Placements.Single(p => p.ScreenId == "rx-a");
            var n = Rig.OrderedLivePlacements(services.State, services.Screens.All).FindIndex(x => x.Placement.ScreenId == "rx-a") + 1;
            Assert.True(services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} SIGNAL 3840x2160 50 RGB 8 SDR").Action, ActionOrigin.Desk).Ok);
            Assert.Equal(SignalVerdict.Match, services.Actions.SignalReportFor(placement, services.Screens.All.Single(s => s.Id == "rx-a")).Verdict);

            // The engineer reads the processor's panel: it receives 1080p — MISMATCH, whatever Windows sends; journaled once.
            var said = services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} RECEIVED 1920x1080 50").Action, ActionOrigin.Desk);
            Assert.True(said.Ok, said.Message);
            Assert.Contains("engineer receives 1920×1080 · 50 Hz", said.Message);
            Assert.EndsWith("MISMATCH.", said.Message);
            Assert.Equal("engineer", placement.ReceivedBy);
            Assert.NotNull(placement.ReceivedAtUtc);
            var view = services.Actions.SignalReportFor(placement, services.Screens.All.Single(s => s.Id == "rx-a"));
            Assert.Equal(SignalVerdict.Mismatch, view.Verdict);
            Assert.Contains(view.Lines, l => l.Item == "Received raster" && l.Light == CheckLight.Red);
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "Received" && e.Origin == "engineer" && e.Outcome == "MISMATCH");
            using (var doc = JsonDocument.Parse(services.Actions.SignalJson(n.ToString())))
            {
                Assert.StartsWith("1920×1080 · 50 Hz — engineer at ", doc.RootElement.GetProperty("received").GetString());
                Assert.Equal("MISMATCH", doc.RootElement.GetProperty("result").GetString());
            }
            var router = services.NewRouter();
            using (var doc = JsonDocument.Parse(router.StateJson()))
            {
                var row = doc.RootElement.GetProperty("screens").EnumerateArray().Single(r => r.GetProperty("n").GetInt32() == n);
                Assert.StartsWith("1920×1080", row.GetProperty("signal").GetProperty("received").GetString());
            }

            // FORGET: the word goes, MATCH stands again.
            Assert.True(services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} RECEIVED CLEAR").Action, ActionOrigin.Desk).Ok);
            Assert.False(placement.Received.IsSet);
            Assert.Equal("", placement.ReceivedBy);
            Assert.Equal(SignalVerdict.Match, services.Actions.SignalReportFor(placement, services.Screens.All.Single(s => s.Id == "rx-a")).Verdict);

            // A device with an input-status adapter: asked on its clock through the fake wire, its answer read through the pattern, said for its screen.
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            var processor = new DeviceConfig { Name = "Processor", Link = DeviceLink.Tcp, Port = "10.0.0.40", NetPort = 5000, Profile = DeviceProfile.Lines, InputScreen = n.ToString(), InputQuery = "STATUS?", InputPattern = @"INPUT (?<w>\d+)x(?<h>\d+)@(?<hz>[\d.]+)\s+(?<enc>\w+)\s+(?<bits>\d+)", InputEverySeconds = 5 };
            services.State.Interactive.Devices.Add(processor);
            services.State.Interactive.Enabled = true;
            services.Devices.Reconcile();
            Assert.Same(fake, services.Devices.LinkFor(processor.Id));
            services.Devices.Poll();
            Assert.Contains(fake.Written, l => l.StartsWith("STATUS?", StringComparison.Ordinal));                              // asked
            fake.Say("INPUT 3840x2160@60 RGB 8");
            for (var i = 0; i < 50 && placement.ReceivedBy != "Processor"; i++) Dispatcher.UIThread.RunJobs();
            Assert.Equal("Processor", placement.ReceivedBy);
            Assert.Equal("3840x2160 60 RGB 8", SignalWords.Of(placement.Received));
            view = services.Actions.SignalReportFor(placement, services.Screens.All.Single(s => s.Id == "rx-a"));
            Assert.Equal(SignalVerdict.Mismatch, view.Verdict);                                                                    // 60 Hz received against a 50 Hz contract
            Assert.Contains(view.Lines, l => l.Item == "Received rate" && l.Light == CheckLight.Red && l.Value.Contains("Processor receives 60 Hz"));
            Assert.Contains(view.Lines, l => l.Item == "Received raster" && l.Light == CheckLight.Green);
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "Received" && e.Origin == "Processor");

            // The same word again moves nothing; a new one does.
            var entries = services.Journal.Tail(50).Count(e => e.Kind == "Received");
            fake.Say("INPUT 3840x2160@60 RGB 8");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(entries, services.Journal.Tail(50).Count(e => e.Kind == "Received"));
            fake.Say("INPUT 3840x2160@50 RGB 8");
            for (var i = 0; i < 50 && SignalWords.Of(placement.Received) != "3840x2160 50 RGB 8"; i++) Dispatcher.UIThread.RunJobs();
            Assert.Equal("3840x2160 50 RGB 8", SignalWords.Of(placement.Received));
            Assert.Equal(SignalVerdict.Match, services.Actions.SignalReportFor(placement, services.Screens.All.Single(s => s.Id == "rx-a")).Verdict);
            Assert.Equal(entries + 1, services.Journal.Tail(50).Count(e => e.Kind == "Received"));

            // An answer that is not the input status is ignored; a screen the rig does not have is a warning, not a throw.
            fake.Say("OK");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("3840x2160 50 RGB 8", SignalWords.Of(placement.Received));
            services.Actions.ReceiveFromDevice(new InputStatusReport("Processor", "no such screen", placement.Received, "raw", DateTime.UtcNow));
        }
        finally
        {
            DisplayObservation.Source = null;
        }
    }
}
