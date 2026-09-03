using System.Collections.Concurrent;
using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Services;

/// <summary>
/// An immutable-by-convention copy of the show state, safe to read from any render thread.
/// Also memoises colour parsing so renderers never parse hex strings per frame.
/// </summary>
public sealed class ShowSnapshot
{
    private readonly ConcurrentDictionary<string, SKColor> _colorCache = new();

    public required ShowState State { get; init; }
    public required long Version { get; init; }

    /// <summary>
    /// Runtime-only: outputs draw their screen-number badge until this UTC time. Carried on
    /// the snapshot (not the serialized model) so it survives the clone to render threads
    /// without ever landing in settings files.
    /// </summary>
    public DateTime? IdentifyUntilUtc { get; init; }

    /// <summary>Runtime-only: what the playlist is showing right now (null = none).</summary>
    public PlaylistNow? PlaylistNow { get; init; }

    /// <summary>Runtime-only: on-screen channel-ident indicator ("LEFT", "RIGHT", "L+R"; empty = none).</summary>
    public string ToneIndicator { get; init; } = "";

    /// <summary>Runtime-only: live ticker text from the configured feed (empty = use static text).</summary>
    public string FeedText { get; init; } = "";

    /// <summary>Runtime-only: output windows are open (drives multiview tally).</summary>
    public bool OutputsLive { get; init; }

    /// <summary>
    /// Runtime-only: the version at which the last CUT was published. A sink that has not yet
    /// shown that version switches without a crossfade, whatever the transition setting says —
    /// so a cut stays a cut inside a bulk edit and on a sink that skipped a frame.
    /// </summary>
    public long CutAtVersion { get; init; }

    /// <summary>Runtime-only: a fade length (ms) requested for one publish — the recall that carried it.</summary>
    public int FadeOverrideMs { get; init; } = -1;

    /// <summary>The version the override belongs to; only the sink that starts its fade on that version uses it.</summary>
    public long FadeOverrideVersion { get; init; } = -1;

    /// <summary>The fade this snapshot asks for, in seconds: the override on its own version, else the show setting.</summary>
    public double FadeSecondsFor(long version)
        => FadeOverrideMs >= 0 && FadeOverrideVersion == version
            ? FadeOverrideMs / 1000.0
            : State.Transition.DurationMs / 1000.0;

    /// <summary>Fades are on for this snapshot: the setting, or a one-off override above zero.</summary>
    public bool FadesEnabled => State.Transition.Enabled || (FadeOverrideMs > 0 && FadeOverrideVersion == Version);

    public SKColor Color(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        return _colorCache.GetOrAdd(hex, static (h, fb) => ColorUtil.TryParse(h, out var c) ? c : fb, fallback);
    }

    private readonly ConcurrentDictionary<string, int> _transitionKeys = new();

    /// <summary>
    /// Identity of the content a sink shows — changes when a crossfade should run (pattern
    /// or media identity, blackout, playlist item), stays put across per-frame animation.
    /// Memoised per snapshot; compared only within this process.
    /// </summary>
    public int TransitionKeyFor(string? screenId)
    {
        return _transitionKeys.GetOrAdd(screenId ?? "", _ =>
        {
            var cfg = PatternFor(screenId);
            var json = JsonUtil.Serialize(cfg);
            var playlist = cfg.Kind == PatternKind.Media && cfg.Media.Source == MediaSource.Playlist
                ? PlaylistNow?.Path ?? ""
                : "";
            return HashCode.Combine(State.Blackout, json, playlist);
        });
    }

    /// <summary>
    /// The pattern a given sink should draw: a content target (a single screen, or a joined
    /// canvas by its member key) with "own pattern" on uses its assignment; everything else
    /// shows the program. Hot path: plain loops, no allocation.
    /// </summary>
    public PatternConfig PatternFor(string? targetId)
    {
        if (targetId is not null && ContentTargets.UsesOwnPattern(State, targetId))
        {
            foreach (var a in State.Independent)
            {
                if (a.ScreenId == targetId) return a.Pattern;
            }
        }
        return State.Pattern;
    }
}

