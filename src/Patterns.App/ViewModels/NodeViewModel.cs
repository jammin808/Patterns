using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Patterns.App.Services;
using Patterns.Core.Arcade;
using Patterns.Arcade;
using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// A node's window, as a view model: the pages its kind shows over a <see cref="NodeHost"/>,
/// with a status line — nothing of the desk. The pages' XAML binds to the interfaces this
/// implements, exactly as it binds to the desk's view model on a desk: the Run surface and the
/// Cues page of a caller, the stage's display and controls of a caller and a stage timer, the
/// Arcade page of the arcade, the Nodes page of every node.
/// </summary>
public sealed class NodeViewModel : Observable, IArcadePage, INodesPage, IRunPage, ICuesPage, IStagePage, IStageDisplay, IRunPageOwner, IDeskMenuHost
{
    /// <summary>A cue row's right-click menu on a node: the stack's own verbs and edits over the mirrored show; the preview and the assistant live on the desk.</summary>
    public DeskMenuVm? MenuFor(string kind, object? subject)
    {
        if (kind != "cue") return null;
        var cue = subject switch { RunRow r => r.Cue, CueRow c => c.Cue, Patterns.Core.Model.RunCueConfig q => q, _ => null };
        if (cue is null) return null;
        return CueMenus.Build(_host, Run, Cues, cue, subject as RunRow, subject as CueRow, Services.DeskMenuFacts.Node(_host.State),
            route =>
            {
                if (route.Page == "Cues") OpenCueInEditor(cue);
                return route.Page == "Cues" ? $"Cue {cue.Number} in the editor." : "That page is the desk's — open it there.";
            },
            _ => "The assistant lives on the desk — ask there.",
            m => StatusMessage = m);
    }

    /// <summary>The tabs of the node window, in its order; a kind hides the ones it does not show.</summary>
    public const int RunTab = 0, CuesTab = 1, StageTab = 2, ArcadeTab = 3, NodesTab = 4, MachineTab = 5;

    private readonly NodeHost _host;
    private readonly Views.ArcadeWindowHost _arcadeWindows;
    private string _statusMessage = "";
    private string _playQuestionLine = "";
    private string _stageDraft = "";
    private string _seen = "";
    private string _stageSeen = "";
    private string _displaySeen = "";
    private string _machineSeen = "";
    private int _selectedTab;
    private bool _stageControlsOpen;

    public NodeViewModel(NodeHost host)
    {
        _host = host;
        host.Kernel.Notifier = m => StatusMessage = m;
        Cues = new CueEditor(host, m => StatusMessage = m);
        Run = new RunViewModel(host, this);
        host.ShowMirrored += _ =>
        {
            Cues.Refresh();
            Run.Refresh();
            Raise(nameof(HasLowerThirds));
            Poll();
        };
        _selectedTab = host.Kind switch { NodeKind.Timer => StageTab, NodeKind.Arcade => ArcadeTab, _ => RunTab };
        _arcadeWindows = new Views.ArcadeWindowHost(this, n => host.Screens.FirstOrDefault(s => s.Index == n && !s.IsVirtual && !s.IsPlanned && !s.IsMissing));
        host.Arcade.WindowHost = (mode, display) => _arcadeWindows.Handle(mode, display);
        _stageControlsOpen = host.Kind != NodeKind.Timer;                 // a timer's window is the display; its controls fold away
        StatusMessage = $"{NodeKinds.Label(host.Kind)} node — {host.Kernel.Beacon.MachineName}. "
                        + (host.IsFollower ? "LINK on the Nodes page follows a desk; alone, " + (host.Kind == NodeKind.Caller ? "the stack is rehearsed on paper." : "the clock is this node's own.") : "The desk finds this node on the beacon.");
    }

    public NodeHost Host => _host;

    /// <summary>The window this view model is shown in — the file pickers are its; null headless.</summary>
    public Window? Window { get; set; }

    public string WindowTitle => $"Patterns — {NodeKinds.Label(_host.Kind)} node";

    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    public bool HasRun => _host.Kind == NodeKind.Caller;

