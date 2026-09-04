using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Particles;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The Particles page: scenes in packs, a chip applying a scene, and the operator's own scenes beside them.</summary>
public class ParticlesPageTests
{
    [AvaloniaFact]
    public void ScenesComeInPacksAndAChipAppliesOne()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            Assert.Equal(ParticlePresets.Categories, vm.ParticlePackGroups.Select(g => g.Category).ToArray());
            Assert.Equal(ParticlePresets.Packs.Count, vm.ParticlePackGroups.Sum(g => g.Chips.Count));
            var awards = vm.ParticlePackGroups.Single(g => g.Category == "Awards");
            Assert.Contains(awards.Chips, c => c.Name == "Gold dust");

            vm.ActivePattern.Kind = PatternKind.Particles;
            var embers = vm.ParticlePackGroups.Single(g => g.Category == "Classic").Chips.Single(c => c.Name == "Embers");
            vm.ApplyParticleChipCommand.Execute(embers);
            Assert.Equal(ParticleEmitter.BottomEdge, vm.ActivePattern.Particles.Emitter);
            Assert.Equal("Embers", vm.ActivePattern.Particles.Preset);

            // A saved particle preset becomes a Custom chip; a saved pattern of another kind does not.
            vm.ActivePattern.Particles.Count = 4321;
            vm.NewPresetName = "My sparkle";
            vm.SavePresetCommand.Execute(null);
            vm.ActivePattern.Kind = PatternKind.Grid;
            vm.NewPresetName = "A grid";
            vm.SavePresetCommand.Execute(null);
            var custom = vm.ParticlePackGroups.Single(g => g.Category == "Custom");
            Assert.Equal("My sparkle", Assert.Single(custom.Chips).Name);

            vm.ActivePattern.Kind = PatternKind.Particles;
            vm.ActivePattern.Particles.Count = 10;
            vm.ApplyParticleChipCommand.Execute(custom.Chips[0]);
            Assert.Equal(4321, vm.ActivePattern.Particles.Count);
            Assert.Equal(ParticleEmitter.BottomEdge, vm.ActivePattern.Particles.Emitter);

            var host = new Window { DataContext = vm, Width = 900, Height = 1400, Content = new ScrollViewer { Content = new ParticlesSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = host.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var chips = host.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("chip")).ToList();
            Assert.True(chips.Count >= ParticlePresets.Packs.Count + 1, $"{chips.Count} chips");
            Assert.Contains(chips, c => Equals(c.Content, "Starcloth"));
            Assert.Contains(chips, c => Equals(c.Content, "My sparkle"));
            host.Close();
        }
        finally
        {
            b.Dispose();
        }
    }
}
