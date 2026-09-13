using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>The NODES foot of the rail and the Nodes page: every other Patterns heard, what is linked, and this process's own identity.</summary>
public sealed partial class MainViewModel
{
    private string _nodesSeen = "";

    /// <summary>"Patterns — show test pattern suite" on the desk; "Patterns — Caller node" and the like on a node.</summary>
    public string WindowTitle => _services.IsDesk ? "Patterns — show test pattern suite" : $"Patterns — {NodeKinds.Label(_services.Profile)} node";

    public bool IsDesk => _services.IsDesk;

    public bool IsCallerNode => _services.Profile == NodeKind.Caller;

    public string NodesWord => _services.Nodes.RailWord;

    public string NodesLine => _services.Nodes.RailLine;

    public string NodesHue => _services.Nodes.Hue;

    public string NodesIdentity => _services.Nodes.Identity;

    public string NodesLinkWords => _services.Twin.Status;

    /// <summary>On the desk's tick: the beacons onto the cards, the rail's word when it moved.</summary>
    private void PollNodes()
    {
        _services.Nodes.Poll();
        var now = $"{NodesWord}|{NodesHue}|{NodesLine}|{NodesIdentity}|{NodesLinkWords}";
        if (now == _nodesSeen) return;
        _nodesSeen = now;
        Raise(nameof(NodesWord));
        Raise(nameof(NodesLine));
        Raise(nameof(NodesHue));
        Raise(nameof(NodesIdentity));
        Raise(nameof(NodesLinkWords));
    }

    private RelayCommand<NodeCard>? _openNodePages;

    /// <summary>OPEN PAGES: the node's own pages in the browser.</summary>
    public RelayCommand<NodeCard> OpenNodePagesCommand => _openNodePages ??= new RelayCommand<NodeCard>(card =>
    {
        if (card is null || card.PagesUrl.Length == 0) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(card.PagesUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"The browser could not be opened: {ex.Message}";
        }
    });

    private RelayCommand<NodeCard>? _linkNode;

    /// <summary>LINK: this caller node follows that desk — its address and link port onto the twin settings, and the link dials.</summary>
    public RelayCommand<NodeCard> LinkNodeCommand => _linkNode ??= new RelayCommand<NodeCard>(card =>
    {
        if (card is null) return;
        StatusMessage = _services.Twin.LinkTo(card);
        PollNodes();
    });

    /// <summary>The caller's own cues as they stand here: "12 cues in 1 stack".</summary>
    public string PlanWords => Patterns.Core.Services.CuePlan.Count(State.Stacks);

    private RelayCommand? _offerPlan;

    /// <summary>OFFER PLAN on a caller: the cues here to the desk's Nodes page.</summary>
    public RelayCommand OfferPlanCommand => _offerPlan ??= new RelayCommand(() => StatusMessage = _services.Twin.OfferPlan().Message);

    private RelayCommand<TwinService.PlanOffer>? _applyPlan;

    /// <summary>APPLY on the desk: the caller's plan onto this show, a version kept first.</summary>
    public RelayCommand<TwinService.PlanOffer> ApplyPlanCommand => _applyPlan ??= new RelayCommand<TwinService.PlanOffer>(offer =>
    {
        if (offer is null) return;
        StatusMessage = _services.Twin.ApplyPlan(offer).Message;
        Cues.Refresh();
    });

    private RelayCommand<TwinService.PlanOffer>? _dismissPlan;

    public RelayCommand<TwinService.PlanOffer> DismissPlanCommand => _dismissPlan ??= new RelayCommand<TwinService.PlanOffer>(offer =>
    {
        if (offer is null) return;
        _services.Twin.DismissPlan(offer);
        StatusMessage = $"The caller {offer.Caller}'s plan was set aside.";
    });

    private RelayCommand? _unlinkNode;

    public RelayCommand UnlinkNodeCommand => _unlinkNode ??= new RelayCommand(() =>
    {
        StatusMessage = _services.Twin.Unlink();
        PollNodes();
    });
}
