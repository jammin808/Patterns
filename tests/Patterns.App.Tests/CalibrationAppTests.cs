using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The camera as the tests have it: a room that is not there, read through the same overlay the
/// outputs draw from — whichever projector the overlay lights, the room photographs.
/// </summary>
internal sealed class FakeCalibrationCamera : ICalibrationCamera
{
    private readonly CalibrationSimulator _room;
    private readonly string[] _ids;

    public FakeCalibrationCamera(CalibrationSimulator room)
    {
        _room = room;
        _ids = Enumerable.Range(0, room.Count).Select(i => room.ProjectorAt(i).Id).ToArray();
    }

    public string Name => "fake";

    public int Frames { get; private set; }

    public bool Disposed { get; private set; }

    public static GreyFrame Photograph(CalibrationSimulator room, IReadOnlyList<string> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (CalibrationOverlay.PatternFor(ids[i]) is { } pattern) return room.Frame(i, pattern);
        }
        return room.Frame(0, new CalPattern(CalStep.Black, 0, false));                  // nothing lit: every projector dark
    }

    public Task<GreyFrame?> GrabAsync(CancellationToken ct)
    {
        Frames++;
        return Task.FromResult<GreyFrame?>(Photograph(_room, _ids));
    }

    public void Dispose() => Disposed = true;
}

