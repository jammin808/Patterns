using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Keeps one NDI® receiver matched to the active media config (mirror of VideoEngine's
/// reconcile contract) and owns the network source finder for the UI's pick list.
/// </summary>
public sealed class NdiInputEngine : IDisposable
{
    private readonly NdiFinder _finder = new();
    private NdiReceiver? _receiver;
    private string _activeSource = "";
    private (NdiReceiver Receiver, DateTime RetiredUtc)? _retired;

    /// <summary>Also called from the app's 1 s poll so a retired receiver never lingers.</summary>
    public void SweepRetired()
    {
        if (_retired is { } sweep && DateTime.UtcNow - sweep.RetiredUtc > TimeSpan.FromSeconds(4))
        {
            NdiInput.Previous = null;
            sweep.Receiver.Dispose();
            _retired = null;
        }
    }

    /// <summary>Reconciles the running receiver with the current snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap)
    {
        SweepRetired();

        var wanted = MediaLocator.FindActiveNdiSource(snap.State);
        if (wanted == _activeSource) return;
        _activeSource = wanted;

        NdiInput.Current = null;
        if (_retired is { } old)
        {
            old.Receiver.Dispose();
            _retired = null;
        }
        NdiInput.Previous = null;
        if (_receiver is not null)
        {
            // Keep receiving briefly so a crossfade fades out live frames.
            NdiInput.Previous = _receiver;
            _retired = (_receiver, DateTime.UtcNow);
            _receiver = null;
        }

        if (string.IsNullOrWhiteSpace(wanted)) return;

        NdiInterop.ReprobeIfUnavailable();
        if (!NdiInterop.Available)
        {
            NdiInput.AvailabilityNote =
                "NDI runtime not found — install it from ndi.video, or drop Processing.NDI.Lib.x64.dll beside Patterns.exe.";
            return;
        }

        NdiInput.AvailabilityNote = "";
        _receiver = new NdiReceiver(wanted);
        NdiInput.Current = _receiver;
    }

    /// <summary>Sources currently visible on the network (empty when NDI is unavailable).</summary>
    public IReadOnlyList<string> DiscoverSources()
    {
        NdiInterop.ReprobeIfUnavailable();
        return _finder.CurrentSources();
    }

    public void Dispose()
    {
        NdiInput.Current = null;
        NdiInput.Previous = null;
        _receiver?.Dispose();
        _receiver = null;
        if (_retired is { } r)
        {
            r.Receiver.Dispose();
            _retired = null;
        }
        _finder.Dispose();
    }
}
