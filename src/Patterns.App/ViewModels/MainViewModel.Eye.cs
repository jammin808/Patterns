using Patterns.App.Services;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>One lens chip of the Eye page.</summary>
public sealed record EyeLensChip(string Label, string Word, bool IsCurrent);

/// <summary>
/// One link of the selected thing, as the side card lists it — a row per link, so two links to one thing are two rows: the thing
/// at its other end (its id, label, line, colour and mark) and the link's direction, verb and mark.
/// </summary>
public sealed record EyeContactRow(string Id, string Label, string Sub, string Verb, string Hue, string Mark = "○");

/// <summary>One link of the selected app as the side card's LINKS list shows it: its mark in its colour, whose link it is, its words, its flags and its clock.</summary>
public sealed record EyeLinkView(string Mark, string Hue, string Title, string Words, string Flags, string Clock)
{
    public bool HasFlags => Flags.Length > 0;

    public bool HasClock => Clock.Length > 0;
}

/// <summary>The EYE foot of the rail and the God's Eye page (round 66): the picture's words, the selection, its contacts, the lenses, the verbs.</summary>
public sealed partial class MainViewModel
{
    private long _eyeSeen = -1;
    private string _eyeSelectedId = "";

    public EyeService Eye => _services.Eye;

    public string EyeHeadline => _services.Eye.Shown.Headline;

    public string EyeWord => _services.Eye.RailWord;

    public string EyeHue => _services.Eye.RailHue;

    /// <summary>The rail's EYE foot's mark — the worst light's shape beside its colour, so the foot is never colour alone.</summary>
    public string EyeMark => _services.Eye.RailMark;

    public string EyeLine => _services.Eye.RailLine;

    /// <summary>Moves when the picture or the view moved — the canvas redraws on it.</summary>
    public long EyeRev => _services.Eye.Rev;

    public IReadOnlyList<EyeLensChip> EyeLenses => Enum.GetValues<EyeLens>()
        .Select(l => new EyeLensChip(l.ToString().ToUpperInvariant(), l.ToString().ToLowerInvariant(), l == _services.Eye.Lens)).ToList();

    public string EyeProblemsWords
    {
        get
        {
            var g = _services.Eye.Shown;
            if (g.Nodes.Count == 0) return "";
            if (g.Problems.Count == 0) return "Nothing red or amber.";
            var at = _services.Eye.FocusId is { } f ? g.ProblemIndex(f) : -1;
            var counts = string.Join(" · ", new[] { g.Red > 0 ? $"{g.Red} red" : "", g.Amber > 0 ? $"{g.Amber} amber" : "" }.Where(w => w.Length > 0));
            var theirs = g.Problems.Count - g.CountedProblems;
            if (counts.Length == 0) counts = $"{theirs} of other apps' own, not counted";      // only borrowed problems: said as theirs, never blank
            return at >= 0 ? $"Problem {at + 1} of {g.Problems.Count} · {counts}" : counts;
        }
    }

    public string EyeFocusWords => _services.Eye.FocusId is { } f && _services.Eye.Shown.Find(f) is { } n ? $"Eye on {n.Label}" : "The whole picture";

    /// <summary>The thing selected on the picture (a click), "" for none. The selection is the side card's; the focus is the camera's.</summary>
    public string EyeSelectedId
    {
        get => _eyeSelectedId;
        set
        {
            var next = value ?? "";
            if (_eyeSelectedId == next) return;
            _eyeSelectedId = next;
            if (next.Length > 0) _eyeSelectedEdge = "";                                       // a thing or a link is selected, never both
            RaiseEyeSelection();
        }
    }

    public EyeNode? EyeSelected => _eyeSelectedId.Length > 0 ? _services.Eye.Shown.Find(_eyeSelectedId) : null;

    public bool HasEyeSelection => EyeSelected is not null;

    public string EyeSelectedLabel => EyeSelected?.Label ?? "";

    public string EyeSelectedSub => EyeSelected?.Sub ?? "";

    public string EyeSelectedKind => EyeSelected is { } n ? $"{KindWord(n.Kind)} · {EyeLayout.Where(n)} · {EyeGraph.Light(n.Light)}" : "";

    public string EyeSelectedWords => EyeSelected is { } n ? string.Join("\n", n.Words) : "";

    public string EyeSelectedHue => EyeSelected is { } n ? EyeService.Hue(n.Light) : "#4A505E";

