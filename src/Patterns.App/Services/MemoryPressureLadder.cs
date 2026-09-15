using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The memory pressure ladder: once a second the media memory is read against its budget
/// (<see cref="MediaMemory"/>), the rung is taken from the ratio, and the steps for that rung are
/// applied — the same steps every time at that rung, so the response is deterministic and the
/// words say what was done. Elevated: the retired swept and the pictures trimmed to half their
/// budget (never the one last drawn). High: the standby cue's pre-roll held back and the decks'
/// page window narrowed as well. Critical: a source the preview alone wants is not opened either.
/// Stepping down restores each thing. The source on air, the program frame, the cue state, the
/// geometry and the route truth are never touched, at any rung.
/// </summary>
public sealed class MemoryPressureLadder
{
    private readonly AppServices _services;

    public MemoryPressureLadder(AppServices services)
    {
        _services = services;
    }

    /// <summary>The rung the ladder stands on.</summary>
    public MemoryPressure Level { get; private set; }

    /// <summary>The last reading taken.</summary>
    public MediaMemory.Reading? Reading { get; private set; }

    /// <summary>Times the rung changed this session.</summary>
    public int Transitions { get; private set; }

    /// <summary>What the last change did: "high pressure at 91 %: retired swept, pictures trimmed (3 let go), pre-roll held back, decks narrowed".</summary>
    public string LastChange { get; private set; } = "";

    /// <summary>Pictures the trim let go this session.</summary>
    public int PicturesTrimmed { get; private set; }

    /// <summary>When the share first sat below the current rung's leaving line (-1 while it has not): the ladder's dwell (round 64).</summary>
    private double _belowSince = -1;

    /// <summary>The clock the dwell is measured on: the show clock, or a test's.</summary>
    public Func<double> Clock { get; set; } = static () => ShowClock.Seconds;

    /// <summary>One reading applied: the rung and its steps, every poll, idempotent at the same rung.</summary>
    public void Apply(MediaMemory.Reading reading)
    {
        Reading = reading;
        // Up at once; down only after the share has sat below the rung's leaving line for the
        // dwell, and one rung at a time — a reading on a line no longer suppresses and restores
        // the pre-roll every poll.
        var level = MediaMemory.Step(Level, reading.Total, reading.Budget, Clock(), ref _belowSince);
        var trimmed = 0;
        if (level >= MemoryPressure.Elevated)
        {
            RetiredFrames.Sweep();
            trimmed = ImageCache.TrimTo(ImageCache.BudgetBytes / 2);
            PicturesTrimmed += trimmed;
        }
        _services.Video.PreRollSuppressed = level >= MemoryPressure.High;
        PdfDeckSource.Window = level >= MemoryPressure.High ? PdfDeckSource.NarrowWindow : PdfDeckSource.DefaultWindow;
        _services.Video.RefuseNonCriticalOpens = level >= MemoryPressure.Critical;

        if (level == Level) return;
        var was = Level;
        Level = level;
        Transitions++;
        LastChange = level == MemoryPressure.None
            ? $"pressure eased from {MediaMemory.Word(was)} at {reading.Share * 100:0} %: everything restored"
            : $"{MediaMemory.Word(level)} pressure at {reading.Share * 100:0} %: {MediaMemory.Steps(level)}{(trimmed > 0 ? $" ({trimmed} picture{(trimmed == 1 ? "" : "s")} let go)" : "")}";
        if (level >= MemoryPressure.High) Log.Warn($"Memory {LastChange} — {reading.Parts}.");
        else Log.Info($"Memory {LastChange}.");
    }
}
