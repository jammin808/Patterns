using Avalonia.Threading;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Hosts one browser per web page the show references, published on the <see cref="InputBus"/>
/// like the NDI receivers: a page mounts when a pattern, a layer or the sandboxed preview wants
/// it, retires briefly for a crossfade when nothing does, and reopens when its viewport changes.
/// The same address on three screens costs one browser. Windows with the WebView2 runtime only;
/// elsewhere the placeholder card says so.
///
/// It also keeps the armed web VT (<see cref="WebVt"/>): a page's video set up off air, put at
/// its mark and held, and played from there the moment the page reaches an output — and a page a
/// cue ahead asks to play from a point is opened early (pre-rolled), so the take lands on the
/// right frame with the advert already behind it.
/// </summary>
public sealed class WebEngine : IDisposable
{
    /// <summary>Simultaneous pages — each is a browser process family.</summary>
    public const int MaxPages = 4;

    /// <summary>How often a page's player is read while something is pending — an arm to prepare, a play to land, an advert to skip.</summary>
    public static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(250);

    private sealed record Page(IWebSource Source, string Format, bool PreRoll, DateTime? UnwantedUtc = null);

    /// <summary>
    /// A page opened for a cue ahead stays this long after the cue stops being ahead: the moment
    /// a cue runs, it is no longer "next" and its look has not landed yet, and a page closed in
    /// that gap would open again a publish later — a fresh browser, the arm gone, the take on the top.
    /// </summary>
    public static readonly TimeSpan PreRollGrace = TimeSpan.FromSeconds(4);

    /// <summary>The VT state of one page: its arm, what its player last said, and where it stood.</summary>
    public sealed class PageVt
    {
        public WebArm Arm = WebArm.None;
        public WebPlayerReading Reading;
        public DateTime ReadingUtc;
        public PageService Service;
        /// <summary>Seen on an output (not the preview alone) at the last reconcile.</summary>
        public bool OnAir;
        /// <summary>Where the player stands, as it reported: a prepare or a play is a request until the reading shows it landed.</summary>
        public WebVtPhase Phase;
        /// <summary>When the last request was sent: a request unanswered past its timeout fails.</summary>
        public DateTime RequestedUtc;
        /// <summary>Prepares sent since the arm: a player that will not answer is asked three times, then the words say it failed.</summary>
        public int PrepareAttempts;
        /// <summary>The mark has been observed on the player since the arm (paused at it).</summary>
        public bool Prepared => Phase == WebVtPhase.PreparedObserved;
        /// <summary>A prepare is due: never asked, or failed with asks left — and never while one is pending an answer.</summary>
        public bool PrepareDue => Phase == WebVtPhase.Unprepared || (Phase == WebVtPhase.Failed && PrepareAttempts < MaxPrepareAttempts);
        public const int MaxPrepareAttempts = 3;
        /// <summary>The mark was applied while an advert showed — applied again once it has gone.</summary>
        public bool PreparedUnderAd;
        /// <summary>The page reached the air before its player answered: play from the mark as soon as it does.</summary>
        public bool PendingPlay;
        public double PendingFrom;
        public int AdSkips;
        public bool ReadingPending;
        /// <summary>The operator disarmed the look's own arm: it stays off until the page leaves the air (or the look's mark moves).</summary>
        public bool LookDisarmed;
    }

    private readonly string _userDataFolder;
    private readonly Dictionary<string, Page> _pages = new();
    private readonly List<(string Key, IWebSource Source, DateTime RetiredUtc)> _retired = new();
    private readonly Dictionary<string, PageVt> _vts = new(StringComparer.Ordinal);
    private DispatcherTimer? _fast;

    public WebEngine(string baseDirectory)
    {
        _userDataFolder = Path.Combine(baseDirectory, "webview2");
    }

    /// <summary>Tests (and any other browser) stand in for WebView2 here: a wanted page → a source, or null to skip it.</summary>
    public Func<MediaLocator.WantedInput, IWebSource?>? SourceFactory { get; set; }

    /// <summary>Non-empty when more pages are wanted than the cap allows.</summary>
    public string LimitNote { get; private set; } = "";

    /// <summary>Mounted keys with a short status each — the Media page's active-inputs line.</summary>
    public IReadOnlyList<(string Key, string Status)> MountStatuses
        => _pages.Select(kv => (kv.Key, kv.Value.Source.StatusText)).ToList();

    public int PageCount => _pages.Count;