    /// <summary>The selected thing's mark — the shape beside its colour, derived from its light (✓ △ ✕ ○ i).</summary>
    public string EyeSelectedMark => EyeSelected is { } n ? EyeMarks.Glyph(n.Mark) : "";

    public bool EyeCanOpen => EyeSelected?.Route is not null;

    public string EyeOpenWords => EyeSelected?.Route is { } r ? $"OPEN {r.Page.ToUpperInvariant()}" : "OPEN";

    /// <summary>The selected thing is another app's card with things to draw — its cluster opens or folds from the side card.</summary>
    public bool EyeCanCluster => EyeSelected is { Kind: EyeKind.ShowApp } n && _services.Eye.Graph.NamedBy(n.Id) > 0;

    public string EyeClusterWords => EyeSelected is { } n && _services.Eye.Placement.IsExpanded(n.Id) ? "FOLD CLUSTER" : "OPEN CLUSTER";

    private RelayCommand? _eyeCluster;

    /// <summary>OPEN / FOLD CLUSTER: the selected app's cluster on the NETWORK band — the desk's view, the menu's same edit.</summary>
    public RelayCommand EyeClusterCommand => _eyeCluster ??= new RelayCommand(() =>
    {
        if (EyeSelected is not { } n) return;
        StatusMessage = _services.Eye.Cluster(n.Id, !_services.Eye.Placement.IsExpanded(n.Id)).Message;
        RaiseEye();
    });

    public IReadOnlyList<EyeContactRow> EyeContacts
    {
        get
        {
            var g = _services.Eye.Shown;
            if (EyeSelected is not { } n) return Array.Empty<EyeContactRow>();
            // A row per link, in the menu's LINKED TO order: the right-click menu and the side card list the same links.
            var rows = new List<EyeContactRow>();
            foreach (var id in g.Neighbours(n.Id))
            {
                var c = g.Find(id);
                if (c is null) continue;
                foreach (var edge in g.EdgesBetween(n.Id, id))
                {
                    var words = EyeMarks.Unmarked(edge.Words, edge.Mark);                          // the mark said once, first
                    var source = EyeMarks.SourceWords(edge);                                         // a claim says whose words it is, as the menu does
                    var verb = $"{EyeMarks.Glyph(edge.Mark)} " + (edge.From == n.Id ? $"→ {edge.Verb}" : $"← {edge.Verb}") + (words.Length > 0 ? " · " + words : "") + (source.Length > 0 ? " · " + source : "");
                    rows.Add(new EyeContactRow(c.Id, c.Label, c.Sub, verb, EyeService.Hue(c.Light), EyeMarks.Glyph(c.Mark)));
                }
            }
            return rows;
        }
    }

    /// <summary>
    /// The side card's LINKS on an app's card: each link between this desk and the app from its own record (the link this desk holds or
    /// drives it by, each link its connector made to this desk's wire), with its flags and its clock; empty for any other thing, and
    /// while a replay is open — the records are of now, and the picture is of then. Read live on the desk's tick: the clock and the
    /// samples move without moving the picture's hash (<see cref="PollEye"/>).
    /// </summary>
    public IReadOnlyList<EyeLinkView> EyeLinks
    {
        get
        {
            if (EyeReplaying || EyeSelected is not { Kind: EyeKind.ShowApp } n || n.App.Length == 0) return Array.Empty<EyeLinkView>();
            var app = _services.Eye.Apps.FirstOrDefault(a => a.Id == n.App);
            return app is null ? Array.Empty<EyeLinkView>()
                : Patterns.Core.Services.EyeLinks.Of(app).Select(r => new EyeLinkView(r.Mark, EyeService.Hue(r.Light), r.Title, r.Words, r.Flags, r.Clock)).ToList();
        }
    }

    public bool HasEyeLinks => EyeLinks.Count > 0;

    // ---- a link selected (a click on its segment, after the things) ----------------------------

    private string _eyeSelectedEdge = "";

    /// <summary>The link selected on the picture, by its menu subject (<see cref="EyeMenus.EdgeSubject"/>); "" for none.</summary>
    public string EyeSelectedEdgeId
    {
        get => _eyeSelectedEdge;
        set
        {
            var next = value ?? "";
            if (_eyeSelectedEdge == next) return;
            _eyeSelectedEdge = next;
            if (next.Length > 0) _eyeSelectedId = "";
            RaiseEyeSelection();
        }
    }

