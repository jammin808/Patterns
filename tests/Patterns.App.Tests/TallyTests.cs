using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Views.Sections;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The desk says what is in use: the look on air and the look in the preview light on the
/// Looks page and the Show panel, and every VOG, stinger or effect sting lights while it plays.
/// </summary>
public class TallyTests
{
    private static LookConfig Save(TestApp.Booted b, string name, PatternKind kind)
    {
        b.Vm.ActivePattern.Kind = kind;
        b.Vm.NewLookName = name;
        b.Vm.SaveLookCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return LookService.Find(b.Vm.State, name)!;
    }

    [AvaloniaFact]
    public void TheLookInUseLightsForProgramAndPreviewAndSaysWhenItWasEdited()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            var grid = Save(b, "Grid", PatternKind.Grid);
            var bars = Save(b, "Bars", PatternKind.ColorBars);

            // Nothing was recalled yet, but the picture is Bars — the tally follows the picture.
            vm.PollNow();
            Assert.True(bars.IsOnAir);
            Assert.Equal("PROGRAM", bars.TallyText);
            Assert.False(grid.IsOnAir);
            Assert.Equal("", grid.TallyText);

            vm.ApplyLook(grid);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(grid.Id, b.Services.AirLookId);
            Assert.True(grid.IsOnAir && !bars.IsOnAir);
            Assert.Equal("PROGRAM", grid.TallyText);
            Assert.True(grid.HasTally);

            // Edited on air: still that look, and the chip says so.
            vm.ActivePattern.Kind = PatternKind.Checkerboard;
            vm.PollNow();
            Assert.True(grid.IsOnAir);
            Assert.Equal("PROGRAM · EDITED", grid.TallyText);

            vm.ApplyLook(bars);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(("PROGRAM", ""), (bars.TallyText, grid.TallyText));

            // The preview: → PVW in the sandbox lights green; the program stays red on its own look.
            vm.IsSandboxActive = true;
            vm.ApplyLookToPreview(grid);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(grid.Id, b.Services.PreviewLookId);
            Assert.True(grid.IsInPreview && !grid.IsOnAir);
            Assert.Equal("PREVIEW", grid.TallyText);
            Assert.Equal("PROGRAM", bars.TallyText);
            vm.ActivePattern.Kind = PatternKind.Checkerboard;
            vm.PollNow();
            Assert.Equal("PREVIEW · EDITED", grid.TallyText);

            // TAKE: the preview's look is the program's now — edited, since the picture is a checker.
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            vm.PollNow();
            Assert.Equal(grid.Id, b.Services.AirLookId);
            Assert.Equal("", b.Services.PreviewLookId);
            Assert.True(grid.IsOnAir && !grid.IsInPreview);
            Assert.Equal("PROGRAM · EDITED", grid.TallyText);
            Assert.Equal("", bars.TallyText);

            // A preview look that is discarded goes dark; the program's tally is untouched.
            vm.IsSandboxActive = true;
            vm.ApplyLookToPreview(bars);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("PREVIEW", bars.TallyText);
            vm.IsSandboxActive = false;
            vm.PollNow();
            Assert.False(bars.IsInPreview);
            Assert.Equal("", bars.TallyText);
            Assert.Equal("PROGRAM · EDITED", grid.TallyText);

            // A playlist part replaces the look's picture: nothing is "the look on air" until one is recalled.
            vm.IsSandboxActive = false;
            vm.ActivePattern.Media.Playlist.Sections.Add(new PlaylistSectionConfig { Name = "Main" });
            b.Services.Actions.Execute(ShowActionKind.PlaylistPart, ActionOrigin.Desk, "1");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", b.Services.AirLookId);

            // The Looks page renders the chip.
            vm.ApplyLook(bars);
            Dispatcher.UIThread.RunJobs();
            var host = new Window { DataContext = vm, Width = 900, Height = 700, Content = new ScrollViewer { Content = new LooksSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var chips = host.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "PROGRAM").ToList();
            Assert.Single(chips);
            using var frame = host.CaptureRenderedFrame();
            Assert.NotNull(frame);
            host.Close();

            // The fingerprint ignores a countdown's arm time: a recalled duration countdown is still the same look.
            var state = new ShowState();
            state.Countdown.ArmedAtUtc = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
            var json = LookService.Capture(state);
            state.Countdown.ArmedAtUtc = new DateTime(2026, 9, 5, 11, 0, 0, DateTimeKind.Utc);
            Assert.True(LookService.Matches(json, state));
            state.Pattern.Kind = PatternKind.Checkerboard;
            Assert.False(LookService.Matches(json, state));
            Assert.Equal("", LookService.Fingerprint("not json"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void PlayingVogsStingersAndStingsLightTheirRowsAndChips()
    {
        var b = TestApp.Boot();
        var seats = AudioFakes.TempFile("seats.wav");
        try
        {
            var vm = b.Vm;
            vm.IsSandboxActive = false;
            vm.ActivePattern.Kind = PatternKind.Grid;
            AudioFakes.Install(b);
            var vog = new StingerItemConfig { Path = seats, Name = "Seats", Kind = StingerKind.Vog };
            var sting = new StingerItemConfig { Source = StingerSource.EffectPulse, PulsePreset = PulsePreset.Strobe, PulseMs = 150, Kind = StingerKind.Sting };
            vm.State.Stingers.Items.Add(vog);
            vm.State.Stingers.Items.Add(sting);
            EffectImpulses.Clear();

            Assert.Equal(ActionStatus.Requested, b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, vog.Id).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vog.IsOnAir);
            Assert.StartsWith("ON AIR", vog.OnAirText);
            Assert.False(vog.ShowsProgress);
            Assert.False(sting.IsOnAir);

            // The Show panel lights exactly one chip (the poll regroups the panel's chips from the library).
            vm.PollNow();
            Assert.True(vog.IsOnAir);
            var host = new Window { DataContext = vm, Width = 900, Height = 900, Content = new ScrollViewer { Content = new ShowSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var lit = host.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("air")).ToList();
            Assert.Single(lit);
            host.Close();

            b.Services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vog.IsOnAir);
            Assert.Equal("", vog.OnAirText);

            // A sting lights with its bar for its length, then goes dark by itself.
            Assert.Equal(ActionStatus.Requested, b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, sting.Id).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(sting.IsOnAir);
            Assert.True(sting.ShowsProgress);
            Assert.InRange(sting.OnAirProgress, 0, 1);
            Assert.Contains("left", sting.OnAirText);
            Assert.Equal(sting.Id, b.Services.Stingers.PulseId);
            Thread.Sleep(260);
            vm.PollNow();
            Assert.False(sting.IsOnAir);
            Assert.False(sting.ShowsProgress);

            // The Audio page renders the rows with the tally in place.
            b.Services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, vog.Id);
            Dispatcher.UIThread.RunJobs();
            var audio = new Window { DataContext = vm, Width = 900, Height = 1400, Content = new ScrollViewer { Content = new AudioSection() } };
            audio.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.Single(audio.GetVisualDescendants().OfType<TextBlock>(), t => t.Text is { } s && s.StartsWith("ON AIR"));
            audio.Close();
        }
        finally
        {
            EffectImpulses.Clear();
            b.Dispose();
            try { File.Delete(seats); } catch { }
        }
    }
}
