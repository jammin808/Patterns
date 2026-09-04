using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The Run surface: the gate, GO, standby, HOLD, confirm, STOP ALL, the keys, the held automation, the place after a relaunch.</summary>
public class RunTests
{
    private sealed record Built(CueStackConfig Stack, LookConfig A, LookConfig B, RunCueConfig CueA, RunCueConfig CueB, RunCueConfig Gone, RunCueConfig Clock);

    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewLookName = name;
        vm.SaveLookCommand.Execute(null);
        return LookService.Find(vm.State, name)!;
    }

    private static RunCueConfig Cue(CueStackConfig stack, string number, string name, params CueActionConfig[] actions)
    {
        var cue = new RunCueConfig { Number = number, Name = name };
        foreach (var a in actions) cue.Actions.Add(a);
        stack.Cues.Add(cue);
        return cue;
    }

    /// <summary>Looks A (colour bars) and B (focus); cues A, B (needs confirm), Gone (broken), Clock. Program on Grid.</summary>
    private static Built Build(TestApp.Booted b)
    {
        b.Vm.IsSandboxActive = false;
        var a = SaveLook(b.Vm, "A", PatternKind.ColorBars);
        var bb = SaveLook(b.Vm, "B", PatternKind.Focus);
        b.Vm.ActivePattern.Kind = PatternKind.Grid;
        var stack = CueStacks.Caller(b.Vm.State);
        var cueA = Cue(stack, "01.010", "A", new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = a.Id });
        var cueB = Cue(stack, "01.020", "B", new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = bb.Id });
        cueB.RequireConfirm = true;
        var gone = Cue(stack, "01.030", "Gone", new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = "nope" });
        var clock = Cue(stack, "01.040", "Clock", new CueActionConfig { Kind = CueActionKind.ClockOn });
        Dispatcher.UIThread.RunJobs();
        return new Built(stack, a, bb, cueA, cueB, gone, clock);
    }

    [AvaloniaFact]
    public void GoRunsTheStandbyCueAdvancesStandbyAndKeepsThePlace()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            var stack = b.Services.CueStack;
            Assert.False(stack.Armed);
            Assert.Null(stack.Runtime.StandbyCueId);

            stack.SetArmed(true, ActionOrigin.Desk);
            Assert.Equal(built.CueA.Id, stack.Runtime.StandbyCueId); // arming puts the first cue on standby
            var seqBefore = stack.Runtime.Seq;

            var result = b.Vm.Run.Go(ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.True(result.Ok, result.Message);
            Assert.Equal(PatternKind.ColorBars, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(built.CueB.Id, stack.Runtime.StandbyCueId);   // standby moved on
            Assert.Equal(built.CueA.Id, stack.Runtime.LastCueId);
            Assert.Equal("01.010 A", b.Services.AirLabel);
            Assert.True(stack.Runtime.Seq > seqBefore);
            var row = stack.History.First();
            Assert.Equal(CueOutcome.Done, row.Outcome);
            Assert.Equal("01.010 A", row.Label);
            Assert.Equal("desk", row.Origin);
            var journal = b.Services.Journal.Tail(1).Single();
            Assert.Equal("CueGo", journal.Kind);
            Assert.Equal("Done", journal.Outcome);

            // The sidecar carries the place, written on the GO itself.
            var sidecar = new RecoveryStore(b.Dir).Read();
            Assert.NotNull(sidecar?.Run);
            Assert.Equal(built.CueA.Id, sidecar!.Run!.LastCueId);
            Assert.Equal(built.CueB.Id, sidecar.Run.StandbyCueId);
            Assert.Single(sidecar.Run.History);

            // The rows know: A is last, B is standby, Gone is broken.
            b.Vm.Run.Refresh();
            var rows = b.Vm.Run.Rows;
            Assert.True(rows[0].IsLast);
            Assert.True(rows[1].IsStandby);
            Assert.True(rows[2].IsBroken);
            Assert.True(rows[2].IsNext);
            Assert.StartsWith("GO  01.020", b.Vm.Run.GoText);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheGateRefusesInOrderWithAReasonEveryTime()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            var stack = b.Services.CueStack;
            var t = new DateTime(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc);

            var unarmed = stack.Go(ActionOrigin.Desk, nowUtc: t);
            Assert.Equal(ActionStatus.Refused, unarmed.Status);
            Assert.Contains("not armed", unarmed.Message);
            Assert.Equal(CueOutcome.Refused, stack.History.First().Outcome);
            Assert.Equal("Refused", b.Services.Journal.Tail(1).Single().Outcome);

            stack.SetArmed(true, ActionOrigin.Desk);
            stack.SetHold(true, ActionOrigin.Desk);
            Assert.Contains("held", stack.Go(ActionOrigin.Desk, nowUtc: t).Message);
            stack.SetHold(false, ActionOrigin.Desk);

            b.Services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);
            Assert.Contains("blackout", stack.Go(ActionOrigin.Desk, nowUtc: t).Message);
            b.Services.Actions.Execute(ShowActionKind.BlackoutOff, ActionOrigin.Desk);

            stack.Standby(null);
            Assert.Contains("no cue on standby", stack.Go(ActionOrigin.Desk, nowUtc: t).Message);
            stack.Standby(built.CueA.Id);

            Assert.Contains("standby moved", stack.Go(new ActionOrigin(OriginKind.Tcp, "", "10.0.0.5:1"), seenStandbyId: built.CueB.Id, nowUtc: t).Message);
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind); // nothing fired so far

            // Two GOs 50 ms apart fire one cue.
            Assert.True(stack.Go(ActionOrigin.Desk, nowUtc: t).Ok);
            Assert.Equal(PatternKind.ColorBars, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(built.CueB.Id, stack.Runtime.StandbyCueId);
            var tooSoon = stack.Go(ActionOrigin.Desk, nowUtc: t.AddMilliseconds(50));
            Assert.Contains("too soon", tooSoon.Message);
            Assert.Equal(built.CueB.Id, stack.Runtime.StandbyCueId);

            // B asks for confirmation: the first GO arms a window, the second inside it fires.
            var confirm = stack.Go(ActionOrigin.Desk, nowUtc: t.AddMilliseconds(400));
            Assert.Equal(ActionStatus.Requested, confirm.Status);
            Assert.StartsWith("CONFIRM 01.020", confirm.Message);
            Assert.Equal("CONFIRM 01.020", stack.ConfirmText);
            Assert.Equal(PatternKind.ColorBars, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.True(stack.Go(ActionOrigin.Desk, nowUtc: t.AddMilliseconds(1200)).Ok);
            Assert.Equal(PatternKind.Focus, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Null(stack.ConfirmText);
            Assert.Equal(built.Gone.Id, stack.Runtime.StandbyCueId);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ConfirmExpiresOnItsOwnAndEscCancelsIt()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            var stack = b.Services.CueStack;
            var t = new DateTime(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc);
            stack.SetArmed(true, ActionOrigin.Desk);
            stack.Standby(built.CueB.Id);

            Assert.Equal(ActionStatus.Requested, stack.Go(ActionOrigin.Desk, nowUtc: t).Status);
            Assert.NotNull(stack.Runtime.ConfirmPendingCueId);
            b.Vm.IsRunLayout = true;
            b.Vm.Run.EscapePressed();
            Assert.Null(stack.Runtime.ConfirmPendingCueId);

            Assert.Equal(ActionStatus.Requested, stack.Go(ActionOrigin.Desk, nowUtc: t.AddSeconds(1)).Status);
            stack.Poll(t.AddSeconds(3));
            Assert.NotNull(stack.Runtime.ConfirmPendingCueId);
            stack.Poll(t.AddSeconds(6));
            Assert.Null(stack.Runtime.ConfirmPendingCueId); // the window closed by itself
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ABrokenStandbyCueIsRefusedAndTheProgramSnapshotIsByteIdentical()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            var stack = b.Services.CueStack;
            stack.SetArmed(true, ActionOrigin.Desk);
            stack.Standby(built.Gone.Id);
            var before = JsonUtil.Serialize(b.Services.Bus.Current.State);
            var version = b.Services.Bus.Current.Version;

            var result = b.Vm.Run.Go(ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ActionStatus.Refused, result.Status);
            Assert.Contains("not found", result.Message);
            Assert.Equal(before, JsonUtil.Serialize(b.Services.Bus.Current.State));
            Assert.Equal(version, b.Services.Bus.Current.Version); // not even a publish
            Assert.Equal(built.Gone.Id, stack.Runtime.StandbyCueId); // standby stays until the caller moves it
            Assert.Equal(CueOutcome.Refused, stack.History.First().Outcome);
            Assert.StartsWith("BROKEN", b.Vm.Run.StandbyProblem);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void StopAllStopsAudioStingersAndToneAndNothingElse()
    {
        var b = TestApp.Boot();
        try
        {
            Build(b);
            b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            b.Vm.State.AudioPlayer.Playing = true;
            b.Vm.State.Tone.Enabled = true;
            b.Vm.State.Stream.Active = true;
            b.Vm.State.Spotify.Enabled = true;
            b.Vm.State.Spotify.Playing = true;
            b.Services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);

            b.Vm.Run.StopAllCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(b.Vm.State.AudioPlayer.Playing);
            Assert.False(b.Vm.State.Spotify.Playing);
            Assert.False(b.Vm.State.Tone.Enabled);
            Assert.True(b.Vm.State.Stream.Active);       // a broadcast destination, not running media
            Assert.True(b.Services.Outputs.IsLive);      // never the outputs
            Assert.True(b.Vm.State.Blackout);            // never blackout

            // Esc twice within a second on the Run surface is the same thing.
            b.Vm.IsRunLayout = true;
            b.Vm.State.AudioPlayer.Playing = true;
            b.Vm.Run.EscapePressed();
            Assert.True(b.Vm.State.AudioPlayer.Playing);
            Assert.Contains("Esc again", b.Vm.StatusMessage);
            b.Vm.Run.EscapePressed();
            Assert.False(b.Vm.State.AudioPlayer.Playing);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ArmingHoldsTheScheduleAndPlainFKeysButDeliberateRecallsStayLive()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            built.A.Hotkey = 1;
            var now = DateTime.Now;
            b.Vm.State.LooksAndCues.Cues.Add(new CueConfig { Time = now.ToString("HH:mm"), LookName = "A", Enabled = true });
            var stack = b.Services.CueStack;
            stack.SetArmed(true, ActionOrigin.Desk);

            b.Services.Actions.RunSchedule(now);
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind); // the schedule waited
            Assert.StartsWith("HELD", stack.NextAutoText(now.AddMinutes(-1)));

            Assert.False(b.Services.Actions.ApplyLookHotkey(1, ActionOrigin.Keyboard));  // a stray F1 cannot fire
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Contains("held", b.Services.Journal.Tail(1).Single().Message);

            Assert.True(b.Services.Actions.ApplyLook(built.B, ActionOrigin.Desk).Ok);       // the desk's look button still works
            Assert.Equal(PatternKind.Focus, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Equal("B", b.Services.AirLabel);

            stack.SetArmed(false, ActionOrigin.Desk);
            b.Services.Actions.RunSchedule(now);
            Assert.Equal(PatternKind.ColorBars, b.Services.Bus.Current.State.Pattern.Kind); // disarmed: the schedule runs

            // The show can opt out: automation stays live while armed.
            built.Stack.SuspendAutomationWhileArmed = false;
            stack.SetArmed(true, ActionOrigin.Desk);
            Assert.False(stack.SuspendsAutomation);
            Assert.True(b.Services.Actions.ApplyLookHotkey(1, ActionOrigin.Keyboard));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheCallersKeysWorkOnlyOnTheRunSurface()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            var stack = b.Services.CueStack;

            void Tap(Key key, PhysicalKey physical)
            {
                b.Window.KeyPress(key, RawInputModifiers.None, physical, null);
                b.Window.KeyRelease(key, RawInputModifiers.None, physical, null);
                Dispatcher.UIThread.RunJobs();
            }

            Tap(Key.Return, PhysicalKey.Enter);
            Assert.Empty(stack.History); // not the Run surface: Enter means nothing

            b.Vm.IsRunLayout = true;
            Tap(Key.Return, PhysicalKey.Enter);
            Assert.Equal(CueOutcome.Refused, stack.History.First().Outcome); // disarmed: refused, and it says so
            Assert.Contains("not armed", stack.History.First().Detail);

            stack.SetArmed(true, ActionOrigin.Desk);
            Tap(Key.Down, PhysicalKey.ArrowDown);
            Assert.Equal(built.CueB.Id, stack.Runtime.StandbyCueId);
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind); // no output change
            Tap(Key.Up, PhysicalKey.ArrowUp);
            Assert.Equal(built.CueA.Id, stack.Runtime.StandbyCueId);

            Tap(Key.Return, PhysicalKey.Enter);
            Assert.Equal(PatternKind.ColorBars, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(CueOutcome.Done, stack.History.First().Outcome);
            Assert.Equal("keyboard", stack.History.First().Origin);

            b.Vm.ToggleRunLayoutCommand.Execute(null);
            Assert.True(b.Vm.IsRunLayout); // EXIT RUN is refused while armed
            Assert.Contains("Disarm", b.Vm.StatusMessage);
            stack.SetArmed(false, ActionOrigin.Desk);
            b.Vm.ToggleRunLayoutCommand.Execute(null);
            Assert.False(b.Vm.IsRunLayout);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARelaunchRestoresTheCallersPlaceDisarmedAndFiresNothing()
    {
        var first = TestApp.Boot();
        var dir = first.Dir;
        string cueAId, cueBId;
        RunPlace place;
        try
        {
            var built = Build(first);
            cueAId = built.CueA.Id;
            cueBId = built.CueB.Id;
            first.Services.CueStack.SetArmed(true, ActionOrigin.Desk);
            Assert.True(first.Vm.Run.Go(ActionOrigin.Desk).Ok);
            place = first.Services.CueStack.Place();
            first.Services.SaveNow();
        }
        finally
        {
            first.Dispose(); // a clean exit clears the sidecar…
        }
        new RecoveryStore(dir).Write(false, false, null, place); // …so the crash is simulated by putting it back

        var services = new AppServices(new SettingsStore(dir));
        AppServices.Instance = services;
        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        services.AttachMainWindow(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var versionBefore = services.Bus.Current.Version;
            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            var stack = services.CueStack;
            Assert.True(vm.IsRunLayout);
            Assert.False(stack.Armed);
            Assert.Equal(cueBId, stack.Runtime.StandbyCueId);
            Assert.Equal(cueAId, stack.Runtime.LastCueId);
            Assert.Single(stack.History);
            Assert.StartsWith("Restored after restart", vm.Run.Banner);
            Assert.Contains("01.010", vm.Run.Banner);
            Assert.Contains("press ARM", vm.Run.Banner);
            Assert.Equal(versionBefore, services.Bus.Current.Version); // nothing fired, nothing republished
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void ARequestedCueSettlesLaterInsteadOfPretending()
    {
        var b = TestApp.Boot();
        try
        {
            Build(b);
            var track = Path.Combine(b.Dir, "walk-in.mp3");
            File.WriteAllBytes(track, new byte[16]);
            b.Vm.State.AudioPlayer.Path = track;
            var stack = b.Services.CueStack;
            var audio = Cue(stack.Stack, "01.050", "Music", new CueActionConfig { Kind = CueActionKind.AudioPlay });
            stack.SetArmed(true, ActionOrigin.Desk);
            stack.Standby(audio.Id);
            var t = new DateTime(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc);

            var result = stack.Go(ActionOrigin.Desk, nowUtc: t);
            Assert.Equal(ActionStatus.Requested, result.Status);
            Assert.Equal(CueOutcome.Requested, stack.History.First().Outcome);
            Assert.Equal("01.050 Music", b.Services.AirLabel);

            stack.Poll(t.AddSeconds(2));
            var settled = stack.History.First().Outcome;
            Assert.True(settled is CueOutcome.Requested or CueOutcome.FailedLate, settled.ToString());
            stack.Poll(t.AddSeconds(13));
            var final = stack.History.First().Outcome;
            Assert.NotEqual(CueOutcome.Requested, final); // Done once the window passed, or FailedLate with the service's words
            if (final == CueOutcome.FailedLate) Assert.Contains("later:", stack.History.First().Detail);
        }
        finally
        {
            b.Dispose();
        }
    }
}
