using Patterns.App.Services;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>One lens chip of the Eye page.</summary>
public sealed record EyeLensChip(string Label, string Word, bool IsCurrent);

/// <summary>One thing the selected thing links to, as the side card lists it.</summary>
public sealed record EyeContactRow(string Id, string Label, string Sub, string Verb, string Hue);

/// <summary>The EYE foot of the rail and the God's Eye page (round 66): the picture's words, the selection, its contacts, the lenses, the verbs.</summary>
public sealed partial class MainViewModel
{
    private long _eyeSeen = -1;
    private string _eyeSelectedId = "";

    public EyeService Eye => _services.Eye;

    public string EyeHeadline => _services.Eye.Graph.Headline;

    public string EyeWord => _services.Eye.RailWord;

    public string EyeHue => _services.Eye.RailHue;

    public string EyeLine => _services.Eye.RailLine;

    /// <summary>Moves when the picture or the view moved — the canvas redraws on it.</summary>
    public long EyeRev => _services.Eye.Rev;

    public IReadOnlyList<EyeLensChip> EyeLenses => Enum.GetValues<EyeLens>()
        .Select(l => new EyeLensChip(l.ToString().ToUpperInvariant(), l.ToString().ToLowerInvariant(), l == _services.Eye.Lens)).ToList();

    public string EyeProblemsWords
    {
        get
        {
            var g = _services.Eye.Graph;
            if (g.Nodes.Count == 0) return "";
            if (g.Problems.Count == 0) return "Nothing red or amber.";
            var at = _services.Eye.FocusId is { } f ? g.ProblemIndex(f) : -1;
            var counts = string.Join(" · ", new[] { g.Red > 0 ? $"{g.Red} red" : "", g.Amber > 0 ? $"{g.Amber} amber" : "" }.Where(w => w.Length > 0));
            return at >= 0 ? $"Problem {at + 1} of {g.Problems.Count} · {counts}" : counts;
        }
    }

    public string EyeFocusWords => _services.Eye.FocusId is { } f && _services.Eye.Graph.Find(f) is { } n ? $"Eye on {n.Label}" : "The whole picture";

    /// <summary>The thing selected on the picture (a click), "" for none. The selection is the side card's; the focus is the camera's.</summary>
    public string EyeSelectedId
    {
        get => _eyeSelectedId;
        set
        {
            var next = value ?? "";
            if (_eyeSelectedId == next) return;
            _eyeSelectedId = next;
            RaiseEyeSelection();
        }
    }

    public EyeNode? EyeSelected => _eyeSelectedId.Length > 0 ? _services.Eye.Graph.Find(_eyeSelectedId) : null;

    public bool HasEyeSelection => EyeSelected is not null;

    public string EyeSelectedLabel => EyeSelected?.Label ?? "";

    public string EyeSelectedSub => EyeSelected?.Sub ?? "";

    public string EyeSelectedKind => EyeSelected is { } n ? $"{KindWord(n.Kind)} · {EyeLayout.BandLabel(n.Plane)} · {EyeGraph.Light(n.Light)}" : "";

    public string EyeSelectedWords => EyeSelected is { } n ? string.Join("\n", n.Words) : "";

    public string EyeSelectedHue => EyeSelected is { } n ? EyeService.Hue(n.Light) : "#4A505E";

    public bool EyeCanOpen => EyeSelected?.Route is not null;

    public string EyeOpenWords => EyeSelected?.Route is { } r ? $"OPEN {r.Page.ToUpperInvariant()}" : "OPEN";

    public IReadOnlyList<EyeContactRow> EyeContacts
    {
        get
        {
            var g = _services.Eye.Graph;
            if (EyeSelected is not { } n) return Array.Empty<EyeContactRow>();
            var rows = new List<EyeContactRow>();
            foreach (var id in g.Neighbours(n.Id))
            {
                var c = g.Find(id);
                if (c is null) continue;
                var edge = g.EdgeBetween(n.Id, id);
                var verb = edge is null ? "" : edge.From == n.Id ? $"→ {edge.Verb}" : $"← {edge.Verb}";
                rows.Add(new EyeContactRow(c.Id, c.Label, c.Sub, verb, EyeService.Hue(c.Light)));
            }
            return rows;
        }
    }

    private RelayCommand<string>? _eyeSelect;

    /// <summary>A click on the picture, or a contact row: the side card shows it.</summary>
    public RelayCommand<string> EyeSelectCommand => _eyeSelect ??= new RelayCommand<string>(id => EyeSelectedId = id ?? "");

    private RelayCommand<string>? _eyeFocus;

    /// <summary>FOCUS on a thing — through the action layer, so a key and the wire do the same.</summary>
    public RelayCommand<string> EyeFocusCommand => _eyeFocus ??= new RelayCommand<string>(id =>
    {
        if (string.IsNullOrEmpty(id)) return;
        EyeSelectedId = id;
        RunEyeVerb(new ShowAction(ShowActionKind.EyeFocus, "", id));
    });

    private RelayCommand? _eyeFocusSelected;

