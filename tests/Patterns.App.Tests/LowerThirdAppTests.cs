using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.Views.Controls;
using Patterns.App.Views.Sections;
using Patterns.Core.LowerThirds;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The Lower thirds page and the ways a design goes on air: the desk, the sandbox, the remote, the panel, the preview.</summary>
public class LowerThirdAppTests
{
    [AvaloniaFact]
    public void TheDesignerMakesEditsAndSavesADesign()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            Assert.Empty(vm.State.LowerThirds.Designs);
            vm.NewLowerThirdPreset = "Neon";
            vm.NewLowerThirdCommand.Execute(null);
            var neon = Assert.Single(vm.State.LowerThirds.Designs);
            Assert.Same(neon, vm.SelectedLowerThird);
            Assert.Same(neon.Elements[0], vm.SelectedElement);
            Assert.True(vm.HasLowerThird);
            vm.NewLowerThirdCommand.Execute(null);
            Assert.Equal("Neon 2", vm.State.LowerThirds.Designs[1].Name); // never the same name twice
            vm.SelectedLowerThird = neon;

            // An element of every kind, a motion chip, a key, a brand word, a move, a removal.
            var text = vm.AddElement(LowerThirdElementKind.Text)!;
            Assert.Same(text, vm.SelectedElement);
            Assert.True(vm.ElementIsText);
            Assert.Equal(2, text.In.Count);
            vm.MotionInCommand.Execute("SlideLeft");
            Assert.Equal(-(neon.Width + 240), text.In[0].X);
            vm.MotionOutCommand.Execute("Wipe");
            Assert.Equal(0, text.Out[1].Reveal);
            vm.AddInKeyCommand.Execute(null);
            Assert.Equal(3, text.In.Count);
            vm.RemoveInKeyCommand.Execute(text.In[2]);
            Assert.Equal(2, text.In.Count);
            vm.ElementColorWordCommand.Execute("TextColor:accent");
            Assert.Equal("accent", text.TextColor);
            var clip = vm.AddElement(LowerThirdElementKind.Media)!;
            Assert.True(vm.ElementHasFile);
            Assert.True(vm.ElementIsMedia);
            clip.Kind = LowerThirdElementKind.Particles;   // a kind change re-reads the page's groups
            Assert.True(vm.ElementIsParticles);
            Assert.False(vm.ElementHasFile);
            var count = neon.Elements.Count;
            vm.MoveElementUpCommand.Execute(clip);
            Assert.Equal(count - 2, neon.Elements.IndexOf(clip));
            vm.RemoveElementCommand.Execute(clip);
            Assert.Equal(count - 1, neon.Elements.Count);
            Assert.NotNull(vm.SelectedElement);

            // The preview scrubs the design's own timeline: in, a 1.5 s hold, out.
            neon.InMs = 600;
            neon.OutMs = 400;
            Assert.Equal(2500, vm.PreviewLengthMs);
            vm.PreviewTimeMs = 9999;
            Assert.Equal(2500, vm.PreviewTimeMs);
            neon.HoldMs = 3000;
            Assert.Equal(4000, vm.PreviewLengthMs);
            neon.HoldMs = 0;

