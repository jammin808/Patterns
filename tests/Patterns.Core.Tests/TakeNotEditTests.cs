using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 26: a transition is how one picture becomes the next in front of a room. It is not how a
/// desk answers a slider.
///
/// The engine used to infer "transition now" from a consequence — this sink's content identity
/// differs from the one it drew last frame — which cannot tell a TAKE from somebody dragging a
/// number. So choosing a screen to edit, changing a pattern type or typing in a box dissolved the
/// wall over 400 ms, which reads as the desk lagging. Now the snapshot is TOLD: a publish that
/// came from a verb, a send or the show moving on is a take; everything else is an edit.
/// </summary>
public class TakeNotEditTests
{
    private const int W = 64;
    private const int H = 32;

    private static ShowState Flat(string colour)
    {
        var s = RenderTestHarness.State(x =>
        {
            x.Pattern.Kind = PatternKind.FlatField;
            x.Pattern.FlatField.Color = colour;
            x.Pattern.FlatField.ShowLabel = false;
            x.Pattern.Canvas.FollowOutput = true;
            x.Transition.Enabled = true;
            x.Transition.DurationMs = 1000;
        });
        return s;
    }

    private static SKColor Draw(PatternEngine engine, SinkState sink, ShowSnapshot snap, double time, string? screenId = null)
    {
        var info = new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(W, H),
            ReferenceSize = new SKSizeI(W, H),
            Time = time,
            Now = new DateTime(2026, 9, 11, 12, 0, 0),
            UtcNow = RenderTestHarness.FixedUtcNow,
            Sink = SinkKind.Output,
            SinkIndex = 1,
            SinkLabel = "t",
            ScreenId = screenId,
        };
        engine.Render(surface.Canvas, snap, in ctx, sink);
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(image);
        return bmp.GetPixel(W / 2, H / 2);
    }

    private static bool Red(SKColor c) => c.Red > 200 && c.Blue < 60;
    private static bool Blue(SKColor c) => c.Blue > 200 && c.Red < 60;

    [Fact]
    public void TheSameChangeTransitionsWhenItIsTakenAndArrivesAtOnceWhenItIsEdited()
    {
        var red = Flat("#FF0000");
        var blue = Flat("#0000FF");

        // Edited: the new picture is simply there. No dissolve, nothing half way.
        var engine = new PatternEngine();
        using (var sink = new SinkState())
        {
            Assert.True(Red(Draw(engine, sink, RenderTestHarness.Snap(red, 1, isTake: false), 0)));
            Assert.True(Blue(Draw(engine, sink, RenderTestHarness.Snap(blue, 2, isTake: false), 0)));
            Assert.Null(sink.TransitionFrom);
        }

        // Taken: the same change, over the show's transition time.
        using (var sink = new SinkState())
        {
            Assert.True(Red(Draw(engine, sink, RenderTestHarness.Snap(red, 1), 0)));
            var start = Draw(engine, sink, RenderTestHarness.Snap(blue, 2), 0);
            Assert.True(Red(start), "a transition starts on the picture the room already has");
            Assert.NotNull(sink.TransitionFrom);
            Assert.True(Blue(Draw(engine, sink, RenderTestHarness.Snap(blue, 2), 1.2)));
            Assert.Null(sink.TransitionFrom);
        }
    }

    [Fact]
    public void AnEditLandingMidTakeNeverCutsTheTransitionShort()
    {
        // The one that would have been felt in a room: with EDIT SAFE open the operator's very
        // next keystroke lands 50 ms into a 400 ms dissolve. A transition that started because
        // somebody took the show must finish, whatever is edited underneath it.
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var red = Flat("#FF0000");
        var blue = Flat("#0000FF");

        Draw(engine, sink, RenderTestHarness.Snap(red, 1), 0);
        Draw(engine, sink, RenderTestHarness.Snap(blue, 2), 0);
        Assert.NotNull(sink.TransitionFrom);

        // An edit publishes: a different colour again, not a take.
        var green = Flat("#00FF00");
        var mid = Draw(engine, sink, RenderTestHarness.Snap(green, 3, isTake: false), 0.5);
        Assert.NotNull(sink.TransitionFrom);                       // still crossing
        Assert.False(Red(mid) || Blue(mid), "half way through it is a mixture, not a cut");

        // And it ends on its own clock, on the picture the show is now showing.
        var end = Draw(engine, sink, RenderTestHarness.Snap(green, 3, isTake: false), 1.2);
        Assert.Null(sink.TransitionFrom);
        Assert.True(end.Green > 200);
    }

    [Fact]
    public void APanePointedAtAnotherTargetSwitchesRatherThanDissolvingTwoPicturesTogether()
    {
        // Clicking another tile is the operator looking somewhere else. The two keys are for two
        // pictures, and dissolving one screen's picture into another's means nothing.
        var state = Flat("#FF0000");
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, Enabled = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, X = 1920, Enabled = true });
        ContentTargets.SetOwnPattern(state, "b", true);
        var own = ContentTargets.EnsureAssignment(state, "b").Pattern;
        own.Kind = PatternKind.FlatField;
        own.FlatField.Color = "#0000FF";
        own.FlatField.ShowLabel = false;
        own.Canvas.FollowOutput = true;

        var engine = new PatternEngine();
        using var sink = new SinkState();
        var snap = RenderTestHarness.Snap(state, 1);

        Assert.True(Red(Draw(engine, sink, snap, 0, "a")));
        // The same snapshot, a different target: straight there, nothing crossing.
        Assert.True(Blue(Draw(engine, sink, snap, 0.1, "b")));
        Assert.Null(sink.TransitionFrom);
        Assert.False(PatternEngine.WillStartFade(snap, "a", sink, SinkKind.Monitor));
        Assert.True(Red(Draw(engine, sink, snap, 0.2, "a")));
        Assert.Null(sink.TransitionFrom);
    }

    [Fact]
    public void TransitionsSwitchedOffAbandonWhatIsAlreadyCrossing()
    {
        // Off is off — distinct from a publish that is merely an edit, which starts nothing but
        // leaves what is running alone.
        var engine = new PatternEngine();
        using var sink = new SinkState();
        Draw(engine, sink, RenderTestHarness.Snap(Flat("#FF0000"), 1), 0);
        Draw(engine, sink, RenderTestHarness.Snap(Flat("#0000FF"), 2), 0);
        Assert.NotNull(sink.TransitionFrom);

        var off = Flat("#0000FF");
        off.Transition.Enabled = false;
        Assert.True(Blue(Draw(engine, sink, RenderTestHarness.Snap(off, 3), 0.2)));
        Assert.Null(sink.TransitionFrom);
    }

    [Fact]
    public void ATakeIsWhatTheSnapshotSaysItIsAndTheScopeCannotLeak()
    {
        var state = new ShowState();
        var bus = new SnapshotBus(state);

        bus.Publish(state);
        Assert.False(bus.Current.IsTake);
        Assert.False(bus.Current.FadesEnabled);   // the show's transitions are on, but this is an edit

        using (var take = bus.Take())
        {
            Assert.True(bus.InTake);
            bus.Publish(state);
            Assert.True(bus.Current.IsTake);
            Assert.True(bus.Current.FadesEnabled);

            // Nested: an action inside an action is still one take, and the inner one ending does
            // not end the outer one.
            using (var inner = bus.Take())
            {
                bus.Publish(state);
                Assert.True(bus.Current.IsTake);
            }
            Assert.True(bus.InTake);
            bus.Publish(state);
            Assert.True(bus.Current.IsTake);
        }

        // Out of the scope, the very next publish is an edit again — a take cannot attach itself
        // to whatever happens to publish next, which is what a "next publish" flag would do.
        Assert.False(bus.InTake);
        bus.Publish(state);
        Assert.False(bus.Current.IsTake);

        // A recall that named its own fade still transitions on a show whose crossfades are off.
        state.Transition.Enabled = false;
        bus.FadeOnNextPublish(800);
        bus.Publish(state);
        Assert.True(bus.Current.FadesEnabled);
        Assert.False(bus.Current.TransitionsOff);
        bus.Publish(state);
        Assert.True(bus.Current.TransitionsOff);
    }

    [Fact]
    public void WhatAPictureLooksLikeIsThePictureAndNothingElse()
    {
        // The rule the whole design leans on: only the pattern a target draws is identity. So an
        // audio device, the desk's monitor pick, a lower-third element, a monitor wall's tiles and
        // the transition settings themselves can never arm a transition — however they are changed
        // and whoever changes them. Nothing else in the repo pins this.
        var state = new ShowState();
        var before = RenderTestHarness.Snap(state, 1).TransitionKeyFor(null);

        state.AudioPlayer.Devices.Add("Focusrite Scarlett 2i2");
        state.AudioPlayer.VolumePct = 55;
        state.Monitor.Source = AudioMonitor.Preview;
        state.Monitor.OutputId = "b";
        state.Transition.Kind = TransitionKind.Wipe;
        state.Transition.DurationMs = 2500;
        state.Multiviews.Add(new MultiviewOptions { Id = "wall", Name = "Gallery" });
        state.LowerThirds.Designs.Add(new LowerThirds.LowerThirdDesign { Name = "Speaker" });
        state.Overlays.Clock.Enabled = true;
        state.Overlays.Message.Text = "Doors in five";
        state.Stream.Active = true;

        Assert.Equal(before, RenderTestHarness.Snap(state, 2).TransitionKeyFor(null));

        // And the picture itself still is.
        state.Pattern.Kind = PatternKind.ColorBars;
        Assert.NotEqual(before, RenderTestHarness.Snap(state, 3).TransitionKeyFor(null));
    }
}
