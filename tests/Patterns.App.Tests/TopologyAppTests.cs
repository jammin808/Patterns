using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Live-change safety: an edit that reopens a source landing while the source is on air is
/// staged — the running decoder stays, the words say what is pending and when it applies — and
/// applied when the source leaves the air or the outputs go off; the live-safe part of the edit
/// still applies in place. And the dirty domains audited by behaviour: the monitor rule reaches
/// the inputs, an unrelated section does not.
/// </summary>
public class TopologyAppTests
{
    private static readonly DateTime T0 = new(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);

    private static ShowSnapshot CaptureSnap(string device, long version, bool outputsLive, bool lowLatency = false, string format = "", double volume = 100)
    {
        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Media.Source = MediaSource.Capture;
        s.Pattern.Media.CaptureDevice = device;
        s.Pattern.Media.VolumePct = volume;
        if (format.Length > 0) s.SetCaptureFormat(device, format);
        s.SetCaptureLowLatency(device, lowLatency);
        return new ShowSnapshot { State = s, Version = version, OutputsLive = outputsLive };
    }

    [AvaloniaFact]
    public void AReopenUnderASourceOnAirIsStagedAndAppliesWhenItLeavesTheAir()
    {
        InputBus.Clear();
        var opened = new List<FakeSource>();
        using var engine = new VideoEngine { SourceFactory = w => { var f = new FakeSource(w); opened.Add(f); return f; } };
        try
        {
            engine.Reconcile(CaptureSnap("Cam Link 4K", 1, outputsLive: true), null, T0);
            Assert.Single(opened);
            Assert.Empty(engine.PendingChanges);

            // The low-latency profile lands while the camera is on air: staged, the decoder stays, the words say so.
            engine.Reconcile(CaptureSnap("Cam Link 4K", 2, outputsLive: true, lowLatency: true, volume: 60), null, T0.AddSeconds(1));
            Assert.Single(opened);                                                                       // no reopen
            Assert.False(opened[0].Disposed);
            var pending = Assert.Single(engine.PendingChanges);
            Assert.Equal(TopologyEdit.CaptureLowLatency, pending.Edit);
            Assert.Equal("Cam Link 4K", pending.Target);
            Assert.Equal("Low latency change pending — Cam Link 4K is on air; applies when it leaves the air or the outputs go off air.", engine.PendingNote);
            Assert.Equal(60, opened[0].VolumePct);                                                       // the live-safe part of the edit applied in place

            // A format change as well: the earliest reopen wins the words (one reopen carries both).
            engine.Reconcile(CaptureSnap("Cam Link 4K", 3, outputsLive: true, lowLatency: true, format: "1920x1080@60"), null, T0.AddSeconds(2));
            Assert.Single(opened);
            Assert.Equal(TopologyEdit.CaptureLowLatency, Assert.Single(engine.PendingChanges).Edit);

            // The outputs go off air: the reopen happens, with everything staged, and nothing is pending.
            engine.Reconcile(CaptureSnap("Cam Link 4K", 4, outputsLive: false, lowLatency: true, format: "1920x1080@60"), null, T0.AddSeconds(3));
            Assert.Equal(2, opened.Count);
            Assert.True(opened[0].FadeMs >= 0, "the old decoder retired");
            Assert.True(opened[1].Wanted.LowLatency);
            Assert.Equal("1920x1080@60", opened[1].Wanted.Format);
            Assert.Empty(engine.PendingChanges);
            Assert.Equal("", engine.PendingNote);

            // With the outputs off, a change reopens at once, as before.
            engine.Reconcile(CaptureSnap("Cam Link 4K", 5, outputsLive: false, lowLatency: false, format: "1920x1080@60"), null, T0.AddSeconds(4));
            Assert.Equal(3, opened.Count);
            Assert.Empty(engine.PendingChanges);
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [AvaloniaFact]
    public void ASourceThePreviewAloneShowsReopensAtOnceEvenWithTheOutputsLive()
    {
        InputBus.Clear();
        var opened = new List<FakeSource>();
        using var engine = new VideoEngine { SourceFactory = w => { var f = new FakeSource(w); opened.Add(f); return f; } };
        try
        {
            var program = new ShowSnapshot { State = new ShowState(), Version = 1, OutputsLive = true };
            engine.Reconcile(program, CaptureSnap("Preview cam", 1, outputsLive: true), T0);
            Assert.Single(opened);
            engine.Reconcile(program, CaptureSnap("Preview cam", 2, outputsLive: true, lowLatency: true), T0.AddSeconds(1));
            Assert.Equal(2, opened.Count);                                                               // not the room's picture: reopened at once
            Assert.Empty(engine.PendingChanges);
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [AvaloniaFact]
    public void TheDeskSaysWhatIsPendingOnTheMediaPageInStateAndForCompanion()
    {
        var b = TestApp.Boot();
        var opened = new List<FakeSource>();
        try
        {
            var (services, vm, window) = b;
            services.Video.SourceFactory = w => { var f = new FakeSource(w); opened.Add(f); return f; };
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Capture;
            vm.State.Pattern.Media.CaptureDevice = "IMAG cam";
            Settle(window);
            Assert.Single(opened);
            services.Bus.OutputsLive = true;                                                             // the room is looking at the outputs
            vm.State.SetCaptureLowLatency("IMAG cam", true);                                             // the profile lands through the desk's own publish
            Settle(window);
            Assert.Single(opened);                                                                       // staged: the decoder on air stays
            Assert.Single(services.Video.PendingChanges);
            vm.Media.RefreshActiveInputs();                                                              // the live inputs line, as the page's tick refreshes it
            Assert.Contains("Low latency change pending — IMAG cam is on air", vm.Media.ActiveInputsText);
            var inputs = System.Text.Json.JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("inputs");
            Assert.Contains("Low latency change pending", inputs.GetProperty("pendingNote").GetString());
            Assert.Equal("Low latency", inputs.GetProperty("pending")[0].GetProperty("what").GetString());
            Assert.Equal(1, inputs.GetProperty("mounted").GetInt32());
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheDirtyDomainsAreAuditedByBehaviourTheMonitorReachesTheInputsAndACountdownDoesNot()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            Settle(window);
            var inputsBefore = services.Reconciles.Of("inputs") ?? new ReconcileBudget.Line("inputs", 0, 0, -1, -1, 0);
            var oscBefore = services.Reconciles.Of("osc") ?? new ReconcileBudget.Line("osc", 0, 0, -1, -1, 0);

            vm.State.Monitor.VolumePct = 42;                                                             // the monitor rule: the inputs read it
            Settle(window);
            var inputsAfterMonitor = services.Reconciles.Of("inputs")!;
            Assert.True(inputsAfterMonitor.Runs > inputsBefore.Runs, "the inputs reconciled for a monitor change");
            Assert.True((services.Reconciles.Of("osc")?.Skipped ?? 0) > oscBefore.Skipped, "OSC did not run for a monitor change");

            vm.State.Countdown.Enabled = !vm.State.Countdown.Enabled;                                    // no system reads the countdown: everything skips
            Settle(window);
            var inputsAfterCountdown = services.Reconciles.Of("inputs")!;
            Assert.Equal(inputsAfterMonitor.Runs, inputsAfterCountdown.Runs);
            Assert.True(inputsAfterCountdown.Skipped > inputsAfterMonitor.Skipped, "the inputs were skipped for a countdown change");

            var oscRunsBefore = services.Reconciles.Of("osc")?.Runs ?? 0;
            vm.State.Control.OscPort = vm.State.Control.OscPort + 1;                                 // the remote's config: OSC reads it, the inputs do not
            Settle(window);
            Assert.True((services.Reconciles.Of("osc")?.Runs ?? 0) > oscRunsBefore, "OSC reconciled for its config");
            Assert.Equal(inputsAfterCountdown.Runs, services.Reconciles.Of("inputs")!.Runs);
        }
        finally
        {
            b.Dispose();
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
