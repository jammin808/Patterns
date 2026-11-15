using System.ComponentModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// G34 (Show Bus 1.3 §13.16.6): the Nodes page's Stagewatch boxes as both desks draw them — the link on, its address, the stage's
/// follow, Lower floor and Release — each a press of the link's one show action from the desk: one journal row each, and the verb a
/// menu entry, a deck's key and the wire press too. A take that would change nothing is said, never run. The key is the one box no line
/// reaches: typed here alone, a new key starts bound to nothing (<see cref="StagewatchLinkConfig.TypeKey"/>). Every box reads the
/// settings and follows a change made anywhere at once, so a deck's LINK OFF unticks the box as it happens.
/// </summary>
public sealed class StagewatchBoxes : Observable
{
    private readonly StagewatchLinkConfig _config;
    private readonly StagewatchLinkService _link;
    private readonly Func<ShowAction, ActionResult> _press;
    private bool _floorShown;

    /// <param name="config">The link's settings, the boxes' one source.</param>
    /// <param name="link">The link, for the floor it records.</param>
    /// <param name="press">Runs one of the link's verbs from the desk, its words to the strip.</param>
    public StagewatchBoxes(StagewatchLinkConfig config, StagewatchLinkService link, Func<ShowAction, ActionResult> press)
    {
        _config = config;
        _link = link;
        _press = press;
        config.PropertyChanged += Moved;
    }

    private void Moved(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StagewatchLinkConfig.Enabled):
                Raise(nameof(On));
                break;
            case nameof(StagewatchLinkConfig.Host):
                Raise(nameof(Address));
                break;
            case nameof(StagewatchLinkConfig.FollowStage):
                Raise(nameof(Follow));
                break;
            case nameof(StagewatchLinkConfig.ApiKey):
                Raise(nameof(Key));
                break;
        }
    }

    /// <summary>The link on or off: STAGEWATCH LINK ON|OFF.</summary>
    public bool On
    {
        get => _config.Enabled;
        set
        {
            if (value != _config.Enabled) Press(new ShowAction(ShowActionKind.StagewatchLinkSet, "", value ? "ON" : "OFF"));
            Raise();
        }
    }

    /// <summary>The address typed — host[:port], or id: and a heard Stagewatch's id; empty finds one: STAGEWATCH LINK, when the box is left.</summary>
    public string Address
    {
        get => _config.Host;
        set
        {
            var typed = (value ?? "").Trim();
            if (!string.Equals(typed, _config.Host, StringComparison.Ordinal))
                Press(new ShowAction(ShowActionKind.StagewatchLinkSet, "", typed.Length == 0 ? "AUTO" : typed));
            Raise();                                                                        // a refused address leaves the box as the settings hold it
        }
    }

    /// <summary>The stage pages follow the Stagewatch's timer: STAGEWATCH FOLLOW ON|OFF.</summary>
    public bool Follow
    {
        get => _config.FollowStage;
        set
        {
            if (value != _config.FollowStage) Press(new ShowAction(ShowActionKind.StagewatchFollow, "", value ? "ON" : "OFF"));
            Raise();
        }
    }

    /// <summary>The key, typed here and nowhere else; a new one is bound to nothing until its first answer or a person binds it.</summary>
    public string Key
    {
        get => _config.ApiKey;
        set => _config.TypeKey(value);
    }

    /// <summary>A floor a verified link recorded for the Stagewatch, as the last look saw it: Lower floor is offered while one is.</summary>
    public bool HasFloor => _floorShown;

    /// <summary>Why Release is greyed: this desk follows no fact of a Stagewatch's for safety, so none is ever held (Show Bus 1.3 §13.17).</summary>
    public string ReleaseWords => StagewatchVerbs.NothingHeldWords;

    private RelayCommand? _lowerFloor;

    /// <summary>STAGEWATCH FLOOR LOWER, the menu entry's and the deck's press.</summary>
    public RelayCommand LowerFloorCommand => _lowerFloor ??= new RelayCommand(() =>
    {
        Press(new ShowAction(ShowActionKind.StagewatchFloorLower));
        Look();
    });

    private RelayCommand? _release;

    /// <summary>STAGEWATCH RELEASE — refused with its reason while nothing is held, which here is always.</summary>
    public RelayCommand ReleaseCommand => _release ??= new RelayCommand(() => Press(new ShowAction(ShowActionKind.StagewatchRelease)));

    private string _otherShown = "";

    /// <summary>
    /// The id that answered LINK in the Stagewatch's place, as the last look saw it — what ACCEPT NEW ID binds the link to, drawn beside
    /// the button so the press is the one the person read (Show Bus 1.3 §13.16.6); "" while none waits.
    /// </summary>
    public string OtherId => _otherShown;

    /// <summary>Accept new id is offered while a node answers in the Stagewatch's place.</summary>
    public bool HasOtherId => _otherShown.Length > 0;

    /// <summary>The words beside ACCEPT NEW ID: which id answers, and what the press does.</summary>
    public string OtherIdWords => _otherShown.Length > 0
        ? $"Something else answers in the Stagewatch's place, as {_otherShown} — compare it on its own screen first; accepting links to it with no pin or floor of the old node's, and this desk's key stays with the node it was held for"
        : "";

    private RelayCommand? _acceptNewId;

    /// <summary>
    /// Accept new id — the Nodes card's right-click entry and the Eye's: a person's press on this desk, bound to the id drawn beside
    /// it (one that changed since is refused with the reason), never a line's.
    /// </summary>
    public RelayCommand AcceptNewIdCommand => _acceptNewId ??= new RelayCommand(() =>
    {
        Press(new ShowAction(ShowActionKind.StagewatchAcceptId, "", _otherShown));
        Look();
    });

    /// <summary>On the page's tick: whether a floor is recorded and which id answers in the Stagewatch's place, said when either moved.</summary>
    public void Look()
    {
        var other = _link.OtherId;
        if (!string.Equals(other, _otherShown, StringComparison.Ordinal))
        {
            _otherShown = other;
            Raise(nameof(OtherId));
            Raise(nameof(HasOtherId));
            Raise(nameof(OtherIdWords));
        }
        var floor = _link.HasFloor;
        if (floor == _floorShown) return;
        _floorShown = floor;
        Raise(nameof(HasFloor));
    }

    private void Press(ShowAction action) => _press(action);
}
