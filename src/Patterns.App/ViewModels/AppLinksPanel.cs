using System.Collections.ObjectModel;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// One app link's row on the Nodes page's App links table (Show Bus 1.3 §13.16.5): its light's mark and words — who, from where, what
/// proved it, scope, carrier, since when, and when it ended — and the facts its right-click menu reads. Never a key or a proof.
/// </summary>
/// <param name="Acts">The page may close and revoke (the desk); a node lists its links and leaves both to the desk, as its menu says.</param>
public sealed record AppLinkTableRow(AppLinkRow Row, bool Acts = true)
{
    public string Lid => Row.Lid;
    public string Mark => Row.Mark;
    public string Words => Row.Words;

    /// <summary>The row's colour beside its mark: green, amber over green while plain or seen in clear, grey once ended.</summary>
    public string Hue => Row.Light switch
    {
        CheckLight.Green => "#46D27A",
        CheckLight.Amber => "#FFB020",
        _ => "#8A93A6",
    };

    /// <summary>REVOKE KEY is drawn for a link that stands on a key and is open, on the desk; a code's link has none (NEW TOKEN ends it).</summary>
    public bool CanRevoke => Acts && !Row.Ended && Row.KeyKid.Length > 0;

    /// <summary>
    /// CLOSE is drawn while the link is open — not ended, not told its last word — on the desk: in step with the row's menu, which draws
    /// it disabled with the reason on a node and on a closing row, and with the action, which refuses a second close.
    /// </summary>
    public bool CanClose => Acts && Row.Open;

    /// <summary>The link as its menu reads it.</summary>
    public Patterns.Core.Menus.MenuAppLink Menu => Patterns.Core.Menus.MenuAppLink.Of(Row);
}

/// <summary>
/// Show Bus 1.3 §13.16.5–6 on the Nodes page: the acceptor's list — each link this desk's wire accepted that LINK verified, open or
/// ended within peer.forget.ms, and each refusal of the last minute with its count — and the operator's two actions on a link,
/// <i>Close</i> (APP LINK CLOSE) and <i>Revoke key</i> (KEY REVOKE), each a press of the one show action from the desk, so a row's
/// button and its right-click entry are the same verb and one journal row. The rows follow the wire at the desk's tick.
/// </summary>
public sealed class AppLinksPanel : Observable
{
    private readonly Func<IReadOnlyList<AppLinkRow>> _rows;
    private readonly Func<IReadOnlyList<AppLinkRefusal>> _refusals;
    private readonly Func<ShowAction, ActionResult> _press;
    private readonly Func<long> _uncounted;
    private readonly bool _acts;
    private string _seen = "";
    private string _refusalWords = "";

    /// <param name="rows">The acceptor's list as the wire holds it now.</param>
    /// <param name="refusals">The LINK refusals of the last minute.</param>
    /// <param name="press">Runs one action from the desk; its words to the strip.</param>
    /// <param name="uncounted">The refusals the list's bound left out of its rows this run.</param>
    /// <param name="acts">The desk's page closes and revokes; a node's lists only (both are the desk's).</param>
    public AppLinksPanel(Func<IReadOnlyList<AppLinkRow>> rows, Func<IReadOnlyList<AppLinkRefusal>> refusals, Func<ShowAction, ActionResult> press, Func<long> uncounted, bool acts)
    {
        _rows = rows;
        _refusals = refusals;
        _press = press;
        _uncounted = uncounted;
        _acts = acts;
        Look();
    }

    /// <summary>The links, a row each: the open ones by when they stood, then the ended ones, grey.</summary>
    public ObservableCollection<AppLinkTableRow> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    /// <summary>The refusals of the last minute, one line each with its count; "" with none.</summary>
    public string RefusalWords { get => _refusalWords; private set { if (Set(ref _refusalWords, value)) Raise(nameof(HasRefusals)); } }

    public bool HasRefusals => _refusalWords.Length > 0;

    private RelayCommand<AppLinkTableRow>? _close;

    /// <summary>CLOSE on a row: APP LINK CLOSE — the link hears that the operator closed it and ends; its key stays in force.</summary>
    public RelayCommand<AppLinkTableRow> CloseCommand => _close ??= new RelayCommand<AppLinkTableRow>(row =>
    {
        if (row is null || !row.CanClose) return;
        _press(new ShowAction(ShowActionKind.AppLinkClose, row.Lid));
        Look();
    });

    private RelayCommand<AppLinkTableRow>? _revoke;

    /// <summary>REVOKE KEY on a row: KEY REVOKE for the key the link stands on — this link and every other that uses it end at once.</summary>
    public RelayCommand<AppLinkTableRow> RevokeKeyCommand => _revoke ??= new RelayCommand<AppLinkTableRow>(row =>
    {
        if (row is null || !row.CanRevoke) return;
        _press(new ShowAction(ShowActionKind.AppKeyRevoke, row.Row.KeyKid));
        Look();
    });

    /// <summary>The rows from the wire, redrawn only when they moved.</summary>
    public void Look()
    {
        var rows = _rows();
        var now = string.Join("|", rows.Select(r => r.Lid + ":" + r.Words + ":" + r.Mark));
        if (now != _seen)
        {
            _seen = now;
            Rows.Clear();
            foreach (var r in rows) Rows.Add(new AppLinkTableRow(r, _acts));
            Raise(nameof(HasRows));
        }
        var more = AcceptorWords.Uncounted(_uncounted());
        RefusalWords = string.Join("\n", _refusals().Select(r => "△ " + r.Line).Concat(more.Length > 0 ? new[] { "△ " + more } : Array.Empty<string>()));
    }
}
