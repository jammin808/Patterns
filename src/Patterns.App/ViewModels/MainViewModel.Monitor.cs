using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>What the desk's own speakers can be listening to.</summary>
    public EnumItem[] MonitorSources => Lists.MonitorSources;

    /// <summary>The output devices the monitor can use — none, then every render endpoint Windows has.</summary>
    public ObservableCollection<string> MonitorDevices { get; } = new();

    /// <summary>"'Scarlett 2i2' is not plugged in…" — empty while every named output is present.</summary>
    public string AudioDeviceTrouble => AudioPlayerService.MissingDeviceWords();

    /// <summary>Every output the desk can listen to on its own — the rig in wall order.</summary>
    public ObservableCollection<EditTarget> MonitorOutputs { get; } = new();

    public bool MonitorIsOutput => State.Monitor.Source == AudioMonitor.Output;

    /// <summary>What it is listening to, in a line — and, when a screen simply follows the show, that it does.</summary>
    public string MonitorWords => AudioMonitorRule.Words(State);

    /// <summary>"PGM", "PVW", "SCREEN 2", "SILENT" — the short word for the status line and the chip.</summary>
    public string MonitorWord => State.Monitor.Source switch
    {
        AudioMonitor.Silent => "SILENT",
        AudioMonitor.Preview => "PVW",
        AudioMonitor.Output => State.Monitor.OutputId.Length == 0
            ? "PGM"
            : AudioMonitorRule.LabelFor(State, State.Monitor.OutputId).ToUpperInvariant(),
        _ => "PGM",
    };

    private MonitorConfig? _hookedMonitor;

    /// <summary>
    /// The monitor row follows the choice on the click, not on the next poll: picking an output has
    /// to show its picker at once. Re-hooked when a show is loaded, because the model under the
    /// desk is a different object then.
    /// </summary>
    public void HookMonitor()
    {
        RefreshMonitorDevices();
        if (!ReferenceEquals(_hookedMonitor, State.Monitor))
        {
            if (_hookedMonitor is not null) _hookedMonitor.PropertyChanged -= OnMonitorChanged;
            _hookedMonitor = State.Monitor;
            _hookedMonitor.PropertyChanged += OnMonitorChanged;
        }
        RaiseMonitor();
    }

    private void OnMonitorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A device named by a show that this machine has not got must stay in the list, or the
        // picker would resolve it to nothing and write that emptiness straight back into the show.
        if (e.PropertyName == nameof(MonitorConfig.Device)) RefreshMonitorDevices();
        RaiseMonitor();
    }

    private void RaiseMonitor()
    {
        Raise(nameof(MonitorIsOutput));
        Raise(nameof(MonitorWords));
        Raise(nameof(MonitorWord));
        Raise(nameof(AudioDeviceTrouble));
    }

    private bool _listingDevices;

    /// <summary>
    /// The device list follows what Windows has, with "none" first — the desk's default.
    ///
    /// Reconciled in place and never cleared. The picker is bound both ways, so a list that is
    /// empty for even an instant loses its selection and writes that emptiness straight back into
    /// the show: choose a headphone output and it would read back as "none" the moment the list
    /// was rebuilt underneath it. Nothing already chosen is ever removed, so the selection holds.
    /// </summary>
    public void RefreshMonitorDevices()
    {
        if (_listingDevices) return;                       // the picker's write-back must not re-enter
        _listingDevices = true;
        try
        {
            var wanted = new List<string> { "" };
            wanted.AddRange(AudioPlayerService.OutputDevices());
            // A device the show names but the machine has not got stays in the list, so the choice
            // reads back as what was chosen rather than silently becoming "none".
            if (State.Monitor.Device.Length > 0 && !wanted.Contains(State.Monitor.Device)) wanted.Add(State.Monitor.Device);
            if (MonitorDevices.SequenceEqual(wanted)) return;
            for (var i = MonitorDevices.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(MonitorDevices[i])) MonitorDevices.RemoveAt(i);
            }
            for (var i = 0; i < wanted.Count; i++)
            {
                if (i >= MonitorDevices.Count) MonitorDevices.Add(wanted[i]);
                else if (!string.Equals(MonitorDevices[i], wanted[i], StringComparison.Ordinal)) MonitorDevices.Insert(i, wanted[i]);
            }
            while (MonitorDevices.Count > wanted.Count) MonitorDevices.RemoveAt(MonitorDevices.Count - 1);
        }
        finally
        {
            _listingDevices = false;
        }
    }

    /// <summary>The outputs the monitor picker offers: joined canvases, then every screen, in wall order.</summary>
    public void RefreshMonitorOutputs()
    {
        var geo = Rig.Geometry(State, _services.Screens.All);
        var wanted = new List<EditTarget>();
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        foreach (var s in geo.Screens) wanted.Add(new EditTarget(geo.LabelFor(State, s.Id), s.Id));
        // In place, like the device list and for the same reason: this picker is bound both ways,
        // so emptying it for an instant while the rig is reconciled would write "no output" back
        // into the show and drop the desk's monitor to the programme mid-check.
        if (MonitorOutputs.SequenceEqual(wanted)) return;
        for (var i = MonitorOutputs.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(MonitorOutputs[i])) MonitorOutputs.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= MonitorOutputs.Count) MonitorOutputs.Add(wanted[i]);
            else if (!MonitorOutputs[i].Equals(wanted[i])) MonitorOutputs.Insert(i, wanted[i]);
        }
        while (MonitorOutputs.Count > wanted.Count) MonitorOutputs.RemoveAt(MonitorOutputs.Count - 1);
    }
}
