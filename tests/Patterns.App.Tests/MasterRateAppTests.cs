using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 80: the master rate in force reaches every reader on the desk — the output windows' pacing,
/// the facts' target and the Super Check row, STATE, the Screens page's switch and words, the Eye's
/// desk words and the assistant's facts — and the switch turns it off and on with the words following.
/// </summary>
public class MasterRateAppTests
{
    [AvaloniaFact]
    public void TheRateInForceReachesEveryReaderAndTheSwitchTurnsIt()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            // The headless display, as the desk adopted it at boot, last seen at 50 Hz under a 60 fps show.
            var output = services.State.Output;
            output.MasterFps = 60;
            Assert.NotEmpty(output.Placements);
            var lobby = output.Placements.First();
            lobby.Enabled = true;
            lobby.CustomLabel = "Lobby";
            lobby.DisplayHz = 50;

            // The rule: 50 leads, and the words name it.
            var master = Rig.MasterRate(services.State, services.Screens.All);
            Assert.Equal(50, master.Effective);
            Assert.Equal("50 fps — following Lobby (50 Hz); set 60", master.Words);

            // STATE, at the end of its row.
            var json = new CommandRouter(services).StateJson();
            Assert.Contains("\"masterFps\":60", json);
            Assert.Contains("\"masterFollows\":true", json);
            Assert.Contains("\"masterFpsEffective\":50", json);
            Assert.Contains("\"masterRate\":\"50 fps", json);

            // The facts: the target every rate row reads, and the row itself.
            var facts = services.Metrics.GatherFacts();
            Assert.Equal(50, facts.TargetFps);
            Assert.True(facts.Master!.Followed);
            Assert.Equal(CheckLight.Green, SuperCheck.Run(facts).Rows.Single(r => r.Item == "Master rate").Light);

            // The Screens page, the Eye and the assistant's facts say the same.
            Assert.True(vm.Screens.FollowDisplays);
            Assert.Equal("Master rate 50 fps — following Lobby (50 Hz); set 60.", vm.Screens.MasterRateWords);
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find(EyeGraph.DeskId)!.Words, w => w == "Master rate 50 fps — following Lobby (50 Hz); set 60");
            Assert.Equal("50 fps — following Lobby (50 Hz); set 60", services.GatherFacts().MasterRate);

            // The outputs pace to the rate in force: the viewports built for the windows carry 50, not 60.
            vm.GoCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Outputs.IsLive);
            Assert.All(services.Outputs.Windows, w => Assert.Equal(50, w.Pipeline.Viewport.MasterFps));

            // The switch off: the setting stands, the display is named as over-asked, every reader follows.
            vm.Screens.FollowDisplays = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(output.FollowDisplays);
            Assert.Equal("Master rate 60 fps — Lobby refreshes at 50 Hz and is not followed.", vm.Screens.MasterRateWords);
            json = new CommandRouter(services).StateJson();
            Assert.Contains("\"masterFollows\":false", json);
            Assert.Contains("\"masterFpsEffective\":60", json);
            facts = services.Metrics.GatherFacts();
            Assert.Equal(60, facts.TargetFps);
            Assert.Equal(CheckLight.Amber, SuperCheck.Run(facts).Rows.Single(r => r.Item == "Master rate").Light);
            Assert.All(services.Outputs.Windows, w => Assert.Equal(60, w.Pipeline.Viewport.MasterFps));
        }
        finally
        {
            services.Shutdown();
        }
    }
}
