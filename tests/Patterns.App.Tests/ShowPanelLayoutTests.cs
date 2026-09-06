using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15's two Show-panel asks: the ■ Stop for a VOG or stinger sits right under the chips
/// that fire them (it used to sit below the lower thirds and the people), and the wall tile's
/// OUTPUT toggle fits inside its tile (the switch it replaces hung outside).
/// </summary>
public class ShowPanelLayoutTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static Window Panel(MainViewModel vm)
    {
        var host = new Window { DataContext = vm, Width = 900, Height = 2400, Content = new ScrollViewer { Content = new ShowSection() } };
        host.Show();
        Settle(host);
        return host;
    }

    private static double Top(Control c, Visual host) => c.TranslatePoint(new Point(0, 0), host)!.Value.Y;

    [AvaloniaFact]
    public void TheStingerStopSitsUnderTheChipsAndAboveTheLowerThirds()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            vm.NewLowerThird("Neon");
            vm.NewEntry("Jane Doe");
            vm.State.Stingers.Items.Add(new StingerItemConfig { Source = StingerSource.EffectPulse, PulsePreset = PulsePreset.Strobe, PulseMs = 150, Kind = StingerKind.Sting, Name = "Strobe" });
            vm.State.Stingers.Items.Add(new StingerItemConfig { Source = StingerSource.EffectPulse, PulsePreset = PulsePreset.Flash, PulseMs = 150, Kind = StingerKind.Vog, Name = "Flash" });
            vm.PollNow();
            var host = Panel(vm);

            var stop = host.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "■ Stop" && ReferenceEquals(x.Command, vm.StopStingerCommand));
            var stingers = host.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "STINGERS");
            var lowerThirds = host.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "LOWER THIRDS");
            var people = host.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "PEOPLE");
            var stingChips = host.GetVisualDescendants().OfType<UniformGrid>().Single(g => g.Children.Count > 0 && g.Children.All(c => c.DataContext is StingerItemConfig { Kind: StingerKind.Sting }));

            Assert.True(stop.IsEffectivelyVisible);
            Assert.True(Top(stingers, host) < Top(stop, host), "the stop is under the STINGERS heading");
            Assert.True(Top(stingChips, host) < Top(stop, host), "the stop is under the stinger chips");
            Assert.True(Top(stop, host) < Top(lowerThirds, host), "the stop is above LOWER THIRDS");
            Assert.True(Top(stop, host) < Top(people, host), "the stop is above PEOPLE");
            // Right under the chips: one row of chips and a margin, nothing else in between.
            Assert.True(Top(stop, host) - (Top(stingChips, host) + stingChips.Bounds.Height) < 40, $"{Top(stop, host) - (Top(stingChips, host) + stingChips.Bounds.Height)} px between the chips and the stop");

            // The row is there without a lower third or a person too — a stinger fired from a cue or the wire still needs its stop.
            host.Close();
            vm.State.LowerThirds.Designs.Clear();
            vm.State.LowerThirds.Entries.Clear();
            host = Panel(vm);
            Assert.Single(host.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "■ Stop" && ReferenceEquals(x.Command, vm.StopStingerCommand));
            host.Close();
        }
        finally
        {
            EffectImpulses.Clear();
            b.Dispose();
        }
    }

    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    [AvaloniaFact]
    public void TheOutputToggleFitsInsideItsTileAndSwitchesTheScreen()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var fakes = ThreeScreens();
            services.Screens.All.Clear();
            foreach (var s in fakes) services.Screens.All.Add(s);
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(fakes);
            foreach (var p in vm.State.Output.Placements) p.Enabled = true;
            vm.RebuildSwitcherTiles();
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Settle(window);

            // No switch anywhere on the wall; every screen tile carries OUT inside its own bounds, the program tile none.
            Assert.Empty(window.GetVisualDescendants().OfType<ToggleSwitch>());
            var tiles = window.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("tile") && x.DataContext is SwitcherTile).ToList();
            Assert.Equal(4, tiles.Count);                                            // PGM + three screens
            foreach (var tile in tiles)
            {
                var switcherTile = (SwitcherTile)tile.DataContext!;
                var outs = tile.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Content as string == "OUT").ToList();
                Assert.Single(outs);
                var toggle = outs[0];
                if (switcherTile.IsProgramTile)
                {
                    Assert.False(toggle.IsEffectivelyVisible);
                    continue;
                }
                Assert.True(toggle.IsEffectivelyVisible, $"{switcherTile.Title}: OUT visible");
                Assert.True(toggle.IsChecked);
                var origin = toggle.TranslatePoint(new Point(0, 0), tile)!.Value;
                Assert.True(origin.X >= 0 && origin.X + toggle.Bounds.Width <= tile.Bounds.Width + 0.5,
                    $"{switcherTile.Title}: OUT spans {origin.X}..{origin.X + toggle.Bounds.Width} in a tile {tile.Bounds.Width} wide");
                Assert.True(origin.Y >= 0 && origin.Y + toggle.Bounds.Height <= tile.Bounds.Height + 0.5,
                    $"{switcherTile.Title}: OUT spans {origin.Y}..{origin.Y + toggle.Bounds.Height} in a tile {tile.Bounds.Height} tall");
                // The bottom row (OWN MON ARM LOCK and the size) fits too.
                foreach (var button in tile.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.IsEffectivelyVisible))
                {
                    var at = button.TranslatePoint(new Point(0, 0), tile)!.Value;
                    Assert.True(at.X + button.Bounds.Width <= tile.Bounds.Width + 0.5, $"{switcherTile.Title}: {button.Content} hangs outside the tile");
                }
            }

            // OUT off on the lobby: the screen's output goes off, live, and pinned; on again brings it back.
            var lobby = tiles.Single(t => ((SwitcherTile)t.DataContext!).TargetId == "c");
            var lobbyOut = lobby.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Content as string == "OUT");
            var placement = vm.State.Output.Placements.First(p => p.ScreenId == "c");
            lobbyOut.IsChecked = false;
            Settle(window);
            Assert.False(placement.Enabled);
            Assert.True(placement.UserPinned);
            Assert.False(((SwitcherTile)lobby.DataContext!).Enabled);
            lobbyOut.IsChecked = true;
            Settle(window);
            Assert.True(placement.Enabled);
        }
        finally
        {
            b.Dispose();
        }
    }
}
