using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- the assistant: a page of its own (AssistantPage); the desk answers for its lists ----------

    /// <summary>The Assistant page — the section's own DataContext.</summary>
    public AssistantPage Assistant { get; }

    /// <summary>A proposal has been applied to the show: the desk's lists that read what it added.</summary>
    internal void AfterAssistantApplied(AssistantProposal p)
    {
        if (p.Screens.Count > 0)
        {
            _services.Screens.Refresh();
            RebuildEditTargets();
            RaiseModeChanged();
        }
        if (p.Looks.Count > 0) Show.RaiseLookNames();
        if (p.LowerThirds.Count > 0)
        {
            SelectedLowerThird = State.LowerThirds.Designs.LastOrDefault();
            RefreshLowerThirdTallies();
        }
    }
}
