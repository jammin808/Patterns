using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Rendering;

/// <summary>
/// Skia's GPU resource cache governed at run time (round 69). Round 57 sized it once at start by the
/// machine's class; now the metrics tick asks for the limit the <see cref="GpuGovernor"/> gives for
/// this card and this rung, every sink's draw applies it through its Skia lease — the only place the
/// process reaches the GPU context — at most once a second, purges the unlocked resources when the
/// rung asks, and reads the cache's fill back for the Machine page and STATE. A software backend
/// (no context) says so rather than pretending.
/// </summary>
public static class GpuCacheGovernor
{
    private static long _wanted = -1;
    private static long _applied = -1;
    private static long _lastTicks;
    private static long _usedBytes;
    private static int _resources;
    private static int _purges;
    private static int _applications;
    private static volatile bool _purgeWanted;
    private static volatile bool _hasContext;

    /// <summary>How often a sink's draw does the governor's work: once a second is plenty for a limit and a reading.</summary>
    public const long CadenceMs = 1000;

    /// <summary>The policy's answer for now, set from the metrics tick: the limit for this card and rung, and whether the unlocked resources should go.</summary>
    public static void Want(long limitBytes, bool purge)
    {
        Interlocked.Exchange(ref _wanted, limitBytes);
        if (purge) _purgeWanted = true;
    }

    /// <summary>A sink's draw, with its lease's context (null under software rendering): the limit applied when it changed, the purge when asked, the usage read — once a second.</summary>
    public static void Apply(GRContext? context, long? nowTicks = null)
    {
        if (context is null)
        {
            _hasContext = false;
            return;
        }
        var now = nowTicks ?? Environment.TickCount64;
        var last = Interlocked.Read(ref _lastTicks);
        if (now - last < CadenceMs) return;
        if (Interlocked.CompareExchange(ref _lastTicks, now, last) != last) return;   // another sink's draw is doing it this second
        _hasContext = true;
        try
        {
            var wanted = Interlocked.Read(ref _wanted);
            if (wanted > 0 && wanted != Interlocked.Read(ref _applied))
            {
                context.SetResourceCacheLimit(wanted);
                Interlocked.Exchange(ref _applied, wanted);
                Interlocked.Increment(ref _applications);
            }
            if (_purgeWanted)
            {
                _purgeWanted = false;
                context.PurgeUnlockedResources(scratchResourcesOnly: false);
                Interlocked.Increment(ref _purges);
            }
            context.GetResourceCacheUsage(out var count, out var bytes);
            Interlocked.Exchange(ref _resources, count);
            Interlocked.Exchange(ref _usedBytes, bytes);
        }
        catch (Exception ex)
        {
            Log.Warn("GPU cache governor could not reach the context.", ex);
        }
    }

    /// <summary>The governor's facts: whether a GPU context was seen, the limit applied (or wanted, before any draw), the fill, the purges and the applications.</summary>
    public static (bool HasContext, long LimitBytes, long UsedBytes, int Resources, int Purges, int Applications) Facts
    {
        get
        {
            var applied = Interlocked.Read(ref _applied);
            var limit = applied > 0 ? applied : Interlocked.Read(ref _wanted);
            return (_hasContext, limit, Interlocked.Read(ref _usedBytes), _resources, _purges, _applications);
        }
    }

    /// <summary>"GPU cache 48 MB of 128 MB (212 resources) · purged 3×" / "GPU cache: no GPU context (software rendering)".</summary>
    public static string Words
    {
        get
        {
            var f = Facts;
            return GpuGovernor.Words(f.HasContext, f.LimitBytes, f.UsedBytes, f.Resources, f.Purges);
        }
    }

    /// <summary>Tests: back to nothing seen and nothing wanted.</summary>
    public static void ResetForTests()
    {
        _wanted = -1;
        _applied = -1;
        _lastTicks = 0;
        _usedBytes = 0;
        _resources = 0;
        _purges = 0;
        _applications = 0;
        _purgeWanted = false;
        _hasContext = false;
    }
}
