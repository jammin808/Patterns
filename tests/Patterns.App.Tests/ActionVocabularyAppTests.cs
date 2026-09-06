using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 16: one action vocabulary, one executor. On a live desk: a cue carries the verbs only the
/// desk and the wire had — the clock's hours, seconds and date, the message's scroll, a countdown
/// to a time and its label, the logo, the PiP, the pattern's kind, a clean picture — straight
/// through the executor with no map; the desk's own kinds are refused from a cue with the reason;
/// and a fade's length means seconds from the desk's key, the wire's line and a cue's step alike.
/// </summary>
public class ActionVocabularyAppTests
{
    private static CueActionConfig Step(ShowActionKind kind, string target = "", string value = "") => new() { Kind = kind, Target = target, Value = value };

    [AvaloniaFact]
    public void ACueRunsAnyVerbTheDeskCanAndAFadeMeansSecondsFromEverySource()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            var stack = CueStacks.Caller(vm.State);
            RunCueConfig Cue(string name, params CueActionConfig[] steps)
            {
                var cue = new RunCueConfig { Name = name };
                foreach (var s in steps) cue.Actions.Add(s);
                stack.Cues.Add(cue);
                return cue;
            }

            // One cue, eleven verbs a cue never had, no map between the cue and the executor.
            var dress = Cue("Dress",
                Step(ShowActionKind.ClockOn), Step(ShowActionKind.ClockFormat, value: "24"), Step(ShowActionKind.ClockSeconds, value: "on"),
                Step(ShowActionKind.ClockDate, value: "show"), Step(ShowActionKind.MessageOn, value: "Welcome"), Step(ShowActionKind.MessageScroll, value: "on"),
                Step(ShowActionKind.CountdownTo, value: "23:59"), Step(ShowActionKind.CountdownLabel, value: "Back at"),
                Step(ShowActionKind.LogoOn), Step(ShowActionKind.PipOn), Step(ShowActionKind.PatternKind, value: "color bars"));
            var r = services.Actions.FireCue(dress, ActionOrigin.Desk);
            Assert.True(r.Ok, r.Message);
            var air = services.AirState;
            Assert.True(air.Overlays.Clock.Enabled);
            Assert.True(air.Overlays.Clock.TwentyFourHour);
            Assert.True(air.Overlays.Clock.ShowSeconds);
            Assert.True(air.Overlays.Clock.ShowDate);
            Assert.True(air.Overlays.Message.Enabled);
            Assert.Equal("Welcome", air.Overlays.Message.Text);
            Assert.True(air.Overlays.Message.Scroll);
            Assert.Equal(CountdownTargetKind.TimeOfDay, air.Countdown.TargetKind);
            Assert.Equal("23:59", air.Countdown.TargetTime);
            Assert.True(air.Countdown.Enabled);
            Assert.Equal("Back at", air.Countdown.Label);
            Assert.True(air.Overlays.Logo.Enabled);
            Assert.True(air.Overlays.Pip.Enabled);
            Assert.Equal(PatternKind.ColorBars, air.Pattern.Kind);
            Assert.Contains("Clock 24-hour", CueSummary.Describe(vm.State, dress, max: 12));

            // A clean picture as a cue.
            var clean = Cue("Clean", Step(ShowActionKind.OverlaysOff));
            Assert.True(services.Actions.FireCue(clean, ActionOrigin.Desk).Ok);
            Assert.False(air.Overlays.Clock.Enabled);
            Assert.False(air.Overlays.Message.Enabled);
            Assert.False(air.Countdown.Enabled);
            Assert.False(air.Overlays.Logo.Enabled);
            Assert.False(air.Overlays.Pip.Enabled);

            // The desk's own kinds never run from a cue: the checks refuse the cue with the reason and nothing moves.
            var take = Cue("Take it", Step(ShowActionKind.Take));
            var refused = services.Actions.FireCue(take, ActionOrigin.Desk);
            Assert.False(refused.Ok);
            Assert.Contains("desk key", refused.Message);
            var restart = Cue("Restart", Step(ShowActionKind.Restart, "1234"));
            Assert.Contains("passcode", services.Actions.FireCue(restart, ActionOrigin.Desk).Message);

            // One convention for a fade's length — seconds — from the desk's key, the wire's line and a cue's step, into the one executor.
            vm.State.Transition.DurationMs = 700;
            Assert.True(services.Actions.Execute(ShowActionKind.FadeToBlack, ActionOrigin.Desk, "", "1.5").Ok);
            Assert.Equal(1500, services.Bus.Current.FadeOverrideMs);
            Assert.True(vm.State.Blackout);
            Assert.True(services.Actions.Execute(ShowActionKind.FadeUp, ActionOrigin.Desk, "", "0.5").Ok);
            Assert.Equal(500, services.Bus.Current.FadeOverrideMs);
            var router = new CommandRouter(services);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 2"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Blackout);
            Assert.Equal(2000, services.Bus.Current.FadeOverrideMs);
            Assert.Equal(new ShowAction(ShowActionKind.FadeToBlack, "SCREEN 2", "2.5"), ControlProtocol.Parse("FADE 2.5 SCREEN 2").Action);
            Assert.Equal(new ShowAction(ShowActionKind.FadeUp, "", ""), ControlProtocol.Parse("FADE UP").Action);
            var up = Cue("Up", Step(ShowActionKind.FadeUp, value: "1"));
            Assert.True(services.Actions.FireCue(up, ActionOrigin.Desk).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.Blackout);
            Assert.Equal(1000, services.Bus.Current.FadeOverrideMs);
            // Milliseconds are read when they say so; blank is the show's own time; words are refused.
            Assert.True(services.Actions.Execute(ShowActionKind.FadeToBlack, ActionOrigin.Desk, "", "1500ms").Ok);
            Assert.Equal(1500, services.Bus.Current.FadeOverrideMs);
            Assert.True(services.Actions.Execute(ShowActionKind.FadeUp, ActionOrigin.Desk, "", "").Ok);
            Assert.Equal(700, services.Bus.Current.FadeOverrideMs);
            var junk = services.Actions.Execute(ShowActionKind.FadeToBlack, ActionOrigin.Desk, "", "slow");
            Assert.False(junk.Ok);
            Assert.Contains("seconds", junk.Message);
            Assert.False(vm.State.Blackout);
        }
        finally
        {
            b.Dispose();
        }
    }
}
