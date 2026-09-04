namespace Patterns.Core.Services;

/// <summary>The four things that carry sound during a show, each with a gain of its own.</summary>
public enum AudioBus
{
    /// <summary>Background music: the file track and break music together.</summary>
    Music,
    /// <summary>A transition stinger's sound (an audio file fired as a sting).</summary>
    StingSound,
    /// <summary>A VOG announcement's sound.</summary>
    VogSound,
    /// <summary>The soundtrack of a clip on the screens — a VOG clip, a sting clip, any playing video.</summary>
    ClipAudio,
}

/// <summary>What the gain rules look at right now.</summary>
/// <param name="VogSoundPlaying">A VOG sound is on air and has not been told to leave.</param>
/// <param name="DuckPct">The show's duck level — the share of its own volume the ducked sound keeps.</param>
/// <param name="StingRamp">The sting fade on the music (0–1), a pure ramp owned by the stinger service.</param>
/// <param name="LiveDuck">The operator's live duck as a factor (1 = off; a ramp while it moves): an announcement from the room.</param>
public readonly record struct GainInputs(bool VogSoundPlaying, double DuckPct, double StingRamp, double LiveDuck = 1.0);

/// <summary>
/// Who ducks whom, in one table with one test. A VOG sound is an announcement: everything else
/// steps down to the duck level underneath it and comes back the moment it ends — the music, a
/// stinger sound that keeps playing, the soundtrack of a clip that keeps its screens. A sting
/// fades the music (the ramp) and never touches a sound of its own. The live duck — the
/// operator making room for a microphone at the desk — is a factor on the same three buses.
/// Nothing ducks a VOG: it is itself an announcement.
/// </summary>
public static class GainRules
{
    public static double For(AudioBus bus, in GainInputs g)
    {
        var duck = g.VogSoundPlaying ? MusicLevel.Duck(g.DuckPct) : 1.0;
        var live = Math.Clamp(g.LiveDuck, 0, 1);
        return bus switch
        {
            AudioBus.Music => Math.Min(duck, Math.Clamp(g.StingRamp, 0, 1)) * live,
            AudioBus.StingSound => duck * live,
            AudioBus.ClipAudio => duck * live,
            _ => 1.0,
        };
    }
}
