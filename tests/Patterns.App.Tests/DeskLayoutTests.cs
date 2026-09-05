using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The desk's sections resize: a divider between the page and the screens, a handle between
/// PROGRAM and PREVIEW, WIDE to reduce the screens to a strip — all remembered in the show.
/// </summary>
public class DeskLayoutTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static (double Left, double Right) Extent(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
        return (origin.X, origin.X + control.Bounds.Width);
    }

    [AvaloniaFact]
    public void TheDividersMoveTheSectionsAndTheShowRemembersThem()
    {
        var b = TestApp.Boot();
        try
        {
            var window = b.Window;
            var vm = b.Vm;
            window.Width = 1420;
            window.Height = 900;
            Settle(window);

            var splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.Classes.Contains("deskDivider"));
            var handle = window.GetVisualDescendants().OfType<Thumb>().Single(t => t.Classes.Contains("paneHandle"));
            Assert.True(splitter.IsEffectivelyVisible);
            Assert.True(handle.IsEffectivelyVisible);
            Assert.Equal(DeskLayoutConfig.DefaultEditorWidth, window.EditorColumnWidth);
            Assert.Equal(DeskLayoutConfig.DefaultProgramShare, window.ProgramShareApplied, 3);

            // The page column follows the divider, within its limits, and the show remembers it.
            window.SetEditorWidth(600);
            Settle(window);
            Assert.Equal(600, window.EditorColumnWidth);
            Assert.Equal(600, vm.State.Desk.EditorWidth);
            var pages = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "Pages");
            Assert.InRange(pages.Bounds.Width, 590, 610);
            window.SetEditorWidth(100);
            Assert.Equal(DeskLayoutConfig.MinEditorWidth, vm.State.Desk.EditorWidth);

            // The panes share their column as the handle says; the show remembers that too.
            window.SetProgramShare(0.6);
            Settle(window);
            Assert.Equal(0.6, window.ProgramShareApplied, 3);
            Assert.Equal(0.6, vm.State.Desk.ProgramShare, 3);
            window.SetProgramShare(0.95);
            Assert.Equal(DeskLayoutConfig.MaxProgramShare, vm.State.Desk.ProgramShare, 3);
            var pgm = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PgmCanvas");
            var pvw = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PreviewCanvas");
            Assert.True(pgm.Bounds.Height > pvw.Bounds.Height, $"PROGRAM {pgm.Bounds.Height:0} against PREVIEW {pvw.Bounds.Height:0} at an 80 % share");

            // WIDE: the page takes the room, the screens reduce to a strip; off again restores the width.
            window.SetEditorWidth(600);
            vm.WideWorkArea = true;
            Settle(window);
            Assert.True(window.IsWideApplied);
            Assert.True(vm.State.Desk.WideWorkArea);
            Assert.True(pages.Bounds.Width > 800, $"the page has the room ({pages.Bounds.Width:0})");
            var take = window.GetVisualDescendants().OfType<Button>().First(x => x.Content as string == "TAKE");
            Assert.True(take.IsEffectivelyVisible);
            Assert.True(Extent(take, window).Right <= window.Bounds.Width + 0.5, "the wall's TAKE is still on the window");
            vm.WideWorkArea = false;
            Settle(window);
            Assert.False(window.IsWideApplied);
            Assert.Equal(600, window.EditorColumnWidth);

            // A wide page never pushes the screens off the window: at the minimum width the show's
            // value is held back and TAKE stays inside.
            window.SetEditorWidth(1000);
            window.Width = window.MinWidth;
            Settle(window);
            Assert.Equal(1000, vm.State.Desk.EditorWidth);
            Assert.True(window.EditorColumnWidth < 1000, $"held back to {window.EditorColumnWidth:0}");
            Assert.True(Extent(take, window).Right <= window.Bounds.Width + 0.5, "TAKE runs off the window");

            // The Run layout hides the dividers with the panes.
            vm.SelectRunCommand.Execute(null);
            Settle(window);
            Assert.False(splitter.IsEffectivelyVisible);
            Assert.False(handle.IsEffectivelyVisible);

            // The layout travels with the show file, and an older file without it lays out as it always did.
            var json = JsonUtil.Serialize(vm.State);
            var back = JsonUtil.Deserialize<ShowState>(json)!;
            Assert.Equal(1000, back.Desk.EditorWidth);
            Assert.Equal(DeskLayoutConfig.MaxProgramShare, back.Desk.ProgramShare, 3);
            var older = JsonUtil.Deserialize<ShowState>("{}")!;
            Assert.Equal(DeskLayoutConfig.DefaultEditorWidth, older.Desk.EditorWidth);
            Assert.False(older.Desk.WideWorkArea);
        }
        finally
        {
            b.Dispose();
        }
    }
}
