using Patterns.Assistant;
using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>A proposal as a chip on the Assistant page: its kind, its words, the parts it carries, and APPLY once.</summary>
public sealed class AssistantChip : Observable
{
    private bool _applied;
    private string _appliedText = "";

    public AssistantChip(AssistantPage page, AssistantProposal proposal)
    {
        Proposal = proposal;
        ApplyCommand = new RelayCommand(() => page.ApplyProposal(this));
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
public sealed class AssistantRow : Observable
{
    private bool _latest;

    public AssistantRow(bool mine, string text, IReadOnlyList<string> questions, IReadOnlyList<AssistantChip> chips, bool declined = false, bool note = false)
    {
        IsMine = mine;
        Text = text;
        Questions = questions;
        Chips = chips;
        IsDeclined = declined;
        IsNote = note;
    }

    /// <summary>The newest row — the one under the ask box, lit so the eye lands on the answer.</summary>
    public bool IsLatest { get => _latest; set => Set(ref _latest, value); }

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

/// <summary>A file attached to the next ask, as its chip on the page: the label and its ✕.</summary>
public sealed class AssistantAttachmentChip
{
    public AssistantAttachmentChip(AssistantPage page, AssistantAttachment attachment)
    {
        Attachment = attachment;
        RemoveCommand = new RelayCommand(() => page.RemoveAttachment(this));
    }

    public AssistantAttachment Attachment { get; }
    public string Label => Attachment.Label;
    public RelayCommand RemoveCommand { get; }
}

/// <summary>
/// The Assistant page: the key, the conversation, the files that ride with an ask, and the
/// proposals applied. A page of its own — the section's DataContext is this object — that asks
/// the desk for two things only: what is being edited when a question goes, and the lists a
/// proposal changes once it has been applied.
/// </summary>
public sealed class AssistantPage : Observable
{
    private static readonly FilePickerFileType AttachTypes = new("Pictures, PDF, text, spreadsheets, Word & PowerPoint")
    {
        Patterns = Patterns.Assistant.AssistantAttachments.AllExtensions.Select(e => "*" + e).ToArray(),
    };

    private readonly AppServices _services;
    private readonly MainViewModel _desk;
    private string _input = "";
    private string _status = "";
    private string _keyDraft = "";
    private bool _busy;
    private RelayCommand? _ask;
    private RelayCommand? _saveKey;
    private RelayCommand? _forgetKey;
    private RelayCommand? _clear;
    private RelayCommand? _attachFiles;
    private RelayCommand<string>? _starter;

    public AssistantPage(MainViewModel desk, AppServices services)
    {
        _desk = desk;
        _services = services;
    }

    /// <summary>The files that ride with the next ask — a screenshot, a brief, a running order, a mixture — as chips until the ask goes.</summary>
    public ObservableCollection<AssistantAttachmentChip> Attachments { get; } = new();

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>"2 files ride with the next ask — a picture and a table."</summary>
    public string AttachmentsText => Attachments.Count == 0 ? ""
        : $"{Attachments.Count} file{(Attachments.Count == 1 ? "" : "s")} ride{(Attachments.Count == 1 ? "s" : "")} with the next ask: {string.Join(", ", Attachments.Select(c => c.Attachment.KindWord))}. Ask for a plan, or press ASK with nothing typed.";

    /// <summary>ATTACH…: pictures, PDFs, text, spreadsheets, Word and PowerPoint files, read now and sent with the next ask.</summary>
    public RelayCommand AttachFilesCommand => _attachFiles ??= new RelayCommand(() => _ = AttachFilesAsync());

    private async Task AttachFilesAsync()
    {
        var window = _services.MainWindow;
        if (window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach to the assistant's next ask",
                AllowMultiple = true,
                FileTypeFilter = new[] { AttachTypes, FilePickerFileTypes.All },
            });
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path is not null) AddAttachment(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Assistant attach picker failed.", ex);
            Status = "The file picker could not open: " + ex.Message;
        }
    }

    /// <summary>A file onto the next ask, read now (a picture reduced, a sheet read as a table); a file that cannot be read says why on the status line. Tests call this without a picker.</summary>
    public bool AddAttachment(string path)
    {
        if (Attachments.Count >= Patterns.Assistant.AssistantAttachments.MaxAttachments)
        {
            Status = $"At most {Patterns.Assistant.AssistantAttachments.MaxAttachments} files ride with one ask.";
            return false;
        }
        var result = Patterns.Assistant.AssistantAttachments.Read(path);
        if (result.Attachment is null)
        {
            Status = result.Refusal;
            return false;
        }
        Attachments.Add(new AssistantAttachmentChip(this, result.Attachment));
        RaiseAttachments();
        Status = $"Attached {result.Attachment.Label}.";
        return true;
    }

    public void RemoveAttachment(AssistantAttachmentChip chip)
    {
        if (Attachments.Remove(chip)) RaiseAttachments();
    }

    private void RaiseAttachments()
    {
        Raise(nameof(HasAttachments));
        Raise(nameof(AttachmentsText));
    }

    /// <summary>The conversation this session, newest first — the latest answer sits under the ask box, the history runs down the page.</summary>
    public ObservableCollection<AssistantRow> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    /// <summary>The ask box.</summary>
    public string Input { get => _input; set => Set(ref _input, value ?? ""); }

    /// <summary>The page's status line: what the last ask came back with, or why it did not go.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) Raise(nameof(CanAsk));
        }
    }

    public bool CanAsk => !_busy;

    public bool HasKey => _services.Assistant.HasKey;

    /// <summary>The KEY block's line: saved and masked, or how to switch the assistant on.</summary>
    public string KeyText => HasKey
        ? $"Key saved on this machine ({_services.Assistant.KeyMasked}) — beside the settings file, never inside a show file."
        : "No key saved — the assistant is off and nothing leaves this machine. Paste an Anthropic API key to switch it on.";

    /// <summary>The paste box; cleared once saved so the key never sits on screen.</summary>
    public string KeyDraft { get => _keyDraft; set => Set(ref _keyDraft, value ?? ""); }

    /// <summary>One-press starts for a first conversation.</summary>
    public IReadOnlyList<string> Starters { get; } = new[]
    {
        "Work out a plan from what I have attached",
        "Plan a show: two screens, a walk-in look, a keynote and a break",
        "A walk-in look with the clock and a welcome message",
        "A lower third for the keynote speaker",
        "Cues for a conference morning: doors, walk-in, welcome, keynote, break",
        "What can you help me build?",
    };

    public RelayCommand AskCommand => _ask ??= new RelayCommand(() => _ = AskAsync(Input));

    public RelayCommand<string> StarterCommand => _starter ??= new RelayCommand<string>(words =>
    {
        if (words is null) return;
        Input = words;
        _ = AskAsync(words);
    });

    public RelayCommand SaveKeyCommand => _saveKey ??= new RelayCommand(() =>
    {
        var key = AssistantKey.Normalise(KeyDraft);
        if (key.Length == 0)
        {
            Status = "Paste a key first — an Anthropic API key from your own account.";
            return;
        }
        _services.Assistant.SaveKey(key);
        KeyDraft = "";
        RaiseKey();
        Status = "Key saved. Ask away — the assistant sees the show's names and counts, never its files, addresses or keys.";
        _desk.StatusMessage = "Assistant key saved beside the settings file — FORGET takes it off this machine.";
    });

    public RelayCommand ForgetKeyCommand => _forgetKey ??= new RelayCommand(() =>
    {
        _services.Assistant.ForgetKey();
        RaiseKey();
        Status = "Key forgotten — the assistant is off on this machine.";
    });

    public RelayCommand ClearCommand => _clear ??= new RelayCommand(() =>
    {
        _services.Assistant.Clear();
        Rows.Clear();
        Raise(nameof(HasRows));
        Status = "Conversation cleared — the next ask starts afresh.";
    });

    private void RaiseKey()
    {
        Raise(nameof(HasKey));
        Raise(nameof(KeyText));
    }

    private void AddRow(AssistantRow row)
    {
        foreach (var r in Rows) r.IsLatest = false;
        row.IsLatest = true;
        Rows.Insert(0, row);   // newest at the top
        if (Rows.Count == 1) Raise(nameof(HasRows));
    }

    /// <summary>One ask: the question on the page at once with the files attached, the answer when it lands, every failure a row in words.</summary>
    public async Task AskAsync(string? text)
    {
        var question = (text ?? "").Trim();
        var files = Attachments.Select(c => c.Attachment).ToList();
        if (question.Length == 0 && files.Count > 0) question = "Read what I have attached and work out a plan for the show from it.";
        if (question.Length == 0)
        {
            Status = "Type a question, or what you want built — or attach a file and press ASK.";
            return;
        }
        if (Busy) return;
        Busy = true;
        Input = "";
        var shown = files.Count == 0 ? question : question + "\n📎 " + string.Join(" · ", files.Select(f => f.Label));
        AddRow(new AssistantRow(true, shown, Array.Empty<string>(), Array.Empty<AssistantChip>()));
        Status = HasKey ? "Asking…" : "";
        try
        {
            var target = _desk.EditTarget;
            _services.Assistant.EditingTarget = target.ScreenId is null ? "Program" : $"{target.Label} (its own picture)";
            var answer = await _services.Assistant.AskAsync(question, files);
            if (answer.Sent && files.Count > 0)
            {
                Attachments.Clear();   // sent: the chips go; the turn keeps them for the conversation
                RaiseAttachments();
            }
            Status = answer.Status;
            if (answer.Reply is { } reply)
            {
                var chips = reply.Proposals.Select(p => new AssistantChip(this, p)).ToList();
                AddRow(new AssistantRow(false, reply.Reply, reply.Questions, chips, declined: !reply.InScope));
            }
            else
            {
                AddRow(new AssistantRow(false, answer.Status, Array.Empty<string>(), Array.Empty<AssistantChip>(), note: true));
            }
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>A proposal that draws — a pattern, overlays, a brand the patterns use, a look's picture — as against one that only adds to the lists (screens, designs, cues).</summary>
    public static bool ProposalDraws(AssistantProposal p)
        => p.Pattern is not null || p.Overlays is not null || p.Brand is not null || p.Looks.Count > 0;

    /// <summary>
    /// APPLY: the proposal into the show through Core, in one publish, and the desk's lists told.
    /// The assistant designs in the preview and only there: a proposal that draws opens EDIT SAFE
    /// when it is off, so the program on air stays what it is until TAKE or CUT, and lands on the
    /// program's own pattern (the PGM pane), never on a screen's own picture — the editing target
    /// comes back to Program first. Lists (screens, designs, cues) need no preview and open none.
    /// </summary>
    public void ApplyProposal(AssistantChip chip)
    {
        if (chip.IsApplied || !chip.Proposal.CanApply) return;
        var p = chip.Proposal;
        var draws = ProposalDraws(p);
        var opened = false;
        if (draws)
        {
            if (!_desk.IsSandboxActive)
            {
                _desk.IsSandboxActive = true;
                opened = true;
            }
            if (_desk.EditTarget.ScreenId is not null) _desk.EditTarget = _desk.EditTargets[0];
        }
        var report = new ApplyReport();
        _services.BulkEdit(() => report = AssistantApply.Apply(_services.State, p));
        _desk.AfterAssistantApplied(p);
        var where = draws
            ? (opened ? " — in the preview (EDIT SAFE opened): TAKE or CUT puts it on air." : " — in the preview: TAKE or CUT puts it on air.")
            : "";
        chip.MarkApplied(report.Summary + where);
        Status = "Applied: " + report.Summary + where;
        _desk.StatusMessage = "Applied: " + report.Summary + where;
    }
}