    public bool HasCues => _host.Kind == NodeKind.Caller;

    public bool HasStage => _host.IsFollower;

    public bool HasArcade => _host.Kind == NodeKind.Arcade;

    /// <summary>The timer's controls under the display, open or folded; a timer's window starts folded, a caller's open.</summary>
    public bool StageControlsOpen { get => _stageControlsOpen; set => Set(ref _stageControlsOpen, value); }

    /// <summary>On the tick: the words as they move.</summary>
    public void Poll()
    {
        var now = $"{ArcadeWords}|{ArcadeStatus}|{ArcadeBoard}|{PlayCode}|{PlayWords}|{PlayJoinUrl}|{NodesLine}|{NodesIdentity}|{NodesLinkWords}|{PlanWords}";
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
            Raise(nameof(NodesLinkWords));
            Raise(nameof(PlanWords));
        }
        var waiting = _host.Play.Room.Waiting();
        if (waiting.Count != PlayQueue.Count || !waiting.Select(m => m.Id).SequenceEqual(PlayQueue.Select(m => m.Id)))
        {
            PlayQueue.Clear();
            foreach (var item in waiting) PlayQueue.Add(item);
        }
        if (HasRun)
        {
            Run.Tick();
            Raise(nameof(HeaderClock));
        }
        if (HasStage)
        {
            PollStage();
            PollDisplay();
        }
        PollMachine();
    }

    private ActionResult Run_(ShowAction action) => _host.Actions.Execute(action, ActionOrigin.Desk);

    private void Report(ActionResult result)
    {
        if (result.Message.Length > 0) StatusMessage = result.Message;
    }

    // ---- the Arcade page ----

    public ArcadeService Arcade => _host.Arcade;

    public string ArcadeWords => Arcade.Words;

    public string ArcadeStatus => Arcade.Status;

    public string ArcadeBoard => Arcade.BoardWords;

    public bool ArcadeNdi
    {
        get => Arcade.NdiOn;
        set
        {
            if (value == Arcade.NdiOn) return;
            StatusMessage = Run_(new ShowAction(ShowActionKind.ArcadeNdi, "", value ? "on" : "off")).Message;
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
            StatusMessage = Run_(new ShowAction(ShowActionKind.ArcadeSize, "", value)).Message;
            Raise(nameof(ArcadeSize));
        }
    }

    private RelayCommand<string>? _arcadeGame;

    public RelayCommand<string> ArcadeGameCommand => _arcadeGame ??= new RelayCommand<string>(id =>
        StatusMessage = Run_(new ShowAction(ShowActionKind.ArcadeAttract, "", id ?? "")).Message);

    private RelayCommand<string>? _arcadeStart;

    public RelayCommand<string> ArcadeStartCommand => _arcadeStart ??= new RelayCommand<string>(players =>
    {
        var game = Arcade.Snapshot().GameId;
        if (game.Length == 0) game = ArcadeEngine.Catalogue[0].Id;
        StatusMessage = Run_(new ShowAction(ShowActionKind.ArcadeStart, "", $"{game} {players ?? "1"}")).Message;
    });

    private RelayCommand? _arcadePause;

    public RelayCommand ArcadePauseCommand => _arcadePause ??= new RelayCommand(() =>
    {
        var kind = Arcade.Phase == ArcadePhase.Paused ? ShowActionKind.ArcadeResume : ShowActionKind.ArcadePause;
        StatusMessage = Run_(new ShowAction(kind)).Message;
    });

    private RelayCommand? _arcadeStop;

    public RelayCommand ArcadeStopCommand => _arcadeStop ??= new RelayCommand(() => StatusMessage = Run_(new ShowAction(ShowActionKind.ArcadeStop)).Message);

    private RelayCommand<string>? _arcadeWindow;

    /// <summary>POP OUT / FULLSCREEN / CLOSE: the node's own window for the game.</summary>
    public RelayCommand<string> ArcadeWindowCommand => _arcadeWindow ??= new RelayCommand<string>(mode =>
        StatusMessage = Arcade.Run(new ShowAction(ShowActionKind.ArcadeWindow, "", mode ?? "on")).Message);

    /// <summary>The game's own window while it is open.</summary>
    public Views.ArcadeWindowHost ArcadeWindows => _arcadeWindows;

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
        StatusMessage = cmd.IsAction ? Run_(cmd.Action).Message : "Not a verb.";
        Poll();
    });

    private RelayCommand? _playAdd;

    public RelayCommand PlayAddCommand => _playAdd ??= new RelayCommand(() =>
    {
        if (PlayQuestionLine.Trim().Length == 0) return;
        var result = Run_(new ShowAction(ShowActionKind.PlayAdd, "", PlayQuestionLine.Trim()));
        StatusMessage = result.Message;
        if (result.Ok) PlayQuestionLine = "";
        Poll();
    });

    private RelayCommand<ModerationItem>? _playApprove;

    public RelayCommand<ModerationItem> PlayApproveCommand => _playApprove ??= new RelayCommand<ModerationItem>(item =>
    {
        if (item is null) return;
        StatusMessage = Run_(new ShowAction(ShowActionKind.PlayApprove, "", item.Id)).Message;
        Poll();
    });

    private RelayCommand<ModerationItem>? _playReject;

    public RelayCommand<ModerationItem> PlayRejectCommand => _playReject ??= new RelayCommand<ModerationItem>(item =>
    {
        if (item is null) return;
        StatusMessage = Run_(new ShowAction(ShowActionKind.PlayReject, "", item.Id)).Message;
        Poll();
    });

    // ---- the Nodes page ----

    public ShowState State => _host.State;

    public bool IsDesk => false;

    public bool IsCallerNode => _host.Kind == NodeKind.Caller;

    public string NodesIdentity => _host.Kernel.Nodes.Identity;

    public string NodesLine => _host.Kernel.Nodes.RailLine;

    /// <summary>The link's words on a follower; the arcade, which holds no link, says how it is found.</summary>
    public string NodesLinkWords => _host.Twin?.Status ?? "This node has no link of its own: the desk finds it on the beacon and speaks to it on its wire.";

    public string PlanWords => _host.Kind == NodeKind.Caller ? CuePlan.Count(State.Stacks) : "";

    public ObservableCollection<NodeCard> Nodes => _host.Kernel.Nodes.Nodes;

    /// <summary>Plans are offered to a desk; a node holds none.</summary>
    public ObservableCollection<TwinService.PlanOffer> Plans { get; } = new();

    private RelayCommand? _offerPlan;

    public RelayCommand OfferPlanCommand => _offerPlan ??= new RelayCommand(() =>
        StatusMessage = _host.Twin is { } twin ? twin.OfferPlan().Message : "A plan is a caller's to offer; this node has no cues.");

    private RelayCommand? _unlinkNode;

    public RelayCommand UnlinkNodeCommand => _unlinkNode ??= new RelayCommand(() =>
    {
        StatusMessage = _host.Twin?.Unlink() ?? "This node holds no link.";
        Poll();
    });

    private RelayCommand<TwinService.PlanOffer>? _applyPlan;

    public RelayCommand<TwinService.PlanOffer> ApplyPlanCommand => _applyPlan ??= new RelayCommand<TwinService.PlanOffer>(_ => StatusMessage = "APPLY is the desk's press.");

    private RelayCommand<TwinService.PlanOffer>? _dismissPlan;

    public RelayCommand<TwinService.PlanOffer> DismissPlanCommand => _dismissPlan ??= new RelayCommand<TwinService.PlanOffer>(_ => { });

    private RelayCommand<NodeCard>? _linkNode;

    /// <summary>LINK: a caller or a stage timer follows that desk — its address and link port onto the twin settings, and the link dials.</summary>
    public RelayCommand<NodeCard> LinkNodeCommand => _linkNode ??= new RelayCommand<NodeCard>(card =>
    {
        if (card is null) return;
        StatusMessage = _host.Twin is { } twin ? twin.LinkTo(card) : "A caller or a stage timer links to a desk; an arcade node is found by it.";
        Poll();
    });

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

    // ---- the Machine tab: what this node is, its ports, the desk's key, the watchdog, an update, a restart ----

    /// <summary>"Caller node · CALLER-PC · build 1.4.0 · folder …".</summary>
    public string MachineIdentity => NodeMachine.Identity(_host.Kind, _host.Kernel.Beacon.MachineName, UpdateService.RunningVersion, _host.Kernel.Store.BaseDirectory);

    public string MachinePorts => NodeMachine.PortsWords(_host.Kind, State.Control);

    /// <summary>The wire's own line: up on which ports, or why it failed to start.</summary>
    public string MachineWireStatus => _host.Control.Status;

    public string MachineWatchdog => NodeMachine.WatchdogWords(_host.Updates.Supervised);

    /// <summary>A caller or a timer links with the desk's key; the arcade holds none.</summary>
    public bool HasLink => _host.IsFollower;

    public string MachineKeyWords => NodeMachine.KeyWords(State.Twin.Key.Length > 0, _host.Twin is { IsLinkedToDesk: true });

    public string UpdateStatus => _host.Updates.Status;

    public string UpdateLastNote => _host.Updates.LastNote;

    public string ManagementStatus => _host.Management.Status;

    public string MachineHelp => NodeMachine.Help(_host.Kind);

    /// <summary>This node's front door — its pages, where the desk's OPEN PAGES lands; "" while remote control is off.</summary>
    public string MachineFrontDoor
    {
        get
        {
            if (!State.Control.Enabled) return "";
            var urls = _host.Control.RemoteUrls();
            return urls.Count > 0 ? urls[0] : $"http://{Environment.MachineName}:{State.Control.HttpPort}/";
        }
    }

    private RelayCommand? _applyUpdate;

    /// <summary>APPLY UPDATE from the node's own window: no passcode — whoever sits at the machine owns it, as on a desk.</summary>
    public RelayCommand ApplyUpdateCommand => _applyUpdate ??= new RelayCommand(() => StatusMessage = _host.Updates.Apply("", ActionOrigin.Desk, byPolicy: true).Message);

    private RelayCommand? _restart;

    /// <summary>RESTART from the node's own window: through the watchdog, which brings the node back linked; refused in words without one.</summary>
    public RelayCommand RestartCommand => _restart ??= new RelayCommand(() => StatusMessage = _host.RestartInPlace(ActionOrigin.Desk).Message);

    private RelayCommand? _checkIn;

    public RelayCommand CheckInCommand => _checkIn ??= new RelayCommand(() =>
    {
        _host.Management.CheckInNow();
        StatusMessage = _host.Management.Status;
        PollMachine();
    });

    private RelayCommand? _openFrontDoor;

    public RelayCommand OpenFrontDoorCommand => _openFrontDoor ??= new RelayCommand(() =>
    {
        var url = MachineFrontDoor;
        if (url.Length == 0)
        {
            StatusMessage = "Remote control is off — this node's pages are closed.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"The browser could not be opened: {ex.Message}";
        }
    });

    private RelayCommand? _openFolder;

    /// <summary>The node's own folder: its settings, its show, its log, the updates folder an update is dropped into.</summary>
    public RelayCommand OpenFolderCommand => _openFolder ??= new RelayCommand(() =>
    {
        var dir = _host.Kernel.Store.BaseDirectory;
        StatusMessage = $"This node's folder: {dir}";
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"The folder could not be opened: {ex.Message}";
        }
    });

    private void PollMachine()
    {
        var now = $"{MachineIdentity}|{MachinePorts}|{MachineWireStatus}|{MachineWatchdog}|{MachineKeyWords}|{UpdateStatus}|{UpdateLastNote}|{ManagementStatus}|{MachineFrontDoor}";
        if (now == _machineSeen) return;
        _machineSeen = now;
        Raise(nameof(MachineIdentity));
        Raise(nameof(MachinePorts));
        Raise(nameof(MachineWireStatus));
        Raise(nameof(MachineWatchdog));
        Raise(nameof(MachineKeyWords));
        Raise(nameof(UpdateStatus));
        Raise(nameof(UpdateLastNote));
        Raise(nameof(ManagementStatus));
        Raise(nameof(MachineFrontDoor));
    }

    // ---- the Run surface: the caller's, over the stack as it stands here ----

    public RunViewModel Run { get; }

    /// <summary>The desk's clock while linked — the room's one clock — and this machine's alone.</summary>
    public string HeaderClock => _host.Kernel.Clock.Now.ToString("HH:mm:ss");

    /// <summary>A node's outputs are always held — the chip would say nothing a caller does not know.</summary>
    public bool IsPrepMode => false;

    public bool HasRunWall => false;

    public bool HasRunMonitor => false;
    public Patterns.App.Rendering.PipelineViewport? RunMonitorViewport => null;
    public string RunMonitorTitle => "";
    public double RunMonitorRatio => 16.0 / 9.0;

    // ---- the caller's lower thirds (round 62): the mirrored designs as chips; the verbs go to the desk ----

    private string _lowerThirdStatus = "";
    private RelayCommand<Patterns.Core.LowerThirds.LowerThirdDesign>? _chipLowerThird;
    private RelayCommand? _hideLowerThird;
    private RelayCommand? _takeLowerThird;

    public Patterns.Core.LowerThirds.LowerThirdsConfig LowerThirds => State.LowerThirds;

    public bool HasLowerThirds => State.LowerThirds.Designs.Count > 0;

    /// <summary>A chip: the design on air, through the desk when linked (LT on the wire); alone, the node says so.</summary>
    public RelayCommand<Patterns.Core.LowerThirds.LowerThirdDesign> ChipLowerThirdCommand => _chipLowerThird ??= new(d =>
    {
        if (d is null) return;
        LowerThirdStatus = Run_(new ShowAction(ShowActionKind.LowerThirdShow, d.Id)).Message;
    });

    public RelayCommand HideLowerThirdCommand => _hideLowerThird ??= new(() => LowerThirdStatus = Run_(new ShowAction(ShowActionKind.LowerThirdHide)).Message);

    public RelayCommand TakeLowerThirdCommand => _takeLowerThird ??= new(() => LowerThirdStatus = Run_(new ShowAction(ShowActionKind.LowerThirdTake)).Message);

    public string LowerThirdStatus
    {
        get => _lowerThirdStatus;
        private set { if (Set(ref _lowerThirdStatus, value) && value.Length > 0) StatusMessage = value; }
    }

    /// <summary>A node has no preview of its own: its chips go to the desk's air, as its keys do.</summary>
    public bool LowerThirdChipsToPreview { get => false; set { } }

    public bool HasLowerThirdInPreview => false;

    public bool IsRunWallCollapsed
    {
        get => State.Desk.RunWallCollapsed;
        set
        {
            if (State.Desk.RunWallCollapsed == value) return;
            State.Desk.RunWallCollapsed = value;
            Raise(nameof(IsRunWallCollapsed));
            Raise(nameof(RunWallToggleText));
        }
    }

    public string RunWallToggleText => IsRunWallCollapsed ? "◂ EXPAND TILES" : "▸ COLLAPSE TILES";

    private RelayCommand? _popOutRun;

    public RelayCommand PopOutRunCommand => _popOutRun ??= new RelayCommand(() => StatusMessage = "This window is the Run surface — the desk's pops out; a caller node's is the whole window.");

    public string StreakWords => "";

    public bool HasStreak => false;

    /// <summary>BLACKOUT on the Run surface: the desk's, called from here while linked; refused alone, since a node has no outputs to black.</summary>
    public bool IsBlackout
    {
        get => State.Blackout;
        set
        {
            if (value == State.Blackout) return;
            Report(Run_(new ShowAction(value ? ShowActionKind.BlackoutOn : ShowActionKind.BlackoutOff)));
            Raise(nameof(IsBlackout));
        }
    }

    public void OpenCueInEditor(RunCueConfig cue)
    {
        Cues.SelectedCue = cue;
        SelectedTab = CuesTab;
    }

    // ---- the Cues page ----

    public CueEditor Cues { get; }

    public bool ClickerArmed
    {
        get => _host.Kernel.Cues.For(CueStacks.Clicker(State)).Armed;
        set
        {
            var rt = _host.Kernel.Cues.For(CueStacks.Clicker(State));
            if (rt.Armed == value) return;
            Report(Run_(new ShowAction(value ? ShowActionKind.ListArm : ShowActionKind.ListDisarm, CueStacks.Clicker(State).Id)));
            Raise(nameof(ClickerArmed));
        }
    }

    private static readonly FilePickerFileType CueSheetTypes = new("Cue sheet (CSV or Excel)") { Patterns = new[] { "*.csv", "*.xlsx" } };
    private static readonly FilePickerFileType CsvTypes = new("CSV") { Patterns = new[] { "*.csv" } };

    private RelayCommand? _importSheet;

    public RelayCommand ImportCueSheetCommand => _importSheet ??= new RelayCommand(() => _ = ImportCueSheetAsync(append: false));

    private RelayCommand? _importSheetAppend;

    public RelayCommand ImportCueSheetAppendCommand => _importSheetAppend ??= new RelayCommand(() => _ = ImportCueSheetAsync(append: true));

    private RelayCommand? _exportSheet;

    public RelayCommand ExportCueSheetCommand => _exportSheet ??= new RelayCommand(() => _ = SaveTextAsync("Export the cue list", (Cues.SelectedStack?.Name ?? "cues") + ".csv", Cues.ExportCsv(), "Cue list exported"));

    private RelayCommand? _saveTemplate;

    public RelayCommand SaveCueTemplateCommand => _saveTemplate ??= new RelayCommand(() => _ = SaveTextAsync("Save the cue sheet template", "cue-sheet-template.csv", CueSheet.Template(), "Template saved"));

    private RelayCommand? _presenterReset;

    public RelayCommand PresenterResetCommand => _presenterReset ??= new RelayCommand(() =>
    {
        Report(Run_(new ShowAction(ShowActionKind.ListReset, CueStacks.Clicker(State).Id)));
        Raise(nameof(PresenterStepText));
    });

    /// <summary>The clicker's place: a caller sees the list, and where it stands, as the desk's page shows it.</summary>
    public string PresenterStepText
    {
        get
        {
            var clicker = CueStacks.Clicker(State);
            var rt = _host.Kernel.Cues.For(clicker);
            if (clicker.Cues.Count == 0) return "No clicker list.";
            return rt.CurrentIndex < 0 ? $"Clicker: {clicker.Cues.Count} cues, at the start." : $"Clicker: {rt.CurrentIndex + 1} of {clicker.Cues.Count}.";
        }
    }

    private RelayCommand? _openPopOut;

    /// <summary>The settings column beside the list is always there on a node with a cue selected.</summary>
    public RelayCommand OpenPopOutCommand => _openPopOut ??= new RelayCommand(() => StatusMessage = Cues.HasSelection ? "The cue's settings are in the column beside the list." : "Select a cue: its settings open beside the list.");

    public PopOutState PopOut { get; } = new();

    public string PopOutHint => "A cue's settings open in the column beside the list when it is selected.";

    /// <summary>Reads a CSV or the first sheet of an .xlsx into the selected list; returns the words for the status line.</summary>
    public string ImportCueSheetFrom(string path, bool append)
    {
        TableData table;
        try
        {
            table = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? XlsxTable.Read(File.ReadAllBytes(path))
                : CsvTable.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Error("Cue sheet read failed.", ex);
            return $"Could not read {Path.GetFileName(path)}: {ex.Message}";
        }
        var report = Cues.ImportRows(table, append);
        return $"{report.Split('\n')[0]} ({Path.GetFileName(path)})";
    }

    private async Task ImportCueSheetAsync(bool append)
    {
        if (Window is not { } window) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = append ? "Append a cue sheet" : "Import a cue sheet",
                AllowMultiple = false,
                FileTypeFilter = new[] { CueSheetTypes },
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path is null) return;
            StatusMessage = ImportCueSheetFrom(path, append);
        }
        catch (Exception ex)
        {
            StatusMessage = $"The file could not be chosen: {ex.Message}";
        }
    }

    private async Task SaveTextAsync(string title, string suggestedName, string text, string doneWord)
    {
        if (Window is not { } window) return;
        try
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = new[] { CsvTypes },
            });
            var path = file?.TryGetLocalPath();
            if (path is null) return;
            await File.WriteAllTextAsync(path, text);
            StatusMessage = $"{doneWord}: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The file could not be saved: {ex.Message}";
        }
    }

    // ---- the Stage block: the timer's controls, a message, the receipts ----

    public string StageDraft { get => _stageDraft; set => Set(ref _stageDraft, value ?? ""); }

    public string StageStatus => _host.Stage.StatusLine;

    /// <summary>Where this node's own stage pages are — a display on a tablet, the controller on a phone.</summary>
    public string StageUrls
    {
        get
        {
            var control = State.Control;
            if (!control.Enabled) return "This node's remote control is off — the stage pages need its HTTP port (Control → Enabled in its settings).";
            var urls = _host.Control.RemoteUrls();
            var root = urls.Count > 0 ? urls[0].TrimEnd('/') : $"http://{Environment.MachineName}:{control.HttpPort}";
            return $"{root}/stage — the speaker's display · {root}/stage?view=crew — the crew's · {root}/timer — the controller"
                   + (_host.Twin is { IsLinkedToDesk: true } ? " · every verb and every ACK from here goes to the desk this node follows" : "");
        }
    }

    private void PollStage()
    {
        var now = StageStatus + "|" + StageUrls;
        if (now == _stageSeen) return;
        _stageSeen = now;
        Raise(nameof(StageStatus));
        Raise(nameof(StageUrls));
    }

    private RelayCommand? _stageSend;
    public RelayCommand StageSendCommand => _stageSend ??= new RelayCommand(() =>
    {
        Report(Run_(new ShowAction(ShowActionKind.StageMessage, "speaker", StageDraft)));
        StageDraft = "";
        PollStage();
    });

    private RelayCommand? _stageSendCrew;
    public RelayCommand StageSendCrewCommand => _stageSendCrew ??= new RelayCommand(() =>
    {
        Report(Run_(new ShowAction(ShowActionKind.StageMessage, "crew", StageDraft)));
        StageDraft = "";
        PollStage();
    });

    private RelayCommand<string>? _stagePreset;
    public RelayCommand<string> StagePresetCommand => _stagePreset ??= new RelayCommand<string>(words =>
    {
        if (string.IsNullOrWhiteSpace(words)) return;
        Report(Run_(new ShowAction(ShowActionKind.StageMessage, "speaker", words)));
        PollStage();
    });

    private RelayCommand? _stageClear;
    public RelayCommand StageClearCommand => _stageClear ??= new RelayCommand(() => { Report(Run_(new ShowAction(ShowActionKind.StageClear))); PollStage(); });

    private RelayCommand? _stageFlash;
    public RelayCommand StageFlashCommand => _stageFlash ??= new RelayCommand(() => Report(Run_(new ShowAction(ShowActionKind.TimerFlash))));

    private RelayCommand? _stagePause;
    public RelayCommand StagePauseCommand => _stagePause ??= new RelayCommand(() => { Report(Run_(new ShowAction(ShowActionKind.TimerPause))); PollStage(); });

    private RelayCommand? _stageResume;
    public RelayCommand StageResumeCommand => _stageResume ??= new RelayCommand(() => { Report(Run_(new ShowAction(ShowActionKind.TimerResume))); PollStage(); });

    private RelayCommand<string>? _stageAdd;
    public RelayCommand<string> StageAddCommand => _stageAdd ??= new RelayCommand<string>(seconds =>
    {
        if (string.IsNullOrWhiteSpace(seconds)) return;
        Report(Run_(new ShowAction(ShowActionKind.TimerAdd, "", seconds)));
        PollStage();
    });

    private RelayCommand? _armCountdown;

    /// <summary>"Start now": the countdown's duration from this moment — the desk's clock while linked, this node's own alone.</summary>
    public RelayCommand ArmCountdownCommand => _armCountdown ??= new RelayCommand(() => Report(Run_(new ShowAction(ShowActionKind.CountdownStart))));

    // ---- the stage display: what the speaker sees ----

    private static readonly IBrush IdleBrush = Brush.Parse("#4A505E");
    private static readonly IBrush GreenBrush = Brush.Parse("#2EE68A");
    private static readonly IBrush AmberBrush = Brush.Parse("#FFC24D");
    private static readonly IBrush RedBrush = Brush.Parse("#FF5C7A");

    private StageTime Time => _host.Stage.Time();

    public string DisplayTime
    {
        get
        {
            var t = Time;
            return t.Phase switch
            {
                StageTimerPhase.Idle => State.Stage.SpeakerSeesClock ? _host.Kernel.Clock.Now.ToString("HH:mm:ss") : "--:--",
                StageTimerPhase.Over => "-" + StageTimer.Format(-t.RemainingSeconds),
                _ => t.Text,
            };
        }
    }

    public string DisplayLabel => Time.Phase == StageTimerPhase.Idle ? (State.Stage.SpeakerSeesClock ? "" : "No timer running") : State.Countdown.Label;

    public IBrush DisplayBrush => Time.Phase switch
    {
        StageTimerPhase.Idle => IdleBrush,
        StageTimerPhase.Over => RedBrush,
        _ => Time.Colour switch { "red" => RedBrush, "amber" => AmberBrush, _ => GreenBrush },
    };

    public string DisplayMessage => _host.Stage.Pending("speaker")?.Text ?? "";

    public bool DisplayHasMessage => DisplayMessage.Length > 0;

    public bool DisplayFlashing => State.Stage.FlashUntilUtc is { } until && until > _host.Kernel.Clock.UtcNow;

    /// <summary>The running order's segment for the speaker, when the desk lets the speaker see it: what is on, what is next.</summary>
    public string DisplaySegment
    {
        get
        {
            if (!State.Stage.SpeakerSeesSegment) return "";
            var stack = _host.CueStack;
            var parts = new List<string>();
            if (stack.LastCue is { } on) parts.Add($"NOW  {on.Number} {on.Name}");
            if (stack.StandbyCue is { } next) parts.Add($"NEXT  {next.Number} {next.Name}");
            return string.Join("     ", parts);
        }
    }

    public string DisplayLinkWords => _host.Twin?.Status ?? "";

    private RelayCommand? _displayAck;

    /// <summary>ACK on the display: the receipt goes to the desk this node follows, or lands here alone.</summary>
    public RelayCommand DisplayAckCommand => _displayAck ??= new RelayCommand(() =>
    {
        if (_host.Stage.Pending("speaker") is not { } pending) return;
        Report(Run_(new ShowAction(ShowActionKind.StageAck, "", pending.Id)));
        PollDisplay();
    });

    private void PollDisplay()
    {
        var now = $"{DisplayTime}|{DisplayLabel}|{DisplayMessage}|{DisplayFlashing}|{DisplaySegment}|{DisplayLinkWords}|{Time.Phase}|{Time.Colour}";
        if (now == _displaySeen) return;
        _displaySeen = now;
        Raise(nameof(DisplayTime));
        Raise(nameof(DisplayLabel));
        Raise(nameof(DisplayBrush));
        Raise(nameof(DisplayMessage));
        Raise(nameof(DisplayHasMessage));
        Raise(nameof(DisplayFlashing));
        Raise(nameof(DisplaySegment));
        Raise(nameof(DisplayLinkWords));
    }
}