            // The page hosts it all and the file round trip lands a new design.
            var host = new Window { DataContext = vm, Width = 900, Height = 3000, Content = new ScrollViewer { Content = new LowerThirdsSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(host.GetVisualDescendants().OfType<LowerThirdPreview>(), p => ReferenceEquals(p.Design, neon));
            Assert.Contains(host.GetVisualDescendants().OfType<ListBox>(), l => ReferenceEquals(l.SelectedItem, neon));
            vm.SaveLowerThirdFileCommand.Execute(null);
            var file = Assert.Single(vm.LowerThirdFiles);
            Assert.Equal("Neon", file.Label);
            Assert.True(File.Exists(file.Id));
            vm.LoadLowerThirdFileCommand.Execute(file.Id);
            Assert.Equal(3, vm.State.LowerThirds.Designs.Count);
            Assert.Equal("Neon 3", vm.SelectedLowerThird!.Name);
            Assert.NotEqual(neon.Id, vm.SelectedLowerThird.Id);
            Dispatcher.UIThread.RunJobs();
            host.Close();

            // Deleting the selected design moves the selection on; deleting the one on air takes it off.
            vm.ShowLowerThird(neon);
            Assert.True(vm.State.LowerThirds.IsShowing);
            vm.DeleteLowerThirdCommand.Execute(neon);
            Assert.False(vm.State.LowerThirds.IsShowing);
            Assert.Equal(2, vm.State.LowerThirds.Designs.Count);
            Assert.NotNull(vm.SelectedLowerThird);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ShowAndHideReachTheAirFromEveryOriginAndLightTheTally()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.IsSandboxActive = false;
            var clean = vm.NewLowerThird("Clean");
            var tag = vm.NewLowerThird("Tag");

            // The desk: the action layer, the state, the snapshot the sinks see, the tally.
            vm.ShowLowerThird(clean);
            Assert.Equal(clean.Id, vm.State.LowerThirds.ActiveId);
            Assert.True(vm.State.LowerThirds.IsShowing);
            Assert.Equal(clean.Id, services.Bus.Current.State.LowerThirds.ActiveId);
            Assert.NotNull(services.Bus.Current.State.LowerThirds.ShownAtUtc);
            vm.RefreshTallies();
            Assert.True(clean.IsOnAir);
            Assert.False(tag.IsOnAir);
            Assert.Contains(clean.OnAirText, new[] { "ARRIVING", "ON AIR" });
            Assert.StartsWith("On air: Clean", vm.LowerThirdStatus);

            // The remote, by number and by name; OFF; STATE carries the list and the one on.
            var router = new CommandRouter(services);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT 2"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(tag.Id, vm.State.LowerThirds.ActiveId);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LOWERTHIRD clean"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(clean.Id, vm.State.LowerThirds.ActiveId);
            Assert.Contains("\"lowerThird\":\"Clean\"", router.StateJson());
            Assert.Contains("\"lowerThirds\":[{\"n\":1,\"name\":\"Clean\"},{\"n\":2,\"name\":\"Tag\"}]", router.StateJson());
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT nobody"))));
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT OFF"))));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.State.LowerThirds.IsShowing);
            Assert.NotNull(vm.State.LowerThirds.HiddenAtUtc);
            vm.RefreshTallies();
            Assert.Contains(clean.OnAirText, new[] { "LEAVING", "" });

            // The Show panel: one chip per design, the one on air lit, a hide button.
            vm.ShowLowerThird(tag);
            vm.RefreshTallies();
            var panel = new Window { DataContext = vm, Width = 900, Height = 2400, Content = new ScrollViewer { Content = new ShowSection() } };
            panel.Show();
            Dispatcher.UIThread.RunJobs();
            var chips = panel.GetVisualDescendants().OfType<Button>().Where(x => x.DataContext is LowerThirdDesign).ToList();
            Assert.Equal(2, chips.Count);
            Assert.Single(chips, x => x.Classes.Contains("air"));
            Assert.Contains(panel.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "■ Hide");
            panel.Close();
            vm.HideLowerThird();

            // The sandbox: a design made while it is open goes to the frozen program as a copy; the preview stays untouched.
            vm.IsSandboxActive = true;
            var glass = vm.NewLowerThird("Glass");
            vm.ShowLowerThird(glass);
            var air = services.AirState.LowerThirds;
            Assert.NotSame(vm.State.LowerThirds, air);
            Assert.Equal(glass.Id, air.ActiveId);
            Assert.True(air.IsShowing);
            Assert.NotNull(air.Find(glass.Id));
            Assert.False(vm.State.LowerThirds.IsShowing); // the edited state was never told
            vm.HideLowerThird();
            Assert.False(air.IsShowing);
            vm.IsSandboxActive = false;
        }
        finally
        {
            b.Dispose();
        }
    }

    [Fact]
    public void ThePreviewDrawsTheDesignAtAnInstantOfItsTimeline()
    {
        var state = new Patterns.Core.Model.ShowState();
        var clean = LowerThirdPresets.Create("Clean");
        state.LowerThirds.Designs.Add(clean);
        using var sink = new SinkState();
        var info = new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);

        static SKColor At(SKSurface s, SKImageInfo i, int x, int y)
        {
            using var bmp = new SKBitmap(i);
            s.ReadPixels(i, bmp.GetPixels(), i.RowBytes, 0, 0);
            return bmp.GetPixel(x, y);
        }

        // The panel's centre on a 640×360 stage: the design box at (60,820)+(480,100) of 1920×1080, scaled by a third.
        LowerThirdPreview.RenderPreview(surface.Canvas, sink, state, clean, timeMs: clean.InMs + 500, 640, 360, version: 1);
        surface.Canvas.Flush();
        var held = At(surface, info, 180, 306);
        Assert.True(held.Red > 200 && held.Green > 200 && held.Blue > 200, "the Clean panel is light at the hold");

        LowerThirdPreview.RenderPreview(surface.Canvas, sink, state, clean, timeMs: 0, 640, 360, version: 2);
        surface.Canvas.Flush();
        var start = At(surface, info, 180, 306);
        Assert.True(start.Red < 120, "at the start the panel has not arrived");

        LowerThirdPreview.RenderPreview(surface.Canvas, sink, state, clean, timeMs: clean.InMs + LowerThirdPreview.WaitingHoldMs + clean.OutMs + 100, 640, 360, version: 3);
        surface.Canvas.Flush();
        var gone = At(surface, info, 180, 306);
        Assert.True(gone.Red < 120, "after the way out the stage is bare again");
        var frame = At(surface, info, 320, 18); // the safe-area frame's top edge, at 54 of 1080 → 18 of 360
        Assert.True(frame.Red > gone.Red, "the safe-area frame is drawn");
    }
}
