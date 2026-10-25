using System.Globalization;
using System.Security.Cryptography;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 89: this machine's kept page addresses — the one writer of <see cref="WebPassBook.FileName"/> beside the
/// settings, and the one place every entry of the desk secures an address it is handed. A private address (a
/// Stagewatch window's, one with a pass word, one with a user) is kept here and the show holds
/// <c>kept://host[:port]/&lt;id&gt;</c> instead; the browser source resolves that at navigation and nothing else
/// ever reads the real address back.
/// <para>
/// The book is immutable and swapped whole (readers on any thread hold a book that cannot move under them); an entry
/// is on the disk before any reader sees it, so a kept address the show names is one a restart opens too — a book
/// that could not be written keeps nothing, and the address is held unkept (black, with words) rather than kept for
/// this run alone. A file on disk that is not a book this build reads (damaged, or a book of another version) is never
/// written over: nothing is kept this run, and <see cref="Frozen"/> says so. A file that could not be read at all (a scan
/// or a share holding it) freezes the book only until it reads: the next keep or resolution reads it again. A full book
/// makes room only from passes no show this desk holds names (<see cref="Shows"/>).
/// </para>
/// </summary>
public sealed class WebPasses
{
    /// <summary>How often a book whose file could not be read is tried again, at most.</summary>
    public static readonly TimeSpan RereadEvery = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Func<int, byte[]> _random;
    private readonly Func<DateTime> _clock;
    private WebPassBook _book;
    private string _frozen;
    private bool _unread;                         // frozen only because the file could not be read: read again, at most once a second
    private DateTime _nextRead;
    private int _quiet;
    private bool _changedQuietly;
    private ShowState? _securing;                 // the show being secured: its kept ids count as named while it is (a loaded show is not the desk's yet)

    /// <param name="directory">The settings' folder (this machine's own: a standby launched here runs with its own).</param>
    /// <param name="random">The random source a new id's bytes come from; the system's unless a test hands in fixed bytes.</param>
    /// <param name="clock">When an entry was kept; the system's UTC clock unless a test pins it.</param>
    public WebPasses(string directory, Func<int, byte[]>? random = null, Func<DateTime>? clock = null)
    {
        Path = System.IO.Path.Combine(directory, WebPassBook.FileName);
        _random = random ?? RandomNumberGenerator.GetBytes;
        _clock = clock ?? (static () => DateTime.UtcNow);
        _book = Load(Path, quiet: false, out var frozen, out var unread);
        _frozen = frozen;
        _unread = unread;
        _nextRead = _clock() + RereadEvery;
    }

    /// <summary>The book's file.</summary>
    public string Path { get; }

    /// <summary>The book as it stands — immutable; read from any thread.</summary>
    public WebPassBook Book => Volatile.Read(ref _book);

    /// <summary>"" when the book can be written; else why nothing is kept on this machine (a file that is not a book this build reads, for the run; one that could not be read, until it reads).</summary>
    public string Frozen => Volatile.Read(ref _frozen);

    /// <summary>The shows this desk holds — the edited show and the air — whose kept ids a full book never drops; null: none named.</summary>
    public Func<IEnumerable<ShowState>>? Shows { get; set; }

    /// <summary>The book changed (an address kept). Raised on the thread that kept it, after the file was written.</summary>
    public event Action? Changed;

    /// <summary>Words for the operator: an address kept, or one that could not be (raised beside <see cref="Changed"/>).</summary>
    public event Action<string>? Noticed;

    /// <summary>The real addresses the book keeps — secret values, masked wherever free text carries them (the support bundle).</summary>
    public IReadOnlyList<string> SecretValues
    {
        get
        {
            RereadIfUnread();
            return Book.SecretValues;
        }
    }

    /// <summary>What an address opens on this machine: a kept address this book holds, its real address; any other held address nothing, with why.</summary>
    public WebPassResolution Resolve(string? address)
    {
        if (WindowAddresses.IsHeld(address)) RereadIfUnread();
        return Book.Resolve(address);
    }

    /// <summary>An address in words a reader may publish (<see cref="WebPassBook.Label"/>).</summary>
    public string Label(string? address)
    {
        if (WindowAddresses.IsHeld(address)) RereadIfUnread();
        return Book.Label(address);
    }

    /// <summary>
    /// The address the show holds for one an entry was handed (<see cref="WindowAddresses.Secure"/>): a private one kept
    /// here (its kept address), or held unkept when it cannot be (<paramref name="why"/> says why); a held one in its own
    /// form; any other as it was given.
    /// </summary>
    public string Secure(string? typed, out string why)
    {
        var reason = "";
        var held = WindowAddresses.Secure(typed, url =>
        {
            if (TryKeep(url, out var kept, out var w)) return kept;
            reason = w;
            return "";
        });
        why = reason;
        return held;
    }

