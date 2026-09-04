using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Boots the new-feature UI for real: playlist panel, looks &amp; audio tabs, feed options,
/// LED map editor and rotation/trim strip all instantiate against the live view model —
/// missing resources or broken bindings fail here, not on show night.
/// </summary>
public class NewFeatureUiTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var b = TestApp.Boot();
        return (b.Services, b.Vm, b.Window);
    }

    [AvaloniaFact]
    public void EveryNewSectionInstantiatesAgainstLiveState()
    {
        var (services, vm, window) = Boot();
        try
        {
            // Put the state into the new-feature shapes first.
            vm.ActivePattern.Media.Source = MediaSource.Playlist;
            vm.ActivePlaylistSection.Items.Add(new PlaylistItemConfig { Path = @"C:\media\a.png", ScheduledTime = "12:30" });
            vm.ActivePlaylistSection.Folders.Add(@"C:\media\walkin");
            vm.ActivePattern.Kind = PatternKind.LedWall;
            vm.ActivePattern.LedWall.UseCustomMap = true;
            vm.ActivePattern.LedWall.CustomTiles.Add(new LedTileConfig { X = 0, Y = 0, Width = 128, Height = 128 });
            vm.ActivePattern.LedWall.CustomTiles.Add(new LedTileConfig { X = 128, Y = 0, Width = 64, Height = 128 });
            vm.SelectedLedTile = vm.ActivePattern.LedWall.CustomTiles[0];
            vm.State.Overlays.Message.Enabled = true;
            vm.State.Overlays.Message.UseFeed = true;
            vm.State.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in", Hotkey = 1, Json = LookService.Capture(vm.State) });
            vm.State.LooksAndCues.Cues.Add(new CueConfig { Time = "18:00", LookName = "Walk-in" });
            vm.State.Stingers.Items.Add(new StingerItemConfig { Path = "C:/show/seats.wav", Name = "Take your seats" });
            vm.State.Stingers.Items.Add(new StingerItemConfig
            {
                Path = "C:/show/whoosh.mp4", Name = "Whoosh", Kind = StingerKind.Sting,
                After = StingerAfter.Custom, AfterTarget = vm.State.LooksAndCues.Looks[0].Id,
            });
            vm.State.Spotify.Enabled = true;
            vm.State.Spotify.Items.Add(new SpotifyItemConfig { Name = "Interval bed", Uri = "spotify:playlist:X" });
            vm.State.Spotify.Items.Add(new SpotifyItemConfig { Uri = "spotify:track:Y" });
            Dispatcher.UIThread.RunJobs();

            var host = new Window { DataContext = vm, Width = 600, Height = 900 };
            foreach (var section in new UserControl[]
                     {
                         new PatternSection(), new MediaSection(), new LooksSection(),
                         new AudioSection(), new OutputsSection(), new BrandingSection(),
                         new OverlaysSection(), new WebSection(), new ShowSection(),
                     })
            {
                host.Content = new ScrollViewer { Content = section };
                host.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                host.Hide();
            }
            host.Close();
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void LooksSaveAndRecallThroughTheVm()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            vm.State.Overlays.Message.Text = "WALK-IN";
            vm.NewLookName = "Walk-in";
            vm.NewLookHotkey = 3;
            vm.SaveLookCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var look = Assert.Single(vm.State.LooksAndCues.Looks);
            Assert.Equal("Walk-in", look.Name);
            Assert.Equal(3, look.Hotkey);

            // Operator changes everything…
            vm.ActivePattern.Kind = PatternKind.Motion;
            vm.State.Overlays.Message.Text = "CHANGED";
            Dispatcher.UIThread.RunJobs();

            // …and F3 brings the look back, snapshot included.
            Assert.True(vm.ApplyLookHotkey(3));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
            Assert.Equal("WALK-IN", vm.State.Overlays.Message.Text);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Current.State.Pattern.Kind);

            Assert.False(vm.ApplyLookHotkey(9)); // unassigned key falls through
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void SavingASecondLookStealsTheHotkey()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.NewLookName = "One";
            vm.NewLookHotkey = 5;
            vm.SaveLookCommand.Execute(null);
            vm.NewLookName = "Two";
            vm.NewLookHotkey = 5;
            vm.SaveLookCommand.Execute(null);

            Assert.Equal(0, vm.State.LooksAndCues.Looks.First(l => l.Name == "One").Hotkey);
            Assert.Equal(5, vm.State.LooksAndCues.Looks.First(l => l.Name == "Two").Hotkey);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void PlaylistItemsReorderAndRemove()
    {
        var (services, vm, window) = Boot();
        try
        {
            var items = vm.ActivePlaylistSection.Items;
            items.Add(new PlaylistItemConfig { Path = "a.png" });
            items.Add(new PlaylistItemConfig { Path = "b.png" });
            items.Add(new PlaylistItemConfig { Path = "c.png" });

            vm.MovePlaylistItemDownCommand.Execute(items[0]);          // a b c → b a c
            Assert.Equal(new[] { "b.png", "a.png", "c.png" }, items.Select(i => i.Path));

            vm.MovePlaylistItemUpCommand.Execute(items[2]);            // b a c → b c a
            Assert.Equal(new[] { "b.png", "c.png", "a.png" }, items.Select(i => i.Path));

            vm.MovePlaylistItemUpCommand.Execute(items[0]);            // top stays put
            Assert.Equal("b.png", items[0].Path);

            vm.RemovePlaylistItemCommand.Execute(items[1]);
            Assert.Equal(new[] { "b.png", "a.png" }, items.Select(i => i.Path));

            vm.ActivePlaylistSection.Folders.Add(@"C:\shows\loop");
            vm.RemovePlaylistFolderCommand.Execute(@"C:\shows\loop");
            Assert.Empty(vm.ActivePlaylistSection.Folders);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void FontListOffersBuiltInAndMapsToTheModel()
    {
        var (services, vm, window) = Boot();
        try
        {
            Assert.Equal(MainViewModel.BuiltInFontLabel, vm.FontFamilies[0]);
            Assert.Equal(MainViewModel.BuiltInFontLabel, vm.SelectedFontFamily); // empty model value

            vm.SelectedFontFamily = "Comic Sans MS";
            Dispatcher.UIThread.RunJobs(); // realized combos may push coercions back
            Assert.Equal("Comic Sans MS", vm.State.Brand.FontFamily);

            // The combo coerces list-missing values to null — that must never clear the model
            // (a show made on another machine keeps its font; rendering falls back to Inter).
            vm.SelectedFontFamily = null;
            Assert.Equal("Comic Sans MS", vm.State.Brand.FontFamily);

            vm.SelectedFontFamily = MainViewModel.BuiltInFontLabel;
            Assert.Equal("", vm.State.Brand.FontFamily); // built-in maps back to empty
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void ToneStartsOffAndTrimResetRestoresNeutral()
    {
        var (services, vm, window) = Boot();
        try
        {
            Assert.False(vm.State.Tone.Enabled); // must never auto-start

            if (vm.State.Output.Placements.Count > 0)
            {
                vm.SelectedPlacement = vm.State.Output.Placements[0];
                vm.SelectedBrightness = 60;
                vm.SelectedGamma = 1.8;
                vm.SelectedTrimB = 80;
                Assert.True(vm.State.Output.Placements[0].HasTrims);

                vm.ResetTrimsCommand.Execute(null);
                Assert.False(vm.State.Output.Placements[0].HasTrims);
            }
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }
}
