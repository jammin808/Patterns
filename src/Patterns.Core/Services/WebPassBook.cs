using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>One private page address this machine keeps, under the random id the show names it by.</summary>
public sealed record WebPass(string Id, string Url, DateTime KeptUtc);

/// <summary>
/// What a held address opens: the real address when this machine keeps it (<see cref="Url"/>), or nothing and the
/// reason in words. An address that is not held opens as it is.
/// </summary>
public readonly record struct WebPassResolution(string? Url, string Why)
{
    public bool Resolved => Url is not null;
}

/// <summary>
/// Round 89: this machine's book of kept page addresses — the only place a private address and its pass rest
/// (<see cref="FileName"/>, beside the settings, never in a support bundle). Immutable: every change answers a new book,
/// so the one writer swaps it whole and every reader holds a book that cannot move under it. An id is drawn from the
/// random source when an address is first kept and is never a function of it; the same address kept again answers its
/// own entry, so two pastes share one mount and one browser. At most <see cref="Max"/> entries: a full book makes
/// room by the oldest pass the show no longer names, and never drops one the show still names. Pure.
/// </summary>
public sealed class WebPassBook
{
    /// <summary>The book's file beside the settings — this machine's alone; the twin carries its entries only with the show's secrets.</summary>
    public const string FileName = "patterns.webpasses.json";

    /// <summary>The book's bound.</summary>
    public const int Max = 64;

    /// <summary>The file's version; a book of another version is not read (and so never rewritten over).</summary>
    public const int Version = 1;

    /// <summary>The reason a kept address opens nothing here.</summary>
    public const string NotHereWhy = "kept on another machine — this machine holds no pass for it; paste the address here to show it";

    /// <summary>The reason an unkept address opens nothing anywhere.</summary>
    public const string UnkeptWhy = "a private address written into the show without being kept — paste the address again to keep it on this machine";

    /// <summary>The reason a kept id that names another host opens nothing.</summary>
    public const string MismatchWhy = "the kept id belongs to another address — paste the address again";

    /// <summary>The words for a book with no room left: every pass in it is one the show still names.</summary>
    public const string FullWhy = "this machine already keeps 64 private addresses the show names — remove a page that uses one first";

    private const int IdTries = 8;

    private readonly WebPass[] _passes;

    private WebPassBook(WebPass[] passes) => _passes = passes;

    /// <summary>The empty book.</summary>
    public static WebPassBook Empty { get; } = new(Array.Empty<WebPass>());

    /// <summary>The entries, in the order they were kept.</summary>
    public IReadOnlyList<WebPass> Passes => _passes;

    public int Count => _passes.Length;

    /// <summary>The entry kept under an id, or null.</summary>
    public WebPass? Find(string? id) => Array.Find(_passes, p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>The kept address of an entry.</summary>
    public static string AddressOf(WebPass pass) => WindowAddresses.KeptFor(pass.Url, pass.Id);

    /// <summary>The real addresses the book keeps — the secret values a support bundle masks wherever they appear.</summary>
    public IReadOnlyList<string> SecretValues => _passes.Select(p => p.Url).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Keeps an address and answers the book and the kept address the show holds instead: the entry already kept for
    /// the same address when there is one, else a new entry under <paramref name="newId"/>'s id (drawn again on the
    /// rare id the book already has). A full book makes room by its oldest entry <paramref name="inUse"/> does not name;
    /// when the show names every one, nothing is kept and the address answered is "" (<see cref="FullWhy"/>). An address
    /// with no host is not kept either.
    /// </summary>
    public (WebPassBook Book, string Address) Keep(string url, Func<string> newId, DateTime keptUtc, Func<string, bool>? inUse = null)
    {
        ArgumentNullException.ThrowIfNull(newId);
        var normalized = (url ?? "").Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal)) normalized = "https://" + normalized;
        if (WindowAddresses.IsHeld(normalized) || WindowAddresses.HostOf(normalized).Length == 0) return (this, "");
        var existing = Array.Find(_passes, p => string.Equals(p.Url, normalized, StringComparison.Ordinal));
        if (existing is not null) return (this, AddressOf(existing));

        // Room first: a book that cannot keep the address draws no id for it.
        var list = _passes.ToList();
        if (list.Count >= Max)
        {
            var drop = list.Where(p => inUse is null || !inUse(p.Id)).OrderBy(p => p.KeptUtc).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
            if (drop is null) return (this, "");
            list.Remove(drop);
        }

