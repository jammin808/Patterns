using Patterns.Core.Model;

namespace Patterns.Core.Effects;

/// <summary>Editable defaults for the Fractal pattern: a scene sets the family, the view, the depth, the motion and the palette; the sound settings are the operator's.</summary>
public static class FractalPresets
{
    public sealed record Scene(string Name, Action<FractalOptions> Apply);

    public static readonly IReadOnlyList<Scene> Scenes = Build();

    public static readonly string[] Names = Scenes.Select(s => s.Name).ToArray();

    /// <summary>Applies a scene by name; an unknown name applies the first.</summary>
    public static void Apply(string name, FractalOptions o)
    {
        var scene = Scenes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Scenes[0];
        scene.Apply(o);
        o.Preset = scene.Name;
    }

    private static List<Scene> Build()
    {
        var list = new List<Scene>();

        void Add(string name, FractalKind kind, double zoom, double cx, double cy, int iterations, double speed,
            double jr, double ji, string colors)
            => list.Add(new Scene(name, o =>
            {
                o.Kind = kind;
                o.Zoom = zoom;
                o.CenterX = cx;
                o.CenterY = cy;
                o.Iterations = iterations;
                o.Speed = speed;
                o.JuliaReal = jr;
                o.JuliaImag = ji;
                o.ColorsCsv = colors;
            }));

        Add("Mandelbrot classic", FractalKind.Mandelbrot, 1, -0.6, 0, 96, 0.5, -0.72, 0.27, "#0B0C2A,#1E3A8A,#3EC1F3,#FFFFFF,#FFB020");
        Add("Seahorse valley", FractalKind.Mandelbrot, 60, -0.745, 0.1, 200, 0.3, -0.72, 0.27, "#050510,#3A0CA3,#F72585,#FFD166,#FFFFFF");
        Add("Julia swirl", FractalKind.Julia, 1.1, 0, 0, 120, 0.5, -0.72, 0.27, "#10002B,#5A189A,#C77DFF,#FFFFFF,#F72585");
        Add("Julia dragon", FractalKind.Julia, 1.2, 0, 0, 160, 0.4, -0.8, 0.156, "#03071E,#0077B6,#48CAE4,#CAF0F8,#FFB703");
        Add("Burning ship", FractalKind.BurningShip, 1, -0.5, -0.5, 96, 0.3, -0.72, 0.27, "#000000,#7F1D1D,#F97316,#FDE68A,#FFFFFF");
        Add("Newton triad", FractalKind.Newton, 1, 0, 0, 40, 0.4, -0.72, 0.27, "#3EC1F3,#F03EAE,#FFB020");
        Add("Domain warp lava", FractalKind.DomainWarp, 1, 0, 0, 32, 0.8, -0.72, 0.27, "#1A0000,#7F1D1D,#F97316,#FDE68A,#FFFFFF");
        Add("Domain warp ocean", FractalKind.DomainWarp, 1, 0, 0, 32, 0.6, -0.72, 0.27, "#02111B,#0B3954,#087E8B,#BFD7EA,#FFFFFF");

        return list;
    }
}
