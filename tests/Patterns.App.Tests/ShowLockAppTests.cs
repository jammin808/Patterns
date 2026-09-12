using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>A machine the tests hold: every call recorded, every answer chosen.</summary>
internal sealed class FakeMachineLock : IMachineLock
{
    public List<string> Calls { get; } = new();
    public bool Available { get; set; } = true;
    public int Playing { get; set; }
    public bool Pending { get; set; }
    public string Foreground { get; set; } = "Patterns";
    public string Receipt { get; set; } = "";
    public LockItemState SoundsAnswer { get; set; } = LockItemState.Done;
    public IReadOnlyList<string> LastAllowed { get; private set; } = Array.Empty<string>();

    public LockItemState Notifications(bool off) { Calls.Add($"notifications:{off}"); return LockItemState.Done; }
    public LockItemState SystemSounds(bool off) { Calls.Add($"sounds:{off}"); return off ? SoundsAnswer : LockItemState.Done; }
    public int OtherAudio(bool mute, IReadOnlyList<string> allowedProcessNames) { Calls.Add($"audio:{mute}"); LastAllowed = allowedProcessNames; return mute ? Playing : 0; }
    public LockItemState Shortcuts(bool off) { Calls.Add($"shortcuts:{off}"); return LockItemState.Done; }
    public LockItemState KeepAwake(bool on) { Calls.Add($"awake:{on}"); return LockItemState.Done; }
    public LockItemState WindowsKey(bool blocked) { Calls.Add($"winkey:{blocked}"); return LockItemState.Done; }
    public bool UpdateRestartPending() => Pending;
    public string ForegroundApp() => Foreground;
    public string RestoreFromReceipt() { Calls.Add("receipt"); return Receipt; }
}

/// <summary>
/// The show lock on the desk: every item held and said, the allowed audio let through, new
/// sessions muted on the tick, the foreground watched, a pending restart read, everything put
/// back on release; on with the outputs and off with them; the wire's words; the brief's line.
/// </summary>
public class ShowLockAppTests
{
    [AvaloniaFact]
    public void TheLockHoldsEveryItemSaysSoWatchesAndPutsEverythingBack()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var lockService = services.ShowLock;
            var machine = new FakeMachineLock { Playing = 2 };
            lockService.Machine = machine;
            var clock = new DateTime(2026, 9, 12, 19, 30, 2, DateTimeKind.Utc);
            lockService.Clock = () => clock;
            vm.State.Lock.AllowedAudio = "Spotify, vlc";

            Assert.False(lockService.Locked);
            Assert.StartsWith("Not locked", lockService.Status);
            Assert.Equal("", lockService.HealthWords);

            var on = services.Actions.Execute(ShowActionKind.ShowLockOn, ActionOrigin.Desk);
            Assert.True(on.Ok, on.Message);
            Assert.True(lockService.Locked);
            Assert.StartsWith("SHOW LOCK ON — LOCKED since ", on.Message);
            Assert.Contains("notifications off · system sounds off · 2 other apps' audio muted (Spotify, vlc let through) · shortcut keys off · awake, display on · Windows key off · no restart pending", lockService.Status);
            Assert.Equal(new[] { "spotify", "vlc" }, machine.LastAllowed);
            Assert.Equal(new[] { "notifications:True", "sounds:True", "audio:True", "shortcuts:True", "awake:True", "winkey:True" }, machine.Calls);
            Assert.Equal(7, lockService.Report.Items.Count);
            Assert.All(lockService.Report.Items, i => Assert.Equal(LockItemState.Done, i.State));
            Assert.True(services.Actions.Execute(ShowActionKind.ShowLockOn, ActionOrigin.Desk).Ok);   // twice is not an error
            Assert.Contains("\"locked\":true", lockService.StatusJson());
            Assert.Contains("\"key\":\"audio\"", lockService.StatusJson());
            Assert.Contains("Show lock: LOCKED since", ShowBrief.Summarise(vm.State, new ShowFacts { ShowLock = lockService.Status }));

