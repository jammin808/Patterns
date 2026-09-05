using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Freeze on the sinks that leave the machine, the verbs and the OSC of the round's easy pro features, and the show file's earlier versions.</summary>
public class ProFeatureTests
{
    private static ShowState Flat(string color) => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = color;
        s.Pattern.FlatField.ShowLabel = false;
        s.Pattern.Canvas.FollowOutput = true;
        s.Transition.Enabled = false;
    });

    private static SKBitmap Render(PatternEngine engine, SinkState sink, ShowSnapshot snap, SinkKind kind, double time = 1.0)
    {
        var info = new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(64, 32),
            ReferenceSize = new SKSizeI(64, 32),
            Time = time,
            Now = new DateTime(2026, 8, 29, 12, 0, 0),
            UtcNow = RenderTestHarness.FixedUtcNow,
            Sink = kind,
            SinkIndex = 1,
            SinkLabel = "t",
        };
        engine.Render(surface.Canvas, snap, in ctx, sink);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static bool Red(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;
    private static bool Blue(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;
    private static bool Black(SKColor c) => c.Red < 20 && c.Green < 20 && c.Blue < 20;

    [Fact]
    public void AFrozenOutputHoldsItsFrameWhileTheDeskMovesAndABlackoutStillTakesIt()
    {
        var engine = new PatternEngine();
        using var output = new SinkState();
        using var ndi = new SinkState();
        using var monitor = new SinkState();

        var red = new ShowSnapshot { State = Flat("#FF0000"), Version = 1, Frozen = true };
        using (var a = Render(engine, output, red, SinkKind.Output)) Assert.True(Red(a.GetPixel(32, 16)));
        Assert.NotNull(output.FrozenFrame);

        // The show turns blue: the output and an NDI sender (frozen since their first frame) keep red; a monitor moves.
        var blue = new ShowSnapshot { State = Flat("#0000FF"), Version = 2, Frozen = true };
        using (var b = Render(engine, output, blue, SinkKind.Output, 2.0)) Assert.True(Red(b.GetPixel(32, 16)), $"the frozen output holds red, got {b.GetPixel(32, 16)}");
        using (var n = Render(engine, ndi, red, SinkKind.Ndi)) Assert.True(Red(n.GetPixel(32, 16)));
        using (var n = Render(engine, ndi, blue, SinkKind.Ndi, 2.0)) Assert.True(Red(n.GetPixel(32, 16)), "an NDI send holds too");
        using (var m = Render(engine, monitor, blue, SinkKind.Monitor, 2.0)) Assert.True(Blue(m.GetPixel(32, 16)), "a monitor keeps moving");
        Assert.Null(monitor.FrozenFrame);

        // A blackout takes a frozen output; when it lifts the freeze holds again.
        var blackState = Flat("#0000FF");
        blackState.Blackout = true;
        var black = new ShowSnapshot { State = blackState, Version = 3, Frozen = true };
        using (var k = Render(engine, output, black, SinkKind.Output, 3.0)) Assert.True(Black(k.GetPixel(32, 16)), "blackout wins over the freeze");
        Assert.Null(output.FrozenFrame);   // the blackout dropped the held frame: the next freeze frame is captured afresh
        using (var b = Render(engine, output, blue, SinkKind.Output, 4.0)) Assert.True(Blue(b.GetPixel(32, 16)), "after the blackout the freeze holds what it sees now");
        using (var b = Render(engine, output, red, SinkKind.Output, 5.0)) Assert.True(Blue(b.GetPixel(32, 16)));

        // Released: the output moves, the held frame goes.
        var free = new ShowSnapshot { State = Flat("#FF0000"), Version = 4 };
        using (var f = Render(engine, output, free, SinkKind.Output, 6.0)) Assert.True(Red(f.GetPixel(32, 16)));
        Assert.Null(output.FrozenFrame);
    }

    [Fact]
    public void TheVerbsTheSecondsAndTheOscKnowFreezeFadeAndLookBack()
    {
        Assert.Equal(RemoteCommandKind.FreezeToggle, ControlProtocol.Parse("FREEZE").Kind);
        Assert.Equal(RemoteCommandKind.FreezeOn, ControlProtocol.Parse("freeze on").Kind);
        Assert.Equal(RemoteCommandKind.FreezeOff, ControlProtocol.Parse("FREEZE OFF").Kind);

        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 0, ""), ControlProtocol.Parse("FADE"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 2000, ""), ControlProtocol.Parse("FADE 2"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 2500, ""), ControlProtocol.Parse("fade 2.5"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 1500, ""), ControlProtocol.Parse("FADE 1500ms"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeToBlack, 1000, ""), ControlProtocol.Parse("FADE DOWN 1"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeUp, 0, ""), ControlProtocol.Parse("FADE UP"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeUp, 3000, ""), ControlProtocol.Parse("FADE UP 3"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.FadeUp, 3000, ""), ControlProtocol.Parse("FADEUP 3s"));
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("FADE slowly").Kind);
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LookBack, 0, ""), ControlProtocol.Parse("LOOKBACK"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LookBack, 0, "cut"), ControlProtocol.Parse("LOOKBACK cut"));

        Assert.True(ControlProtocol.TryParseSeconds("", out var ms) && ms == 0);
        Assert.True(ControlProtocol.TryParseSeconds("0.25", out ms) && ms == 250);
        Assert.False(ControlProtocol.TryParseSeconds("-1", out _));

        Assert.Equal("FREEZE ON", OscMap.ToLine(OscMessage.Of("/patterns/freeze", 1)));
        Assert.Equal("FREEZE TOGGLE", OscMap.ToLine(OscMessage.Of("/patterns/freeze")));
        Assert.Equal("FADE 2", OscMap.ToLine(OscMessage.Of("/patterns/fade", 2)));
        Assert.Equal("FADE", OscMap.ToLine(OscMessage.Of("/patterns/fade")));
        Assert.Equal("FADEUP 3", OscMap.ToLine(OscMessage.Of("/patterns/fade/up", 3)));
        Assert.Equal("FADE 1.5", OscMap.ToLine(OscMessage.Of("/patterns/fade/down/1.5")));
        Assert.Equal("LOOKBACK", OscMap.ToLine(OscMessage.Of("/patterns/lookback")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/freeze"));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/fade"));

        var fed = OscFeedback.FromState("{\"frozen\":true,\"previousLook\":\"Walk-in\"}");
        Assert.Equal(1, Assert.Single(fed, x => x.Address == "/patterns/state/freeze").Args[0]);
        Assert.Equal("Walk-in", Assert.Single(fed, x => x.Address == "/patterns/state/look/previous").Args[0]);
    }

    [Fact]
    public void TheStoreKeepsEarlierVersionsOfTheShowSpacedAndTwentyDeep()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-backups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir) { BackupSpacing = TimeSpan.Zero };
            var state = new ShowState { Name = "One" };

            store.Save(state);
            Assert.Empty(store.ListBackups());              // the first save: nothing to keep yet
            Assert.Null(store.PreviousSavePath);

            store.Save(state);
            Assert.Empty(store.ListBackups());              // the same content: not a version
            Assert.NotNull(store.PreviousSavePath);         // …but the previous file is kept by the atomic write

            state.Name = "Two";
            store.Save(state);
            var kept = store.ListBackups();
            var one = Assert.Single(kept);
            Assert.Equal("One", store.LoadFrom(one.Path)!.Name);   // the version as it was before the change
            Assert.Equal("Two", store.Load().Name);

            // Twenty deep: the oldest go.
            for (var i = 3; i <= 30; i++)
            {
                state.Name = $"V{i}";
                store.Save(state);
            }
            kept = store.ListBackups();
            Assert.Equal(SettingsStore.BackupsKept, kept.Count);
            Assert.True(kept[0].When >= kept[^1].When);     // newest first
            Assert.Equal("V29", store.LoadFrom(kept[0].Path)!.Name);
            Assert.Contains("V", store.LoadFrom(kept[^1].Path)!.Name);

            // Spacing: within the window a change is not a new version.
            var spaced = new SettingsStore(dir) { BackupSpacing = TimeSpan.FromMinutes(5) };
            state.Name = "Later";
            spaced.Save(state);
            Assert.Equal(SettingsStore.BackupsKept, spaced.ListBackups().Count);
            Assert.Equal("V29", spaced.LoadFrom(spaced.ListBackups()[0].Path)!.Name);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a temp dir */ }
        }
    }
}
