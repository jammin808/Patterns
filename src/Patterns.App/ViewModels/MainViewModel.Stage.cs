using Patterns.Core.Model;

namespace Patterns.App.ViewModels;

/// <summary>The STAGE block of the Countdown page: the timer's controls, a message to the speaker or the crew, the receipts, where the displays are.</summary>
public sealed partial class MainViewModel
{
    private string _stageDraft = "";
    private string _stageStatusSeen = "";

    public string StageDraft { get => _stageDraft; set => Set(ref _stageDraft, value ?? ""); }

    public string StageStatus => _services.Stage.StatusLine;

    /// <summary>"http://FOH-PC:9696/stage — the speaker's display · /stage?view=crew — the crew's · /timer — the controller".</summary>
    public string StageUrls
    {
        get
        {
            var control = State.Control;
            if (!control.Enabled) return "The remote control is off (Remote page) — the stage pages need its HTTP port.";
            var urls = _services.Control.RemoteUrls();
            var root = urls.Count > 0 ? urls[0].TrimEnd('/') : $"http://{Environment.MachineName}:{control.HttpPort}";
            return $"{root}/stage — the speaker's display · {root}/stage?view=crew — the crew's · {root}/timer — the controller";
        }
    }

    private void PollStage()
    {
        var now = StageStatus;
        if (now == _stageStatusSeen) return;
        _stageStatusSeen = now;
        Raise(nameof(StageStatus));
    }

    private RelayCommand? _stageSend;
    public RelayCommand StageSendCommand => _stageSend ??= new RelayCommand(() =>
    {
        Report(_services.Actions.Execute(new ShowAction(ShowActionKind.StageMessage, "speaker", StageDraft), ActionOrigin.Desk));
        StageDraft = "";
        PollStage();
    });

    private RelayCommand? _stageSendCrew;
    public RelayCommand StageSendCrewCommand => _stageSendCrew ??= new RelayCommand(() =>
    {
        Report(_services.Actions.Execute(new ShowAction(ShowActionKind.StageMessage, "crew", StageDraft), ActionOrigin.Desk));
        StageDraft = "";
        PollStage();
    });

    private RelayCommand<string>? _stagePreset;
    public RelayCommand<string> StagePresetCommand => _stagePreset ??= new RelayCommand<string>(words =>
    {
        if (string.IsNullOrWhiteSpace(words)) return;
        Report(_services.Actions.Execute(new ShowAction(ShowActionKind.StageMessage, "speaker", words), ActionOrigin.Desk));
        PollStage();
    });

    private RelayCommand? _stageClear;
    public RelayCommand StageClearCommand => _stageClear ??= new RelayCommand(() => { Report(_services.Actions.Execute(ShowActionKind.StageClear, ActionOrigin.Desk)); PollStage(); });

    private RelayCommand? _stageFlash;
    public RelayCommand StageFlashCommand => _stageFlash ??= new RelayCommand(() => Report(_services.Actions.Execute(ShowActionKind.TimerFlash, ActionOrigin.Desk)));

    private RelayCommand? _stagePause;
    public RelayCommand StagePauseCommand => _stagePause ??= new RelayCommand(() => { Report(_services.Actions.Execute(ShowActionKind.TimerPause, ActionOrigin.Desk)); PollStage(); });

    private RelayCommand? _stageResume;
    public RelayCommand StageResumeCommand => _stageResume ??= new RelayCommand(() => { Report(_services.Actions.Execute(ShowActionKind.TimerResume, ActionOrigin.Desk)); PollStage(); });

    private RelayCommand<string>? _stageAdd;
    /// <summary>+60 / −30: seconds onto what is left.</summary>
    public RelayCommand<string> StageAddCommand => _stageAdd ??= new RelayCommand<string>(seconds =>
    {
        if (string.IsNullOrWhiteSpace(seconds)) return;
        Report(_services.Actions.Execute(new ShowAction(ShowActionKind.TimerAdd, "", seconds), ActionOrigin.Desk));
        PollStage();
    });
}
