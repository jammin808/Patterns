using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Audio;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 71: the desk never asks Windows for its audio devices on its own thread. The tick's audio
/// area, the right-click menus, the pickers, the routing table and the health facts read the
/// catalogue; a change in the catalogue reaches every list on the next tick.
/// </summary>
public class AudioEndpointsAppTests
{
    [AvaloniaFact]
    public void TheTickReadsTheCatalogueNeverTheMachineAndEveryListFollowsAChange()
    {
        var b = TestApp.Boot();
        try
        {
            var catalogue = b.Services.AudioEndpoints;
            Assert.True(SpinWait.SpinUntil(() => catalogue.Reads >= 1, TimeSpan.FromSeconds(5)), "the start's read never came");
            var reads = catalogue.Reads;

            // The machine, as a test says it is.
            var render = new List<AudioEndpoint> { new("{r}.a", "Test Speakers"), new("{r}.b", "Test HDMI") };
            var capture = new List<AudioEndpoint> { new("{c}.a", "Test Mic") };
            catalogue.Read = () => (render, capture);
            Assert.True(catalogue.ReadNow("test"));
            reads++;

            // A sound-reactive pattern on the desk: the case that asked Windows every second.
            b.Vm.State.Pattern.Kind = PatternKind.Fractal;
            for (var i = 0; i < 60; i++) b.Vm.PollNow();
            Assert.Equal(reads, catalogue.Reads);                                                    // sixty ticks, not one read of the machine
            Assert.DoesNotContain("fault", b.Vm.DeskTickText, StringComparison.OrdinalIgnoreCase);   // every area of the tick ran

            Assert.Contains("Test Mic", b.Vm.Audio.CaptureDevices);                                  // the fractal's input picker
            Assert.Contains("Test Speakers", b.Vm.MonitorDevices);                                   // the monitor picker
            Assert.Contains(b.Vm.Audio.Devices, d => d.Name == "Test HDMI");                         // the track player's outputs
            Assert.Contains(b.Vm.Screens.AudioOutputChoices, c => c.Label.Contains("Test Speakers", StringComparison.Ordinal));   // a screen's sound output
            var screenId = b.Services.State.Output.Placements.Select(p => p.ScreenId).FirstOrDefault() ?? b.Services.Screens.All.Select(i => i.Id).First();
            var menu = DeskMenuFacts.Screen(b.Services, screenId);                                   // a screen tile's menu (the programme's has no sound output of its own)
            Assert.Contains(menu.SoundChoices, c => c.Label.Contains("Test HDMI", StringComparison.Ordinal));                       // the right-click menu's choices
            Assert.Equal(reads, catalogue.Reads);                                                    // none of them asked the machine either

            // A device leaves: the next tick moves every list, and the facts say what happened.
            render.RemoveAt(1);
            Assert.True(catalogue.ReadNow("device removed"));
            b.Vm.PollNow();
            Assert.DoesNotContain(b.Vm.Audio.Devices, d => d.Name == "Test HDMI");
            Assert.Contains("Test Speakers", b.Vm.MonitorDevices);
            var facts = b.Services.Metrics.GatherFacts();
            Assert.Contains("1 output · 1 input", facts.AudioEndpointWords, StringComparison.Ordinal);
            Assert.Contains("(device removed)", facts.AudioEndpointWords, StringComparison.Ordinal);
            Assert.Equal(OperatingSystem.IsWindows() ? 1 : -1, facts.AudioOutputDevices);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARefreshAskedForIsANudgeNotAWait()
    {
        var b = TestApp.Boot();
        try
        {
            var catalogue = b.Services.AudioEndpoints;
            Assert.True(SpinWait.SpinUntil(() => catalogue.Reads >= 1, TimeSpan.FromSeconds(5)), "the start's read never came");
            var nudges = catalogue.Nudges;
            var reads = catalogue.Reads;
            b.Vm.Audio.RefreshCaptureDevicesCommand.Execute(null);
            Assert.Equal(nudges + 1, catalogue.Nudges);
            Assert.Equal(reads, catalogue.Reads);                                                    // the read is the worker's, after the debounce — not the button's
            Assert.True(SpinWait.SpinUntil(() => catalogue.Reads > reads, TimeSpan.FromSeconds(5)), "the asked-for read never came");
        }
        finally
        {
            b.Dispose();
        }
    }
}
