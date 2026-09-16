using Patterns.Core.Geometry;
using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.RigDay;
using Patterns.Rendering.RigDay;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Rig day, gamified — opt-in, per operator, off in one switch: the show-ready bar from the facts
/// the super-check reads, the alignment game on a projector after a calibration (the solver's
/// targets on the lattice, the picked node driven by the keys, a lock within a pixel), Blend Quest
/// over the joins' audits, and the caller's on-time streak. Quiet unless asked: nothing here runs
/// while the switch is off.
/// </summary>
public sealed class RigDayService
{
    private readonly AppServices _s;
    private DateTime _lastFactsUtc = DateTime.MinValue;
    private readonly CelebrationTrack _track = new();
    private Celebration? _celebration;
    private int _lockedBefore;

    public RigDayService(AppServices s) => _s = s;

    public bool Enabled => _s.State.Install.RigDayGames;
    public ShowReadyScore? Ready { get; private set; }
    public AlignmentGame? Align { get; private set; }
    public IReadOnlyList<QuestLevel> Quest { get; private set; } = Array.Empty<QuestLevel>();
    public OnTimeStreak Streak { get; } = new();
    public string ReadyWords => Enabled && Ready is { } r ? r.Words : "";
    public string ReadyNext => Enabled && Ready is { } r ? r.Next : "";
    public string QuestWords => Enabled ? BlendQuest.Words(Quest) : "";
    public string AlignWords => Enabled && Align is { } a ? a.Words : "";
    public string StreakWords => Enabled ? Streak.Words : "";

    /// <summary>What is being celebrated right now — null once it is over, or with the games off.</summary>
    public Celebration? Celebration => Enabled && _celebration is { } c && !c.IsOver(DateTime.UtcNow) ? c : null;

    /// <summary>"★ SHOW READY" while a celebration runs, for the desk's lines; "" otherwise.</summary>
    public string CelebrationChip => Celebration is { } c ? "★ " + c.Chip : "";

    /// <summary>A moment marked: the word on the desk, the sweep on the lattice (the outputs take their viewports again), the chip in the status.</summary>
    private void Celebrate(Celebration c)
    {
        _celebration = c;
        _s.Notify("Rig day: " + c.Words);
        _s.Outputs.OnScreensChanged();
    }

    public ActionResult SetEnabled(bool on)
    {
        if (on == Enabled) return ActionResult.Done(on ? "Rig day's games are on." : "Rig day's games are off.");
        _s.BulkEdit(() => _s.State.Install.RigDayGames = on);
        if (!on)
        {
            StopAlign();
            Ready = null;
            Quest = Array.Empty<QuestLevel>();
        }
        else
        {
            _lastFactsUtc = DateTime.MinValue;
            Poll();
        }
        _s.Journal.Record("Desk", on ? "RigDayOn" : "RigDayOff", "", "Done", "");
        return ActionResult.Done(on ? "Rig day's games are on — the show-ready bar on the health line, the alignment game and Blend Quest on the Screens page, the streak on the Run surface." : "Rig day's games are off — nothing of them shows.");
    }

    /// <summary>The arrangement as the Screens page and the audit read it: enabled screens with a display (or a plan) behind them.</summary>
    public List<ArrangedScreen> Arranged()
    {
        var result = new List<ArrangedScreen>();
        foreach (var p in _s.State.Output.Placements)
        {
            if (!p.Enabled) continue;
            var info = _s.Screens.All.FirstOrDefault(x => x.Id == p.ScreenId);
            if (info is null) continue;
            var size = OutputWindowManager.EffectiveSize(p, info);
            result.Add(new ArrangedScreen(p.ScreenId, RasterRect.Create(p.X, p.Y, size.Width, size.Height), p.BlendsOverlaps));
        }
        return result;
    }

    private string NameOf(string screenId)
    {
        var p = _s.State.Output.Placements.FirstOrDefault(x => x.ScreenId == screenId);
        if (p is not null && p.CustomLabel.Length > 0) return p.CustomLabel;
        var info = _s.Screens.All.FirstOrDefault(x => x.Id == screenId);
        return info is not null && info.Label.Length > 0 ? info.Label : screenId;
    }