    /// <summary>As <see cref="Secure(string?, out string)"/>, the reason left to <see cref="Noticed"/>.</summary>
    public string Secure(string? typed) => Secure(typed, out _);

    /// <summary>
    /// A line of words — a device's command line ("WEB OPEN 10.0.0.5:9924/w/… ON lobby") — with every private address
    /// in it, its scheme written or not, kept here and held in its place; every other word as it was, byte for byte.
    /// </summary>
    public string SecureText(string? text)
    {
        var t = text ?? "";
        return WindowAddresses.RewriteBareText(WindowAddresses.RewriteText(t, a => Secure(a)), a => Secure(a));
    }

    /// <summary>
    /// A line of words with every private address in it read in the form the show holds it — the kept address this book
    /// already holds for it, else its unkept form — and nothing new kept: for a line that looks up what the show holds
    /// (MIDI FORGET), never one that binds or opens.
    /// </summary>
    public string HeldText(string? text)
    {
        var book = Book;
        string Held(string a) => WindowAddresses.Secure(a, url => KeptIn(book, url));
        return WindowAddresses.RewriteBareText(WindowAddresses.RewriteText(text ?? "", Held), Held);
    }

    /// <summary>The kept address a book holds for a real address (as <see cref="WebPassBook.Keep"/> stores it), or "".</summary>
    private static string KeptIn(WebPassBook book, string url)
    {
        var pass = book.Passes.FirstOrDefault(p => string.Equals(p.Url, url, StringComparison.Ordinal));
        return pass is null ? "" : WebPassBook.AddressOf(pass);
    }

    /// <summary>
    /// Keeps a private address: true with its kept address (the entry already kept for it, or a new one written to the
    /// disk first), false with the reason when the book is frozen, full of passes the shows name, or could not be written.
    /// </summary>
    public bool TryKeep(string url, out string held, out string why)
    {
        held = "";
        why = "";
        var added = false;
        bool quiet;
        RereadIfUnread();
        lock (_gate)
        {
            quiet = _quiet > 0;
            if (Frozen.Length > 0)
            {
                why = Frozen;
            }
            else
            {
                var book = _book;
                HashSet<string>? named = null;
                bool InUse(string id) => (named ??= NamedIds()).Contains(id);
                var (next, address) = book.Keep(url, () => WindowAddresses.NewId(_random), _clock(), Shows is null && _securing is null ? null : InUse);
                if (address.Length == 0)
                {
                    why = book.Count >= WebPassBook.Max ? WebPassBook.FullWhy : "the address names no host a browser opens";
                }
                else if (ReferenceEquals(next, book))
                {
                    held = address;
                }
                else if (Write(next, out why))
                {
                    Volatile.Write(ref _book, next);
                    held = address;
                    added = true;
                    if (quiet) _changedQuietly = true;      // a whole show being secured says it once, at its end
                }
            }
        }
        if (quiet) return held.Length > 0;
        var host = WindowAddresses.HostOf(url);
        if (added)
        {
            RaiseChanged();
            Notice($"Kept {host}'s private address on this machine — the show holds {held}; its pass never leaves this machine.");
        }
        else if (held.Length == 0)
        {
            Notice($"{host}'s private address could not be kept on this machine — {why}. Its page stays black until it is pasted again.");
        }
        return held.Length > 0;
    }

    /// <summary>
    /// Every private address a show holds kept here and held in its place (<see cref="WebPassMigration.Rewrite"/>):
    /// the pictures, the layers, the Remote &amp; web page, the saved pages, the nicknames, the cues, the devices' lines
    /// and the looks. Said once for the whole show, never once per address.
    /// </summary>
    public WebPassScrub SecureShow(ShowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        WebPassScrub scrub;
        bool changed;
        lock (_gate)
        {
            _quiet++;
            var outer = _securing;
            _securing = state;
            try
            {
                scrub = WebPassMigration.Rewrite(state, url => TryKeep(url, out var held, out _) ? held : "");
            }
            finally
            {
                _securing = outer;
                _quiet--;
                changed = _changedQuietly && _quiet == 0;
                if (_quiet == 0) _changedQuietly = false;
            }
        }
        if (changed) RaiseChanged();
        return scrub;
    }

