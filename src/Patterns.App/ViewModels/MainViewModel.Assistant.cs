using System.Collections.ObjectModel;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>A proposal as a chip on the Assistant page: its kind, its words, the parts it carries, and APPLY once.</summary>
public sealed class AssistantChip : Observable
{
    private bool _applied;
    private string _appliedText = "";

    public AssistantChip(MainViewModel vm, AssistantProposal proposal)
    {
        Proposal = proposal;
        ApplyCommand = new RelayCommand(() => vm.ApplyAssistantProposal(this));
    }

    public AssistantProposal Proposal { get; }

    public string Kind => Proposal.KindLabel;
    public string Title => Proposal.Title;
    public string Summary => Proposal.Summary;
    public bool HasSummary => Proposal.Summary.Length > 0;

    /// <summary>"2 screens · brand · overlays · looks: Walk-in, Keynote · lower thirds: 1 · cues: 3".</summary>
    public string Detail
    {
        get
        {
            var p = Proposal;
            var parts = new List<string>();
            if (p.Screens.Count > 0) parts.Add(p.Screens.Count == 1 ? $"screen: {p.Screens[0].Label}" : $"{p.Screens.Count} screens: {string.Join(", ", p.Screens.Select(s => s.Label))}");
            if (p.Brand is not null) parts.Add("brand");
            if (p.Overlays is not null) parts.Add("overlays");
            if (p.Pattern is { } pat) parts.Add($"pattern {pat.Kind}");
            if (p.Looks.Count > 0) parts.Add((p.Looks.Count == 1 ? "look: " : "looks: ") + string.Join(", ", p.Looks.Select(l => l.Name)));
            if (p.LowerThirds.Count > 0) parts.Add((p.LowerThirds.Count == 1 ? "lower third: " : "lower thirds: ") + string.Join(", ", p.LowerThirds.Select(l => l.Name)));
            if (p.Cues.Count > 0) parts.Add(p.Cues.Count == 1 ? $"cue: {p.Cues[0].Name}" : $"{p.Cues.Count} cues: {string.Join(", ", p.Cues.Select(c => c.Name))}");
            return string.Join(" · ", parts);
        }
    }

    public bool HasDetail => Detail.Length > 0;
    public bool HasSteps => Proposal.Steps.Count > 0;
    public string StepsText => string.Join("\n", Proposal.Steps.Select((s, i) => $"{i + 1}. {s}"));

    /// <summary>APPLY shows while there is something to apply and it has not been applied yet.</summary>
    public bool CanApply => Proposal.CanApply && !_applied;

    public bool IsApplied
    {
        get => _applied;
        private set
        {
            if (Set(ref _applied, value)) Raise(nameof(CanApply));
        }
    }

    public string AppliedText { get => _appliedText; private set => Set(ref _appliedText, value); }

    public RelayCommand ApplyCommand { get; }

    public void MarkApplied(string words)
    {
        AppliedText = words;
        IsApplied = true;
    }
}

/// <summary>One row of the conversation: the operator's words, or the assistant's with its questions and proposals.</summary>
public sealed class AssistantRow
{
    public AssistantRow(bool mine, string text, IReadOnlyList<string> questions, IReadOnlyList<AssistantChip> chips, bool declined = false, bool note = false)
    {
        IsMine = mine;
        Text = text;
        Questions = questions;
        Chips = chips;
        IsDeclined = declined;
        IsNote = note;
    }

    public bool IsMine { get; }
    public string Who => IsMine ? "YOU" : IsNote ? "PATTERNS" : "ASSISTANT";
    public string Text { get; }
    public IReadOnlyList<string> Questions { get; }
    public bool HasQuestions => Questions.Count > 0;
    public string QuestionsText => string.Join("\n", Questions.Select(q => "• " + q));
    public IReadOnlyList<AssistantChip> Chips { get; }
    public bool HasChips => Chips.Count > 0;

    /// <summary>The assistant declined (out of scope, or a probe stopped on this side).</summary>
    public bool IsDeclined { get; }

    /// <summary>A line from Patterns itself — no key, a failure — not from the model.</summary>
    public bool IsNote { get; }
}

public sealed partial class MainViewModel
{
    // ---- the assistant: the key, the conversation, the proposals applied ---------------------

    private string _assistantInput = "";
    private string _assistantStatus = "";
    private string _assistantKeyDraft = "";
    private bool _assistantBusy;
    private RelayCommand? _askAssistant;
    private RelayCommand? _saveAssistantKey;
    private RelayCommand? _forgetAssistantKey;
    private RelayCommand? _clearAssistant;
    private RelayCommand<string>? _assistantStarter;

    /// <summary>The conversation this session, oldest first.</summary>
    public ObservableCollection<AssistantRow> AssistantRows { get; } = new();

    public bool HasAssistantRows => AssistantRows.Count > 0;

    /// <summary>The ask box.</summary>
    public string AssistantInput { get => _assistantInput; set => Set(ref _assistantInput, value ?? ""); }

    /// <summary>The page's status line: what the last ask came back with, or why it did not go.</summary>
    public string AssistantStatus { get => _assistantStatus; private set => Set(ref _assistantStatus, value); }

    public bool AssistantBusy
    {
        get => _assistantBusy;
        private set
        {
            if (Set(ref _assistantBusy, value)) Raise(nameof(CanAskAssistant));
        }
    }

    public bool CanAskAssistant => !_assistantBusy;

    public bool HasAssistantKey => _services.Assistant.HasKey;

