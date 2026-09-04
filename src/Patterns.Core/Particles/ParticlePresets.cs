using Patterns.Core.Model;

namespace Patterns.Core.Particles;

/// <summary>One curated particle scene: the pack it belongs to, its name, and what it sets.</summary>
public sealed record ParticlePack(string Category, string Name, Action<ParticleOptions> Apply);

/// <summary>
/// Curated starting points for the mini studio, filed in packs — the classic seven first, then
/// awards, modern, nature, moods, star cloths, night skies and feel-good scenes. Applying one
/// sets every studio parameter (never the background or the brand-colour switch, which are the
/// operator's), so a scene reads the same on every rig.
/// </summary>
public static class ParticlePresets
{
    public static readonly IReadOnlyList<ParticlePack> Packs = Build();

    /// <summary>The pack names in the order the page shows them.</summary>
    public static readonly string[] Categories = Packs.Select(p => p.Category).Distinct().ToArray();

    /// <summary>Every scene name, classic first — the chips and the Library's Particles section.</summary>
    public static readonly string[] Names = Packs.Select(p => p.Name).ToArray();

    public static IEnumerable<ParticlePack> In(string category) => Packs.Where(p => p.Category == category);

    /// <summary>Applies a scene by name; an unknown name applies Snow, as it always has.</summary>
    public static void Apply(string name, ParticleOptions o)
    {
        var pack = Packs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Packs[0];
        pack.Apply(o);
        o.Preset = name;
    }

