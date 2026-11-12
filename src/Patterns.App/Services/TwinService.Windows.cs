using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Show Bus 1.3 §12 on the twin: the windows a Stagewatch granted. A grant belongs to the node it was granted to, and a standby is a
/// node of its own, so a granted window on the main's air opens on the standby only through a grant of its own. The standby's WINDOWS
/// word tells the main which it holds — its id and the held forms, never a path; the main reads it against its air, the standby its own
/// grants against the air it mirrors, and every reader says which window a takeover would show black.
/// </summary>
public sealed partial class TwinService
{
    private string _windowsSent = "";           // the WINDOWS word this link was last sent; "" on a new link

    /// <summary>A standby's line on what it is without, with the windows' sentence after it while a window on air shows or would show black.</summary>
    private string WithWindows(string secrets)
    {
        var windows = string.Concat(WindowsSentence());
        return secrets.Length == 0 ? windows : windows.Length == 0 ? secrets : secrets + " · " + windows;
    }

    /// <summary>
    /// The granted windows a takeover would open — on a main, each standby's last WINDOWS word read against this desk's air and grants;
    /// on a standby, its own grants read against the air it mirrors, or its own air once it took the show. The Super Check's row,
    /// STATE's <c>twin.windows</c>, the twin's line, the glance, the health words and the Eye read it; never an address.
    /// </summary>
    public TwinWindowsReport WindowsReport => _role switch
    {
        TwinRole.Main => TwinWindows.ForMain(StandbysWindows(), TwinWindows.OnAir(_services.AirState, _kernel.State.Control.Stagewatch.Grants)),
        TwinRole.Standby => StandbyWindows(),
        _ => TwinWindowsReport.None,
    };

    /// <summary>The windows' sentence for the twin's line and the health words: one item while a window on air shows or would show black.</summary>
    private IEnumerable<string> WindowsSentence()
    {
        var report = WindowsReport;
        if (report.Lacking) yield return "WINDOWS: " + report.Words + ".";
    }

    /// <summary>The glance's word while a granted window on air shows or would show black after a takeover.</summary>
    private const string WindowsGlance = "WINDOW BLACK ON TAKEOVER";

    /// <summary>STATE's <c>twin.windows</c>: the granted windows on air a takeover shows black, and the grant asked for; "" when none would.</summary>
    public string WindowsWords
    {
        get
        {
            var report = WindowsReport;
            return report.Lacking ? report.Words : "";
        }
    }

    /// <summary>The standbys that could take the show, each with its last WINDOWS word (null before one) — a holder of the show and a follower never.</summary>
    private List<(string Name, TwinWindowsHeld? Held)> StandbysWindows()
    {
        lock (_gate)
        {
            return _standbys.Where(s => !s.IsFollower && !s.HoldsShow).Select(s => (s.Name, s.Windows)).ToList();
        }
    }

    /// <summary>
    /// The WINDOWS word to the main — this desk's Show Bus id and the held forms of the windows a Stagewatch granted this desk itself
    /// whose address reached it, never a path — once on each link as the welcome lands and whenever it changes. A grant belongs to this
    /// node, and no grant's address rides the twin either way.
    /// </summary>
    private void SendWindows()
    {
        if (IsFollowerNode || !_welcomedThisLink || !_mainProved) return;
        var json = TwinWindowsHeld.Of(_kernel.ShowBusId, _kernel.State.Control.Stagewatch.Grants).ToJson();
        if (string.Equals(json, _windowsSent, StringComparison.Ordinal)) return;
        _windowsSent = json;
        var line = TwinMessage.Format(TwinWord.Windows, json);
        _ = Task.Run(() => TryWriteToMain(line), CancellationToken.None);
    }

    /// <summary>
    /// The granted windows on the air this standby would take — the main's programme as it mirrors it (the AIR record's frozen air
    /// while the main's EDIT SAFE is open, its show otherwise), its own air once it took over — read against the grants of its own:
    /// those a takeover shows black, the grant Stagewatch is asked for by this desk's id and, after a takeover, whether each page is
    /// ready here. Nothing on a follower.
    /// </summary>
    private TwinWindowsReport StandbyWindows()
    {
        if (IsFollowerNode) return TwinWindowsReport.None;
        var tookOver = _phase == TwinPhase.TookOver;
        var air = tookOver ? _services.AirState : _mirroredAir?.Air ?? _kernel.State;
        var own = _kernel.State.Control.Stagewatch.Grants.ToList();
        var side = new TwinStandbySide(_kernel.ShowBusId, _mainName, tookOver, _kernel.Stagewatch.ApprovalsLive, _services.WindowReadiness);
        return TwinWindows.ForStandby(TwinWindows.OnAir(air, own), own, side);
    }
}
