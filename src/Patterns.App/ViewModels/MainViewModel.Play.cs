using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Play;

namespace Patterns.App.ViewModels;

/// <summary>The AUDIENCE block of the Arcade page: the room's door, a question's line, the verbs, the queue.</summary>
public sealed partial class MainViewModel
{
    private string _playSeen = "";
    private string _playQuestionLine = "";

    public string PlayCode => _services.Play.Code;

    public string PlayJoinUrl => _services.Play.JoinUrl;

    /// <summary>"ABCD · 12 joined · open: quiz 'Which hall?' — 8 answers · 2 waiting for you".</summary>
    public string PlayWords
    {
        get
        {
            var room = _services.Play.Room;
            var q = room.Current;
            var waiting = room.Waiting().Count;
            var parts = new List<string> { $"{room.PlayerCount} joined" };
            parts.Add(q is null ? "no question yet" : $"{q.State.ToString().ToLowerInvariant()}: {q.KindWord} '{q.Text}' — {q.AnswerCount} answer{(q.AnswerCount == 1 ? "" : "s")}");
            if (waiting > 0) parts.Add($"{waiting} waiting for you");
            parts.Add($"wall: {_services.Play.Wall.ToString().ToLowerInvariant()}");
            return string.Join(" · ", parts);
        }
    }

    public string PlayQuestionLine
    {
        get => _playQuestionLine;
        set => Set(ref _playQuestionLine, value ?? "");
    }

    public ObservableCollection<ModerationItem> PlayQueue { get; } = new();

    private void PollPlay()
    {
        _services.Play.Tick();
        if (!IsArcadeNode && !IsArcadePage) return;
        var now = $"{PlayCode}|{PlayWords}|{PlayJoinUrl}";
        if (now != _playSeen)
        {
            _playSeen = now;
            Raise(nameof(PlayCode));
            Raise(nameof(PlayWords));
            Raise(nameof(PlayJoinUrl));
        }
        var waiting = _services.Play.Room.Waiting();
        if (waiting.Count != PlayQueue.Count || !waiting.Select(m => m.Id).SequenceEqual(PlayQueue.Select(m => m.Id)))
        {
            PlayQueue.Clear();
            foreach (var item in waiting) PlayQueue.Add(item);
        }
    }

    private RelayCommand<string>? _playVerb;

    /// <summary>A PLAY line from a button: "PLAY NEXT", "PLAY SHOW join"…</summary>
    public RelayCommand<string> PlayVerbCommand => _playVerb ??= new RelayCommand<string>(line =>
    {
        var cmd = Patterns.Core.Services.ControlProtocol.Parse(line ?? "");
        StatusMessage = cmd.IsAction ? _services.Actions.Execute(cmd.Action, ActionOrigin.Desk).Message : "Not a verb.";
        PollPlay();
    });

    private RelayCommand? _playAdd;

    public RelayCommand PlayAddCommand => _playAdd ??= new RelayCommand(() =>
    {
        if (PlayQuestionLine.Trim().Length == 0) return;
        var result = _services.Actions.Execute(new ShowAction(ShowActionKind.PlayAdd, "", PlayQuestionLine.Trim()), ActionOrigin.Desk);
        StatusMessage = result.Message;
        if (result.Ok) PlayQuestionLine = "";
        PollPlay();
    });

    private RelayCommand<ModerationItem>? _playApprove;

    public RelayCommand<ModerationItem> PlayApproveCommand => _playApprove ??= new RelayCommand<ModerationItem>(item =>
    {
        if (item is null) return;
        StatusMessage = _services.Actions.Execute(new ShowAction(ShowActionKind.PlayApprove, "", item.Id), ActionOrigin.Desk).Message;
        PollPlay();
    });

    private RelayCommand<ModerationItem>? _playReject;

    public RelayCommand<ModerationItem> PlayRejectCommand => _playReject ??= new RelayCommand<ModerationItem>(item =>
    {
        if (item is null) return;
        StatusMessage = _services.Actions.Execute(new ShowAction(ShowActionKind.PlayReject, "", item.Id), ActionOrigin.Desk).Message;
        PollPlay();
    });
}
