using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Freeze, the timed fade and the previous look on a live desk: the bus, the state the remotes read, the desk's own buttons, the show file untouched.</summary>
public class ProFeaturesAppTests
{
    [AvaloniaFact]
    public void FreezeFadeAndLookBackDriveTheShowFromTheDeskAndTheWire()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();

            // FADE: a blackout carrying its own fade to the bus; a second FADE is refused; FADE UP lifts it the same way.
            vm.State.Transition.Enabled = false;
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 2"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Blackout);
            Assert.Equal(2000, services.Bus.Current.FadeOverrideMs);
            Assert.Equal(services.Bus.Current.Version, services.Bus.Current.FadeOverrideVersion);
            Assert.True(services.Bus.Current.FadesEnabled);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 2"))));
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE UP 1"))));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.Blackout);
            Assert.Equal(1000, services.Bus.Current.FadeOverrideMs);
            // The desk's own buttons take the seconds beside them; with no number the show's time.
            vm.FadeSeconds = 3.5;
            vm.FadeToBlackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Blackout);
            Assert.Equal(3500, services.Bus.Current.FadeOverrideMs);
            vm.State.Transition.DurationMs = 700;
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE UP"))));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.Blackout);
            Assert.Equal(700, services.Bus.Current.FadeOverrideMs);

            // LOOK BACK: two looks on air in turn, then back, then the swap.
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LOOKBACK"))));   // nothing to go back to yet
            vm.ActivePattern.Kind = PatternKind.FlatField;
            Dispatcher.UIThread.RunJobs();
            var one = new LookConfig { Id = "look-one", Name = "One", Json = LookService.Capture(vm.State) };
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var two = new LookConfig { Id = "look-two", Name = "Two", Json = LookService.Capture(vm.State) };
            vm.State.LooksAndCues.Looks.Add(one);
            vm.State.LooksAndCues.Looks.Add(two);
            Assert.True(services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, "One").Ok);
            Assert.True(services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, "Two").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("look-two", services.AirLookId);
            Assert.Equal("look-one", services.PreviousAirLookId);
            Assert.Equal("One", vm.PreviousLookName);
            Assert.Contains("\"previousLook\":\"One\"", router.StateJson());
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LOOKBACK"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("look-one", services.AirLookId);
            Assert.Equal("look-two", services.PreviousAirLookId);
            Assert.Equal(PatternKind.FlatField, vm.State.Pattern.Kind);   // the picture followed the look back
            vm.LookBackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("look-two", services.AirLookId);
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Contains("BACK TO 'One'", vm.LookBackText);

            // FREEZE: the flag on the bus and on the snapshot, from the wire and the desk, the desk's button following.
            Assert.False(vm.IsFrozen);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FREEZE ON"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Bus.Frozen);
            Assert.True(services.Bus.Current.Frozen);
            Assert.Contains("\"frozen\":true", router.StateJson());
            vm.PollNow();
            Assert.True(vm.IsFrozen);
            vm.IsFrozen = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Bus.Current.Frozen);
            Assert.Contains("\"frozen\":false", router.StateJson());
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FREEZE"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Bus.Current.Frozen);
            vm.FreezeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Bus.Current.Frozen);

            // None of it is in the show file.
            var json = JsonUtil.Serialize(vm.State);
            Assert.DoesNotContain("Frozen", json);
            Assert.DoesNotContain("PreviousAirLookId", json);
            Assert.DoesNotContain("FadeSeconds", json);

            // The Machine page's versions: the store in the test's own directory keeps them on a change.
            services.Store.BackupSpacing = TimeSpan.Zero;
            vm.State.Name = "Show A";
            services.Store.Save(vm.State);
            vm.State.Name = "Show B";
            services.Store.Save(vm.State);
            vm.RefreshBackups();
            Assert.NotEmpty(vm.BackupChoices);
            Assert.Contains("earlier version", vm.BackupsSummary);
            vm.SelectedBackup = vm.BackupChoices.Last();   // the oldest timed version: the show as it was named A
            vm.RestoreBackupCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Show A", vm.State.Name);
            Assert.StartsWith("Show restored", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
