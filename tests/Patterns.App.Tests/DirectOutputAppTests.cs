using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.Views;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Direct output on the app: the fuse across a start, the window-side part live, the Screens page, the Machine page, the super-check.</summary>
public class DirectOutputAppTests
{
    private static readonly GpuAdapterInfo Discrete = new("NVIDIA GeForce RTX 4070", GpuAdapterInfo.VendorNvidia, 1, 12288, 7, false);

    [AvaloniaFact]
    public void AStartWithDirectOutputArmsTheFuseAndTheDeskDisarmsIt()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            GpuService.Seed(new[] { Discrete }, 0, Discrete.Name);
            var placement = vm.State.Output.Placements.First(p => !p.Planned);
            placement.DirectOutput = true;
            var fuse = Path.Combine(b.Dir, DirectOutput.FuseFileName);

            // The start: asked for, a discrete card, Windows 11 — the swap chain, and the fuse armed.
            var plan = DirectOutputService.Initialize(b.Dir, vm.State, new[] { Discrete }, Discrete.Name, isWindows: true, windowsBuild: 22631);
            Assert.Equal(DirectOutputMode.LowLatencySwapChain, plan.Mode);
            Assert.Equal(DirectOutputMode.LowLatencySwapChain, DirectOutputService.ModeInForce);
            Assert.Equal(Avalonia.Win32CompositionMode.LowLatencyDxgiSwapChain, DirectOutputService.CompositionModes()[0]);
            Assert.True(File.Exists(fuse));
            Assert.StartsWith("DIRECT", DirectOutputService.Status(vm.State, placement));
            Assert.Equal("1 output asks · low-latency swap chain in force from this start.", DirectOutputService.Summary(vm.State));

            // The desk came up: the fuse comes out.
            DirectOutputService.MarkStarted();
            Assert.False(File.Exists(fuse));

            // A start that never reached the desk leaves the fuse behind: the next start composes and says so.
            File.WriteAllText(fuse, "armed");
            DirectOutputService.ResetForTests();
            plan = DirectOutputService.Initialize(b.Dir, vm.State, new[] { Discrete }, Discrete.Name, isWindows: true, windowsBuild: 22631);
            Assert.Equal(DirectOutputMode.Composed, plan.Mode);
            Assert.Contains("Held off", plan.Reason);
            Assert.True(DirectOutputService.FuseTripped);
            Assert.Equal(Avalonia.Win32CompositionMode.WinUIComposition, DirectOutputService.CompositionModes()[0]);
            Assert.True(File.Exists(fuse));                 // held until the operator asks again
            Assert.Contains("Held off", DirectOutputService.Status(vm.State, placement));
            DirectOutputService.MarkStarted();              // nothing was armed this start: the fuse stays
            Assert.True(File.Exists(fuse));

            // Ticking again clears it: the next start tries again, and the desk says restart.
            vm.SelectedPlacement = placement;
            vm.SelectedDirectOutput = false;
            vm.SelectedDirectOutput = true;
            Assert.False(DirectOutputService.FuseTripped);
            Assert.False(File.Exists(fuse));
            Assert.StartsWith("Restart Patterns", vm.DirectOutputStatus);
            Assert.Equal("1 output asks · restart Patterns to take effect.", vm.DirectOutputSummary);

            // The super-check reads the same facts and lights the row amber until it is in force.
            var facts = b.Services.Metrics.GatherFacts();
            Assert.Equal(1, facts.DirectOutputsAsking);
            Assert.False(facts.DirectOutputInForce);
            Assert.Contains("1 output asks", facts.DirectOutputSummary);
            var row = Assert.Single(SuperCheck.Run(facts).Rows, r => r.Item == "Direct output");
            Assert.Equal(CheckLight.Amber, row.Light);
        }
        finally
        {
            DirectOutputService.ResetForTests();
            GpuService.Seed(Array.Empty<GpuAdapterInfo>(), -1, "");
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADirectOutputWindowIsPreparedLiveAndTheScreensPageShowsTheTick()
    {
        var b = TestApp.Boot();
        var prepared = new List<(OutputWindow Window, bool Direct)>();
        try
        {
            var vm = b.Vm;
            DirectOutputService.WindowHook = (w, direct) => prepared.Add(((OutputWindow)w, direct));
            var placement = vm.State.Output.Placements.First(p => !p.Planned);
            vm.SelectedPlacement = placement;
            Assert.True(vm.SelectedIsDisplay);
            Assert.False(vm.SelectedDirectOutput);
            vm.SelectedDirectOutput = true;
            Assert.True(placement.DirectOutput);

            // Outputs on: the direct window is prepared as direct the moment it opens.
            b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive);
            var window = b.Services.Outputs.Windows.Single(w => w.TargetScreenId == placement.ScreenId);
            Assert.True(window.IsDirect);
            Assert.Contains(prepared, p => ReferenceEquals(p.Window, window) && p.Direct);

            // Unticked with the outputs live: the same window takes the defaults back — no reopen.
            prepared.Clear();
            vm.SelectedDirectOutput = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(window, b.Services.Outputs.Windows.Single(w => w.TargetScreenId == placement.ScreenId));
            Assert.False(window.IsDirect);
            Assert.Contains(prepared, p => ReferenceEquals(p.Window, window) && !p.Direct);
            b.Services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            // The Screens page: the tick and its line for a display, and the tick writes back.
            vm.SelectedDirectOutput = true;
            var host = new Window { DataContext = vm, Width = 900, Height = 2400, Content = new ScrollViewer { Content = new OutputsSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            var tick = host.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Direct output — bypass the desktop compositor");
            Assert.True(tick.IsVisible);
            Assert.True(tick.IsChecked);
            Assert.NotEmpty(vm.DirectOutputStatus);
            tick.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(placement.DirectOutput);
            host.Close();

            // A planned screen never opens a window: no tick for it.
            var planned = new ScreenPlacement { ScreenId = ScreenPlacement.PlannedIdPrefix + "wall", Planned = true };
            vm.State.Output.Placements.Add(planned);
            vm.SelectedPlacement = planned;
            Assert.False(vm.SelectedIsDisplay);
            vm.State.Output.Placements.Remove(planned);

            // The Machine page carries the summary.
            Assert.StartsWith("Off", vm.DirectOutputSummary);
            var admin = new Window { DataContext = vm, Width = 900, Height = 2400, Content = new ScrollViewer { Content = new AdminSection() } };
            admin.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(admin.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.DirectOutputSummary);
            admin.Close();
        }
        finally
        {
            DirectOutputService.ResetForTests();
            b.Dispose();
        }
    }
}
