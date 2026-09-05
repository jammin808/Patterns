using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The lower-thirds triage: a design on air drawn on every sink that leaves the machine, the
/// show's default design and the in-place put behind UPDATE, and the preview / take / update
/// verbs on the wire, in the cue actions and over OSC.
/// </summary>
public class LowerThirdTriageTests
{
    /// <summary>A white bar filling its box with no way in or out: on screen the instant it is shown, gone the instant it is hidden.</summary>
    private static LowerThirdDesign Slab()
    {
        var d = LowerThirdPresets.Blank();
        d.Name = "Slab";
        d.Anchor = Anchor9.BottomLeft;
        d.MarginX = 0;
        d.MarginY = 0;
        d.Width = 960;
        d.Height = 220;
        d.InMs = 0;
        d.OutMs = 0;
        d.HoldMs = 0;
        d.Elements.Add(new LowerThirdElement
        {
            Kind = LowerThirdElementKind.Bar,
            Name = "Bar",
            Enabled = true,
            X = 0,
            Y = 0,
            W = 960,
            H = 220,
            Opacity = 1,
            Fill = LowerThirdFill.Solid,
            FillColor = "#FFFFFF",
        });
        return d;
    }

    private static bool White(SKColor c) => c.Red > 200 && c.Green > 200 && c.Blue > 200;
    private static bool Dark(SKColor c) => c.Red < 30 && c.Green < 30 && c.Blue < 30;

