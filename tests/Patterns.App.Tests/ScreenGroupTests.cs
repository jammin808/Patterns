using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 17: "how do I allocate and change what group a screen is in?" — groups as in main
/// screens, repeaters, info desks, NDI feeds. That is the screen's role (and the feed screens),
/// set on SETUP → Screens; every wall tile now reads the group on its foot line, and a click there
/// opens the Screens page on that screen. TICKED GROUPS keeps its other meaning, the joined canvases.
/// </summary>
public class ScreenGroupTests
{
    private const string A = ScreenPlacement.PlannedIdPrefix + "a";
    private const string B = ScreenPlacement.PlannedIdPrefix + "b";
    private const string C = ScreenPlacement.PlannedIdPrefix + "c";
    private const string Ndi = "ndi:gfx";

    /// <summary>Three planned screens (the rig the app sees without hardware) and an NDI send, whose own screen the desk makes.</summary>
    private static void Rig(TestApp.Booted b)
    {
        var vm = b.Vm;
        b.Services.BulkEdit(() =>
        {
            vm.State.Output.Placements.Clear();
            foreach (var (id, label, x) in new[] { (A, "Left", 0), (B, "Right", 3000), (C, "Lobby", 6000) })
            {
                vm.State.Output.Placements.Add(new ScreenPlacement
                {
                    ScreenId = id, Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, X = x, Enabled = true, CustomLabel = label, UserPinned = true,
                });
            }
            vm.State.Ndi.Senders.Add(new NdiSenderConfig { Id = "gfx", Name = "Graphics", Width = 1280, Height = 720 });
        });
        vm.SyncVirtualScreens();
        vm.RebuildSwitcherTiles();
        Dispatcher.UIThread.RunJobs();
    }

    private static SwitcherTile Tile(MainViewModel vm, string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);

    [AvaloniaFact]
    public void EveryTileReadsItsGroupAndAClickOpensTheScreensPageOnThatScreen()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            Rig(b);
            var placements = vm.State.Output.Placements;
            placements.First(p => p.ScreenId == B).Role = ScreenRole.Confidence;
            var c = placements.First(p => p.ScreenId == C);
            c.Role = ScreenRole.Repeater;
            c.MirrorOf = A;
            vm.RebuildSwitcherTiles();
            Dispatcher.UIThread.RunJobs();

            // The foot line: the group, then the size — or what a repeater repeats; a feed screen its kind; PGM its size alone.
            Assert.Equal("MAIN · 1920×1080", Tile(vm, A).FootText);
            Assert.Equal("CONF · 1920×1080", Tile(vm, B).FootText);
            Assert.StartsWith("REP ↳ ", Tile(vm, C).FootText);
            Assert.Contains("Left", Tile(vm, C).FootText);
            Assert.Equal("NDI · 1280×720", Tile(vm, Ndi).FootText);
            var pgm = vm.SwitcherTiles.Single(t => t.IsProgramTile);
            Assert.Equal("", pgm.KindWord);
            Assert.Equal(pgm.SizeText, pgm.FootText);
            Assert.Contains("stage monitor", Tile(vm, B).GroupTip);
            Assert.Contains("SETUP → Screens", Tile(vm, B).GroupTip);
            Assert.Contains("NDI page", Tile(vm, Ndi).GroupTip);

            // On the wall the foot line is a button on every screen tile and never on PGM.
            vm.IsSandboxActive = false;
            window.Width = 1600;
            window.Height = 1000;
            Settle();
            var wall = window.GetVisualDescendants().OfType<WallView>().Single(w => w.IsEffectivelyVisible);
            var feet = wall.GetVisualDescendants().OfType<Button>().Where(x => x.Name == "TileGroup").ToList();
            Assert.Equal(vm.SwitcherTiles.Count, feet.Count);   // one per tile, PGM's hidden
            Assert.True(feet.Count >= 5, "PGM, the three planned screens and the NDI send's own screen");
            foreach (var foot in feet)
            {
                var tile = (SwitcherTile)foot.DataContext!;
                Assert.Equal(!tile.IsProgramTile, foot.IsEffectivelyVisible);
                Assert.Same(tile.OpenSetupCommand, foot.Command);
            }

            // A click: SETUP → Screens opens with that screen selected, and a role changed there reads on the tile at once.
            Tile(vm, B).OpenSetupCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Shell.IndexOf("Screens"), vm.SelectedPageIndex);
            Assert.Equal(B, vm.SelectedPlacement?.ScreenId);
            Assert.Equal(ScreenRole.Confidence, vm.SelectedRole);
            Assert.Contains("group", vm.StatusMessage);
            vm.SelectedRole = ScreenRole.Info;
            Assert.Equal("INFO · 1920×1080", Tile(vm, B).FootText);
            Assert.Equal("INFO", Tile(vm, B).RoleBadge);
            vm.SelectedRole = ScreenRole.Main;
            Assert.Equal("MAIN · 1920×1080", Tile(vm, B).FootText);
            Assert.Equal("", Tile(vm, B).RoleBadge);

            // The repeater's source changed on the page reads on its tile at once too.
            Tile(vm, C).OpenSetupCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(C, vm.SelectedPlacement?.ScreenId);
            vm.SelectedMirrorOf = B;
            Assert.StartsWith("REP ↳ ", Tile(vm, C).FootText);
            Assert.Contains("Right", Tile(vm, C).FootText);

            // PGM's foot line goes nowhere.
            var page = vm.SelectedPageIndex;
            pgm.OpenSetupCommand.Execute(null);
            Assert.Equal(page, vm.SelectedPageIndex);
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void TheHelpSaysWhatAGroupIsInBothSensesAndWhereItIsSet()
    {
        Assert.Contains("its group on the desk", HelpBodies.ScreenRoles);
        Assert.Contains("foot line", HelpBodies.ScreenRoles);
        Assert.Contains("Groups, in both senses", HelpBodies.Switcher);
        Assert.Contains("NDI, STREAM", HelpBodies.Switcher);
        var topic = HelpTopics.All.Single(t => t.Id == "screen-roles");
        Assert.Contains("groups", topic.Title);
        Assert.Contains("group", topic.Keywords);
        Assert.Contains("ndi feed", topic.Keywords);
        Assert.Contains("infodesk", topic.Keywords);
        Assert.Contains("foot line", topic.Where);
    }

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
