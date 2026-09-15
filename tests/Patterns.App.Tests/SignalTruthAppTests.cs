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
/// Round 65: signal truth on the desk — a display's observation handed in, a contract set from
/// the wire and the Screens page, the view read back as JSON, the facts under Super Check's
/// SIGNAL, the STATE row's words; the verdict following the observation.
/// </summary>
public class SignalTruthAppTests
{
    private static readonly PixelRect Wall = new(0, 9000, 1920, 1080);

    private static SignalObservation Observation(SignalRate rate, PixelEncoding? encoding = PixelEncoding.RGB, int bits = 8, bool? hdr = false)
        => new(Wall.X, Wall.Y, Wall.Width, Wall.Height, Wall.Width, Wall.Height, rate, encoding, bits, hdr, false, Monitor: "Test LED", Connector: "HDMI");

    [AvaloniaFact]
    public void TheContractIsSetFromTheWireHeldAgainstTheObservationAndReadEverywhere()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        var vm = b.Vm;
        var observation = Observation(SignalRate.Of(50, 1));
        DisplayObservation.Source = () => new[] { observation };
        try
        {
            // One display, handed in the way a hot-plug would: the desk makes its placement.
            services.Screens.Source = () => new[] { new ScreenInfo("sig-a", "Test LED", Wall, 1.0, false, 0, Hz: 50) };
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var placement = services.State.Output.Placements.Single(p => p.ScreenId == "sig-a");
            var n = Rig.OrderedLivePlacements(services.State, services.Screens.All).FindIndex(x => x.Placement.ScreenId == "sig-a") + 1;
            Assert.True(n >= 1);

            // No contract yet: the view shows the evidence and no verdict.
            var open = services.Actions.SignalReportFor(placement, services.Screens.All.Single(s => s.Id == "sig-a"));
            Assert.Equal(SignalVerdict.Unverified, open.Verdict);
            Assert.Contains("1920×1080 · 50 Hz (50/1) · RGB 4:4:4 · 8-bit · SDR", open.Observed);

            // The wire sets the contract; the answer says the verdict.
            var set = services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} SIGNAL 1920x1080 50 RGB 8 SDR").Action, ActionOrigin.Desk);
            Assert.True(set.Ok, set.Message);
            Assert.Contains("MATCH", set.Message);
            Assert.Equal("1920x1080 50 RGB 8 SDR", SignalWords.Of(placement.Signal));
            Dispatcher.UIThread.RunJobs();

            using (var one = JsonDocument.Parse(services.Actions.SignalJson(n.ToString())))
            {
                var root = one.RootElement;
                Assert.Equal("MATCH", root.GetProperty("result").GetString());
                Assert.Equal("1920×1080 · 50 Hz · RGB 4:4:4 · 8-bit · SDR", root.GetProperty("design").GetString());
                Assert.Contains(root.GetProperty("lines").EnumerateArray(), l => l.GetProperty("item").GetString() == "Rate" && l.GetProperty("light").GetString() == "green");
            }
            using (var all = JsonDocument.Parse(services.Actions.SignalJson("")))                       // every screen
            {
                Assert.Contains(all.RootElement.EnumerateArray(), r => r.GetProperty("result").GetString() == "MATCH");
            }
            Assert.Contains("No screen", services.Actions.SignalJson("99"));

            // The facts and Super Check: a SIGNAL section with the screen's lines; STATE's screen row carries the words.
            var facts = services.Metrics.GatherFacts();
            var report = Assert.Single(facts.Signals, r => r.Verdict == SignalVerdict.Match);
            Assert.Contains("1920×1080 · 50 Hz · RGB 4:4:4 · 8-bit · SDR", report.Design);
            // The journal: one entry per verdict change, never one per reading.
            Assert.Contains(services.Journal.Tail(50), e => e.Kind == "SignalVerdict" && e.Target == "Test LED" && e.Outcome == "MATCH");
            services.Metrics.GatherFacts();
            Assert.Single(services.Journal.Tail(50), e => e.Kind == "SignalVerdict" && e.Target == "Test LED");
            Assert.Contains(SuperCheck.Run(facts).Rows, r => r.Section == "SIGNAL" && r.Item.EndsWith(" rate") && r.Light == CheckLight.Green);
            using (var state = JsonDocument.Parse(JsonUtil.SerializeCompact(services.Actions.RemoteScreens())))
            {
                var row = state.RootElement.EnumerateArray().Single(r => r.GetProperty("label").GetString() == "Test LED");
                Assert.Equal("MATCH", row.GetProperty("signal").GetProperty("result").GetString());
                Assert.StartsWith("1920×1080 · 50 Hz (50/1)", row.GetProperty("signal").GetProperty("observed").GetString());
            }

