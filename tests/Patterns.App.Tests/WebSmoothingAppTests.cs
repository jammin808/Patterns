using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 68 on the desk: the desk's capture plan reaches an open page live, the look's smoothing
/// choice rides the reconcile, and a page leaving the programme is told the transition's fade and
/// kept for the crossfade.
/// </summary>
public class WebSmoothingAppTests
{
    private static List<FakeWebSource> FakePages(AppServices services)
    {
        var made = new List<FakeWebSource>();
        services.WebIn.SourceFactory = w =>
        {
            var page = new FakeWebSource(w.Target, WebEngine.ParseSize(w.Format), SKColors.Blue);
            made.Add(page);
            return page;
        };
        return made;
    }

    [AvaloniaFact]
    public void TheDeskPlansTheCaptureAndTheLookChoosesTheSmoothing()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var pages = FakePages(services);
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Web;
            vm.State.Pattern.Media.WebUrl = "https://www.youtube.com/embed/abc";
            vm.State.Pattern.Media.WebSmoothing = WebSmoothing.Smooth;
            Dispatcher.UIThread.RunJobs();
            services.WebIn.Reconcile(services.Bus.Current);
            var page = Assert.Single(pages);
            Assert.Equal(WebSmoothing.Smooth, page.Smoothing);

            // The desk's plan: never larger than the page's own viewport, this machine's quality, every frame with the ladder at full.
            Assert.NotNull(services.WebIn.PlanFor);
            var wanted = new MediaLocator.WantedInput("web:https://example.com", MediaLocator.WantedKind.Web, "https://example.com", false, true, 0, "1920x1080");
            var plan = services.WebIn.PlanFor!(wanted, 30);
            Assert.InRange(plan.MaxWidth, 1, 1920);
            Assert.InRange(plan.MaxHeight, 1, 1080);
            Assert.Equal(1, plan.EveryNthFrame);
            var machine = MemoryBudget.ClassOf(MemoryBudget.MachineMB);
            Assert.Equal(WebCapturePolicy.QualityFor(machine), plan.JpegQuality);
            Assert.Equal(FrameSmoother.Bounds.For(machine), plan.Smoothing);

            // The next reconcile applies the plan to the open page live, and the choice changes live too — the same browser throughout.
            vm.State.Pattern.Media.WebSmoothing = WebSmoothing.LowLatency;
            Dispatcher.UIThread.RunJobs();
            services.WebIn.Reconcile(services.Bus.Current);
            Assert.Equal(WebSmoothing.LowLatency, page.Smoothing);
            Assert.NotNull(page.Plan);
            Assert.True(page.PlansApplied >= 1);                                          // the desk's own publishes reconcile too
            Assert.InRange(page.Plan!.Value.MaxWidth, 1, 1920);
            Assert.Single(pages);
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APageLeavingTheProgrammeIsToldTheTransitionsFadeAndKeptForTheCrossfade()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var pages = FakePages(services);
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Web;
            vm.State.Pattern.Media.WebUrl = "https://example.com/video";
            vm.State.Transition.Enabled = true;
            vm.State.Transition.DurationMs = 800;
            Dispatcher.UIThread.RunJobs();
            services.WebIn.Reconcile(services.Bus.Current);
            var page = Assert.Single(pages);
            Assert.Equal(-1, page.LeftAfter);

            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            services.WebIn.Reconcile(services.Bus.Current);
            Assert.Equal(0.8, page.LeftAfter, 6);
            Assert.False(page.Disposed);                                                  // kept for the crossfade's frames; the sweep closes it later
            Assert.Equal(0, services.WebIn.PageCount);

            var cut = new ShowState();
            cut.Transition.Enabled = false;
            Assert.Equal(0, WebEngine.LeaveFadeOf(cut));                                  // a cut leaves at once
            cut.Transition.Enabled = true;
            cut.Transition.DurationMs = 1200;
            Assert.Equal(1.2, WebEngine.LeaveFadeOf(cut), 6);
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }
}
