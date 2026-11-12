using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.Menus;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The Eye's cost and marks on the desk (the plan's P94.2): the facts read once a second rebuild nothing when only what runs on
/// moved, each rebuild's cost reaches the metrics sample; the canvas walks the focus once per revision and focus however often it
/// draws, skips the links that miss its view and draws every light's mark; and two links between one pair are two rows on the
/// side card, each with its verb and mark.
/// </summary>
public sealed class EyeCostAppTests
{
    private const string AppId = "0a1b2c3d";
    private const string Timer = "app:" + AppId + ":timer";
    private const string Lobby = "app:" + AppId + ":lobby";

    /// <summary>A PlayMap whose timer and lobby are joined twice — feeds one way, hears the other — beside a dozen outputs.</summary>
    private static EyeShowApp Joined(long rev, string headline = "Stage left") => new(AppId, "PlayMap", "Stage left", ShowBusState.Live, false, "")
    {
        ContextRev = rev,
        Context = new ShowContext
        {
            Node = new ShowContextNode { Kind = "playmap", Id = AppId, Name = "Stage left" },
            Light = "green",
            Headline = headline,
            Items = new[]
            {
                new ShowContextItem { Id = "timer", Kind = "timer", Label = "Keynote", Light = "green", State = "12:30 left" },
                new ShowContextItem { Id = "lobby", Kind = "screen", Label = "Lobby", Light = "amber", State = "not seen" },
            }.Concat(Enumerable.Range(1, 8).Select(i => new ShowContextItem { Id = $"out{i}", Kind = "output", Label = $"Out {i}", Light = "green", State = "60 fps" })).ToList(),
            Links = new[]
            {
                new ShowContextLink { From = "timer", To = "lobby", Kind = "feeds", Light = "green" },
                new ShowContextLink { From = "lobby", To = "timer", Kind = "hears", Light = "amber" },
            },
        },
    };

