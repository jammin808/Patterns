using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>What the desk's own speakers can be listening to.</summary>
    public EnumItem[] MonitorSources => Lists.MonitorSources;

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
        if (!ReferenceEquals(_hookedMonitor, State.Monitor))
        {
            if (_hookedMonitor is not null) _hookedMonitor.PropertyChanged -= OnMonitorChanged;
            _hookedMonitor = State.Monitor;
            _hookedMonitor.PropertyChanged += OnMonitorChanged;
        }
        RaiseMonitor();
    }

    private void OnMonitorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RaiseMonitor();

    private void RaiseMonitor()
    {
        Raise(nameof(MonitorIsOutput));
        Raise(nameof(MonitorWords));
        Raise(nameof(MonitorWord));
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
        if (MonitorOutputs.Count == wanted.Count && MonitorOutputs.SequenceEqual(wanted)) return;
        MonitorOutputs.Clear();
        foreach (var t in wanted) MonitorOutputs.Add(t);
    }
}