    /// <summary>The KEY block's line: saved and masked, or how to switch the assistant on.</summary>
    public string AssistantKeyText => HasAssistantKey
        ? $"Key saved on this machine ({_services.Assistant.KeyMasked}) — beside the settings file, never inside a show file."
        : "No key saved — the assistant is off and nothing leaves this machine. Paste an Anthropic API key to switch it on.";

    /// <summary>The paste box; cleared once saved so the key never sits on screen.</summary>
    public string AssistantKeyDraft { get => _assistantKeyDraft; set => Set(ref _assistantKeyDraft, value ?? ""); }

    /// <summary>One-press starts for a first conversation.</summary>
    public IReadOnlyList<string> AssistantStarters { get; } = new[]
    {
        "Plan a show: two screens, a walk-in look, a keynote and a break",
        "A walk-in look with the clock and a welcome message",
        "A lower third for the keynote speaker",
        "Cues for a conference morning: doors, walk-in, welcome, keynote, break",
        "What can you help me build?",
    };

    public RelayCommand AskAssistantCommand => _askAssistant ??= new RelayCommand(() => _ = AskAssistantAsync(AssistantInput));

    public RelayCommand<string> AssistantStarterCommand => _assistantStarter ??= new RelayCommand<string>(words =>
    {
        if (words is null) return;
        AssistantInput = words;
        _ = AskAssistantAsync(words);
    });

    public RelayCommand SaveAssistantKeyCommand => _saveAssistantKey ??= new RelayCommand(() =>
    {
        var key = AssistantKey.Normalise(AssistantKeyDraft);
        if (key.Length == 0)
        {
            AssistantStatus = "Paste a key first — an Anthropic API key from your own account.";
            return;
        }
        _services.Assistant.SaveKey(key);
        AssistantKeyDraft = "";
        RaiseAssistantKey();
        AssistantStatus = "Key saved. Ask away — the assistant sees the show's names and counts, never its files, addresses or keys.";
        StatusMessage = "Assistant key saved beside the settings file — FORGET takes it off this machine.";
    });

    public RelayCommand ForgetAssistantKeyCommand => _forgetAssistantKey ??= new RelayCommand(() =>
    {
        _services.Assistant.ForgetKey();
        RaiseAssistantKey();
        AssistantStatus = "Key forgotten — the assistant is off on this machine.";
    });

    public RelayCommand ClearAssistantCommand => _clearAssistant ??= new RelayCommand(() =>
    {
        _services.Assistant.Clear();
        AssistantRows.Clear();
        Raise(nameof(HasAssistantRows));
        AssistantStatus = "Conversation cleared — the next ask starts afresh.";
    });

    private void RaiseAssistantKey()
    {
        Raise(nameof(HasAssistantKey));
        Raise(nameof(AssistantKeyText));
    }

    private void AddAssistantRow(AssistantRow row)
    {
        AssistantRows.Add(row);
        if (AssistantRows.Count == 1) Raise(nameof(HasAssistantRows));
    }

    /// <summary>One ask: the question on the page at once, the answer when it lands, every failure a row in words.</summary>
    public async Task AskAssistantAsync(string? text)
    {
        var question = (text ?? "").Trim();
        if (question.Length == 0)
        {
            AssistantStatus = "Type a question, or what you want built.";
            return;
        }
        if (AssistantBusy) return;
        AssistantBusy = true;
        AssistantInput = "";
        AddAssistantRow(new AssistantRow(true, question, Array.Empty<string>(), Array.Empty<AssistantChip>()));
        AssistantStatus = HasAssistantKey ? "Asking…" : "";
        try
        {
            var answer = await _services.Assistant.AskAsync(question);
            AssistantStatus = answer.Status;
            if (answer.Reply is { } reply)
            {
                var chips = reply.Proposals.Select(p => new AssistantChip(this, p)).ToList();
                AddAssistantRow(new AssistantRow(false, reply.Reply, reply.Questions, chips, declined: !reply.InScope));
            }
            else
            {
                AddAssistantRow(new AssistantRow(false, answer.Status, Array.Empty<string>(), Array.Empty<AssistantChip>(), note: true));
            }
        }
        finally
        {
            AssistantBusy = false;
        }
    }

    /// <summary>APPLY: the proposal into the show through Core, in one publish, and the desk's lists told.</summary>
    public void ApplyAssistantProposal(AssistantChip chip)
    {
        if (chip.IsApplied || !chip.Proposal.CanApply) return;
        var report = new ApplyReport();
        _services.BulkEdit(() => report = AssistantApply.Apply(State, chip.Proposal));
        var p = chip.Proposal;
        if (p.Screens.Count > 0)
        {
            _services.Screens.Refresh();
            RebuildEditTargets();
            RaiseModeChanged();
        }
        if (p.Looks.Count > 0) Raise(nameof(LookNames));
        if (p.LowerThirds.Count > 0)
        {
            var lowers = State.LowerThirds;
            if (lowers.Designs.Count > 0 && (lowers.DefaultDesignId.Length == 0 || lowers.Find(lowers.DefaultDesignId) is null)) lowers.DefaultDesignId = lowers.Designs[0].Id;
            SelectedLowerThird = lowers.Designs.LastOrDefault();
            RefreshLowerThirdTallies();
        }
        chip.MarkApplied(report.Summary);
        AssistantStatus = "Applied: " + report.Summary;
        StatusMessage = "Applied: " + report.Summary;
    }
}
