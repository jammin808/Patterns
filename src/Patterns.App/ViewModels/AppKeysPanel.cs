using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// One app key's row on the Nodes page (Show Bus 1.3 §13.16.5): its kid, its words — the connector it speaks for, its scope, how and
/// when it was made, its last link, and the mark when it was seen in clear — the light's shape beside its colour, and the facts its
/// right-click menu reads. Never the key.
/// </summary>
public sealed record AppKeyRow(string Kid, string Bound, string Scope, string Made, bool ClearSeen, string Words)
{
    /// <summary>✓ a key in force; △ amber once it was seen in clear (replace it).</summary>
    public string Mark => ClearSeen ? "△" : "✓";

    /// <summary>The row's light: green, or amber while it was seen in clear.</summary>
    public string Hue => ClearSeen ? "#FFB020" : "#46D27A";

    /// <summary>The key as its menu reads it.</summary>
    public Patterns.Core.Menus.MenuAppKey Menu => new(Kid, Bound, Scope, Made, ClearSeen);
}

/// <summary>
/// Show Bus 1.3 §13.5 and §13.16.5–6 on the Nodes page: the acceptor's app keys and its three operator actions — <i>Make key for…</i>
/// (KEY MAKE: a connector's kind:id and a scope), <i>Let this key speak for &lt;node&gt;</i> (KEY BIND) and <i>Revoke key</i> (KEY
/// REVOKE) — each a press of the one show action from the desk, so a row's button and its right-click entry are the same verb and one
/// journal row. A key made is shown here once (<see cref="Shown"/>), from the store's one-time hand-over, and is gone from the desk
/// when it is hidden; nothing else ever shows it. The rows follow the kept keys at the desk's tick.
/// </summary>
public sealed class AppKeysPanel : Observable
{
    private readonly AppKeyStore _keys;
    private readonly Func<string> _token;
    private readonly Func<ShowAction, ActionResult> _press;
    private string _seen = "";
    private string _makeFor = "";
    private string _makeScope = "follow";
    private string _bindKid = "";
    private string _bindFor = "";
    private string _bindScope = "";
    private string _shown = "";
    private string _shownWords = "";

    /// <param name="keys">The kept keys.</param>
    /// <param name="token">The pairing token as it is now: the keys enrolled from it are in force with it.</param>
    /// <param name="press">Runs one KEY verb from the desk; its words to the strip.</param>
    public AppKeysPanel(AppKeyStore keys, Func<string> token, Func<ShowAction, ActionResult> press)
    {
        _keys = keys;
        _token = token;
        _press = press;
        Look();
    }

    /// <summary>The keys in force, a row each.</summary>
    public ObservableCollection<AppKeyRow> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    /// <summary>13.5.3's ladder, for the scope boxes.</summary>
    public IReadOnlyList<string> Scopes => LinkAcceptor.Scopes;

    /// <summary>How the keys are kept at rest, in words.</summary>
    public string Protection => _keys.Protection;

    /// <summary>The connector a new key is made for, typed as kind:id (its hello's kind and id).</summary>
    public string MakeFor { get => _makeFor; set => Set(ref _makeFor, value ?? ""); }

    /// <summary>The new key's scope: follow unless the operator picks more.</summary>
    public string MakeScope { get => _makeScope; set => Set(ref _makeScope, value ?? "follow"); }

    /// <summary>The kid BIND moves, set by a row's Bind… press or its menu entry.</summary>
    public string BindKid { get => _bindKid; set => Set(ref _bindKid, value ?? ""); }

    /// <summary>The connector BIND lets the key speak for, typed as kind:id.</summary>
    public string BindFor { get => _bindFor; set => Set(ref _bindFor, value ?? ""); }

    /// <summary>The scope BIND sets; empty keeps the key's own.</summary>
    public string BindScope { get => _bindScope; set => Set(ref _bindScope, value ?? ""); }

    /// <summary>The key KEY MAKE made, shown once until HIDE; "" otherwise.</summary>
    public string Shown { get => _shown; private set { if (Set(ref _shown, value)) Raise(nameof(HasShown)); } }

    public bool HasShown => _shown.Length > 0;

    /// <summary>What the shown key is for, beside it.</summary>
    public string ShownWords { get => _shownWords; private set => Set(ref _shownWords, value); }

    private RelayCommand? _make;

    /// <summary>MAKE KEY: KEY MAKE for the connector typed, at the scope picked; the key is shown here once.</summary>
    public RelayCommand MakeCommand => _make ??= new RelayCommand(() =>
    {
        var result = _press(new ShowAction(ShowActionKind.AppKeyMake, MakeFor.Trim(), MakeScope));
        if (result.Ok && _keys.TakeShown() is { } key)
        {
            Shown = key;
            ShownWords = $"For {AppKeys.ConnectorWords(AppKeys.Connector(MakeFor) ?? MakeFor)} only, scope {MakeScope} — copy it into that app now: it is shown here once and kept nowhere, and HIDE forgets it.";
            MakeFor = "";
        }
        Look();
    });

    private RelayCommand? _hide;

    /// <summary>HIDE: the shown key leaves the desk; it is never shown again.</summary>
    public RelayCommand HideShownCommand => _hide ??= new RelayCommand(() =>
    {
        Shown = "";
        ShownWords = "";
    });

    private RelayCommand<AppKeyRow>? _revoke;

    /// <summary>REVOKE on a row: KEY REVOKE, its live links refused at once.</summary>
    public RelayCommand<AppKeyRow> RevokeCommand => _revoke ??= new RelayCommand<AppKeyRow>(row =>
    {
        if (row is null) return;
        _press(new ShowAction(ShowActionKind.AppKeyRevoke, row.Kid));
        Look();
    });

    private RelayCommand<AppKeyRow>? _pickBind;

    /// <summary>BIND… on a row: that key into the BIND boxes, its connector and scope beside it to change.</summary>
    public RelayCommand<AppKeyRow> PickBindCommand => _pickBind ??= new RelayCommand<AppKeyRow>(row =>
    {
        if (row is null) return;
        Pick(row.Kid);
    });

    /// <summary>The key a row's press or a menu's route named, into the BIND boxes; false when no such key is kept.</summary>
    public bool Pick(string kid)
    {
        var key = _keys.InForce(_token()).FirstOrDefault(k => k.Kid == kid);
        if (key is null) return false;
        BindKid = key.Kid;
        BindFor = key.Bound;
        BindScope = key.Scope;
        return true;
    }

    private RelayCommand? _bind;

    /// <summary>BIND: KEY BIND for the key picked — the connector and scope in the boxes; its live links end when it moved or fell.</summary>
    public RelayCommand BindCommand => _bind ??= new RelayCommand(() =>
    {
        var value = BindFor.Trim() + (BindScope.Length > 0 ? " " + BindScope : "");
        var result = _press(new ShowAction(ShowActionKind.AppKeyBind, BindKid, value));
        if (result.Ok) BindKid = "";
        Look();
    });

    /// <summary>The rows from the kept keys, redrawn only when they moved.</summary>
    public void Look()
    {
        var keys = _keys.InForce(_token());
        var now = string.Join("|", keys.Select(AppKeys.Words));
        if (now == _seen) return;
        _seen = now;
        Rows.Clear();
        foreach (var k in keys) Rows.Add(new AppKeyRow(k.Kid, k.Bound, k.Scope, k.Made, k.ClearSeen is not null, AppKeys.Words(k)));
        Raise(nameof(HasRows));
        Raise(nameof(Protection));
    }
}
