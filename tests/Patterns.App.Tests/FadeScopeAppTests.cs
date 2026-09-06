using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15: "Fade to black should also be for a screen or a group independently (blackout
/// should be for all). This should be linked to an audio fade as well (this could be an option,
/// but by default it should be on)." On a live desk: the wire fading one screen while the rest
/// keep their picture, the rig fade taking the sound with it and the fade up bringing it back,
/// the option off, a plain blackout leaving the sound alone, STATE, the desk's picker with the
/// focus and the ticks (there without EDIT SAFE), and a cue.
/// </summary>
public class FadeScopeAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void AScreenFadesOnItsOwnAndARigFadeTakesTheSoundWithIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            var router = new CommandRouter(services);
            var clock = new DateTime(2026, 9, 6, 19, 0, 0, DateTimeKind.Utc);
            services.Stingers.NowUtc = () => clock;
            vm.State.Transition.Enabled = false;
            vm.State.Transition.DurationMs = 700;
            Assert.True(vm.State.Switcher.FadeAudioWithBlack, "the audio link is on by default");

            // FADE 1 SCREEN 2: the second screen alone goes black over a second; the blackout stays off, the sound stays up.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 1 SCREEN 2"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "b" }, services.Bus.BlackTargets);
            Assert.False(vm.State.Blackout);
            Assert.True(services.Bus.Current.IsBlack("b"));
            Assert.False(services.Bus.Current.IsBlack("a"));
            Assert.Equal(1000, services.Bus.Current.FadeOverrideMs);
            Assert.Equal(services.Bus.Current.Version, services.Bus.Current.FadeOverrideVersion);
            Assert.True(services.Bus.Current.FadesEnabled);
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(5)));
            Assert.False(services.Stingers.BlackAudioActive);
            var json = router.StateJson();
            Assert.Contains("\"black\":{\"count\":1,\"text\":\"", json);
            Assert.Contains("\"audio\":false", json);
            Assert.Contains("\"black\":true", json);                       // the screen's own row
            Assert.Contains(services.Journal.Tail(3), e => e.Kind == "FadeToBlack" && e.Origin == "tcp");

            // Again is refused; a group the wall has not got, a screen it has not got, junk words — all refused, nothing changes.
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE SCREEN 2"))));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE GROUP A"))));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 1 SCREEN 9"))));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE GROUPS"))));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE UP SCREEN 1"))));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE sideways"))));
            Assert.Equal(new[] { "b" }, services.Bus.BlackTargets);

            // A second screen by its id, then up again by its number.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE ID c"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(700, services.Bus.Current.FadeOverrideMs);   // no seconds: the show's own time
            Assert.Equal(2, services.Bus.BlackTargets.Count);
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(5)), 6);   // one screen still lit: the sound stays
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADEUP 0.5 SCREEN 3"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "b" }, services.Bus.BlackTargets);
            Assert.Equal(500, services.Bus.Current.FadeOverrideMs);

            // FADE 2 — the rig: the blackout, faded over two seconds, and the programme's sound with it.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 2"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Blackout);
            Assert.Equal(2000, services.Bus.Current.FadeOverrideMs);
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock), 6);
            Assert.Equal(0.5, services.Stingers.BlackFactorAt(clock.AddSeconds(1)), 6);
            Assert.Equal(0.0, services.Stingers.BlackFactorAt(clock.AddSeconds(2)), 6);
            Assert.True(services.Stingers.BlackAudioActive);
            Assert.True(services.Stingers.MusicRamping(clock.AddSeconds(1)));
            Assert.Equal(0.0, services.Stingers.MusicGainAt(clock.AddSeconds(3)), 6);
            Assert.Equal(0.0, services.Stingers.GainAt(AudioBus.ClipAudio, clock.AddSeconds(3)), 6);
            Assert.Equal(1.0, services.Stingers.GainAt(AudioBus.StingSound, clock.AddSeconds(3)), 6);   // a sting plays through
            Assert.Equal(1.0, services.Stingers.GainAt(AudioBus.VogSound, clock.AddSeconds(3)), 6);
            Assert.Contains("\"audio\":true", router.StateJson());
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 2"))));

            // FADE UP 1 — the rig: the blackout lifts, every screen black on its own comes back, the sound ramps up from where it is.
            clock = clock.AddSeconds(3);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADEUP 1"))));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.Blackout);
            Assert.Empty(services.Bus.BlackTargets);
            Assert.Equal(1000, services.Bus.Current.FadeOverrideMs);
            Assert.Equal(0.0, services.Stingers.BlackFactorAt(clock), 6);
            Assert.Equal(0.5, services.Stingers.BlackFactorAt(clock.AddMilliseconds(500)), 6);
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(1)), 6);
            Assert.False(services.Stingers.BlackAudioActive);
            clock = clock.AddSeconds(2);

            // The option off: the rig fades, the sound stays; the fade up leaves it alone too.
            vm.State.Switcher.FadeAudioWithBlack = false;
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 1"))));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Blackout);
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(5)), 6);
            Assert.False(services.Stingers.BlackAudioActive);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADEUP 1"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(5)), 6);
            vm.State.Switcher.FadeAudioWithBlack = true;

            // A plain BLACKOUT never touches the sound.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("BLACKOUT ON"))));
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(5)), 6);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("BLACKOUT OFF"))));
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(5)), 6);

            // A fade to black lifted by a plain BLACKOUT OFF brings the sound back over the show's transition.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 1"))));
            Dispatcher.UIThread.RunJobs();
            clock = clock.AddSeconds(2);
            Assert.Equal(0.0, services.Stingers.BlackFactorAt(clock), 6);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("BLACKOUT OFF"))));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.Blackout);
            Assert.Equal(0.5, services.Stingers.BlackFactorAt(clock.AddMilliseconds(350)), 6);
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddMilliseconds(700)), 6);
            clock = clock.AddSeconds(2);

            // Every screen faded on its own is the rig dark too: the sound goes; the first one up brings it back.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 1 SCREEN 1"))));
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 1 SCREEN 2"))));
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(5)), 6);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE 1 SCREEN 3"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, services.Bus.BlackTargets.Count);
            Assert.False(vm.State.Blackout);
            Assert.Equal(0.0, services.Stingers.BlackFactorAt(clock.AddSeconds(1)), 6);
            Assert.Contains("\"black\":{\"count\":3,", router.StateJson());
            clock = clock.AddSeconds(2);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE UP 1 SCREEN 2"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, services.Bus.BlackTargets.Count);
            Assert.Equal(1.0, services.Stingers.BlackFactorAt(clock.AddSeconds(1)), 6);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADEUP"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(services.Bus.BlackTargets);

            // The show file never carries a screen black on its own.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("FADE SCREEN 2"))));
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("BlackTargets", JsonUtil.Serialize(vm.State));
            Assert.DoesNotContain("blackTargets", JsonUtil.Serialize(vm.State));
        }
        finally
        {
            b.Dispose();
        }
    }

    private static Window Panel(MainViewModel vm)
    {
        var host = new Window { DataContext = vm, Width = 1100, Height = 2400, Content = new ScrollViewer { Content = new ShowSection() } };
        host.Show();
        Settle(host);
        return host;
    }

    [AvaloniaFact]
    public void TheDeskPicksWhereTheFadeLandsAndTheTicksAreThereWithoutEditSafe()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Settle(window);
            vm.State.Transition.Enabled = false;

            // The picker starts on every screen; WITH THE SOUND reads the show's setting, on by default.
            var host = Panel(vm);
            var picker = host.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "FadeScopePicker");
            var tick = host.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "FadeAudioTick");
            Assert.Equal(4, picker.ItemCount);
            Assert.Same(vm.FadeScopes[0], picker.SelectedItem);
            Assert.Equal("EVERY SCREEN", vm.SelectedFadeScope.ToString());
            Assert.True(tick.IsChecked);
            tick.IsChecked = false;
            Settle(host);
            Assert.False(vm.State.Switcher.FadeAudioWithBlack);
            tick.IsChecked = true;
            Settle(host);
            Assert.True(vm.State.Switcher.FadeAudioWithBlack);
            host.Close();

            // Every screen tile carries its tick with EDIT SAFE off; the program tile has none.
            var ticks = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Name == "TileTick").ToList();
            Assert.Equal(4, ticks.Count);
            foreach (var t in ticks)
            {
                var tile = (SwitcherTile)t.DataContext!;
                Assert.Equal(!tile.IsProgramTile, t.IsEffectivelyVisible);
            }
            Assert.False(vm.IsSandboxActive);

            // THE FOCUSED SCREEN: click the second tile, fade — that target alone goes black; up brings it back.
            var tiles = vm.SwitcherTiles;
            var tileB = tiles.Single(t => t.TargetId == "b");
            var tileC = tiles.Single(t => t.TargetId == "c");
            vm.SelectedFadeScope = vm.FadeScopes[1];
            vm.SelectTileCommand.Execute(tileB);
            vm.FadeSeconds = 1.5;
            vm.FadeToBlackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "b" }, services.Bus.BlackTargets);
            Assert.False(vm.State.Blackout);
            Assert.StartsWith("Fading", vm.StatusMessage);
            Assert.Equal(1500, services.Bus.Current.FadeOverrideMs);
            // The wall says so: the tile wears BLACK and its tally is off; the others keep theirs.
            vm.PollNow();
            Settle(window);
            Assert.True(tileB.IsBlack);
            Assert.False(tileB.IsOnAir);
            Assert.False(tileC.IsBlack);
            var badge = window.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("blackBadge") && ReferenceEquals(x.DataContext, tileB));
            Assert.True(badge.IsEffectivelyVisible);
            vm.FadeUpCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(services.Bus.BlackTargets);
            vm.PollNow();
            Settle(window);
            Assert.False(tileB.IsBlack);
            Assert.False(badge.IsEffectivelyVisible);

            // THE TICKED SCREENS: tick the lobby (the sandbox closed), fade — the lobby alone; the ticks stay for the fade up.
            tileC.IsSendTarget = true;
            vm.SelectedFadeScope = vm.FadeScopes[2];
            vm.FadeToBlackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "c" }, services.Bus.BlackTargets);
            Assert.True(tileC.IsSendTarget);
            vm.FadeUpCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(services.Bus.BlackTargets);
            tileC.IsSendTarget = false;
            vm.FadeToBlackCommand.Execute(null);
            Assert.Contains("Tick the wall tiles", vm.StatusMessage);

            // THE TICKED GROUPS with no canvas on the wall: refused with the reason, nothing changes.
            tileB.IsSendTarget = true;
            vm.SelectedFadeScope = vm.FadeScopes[3];
            vm.FadeToBlackCommand.Execute(null);
            Assert.Contains("Tick a group", vm.StatusMessage);
            Assert.Empty(services.Bus.BlackTargets);
            tileB.IsSendTarget = false;

            // The program tile focused means the rig: the blackout with a fade.
            vm.SelectedFadeScope = vm.FadeScopes[1];
            vm.SelectTileCommand.Execute(tiles.Single(t => t.IsProgramTile));
            vm.FadeToBlackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Blackout);
            Assert.Empty(services.Bus.BlackTargets);
            vm.FadeUpCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.Blackout);

            // A cue: Fade to black on SCREEN 2 for a second, then Fade up — the blackout transport leaves both alone.
            var stack = CueStacks.Caller(vm.State);
            var down = new RunCueConfig { Number = "1", Name = "Stage left out" };
            down.Actions.Add(new CueActionConfig { Kind = CueActionKind.FadeToBlack, Target = "SCREEN 2", Value = "1" });
            var up = new RunCueConfig { Number = "2", Name = "Stage left back" };
            up.Actions.Add(new CueActionConfig { Kind = CueActionKind.FadeUp, Target = "SCREEN 2", Value = "1" });
            stack.Cues.Add(down);
            stack.Cues.Add(up);
            Dispatcher.UIThread.RunJobs();
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            var at = new DateTime(2026, 9, 6, 20, 0, 0, DateTimeKind.Utc);
            Assert.True(services.CueStack.Go(ActionOrigin.Desk, nowUtc: at).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "b" }, services.Bus.BlackTargets);
            Assert.False(vm.State.Blackout);
            Assert.Equal(1000, services.Bus.Current.FadeOverrideMs);
            Assert.True(services.CueStack.Go(ActionOrigin.Desk, nowUtc: at.AddSeconds(2)).Ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(services.Bus.BlackTargets);
            Assert.False(vm.State.Blackout);
        }
        finally
        {
            b.Dispose();
        }
    }
}
