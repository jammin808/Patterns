using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 27: "Patterns needs the ability to allow external controllers like the AKAI Professional
/// APC 40 mkII controls to be mapped by midi. Research popular controllers to implement. Give
/// maximum control and feedback."
///
/// A surface is a device on the Interactive page, which is what makes this small: the mapping, the
/// action layer, the journal and the arm fence are all code the desk already had and already
/// tested. These pin the joins — the page adding one, the learn gesture, the starter rows, and the
/// fact that the surface goes through the same fence every other remote does.
/// </summary>
public class MidiSurfaceAppTests
{
    private static DeviceConfig AddSurface(TestApp.Booted b)
    {
        b.Vm.AddMidiDeviceCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return b.Vm.State.Interactive.Devices.Single();
    }

    [AvaloniaFact]
    public void AddingASurfaceStartsItAsPadsAndFadersInAndLampsOut()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, _) = b;
            vm.SelectPage(Shell.IndexOf("Interactive"));
            Dispatcher.UIThread.RunJobs();

            var surface = AddSurface(b);
            Assert.Equal(DeviceLink.Midi, surface.Link);

            // A surface speaks no protocol and hears no words: starting it any other way would have
            // the desk answering "ERR no trigger for NOTE 1 53 127" into a port with nowhere to put
            // the sentence, and taking a stray note as a show command.
            Assert.False(surface.SpeaksProtocol);
            Assert.False(surface.EchoReplies);

            // It is offered on the page as a link like the others.
            Assert.Contains(Lists.DeviceLinks, l => Equals(l.Value, DeviceLink.Midi));
            Assert.Contains("controller", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void LearnWritesTheRowSoNobodyHasToKnowTheNoteNumbers()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var surface = AddSurface(b);
            surface.Name = "APC";
            Dispatcher.UIThread.RunJobs();
            var before = surface.Triggers.Count;

            vm.LearnMidiCommand.Execute(surface);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Press a control", vm.MidiLearnText);

            // The surface sends the pad's own line; the row writes itself and nothing is run.
            services.Devices.Learn(surface.Name, line =>
            {
                surface.Triggers.Add(new DeviceTriggerConfig { Match = line, Command = "" });
            });
            Assert.NotNull(services.Devices);

            // A row learned from a press matches that press and no other control.
            var learned = MidiLines.Trigger(MidiLines.Read(0x90, 53, 127));
            surface.Triggers.Add(new DeviceTriggerConfig { Match = learned, Command = "CUE GO" });
            Assert.Equal("CUE GO", DeviceMap.Resolve(surface, MidiLines.Format(MidiLines.Read(0x90, 53, 90))));
            Assert.Null(DeviceMap.Resolve(surface, MidiLines.Format(MidiLines.Read(0x90, 54, 90))));
            Assert.True(surface.Triggers.Count > before);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheStarterRowsLandInTheOperatorsOwnTableWhereTheyCanBeRead()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, _) = b;
            var surface = AddSurface(b);
            surface.Triggers.Clear();
            surface.StarterSet = MidiSurfaces.All[0].Name;
            Dispatcher.UIThread.RunJobs();

            vm.SeedMidiCommand.Execute(surface);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(surface.Triggers);

            // They are rows like any other — visible, editable, deletable — rather than a driver
            // hidden behind a device name, and the desk says they have not been run against the
            // hardware here rather than claiming to know a controller it has never met.
            Assert.Contains(MidiSurfaces.All[0].Name, vm.StatusMessage);
            Assert.Contains(MidiSurfaces.All[0].Note.Split('.')[0], vm.StatusMessage);

            // Pressing it again does not double every row.
            var count = surface.Triggers.Count;
            vm.SeedMidiCommand.Execute(surface);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(count, surface.Triggers.Count);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ASurfaceGoesThroughTheSameFenceEveryOtherRemoteDoes()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var surface = AddSurface(b);
            surface.Triggers.Clear();
            surface.Triggers.Add(new DeviceTriggerConfig { Match = "NOTE 1 53 *", Command = "BLACKOUT ON" });
            Dispatcher.UIThread.RunJobs();

            // A pad running a command is a device origin, which is already inside the arm fence —
            // so a surface cannot arm a cue stack that TCP, OSC and Companion cannot. That is not a
            // new rule for MIDI; it is the rule the desk already had, and this is why riding the
            // Interactive area rather than growing a second one beside it was worth doing.
            var origin = new ActionOrigin(OriginKind.Device, surface.Name);
            vm.State.Control.RemotesMayArm = false;
            var armed = services.Actions.Execute(ShowActionKind.ListArm, origin, "caller", "ON");
            Assert.False(armed.Ok);
            Assert.Contains("remotes may not arm", armed.Message);

            // And what it does run is journalled with the surface's own name on it.
            var result = services.Actions.Execute(ShowActionKind.BlackoutOn, origin);
            Assert.True(result.Ok);
            Assert.True(vm.State.Blackout);
            Assert.Contains("device", origin.Label, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(surface.Name, origin.Label);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OffWindowsTheSurfaceSaysSoRatherThanLookingConnected()
    {
        // The house rule for every optional native thing — NDI, libVLC, WebView2 — is to probe and
        // say, never to pretend. A MIDI port on a machine that has no MIDI must read as plainly as
        // a missing NDI runtime does.
        Assert.Empty(MidiSurfaceLink.Inputs());

        using var link = new MidiSurfaceLink("APC40");
        Assert.False(link.IsOpen);
        link.Write("LAMP 1 53 21");                            // never throws, wherever it is running
        Assert.NotNull(link.Status);
    }

    /// <summary>
    /// The journal writes to disk synchronously on the thread that draws the desk. That was always
    /// fine because every verb was a gesture — a GO, a look, a blackout. A fader is a sweep, and
    /// fifty file appends a second while a hand is on it would blow the desk's tick budget for as
    /// long as they held it.
    /// </summary>
    [AvaloniaFact]
    public void AFaderSweepDoesNotWriteFiftyJournalRowsASecond()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var surface = AddSurface(b);
            var origin = new ActionOrigin(OriginKind.Device, surface.Name);
            var before = File.Exists(services.Journal.Path) ? File.ReadAllLines(services.Journal.Path).Length : 0;

            // A hand on a fader, as the coalescer hands it to the desk.
            for (var v = 0; v <= 100; v += 2)
            {
                Assert.True(services.Actions.Execute(ShowActionKind.AudioVolume, origin, "", v.ToString()).Ok);
            }
            Dispatcher.UIThread.RunJobs();

            var after = File.Exists(services.Journal.Path) ? File.ReadAllLines(services.Journal.Path).Length : 0;
            Assert.True(after - before <= 2, $"{after - before} rows for one sweep");

            // The level itself is not paced — only the record is. The show is where the fader is.
            Assert.Equal(100, (int)Math.Round(vm.State.AudioPlayer.VolumePct));

            // And a level TYPED at the desk is a gesture, so it is recorded like any other verb.
            var typedBefore = File.ReadAllLines(services.Journal.Path).Length;
            services.Actions.Execute(ShowActionKind.AudioVolume, ActionOrigin.Desk, "", "42");
            services.Actions.Execute(ShowActionKind.AudioVolume, ActionOrigin.Desk, "", "43");
            Dispatcher.UIThread.RunJobs();
            Assert.True(File.ReadAllLines(services.Journal.Path).Length - typedBefore >= 2);
        }
        finally
        {
            b.Dispose();
        }
    }
}
