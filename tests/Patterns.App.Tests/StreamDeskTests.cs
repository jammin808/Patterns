using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The stream where an operator works: a command wherever they act, and one health reading the
/// rail, the page, the panel, the phone and the wire all show.
/// </summary>
public class StreamDeskTests
{
    private static string Run(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static void Destination(MainViewModel vm, string url = "rtmp://a.example/live/key")
    {
        var d = vm.State.Stream.Destinations.FirstOrDefault();
        if (d is null)
        {
            d = new StreamDestinationConfig();
            vm.State.Stream.Destinations.Add(d);
        }
        d.Enabled = true;
        d.Url = url;
    }

    [AvaloniaFact]
    public void TheRailsFootCarriesTheStreamAndOpensItsPage()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1500;
            window.Height = 950;
            Dispatcher.UIThread.RunJobs();

            var foot = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Name == "RailStream");
            Assert.NotNull(foot);

            // Dark and quiet until something is asked for.
            vm.PollNow();
            Assert.Equal("OFF", vm.StreamWord);
            Assert.False(vm.StreamOnAir);
            Assert.False(vm.StreamTrouble);

            // Asked for with nowhere to send it: the rail is the one place that says so on every
            // page, which is the whole reason it is there.
            vm.State.Stream.Active = true;
            services.Stream.Poll();
            vm.PollNow();
            Assert.Equal("NO DEST", vm.StreamWord);
            Assert.True(vm.StreamTrouble);
            Assert.Equal(StreamHealth.Read(StreamFacts.None with { Wanted = true }).Hue, vm.StreamHue);

            // And it is a way to the page, by name — a page number goes stale the next time one is added.
            vm.SelectPageByNameCommand.Execute("Stream");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Shell.IndexOf("Stream"), vm.SelectedPageIndex);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheShowPanelCanStartAndStopTheStreamWithoutLeavingThePage()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1500;
            window.Height = 950;
            vm.ShowControls.IsOpen = true;
            Dispatcher.UIThread.RunJobs();

            var start = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Name == "ShowStreamStart");
            var stop = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Name == "ShowStreamStop");
            Assert.NotNull(start);
            Assert.NotNull(stop);

            Destination(vm);
            start!.Command!.Execute(start.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Stream.Active);

            stop!.Command!.Execute(stop.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.Stream.Active);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ALookCarriesTheStreamOnAirAndNeverIntoThePreview()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            Destination(vm);

            vm.ActivePattern.Kind = PatternKind.LedWall;
            vm.NewLookName = "Doors open";
            vm.SaveLookCommand.Execute(null);
            var look = vm.State.LooksAndCues.Looks.First(l => l.Name == "Doors open");
            look.Stream = LookConfig.LookStream.Start;
            look.Hotkey = 4;
            Dispatcher.UIThread.RunJobs();

            // An F-key, the clicker list and an install's schedule all recall a look — so a look
            // that carries the stream gives the three of them a stream command at once.
            Assert.True(services.Actions.ApplyLookHotkey(4, ActionOrigin.Keyboard));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Stream.Active);

            // And the same look loaded into the preview leaves it alone: nothing that has not
            // gone to air may reach the internet.
            vm.State.Stream.Active = false;
            vm.IsSandboxActive = true;
            vm.ApplyLookToPreviewCommand.Execute(look);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.Stream.Active);

            // A look that says nothing about the stream never touches it.
            look.Stream = LookConfig.LookStream.Leave;
            vm.IsSandboxActive = false;
            vm.State.Stream.Active = true;
            Assert.True(services.Actions.ApplyLookHotkey(4, ActionOrigin.Keyboard));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Stream.Active);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheHealthTravelsOnTheWireAndTheCueVocabularyStillNamesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var router = new CommandRouter(services);
            Destination(vm);

            Assert.Equal("OK", Run(router, "STREAM ON"));
            services.Stream.Poll();
            Dispatcher.UIThread.RunJobs();

            var json = router.StateJson();
            Assert.Contains("\"active\":true", json);
            Assert.Contains("\"word\":", json);
            Assert.Contains("\"hue\":", json);
            Assert.Contains("\"health\":", json);

            // The feedback bundle a lighting desk or a TouchOSC page reads carries the word too.
            var bundle = OscFeedback.FromState(json);
            Assert.Contains(bundle, m => m.Address == "/patterns/state/stream/health");

            // And the cue vocabulary has named it all along — the picker offers what CueKinds holds.
            Assert.Contains(ShowActionKind.StreamStart, ActionSpec.CueKinds);
            Assert.Contains(ShowActionKind.StreamStop, ActionSpec.CueKinds);
            Assert.Contains(CueEditor.KindChoices, k => k.Id == nameof(ShowActionKind.StreamStart));
            Assert.Contains(CueEditor.KindChoices, k => k.Id == nameof(ShowActionKind.StreamStop));

            Assert.Equal("OK", Run(router, "STREAM OFF"));
            Assert.False(services.State.Stream.Active);
        }
        finally
        {
            b.Dispose();
        }
    }
}
