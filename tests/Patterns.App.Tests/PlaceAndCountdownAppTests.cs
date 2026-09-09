using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 20 on a live desk: the countdown's one key (the gap every other overlay had filled), and
/// an overlay's place — a drag told from the nearest anchor so the Nudge sliders stay relative, and
/// the pixel fields beside them that read and write the same place.
/// </summary>
public class PlaceAndCountdownAppTests
{
    private static string Run(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    [AvaloniaFact]
    public void OneKeyTurnsTheCountdownOnAndOffFromEveryRemote()
    {
        var b = TestApp.Boot();
        try
        {
            var services = b.Services;
            b.Vm.IsSandboxActive = false;
            var router = new CommandRouter(services);
            var air = services.AirState;
            Assert.False(air.Countdown.Enabled);

            // A bare COUNTDOWN — the phone's key, a Stream Deck key, /patterns/countdown — starts it
            // as the desk has it set up, and says so.
            Assert.Equal("OK", Run(router, "COUNTDOWN"));
            Assert.True(air.Countdown.Enabled);
            Assert.Equal(CountdownTargetKind.TimeOfDay, air.Countdown.TargetKind);   // the show's own set-up, not a guess

            // The same key again takes it off — which is what "turn the countdown off and on" needs.
            Assert.Equal("OK", Run(router, "COUNTDOWN"));
            Assert.False(air.Countdown.Enabled);

            Assert.Equal("OK", Run(router, "COUNTDOWN TOGGLE"));
            Assert.True(air.Countdown.Enabled);
            Assert.Equal("OK", Run(router, "TIMER"));
            Assert.False(air.Countdown.Enabled);

            // With a duration set up, the toggle arms it from now rather than leaving a dead clock.
            services.State.Countdown.TargetKind = CountdownTargetKind.Duration;
            services.State.Countdown.DurationMinutes = 12;
            services.State.Countdown.ArmedAtUtc = null;
            Assert.Equal("OK", Run(router, "COUNTDOWN"));
            Assert.True(air.Countdown.Enabled);
            Assert.NotNull(air.Countdown.ArmedAtUtc);

            // And the state the remotes read back says it is on.
            Assert.Contains("\"on\":true", router.StateJson());
            Assert.Equal("OK", Run(router, "COUNTDOWN"));
            Assert.False(air.Countdown.Enabled);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheToggleIsACueActionAndTheSheetReadsIt()
    {
        // Every overlay's toggle is a cue action; the countdown's was the one missing.
        Assert.Contains(ShowActionKind.CountdownToggle, ActionSpec.CueKinds);
        Assert.Null(ActionSpec.DeskOnly(ShowActionKind.CountdownToggle));
        Assert.Equal("Countdown on / off", ActionSpec.Label(ShowActionKind.CountdownToggle));
        Assert.Equal("Countdown on / off", CueSummary.DescribeAction(new ShowState(), new CueActionConfig { Kind = ShowActionKind.CountdownToggle }));
        Assert.Equal(ShowActionKind.CountdownToggle, CueSheet.ParseKind("countdown toggle"));
        Assert.Equal(ShowActionKind.CountdownToggle, CueSheet.ParseKind("Timer Toggle"));
    }

    [AvaloniaFact]
    public void ADropIsToldFromTheCornerItLandedInAndNothingMoves()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var clock = vm.State.Overlays.Clock;
            clock.Enabled = true;
            clock.Anchor = Anchor9.Center;
            clock.OffsetXPct = 0;
            clock.OffsetYPct = 0;

            // The pane's own maths: a box dropped in the bottom-right corner of an HD canvas.
            var canvas = new SKSizeI(1920, 1080);
            var margin = OverlayPlace.MarginFor(canvas);
            var dropped = DrawUtil.Anchored(canvas, 360, 140, Anchor9.BottomRight, margin);
            var (anchor, x, y) = OverlayPlace.Reanchor(canvas, dropped, margin);
            vm.DragReanchor(HitKind.Clock, anchor, x, y);

            Assert.Equal(Anchor9.BottomRight, clock.Anchor);
            Assert.Equal(0, clock.OffsetXPct, 3);
            Assert.Equal(0, clock.OffsetYPct, 3);

            // The same pixels: the picture did not move, only the reading.
            var after = DrawUtil.Anchored(canvas, 360, 140, clock.Anchor, margin, clock.OffsetXPct, clock.OffsetYPct);
            Assert.Equal(dropped.Left, after.Left, 2);
            Assert.Equal(dropped.Top, after.Top, 2);

            // A layer has no anchor and is left alone.
            Assert.Null(vm.AnchoredOf(HitKind.Layer1));
            Assert.NotNull(vm.AnchoredOf(HitKind.Countdown));
            Assert.NotNull(vm.AnchoredOf(HitKind.Badge));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePixelFieldsReadThePlaceAndWriteItBack()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var canvas = new SKSizeI(1920, 1080);
            var margin = OverlayPlace.MarginFor(canvas);
            var box = DrawUtil.Anchored(canvas, 400, 160, Anchor9.TopLeft, margin);
            vm.State.Overlays.Clock.Anchor = Anchor9.TopLeft;
            // Stand in for the pane: the box the last frame drew, and the canvas it drew it on.
            vm.PreviewBox = kind => kind == HitKind.Clock
                ? new PlaceEditor.PlacedBox(
                    DrawUtil.Anchored(canvas, 400, 160, vm.State.Overlays.Clock.Anchor, margin,
                        vm.State.Overlays.Clock.OffsetXPct, vm.State.Overlays.Clock.OffsetYPct),
                    canvas, margin)
                : null;

            var place = vm.ClockPlace;
            place.Refresh();
            Assert.True(place.HasBox);
            Assert.Equal(Math.Round(box.Left), place.XPx);
            Assert.Equal(Math.Round(box.Top), place.YPx);
            Assert.Contains("1920 × 1080", place.Words);

            // Typing a pixel lands the box on exactly those pixels — the nudge follows.
            place.XPx = 640;
            place.YPx = 300;
            Assert.Equal(640, place.XPx);
            Assert.Equal(300, place.YPx);
            var landed = DrawUtil.Anchored(canvas, 400, 160, Anchor9.TopLeft, margin,
                vm.State.Overlays.Clock.OffsetXPct, vm.State.Overlays.Clock.OffsetYPct);
            Assert.Equal(640, landed.Left, 2);
            Assert.Equal(300, landed.Top, 2);

            // Nothing drawn: the fields go quiet rather than lying about where something is.
            vm.PreviewBox = _ => null;
            vm.WeatherPlace.Refresh();
            Assert.False(vm.WeatherPlace.HasBox);
            Assert.Contains("Not on the picture", vm.WeatherPlace.Words);

            // The poll refreshes every one of them.
            Assert.Equal(7, vm.Places.Count());
            vm.PollNow();
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ChoosingAPositionPutsTheElementThereAndResetPutsItBack()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var canvas = new SKSizeI(1920, 1080);
            var margin = OverlayPlace.MarginFor(canvas);
            var clock = vm.State.Overlays.Clock;

            // Dragged across the frame and re-anchored: bottom-right with a nudge of its own.
            var dropped = SKRect.Create(1400, 800, 360, 140);
            var (anchor, x, y) = OverlayPlace.Reanchor(canvas, dropped, margin, clock.Anchor);
            vm.DragReanchor(HitKind.Clock, anchor, x, y);
            Assert.Equal(Anchor9.BottomRight, clock.Anchor);
            Assert.True(vm.ClockPlace.IsNudged, "the drop left it off the corner");

            // The Position picker is bound to this, and choosing a position means that position on
            // the screen — not that position plus wherever the drag left it.
            vm.ClockPlace.Anchor = Anchor9.TopLeft;
            Assert.Equal(Anchor9.TopLeft, clock.Anchor);
            Assert.Equal(0, clock.OffsetXPct, 6);
            Assert.Equal(0, clock.OffsetYPct, 6);
            Assert.False(vm.ClockPlace.IsNudged);
            var placed = DrawUtil.Anchored(canvas, 360, 140, clock.Anchor, margin, clock.OffsetXPct, clock.OffsetYPct);
            Assert.Equal(DrawUtil.Anchored(canvas, 360, 140, Anchor9.TopLeft, margin), placed);

            // The picker follows a drag rather than fighting it, and re-picking the same position
            // changes nothing (that is what RESET is for).
            var again = OverlayPlace.Reanchor(canvas, dropped, margin, clock.Anchor);
            vm.DragReanchor(HitKind.Clock, again.Anchor, again.OffsetXPct, again.OffsetYPct);
            Assert.Equal(clock.Anchor, vm.ClockPlace.Anchor);
            Assert.True(vm.ClockPlace.IsNudged);
            vm.ClockPlace.Anchor = clock.Anchor;
            Assert.True(vm.ClockPlace.IsNudged);

            // The picker follows the model itself, not only the poll: a look recall or a nudge
            // slider reaches it at once, the way binding straight at the model used to.
            vm.ClockPlace.Refresh();                 // the poll's tick establishes the hook
            var raised = new List<string>();
            vm.ClockPlace.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            clock.Anchor = Anchor9.TopRight;
            Assert.Contains(nameof(PlaceEditor.Anchor), raised);
            Assert.Equal(Anchor9.TopRight, vm.ClockPlace.Anchor);
            clock.Anchor = Anchor9.BottomRight;

            // RESET: back onto the position the picker names, with no nudge.
            Assert.True(vm.ClockPlace.ResetCommand.CanExecute(null));
            vm.ClockPlace.ResetCommand.Execute(null);
            Assert.Equal(0, clock.OffsetXPct, 6);
            Assert.Equal(0, clock.OffsetYPct, 6);
            Assert.False(vm.ClockPlace.IsNudged);
            Assert.Equal(Anchor9.BottomRight, clock.Anchor);   // the position it was told, kept
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void EveryOverlayPageCarriesItsPixelRow()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            window.Width = 1420;
            window.Height = 900;

            foreach (var (page, count) in new[] { ("Overlays", 5), ("Countdown", 1), ("Branding", 1) })
            {
                vm.SelectPage(Shell.IndexOf(page));
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var rows = window.GetVisualDescendants().OfType<PlaceRow>().ToList();
                Assert.True(rows.Count >= count, $"the {page} page carries {count} pixel row(s), found {rows.Count}");
                Assert.All(rows, r => Assert.NotNull(r.Place));
                // Every row carries its way back: RESET onto the position the picker names.
                var resets = window.GetVisualDescendants().OfType<Button>()
                    .Count(x => (x.Content as string) == "RESET TO POSITION");
                Assert.True(resets >= count, $"the {page} page carries {count} RESET button(s), found {resets}");
            }
        }
        finally
        {
            b.Dispose();
        }
    }
}
