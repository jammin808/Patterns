using Patterns.App.Rendering;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>A camera the calibration reads from: one grey frame each time it is asked, after the outputs have settled.</summary>
public interface ICalibrationCamera : IDisposable
{
    string Name { get; }

    Task<GreyFrame?> GrabAsync(CancellationToken ct);
}

/// <summary>An NDI source as the camera — a phone with an NDI camera app, a capture card through NDI Tools: the newest frame, read as grey.</summary>
public sealed class NdiCalibrationCamera : ICalibrationCamera
{
    private readonly NdiReceiver _receiver;

    public NdiCalibrationCamera(string sourceName)
    {
        Name = sourceName;
        _receiver = new NdiReceiver(sourceName);
    }

    public string Name { get; }

    public async Task<GreyFrame?> GrabAsync(CancellationToken ct)
    {
        for (var tries = 0; tries < 60; tries++)
        {
            ct.ThrowIfCancellationRequested();
            if (_receiver.FrameSize is { } size && size.Width > 0 && size.Height > 0)
            {
                var frame = await Task.Run(() =>
                {
                    using var bitmap = new SKBitmap(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var canvas = new SKCanvas(bitmap);
                    canvas.Clear(SKColors.Black);
                    return _receiver.DrawFrame(canvas, SKRect.Create(0, 0, size.Width, size.Height), null) ? CalibrationFrames.ToGrey(bitmap) : null;
                }, ct);
                if (frame is not null) return frame;
            }
            await Task.Delay(50, ct);
        }
        return null;
    }

    public void Dispose() => _receiver.Dispose();
}

/// <summary>Photographs from a folder as the camera: cal-1.png, cal-2.png… in the plan's order — the offline path, a phone and a walk.</summary>
public sealed class FolderCalibrationCamera : ICalibrationCamera
{
    private readonly string _folder;
    private int _next = 1;

    public FolderCalibrationCamera(string folder)
    {
        _folder = folder;
        Name = "photos in " + folder;
    }

    public string Name { get; }

    public static string FileFor(string folder, int index) => Path.Combine(folder, $"cal-{index}.png");

