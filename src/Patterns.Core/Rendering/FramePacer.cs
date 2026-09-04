namespace Patterns.Core.Rendering;

/// <summary>
/// Turns a display's own refresh into the show's frame rate. A sink that redraws on every
/// vsync asks, each time, whether the show clock has entered a new frame slot at the target
/// rate; it presents only when it has, so 30 on a 60 Hz display presents every other vsync and
/// 25 on 60 presents 5 of every 12 — evenly, never in bursts — and the same slot arithmetic on
/// the same clock keeps every output and sender on the same frame. Pure; unit tested.
/// </summary>
public static class FramePacer
{
    /// <summary>The frame slot the show clock is in at the target rate.</summary>
    public static long SlotOf(double clock, int targetFps) => (long)Math.Floor(clock * targetFps);

    /// <summary>
    /// True when this vsync should present: the target is unlimited, or the clock has entered a
    /// slot the sink has not presented yet. <paramref name="lastSlot"/> is the sink's own memory.
    /// </summary>
    public static bool ShouldPresent(double clock, int targetFps, ref long lastSlot)
    {
        if (targetFps <= 0) return true;
        var slot = SlotOf(clock, targetFps);
        if (slot == lastSlot) return false;
        lastSlot = slot;
        return true;
    }
}
