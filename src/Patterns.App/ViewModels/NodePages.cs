using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// What the Arcade page binds to: the desk's view model on a desk, a node's on an arcade node.
/// The page's XAML is written against this, so the same page serves both and never knows which.
/// </summary>
public interface IArcadePage
{
    ArcadeService Arcade { get; }
    string ArcadeWords { get; }
    string ArcadeStatus { get; }
    string ArcadeBoard { get; }
    bool ArcadeNdi { get; set; }
    IReadOnlyList<string> ArcadeSizes { get; }
    string ArcadeSize { get; set; }
    RelayCommand<string> ArcadeGameCommand { get; }
    RelayCommand<string> ArcadeStartCommand { get; }
    RelayCommand ArcadePauseCommand { get; }
    RelayCommand ArcadeStopCommand { get; }
    /// <summary>The game's own window: "on" opens it, "full" fills the display, "off" closes it.</summary>
    RelayCommand<string> ArcadeWindowCommand { get; }
    string PlayCode { get; }
    string PlayJoinUrl { get; }
    string PlayWords { get; }
    string PlayQuestionLine { get; set; }
    ObservableCollection<ModerationItem> PlayQueue { get; }
    RelayCommand<string> PlayVerbCommand { get; }
    RelayCommand PlayAddCommand { get; }
    RelayCommand<ModerationItem> PlayApproveCommand { get; }
    RelayCommand<ModerationItem> PlayRejectCommand { get; }
}

/// <summary>What the Nodes page binds to — the desk's view model, or a node's.</summary>
public interface INodesPage
{
    ShowState State { get; }
    bool IsDesk { get; }
    bool IsCallerNode { get; }
    string NodesIdentity { get; }
    string NodesLine { get; }
    string NodesLinkWords { get; }
    string PlanWords { get; }
    ObservableCollection<NodeCard> Nodes { get; }
    ObservableCollection<TwinService.PlanOffer> Plans { get; }
    RelayCommand OfferPlanCommand { get; }
    RelayCommand UnlinkNodeCommand { get; }
    RelayCommand<TwinService.PlanOffer> ApplyPlanCommand { get; }
    RelayCommand<TwinService.PlanOffer> DismissPlanCommand { get; }
    RelayCommand<NodeCard> LinkNodeCommand { get; }
    RelayCommand<NodeCard> OpenNodePagesCommand { get; }
}

/// <summary>What the Run surface asks of the view model it lives in: a line for the status strip, and the editor a row's OPEN IN EDITOR goes to.</summary>
public interface IRunPageOwner
{
    string StatusMessage { set; }
    void OpenCueInEditor(Patterns.Core.Model.RunCueConfig cue);
}

/// <summary>
/// The Run surface — the LIVE strip, the list, the transport — as its XAML binds it: the desk's
/// window has the wall beside the list and the rig day streak; a caller node has the list, the
/// history and the clock, and the same GO.
/// </summary>
public interface IRunPage
{
    RunViewModel Run { get; }
    string HeaderClock { get; }
    bool IsPrepMode { get; }
    /// <summary>The desk has the wall's tiles beside the list; a node has no wall, and the history takes the room.</summary>
    bool HasRunWall { get; }
    bool IsRunWallCollapsed { get; set; }
    string RunWallToggleText { get; }
    RelayCommand PopOutRunCommand { get; }
    string StreakWords { get; }
    bool HasStreak { get; }
    bool IsBlackout { get; set; }
}

/// <summary>The Cues page: the editor, the clicker's arming, a cue sheet in and out, the settings column beside the list.</summary>
public interface ICuesPage
{
    CueEditor Cues { get; }
    bool ClickerArmed { get; set; }
    RelayCommand ImportCueSheetCommand { get; }
    RelayCommand ImportCueSheetAppendCommand { get; }
    RelayCommand ExportCueSheetCommand { get; }
    RelayCommand SaveCueTemplateCommand { get; }
    RelayCommand PresenterResetCommand { get; }
    string PresenterStepText { get; }
    RelayCommand OpenPopOutCommand { get; }
    PopOutState PopOut { get; }
    string PopOutHint { get; }
}

/// <summary>The STAGE block: the timer's transport, the thresholds, a message to the speaker or the crew, the receipts, where the displays are.</summary>
public interface IStagePage
{
    ShowState State { get; }
    string StageStatus { get; }
    string StageUrls { get; }
    string StageDraft { get; set; }
    RelayCommand StageSendCommand { get; }
    RelayCommand StageSendCrewCommand { get; }
    RelayCommand<string> StagePresetCommand { get; }
    RelayCommand StageClearCommand { get; }
    RelayCommand StageFlashCommand { get; }
    RelayCommand StagePauseCommand { get; }
    RelayCommand StageResumeCommand { get; }
    RelayCommand<string> StageAddCommand { get; }
    RelayCommand ArmCountdownCommand { get; }
}

/// <summary>The stage display itself — a timer node's kiosk: the time in the colour of what is left, the label, the message in large type with its ACK, the segment, a flash.</summary>
public interface IStageDisplay
{
    string DisplayTime { get; }
    string DisplayLabel { get; }
    Avalonia.Media.IBrush DisplayBrush { get; }
    string DisplayMessage { get; }
    bool DisplayHasMessage { get; }
    bool DisplayFlashing { get; }
    string DisplaySegment { get; }
    string DisplayLinkWords { get; }
    RelayCommand DisplayAckCommand { get; }
}

