using Patterns.Core.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// One window a Stagewatch granted this desk as every reader carries it — STATE's <c>bus.stagewatch.windows</c>, the Run strip's chip,
/// the menus, the Nodes page, the Eye, the assistant's facts and the deck: its held form and name, whether its address reached this
/// desk and why not, whether its page is ready to go on air, and where it is shown — read from the air alone (<see cref="Screens"/>),
/// with the targets the preview stages it on that the air does not show it on (<see cref="Staged"/>). Never an address.
/// </summary>
public sealed record StagewatchWindowRow(
    string Sw, string Window, string Name, string How, string Held, bool Granted, string Cannot, string Link,
    bool Ready, string ReadyWhy, string Shown, IReadOnlyList<string> Screens, IReadOnlyList<string> Staged);

/// <summary>
/// Round 96 (Show Bus 1.3 §12): the windows Stagewatch granted this desk, read on the UI thread from the grants kept with the machine,
/// the page mounted under each one's held form and the air. Readiness is the browser's word (12.9), never the show's; "shown" is the
/// air's: the programme state while EDIT SAFE is open is the frozen air, and the edited state is only the preview.
/// </summary>
public sealed class StagewatchWindows
{
    private readonly AppServices _s;

    public StagewatchWindows(AppServices services) => _s = services;

    /// <summary>The grants kept now, in the order Stagewatch listed them.</summary>
    public IReadOnlyList<StagewatchGrant> Grants => _s.State.Control.Stagewatch.Grants.ToList();

    /// <summary>A granted window by its name (case-blind) or id; null when none is granted under it.</summary>
    public StagewatchGrant? Find(string? nameOrId) => StagewatchGrants.Find(Grants, nameOrId);

    /// <summary>Whether a granted window's page is ready to go on air now, and why not: the page mounted under its held form, as its browser reads it.</summary>
    public WindowReadiness Readiness(StagewatchGrant? grant)
    {
        if (grant is null) return StagewatchGrants.Readiness(null, false, false, "", null);
        var page = _s.WebIn.For(InputKeys.Web(grant.Held));
        return StagewatchGrants.Readiness(grant, page is not null, page?.Withheld ?? false, page?.WithheldWhy ?? "", page?.Shell);
    }

    /// <summary>Every granted window's row, in the grants' order.</summary>
    public IReadOnlyList<StagewatchWindowRow> Rows()
    {
        var grants = Grants;
        if (grants.Count == 0) return Array.Empty<StagewatchWindowRow>();
        var air = _s.AirState;
        var preview = _s.Sandbox.Active ? _s.State : null;
        var live = _s.OutputsLive;
        var rows = new List<StagewatchWindowRow>(grants.Count);
        foreach (var g in grants)
        {
            var held = g.Held;
            var screens = StagewatchGrants.Places(air, held);
            var staged = preview is null ? Array.Empty<string>() : StagewatchGrants.Places(preview, held).Where(t => !screens.Contains(t)).ToArray();
            var ready = Readiness(g);
            rows.Add(new StagewatchWindowRow(g.Sw, g.Window, g.Name, g.How, held, g.Opens, g.Cannot, g.Link, ready.Ready, ready.Why,
                StagewatchGrants.Shown(screens.Count > 0, staged.Length > 0, live), screens, staged));
        }
        return rows;
    }

    /// <summary>One line for the desk's readers: each window, where it is shown and whether it is ready; "" when none is granted.</summary>
    public string Words()
    {
        var rows = Rows();
        return rows.Count == 0 ? "" : string.Join("; ", rows.Select(RowWords));
    }

    /// <summary>A row in words: "lobby — on air on 2", "foyer — in the preview — not on air (ready)", "stage — not shown: …".</summary>
    public static string RowWords(StagewatchWindowRow r)
    {
        var where = r.Screens.Count > 0 ? " on " + string.Join(", ", r.Screens) : r.Staged.Count > 0 ? " on the PVW of " + string.Join(", ", r.Staged) : "";
        var shown = r.Shown.Length > 0 ? r.Shown + where : "not shown";
        var ready = r.Ready ? "ready" : r.ReadyWhy;
        return $"{r.Name} — {shown} ({ready})";
    }
}