    /// <summary>
    /// A show read from a file becomes safe before anything reads it: <see cref="SecureShow"/>, then — when it took a
    /// pass out — the one journal row that says what was kept and what could not be, and one log line.
    /// </summary>
    public WebPassScrub MigrateShow(ShowState state, ShowLog journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var scrub = SecureShow(state);
        if (!scrub.Any) return scrub;
        journal.Record(WebPassMigration.Row(scrub, _clock()));
        var words = WebPassMigration.Words(scrub);
        if (scrub.Unkept > 0) Log.Warn("Web pages: " + words + ".");
        else Log.Info("Web pages: " + words + ".");
        Notice(char.ToUpperInvariant(words[0]) + words[1..] + ".");
        return scrub;
    }

    /// <summary>A preset's picture made safe before it lands on a pattern: its page and its two layers' (a preset saved before round 89 can hold a pass).</summary>
    public void SecurePattern(PatternConfig pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        pattern.Media.WebUrl = Keep(pattern.Media.WebUrl);
        pattern.Layer1.WebUrl = Keep(pattern.Layer1.WebUrl);
        pattern.Layer2.WebUrl = Keep(pattern.Layer2.WebUrl);

        string Keep(string value) => string.IsNullOrEmpty(value) || !(WindowAddresses.LooksPrivate(value) || WindowAddresses.IsHeld(value)) ? value : Secure(value);
    }

    /// <summary>
    /// A book frozen only because its file could not be read is read again — at most once every <see cref="RereadEvery"/>
    /// — before a keep or a resolution: once the file reads, the freeze clears, the book is what it holds, and the
    /// change is raised (a held page waiting on it opens). A file that reads and is not a book stays frozen for the run.
    /// </summary>
    private void RereadIfUnread()
    {
        if (!Volatile.Read(ref _unread)) return;
        var read = false;
        var quiet = false;
        lock (_gate)
        {
            var now = _clock();
            if (!_unread || now < _nextRead) return;
            _nextRead = now + RereadEvery;
            var book = Load(Path, quiet: true, out var frozen, out var unread);
            if (!unread)
            {
                Volatile.Write(ref _book, book);
                Volatile.Write(ref _frozen, frozen);
                Volatile.Write(ref _unread, false);
                read = true;
                quiet = _quiet > 0;
                if (quiet) _changedQuietly = true;      // a whole show being secured says it once, at its end
            }
        }
        if (!read) return;
        Log.Info(Frozen.Length == 0
            ? $"The book of kept page addresses reads now — {Book.Count.ToString(CultureInfo.InvariantCulture)} kept on this machine."
            : $"The book of kept page addresses reads now, and is not a book this build reads — {Frozen}.");
        if (!quiet) RaiseChanged();
    }

    private HashSet<string> NamedIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var show in Shows?.Invoke() ?? Array.Empty<ShowState>())
        {
            if (show is not null) ids.UnionWith(WebPassMigration.KeptIdsIn(show));
        }
        if (_securing is { } securing) ids.UnionWith(WebPassMigration.KeptIdsIn(securing));
        return ids;
    }

    private bool Write(WebPassBook next, out string why)
    {
        why = "";
        try
        {
            AtomicFile.WriteAllText(Path, next.ToJson());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("The book of kept page addresses could not be written — nothing new was kept.", ex);
            why = "this machine's book of kept addresses could not be written (" + ex.Message + ")";
            return false;
        }
    }

    private void Notice(string words)
    {
        try
        {
            Noticed?.Invoke(words);
        }
        catch (Exception ex)
        {
            Log.Warn("A kept-address listener failed.", ex);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("A kept-address listener failed.", ex);
        }
    }

    /// <summary>
    /// The book on disk: empty when there is none; empty and frozen (with the words) when one is there and is not a book
    /// this build reads; empty, frozen and <paramref name="unread"/> when it could not be read at all — the case read again.
    /// </summary>
    private static WebPassBook Load(string path, bool quiet, out string frozen, out bool unread)
    {
        frozen = "";
        unread = false;
        if (!File.Exists(path)) return WebPassBook.Empty;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!quiet) Log.Warn("The book of kept page addresses could not be read — nothing is kept on this machine until it reads; it is tried again.", ex);
            frozen = $"{WebPassBook.FileName} beside the settings could not be read ({ex.Message}) — nothing is kept on this machine until it reads; the next address kept or private page opened tries it again, as does a restart";
            unread = true;
            return WebPassBook.Empty;
        }
        if (WebPassBook.TryParse(text, out var book)) return book;
        Log.Warn($"{WebPassBook.FileName} is not a book this build reads (damaged, or another version) — it is left as it is, and nothing is kept on this machine this run.");
        frozen = $"{WebPassBook.FileName} beside the settings is not a book this build reads (damaged, or another version) — move it aside and restart Patterns to keep addresses here";
        return WebPassBook.Empty;
    }
}
