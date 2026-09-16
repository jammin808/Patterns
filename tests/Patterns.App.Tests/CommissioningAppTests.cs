using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;
using Patterns.Platform.Windows;

namespace Patterns.App.Tests;

/// <summary>
/// Round 65.10: the commissioning flow on the desk — a display handed in, the flow walked from
/// DISCOVER to KNOWN GOOD through the wire's verbs, the test route holding it at CONTRACT, the
/// STATE row, COMMISSION STATUS, the walkthrough's checks, the Machine page's block and the brief.
/// </summary>
public class CommissioningAppTests
{
    private static readonly PixelRect Wall = new(0, 9000, 1920, 1080);
    private const string DevicePath = @"\\?\DISPLAY#PTN0001#5&2a3c8f1&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static SignalObservation Observation(SignalRate rate)
        => new(Wall.X, Wall.Y, Wall.Width, Wall.Height, Wall.Width, Wall.Height, rate, PixelEncoding.RGB, 8, false, false, Monitor: "Test LED", Connector: "HDMI", DevicePath: DevicePath);

    private static MachineFacts Facts() => new(new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc), "1.65.10", "SHOW-PC-1", "Windows 11 Pro 24H2 (build 26100.4351)", "10.0.0", "Intel Core i9-13900K", 32, 63.7,
        new[] { new GpuFact("NVIDIA RTX A4000", 0x10DE, 0x24B0, 16376, "32.0.15.6094", "2024-08-20", "NVIDIA", false) },
        new[] { new MachineDisplayFact("Test LED", Wall.X, Wall.Y, Wall.Width, Wall.Height, "50", "HDMI", "PTN 0001 · PATTERNS LED", "AB12CD34EF56", "RGB 4:4:4", 8, "SDR") },
        new[] { new AudioEndpointFact("Speakers (Realtek)", "{out-1}", "out", true, 48000, 32, 2) },
        "High performance", false, "on", "off", "as Windows decides", Array.Empty<string>());

    [AvaloniaFact]
    public void TheFlowIsWalkedFromDiscoverToKnownGood()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        var facts = Facts();
        MachineProbe.Source = () => facts;
        DisplayObservation.Source = () => new[] { Observation(SignalRate.Of(50, 1)) };
        EdidReader.Source = path => path == DevicePath ? Patterns.Core.Tests.EdidSamples.PatternsLed() : null;
        try
        {
            // One display, handed in beside the boot's own (nothing goes missing); the boot's screens are switched off so the
            // flow judges ours alone. DISCOVER and ASSIGN are green, the flow stands at CONTRACT.
            var existing = services.Screens.All.Where(s => !s.IsPlanned && !s.IsVirtual).ToList();
            services.Screens.Source = () => existing.Concat(new[] { new ScreenInfo("com-a", "Test LED", Wall, 1.0, false, 0, Hz: 50) }).ToList();
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var placement = services.State.Output.Placements.Single(p => p.ScreenId == "com-a");
            services.BulkEdit(() => { foreach (var other in services.State.Output.Placements.Where(p => p.ScreenId != "com-a" && !p.IsVirtual)) other.Enabled = false; placement.Enabled = true; });
            Dispatcher.UIThread.RunJobs();
            var n = Rig.OrderedLivePlacements(services.State, services.Screens.All).FindIndex(x => x.Placement.ScreenId == "com-a") + 1;
            var report = services.Actions.CommissioningReport();
            Assert.Equal(CommissionStage.Contract, report.Current!.Stage);
            Assert.Contains(report.Lines, l => l.Stage == CommissionStage.Discover && l.Light == CheckLight.Green);
            Assert.Contains($"SCREEN {n} SIGNAL", report.Next);

            // The test route first — the path proven on the diagnostic profile — holds the flow at CONTRACT and says so.
            var route = services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} TESTROUTE ON").Action, ActionOrigin.Desk);
            Assert.True(route.Ok, route.Message);
            Assert.Contains("TEST ROUTE", route.Message);
            Assert.True(placement.TestRoute);
            Assert.False(placement.Signal.IsSet);                                                        // the contract itself untouched
            var view = services.Actions.SignalReportFor(placement, services.Screens.All.Single(s => s.Id == "com-a"));
            Assert.Equal("Test route", view.Lines[0].Item);
            Assert.StartsWith("1920×1080 · 50 Hz · RGB 4:4:4 · 8-bit · SDR", view.Design);
            Assert.Equal(SignalVerdict.Match, view.Verdict);                                              // the diagnostic profile is what the display carries here
            report = services.Actions.CommissioningReport();
            Assert.Equal(CommissionStage.Contract, report.Current!.Stage);
            Assert.Contains("TEST ROUTE", report.Current.Value);
            Assert.Contains($"SCREEN {n} TESTROUTE OFF", report.Next);
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "TestRoute" && e.Outcome == "On");

            // Off the route, the contract set: CONTRACT, CAPABILITY and VERIFY go green; the outputs and the rig remain.
            Assert.True(services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} TEST ROUTE OFF").Action, ActionOrigin.Desk).Ok);
            Assert.False(placement.TestRoute);
            Assert.True(services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} SIGNAL 1920x1080 50 RGB 8 SDR").Action, ActionOrigin.Desk).Ok);
            report = services.Actions.CommissioningReport();
            Assert.Equal(CommissionStage.OutputTest, report.Current!.Stage);
            Assert.Contains(report.Lines, l => l.Stage == CommissionStage.Capability && l.Light == CheckLight.Green);
            Assert.Contains(report.Lines, l => l.Stage == CommissionStage.Verify && l.Light == CheckLight.Green);
            Assert.Equal(5, report.Done);                                                              // Discover, Assign, Contract, Capability, Verify — the outputs and the rig remain

            // STATE and COMMISSION STATUS say where the flow is.
            var router = services.NewRouter();
            using (var doc = JsonDocument.Parse(router.StateJson()))
            {
                var row = doc.RootElement.GetProperty("commissioning");
                Assert.False(row.GetProperty("complete").GetBoolean());
                Assert.Equal("Output test", row.GetProperty("stage").GetString());
                Assert.Contains("IDENTIFY", row.GetProperty("next").GetString());
            }
            var pending = router.ExecuteAsync(ControlProtocol.Parse("COMMISSION STATUS"));
            for (var i = 0; i < 1000 && !pending.IsCompleted; i++) Dispatcher.UIThread.RunJobs();
            var answer = pending.GetAwaiter().GetResult();
            Assert.StartsWith("OK ", answer);
            using (var doc = JsonDocument.Parse(answer[3..]))
            {
                Assert.Equal(7, doc.RootElement.GetProperty("stages").GetArrayLength());
                Assert.Equal(5, doc.RootElement.GetProperty("done").GetInt32());
                Assert.Contains(doc.RootElement.GetProperty("stages").EnumerateArray(), s => s.GetProperty("stage").GetString() == "Verify" && s.GetProperty("light").GetString() == "green");
            }

            // The walkthrough's checks read the same evidence; the Machine page's block and the brief carry the lines.
            Assert.Equal(true, b.Vm.EvaluateWalkCheck("commission-contract"));
            Assert.Equal(true, b.Vm.EvaluateWalkCheck("commission-verify"));
            Assert.Equal(false, b.Vm.EvaluateWalkCheck("commission-output"));
            Assert.Contains(Walkthroughs.All, w => w.Id == "tech-commission" && w.Steps.Count == 7 && w.Steps.All(s => s.Check.StartsWith("commission-")));
            b.Vm.RefreshRig();
            Assert.StartsWith("5 of 7 stages green", b.Vm.CommissioningText);
            Assert.Contains("✓ Verify: MATCH on every contracted screen (1)", b.Vm.CommissioningText);
            var brief = services.Kernel.Facts().Commissioning;
            Assert.StartsWith("Commissioning: 5 of 7 stages green", brief[0]);
            Assert.Contains(brief, l => l.StartsWith("· Output test: outputs not yet opened"));

            // The rig saved: KNOWN GOOD green; the outputs are the one stage left, honestly.
            Assert.True(services.Actions.Execute(ControlProtocol.Parse("RIG SAVE first show").Action, ActionOrigin.Desk).Ok);
            report = services.Actions.CommissioningReport();
            Assert.Contains(report.Lines, l => l.Stage == CommissionStage.KnownGood && l.Light == CheckLight.Green);
            Assert.Equal(CommissionStage.OutputTest, report.Current!.Stage);
            Assert.Equal(6, report.Done);
            Assert.False(report.Complete);

            // A MISMATCH is red at VERIFY with the observation and the test route offered.
            DisplayObservation.Source = () => new[] { Observation(SignalRate.Of(60, 1)) };
            DisplayObservation.Forget();
            report = services.Actions.CommissioningReport();
            var verify = report.Lines.Single(l => l.Stage == CommissionStage.Verify);
            Assert.Equal(CheckLight.Red, verify.Light);
            Assert.Contains("60 Hz", verify.Next);
            Assert.Contains($"SCREEN {n} TESTROUTE ON", verify.Next);
            Assert.Equal(CheckLight.Red, report.Overall);
        }
        finally
        {
            MachineProbe.Source = null;
            DisplayObservation.Source = null;
            EdidReader.Source = null;
        }
    }
}
