using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;
using Patterns.Platform.Windows;

namespace Patterns.App.Tests;

/// <summary>
/// Round 65.9: the machine as Windows describes it handed in, the rig saved as known good from
/// the wire, the comparison at every reading of the facts (Super Check's RIG rows, STATE's
/// machine row, RIG STATUS, the Machine page's block, the support info, the assistant's brief),
/// each change of the comparison journaled once, and FORGET.
/// </summary>
public class KnownGoodRigAppTests
{
    private static readonly DateTime Taken = new(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc);

    private static MachineFacts Facts(string driver = "32.0.15.6094", bool ledWall = true)
    {
        var displays = new List<MachineDisplayFact> { new("Desk monitor", 0, 0, 2560, 1440, "60", "DisplayPort", "DEL A0B1 · DELL U2723", "0011223344556677", "RGB 4:4:4", 8, "SDR") };
        if (ledWall) displays.Add(new MachineDisplayFact("LED wall", 2560, 0, 3840, 2160, "50", "HDMI", "PTN 0001 · PATTERNS LED", "AB12CD34EF56", "RGB 4:4:4", 8, "SDR"));
        return new MachineFacts(Taken, "1.65.9", "SHOW-PC-1", "Windows 11 Pro 24H2 (build 26100.4351)", "10.0.0", "Intel Core i9-13900K", 32, 63.7,
            new[] { new GpuFact("NVIDIA RTX A4000", 0x10DE, 0x24B0, 16376, driver, "2024-08-20", "NVIDIA", false) },
            displays,
            new[] { new AudioEndpointFact("Speakers (Realtek)", "{out-1}", "out", true, 48000, 32, 2), new AudioEndpointFact("Mic (USB)", "{in-1}", "in", true, 48000, 16, 1) },
            "High performance", false, "on", "off", "as Windows decides", Array.Empty<string>());
    }

