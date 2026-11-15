using System.Security.Cryptography;
using System.Text.Json;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Show Bus 1.3 §13.5.4: the app keys this desk accepts on its wire, kept with the machine — sealed to this Windows user through
/// <see cref="SecretSeal"/> beside the TLS key (<see cref="AppKeys.FileName"/> in the settings' folder), never in the show, a backup,
/// a support bundle, the twin's traffic or STATE. Only K is kept of a key; the key an operator makes is held here once, until the
/// Nodes page shows it (<see cref="TakeShown"/>), and then nowhere.
///
/// One writer: every change goes through the gate, swaps in a new list and writes the file whole before it is answered, so a key
/// revoked is out of force the moment the verb returns. Readers (the wire's acceptor on its socket threads, STATE, the desk) take
/// the list as it stands, which never changes under them. A change that ends live links (a revoke, a binding moved, a scope lowered,
/// a new code's revocation of the keys enrolled from the old one) raises <see cref="LinksEnd"/> with the kids and the words their
/// links are refused in; <see cref="Changed"/> says the list moved at all, for STATE's push and the desk.
/// </summary>
public sealed class AppKeyStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Func<byte[], byte[]?> _seal;
    private readonly bool _sealRequired;
    private IReadOnlyList<AppKey> _keys = Array.Empty<AppKey>();
    private byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private string? _shown;
    private string? _shownKid;
    private bool _writeFaultSaid;
    private bool _sealFaultSaid;

    /// <summary>A store in memory only (a node's, and the tests').</summary>
    public AppKeyStore()
    {
        _seal = static _ => null;
    }

    /// <param name="folder">The settings' folder: the keys are read from there now and kept there.</param>
    public AppKeyStore(string folder)
        : this(folder, static data => SecretSeal.Seal(data, "Patterns app keys"), OperatingSystem.IsWindows())
    {
    }

    /// <summary>
    /// The store with its seal named — the seam a test fails the seal through. <paramref name="sealRequired"/> (Windows): a list the seal
    /// refuses is written nowhere, never plain; off Windows no seal is asked for and the list is kept in a file only its owner reads.
    /// </summary>
    public AppKeyStore(string folder, Func<byte[], byte[]?> seal, bool sealRequired)
    {
        _path = Path.Combine(folder, AppKeys.FileName);
        _seal = seal;
        _sealRequired = sealRequired;
        Read();
    }

    /// <summary>The kept keys, as they stand now.</summary>
    public IReadOnlyList<AppKey> Keys => Volatile.Read(ref _keys);

    /// <summary>How the keys are kept at rest, for the Super Check and the Nodes page.</summary>
    public string Protection { get; private set; } = "";

    /// <summary>
    /// "" while the keys are kept as <see cref="Protection"/> says; otherwise why they are not (the seal refused them: they hold for this
    /// run only) — the Super Check's App keys row is amber with it.
    /// </summary>
    public string Problem { get; private set; } = "";

    /// <summary>Raised after every change of the list (any thread): STATE pushes, the desk redraws.</summary>
    public event Action? Changed;

    /// <summary>Raised after a change that ends live links: the kids whose links are refused now, and the words they are refused in.</summary>
    public event Action<IReadOnlyList<string>, string>? LinksEnd;

    /// <summary>The mark of <paramref name="token"/> as the kept enrolled keys carry it ("" for no code of 13.5.1's shape).</summary>
    public string CodeMark(string? token)
    {
        lock (_gate) return AppKeys.CodeMark(_salt, token);
    }

    /// <summary>The keys in force while <paramref name="token"/> is the pairing token: every issued key, and the keys enrolled from it.</summary>
    public IReadOnlyList<AppKey> InForce(string? token) => AppKeys.InForce(Keys, CodeMark(token));

    /// <summary>KEY MAKE. The new key is held for the Nodes page to show once; the answer never carries it.</summary>
    public (bool Ok, string Words, AppKey? Made) Make(string bound, string scope, DateTime utcNow)
    {
        IReadOnlyList<AppKey> keys;
        string? secret;
        AppKey? made;
        string words;
        lock (_gate)
        {
            (keys, secret, made, words) = AppKeys.Make(_keys, bound, scope, utcNow);
            if (made is null) return (false, words, null);
            Swap(keys);
            _shown = secret;
            _shownKid = made.Kid;
        }
        Changed?.Invoke();
        return (true, words, made);
    }

    /// <summary>The key KEY MAKE made last, once: the Nodes page shows it and it is gone from here.</summary>
    public string? TakeShown()
    {
        lock (_gate)
        {
            var shown = _shown;
            _shown = null;
            _shownKid = null;
            return shown;
        }
    }

    /// <summary>KEY BIND: a binding moved, or a scope lowered, ends that key's live links.</summary>
    public (bool Ok, string Words) Bind(string kid, string bound, string? scope)
    {
        IReadOnlyList<string>? ends = null;
        string words;
        lock (_gate)
        {
            var (keys, ok, endsLinks, said) = AppKeys.Bind(_keys, kid, bound, scope);
            words = said;
            if (!ok) return (false, words);
            Swap(keys);
            if (endsLinks) ends = new[] { kid };
        }
        Raise(ends, MovedWords);
        return (true, words);
    }

    /// <summary>KEY REVOKE: the key goes, and its live links are refused at once (13.5.5).</summary>
    public (bool Ok, string Words) Revoke(string kid)
    {
        string words;
        lock (_gate)
        {
            var (keys, ok, said) = AppKeys.Revoke(_keys, kid);
            words = said;
            if (!ok) return (false, words);
            Swap(keys);
            // A key revoked before the Nodes page took it is shown nowhere.
            if (_shownKid == kid)
            {
                _shown = null;
                _shownKid = null;
            }
        }
        Raise(new[] { kid }, RevokedWords);
        return (true, words);
    }

    /// <summary>13.5.5's words a key's live link is refused in when its binding moved or its scope fell on this desk.</summary>
    public const string MovedWords = "this key's binding or scope changed on this desk";

    /// <summary>13.5.5's words a revoked key's live link is refused in (the scripted peer's own).</summary>
    public const string RevokedWords = "this key was revoked";

    /// <summary>13.5.5's words a link proved with the old code, or with a key enrolled from it, is refused in.</summary>
    public const string ReplacedWords = "the pairing code was replaced";

    /// <summary>
    /// 13.5.7: the key a code proved over TLS enrols for <paramref name="bound"/>, and the kid of the one enrolled before for it that gave
    /// way (null for a first enrolment) — the new key takes that one's scope, so a raise the operator made with KEY BIND survives a
    /// connector that proves the code again. Null past the bound.
    /// </summary>
    public (AppKey Key, string? Replaced)? Enrol(byte[] k, string bound, string name, string? token, DateTime utcNow)
    {
        IReadOnlyList<string>? ends = null;
        AppKey enrolled;
        string? replaced;
        lock (_gate)
        {
            var before = _keys.Where(x => x.Enrolled && x.Bound == bound).Select(x => x.Kid).ToList();
            var (keys, key) = AppKeys.Enrol(_keys, k, bound, name, AppKeys.CodeMark(_salt, token), utcNow);
            if (key is null) return null;
            enrolled = key;
            Swap(keys);
            replaced = before.Count > 0 ? before[0] : null;
            if (before.Count > 0) ends = before;
        }
        Raise(ends, "this key gave way to a new enrolment for its connector");
        return (enrolled, replaced);
    }

    /// <summary>13.5.5: a new code (<paramref name="token"/>) revokes every key enrolled from another; the kids that went.</summary>
    public IReadOnlyList<string> RevokeEnrolled(string? token)
    {
        IReadOnlyList<string> gone;
        lock (_gate)
        {
            var (keys, revoked) = AppKeys.RevokeEnrolled(_keys, AppKeys.CodeMark(_salt, token));
            gone = revoked;
            if (gone.Count == 0) return gone;
            Swap(keys);
        }
        Raise(gone, ReplacedWords);
        return gone;
    }

    /// <summary>The kept issued key a secret presented in clear is, or null.</summary>
    public AppKey? OfPresented(string? secret) => AppKeys.OfPresented(Keys, secret);

    /// <summary>13.6.9: the key was seen in clear from <paramref name="address"/>; true at its first mark (the caller journals it then).</summary>
    public bool MarkClear(string kid, string address, string via, DateTime utcNow)
    {
        bool first;
        lock (_gate)
        {
            IReadOnlyList<AppKey> keys;
            (keys, first) = AppKeys.MarkClear(_keys, kid, address, via, utcNow);
            if (ReferenceEquals(keys, _keys)) return false;
            // A repeat moves the mark's last time and count in memory; only a first mark is written at once.
            Volatile.Write(ref _keys, keys);
            if (first) Write();
        }
        Changed?.Invoke();
        return first;
    }

    /// <summary>The key linked now: its last use, kept in memory and written with the next change.</summary>
    public void Used(string kid, DateTime utcNow)
    {
        lock (_gate)
        {
            if (_keys.All(k => k.Kid != kid)) return;
            Volatile.Write(ref _keys, _keys.Select(k => k.Kid == kid ? k with { LastUseUtc = utcNow } : k).ToList());
        }
        Changed?.Invoke();
    }

    private void Swap(IReadOnlyList<AppKey> keys)
    {
        Volatile.Write(ref _keys, keys);
        Write();
    }

    private void Raise(IReadOnlyList<string>? ends, string words)
    {
        Changed?.Invoke();
        if (ends is { Count: > 0 }) LinksEnd?.Invoke(ends, words);
    }

    /// <summary>The words the Super Check and the Nodes page say while the seal refuses the keys.</summary>
    public const string UnsealedWords = "this Windows user's DPAPI refused to seal the app keys — they hold for this run only and are written nowhere (never in plain); a restart forgets them, so make the connectors' keys again then";

    /// <summary>
    /// The list written whole, under the gate: sealed on Windows, an owner-only file elsewhere, created owner-only before it is moved
    /// into place. A seal that is required and refuses writes nothing — the keys hold in memory for this run, the file a revoked key
    /// could come back from is removed, and it is said once. A write failure is said once too. The caller raises <see cref="Changed"/>
    /// once it has left the gate.
    /// </summary>
    private void Write()
    {
        if (_path is null) return;
        var plain = AppKeys.Write(_keys, _salt);
        try
        {
            var sealedBytes = _seal(plain);
            if (sealedBytes is null && _sealRequired)
            {
                Unsealed();
                return;
            }
            Problem = "";
            _sealFaultSaid = false;
            var protection = sealedBytes is null ? "owner-only-file" : "dpapi-user";
            Protection = "the keys " + (sealedBytes is null ? SecretSeal.OwnerOnlyWords : SecretSeal.SealedWords);
            var json = JsonSerializer.Serialize(new { v = 1, protection, data = Convert.ToBase64String(sealedBytes ?? plain) });
            AtomicFile.WriteAllTextOwnerOnly(_path, json);
            SecretSeal.OwnerOnly(_path, "App keys");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_writeFaultSaid) Log.Warn("App keys: the kept keys could not be written — they hold for this run only.", ex);
            _writeFaultSaid = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>The seal refused a list it must seal: nothing is written, the older file goes, and it is said once.</summary>
    private void Unsealed()
    {
        Problem = UnsealedWords;
        Protection = "the keys held for this run only — not written (DPAPI refused to seal them)";
        if (!_sealFaultSaid) Log.Warn("App keys: " + UnsealedWords + ".");
        _sealFaultSaid = true;
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>The kept list, read once at start. A file that cannot be read or opened is said once, kept aside, and the list starts empty.</summary>
    private void Read()
    {
        if (_path is null || !File.Exists(_path))
        {
            Protection = OperatingSystem.IsWindows() ? "the keys " + SecretSeal.SealedWords : "the keys " + SecretSeal.OwnerOnlyWords;
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var r = doc.RootElement;
            var protection = r.TryGetProperty("protection", out var p) ? p.GetString() ?? "" : "";
            var data = r.TryGetProperty("data", out var d) ? Convert.FromBase64String(d.GetString() ?? "") : Array.Empty<byte>();
            var plain = protection == "dpapi-user" ? SecretSeal.Unseal(data) : protection == "owner-only-file" ? data : null;
            (IReadOnlyList<AppKey> Keys, byte[] Salt, int Dropped)? read;
            try
            {
                read = plain is null ? null : AppKeys.Read(plain);
            }
            finally
            {
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
            if (read is not { } kept)
            {
                SetAside("could not be opened by this Windows user or read");
                return;
            }
            _keys = kept.Keys;
            _salt = kept.Salt;
            Protection = "the keys " + (protection == "dpapi-user" ? SecretSeal.SealedWords : SecretSeal.OwnerOnlyWords);
            if (kept.Dropped > 0) Log.Warn($"App keys: {kept.Dropped} kept key row(s) this build cannot read were left out — make those connectors' keys again on the Nodes page.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            SetAside(ex.Message);
        }
    }

    private void SetAside(string why)
    {
        Log.Warn($"App keys: {AppKeys.FileName} {why} — no app key is in force; it is kept aside as {AppKeys.FileName}.unreadable and the connectors' keys are made again on the Nodes page.");
        try
        {
            File.Move(_path!, _path + ".unreadable", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("App keys: the unreadable file could not be set aside.", ex);
        }
        Protection = "the keys " + (OperatingSystem.IsWindows() ? SecretSeal.SealedWords : SecretSeal.OwnerOnlyWords);
    }
}