    public RelayCommand EyeFocusSelectedCommand => _eyeFocusSelected ??= new RelayCommand(() =>
    {
        if (_eyeSelectedId.Length > 0) RunEyeVerb(new ShowAction(ShowActionKind.EyeFocus, "", _eyeSelectedId));
    });

    private RelayCommand? _eyeNext;

    public RelayCommand EyeNextCommand => _eyeNext ??= new RelayCommand(() =>
    {
        RunEyeVerb(new ShowAction(ShowActionKind.EyeNext));
        if (_services.Eye.FocusId is { } f) EyeSelectedId = f;
    });

    private RelayCommand? _eyePrev;

    public RelayCommand EyePrevCommand => _eyePrev ??= new RelayCommand(() =>
    {
        RunEyeVerb(new ShowAction(ShowActionKind.EyePrev));
        if (_services.Eye.FocusId is { } f) EyeSelectedId = f;
    });

    private RelayCommand? _eyeReset;

    public RelayCommand EyeResetCommand => _eyeReset ??= new RelayCommand(() =>
    {
        RunEyeVerb(new ShowAction(ShowActionKind.EyeReset));
        EyeSelectedId = "";
    });

    private RelayCommand<string>? _eyeLens;

    public RelayCommand<string> EyeLensCommand => _eyeLens ??= new RelayCommand<string>(word => RunEyeVerb(new ShowAction(ShowActionKind.EyeLens, "", word ?? "all")));

    private RelayCommand? _eyeOpen;

    /// <summary>OPEN: the page of the rail that shows the selected thing, with it selected there.</summary>
    public RelayCommand EyeOpenCommand => _eyeOpen ??= new RelayCommand(() =>
    {
        if (EyeSelected?.Route is { } route) StatusMessage = GoTo(route) ?? StatusMessage;
    });

    private RelayCommand? _eyeAsk;

    /// <summary>ASK: the selected thing, its facts and its links, in the question — the Eye menu's own words.</summary>
    public RelayCommand EyeAskCommand => _eyeAsk ??= new RelayCommand(() =>
    {
        if (EyeSelected is not { } n) return;
        var menu = EyeMenus.For(MenuFacts(), _services.Eye.Graph, n);
        var ask = menu.Flatten().FirstOrDefault(e => e.Id == (n.IsProblem ? "eye.why" : "eye.ask")) ?? menu.Flatten().First(e => e.Scope == MenuScope.Ask);
        StatusMessage = AskAssistant(ask.Question) ?? StatusMessage;
    });

    private void RunEyeVerb(ShowAction action)
    {
        var result = _services.Actions.Execute(action, ActionOrigin.Desk);
        StatusMessage = result.Message;
        RaiseEye();
    }

    /// <summary>On the desk's tick: the facts read, the picture rebuilt when they moved, the words raised when the revision moved.</summary>
    private void PollEye()
    {
        if (!_services.IsDesk) return;
        _services.Eye.Refresh();
        if (_services.Eye.Rev == _eyeSeen) return;
        RaiseEye();
    }

    private void RaiseEye()
    {
        _eyeSeen = _services.Eye.Rev;
        Raise(nameof(EyeHeadline));
        Raise(nameof(EyeWord));
        Raise(nameof(EyeHue));
        Raise(nameof(EyeLine));
        Raise(nameof(EyeRev));
        Raise(nameof(EyeLenses));
        Raise(nameof(EyeProblemsWords));
        Raise(nameof(EyeFocusWords));
        if (_eyeSelectedId.Length > 0 && _services.Eye.Graph.Find(_eyeSelectedId) is null) _eyeSelectedId = "";   // the selected thing left the picture
        RaiseEyeSelection();
    }

    private void RaiseEyeSelection()
    {
        Raise(nameof(EyeSelectedId));
        Raise(nameof(EyeSelected));
        Raise(nameof(HasEyeSelection));
        Raise(nameof(EyeSelectedLabel));
        Raise(nameof(EyeSelectedSub));
        Raise(nameof(EyeSelectedKind));
        Raise(nameof(EyeSelectedWords));
        Raise(nameof(EyeSelectedHue));
        Raise(nameof(EyeCanOpen));
        Raise(nameof(EyeOpenWords));
        Raise(nameof(EyeContacts));
    }

    private static string KindWord(EyeKind kind) => kind switch
    {
        EyeKind.Desk => "this desk",
        EyeKind.Source => "source",
        EyeKind.Screen => "screen",
        EyeKind.Display => "display",
        EyeKind.NdiSend => "NDI send",
        EyeKind.FarEnd => "far end",
        EyeKind.Device => "device",
        EyeKind.Deck => "deck",
        EyeKind.Companion => "Companion heard",
        EyeKind.Peers => "clients",
        EyeKind.Osc => "OSC",
        EyeKind.Stack => "cue stack",
        EyeKind.Assistant => "assistant",
        EyeKind.Twin => "twin",
        EyeKind.Node => "node",
        EyeKind.AudioSource => "audio source",
        EyeKind.AudioOut => "audio output",
        EyeKind.Room => "audience room",
        _ => "stream",
    };
}