    public EyeEdge? EyeSelectedEdge
    {
        get
        {
            if (_eyeSelectedEdge.Length == 0) return null;
            var g = _services.Eye.Shown;
            return g.Edges.FirstOrDefault(e => EyeMenus.EdgeSubject(g, e) == _eyeSelectedEdge);
        }
    }

    public bool HasEyeEdgeSelection => EyeSelectedEdge is not null;

    /// <summary>Neither a thing nor a link is selected: the side card says how to pick one.</summary>
    public bool EyeNothingSelected => !HasEyeSelection && !HasEyeEdgeSelection;

    public string EyeEdgeTitle => EyeSelectedEdge is { } e ? $"{_services.Eye.Shown.Find(e.From)?.Label ?? e.From} → {_services.Eye.Shown.Find(e.To)?.Label ?? e.To}" : "";

    public string EyeEdgeMark => EyeSelectedEdge is { } e ? EyeMarks.Glyph(e.Mark) : "";

    public string EyeEdgeHue => EyeSelectedEdge is { } e ? EyeService.Hue(e.Light) : "#4A505E";

    public string EyeEdgeWords => EyeSelectedEdge is { } e ? $"{e.Verb} · {EyeMarks.Unmarked(e.Words, e.Mark)}".TrimEnd(' ', '·') : "";

    /// <summary>Where the link comes from, in words: this desk's own, a link's record, a thing's own context, a claim ("❝ PlayMap Stage left says"), or a hello alone.</summary>
    public string EyeEdgeSource => EyeSelectedEdge is { } e ? (EyeMarks.SourceWords(e) is { Length: > 0 } s ? s : e.Origin == EyeOrigin.Link ? "a link's own record" : "this desk's own") : "";

    /// <summary>The two ends of the selected link, as contact rows — select one to read it.</summary>
    public IReadOnlyList<EyeContactRow> EyeEdgeEnds
    {
        get
        {
            if (EyeSelectedEdge is not { } e) return Array.Empty<EyeContactRow>();
            var g = _services.Eye.Shown;
            return new[] { (e.From, "leaves from"), (e.To, "lands on") }
                .Select(x => (Node: g.Find(x.Item1), Words: x.Item2)).Where(x => x.Node is not null)
                .Select(x => new EyeContactRow(x.Node!.Id, x.Node.Label, x.Node.Sub, x.Words, EyeService.Hue(x.Node.Light), EyeMarks.Glyph(x.Node.Mark))).ToList();
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
        var menu = EyeMenus.For(MenuFacts(), _services.Eye.Shown, n, _services.Eye.Placement);
        var ask = menu.Flatten().FirstOrDefault(e => e.Id == (n.IsProblem ? "eye.why" : "eye.ask")) ?? menu.Flatten().First(e => e.Scope == MenuScope.Ask);
        StatusMessage = AskAssistant(ask.Question) ?? StatusMessage;
    });

    // ---- the replay (round 84) ----------------------------------------------------------------

    /// <summary>True while the page shows the record at an instant instead of the picture of now; the rail stays live.</summary>
    public bool EyeReplaying => _services.Eye.Replaying;

    public string EyeReplayButton => EyeReplaying ? "NOW" : "REPLAY";

    /// <summary>The strip's line: the instant, the rows in its window, how the desk was doing then — or that the record is still being read.</summary>
    public string EyeReplayWords => _services.Eye.Moment is { } m ? EyeReplay.Words(m) : _services.Eye.Reading ? "Reading the record on a worker — the picture relights when it is read." : "";

    /// <summary>The record's reach, first stamp to last, in the desk's clock.</summary>
    public string EyeReplaySpan => _services.Eye.Record is { IsEmpty: false, FirstUtc: { } first, LastUtc: { } last }
        ? $"{first.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} → {last.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}"
        : "";

    /// <summary>The scrub bar: 0 at the record's first stamp, 1 at its last; a drag moves the replay without a verb.</summary>
    public double EyeReplayPosition
    {
        get => _services.Eye.Replaying ? _services.Eye.Record.Position(_services.Eye.ReplayAtUtc) : 1;
        set
        {
            if (!_services.Eye.Replaying) return;
            _services.Eye.Scrub(value);
            RaiseEye();
        }
    }

