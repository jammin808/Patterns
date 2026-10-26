using System.Globalization;
using Patterns.Core.Services;

namespace Patterns.Bench;

/// <summary>
/// What the bench was started with: its folder, its doors, the Stagewatch link, the clips and the control. Every option is
/// named, given once and has its value; anything else is refused with words and exit code 2, and a refusal never repeats
/// an argument it did not recognise — an argument may be a secret typed in the wrong place, so it is named by its position.
/// The secrets themselves (the pairing token, a Stagewatch key) are read from files and never taken from the command line.
/// </summary>
internal sealed record BenchOptions
{
    public const string Usage =
        "Patterns.Bench --home <folder> --token-file <file> [--http <port>] [--wire <port>] [--bind <address>] [--name <words>] " +
        "[--stagewatch <host:port>|find] [--stagewatch-key-file <file>] [--trust-loopback on|off] [--clips synthetic] " +
        "[--control stdin] [--exit-after <seconds>]";

    private static readonly string[] Known =
    {
        "--home", "--token-file", "--http", "--wire", "--bind", "--name", "--stagewatch", "--stagewatch-key-file",
        "--trust-loopback", "--clips", "--control", "--exit-after",
    };

    /// <summary>
    /// The bench's own folder: its settings (written fresh at every start), its log, its show network id, its clips. Made
    /// by the bench for this user alone where it does not exist; a folder holding anything the bench did not write, or one
    /// another user can enter, is refused (<c>Program.TakeHome</c>).
    /// </summary>
    public string Home { get; init; } = "";

    /// <summary>The file whose first line is the pairing token.</summary>
    public string TokenFile { get; init; } = "";

    public int Http { get; init; } = 9696;
    public int Wire { get; init; } = 9697;

    /// <summary>The control listeners' address; "" is every interface.</summary>
    public string Bind { get; init; } = "";

    /// <summary>The show's name and the name its hello says; null keeps the fresh show's and the machine's.</summary>
    public string? Name { get; init; }

    /// <summary>The Stagewatch link: null is off, "" follows the first Stagewatch heard (<c>find</c>), otherwise the address typed.</summary>
    public string? Stagewatch { get; init; }

    /// <summary>The file whose first line is the Stagewatch key the link presents; null presents none.</summary>
    public string? StagewatchKeyFile { get; init; }

    /// <summary>Whether this machine's own connections skip the token (the desk's default); off unless asked, so the lane pairs as another machine does.</summary>
    public bool TrustLoopback { get; init; }

    /// <summary>Every clip the desk opens is a synthetic one (<see cref="BenchClip"/>); off, the bench opens no clip at all.</summary>
    public bool SyntheticClips { get; init; }

    /// <summary>Control lines are read from standard input, whose end stops the bench.</summary>
    public bool ControlOnStdin { get; init; }

    /// <summary>The bench stops by itself after this many seconds; null runs until told.</summary>
    public int? ExitAfterSeconds { get; init; }

    /// <summary>The options read, or null and the refusal's words.</summary>
    public static BenchOptions? Parse(IReadOnlyList<string> args, out string refusal)
    {
        refusal = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var o = new BenchOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            var position = (i + 1).ToString(CultureInfo.InvariantCulture);
            if (!Known.Contains(name, StringComparer.Ordinal))
            {
                refusal = $"argument {position} is not an option the bench knows — {Usage}";
                return null;
            }
            if (!seen.Add(name))
            {
                refusal = $"{name} is given twice";
                return null;
            }
            if (i + 1 >= args.Count)
            {
                refusal = $"{name} needs a value";
                return null;
            }
            var value = args[++i];
            var next = Apply(o, name, value, out refusal);
            if (next is null) return null;
            o = next;
        }
        if (o.Home.Length == 0 || o.TokenFile.Length == 0)
        {
            refusal = $"--home and --token-file are both needed — {Usage}";
            return null;
        }
        if (o.Http == o.Wire)
        {
            refusal = "--http and --wire name the same port";
            return null;
        }
        if (o.StagewatchKeyFile is not null && o.Stagewatch is null)
        {
            refusal = "--stagewatch-key-file needs --stagewatch: a key with no link goes nowhere";
            return null;
        }
        return o;
    }

    private static BenchOptions? Apply(BenchOptions o, string name, string value, out string refusal)
    {
        refusal = "";
        switch (name)
        {
            case "--home":
                if (value.Trim().Length == 0) return Refuse(name, "a folder", out refusal);
                return o with { Home = Path.GetFullPath(value) };
            case "--token-file":
                if (value.Trim().Length == 0) return Refuse(name, "a file", out refusal);
                return o with { TokenFile = Path.GetFullPath(value) };
            case "--http":
                return Port(value) is { } http ? o with { Http = http } : Refuse(name, "a port from 1024 to 65535", out refusal);
            case "--wire":
                return Port(value) is { } wire ? o with { Wire = wire } : Refuse(name, "a port from 1024 to 65535", out refusal);
            case "--bind":
                if (!BindAddress.TryParse(value, out _, out var problem))
                {
                    refusal = $"--bind: {problem}";
                    return null;
                }
                return o with { Bind = value.Trim() };
            case "--name":
                return Words(value, 64) is { } words ? o with { Name = words } : Refuse(name, "words, at most 64 characters", out refusal);
            case "--stagewatch":
                if (value == "find") return o with { Stagewatch = "" };
                return Words(value, 255) is { } host && !host.Contains(' ', StringComparison.Ordinal)
                    ? o with { Stagewatch = host }
                    : Refuse(name, "host:port, or find for the first Stagewatch heard", out refusal);
            case "--stagewatch-key-file":
                if (value.Trim().Length == 0) return Refuse(name, "a file", out refusal);
                return o with { StagewatchKeyFile = Path.GetFullPath(value) };
            case "--trust-loopback":
                return value switch
                {
                    "on" => o with { TrustLoopback = true },
                    "off" => o with { TrustLoopback = false },
                    _ => Refuse(name, "on or off", out refusal),
                };
            case "--clips":
                return value == "synthetic" ? o with { SyntheticClips = true } : Refuse(name, "synthetic, the one kind the bench has", out refusal);
            case "--control":
                return value == "stdin" ? o with { ControlOnStdin = true } : Refuse(name, "stdin, the one control the bench reads", out refusal);
            case "--exit-after":
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is >= 1 and <= 86_400
                    ? o with { ExitAfterSeconds = s }
                    : Refuse(name, "whole seconds from 1 to 86400", out refusal);
            default:
                return Refuse(name, "nothing the bench knows", out refusal);
        }
    }

    private static BenchOptions? Refuse(string name, string wanted, out string refusal)
    {
        refusal = $"{name} takes {wanted}";
        return null;
    }

    private static int? Port(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1024 and <= 65535 ? port : null;

    private static string? Words(string value, int max)
    {
        var w = value.Trim();
        return w.Length is > 0 && w.Length <= max && !w.Any(char.IsControl) ? w : null;
    }
}