    public Task<GreyFrame?> GrabAsync(CancellationToken ct)
    {
        var path = FileFor(_folder, _next++);
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            return Task.FromResult(bitmap is null ? null : CalibrationFrames.ToGrey(bitmap));
        }
        catch (Exception ex)
        {
            Log.Warn($"The calibration photo '{path}' could not be read.", ex);
            return Task.FromResult<GreyFrame?>(null);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Pictures to grey frames and back.</summary>
public static class CalibrationFrames
{
    public static GreyFrame ToGrey(SKBitmap bitmap)
    {
        var frame = new GreyFrame(bitmap.Width, bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                frame.Pixels[y * bitmap.Width + x] = (byte)((c.Red * 54 + c.Green * 183 + c.Blue * 19) >> 8);
            }
        }
        return frame;
    }

    public static SKBitmap ToBitmap(GreyFrame frame)
    {
        var bitmap = new SKBitmap(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var v = frame.Pixels[y * frame.Width + x];
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        }
        return bitmap;
    }

    public static void WritePng(GreyFrame frame, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = ToBitmap(frame);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}

/// <summary>
/// Camera calibration on the desk: the outputs show the structured light one projector at a
/// time, the camera is read after each pattern, the frames are decoded and solved, and APPLY puts
/// each projector in its place with its mesh and its blend mask (a PNG beside the media). The
/// camera is an NDI source, or photographs in a folder taken by hand with NEXT PATTERN; a run
/// against a room that is not there shows what the report reads like without a projector. UNDO
/// puts the placements back as they were before APPLY.
/// </summary>
public sealed class CalibrationService
{
    private readonly AppServices _s;
    private CancellationTokenSource? _cts;
    private readonly List<(string ScreenId, int X, int Y, int Columns, int Rows, string Mesh, bool BlendAuto, int L, int T, int R, int B, string Mask)> _before = new();
    private IReadOnlyList<(ScreenPlacement Placement, CalPattern Pattern)> _plan = Array.Empty<(ScreenPlacement, CalPattern)>();
    private int _planStep = -1;

    public CalibrationService(AppServices services) => _s = services;

    /// <summary>How long the outputs and the camera are given after each pattern before the frame is read.</summary>
    public int SettleMs { get; set; } = 500;

    /// <summary>The lattice the solver makes per projector.</summary>
    public int MeshDensity { get; set; } = 9;

    /// <summary>The camera's frames are read at no more than this width; the solver never needs more.</summary>
    public int MaxCameraWidth { get; set; } = 640;

    /// <summary>How a camera is opened by name; the tests hand in a fake.</summary>
    public Func<string, ICalibrationCamera?> OpenCamera { get; set; } = name => new NdiCalibrationCamera(name);

    public bool Running => _cts is not null;

    public double Progress { get; private set; }

    public string Status { get; private set; } = "No calibration yet.";

    public CalibrationSolution? Solution { get; private set; }

    public bool Applied { get; private set; }

    /// <summary>The NDI sources a camera could be.</summary>
    public IReadOnlyList<string> CameraNames()
    {
        try
        {
            using var finder = new NdiFinder();
            return finder.CurrentSources();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The projectors a calibration reads: every screen that is on with a display behind it.</summary>
    public IReadOnlyList<ScreenPlacement> Projectors()
        => _s.State.Output.Placements.Where(p => p.Enabled && !p.IsVirtual && !p.Planned && _s.Screens.Real.Any(s => s.Id == p.ScreenId)).ToList();

    private (int Width, int Height) RasterOf(ScreenPlacement p)
    {
        var info = _s.Screens.All.FirstOrDefault(s => s.Id == p.ScreenId);
        return info is null ? (p.PlannedWidth, p.PlannedHeight) : (info.Bounds.Width, info.Bounds.Height);
    }

    private string NameOf(ScreenPlacement p)
    {
        if (p.CustomLabel.Length > 0) return p.CustomLabel;
        var info = _s.Screens.All.FirstOrDefault(s => s.Id == p.ScreenId);
        return info?.Label is { Length: > 0 } label ? label : p.ScreenId;
    }

    /// <summary>The whole sequence, projector by projector: what the plan file lists and NEXT PATTERN steps through.</summary>
    public IReadOnlyList<(ScreenPlacement Placement, CalPattern Pattern)> Plan(IReadOnlyList<ScreenPlacement> projectors)
    {
        var plan = new List<(ScreenPlacement, CalPattern)>();
        foreach (var p in projectors)
        {
            var (w, h) = RasterOf(p);
            foreach (var pattern in GrayCode.Sequence(w, h)) plan.Add((p, pattern));
        }
        return plan;
    }

    /// <summary>The live run: every pattern shown and read through the camera, then solved. Nothing is applied until APPLY.</summary>
    public async Task<ActionResult> RunAsync(string cameraName, CancellationToken ct = default)
    {
        if (Running) return ActionResult.Refused("A calibration is running.");
        var projectors = Projectors();
        if (projectors.Count == 0) return ActionResult.Refused("No projector to calibrate: a screen that is on, with a display behind it.");
        ICalibrationCamera? camera;
        try
        {
            camera = OpenCamera(cameraName);
        }
        catch (Exception ex)
        {
            return ActionResult.Refused($"The camera '{cameraName}' could not be opened: {ex.Message}");
        }
        if (camera is null) return ActionResult.Refused($"The camera '{cameraName}' could not be opened.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        try
        {
            using (camera)
            {
                var plan = Plan(projectors);
                var frames = new Dictionary<string, List<GreyFrame>>();
                CalibrationOverlay.Begin();
                _s.Outputs.NotifySnapshot();
                await Task.Delay(SettleMs, token);
                for (var i = 0; i < plan.Count; i++)
                {
                    var (p, pattern) = plan[i];
                    CalibrationOverlay.Show(p.ScreenId, pattern);
                    _s.Outputs.NotifySnapshot();
                    Status = $"Reading {NameOf(p)}: {pattern.Name} ({i + 1} of {plan.Count})";
                    Progress = (i + 1) / (double)plan.Count;
                    await Task.Delay(SettleMs, token);
                    var frame = await camera.GrabAsync(token);
                    if (frame is null)
                    {
                        Status = $"The camera gave no frame for {NameOf(p)}: {pattern.Name} — is it sending?";
                        return ActionResult.Refused(Status);
                    }
                    var factor = Math.Max(1, frame.Width / Math.Max(160, MaxCameraWidth));
                    if (!frames.TryGetValue(p.ScreenId, out var list)) frames[p.ScreenId] = list = new List<GreyFrame>();
                    list.Add(frame.Downsampled(factor));
                }
                CalibrationOverlay.End();
                _s.Outputs.NotifySnapshot();
                return Solve(projectors, id => frames[id]);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Calibration cancelled.";
            return ActionResult.Refused(Status);
        }
        catch (Exception ex)
        {
            Log.Error("Calibration failed.", ex);
            Status = "Calibration failed: " + ex.Message;
            return ActionResult.Refused(Status);
        }
        finally
        {
            CalibrationOverlay.End();
            _s.Outputs.NotifySnapshot();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// The desk is going: a run in flight is cancelled and the structured light ends now, not when
    /// the camera's grab notices — the overlay is a process-wide flag, and every output sink paints
    /// black while it stands, so it must not outlive the desk that raised it.
    /// </summary>
    public void Shutdown()
    {
        Cancel();
        CalibrationOverlay.End();
    }

    private ActionResult Solve(IReadOnlyList<ScreenPlacement> projectors, Func<string, IReadOnlyList<GreyFrame>> framesOf)
    {
        var samples = new List<ProjectorSample>();
        foreach (var p in projectors)
        {
            var (w, h) = RasterOf(p);
            var sequence = GrayCode.Sequence(w, h);
            var frames = framesOf(p.ScreenId);
            if (frames.Count != sequence.Count) return ActionResult.Refused($"{NameOf(p)} needs {sequence.Count} frames and has {frames.Count}.");
            samples.Add(new ProjectorSample(p.ScreenId, NameOf(p), w, h, GrayCode.Decode(sequence, frames, w, h)));
        }
        Solution = Calibrator.Solve(samples, Calibrator.AutoCanvas(samples), MeshDensity, MeshDensity);
        Applied = false;
        Status = "Solved — read the report, then APPLY.";
        Log.Info("Calibration solved:\n" + Solution.Report);
        _s.Journal.Record("Desk", "Calibration", $"{samples.Count} projectors", "Solved", Solution.Report.Split('\n')[0]);
        return ActionResult.Done(Status);
    }

    /// <summary>The offline path, step one: the plan written as cal-plan.txt — one line per photo — and the first pattern shown.</summary>
    public string WritePlan(string folder)
    {
        var projectors = Projectors();
        _plan = Plan(projectors);
        Directory.CreateDirectory(folder);
        var lines = _plan.Select((step, i) => $"cal-{i + 1}.png\t{NameOf(step.Placement)}\t{step.Pattern.Name}").ToList();
        File.WriteAllLines(Path.Combine(folder, "cal-plan.txt"), lines);
        _planStep = -1;
        NextPattern();
        return $"{_plan.Count} photos to take, in {folder}: cal-plan.txt lists each. The first pattern is up — photograph it as cal-1.png, then NEXT PATTERN.";
    }

    /// <summary>The offline path: the next pattern on its projector; past the last, the outputs show the show again.</summary>
    public string NextPattern()
    {
        if (_plan.Count == 0) return "Write the plan first.";
        _planStep++;
        if (_planStep >= _plan.Count)
        {
            CalibrationOverlay.End();
            _s.Outputs.NotifySnapshot();
            _planStep = -1;
            return "Every pattern has been shown — the outputs show the show again. SOLVE FROM PHOTOS once the files are in the folder.";
        }
        var (p, pattern) = _plan[_planStep];
        CalibrationOverlay.Show(p.ScreenId, pattern);
        _s.Outputs.NotifySnapshot();
        return $"Pattern {_planStep + 1} of {_plan.Count} on {NameOf(p)}: {pattern.Name} — photograph it as cal-{_planStep + 1}.png.";
    }

    /// <summary>The offline path, step three: the photographs read in the plan's order and solved.</summary>
    public ActionResult SolveFromFolder(string folder)
    {
        var projectors = Projectors();
        if (projectors.Count == 0) return ActionResult.Refused("No projector to calibrate.");
        CalibrationOverlay.End();
        _s.Outputs.NotifySnapshot();
        var plan = Plan(projectors);
        var frames = new Dictionary<string, List<GreyFrame>>();
        using var camera = new FolderCalibrationCamera(folder);
        for (var i = 0; i < plan.Count; i++)
        {
            var frame = camera.GrabAsync(CancellationToken.None).Result;
            if (frame is null) return ActionResult.Refused($"{FolderCalibrationCamera.FileFor(folder, i + 1)} is missing or unreadable ({plan[i].Pattern.Name} on {NameOf(plan[i].Placement)}).");
            var factor = Math.Max(1, frame.Width / Math.Max(160, MaxCameraWidth));
            if (!frames.TryGetValue(plan[i].Placement.ScreenId, out var list)) frames[plan[i].Placement.ScreenId] = list = new List<GreyFrame>();
            list.Add(frame.Downsampled(factor));
        }
        return Solve(projectors, id => frames[id]);
    }

    /// <summary>A run against a room that is not there: the projectors as if seen by a 640×360 camera, side by side with a tenth of overlap — the report's words without a projector.</summary>
    public ActionResult RunDemo()
    {
        var projectors = Projectors();
        if (projectors.Count == 0) return ActionResult.Refused("No projector to calibrate: a screen that is on, with a display behind it.");
        var room = new CalibrationSimulator(640, 360);
        var n = projectors.Count;
        var span = 560f / (n - 0.1f * (n - 1));
        for (var i = 0; i < n; i++)
        {
            var (w, h) = RasterOf(projectors[i]);
            var left = 40 + i * span * 0.9f;
            var right = left + span;
            var tilt = (i % 2 == 0 ? 1 : -1) * 6f;
            room.AddProjector(projectors[i].ScreenId, NameOf(projectors[i]), w, h, new SKPoint(left, 60 + tilt), new SKPoint(right, 60 - tilt), new SKPoint(right + 2, 300 + tilt), new SKPoint(left - 2, 300 - tilt));
        }
        var samples = room.Samples();
        Solution = Calibrator.Solve(samples, Calibrator.AutoCanvas(samples), MeshDensity, MeshDensity);
        Applied = false;
        Status = "Solved against a room that is not there — the words, not this rig. Run with a camera for the real thing.";
        return ActionResult.Done(Status);
    }

    /// <summary>The solution onto the rig: each projector's place, mesh and mask, its zones off (the mask blends); the placements as they were are kept for UNDO.</summary>
    public ActionResult Apply()
    {
        if (Solution is not { } solution) return ActionResult.Refused("Nothing to apply — run a calibration first.");
        var maskFolder = Path.Combine(_s.Store.MediaDirectory, "calibration");
        _before.Clear();
        var applied = 0;
        _s.BulkEdit(() =>
        {
            foreach (var sol in solution.Projectors)
            {
                var p = _s.State.Output.Placements.FirstOrDefault(x => x.ScreenId == sol.ScreenId);
                if (p is null || sol.Mesh.Length == 0 && double.IsNaN(sol.FitResidualPx)) continue;
                _before.Add((p.ScreenId, p.X, p.Y, p.WarpMeshColumns, p.WarpMeshRows, p.WarpMesh, p.BlendAuto, p.BlendLeftPx, p.BlendTopPx, p.BlendRightPx, p.BlendBottomPx, p.BlendMaskPath));
                var maskPath = Path.Combine(maskFolder, $"{SafeName(p.ScreenId)}.png");
                try
                {
                    CalibrationFrames.WritePng(sol.BlendMask, maskPath);
                }
                catch (Exception ex)
                {
                    Log.Warn($"The blend mask for {sol.Name} could not be written.", ex);
                    maskPath = "";
                }
                p.X = sol.X;
                p.Y = sol.Y;
                p.WarpMeshColumns = sol.MeshColumns;
                p.WarpMeshRows = sol.MeshRows;
                p.WarpMesh = sol.Mesh;
                p.BlendAuto = false;
                p.BlendLeftPx = p.BlendTopPx = p.BlendRightPx = p.BlendBottomPx = 0;
                p.BlendMaskPath = maskPath;
                applied++;
            }
        });
        Applied = true;
        Status = $"Applied to {applied} projector{(applied == 1 ? "" : "s")}: each in its place with its mesh and its blend mask. UNDO puts them back.";
        _s.Journal.Record("Desk", "CalibrationApply", $"{applied} projectors", "Done", Status);
        return ActionResult.Done(Status);
    }

    /// <summary>The placements as they were before APPLY.</summary>
    public ActionResult Undo()
    {
        if (_before.Count == 0) return ActionResult.Refused("Nothing to undo.");
        _s.BulkEdit(() =>
        {
            foreach (var b in _before)
            {
                var p = _s.State.Output.Placements.FirstOrDefault(x => x.ScreenId == b.ScreenId);
                if (p is null) continue;
                p.X = b.X;
                p.Y = b.Y;
                p.WarpMeshColumns = b.Columns;
                p.WarpMeshRows = b.Rows;
                p.WarpMesh = b.Mesh;
                p.BlendAuto = b.BlendAuto;
                p.BlendLeftPx = b.L;
                p.BlendTopPx = b.T;
                p.BlendRightPx = b.R;
                p.BlendBottomPx = b.B;
                p.BlendMaskPath = b.Mask;
            }
        });
        _before.Clear();
        Applied = false;
        Status = "Undone — the placements are as they were before APPLY.";
        return ActionResult.Done(Status);
    }

    /// <summary>CALIBRATE STATUS: the run, the solution and whether it is on the rig, as JSON.</summary>
    public string StatusJson()
    {
        var s = Solution;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            running = Running,
            progress = Math.Round(Progress, 3),
            status = Status,
            solved = s is not null,
            applied = Applied,
            canvas = s is null ? null : new { width = s.CanvasWidth, height = s.CanvasHeight },
            projectors = s is null
                ? Array.Empty<object>()
                : s.Projectors.Select(p => (object)new
                {
                    id = p.ScreenId,
                    name = p.Name,
                    x = p.X,
                    y = p.Y,
                    mesh = p.Mesh.Length > 0 ? $"{p.MeshColumns}×{p.MeshRows}" : "",
                    coverage = Math.Round(p.CanvasCoverage, 3),
                    residualPx = double.IsNaN(p.FitResidualPx) ? (double?)null : Math.Round(p.FitResidualPx, 2),
                    words = p.Words,
                }).ToArray(),
            report = s?.Report ?? "",
        });
    }

    /// <summary>A screen id as a file name: anything a file system or a URL would object to becomes an underscore.</summary>
    internal static string SafeName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => invalid.Contains(c) || c is ':' or '@' or ',' or ' ' ? '_' : c).ToArray();
        var safe = new string(chars).Trim('_');
        return safe.Length == 0 ? "screen" : safe;
    }
}
