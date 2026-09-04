namespace Patterns.Core.Effects;

/// <summary>What the sound is doing right now, each 0–1: the overall level and three bands.</summary>
public readonly record struct AudioLevelFrame(float Level, float Low, float Mid, float High)
{
    public static readonly AudioLevelFrame Zero = default;

    public bool IsSilent => Level <= 0.001f && Low <= 0.001f && Mid <= 0.001f && High <= 0.001f;
}

/// <summary>
/// One window of sound into levels. Pure: a Hann window, a radix-2 FFT, the energy in three
/// bands (20–250, 250–2000, 2000–8000 Hz) and the RMS, each scaled so ordinary programme
/// material lands in the middle of 0–1 and a full-scale sine reaches the top.
/// </summary>
public static class Spectrum
{
    public const int Window = 1024;

    public const double LowHz = 20, LowMidHz = 250, MidHighHz = 2000, HighHz = 8000;

    public static AudioLevelFrame Analyse(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0) return AudioLevelFrame.Zero;
        var n = Window;
        var re = new double[n];
        var im = new double[n];
        var start = Math.Max(0, samples.Length - n);
        var count = Math.Min(n, samples.Length);
        double sumSq = 0;
        for (var i = 0; i < count; i++)
        {
            var s = samples[start + i];
            sumSq += s * s;
            var hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            re[i] = s * hann;
        }
        Fft(re, im);

        double low = 0, mid = 0, high = 0;
        for (var k = 1; k < n / 2; k++)
        {
            var hz = k * sampleRate / (double)n;
            if (hz < LowHz || hz > HighHz) continue;
            // A Hann window halves a sine's peak; the 2/n puts a full-scale sine's bin at ~1.
            var mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * 4.0 / n;
            var power = mag * mag;
            if (hz < LowMidHz) low += power;
            else if (hz < MidHighHz) mid += power;
            else high += power;
        }
        var rms = Math.Sqrt(sumSq / count);
        return new AudioLevelFrame(
            Clamp(rms * 2.5),
            Clamp(Math.Sqrt(low) * 1.5),
            Clamp(Math.Sqrt(mid) * 1.5),
            Clamp(Math.Sqrt(high) * 1.5));
    }

    private static float Clamp(double v) => (float)Math.Clamp(v, 0, 1);

    /// <summary>In-place radix-2 FFT; the arrays' length must be a power of two.</summary>
    public static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wr = Math.Cos(ang);
            var wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}

/// <summary>Levels that jump up and ease down — an attack of 30 ms and a release of 250 ms — so a picture follows a beat without flickering.</summary>
public sealed class LevelSmoother
{
    private AudioLevelFrame _current;

    public double AttackSeconds { get; init; } = 0.03;

    public double ReleaseSeconds { get; init; } = 0.25;

    public AudioLevelFrame Current => _current;

    public AudioLevelFrame Follow(AudioLevelFrame target, double dtSeconds)
    {
        var dt = Math.Clamp(dtSeconds, 0, 5);
        _current = new AudioLevelFrame(
            Step(_current.Level, target.Level, dt),
            Step(_current.Low, target.Low, dt),
            Step(_current.Mid, target.Mid, dt),
            Step(_current.High, target.High, dt));
        return _current;
    }

    private float Step(float from, float to, double dt)
    {
        var tau = to > from ? AttackSeconds : ReleaseSeconds;
        var k = 1 - Math.Exp(-dt / Math.Max(1e-4, tau));
        return (float)(from + (to - from) * k);
    }
}

/// <summary>
/// The sound levels every renderer reads: one static channel, written by the analyser's capture
/// thread and read on every sink's frame. Levels older than a second read as silence, so a
/// capture that stopped never leaves a picture frozen mid-beat.
/// </summary>
public static class AudioLevels
{
    private static readonly object Gate = new();
    private static AudioLevelFrame _frame;
    private static DateTime _atUtc = DateTime.MinValue;

    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(1);

    public static void Publish(AudioLevelFrame frame, DateTime utcNow)
    {
        lock (Gate)
        {
            _frame = frame;
            _atUtc = utcNow;
        }
    }

    public static AudioLevelFrame Read(DateTime utcNow)
    {
        lock (Gate)
        {
            var age = utcNow - _atUtc;
            return age > Stale || age < -Stale ? AudioLevelFrame.Zero : _frame;
        }
    }

    public static DateTime LastUtc
    {
        get
        {
            lock (Gate) return _atUtc;
        }
    }

    public static void Clear() => Publish(AudioLevelFrame.Zero, DateTime.MinValue);
}