    private static List<ParticlePack> Build()
    {
        var list = new List<ParticlePack>();

        void Add(string category, string name, ParticleEmitter emitter, ParticleShape shape, int count,
            double sizeMin, double sizeMax, double speedMin, double speedMax, double directionDeg, double spreadDeg,
            double gravityY, double windX, double wobble, double spin, bool glow, string colors)
            => list.Add(new ParticlePack(category, name, o =>
            {
                o.Emitter = emitter;
                o.Shape = shape;
                o.Count = count;
                o.SizeMin = sizeMin; o.SizeMax = sizeMax;
                o.SpeedMin = speedMin; o.SpeedMax = speedMax;
                o.DirectionDeg = directionDeg; o.SpreadDeg = spreadDeg;
                o.GravityY = gravityY; o.WindX = windX; o.Wobble = wobble;
                o.RotationSpeed = spin;
                o.Glow = glow;
                o.ColorsCsv = colors;
            }));

        // Classic — the seven the studio has always had, unchanged.
        Add("Classic", "Snow", ParticleEmitter.TopEdge, ParticleShape.Circle, 900, 1.5, 5, 30, 95, 90, 18, 8, 12, 0.7, 0, false, "#FFFFFF,#E8F2FF");
        Add("Classic", "Confetti", ParticleEmitter.TopEdge, ParticleShape.Square, 900, 3, 8, 90, 240, 90, 50, 60, 25, 0.8, 1.4, false, "#FF4D6D,#FFD166,#06D6A0,#4CC9F0,#B388FF,#FFFFFF");
        Add("Classic", "Starfield", ParticleEmitter.Center, ParticleShape.Circle, 1600, 1, 2.6, 40, 160, 0, 360, 0, 0, 0, 0, true, "#FFFFFF,#BBDDFF,#FFEECC");
        Add("Classic", "Rain", ParticleEmitter.TopEdge, ParticleShape.Streak, 1400, 5, 11, 900, 1400, 83, 4, 240, -40, 0, 0, false, "#9FBFDF,#7FA8CC");
        Add("Classic", "Bokeh", ParticleEmitter.FullArea, ParticleShape.Bokeh, 90, 20, 90, 4, 22, 0, 360, -4, 6, 0.5, 0, true, "#FFB020,#F03EAE,#3EC1F3,#FFE28A");
        Add("Classic", "Embers", ParticleEmitter.BottomEdge, ParticleShape.Circle, 500, 1.5, 4.5, 50, 140, -90, 40, -55, 18, 0.9, 0, true, "#FF6B35,#FFA630,#FFD97D,#FF3E1D");
        Add("Classic", "Fireflies", ParticleEmitter.FullArea, ParticleShape.Bokeh, 160, 2, 7, 6, 30, 0, 360, 0, 0, 1, 0, true, "#D7FF6E,#A8FF3E,#FFFFAA");

        // Awards — gold, sparkle, champagne.
        Add("Awards", "Gold dust", ParticleEmitter.FullArea, ParticleShape.Bokeh, 700, 1.5, 4, 6, 24, -90, 360, -3, 4, 0.6, 0, true, "#FFD700,#FFE9A8,#FFB300,#FFF6D5");
        Add("Awards", "Champagne", ParticleEmitter.BottomEdge, ParticleShape.Circle, 600, 1.5, 5, 60, 160, -90, 25, -40, 6, 0.5, 0, true, "#FFF3C4,#FFE082,#FFFFFF");
        Add("Awards", "Red-carpet sparkle", ParticleEmitter.FullArea, ParticleShape.Star, 220, 3, 9, 4, 18, 0, 360, 0, 3, 0.4, 0.6, true, "#FFD700,#FF4D6D,#FFFFFF,#FFB300");
        Add("Awards", "Ticker tape", ParticleEmitter.TopEdge, ParticleShape.Streak, 500, 6, 14, 80, 200, 90, 30, 40, 30, 0.9, 1.0, false, "#FFD700,#FFFFFF,#C0C0C0");

        // Modern — clean, cool, technical.
        Add("Modern", "Data stream", ParticleEmitter.BottomEdge, ParticleShape.Streak, 900, 4, 10, 300, 700, -90, 3, -80, 0, 0, 0, true, "#3EC1F3,#7FE7FF,#FFFFFF");
        Add("Modern", "Pixel dust", ParticleEmitter.FullArea, ParticleShape.Square, 1200, 1, 3, 8, 40, 0, 360, 0, 6, 0.2, 0, true, "#FFFFFF,#9FB6FF,#5C7CFF");
        Add("Modern", "Neon rain", ParticleEmitter.TopEdge, ParticleShape.Streak, 700, 5, 12, 500, 900, 90, 6, 120, 0, 0, 0, true, "#F03EAE,#3EC1F3,#B388FF");
        Add("Modern", "Grid motes", ParticleEmitter.FullArea, ParticleShape.Square, 260, 4, 8, 10, 30, 0, 360, 0, 0, 0.3, 0.2, false, "#3EC1F3,#FFFFFF");

        // Nature — seasons and gardens.
        Add("Nature", "Autumn leaves", ParticleEmitter.TopEdge, ParticleShape.Square, 380, 6, 14, 40, 110, 90, 40, 20, 35, 1, 0.8, false, "#D2691E,#FF8C00,#B22222,#DAA520,#8B4513");
        Add("Nature", "Cherry blossom", ParticleEmitter.TopEdge, ParticleShape.Circle, 600, 3, 8, 30, 90, 90, 35, 6, 22, 0.9, 0, false, "#FFC0CB,#FFB7C5,#FFFFFF,#FF9EB5");
        Add("Nature", "Dandelion seeds", ParticleEmitter.FullArea, ParticleShape.Bokeh, 140, 6, 16, 15, 50, 0, 60, -4, 30, 1, 0, false, "#FFFFFF,#F4F4F4");
        Add("Nature", "Pollen", ParticleEmitter.FullArea, ParticleShape.Bokeh, 400, 2, 5, 6, 26, 0, 360, -2, 8, 0.8, 0, true, "#FFE066,#FFF2A8,#FFD23F");

        // Moods — slow, soft, large.
        Add("Moods", "Calm", ParticleEmitter.FullArea, ParticleShape.Bokeh, 60, 30, 110, 3, 12, 0, 360, 0, 2, 0.4, 0, true, "#3E6FF3,#6BA4FF,#A8D0FF");
        Add("Moods", "Warm glow", ParticleEmitter.FullArea, ParticleShape.Bokeh, 80, 24, 90, 4, 16, -90, 360, -3, 3, 0.5, 0, true, "#FFB020,#FF7A3D,#FFD98A");
        Add("Moods", "Dusk", ParticleEmitter.FullArea, ParticleShape.Bokeh, 110, 16, 70, 5, 20, 0, 360, 0, 5, 0.5, 0, true, "#7B4DFF,#FF6F91,#FFB86B");
        Add("Moods", "Deep blue", ParticleEmitter.FullArea, ParticleShape.Circle, 500, 1, 4, 5, 25, 0, 360, -3, 0, 0.7, 0, true, "#1E5AA8,#3EC1F3,#9FE1FF");

        // Starcloth — a field that stays put and shimmers, like the drape behind a band.
        Add("Starcloth", "Starcloth", ParticleEmitter.FullArea, ParticleShape.Circle, 1400, 0.8, 2.2, 0, 2, 0, 360, 0, 0, 0.15, 0, true, "#FFFFFF,#E8F0FF,#FFF4DA");
        Add("Starcloth", "Starcloth blue", ParticleEmitter.FullArea, ParticleShape.Circle, 1200, 0.8, 2.2, 0, 2, 0, 360, 0, 0, 0.15, 0, true, "#BFD8FF,#7FB4FF,#FFFFFF");
        Add("Starcloth", "Starcloth dense", ParticleEmitter.FullArea, ParticleShape.Circle, 3000, 0.6, 1.8, 0, 2, 0, 360, 0, 0, 0.12, 0, true, "#FFFFFF,#EEF3FF");

        // Night sky — slow stars and the odd streak.
        Add("Night sky", "Night sky", ParticleEmitter.FullArea, ParticleShape.Circle, 900, 0.8, 2.6, 1, 6, 0, 360, 0, 2, 0.2, 0, true, "#FFFFFF,#CFE0FF,#FFE9C4");
        Add("Night sky", "Shooting stars", ParticleEmitter.TopEdge, ParticleShape.Streak, 24, 3, 6, 900, 1600, 25, 10, 0, 0, 0, 0, true, "#FFFFFF,#DDEEFF");
        Add("Night sky", "Milky drift", ParticleEmitter.FullArea, ParticleShape.Bokeh, 300, 2, 7, 2, 8, 0, 360, 0, 3, 0.3, 0, true, "#E6EEFF,#FFFFFF,#CBD7FF");

        // Feel-good — parties, bubbles, sparklers.
        Add("Feel-good", "Party confetti", ParticleEmitter.TopEdge, ParticleShape.Star, 800, 3, 9, 100, 260, 90, 60, 70, 20, 0.9, 1.8, false, "#FF4D6D,#FFD166,#06D6A0,#4CC9F0,#B388FF,#FF8C42");
        Add("Feel-good", "Bubbles", ParticleEmitter.BottomEdge, ParticleShape.Bokeh, 260, 6, 22, 40, 120, -90, 30, -25, 10, 1, 0, false, "#BFEFFF,#E0F7FF,#FFFFFF");
        Add("Feel-good", "Fireworks", ParticleEmitter.Center, ParticleShape.Star, 900, 1.5, 4, 120, 320, 0, 360, 0, 0, 0, 2, true, "#FF4D6D,#FFD166,#4CC9F0,#B388FF,#FFFFFF");
        Add("Feel-good", "Sparkler", ParticleEmitter.Center, ParticleShape.Circle, 700, 1, 3, 160, 360, 0, 360, 0, 0, 0, 0, true, "#FFFFFF,#FFE27A,#FFB020,#FF6B35");
        Add("Feel-good", "Sunshine", ParticleEmitter.FullArea, ParticleShape.Bokeh, 120, 10, 40, 6, 20, -90, 360, -2, 4, 0.5, 0, true, "#FFF176,#FFD54F,#FFFFFF");

        return list;
    }
}
