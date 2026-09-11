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
/// The desk's monitor bus where it actually bites: the preview. EDIT SAFE means the operator is
/// nearly always holding a second picture, and if that picture is a clip its soundtrack used to
/// play over the programme's — the one case of the overlapping mix that happens on every show.
/// </summary>
public class AudioMonitorAppTests
{
    private static ShowState Clip(string path)
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Video;
        state.Pattern.Media.VideoPath = path;
        state.Pattern.Media.Mute = false;
        return state;
    }

    private static ShowSnapshot Snap(ShowState state) => new() { State = state, Version = 1 };

    [AvaloniaFact]
    public void ThePreviewsClipIsSilentAtTheDeskUntilItIsWhatYouAreListeningTo()
    {
        var program = Clip("/clips/on-air.mp4");
        var preview = Clip("/clips/next.mp4");

        var wanted = VideoEngine.WantedVideoInputs(Snap(program), Snap(preview));
        var onAir = wanted.Single(w => w.Target == "/clips/on-air.mp4");
        var next = wanted.Single(w => w.Target == "/clips/next.mp4");
        Assert.False(onAir.Mute);
        Assert.Equal(AudioDestination.Program, onAir.Destination);
        Assert.True(next.Mute, "the clip being built is mounted for its pictures, not for its sound");

        // Listening to the preview with no output of the operator's own: the programme carries on
        // — the room's sound is not the desk's to take away — and there is nowhere to audition.
        program.Monitor.Source = AudioMonitor.Preview;
        wanted = VideoEngine.WantedVideoInputs(Snap(program), Snap(preview));
        Assert.False(wanted.Single(w => w.Target == "/clips/on-air.mp4").Mute);
        Assert.True(wanted.Single(w => w.Target == "/clips/next.mp4").Mute);

        // Name a monitor output and the preview plays there, beside a programme that never stopped.
        program.Monitor.Device = "Headphones (Realtek)";
        wanted = VideoEngine.WantedVideoInputs(Snap(program), Snap(preview));
        Assert.Equal(AudioDestination.Program, wanted.Single(w => w.Target == "/clips/on-air.mp4").Destination);
        var audition = wanted.Single(w => w.Target == "/clips/next.mp4");
        Assert.False(audition.Mute);
        Assert.Equal(AudioDestination.Monitor, audition.Destination);

        // The same clip in both: one decoder on two buses, and the room's claim on it wins — one
        // player has one device, so auditioning must never take it off the PA.
        var same = Clip("/clips/on-air.mp4");
        same.Monitor.Source = AudioMonitor.Preview;
        same.Monitor.Device = "Headphones (Realtek)";
        var one = Assert.Single(VideoEngine.WantedVideoInputs(Snap(same), Snap(Clip("/clips/on-air.mp4"))));
        Assert.False(one.Mute);
        Assert.Equal(AudioDestination.Program, one.Destination);
    }

    [AvaloniaFact]
    public void TheAudioPageSaysWhatTheDeskIsHearingAndFollowsTheChoiceAtOnce()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            window.Width = 1500;
            window.Height = 950;
            vm.State.Output.Placements.Add(new ScreenPlacement
            {
                ScreenId = "b", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, Enabled = true, CustomLabel = "Stage left",
            });
            vm.ReconcilePlacements();
            vm.SelectPage(Shell.IndexOf("Audio"));
            Dispatcher.UIThread.RunJobs();

            // The block is there, and the desk opens listening to the programme.
            var picker = window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(x => x.Name == "MonitorSource");
            Assert.NotNull(picker);
            Assert.Equal(AudioMonitor.Program, vm.State.Monitor.Source);
            Assert.Equal("PGM", vm.MonitorWord);
            Assert.Contains("The room hears the programme", vm.MonitorWords);
            Assert.False(vm.MonitorIsOutput);
            Assert.Contains("", vm.MonitorDevices);   // "none" is always offered

            // Picking an output shows its picker on the click, not on the next poll.
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            vm.State.Monitor.Source = AudioMonitor.Output;
            Assert.True(vm.MonitorIsOutput);
            Assert.Contains(nameof(vm.MonitorIsOutput), raised);
            // With no output of the operator's own there is nowhere to audition that is not the
            // room, and the line says so rather than pretending.
            Assert.Contains("Pick a monitor output", vm.MonitorWords);

            vm.State.Monitor.Device = "Headphones";
            vm.State.Monitor.OutputId = "b";
            Assert.Contains("Stage left on Headphones", vm.MonitorWords);
            Assert.Equal("STAGE LEFT", vm.MonitorWord);
            Assert.Contains(vm.MonitorOutputs, t => t.ScreenId == "b");

            vm.State.Monitor.Source = AudioMonitor.Silent;
            Assert.Equal("SILENT", vm.MonitorWord);
            Assert.False(vm.MonitorIsOutput);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// The fault that cost this feature a round: the monitor's picker is bound both ways over a
    /// list the desk rebuilds, so a list emptied for an instant dropped the selection and wrote
    /// that emptiness straight back into the show. The operator chose their headphones, the rig
    /// was reconciled a moment later, and the choice silently became "none" — which reads as the
    /// monitor being broken, not as a list having been rebuilt.
    /// </summary>
    [AvaloniaFact]
    public void TheMonitorsOutputSurvivesTheListBeingRebuiltUnderIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, _) = b;
            vm.SelectPage(Shell.IndexOf("Audio"));
            Dispatcher.UIThread.RunJobs();

            // A device this machine has not got — every show carries names from the rig it was
            // built on — is offered rather than resolved away.
            vm.State.Monitor.Source = AudioMonitor.Output;
            vm.State.Monitor.Device = "Scarlett 2i2";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Scarlett 2i2", vm.State.Monitor.Device);
            Assert.Contains("Scarlett 2i2", vm.MonitorDevices);

            // And it holds through every rebuild the desk does on its own.
            for (var i = 0; i < 5; i++)
            {
                vm.RefreshMonitorDevices();
                vm.RefreshMonitorOutputs();
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal("Scarlett 2i2", vm.State.Monitor.Device);
            Assert.Single(vm.MonitorDevices, d => d == "Scarlett 2i2");
            Assert.Single(vm.MonitorDevices, d => d.Length == 0);            // one "none", not one per rebuild
            Assert.Contains("Scarlett 2i2", vm.MonitorWords);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// A named interface that is not plugged in does not stop the show — but the desk says where
    /// the sound went instead, because an operator who is not told finds out from the room.
    /// </summary>
    [AvaloniaFact]
    public void AnInterfaceThatIsNotPluggedInIsSaidOutLoudRatherThanQuietlySubstituted()
    {
        Assert.Equal("", AudioPlayerService.MissingDeviceWords(Array.Empty<string>()));
        var one = AudioPlayerService.MissingDeviceWords(new[] { "Scarlett 2i2" });
        Assert.Contains("'Scarlett 2i2' is not plugged in", one);
        Assert.Contains("machine's own output", one);
        var two = AudioPlayerService.MissingDeviceWords(new[] { "Scarlett 2i2", "Dante Virtual" });
        Assert.Contains("'Scarlett 2i2', 'Dante Virtual' are not plugged in", two);
    }

    /// <summary>
    /// The split as the engine hands it to the decoders: the room's mounts carry a programme
    /// device, the operator's carry theirs, and the two names are never the same wire.
    /// </summary>
    [AvaloniaFact]
    public void TheEngineHandsEachMountTheDeviceItsBusBelongsTo()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.State.AudioPlayer.Devices.Clear();
            vm.State.AudioPlayer.Devices.Add("Scarlett 2i2");
            vm.State.Monitor.Source = AudioMonitor.Preview;
            vm.State.Monitor.Device = "Headphones";
            Dispatcher.UIThread.RunJobs();

            // Nothing on this machine has either endpoint, so both resolve to "the default" — but
            // the question the engine asks is per destination, and that is what the split is.
            Assert.NotNull(services.Video.DeviceFor);
            services.Video.DeviceFor!(AudioDestination.Program);
            services.Video.DeviceFor!(AudioDestination.Monitor);

            var program = Clip("/clips/on-air.mp4");
            program.Monitor.Source = AudioMonitor.Preview;
            program.Monitor.Device = "Headphones";
            var wanted = VideoEngine.WantedVideoInputs(Snap(program), Snap(Clip("/clips/next.mp4")));
            Assert.Equal(AudioDestination.Program, wanted.Single(w => w.Target == "/clips/on-air.mp4").Destination);
            Assert.Equal(AudioDestination.Monitor, wanted.Single(w => w.Target == "/clips/next.mp4").Destination);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// The soundcheck tone belongs to the room, so it goes out on the programme's own interface.
    /// A tone on the machine's speakers while the PA is on a USB card checks nothing, and the
    /// engineer at the far end of the hall is the one who finds out.
    /// </summary>
    [Fact]
    public void TheSoundcheckToneGoesOutOnTheProgrammesOwnInterface()
    {
        var state = new ShowState();
        Assert.Equal("", AudioService.ProgrammeOutputName(state));            // nothing named: the machine's own

        state.AudioPlayer.Devices.Add(AudioPlayerService.DefaultDeviceKey);
        Assert.Equal("", AudioService.ProgrammeOutputName(state));            // the machine's own, chosen on purpose

        state.AudioPlayer.Devices.Add("Scarlett 2i2");
        Assert.Equal("Scarlett 2i2", AudioService.ProgrammeOutputName(state)); // the room's wire wins over the laptop's

        state.AudioPlayer.Devices.Clear();
        state.AudioPlayer.Devices.Add("Dante Virtual");
        state.AudioPlayer.Devices.Add("Scarlett 2i2");
        Assert.Equal("Dante Virtual", AudioService.ProgrammeOutputName(state)); // one tone, one wire: the first
    }
}
