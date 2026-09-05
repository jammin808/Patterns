using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>A section chip on the Help page: ALL, or one group of the catalogue.</summary>
public sealed class HelpGroupChip : Observable
{
    private bool _isCurrent;

    public HelpGroupChip(MainViewModel vm, HelpGroup? group)
    {
        Group = group;
        Label = group is { } g ? HelpTopics.GroupLabel(g) : "ALL";
        Blurb = group is { } b ? HelpTopics.GroupBlurb(b) : "every topic, in the order a show happens.";
        PickCommand = new RelayCommand(() => vm.HelpGroupFilter = group);
    }

    public HelpGroup? Group { get; }
    public string Label { get; }
    public string Blurb { get; }
    public RelayCommand PickCommand { get; }

    public bool IsCurrent { get => _isCurrent; private set => Set(ref _isCurrent, value); }

    public void Refresh(bool current) => IsCurrent = current;
}

/// <summary>A page a topic lives on: GO opens it.</summary>
public sealed class HelpPageLink
{
    public HelpPageLink(MainViewModel vm, string header)
    {
        Header = header;
        GoCommand = new RelayCommand(() =>
        {
            var page = Shell.Pages.FirstOrDefault(p => p.Header == header);
            if (page is not null) vm.SelectPage(page.Index);
        });
    }

    public string Header { get; }
    public RelayCommand GoCommand { get; }
}

/// <summary>
/// One topic of the catalogue as a card: closed, its title and its place in the workflow; open,
/// how it works, what to do in order, the words on the wire and the pages it lives on. A search
/// opens the cards it finds and shows the words around the match.
/// </summary>
public sealed class HelpRow : Observable
{
    private bool _isOpen;
    private string _snippet = "";

    public HelpRow(MainViewModel vm, HelpTopic topic)
    {
        Topic = topic;
        GroupLabel = HelpTopics.GroupLabel(topic.Group);
        StepsText = string.Join("\n", topic.Steps.Select((s, i) => $"{i + 1}.  {s}"));
        Pages = topic.Pages.Select(p => new HelpPageLink(vm, p)).ToList();
        ToggleCommand = new RelayCommand(() => IsOpen = !IsOpen);
    }

    public HelpTopic Topic { get; }
    public string Id => Topic.Id;
    public string Title => Topic.Title;
    public string GroupLabel { get; }
    public string Where => Topic.Where;
    public string Body => Topic.Body;
    public string StepsText { get; }
    public bool HasSteps => Topic.HasSteps;
    public string Wire => Topic.Wire;
    public bool HasWire => Topic.HasWire;
    public IReadOnlyList<HelpPageLink> Pages { get; }
    public bool HasPages => Pages.Count > 0;
    public RelayCommand ToggleCommand { get; }

    /// <summary>The whole card, or just the title and its place.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (Set(ref _isOpen, value)) Raise(nameof(OpenText));
        }
    }

    public string OpenText => IsOpen ? "▾" : "▸";

    /// <summary>The words around a search match; empty outside a search.</summary>
    public string Snippet
    {
        get => _snippet;
        set
        {
            if (Set(ref _snippet, value ?? "")) Raise(nameof(HasSnippet));
        }
    }

    public bool HasSnippet => _snippet.Length > 0;
}
