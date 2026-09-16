namespace Patterns.Core.Services;

/// <summary>A camera's resting place: the scale and the offset — a value a view stack keeps and a share line carries.</summary>
public readonly record struct EyeView(double Scale, double X, double Y);

/// <summary>
/// The Eye's camera (round 66): pure maths, no UI. A scale and an offset map the world to the view
/// (view = world × scale + offset); fit-to-rectangle; zoom about a view point so the world point
/// under the pointer stays under it; pan; and a critically damped spring on the offset and on the
/// logarithm of the scale, stepped by the frame's dt and sub-stepped when a frame is long, so a
/// focus glides and never overshoots and a 30 Hz machine gets the same motion as a 144 Hz one.
/// </summary>
public sealed class EyeCamera
{
    public const double MinScale = 0.12;
    public const double MaxScale = 3.0;
    private const double SubStep = 1.0 / 120;
    private const double SettleDistance = 0.25;     // view pixels
    private const double SettleLogScale = 0.002;

    private double _logScale;
    private double _targetLogScale;
    private double _x, _y;
    private double _vx, _vy, _vs;

    public EyeCamera()
    {
        Scale = 1;
        TargetScale = 1;
    }

    public double Scale { get; private set; }
    public double X => _x;
    public double Y => _y;
    public double TargetScale { get; private set; }
    public double TargetX { get; private set; }
    public double TargetY { get; private set; }

    /// <summary>The spring's natural frequency, radians per second: higher is snappier; the damping is always critical.</summary>
    public double Omega { get; set; } = 10;

    public bool IsSettled => Math.Abs(TargetX - X) < SettleDistance && Math.Abs(TargetY - Y) < SettleDistance && Math.Abs(_targetLogScale - _logScale) < SettleLogScale
                             && Math.Abs(_vx) < SettleDistance && Math.Abs(_vy) < SettleDistance && Math.Abs(_vs) < SettleLogScale;

    public EyeView View => new(Scale, X, Y);

    public EyeView Target => new(TargetScale, TargetX, TargetY);

    /// <summary>Fits a world rectangle into a view with a margin, the scale clamped; animated unless told to snap.</summary>
    public void FitTo(EyeRect world, double viewW, double viewH, double pad = 40, double maxScale = MaxScale, bool animate = true)
    {
        if (world.IsEmpty || viewW <= 0 || viewH <= 0) return;
        var sx = Math.Max(1, viewW - 2 * pad) / world.W;
        var sy = Math.Max(1, viewH - 2 * pad) / world.H;
        var scale = Math.Clamp(Math.Min(sx, sy), MinScale, Math.Min(maxScale, MaxScale));
        var x = viewW / 2 - world.CenterX * scale;
        var y = viewH / 2 - world.CenterY * scale;
        SetTarget(scale, x, y, animate);
    }

    /// <summary>A focus: the thing and its neighbours fitted, never closer than 1.6× — a node alone is not a poster.</summary>
    public void FocusOn(EyeRect area, double viewW, double viewH, bool animate = true) => FitTo(area.Inflate(40), viewW, viewH, 40, 1.6, animate);

    /// <summary>Zooms by a factor about a view point: the world point under it stays under it. Immediate, as a wheel should be.</summary>
    public void ZoomAt(double vx, double vy, double factor)
    {
        if (factor <= 0 || !double.IsFinite(factor)) return;
        var scale = Math.Clamp(TargetScale * factor, MinScale, MaxScale);
        var ratio = scale / TargetScale;
        var x = vx - (vx - TargetX) * ratio;
        var y = vy - (vy - TargetY) * ratio;
        SetTarget(scale, x, y, animate: false);
    }

    /// <summary>Moves the picture by view pixels — the drag; immediate, the spring is not fought.</summary>
    public void Pan(double dx, double dy) => SetTarget(TargetScale, TargetX + dx, TargetY + dy, animate: false);

    /// <summary>Back to a kept view.</summary>
    public void Restore(EyeView view, bool animate = true) => SetTarget(Math.Clamp(view.Scale, MinScale, MaxScale), view.X, view.Y, animate);

    /// <summary>The current view jumps to the target; the spring is still.</summary>
    public void Snap()
    {
        Scale = TargetScale;
        _logScale = _targetLogScale;
        _x = TargetX;
        _y = TargetY;
        _vx = _vy = _vs = 0;
    }

    /// <summary>One frame of the spring: true while the camera still moves.</summary>
    public bool Step(double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0) return !IsSettled;
        if (IsSettled)
        {
            Snap();
            return false;
        }
        dt = Math.Min(dt, 0.25);                                       // a stall is not a leap
        var w = Math.Max(0.1, Omega);
        while (dt > 0)
        {
            var h = Math.Min(dt, SubStep);
            dt -= h;
            Integrate(ref _logScale, _targetLogScale, ref _vs, w, h);
            Integrate(ref _x, TargetX, ref _vx, w, h);
            Integrate(ref _y, TargetY, ref _vy, w, h);
        }
        Scale = Math.Exp(_logScale);
        if (IsSettled)
        {
            Snap();
            return false;
        }
        return true;
    }

    public (double X, double Y) WorldToView(double wx, double wy) => (wx * Scale + X, wy * Scale + Y);

    public (double X, double Y) ViewToWorld(double vx, double vy) => ((vx - X) / Scale, (vy - Y) / Scale);

    public EyeRect WorldToView(EyeRect r) => new(r.X * Scale + X, r.Y * Scale + Y, r.W * Scale, r.H * Scale);

    private void SetTarget(double scale, double x, double y, bool animate)
    {
        TargetScale = scale;
        _targetLogScale = Math.Log(scale);
        TargetX = x;
        TargetY = y;
        if (!animate) Snap();
    }

    /// <summary>Semi-implicit Euler on a critically damped spring: a = ω²(target − x) − 2ωv.</summary>
    private static void Integrate(ref double x, double target, ref double v, double w, double h)
    {
        var a = w * w * (target - x) - 2 * w * v;
        v += a * h;
        x += v * h;
    }
}