    /// <summary>The rows of the window, newest first, as the card lists them.</summary>
    public string EyeReplayText => _services.Eye.Moment is { } m ? string.Join("\n", m.Recent.Select(EyeReplay.Line).Reverse()) : "";

    public bool HasEyeReplayRows => _services.Eye.Moment is { Recent.Count: > 0 };

    private RelayCommand? _eyeReplay;

    /// <summary>REPLAY / NOW — through the action layer, so a key, a menu line and the wire do the same.</summary>
    public RelayCommand EyeReplayCommand => _eyeReplay ??= new RelayCommand(() => RunEyeVerb(new ShowAction(ShowActionKind.EyeReplay, "", EyeReplaying ? "OFF" : "ON")));

    private RelayCommand<string>? _eyeReplayStep;

    /// <summary>The replay stepped by a number of seconds (negative for back), held inside the record.</summary>
    public RelayCommand<string> EyeReplayStepCommand => _eyeReplayStep ??= new RelayCommand<string>(seconds =>
    {
        if (!double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) return;
        _services.Eye.Step(TimeSpan.FromSeconds(s));
        RaiseEye();
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
        if (_services.Eye.Rev == _eyeSeen)
        {
            RaiseEyeLinksIfMoved();
            return;
        }
        RaiseEye();
    }

    private string _eyeLinksSeen = "";

    /// <summary>
    /// The side card's LINKS raised when their words moved though the picture did not — a link's clock, its samples, its flags — so
    /// the rows never stand minutes old as current; compared as words, a few rows at most, and nothing rebuilt.
    /// </summary>
    private void RaiseEyeLinksIfMoved()
    {
        var words = EyeLinksWords();
        if (string.Equals(words, _eyeLinksSeen, StringComparison.Ordinal)) return;
        _eyeLinksSeen = words;
        Raise(nameof(EyeLinks));
        Raise(nameof(HasEyeLinks));
    }

    private string EyeLinksWords() => string.Join("\n", EyeLinks.Select(r => $"{r.Mark}|{r.Hue}|{r.Title}|{r.Words}|{r.Flags}|{r.Clock}"));

    private void RaiseEye()
    {
        _eyeSeen = _services.Eye.Rev;
        Raise(nameof(EyeHeadline));
        Raise(nameof(EyeWord));
        Raise(nameof(EyeHue));
        Raise(nameof(EyeMark));
        Raise(nameof(EyeLine));
        Raise(nameof(EyeRev));
        Raise(nameof(EyeLenses));
        Raise(nameof(EyeProblemsWords));
        Raise(nameof(EyeFocusWords));
        Raise(nameof(EyeReplaying));
        Raise(nameof(EyeReplayButton));
        Raise(nameof(EyeReplayWords));
        Raise(nameof(EyeReplaySpan));
        Raise(nameof(EyeReplayPosition));
        Raise(nameof(EyeReplayText));
        Raise(nameof(HasEyeReplayRows));
        if (_eyeSelectedId.Length > 0 && _services.Eye.Shown.Find(_eyeSelectedId) is null) _eyeSelectedId = "";   // the selected thing left the picture
        if (_eyeSelectedEdge.Length > 0 && EyeSelectedEdge is null) _eyeSelectedEdge = "";                        // the selected link left it
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
        Raise(nameof(EyeSelectedMark));
        Raise(nameof(EyeCanOpen));
        Raise(nameof(EyeOpenWords));
        Raise(nameof(EyeContacts));
        Raise(nameof(EyeCanCluster));
        Raise(nameof(EyeClusterWords));
        _eyeLinksSeen = EyeLinksWords();
        Raise(nameof(EyeLinks));
        Raise(nameof(HasEyeLinks));
        Raise(nameof(EyeSelectedEdgeId));
        Raise(nameof(EyeSelectedEdge));
        Raise(nameof(HasEyeEdgeSelection));
        Raise(nameof(EyeEdgeTitle));
        Raise(nameof(EyeEdgeMark));
        Raise(nameof(EyeEdgeHue));
        Raise(nameof(EyeEdgeWords));
        Raise(nameof(EyeEdgeSource));
        Raise(nameof(EyeEdgeEnds));
        Raise(nameof(EyeNothingSelected));
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
        EyeKind.ShowApp => "show app",
        EyeKind.ShowAppItem => "another app's",
        _ => "stream",
    };
}
