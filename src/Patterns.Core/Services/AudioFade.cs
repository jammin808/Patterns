namespace Patterns.Core.Services;

/// <summary>
/// The stop fade: how a sound or a clip that is told to stop leaves the air. Pure and time-based
/// like <see cref="MusicLevel"/>, so a missed poll never stalls it, and one rule for the WASAPI
/// voices and the libVLC clips alike. Also the answer to "how long may a retired decoder live":
/// long enough for the longest fade still in flight, plus a margin, and not the flat four seconds
/// it used to get — a retired clip that lingers is a clip that can be heard again.
/// </summary>
public static class AudioFade
{
    /// <summary>Added to the longest fade so a sink that renders late still finds the retired frames.</summary>
    public const int RetireMarginMs = 300;

    /// <summary>1 at the start of the fade, 0 at its end, clamped. A zero-length fade is silence at once.</summary>
    public static double GainAt(DateTime startUtc, DateTime nowUtc, int ms)
        => 1 - MusicLevel.Progress(startUtc, nowUtc, ms);

    /// <summary>The fade has reached silence.</summary>
    public static bool Done(DateTime startUtc, DateTime nowUtc, int ms)
        => MusicLevel.Progress(startUtc, nowUtc, ms) >= 1;

    /// <summary>
    /// How long a retired source is kept decoding: the longer of the picture's crossfade and the
    /// sound's stop fade, plus the margin. Never negative inputs; never less than the margin.
    /// </summary>
    public static int RetireHoldMs(int transitionMs, int stopFadeMs)
        => Math.Max(Math.Max(transitionMs, 0), Math.Max(stopFadeMs, 0)) + RetireMarginMs;
}
