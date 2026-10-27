using System.Globalization;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// One refused key's hold: the link's target (<c>node:&lt;id&gt;</c> or <c>addr:&lt;host&gt;:&lt;port&gt;</c>), the key's kid, its end in
/// wall time (ms since the epoch) and the word it was refused with — a refusal's class, or <c>busy</c> for a rate limit. <see cref="Public"/>
/// false: the refusal let no keyless link read either, so nothing at all is dialled inside it. <see cref="BusyUntilWallMs"/>: a rate
/// limit's own end inside a refusal's hold (0 when none), which a word that takes the key leaves standing.
/// </summary>
public sealed record LinkHold(string Target, string Kid, long UntilWallMs, string Word, bool Public = true, long BusyUntilWallMs = 0);

/// <summary>
/// Show Bus 1.3 §13.15.3: a refused key's hold outlasts a restart. Each hold is kept by its link's target and the key's kid — a
/// kid is not a secret (13.5.1) and is what 13.19.2 names a key by in any record — never by the key or a hash of it, with its end in
/// wall time, so a process started inside it presents that key no sooner than the one that was refused would have. A hold is
/// written before the caller's next I/O, dropped at its end, and dropped when the key changes, so the store stays bounded by the
/// targets the link has held keys for (<see cref="MostTargets"/> at most, the soonest to end giving way). Thread-safe: the link's
/// lanes write and read it. One hold a target and kid: a hold standing is never shortened — the later end stands, a refusal's word
/// stands over a rate limit's, and the public view may be read inside it only while no word that held it forbade that. Kept beside
/// the settings (<see cref="FileName"/>) when given a folder; a file that cannot be read is said once and the store starts empty,
/// and a write that fails leaves the hold in memory, said once.
/// </summary>
public sealed class LinkHolds
{
    /// <summary>The holds' file beside the settings.</summary>
    public const string FileName = "patterns.linkholds.json";

    /// <summary>The most holds kept at once.</summary>
    public const int MostTargets = 32;

    private readonly object _gate = new();
    private readonly List<LinkHold> _holds = new();
    private readonly string? _path;
    private bool _writeFaultSaid;

    /// <summary>A store in memory only.</summary>
    public LinkHolds()
    {
    }

    /// <param name="folder">Where the holds are kept across a restart (the settings' folder); they are read from there now.</param>
    public LinkHolds(string folder)
    {
        _path = Path.Combine(folder, FileName);
        Read();
    }

    /// <summary>The hold standing for <paramref name="target"/> and <paramref name="kid"/> at <paramref name="nowWallMs"/>, or null — an ended one is dropped.</summary>
    public LinkHold? For(string target, string kid, long nowWallMs)
    {
        lock (_gate)
        {
            if (DropEnded(nowWallMs)) Write();
            return _holds.FirstOrDefault(h => h.Target == target && h.Kid == kid);
        }
    }

    /// <summary>Every hold standing at <paramref name="nowWallMs"/>.</summary>
    public IReadOnlyList<LinkHold> All(long nowWallMs)
    {
        lock (_gate)
        {
            if (DropEnded(nowWallMs)) Write();
            return _holds.ToList();
        }
    }

    /// <summary>
    /// Records a hold for the target and kid, merged with one standing there (the later end, a refusal's word over <c>busy</c>, the
    /// public view only while both let it be read), and writes the store before the caller does anything else. A hold already ended
    /// is not kept.
    /// </summary>
    public void Hold(string target, string kid, long untilWallMs, string word, long nowWallMs, bool publicView = true)
    {
        if (target.Length == 0 || !PeerLink.IsKid(kid) || untilWallMs <= nowWallMs) return;
        lock (_gate)
        {
            DropEnded(nowWallMs);
            var busy = word == "busy" ? untilWallMs : 0;
            var hold = new LinkHold(target, kid, untilWallMs, word, publicView, busy);
            if (_holds.FindIndex(h => h.Target == target && h.Kid == kid) is var i and >= 0)
            {
                var old = _holds[i];
                hold = new LinkHold(target, kid, Math.Max(old.UntilWallMs, untilWallMs), word == "busy" ? old.Word : word, old.Public && publicView,
                    Math.Max(old.BusyUntilWallMs, busy));
                _holds.RemoveAt(i);
            }
            _holds.Add(hold);
            while (_holds.Count > MostTargets) _holds.Remove(_holds.MinBy(h => h.UntilWallMs)!);
            Write();
        }
    }

