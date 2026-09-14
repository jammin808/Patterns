using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The routing matrix on the desk: the wire switches it on and seeds it, routes and unroutes,
/// sets a VOG's mode; STATE carries the matrix; the tone and the playlist read their outputs from
/// it; a web page's sound is steered to the device its picture is routed to, or muted when it is
/// routed nowhere; and the Audio page's rows follow the show.
/// </summary>
public class AudioRoutingAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    [AvaloniaFact]
    public void TheWireDrivesTheMatrixAndTheStateCarriesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            var state = vm.State;
            state.AudioPlayer.Devices.Add("Scarlett 2i2");
            state.Ndi.Senders.Add(new NdiSenderConfig { Id = "ndi1", Name = "Stream", Enabled = false });   // off: never a lane on this machine

            // Off: the matrix says so; on with nothing in it seeds audio-follows-video.
            Assert.False(state.AudioRouting.Enabled);
            Assert.Equal("OK", Send(router, "AUDIO ROUTING ON"));
            Assert.True(state.AudioRouting.Enabled);
            Assert.NotNull(AudioRouting.Route(state, "programme", "dev:Scarlett 2i2"));
            Assert.NotNull(AudioRouting.Route(state, "tone", "dev:Scarlett 2i2"));

            // A route by words: the computer's output is always a destination; a level rides after AT.
            Assert.Equal("OK", Send(router, "AUDIO ROUTE music TO computer AT -6"));
            var route = AudioRouting.Route(state, "music", "dev:(computer output)");
            Assert.NotNull(route);
            Assert.Equal(-6, route!.LevelDb);
            Assert.StartsWith("ERR", Send(router, "AUDIO ROUTE bagpipes TO computer"));
            Assert.StartsWith("ERR", Send(router, "AUDIO ROUTE music TO the moon"));
            Assert.Equal("OK", Send(router, "AUDIO VOG computer REPLACE"));
            Assert.Equal(AudioVogMode.Replace, AudioRouting.Row(state, "dev:(computer output)")!.VogMode);
            Assert.Equal("OK", Send(router, "AUDIO UNROUTE music FROM computer"));
            Assert.Null(AudioRouting.Route(state, "music", "dev:(computer output)"));

            // STATE carries the matrix: on, the sources, each destination with its lanes.
            var st = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("audioRouting");
            Assert.True(st.GetProperty("on").GetBoolean());
            Assert.Contains(st.GetProperty("sources").EnumerateArray(), s => s.GetProperty("id").GetString() == "music");
            var scarlett = st.GetProperty("destinations").EnumerateArray().Single(d => d.GetProperty("key").GetString() == "dev:Scarlett 2i2");
            Assert.Equal("device", scarlett.GetProperty("kind").GetString());
            Assert.Contains(scarlett.GetProperty("lanes").EnumerateArray(), l => l.GetProperty("source").GetString() == "programme" && Math.Abs(l.GetProperty("gain").GetDouble() - 1) < 1e-6);
            var computer = st.GetProperty("destinations").EnumerateArray().Single(d => d.GetProperty("key").GetString() == "dev:(computer output)");
            Assert.Equal("replace", computer.GetProperty("vogMode").GetString());

            // The tone follows the matrix: its first routed destination, at its gain; off the matrix, the programme's first output.
            AudioRouting.SetRoute(state, "tone", "dev:Scarlett 2i2", -6);
            var (device, gain) = AudioService.ToneOutput(state);
            Assert.Equal("Scarlett 2i2", device);
            Assert.Equal(Db.ToGain(-6), gain, 6);
            AudioRouting.ClearRoute(state, "tone", "dev:Scarlett 2i2");
            Assert.Equal(("", 0.0), AudioService.ToneOutput(state));
            Assert.Equal("OK", Send(router, "AUDIO ROUTING OFF"));
            Assert.Equal(("Scarlett 2i2", 1.0), AudioService.ToneOutput(state));
            Assert.False(System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("audioRouting").GetProperty("on").GetBoolean());

            // A cue's route runs through the same executor and the checks read it.
            var cue = new CueActionConfig { Kind = ShowActionKind.AudioRoute, Target = "programme", Value = "Scarlett 2i2 AT -3" };
            Assert.True(services.Actions.Execute(cue.ToAction(), new ActionOrigin(OriginKind.Cue, "01.010")).Ok);
            Assert.Equal(-3, AudioRouting.Route(state, "programme", "dev:Scarlett 2i2")!.LevelDb);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AWebPagesSoundIsSteeredWhereItsPictureIsRoutedAndMutedWhenNowhere()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = new List<FakeWebSource>();
            services.WebIn.SourceFactory = w =>
            {
                var page = new FakeWebSource(w.Target, WebEngine.ParseSize(w.Format), SKColors.Blue);
                made.Add(page);
                return page;
            };
            vm.IsSandboxActive = false;
            var state = vm.State;
            state.Pattern.Kind = PatternKind.Media;
            state.Pattern.Media.Source = MediaSource.Web;
            state.Pattern.Media.WebUrl = "https://example.com/info";
            Settle(window);
            var page = Assert.Single(made);
            Assert.Equal("", page.AudioDevice);

            // The matrix on: the programme's picture routed to an HDMI output — the page's sound goes there.
            state.AudioRouting.Enabled = true;
            AudioRouting.SetRoute(state, "programme", "dev:NVIDIA HDMI 3");
            Settle(window);
            services.WebIn.Reconcile(services.Bus.Current);
            Assert.Equal("NVIDIA HDMI 3", page.AudioDevice);
            Assert.Contains("HDMI 3", page.AudioRouteNote);
            var st = System.Text.Json.JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("audioRouting");
            Assert.Contains(st.GetProperty("pages").EnumerateArray(), p => p.GetProperty("device").GetString() == "NVIDIA HDMI 3");

            // Routed to the computer's own output and an NDI send: one output picker — the first device, the send is the mixer's.
            AudioRouting.ClearRoute(state, "programme", "dev:NVIDIA HDMI 3");
            AudioRouting.SetRoute(state, "programme", "dev:(computer output)");
            AudioRouting.SetRoute(state, "programme", "ndi:ndi1");
            services.WebIn.Reconcile(services.Bus.Current);
            Assert.Equal("", page.AudioDevice);

            // Routed nowhere: silent.
            AudioRouting.ClearRoute(state, "programme", "dev:(computer output)");
            AudioRouting.ClearRoute(state, "programme", "ndi:ndi1");
            services.WebIn.Reconcile(services.Bus.Current);
            Assert.True(page.IsMuted);

            // Off again: the machine's default, and the look's own mute stands.
            state.AudioRouting.Enabled = false;
            services.WebIn.Reconcile(services.Bus.Current);
            Assert.Equal("", page.AudioDevice);
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheAudioPagesRowsFollowTheShowAndATickWritesACrosspoint()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var state = vm.State;
            state.AudioPlayer.Devices.Add("Scarlett 2i2");
            state.Output.Placements.Add(new ScreenPlacement { ScreenId = "INFO", CustomLabel = "Info screen", UseCustomPattern = true });
            vm.Audio.RoutingOnCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(state.AudioRouting.Enabled);
            vm.Audio.RefreshRouting(force: true);
            var row = Assert.Single(vm.Audio.RoutingRows);
            Assert.Equal("Scarlett 2i2", row.Label);
            Assert.Contains(row.Cells, c => c.Source.Id == "screen:INFO" && !c.IsOn);
            Assert.Contains(row.Cells, c => c.Source.Id == "programme" && c.IsOn);

            // A tick on the info screen's cell: the crosspoint is made at 0 dB; a level typed moves it; the tick off removes it.
            var info = row.Cells.Single(c => c.Source.Id == "screen:INFO");
            info.IsOn = true;
            Assert.NotNull(AudioRouting.Route(state, "screen:INFO", "dev:Scarlett 2i2"));
            info.LevelText = "-6";
            Assert.Equal(-6, AudioRouting.Route(state, "screen:INFO", "dev:Scarlett 2i2")!.LevelDb);
            info.IsOn = false;
            Assert.Null(AudioRouting.Route(state, "screen:INFO", "dev:Scarlett 2i2"));

            // A destination added by its words, and removed with its routes.
            vm.Audio.RoutingDestinationPick = "Computer audio output";
            vm.Audio.RoutingAddDestinationCommand.Execute(null);
            Assert.Equal(2, vm.Audio.RoutingRows.Count);
            vm.Audio.RoutingRemoveDestinationCommand.Execute(vm.Audio.RoutingRows[1]);
            Assert.Single(vm.Audio.RoutingRows);
            Assert.Contains("Routing on", vm.Audio.RoutingWords);
        }
        finally
        {
            b.Dispose();
        }
    }
}
