namespace Patterns.Core.Effects;

/// <summary>
/// How often the picture may flash. A full-field light-to-dark-to-light change is the one visual
/// effect with a documented harm attached to it: broadcast and web guidance both draw the line at
/// three such changes a second, and a room full of people is exactly who Patterns draws for. A
/// sting's flash channel and a sound-reactive scene both aim straight at that line — a beat at
/// 128 BPM is 2.1 flashes a second, and on the off-beat as well it is 4.3.
///
/// So the engine limits it rather than the presets: a flash that comes too soon after the last one
/// is not shortened or dimmed, it is dropped, and the one after it lands. The peak is capped too —
/// a slow whole-screen white is still unpleasant on a wall.
///
/// One guard per sink (<see cref="Rendering.SinkState"/>), because a flash is what one screen's
/// audience sees; it holds no clock of its own — the show clock is passed in — so a sequence of
/// calls is a unit test rather than a wait.
/// </summary>
public sealed class FlashGuard
{
    /// <summary>The line the guidance draws: three large-area luminance changes in any one second.</summary>
    public const int MaxFlashesPerSecond = 3;

    /// <summary>The most white a flash may put over the picture, whatever it asked for.</summary>
    public const float MaxLevel = 0.7f;

    /// <summary>Above this the screen counts as flashed; the guidance's threshold is a share of the screen's luminance, and a full-field white at a tenth is where that starts to matter.</summary>
    public const float RiseLevel = 0.10f;

    /// <summary>Below this the screen counts as dark again, so the next rise is a new flash. Under the rise level, with room between, so a flicker at the boundary is not counted twice.</summary>
    public const float FallLevel = 0.04f;

    /// <summary>The shortest gap between two flashes.</summary>
    public static readonly double MinPeriodSeconds = 1.0 / MaxFlashesPerSecond;

    private bool _lit;
    private bool _dropped;
    private double _lastRiseSeconds = double.NegativeInfinity;

    /// <summary>Flashes let through since this guard was made (the tests, and the Machine page if it ever asks).</summary>
    public int Allowed { get; private set; }

    /// <summary>Flashes dropped for coming too soon.</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// The flash this frame may actually draw: what was asked for, capped, or nothing when it is
    /// too soon after the last one. A dropped flash stays dropped for the whole of its pulse —
    /// letting it in halfway would be a shorter, sharper flash than the one refused.
    /// </summary>
    /// <param name="wanted">The flash the sting or the scene asked for, 0–1.</param>
    /// <param name="seconds">The show clock.</param>
    public float Limit(float wanted, double seconds)
    {
        // A show clock that went backwards (a reset, a test's own clock): start again rather than
        // hold everything down until the old time comes round.
        if (seconds < _lastRiseSeconds)
        {
            _lastRiseSeconds = double.NegativeInfinity;
            _lit = false;
            _dropped = false;
        }

        if (float.IsNaN(wanted) || wanted <= FallLevel)
        {
            _lit = false;
            _dropped = false;
            return wanted > 0 && !float.IsNaN(wanted) ? Math.Min(wanted, MaxLevel) : 0f;
        }

        if (_lit) return _dropped ? 0f : Math.Min(wanted, MaxLevel);

        // Between the fall and the rise: on its way up, not yet a flash.
        if (wanted < RiseLevel) return _dropped ? 0f : Math.Min(wanted, MaxLevel);

        // A new flash.
        _lit = true;
        if (seconds - _lastRiseSeconds < MinPeriodSeconds)
        {
            _dropped = true;
            Dropped++;
            return 0f;
        }
        _dropped = false;
        _lastRiseSeconds = seconds;
        Allowed++;
        return Math.Min(wanted, MaxLevel);
    }

    /// <summary>Forget the last flash — a sink that has been closed and opened again, and the tests.</summary>
    public void Reset()
    {
        _lit = false;
        _dropped = false;
        _lastRiseSeconds = double.NegativeInfinity;
        Allowed = 0;
        Dropped = 0;
    }

    /// <summary>
    /// How far a sound-reactive scene may swing the picture's brightness: enough to read as
    /// reactive, never enough to be a flash. A scene that wants more than this is asking for a
    /// strobe and should use the flash channel, where <see cref="Limit"/> counts it.
    /// </summary>
    public const float MaxAudioBrightnessSwing = 0.25f;

    /// <summary>The brightness a scene may draw at for an audio level, bounded by <see cref="MaxAudioBrightnessSwing"/>.</summary>
    public static float Brightness(float baseline, float level, float amount)
    {
        var swing = Math.Clamp(amount, 0f, 1f) * MaxAudioBrightnessSwing;
        return Math.Clamp(baseline + (Math.Clamp(level, 0f, 1f) - 0.5f) * 2f * swing, 0.05f, 2f);
    }
}
