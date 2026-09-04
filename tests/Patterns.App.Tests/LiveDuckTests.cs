using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The live duck: an operator or a showcaller makes way for an announcement from the room —
/// everything but a VOG steps down, ramping, and comes back when lifted. A latch, never a
/// programme source: STOP ALL leaves it, and a restart never comes up ducked.
/// </summary>
public class LiveDuckTests
{
    private static readonly DateTime T0 = new(2026, 9, 4, 22, 0, 0, DateTimeKind.Utc);

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

    [AvaloniaFact]
    public void TheVerbsDuckTheMusicWithARampAndTheWireSaysSo()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            b.Vm.State.Stingers.DuckToPct = 10;
            b.Vm.State.Stingers.DuckFadeMs = 300;
            Assert.False(b.Vm.State.Stingers.DuckActive);
            Assert.Equal(1.0, b.Services.Stingers.MusicGainAt(T0));

            var router = new CommandRouter(b.Services);
            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("DUCK ON"))));
            Assert.True(b.Vm.State.Stingers.DuckActive);
            Assert.Contains("\"duck\":true", router.StateJson());

            // The ramp: 1 → 0.1 over 300 ms from the press, on the injected clock.
            var now = DateTime.UtcNow;
            Assert.InRange(b.Services.Stingers.MusicGainAt(now), 0.85, 1.0);
            Assert.Equal(0.1, b.Services.Stingers.MusicGainAt(now.AddSeconds(2)), 3);
            Assert.True(b.Services.Stingers.MusicRamping(now.AddMilliseconds(100)));
            Assert.False(b.Services.Stingers.MusicRamping(now.AddSeconds(1)));
            // Every ducked bus, never the VOG bus.
            Assert.Equal(0.1, b.Services.Stingers.GainAt(AudioBus.StingSound, now.AddSeconds(2)), 3);
            Assert.Equal(0.1, b.Services.Stingers.GainAt(AudioBus.ClipAudio, now.AddSeconds(2)), 3);
            Assert.Equal(1.0, b.Services.Stingers.GainAt(AudioBus.VogSound, now.AddSeconds(2)));
            // The clip gain follows the ramp on the player's polls: at the press it is still 1.
            b.Services.AudioPlayer.ApplyGains(now.AddSeconds(2));
            Assert.Equal(0.1, b.Services.Video.ClipGain, 3);

            // Idempotent, and bare DUCK toggles.
            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("DUCK ON"))));
            Assert.True(b.Vm.State.Stingers.DuckActive);
            Assert.Equal("OK", Pump(router.ExecuteAsync(ControlProtocol.Parse("DUCK"))));
            Assert.False(b.Vm.State.Stingers.DuckActive);
            Assert.Contains("\"duck\":false", router.StateJson());
            Assert.Equal(1.0, b.Services.Stingers.MusicGainAt(DateTime.UtcNow.AddSeconds(2)));
            b.Services.AudioPlayer.ApplyGains(DateTime.UtcNow.AddSeconds(2));
            Assert.Equal(1.0, b.Services.Video.ClipGain);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void StopAllLeavesTheDuckAndSoDoesALookRecall()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            b.Vm.ActivePattern.Kind = PatternKind.Grid;
            b.Vm.NewLookName = "Awards";
            b.Vm.SaveLookCommand.Execute(null);
            b.Vm.State.AudioPlayer.Playing = true;

            Assert.Equal(ActionStatus.Done, b.Services.Actions.Execute(ShowActionKind.DuckOn, ActionOrigin.Desk).Status);
            b.Services.Actions.Execute(ShowActionKind.StopAll, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Vm.State.AudioPlayer.Playing);
            Assert.True(b.Vm.State.Stingers.DuckActive, "STOP ALL is about programme sources; the room is still speaking");

            Assert.True(b.Services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, "Awards").Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Vm.State.Stingers.DuckActive);

            // The duck never reaches the show file: a saved and reloaded show comes up lifted.
            var json = JsonUtil.Serialize(b.Vm.State);
            Assert.DoesNotContain("DuckActive", json);
            Assert.Contains("DuckToPct", json);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACueDucksAndLiftsAndTheJournalSaysSo()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            var stack = CueStacks.Caller(b.Vm.State);
            var down = new RunCueConfig { Number = "1", Name = "Mic to the floor", Actions = { new CueActionConfig { Kind = CueActionKind.DuckOn } } };
            var up = new RunCueConfig { Number = "2", Name = "Back to music", Actions = { new CueActionConfig { Kind = CueActionKind.DuckOff } } };
            stack.Cues.Add(down);
            stack.Cues.Add(up);
            b.Vm.Cues.OnShowLoaded();

            var report = CueValidator.Validate(b.Vm.State, stack);
            Assert.False(report.IsBroken(down.Id), report.ReasonFor(down.Id));
            Assert.False(report.IsBroken(up.Id), report.ReasonFor(up.Id));
            Assert.Equal("Duck for announcement", CueSummary.DescribeAction(b.Vm.State, down.Actions[0]));
            Assert.Equal("Lift the duck", CueSummary.DescribeAction(b.Vm.State, up.Actions[0]));

            Assert.True(b.Services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, down.Id), ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Vm.State.Stingers.DuckActive);
            Assert.True(b.Services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, up.Id), ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Vm.State.Stingers.DuckActive);

            // The cue is what gets journaled, once per fire, with the outcome of its actions.
            var fires = b.Services.Journal.Tail(10).Where(e => e.Kind == "CueFire" && e.Outcome == "Done").ToList();
            Assert.Equal(2, fires.Count);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheDrawerTogglesItAndTheChipsFollow()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.IsSandboxActive = false;
            b.Vm.ShowControls.IsOpen = true;
            Assert.False(b.Vm.ShowControls.DuckOnAir);
            b.Vm.ShowControls.DuckToggleCommand.Execute(null);
            Assert.True(b.Vm.State.Stingers.DuckActive);
            Assert.True(b.Vm.ShowControls.DuckOnAir);
            Assert.Contains("ducked to 10%", b.Vm.ShowControls.DuckAirText);
            b.Vm.Run.Refresh();
            Assert.True(b.Vm.Run.IsDucked);
            Assert.Contains("10%", b.Vm.Run.DuckTip);

            b.Vm.ShowControls.DuckToggleCommand.Execute(null);
            Assert.False(b.Vm.ShowControls.DuckOnAir);
            Assert.Equal("off", b.Vm.ShowControls.DuckAirText);
            b.Vm.Run.Refresh();
            Assert.False(b.Vm.Run.IsDucked);
        }
        finally
        {
            b.Dispose();
        }
    }
}
