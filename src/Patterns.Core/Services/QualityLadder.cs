using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// One sink's last complete second, as the ladder judges it: the frame time 95 % of its frames
/// came in under, its worst frame, the presentation slots it missed, and the rate it presents at
/// (0 for an unpaced sink, judged at 60).
/// </summary>
public readonly record struct FrameSecond(double P95Ms, double WorstMs, int Missed, int TargetFps)
{
    /// <summary>A second known by its worst frame alone — a sink without a histogram, the tests.</summary>
    public static FrameSecond OfWorst(double worstMs, int targetFps = 0) => new(worstMs, worstMs, 0, targetFps);

    /// <summary>The frame slot at this second's rate, ms.</summary>
    public double SlotMs => QualityLadder.SlotMs(TargetFps);

    /// <summary>The budget this second's frames are judged against, ms.</summary>
    public double BudgetMs => QualityLadder.BudgetMs(TargetFps);

    /// <summary>The rate the second was judged at.</summary>
    public int JudgedFps => TargetFps > 0 ? TargetFps : QualityLadder.DefaultFps;
}

/// <summary>
/// What the last session on this machine settled at: the ladder's level when it ended, the steps
/// it took, and the machine it was — so Auto starts there next time rather than at full and three
/// slow seconds later.
/// </summary>
public sealed record QualityProfile(string Machine, int Level, int StepsDown, DateTime SavedUtc);

/// <summary>The profile's file beside the settings: read at the start, written when the ladder settles and at the end.</summary>
public sealed class QualityProfileStore
{
    public const string FileName = "patterns.quality.json";

    public QualityProfileStore(string directory) => FilePath = Path.Combine(directory, FileName);

    public string FilePath { get; }

    public QualityProfile? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonUtil.Deserialize<QualityProfile>(File.ReadAllText(FilePath));
        }
        catch (Exception ex)
        {
            Log.Warn("Quality profile unreadable.", ex);
            return null;
        }
    }

    /// <summary>A temp file moved over the old one: a reader never sees half a profile.</summary>
    public void Write(QualityProfile profile)
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonUtil.Serialize(profile));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Quality profile could not be written.", ex);
        }
    }
}

/// <summary>
/// The adaptive quality ladder — a game engine's dynamic resolution, for the effects. The frame
/// budget measures a sink's second; this acts on it: when an output's frames press against its
/// own rate's budget for three seconds running — the second's p95 past 85 % of the frame slot
/// (14 ms at 60 fps, 28 at 30), three presentation slots missed, or a frame past the stutter line
/// — the effects step down a level (particles and fractal iterations to 70 %, then 50 %, then
/// 35 %; the CPU fractal raster shrinks with them), and after thirty clean seconds they step back
/// up. The same level applies to every sink at once, so two outputs of one canvas and the NDI
/// feed keep the same picture. The Machine page can lock a level (Full for a machine that must
/// never step down; Balanced or Economy for a small laptop from the first minute). Auto starts a
/// session where the last one on this machine settled, or a level down on a small machine. The
/// ladder scales the effects alone: particles, fractal iterations and the CPU raster, the
/// reactive pattern's detail, the lower third's particles — never a test card, text, a lower
/// third's words, the authority, the cue timing. Pure; the App feeds it one second at a time.
/// </summary>
public sealed class QualityLadder
{
    /// <summary>The lowest level; 0 is full quality.</summary>
    public const int Lowest = 3;

    /// <summary>The rate an unpaced sink — the preview, a monitor — is judged at: a display's usual 60 Hz.</summary>
    public const int DefaultFps = 60;

    /// <summary>The share of a frame slot a second's frames should come in under: the budget is 85 % of the slot, so a 60 fps output is judged against 14 ms and a 30 fps one against 28.</summary>
    public const double Safety = 0.85;

    /// <summary>Presentation slots missed in one second that count against the level on their own: the room saw judder.</summary>
    public const int MissedSlotsToCount = 3;

    /// <summary>The frame slot at a rate, ms (an unpaced sink's at <see cref="DefaultFps"/>).</summary>
    public static double SlotMs(int targetFps) => 1000.0 / (targetFps > 0 ? targetFps : DefaultFps);

    /// <summary>The budget a second's frames are judged against at a rate, ms: the slot less the safety margin.</summary>
    public static double BudgetMs(int targetFps) => SlotMs(targetFps) * Safety;

    /// <summary>Whether a second counts against the level: its p95 past its budget, slots missed, or a stutter.</summary>
    public static bool UnderPressure(in FrameSecond s)
        => s.P95Ms > BudgetMs(s.TargetFps) || s.Missed >= MissedSlotsToCount || s.WorstMs > FrameBudget.StutterMs;

    /// <summary>Why a second counts, or how it was clean: "p95 20 ms against a 14.2 ms budget at 60 fps", "3 slots missed in a second at 60 fps", "a 51 ms frame".</summary>
    public static string PressureWords(in FrameSecond s)
    {
        var budget = BudgetMs(s.TargetFps);
        if (s.P95Ms > budget) return $"p95 {s.P95Ms:0.#} ms against a {budget:0.#} ms budget at {s.JudgedFps} fps";
        if (s.Missed >= MissedSlotsToCount) return $"{s.Missed} slots missed in a second at {s.JudgedFps} fps";
        if (s.WorstMs > FrameBudget.StutterMs) return $"a {s.WorstMs:0} ms frame";
        return $"p95 {Math.Max(0, s.P95Ms):0.#} ms under the {budget:0.#} ms budget at {s.JudgedFps} fps";
    }

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

    /// <summary>
    /// Where Auto begins a session: the level the last session on this machine settled at, or
    /// one down on a small machine — a start, not a step: nothing counted, no time stamped.
    /// Ignored under a lock. True when the session starts below full.
    /// </summary>
    public bool Start(int level, string cause)
    {
        if (Mode != QualityMode.Auto) return false;
        level = Math.Clamp(level, 0, Lowest);
        Volatile.Write(ref _level, level);
        _slowRun = 0;
        _cleanRun = 0;
        Cause = level > 0 ? cause : "";
        ChangedUtc = null;
        return level > 0;
    }

    /// <summary>
    /// The level Auto starts a session at: what the profile says when it is this machine's, else
    /// one down on a small machine (under <see cref="WarmUpPlan.SmallMachineGB"/>, the same small
    /// machine the warm-up knows) until thirty clean seconds prove it, else full.
    /// </summary>
    public static int StartingLevel(QualityProfile? profile, string machine, double machineGB)
    {
        if (profile is not null && profile.Machine == machine) return Math.Clamp(profile.Level, 0, Lowest);
        return machineGB < WarmUpPlan.SmallMachineGB ? 1 : 0;
    }

    /// <summary>One second of the worst sink, known by its worst frame alone. True when the level changed.</summary>
    public bool Observe(double worstMs, string sink, DateTime nowUtc) => Observe(FrameSecond.OfWorst(worstMs), sink, nowUtc);

    /// <summary>One second of the sink that pressed hardest against its own budget. True when the level changed.</summary>
    public bool Observe(in FrameSecond second, string sink, DateTime nowUtc)
    {
        if (Mode != QualityMode.Auto) return false;
        if (UnderPressure(second))
        {
            _slowRun++;
            _cleanRun = 0;
            if (_slowRun >= SlowSecondsToStepDown && Level < Lowest)
            {
                Volatile.Write(ref _level, Level + 1);
                _slowRun = 0;
                StepsDown++;
                Cause = $"{sink}: {PressureWords(second)} for {SlowSecondsToStepDown} s";
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