            // The tick: another app starts playing — muted, and said; the foreground goes to Teams — logged in the journal.
            machine.Calls.Clear();
            machine.Playing = 3;
            machine.Foreground = "ms-teams";
            clock = clock.AddSeconds(3);
            lockService.Tick();
            Assert.Contains("audio:True", machine.Calls);
            Assert.Contains("3 other apps' audio muted", lockService.Status);
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "FocusLost" && e.Target == "ms-teams");

            // A pending restart is read once a minute and said everywhere.
            machine.Pending = true;
            clock = clock.AddMinutes(1.1);
            lockService.Tick();
            Assert.True(lockService.Status.Contains("Windows Update: a restart is pending"), $"{lockService.Status} | clock {clock:HH:mm:ss} | pending {machine.UpdateRestartPending()} | report {lockService.Report.UpdateRestartPending} | lastCheck {lockService.LastUpdateCheckUtc:HH:mm:ss}");
            Assert.Contains("⚠ Windows Update", lockService.Status);
            Assert.True(lockService.Report.UpdateRestartPending);
            Assert.Contains("restart pending", vm.StatusMessage);

            // Released: everything that was held goes back, in the same order.
            machine.Calls.Clear();
            var off = services.Actions.Execute(ShowActionKind.ShowLockOff, ActionOrigin.Desk);
            Assert.True(off.Ok, off.Message);
            Assert.StartsWith("SHOW LOCK OFF — notifications, system sounds, other apps' audio, the shortcut keys, sleep and the screensaver, the Windows key put back as they were.", off.Message);
            Assert.Equal(new[] { "notifications:False", "sounds:False", "audio:False", "shortcuts:False", "awake:False", "winkey:False" }, machine.Calls);
            Assert.False(lockService.Locked);
            Assert.Contains(services.Journal.Tail(10), e => e.Kind == "ShowLockOff");

            // An item that fails is said, the rest still hold; an item left out is left as it is.
            machine.SoundsAnswer = LockItemState.Failed;
            vm.State.Lock.WindowsKey = false;
            Assert.True(services.Actions.Execute(ShowActionKind.ShowLockOn, ActionOrigin.Desk).Ok);
            Assert.Contains("✗ System sounds — could not be set", lockService.Status);
            Assert.Equal("SHOW LOCK: system sounds could not be held", lockService.Report.HealthWords(true));
            Assert.Contains(lockService.Report.Items, i => i.Key == ShowLockWords.WindowsKey && i.State == LockItemState.Off);
            Assert.True(services.Actions.Execute(ShowActionKind.ShowLockOff, ActionOrigin.Desk).Ok);

            // The wire: the same verbs, and the status as JSON.
            var router = new CommandRouter(services);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("SHOWLOCK ON"))));
            Assert.True(lockService.Locked);
            Assert.Contains("\"locked\":true", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("SHOWLOCK STATUS"))));
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("SHOWLOCK OFF"))));
            Assert.False(lockService.Locked);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheLockGoesOnWithTheOutputsAndOffWithThemAndNotByHandAndNotOnAnotherPlatform()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var lockService = services.ShowLock;
            var machine = new FakeMachineLock();
            lockService.Machine = machine;
            vm.State.Lock.AutoWithOutputs = true;
            Dispatcher.UIThread.RunJobs();

            // With the outputs: on when they open, released when they close.
            var on = services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Assert.True(on.Ok, on.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Outputs.IsLive);
            Assert.True(lockService.Locked);
            Assert.True(lockService.Auto);
            Assert.Contains("(with the outputs)", lockService.Status);
            services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Outputs.IsLive);
            Assert.False(lockService.Locked);

            // Locked by hand, the outputs closing leaves it; the desk's button releases it.
            vm.ShowLockToggleCommand.Execute(null);
            Assert.True(lockService.Locked);
            Assert.False(lockService.Auto);
            Assert.Equal("RELEASE THE MACHINE", vm.ShowLockButtonText);
            Assert.True(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(lockService.Locked);
            vm.ShowLockToggleCommand.Execute(null);
            Assert.False(lockService.Locked);
            Assert.Equal("LOCK THE MACHINE FOR THE SHOW", vm.ShowLockButtonText);

            // The outputs live and the machine not held: the health line says so — unless the show asked for no auto lock and locked by hand.
            vm.State.Lock.AutoWithOutputs = false;
            Assert.True(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.False(lockService.Locked);
            Assert.StartsWith("SHOW LOCK OFF — the outputs are live", lockService.HealthWords);
            services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            // Another platform: every item says only Windows has it, and the lock still counts as on.
            lockService.Machine = new NoMachineLock();
            Assert.True(services.Actions.Execute(ShowActionKind.ShowLockOn, ActionOrigin.Desk).Ok);
            Assert.All(lockService.Report.Items, i => Assert.Equal(LockItemState.NotHere, i.State));
            Assert.Contains("– Notifications — only Windows has this", lockService.Report.Items[0].Line);
            Assert.Equal(CheckLight.Green, lockService.Report.Light(false));
            Assert.True(services.Actions.Execute(ShowActionKind.ShowLockOff, ActionOrigin.Desk).Ok);

            // The receipt a crash left is put back at the start and said.
            var crashed = new FakeMachineLock { Receipt = "The previous run ended with the show lock on: the sounds, the notifications put back." };
            lockService.Machine = crashed;
            lockService.RestoreAfterCrash();
            Assert.Contains("receipt", crashed.Calls);
            Assert.StartsWith("The previous run ended with the show lock on", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
