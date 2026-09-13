using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Arcade;
using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// A node's window, as a view model: the Arcade page and the Nodes page over a
/// <see cref="NodeHost"/>, with a status line — nothing of the desk. The pages' XAML binds to
/// the interfaces this implements, exactly as it binds to the desk's view model on a desk.
/// </summary>
public sealed class NodeViewModel : Observable, IArcadePage, INodesPage
{
    private readonly NodeHost _host;
    private string _statusMessage = "";
    private string _playQuestionLine = "";
    private string _seen = "";
    private int _selectedTab;

    public NodeViewModel(NodeHost host)
    {
        _host = host;
        host.Kernel.Notifier = m => StatusMessage = m;
        StatusMessage = $"{NodeKinds.Label(host.Kind)} node — {host.Kernel.Beacon.MachineName}. The desk finds this node on the beacon.";
    }

    public NodeHost Host => _host;

    public string WindowTitle => $"Patterns — {NodeKinds.Label(_host.Kind)} node";

    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    /// <summary>On the tick: the words as they move.</summary>
    public void Poll()
    {
        var now = $"{ArcadeWords}|{ArcadeStatus}|{ArcadeBoard}|{PlayCode}|{PlayWords}|{PlayJoinUrl}|{NodesLine}|{NodesIdentity}";
        if (now != _seen)
        {
            _seen = now;
            Raise(nameof(ArcadeWords));
            Raise(nameof(ArcadeStatus));
            Raise(nameof(ArcadeBoard));
            Raise(nameof(PlayCode));
            Raise(nameof(PlayWords));
            Raise(nameof(PlayJoinUrl));
            Raise(nameof(NodesLine));
            Raise(nameof(NodesIdentity));
        }
        var waiting = _host.Play.Room.Waiting();
        if (waiting.Count != PlayQueue.Count || !waiting.Select(m => m.Id).SequenceEqual(PlayQueue.Select(m => m.Id)))
        {
            PlayQueue.Clear();
            foreach (var item in waiting) PlayQueue.Add(item);
        }
    }

    private ActionResult Run(ShowAction action) => _host.Actions.Execute(action, ActionOrigin.Desk);

    // ---- the Arcade page ----

    public ArcadeService Arcade => _host.Kernel.Arcade;

    public string ArcadeWords => Arcade.Words;

    public string ArcadeStatus => Arcade.Status;

    public string ArcadeBoard => Arcade.BoardWords;

    public bool ArcadeNdi
    {
        get => Arcade.NdiOn;
        set
        {
            if (value == Arcade.NdiOn) return;
            StatusMessage = Run(new ShowAction(ShowActionKind.ArcadeNdi, "", value ? "on" : "off")).Message;
            Raise(nameof(ArcadeNdi));
        }
    }

    public IReadOnlyList<string> ArcadeSizes { get; } = new[] { "1280x720", "1920x1080", "2560x1440", "3840x1080", "3840x2160" };

    public string ArcadeSize
    {
        get => $"{Arcade.Width}x{Arcade.Height}";
        set
        {
            if (string.IsNullOrEmpty(value) || value == ArcadeSize) return;
            StatusMessage = Run(new ShowAction(ShowActionKind.ArcadeSize, "", value)).Message;
            Raise(nameof(ArcadeSize));
        }
    }

    private RelayCommand<string>? _arcadeGame;

    public RelayCommand<string> ArcadeGameCommand => _arcadeGame ??= new RelayCommand<string>(id =>
        StatusMessage = Run(new ShowAction(ShowActionKind.ArcadeAttract, "", id ?? "")).Message);

    private RelayCommand<string>? _arcadeStart;

    public RelayCommand<string> ArcadeStartCommand => _arcadeStart ??= new RelayCommand<string>(players =>
    {
        var game = Arcade.Snapshot().GameId;
        if (game.Length == 0) game = ArcadeEngine.Catalogue[0].Id;
        StatusMessage = Run(new ShowAction(ShowActionKind.ArcadeStart, "", $"{game} {players ?? "1"}")).Message;
    });

    private RelayCommand? _arcadePause;

