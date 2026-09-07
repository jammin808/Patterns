using Patterns.Core.Model;

namespace Patterns.App.ViewModels;

/// <summary>
/// What the pop-out settings column beside the page shows: a key the host builds a panel for
/// ("cue", "screen", "element"), a title, and the identity of the selection it is showing, so
/// a column the operator closed stays closed until something else is selected.
/// </summary>
public sealed class PopOutState : Observable
{
    private string _key = "";
    private string _title = "";
    private string _hue = "";
    private string _identity = "";
    private string _dismissed = "";

    public string Key { get => _key; private set => Set(ref _key, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }

    /// <summary>The page's hue class ("hue-cues"), so the column's bands wear the page's neon.</summary>
    public string Hue { get => _hue; private set => Set(ref _hue, value); }

    /// <summary>The selection on show ("cue:<id>"), so a re-selection of the same item is not a change.</summary>
    public string Identity => _identity;

    public bool IsOpen => _key.Length > 0;

    /// <summary>The selection the operator closed the column on; the column stays closed for it until the selection moves.</summary>
    public string DismissedIdentity => _dismissed;

    public void Show(string key, string title, string identity, string hue)
    {
        var wasOpen = IsOpen;
        _identity = identity;
        Hue = hue;
        Title = title;
        Key = key;
        if (wasOpen != IsOpen) Raise(nameof(IsOpen));
    }

    /// <summary>The selection or the page went: the column closes; a closed-by-hand mark is kept for its selection.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        _identity = "";
        Title = "";
        Key = "";
        Raise(nameof(IsOpen));
    }

    /// <summary>◀ CLOSE: closed for this selection until another is made, or SETTINGS ▸.</summary>
    public void Dismiss()
    {
        _dismissed = _identity;
        Close();
    }

    /// <summary>SETTINGS ▸ on the page: forget the closing, the next refresh opens it.</summary>
    public void ClearDismissal() => _dismissed = "";
}

public sealed partial class MainViewModel
{
    // ---- the pop-out settings column beside the page -------------------------------------

    private RelayCommand? _openPopOut;
    private RelayCommand? _closePopOut;

    /// <summary>The pop-out column's state: what it shows, if anything.</summary>
    public PopOutState PopOut { get; } = new();

    /// <summary>The line on a page whose settings moved to the column.</summary>
    public string PopOutHint => "The settings are in the column beside this page — the list keeps its room here. ◀ CLOSE hides the column; SETTINGS ▸ brings it back.";

    /// <summary>SETTINGS ▸ on the page: the column for the current selection, closed by hand or not.</summary>
    public RelayCommand OpenPopOutCommand => _openPopOut ??= new RelayCommand(() =>
    {
        PopOut.ClearDismissal();
        RefreshPopOut();
    });

    /// <summary>◀ CLOSE on the column.</summary>
    public RelayCommand ClosePopOutCommand => _closePopOut ??= new RelayCommand(() => PopOut.Dismiss());

    /// <summary>
    /// The column follows the page and its selection: the Cues page with a cue selected shows the
    /// cue's settings, the Screens page with a screen the screen's, the Lower thirds page with an
    /// element the element's; any other page, no selection, or the Run layout closes it. Called
    /// on every page switch and every change of those selections.
    /// </summary>
    public void RefreshPopOut()
    {
        (string Key, string Title, string Identity)? want = null;
        var header = Shell.Pages[_page].Header;
        if (!_isRunLayout)
        {
            if (header == "Cues" && Cues.SelectedCue is { } cue)
                want = ("cue", $"SELECTED CUE · {cue.Number} {cue.Name}".TrimEnd(), "cue:" + cue.Id);
            else if (header == "Screens" && HasSelection && SelectedPlacement is { } placement)
                want = ("screen", "SELECTED SCREEN · " + SelectedScreenTitle, "screen:" + placement.ScreenId);
            else if (header == "Lower thirds" && SelectedElement is { } element)
                want = ("element", "SELECTED ELEMENT · " + (element.Name.Length > 0 ? element.Name : element.Kind.ToString()), "element:" + element.Id);
        }
        if (want is null)
        {
            PopOut.Close();
            return;
        }
        var (key, title, identity) = want.Value;
        if (PopOut.DismissedIdentity == identity) return;   // closed by hand for this selection
        PopOut.Show(key, title, identity, Shell.HueClass(header));
    }
}
