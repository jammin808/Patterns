using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Services;

/// <summary>
/// A display's raw (unrotated) pixel size and the name the operator reads. Carried on the
/// snapshot so Core can size and name content targets without knowing about Avalonia.
/// </summary>
public readonly record struct ScreenGeometry(int Width, int Height, string Label)
{
    public SKSizeI Size => new(Width, Height);
}

/// <summary>Which content target a sink draws, which slice of it, and at what pixel size.</summary>
public readonly record struct TargetViewport(
    string? TargetId, SKSizeI ReferenceSize, SKPointI Origin, SKSizeI ViewportSize)
{
    /// <summary>The shape a miniature of this viewport takes; 16:9 when the size is degenerate.</summary>
    public float Aspect => ViewportSize.Height > 0
        ? ViewportSize.Width / (float)ViewportSize.Height
        : 16f / 9f;
}

/// <summary>
/// The rig's pixel geometry, resolved once per snapshot: which placements have a size behind
/// them, what each is worth in arrangement space, which of them form joined canvases, and
/// therefore the size, slice and name of every content target. One implementation, so the
/// wall, the output windows, the arrange overview and the multiview tell the same truth.
/// Immutable once built; safe to read from any render thread.
/// <c>Patterns.App.Services.Rig</c> is the App-side wrapper over this same code.
/// </summary>
public sealed class RigGeometry
{
    /// <summary>The shape everything falls back to when nothing real is behind a target: 16:9.</summary>
    public static readonly SKSizeI FallbackTargetSize = new(1920, 1080);

    /// <summary>No display has been measured — a headless render, or the moment before enumeration.</summary>
    public static readonly IReadOnlyDictionary<string, ScreenGeometry> NoDisplays =
        new Dictionary<string, ScreenGeometry>(StringComparer.Ordinal);

    /// <summary>A rig with no screens: every question answers 16:9, none of them throws.</summary>
    public static readonly RigGeometry Empty = new();

    private readonly ArrangedScreen[] _screens;
    private readonly string[] _displayLabels;
    private readonly string[] _targets;
    private readonly Dictionary<string, string[]> _members;
    private readonly Dictionary<string, string> _letters;
    private readonly Dictionary<string, string> _canvasOf;
    private readonly Dictionary<string, SKPointI> _originOf;
    private readonly SKSizeI _programSize;

    private RigGeometry()
    {
        _screens = Array.Empty<ArrangedScreen>();
        _displayLabels = Array.Empty<string>();
        _targets = Array.Empty<string>();
        _members = new Dictionary<string, string[]>(StringComparer.Ordinal);
        _letters = new Dictionary<string, string>(StringComparer.Ordinal);
        _canvasOf = new Dictionary<string, string>(StringComparer.Ordinal);
        _originOf = new Dictionary<string, SKPointI>(StringComparer.Ordinal);
        _programSize = FallbackTargetSize;
    }

    private RigGeometry(
        ArrangedScreen[] screens,
        string[] displayLabels,
        string[] targets,
        Dictionary<string, string[]> members,
        Dictionary<string, string> letters,
        Dictionary<string, string> canvasOf,
        Dictionary<string, SKPointI> originOf)
    {
        _screens = screens;
        _displayLabels = displayLabels;
        _targets = targets;
        _members = members;
        _letters = letters;
        _canvasOf = canvasOf;
        _originOf = originOf;
        // The program takes the first target's shape, so the panes and the tiles show something
        // true. Measured last: SizeOf reads the arrays above and never the size being assigned.
        _programSize = targets.Length > 0 ? SizeOf(targets[0]) : FallbackTargetSize;
    }