    [Fact]
    public void ADesignOnAirDrawsOnEverySinkThatLeavesTheMachineAndGoesWhenHidden()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = "#000000";
            s.Pattern.FlatField.ShowLabel = false;
            s.Pattern.Canvas.FollowOutput = true;
            s.Transition.Enabled = false;
        });
        var slab = Slab();
        state.LowerThirds.Designs.Add(slab);
        state.LowerThirds.Show(slab, ShowClock.UtcAt(0));

        // A 640×360 frame: the slab is the bottom-left 320×73 of it, on the output, the NDI send, the stream, the desk's panes.
        foreach (var kind in new[] { SinkKind.Output, SinkKind.Ndi, SinkKind.Stream, SinkKind.Preview, SinkKind.Monitor })
        {
            using var bmp = RenderTestHarness.Render(state, 640, 360, time: 1.0, sinkKind: kind);
            var inside = bmp.GetPixel(160, 340);
            Assert.True(White(inside), $"{kind}: the lower third is drawn, got {inside}");
            var outside = bmp.GetPixel(480, 40);
            Assert.True(Dark(outside), $"{kind}: the rest of the frame is the black field, got {outside}");
        }

        // Told to leave (no way out): the very next frame is bare again, and a re-show brings it back.
        state.LowerThirds.Hide(ShowClock.UtcAt(2));
        using (var gone = RenderTestHarness.Render(state, 640, 360, time: 3.0, sinkKind: SinkKind.Output))
        {
            Assert.True(Dark(gone.GetPixel(160, 340)), "hidden: the output is bare");
        }
        state.LowerThirds.Show(slab, ShowClock.UtcAt(4));
        using (var back = RenderTestHarness.Render(state, 640, 360, time: 5.0, sinkKind: SinkKind.Ndi))
        {
            Assert.True(White(back.GetPixel(160, 340)), "shown again: back on the send");
        }
    }

    [Fact]
    public void TheShowsDefaultDesignTheInPlacePutAndTheShowFile()
    {
        var cfg = new LowerThirdsConfig();
        Assert.Null(cfg.DefaultDesign);
        var clean = LowerThirdPresets.Create("Clean");
        var neon = LowerThirdPresets.Create("Neon");
        cfg.Designs.Add(clean);
        cfg.Designs.Add(neon);
        Assert.Same(clean, cfg.DefaultDesign);          // none chosen: the first
        cfg.DefaultDesignId = neon.Id;
        Assert.Same(neon, cfg.DefaultDesign);
        cfg.DefaultDesignId = "gone";
        Assert.Same(clean, cfg.DefaultDesign);          // a deleted id falls back to the first
        cfg.DefaultDesignId = neon.Id;

        // Put: the same id replaces in place (row two stays row two), a new id appends, the same instance is left alone.
        var edited = neon.Clone(newId: false);
        edited.PersonName = "Fixed";
        Assert.Same(edited, cfg.Put(edited));
        Assert.Same(edited, cfg.Designs[1]);
        Assert.Equal(2, cfg.Designs.Count);
        Assert.Same(edited, cfg.Find(neon.Id));
        var glass = LowerThirdPresets.Create("Glass");
        cfg.Put(glass);
        Assert.Equal(3, cfg.Designs.Count);
        Assert.Same(glass, cfg.Designs[2]);
        Assert.Same(edited, cfg.Put(edited));
        Assert.Equal(3, cfg.Designs.Count);

        // The same design is the saved fields, never the tallies.
        Assert.True(LowerThirdsConfig.SameDesign(neon, neon.Clone(newId: false)));
        var other = neon.Clone(newId: false);
        other.IsOnAir = true;
        other.IsInPreview = true;
        other.IsDefault = true;
        other.PreviewText = "IN PREVIEW";
        Assert.True(LowerThirdsConfig.SameDesign(neon, other));
        other.PersonName = "Someone else";
        Assert.False(LowerThirdsConfig.SameDesign(neon, other));

        // The show file keeps the default and never the tallies.
        var state = new ShowState();
        state.LowerThirds.Designs.Add(clean);
        state.LowerThirds.DefaultDesignId = clean.Id;
        clean.IsDefault = true;
        clean.IsInPreview = true;
        var json = JsonUtil.Serialize(state);
        Assert.Contains($"\"DefaultDesignId\": \"{clean.Id}\"", json);
        Assert.DoesNotContain("IsDefault", json);
        Assert.DoesNotContain("IsInPreview", json);
        Assert.DoesNotContain("PreviewText", json);
        var back = JsonUtil.Deserialize<ShowState>(json)!;
        Assert.Equal(clean.Id, back.LowerThirds.DefaultDesignId);
        Assert.Equal("Clean", back.LowerThirds.DefaultDesign!.Name);
    }

    [Fact]
    public void TheVerbsTheCueActionsAndTheOscKnowPreviewTakeAndUpdate()
    {
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPreview, 2, "", ""), ControlProtocol.Parse("LT PREVIEW 2"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPreview, 0, "Neon", "3"), ControlProtocol.Parse("lowerthird preview Neon with 3"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPreview, 0, "", "Jane Doe"), ControlProtocol.Parse("LT PVW WITH Jane Doe"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPreviewOff, 0, ""), ControlProtocol.Parse("LT PREVIEW OFF"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdPreviewOff, 0, ""), ControlProtocol.Parse("LT PREVIEW CLEAR"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdTake, 0, ""), ControlProtocol.Parse("LT TAKE"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdUpdate, 0, ""), ControlProtocol.Parse("lt update"));
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LT PREVIEW").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LT PREVIEW Neon WITH").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LT PREVIEW WITH").Kind);
        // A design whose name merely starts with the word is still a design.
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdShow, 0, "Previewer"), ControlProtocol.Parse("LT Previewer"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdShow, 0, "Take Two"), ControlProtocol.Parse("LT Take Two"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdHide, 0, ""), ControlProtocol.Parse("LT OFF"));

        Assert.Equal("LOWERTHIRD PREVIEW 2", OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/preview/2")));
        Assert.Equal("LOWERTHIRD PREVIEW 2 WITH 3", OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/preview/2/3")));
        Assert.Equal("LOWERTHIRD PREVIEW Neon WITH Jane", OscMap.ToLine(OscMessage.Of("/patterns/lt/preview", "Neon", "Jane")));
        Assert.Equal("LOWERTHIRD PREVIEW OFF", OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/preview/off")));
        Assert.Equal("LOWERTHIRD TAKE", OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/take")));
        Assert.Equal("LOWERTHIRD UPDATE", OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/update")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/preview")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/lowerthird/take"));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/lowerthird/preview"));

        var fed = OscFeedback.FromState("{\"lowerThirdPreview\":\"Neon\",\"lowerThirdPreviewPerson\":\"Jane\",\"lowerThirdDefault\":\"Clean\",\"lowerThirdEdited\":true}");
        Assert.Equal("Neon", Assert.Single(fed, x => x.Address == "/patterns/state/lowerthird/preview").Args[0]);
        Assert.Equal("Jane", Assert.Single(fed, x => x.Address == "/patterns/state/lowerthird/preview/person").Args[0]);
        Assert.Equal("Clean", Assert.Single(fed, x => x.Address == "/patterns/state/lowerthird/default").Args[0]);
        Assert.Equal(1, Assert.Single(fed, x => x.Address == "/patterns/state/lowerthird/edited").Args[0]);

        // The cue actions: the spec, the words, the checks.
        Assert.Equal((TargetKind.LowerThird, ValueKind.Person), CueActionSpec.For(CueActionKind.LowerThirdPreview));
        Assert.Equal((TargetKind.None, ValueKind.None), CueActionSpec.For(CueActionKind.LowerThirdTake));
        Assert.Contains(CueActionKind.LowerThirdPreview, CueActionSpec.Editable);
        Assert.Contains(CueActionKind.LowerThirdTake, CueActionSpec.Editable);
        Assert.False(CueActionSpec.ChangesContent(CueActionKind.LowerThirdTake));
        var state = SettingsStore.Fresh();
        var neon = LowerThirdPresets.Create("Neon");
        state.LowerThirds.Designs.Add(neon);
        state.LowerThirds.Entries.Add(new LowerThirdEntry { Name = "Jane Doe", Role = "CEO" });
        var jane = state.LowerThirds.Entries[0];
        Assert.Equal("Lower third to preview 'Neon' — Jane Doe", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.LowerThirdPreview, Target = neon.Id, Value = jane.Id }));
        Assert.Equal("Lower third to preview (the default) — Jane Doe", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.LowerThirdPreview, Value = jane.Id }));
        Assert.Equal("Lower third take (preview to air)", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.LowerThirdTake }));
        var stack = CueStacks.Caller(state);
        var good = new RunCueConfig { Name = "Preview Jane" };
        good.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdPreview, Target = "", Value = jane.Id });
        var take = new RunCueConfig { Name = "Take" };
        take.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdTake });
        var bad = new RunCueConfig { Name = "Bad" };
        bad.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdPreview, Target = "no-such" });
        stack.Cues.Add(good);
        stack.Cues.Add(take);
        stack.Cues.Add(bad);
        var report = CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => true });
        Assert.DoesNotContain(report.Issues, p => p.CueId == good.Id);     // an empty target is the show's default
        Assert.DoesNotContain(report.Issues, p => p.CueId == take.Id);
        Assert.Contains(report.Issues, p => p.CueId == bad.Id && p.Severity == IssueSeverity.Hard);

        // A saved look says which design it carries — what a recall syncs to the frozen program first.
        state.LowerThirds.Show(neon, ShowClock.UtcAt(-5));
        Assert.Equal(neon.Id, LookService.LowerThirdIdOf(LookService.Capture(state)));
        state.LowerThirds.Hide(ShowClock.UtcAt(-1));
        Assert.Equal("", LookService.LowerThirdIdOf(LookService.Capture(state)));
        Assert.Null(LookService.LowerThirdIdOf("not json"));
    }
}
