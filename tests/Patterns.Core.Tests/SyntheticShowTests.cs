using System.Globalization;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 84: synthetic shows as a fence. A fixed-seed generator lays out rigs the hand-written tests never
/// drew — two to seven screens of mixed sizes, some dragged flush into canvases, repeaters, locks, screens
/// switched off, own pictures on screens and canvases, ticks, un-armed tiles and a focus — and every one
/// is run through the take resolver under every scope, the JSON round trip, the snapshot clone and the
/// shown-picture rule, with the doctrine's invariants asserted on each: a locked target is never taken, a
/// repeater is never taken, an un-armed one is never taken, a plan that would change nothing is a refusal,
/// the taken, the held and the outside partition the rig, the same rig makes the same plan twice, a clone
/// is equal and apart, and a lock never moves a picture. The seeds are fixed, so a failure names its rig.
/// </summary>
public class SyntheticShowTests
{
    /// <summary>xorshift64 from a fixed seed: the same rig on every machine and every run.</summary>
    private sealed class Noise
    {
        private ulong _s;

        public Noise(ulong seed) => _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed * 0x9E3779B97F4A7C15UL;

        public int Next(int max)
        {
            _s ^= _s << 13;
            _s ^= _s >> 7;
            _s ^= _s << 17;
            return (int)(_s % (ulong)max);
        }

        public bool Chance(int percent) => Next(100) < percent;
    }

    private static readonly (int Width, int Height)[] Sizes = { (1920, 1080), (1280, 720), (3840, 2160), (1920, 1200) };

    private static readonly string[] Kinds = { "main", "confidence", "info", "repeater", "" };

    /// <summary>One synthetic rig: the show, its geometry, and the desk's switches the resolver reads through the targets.</summary>
    private sealed record Synthetic(ulong Seed, ShowState State, RigGeometry Geometry, HashSet<string> Ticked, HashSet<string> Unarmed, string? Focused)
    {
        /// <summary>The rig as a take sees it, derived the way the desk derives it (ShowActions.RigTargets): the geometry's targets in wall order, each with its switches read from the show.</summary>
        public List<TakeTarget> Targets()
        {
            var byId = State.Output.Placements.ToDictionary(p => p.ScreenId, StringComparer.Ordinal);
            var rig = new List<TakeTarget>();
            foreach (var target in Geometry.Targets)
            {
                var canvas = ContentTargets.IsCanvasKey(target);
                var mirror = !canvas && byId[target].MirrorOf.Length > 0 && ContentTargets.IsInRig(State, byId[target].MirrorOf);
                var key = canvas ? TakeShapes.Canvas(ContentTargets.Members(target)) : mirror ? TakeShapes.Mirror(byId[target].MirrorOf) : TakeShapes.Own;
                var shape = canvas ? "a canvas" : mirror ? "a repeater" : "a screen of its own";
                rig.Add(new TakeTarget(target, Geometry.LabelFor(State, target), canvas, mirror, ScreenRoles.IsLocked(State, target), !Unarmed.Contains(target), Ticked.Contains(target),
                    shape, key, !mirror && ContentTargets.UsesOwnPattern(State, target), ScreenRoles.KindOf(State, target)));
            }
            return rig;
        }
    }

