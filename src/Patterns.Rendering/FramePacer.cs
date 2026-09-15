namespace Patterns.Rendering;

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
    /// The pacer with an epoch (round 64): a continuous run of vsyncs at one target rate. A slot
    /// is missed only inside an epoch — a frame that should have been presented during this run
    /// and was not. The first vsync of an epoch presents and misses nothing: a sink that sat
    /// static for a minute and animates again, a target that changed from 60 to 50, a display
    /// that changed, an unpaced sink that became paced, a canvas put back in the tree — none of
    /// them invent the thousand slots that went by while nothing was owed.
    /// </summary>
    public static bool ShouldPresent(double clock, int targetFps, FramePacerState state, out int missed)
    {
        missed = 0;
        if (targetFps <= 0)
        {
            state.Leave();                                                  // unpaced: every beat, and no epoch to count against
            return true;
        }
        var slot = SlotOf(clock, targetFps);
        if (!state.Active || state.TargetFps != targetFps)
        {
            state.Active = true;                                            // a new epoch: this vsync presents, the count starts here
            state.TargetFps = targetFps;
            state.LastSlot = slot;
            state.Epochs++;
            return true;
        }
        if (slot == state.LastSlot) return false;
        if (slot > state.LastSlot + 1) missed = (int)Math.Min(int.MaxValue, slot - state.LastSlot - 1);
        state.LastSlot = slot;
        return true;
    }

    /// <summary>
    /// True when this vsync should present: the target is unlimited, or the clock has entered a
    /// slot the sink has not presented yet. <paramref name="lastSlot"/> is the sink's own memory.
    /// </summary>
    public static bool ShouldPresent(double clock, int targetFps, ref long lastSlot) => ShouldPresent(clock, targetFps, ref lastSlot, out _);

    /// <summary>
    /// As above, and how many slots went by unpresented since the last present: a frame in the same
    /// slot as the last is a wait, not a drop; a frame two or more slots on is a drop of the slots
    /// between — the frames the room did not get, which is what a presentation-drop counter counts.
    /// </summary>
    public static bool ShouldPresent(double clock, int targetFps, ref long lastSlot, out int missed)
    {
        missed = 0;
        if (targetFps <= 0) return true;
        var slot = SlotOf(clock, targetFps);
        if (slot == lastSlot) return false;
        if (lastSlot >= 0 && slot > lastSlot + 1) missed = (int)Math.Min(int.MaxValue, slot - lastSlot - 1);
        lastSlot = slot;
        return true;
    }
}

/// <summary>
/// One sink's pacing epoch: whether it is in a continuous run, at what target, the last slot it
/// presented in that run, and how many epochs it has begun (tests read it). <see cref="Leave"/>
/// ends the run — leaving Continuous cadence, a canvas leaving the tree, a pipeline replaced —
/// so the next vsync starts afresh.
/// </summary>
public sealed class FramePacerState
{
    public bool Active { get; internal set; }
    public int TargetFps { get; internal set; }
    public long LastSlot { get; internal set; } = -1;
    public int Epochs { get; internal set; }

    /// <summary>The run ends: whatever slots go by until the next present are nobody's.</summary>
    public void Leave()
    {
        Active = false;
        TargetFps = 0;
        LastSlot = -1;
    }
}
