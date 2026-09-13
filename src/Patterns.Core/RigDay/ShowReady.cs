using Patterns.Core.Services;

namespace Patterns.Core.RigDay;

/// <summary>One step of the show-ready bar: done or not, whether it applies to this rig at all, and the words that say what is left.</summary>
public sealed record ReadyStep(string Name, bool Done, bool Applies, string Words);

/// <summary>What the desk knows when the bar is drawn — the same facts the super-check and the pages read.</summary>
public sealed record ReadyFacts(bool OutputsLive, int Joins, int JoinsWarning, int Projectors, double? WorstResidualPx, bool CalibrationApplied, bool LockHeld, CheckLight? CheckOverall);

public sealed record ShowReadyScore(IReadOnlyList<ReadyStep> Steps)
{
    public int Done => Steps.Count(s => s.Done);
    public int Total => Steps.Count;
    public bool IsFull => Done == Total;
    public string Bar => new string('▰', Done) + new string('▱', Total - Done);
    /// <summary>"show-ready 3/5 ▰▰▰▱▱".</summary>
    public string Words => $"show-ready {Done}/{Total} {Bar}";
    /// <summary>The first step still open, in its own words; "all clear" when the bar is full.</summary>
    public string Next => Steps.FirstOrDefault(s => !s.Done)?.Words ?? "all clear — the rig is show-ready";
}

/// <summary>
/// The show-ready score on a rig day: outputs on, every join's audit green, the calibration's
/// residual under a pixel, the show lock held, the super-check all clear — each a step of a bar
/// that fills. The same facts the super-check already reads, told as progress; a step that does
/// not apply to this rig (no joins, no projectors) counts as done and says so.
/// </summary>
public static class ShowReady
{
    public const double ResidualGoal = 1.0;

    public static ShowReadyScore Score(ReadyFacts f)
    {
        var steps = new List<ReadyStep>
        {
            new("Outputs", f.OutputsLive, true, f.OutputsLive ? "outputs on" : "outputs closed — OUTPUTS ON opens them"),
        };
        if (f.Joins == 0) steps.Add(new("Joins", true, false, "no joins to blend"));
        else if (f.JoinsWarning == 0) steps.Add(new("Joins", true, true, $"every join's audit green ({f.Joins})"));
        else steps.Add(new("Joins", false, true, $"{f.JoinsWarning} of {f.Joins} join{(f.Joins == 1 ? "" : "s")} warn — Screens page, Edge blend"));
        if (f.Projectors == 0) steps.Add(new("Calibration", true, false, "no projectors to calibrate"));
        else if (!f.CalibrationApplied || f.WorstResidualPx is null) steps.Add(new("Calibration", false, true, "no calibration applied — CALIBRATE RUN <camera>, then APPLY"));
        else if (f.WorstResidualPx.Value < ResidualGoal) steps.Add(new("Calibration", true, true, $"calibration under a pixel ({f.WorstResidualPx.Value:0.00} px)"));
        else steps.Add(new("Calibration", false, true, $"calibration residual {f.WorstResidualPx.Value:0.0} px — the alignment game, or a steadier run"));
        steps.Add(new("Show lock", f.LockHeld, true, f.LockHeld ? "show lock held" : "show lock off — SHOWLOCK ON"));
        steps.Add(f.CheckOverall switch
        {
            CheckLight.Green => new ReadyStep("Super-check", true, true, "super-check all clear"),
            null => new ReadyStep("Super-check", false, true, "super-check not run — Machine page, SUPER-CHECK"),
            var light => new ReadyStep("Super-check", false, true, $"super-check {light.ToString()!.ToLowerInvariant()} — Machine page"),
        });
        return new ShowReadyScore(steps);
    }
}