    [AvaloniaFact]
    public void AFactsTickThatMovesNothingTheEyeDrawsRebuildsNothingAndARebuildsCostReachesTheMetrics()
    {
        var b = TestApp.Boot();
        var (services, _, _) = b;
        try
        {
            var rev = 1L;
            var headline = "Stage left";
            services.Eye.AppsOverride = () => new[] { Joined(rev, headline) };
            services.Eye.Refresh();
            var graph = services.Eye.Graph;
            var builds = services.Eye.Cost.Total;
            Assert.True(builds >= 1);
            Assert.Equal(graph.Nodes.Count, services.Eye.Cost.Last!.Value.Nodes);
            Assert.Equal(graph.Edges.Count, services.Eye.Cost.Last!.Value.Edges);

            // The context's words move under the same revision: the key reads the revision, so nothing is rebuilt.
            headline = "Stage left — moved";
            Assert.False(services.Eye.Refresh());
            Assert.Same(graph, services.Eye.Graph);
            Assert.Equal(builds, services.Eye.Cost.Total);
            // Its receiver keeps a new revision: one rebuild, measured.
            rev = 2;
            Assert.True(services.Eye.Refresh());
            Assert.Equal(builds + 1, services.Eye.Cost.Total);
            Assert.Contains(services.Eye.Graph.Nodes, n => n.Id == "app:" + AppId && n.Sub == headline);

            // The metrics sample carries the minute's rebuilds — the slowest, the count, the size — and the file's line ends with them.
            services.Metrics.Poll();
            var sample = services.Metrics.Current!;
            Assert.True(sample.EyeBuilds >= 2, sample.EyeBuilds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(sample.EyeBuildWorstMs >= 0);
            Assert.Equal(services.Eye.Graph.Nodes.Count, sample.EyeNodes);
            Assert.EndsWith("," + sample.EyeNodes.ToString(System.Globalization.CultureInfo.InvariantCulture), MetricsCsv.Line(sample), StringComparison.Ordinal);
        }
        finally
        {
            services.Eye.AppsOverride = null;
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheCanvasWalksOncePerRevisionAndFocusCullsWhatItsViewMissesAndDrawsEveryMark()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        Window? window = null;
        try
        {
            services.Eye.AppsOverride = () => new[] { Joined(1) };
            services.Eye.Refresh();
            var canvas = new EyeCanvas { DataContext = vm };
            window = new Window { Width = 1000, Height = 640, Content = canvas };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var bitmap = new RenderTargetBitmap(new PixelSize(1000, 640));

            // The whole picture, fitted: a mark for every thing placed on a lens and for every link drawn — none culled.
            bitmap.Render(canvas);
            var g = services.Eye.Shown;
            var p = services.Eye.Placement;
            var placed = g.Nodes.Count(n => g.Visible(n, services.Eye.Lens) && !p.Of(n.Id).IsEmpty);
            Assert.True(canvas.EdgesDrawn > 0);
            Assert.Equal(0, canvas.EdgesCulled);
            Assert.Equal(placed + canvas.EdgesDrawn, canvas.MarksDrawn);

            // Below the label scale a thing is its mark alone — still drawn, in its geometry.
            canvas.Camera.Restore(new EyeView(EyeCamera.MinScale, 0, 0), animate: false);
            bitmap.Render(canvas);
            Assert.True(canvas.Camera.Scale < EyeLayout.LabelScale);
            var view = new EyeRect(0, 0, 1000, 640);
            var inView = g.Nodes.Count(n => g.Visible(n, services.Eye.Lens) && !p.Of(n.Id).IsEmpty && canvas.Camera.WorldToView(p.Of(n.Id)).Intersects(view));
            Assert.True(inView > 0);
            Assert.Equal(inView + canvas.EdgesDrawn, canvas.MarksDrawn);

            // Zoomed in on one thing: the links whose segments miss the view are skipped, and counted.
            var timer = p.Of(Timer);
            canvas.Camera.Restore(new EyeView(EyeCamera.MaxScale, -timer.X * EyeCamera.MaxScale + 20, -timer.Y * EyeCamera.MaxScale + 20), animate: false);
            bitmap.Render(canvas);
            Assert.True(canvas.EdgesCulled > 0, "a zoomed view skips the links it cannot show");

            // Focused: the walk is taken once for the revision and the focus, however many frames draw it.
            Assert.True(services.Eye.Focus(Timer).Ok);
            var walks = services.Eye.HopsWalks;
            for (var i = 0; i < 6; i++) bitmap.Render(canvas);
            Assert.Equal(walks + 1, services.Eye.HopsWalks);
            Assert.Equal(1, services.Eye.FocusHops![Lobby]);
            Assert.Equal(walks + 1, services.Eye.HopsWalks);
            // A new revision (a lens) or a new focus walks once more, and only once.
            Assert.True(services.Eye.SetLens("video").Ok);
            for (var i = 0; i < 3; i++) bitmap.Render(canvas);
            Assert.Equal(walks + 2, services.Eye.HopsWalks);
            Assert.True(services.Eye.Focus(Lobby).Ok);
            for (var i = 0; i < 3; i++) bitmap.Render(canvas);
            Assert.Equal(walks + 3, services.Eye.HopsWalks);
        }
        finally
        {
            window?.Close();
            services.Eye.AppsOverride = null;
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TwoLinksToOneThingAreTwoRowsOnTheSideCardAndTwoEntriesInItsMenuEachWithItsMark()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            services.Eye.AppsOverride = () => new[] { Joined(1) };
            services.Eye.Refresh();
            vm.EyeSelectCommand.Execute(Timer);
            Assert.Equal("✓", vm.EyeSelectedMark);
            var rows = vm.EyeContacts.Where(r => r.Id == Lobby).ToList();
            Assert.Equal(2, rows.Count);
            Assert.StartsWith("✓ → feeds", rows[0].Verb, StringComparison.Ordinal);
            Assert.StartsWith("△ ← hears", rows[1].Verb, StringComparison.Ordinal);
            Assert.All(rows, r => Assert.Equal("△", r.Mark));                                  // the lobby's own light, amber, as its shape

            var menu = vm.MenuFor("eye", services.Eye.Shown.Find(Timer)!)!;
            Assert.NotNull(menu.Find("eye.contact:" + Lobby));
            Assert.NotNull(menu.Find("eye.contact:" + Lobby + "#2"));
            // The rows and the menu list the same links in the same order: each LINKED TO entry's detail opens with its row's
            // mark and verb (the menu's "feeds it" is the row's "→ feeds").
            var entries = services.Eye.Shown.EdgesOf(Timer).Count;
            Assert.Equal(entries, vm.EyeContacts.Count);
            var linked = menu.Menu.Flatten().Where(e => e.Id.StartsWith("eye.contact:", StringComparison.Ordinal)).ToList();
            Assert.Equal(vm.EyeContacts.Count, linked.Count);
            Assert.Equal(vm.EyeContacts.Select(r => r.Id).ToArray(), linked.Select(e => e.Wire["EYE FOCUS ".Length..]).ToArray());
            // Mark and direction alike, row by row: the row's → is the menu's "… it", its ← the menu's "… this".
            static string Row(EyeContactRow r) => r.Verb[..1] + (r.Verb.Split(' ')[1] == "→" ? ">" : "<");
            static string Entry(MenuEntry e) => e.Detail[..1] + (e.Detail.Split(" · ")[0].EndsWith(" it", StringComparison.Ordinal) ? ">" : "<");
            Assert.Equal(vm.EyeContacts.Select(Row).ToArray(), linked.Select(Entry).ToArray());
        }
        finally
        {
            services.Eye.AppsOverride = null;
            b.Dispose();
        }
    }
}
