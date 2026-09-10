using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.App.Views.Controls;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 22 on a live desk: a restart of any kind puts the audience's picture back where it was,
/// and a picture the operator changes reaches the switcher on the frame that changes it.
/// </summary>
public class RestartAndSwitcherTests
{
    private static TestApp.Booted Boot(Action<string>? prepare = null)
        => TestApp.Boot("patterns-restart-", prepare);

    private static void GoLive(MainViewModel vm)
    {
        vm.GoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    // ---- the record follows the air, by construction -----------------------------------------

    [AvaloniaFact]
    public void ArmingEditSafeIsEnoughToPutTheAirInTheRecord()
    {
        var b = Boot();
        try
        {
            var (services, vm, _) = b;
            vm.ActivePattern.Kind = PatternKind.LedWall;
            GoLive(vm);
            Assert.True(services.Outputs.IsLive);

            // EDIT SAFE goes on and the operator builds the next look. Nothing here is an
            // air-targeted edit, so nothing used to tell the record the desk had split — and a
            // crash then put this half-built preview on the audience's screens.
            vm.IsSandboxActive = true;
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();

            var saved = services.Recovery.Read();
            Assert.NotNull(saved);
            Assert.True(saved!.Sandboxed);
            Assert.NotNull(saved.Air);
            Assert.Equal(PatternKind.LedWall, saved.Air!.Pattern.Kind);   // what the audience was seeing
            Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);   // not the operator's edit
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRecordFollowsATakeAndAPerScreenSend()
    {
        var b = Boot();
        try
        {
            var (services, vm, _) = b;
            vm.ActivePattern.Kind = PatternKind.Grid;
            GoLive(vm);
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();

            // TAKE: the record must name the picture that just went to air, not the one before it.
            vm.ActivePattern.Kind = PatternKind.LedWall;
            vm.SandboxSendAllCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSandboxActive); // EDIT SAFE re-arms
            var afterTake = services.Recovery.Read();
            Assert.Equal(PatternKind.LedWall, afterTake!.Air!.Pattern.Kind);

            // A per-screen SEND moves the audience's picture without the split opening or
            // closing and without going through the air seam — the case a hand-maintained
            // dirty flag missed, and the reason the record now watches the program itself.
            var tile = vm.SwitcherTiles.First(t => !t.IsProgramTile);
            vm.ActivePattern.Kind = PatternKind.Focus;
            tile.IsSendTarget = true;
            vm.SandboxSendSelectedCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var afterSend = services.Recovery.Read();
            var sent = afterSend!.Air!.Independent.FirstOrDefault(a => a.ScreenId == tile.TargetId);
            Assert.NotNull(sent);
            Assert.Equal(PatternKind.Focus, sent!.Pattern.Kind);
            Assert.Equal(PatternKind.LedWall, afterSend.Air.Pattern.Kind); // every other screen kept the take

            // And leaving EDIT SAFE hands the record back to the settings file rather than
            // leaving a look behind that a later crash would restore over live content.
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            var unsplit = services.Recovery.Read();
            Assert.Equal(false, unsplit!.Sandboxed);
            Assert.Null(unsplit.Air);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADeliberateRestartKeepsTheAirAndTheCallersPlace()
    {
        var b = Boot();
        try
        {
            var (services, vm, _) = b;
            vm.ActivePattern.Kind = PatternKind.LedWall;
            GoLive(vm);
            vm.IsSandboxActive = true;
            vm.ActivePattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();

            var before = services.Recovery.Read();
            Assert.Equal(PatternKind.LedWall, before!.Air!.Pattern.Kind);

            // The Machine page's RESTART used to write a two-field record — no air, no place —
            // so the restart the operator asked for lost more than one they did not.
            services.PrepareRestart();
            var after = services.Recovery.Read();
            Assert.NotNull(after);
            Assert.True(after!.Sandboxed);
            Assert.NotNull(after.Air);
            Assert.Equal(PatternKind.LedWall, after.Air!.Pattern.Kind);
            Assert.True(after.Live);
        }
        finally
        {
            b.Dispose();
        }
    }

    // ---- and the restart puts it back ---------------------------------------------------------

    /// <summary>A record left by a split desk: the program on one picture, the settings file on another.</summary>
    private static void WriteSplitRecord(string dir, Action<ShowState>? shapeAir = null, IReadOnlyList<string>? black = null)
    {
        var air = new ShowState();
        air.Pattern.Kind = PatternKind.LedWall;
        air.Brand.PrimaryColor = "#112233";
        shapeAir?.Invoke(air);
        new RecoveryStore(dir).Write(new RecoverySnapshot(
            true, false, DateTime.UtcNow, Air: air, Sandboxed: true, BlackTargets: black));
    }

    [AvaloniaFact]
    public void ARestartComesBackSplitWithTheProgramOnAirAndThePreviewStillTheOperatorsEdit()
    {
        var b = Boot(dir => WriteSplitRecord(dir));
        try
        {
            var (services, vm, _) = b;
            // The show as saved is the untaken preview, and this desk does not arm EDIT SAFE by
            // itself — the record has to say the desk was split, or the two collapse into one.
            vm.State.Switcher.EditSafeByDefault = false;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            vm.State.Brand.PrimaryColor = "#FFAA00";
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Sandbox.Active);

            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            Assert.True(services.Sandbox.Active);
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.State.Pattern.Kind);      // the audience's picture
            Assert.Equal(PatternKind.ColorBars, services.Bus.Sandbox!.State.Pattern.Kind);   // the operator's, still theirs
            Assert.True(services.Outputs.IsLive);

            // The brand kit is the proof the record is the program and not a look of it: a look
            // carries no kit, so the client's wall used to repaint in the next client's colours.
            Assert.Equal("#112233", services.Bus.Current.State.Brand.PrimaryColor);
            Assert.Equal("#FFAA00", services.Bus.Sandbox!.State.Brand.PrimaryColor);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AScreenTheOperatorFadedOutComesBackDark()
    {
        var b = Boot(dir => WriteSplitRecord(dir, black: new[] { "screen-2" }));
        try
        {
            var (services, vm, _) = b;
            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("screen-2", services.Bus.BlackTargets);
            Assert.True(services.Bus.Current.IsBlack("screen-2"));
            Assert.False(services.Bus.Current.IsBlack("screen-1"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStreamThatWasUpIsNamedRatherThanStartedBehindTheOperatorsBack()
    {
        var b = Boot(dir =>
        {
            var air = new ShowState();
            air.Pattern.Kind = PatternKind.LedWall;
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Air: air, Sandboxed: true, Streaming: true));
        });
        try
        {
            var (services, vm, _) = b;
            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            // The stream is not a Program output and it pushes to somewhere public: the desk
            // says it was up and leaves the decision with the operator.
            Assert.False(services.State.Stream.Active);
            Assert.Contains("press STREAM", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADeskThatWasEditingLiveComesBackEditingLive()
    {
        var b = Boot(dir =>
        {
            var air = new ShowState();
            air.Pattern.Kind = PatternKind.LedWall;
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Air: air, Sandboxed: false));
        });
        try
        {
            var (services, vm, _) = b;
            services.StartDefaultSandbox();   // the show's own default arms EDIT SAFE on this start
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Sandbox.Active);

            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            // The record says the operator was editing live. Coming back split would swallow
            // their next change without a word, and they would find out when the caller asked
            // why nothing had happened.
            Assert.False(services.Sandbox.Active);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARecordAnOlderBuildLeftBehindStillPutsTheShowBack()
    {
        var b = Boot(dir =>
        {
            var onAir = new ShowState();
            onAir.Pattern.Kind = PatternKind.LedWall;
            new RecoveryStore(dir).Write(live: true, audioPlaying: false, airLook: LookService.Capture(onAir));
        });
        try
        {
            var (services, vm, _) = b;
            // An older build's record cannot say whether the desk was split, so the desk acts
            // on no opinion it does not have and leaves EDIT SAFE as this start armed it.
            services.StartDefaultSandbox();
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();

            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PatternKind.LedWall, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.ColorBars, services.Bus.Sandbox!.State.Pattern.Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRecordSurvivesUntilTheRecoveryThatNeedsItHasRun()
    {
        var b = Boot(dir => WriteSplitRecord(dir));
        try
        {
            var (services, vm, _) = b;

            // Booting the desk publishes many times with nothing live. The ordinary bookkeeping
            // would delete the record as "nothing live" on the first of them — and a second
            // fault inside the same start would then find nothing at all, which is the one
            // failure the record exists for.
            vm.State.Pattern.Kind = PatternKind.Focus;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(services.Recovery.Read());

            services.RecoverWhenReady(vm);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.LedWall, services.Bus.Current.State.Pattern.Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheDeskComesBackKnowingWhatItCallsThePicture()
    {
        var b = Boot(dir =>
        {
            var air = new ShowState();
            air.Pattern.Kind = PatternKind.LedWall;
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Air: air, Sandboxed: true,
                AirLabel: "03.020 Five-minute call", AirLookId: "look-walkin", PreviousAirLookId: "look-doors"));
        });
        try
        {
            var (services, vm, _) = b;
            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            // Without these the wall is right and the desk claims to know nothing about it: the
            // LIVE strip reads "—", no look row lights, and LOOK BACK refuses.
            Assert.Equal("03.020 Five-minute call", services.AirLabel);
            Assert.Equal("look-walkin", services.AirLookId);
            Assert.Equal("look-doors", services.PreviousAirLookId);   // and LOOK BACK still goes back
        }
        finally
        {
            b.Dispose();
        }
    }

    private sealed class DeadProbe : IProcessProbe
    {
        public readonly Dictionary<int, long> Alive = new();
        public readonly List<int> Killed = new();

        public long? StartTicks(int pid) => Alive.TryGetValue(pid, out var t) ? t : null;

        public string ExePath(int pid) => Environment.ProcessPath ?? "Patterns";

        public bool Kill(int pid)
        {
            Killed.Add(pid);
            Alive.Remove(pid);
            return true;
        }
    }

    /// <summary>A previous run wedged with its windows still playing, which this start takes back.</summary>
    private static void HungPredecessor(string dir)
    {
        var silent = DateTime.UtcNow - OutputOwnership.HeartbeatSilent - TimeSpan.FromSeconds(5);
        new OutputOwnerStore(dir).Write(new OutputOwner(4242, 1000, silent, silent,
            new[] { "Main wall" }, Environment.MachineName, Environment.ProcessPath ?? "Patterns"));
        var probe = new DeadProbe();
        probe.Alive[4242] = 1000;
        OutputTakeover.ClaimAtStart(dir, enabled: true, probe, wait: _ => { });
    }

    [AvaloniaFact]
    public void ATakeoverWithNoRecordSaysTheScreensCarryAGuess()
    {
        var b = Boot(HungPredecessor);
        try
        {
            var (services, vm, _) = b;
            Assert.True(services.Takeover.TookOver);
            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            // The screens carry the settings file, which while EDIT SAFE was open is the untaken
            // preview. That is the best this branch can do — and it is said, not dressed up.
            Assert.Contains("no record of what was on air", vm.StatusMessage);
            Assert.Contains("Check PGM", vm.StatusMessage);
            Assert.True(services.Outputs.IsLive);
        }
        finally
        {
            OutputTakeover.Reset();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeoverActsOnItsRecordHoweverOldItIs()
    {
        var b = Boot(dir =>
        {
            var air = new ShowState();
            air.Pattern.Kind = PatternKind.LedWall;
            // A desk that went live yesterday morning and never moved the air wrote its record
            // yesterday morning. Off a takeover that would be an old day; on one it is proof.
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow.AddHours(-20), Air: air, Sandboxed: true));
            HungPredecessor(dir);
        });
        try
        {
            var (services, vm, _) = b;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();

            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(PatternKind.LedWall, services.Bus.Current.State.Pattern.Kind);
            Assert.DoesNotContain("no record", vm.StatusMessage);
        }
        finally
        {
            OutputTakeover.Reset();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeoversWordsSurviveTheRunSurfaceItOpens()
    {
        var b = Boot(dir =>
        {
            var air = new ShowState();
            air.Pattern.Kind = PatternKind.LedWall;
            new RecoveryStore(dir).Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Air: air, Sandboxed: true,
                Run: new RunPlace(null, null, null, Array.Empty<CueExecutionRecord>())));
            HungPredecessor(dir);
        });
        try
        {
            var (services, vm, _) = b;
            services.TryRecover(vm);
            Dispatcher.UIThread.RunJobs();

            // The line that says the windows in the room are this desk's now, not the ghost's,
            // must not be pushed off the strip by the surface the recovery itself opens.
            Assert.True(vm.IsRunLayout);
            Assert.Contains("back from the last run", vm.Run.Banner);
            Assert.DoesNotContain("Enter is GO", vm.StatusMessage);
        }
        finally
        {
            OutputTakeover.Reset();
            b.Dispose();
        }
    }

    // ---- the switcher repaints on the frame the picture changes ------------------------------

    /// <summary>Draws until the pipeline stops asking for frames; returns how many it took.</summary>
    private static int Settle(RenderPipeline p)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var frames = 0;
        do
        {
            Hash(p);
            frames++;
            Thread.Sleep(5);
        }
        while (p.Cadence == RedrawCadence.Continuous && DateTime.UtcNow < deadline);
        return frames;
    }

    private static string Hash(RenderPipeline p, int w = 128, int h = 72)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Black);
        p.Render(surface.Canvas, w, h, 1);
        surface.Canvas.Flush();
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 80);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.ToArray()));
    }

    [AvaloniaFact]
    public void APatternTypeChangeReachesTheSwitcherTileAndDoesNotStrandItOnTheOldPicture()
    {
        var b = Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1500;
            window.Height = 950;
            vm.State.Transition.DurationMs = 100; // the shortest fade the desk allows
            vm.IsSandboxActive = false;
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();

            var tile = window.GetVisualDescendants().OfType<MonitorTileControl>()
                .FirstOrDefault(t => t.Pipeline is not null);
            Assert.NotNull(tile);
            var pipeline = tile!.Pipeline!;

            // Draw until nothing is in flight: the desk's own render pass may have started a
            // fade of its own while the window was coming up.
            Settle(pipeline);
            Assert.Equal(RedrawCadence.Static, pipeline.Cadence);
            var grid = Hash(pipeline); // the outgoing picture the fade will fade from

            vm.ActivePattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();

            // The tile must be asking for frames BEFORE the frame that arms the crossfade is
            // drawn. It used to decide afterwards, so it armed the fade, drew the grid over the
            // bars at full opacity, and then stopped — the miniature sat on the grid until some
            // unrelated edit published again.
            Assert.Equal(RedrawCadence.Continuous, pipeline.Cadence);

            var frames = Settle(pipeline);
            Assert.True(frames > 1, "the fade was drawn over more than one frame");
            var bars = Hash(pipeline);
            Assert.NotEqual(grid, bars);
            Assert.Equal(RedrawCadence.Static, pipeline.Cadence); // and it settles back to drawing on change
        }
        finally
        {
            b.Dispose();
        }
    }
}