    /// <summary>
    /// Resolves the geometry of the placements in <paramref name="state"/> against the measured
    /// displays. A planned screen is sized from the show file, a real one from the table, and a
    /// placement with neither is skipped — exactly the set the wall draws.
    /// </summary>
    public static RigGeometry Build(ShowState state, IReadOnlyDictionary<string, ScreenGeometry> displays)
    {
        var placed = new List<ArrangedScreen>();
        var labels = new List<string>();
        foreach (var p in state.Output.Placements)
        {
            SKSizeI raw;
            string label;
            if (p.Planned)
            {
                // The model first: pre-programming has to be exact with nothing plugged in.
                raw = new SKSizeI(p.PlannedWidth, p.PlannedHeight);
                label = displays.TryGetValue(p.ScreenId, out var planned) ? planned.Label : "";
            }
            else if (displays.TryGetValue(p.ScreenId, out var known))
            {
                raw = known.Size;
                label = known.Label;
            }
            else
            {
                continue; // no display behind it — the wall does not draw it either
            }

            // A size of zero would make every miniature's scale infinite.
            raw = new SKSizeI(Math.Max(1, raw.Width), Math.Max(1, raw.Height));
            var eff = EffectiveSize(p, raw);
            // Enabled is not geometry: the wall shows switched-off targets too.
            placed.Add(new ArrangedScreen(p.ScreenId, SKRectI.Create(p.X, p.Y, eff.Width, eff.Height), p.BlendAuto));
            labels.Add(label);
        }

        // Wall order, and a deterministic one: ScreenLayout.Groups ends in unstable List<T>.Sort
        // calls, and the canvas letters A/B address the wall, the remote GROUP verb and saved
        // Companion presets. OrderBy/ThenBy is stable, matching Rig.OrderedLivePlacements.
        var order = Enumerable.Range(0, placed.Count)
            .OrderBy(i => placed[i].Rect.Left).ThenBy(i => placed[i].Rect.Top)
            .ToArray();
        var screens = new ArrangedScreen[order.Length];
        var displayLabels = new string[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            screens[i] = placed[order[i]];
            displayLabels[i] = labels[order[i]];
        }

        var canvases = ScreenLayout.Groups(screens)
            .Where(g => g.Count > 1)
            .OrderBy(g => ScreenLayout.Union(g).Left).ThenBy(g => ScreenLayout.Union(g).Top)
            .ToList();

        var members = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var letters = new Dictionary<string, string>(StringComparer.Ordinal);
        var canvasOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var originOf = new Dictionary<string, SKPointI>(StringComparer.Ordinal);
        var targets = new List<string>();

        for (var i = 0; i < canvases.Count; i++)
        {
            var group = canvases[i];
            var key = CanvasNameConfig.KeyFor(group.Select(m => m.Id));
            var union = ScreenLayout.Union(group);
            members[key] = group.Select(m => m.Id).ToArray();
            letters[key] = ((char)('A' + i)).ToString();
            foreach (var m in group)
            {
                canvasOf[m.Id] = key;
                originOf[m.Id] = new SKPointI(m.Rect.Left - union.Left, m.Rect.Top - union.Top);
            }
            targets.Add(key);
        }
        foreach (var s in screens)
        {
            if (!canvasOf.ContainsKey(s.Id)) targets.Add(s.Id);
        }

        return new RigGeometry(screens, displayLabels, targets.ToArray(), members, letters, canvasOf, originOf);
    }

    /// <summary>Every placement with a size behind it, in wall order: left to right, then top down.</summary>
    public IReadOnlyList<ArrangedScreen> Screens => _screens;

    /// <summary>Every content target, in wall order: joined canvases by member key, then stand-alone screens.</summary>
    public IReadOnlyList<string> Targets => _targets;

    /// <summary>A canvas's member ids in wall order; empty when this key names no canvas here.</summary>
    public IReadOnlyList<string> MembersOf(string canvasKey)
        => _members.TryGetValue(canvasKey, out var m) ? m : Array.Empty<string>();

    /// <summary>The wall letter of a canvas ("A", "B"…); empty when this key names no canvas here.</summary>
    public string LetterOf(string canvasKey)
        => _letters.TryGetValue(canvasKey, out var letter) ? letter : "";

    /// <summary>The target a screen renders through: the canvas it joined, or the screen itself.</summary>
    public string TargetOf(string screenId)
        => _canvasOf.TryGetValue(screenId, out var key) ? key : screenId;

    /// <summary>The 1-based wall number of a screen; 0 when it has no display behind it.</summary>
    public int NumberOf(string screenId)
    {
        for (var i = 0; i < _screens.Length; i++)
        {
            if (_screens[i].Id == screenId) return i + 1;
        }
        return 0;
    }

