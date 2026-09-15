using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// This machine's arcade on the <see cref="InputBus"/>: mounted under <see cref="InputKeys.ArcadeKey"/>
/// while the program (or the sandbox) shows it in a pattern, a layer, the inset or a wall tile,
/// and let go a few seconds after the last picture stopped wanting it, so a crossfade out of the
/// game fades real frames. The loop starts with the first want; the picture is copied out of the
/// loop's buffers only while a want stands (<see cref="ArcadeService.WantPicture"/>). The same
/// reconcile contract as the video, NDI, web and deck engines, with one mount to keep.
/// </summary>
public sealed class ArcadeInputEngine : IDisposable
{
    /// <summary>How long the last frames stay on the fade-out side after the last want went.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(4);

    private readonly ArcadeService _arcade;
    private bool _mounted;
    private DateTime? _retiredUtc;

    public ArcadeInputEngine(ArcadeService arcade)
    {
        _arcade = arcade;
    }

    /// <summary>Whether the game's picture is on the bus right now.</summary>
    public bool IsMounted => _mounted;

    /// <summary>Mounted keys with a short status — the Media tab's active-inputs line.</summary>
    public IReadOnlyList<(string Key, string Status)> MountStatuses
        => _mounted ? new[] { (InputKeys.ArcadeKey, _arcade.Source.StatusText) } : Array.Empty<(string, string)>();

    /// <summary>Also called from the app's 1 s poll, so a retired picture never lingers.</summary>
    public void SweepRetired() => SweepRetired(DateTime.UtcNow);

    /// <summary>The sweep at a given moment — the poll's now, or a test's later.</summary>
    public void SweepRetired(DateTime nowUtc)
    {
        if (_retiredUtc is not { } at || nowUtc - at <= Hold) return;
        _retiredUtc = null;
        InputBus.ClearPreviousIf(InputKeys.ArcadeKey, _arcade.Source);
        if (!_mounted) _arcade.WantPicture(false);
    }

    /// <summary>Reconciles the mount with the program (and sandbox) snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap, ShowSnapshot? sandbox = null)
    {
        SweepRetired();
        var wanted = Wants(snap) || (sandbox is not null && Wants(sandbox));
        if (wanted && !_mounted)
        {
            _mounted = true;
            _arcade.WantPicture(true);
            InputBus.Mount(InputKeys.ArcadeKey, _arcade.Source);
            if (_retiredUtc is not null)
            {
                // Wanted again within the hold: the fade-out entry is this same source; drop it.
                _retiredUtc = null;
                InputBus.ClearPreviousIf(InputKeys.ArcadeKey, _arcade.Source);
            }
        }
        else if (!wanted && _mounted)
        {
            _mounted = false;
            InputBus.Unmount(InputKeys.ArcadeKey);
            // Keep the frames coming briefly so a crossfade out of the game fades the game.
            InputBus.SetPrevious(InputKeys.ArcadeKey, _arcade.Source);
            _retiredUtc = DateTime.UtcNow;
        }
    }

    private static bool Wants(ShowSnapshot snap)
    {
        foreach (var w in MediaLocator.FindWantedInputs(snap))
        {
            if (w.Kind == MediaLocator.WantedKind.Arcade) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_mounted) InputBus.Unmount(InputKeys.ArcadeKey);
        if (_retiredUtc is not null) InputBus.ClearPreviousIf(InputKeys.ArcadeKey, _arcade.Source);
        _mounted = false;
        _retiredUtc = null;
        _arcade.WantPicture(false);
    }
}
