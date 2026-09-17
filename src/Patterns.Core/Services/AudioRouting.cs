using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>Decibels, the way a desk reads them: 0 is unity, −60 and below is off, +12 is the ceiling.</summary>
public static class Db
{
    public const double Floor = -60;
    public const double Ceiling = 12;

    /// <summary>A level as the matrix stores it: −60…+12, NaN read as 0.</summary>
    public static double ClampLevel(double db) => double.IsFinite(db) ? Math.Clamp(db, Floor, Ceiling) : 0;

    /// <summary>The linear gain for a level: exactly 0 at the floor and below, so "−60" is silence and not a whisper.</summary>
    public static double ToGain(double db)
    {
        if (!double.IsFinite(db) || db <= Floor) return 0;
        return Math.Pow(10, Math.Min(db, Ceiling) / 20.0);
    }

    /// <summary>The level for a linear gain; the floor for silence.</summary>
    public static double FromGain(double gain)
    {
        if (!double.IsFinite(gain) || gain <= 0) return Floor;
        return Math.Max(Floor, 20 * Math.Log10(gain));
    }

    /// <summary>A duck level given as a share of the volume (the show's DuckPct) as dB.</summary>
    public static double FromPercent(double pct) => FromGain(Math.Clamp(pct, 0, 100) / 100.0);

    /// <summary>"0 dB", "−6 dB", "+3 dB", "off".</summary>
    public static string Text(double db)
    {
        if (db <= Floor) return "off";
        var r = Math.Round(db, 1);
        var sign = r > 0 ? "+" : r < 0 ? "−" : "";
        return $"{sign}{Math.Abs(r):0.#} dB";
    }
}

/// <summary>What kind of thing a source is — the colour it wears on the matrix.</summary>
public enum RoutedSourceKind
{
    /// <summary>The programme's own sound: the clip or page the room is watching.</summary>
    Programme,
    /// <summary>One screen's own picture — its clip's or its page's soundtrack.</summary>
    Screen,
    /// <summary>The sandboxed preview's sound.</summary>
    Preview,
    /// <summary>The audio playlist.</summary>
    Music,
    /// <summary>VOG announcements.</summary>
    Vog,
    /// <summary>Stinger sounds.</summary>
    Sting,
    /// <summary>The soundcheck tone.</summary>
    Tone,
}

public enum AudioDestinationKind
{
    /// <summary>A Windows output by name — a sound card, an HDMI screen's audio, the computer's own.</summary>
    Device,
    /// <summary>An NDI send's embedded audio.</summary>
    Ndi,
}

/// <summary>A source as the matrix lists it.</summary>
public sealed record AudioSourceInfo(string Id, string Label, RoutedSourceKind Kind);

/// <summary>A destination as the matrix lists it: configured (a row in the show) or merely present on this machine.</summary>
public sealed record AudioDestinationInfo(string Key, string Label, AudioDestinationKind Kind, bool Configured, bool Present);

/// <summary>One lane of a destination's plan: a source at a gain, the VOG policy already applied; <see cref="Followed"/> when the picture made the route rather than a row (round 69).</summary>
public readonly record struct AudioLane(string Source, double LevelDb, double Gain, bool Followed = false);

/// <summary>
/// A route the picture makes (round 69): the sound of what a screen shows, on the output the screen
/// names — the programme's while it shows the programme, its own picture's while it shows one of
/// its own, the repeated target's while it repeats one. Derived, never stored; it moves with the take.
/// </summary>
public sealed record FollowedRoute(string ScreenId, string Source, string Destination);

/// <summary>
/// A crosspoint as the matrix reads it now: the operator's own row, or one the picture made where
/// no row names that crosspoint. The operator's row wins — a level, a mute or a row switched off
/// stands whatever the picture is doing.
/// </summary>
public sealed record EffectiveRoute(string Source, string Destination, double LevelDb, bool Enabled, bool Followed, string ScreenId = "");

/// <summary>What one destination should be doing right now.</summary>
public sealed record AudioDestinationPlan(string Key, string Label, AudioDestinationKind Kind, int DelayMs, bool Mute, AudioVogMode VogMode, IReadOnlyList<AudioLane> Lanes)
{
    /// <summary>The gain a source has here; 0 when it is not routed.</summary>
    public double GainFor(string source)
    {
        foreach (var lane in Lanes)
        {
            if (lane.Source == source) return lane.Gain;
        }
        return 0;
    }

    public bool Carries(string source) => Lanes.Any(l => l.Source == source);
}

/// <summary>A place to open a sound on: the device, the gain to open it at, its delay.</summary>
public readonly record struct AudioOutputPick(string Device, double Gain, int DelayMs);

/// <summary>Where a clip's soundtrack should go, as the decoder can honour it.</summary>
public enum ClipAudioPath
{
    /// <summary>The matrix is off: the two wires of round 26 (the programme's device, the monitor, or silence).</summary>
    Classic,
    /// <summary>One device wants it: the decoder plays to that device itself.</summary>
    Device,
    /// <summary>Several destinations, or an NDI send, want it: the decoder's sound goes through the mixer.</summary>
    Mixer,
    /// <summary>Nothing routes it: silent.</summary>
    Silent,
}

