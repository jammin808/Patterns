using Patterns.Core.Model;
using Patterns.Core.Rendering;

namespace Patterns.Core.Patterns;

public static class PatternRegistry
{
    public static IReadOnlyDictionary<PatternKind, IPatternRenderer> CreateAll() =>
        new Dictionary<PatternKind, IPatternRenderer>
        {
            [PatternKind.Grid] = new GridPattern(),
            [PatternKind.Checkerboard] = new CheckerboardPattern(),
            [PatternKind.ColorBars] = new BarsPattern(),
            [PatternKind.Ramp] = new RampPattern(),
            [PatternKind.Focus] = new FocusPattern(),
            [PatternKind.Geometry] = new GeometryPattern(),
            [PatternKind.FlatField] = new FlatFieldPattern(),
            [PatternKind.LedWall] = new LedWallPattern(),
            [PatternKind.VideoWall] = new VideoWallPattern(),
            [PatternKind.ProjectionBlend] = new BlendPattern(),
            [PatternKind.Motion] = new MotionPattern(),
            [PatternKind.ColorCycle] = new ColorCyclePattern(),
            [PatternKind.Media] = new MediaPattern(),
            [PatternKind.Particles] = new ParticlePattern(),
        };
}
