using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Effects;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The round-14 ask for the Show panel: VOGs, stingers, lower thirds and people three to a row,
/// every chip lit while its action is on air or in the preview, with a line that says so.
/// </summary>
public class ShowPanelChipsTests
{
    private static Window Panel(MainViewModel vm)
    {
        var host = new Window { DataContext = vm, Width = 900, Height = 2400, Content = new ScrollViewer { Content = new ShowSection() } };
        host.Show();
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        return host;
    }

    private static List<object?> Lit(Window host, string cls)
        => host.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains(cls)).Select(x => x.DataContext).ToList();

    private static List<string?> Lines(Window host)
        => host.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("chipLine") && t.IsEffectivelyVisible).Select(t => t.Text).ToList();

    [AvaloniaFact]
    public void TheChipsSitThreeToARowAndLightWhileTheirActionIsLiveOrInThePreview()
    {
        var b = TestApp.Boot();
        var seats = AudioFakes.TempFile("seats.wav");
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.IsSandboxActive = false;
            vm.ActivePattern.Kind = PatternKind.Grid;
            AudioFakes.Install(b);
            var neon = vm.NewLowerThird("Neon");
            neon.InMs = 0;
            neon.OutMs = 0;
            neon.HoldMs = 0;
            var jane = vm.NewEntry("Jane Doe");
            jane.Role = "Chief Executive";
            var sam = vm.NewEntry("Sam Patel");
            sam.Role = "Head of Product";
            var vog = new StingerItemConfig { Path = seats, Name = "Seats", Kind = StingerKind.Vog };
            var sting = new StingerItemConfig { Source = StingerSource.EffectPulse, PulsePreset = PulsePreset.Strobe, PulseMs = 150, Kind = StingerKind.Sting };
            vm.State.Stingers.Items.Add(vog);
            vm.State.Stingers.Items.Add(sting);
            EffectImpulses.Clear();
            vm.PollNow();   // the panel's VOG and STINGER groups regroup from the library

            // Idle: the second line says what the chip is.
            Assert.Equal("Chief Executive", jane.ChipText);
            Assert.Equal(neon.PersonName, neon.ChipText);

            // Jane into Neon and on air: her chip and the design's light red, her line naming the design.
            Assert.True(vm.ShowEntry(jane, neon).Ok);
            vm.RefreshTallies();
            Assert.True(jane.IsOnAir);
            Assert.False(sam.IsOnAir);
            Assert.Equal("ON AIR · Neon", jane.OnAirText);
            Assert.Equal("ON AIR · Neon", jane.ChipText);
            Assert.True(neon.IsOnAir);
            Assert.Equal("ON AIR", neon.ChipText);

            // The panel: the four groups three to a row; the lit chips are the design and the person, each with its line.
            var host = Panel(vm);
            var grids = host.GetVisualDescendants().OfType<UniformGrid>().Where(g => g.Children.Count > 0).ToList();
            UniformGrid Group(Func<object?, bool> holds) => grids.Single(g => g.Children.All(c => holds(c.DataContext)));
            var vogs = Group(o => o is StingerItemConfig { Kind: StingerKind.Vog });
            var stings = Group(o => o is StingerItemConfig { Kind: StingerKind.Sting });
            var designs = Group(o => o is LowerThirdDesign);
            var people = Group(o => o is LowerThirdEntry);
            Assert.All(new[] { vogs, stings, designs, people }, g => Assert.Equal(3, g.Columns));
            var lit = Lit(host, "air");
            Assert.Equal(2, lit.Count);
            Assert.Contains(neon, lit);
            Assert.Contains(jane, lit);
            Assert.Empty(Lit(host, "pvw"));
            var lines = Lines(host);
            Assert.Contains("ON AIR · Neon", lines);
            Assert.Contains("ON AIR", lines);
            Assert.Contains("Head of Product", lines);          // idle: the role
            Assert.DoesNotContain("Chief Executive", lines);    // Jane's line is her tally now
            host.Close();

            // A VOG fired lights its chip with the seconds on the line; the lower third's lights stay.
            Assert.Equal(ActionStatus.Requested, services.Actions.Execute(ShowActionKind.StingerFire, ActionOrigin.Desk, vog.Id).Status);
            Dispatcher.UIThread.RunJobs();
            vm.RefreshTallies();
            Assert.True(vog.IsOnAir);
            Assert.StartsWith("ON AIR", vog.OnAirText);
            host = Panel(vm);
            var vogChip = host.GetVisualDescendants().OfType<Button>().Single(x => ReferenceEquals(x.DataContext, vog));
            Assert.Contains("air", vogChip.Classes);
            Assert.Contains(vogChip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vog.OnAirText && t.IsEffectivelyVisible);
            Assert.Equal(3, Lit(host, "air").Count);
            host.Close();
            services.Actions.Execute(ShowActionKind.StingerStop, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            vm.RefreshTallies();
            Assert.False(vog.IsOnAir);

            // The preview: Sam into Neon behind EDIT SAFE lights his chip green while Jane stays red on air; the design carries both, its line reading the air.
            vm.IsSandboxActive = true;
            vm.PreviewEntry(sam, neon);
            vm.RefreshTallies();
            Assert.True(sam.IsInPreview);
            Assert.False(sam.IsOnAir);
            Assert.Equal("IN PREVIEW · Neon", sam.ChipText);
            Assert.True(jane.IsOnAir);
            Assert.False(jane.IsInPreview);
            Assert.True(neon.IsInPreview && neon.IsOnAir);
            Assert.Equal("ON AIR", neon.ChipText);
            host = Panel(vm);
            var samChip = host.GetVisualDescendants().OfType<Button>().Single(x => ReferenceEquals(x.DataContext, sam));
            Assert.Contains("pvw", samChip.Classes);
            Assert.DoesNotContain("air", samChip.Classes);
            Assert.Contains("IN PREVIEW · Neon", Lines(host));
            host.Close();

            // TAKE: Sam to air, Jane dark, the preview clear.
            vm.TakeLowerThirdCommand.Execute(null);
            vm.RefreshTallies();
            Assert.True(sam.IsOnAir);
            Assert.False(sam.IsInPreview);
            Assert.False(jane.IsOnAir || jane.IsInPreview);
            Assert.Equal("ON AIR · Neon", sam.ChipText);

            // Hide: every chip dark, the lines back to what the chips are.
            vm.IsSandboxActive = false;
            Assert.True(services.Actions.Execute(ShowActionKind.LowerThirdHide, ActionOrigin.Desk).Ok);
            vm.RefreshTallies();
            Assert.False(sam.IsOnAir);
            Assert.Equal("Head of Product", sam.ChipText);
            Assert.False(neon.IsOnAir);
            Assert.Equal(neon.PersonName, neon.ChipText);

            // A name typed over by hand is nobody's: the design stays lit, the person's chip goes dark.
            Assert.True(services.Actions.Execute(ShowActionKind.LowerThirdShow, ActionOrigin.Desk, neon.Id, jane.Id).Ok);
            vm.RefreshTallies();
            Assert.True(jane.IsOnAir);
            services.AirState.LowerThirds.Active!.PersonName = "Somebody Else";
            vm.RefreshTallies();
            Assert.True(services.AirState.LowerThirds.IsShowing);
            Assert.False(jane.IsOnAir);
            Assert.Equal("Chief Executive", jane.ChipText);
        }
        finally
        {
            EffectImpulses.Clear();
            b.Dispose();
            try { File.Delete(seats); } catch { }
        }
    }
}
