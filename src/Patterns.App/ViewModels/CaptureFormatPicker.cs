using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;

namespace Patterns.App.ViewModels;

/// <summary>
/// The "Format" picker beside a capture device: the modes the card's driver advertises, plus
/// "Device default", with the choice stored per device on the show (<see cref="ShowState.CaptureFormats"/>)
/// so the same card opens the same way wherever it is used. A stored mode the card no longer
/// lists stays offered, so a show built on the desk survives the card being unplugged.
/// </summary>
public sealed class CaptureFormatPicker : Observable
{
    public const string DefaultLabel = "Device default";

    private readonly Func<ShowState> _state;
    private readonly Func<string> _device;
    private readonly Action _changed;
    private readonly List<(string Label, string Key)> _entries = new();
    private string _selected = DefaultLabel;
    private string _forDevice = "";
    private bool _lowLatency;

    /// <summary>The probe behind the list — the DirectShow query by default, a fake in tests.</summary>
    public Func<string, IReadOnlyList<CaptureFormat>> Probe { get; set; } = CaptureDevices.FormatsFor;

    public CaptureFormatPicker(Func<ShowState> state, Func<string> device, Action changed)
    {
        _state = state;
        _device = device;
        _changed = changed;
    }

    public ObservableCollection<string> Options { get; } = new() { DefaultLabel };

    /// <summary>The label picked; setting it stores the mode for the device and asks the decoder to reopen.</summary>
    public string Selected
    {
        get => _selected;
        set
        {
            var label = value ?? DefaultLabel;
            if (!Set(ref _selected, label)) return;
            var device = _device();
            if (device.Length == 0) return;
            var key = _entries.FirstOrDefault(e => e.Label == label).Key ?? "";
            if (_state().CaptureFormatFor(device) == key) return;
            _state().SetCaptureFormat(device, key);
            _changed();
        }
    }

    /// <summary>
    /// Low latency (IMAG): the device opens with no input buffer and no clock smoothing, so a
    /// frame reaches the screen as soon as it is decoded and a hitch drops one rather than shows
    /// it late. Stored per device on the show beside the mode; setting it reopens the decoder.
    /// </summary>
    public bool LowLatency
    {
        get => _lowLatency;
        set
        {
            if (!Set(ref _lowLatency, value)) return;
            var device = _device();
            if (device.Length == 0) return;
            if (_state().CaptureLowLatencyFor(device) == value) return;
            _state().SetCaptureLowLatency(device, value);
            _changed();
        }
    }

    /// <summary>Re-lists the device's modes and re-reads the stored choice. Cheap when the device is unchanged.</summary>
    public void Refresh(bool force = false)
    {
        var device = _device();
        if (!force && device == _forDevice) return;
        _forDevice = device;
        _entries.Clear();
        _entries.Add((DefaultLabel, ""));
        if (device.Length > 0)
        {
            foreach (var f in Probe(device))
            {
                if (_entries.All(e => e.Key != f.Key)) _entries.Add((f.Label, f.Key));
            }
            var stored = _state().CaptureFormatFor(device);
            if (stored.Length > 0 && _entries.All(e => e.Key != stored) && CaptureFormat.TryParse(stored, out var kept))
            {
                _entries.Insert(1, (kept.Label + " (saved)", stored));
            }
        }
        Options.Clear();
        foreach (var e in _entries) Options.Add(e.Label);
        var current = _state().CaptureFormatFor(device);
        _selected = _entries.FirstOrDefault(e => e.Key == current).Label ?? DefaultLabel;
        Raise(nameof(Selected));
        _lowLatency = device.Length > 0 && _state().CaptureLowLatencyFor(device);
        Raise(nameof(LowLatency));
    }
}
