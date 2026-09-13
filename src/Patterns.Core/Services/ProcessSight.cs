namespace Patterns.Core.Services;

/// <summary>
/// What one look at a process id found. Three answers, not two: no such process; a process that
/// is up but that this one may not read (another user's, an elevated one, a protected one — its
/// start time is refused, and usually its exe); or a process read whole, with its start time and
/// its exe. Every fence reads the middle answer as a fence, never as an absence: a process that
/// cannot be read may very well still have the screens, and "I could not see it" is not "it is
/// gone". Pure, so the rules are unit tested without a process tree.
/// </summary>
/// <param name="Exists">A process with that id is up.</param>
/// <param name="Readable">Its start time could be read — the one thing that tells the process that wrote a record from another that was handed its id.</param>
/// <param name="StartTicks">Its start time, UTC ticks, when readable.</param>
/// <param name="ExePath">Its executable, "" when that could not be read.</param>
public sealed record ProcessSight(bool Exists, bool Readable, long? StartTicks, string ExePath)
{
    /// <summary>No process with that id.</summary>
    public static readonly ProcessSight Gone = new(false, false, null, "");

    /// <summary>Up, and not this process's to read.</summary>
    public static ProcessSight Unreadable(string exePath = "") => new(true, false, null, exePath);

    /// <summary>Up and read: its start time, and its exe when that could be read too.</summary>
    public static ProcessSight Alive(long startTicks, string exePath = "") => new(true, true, startTicks, exePath);

    /// <summary>The very process that wrote a record: up, read, and started when the record says.</summary>
    public bool IsTheOne(long startedAtUtcTicks) => Exists && Readable && StartTicks == startedAtUtcTicks;

    /// <summary>Nothing of it holds anything: gone, or its id handed out again to a process that started at another time.</summary>
    public bool IsGoneOrReused(long startedAtUtcTicks) => !Exists || (Readable && StartTicks != startedAtUtcTicks);

    /// <summary>Up, and this process cannot say whether it is the one: the fence case.</summary>
    public bool IsUnreadable => Exists && !Readable;

    /// <summary>A look built from the old two-answer read, where a start time is a readable process and none is gone.</summary>
    public static Func<int, ProcessSight> From(Func<int, long?> startTicksOf)
        => pid => startTicksOf(pid) is { } started ? Alive(started) : Gone;
}