    /// <summary>On the tick, when the switch is on: the facts every two seconds, the bar, the quest; a word once when the bar fills.</summary>
    public void Poll()
    {
        if (!Enabled) return;
        var now = DateTime.UtcNow;
        if ((now - _lastFactsUtc).TotalSeconds < 2) return;
        _lastFactsUtc = now;
        var arranged = Arranged();
        Quest = BlendQuest.Levels(arranged, id => BlendAudit.For(id, arranged, sid => _s.State.Output.Placements.FirstOrDefault(x => x.ScreenId == sid), NameOf), NameOf);
        var joins = Quest.Where(l => !l.Boss).ToList();
        var solution = _s.Calibration.Solution;
        double? worst = null;
        if (solution is not null && solution.Projectors.Count > 0)
        {
            var residuals = solution.Projectors.Select(p => p.FitResidualPx).Where(r => !double.IsNaN(r)).ToList();
            if (residuals.Count > 0) worst = residuals.Max();
        }
        Ready = ShowReady.Score(new ReadyFacts(
            _s.Outputs.IsLive,
            joins.Count,
            joins.Count(l => !l.Cleared),
            _s.Calibration.Projectors().Count,
            worst,
            _s.Calibration.Applied,
            _s.ShowLock.Locked,
            _s.Metrics.LastReport?.Overall));
        if (_track.Observe(Ready, Quest, now) is { } moment) Celebrate(moment);
    }

    // ---- the alignment game -------------------------------------------------------------------

    /// <summary>ALIGN START &lt;screen&gt;: the solver's mesh for that projector as the targets, the placement's mesh as the start.</summary>
    public ActionResult StartAlign(string screenWord)
    {
        if (!Enabled) return ActionResult.Refused("Rig day's games are off — RIGDAY ON first (Machine page, RIG DAY GAMES).");
        var word = (screenWord ?? "").Trim();
        var placement = _s.State.Output.Placements.FirstOrDefault(p => p.ScreenId == word)
                        ?? _s.State.Output.Placements.FirstOrDefault(p => string.Equals(NameOf(p.ScreenId), word, StringComparison.OrdinalIgnoreCase))
                        ?? (word.Length == 0 && _s.Calibration.Projectors() is { Count: > 0 } projectors ? projectors[0] : null);
        if (placement is null) return ActionResult.Refused(word.Length == 0 ? "ALIGN START <screen> — a projector with a calibration." : $"No screen '{word}'.");
        var solved = _s.Calibration.Solution?.Projectors.FirstOrDefault(p => p.ScreenId == placement.ScreenId);
        if (solved is null || solved.Mesh.Length == 0 && double.IsNaN(solved.FitResidualPx)) return ActionResult.Refused($"No calibration for {NameOf(placement.ScreenId)} — CALIBRATE RUN <camera> (or CALIBRATE DEMO) first; the solver's mesh is the target.");
        var info = _s.Screens.All.FirstOrDefault(x => x.Id == placement.ScreenId);
        var size = info is null ? new SKSizeI(placement.PlannedWidth, placement.PlannedHeight) : OutputWindowManager.EffectiveSize(placement, info);
        if (size.Width < 1 || size.Height < 1) return ActionResult.Refused("That screen has no size to align.");
        Align = AlignmentGame.From(placement.ScreenId, NameOf(placement.ScreenId), placement.WarpMeshColumns, placement.WarpMeshRows, size.Width, size.Height,
            placement.WarpMesh, solved.Mesh, solved.MeshColumns, solved.MeshRows);
        _lockedBefore = Align.LockedCount;
        ShowTargets();
        _s.Journal.Record("Desk", "AlignStart", NameOf(placement.ScreenId), "Done", Align.Words);
        return ActionResult.Done(Align.IsDone ? $"{Align.Words} — nothing to align." : $"Alignment game on {Align.Name}: the arrows drive the lit node (Shift for five), Tab the next, S snaps it, Esc stops. {Align.Words}");
    }

    private void ShowTargets()
    {
        if (Align is null) return;
        _s.RigEditor.ShowLattice(Align.ScreenId, Align.Picked, Align.TargetNodes);
    }

