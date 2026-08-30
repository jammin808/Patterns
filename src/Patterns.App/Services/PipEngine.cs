using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Keeps the picture-in-picture live source running: a second NDI receiver or a second
/// (always muted) capture decoder, independent of the main media — so a camera can sit in
/// the corner of a schedule, or a schedule feed in the corner of a camera.
/// </summary>
public sealed class PipEngine : IDisposable
{
    private readonly VideoEngine _video;
    private NdiReceiver? _ndi;
    private VlcFrameSource? _vlc;
    private string _activeKey = "";

    public PipEngine(VideoEngine video)
    {
        _video = video;
    }

    /// <summary>Reconciles the PiP source with the current snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap)
    {
        var pip = snap.State.Overlays.Pip;
        var target = pip.Source == PipSource.NdiFeed ? pip.NdiSourceName : pip.CaptureDevice;
        var key = pip.Enabled && !string.IsNullOrWhiteSpace(target) ? $"{pip.Source}|{target}" : "";
        if (key == _activeKey) return;
        _activeKey = key;

        PipInput.Current = null;
        _ndi?.Dispose();
        _ndi = null;
        _vlc?.Dispose();
        _vlc = null;

        if (key.Length == 0) return;

        try
        {
            if (pip.Source == PipSource.NdiFeed)
            {
                NdiInterop.ReprobeIfUnavailable();
                if (!NdiInterop.Available) return;
                _ndi = new NdiReceiver(target);
                PipInput.Current = _ndi;
            }
            else
            {
                var vlc = _video.SharedVlc;
                if (vlc is null) return;
                _vlc = new VlcFrameSource(vlc, target, loop: false, isCapture: true, mute: true, volumePct: 0);
                PipInput.Current = _vlc;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"PiP source open failed for '{target}'.", ex);
            _activeKey = ""; // retry on the next change
        }
    }

    public void Dispose()
    {
        PipInput.Current = null;
        _ndi?.Dispose();
        _vlc?.Dispose();
        _ndi = null;
        _vlc = null;
    }
}
