using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15's two Machine-page asks: the GPU and its video memory drawn as lines like CPU and
/// memory (they were one line of text), and HEALTH AT A GLANCE's bars kept inside their pills
/// (the theme's minimum width for a progress bar was wider than a tile).
/// </summary>
public class MachinePageLayoutTests
{
    private static MetricSample Sample(int i, double busy = 34, double vramUsed = 1200, double vramTotal = 8192) => new()
    {
        Utc = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        CpuAppPct = 9,
        CpuSystemPct = 22,
        RamAppMB = 640,
        RamSystemPct = 48,
        RamTotalMB = 16384,
        OutputFps = 60,
        OutputWindows = 1,
        PreviewFps = 60,
        DiskFreeGB = 90,
        Threads = 40,
        Handles = 900,
        GpuBusyPct = busy,
        VramUsedMB = vramUsed,
        VramTotalMB = vramTotal,
    };

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static AdminSection MachinePage(TestApp.Booted b)
    {
        b.Window.Width = 1420;
        b.Window.Height = 900;
        b.Vm.SelectPage(Shell.IndexOf("Machine"));
        Settle(b.Window);
        return b.Window.GetVisualDescendants().OfType<AdminSection>().First();
    }

    [AvaloniaFact]
    public void TheCardsBusyShareAndItsMemoryAreLinesLikeCpuAndMemory()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            services.Metrics.Live = false;   // the tiles read the fed samples, never a live reading of this machine
            for (var i = 0; i < 70; i++) services.Metrics.Ingest(Sample(i));
            vm.PollNow();

            Assert.StartsWith("busy 34%", vm.AdminGpuText);
            Assert.StartsWith("in use 1.2 GB of 8.0 GB (15%)", vm.AdminVramText);
            Assert.True(vm.AdminGpuSpark.Count > 10);
            Assert.True(vm.AdminVramSpark.Count > 10);
            Assert.True(vm.AdminGpuDaySpark.Count >= 2);                          // 70 samples = two 30-second averages
            Assert.True(vm.AdminVramDaySpark.Count >= 2);
            // The busy line sits a third of the way up a 0–100 scale; the memory line against the card's total, low.
            Assert.All(vm.AdminGpuSpark, p => Assert.InRange(p.Y, 0, 56));
            Assert.All(vm.AdminVramSpark, p => Assert.InRange(p.Y, 0, 56));

            // The page: the two new rows with their lines, beside the CPU, memory and rendering ones.
            var page = MachinePage(b);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "GPU");
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Video memory");
            var gpu = page.GetVisualDescendants().OfType<Polyline>().Single(p => p.Name == "GpuSpark");
            var vram = page.GetVisualDescendants().OfType<Polyline>().Single(p => p.Name == "VramSpark");
            Assert.Same(vm.AdminGpuSpark, gpu.Points);
            Assert.Same(vm.AdminVramSpark, vram.Points);
            Assert.Equal(10, page.GetVisualDescendants().OfType<Border>().Count(x => x.Classes.Contains("spark")));   // five rows, three minutes and the day
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.AdminGpuText);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.AdminVramText);

            // A card with no readings: the words say so, the lines lie flat, nothing throws.
            for (var i = 70; i < 140; i++) services.Metrics.Ingest(Sample(i, busy: -1, vramUsed: -1, vramTotal: -1));
            vm.PollNow();
            Assert.StartsWith("busy n/a", vm.AdminGpuText);
            Assert.StartsWith("in use n/a", vm.AdminVramText);
            Assert.True(vm.AdminGpuSpark.Count > 10);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheHealthTilesBarsStayInsideTheirPills()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            // The live sampler off: on a slow first test its reading of this machine (a Linux runner
            // knows its disk and little else on the first tick) landed between the feed and the read.
            services.Metrics.Live = false;
            for (var i = 0; i < 70; i++) services.Metrics.Ingest(Sample(i));
            vm.PollNow();
            var page = MachinePage(b);

            var tiles = page.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("hTile")).ToList();
            Assert.True(tiles.Count >= 10, $"{tiles.Count} tiles");
            var bars = tiles
                .SelectMany(t => t.GetVisualDescendants().OfType<ProgressBar>().Where(p => p.IsEffectivelyVisible).Select(p => (Tile: t, Bar: p)))
                .ToList();
            Assert.True(bars.Count >= 3, $"{bars.Count} bars");                     // CPU, memory, GPU, disk… carry a share
            foreach (var (tile, bar) in bars)
            {
                var title = tile.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("hTitle")).Text;
                var at = bar.TranslatePoint(new Point(0, 0), tile)!.Value;
                Assert.True(at.X >= 0 && at.X + bar.Bounds.Width <= tile.Bounds.Width + 0.5,
                    $"{title}: the bar spans {at.X:0}..{at.X + bar.Bounds.Width:0} in a pill {tile.Bounds.Width:0} wide");
                Assert.True(bar.Bounds.Width <= tile.Bounds.Width - 20, $"{title}: the bar ({bar.Bounds.Width:0}) is wider than the pill's inside ({tile.Bounds.Width - 22:0})");
                Assert.True(bar.Bounds.Width >= tile.Bounds.Width - 30, $"{title}: the bar ({bar.Bounds.Width:0}) does not fill the pill ({tile.Bounds.Width:0})");
            }
        }
        finally
        {
            b.Dispose();
        }
    }
}
