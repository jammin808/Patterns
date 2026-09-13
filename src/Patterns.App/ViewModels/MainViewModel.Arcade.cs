using Patterns.App.Services;
using Patterns.Core.Arcade;
using Patterns.Core.Model;

namespace Patterns.App.ViewModels;

/// <summary>The Arcade page: the game in hand, its words, the picture's size and NDI, the board — the arcade node's own page.</summary>
public sealed partial class MainViewModel
{
    private string _arcadeSeen = "";

    public bool IsArcadeNode => _services.Profile == NodeKind.Arcade;

    /// <summary>The arcade the page draws and the keys drive — the desk's.</summary>
    public ArcadeService Arcade => _services.Arcade;

    /// <summary>The Arcade page is the one showing — on a desk, the keys are the pads only then.</summary>
    public bool IsArcadePage => SelectedPageIndex == Shell.IndexOf("Arcade");

    public string ArcadeWords => _services.Arcade.Words;

    public string ArcadeStatus => _services.Arcade.Status;

    public string ArcadeBoard => _services.Arcade.BoardWords;

    public bool ArcadeNdi
    {
        get => _services.Arcade.NdiOn;
        set
        {
            if (value == _services.Arcade.NdiOn) return;
            StatusMessage = _services.Arcade.Run(new ShowAction(ShowActionKind.ArcadeNdi, "", value ? "on" : "off")).Message;
            Raise(nameof(ArcadeNdi));
        }
    }

    public IReadOnlyList<string> ArcadeSizes { get; } = new[] { "1280x720", "1920x1080", "2560x1440", "3840x1080", "3840x2160" };

    public string ArcadeSize
    {
        get => $"{_services.Arcade.Width}x{_services.Arcade.Height}";
        set
        {
            if (string.IsNullOrEmpty(value) || value == ArcadeSize) return;
            StatusMessage = _services.Arcade.Run(new ShowAction(ShowActionKind.ArcadeSize, "", value)).Message;
            Raise(nameof(ArcadeSize));
        }
    }

    /// <summary>On the tick: the words as they move.</summary>
    private void PollArcade()
    {
        if (!IsArcadeNode && !IsArcadePage) return;
        var now = $"{ArcadeWords}|{ArcadeStatus}|{ArcadeBoard}";
        if (now == _arcadeSeen) return;
        _arcadeSeen = now;
        Raise(nameof(ArcadeWords));
        Raise(nameof(ArcadeStatus));
        Raise(nameof(ArcadeBoard));
    }

    private RelayCommand<string>? _arcadeGame;

    /// <summary>A game's button: the house plays it; START on a pad joins.</summary>
    public RelayCommand<string> ArcadeGameCommand => _arcadeGame ??= new RelayCommand<string>(id =>
        StatusMessage = _services.Actions.Execute(new ShowAction(ShowActionKind.ArcadeAttract, "", id ?? ""), ActionOrigin.Desk).Message);

    private RelayCommand<string>? _arcadeStart;

    /// <summary>START 1P / 2P: the game in hand (else Pong) for that many people.</summary>
    public RelayCommand<string> ArcadeStartCommand => _arcadeStart ??= new RelayCommand<string>(players =>
    {
        var game = _services.Arcade.Snapshot().GameId;
        if (game.Length == 0) game = ArcadeEngine.Catalogue[0].Id;
        StatusMessage = _services.Actions.Execute(new ShowAction(ShowActionKind.ArcadeStart, "", $"{game} {players ?? "1"}"), ActionOrigin.Desk).Message;
    });

    private RelayCommand? _arcadePause;

    public RelayCommand ArcadePauseCommand => _arcadePause ??= new RelayCommand(() =>
    {
        var kind = _services.Arcade.Phase == ArcadePhase.Paused ? ShowActionKind.ArcadeResume : ShowActionKind.ArcadePause;
        StatusMessage = _services.Actions.Execute(new ShowAction(kind), ActionOrigin.Desk).Message;
    });

    private RelayCommand? _arcadeStop;

    public RelayCommand ArcadeStopCommand => _arcadeStop ??= new RelayCommand(() =>
        StatusMessage = _services.Actions.Execute(new ShowAction(ShowActionKind.ArcadeStop), ActionOrigin.Desk).Message);
}
