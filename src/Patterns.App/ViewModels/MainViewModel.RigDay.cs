using Patterns.App.Services;
using Patterns.Core.Model;

namespace Patterns.App.ViewModels;

/// <summary>Rig day's games on the desk: the switch on the Machine page, the alignment game and the quest on the Screens page, the streak on the Run surface.</summary>
public sealed partial class MainViewModel
{
    private string _rigDaySeen = "";

    public bool IsRigDayOn
    {
        get => _services.RigDay.Enabled;
        set
        {
            if (value == _services.RigDay.Enabled) return;
            StatusMessage = _services.Actions.Execute(new ShowAction(value ? ShowActionKind.RigDayOn : ShowActionKind.RigDayOff), ActionOrigin.Desk).Message;
            PollRigDay(force: true);
        }
    }

    public string RigDayWords => _services.RigDay.ReadyWords.Length == 0 ? "" : $"{_services.RigDay.ReadyWords} — {_services.RigDay.ReadyNext}";

    public string QuestWords => _services.RigDay.QuestWords;

    public string AlignWords => _services.RigDay.AlignWords;

    public bool IsAligning => _services.RigDay.Align is not null;

    public string StreakWords => _services.RigDay.StreakWords;

    public bool HasStreak => StreakWords.Length > 0;

    /// <summary>The Screens page is the one showing — the alignment game's keys work only then.</summary>
    public bool IsScreensPage => SelectedPageIndex == Shell.IndexOf("Screens");

    private void PollRigDay(bool force = false)
    {
        _services.RigDay.Poll();
        var now = $"{IsRigDayOn}|{RigDayWords}|{QuestWords}|{AlignWords}|{StreakWords}";
        if (!force && now == _rigDaySeen) return;
        _rigDaySeen = now;
        Raise(nameof(IsRigDayOn));
        Raise(nameof(RigDayWords));
        Raise(nameof(QuestWords));
        Raise(nameof(AlignWords));
        Raise(nameof(IsAligning));
        Raise(nameof(StreakWords));
        Raise(nameof(HasStreak));
    }

    private RelayCommand? _alignStart;

    /// <summary>ALIGNMENT GAME on the Screens page: the selected projector, else the first with a calibration.</summary>
    public RelayCommand AlignStartCommand => _alignStart ??= new RelayCommand(() =>
    {
        var screen = Screens.SelectedPlacement?.ScreenId ?? "";
        StatusMessage = _services.Actions.Execute(new ShowAction(ShowActionKind.AlignStart, "", screen), ActionOrigin.Desk).Message;
        PollRigDay(force: true);
    });

    private RelayCommand? _alignStop;

    public RelayCommand AlignStopCommand => _alignStop ??= new RelayCommand(() =>
    {
        StatusMessage = _services.Actions.Execute(new ShowAction(ShowActionKind.AlignStop), ActionOrigin.Desk).Message;
        PollRigDay(force: true);
    });

    /// <summary>A key of the alignment game: the arrows, Tab, Backspace, S and Esc — true when it was one.</summary>
    public bool AlignKey(Avalonia.Input.Key key, bool shift)
    {
        if (_services.RigDay.Align is null) return false;
        var step = shift ? 5f : 1f;
        ActionResult? result = key switch
        {
            Avalonia.Input.Key.Left => _services.RigDay.Nudge(-step, 0),
            Avalonia.Input.Key.Right => _services.RigDay.Nudge(step, 0),
            Avalonia.Input.Key.Up => _services.RigDay.Nudge(0, -step),
            Avalonia.Input.Key.Down => _services.RigDay.Nudge(0, step),
            Avalonia.Input.Key.Tab => _services.RigDay.NextNode(),
            Avalonia.Input.Key.Back => _services.RigDay.PrevNode(),
            Avalonia.Input.Key.S => _services.RigDay.Snap(),
            Avalonia.Input.Key.Escape => _services.RigDay.StopAlign(),
            _ => null,
        };
        if (result is null) return false;
        StatusMessage = result.Message;
        PollRigDay(force: true);
        return true;
    }
}
