namespace Patterns.Core.Services;

/// <summary>
/// The line wire's ceilings: how many Companion connections a port keeps open in all and from one
/// address, how long a line that has started may take to end, and the same two ceilings for the
/// web remote's port. Companion holds one connection; a tablet holds one; the desk's own pages a
/// few. Past these is not a remote — a client reconnecting in a loop, a port scanner, a program
/// that opened a socket and forgot it — and the door says so once and closes, with the show
/// untouched. The audience port has budgets of its own (<see cref="Play.AudienceBudget"/>);
/// these are the trusted side's, and they are ceilings on connections, never on lines: a
/// Companion that sits idle for an hour between presses is never cut.
/// </summary>
public sealed record WireLimits(
    int MaxClients = 64,
    int MaxClientsPerAddress = 16,
    double LineSeconds = 10,
    int MaxHttpClients = 256,
    int MaxHttpClientsPerAddress = 64)
{
    public static readonly WireLimits Default = new();

    /// <summary>The line a refused Companion connection reads before the door closes — which ceiling, and what to do.</summary>
    public string BusyWords(int fromAddress) => fromAddress >= MaxClientsPerAddress
        ? $"busy — {MaxClientsPerAddress} connections already open from this address; close one first"
        : $"busy — {MaxClients} connections already open on this port; close one first";

    /// <summary>The words for a line that started and did not end within its seconds.</summary>
    public string SlowLineWords() => $"a line that did not end within {LineSeconds:0.#} s";
}

/// <summary>
/// Open connections counted in all and per address under two ceilings. The audience's door, the
/// wire's and the web remote's keep one each, so the rule is written once and tested once.
/// Thread-safe: the accept loops admit and release from their own tasks.
/// </summary>
public sealed class ConnectionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _byAddress = new(StringComparer.Ordinal);
    private int _open;

    /// <summary>How many are open right now.</summary>
    public int Open
    {
        get { lock (_gate) return _open; }
    }

    /// <summary>How many are open from one address.</summary>
    public int From(string address)
    {
        lock (_gate) return _byAddress.TryGetValue(address, out var n) ? n : 0;
    }

    /// <summary>Counts one more from the address when both ceilings allow it; false, and nothing counted, otherwise.</summary>
    public bool TryAdmit(string address, int maxTotal, int maxPerAddress)
    {
        lock (_gate)
        {
            _byAddress.TryGetValue(address, out var mine);
            if (_open >= maxTotal || mine >= maxPerAddress) return false;
            _byAddress[address] = mine + 1;
            _open++;
            return true;
        }
    }

    /// <summary>One connection from the address closed. A release of one never admitted changes nothing: the count never goes below zero.</summary>
    public void Release(string address)
    {
        lock (_gate)
        {
            if (!_byAddress.TryGetValue(address, out var mine)) return;
            if (mine <= 1) _byAddress.Remove(address); else _byAddress[address] = mine - 1;
            if (_open > 0) _open--;
        }
    }
}
