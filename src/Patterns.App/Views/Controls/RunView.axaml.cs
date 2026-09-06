using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;

namespace Patterns.App.Views.Controls;

/// <summary>
/// The Run layout: what a show caller reads and presses. The divider between the wall and the
/// cue stack is the show's (<see cref="DeskLayoutConfig.RunCueShare"/>): laid out from it here,
/// written back when the caller drags it, and followed by every Run surface at once — the main
/// window's and the pop-out's.
/// </summary>
public partial class RunView : UserControl
{
    private DeskLayoutConfig? _desk;
    private bool _applying;

    public RunView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        AttachedToVisualTree += (_, _) => Hook();
    }

    private void Hook()
    {
        if (DataContext is not MainViewModel vm) return;
        var desk = vm.State.Desk;
        if (!ReferenceEquals(_desk, desk))
        {
            _desk = desk;
            desk.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeskLayoutConfig.RunCueShare)) ApplyShare();
            };
        }
        ApplyShare();
    }

    /// <summary>The cue stack's share of the Run area as the columns carry it now (their star weights).</summary>
    public double CueShareApplied
    {
        get
        {
            var columns = RunSplit.ColumnDefinitions;
            var wall = columns[0].Width.IsStar ? columns[0].Width.Value : 0;
            var cue = columns[2].Width.IsStar ? columns[2].Width.Value : 0;
            return wall + cue > 0 ? cue / (wall + cue) : 0;
        }
    }

    /// <summary>Lays the two columns out from the show's share. Idempotent; a fault here is contained and the columns stay as they were.</summary>
    public void ApplyShare()
    {
        if (_desk is null || _applying) return;
        _applying = true;
        try
        {
            var share = _desk.RunCueShare;
            if (double.IsNaN(share) || share <= 0 || share >= 1) share = DeskLayoutConfig.DefaultRunCueShare;
            var columns = RunSplit.ColumnDefinitions;
            columns[0].Width = new GridLength(1 - share, GridUnitType.Star);
            columns[2].Width = new GridLength(share, GridUnitType.Star);
        }
        catch (Exception ex) when (!UiFaults.IsFatal(ex))
        {
            UiFaults.Contain(ex, "the Run layout");
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>The cue stack's share, as a drag of the divider sets it; remembered in the show (clamped there).</summary>
    public void SetCueShare(double share)
    {
        if (_desk is null) return;
        _desk.RunCueShare = share;   // clamps; the change event re-applies
        ApplyShare();
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        var columns = RunSplit.ColumnDefinitions;
        var wall = columns[0].ActualWidth;
        var cue = columns[2].ActualWidth;
        if (wall + cue > 0) SetCueShare(cue / (wall + cue));
    }
}
