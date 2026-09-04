using Patterns.Core.Model;

namespace Patterns.Core.Particles;

/// <summary>
/// Where an edge emitter's particles should be born so the whole canvas stays covered. Pure.
/// A field that drifts sideways — wind, a slanted direction — sweeps the upwind side bare when
/// every particle is born on the top edge: in the steady state the flux through the upwind side
/// edge is as real as the flux through the top, so that share of births belongs there, entering
/// at the depth and with the sideways speed a particle born on the top would have by then.
/// </summary>
/// <param name="SideFraction">The share of births that enter through the upwind side edge (0 – 0.75).</param>
/// <param name="FromLeft">The upwind side: the field drifts right, so it enters from the left.</param>
/// <param name="AccelShare">How much of the sideways speed is built up on the way (1 = none at birth, all from the wind).</param>
/// <param name="V0">Along-flow speed at birth, positive into the canvas.</param>
/// <param name="A">Along-flow acceleration, positive into the canvas.</param>
/// <param name="Ax">The sim's sideways acceleration (wind) in canvas axes.</param>
/// <param name="Ay">The sim's downward acceleration (gravity) in canvas axes.</param>
/// <param name="Cross">Seconds the mean particle takes to cross the canvas along the flow.</param>
public readonly record struct EdgeFlux(float SideFraction, bool FromLeft, float AccelShare, float V0, float A, float Ax, float Ay, float Cross)
{
    public static readonly EdgeFlux None = new(0, true, 0, 0, 0, 0, 0, 0);

    /// <summary>Never more than this share leaves the top edge: the edge the operator chose stays the main source.</summary>
    public const float MaxSideFraction = 0.75f;

    /// <summary>Below this the side share is noise, and the estimate says so with a plain zero.</summary>
    public const float MinSideFraction = 0.02f;

    public static EdgeFlux Estimate(ParticleOptions o, float w, float h)
    {
        var sign = o.Emitter switch
        {
            ParticleEmitter.TopEdge => 1f,
            ParticleEmitter.BottomEdge => -1f,
            _ => 0f,
        };
        if (sign == 0 || w <= 0 || h <= 0) return None;

        var speed = (float)((o.SpeedMin + o.SpeedMax) / 2);
        var dir = (float)(o.DirectionDeg * Math.PI / 180);
        var vx0 = speed * MathF.Cos(dir);
        var vy0 = speed * MathF.Sin(dir);
        var ax = (float)(o.WindX * 0.35);   // ParticleSim.StepFixed: Vx += WindX * dt * 0.35
        var ay = (float)o.GravityY;         // ParticleSim.StepFixed: Vy += GravityY * dt
        var v0 = sign * vy0;
        var a = sign * ay;

        var cross = TimeToTravel(v0, a, h);
        if (float.IsInfinity(cross)) return None; // the flow never crosses: nothing to balance
        cross = MathF.Min(cross, 60);
        var vxMean = vx0 + 0.5f * ax * cross;
        var vyMean = MathF.Max(v0 + 0.5f * a * cross, 0.01f);
        var side = h * MathF.Abs(vxMean);
        var top = w * vyMean;
        var fraction = side / (side + top);
        if (fraction < MinSideFraction) return None;

        var built = MathF.Abs(ax * cross);
        var atBirth = MathF.Abs(vx0);
        var share = built + atBirth > 0 ? built / (built + atBirth) : 0;
        return new EdgeFlux(MathF.Min(fraction, MaxSideFraction), vxMean > 0, share, v0, a, ax, ay, cross);
    }

    /// <summary>Seconds for something moving at <paramref name="v0"/> with acceleration <paramref name="a"/> to first travel a distance; infinity when it never does.</summary>
    public static float TimeToTravel(float v0, float a, float distance)
    {
        if (distance <= 0) return 0;
        if (MathF.Abs(a) < 1e-4f) return v0 > 1e-4f ? distance / v0 : float.PositiveInfinity;
        var disc = v0 * v0 + 2 * a * distance;
        if (disc < 0) return float.PositiveInfinity;
        var root = MathF.Sqrt(disc);
        var t1 = (-v0 - root) / a;
        var t2 = (-v0 + root) / a;
        var t = float.PositiveInfinity;
        if (t1 > 0) t = t1;
        if (t2 > 0 && t2 < t) t = t2;
        return t;
    }
}
