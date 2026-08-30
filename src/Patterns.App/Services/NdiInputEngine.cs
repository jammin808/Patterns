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

    /// <summary>Reconciles the running receiver with the current snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap)
    {
        var wanted = MediaLocator.FindActiveNdiSource(snap.State);
        if (wanted == _activeSource) return;
        _activeSource = wanted;

        NdiInput.Current = null;
        _receiver?.Dispose();
        _receiver = null;

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
        _receiver?.Dispose();
        _receiver = null;
        _finder.Dispose();
    }
}
