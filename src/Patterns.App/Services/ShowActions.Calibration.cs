using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>The camera calibration's verbs: a run through a camera, cancelled, the demo, applied, undone.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunCalibration(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.CalibrateRun:
            {
                if (a.Value.Length == 0) return ActionResult.Refused("CALIBRATE RUN <camera>: name the NDI source the camera is.");
                var task = _s.Calibration.RunAsync(a.Value);
                if (task.IsCompleted) return task.Result;                                 // refused before it began: no projector, no camera
                _ = task.ContinueWith(t => Log.Error("The calibration run faulted.", t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
                return ActionResult.Requested($"Calibration started through '{a.Value}' — CALIBRATE STATUS follows it; CALIBRATE APPLY once it has solved.");
            }
            case ShowActionKind.CalibrateCancel:
                if (!_s.Calibration.Running) return ActionResult.Refused("No calibration is running.");
                _s.Calibration.Cancel();
                return ActionResult.Done("Calibration cancelled — the outputs show the show again.");
            case ShowActionKind.CalibrateDemo:
                return _s.Calibration.RunDemo();
            case ShowActionKind.CalibrateApply:
                return _s.Calibration.Apply();
            case ShowActionKind.CalibrateUndo:
                return _s.Calibration.Undo();
            default:
                return null;
        }
    }
}