    /// <summary>The display's own name (OS or planned); empty when the screen is not in the rig.</summary>
    public string DisplayLabel(string screenId)
    {
        for (var i = 0; i < _screens.Length; i++)
        {
            if (_screens[i].Id == screenId) return _displayLabels[i];
        }
        return "";
    }

    /// <summary>
    /// The pixel size of a content target: a canvas's union, a screen's effective (rotation-aware)
    /// size, the program (null) taking the first target's shape. Never zero on either axis.
    /// </summary>
    public SKSizeI SizeOf(string? targetId)
    {
        if (targetId is null) return _programSize;
        if (ContentTargets.IsCanvasKey(targetId))
        {
            // Not required to be one group: a canvas dragged apart still measures its members.
            var ids = ContentTargets.Members(targetId);
            SKRectI? union = null;
            foreach (var s in _screens)
            {
                if (Array.IndexOf(ids, s.Id) < 0) continue;
                union = union is { } u ? SKRectI.Union(u, s.Rect) : s.Rect;
            }
            return union is { } r ? new SKSizeI(r.Width, r.Height) : FallbackTargetSize;
        }
        foreach (var s in _screens)
        {
            if (s.Id == targetId) return new SKSizeI(s.Rect.Width, s.Rect.Height);
        }
        return FallbackTargetSize;
    }

    /// <summary>The whole of a target: what a monitor of it, or an NDI sender fed by it, draws.</summary>
    public TargetViewport ViewportForTarget(string? targetId)
    {
        var size = SizeOf(targetId);
        return new TargetViewport(targetId, size, default, size);
    }

    /// <summary>
    /// What a multiview tile of this id draws: the program for an empty id, a member screen's own
    /// slice of the canvas it joined, and otherwise the whole target.
    /// </summary>
    public TargetViewport ViewportForTile(string targetId)
    {
        if (targetId.Length == 0)
        {
            return new TargetViewport(null, _programSize, default, _programSize);
        }
        if (_canvasOf.TryGetValue(targetId, out var key))
        {
            return new TargetViewport(key, SizeOf(key), _originOf[targetId], SizeOf(targetId));
        }
        var size = SizeOf(targetId);
        return new TargetViewport(targetId, size, default, size);
    }

    /// <summary>
    /// The words the wall uses for a target — "A · Main wall", "3 · Lobby". With no geometry
    /// behind the id it falls back to the multiview's older rule, so a headless render reads
    /// exactly as it did before the rig was carried on the snapshot.
    /// </summary>
    public string LabelFor(ShowState state, string targetId)
    {
        if (ContentTargets.IsCanvasKey(targetId))
        {
            var letter = LetterOf(targetId);
            var stored = state.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == targetId)?.Name;
            var name = string.IsNullOrWhiteSpace(stored)
                ? (letter.Length > 0 ? $"Canvas {letter}" : "Canvas")
                : stored!;
            return letter.Length > 0 ? $"{letter} · {name}" : name;
        }
        var n = NumberOf(targetId);
        if (n > 0)
        {
            var p = state.Output.Placements.FirstOrDefault(x => x.ScreenId == targetId);
            var label = p is not null && p.CustomLabel.Length > 0 ? p.CustomLabel : DisplayLabel(targetId);
            return $"{n} · {(label.Length > 0 ? label : targetId)}";
        }
        // No geometry for this id: the older rule, verbatim.
        var ordered = state.Output.Placements.OrderBy(x => x.X).ThenBy(x => x.Y).ToList();
        var placement = ordered.FirstOrDefault(x => x.ScreenId == targetId);
        if (placement is null) return "SCREEN";
        var i = ordered.IndexOf(placement) + 1;
        return placement.CustomLabel.Length > 0 ? $"{i} · {placement.CustomLabel}" : $"SCREEN {i}";
    }

    /// <summary>The size a screen occupies in arrangement space (swapped for portrait rotations).</summary>
    public static SKSizeI EffectiveSize(ScreenPlacement p, SKSizeI raw)
        => p.Rotation is OutputRotation.Rot90 or OutputRotation.Rot270
            ? new SKSizeI(raw.Height, raw.Width)
            : raw;
}