        var id = newId();
        for (var tries = 1; Find(id) is not null && tries < IdTries; tries++) id = newId();
        if (!WindowAddresses.IsId(id) || Find(id) is not null) throw new InvalidOperationException("The id source gave no fresh kept id.");
        var pass = new WebPass(id, normalized, keptUtc.Kind == DateTimeKind.Utc ? keptUtc : DateTime.SpecifyKind(keptUtc, DateTimeKind.Utc));
        list.Add(pass);
        return (new WebPassBook(list.ToArray()), AddressOf(pass));
    }

    /// <summary>
    /// What an address opens: a kept address this book holds opens its real address; a kept address it does not hold,
    /// an unkept address or a kept id under another host opens nothing, with the reason; any other address opens as it is.
    /// </summary>
    public WebPassResolution Resolve(string? address)
    {
        var a = (address ?? "").Trim();
        if (!WindowAddresses.IsHeld(a)) return new WebPassResolution(a, "");
        if (!WindowAddresses.TryKept(a, out _, out _, out var id)) return new WebPassResolution(null, UnkeptWhy);
        var pass = Find(id);
        if (pass is null) return new WebPassResolution(null, NotHereWhy);
        return string.Equals(AddressOf(pass), WindowAddresses.KeptFor(a, id), StringComparison.OrdinalIgnoreCase)
            ? new WebPassResolution(pass.Url, "")
            : new WebPassResolution(null, MismatchWhy);
    }

    /// <summary>
    /// An address in words a reader may publish: a held address by its host and what this machine can do with it
    /// ("10.0.0.5 (private address, kept)", "… kept on another machine", "… not kept — paste it again"); any other
    /// address by its short name.
    /// </summary>
    public string Label(string? address)
    {
        var a = (address ?? "").Trim();
        if (!WindowAddresses.IsHeld(a)) return WebAddress.ShortName(a);
        var host = WindowAddresses.HostOf(a);
        if (!WindowAddresses.IsKept(a)) return $"{host} (private address, not kept — paste it again)";
        return Resolve(a).Resolved ? $"{host} (private address, kept)" : $"{host} (private address, kept on another machine)";
    }

    /// <summary>
    /// This book with another's entries taken in — a standby taking the main's book: every entry of
    /// <paramref name="incoming"/> stands (an id in both takes the incoming address, the one the show it came with names),
    /// and this book's own entries fill the room left, its oldest unnamed by <paramref name="inUse"/> going first.
    /// </summary>
    public WebPassBook Merge(WebPassBook incoming, Func<string, bool>? inUse = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var ids = new HashSet<string>(incoming._passes.Select(p => p.Id), StringComparer.Ordinal);
        var own = _passes.Where(p => !ids.Contains(p.Id)).ToList();
        while (own.Count > 0 && incoming._passes.Length + own.Count > Max)
        {
            var drop = own.Where(p => inUse is null || !inUse(p.Id)).OrderBy(p => p.KeptUtc).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault()
                       ?? own.OrderBy(p => p.KeptUtc).ThenBy(p => p.Id, StringComparer.Ordinal).First();
            own.Remove(drop);
        }
        var merged = incoming._passes.Concat(own).OrderBy(p => p.KeptUtc).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        return new WebPassBook(merged);
    }

    /// <summary>The book as its file holds it: <c>{"v":1,"passes":[{"id","url","keptUtc"}]}</c>.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteStartArray("passes");
            foreach (var p in _passes)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("url", p.Url);
                w.WriteString("keptUtc", p.KeptUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads a book: false for text that is not one, or one of another version — the caller keeps the file as it is.
    /// Within a book, an entry without a valid id or an address with a host is skipped, a repeated id keeps its first
    /// entry, and past <see cref="Max"/> the newest are kept.
    /// </summary>
    public static bool TryParse(string? json, out WebPassBook book)
    {
        book = Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Version) return false;
            var list = new List<WebPass>();
            if (root.TryGetProperty("passes", out var passes) && passes.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in passes.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var id = Text(e, "id");
                    var url = Text(e, "url").Trim();
                    if (!WindowAddresses.IsId(id) || !url.Contains("://", StringComparison.Ordinal) || WindowAddresses.IsHeld(url) || WindowAddresses.HostOf(url).Length == 0) continue;
                    if (list.Exists(p => p.Id == id)) continue;
                    var at = DateTime.TryParse(Text(e, "keptUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                        ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                        : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
                    list.Add(new WebPass(id, url, at));
                }
            }
            book = new WebPassBook(list.OrderByDescending(p => p.KeptUtc).ThenBy(p => p.Id, StringComparer.Ordinal).Take(Max)
                .OrderBy(p => p.KeptUtc).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }

        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    }
}