    private static Synthetic Make(ulong seed)
    {
        var noise = new Noise(seed);
        var state = new ShowState();
        var displays = new Dictionary<string, ScreenGeometry>(StringComparer.Ordinal);
        var count = 2 + noise.Next(6);
        var x = 0;
        string? previous = null;
        for (var i = 0; i < count; i++)
        {
            var id = "s" + i.ToString(CultureInfo.InvariantCulture);
            var (width, height) = Sizes[noise.Next(Sizes.Length)];
            var flush = previous is not null && noise.Chance(40);                       // dragged flush against the one before: they join a canvas
            if (!flush && previous is not null) x += 100;                              // a gap: a screen of its own
            var role = noise.Next(100) switch { < 50 => ScreenRole.Main, < 70 => ScreenRole.Confidence, < 85 => ScreenRole.Info, _ => ScreenRole.Repeater };
            var placement = new ScreenPlacement { ScreenId = id, X = x, Y = 0, Enabled = noise.Chance(90), Role = role, FollowsCues = !noise.Chance(20) };
            if (role == ScreenRole.Repeater && previous is not null) placement.MirrorOf = previous;
            state.Output.Placements.Add(placement);
            displays[id] = new ScreenGeometry(width, height, "Display " + id);
            x += width;
            previous = id;
        }
        var geometry = RigGeometry.Build(state, displays);
        var kinds = Enum.GetValues<PatternKind>();
        var ticked = new HashSet<string>(StringComparer.Ordinal);
        var unarmed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in geometry.Targets)
        {
            if (noise.Chance(30))
            {
                // An own picture on a screen or a canvas, of any kind — the shown-picture rule must find it.
                var assignment = ContentTargets.EnsureAssignment(state, target);
                assignment.Pattern.Kind = kinds[noise.Next(kinds.Length)];
                ContentTargets.SetOwnPattern(state, target, true);
            }
            if (noise.Chance(40)) ticked.Add(target);
            if (noise.Chance(20)) unarmed.Add(target);
        }
        var focused = noise.Chance(30) ? null : geometry.Targets[noise.Next(geometry.Targets.Count)];
        return new Synthetic(seed, state, geometry, ticked, unarmed, focused);
    }

    private static IEnumerable<(FadeScope Scope, IReadOnlyList<string>? Named)> Scopes(Synthetic s)
    {
        yield return (FadeScope.Everything, null);
        yield return (FadeScope.Focused, null);
        yield return (FadeScope.Ticked, null);
        yield return (FadeScope.Groups, null);
        yield return (FadeScope.Canvases, null);
        yield return (FadeScope.GroupOf(ScreenRole.Main), null);
        yield return (FadeScope.GroupOf(ScreenRole.Confidence), null);
        yield return (FadeScope.GroupOf(ScreenRole.Info), null);
        foreach (var target in s.Geometry.Targets) yield return (new FadeScope(FadeScopeKind.Target, target), new[] { target });
        yield return (new FadeScope(FadeScopeKind.Target, "nowhere"), new[] { "nowhere" });
        yield return (new FadeScope(FadeScopeKind.Screen, "9"), Array.Empty<string>());
    }

    /// <summary>The candidates a scope names, in wall order, or null when the scope names nothing and the plan must be a refusal.</summary>
    private static List<TakeTarget>? Candidates(Synthetic s, IReadOnlyList<TakeTarget> rig, FadeScope scope, IReadOnlyList<string>? named)
    {
        if (scope.IsEverything || (scope.Kind == FadeScopeKind.Focused && s.Focused is null)) return rig.ToList();
        switch (scope.Kind)
        {
            case FadeScopeKind.Focused:
                return rig.Where(t => t.Id == s.Focused).ToList();
            case FadeScopeKind.Ticked:
                return rig.Where(t => t.Ticked).ToList();
            case FadeScopeKind.Groups:
            {
                var kinds = rig.Where(t => t.Ticked && ScreenRoles.IsTakeKind(t.Kind)).Select(t => t.Kind).Distinct(StringComparer.Ordinal).ToList();
                return rig.Where(t => kinds.Contains(t.Kind)).ToList();
            }
            case FadeScopeKind.Group:
                return rig.Where(t => t.Kind == scope.Arg).ToList();
            case FadeScopeKind.Canvases:
                return rig.Where(t => t.Ticked && t.IsCanvas).ToList();
            default:
                return named is null ? null : rig.Where(t => named.Contains(t.Id)).ToList();
        }
    }

    private static string Reason(TakeTarget t) => t.Locked ? TakeHeld.LockedReason : t.IsMirror ? TakeHeld.RepeaterReason : TakeHeld.NotArmedReason;

    private static void CheckPlan(Synthetic s, IReadOnlyList<TakeTarget> rig, FadeScope scope, IReadOnlyList<string>? named)
    {
        var plan = TakePlan.Resolve(rig, scope, s.Focused, named);
        var why = $"seed {s.Seed} · scope {scope.Kind} {scope.Arg} · rig {string.Join(", ", rig.Select(t => t.Id))}";
        var candidates = Candidates(s, rig, scope, named);
        var everything = scope.IsEverything || (scope.Kind == FadeScopeKind.Focused && s.Focused is null);

        // The plan is the same every time the same rig is read.
        Assert.Equal(JsonUtil.SerializeCompact(plan), JsonUtil.SerializeCompact(TakePlan.Resolve(rig, scope, s.Focused, named)));

        // LOCKED means locked; a repeater draws its source; un-armed keeps its picture — whatever the scope says.
        foreach (var t in rig)
        {
            if (t.Locked || t.IsMirror || !t.Armed) Assert.DoesNotContain(t.Id, plan.Taken);
        }

        if (candidates is null || candidates.Count == 0)
        {
            Assert.True(plan.IsRefused, why);
            Assert.NotEmpty(plan.Refusal!);
            Assert.Equal(plan.Refusal, plan.Words);
            Assert.Empty(plan.Taken);
            return;
        }

        var expectedTaken = candidates.Where(t => !t.Locked && !t.IsMirror && t.Armed).Select(t => t.Id).ToList();
        var expectedHeld = candidates.Where(t => t.Locked || t.IsMirror || !t.Armed).Select(t => (t.Id, Reason(t))).ToList();
        var inside = new HashSet<string>(candidates.Select(t => t.Id), StringComparer.Ordinal);
        var expectedOutside = rig.Where(t => !inside.Contains(t.Id)).Select(t => t.Id).ToList();

        Assert.Equal(expectedTaken, plan.Taken);
        Assert.Equal(expectedHeld, plan.Held.Select(h => (h.Id, h.Reason)).ToList());
        Assert.Equal(expectedTaken.Count == 0, plan.IsRefused);
        if (plan.IsRefused)
        {
            // A plan that would change nothing is a refusal that names why — and names a held target when there is one.
            Assert.NotEmpty(plan.Refusal!);
            Assert.Equal(plan.Refusal, plan.Words);
            if (expectedHeld.Count == 1) Assert.Contains(candidates[0].Label, plan.Refusal!, StringComparison.Ordinal);
            return;
        }

        // The taken, the held and the outside partition the rig, each once, in wall order.
        Assert.Equal(expectedOutside, plan.Outside);
        var all = plan.Taken.Concat(plan.Held.Select(h => h.Id)).Concat(plan.Outside).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(rig.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal), all.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(plan.Held.Select(h => h.Id).Concat(plan.Outside), plan.Kept);

        // A scoped take lands on its targets alone; the whole rig is never scoped.
        Assert.Equal(!everything, plan.IsScoped);
        if (everything) Assert.Empty(plan.Outside);

        // The words a key shows are the plan's, and a ticket keeps one label, shape and key per taken target.
        Assert.NotEmpty(plan.Where);
        Assert.StartsWith("→", plan.Words, StringComparison.Ordinal);
        Assert.Equal(plan.Taken.Count, plan.TakenLabels.Count);
        Assert.Equal(plan.Taken.Count, plan.TakenShapes.Count);
        Assert.Equal(plan.Taken.Count, plan.TakenKeys.Count);
        Assert.Equal(rig.Where(t => plan.Taken.Contains(t.Id)).Select(t => t.Label), plan.TakenLabels);
        Assert.Equal(rig.Where(t => plan.Taken.Contains(t.Id)).Select(t => t.ShapeKey), plan.TakenKeys);
        Assert.Equal(rig.Where(t => plan.Taken.Contains(t.Id) && t.KeepsOwn).Select(t => t.Label), plan.KeepsOwnLabels);
        foreach (var h in plan.Held) Assert.Contains($"{h.Label} ({h.Reason})", plan.Words, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    [InlineData(5UL)]
    [InlineData(6UL)]
    [InlineData(7UL)]
    [InlineData(8UL)]
    [InlineData(9UL)]
    [InlineData(10UL)]
    [InlineData(11UL)]
    [InlineData(12UL)]
    public void EveryScopeResolvesByTheRulesOnASyntheticRig(ulong seed)
    {
        var s = Make(seed);
        var rig = s.Targets();
        Assert.NotEmpty(rig);
        Assert.Equal(rig.Count, rig.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rig, t => Assert.Contains(t.Kind, Kinds));
        Assert.All(rig, t => Assert.NotEmpty(t.Label));
        foreach (var (scope, named) in Scopes(s)) CheckPlan(s, rig, scope, named);
    }

    [Fact]
    public void FiveHundredSyntheticRigsResolveByTheRules()
    {
        var canvases = 0;
        var repeaters = 0;
        var refusals = 0;
        for (var seed = 100UL; seed < 600UL; seed++)
        {
            var s = Make(seed);
            var rig = s.Targets();
            canvases += rig.Count(t => t.IsCanvas);
            repeaters += rig.Count(t => t.IsMirror);
            foreach (var (scope, named) in Scopes(s))
            {
                CheckPlan(s, rig, scope, named);
                if (TakePlan.Resolve(rig, scope, s.Focused, named).IsRefused) refusals++;
            }
        }
        // The generator reached the shapes the fence is for — a run that drew no canvas or no repeater would prove little.
        Assert.True(canvases > 50, $"canvases drawn: {canvases}");
        Assert.True(repeaters > 50, $"repeaters drawn: {repeaters}");
        Assert.True(refusals > 100, $"refusals met: {refusals}");
    }

    [Theory]
    [InlineData(21UL)]
    [InlineData(22UL)]
    [InlineData(23UL)]
    [InlineData(24UL)]
    [InlineData(25UL)]
    [InlineData(26UL)]
    [InlineData(27UL)]
    [InlineData(28UL)]
    public void TheModelHoldsOnASyntheticRig(ulong seed)
    {
        var s = Make(seed);
        var state = s.State;

        // The show file round-trips: what is written is what is read, and what is read writes the same.
        var json = JsonUtil.SerializeCompact(state);
        var back = JsonUtil.Deserialize<ShowState>(json);
        Assert.NotNull(back);
        Assert.Equal(json, JsonUtil.SerializeCompact(back!));

        // The frozen clone is equal and apart: a write on it is refused (the snapshot fence), and a write on the live show never reaches it.
        var clone = SnapshotClone.Clone(state);
        Assert.Equal(json, JsonUtil.SerializeCompact(clone));
        Assert.NotSame(state.Output.Placements[0], clone.Output.Placements[0]);
        Assert.Throws<InvalidOperationException>(() => clone.Output.Placements[0].FollowsCues = !clone.Output.Placements[0].FollowsCues);
        var was = state.Output.Placements[0].FollowsCues;
        state.Output.Placements[0].FollowsCues = !was;
        Assert.Equal(was, clone.Output.Placements[0].FollowsCues);
        Assert.NotEqual(json, JsonUtil.SerializeCompact(state));
        state.Output.Placements[0].FollowsCues = was;
        Assert.Equal(json, JsonUtil.SerializeCompact(state));

        // The shown-picture rule: a repeater draws its source's, an own picture is its assignment's, everything else the programme.
        foreach (var target in s.Geometry.Targets)
        {
            var shown = LookService.Shown(state, target);
            Assert.NotNull(shown);
            var resolved = ScreenRoles.ResolveMirror(state, target);
            Assert.True(ContentTargets.IsInRig(state, resolved), $"seed {seed}: {target} resolves to {resolved}");
            if (ContentTargets.UsesOwnPattern(state, resolved)) Assert.Same(state.Independent.Single(a => a.ScreenId == resolved).Pattern, shown);
            else Assert.Same(state.Pattern, shown);
            Assert.Contains(ScreenRoles.KindOf(state, target), Kinds);
            if (ContentTargets.IsCanvasKey(target)) Assert.Equal(target, resolved);
        }
        Assert.Same(state.Pattern, LookService.Shown(state, null));
        Assert.Same(state.Pattern, LookService.Shown(state, ""));
        Assert.Same(state.Pattern, LookService.Shown(state, "nowhere"));

        // A lock never moves a picture: locked, the target shows what it showed; unlocked again, still.
        foreach (var target in s.Geometry.Targets)
        {
            var before = JsonUtil.SerializeCompact(LookService.Shown(state, target));
            ScreenRoles.SetLocked(state, target, true, LookService.Shown(state, target));
            Assert.True(ScreenRoles.IsLocked(state, target), $"seed {seed}: {target} locked");
            Assert.Equal(before, JsonUtil.SerializeCompact(LookService.Shown(state, target)));
            ScreenRoles.SetLocked(state, target, false);
            Assert.False(ScreenRoles.IsLocked(state, target), $"seed {seed}: {target} unlocked");
            Assert.Equal(before, JsonUtil.SerializeCompact(LookService.Shown(state, target)));
        }
    }
}
