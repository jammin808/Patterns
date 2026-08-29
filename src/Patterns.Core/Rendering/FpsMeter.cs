namespace Patterns.Core.Rendering;

/// <summary>Measures a sink's real presentation cadence. Allocation-free.</summary>
public sealed class FpsMeter
{
    private readonly double[] _intervals = new double[120];
    private int _next;
    private int _filled;
    private double _lastTime = double.NaN;

    public double Fps { get; private set; }
    public double WorstMs { get; private set; }

    public void Tick(double timeSeconds)
    {
        if (!double.IsNaN(_lastTime))
        {
            var dt = timeSeconds - _lastTime;
            if (dt > 0 && dt < 1)
            {
                _intervals[_next] = dt;
                _next = (_next + 1) % _intervals.Length;
                if (_filled < _intervals.Length) _filled++;

                double sum = 0, worst = 0;
                for (var i = 0; i < _filled; i++)
                {
                    sum += _intervals[i];
                    if (_intervals[i] > worst) worst = _intervals[i];
                }
                if (sum > 0) Fps = _filled / sum;
                WorstMs = worst * 1000.0;
            }
        }
        _lastTime = timeSeconds;
    }

    public void Reset()
    {
        _filled = 0;
        _next = 0;
        _lastTime = double.NaN;
        Fps = 0;
        WorstMs = 0;
    }
}
