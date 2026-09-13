using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The live desk on a booted app: with the stack armed and the outputs live, a calibration, an
/// update and a restart are refused at the one door every verb comes through — and the update
/// window keeps the same word — while content flows; DISARM lifts it.
/// </summary>
public class LivePolicyAppTests
{
    [AvaloniaFact]
    public void ArmedAndLiveRefusesTheWorkThatIsNotTheShowAndDisarmLiftsIt()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.IsSandboxActive = false;
            Assert.True(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Outputs.IsLive);
            var stack = CueStacks.Caller(vm.State);
            stack.Cues.Add(new RunCueConfig { Number = "01", Name = "Walk-in" });
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            Assert.True(services.CueStack.Armed);

            // Refused at the door, whoever asks — the desk, the wire — with the words that lift it; journaled as refused.
            foreach (var (kind, value) in new[] { (ShowActionKind.CalibrateRun, "cam"), (ShowActionKind.CalibrateDemo, ""), (ShowActionKind.UpdateApply, "1234"), (ShowActionKind.Restart, "1234") })
            {
                var refused = services.Actions.Execute(new ShowAction(kind, "", value), ActionOrigin.Desk);
                Assert.Equal(ActionStatus.Refused, refused.Status);
                Assert.StartsWith("Not while the stack is armed and the outputs are live:", refused.Message);
                Assert.Contains("DISARM first", refused.Message);
            }
            Assert.Contains(services.Kernel.Journal.Tail(8), e => e.Kind == "CalibrateRun" && e.Outcome == "Refused");
            Assert.False(services.Calibration.Running);
            var window = services.Updates.Apply("", new ActionOrigin(OriginKind.Schedule, "update window"), byPolicy: true);
            Assert.Equal(ActionStatus.Refused, window.Status);
            Assert.StartsWith("Not while the stack is armed and the outputs are live:", window.Message);

            // Content flows: a message, the game on the show, a cue.
            Assert.True(services.Actions.Execute(new ShowAction(ShowActionKind.MessageOn, "", "Doors open"), ActionOrigin.Desk).Ok);
            Assert.Equal("Doors open", vm.State.Overlays.Message.Text);
            Assert.True(services.Actions.Execute(ShowActionKind.CueGo, ActionOrigin.Desk).Ok);
            var arcade = services.Actions.Execute(ShowActionKind.ArcadeStart, ActionOrigin.Desk);
            Assert.DoesNotContain("Not while", arcade.Message);

            // DISARM: the same verbs answer with their own words, not the policy's.
            services.CueStack.SetArmed(false, ActionOrigin.Desk);
            var calibrate = services.Actions.Execute(new ShowAction(ShowActionKind.CalibrateRun, "", "cam"), ActionOrigin.Desk);
            Assert.DoesNotContain("Not while", calibrate.Message);
            var update = services.Actions.Execute(new ShowAction(ShowActionKind.UpdateApply, "", "wrong"), ActionOrigin.Desk);
            Assert.DoesNotContain("Not while", update.Message);
            Assert.Contains("refused", update.Message);                               // the passcode's own refusal
            services.Actions.Execute(ShowActionKind.ArcadeStop, ActionOrigin.Desk);
        }
        finally
        {
            b.Dispose();
        }
    }
}
