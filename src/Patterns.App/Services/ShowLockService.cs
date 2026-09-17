using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The show lock: while the show runs, the machine is held off the things that have interrupted
/// shows — a toast over the desk, a chime through the PA, a Teams call ringing, Sticky Keys
/// popping up under a caller's Shift, the display going to sleep, the Windows key opening Start.
/// It goes on with the outputs (by default) and off with them, or by hand from the Machine page
/// and the wire; every item is put back on unlock and on a clean exit, and a crash leaves a
/// receipt the next start puts back from. Once every two seconds while locked, any audio session
/// that started since is muted and a change of the foreground app is logged; once a minute
/// Windows Update's pending restart is read. The machine itself is behind <see cref="IMachineLock"/>,
/// so the tests hold a fake and the other platforms say "not here".
/// </summary>
public sealed class ShowLockService : IDisposable
{
    private readonly AppServices _s;
    private readonly List<LockItem> _items = new();
    private bool _locked;
    private bool _auto;
    private DateTime? _sinceUtc;
    private DateTime _lastTickUtc;
    private DateTime _lastUpdateCheckUtc;
    private bool _updatePending;
    private string _foreground = "";
    private int _muted;
    private bool _handedOver;

    public ShowLockService(AppServices services, IMachineLock? machine = null)
    {
        _s = services;
        Machine = machine ?? (OperatingSystem.IsWindows() ? new WindowsMachineLock(services.Store.BaseDirectory) : new NoMachineLock());
    }

    /// <summary>The operating system behind the lock; the tests hand in a fake.</summary>
    public IMachineLock Machine { get; set; }

    /// <summary>The clock the words read; the tests pin it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public bool Locked => _locked;

    /// <summary>When Windows Update's pending restart was last read.</summary>
    public DateTime LastUpdateCheckUtc => _lastUpdateCheckUtc;

    /// <summary>Locked by the outputs opening (released when they close), not by hand.</summary>
    public bool Auto => _auto;

    /// <summary>Round 77: this desk's lock has passed to a replacement desk — nothing here is put back when this desk's outputs close or it exits.</summary>
    public bool HandedOver => _handedOver;

    private LockConfig Config => _s.State.Lock;

    public LockReport Report => new(_locked, _sinceUtc, _items.ToList(), _updatePending, _auto);

    /// <summary>The Machine page's line.</summary>
    public string Status => Report.Summary;

    /// <summary>The health line's clause while something is wrong.</summary>
    public string HealthWords => Report.HealthWords(_s.Outputs.IsLive);

    /// <summary>SHOWLOCK STATUS's payload.</summary>
    public string StatusJson()
    {
        var r = Report;
        return JsonSerializer.Serialize(new
        {
            locked = r.Locked,
            auto = r.Auto,
            since = r.SinceUtc,
            words = r.Summary,
            updateRestartPending = r.UpdateRestartPending,
            handedOver = _handedOver,
            items = r.Items.Select(i => new { key = i.Key, title = i.Title, state = i.State.ToString().ToLowerInvariant(), words = i.Words }),
        });
    }

    /// <summary>
    /// Round 77: a handover restart. The machine stays held for the show throughout, so the lock
    /// is not this desk's to put back any more — the replacement, booting with the receipt this
    /// lock wrote, continues it when its own outputs go live and puts everything back when its
    /// show ends. Toggling it twice (this desk's stand-down unlocking, the replacement locking
    /// again) left the machine unlocked under the new desk once, and the receipt it needed deleted.
    /// </summary>
    public void HandOver()
    {
        if (_handedOver) return;
        _handedOver = true;
        if (_locked) Log.Info("Show lock: handed to the replacement desk — the machine stays held; its receipt is the replacement's to keep and to put back.");
    }

    /// <summary>The replacement never came: the lock is this desk's own again, put back with its outputs as before.</summary>
    public void TakeBack()
    {
        if (!_handedOver) return;
        _handedOver = false;
        Log.Info("Show lock: the replacement never came — the lock is this desk's own again.");
    }

    /// <summary>
    /// At the start of a replacement (a takeover of a run still playing): the receipt on disk is
    /// that run's live lock, not a crash's leavings — nothing is put back, and this desk continues
    /// the lock when its outputs go live, remembering the originals that receipt already holds.
    /// </summary>
    public void ContinueAnotherRunsLock()
    {
        _updatePending = SafeUpdatePending();
        Log.Info("Show lock: the receipt is a run's still playing — nothing put back; this desk continues its lock with the outputs.");
    }

