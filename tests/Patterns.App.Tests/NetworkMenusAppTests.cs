using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.Views.Controls;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 99.4 (Show Bus 1.3 §13.16.6): <i>Accept new id</i> is a person's on this desk — refused with the reason while nothing answers
/// in the Stagewatch's place, and refused from every other origin whatever waits; the Eye's menus and the Nodes card read the desk's
/// facts through the one builder; NETWORK is a lens of the page's chips.
/// </summary>
public class NetworkMenusAppTests
{
    [AvaloniaFact]
    public void AcceptNewIdIsTheDesksAloneAndRefusedWithTheReasonWhileNothingAnswersInItsPlace()
    {
        var b = TestApp.Boot();
        try
        {
            var desk = b.Services.Actions.Execute(new ShowAction(ShowActionKind.StagewatchAcceptId, "", "5e6f7a8b"), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, desk.Status);
            Assert.Contains("Nothing else answers", desk.Message, StringComparison.Ordinal);
            Assert.Equal("", b.Services.Kernel.Stagewatch.OtherId);

            foreach (var origin in new[] { new ActionOrigin(OriginKind.Tcp, "", "10.0.0.5:5000"), new ActionOrigin(OriginKind.Companion, "FOH deck") })
            {
                var off = b.Services.Actions.Execute(new ShowAction(ShowActionKind.StagewatchAcceptId, "", "5e6f7a8b"), origin);
                Assert.Equal(ActionStatus.Refused, off.Status);
                Assert.Contains(StagewatchVerbs.AcceptIdWords, off.Message, StringComparison.Ordinal);
            }
            // No line says it: the wire has no word for it, and the menus' fact says nothing waits.
            Assert.Equal("", WireWriter.Line(new ShowAction(ShowActionKind.StagewatchAcceptId, "", "5e6f7a8b")));
            Assert.Equal("", DeskMenuFacts.Desk(b.Services).Stagewatch.OtherId);

            // NETWORK is one of the page's lens chips, run through the action layer like the wire's EYE LENS.
            Assert.Contains(b.Vm.EyeLenses, c => c.Word == "network");
            b.Vm.EyeLensCommand.Execute("network");
            Assert.Equal(EyeLens.Network, b.Services.Eye.Lens);
            Assert.False(b.Vm.HasEyeEdgeSelection);
            Assert.True(b.Vm.EyeNothingSelected);
        }
        finally
        {
            b.Dispose();
        }
    }
    /// <summary>
    /// The Eye design's D4 and D13 on the canvas the desk draws: a linked Stagewatch's card known by its record, a heard PlayMap's ringed
    /// hollow; a link is hit where the canvas draws it and nowhere beyond its reach; a click's selection is the link's card (its title,
    /// source and two ends); a right-click on it opens the link's own menu; and the linked app's card lists
    /// its link's record under LINKS, the rows following the record's clock while the picture stands still.
    /// </summary>
    [AvaloniaFact]
    public void ALinkIsHitWhereItIsDrawnItsClickIsItsCardItsRightClickItsMenuAndALinkedCardListsItsRecord()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        Window? window = null;
        const string sw = "3f9c2a1b7d0e";
        LinkRecord Record(int samples) => new("main", sw, "Hall A", "ws", 1, "pinned", "", "mutual", "52f6", "post", "live", "", "live",
            new[] { "plain" }, "green", "✓", "Stagewatch Hall A · live", 0, 0, 0, null,
            new LinkRecordClock(12, 3, 100, samples, false), new LinkCounts(0, 0, 0, 0, 0, 0), null);
        var samples = 5;
        try
        {
            services.Eye.AppsOverride = () => new[]
            {
                new EyeShowApp(sw, "Stagewatch", "Hall A", ShowBusState.Live, true, "") { Link = Record(samples), Kind = "main" },
                new EyeShowApp("0a1b2c3d", "PlayMap", "Stage left", ShowBusState.Live, false, "") { Kind = "playmap" },
            };
            services.Eye.Refresh();
            var canvas = new EyeCanvas { DataContext = vm };
            window = new Window { Width = 1000, Height = 640, Content = canvas };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var bitmap = new RenderTargetBitmap(new PixelSize(1000, 640));
            bitmap.Render(canvas);
            Assert.True(canvas.HollowDrawn >= 1, "the heard-only PlayMap is ringed hollow");

            // The link between the desk and the linked Stagewatch, hit at the middle of the segment the canvas drew.
            var g = services.Eye.Shown;
            var p = services.Eye.Placement;
            var i = g.Edges.ToList().FindIndex(e => e.From == "app:" + sw && e.To == EyeGraph.DeskId);
            Assert.True(i >= 0, "no link from the Stagewatch's card to the desk: " + string.Join(", ", g.Edges.Select(e => e.From + ">" + e.To)));
            var edge = g.Edges[i];
            var (index, count) = g.Lane(i);
            var (x1, y1, x2, y2) = EyeGeometry.Segment(p.Of(edge.From), p.Of(edge.To), index, count);
            var mid = canvas.Camera.WorldToView((x1 + x2) / 2, (y1 + y2) / 2);
            var at = new Point(mid.X, mid.Y);
            Assert.Null(canvas.NodeAt(at));
            Assert.Same(edge, canvas.EdgeAt(at));
            Assert.NotSame(edge, canvas.EdgeAt(new Point(mid.X + 400, mid.Y + 400)));                // beyond its reach it is not this link

            // A click selects it: the side card is the link's.
            vm.EyeSelectedEdgeId = EyeMenus.EdgeSubject(g, edge);
            Assert.True(vm.HasEyeEdgeSelection);
            Assert.False(vm.HasEyeSelection);
            Assert.Contains("Hall A", vm.EyeEdgeTitle, StringComparison.Ordinal);
            Assert.Equal("a link's own record", vm.EyeEdgeSource);
            Assert.Equal(2, vm.EyeEdgeEnds.Count);

            // A right-click on it opens the link's own menu through the Eye's host (its app's groups are NetworkMenus', held in Core).
            var menu = vm.MenuFor("eye", edge);
            Assert.NotNull(menu);
            Assert.Equal(("eyeedge", EyeMenus.EdgeSubject(g, edge)), (menu!.Menu.Kind, menu.Menu.Subject));
            Assert.Contains(menu.Menu.Groups, x => x.Heading == "EYE");

            // The linked card's LINKS: its record's row, following the clock on the tick though the picture does not move.
            vm.EyeSelectedId = "app:" + sw;
            var row = Assert.Single(vm.EyeLinks);
            Assert.Equal("clock +12 ms ±3 · 5 samples", row.Clock);
            vm.PollNow();                                                                             // the picture settled on its facts
            Assert.Equal("clock +12 ms ±3 · 5 samples", Assert.Single(vm.EyeLinks).Clock);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            samples = 9;
            var rev = services.Eye.Rev;
            vm.PollNow();
            Assert.Equal(rev, services.Eye.Rev);
            Assert.Contains(nameof(vm.EyeLinks), raised);
            Assert.Equal("clock +12 ms ±3 · 9 samples", Assert.Single(vm.EyeLinks).Clock);
        }
        finally
        {
            services.Eye.AppsOverride = null;
            window?.Close();
            b.Dispose();
        }
    }
}