public readonly record struct ClipAudioRoute(ClipAudioPath Path, string Device, double Gain, IReadOnlyList<AudioLane> Lanes);

/// <summary>
/// The duck's envelope: a first-order approach to its target — fast on the way down (the attack),
/// slow on the way back (the release) — so an announcement lands over the music without a click
/// and the music breathes back in rather than jumping. Pure arithmetic over a time step, so a slow
/// poll lands in the right place and never overshoots. The physics of a capacitor, not a ramp.
/// </summary>
public sealed class DuckEnvelope
{
    public double Value { get; private set; } = 1.0;

    /// <summary>Moves toward <paramref name="target"/> over <paramref name="dtSeconds"/>; the time constant is the attack going down, the release coming up.</summary>
    public double Advance(double target, double dtSeconds, int attackMs, int releaseMs)
    {
        target = Math.Clamp(target, 0, 1);
        if (dtSeconds <= 0) return Value;
        var tauMs = target < Value ? Math.Max(1, attackMs) : Math.Max(1, releaseMs);
        // A time constant reaches 63 % of the way in tau; the desk's ear reads "landed" at three.
        var k = 1 - Math.Exp(-dtSeconds * 1000.0 / (tauMs / 3.0));
        Value += (target - Value) * k;
        if (Math.Abs(target - Value) < 0.0005) Value = target;
        return Value;
    }

    public void Reset(double value = 1.0) => Value = Math.Clamp(value, 0, 1);
}

/// <summary>
/// Which soundtrack goes where — the matrix behind the Audio page's ROUTING area.
///
/// Sources are the things that make sound on a desk: the programme's own soundtrack (the clip or
/// the page the room is watching), each screen's own picture, the preview, the playlist, VOGs,
/// stingers and the tone. Destinations are the places sound can leave: every Windows output by name
/// (a sound card feeding the room's desk, each HDMI screen's audio, the computer's own) and every
/// NDI send. A crosspoint puts a source on a destination at a level in dB; a destination has a trim,
/// a lip-sync delay, a mute and its own answer to a VOG — duck the rest, replace them, or leave the
/// announcement out. Resolved to a plan of linear gains the App applies where Windows and the
/// decoders allow. Off, the desk keeps round 26's two wires exactly. Pure; tested.
/// </summary>
public static class AudioRouting
{
    public const string Programme = "programme";
    public const string Preview = "preview";
    public const string Music = "music";
    public const string Vog = "vog";
    public const string Sting = "sting";
    public const string Tone = "tone";

    public const string DevicePrefix = "dev:";
    public const string NdiPrefix = "ndi:";
    public const string ScreenPrefix = "screen:";

    /// <summary>The computer's own output as a destination key.</summary>
    public const string ComputerOutput = "(computer output)";

    public static string ScreenSource(string targetId) => ScreenPrefix + targetId;
    public static string DeviceDestination(string name) => DevicePrefix + name;
    public static string NdiDestination(string senderId) => NdiPrefix + senderId;

    public static bool IsDevice(string key) => key.StartsWith(DevicePrefix, StringComparison.Ordinal);
    public static bool IsNdi(string key) => key.StartsWith(NdiPrefix, StringComparison.Ordinal);

    /// <summary>The device's name from a device key ("" for a key that is not a device).</summary>
    public static string DeviceName(string key) => IsDevice(key) ? key[DevicePrefix.Length..] : "";

    public static string NdiId(string key) => IsNdi(key) ? key[NdiPrefix.Length..] : "";

    public static RoutedSourceKind KindOf(string sourceId) => sourceId switch
    {
        Programme => RoutedSourceKind.Programme,
        Preview => RoutedSourceKind.Preview,
        Music => RoutedSourceKind.Music,
        Vog => RoutedSourceKind.Vog,
        Sting => RoutedSourceKind.Sting,
        Tone => RoutedSourceKind.Tone,
        _ => RoutedSourceKind.Screen,
    };

    /// <summary>Every source the show has right now: the fixed ones, and a screen for each target with a picture of its own.</summary>
    public static IReadOnlyList<AudioSourceInfo> Sources(ShowState state)
    {
        var list = new List<AudioSourceInfo>
        {
            new(Programme, "Programme", RoutedSourceKind.Programme),
        };
        foreach (var p in state.Output.Placements)
        {
            if (!p.UseCustomPattern) continue;
            list.Add(new AudioSourceInfo(ScreenSource(p.ScreenId), AudioMonitorRule.LabelFor(state, p.ScreenId), RoutedSourceKind.Screen));
        }
        foreach (var c in state.Output.CanvasNames)
        {
            if (!c.UseCustomPattern) continue;
            list.Add(new AudioSourceInfo(ScreenSource(c.MemberKey), AudioMonitorRule.LabelFor(state, c.MemberKey), RoutedSourceKind.Screen));
        }
        list.Add(new AudioSourceInfo(Preview, "Preview", RoutedSourceKind.Preview));
        list.Add(new AudioSourceInfo(Music, "Music (playlist)", RoutedSourceKind.Music));
        list.Add(new AudioSourceInfo(Vog, "VOG", RoutedSourceKind.Vog));
        list.Add(new AudioSourceInfo(Sting, "Stingers", RoutedSourceKind.Sting));
        list.Add(new AudioSourceInfo(Tone, "Tone", RoutedSourceKind.Tone));
        return list;
    }

