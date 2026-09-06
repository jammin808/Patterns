using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.App.Views.Sections;
using Patterns.Core.LowerThirds;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The round-14 asks for the Lower thirds page: the design preview always in view at the top
/// while the rest of the page scrolls under it, and a person's entry showing the name and the
/// role with the company, the photo and the note folded into a drop-down.
/// </summary>
public class LowerThirdPageLayoutTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static double TopIn(Visual visual, Visual root) => visual.TranslatePoint(new Point(0, 0), root)?.Y ?? double.NaN;

    [AvaloniaFact]
    public void ThePreviewStaysAtTheTopWhileThePageScrolls()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var design = new LowerThirdDesign { Name = "Neon", Width = 1200, Height = 300 };
            design.Elements.Add(new LowerThirdElement { Name = "Bar", Kind = LowerThirdElementKind.Bar, X = 0, Y = 0, W = 1200, H = 300 });
            design.Elements.Add(new LowerThirdElement { Name = "Name", Kind = LowerThirdElementKind.Text, TextKind = LowerThirdTextKind.Custom, Text = "Jane", X = 20, Y = 20, W = 600, H = 80 });
            vm.State.LowerThirds.Designs.Add(design);
            vm.SelectedLowerThird = design;
            Assert.Equal("Neon — 2 elements", vm.LowerThirdPreviewTitle);

            // The page hosted as the desk hosts it: no scroll viewer around it — the page owns its own.
            var host = new Window { DataContext = vm, Width = 900, Height = 700, Content = new LowerThirdsSection() };
            host.Show();
            Settle(host);
            var preview = host.GetVisualDescendants().OfType<LowerThirdPreview>().Single();
            var scroll = host.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PageScroll");
            // A selected list item asks to be brought into view on the first layout, as it did under the old outer scroll viewer: start from the top.
            scroll.Offset = new Vector(0, 0);
            Settle(host);
            var designs = host.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "DESIGNS");
            Assert.DoesNotContain(preview.GetVisualAncestors(), a => a is ScrollViewer);
            Assert.Contains(designs.GetVisualAncestors(), a => ReferenceEquals(a, scroll));
            var previewTop = TopIn(preview, host);
            var designsTop = TopIn(designs, host);
            Assert.True(previewTop < designsTop, $"the preview ({previewTop:0}) sits above DESIGNS ({designsTop:0})");
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "the page below the preview is longer than the room it has, so it scrolls");

            // Scrolling the page moves DESIGNS up and leaves the preview where it is.
            scroll.Offset = new Vector(0, 300);
            Settle(host);
            Assert.Equal(previewTop, TopIn(preview, host), 0.5);
            Assert.True(TopIn(designs, host) < designsTop, "DESIGNS scrolled up under the pinned preview");

            // No design selected: the preview keeps its place and says so.
            vm.SelectedLowerThird = null;
            Settle(host);
            Assert.Equal("no design selected", vm.LowerThirdPreviewTitle);
            var hint = host.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text is not null && t.Text.StartsWith("No design selected", StringComparison.Ordinal));
            Assert.True(hint.IsEffectivelyVisible);
            Assert.Equal(previewTop, TopIn(preview, host), 0.5);
            host.Close();
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APersonShowsTheNameAndTheRoleWithTheRestInADropDown()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var jane = vm.NewEntry("Jane Doe");
            jane.Role = "Chief Executive";
            jane.Company = "Acme Ltd";
            jane.Note = "say DOH";
            Assert.Same(jane, vm.SelectedEntry);
            Assert.False(vm.EntryMoreExpanded);
            Assert.Equal("More — Acme Ltd · no photo · note", vm.EntryMoreHeader);

            var host = new Window { DataContext = vm, Width = 900, Height = 900, Content = new LowerThirdsSection() };
            host.Show();
            Settle(host);
            static bool IsCompany(TextBox t) => t.Watermark == "Empty = the brand kit's company";
            static bool IsNote(TextBox t) => t.Watermark is not null && t.Watermark.StartsWith("For you", StringComparison.Ordinal);
            var boxes = host.GetVisualDescendants().OfType<TextBox>().ToList();
            var name = boxes.Single(t => t.Watermark is not null && t.Watermark.StartsWith("Jane Doe", StringComparison.Ordinal));
            var role = boxes.Single(t => t.Watermark == "Chief Executive");
            Assert.True(name.IsEffectivelyVisible);
            Assert.True(role.IsEffectivelyVisible);
            // Folded away: the drop-down's content is not even built until it opens.
            Assert.DoesNotContain(boxes.Where(t => t.IsEffectivelyVisible), IsCompany);
            Assert.DoesNotContain(boxes.Where(t => t.IsEffectivelyVisible), IsNote);
            var expander = host.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "EntryMore");
            Assert.False(expander.IsExpanded);

            // The list row reads the name and the role, nothing more (the editor's watermarks are text blocks inside the boxes).
            static bool OutsideABox(TextBlock t) => !t.GetVisualAncestors().OfType<TextBox>().Any();
            var rows = host.GetVisualDescendants().OfType<TextBlock>().Where(t => OutsideABox(t) && t.Text is ("Jane Doe" or "Chief Executive")).ToList();
            Assert.Equal(2, rows.Count);
            Assert.DoesNotContain(host.GetVisualDescendants().OfType<TextBlock>().Where(OutsideABox), t => t.Text == "Acme Ltd");

            // Opening the drop-down shows the rest; the header follows the entry's fields.
            vm.EntryMoreExpanded = true;
            Settle(host);
            Assert.True(expander.IsExpanded);
            var opened = host.GetVisualDescendants().OfType<TextBox>().ToList();
            Assert.True(opened.Single(IsCompany).IsEffectivelyVisible);
            Assert.True(opened.Single(IsNote).IsEffectivelyVisible);
            jane.Company = "";
            jane.Photo = "/shows/jane.png";
            Assert.Equal("More — no company (the brand kit's) · photo · note", vm.EntryMoreHeader);

            // Another entry selected: the header reads that one; the drop-down keeps the desk's choice.
            var sam = vm.NewEntry("Sam Patel");
            Assert.Equal("More — no company (the brand kit's) · no photo · no note", vm.EntryMoreHeader);
            Assert.True(vm.EntryMoreExpanded);
            sam.Note = "second session";
            Assert.EndsWith("· note", vm.EntryMoreHeader);
            jane.Company = "Acme again";                                       // the old selection no longer drives the header
            Assert.EndsWith("· no photo · note", vm.EntryMoreHeader);
            host.Close();
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheDeskHostsThePageWithoutAnOuterScrollViewer()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            window.Width = 1420;
            window.Height = 900;
            vm.SelectPage(Shell.IndexOf("Lower thirds"));
            Settle(window);
            var preview = window.GetVisualDescendants().OfType<LowerThirdPreview>().First(p => p.Name == "DesignPreview");
            Assert.DoesNotContain(preview.GetVisualAncestors(), a => a is ScrollViewer);
            Assert.Contains(window.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Name == "PageScroll");
        }
        finally
        {
            b.Dispose();
        }
    }
}
