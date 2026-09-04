using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The action layer: one code path for every verb, journaled with its origin; the OUTPUTS
/// rename with its aliases; the snapshot-level cut; the key guards on the output windows.
/// </summary>
public class ActionLayerTests
{
    [AvaloniaFact]
    public void OutputsVerbsAndTheirAliasesDriveTheOutputs()
    {
        var b = TestApp.Boot();
        try
        {
            var router = new CommandRouter(b.Services);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("OUTPUTS ON"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive);

            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("STOP")))); // alias
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Services.Outputs.IsLive);

            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("GO")))); // alias, never a cue
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive);

            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("outputs off"))));
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Services.Outputs.IsLive);
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void TheWireVocabularyMapsOntoTheShowVocabulary()
    {
        Assert.Equal(new ShowAction(ShowActionKind.ApplyLookHotkey, "3"), CommandRouter.ToAction(ControlProtocol.Parse("LOOK 3")));
        Assert.Equal(new ShowAction(ShowActionKind.ApplyLook, "Walk-in"), CommandRouter.ToAction(ControlProtocol.Parse("LOOK Walk-in")));
        Assert.Equal(new ShowAction(ShowActionKind.PlaylistPart, "Main"), CommandRouter.ToAction(ControlProtocol.Parse("SECTION Main")));
        Assert.Equal(new ShowAction(ShowActionKind.StingerFire, "2"), CommandRouter.ToAction(ControlProtocol.Parse("STINGER 2")));
        Assert.Equal(new ShowAction(ShowActionKind.CanvasOn, "A"), CommandRouter.ToAction(ControlProtocol.Parse("GROUP a ON")));
        Assert.Equal(new ShowAction(ShowActionKind.ScreenToggle, "1"), CommandRouter.ToAction(ControlProtocol.Parse("SCREEN 1")));
        Assert.Equal(new ShowAction(ShowActionKind.OutputsOn), CommandRouter.ToAction(ControlProtocol.Parse("GO")));
        Assert.Equal(new ShowAction(ShowActionKind.SpotifyPlay, "2"), CommandRouter.ToAction(ControlProtocol.Parse("MUSIC PLAY 2")));
        Assert.Equal(new ShowAction(ShowActionKind.SpotifyPlay, "Interval bed"), CommandRouter.ToAction(ControlProtocol.Parse("MUSIC PLAY Interval bed")));
        Assert.Equal(new ShowAction(ShowActionKind.SpotifyPlay), CommandRouter.ToAction(ControlProtocol.Parse("MUSIC PLAY")));
        Assert.Equal(new ShowAction(ShowActionKind.SpotifyPause), CommandRouter.ToAction(ControlProtocol.Parse("MUSIC PAUSE")));
        Assert.Equal(new ShowAction(ShowActionKind.SpotifyNext), CommandRouter.ToAction(ControlProtocol.Parse("SPOTIFY SKIP")));
        Assert.Equal(new ShowAction(ShowActionKind.SpotifyVolume, "", "0"), CommandRouter.ToAction(ControlProtocol.Parse("MUSIC VOL 0")));
        Assert.Null(CommandRouter.ToAction(ControlProtocol.Parse("PING")));
    }

    [AvaloniaFact]
    public void EveryActionIsJournaledWithItsOrigin()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.State.Pattern.Kind = PatternKind.LedWall;
            b.Vm.NewLookName = "Walk-in";
            b.Vm.SaveLookCommand.Execute(null);
            var look = b.Vm.State.LooksAndCues.Looks.Single();

            var result = b.Services.Actions.ApplyLook(look, new ActionOrigin(OriginKind.Companion, "FOH deck"));
            Assert.True(result.Ok);
            b.Services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Keyboard);
            var refused = b.Services.Actions.Execute(ShowActionKind.ApplyLook, new ActionOrigin(OriginKind.Tcp, "", "10.0.0.5:5000"), "No such look");
            Assert.Equal(ActionStatus.Refused, refused.Status);

            var tail = b.Services.Journal.Tail(10);
            Assert.Contains(tail, e => e.Kind == "ApplyLook" && e.Origin == "companion FOH deck" && e.Outcome == "Done" && e.Target == look.Name); // journaled by name, not id
            Assert.Contains(tail, e => e.Kind == "BlackoutOn" && e.Origin == "keyboard" && e.Outcome == "Done");
            Assert.Contains(tail, e => e.Kind == "ApplyLook" && e.Origin == "tcp 10.0.0.5:5000" && e.Outcome == "Refused");
            Assert.True(File.Exists(b.Services.Journal.Path));

            // The desk's status line follows every action, whatever its origin.
            Assert.Contains("No look named", b.Vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACutInsideABulkEditStaysACut()
    {
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            var engine = new PatternEngine();
            using var sink = new SinkState();
            using var surface = SKSurface.Create(new SKImageInfo(64, 36, SKColorType.Bgra8888, SKAlphaType.Premul));
            var frame = 0L;
            void Render()
            {
                var ctx = new RenderContext
                {
                    ViewportSize = new SKSizeI(64, 36),
                    ReferenceSize = new SKSizeI(64, 36),
                    Time = frame * 0.02,
                    Now = DateTime.Now,
                    UtcNow = DateTime.UtcNow,
                    Frame = frame++,
                    Sink = SinkKind.Output,
                    SinkLabel = "test",
                };
                engine.Render(surface.Canvas, services.Bus.Current, in ctx, sink);
            }

            Assert.True(b.Vm.State.Transition.Enabled);
            b.Vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            Render();

            // CUT from inside a bulk edit: the only snapshot the outputs see is the batched one.
            b.Vm.IsSandboxActive = true;
            b.Vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            services.BulkEdit(() => services.Sandbox.SendAll(cut: true));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.State.Pattern.Kind);
            Render();
            Assert.Null(sink.TransitionFrom);            // no fade started
            Assert.True(b.Vm.State.Transition.Enabled);  // the setting was never touched

            // TAKE still crossfades.
            Assert.True(services.Sandbox.Active);         // EDIT SAFE re-armed after the cut
            b.Vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            services.Sandbox.SendAll(cut: false);
            Dispatcher.UIThread.RunJobs();
            Render();
            Assert.NotNull(sink.TransitionFrom);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void DeletingAReferencedLookIsRefusedAndTheResolverIgnoresCase()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.NewLookName = "Walk-in";
            b.Vm.SaveLookCommand.Execute(null);
            var look = b.Vm.State.LooksAndCues.Looks.Single();
            Assert.Same(look, LookService.Find(b.Vm.State, "walk-IN"));
            Assert.Same(look, LookService.Find(b.Vm.State, look.Id));

            // Saving under a case variant updates the same look rather than making a twin.
            b.Vm.NewLookName = "WALK-IN";
            b.Vm.SaveLookCommand.Execute(null);
            Assert.Single(b.Vm.State.LooksAndCues.Looks);

            var clicker = CueStacks.Clicker(b.Vm.State);
            var cue = new RunCueConfig { Number = "01.010", Name = "Opening" };
            cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id });
            clicker.Cues.Add(cue);
            b.Vm.DeleteLookCommand.Execute(look);
            Assert.Single(b.Vm.State.LooksAndCues.Looks);
            Assert.Contains("Clicker list cue 01.010 Opening", b.Vm.StatusMessage);

            clicker.Cues.Clear();
            b.Vm.DeleteLookCommand.Execute(look);
            Assert.Empty(b.Vm.State.LooksAndCues.Looks);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OneEscOnAnOutputNeverClosesTheOutputsButTwoWithinASecondDo()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.GoCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive);
            var output = b.Services.Outputs.Windows.First();

            output.PressKey(Key.Escape);
            output.ReleaseKey(Key.Escape);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.Outputs.IsLive);
            Assert.Contains("Esc again", b.Vm.StatusMessage);

            output.PressKey(Key.Escape);
            output.ReleaseKey(Key.Escape);
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Services.Outputs.IsLive);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AHeldKeyActsOncePerPressOnAnOutput()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.GoCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var output = b.Services.Outputs.Windows.First();

            // The OS repeats KeyDown while a key is held; only the first counts.
            output.PressKey(Key.B);
            output.PressKey(Key.B);
            output.PressKey(Key.B);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Vm.State.Blackout);

            output.ReleaseKey(Key.B);
            output.PressKey(Key.B);
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Vm.State.Blackout);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OutputsOnIsRefusedInPrepAndLeavesTheKeyboardWithTheDesk()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.State.Mode = ShowMode.Prep;
            var refused = b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, refused.Status);
            Assert.False(b.Services.Outputs.IsLive);

            b.Vm.State.Mode = ShowMode.Show;
            var ok = b.Services.Actions.Execute(ShowActionKind.OutputsOn, new ActionOrigin(OriginKind.Http, "", "10.0.0.9:4000"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(ok.Ok);
            Assert.True(b.Services.Outputs.IsLive);
            Assert.Contains(b.Services.Journal.Tail(5), e => e.Kind == "OutputsOn" && e.Origin == "http 10.0.0.9:4000");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheClickerSkipsAStepWhoseLookIsGoneInsteadOfSticking()
    {
        var b = TestApp.Boot();
        try
        {
            var state = b.Vm.State;
            state.LooksAndCues.Looks.Add(new LookConfig { Name = "One", Json = LookService.Capture(state) });
            state.LooksAndCues.Looks.Add(new LookConfig { Name = "Three", Json = LookService.Capture(state) });
            var clicker = CueStacks.Clicker(state);
            foreach (var name in new[] { "One", "Gone", "Three" })
            {
                var cue = new RunCueConfig { Name = name, Number = CueNumber.Next(clicker.Cues.Count > 0 ? clicker.Cues[^1].Number : null) };
                cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = name }); // by name: "Gone" never resolves
                clicker.Cues.Add(cue);
            }
            var rt = b.Services.Cues.For(clicker);
            Assert.Equal(-1, rt.CurrentIndex);

            var first = b.Services.Actions.Execute(ShowActionKind.PresenterNext, ActionOrigin.Clicker);
            Assert.True(first.Ok);
            Assert.Equal(0, rt.CurrentIndex);

            var second = b.Services.Actions.Execute(ShowActionKind.PresenterNext, ActionOrigin.Clicker);
            Assert.True(second.Ok);
            Assert.Equal(2, rt.CurrentIndex); // over the broken cue, onto Three
            Assert.Contains("skipped", second.Message);
            Assert.Contains("Gone", second.Message);

            var back = b.Services.Actions.Execute(ShowActionKind.PresenterPrev, ActionOrigin.Clicker);
            Assert.True(back.Ok);
            Assert.Equal(0, rt.CurrentIndex); // and back over it the other way

            // Nothing runnable ahead: refused, with the reason — the place stays put.
            state.LooksAndCues.Looks.Clear();
            var stuck = b.Services.Actions.Execute(ShowActionKind.PresenterNext, ActionOrigin.Clicker);
            Assert.False(stuck.Ok);
            Assert.Equal(0, rt.CurrentIndex);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheJournalNamesLooksAndTheScheduleSaysWhichCueFired()
    {
        var b = TestApp.Boot();
        try
        {
            var look = new LookConfig { Name = "Walk-in", Json = LookService.Capture(b.Vm.State) };
            b.Vm.State.LooksAndCues.Looks.Add(look);

            var desk = b.Services.Actions.ApplyLook(look, ActionOrigin.Desk);
            Assert.True(desk.Ok);
            var entry = b.Services.Journal.Tail(1).Single();
            Assert.Equal("ApplyLook", entry.Kind);
            Assert.Equal("Walk-in", entry.Target); // the name, never the id

            var cue = b.Services.Actions.ApplyLook(look, ActionOrigin.Schedule, note: "cue 18:00");
            Assert.True(cue.Ok);
            Assert.StartsWith("Cue 18:00:", cue.Message);
            Assert.Contains("Walk-in", cue.Message);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AHeldTransportKeyOnTheDeskActsOnce()
    {
        var b = TestApp.Boot();
        try
        {
            Assert.False(b.Vm.State.Blackout);

            // Avalonia reports no repeat flag: a held Shift+F8 arrives as a stream of KeyDowns.
            b.Window.KeyPress(Key.F8, RawInputModifiers.Shift, PhysicalKey.F8, null);
            b.Window.KeyPress(Key.F8, RawInputModifiers.Shift, PhysicalKey.F8, null);
            b.Window.KeyPress(Key.F8, RawInputModifiers.Shift, PhysicalKey.F8, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Vm.State.Blackout); // toggled once, not three times

            b.Window.KeyRelease(Key.F8, RawInputModifiers.Shift, PhysicalKey.F8, null);
            b.Window.KeyPress(Key.F8, RawInputModifiers.Shift, PhysicalKey.F8, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Vm.State.Blackout); // a new press acts again
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AnOlderShowFileIsWrittenBackOnceAtStartupSoItsNewIdsStick()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-boot-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir);
        var old = new ShowState { SchemaVersion = 3 };
        old.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in", Json = LookService.Capture(old) });
        var json = System.Text.RegularExpressions.Regex.Replace(JsonUtil.Serialize(old),
            @"(,)?\s*""Id"": ""[0-9a-fA-F]{32}""(,)?", m => m.Groups[1].Success && m.Groups[2].Success ? "," : "");
        File.WriteAllText(store.SettingsPath, json);

        var services = new AppServices(store);
        try
        {
            var id = services.State.LooksAndCues.Looks[0].Id;
            Assert.Equal(32, id.Length);
            var onDisk = new SettingsStore(dir).LoadFrom(store.SettingsPath);
            Assert.NotNull(onDisk);
            Assert.Equal(ShowState.CurrentSchemaVersion, onDisk!.SchemaVersion);
            Assert.Equal(id, onDisk.LooksAndCues.Looks[0].Id); // the same id next time
        }
        finally
        {
            services.Shutdown();
        }
    }
}
