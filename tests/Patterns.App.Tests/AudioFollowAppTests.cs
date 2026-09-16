using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 69: the sound follows the picture on the desk — a screen names its output on the wire, the
/// placement carries it, the matrix comes on, the plan follows the take, STATE and the Screens page
/// and the tile's menu read it, and the graph rebuilds its lanes when the picture moves.
/// </summary>
public class AudioFollowAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    [AvaloniaFact]
    public void AScreenNamesItsOutputAndThePictureRoutesTheSoundEverywhereTheDeskReadsIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            var state = vm.State;
            state.AudioPlayer.Devices.Add("Scarlett 2i2");
            var placement = state.Output.Placements[0];
            Assert.False(state.AudioRouting.Enabled);

            // The wire names the output: the placement carries it, the output has a row, the matrix is on and seeded, the plan follows.
            Assert.Equal("OK", Send(router, "SCREEN 1 AUDIO computer"));
            Assert.Equal("dev:(computer output)", placement.AudioOutput);
            Assert.True(state.AudioRouting.Enabled);
            Assert.NotNull(AudioRouting.Row(state, "dev:(computer output)"));
            Assert.NotNull(AudioRouting.Route(state, "programme", "dev:Scarlett 2i2"));            // seeded as AUDIO ROUTING ON would
            var followed = Assert.Single(AudioRouting.FollowedRoutes(state));
            Assert.Equal(placement.ScreenId, followed.ScreenId);
            Assert.Equal("programme", followed.Source);
            var plan = AudioRouting.PlanFor(state, "dev:(computer output)", false)!;
            Assert.True(plan.Lanes.Single(l => l.Source == "programme").Followed);
            Assert.StartsWith("ERR", Send(router, "SCREEN 1 AUDIO the moon"));
            Assert.StartsWith("ERR", Send(router, "SCREEN 9 AUDIO computer"));
            Assert.Equal("ScreenAudio", services.Journal.Tail(1).Single().Kind);

            // STATE: the screen's row and the matrix's row say so.
            var st = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            var screen = st.GetProperty("screens").EnumerateArray().First();
            Assert.Equal("dev:(computer output)", screen.GetProperty("audioOut").GetString());
            Assert.Equal("Computer audio output", screen.GetProperty("audioOutLabel").GetString());
            Assert.Equal("the programme", screen.GetProperty("audioSource").GetString());
            var routing = st.GetProperty("audioRouting");
            Assert.True(routing.GetProperty("follow").GetBoolean());
            Assert.StartsWith("Sound follows the picture on 1 screen", routing.GetProperty("followWords").GetString());
            var row = Assert.Single(routing.GetProperty("followed").EnumerateArray());
            Assert.Equal("programme", row.GetProperty("source").GetString());
            var computer = routing.GetProperty("destinations").EnumerateArray().Single(d => d.GetProperty("key").GetString() == "dev:(computer output)");
            Assert.True(computer.GetProperty("row").GetBoolean());
            Assert.Contains(computer.GetProperty("lanes").EnumerateArray(), l => l.GetProperty("source").GetString() == "programme" && l.GetProperty("followed").GetBoolean());

            // A take: the screen gets a picture of its own and its output carries that instead — no row touched.
            placement.UseCustomPattern = true;
            Assert.Equal(AudioRouting.ScreenSource(placement.ScreenId), Assert.Single(AudioRouting.FollowedRoutes(state)).Source);
            Assert.True(AudioRouting.PlanFor(state, "dev:(computer output)", false)!.Carries(AudioRouting.ScreenSource(placement.ScreenId)));
            Assert.False(AudioRouting.PlanFor(state, "dev:(computer output)", false)!.Carries("programme"));
            Assert.DoesNotContain(state.AudioRouting.Routes, r => r.Destination == "dev:(computer output)");
            placement.UseCustomPattern = false;

            // Follow off: the rows alone; on again: the picture's route is back.
            Assert.Equal("OK", Send(router, "AUDIO FOLLOW OFF"));
            Assert.False(state.AudioRouting.FollowPicture);
            Assert.Empty(AudioRouting.FollowedRoutes(state));
            Assert.Equal("OK", Send(router, "AUDIO FOLLOW ON"));
            Assert.Single(AudioRouting.FollowedRoutes(state));
            Assert.StartsWith("ERR", Send(router, "AUDIO FOLLOW LOUD"));

            // The Screens page's picker is the same verb; its words say what follows where.
            vm.Screens.SelectedPlacement = placement;
            Assert.Equal("dev:(computer output)", vm.Screens.SelectedAudioOutput);
            Assert.Contains("Computer audio output", vm.Screens.SelectedSoundWords);
            Assert.Contains("follows the picture", vm.Screens.SelectedSoundWords);
            Assert.Contains(vm.Screens.AudioOutputChoices, c => c.ScreenId == "dev:(computer output)");
            Assert.Contains(vm.Screens.AudioOutputChoices, c => c.ScreenId == "dev:Scarlett 2i2");
            vm.Screens.SelectedAudioOutput = "";
            Assert.Equal("", placement.AudioOutput);
            Assert.Equal("ScreenAudio", services.Journal.Tail(1).Single().Kind);
            Assert.Contains("none", vm.Screens.SelectedSoundWords);
            vm.Screens.SelectedAudioOutput = "dev:Scarlett 2i2";
            Assert.Equal("dev:Scarlett 2i2", placement.AudioOutput);

            // The Audio page's matrix reads the followed cell, and the follow switch is the verb.
            vm.Audio.RefreshRouting(force: true);
            var scarlett = vm.Audio.RoutingRows.Single(r => r.Row.Key == "dev:Scarlett 2i2");
            var programmeCell = scarlett.Cells.Single(c => c.Source.Id == "programme");
            Assert.True(programmeCell.IsOn);                                                     // the seed's own row
            Assert.False(programmeCell.Followed);                                               // a row stands: nothing to derive
            AudioRouting.ClearRoute(state, "programme", "dev:Scarlett 2i2");
            vm.Audio.RefreshRouting(force: true);
            programmeCell = scarlett.Cells.Single(c => c.Source.Id == "programme");
            Assert.False(programmeCell.IsOn);
            Assert.True(programmeCell.Followed);                                                // the picture makes it now
            Assert.StartsWith("Sound follows the picture on 1 screen", vm.Audio.RoutingFollowWords);
            vm.Audio.RoutingFollow = false;
            Assert.False(state.AudioRouting.FollowPicture);
            Assert.Equal("AudioFollow", services.Journal.Tail(1).Single().Kind);
            vm.Audio.RoutingFollow = true;

            // The tile's menu: the drawer names the output and offers the others on the same verb as the wire.
            var facts = DeskMenuFacts.Screen(services, placement.ScreenId);
            Assert.Equal("Scarlett 2i2", facts.SoundOut);
            Assert.Equal("the programme", facts.SoundSource);
            var menu = DeskMenus.Screen(DeskMenuFacts.Desk(services), facts);
            var drawer = menu.Find("tile.sound")!;
            Assert.Equal("Sound out — Scarlett 2i2", drawer.Text);
            Assert.True(drawer.Children.Single(c => c.Id == "tile.sound:dev:Scarlett 2i2").IsOn);
            var off = drawer.Children.Single(c => c.Id == "tile.sound:off");
            Assert.Equal("SCREEN 1 AUDIO OFF", off.Wire);
            Assert.Equal(new ShowAction(ShowActionKind.ScreenAudio, placement.ScreenId, "OFF"), off.Action);
            Assert.Equal(ShowActionKind.ScreenAudio, ControlProtocol.Parse(off.Wire).Action.Kind);
            var computerChoice = drawer.Children.Single(c => c.Id == "tile.sound:dev:(computer output)");
            Assert.Equal("SCREEN 1 AUDIO Computer audio output", computerChoice.Wire);
            Assert.Equal(ShowActionKind.ScreenAudio, ControlProtocol.Parse(computerChoice.Wire).Action.Kind);

            // The graph's lanes follow the take: its topology signature moves with the picture, and a quiet tick does not rebuild.
            var graph = services.AudioGraph!;
            graph.Reconcile();
            var rebuilds = graph.TopologyRebuilds;
            for (var i = 0; i < 3; i++) graph.Poll();
            Assert.Equal(rebuilds, graph.TopologyRebuilds);
            placement.UseCustomPattern = true;
            graph.Poll();
            Assert.True(graph.TopologyRebuilds > rebuilds, "a take that moves the screen's picture rebuilds the lanes");
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// Round 72: EDIT SAFE. The routes the picture makes read the picture the audience has — the
    /// frozen programme — never the preview's edit: a screen given its own picture in the preview
    /// keeps carrying the programme's sound, the graph's lanes do not rebuild, and STATE, the tile's
    /// menu and the Audio page say what the room hears. The send moves the picture, and only then
    /// the sound; a discard moves nothing.
    /// </summary>
    [AvaloniaFact]
    public void AnEditInThePreviewMovesNoSoundUntilItIsSent()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            var state = vm.State;
            var placement = state.Output.Placements[0];
            var id = placement.ScreenId;
            placement.AudioOutput = "dev:Test HDMI";
            state.AudioRouting.Enabled = true;
            state.AudioRouting.FollowPicture = true;
            Assert.Equal("programme", Assert.Single(AudioRouting.FollowedRoutes(services.State, services.AirState)).Source);

            var graph = services.AudioGraph!;
            graph.Reconcile();

            // EDIT SAFE opens; the screen gets its own picture in the preview alone.
            services.Sandbox.Enter();
            Assert.True(services.Sandbox.Active);
            Assert.NotSame(services.State, services.AirState);
            ContentTargets.SetOwnPattern(vm.State, id, true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AudioRouting.ScreenSource(id), AudioRouting.SourceOfScreen(services.State, id));                       // the preview would
            Assert.Equal("programme", Assert.Single(AudioRouting.FollowedRoutes(services.State, services.AirState)).Source);   // the room does
            // The graph: the side effects of the publish force a reconcile, and the plan it builds is the room's — the
            // lane carries the programme, not the preview's own picture — and the quiet ticks after it resolve nothing.
            graph.Poll();
            AudioDestinationPlan Lane() => graph.Plan.Single(p => p.Key == "dev:Test HDMI");
            Assert.True(Lane().Carries("programme"), "the lane carries what the room sees");
            Assert.False(Lane().Carries(AudioRouting.ScreenSource(id)), "a preview edit moves no lane");
            var rebuilds = graph.TopologyRebuilds;
            var quiet = graph.QuietTicks;
            for (var i = 0; i < 3; i++) graph.Poll();
            Assert.Equal(rebuilds, graph.TopologyRebuilds);
            Assert.Equal(quiet + 3, graph.QuietTicks);

            // Everything that says so reads the room: STATE, the tile's menu, the Audio page's words.
            var st = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            var routing = st.GetProperty("audioRouting");
            var row = Assert.Single(routing.GetProperty("followed").EnumerateArray());
            Assert.Equal("programme", row.GetProperty("source").GetString());
            Assert.Equal("the programme", row.GetProperty("what").GetString());
            Assert.Contains("(the programme)", routing.GetProperty("followWords").GetString());
            Assert.Equal("the programme", DeskMenuFacts.Screen(services, id).SoundSource);
            vm.Audio.RefreshRouting(force: true);
            Assert.Contains("(the programme)", vm.Audio.RoutingFollowWords);

            // The send: the picture moves, and with it the sound.
            services.Sandbox.SendAll();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AudioRouting.ScreenSource(id), Assert.Single(AudioRouting.FollowedRoutes(services.State, services.AirState)).Source);
            graph.Poll();
            Assert.True(graph.TopologyRebuilds > rebuilds, "the send moves the screen's picture, so the lanes rebuild");
            Assert.True(Lane().Carries(AudioRouting.ScreenSource(id)), "the lane carries the screen's own sound now");
            Assert.False(Lane().Carries("programme"));
            Assert.Equal("its own picture", DeskMenuFacts.Screen(services, id).SoundSource);
            st = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            Assert.Equal(AudioRouting.ScreenSource(id), Assert.Single(st.GetProperty("audioRouting").GetProperty("followed").EnumerateArray()).GetProperty("source").GetString());

            // An edit back in the preview, then discarded: the room's sound never moved.
            if (!services.Sandbox.Active) services.Sandbox.Enter();
            ContentTargets.SetOwnPattern(vm.State, id, false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AudioRouting.ScreenSource(id), Assert.Single(AudioRouting.FollowedRoutes(services.State, services.AirState)).Source);
            graph.Poll();
            Assert.True(Lane().Carries(AudioRouting.ScreenSource(id)));
            Assert.False(Lane().Carries("programme"));
            services.Sandbox.Discard();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AudioRouting.ScreenSource(id), Assert.Single(AudioRouting.FollowedRoutes(services.State, services.AirState)).Source);
            Assert.Equal(AudioRouting.ScreenSource(id), AudioRouting.SourceOfScreen(services.State, id));                       // the discard put the room's picture back in the preview
        }
        finally
        {
            b.Dispose();
        }
    }
}
