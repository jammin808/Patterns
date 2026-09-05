using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Updates for a machine nobody sits at: a package dropped into the updates folder beside the
/// settings — by hand over the network share, or by the management server the site checks in
/// with — is read and shown; UPDATE APPLY (the passcode), the page's button or the update window
/// asks the watchdog to swap the files between two starts of the app and to roll them back if the
/// new build does not stay up. This service never touches the app's own files: that is the
/// supervisor's job, done while the app is not running.
/// </summary>
public sealed class UpdateService
{
    private readonly AppServices _s;
    private string _scannedKey = "";
    private DateTime? _windowFiredOn;

    public UpdateService(AppServices services) => _s = services;

    /// <summary>Tests: stand in for "running under the watchdog".</summary>
    public Func<bool>? SupervisedOverride { get; set; }

    public bool Supervised => SupervisedOverride?.Invoke() ?? LaunchOptions.IsChild;

    public string Folder => UpdatePackage.Folder(_s.Store.BaseDirectory);

    /// <summary>The newest package in the folder, read; null with none.</summary>
    public UpdateInfo? Staged { get; private set; }

    /// <summary>The page's line: what is staged and whether it can be applied, or why not.</summary>
    public string Status { get; private set; } = "";

    /// <summary>What the watchdog wrote after the last update (applied, rolled back), or "".</summary>
    public string LastNote { get; private set; } = "";

    /// <summary>The running build's version, as the package's manifest would name it.</summary>
    public static string RunningVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "dev";

    /// <summary>Reads the folder again (every few seconds from the poll, and after a download): cheap when nothing changed.</summary>
    public void Scan()
    {
        try
        {
            var path = UpdatePackage.Staged(_s.Store.BaseDirectory);
            var key = path is null ? "" : path + "|" + new FileInfo(path).LastWriteTimeUtc.Ticks + "|" + new FileInfo(path).Length;
            if (key != _scannedKey)
            {
                _scannedKey = key;
                Staged = path is null ? null : UpdatePackage.Inspect(path);
            }
            LastNote = UpdateApply.ReadNote(Folder);
        }
        catch (Exception ex)
        {
            Log.Warn("Update folder could not be read.", ex);
        }
        Status = Describe();
    }

    private string Describe()
    {
        if (Staged is null) return $"Nothing staged — drop a patterns-update-<version>.zip into {Folder} (Patterns.exe and a patterns.update.json at its root), or let the management server deliver one.";
        if (!Staged.Ok) return $"Staged package cannot be used — {Staged.Summary}";
        var same = UpdatePackage.IsSameVersion(Staged.Version, RunningVersion) ? " — the same version as this build" : "";
        var how = Supervised ? "APPLY swaps the files through the watchdog and puts the show back" : "start Patterns under the watchdog to apply it in place";
        return $"Staged: {Staged.Summary}{same}; running {RunningVersion}. {how}.";
    }

    /// <summary>UPDATE APPLY: the passcode (unless the policy — the window, the management server — asks), a usable package, the watchdog, then the exit that hands over.</summary>
    public ActionResult Apply(string passcode, ActionOrigin origin, bool byPolicy = false)
    {
        var cfg = _s.State.Install;
        if (!byPolicy && !_s.Gate.Check(cfg.AdminPasscode, passcode, DateTime.UtcNow)) return ActionResult.Refused($"Update refused — {_s.Gate.Reason}.");
        Scan();
        if (Staged is null) return ActionResult.Refused($"Nothing to apply — no package in {Folder}.");
        if (!Staged.Ok) return ActionResult.Refused($"The staged package cannot be used — {string.Join("; ", Staged.Problems)}.");
        if (!Supervised) return ActionResult.Refused("An update in place needs the watchdog — start Patterns normally (not --no-watchdog, with the watchdog on under Machine → Stability), or copy the files by hand.");
        if (_s.ExitRequest is null) return ActionResult.Refused("No way to restart in this session.");
        UpdateApply.WriteRequest(Folder, new UpdateRequest(Staged.Path, Staged.Version, DateTime.UtcNow));
        var code = _s.PrepareRestart(forUpdate: true);
        Log.Info($"Update to {Staged.Version} requested from {origin.Label}: the watchdog applies {Staged.FileName}.");
        return _s.ExitRequest(code)
            ? ActionResult.Requested($"Updating to {Staged.Version} — the watchdog swaps the files and brings the show back in a moment.")
            : ActionResult.Failed("The app did not accept the exit request.");
    }

    /// <summary>The update window: a usable package staged, AutoUpdate on, the window's minute reached — once a day.</summary>
    public void TickWindow(DateTime now)
    {
        var cfg = _s.State.Install;
        if (!cfg.AutoUpdate || Staged is not { Ok: true } || !Supervised) return;
        if (!Schedule.TryParseTime(cfg.UpdateWindow, out var at) || now.Hour != at.Hours || now.Minute != at.Minutes) return;
        if (_windowFiredOn == now.Date) return;
        _windowFiredOn = now.Date;
        if (UpdatePackage.IsSameVersion(Staged.Version, RunningVersion))
        {
            Log.Info($"Update window: the staged package is this build ({RunningVersion}) — nothing to do.");
            return;
        }
        var result = Apply("", new ActionOrigin(OriginKind.Schedule, "update window"), byPolicy: true);
        Log.Info($"Update window at {cfg.UpdateWindow}: {result.Message}");
    }

    /// <summary>For STATE: what is staged, whether it can be applied, the last note.</summary>
    public object StatusRow() => new
    {
        staged = Staged?.FileName ?? "",
        version = Staged?.Version ?? "",
        ok = Staged?.Ok == true,
        running = RunningVersion,
        supervised = Supervised,
        status = Status,
        last = LastNote,
    };
}
