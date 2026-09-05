using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The wall's arming reaches every snapshot, so the multiview's NEXT / HELD badges and the remotes read the next TAKE's scope.</summary>
public class MultiviewTallyAppTests
{
    [AvaloniaFact]
    public void ArmingReachesTheSnapshotsTheBadgesAndTheRemotes()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            var target = services.Bus.Current.Rig.Targets.First();
            var tile = new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = target };
            Assert.Contains("NEXT", MultiviewTally.Badges(services.Bus.Current, tile).Select(x => x.Text));
            Assert.StartsWith("NEXT TAKE → ", MultiviewTally.PreviewTargets(services.Bus.Current));

            // Held on the wall: the program snapshot and the sandbox snapshot both carry it, without a model change.
            var version = services.Bus.Current.Version;
            services.Arming.Set(target, false);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Bus.Current.Version > version);
            Assert.Contains(target, services.Bus.Current.UnarmedTargets);
            Assert.Contains(target, services.Bus.Sandbox!.UnarmedTargets);
            Assert.Contains("HELD", MultiviewTally.Badges(services.Bus.Current, tile).Select(x => x.Text));
            Assert.Equal("NEXT TAKE → NOTHING (ALL HELD)", MultiviewTally.PreviewTargets(services.Bus.Current));
            var json = router.StateJson();
            Assert.Contains("\"editSafe\":true", json);
            Assert.Contains("\"armed\":false", json);
            Assert.Contains("\"own\":false", json);
            var fed = OscFeedback.FromState(json);
            Assert.Equal(1, Assert.Single(fed, x => x.Address == "/patterns/state/editsafe").Args[0]);
            Assert.Equal(0, Assert.Single(fed, x => x.Address == "/patterns/state/armed/1").Args[0]);

            // Armed again: everything follows, and none of it is in the show file.
            services.Arming.ArmAll();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(services.Bus.Current.UnarmedTargets);
            Assert.Contains("\"armed\":true", router.StateJson());
            Assert.DoesNotContain("UnarmedTargets", JsonUtil.Serialize(vm.State));

            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("\"editSafe\":false", router.StateJson());
            Assert.Equal("EDIT SAFE OFF", MultiviewTally.PreviewTargets(services.Bus.Current));
        }
        finally
        {
            b.Dispose();
        }
    }
}
