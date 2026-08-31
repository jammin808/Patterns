using Patterns.Core.Media;
using Patterns.Core.Ndi;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Hosts one NDI® receiver per referenced source, published on the <see cref="InputBus"/>
/// (mirror of <see cref="VideoEngine"/>'s reconcile contract), and owns the network source
/// finder for the UI's pick list. Different screens and multiview tiles receive different
/// feeds at once; the same feed everywhere still costs one receiver.
/// </summary>
public sealed class NdiInputEngine : IDisposable
{
    /// <summary>Simultaneous receivers — each is a network stream plus a decode.</summary>
    public const int MaxReceivers = 6;

    private readonly NdiFinder _finder = new();
    private readonly Dictionary<string, NdiReceiver> _receivers = new();
    private readonly List<(string Key, NdiReceiver Receiver, DateTime RetiredUtc)> _retired = new();

    /// <summary>Non-empty when more feeds are wanted than the receiver cap allows.</summary>
    public string LimitNote { get; private set; } = "";

    /// <summary>Mounted keys with a short status each — the Media tab's active-inputs line.</summary>
    public IReadOnlyList<(string Key, string Status)> MountStatuses
        => _receivers.Select(kv => (kv.Key, kv.Value.IsPlaying ? "receiving" : kv.Value.StatusText)).ToList();

    /// <summary>Also called from the app's 1 s poll so a retired receiver never lingers.</summary>
    public void SweepRetired()
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (DateTime.UtcNow - _retired[i].RetiredUtc <= TimeSpan.FromSeconds(4)) continue;
            InputBus.SetPrevious(_retired[i].Key, null);
            _retired[i].Receiver.Dispose();
            _retired.RemoveAt(i);
        }
    }

    /// <summary>Reconciles the receiver pool with the program (and sandbox) snapshot (UI thread).</summary>
    public void Reconcile(ShowSnapshot snap, ShowSnapshot? sandbox = null)
    {
        SweepRetired();

        var wanted = new List<MediaLocator.WantedInput>();
        var seen = new HashSet<string>();
        foreach (var w in MediaLocator.FindWantedInputs(snap))
        {
            if (w.Kind == MediaLocator.WantedKind.Ndi && seen.Add(w.Key)) wanted.Add(w);
        }
        if (sandbox is not null)
        {
            foreach (var w in MediaLocator.FindWantedInputs(sandbox))
            {
                if (w.Kind == MediaLocator.WantedKind.Ndi && seen.Add(w.Key)) wanted.Add(w);
            }
        }

        foreach (var key in _receivers.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var receiver = _receivers[key];
            _receivers.Remove(key);
            InputBus.Unmount(key);
            // Keep receiving briefly so a crossfade fades out live frames.
            InputBus.SetPrevious(key, receiver);
            _retired.Add((key, receiver, DateTime.UtcNow));
        }

        if (wanted.Count == 0)
        {
            LimitNote = "";
            return;
        }

        NdiInterop.ReprobeIfUnavailable();
        if (!NdiInterop.Available)
        {
            NdiInput.AvailabilityNote =
                "NDI runtime not found — install it from ndi.video, or drop Processing.NDI.Lib.x64.dll beside Patterns.exe.";
            return;
        }
        NdiInput.AvailabilityNote = "";

        var over = 0;
        foreach (var w in wanted)
        {
            if (_receivers.ContainsKey(w.Key)) continue;
            if (_receivers.Count >= MaxReceivers)
            {
                over++;
                continue;
            }
            try
            {
                var receiver = new NdiReceiver(w.Target);
                _receivers[w.Key] = receiver;
                InputBus.Mount(w.Key, receiver);
            }
            catch (Exception ex)
            {
                Log.Error($"NDI receive failed for '{w.Target}'.", ex);
            }
        }
        LimitNote = over > 0
            ? $"Input limit: {MaxReceivers} simultaneous NDI receivers — {over} feed{(over == 1 ? "" : "s")} waiting."
            : "";
    }

    /// <summary>Sources currently visible on the network (empty when NDI is unavailable).</summary>
    public IReadOnlyList<string> DiscoverSources()
    {
        NdiInterop.ReprobeIfUnavailable();
        return _finder.CurrentSources();
    }

    public void Dispose()
    {
        foreach (var (key, receiver) in _receivers)
        {
            InputBus.Unmount(key);
            receiver.Dispose();
        }
        _receivers.Clear();
        foreach (var (key, receiver, _) in _retired)
        {
            InputBus.SetPrevious(key, null);
            receiver.Dispose();
        }
        _retired.Clear();
        _finder.Dispose();
    }
}