            // A bad word is refused and nothing changes.
            var bad = services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} SIGNAL 1920x1080 fast").Action, ActionOrigin.Desk);
            Assert.False(bad.Ok);
            Assert.Contains("'fast' is not a signal word", bad.Message);
            Assert.Equal("1920x1080 50 RGB 8 SDR", SignalWords.Of(placement.Signal));

            // The observation moves: 59.94 from the same display, and the verdict follows with the families named.
            observation = Observation(SignalRate.Of(60000, 1001));
            using (var drift = JsonDocument.Parse(services.Actions.SignalJson(n.ToString())))
            {
                Assert.Equal("MISMATCH", drift.RootElement.GetProperty("result").GetString());
                var rate = drift.RootElement.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("item").GetString() == "Rate");
                Assert.Equal("amber", rate.GetProperty("light").GetString());
                Assert.Contains("PAL-derived", rate.GetProperty("note").GetString());
                Assert.Contains("NTSC-derived", rate.GetProperty("note").GetString());
            }
            services.Actions.SignalReports();
            Assert.Contains(services.Journal.Tail(50), e => e.Kind == "SignalVerdict" && e.Target == "Test LED" && e.Outcome == "MISMATCH" && e.Message.Contains("MATCH → MISMATCH") && e.Message.Contains("59.94"));

            // The Screens page: the words, the view, APPLY and CLEAR.
            vm.Screens.SelectedPlacement = placement;
            Assert.Equal("1920x1080 50 RGB 8 SDR", vm.Screens.SelectedSignalWords);
            Assert.Contains("DESIGN\n1920×1080 · 50 Hz", vm.Screens.SelectedSignalText);
            Assert.Contains("RESULT\nMISMATCH", vm.Screens.SelectedSignalText);
            vm.Screens.SelectedSignalWords = "422 10";
            vm.Screens.ApplySignalCommand.Execute(null);
            Assert.Equal(PixelEncoding.YCbCr422, placement.Signal.Encoding);
            Assert.Equal(10, placement.Signal.BitDepth);
            Assert.Equal("1920x1080 50 422 10 SDR", vm.Screens.SelectedSignalWords);   // the rest stayed
            Assert.Contains("signal contract", vm.Screens.SignalStatus);
            vm.Screens.ClearSignalCommand.Execute(null);
            Assert.False(placement.Signal.IsSet);
            Assert.Equal("", vm.Screens.SelectedSignalWords);
            Assert.Contains("DESIGN\nnone — the display's own", vm.Screens.SelectedSignalText);
        }
        finally
        {
            DisplayObservation.Source = null;
            services.Screens.Source = null;
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void OffWindowsThereAreNoObservationsAndTheHookSuppliesThem()
    {
        DisplayObservation.Source = null;
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(DisplayObservation.Snapshot());
            Assert.Null(DisplayObservation.For(new PixelRect(0, 0, 10, 10)));
        }
        DisplayObservation.Source = () => new[] { Observation(SignalRate.Of(50, 1)) };
        try
        {
            Assert.NotNull(DisplayObservation.For(Wall));
            Assert.NotNull(DisplayObservation.For(new PixelRect(Wall.X, Wall.Y, 960, 540)));     // a scaled desktop: the origin ties them
            Assert.Null(DisplayObservation.For(new PixelRect(5000, 5000, 100, 100)));
            Assert.Equal("HDMI", DisplayObservation.Connector(5));
            Assert.Equal("DisplayPort", DisplayObservation.Connector(9));
            Assert.Equal("internal", DisplayObservation.Connector(0x80000000));
        }
        finally
        {
            DisplayObservation.Source = null;
        }
    }
}