public static class ColorUtil
{
    public static bool TryParse(string? hex, out SKColor color)
    {
        color = SKColors.Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        // Accept RGB, RRGGBB, AARRGGBB.
        if (s.Length == 3)
        {
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        }
        if (s.Length == 6)
        {
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
            color = new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        if (s.Length == 8)
        {
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var argb)) return false;
            color = new SKColor((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
            return true;
        }
        return false;
    }

    public static SKColor Parse(string? hex, SKColor fallback) => TryParse(hex, out var c) ? c : fallback;

    /// <summary>Splits a comma/space separated hex list; guarantees at least one colour.</summary>
    public static SKColor[] ParseList(string? csv, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new[] { fallback };
        var parts = csv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<SKColor>(parts.Length);
        foreach (var p in parts)
        {
            if (TryParse(p, out var c)) list.Add(c);
        }
        if (list.Count == 0) list.Add(fallback);
        return list.ToArray();
    }
}

/// <summary>
/// Publishes show-state snapshots to render sinks. The UI thread mutates <see cref="ShowState"/>;
/// every change publishes a fresh deep-cloned snapshot that render threads pick up via
/// a single volatile read — no locks anywhere on the render path.
/// </summary>
public sealed class SnapshotBus
{
    private volatile ShowSnapshot _current;
    private long _version;

    public SnapshotBus(ShowState initial)
    {
        _current = new ShowSnapshot { State = JsonUtil.Clone(initial), Version = 0 };
    }

    public ShowSnapshot Current => _current;

    /// <summary>Set by the publisher before <see cref="Publish"/> to flash screen badges.</summary>
    public DateTime? IdentifyUntilUtc { get; set; }

    /// <summary>Set by the playlist service; carried on every snapshot.</summary>
    public PlaylistNow? PlaylistNow { get; set; }

    /// <summary>Set by the audio service while channel ident runs.</summary>
    public string ToneIndicator { get; set; } = "";

    /// <summary>Set by the feed service; carried on every snapshot.</summary>
    public string FeedText { get; set; } = "";

    /// <summary>Set by the output manager; carried on every snapshot (multiview tally).</summary>
    public bool OutputsLive { get; set; }

    private bool _cutPending;

    private int _fadePendingMs = -1;

    private int _fadeMs = -1;

    private long _fadeVersion = -1;
    private long _cutVersion;

    /// <summary>
    /// The next published snapshot is a CUT: sinks switch to it without a crossfade. Survives a
    /// bulk edit (the flag waits for the publish) and a skipped frame (the version is carried
    /// on every later snapshot until each sink has seen it).
    /// </summary>
    public void CutOnNextPublish() => _cutPending = true;

    /// <summary>The next publish fades over this many milliseconds (a look's own transition), once.</summary>
    public void FadeOnNextPublish(int ms) => _fadePendingMs = Math.Max(0, ms);

    /// <summary>Raised on the publisher's (UI) thread after a new snapshot is available.</summary>
    public event Action? Changed;

    public void Publish(ShowState state)
    {
        _current = Build(state);
        Changed?.Invoke();
    }

    private volatile ShowSnapshot? _sandbox;

    /// <summary>
    /// While look programming is sandboxed, the preview renders this snapshot and every
    /// other sink stays on <see cref="Current"/> (the frozen program). Null = no sandbox.
    /// </summary>
    public ShowSnapshot? Sandbox => _sandbox;

    public void PublishSandbox(ShowState state)
    {
        _sandbox = Build(state);
        Changed?.Invoke();
    }

    public void ClearSandbox() => _sandbox = null;

    private ShowSnapshot Build(ShowState state)
    {
        var version = ++_version;
        if (_cutPending)
        {
            _cutVersion = version;
            _cutPending = false;
        }
        if (_fadePendingMs >= 0)
        {
            _fadeMs = _fadePendingMs;
            _fadeVersion = version;
            _fadePendingMs = -1;
        }
        return new ShowSnapshot
        {
            State = JsonUtil.Clone(state),
            Version = version,
            IdentifyUntilUtc = IdentifyUntilUtc,
            PlaylistNow = PlaylistNow,
            ToneIndicator = ToneIndicator,
            FeedText = FeedText,
            OutputsLive = OutputsLive,
            CutAtVersion = _cutVersion,
            FadeOverrideMs = _fadeMs,
            FadeOverrideVersion = _fadeVersion,
        };
    }
}

/// <summary>The playlist item currently on screen (immutable; carried on snapshots).</summary>
public sealed record PlaylistNow(string Path, bool IsVideo, int Index, int Count, DateTime StartedUtc, double DurationSeconds);
