using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The memory pressure ladder on a desk: the rung from the bytes, its steps taken on the poll and
/// restored when it eases, the words on the Machine page, in STATE and on the super-check's row;
/// and the engine's bounds — retired sources capped, pre-roll held back, preview-only sources
/// refused at critical.
/// </summary>
public class MemoryPressureAppTests
{
    private static readonly DateTime T0 = new(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public void TheLadderTakesItsStepsAtEachRungAndRestoresThemWhenItEases()
    {
        var b = TestApp.Boot();
        var wasExtra = MediaMemory.Extra;
        try
        {
            var (services, _, _) = b;
            var budget = MediaMemory.BudgetBytes(MemoryBudget.MachineMB);
            Assert.True(budget > 0);

            MediaMemory.Extra = () => budget + 1;                                                        // decks and the rest weigh the whole budget: critical
            services.Metrics.Poll();
            Assert.Equal(MemoryPressure.Critical, services.Metrics.Pressure.Level);
            Assert.True(services.Video.PreRollSuppressed);
            Assert.True(services.Video.RefuseNonCriticalOpens);
            Assert.Equal(PdfDeckSource.NarrowWindow, PdfDeckSource.Window);
            Assert.Equal(1, services.Metrics.Pressure.Transitions);
            Assert.Contains("critical pressure at", services.Metrics.Pressure.LastChange);
            Assert.Contains("CRITICAL pressure", services.Metrics.MemoryCeilingLine());
            var state = System.Text.Json.JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("memory");
            Assert.Equal("critical", state.GetProperty("pressure").GetString());
            Assert.Contains("no new preview-only source opened", state.GetProperty("pressureSteps").GetString());
            Assert.True(state.GetProperty("mediaMB").GetDouble() >= state.GetProperty("mediaBudgetMB").GetDouble());
            var row = SuperCheck.Run(services.Metrics.GatherFacts()).Rows.Single(r => r.Item == "Media memory");
            Assert.Equal(CheckLight.Red, row.Light);
            Assert.Contains("at the budget", row.Note);

            MediaMemory.Extra = () => (long)(budget * 0.72);                                             // eases to elevated: the high and critical steps come off
            services.Metrics.Poll();
            Assert.Equal(MemoryPressure.Elevated, services.Metrics.Pressure.Level);
            Assert.False(services.Video.PreRollSuppressed);
            Assert.False(services.Video.RefuseNonCriticalOpens);
            Assert.Equal(PdfDeckSource.DefaultWindow, PdfDeckSource.Window);
            Assert.Equal(CheckLight.Amber, SuperCheck.Run(services.Metrics.GatherFacts()).Rows.Single(r => r.Item == "Media memory").Light);

            MediaMemory.Extra = () => 0;
            services.Metrics.Poll();
            Assert.Equal(MemoryPressure.None, services.Metrics.Pressure.Level);
            Assert.Equal(3, services.Metrics.Pressure.Transitions);
            Assert.Contains("eased from elevated", services.Metrics.Pressure.LastChange);
            Assert.Equal(CheckLight.Green, SuperCheck.Run(services.Metrics.GatherFacts()).Rows.Single(r => r.Item == "Media memory").Light);
            services.Metrics.Poll();                                                                      // the same rung again: no transition
            Assert.Equal(3, services.Metrics.Pressure.Transitions);
        }
        finally
        {
            MediaMemory.Extra = wasExtra;
            PdfDeckSource.Window = PdfDeckSource.DefaultWindow;
            b.Dispose();
        }
    }

    private static ShowSnapshot CaptureSnap(string device, long version)
    {
        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Media.Source = MediaSource.Capture;
        s.Pattern.Media.CaptureDevice = device;
        s.Stingers.StopFadeMs = 1000;                                                                    // a fade long enough that the retired sources overlap
        return new ShowSnapshot { State = s, Version = version };
    }

    [AvaloniaFact]
    public void RetiredSourcesAreBoundedAndTheOldestFadeIsCutShort()
    {
        InputBus.Clear();
        var opened = new List<FakeSource>();
        using var engine = new VideoEngine { SourceFactory = w => { var f = new FakeSource(w); opened.Add(f); return f; } };
        try
        {
            engine.Reconcile(CaptureSnap("Cam A", 1), null, T0);
            engine.Reconcile(CaptureSnap("Cam B", 2), null, T0.AddMilliseconds(50));
            engine.Reconcile(CaptureSnap("Cam C", 3), null, T0.AddMilliseconds(100));
            Assert.Equal(2, engine.RetiredCount);                                                        // A and B fading, C on air
            Assert.Equal(0, engine.RetiredCutShort);
            engine.Reconcile(CaptureSnap("Cam D", 4), null, T0.AddMilliseconds(150));
            Assert.Equal(VideoEngine.MaxRetired, engine.RetiredCount);                                   // B and C fading: A was let go early
            Assert.Equal(1, engine.RetiredCutShort);
            Assert.True(opened[0].Disposed, "the oldest fade was cut short");
            Assert.False(opened[1].Disposed);
            Assert.False(opened[2].Disposed);
            Assert.Equal(0, engine.RetiredBytes);                                                        // fakes hold no pool
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [AvaloniaFact]
    public void AtHighThePreRollIsHeldBackAndAtCriticalAPreviewOnlySourceIsNotOpened()
    {
        InputBus.Clear();
        var opened = new List<FakeSource>();
        using var engine = new VideoEngine { SourceFactory = w => { var f = new FakeSource(w); opened.Add(f); return f; } };
        try
        {
            var clip = Path.Combine(Path.GetTempPath(), "patterns-pressure-" + Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(clip, new byte[16]);
            var program = new ShowSnapshot { State = new ShowState(), Version = 1 };
            var preRoll = new[] { new MediaLocator.WantedInput(InputKeys.Video(clip), MediaLocator.WantedKind.VideoFile, clip, false, false, 100) };

            engine.PreRollSuppressed = true;
            engine.Reconcile(program, null, T0, preRoll);
            Assert.Empty(opened);                                                                        // held back, and counted
            Assert.Equal(1, engine.PreRollHeldBack);
            engine.PreRollSuppressed = false;
            engine.Reconcile(program, null, T0.AddSeconds(1), preRoll);
            Assert.Single(opened);                                                                       // eased: opened and held on its first frame
            Assert.Equal(0, engine.PreRollHeldBack);

            // A source the preview alone wants (the sandbox's capture) is refused at critical; the programme's is always opened.
            var sandbox = CaptureSnap("Preview cam", 2);
            engine.RefuseNonCriticalOpens = true;
            engine.Reconcile(program, sandbox, T0.AddSeconds(2), preRoll);
            Assert.Equal(1, engine.PressureRefused);
            Assert.Null(InputBus.For(InputKeys.Capture("Preview cam")));
            Assert.Contains("Memory pressure", engine.LimitNote);
            engine.Reconcile(CaptureSnap("Air cam", 3), sandbox, T0.AddSeconds(3), preRoll);
            Assert.NotNull(InputBus.For(InputKeys.Capture("Air cam")));                                  // on air: opened whatever the rung
            Assert.Null(InputBus.For(InputKeys.Capture("Preview cam")));
            engine.RefuseNonCriticalOpens = false;
            engine.Reconcile(CaptureSnap("Air cam", 4), sandbox, T0.AddSeconds(4), preRoll);
            Assert.NotNull(InputBus.For(InputKeys.Capture("Preview cam")));                              // eased: the preview's source opens
            Assert.Equal(0, engine.PressureRefused);
            Assert.Equal("", engine.LimitNote);
            File.Delete(clip);
        }
        finally
        {
            InputBus.Clear();
        }
    }
}
