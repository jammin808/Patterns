using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The adaptive quality ladder — a game engine's dynamic resolution, for the effects. The frame
/// budget measures and names a slow frame; this acts on it: when the worst output frame stays
/// past the hitch line for three seconds running, the effects step down a level (particles and
/// fractal iterations to 70 %, then 50 %, then 35 %; the CPU fractal raster shrinks with them),
/// and after thirty clean seconds they step back up. The same level applies to every sink at
/// once, so two outputs of one canvas and the NDI feed keep the same picture. The Machine page
/// can lock a level (Full for a machine that must never step down; Balanced or Economy for a
/// small laptop from the first minute). Pure; the App feeds it one second at a time.
/// </summary>
public sealed class QualityLadder
{
    /// <summary>The lowest level; 0 is full quality.</summary>
    public const int Lowest = 3;

    /// <summary>A second whose worst frame is past this counts against the level: the frame budget's hitch line.</summary>
    public const double StepDownMs = FrameBudget.SlowMs;

    /// <summary>Slow seconds in a row before a step down — a single hitch (a look change, a clip open) never steps.</summary>
    public const int SlowSecondsToStepDown = 3;

    /// <summary>Clean seconds in a row before a step back up — so the ladder never oscillates across the line.</summary>
    public const int CleanSecondsToStepUp = 30;

    private static readonly double[] Factors = { 1.0, 0.7, 0.5, 0.35 };

    /// <summary>The engine's one ladder: every renderer reads it, the App's poll drives it.</summary>
    public static QualityLadder Shared { get; } = new();

    private int _level;
    private int _slowRun;
    private int _cleanRun;

    public QualityMode Mode { get; private set; } = QualityMode.Auto;

    /// <summary>0 (full) to <see cref="Lowest"/>; read on the render threads.</summary>
    public int Level => Volatile.Read(ref _level);

    /// <summary>What the effects are scaled by at this level.</summary>
    public double Factor => Factors[Level];

    /// <summary>Why the level is what it is — the sink and the seconds, or the clean run, or the lock.</summary>
    public string Cause { get; private set; } = "";

    public DateTime? ChangedUtc { get; private set; }

    /// <summary>How often the ladder stepped down this session.</summary>
    public int StepsDown { get; private set; }

    public static double FactorOf(int level) => Factors[Math.Clamp(level, 0, Lowest)];

    /// <summary>The level a mode locks, or -1 for Auto.</summary>
    public static int LockedLevel(QualityMode mode) => mode switch
    {
        QualityMode.Full => 0,
        QualityMode.Balanced => 1,
        QualityMode.Economy => 2,
        _ => -1,
    };

    /// <summary>The Machine page's choice: Auto adapts, the rest lock a level. True when something changed.</summary>
    public bool SetMode(QualityMode mode)
    {
        if (Mode == mode) return false;
        Mode = mode;
        _slowRun = 0;
        _cleanRun = 0;
        var locked = LockedLevel(mode);
        if (locked >= 0)
        {
            Volatile.Write(ref _level, locked);
            Cause = "locked on the Machine page";
            ChangedUtc = null;
        }
        return true;
    }

    /// <summary>One second of the worst sink's worst frame. True when the level changed.</summary>
    public bool Observe(double worstMs, string sink, DateTime nowUtc)
    {
        if (Mode != QualityMode.Auto) return false;
        if (worstMs > StepDownMs)
        {
            _slowRun++;
            _cleanRun = 0;
            if (_slowRun >= SlowSecondsToStepDown && Level < Lowest)
            {
                Volatile.Write(ref _level, Level + 1);
                _slowRun = 0;
                StepsDown++;
                Cause = $"{sink}: frames past {StepDownMs:0} ms for {SlowSecondsToStepDown} s";
                ChangedUtc = nowUtc;
                return true;
            }
            return false;
        }
        _slowRun = 0;
        _cleanRun++;
        if (_cleanRun >= CleanSecondsToStepUp && Level > 0)
        {
            Volatile.Write(ref _level, Level - 1);
            _cleanRun = 0;
            Cause = $"{CleanSecondsToStepUp} clean seconds";
            ChangedUtc = nowUtc;
            return true;
        }
        return false;
    }

    /// <summary>Particles at a level: never fewer than one.</summary>
    public static int Particles(int count, double factor) => Math.Max(1, (int)Math.Round(count * factor));

    /// <summary>Fractal iterations at a level: never fewer than eight, which still draws the set.</summary>
    public static int Iterations(int iterations, double factor) => Math.Max(8, (int)Math.Round(iterations * factor));

    /// <summary>"70%" — the same in every culture, so the desk, STATE and the tests read one string.</summary>
    public static string Percent(double factor) => $"{Math.Round(factor * 100):0}%";

    /// <summary>The CPU raster's width scale: the pixel count follows the factor (a width scale of √factor), never below half.</summary>
    public static double RasterScale(double factor) => Math.Clamp(Math.Sqrt(Math.Clamp(factor, 0.1, 1)), 0.5, 1);

    /// <summary>The Machine page's line.</summary>
    public string Describe()
    {
        var level = Level;
        var pct = Percent(Factor);
        if (Mode != QualityMode.Auto)
        {
            return level == 0
                ? "Full: locked at full quality — the ladder never steps down here; a slow frame stays slow and the frame budget says so."
                : $"{Mode}: locked at level {level} of {Lowest} — particles and fractal iterations at {pct}, whatever the frames do.";
        }
        if (level == 0)
        {
            var history = StepsDown > 0 ? $" Stepped down {StepsDown} time{(StepsDown == 1 ? "" : "s")} this session and came back." : "";
            return "Auto: full quality — every effect at what the show set." + history;
        }
        return $"Auto, level {level} of {Lowest}: particles and fractal iterations at {pct} ({Cause}); back up a level after {CleanSecondsToStepUp} clean seconds.";
    }

    public void Reset()
    {
        Mode = QualityMode.Auto;
        Volatile.Write(ref _level, 0);
        _slowRun = 0;
        _cleanRun = 0;
        Cause = "";
        ChangedUtc = null;
        StepsDown = 0;
    }
}
