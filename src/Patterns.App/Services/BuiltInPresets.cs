using Patterns.Core.Model;
using Patterns.Core.Effects;
using Patterns.Core.Particles;

namespace Patterns.App.Services;

/// <summary>One factory tile; <paramref name="Section"/> is where the Library files it ("Patterns", or "Particles" for a scene).</summary>
public sealed record BuiltInPreset(string Category, string Name, Action<PatternConfig> Apply, string Section = "Patterns");

/// <summary>The factory pattern library. Every entry works at any canvas size up to 4K+.</summary>
public static class BuiltInPresets
{
    public static readonly IReadOnlyList<BuiltInPreset> All = Build();

    private static List<BuiltInPreset> Build()
    {
        var list = new List<BuiltInPreset>();

        void Add(string category, string name, Action<PatternConfig> apply)
            => list.Add(new BuiltInPreset(category, name, apply));

        // Alignment
        Add("Alignment", "Grid 96 px", p => { p.Kind = PatternKind.Grid; p.Grid.CellSize = 96; p.Grid.Subdivisions = 0; });
        Add("Alignment", "Fine grid 32 px", p => { p.Kind = PatternKind.Grid; p.Grid.CellSize = 32; p.Grid.Subdivisions = 0; p.Grid.ShowDiagonals = true; });
        Add("Alignment", "Crosshatch + subdivisions", p => { p.Kind = PatternKind.Grid; p.Grid.CellSize = 192; p.Grid.Subdivisions = 3; p.Grid.ShowDiagonals = true; });
        Add("Alignment", "Geometry & safe areas", p => { p.Kind = PatternKind.Geometry; p.Geometry.ShowAspectMarkers = true; });
        Add("Alignment", "Focus chart", p => p.Kind = PatternKind.Focus);

        // Colour & levels
        Add("Colour & levels", "SMPTE bars", p => { p.Kind = PatternKind.ColorBars; p.Bars.Variant = BarsVariant.Smpte; });
        Add("Colour & levels", "EBU 100% bars", p => { p.Kind = PatternKind.ColorBars; p.Bars.Variant = BarsVariant.Ebu100; });
        Add("Colour & levels", "Grey ramp", p => { p.Kind = PatternKind.Ramp; p.Ramp.Variant = RampVariant.GrayHorizontal; });
        Add("Colour & levels", "RGB ramps", p => { p.Kind = PatternKind.Ramp; p.Ramp.Variant = RampVariant.Rgb; });
        Add("Colour & levels", "16 steps (banding)", p => { p.Kind = PatternKind.Ramp; p.Ramp.Variant = RampVariant.Steps; p.Ramp.Steps = 16; });
        Add("Colour & levels", "White field", p => { p.Kind = PatternKind.FlatField; p.FlatField.Color = "#FFFFFF"; p.FlatField.LevelPct = 100; });
        Add("Colour & levels", "50% grey field", p => { p.Kind = PatternKind.FlatField; p.FlatField.Color = "#FFFFFF"; p.FlatField.LevelPct = 50; });
        Add("Colour & levels", "Colour cycle R-G-B-W-K", p => { p.Kind = PatternKind.ColorCycle; p.ColorCycle.IntervalSeconds = 2; });

        // Pixel checks
        Add("Pixel checks", "Checker 1 px", p => { p.Kind = PatternKind.Checkerboard; p.Checker.CellSize = 1; });
        Add("Pixel checks", "Checker 8 px", p => { p.Kind = PatternKind.Checkerboard; p.Checker.CellSize = 8; });
        Add("Pixel checks", "Checker 64 px flip", p => { p.Kind = PatternKind.Checkerboard; p.Checker.CellSize = 64; p.Checker.Animate = true; });

        // Walls & blend
        Add("Walls & blend", "LED wall 128px · 10×6", p =>
        {
            p.Kind = PatternKind.LedWall;
            p.LedWall.TileWidth = 128; p.LedWall.TileHeight = 128;
            p.LedWall.DefineByCanvas = false; p.LedWall.Columns = 10; p.LedWall.Rows = 6;
        });
        Add("Walls & blend", "LED wall from canvas 1920×1080 / 104px", p =>
        {
            p.Kind = PatternKind.LedWall;
            p.LedWall.TileWidth = 104; p.LedWall.TileHeight = 104;
            p.LedWall.DefineByCanvas = true; p.LedWall.CanvasWidth = 1920; p.LedWall.CanvasHeight = 1080;
        });
        Add("Walls & blend", "Video wall 2×2 1080p", p =>
        {
            p.Kind = PatternKind.VideoWall;
            p.VideoWall.Columns = 2; p.VideoWall.Rows = 2;
            p.VideoWall.ElementWidth = 1920; p.VideoWall.ElementHeight = 1080;
        });
        Add("Walls & blend", "Blend 2× WUXGA · 320 px", p =>
        {
            p.Kind = PatternKind.ProjectionBlend;
            p.Blend.Projectors = 2; p.Blend.NativeWidth = 1920; p.Blend.NativeHeight = 1200; p.Blend.OverlapPx = 320;
        });
        Add("Walls & blend", "Blend 3× WUXGA · 400 px", p =>
        {
            p.Kind = PatternKind.ProjectionBlend;
            p.Blend.Projectors = 3; p.Blend.NativeWidth = 1920; p.Blend.NativeHeight = 1200; p.Blend.OverlapPx = 400;
        });
        Add("Walls & blend", "Blend 4× WUXGA · 400 px", p =>
        {
            p.Kind = PatternKind.ProjectionBlend;
            p.Blend.Projectors = 4; p.Blend.NativeWidth = 1920; p.Blend.NativeHeight = 1200; p.Blend.OverlapPx = 400;
        });
        Add("Walls & blend", "Blend 2×2 WUXGA · 320/240 px", p =>
        {
            p.Kind = PatternKind.ProjectionBlend;
            p.Blend.Projectors = 2; p.Blend.Rows = 2; p.Blend.NativeWidth = 1920; p.Blend.NativeHeight = 1200;
            p.Blend.OverlapPx = 320; p.Blend.OverlapAcrossPx = 240;
        });

        // Motion
        Add("Motion", "Moving bar 480 px/s", p => { p.Kind = PatternKind.Motion; p.Motion.Variant = MotionVariant.MovingBar; p.Motion.PxPerFrame = 0; });
        Add("Motion", "Judder bar 8 px/frame", p => { p.Kind = PatternKind.Motion; p.Motion.Variant = MotionVariant.MovingBar; p.Motion.PxPerFrame = 8; });
        Add("Motion", "Bouncing FPS box", p => { p.Kind = PatternKind.Motion; p.Motion.Variant = MotionVariant.BouncingBox; });
        Add("Motion", "Frame flash", p => { p.Kind = PatternKind.Motion; p.Motion.Variant = MotionVariant.FrameFlash; });
        Add("Motion", "Zone plate", p => { p.Kind = PatternKind.Motion; p.Motion.Variant = MotionVariant.ZonePlate; });

        // Effects: the fractal scenes.
        foreach (var scene in FractalPresets.Names)
        {
            var name = scene;
            Add("Effects", name, p =>
            {
                p.Kind = PatternKind.Fractal;
                FractalPresets.Apply(name, p.Fractal);
            });
        }

        // Particles: every scene of every pack, filed under its pack.
        foreach (var pack in ParticlePresets.Packs)
        {
            var name = pack.Name;
            list.Add(new BuiltInPreset(pack.Category, name, p =>
            {
                p.Kind = PatternKind.Particles;
                ParticlePresets.Apply(name, p.Particles);
            }, "Particles"));
        }

        return list;
    }
}