    [AvaloniaFact]
    public void TheRigIsSavedComparedJournaledAndReadEverywhere()
    {
        var b = TestApp.Boot();
        var services = b.Services;
        var facts = Facts();
        MachineProbe.Source = () => facts;
        try
        {
            // Nothing saved: STATE says so, Super Check has its grey row, the inventory is already one line.
            Assert.Null(services.Kernel.KnownGood.Known);
            var router = services.NewRouter();
            using (var doc = JsonDocument.Parse(router.StateJson()))
            {
                var machine = doc.RootElement.GetProperty("machine");
                Assert.Equal("not saved", machine.GetProperty("rig").GetString());
                Assert.Equal("NVIDIA RTX A4000 · driver 560.94 · 2 displays · 1 audio output · High performance", machine.GetProperty("inventory").GetString());
            }
            var check = SuperCheck.Run(services.Metrics.GatherFacts());
            Assert.Contains(check.Rows, r => r.Section == "RIG" && r.Item == "Known good" && r.Light == CheckLight.Grey && r.Value == "not saved");
            Assert.DoesNotContain(services.Journal.Tail(20), e => e.Kind == "RigDrift");

            // SAVE from the wire, with the engineer's note: the file lands beside the settings, the journal says so.
            var saved = services.Actions.Execute(ControlProtocol.Parse("RIG SAVE first show").Action, ActionOrigin.Desk);
            Assert.True(saved.Ok, saved.Message);
            Assert.StartsWith("Rig saved as known good — first show: 1 GPU, 2 displays, 0 contracts, 1 audio output.", saved.Message);
            var known = services.Kernel.KnownGood.Known;
            Assert.NotNull(known);
            Assert.Equal("first show", known!.Note);
            Assert.Equal(2, known.Displays.Count);
            Assert.Equal(ShowActions.BindingWords(services.State.Control), known.Bindings);
            if (services.State.Control.Token.Length > 0) Assert.DoesNotContain(services.State.Control.Token, known.ToJson());   // the bindings in words, never the token itself
            var file = Path.Combine(services.Store.BaseDirectory, KnownGoodRig.FileName);
            Assert.True(File.Exists(file));
            Assert.NotNull(RigSnapshot.FromJson(File.ReadAllText(file)));
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "RigKnownGood" && e.Message.Contains("first show"));

            // The same rig reads unchanged: a green row, one "Same" line in the journal however often the facts are read.
            check = SuperCheck.Run(services.Metrics.GatherFacts());
            var row = check.Rows.Single(r => r.Section == "RIG");
            Assert.Equal(CheckLight.Green, row.Light);
            Assert.StartsWith("unchanged since", row.Value);
            services.Metrics.GatherFacts();
            services.Metrics.GatherFacts();
            Assert.Single(services.Journal.Tail(30), e => e.Kind == "RigDrift");
            Assert.Contains(services.Journal.Tail(30), e => e.Kind == "RigDrift" && e.Outcome == "Same");

            // The driver moves and the wall is gone: the headline is amber, the display row red, one more journal line.
            facts = Facts(driver: "32.0.15.6200", ledWall: false);
            check = SuperCheck.Run(services.Metrics.GatherFacts());
            var rows = check.Rows.Where(r => r.Section == "RIG").ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal(CheckLight.Amber, rows[0].Light);
            Assert.StartsWith("2 changes since", rows[0].Value);
            Assert.Contains(rows, r => r.Item == "GPU driver" && r.Light == CheckLight.Amber && r.Value.Contains("32.0.15.6094 (2024-08-20) → 32.0.15.6200"));
            Assert.Contains(rows, r => r.Item == "display" && r.Light == CheckLight.Red && r.Value == "display missing: LED wall (3840×2160 @ 50 Hz)");
            services.Metrics.GatherFacts();
            Assert.Equal(2, services.Journal.Tail(30).Count(e => e.Kind == "RigDrift"));
            Assert.Contains(services.Journal.Tail(30), e => e.Kind == "RigDrift" && e.Outcome == "Changed" && e.Message.Contains("display missing: LED wall"));

            // RIG STATUS on the wire: the machine, the saved rig and the drift as JSON.
            // The router answers on the desk's thread — this thread, in the headless app — so the dispatcher is pumped until the task has its answer.
            var pending = router.ExecuteAsync(ControlProtocol.Parse("RIG STATUS"));
            for (var i = 0; i < 1000 && !pending.IsCompleted; i++) Dispatcher.UIThread.RunJobs();
            Assert.True(pending.IsCompleted, "the router did not answer RIG STATUS");
            var answer = pending.GetAwaiter().GetResult();
            Assert.StartsWith("OK ", answer);
            using (var doc = JsonDocument.Parse(answer[3..]))
            {
                var root = doc.RootElement;
                var machine = root.GetProperty("machine");
                Assert.Equal("Windows 11 Pro 24H2 (build 26100.4351)", machine.GetProperty("windows").GetString());
                Assert.Equal("562.00", machine.GetProperty("gpus")[0].GetProperty("driverFriendly").GetString());
                Assert.Equal(1, machine.GetProperty("displays").GetArrayLength());
                Assert.Equal("first show", root.GetProperty("known").GetProperty("note").GetString());
                Assert.Equal(2, root.GetProperty("known").GetProperty("displays").GetArrayLength());
                var drift = root.GetProperty("drift");
                Assert.False(drift.GetProperty("same").GetBoolean());
                Assert.Equal(2, drift.GetProperty("changes").GetInt32());
                Assert.Contains(drift.GetProperty("lines").EnumerateArray(), l => l.GetProperty("severe").GetBoolean() && l.GetProperty("words").GetString()!.StartsWith("display missing"));
                Assert.StartsWith("2 changes since", root.GetProperty("words").GetString());
            }
            using (var doc = JsonDocument.Parse(router.StateJson()))
            {
                Assert.StartsWith("2 changes since", doc.RootElement.GetProperty("machine").GetProperty("rig").GetString());
            }

            // The assistant's brief: the machine without its name, the verdict last.
            var brief = services.Kernel.Facts().Machine;
            Assert.Contains(brief, l => l.StartsWith("GPU: NVIDIA RTX A4000 · NVIDIA · 16376 MB · driver 562.00"));
            Assert.DoesNotContain(brief, l => l.Contains("SHOW-PC-1"));
            Assert.StartsWith("Known good rig: 2 changes since", brief[^1]);
            Assert.Contains("display missing: LED wall", brief[^1]);

            // The Machine page's block and the support info carry the same words.
            b.Vm.RefreshRig();
            Assert.StartsWith("! 2 changes since", b.Vm.RigText);
            Assert.Contains("!! display missing: LED wall", b.Vm.RigText);
            Assert.Contains("! GPU driver changed on NVIDIA RTX A4000", b.Vm.RigText);
            var support = services.Metrics.SupportInfo();
            Assert.Contains("MACHINE (as Windows describes it)", support);
            Assert.Contains("GPU: NVIDIA RTX A4000", support);
            Assert.Contains("KNOWN GOOD RIG", support);
            Assert.Contains("! display missing: LED wall", support);

            // FORGET: the file goes, the comparison stops, the page says so.
            services.Kernel.KnownGood.Forget();
            Assert.False(File.Exists(file));
            Assert.Equal("not saved", services.Kernel.KnownGood.Words);
            Assert.Null(services.Actions.RigDriftNow());
            b.Vm.RefreshRig();
            Assert.StartsWith("Not saved.", b.Vm.RigText);

            // A rig is never judged against an empty reading: with a rig saved and nothing read yet, no RIG rows, no false alarm.
            services.Actions.Execute(ControlProtocol.Parse("RIG SAVE").Action, ActionOrigin.Desk);
            facts = MachineFacts.Empty;
            var waiting = services.Metrics.GatherFacts();
            Assert.False(waiting.RigChecked);
            Assert.DoesNotContain(SuperCheck.Run(waiting).Rows, r => r.Section == "RIG");
        }
        finally
        {
            MachineProbe.Source = null;
        }
    }

    [Fact]
    public void TheProbeNeverBlocksTheCallerAndKeepsItsReading()
    {
        MachineProbe.Source = null;
        MachineProbe.Forget();
        try
        {
            // Off Windows the probe still answers: the first ask is the kept (empty) reading or a finished one, never a throw.
            var first = MachineProbe.Read();                                      // the kept reading, or empty with a refresh started on a worker
            var now = MachineProbe.ReadNow();
            Assert.False(now.IsEmpty);
            Assert.Equal(Environment.ProcessorCount, now.Cores);
            Assert.True(now.Windows.Length > 0);
            Assert.True(first.IsEmpty || first.Cores == now.Cores);
            var kept = MachineProbe.Read();                                       // kept for half a minute — the same facts, whichever probe landed last
            Assert.False(kept.IsEmpty);
            Assert.Equal(now.Cores, kept.Cores);
            Assert.Equal(now.Windows, kept.Windows);
            Assert.Equal((0x10DEu, 0x2684u), MachineProbe.PciIds(@"PCI\VEN_10DE&DEV_2684&SUBSYS_16F110DE&REV_A1"));
            Assert.Equal((0u, 0u), MachineProbe.PciIds(@"ROOT\DISPLAY\0000"));
        }
        finally
        {
            MachineProbe.Forget();
        }
    }
}
