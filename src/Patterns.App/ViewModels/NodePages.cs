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
