using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 15: "better visual separation between info sections, better use of subtle neon highlights
/// and subdued related backgrounds, slightly bigger headings." Every page wears its own neon — the
/// hue of its chip on the rail — on its title and on its section bands, each band a bold heading on
/// a subdued ground of the same colour; its panels' edges carry the hue faintly; the rail and the
/// page strip underline the current group and page in theirs.
/// </summary>
public class VisualSeparationTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static Color ColorOf(IBrush? brush) => brush is ISolidColorBrush s ? s.Color : Colors.Transparent;

    private static bool SameRgb(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    [AvaloniaFact]
    public void EveryPageWearsItsNeonOnItsHeadingsAndTheRailUnderlinesTheGroup()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            foreach (var page in Shell.Pages.Where(p => p.Index != Shell.RunPage))
            {
                vm.SelectPage(page.Index);
                Settle(window);
                var cls = Shell.HueClass(page.Header);
                // The settings column beside the page wears the page's hue too (its own test reads it); the page is the other one.
                var root = window.GetVisualDescendants().OfType<UserControl>().Where(u => u is not PopOutHost).SingleOrDefault(u => u.Classes.Contains(cls));
                Assert.True(root is not null, $"{page.Header} carries {cls}");
                Assert.True(root!.IsEffectivelyVisible, $"{page.Header} is the page shown");
                var hue = Color.Parse(page.Hue);

                // The title: a step bigger, in the page's neon.
                var h1 = root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("h1"));
                Assert.Equal(22, h1.FontSize);
                Assert.True(SameRgb(hue, ColorOf(h1.Foreground)), $"{page.Header}'s title in its neon");

                // Every section band: bold caps a step bigger, the neon on a subdued ground of the same hue, room inside.
                var bands = root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("h2")).ToList();
                foreach (var h2 in bands)
                {
                    Assert.Equal(14, h2.FontSize);
                    Assert.Equal(FontWeight.Bold, h2.FontWeight);
                    Assert.True(SameRgb(hue, ColorOf(h2.Foreground)), $"{page.Header}'s {h2.Text} in its neon");
                    var ground = ColorOf(h2.Background);
                    Assert.True(SameRgb(hue, ground) && ground.A > 0 && ground.A < 64, $"{page.Header}'s {h2.Text} on a subdued ground of its hue ({ground})");
                    Assert.True(h2.Padding.Left >= 8 && h2.Padding.Top >= 4, $"{page.Header}'s {h2.Text} has room inside its band");
                }

                // A panel's edge carries the hue faintly (a panel that names its own edge keeps it).
                var panels = root.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("panel")).ToList();
                foreach (var panel in panels)
                {
                    var edge = ColorOf(panel.BorderBrush);
                    if (!SameRgb(hue, edge)) continue;
                    Assert.True(edge.A > 0 && edge.A < 96, $"{page.Header}'s panel edge is a hairline of its hue, not a frame ({edge})");
                }
                if (panels.Count > 0) Assert.Contains(panels, p => SameRgb(hue, ColorOf(p.BorderBrush)));
            }

            // The rail: one group underlined, in its own hue; the strip: one page underlined, in its own.
            vm.SelectGroup(ShellGroup.Setup);
            Settle(window);
            var rail = window.GetVisualDescendants().OfType<Border>().First(x => x.Classes.Contains("rail"));
            var railLines = rail.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("hueLine")).ToList();
            Assert.Equal(Shell.Groups.Count, railLines.Count);
            var lit = Assert.Single(railLines, l => l.Opacity > 0.5);
            Assert.True(SameRgb(Color.Parse(Shell.Info(ShellGroup.Setup).Hue), ColorOf(lit.Background)));
            Assert.All(railLines.Where(l => !ReferenceEquals(l, lit)), l => Assert.Equal(0, l.Opacity));
            var chips = window.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("pageChip")).ToList();
            Assert.Equal(Shell.Pages.Count(p => p.Group == ShellGroup.Setup), chips.Count);
            var chipLines = chips.SelectMany(c => c.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("hueLine"))).ToList();
            Assert.Equal(chips.Count, chipLines.Count);
            var current = Assert.Single(chipLines, l => l.Opacity > 0.5);
            Assert.True(SameRgb(Color.Parse(Shell.Pages[vm.SelectedPageIndex].Hue), ColorOf(current.Background)));
        }
        finally
        {
            b.Dispose();
        }
    }
}