    /// <summary>
    /// A word took the key at <paramref name="target"/>, or the operator lifted the hold: a refusal's hold for the target and kid is
    /// over, and a rate limit's standing inside it goes on to its own end.
    /// </summary>
    public void Taken(string target, string kid, long nowWallMs)
    {
        lock (_gate)
        {
            var i = _holds.FindIndex(h => h.Target == target && h.Kid == kid);
            if (i < 0 || _holds[i].Word == "busy") return;
            var busy = _holds[i].BusyUntilWallMs;
            _holds.RemoveAt(i);
            if (busy > nowWallMs) _holds.Add(new LinkHold(target, kid, busy, "busy", true, busy));
            Write();
        }
    }

    /// <summary>The link holds <paramref name="kid"/> now (null: no key): every hold of another key is over, and so is every ended one.</summary>
    public void KeyIs(string? kid, long nowWallMs)
    {
        lock (_gate)
        {
            var dropped = DropEnded(nowWallMs);
            dropped |= _holds.RemoveAll(h => h.Kid != kid) > 0;
            if (dropped) Write();
        }
    }

    /// <summary>Drops every hold ended by <paramref name="nowWallMs"/> (a hold is over at its end); true when one went. Under the gate.</summary>
    private bool DropEnded(long nowWallMs) => _holds.RemoveAll(h => h.UntilWallMs <= nowWallMs) > 0;

    /// <summary>
    /// The holds as kept: target, kid, end and word — and only when they say something, <c>public</c> false and a rate limit's own end
    /// (<c>busy</c>) inside a refusal's hold — nothing else. Under the gate.
    /// </summary>
    private void Write()
    {
        if (_path is null) return;
        var json = JsonSerializer.Serialize(new
        {
            v = 1,
            holds = _holds.Select(h =>
            {
                var row = new Dictionary<string, object> { ["target"] = h.Target, ["kid"] = h.Kid, ["until"] = h.UntilWallMs, ["word"] = h.Word };
                if (!h.Public) row["public"] = false;
                if (h.Word != "busy" && h.BusyUntilWallMs > 0) row["busy"] = h.BusyUntilWallMs;
                return row;
            }),
        });
        try
        {
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The hold stands in memory; a restart before the next write presents the key at once. Said once.
            if (_writeFaultSaid) return;
            _writeFaultSaid = true;
            Log.Warn("Stagewatch link: a refused key's hold could not be kept for a restart.", ex);
        }
    }

    /// <summary>The kept holds, when the file is there and whole; a hold of a shape this store does not write is left out.</summary>
    private void Read()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var read = new List<LinkHold>();
            foreach (var e in doc.RootElement.GetProperty("holds").EnumerateArray().Take(MostTargets))
            {
                var target = e.GetProperty("target").GetString() ?? "";
                var kid = e.GetProperty("kid").GetString() ?? "";
                var word = e.GetProperty("word").GetString() ?? "";
                if (target.Length == 0 || !PeerLink.IsKid(kid) || !PeerLink.VerdictClasses.Contains(word) && word != "key") continue;
                var isPublic = !e.TryGetProperty("public", out var p) || p.ValueKind != JsonValueKind.False;
                var until = e.GetProperty("until").GetInt64();
                var busy = word == "busy" ? until : e.TryGetProperty("busy", out var b) ? b.GetInt64() : 0;
                read.Add(new LinkHold(target, kid, until, word, isPublic, busy));
            }
            lock (_gate) _holds.AddRange(read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Log.Warn("Stagewatch link: the kept holds could not be read — a key refused before this start may be presented again at once.", ex);
        }
    }

    /// <summary>"refused (key) — held until 20:15:05": a hold's words, its end in this machine's local time.</summary>
    public static string Words(LinkHold hold)
        => (hold.Word == "busy" ? "rate-limited" : "refused (" + hold.Word + ")") + " — held until "
           + DateTimeOffset.FromUnixTimeMilliseconds(hold.UntilWallMs).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