    public RelayCommand ArcadePauseCommand => _arcadePause ??= new RelayCommand(() =>
    {
        var kind = Arcade.Phase == ArcadePhase.Paused ? ShowActionKind.ArcadeResume : ShowActionKind.ArcadePause;
        StatusMessage = Run(new ShowAction(kind)).Message;
    });

    private RelayCommand? _arcadeStop;

    public RelayCommand ArcadeStopCommand => _arcadeStop ??= new RelayCommand(() => StatusMessage = Run(new ShowAction(ShowActionKind.ArcadeStop)).Message);

    // ---- the audience block ----

    public string PlayCode => _host.Play.Code;

    public string PlayJoinUrl => _host.Play.JoinUrl;

    public string PlayWords => _host.Play.Words;

    public string PlayQuestionLine { get => _playQuestionLine; set => Set(ref _playQuestionLine, value ?? ""); }

    public ObservableCollection<ModerationItem> PlayQueue { get; } = new();

    private RelayCommand<string>? _playVerb;

    public RelayCommand<string> PlayVerbCommand => _playVerb ??= new RelayCommand<string>(line =>
    {
        var cmd = ControlProtocol.Parse(line ?? "");
        StatusMessage = cmd.IsAction ? Run(cmd.Action).Message : "Not a verb.";
        Poll();
    });

    private RelayCommand? _playAdd;

    public RelayCommand PlayAddCommand => _playAdd ??= new RelayCommand(() =>
    {
        if (PlayQuestionLine.Trim().Length == 0) return;
        var result = Run(new ShowAction(ShowActionKind.PlayAdd, "", PlayQuestionLine.Trim()));
        StatusMessage = result.Message;
        if (result.Ok) PlayQuestionLine = "";
        Poll();
    });

    private RelayCommand<ModerationItem>? _playApprove;

    public RelayCommand<ModerationItem> PlayApproveCommand => _playApprove ??= new RelayCommand<ModerationItem>(item =>
    {
        if (item is null) return;
        StatusMessage = Run(new ShowAction(ShowActionKind.PlayApprove, "", item.Id)).Message;
        Poll();
    });

    private RelayCommand<ModerationItem>? _playReject;

    public RelayCommand<ModerationItem> PlayRejectCommand => _playReject ??= new RelayCommand<ModerationItem>(item =>
    {
        if (item is null) return;
        StatusMessage = Run(new ShowAction(ShowActionKind.PlayReject, "", item.Id)).Message;
        Poll();
    });

    // ---- the Nodes page ----

    public ShowState State => _host.State;

    public bool IsDesk => false;

    public bool IsCallerNode => false;

    public string NodesIdentity => _host.Kernel.Nodes.Identity;

    public string NodesLine => _host.Kernel.Nodes.RailLine;

    public string NodesLinkWords => "This node has no link of its own: the desk finds it on the beacon and speaks to it on its wire.";

    public string PlanWords => "";

    public ObservableCollection<NodeCard> Nodes => _host.Kernel.Nodes.Nodes;

    public ObservableCollection<TwinService.PlanOffer> Plans { get; } = new();

    private RelayCommand? _offerPlan;

    public RelayCommand OfferPlanCommand => _offerPlan ??= new RelayCommand(() => StatusMessage = "A plan is a caller's to offer; this node has no cues.");

    private RelayCommand? _unlinkNode;

    public RelayCommand UnlinkNodeCommand => _unlinkNode ??= new RelayCommand(() => StatusMessage = "This node holds no link.");

    private RelayCommand<TwinService.PlanOffer>? _applyPlan;

    public RelayCommand<TwinService.PlanOffer> ApplyPlanCommand => _applyPlan ??= new RelayCommand<TwinService.PlanOffer>(_ => { });

    private RelayCommand<TwinService.PlanOffer>? _dismissPlan;

    public RelayCommand<TwinService.PlanOffer> DismissPlanCommand => _dismissPlan ??= new RelayCommand<TwinService.PlanOffer>(_ => { });

    private RelayCommand<NodeCard>? _linkNode;

    public RelayCommand<NodeCard> LinkNodeCommand => _linkNode ??= new RelayCommand<NodeCard>(_ => StatusMessage = "A caller links to a desk; an arcade node is found by it.");

    private RelayCommand<NodeCard>? _openNodePages;

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
}