    /// <summary>The mounted page for a key, or null.</summary>
    public IWebSource? For(string key) => _pages.TryGetValue(key, out var page) ? page.Source : null;

    /// <summary>A page's viewport from its wanted Format: "1280x720" → (1280, 720); anything else is 1080p.</summary>
    public static (int Width, int Height) ParseSize(string format)
    {
        var parts = (format ?? "").Split('x', '×');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 320 && h >= 240)
        {
            return (Math.Min(w, 7680), Math.Min(h, 4320));
        }
        return (1920, 1080);
    }

    /// <summary>Also called from the app's 1 s poll so a retired page never lingers.</summary>
    public void SweepRetired()
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (DateTime.UtcNow - _retired[i].RetiredUtc <= TimeSpan.FromSeconds(4)) continue;
            InputBus.ClearPreviousIf(_retired[i].Key, _retired[i].Source);
            Dispose(_retired[i].Source);
            _retired.RemoveAt(i);
        }
    }

    /// <summary>
    /// Reconciles the page pool with the program (and sandbox) snapshot (UI thread). The pages the
    /// cues ahead ask to play from a point (<paramref name="preRoll"/>) are opened too, off air.
    /// </summary>
    public void Reconcile(ShowSnapshot snap, ShowSnapshot? sandbox = null, IReadOnlyList<PreRoll.WebWant>? preRoll = null)
    {
        SweepRetired();

        // The same merge and the same monitor rule the clips go through: a page has a soundtrack
        // too, and a video playing on a page in the preview is one more thing in the mix.
        var wanted = VideoEngine.MergeWithSandbox(MediaLocator.FindWantedInputs(snap), sandbox);
        wanted.RemoveAll(w => w.Kind != MediaLocator.WantedKind.Web);
        wanted = AudioMonitorRule.Apply(snap.State, wanted);

        // A page a cue ahead asks for that no picture wants yet: opened muted and off air, prepared
        // at its mark by the poll. One the show already shows is left to the show.
        var early = new List<MediaLocator.WantedInput>();
        if (preRoll is { Count: > 0 })
        {
            foreach (var p in preRoll)
            {
                if (wanted.Any(w => w.Key == p.Key)) continue;
                early.Add(new MediaLocator.WantedInput(p.Key, MediaLocator.WantedKind.Web, p.Url, false, true, 0, p.Format, p.Zoom, p.Clean)
                    { AutoPlay = true, StartSeconds = p.StartSeconds, Service = p.Service, Destination = AudioDestination.Silent });
            }
        }

        // A page nobody wants any more, or one whose viewport changed, retires — kept briefly so a
        // crossfade fades out real frames — and a new viewport reopens below. One opened for a cue
        // ahead is given its grace first (see PreRollGrace).
        var sweep = DateTime.UtcNow;
        foreach (var key in _pages.Keys.ToList())
        {
            var page = _pages[key];
            var want = wanted.FirstOrDefault(w => w.Key == key) ?? early.FirstOrDefault(w => w.Key == key);
            if (want is null && page.PreRoll)
            {
                if (page.UnwantedUtc is null)
                {
                    _pages[key] = page with { UnwantedUtc = sweep };
                    continue;
                }
                if (sweep - page.UnwantedUtc.Value < PreRollGrace) continue;
            }
            if (want is null || want.Format != page.Format) Retire(key);
            else if (page.UnwantedUtc is not null) _pages[key] = page with { UnwantedUtc = null };
        }

        if (wanted.Count == 0 && early.Count == 0)
        {
            LimitNote = "";
            return;
        }

        if (SourceFactory is null && !Supported(out var note))
        {
            WebInput.AvailabilityNote = note;
            return;
        }
        WebInput.AvailabilityNote = "";

        var now = DateTime.UtcNow;
        var over = 0;
        foreach (var w in wanted.Concat(early))
        {
            var isEarly = !wanted.Contains(w);
            var onAir = !isEarly && OnAir(w);
            if (_pages.TryGetValue(w.Key, out var page))
            {
                // Zoom, sound and the CLEAN style apply live — the page never reloads for them.
                page.Source.ZoomPct = w.Zoom;
                page.Source.IsMuted = w.Mute;
                page.Source.CleanCss = w.Clean;
                if (page.PreRoll != isEarly || page.UnwantedUtc is not null) _pages[w.Key] = page with { PreRoll = isEarly, UnwantedUtc = null };
                RouteSound(page.Source, w, snap.State);
                Track(w, page.Source, onAir, mounted: false, now);
                continue;
            }
            if (_pages.Count >= MaxPages)
            {
                over++;
                continue;
            }
            try
            {
                var source = SourceFactory is { } open ? open(w) : Open(w);
                if (source is null) continue;
                source.ZoomPct = w.Zoom;
                source.IsMuted = w.Mute;
                source.CleanCss = w.Clean;
                _pages[w.Key] = new Page(source, w.Format, isEarly);
                InputBus.Mount(w.Key, source);
                RouteSound(source, w, snap.State);
                Track(w, source, onAir, mounted: true, now);
            }
            catch (Exception ex)
            {
                Log.Error($"Web page open failed for '{w.Target}'.", ex);
                WebInput.AvailabilityNote = $"Could not open the page: {ex.Message}";
            }
        }
        LimitNote = over > 0
            ? $"Page limit: {MaxPages} web pages at once — {over} page{(over == 1 ? "" : "s")} waiting."
            : "";
        ArmFast();
    }

    // ---- the page's sound ----------------------------------------------------------------------

    /// <summary>
    /// With the matrix in charge, the page's sound is steered to the first device its source is
    /// routed to (a page has one output picker, so a source on several devices takes the first
    /// and the note says so), and muted when its source is routed nowhere; with the matrix off,
    /// the machine's default as before.
    /// </summary>
    private static void RouteSound(IWebSource source, MediaLocator.WantedInput w, ShowState state)
    {
        if (!state.AudioRouting.Enabled)
        {
            source.AudioDevice = "";
            return;
        }
        var route = AudioRouting.ClipRoute(state, w.Buses);
        switch (route.Path)
        {
            case ClipAudioPath.Device:
                source.AudioDevice = route.Device == AudioRouting.ComputerOutput ? "" : route.Device;
                break;
            case ClipAudioPath.Mixer:
            {
                var device = route.Lanes.Select(l => l.Source).FirstOrDefault(AudioRouting.IsDevice);
                var name = device is null ? "" : AudioRouting.DeviceName(device);
                source.AudioDevice = name == AudioRouting.ComputerOutput ? "" : name;
                break;
            }
            case ClipAudioPath.Silent:
                source.AudioDevice = "";
                source.IsMuted = true;
                break;
            default:
                source.AudioDevice = "";
                break;
        }
    }

    /// <summary>Every mounted page's sound: where it was steered and what the page said — the Audio page's lines.</summary>
    public IEnumerable<(string Key, string Device, string Note)> AudioRouteNotes()
    {
        foreach (var (key, page) in _pages)
        {
            yield return (key, page.Source.AudioDevice, page.Source.AudioRouteNote);
        }
    }

    // ---- the armed web VT ----------------------------------------------------------------------

    /// <summary>On an output — the programme or a screen's own picture — rather than the preview alone.</summary>
    private static bool OnAir(MediaLocator.WantedInput w)
    {
        if (w.Buses.Count == 0) return true;
        foreach (var bus in w.Buses)
        {
            if (!bus.Preview) return true;
        }
        return false;
    }

    /// <summary>The VT state of a page, made on first sight.</summary>
    public PageVt VtFor(string key)
    {
        if (!_vts.TryGetValue(key, out var vt))
        {
            vt = new PageVt();
            _vts[key] = vt;
        }
        return vt;
    }

    /// <summary>The arm on a page; none for a page nobody armed.</summary>
    public WebArm ArmOf(string key) => _vts.TryGetValue(key, out var vt) ? vt.Arm : WebArm.None;

    /// <summary>What a page's player last said; none for a page with no player (or none asked yet).</summary>
    public WebPlayerReading ReadingOf(string key) => _vts.TryGetValue(key, out var vt) ? vt.Reading : WebPlayerReading.None;

    /// <summary>Whether a mounted page is there for a cue ahead only (nothing shows it yet).</summary>
    public bool IsPreRolled(string key) => _pages.TryGetValue(key, out var page) && page.PreRoll;

    /// <summary>The pages with an arm on them, with their keys — the phone's, the deck's and the Show page's word.</summary>
    public IEnumerable<(string Key, WebArm Arm, WebPlayerReading Reading)> Armed()
    {
        foreach (var (key, vt) in _vts)
        {
            if (vt.Arm.Armed && _pages.ContainsKey(key)) yield return (key, vt.Arm, vt.Reading);
        }
    }

    /// <summary>
    /// Where a page stands after a reconcile: the look's arm put on when it is off air, the take
    /// that fires it, a page that leaves the air re-armed by the look for its next time, and a
    /// page that opens straight onto the air with a start point played once its player answers.
    /// </summary>
    private void Track(MediaLocator.WantedInput w, IWebSource source, bool onAir, bool mounted, DateTime now)
    {
        var vt = VtFor(w.Key);
        vt.Service = w.Service;
        var was = vt.OnAir;

        if (mounted)
        {
            // A fresh browser: nothing the operator armed survives from the last one.
            vt.Arm = WebArm.None;
            vt.Reading = WebPlayerReading.None;
            vt.Phase = WebVtPhase.Unprepared;
            vt.PrepareAttempts = 0;
            vt.PendingPlay = false;
            if (w.AutoPlay)
            {
                if (onAir)
                {
                    // Straight onto the air: the address carried the start where it could; the player is
                    // asked to play from the mark as soon as it answers, so the room never sees the top.
                    vt.PendingPlay = true;
                    vt.PendingFrom = w.StartSeconds;
                    vt.Arm = WebArm.None.ArmedBy(true, w.StartSeconds, now).Fired(now);
                }
                else
                {
                    vt.Arm = WebArm.None.ArmedBy(true, w.StartSeconds, now);
                }
            }
            vt.OnAir = onAir;
            return;
        }

        if (was && !onAir) vt.LookDisarmed = false;   // off the air: the look's ask stands again
        if (WebVt.ShouldFire(vt.Arm, was, onAir))
        {
            Fire(w.Key, source, vt, now);
        }
        else if (!onAir && w.AutoPlay && !vt.LookDisarmed
                 && (was || (vt.Arm.ByLook && vt.Arm.Armed && Math.Abs(vt.Arm.StartSeconds - w.StartSeconds) > 0.01) || (!vt.Arm.Armed && vt.Arm.PlayedUtc is null && !vt.Arm.ByLook)))
        {
            // Left the air with the look's ask still on it, the look's mark moved, or the ask arrived
            // on a page already open: armed by the look again, wound back by the poll.
            if (!vt.Arm.Armed || vt.Arm.ByLook) vt.Arm = vt.Arm.ArmedBy(true, w.StartSeconds, now);
            vt.Phase = WebVtPhase.Unprepared;
            vt.PrepareAttempts = 0;
        }
        else if (!onAir && !w.AutoPlay && vt.Arm.Armed && vt.Arm.ByLook)
        {
            // The look's ask was taken off: its arm goes with it; the operator's own would stay.
            vt.Arm = vt.Arm.Cleared();
            vt.Phase = WebVtPhase.Unprepared;
            vt.PrepareAttempts = 0;
        }
        vt.OnAir = onAir;
    }

    private void Fire(string key, IWebSource source, PageVt vt, DateTime now)
    {
        var from = vt.Arm.StartSeconds;
        try
        {
            source.RunScript(WebVt.PlayFromScript(vt.Service, from));
        }
        catch (Exception ex)
        {
            Log.Warn("The armed page could not be started.", ex);
        }
        vt.Arm = vt.Arm.Fired(now);
        vt.Phase = WebVtPhase.FireRequested;   // a request: the reading says when the player is playing from the mark
        vt.RequestedUtc = now;
        vt.PrepareAttempts = 0;
        vt.PendingPlay = !vt.Reading.Ok;   // no player answered yet: played again the moment one does
        vt.PendingFrom = from;
        Log.Info($"Web VT played from {WebVt.TimeText(from)}: {key}");
    }

    /// <summary>
    /// ARM: the page's video held at a point, to play from it when the page goes to air. The value
    /// is a time, empty for the mark set (else where the player is now), or off. A page on air is
    /// refused — arm it from the preview, or give the look a start point so it arms itself.
    /// </summary>
    public ActionResult Arm(string key, IWebSource source, string name, string value, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var vt = VtFor(key);
        if (WebVt.IsOff(value))
        {
            if (!vt.Arm.Armed) return ActionResult.Done($"{name}: nothing was armed.");
            var byLook = vt.Arm.ByLook;
            vt.Arm = vt.Arm.Cleared();
            vt.Phase = WebVtPhase.Unprepared;
            vt.PrepareAttempts = 0;
            vt.LookDisarmed = byLook;
            return ActionResult.Done(byLook
                ? $"{name}: disarmed — the look's own arm stays off until the page leaves the air (untick Play the video from to drop it)."
                : $"{name}: disarmed — the video plays as the page does.");
        }
        if (vt.OnAir && !IsPreRolled(key))
        {
            return ActionResult.Refused($"{name} is on air — arm it from the preview (or give the look a start point on the Media page, and it arms itself next time).");
        }
        double at;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!WebVt.TryParseTime(value, out at)) return ActionResult.Refused($"'{value}' is not a point in a video — 1:23, 83 or 1m23s.");
        }
        else if (vt.Arm.Armed || vt.Arm.PlayedUtc is not null || vt.Arm.StartSeconds > 0)
        {
            at = vt.Arm.StartSeconds;
        }
        else if (vt.Reading.Ok)
        {
            at = vt.Reading.Position;
        }
        else
        {
            return ActionResult.Refused($"{name} has no video player answering yet — give a time (WEB ARM 1:23), or wait for the page to load.");
        }
        vt.Arm = vt.Arm.ArmedBy(false, at, now);
        vt.Phase = WebVtPhase.Unprepared;
        vt.PrepareAttempts = 0;
        if (vt.Reading.Ok) Prepare(source, vt, now);
        ArmFast();
        return ActionResult.Done($"{name}: ARMED at {WebVt.TimeText(at)} — plays from there when it goes to air.");
    }

    /// <summary>MARK: the start point set without arming — a time, or where the player is now.</summary>
    public ActionResult Mark(string key, IWebSource source, string name, string value, DateTime? nowUtc = null)
    {
        var vt = VtFor(key);
        double at;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (WebVt.IsOff(value)) return Arm(key, source, name, value, nowUtc);
            if (!WebVt.TryParseTime(value, out at)) return ActionResult.Refused($"'{value}' is not a point in a video — 1:23, 83 or 1m23s.");
        }
        else if (vt.Reading.Ok)
        {
            at = vt.Reading.Position;
        }
        else
        {
            return ActionResult.Refused($"{name} has no video player answering yet — give a time (WEB MARK 1:23).");
        }
        vt.Arm = vt.Arm with { StartSeconds = Math.Max(0, at), ByLook = false };
        vt.Phase = WebVtPhase.Unprepared;
        vt.PrepareAttempts = 0;
        if (vt.Arm.Armed && vt.Reading.Ok && !vt.OnAir) Prepare(source, vt, nowUtc ?? DateTime.UtcNow);
        return ActionResult.Done(vt.Arm.Armed
            ? $"{name}: mark {WebVt.TimeText(at)} — armed, plays from there when it goes to air."
            : $"{name}: mark {WebVt.TimeText(at)} — ARM plays from there when it goes to air.");
    }

    private void Prepare(IWebSource source, PageVt vt, DateTime now)
    {
        try
        {
            source.RunScript(WebVt.PrepareScript(vt.Service, vt.Arm.StartSeconds));
        }
        catch (Exception ex)
        {
            Log.Warn("The armed page could not be put at its mark.", ex);
        }
        vt.Phase = WebVtPhase.PrepareRequested;   // requested: the reading says when the player is paused at the mark
        vt.RequestedUtc = now;
        vt.PrepareAttempts++;
        vt.PreparedUnderAd = vt.Reading.AdShowing;
    }

    /// <summary>Where a page's player stands as it reported: the desk's, STATE's and Companion's words read it, never the request.</summary>
    public WebVtPhase PhaseOf(string key) => _vts.TryGetValue(key, out var vt) ? vt.Phase : WebVtPhase.Unprepared;

    /// <summary>
    /// The poll (UI thread, once a second from the desk; four times a second by itself while
    /// something is pending): every page's player read, an advert skipped where the site allows,
    /// an armed page put at its mark once its player answers, a play that was due landed.
    /// </summary>
    public void Poll(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        foreach (var (key, page) in _pages.ToList())
        {
            var vt = VtFor(key);
            if (vt.ReadingPending) continue;
            vt.ReadingPending = true;
            _ = ReadAsync(key, page.Source, vt, now);
        }
    }

    private async Task ReadAsync(string key, IWebSource source, PageVt vt, DateTime now)
    {
        try
        {
            string result;
            try
            {
                result = await source.RunScriptAsync(WebVt.StateScript(vt.Service));
            }
            catch (Exception ex)
            {
                Log.Warn("Reading a page's player failed.", ex);
                return;
            }
            if (!_pages.ContainsKey(key)) return;   // retired while it was being asked
            var reading = WebVt.ParseReading(result);
            var adGone = vt.Reading.AdShowing && !reading.AdShowing;
            vt.Reading = reading;
            vt.ReadingUtc = now;
            var was = vt.Phase;
            vt.Phase = WebVt.Observe(vt.Phase, reading, vt.Phase == WebVtPhase.FireRequested ? vt.PendingFrom : vt.Arm.StartSeconds, vt.RequestedUtc, now);
            if (vt.Phase != was && vt.Phase == WebVtPhase.Failed) Log.Warn($"Web VT {key}: the player did not answer a {(was == WebVtPhase.FireRequested ? "play" : "prepare")} in time ({WebVt.PhaseWords(vt.Phase)}).");
            if (!reading.Ok) return;
            if (reading.AdShowing)
            {
                source.RunScript(WebVt.SkipAdScript(vt.Service));
                vt.AdSkips++;
            }
            if (vt.PendingPlay)
            {
                source.RunScript(WebVt.PlayFromScript(vt.Service, vt.PendingFrom));
                vt.PendingPlay = false;
                return;
            }
            if (vt.Arm.Armed && !vt.OnAir && (vt.PrepareDue || (vt.PreparedUnderAd && adGone)))
            {
                Prepare(source, vt, now);
            }
        }
        finally
        {
            vt.ReadingPending = false;
            ArmFast();
        }
    }

    /// <summary>Something is pending on a page: an arm to prepare, a play to land, an advert showing.</summary>
    private bool Pending()
    {
        foreach (var (key, vt) in _vts)
        {
            if (!_pages.ContainsKey(key)) continue;
            if (vt.PendingPlay || vt.Reading.AdShowing || vt.Phase is WebVtPhase.PrepareRequested or WebVtPhase.FireRequested || (vt.Arm.Armed && !vt.OnAir && vt.PrepareDue)) return true;
        }
        return false;
    }

    /// <summary>The fast poll runs only while something is pending — a desk with nothing armed pays nothing.</summary>
    private void ArmFast()
    {
        var wanted = Pending();
        if (wanted && _fast is null && Dispatcher.UIThread.CheckAccess())
        {
            _fast = global::Patterns.App.Services.DeskTimers.Make(FastPoll);
            _fast.Tick += (_, _) =>
            {
                if (!Pending())
                {
                    _fast?.Stop();
                    _fast = null;
                    return;
                }
                Poll();
            };
            _fast.Start();
        }
        else if (!wanted && _fast is not null)
        {
            _fast.Stop();
            _fast = null;
        }
    }

    /// <summary>The Show page's and the phone's short word for what is armed anywhere on the desk: "VT armed at 1:23 (Sponsor)"; "" with nothing armed.</summary>
    public string ArmedWords(Func<string, string> nameOf)
    {
        foreach (var (key, arm, reading) in Armed())
        {
            return $"{WebVt.ShortWords(arm, reading, PhaseOf(key))} ({nameOf(key)})";
        }
        return "";
    }

    private void Retire(string key)
    {
        var page = _pages[key];
        _pages.Remove(key);
        _vts.Remove(key);   // the arm was on this browser; the next one starts clean
        InputBus.Unmount(key);
        InputBus.SetPrevious(key, page.Source);
        _retired.Add((key, page.Source, DateTime.UtcNow));
    }

    private static bool Supported(out string note)
    {
        if (!OperatingSystem.IsWindows())
        {
            note = "Web pages inside the engine need Windows (WebView2).";
            return false;
        }
        return WebFrameSource.Probe(out note);
    }

    private IWebSource? Open(MediaLocator.WantedInput w)
    {
        if (!OperatingSystem.IsWindows()) return null;
        // A page opening straight onto the air with a start point takes it in the address where the
        // service allows, so the first frame is the right one; the mount key stays the pattern's own.
        var address = w.AutoPlay && OnAir(w) ? WebVt.AddressWithStart(w.Target, w.Service, w.StartSeconds) : w.Target;
        return WebFrameSource.Create(address == w.Target ? w : w with { Target = address }, _userDataFolder);
    }

    private static void Dispose(IWebSource source)
    {
        try
        {
            (source as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Web page close issue.", ex);
        }
    }

    public void Dispose()
    {
        _fast?.Stop();
        _fast = null;
        foreach (var (key, page) in _pages)
        {
            InputBus.Unmount(key);
            Dispose(page.Source);
        }
        _pages.Clear();
        _vts.Clear();
        foreach (var (key, source, _) in _retired)
        {
            InputBus.SetPrevious(key, null);
            Dispose(source);
        }
        _retired.Clear();
    }
}