    public ActionResult StopAlign()
    {
        if (Align is null) return ActionResult.Refused("No alignment game running.");
        var words = Align.Words;
        Align = null;
        _s.RigEditor.ShowLattice("", -1);
        return ActionResult.Done($"Alignment game stopped — {words}");
    }

    public ActionResult NextNode()
    {
        if (Align is null) return ActionResult.Refused("No alignment game running.");
        if (!Align.Next()) return ActionResult.Done(Align.Words);
        ShowTargets();
        return ActionResult.Done(Align.Words);
    }

    public ActionResult PrevNode()
    {
        if (Align is null) return ActionResult.Refused("No alignment game running.");
        if (!Align.Prev()) return ActionResult.Done(Align.Words);
        ShowTargets();
        return ActionResult.Done(Align.Words);
    }

    /// <summary>The picked node moved by a step in the output's pixels; the placement's mesh carries it.</summary>
    public ActionResult Nudge(float dx, float dy)
    {
        if (Align is null) return ActionResult.Refused("No alignment game running.");
        var (node, sx, sy) = Align.Nudge(dx, dy);
        return Apply(node, sx, sy);
    }

    /// <summary>The picked node onto its target in one.</summary>
    public ActionResult Snap()
    {
        if (Align is null) return ActionResult.Refused("No alignment game running.");
        var (node, sx, sy) = Align.SnapMove();
        Align.Nudge(0, 0);
        return Apply(node, sx, sy);
    }

    private ActionResult Apply(int node, float dx, float dy)
    {
        var game = Align!;
        var placement = _s.State.Output.Placements.FirstOrDefault(p => p.ScreenId == game.ScreenId);
        if (placement is null) return ActionResult.Refused("The screen left the rig.");
        _s.RigEditor.NudgeMeshPoint(placement, node, dx, dy);
        game.SetCurrent(WarpGrid.Parse(placement.WarpMesh, placement.WarpMeshColumns, placement.WarpMeshRows));
        var locked = game.LockedCount;
        if (locked > _lockedBefore)
        {
            Celebrate(game.IsDone
                ? Celebration.For(CelebrationKind.ProjectorAligned, game.Words, DateTime.UtcNow)
                : Celebration.For(CelebrationKind.NodeLocked, $"node {node + 1} locked — {locked} of {game.NodeCount}.", DateTime.UtcNow));
            if (!game.IsDone) game.Next();
        }
        _lockedBefore = locked;
        ShowTargets();
        return ActionResult.Done(game.Words);
    }

    /// <summary>A GO with the running order's offset at that moment, for the streak — when the games are on.</summary>
    public void RecordGo(TimeSpan? offset)
    {
        if (!Enabled) return;
        Streak.Record(offset, CueTiming.Tolerance);
    }

    public string StatusJson(string? what)
    {
        var align = Align;
        return JsonUtil.SerializeCompact(new
        {
            enabled = Enabled,
            ready = Ready is null ? null : new { done = Ready.Done, total = Ready.Total, full = Ready.IsFull, bar = Ready.Bar, words = Ready.Words, next = Ready.Next, steps = Ready.Steps.Select(s => new { s.Name, s.Done, s.Applies, s.Words }).ToArray() },
            align = align is null ? null : new { screen = align.ScreenId, name = align.Name, picked = align.Picked + 1, nodes = align.NodeCount, locked = align.LockedCount, worst = Math.Round(align.WorstResidual, 2), residual = Math.Round(align.Residual(align.Picked), 2), done = align.IsDone, nudges = align.Nudges, words = align.Words },
            quest = new { words = QuestWords, levels = Quest.Select(l => new { l.Name, l.Cleared, l.Boss, l.Words, members = l.Members }).ToArray() },
            celebration = Celebration is { } cel ? new { kind = cel.Kind.ToString(), words = cel.Words, chip = cel.Chip, phase = Math.Round(cel.Phase(DateTime.UtcNow), 2) } : null,
            streak = new { Streak.Streak, Streak.Best, Streak.Counted, words = StreakWords },
        });
    }
}
