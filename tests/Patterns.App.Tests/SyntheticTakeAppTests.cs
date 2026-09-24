using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 84: synthetic shows on the live desk. Eight fixed-seed rigs of two to four screens with roles, locks,
/// un-armed tiles and ticks drawn from the seed; one scoped take each (FOCUSED, TICKED, TICKED GROUPS or a group
/// by kind); the desk's own plan read before the press is the oracle for the air after it — every taken target
/// shows the preview, every held or outside target and the programme keep their picture, and a refused plan
/// moves nothing.
/// </summary>
public class SyntheticTakeAppTests
{
    private sealed class Noise
    {
        private ulong _s;

        public Noise(ulong seed) => _s = seed * 0x9E3779B97F4A7C15UL;

        public int Next(int max)
        {
            _s ^= _s << 13;
            _s ^= _s >> 7;
            _s ^= _s << 17;
            return (int)(_s % (ulong)max);
        }

        public bool Chance(int percent) => Next(100) < percent;
    }

    private static void Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void ScopedTakesOnSyntheticRigsLandOnTheirTargetsAlone()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            var landed = 0;
            var refused = 0;
            for (var seed = 1UL; seed <= 8; seed++)
            {
                var noise = new Noise(seed);
                var count = 2 + noise.Next(3);
                var fakes = new List<ScreenInfo>();
                for (var i = 0; i < count; i++) fakes.Add(new ScreenInfo("t" + i, "Screen " + i, new PixelRect(i * 3000, 0, 1920, 1080), 1.0, i == 0, i));

                // The rig on the desk, fresh for the seed: the programme a grid, the preview another kind.
                vm.IsSandboxActive = false;
                vm.State.Independent.Clear();
                services.Screens.All.Clear();
                foreach (var s in fakes) services.Screens.All.Add(s);
                vm.State.Output.Placements.Clear();
                vm.ReconcilePlacements(fakes);
                foreach (var p in vm.State.Output.Placements)
                {
                    p.Enabled = true;
                    p.Role = noise.Next(100) switch { < 60 => ScreenRole.Main, < 80 => ScreenRole.Confidence, _ => ScreenRole.Info };
                }
                vm.RebuildSwitcherTiles();
                Dispatcher.UIThread.RunJobs();
                const PatternKind programme = PatternKind.Grid;
                var preview = seed % 2 == 0 ? PatternKind.ColorBars : PatternKind.Focus;
                vm.State.Pattern.Kind = programme;
                vm.IsSandboxActive = true;
                vm.State.Pattern.Kind = preview;
                Settle();

                SwitcherTile Tile(string id) => vm.SwitcherTiles.Single(t => t.TargetId == id);
                var ids = fakes.Select(f => f.Id).ToList();
                foreach (var id in ids)
                {
                    if (noise.Chance(25)) Tile(id).IsLocked = true;
                    if (noise.Chance(20)) Tile(id).IsArmed = false;
                    if (noise.Chance(50)) Tile(id).IsSendTarget = true;
                }
                Dispatcher.UIThread.RunJobs();
                vm.SelectTileCommand.Execute(Tile(ids[noise.Next(ids.Count)]));
                vm.SelectedTakeScope = vm.TakeScopes[1 + noise.Next(6)];                // FOCUSED, TICKED, TICKED GROUPS, MAIN, CONFIDENCE, INFO
                Dispatcher.UIThread.RunJobs();

                // The desk's own plan, read before the press, is the oracle for the air after it.
                var scope = FadeScope.Parse(vm.SelectedTakeScope.Words) ?? FadeScope.Everything;
                var plan = services.Actions.PlanTake(scope);
                var version = services.Bus.Current.Version;
                vm.TakeCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                var air = services.Bus.Current;
                var why = $"seed {seed} · {vm.SelectedTakeScope.Label} · {plan.Words} · status: {vm.StatusMessage}";
                if (plan.IsRefused)
                {
                    refused++;
                    Assert.Equal(version, air.Version);
                    Assert.Equal(programme, air.State.Pattern.Kind);
                    foreach (var id in ids) Assert.True(programme == air.PatternFor(id).Kind, why);
                    Assert.Contains(plan.Refusal!, vm.StatusMessage, StringComparison.Ordinal);
                }
                else
                {
                    landed++;
                    Assert.NotEqual(version, air.Version);
                    Assert.True(plan.IsScoped, why);                                          // none of the six scopes is the whole rig
                    Assert.Equal(programme, air.State.Pattern.Kind);                           // a scoped take never moves the programme
                    foreach (var id in plan.Taken) Assert.True(preview == air.PatternFor(id).Kind, why);
                    foreach (var id in plan.Kept) Assert.True(programme == air.PatternFor(id).Kind, why);
                    foreach (var id in plan.Taken) Assert.True(Tile(id).IsOwn, why);            // it landed as the target's own picture
                }
            }
            Assert.True(landed >= 3, $"takes landed: {landed}");
            Assert.True(refused >= 1, $"takes refused: {refused}");
        }
        finally
        {
            b.Dispose();
        }
    }
}