    /// <summary>At start: what a previous run's lock left changed goes back, and the desk is told.</summary>
    public void RestoreAfterCrash()
    {
        try
        {
            var words = Machine.RestoreFromReceipt();
            if (words.Length > 0)
            {
                Log.Warn(words);
                _s.Notify(words);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The show lock's receipt could not be put back.", ex);
        }
        _updatePending = SafeUpdatePending();
    }

    /// <summary>The outputs opened or closed: the lock follows when the show asks it to.</summary>
    public void OnOutputsLiveChanged()
    {
        if (!Config.AutoWithOutputs) return;
        if (_s.Outputs.IsLive)
        {
            if (!_locked) Lock(ActionOrigin.Desk, auto: true);
        }
        else if (_locked && _auto && !_handedOver)
        {
            Unlock(ActionOrigin.Desk);
        }
    }

    /// <summary>The machine held for the show: each item the config asks for, in order, each said.</summary>
    public ActionResult Lock(ActionOrigin origin, bool auto = false)
    {
        if (_locked) return ActionResult.Done("The machine is locked for the show already.");
        var cfg = Config;
        _items.Clear();
        if (!Machine.Available)
        {
            foreach (var key in new[] { ShowLockWords.Notifications, ShowLockWords.Sounds, ShowLockWords.Audio, ShowLockWords.Shortcuts, ShowLockWords.Awake, ShowLockWords.WindowsKey })
            {
                _items.Add(new LockItem(key, ShowLockWords.TitleOf(key), LockItemState.NotHere, "only Windows has this"));
            }
        }
        else
        {
            _items.Add(Item(ShowLockWords.Notifications, cfg.Notifications, () => Machine.Notifications(true), "notifications off"));
            _items.Add(Item(ShowLockWords.Sounds, cfg.Sounds, () => Machine.SystemSounds(true), "system sounds off"));
            if (cfg.OtherAudio)
            {
                _muted = Machine.OtherAudio(true, ShowLockWords.Allowed(cfg.AllowedAudio));
                _items.Add(new LockItem(ShowLockWords.Audio, ShowLockWords.TitleOf(ShowLockWords.Audio), _muted < 0 ? LockItemState.Failed : LockItemState.Done, ShowLockWords.MutedWords(_muted) + (cfg.AllowedAudio.Trim().Length > 0 ? $" ({cfg.AllowedAudio.Trim()} let through)" : "")));
            }
            else
            {
                _items.Add(new LockItem(ShowLockWords.Audio, ShowLockWords.TitleOf(ShowLockWords.Audio), LockItemState.Off, ""));
            }
            _items.Add(Item(ShowLockWords.Shortcuts, cfg.Shortcuts, () => Machine.Shortcuts(true), "shortcut keys off"));
            _items.Add(Item(ShowLockWords.Awake, cfg.KeepAwake, () => Machine.KeepAwake(true), "awake, display on"));
            _items.Add(Item(ShowLockWords.WindowsKey, cfg.WindowsKey, () => Machine.WindowsKey(true), "Windows key off"));
        }
        _updatePending = SafeUpdatePending();
        _items.Add(new LockItem(ShowLockWords.Updates, ShowLockWords.TitleOf(ShowLockWords.Updates),
            !Machine.Available ? LockItemState.NotHere : _updatePending ? LockItemState.NeedsAdmin : LockItemState.Done,
            !Machine.Available ? "only Windows has this" : _updatePending ? "a restart is pending — pause updates before doors (docs/SHOW-MACHINE.md)" : "no restart pending (pausing updates needs an administrator: docs/SHOW-MACHINE.md)"));
        _locked = true;
        _auto = auto;
        _sinceUtc = Clock();
        _lastTickUtc = _sinceUtc.Value;
        _foreground = SafeForeground();
        var words = "SHOW LOCK ON — " + Report.Summary;
        Log.Info($"{words} ({origin.Label})");
        _s.Journal.Record(origin.Label, ShowActionKind.ShowLockOn.ToString(), "", Report.Failed > 0 ? "Partial" : "Done", Report.Summary);
        return ActionResult.Done(words);
    }

    private static LockItem Item(string key, bool wanted, Func<LockItemState> set, string doneWords)
    {
        if (!wanted) return new LockItem(key, ShowLockWords.TitleOf(key), LockItemState.Off, "");
        LockItemState state;
        try
        {
            state = set();
        }
        catch (Exception ex)
        {
            Log.Warn($"The show lock's '{key}' threw.", ex);
            state = LockItemState.Failed;
        }
        return new LockItem(key, ShowLockWords.TitleOf(key), state, state == LockItemState.Done ? doneWords : state == LockItemState.Failed ? "could not be set — patterns.log says why" : "");
    }

    /// <summary>Everything put back as it was.</summary>
    public ActionResult Unlock(ActionOrigin origin)
    {
        if (!_locked) return ActionResult.Done("The machine is not locked.");
        var put = new List<string>();
        if (Machine.Available)
        {
            foreach (var item in _items.Where(i => i.State == LockItemState.Done))
            {
                try
                {
                    switch (item.Key)
                    {
                        case ShowLockWords.Notifications: Machine.Notifications(false); put.Add("notifications"); break;
                        case ShowLockWords.Sounds: Machine.SystemSounds(false); put.Add("system sounds"); break;
                        case ShowLockWords.Audio: Machine.OtherAudio(false, Array.Empty<string>()); put.Add("other apps' audio"); break;
                        case ShowLockWords.Shortcuts: Machine.Shortcuts(false); put.Add("the shortcut keys"); break;
                        case ShowLockWords.Awake: Machine.KeepAwake(false); put.Add("sleep and the screensaver"); break;
                        case ShowLockWords.WindowsKey: Machine.WindowsKey(false); put.Add("the Windows key"); break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"The show lock could not put back '{item.Key}'.", ex);
                }
            }
        }
        _locked = false;
        _auto = false;
        _sinceUtc = null;
        _items.Clear();
        _muted = 0;
        var words = put.Count > 0 ? $"SHOW LOCK OFF — {string.Join(", ", put)} put back as they were." : "SHOW LOCK OFF.";
        Log.Info($"{words} ({origin.Label})");
        _s.Journal.Record(origin.Label, ShowActionKind.ShowLockOff.ToString(), "", "Done", words);
        return ActionResult.Done(words);
    }

    /// <summary>Once a second from the desk's poll: every two seconds new audio sessions are muted and a change of the foreground app is logged; once a minute the pending restart is read.</summary>
    public void Tick()
    {
        var now = Clock();
        // A clock that went backwards (the tests pin one; a machine's time is corrected) reads as a minute passed.
        if (now < _lastUpdateCheckUtc || now - _lastUpdateCheckUtc >= TimeSpan.FromMinutes(1))
        {
            _lastUpdateCheckUtc = now;
            var pending = SafeUpdatePending();
            if (pending != _updatePending)
            {
                _updatePending = pending;
                if (pending) _s.Notify("Windows Update has a restart pending — pause updates before doors: docs/SHOW-MACHINE.md.");
                if (_locked)
                {
                    var i = _items.FindIndex(x => x.Key == ShowLockWords.Updates);
                    if (i >= 0) _items[i] = _items[i] with { State = pending ? LockItemState.NeedsAdmin : LockItemState.Done, Words = pending ? "a restart is pending — pause updates before doors (docs/SHOW-MACHINE.md)" : "no restart pending" };
                }
            }
        }
        if (!_locked || _handedOver || (now >= _lastTickUtc && now - _lastTickUtc < TimeSpan.FromSeconds(2))) return;   // handed over: the replacement mutes what starts, from the shared receipt
        _lastTickUtc = now;
        if (Machine.Available && Config.OtherAudio && _items.Any(i => i.Key == ShowLockWords.Audio && i.State == LockItemState.Done))
        {
            var muted = Machine.OtherAudio(true, ShowLockWords.Allowed(Config.AllowedAudio));
            if (muted >= 0 && muted != _muted)
            {
                if (muted > _muted) Log.Info($"Show lock: another app started playing — {ShowLockWords.MutedWords(muted)}.");
                _muted = muted;
                var i = _items.FindIndex(x => x.Key == ShowLockWords.Audio);
                if (i >= 0) _items[i] = _items[i] with { Words = ShowLockWords.MutedWords(muted) + (Config.AllowedAudio.Trim().Length > 0 ? $" ({Config.AllowedAudio.Trim()} let through)" : "") };
            }
        }
        var foreground = SafeForeground();
        if (foreground.Length > 0 && foreground != _foreground)
        {
            var own = string.Equals(foreground, "Patterns", StringComparison.OrdinalIgnoreCase) || string.Equals(foreground, System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
            if (!own && _foreground.Length > 0)
            {
                Log.Warn($"Show lock: the foreground went to {foreground} at {now.ToLocalTime():HH:mm:ss} — the caller's keys go there until the desk is clicked.");
                _s.Journal.Record("Machine", "FocusLost", foreground, "Alert", $"The foreground went to {foreground}.");
            }
            _foreground = foreground;
        }
    }

    private bool SafeUpdatePending()
    {
        try { return Machine.UpdateRestartPending(); }
        catch { return false; }
    }

    private string SafeForeground()
    {
        try { return Machine.ForegroundApp(); }
        catch { return ""; }
    }

    public void Dispose()
    {
        if (_locked && _handedOver)
        {
            Log.Info("Show lock: left held for the replacement desk at exit.");
            return;
        }
        if (_locked)
        {
            try { Unlock(ActionOrigin.Desk); } catch (Exception ex) { Log.Warn("The show lock could not be released on exit.", ex); }
        }
    }
}