/// <summary>The calibration on the desk: the live run, the photographs path, the demo, APPLY and UNDO, the wire, and the outputs' structured light.</summary>
public class CalibrationAppTests
{
    /// <summary>The desk's own display (primary, so its placement is off) and two projectors behind it.</summary>
    private static List<ScreenInfo> DeskAndTwoProjectors() => new()
    {
        new("desk", "Desk", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("pj1", "PJ 1", new Avalonia.PixelRect(1920, 0, 1280, 720), 1.0, false, 1),
        new("pj2", "PJ 2", new Avalonia.PixelRect(3200, 0, 1280, 720), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = DeskAndTwoProjectors();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Two keystoned pictures side by side with a tenth of overlap, as a camera on a tripod would see them.</summary>
    private static CalibrationSimulator Room(int cameraWidth, int cameraHeight)
    {
        var sx = cameraWidth / 640f;
        var sy = cameraHeight / 360f;
        SKPoint P(float x, float y) => new(x * sx, y * sy);
        return new CalibrationSimulator(cameraWidth, cameraHeight)
            .AddProjector("pj1", "PJ 1", 1280, 720, P(40, 60), P(340, 62), P(338, 300), P(42, 298))
            .AddProjector("pj2", "PJ 2", 1280, 720, P(300, 70), P(600, 58), P(604, 304), P(302, 292));
    }

    private static void RunToTheEnd(CalibrationService cal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (cal.Running && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    [AvaloniaFact]
    public void ARunShowsEveryPatternReadsTheCameraSolvesTheRigAndApplyPutsEachProjectorInItsPlaceWithItsMeshAndMask()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            var services = b.Services;
            var cal = services.Calibration;
            var screens = b.Vm.Screens;
            Assert.Equal(new[] { "pj1", "pj2" }, cal.Projectors().Select(p => p.ScreenId));

            var room = Room(640, 360);
            var camera = new FakeCalibrationCamera(room);
            cal.SettleMs = 0;
            cal.OpenCamera = name => name == "fake" ? camera : null;

            // No camera picked: the page says so and nothing runs.
            screens.RunCalibrationCommand.Execute(null);
            Assert.Contains("Pick the camera", b.Vm.StatusMessage);
            Assert.Null(cal.Solution);

            // A camera nobody has: refused, in words.
            screens.CalibrationCameras.Add("fake");
            screens.SelectedCalibrationCamera = "ghost";
            screens.RunCalibrationCommand.Execute(null);
            Assert.Contains("could not be opened", b.Vm.StatusMessage);

            // The run: every pattern of every projector shown and read, the outputs' overlay over and gone, the solution on the page.
            screens.SelectedCalibrationCamera = "fake";
            screens.RunCalibrationCommand.Execute(null);
            RunToTheEnd(cal);
            Assert.False(cal.Running);
            Assert.False(CalibrationOverlay.Active);
            Assert.Equal(2 * GrayCode.Sequence(1280, 720).Count, camera.Frames);
            Assert.True(camera.Disposed);
            Assert.NotNull(cal.Solution);
            Assert.Equal(1.0, cal.Progress, 3);
            screens.PollCalibration();
            Assert.True(screens.HasCalibrationSolution);
            Assert.False(screens.CanUndoCalibration);
            Assert.StartsWith("Solved", screens.CalibrationStatus);
            Assert.Contains("a canvas of", screens.CalibrationReport);
            Assert.Contains("PJ 1 and PJ 2 overlap over", screens.CalibrationReport);
            var solved = cal.Solution!;
            Assert.All(solved.Projectors, p => Assert.InRange(p.FitResidualPx, 0, 1.5));
            Assert.All(solved.Projectors, p => Assert.Equal(9, p.MeshColumns));

            // APPLY: each placement in its solved place with a 9×9 mesh and a mask under media/calibration, its zones off; the viewports carry the mask.
            var p1 = b.Vm.State.Output.Placements.Single(p => p.ScreenId == "pj1");
            var p2 = b.Vm.State.Output.Placements.Single(p => p.ScreenId == "pj2");
            p1.BlendAuto = true;
            p1.BlendRightPx = 100;
            var before = (p1.X, p1.Y, p1.WarpMesh, p1.BlendAuto, p1.BlendRightPx, p1.BlendMaskPath, p2.X);
            screens.ApplyCalibrationCommand.Execute(null);
            Assert.True(cal.Applied);
            Assert.True(screens.CanUndoCalibration);
            Assert.StartsWith("Applied to 2 projectors", b.Vm.StatusMessage);
            Assert.Equal((solved.Projectors[0].X, solved.Projectors[0].Y), (p1.X, p1.Y));
            Assert.True(p2.X > p1.X + 600, $"PJ 2 sits to the right: {p1.X} < {p2.X}");
            Assert.Equal(9, p1.WarpMeshColumns);
            Assert.True(p1.HasMesh);
            Assert.True(WarpGrid.HasMesh(p1));
            Assert.False(p1.BlendAuto);
            Assert.Equal(0, p1.BlendRightPx);
            Assert.True(p1.BlendsOverlaps);
            Assert.True(p1.HasBlend);
            Assert.StartsWith(Path.Combine(services.Store.MediaDirectory, "calibration"), p1.BlendMaskPath);
            Assert.True(File.Exists(p1.BlendMaskPath), p1.BlendMaskPath);
            Assert.True(File.Exists(p2.BlendMaskPath), p2.BlendMaskPath);
            using (var mask = SKBitmap.Decode(p1.BlendMaskPath))
            {
                Assert.Equal((480, 270), (mask.Width, mask.Height));
                Assert.True(mask.GetPixel(4, 135).Red > 240, "PJ 1 alone at the canvas's left edge keeps full light");
                Assert.True(mask.GetPixel(475, 135).Red < 60, "PJ 1's right edge lies in PJ 2's light and fades out");
            }
            var viewports = OutputWindowManager.BuildViewports(b.Vm.State.Output.Placements, services.Screens.All, includePlanned: true);
            var vp1 = viewports.Single(v => v.Screen.Id == "pj1").Viewport;
            Assert.Equal(p1.BlendMaskPath, vp1.BlendMaskPath);
            Assert.Equal("pj1", vp1.OutputId);                                                    // its own output, for the structured light
            Assert.NotEqual("pj1", vp1.ScreenId);                                                 // and a member of the joined canvas the two now make
            Assert.Equal(vp1.ScreenId, viewports.Single(v => v.Screen.Id == "pj2").Viewport.ScreenId);
            var json = JsonUtil.SerializeCompact(b.Vm.State);
            Assert.Equal(p1.BlendMaskPath, JsonUtil.Deserialize<ShowState>(json)!.Output.Placements.Single(p => p.ScreenId == "pj1").BlendMaskPath);

            // The status as JSON, for the wire.
            var status = cal.StatusJson();
            Assert.Contains("\"solved\":true", status);
            Assert.Contains("\"applied\":true", status);
            Assert.Contains("\"mesh\":\"9\\u00D79\"", status);
            Assert.Contains("\"name\":\"PJ 1\"", status);

            // UNDO: everything as it was.
            screens.UndoCalibrationCommand.Execute(null);
            Assert.False(cal.Applied);
            Assert.Equal(before, (p1.X, p1.Y, p1.WarpMesh, p1.BlendAuto, p1.BlendRightPx, p1.BlendMaskPath, p2.X));
            Assert.StartsWith("Undone", b.Vm.StatusMessage);
            Assert.Contains("Nothing to undo", services.Calibration.Undo().Message);
        }
        finally
        {
            CalibrationOverlay.End();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePhotographsPathWritesThePlanStepsThePatternsAndSolvesFromTheFolder()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            var cal = b.Services.Calibration;
            var room = Room(320, 180);
            var ids = new[] { "pj1", "pj2" };
            var folder = Path.Combine(b.Dir, "photos");
            var perProjector = GrayCode.Sequence(1280, 720).Count;

            Assert.Equal("Write the plan first.", cal.NextPattern());
            var words = cal.WritePlan(folder);
            Assert.StartsWith($"{2 * perProjector} photos to take", words);
            var plan = File.ReadAllLines(Path.Combine(folder, "cal-plan.txt"));
            Assert.Equal(2 * perProjector, plan.Length);
            Assert.Equal("cal-1.png\tPJ 1\twhite", plan[0]);
            Assert.Equal("cal-2.png\tPJ 1\tblack", plan[1]);
            Assert.Equal($"cal-{perProjector + 1}.png\tPJ 2\twhite", plan[perProjector]);
            Assert.True(CalibrationOverlay.Active);
            Assert.Equal(CalStep.White, CalibrationOverlay.PatternFor("pj1")!.Value.Step);
            Assert.Null(CalibrationOverlay.PatternFor("pj2"));

            // The walk: photograph what is up, NEXT PATTERN, until the outputs are handed back.
            for (var k = 1; k <= 2 * perProjector; k++)
            {
                CalibrationFrames.WritePng(FakeCalibrationCamera.Photograph(room, ids), FolderCalibrationCamera.FileFor(folder, k));
                var next = cal.NextPattern();
                if (k < 2 * perProjector) Assert.StartsWith($"Pattern {k + 1} of {2 * perProjector}", next);
                else Assert.StartsWith("Every pattern has been shown", next);
            }
            Assert.False(CalibrationOverlay.Active);

            var solved = cal.SolveFromFolder(folder);
            Assert.True(solved.Ok, solved.Message);
            Assert.NotNull(cal.Solution);
            Assert.Equal(2, cal.Solution!.Projectors.Count);
            Assert.All(cal.Solution.Projectors, p => Assert.InRange(p.FitResidualPx, 0, 2.5));
            Assert.Contains("PJ 1 and PJ 2 overlap over", cal.Solution.Report);

            // A photograph missing is named, with what it was meant to show.
            File.Delete(FolderCalibrationCamera.FileFor(folder, 3));
            var missing = cal.SolveFromFolder(folder);
            Assert.False(missing.Ok);
            Assert.Contains("cal-3.png", missing.Message);
            Assert.Contains($"{GrayCode.Sequence(1280, 720)[2].Name} on PJ 1", missing.Message);   // the first stripes: the columns' top bit
        }
        finally
        {
            CalibrationOverlay.End();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheDemoAndTheWireSolveWithoutACameraAndTheVerbsAreTheDesksOwn()
    {
        var b = TestApp.Boot();
        try
        {
            Rig(b);
            var services = b.Services;
            var screens = b.Vm.Screens;
            screens.DemoCalibrationCommand.Execute(null);
            Assert.True(screens.HasCalibrationSolution);
            Assert.Contains("room that is not there", screens.CalibrationStatus);
            Assert.Contains("a canvas of", screens.CalibrationReport);

            // The wire: the same verbs, and the status as JSON.
            var router = new CommandRouter(services);
            Assert.Contains("\"solved\":true", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("CALIBRATE STATUS"))));
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("CALIBRATE APPLY"))));
            Assert.True(services.Calibration.Applied);
            Assert.Contains("\"applied\":true", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("CALIBRATE STATUS"))));
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("CALIBRATE UNDO"))));
            Assert.False(services.Calibration.Applied);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("CALIBRATE CANCEL"))));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("CALIBRATE RUN"))));
            services.Calibration.OpenCamera = _ => null;                                   // no NDI in a test: every camera is nobody's
            var cannot = services.Actions.Execute(new ShowAction(ShowActionKind.CalibrateRun, "", "no such source"), ActionOrigin.Desk);
            Assert.False(cannot.Ok);
            Assert.Contains("could not be opened", cannot.Message);

            // A running order never re-aims the projectors.
            Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.CalibrateRun));
            Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.CalibrateApply));
            Assert.DoesNotContain(ShowActionKind.CalibrateApply, ActionSpec.CueKinds);
            Assert.Equal("Calibrate — apply the solution to the rig", ActionSpec.Label(ShowActionKind.CalibrateApply));

            // No projector: refused in words, on the page and on the wire.
            b.Vm.State.Output.Placements.Clear();
            Assert.Contains("No projector", services.Calibration.RunDemo().Message);
        }
        finally
        {
            CalibrationOverlay.End();
            b.Dispose();
        }
    }

    private static SnapshotBus WhiteBus()
    {
        var state = new ShowState();
        state.Pattern.Canvas.FollowOutput = true;
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = "#FFFFFF";
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.FlatField.ShowBorder = false;
        state.Overlays.Clock.Enabled = false;
        state.Overlays.Info.Enabled = false;
        state.Countdown.Enabled = false;
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        return bus;
    }

    private static SKBitmap Frame(SnapshotBus bus, PipelineViewport viewport, int w, int h)
    {
        using var pipeline = new RenderPipeline(bus, viewport);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        pipeline.Render(surface.Canvas, w, h, renderScaling: 1.0);
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    [Fact]
    public void WhileACalibrationRunsTheOutputsShowTheStructuredLightAndNothingElse()
    {
        var bus = WhiteBus();
        var a = new PipelineViewport(SinkKind.Output, SKSizeI.Empty, default, "cal-a", 1, "A");
        var other = new PipelineViewport(SinkKind.Output, SKSizeI.Empty, default, "cal-b", 2, "B");
        var preview = new PipelineViewport(SinkKind.Preview, SKSizeI.Empty, default, null, 0, "Preview");
        try
        {
            CalibrationOverlay.Begin();
            using (var black = Frame(bus, a, 64, 32)) Assert.Equal(0, black.GetPixel(10, 10).Red);   // begun, nothing shown: black

            var stripes = new CalPattern(CalStep.Column, 1, false);
            CalibrationOverlay.Show("cal-a", stripes);
            using (var lit = Frame(bus, a, 64, 32))
            {
                for (var x = 0; x < 64; x++)
                {
                    var expected = GrayCode.IsLit(stripes, x, 0) ? 255 : 0;
                    Assert.Equal(expected, lit.GetPixel(x, 16).Red);
                }
            }
            using (var dark = Frame(bus, other, 64, 32)) Assert.Equal(0, dark.GetPixel(40, 16).Red);   // the other projector stays black
            var member = new PipelineViewport(SinkKind.Output, new SKSizeI(128, 32), default, "canvas:cal-a+cal-b", 1, "A") { OutputId = "cal-a" };
            using (var joined = Frame(bus, member, 64, 32)) Assert.Equal(GrayCode.IsLit(stripes, 40, 0) ? 255 : 0, joined.GetPixel(40, 16).Red); // a canvas member lights as itself
            using (var desk = Frame(bus, preview, 64, 32)) Assert.Equal(255, desk.GetPixel(40, 16).Red); // the preview shows the show

            CalibrationOverlay.End();
            using (var back = Frame(bus, a, 64, 32)) Assert.Equal(255, back.GetPixel(10, 10).Red);
        }
        finally
        {
            CalibrationOverlay.End();
        }
    }
}
