namespace Patterns.Platform.Windows;

/// <summary>
/// Round 72: the display evidence the platform keeps between asks — the observation of every path
/// (kept two seconds), the EDID of every display (kept by device path), the machine's inventory (kept
/// thirty seconds) — is one set, and a change of the display topology invalidates it as one: the next
/// ask is the new rig's reading, never a stale cache's. The desk calls this where it learns of the
/// change (the window's screens changed); a test calls it to force a fresh read.
/// </summary>
public static class DisplayEvidence
{
    private static long _invalidations;

    /// <summary>How many times the topology's evidence was dropped — the Machine page and a test read it.</summary>
    public static long Invalidations => Interlocked.Read(ref _invalidations);

    /// <summary>Drops every kept display reading at once.</summary>
    public static void InvalidateTopology()
    {
        DisplayObservation.Forget();
        EdidReader.Forget();
        MachineProbe.Forget();
        Interlocked.Increment(ref _invalidations);
    }
}