    /// <summary>A source's label, or its id when the show has no such source any more.</summary>
    public static string SourceLabel(ShowState state, string sourceId)
        => Sources(state).FirstOrDefault(s => s.Id == sourceId)?.Label ?? (sourceId.StartsWith(ScreenPrefix, StringComparison.Ordinal) ? AudioMonitorRule.LabelFor(state, sourceId[ScreenPrefix.Length..]) : sourceId);

    // ---- the sound follows the picture (round 69) ------------------------------------------------

    /// <summary>
    /// The source a screen's picture makes right now: the programme while the screen shows the
    /// programme; its own picture's soundtrack (<c>screen:&lt;id&gt;</c>) while it shows one of its own;
    /// the joined canvas's while it is a member of a canvas with a picture of its own; and a
    /// repeater's is the repeated target's, to the end of the mirror chain. Null for a screen the
    /// rig has not got. The rule the matrix's derived routes and the Screens page's words share.
    /// </summary>
    public static string? SourceOfScreen(ShowState state, string screenId)
    {
        if (string.IsNullOrEmpty(screenId)) return null;
        if (state.Output.Placements.All(p => p.ScreenId != screenId)) return null;
        var target = ScreenRoles.ResolveMirror(state, screenId);
        if (!ContentTargets.IsCanvasKey(target))
        {
            // A member of a joined canvas with a picture of its own shows the canvas's picture, not its own.
            foreach (var c in state.Output.CanvasNames)
            {
                if (!c.UseCustomPattern) continue;
                if (ContentTargets.Members(c.MemberKey).Contains(target, StringComparer.Ordinal)) return ScreenSource(c.MemberKey);
            }
        }
        return ContentTargets.UsesOwnPattern(state, target) ? ScreenSource(target) : Programme;
    }

    /// <summary>
    /// The routes the picture makes: one for every enabled screen that names a sound output, from
    /// the source its picture makes now to that output — none while the show does not let the
    /// sound follow the picture. Read at every plan, so a TAKE that changes what a screen shows
    /// changes what its output carries without a row being touched.
    /// </summary>
    public static IReadOnlyList<FollowedRoute> FollowedRoutes(ShowState state) => FollowedRoutes(state, state);

    /// <summary>
    /// Round 72: the configuration and the picture apart. The rows, the follow switch and which screen
    /// names which output are the show's configuration (the editable state — an operator's row applies
    /// live); what each screen shows is the picture the audience has (the on-air state — a frozen clone
    /// while EDIT SAFE is open). An edit in the preview moves no sound until it is taken.
    /// </summary>
    public static IReadOnlyList<FollowedRoute> FollowedRoutes(ShowState config, ShowState picture)
    {
        var list = new List<FollowedRoute>();
        if (!config.AudioRouting.FollowPicture) return list;
        foreach (var p in config.Output.Placements)
        {
            if (!p.Enabled || p.AudioOutput.Length == 0) continue;
            var source = SourceOfScreen(picture, p.ScreenId);
            if (source is null) continue;
            list.Add(new FollowedRoute(p.ScreenId, source, p.AudioOutput));
        }
        return list;
    }

    /// <summary>
    /// Every crosspoint the matrix reads now: the operator's rows first, in their order, then the
    /// routes the picture makes where no row names the same crosspoint (at 0 dB — the row's trim
    /// still applies). A row for the crosspoint, on or off, is the operator's word and wins.
    /// </summary>
    public static IReadOnlyList<EffectiveRoute> EffectiveRoutes(ShowState state) => EffectiveRoutes(state, state);

