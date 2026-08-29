using Patterns.Core.Model;

namespace Patterns.Core.Particles;

/// <summary>Curated starting points for the mini studio. Applying one overwrites the studio parameters.</summary>
public static class ParticlePresets
{
    public static readonly string[] Names =
    {
        "Snow", "Confetti", "Starfield", "Rain", "Bokeh", "Embers", "Fireflies",
    };

    public static void Apply(string name, ParticleOptions o)
    {
        switch (name)
        {
            case "Confetti":
                o.Emitter = ParticleEmitter.TopEdge;
                o.Shape = ParticleShape.Square;
                o.Count = 900;
                o.SizeMin = 3; o.SizeMax = 8;
                o.SpeedMin = 90; o.SpeedMax = 240;
                o.DirectionDeg = 90; o.SpreadDeg = 50;
                o.GravityY = 60; o.WindX = 25; o.Wobble = 0.8;
                o.RotationSpeed = 1.4;
                o.Glow = false;
                o.ColorsCsv = "#FF4D6D,#FFD166,#06D6A0,#4CC9F0,#B388FF,#FFFFFF";
                break;

            case "Starfield":
                o.Emitter = ParticleEmitter.Center;
                o.Shape = ParticleShape.Circle;
                o.Count = 1600;
                o.SizeMin = 1; o.SizeMax = 2.6;
                o.SpeedMin = 40; o.SpeedMax = 160;
                o.GravityY = 0; o.WindX = 0; o.Wobble = 0;
                o.RotationSpeed = 0;
                o.Glow = true;
                o.ColorsCsv = "#FFFFFF,#BBDDFF,#FFEECC";
                break;

            case "Rain":
                o.Emitter = ParticleEmitter.TopEdge;
                o.Shape = ParticleShape.Streak;
                o.Count = 1400;
                o.SizeMin = 5; o.SizeMax = 11;
                o.SpeedMin = 900; o.SpeedMax = 1400;
                o.DirectionDeg = 83; o.SpreadDeg = 4;
                o.GravityY = 240; o.WindX = -40; o.Wobble = 0;
                o.RotationSpeed = 0;
                o.Glow = false;
                o.ColorsCsv = "#9FBFDF,#7FA8CC";
                break;

            case "Bokeh":
                o.Emitter = ParticleEmitter.FullArea;
                o.Shape = ParticleShape.Bokeh;
                o.Count = 90;
                o.SizeMin = 20; o.SizeMax = 90;
                o.SpeedMin = 4; o.SpeedMax = 22;
                o.DirectionDeg = 0; o.SpreadDeg = 360;
                o.GravityY = -4; o.WindX = 6; o.Wobble = 0.5;
                o.RotationSpeed = 0;
                o.Glow = true;
                o.ColorsCsv = "#FFB020,#F03EAE,#3EC1F3,#FFE28A";
                break;

            case "Embers":
                o.Emitter = ParticleEmitter.BottomEdge;
                o.Shape = ParticleShape.Circle;
                o.Count = 500;
                o.SizeMin = 1.5; o.SizeMax = 4.5;
                o.SpeedMin = 50; o.SpeedMax = 140;
                o.DirectionDeg = -90; o.SpreadDeg = 40;
                o.GravityY = -55; o.WindX = 18; o.Wobble = 0.9;
                o.RotationSpeed = 0;
                o.Glow = true;
                o.ColorsCsv = "#FF6B35,#FFA630,#FFD97D,#FF3E1D";
                break;

            case "Fireflies":
                o.Emitter = ParticleEmitter.FullArea;
                o.Shape = ParticleShape.Bokeh;
                o.Count = 160;
                o.SizeMin = 2; o.SizeMax = 7;
                o.SpeedMin = 6; o.SpeedMax = 30;
                o.DirectionDeg = 0; o.SpreadDeg = 360;
                o.GravityY = 0; o.WindX = 0; o.Wobble = 1;
                o.RotationSpeed = 0;
                o.Glow = true;
                o.ColorsCsv = "#D7FF6E,#A8FF3E,#FFFFAA";
                break;

            default: // Snow
                o.Emitter = ParticleEmitter.TopEdge;
                o.Shape = ParticleShape.Circle;
                o.Count = 900;
                o.SizeMin = 1.5; o.SizeMax = 5;
                o.SpeedMin = 30; o.SpeedMax = 95;
                o.DirectionDeg = 90; o.SpreadDeg = 18;
                o.GravityY = 8; o.WindX = 12; o.Wobble = 0.7;
                o.RotationSpeed = 0;
                o.Glow = false;
                o.ColorsCsv = "#FFFFFF,#E8F2FF";
                break;
        }
        o.Preset = name;
    }
}