    /// <summary>As above, with the picture the audience has apart from the configuration (round 72).</summary>
    public static IReadOnlyList<EffectiveRoute> EffectiveRoutes(ShowState config, ShowState picture)
    {
        var list = new List<EffectiveRoute>();
        foreach (var r in config.AudioRouting.Routes)
        {
            if (r.Source.Length == 0 || r.Destination.Length == 0) continue;
            list.Add(new EffectiveRoute(r.Source, r.Destination, r.LevelDb, r.Enabled, false));
        }
        foreach (var f in FollowedRoutes(config, picture))
        {
            if (list.Any(e => string.Equals(e.Source, f.Source, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Destination, f.Destination, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new EffectiveRoute(f.Source, f.Destination, 0, true, true, f.ScreenId));
        }
        return list;
    }

    /// <summary>The route the picture makes for a crosspoint, or null — the matrix's cell reads it to say "follows the picture".</summary>
    public static FollowedRoute? FollowedRoute(ShowState state, string source, string destination) => FollowedRoute(state, state, source, destination);

    /// <summary>As above, over the picture the audience has (round 72).</summary>
    public static FollowedRoute? FollowedRoute(ShowState config, ShowState picture, string source, string destination)
        => FollowedRoutes(config, picture).FirstOrDefault(f => string.Equals(f.Source, source, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Destination, destination, StringComparison.OrdinalIgnoreCase));

    /// <summary>A hash of the routes the picture makes now: the audio graph folds it into its topology signature, so a take rebuilds the lanes and a quiet tick does not.</summary>
    public static long FollowSignature(ShowState state) => FollowSignature(state, state);

    /// <summary>As above, over the picture the audience has: a take moves it, an edit in the preview does not (round 72).</summary>
    public static long FollowSignature(ShowState config, ShowState picture)
    {
        var h = new HashCode();
        h.Add(config.AudioRouting.FollowPicture);
        foreach (var f in FollowedRoutes(config, picture))
        {
            h.Add(f.ScreenId);
            h.Add(f.Source);
            h.Add(f.Destination);
        }
        return h.ToHashCode();
    }

    /// <summary>"the programme" / "its own picture" / "the canvas Wall's picture" / "Main's picture (repeated)" — what a screen's sound is, for the desk's words.</summary>
    public static string SourceOfScreenWords(ShowState state, string screenId)
    {
        var source = SourceOfScreen(state, screenId);
        if (source is null) return "no such screen";
        if (source == Programme) return "the programme";
        var target = source[ScreenPrefix.Length..];
        var repeated = ScreenRoles.ResolveMirror(state, screenId) != screenId;
        if (target == screenId) return "its own picture";
        var label = AudioMonitorRule.LabelFor(state, target);
        return ContentTargets.IsCanvasKey(target) && !repeated ? $"the canvas {label}'s picture" : $"{label}'s picture{(repeated ? " (repeated)" : "")}";
    }

    /// <summary>
    /// The Audio page's second line and the wire's word: "Sound follows the picture on 3 screens:
    /// Main → Room desk (the programme), Info → Info HDMI (its own picture), Stage left → Stage HDMI
    /// (Main's picture, repeated)." / "… — no screen names a sound output yet (Screens page → Sound out)." / "…: off — the rows alone."
    /// </summary>
    public static string FollowWords(ShowState state) => FollowWords(state, state);

    /// <summary>As above, with the picture the audience has apart (round 72): the words say what the room hears, never what the preview would.</summary>
    public static string FollowWords(ShowState config, ShowState picture)
    {
        var state = config;
        if (!state.AudioRouting.FollowPicture) return "Sound follows the picture: off — the matrix is the rows alone.";
        var followed = FollowedRoutes(config, picture);
        if (followed.Count == 0) return "Sound follows the picture — no screen names a sound output yet (Screens page → Sound out, or SCREEN n AUDIO <output>).";
        var parts = followed.Select(f => $"{AudioMonitorRule.LabelFor(state, f.ScreenId)} → {DestinationLabel(state, f.Destination)} ({SourceOfScreenWords(picture, f.ScreenId)})");
        return $"Sound follows the picture on {followed.Count} screen{(followed.Count == 1 ? "" : "s")}: {string.Join(", ", parts)}.";
    }

    /// <summary>
    /// Every destination: the show's configured rows first, in their order, then every output this
    /// machine has and every NDI send the show runs that has no row yet — present but unrouted, so
    /// the operator sees what could be routed to. A configured device the machine has not got is
    /// kept and marked absent.
    /// </summary>
    public static IReadOnlyList<AudioDestinationInfo> Destinations(ShowState state, IReadOnlyList<string> devicesNow)
    {
        var list = new List<AudioDestinationInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in state.AudioRouting.Destinations)
        {
            if (d.Key.Length == 0 || !seen.Add(d.Key)) continue;
            var present = IsDevice(d.Key)
                ? DeviceName(d.Key) == ComputerOutput || devicesNow.Any(n => string.Equals(n, DeviceName(d.Key), StringComparison.OrdinalIgnoreCase))
                : state.Ndi.Senders.Any(s => s.Id == NdiId(d.Key) && s.Enabled);
            list.Add(new AudioDestinationInfo(d.Key, DestinationLabel(state, d.Key), IsNdi(d.Key) ? AudioDestinationKind.Ndi : AudioDestinationKind.Device, true, present));
        }
        var computer = DeviceDestination(ComputerOutput);
        if (seen.Add(computer)) list.Add(new AudioDestinationInfo(computer, "Computer audio output", AudioDestinationKind.Device, false, true));
        foreach (var name in devicesNow)
        {
            var key = DeviceDestination(name);
            if (seen.Add(key)) list.Add(new AudioDestinationInfo(key, name, AudioDestinationKind.Device, false, true));
        }
        foreach (var sender in state.Ndi.Senders)
        {
            if (!sender.Enabled) continue;
            var key = NdiDestination(sender.Id);
            if (seen.Add(key)) list.Add(new AudioDestinationInfo(key, "NDI " + sender.Name, AudioDestinationKind.Ndi, false, true));
        }
        return list;
    }

    /// <summary>The name a destination wears: the operator's label, else the device's name or "NDI &lt;send&gt;".</summary>
    public static string DestinationLabel(ShowState state, string key)
    {
        var row = state.AudioRouting.Destinations.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        if (row is { Label.Length: > 0 }) return row.Label;
        if (IsDevice(key)) return DeviceName(key) == ComputerOutput ? "Computer audio output" : DeviceName(key);
        if (IsNdi(key))
        {
            var sender = state.Ndi.Senders.FirstOrDefault(s => s.Id == NdiId(key));
            return "NDI " + (sender is null ? NdiId(key) : sender.Name);
        }
        return key;
    }

    /// <summary>The configured row for a key, or null.</summary>
    public static AudioDestinationConfig? Row(ShowState state, string key)
        => state.AudioRouting.Destinations.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The row for a key, made if the show has none yet (a route to a destination is what makes it a row).</summary>
    public static AudioDestinationConfig EnsureRow(ShowState state, string key)
    {
        var row = Row(state, key);
        if (row is not null) return row;
        row = new AudioDestinationConfig { Key = key };
        state.AudioRouting.Destinations.Add(row);
        return row;
    }

    // ---- the matrix's edits ----------------------------------------------------------------

    /// <summary>The crosspoint for a source on a destination, or null.</summary>
    public static AudioRouteConfig? Route(ShowState state, string source, string destination)
        => state.AudioRouting.Routes.FirstOrDefault(r => string.Equals(r.Source, source, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Destination, destination, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts a source on a destination at a level (0 dB unless said); an existing crosspoint keeps its place and takes the level.</summary>
    public static AudioRouteConfig SetRoute(ShowState state, string source, string destination, double levelDb = 0)
    {
        EnsureRow(state, destination);
        var route = Route(state, source, destination);
        if (route is null)
        {
            route = new AudioRouteConfig { Source = source, Destination = destination };
            state.AudioRouting.Routes.Add(route);
        }
        route.LevelDb = levelDb;
        route.Enabled = true;
        return route;
    }

    /// <summary>Takes a source off a destination; false when it was not there.</summary>
    public static bool ClearRoute(ShowState state, string source, string destination)
    {
        var route = Route(state, source, destination);
        if (route is null) return false;
        state.AudioRouting.Routes.Remove(route);
        return true;
    }

    /// <summary>
    /// The matrix's first table when it is switched on with nothing in it: audio follows video. The
    /// programme, the music, VOGs, stingers and the tone on every programme output the show already
    /// names (else the computer's own), and the programme's sound with the music and VOGs on every
    /// NDI send — what the room hears, embedded. Screens with their own pictures and the preview
    /// start unrouted: those are the choices the matrix exists for.
    /// </summary>
    public static int SeedDefaults(ShowState state)
    {
        if (state.AudioRouting.Routes.Count > 0 || state.AudioRouting.Destinations.Count > 0) return 0;
        var made = 0;
        var devices = state.AudioPlayer.Devices.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        if (devices.Count == 0) devices.Add(ComputerOutput);
        foreach (var device in devices)
        {
            var key = DeviceDestination(device);
            var row = EnsureRow(state, key);
            row.DelayMs = state.AudioPlayer.DelayFor(device);
            foreach (var source in new[] { Programme, Music, Vog, Sting, Tone })
            {
                SetRoute(state, source, key);
                made++;
            }
        }
        foreach (var sender in state.Ndi.Senders)
        {
            if (!sender.Enabled) continue;
            var key = NdiDestination(sender.Id);
            EnsureRow(state, key);
            foreach (var source in new[] { Programme, Music, Vog })
            {
                SetRoute(state, source, key);
                made++;
            }
        }
        return made;
    }

    // ---- the plan ------------------------------------------------------------------------------

    /// <summary>The duck level for a destination in dB: its own, else the show's (a destination the picture alone routes to has no row and takes the show's).</summary>
    public static double DuckDbFor(ShowState state, AudioDestinationConfig? row)
        => row?.VogDuckDb ?? Db.FromPercent(state.Stingers.DuckPct);

    /// <summary>
    /// What every configured destination should carry right now. <paramref name="vogPlaying"/> is
    /// the ducker's key: with it, each destination applies its own VOG mode — the others down to the
    /// duck level, or to silence, or the VOG kept off it. The attack and release are not here: this
    /// is the target; the App's envelopes approach it.
    /// </summary>
    public static IReadOnlyList<AudioDestinationPlan> Resolve(ShowState state, bool vogPlaying) => Resolve(state, state, vogPlaying);

    /// <summary>The plan with the configuration and the picture apart (round 72): the rows, the trims and the VOG policy are the configuration's; what a screen's sound is comes from the picture the audience has.</summary>
    public static IReadOnlyList<AudioDestinationPlan> Resolve(ShowState config, ShowState picture, bool vogPlaying)
    {
        var state = config;
        var plans = new List<AudioDestinationPlan>();
        if (!state.AudioRouting.Enabled) return plans;
        var routes = EffectiveRoutes(config, picture);
        // The destinations: the rows in their order, then the outputs the picture alone routes to (round 69) — no row, the defaults.
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in state.AudioRouting.Destinations)
        {
            if (row.Key.Length > 0 && seen.Add(row.Key)) keys.Add(row.Key);
        }
        foreach (var route in routes)
        {
            if (route.Followed && seen.Add(route.Destination)) keys.Add(route.Destination);
        }
        foreach (var key in keys)
        {
            var row = Row(state, key);
            var lanes = new List<AudioLane>();
            var trim = row?.TrimDb ?? 0;
            var mute = row?.Mute ?? false;
            var vogMode = row?.VogMode ?? AudioVogMode.Duck;
            foreach (var route in routes)
            {
                if (!route.Enabled || !string.Equals(route.Destination, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (lanes.Any(l => string.Equals(l.Source, route.Source, StringComparison.OrdinalIgnoreCase))) continue;
                var db = route.LevelDb + trim;
                var gain = mute ? 0 : Db.ToGain(db);
                if (vogPlaying)
                {
                    var isVog = string.Equals(route.Source, Vog, StringComparison.OrdinalIgnoreCase);
                    switch (vogMode)
                    {
                        case AudioVogMode.Duck when !isVog:
                            gain *= Db.ToGain(DuckDbFor(state, row));
                            break;
                        case AudioVogMode.Replace when !isVog:
                            gain = 0;
                            break;
                        case AudioVogMode.Leave when isVog:
                            gain = 0;
                            break;
                    }
                }
                lanes.Add(new AudioLane(route.Source, db, gain, route.Followed));
            }
            plans.Add(new AudioDestinationPlan(key, DestinationLabel(state, key), IsNdi(key) ? AudioDestinationKind.Ndi : AudioDestinationKind.Device,
                row?.DelayMs ?? 0, mute, vogMode, lanes));
        }
        return plans;
    }

    /// <summary>The plan for one destination, or null when nothing routes to it (or the matrix is off).</summary>
    public static AudioDestinationPlan? PlanFor(ShowState state, string key, bool vogPlaying) => PlanFor(state, state, key, vogPlaying);

    /// <summary>As above, with the picture the audience has apart from the configuration (round 72).</summary>
    public static AudioDestinationPlan? PlanFor(ShowState config, ShowState picture, string key, bool vogPlaying)
        => Resolve(config, picture, vogPlaying).FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where a sound of the show's own — the playlist, a VOG, a stinger, the tone — opens: with the
    /// matrix off, the programme's outputs as they always were (the classic list, unity, each with
    /// its delay); with it on, every device destination routed for the source at the crosspoint's
    /// gain and the destination's delay (NDI sends are the mixer's, not a player's). The VOG policy
    /// is not applied here — it moves, and the players follow it through <see cref="Resolve"/> —
    /// except the one that never changes: a Leave destination never opens a VOG at all.
    /// </summary>
    public static IReadOnlyList<AudioOutputPick> OutputsFor(ShowState state, string source)
    {
        var picks = new List<AudioOutputPick>();
        if (!state.AudioRouting.Enabled)
        {
            var classic = state.AudioPlayer.Devices.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            if (classic.Count == 0) classic.Add(ComputerOutput);
            foreach (var device in classic) picks.Add(new AudioOutputPick(device, 1.0, state.AudioPlayer.DelayFor(device)));
            return picks;
        }
        foreach (var plan in Resolve(state, vogPlaying: false))
        {
            if (plan.Kind != AudioDestinationKind.Device) continue;
            if (source == Vog && plan.VogMode == AudioVogMode.Leave) continue;
            foreach (var lane in plan.Lanes)
            {
                if (!string.Equals(lane.Source, source, StringComparison.OrdinalIgnoreCase)) continue;
                picks.Add(new AudioOutputPick(DeviceName(plan.Key), lane.Gain, plan.DelayMs));
                break;
            }
        }
        return picks;
    }

    /// <summary>The device names a source opens on — the list the players take.</summary>
    public static IReadOnlyList<string> DeviceNamesFor(ShowState state, string source)
        => OutputsFor(state, source).Select(p => p.Device).ToList();

    /// <summary>The delay a source's output on a device leaves with: the destination's when the matrix is on, the classic table's otherwise.</summary>
    public static int DelayFor(ShowState state, string device)
    {
        if (state.AudioRouting.Enabled && Row(state, DeviceDestination(device)) is { } row) return row.DelayMs;
        return state.AudioPlayer.DelayFor(device);
    }

    /// <summary>
    /// The source a mounted picture's sound is: the programme when any of its buses is the
    /// programme, else the first screen of its own it is on, else the preview.
    /// </summary>
    public static string SourceForBuses(IReadOnlyList<MediaBus>? buses)
    {
        if (buses is null || buses.Count == 0) return Programme;
        foreach (var bus in buses)
        {
            if (bus.IsProgram) return Programme;
        }
        foreach (var bus in buses)
        {
            if (!bus.Preview && bus.OutputId.Length > 0) return ScreenSource(bus.OutputId);
        }
        return Preview;
    }

    /// <summary>
    /// Round 76: every source a mounted picture's sound is at once — the programme when one of its
    /// buses is the programme, and each screen of its own it plays on — so a clip that is the
    /// programme and a screen's own picture together is carried by both destinations' lanes rather
    /// than the screen's output falling silent; the preview when it is nowhere else. The first is
    /// <see cref="SourceForBuses"/>'s answer, so the words and the signature stay the same.
    /// </summary>
    public static IReadOnlyList<string> SourcesForBuses(IReadOnlyList<MediaBus>? buses)
    {
        if (buses is null || buses.Count == 0) return new[] { Programme };
        var list = new List<string>();
        foreach (var bus in buses)
        {
            if (bus.IsProgram && !list.Contains(Programme)) list.Add(Programme);
        }
        foreach (var bus in buses)
        {
            if (bus.Preview || bus.OutputId.Length == 0) continue;
            var source = ScreenSource(bus.OutputId);
            if (!list.Contains(source)) list.Add(source);
        }
        if (list.Count == 0) list.Add(Preview);
        return list;
    }

    /// <summary>
    /// Round 76: how long a picture's sound takes to leave a destination it is no longer routed to — the
    /// show's transition when it is on (the picture crossfades over it, and its sound with it), a short
    /// click-free release otherwise. A sound that leaves the programme leaves the room's outputs with it.
    /// </summary>
    public static int LeaveFadeMs(ShowState state)
        => state.Transition.Enabled ? Math.Max(LeaveFloorMs, (int)Math.Round(Math.Clamp(state.Transition.DurationMs, 0, 3000))) : LeaveFloorMs;

    /// <summary>The shortest leave: a cut still releases over this, so no click is heard.</summary>
    public const int LeaveFloorMs = 120;

    /// <summary>
    /// Where a clip's soundtrack goes, as a decoder can honour it: the classic two wires with the
    /// matrix off; the one device that wants it, played by the decoder itself; the mixer when
    /// several destinations or an NDI send want it; silence when nothing does.
    /// </summary>
    public static ClipAudioRoute ClipRoute(ShowState state, IReadOnlyList<MediaBus>? buses)
    {
        if (!state.AudioRouting.Enabled) return new ClipAudioRoute(ClipAudioPath.Classic, "", 1.0, Array.Empty<AudioLane>());
        var source = SourceForBuses(buses);
        var lanes = new List<AudioLane>();
        string device = "";
        var devices = 0;
        var ndi = 0;
        foreach (var plan in Resolve(state, vogPlaying: false))
        {
            var lane = plan.Lanes.FirstOrDefault(l => string.Equals(l.Source, source, StringComparison.OrdinalIgnoreCase));
            if (lane.Source is null) continue;
            lanes.Add(lane with { Source = plan.Key });   // the lane keyed by its destination, for the mixer
            if (plan.Kind == AudioDestinationKind.Ndi) ndi++;
            else
            {
                devices++;
                device = DeviceName(plan.Key);
            }
        }
        if (lanes.Count == 0) return new ClipAudioRoute(ClipAudioPath.Silent, "", 0, lanes);
        if (devices == 1 && ndi == 0) return new ClipAudioRoute(ClipAudioPath.Device, device, lanes[0].Gain, lanes);
        return new ClipAudioRoute(ClipAudioPath.Mixer, "", 1.0, lanes);
    }

    // ---- the words -----------------------------------------------------------------------------

    /// <summary>The Audio page's readout: off, or the count of what is routed where and how the VOG behaves.</summary>
    public static string Words(ShowState state)
    {
        var cfg = state.AudioRouting;
        if (!cfg.Enabled) return "Routing off — the programme's outputs carry the show's sound and the monitor the operator's, as before. Switch it on to choose which soundtrack goes where.";
        var rows = cfg.Destinations.Where(d => d.Key.Length > 0).ToList();
        if (rows.Count == 0 && !EffectiveRoutes(state).Any(r => r.Followed)) return "Routing on with nothing routed yet — everything is silent until a source is put on a destination (SEED DEFAULTS puts the show's sound on the programme's outputs).";
        var routes = cfg.Routes.Count(r => r.Enabled);
        var parts = new List<string> { $"Routing on: {routes} route{(routes == 1 ? "" : "s")} on {rows.Count} destination{(rows.Count == 1 ? "" : "s")}." };
        var replace = rows.Where(r => r.VogMode == AudioVogMode.Replace).Select(r => DestinationLabel(state, r.Key)).ToList();
        var leave = rows.Where(r => r.VogMode == AudioVogMode.Leave).Select(r => DestinationLabel(state, r.Key)).ToList();
        var duck = rows.Count - replace.Count - leave.Count;
        var vog = new List<string>();
        if (duck > 0) vog.Add($"ducks {duck}");
        if (replace.Count > 0) vog.Add("replaces on " + string.Join(", ", replace));
        if (leave.Count > 0) vog.Add("stays off " + string.Join(", ", leave));
        if (vog.Count > 0) parts.Add("A VOG " + string.Join(", ", vog) + ".");
        var effective = EffectiveRoutes(state);
        var followed = effective.Count(r => r.Followed);
        if (followed > 0) parts.Add($"The picture makes {followed} more.");
        var unrouted = Sources(state).Where(s => !effective.Any(r => r.Enabled && string.Equals(r.Source, s.Id, StringComparison.OrdinalIgnoreCase))).Select(s => s.Label).ToList();
        if (unrouted.Count > 0) parts.Add("Silent: " + string.Join(", ", unrouted) + ".");
        return string.Join(" ", parts);
    }

    /// <summary>A destination's own line: "Room desk · 0 dB · 40 ms · VOG ducks −20 dB · Programme 0 dB, Music −6 dB".</summary>
    public static string DestinationWords(ShowState state, AudioDestinationConfig row)
    {
        var parts = new List<string> { DestinationLabel(state, row.Key) };
        if (row.Mute) parts.Add("MUTED");
        if (Math.Abs(row.TrimDb) > 0.05) parts.Add("trim " + Db.Text(row.TrimDb));
        if (row.DelayMs > 0) parts.Add($"{row.DelayMs} ms");
        parts.Add(row.VogMode switch
        {
            AudioVogMode.Replace => "VOG replaces the rest",
            AudioVogMode.Leave => "VOG stays off it",
            _ => $"VOG ducks the rest to {Db.Text(DuckDbFor(state, row))}",
        });
        var lanes = EffectiveRoutes(state).Where(r => r.Enabled && string.Equals(r.Destination, row.Key, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{SourceLabel(state, r.Source)} {Db.Text(r.LevelDb)}{(r.Followed ? " (follows the picture)" : "")}").ToList();
        parts.Add(lanes.Count == 0 ? "nothing routed" : string.Join(", ", lanes));
        return string.Join(" · ", parts);
    }

    // ---- the words in ------------------------------------------------------------------------

    /// <summary>A source named by its id, its label, or a word of either ("programme", "music", "info", "screen 2"); null for none.</summary>
    public static string? FindSource(ShowState state, string word)
    {
        var t = (word ?? "").Trim();
        if (t.Length == 0) return null;
        var sources = Sources(state);
        foreach (var s in sources)
        {
            if (s.Id.Equals(t, StringComparison.OrdinalIgnoreCase) || s.Label.Equals(t, StringComparison.OrdinalIgnoreCase)) return s.Id;
        }
        if (t.Equals("program", StringComparison.OrdinalIgnoreCase) || t.Equals("pgm", StringComparison.OrdinalIgnoreCase)) return Programme;
        if (t.Equals("playlist", StringComparison.OrdinalIgnoreCase) || t.Equals("track", StringComparison.OrdinalIgnoreCase)) return Music;
        if (t.Equals("stinger", StringComparison.OrdinalIgnoreCase) || t.Equals("stingers", StringComparison.OrdinalIgnoreCase)) return Sting;
        var screen = t.StartsWith("screen ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("screen:", StringComparison.OrdinalIgnoreCase) ? t[7..].Trim() : t;
        foreach (var s in sources)
        {
            if (s.Kind != RoutedSourceKind.Screen) continue;
            var id = s.Id[ScreenPrefix.Length..];
            if (id.Equals(screen, StringComparison.OrdinalIgnoreCase) || s.Label.Equals(screen, StringComparison.OrdinalIgnoreCase)) return s.Id;
        }
        foreach (var s in sources)
        {
            if (s.Label.Contains(t, StringComparison.OrdinalIgnoreCase)) return s.Id;
        }
        return null;
    }

    /// <summary>A destination named by its key, its label, its device's name or a word of them ("Info HDMI", "NDI 1", "computer", "hdmi"); null for none.</summary>
    public static string? FindDestination(ShowState state, IReadOnlyList<string> devicesNow, string word)
    {
        var t = (word ?? "").Trim();
        if (t.Length == 0) return null;
        var all = Destinations(state, devicesNow);
        foreach (var d in all)
        {
            if (d.Key.Equals(t, StringComparison.OrdinalIgnoreCase) || d.Label.Equals(t, StringComparison.OrdinalIgnoreCase)) return d.Key;
            if (IsDevice(d.Key) && DeviceName(d.Key).Equals(t, StringComparison.OrdinalIgnoreCase)) return d.Key;
        }
        if (t.Equals("computer", StringComparison.OrdinalIgnoreCase) || t.Equals("default", StringComparison.OrdinalIgnoreCase)) return DeviceDestination(ComputerOutput);
        foreach (var d in all)
        {
            if (d.Label.Contains(t, StringComparison.OrdinalIgnoreCase) || d.Key.Contains(t, StringComparison.OrdinalIgnoreCase)) return d.Key;
        }
        return null;
    }

    /// <summary>"Info HDMI AT -6" → the destination word and the level; no AT = 0 dB.</summary>
    public static (string Destination, double LevelDb) ParseRouteValue(string value)
    {
        var t = (value ?? "").Trim();
        var at = t.LastIndexOf(" AT ", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return (t, 0);
        var level = t[(at + 4)..].Trim().Replace("−", "-").Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(level, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db)
            ? (t[..at].Trim(), Db.ClampLevel(db))
            : (t, double.NaN);
    }

    public static bool TryParseVogMode(string? word, out AudioVogMode mode)
    {
        mode = AudioVogMode.Duck;
        switch ((word ?? "").Trim().ToLowerInvariant())
        {
            case "duck": mode = AudioVogMode.Duck; return true;
            case "replace": case "override": case "solo": mode = AudioVogMode.Replace; return true;
            case "leave": case "off": case "none": case "skip": mode = AudioVogMode.Leave; return true;
            default: return false;
        }
    }
}
