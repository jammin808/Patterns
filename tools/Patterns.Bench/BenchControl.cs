using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Assistant;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Bench;

/// <summary>
/// The bench's control lines, run on the UI thread — the desk's one writer — one at a time in the order they were read,
/// each answered with one JSON line that says what happened, never that something was sent:
/// <list type="bullet">
/// <item><c>clip &lt;name&gt; &lt;seconds&gt;</c> — a synthetic clip of that length on the air (the programme's picture,
/// Media → Video), answered with the input key and the length read back from the air;</item>
/// <item><c>propose look &lt;title…&gt;</c> — the assistant's road to a linked Stagewatch approval desk and no other
/// (<see cref="AssistantPage.ProposeToApprovalDesk"/>): queued there, then the chip's words each time the desk moves it,
/// or refused when no approval desk is live — never applied here in its place;</item>
/// <item><c>exit</c> — the bench stops as SIGTERM stops it.</item>
/// </list>
/// Anything else is refused, never guessed. An unknown word is echoed only when it is a short plain word (a verb mistyped);
/// anything else is answered as "?", never repeated — a secret pasted into the wrong window must not come back on stdout.
/// A line is not started before the one ahead of it has answered, so the answers come in the lines' order; the end of the
/// input stops the bench once every line read before it has answered.
/// </summary>
internal sealed partial class BenchControl
{
    /// <summary>The longest control line read; a longer one is refused whole, its tail read and dropped as it arrives.</summary>
    public const int MaxLine = 4096;

    /// <summary>The most lines read and not yet answered; the reader waits for room beyond it.</summary>
    public const int MaxWaiting = 64;

    private const string Known = "unknown control (clip, propose look, exit)";

    private readonly AppServices _services;
    private readonly MainViewModel _vm;
    private readonly BenchOut _out;
    private readonly BenchClips _clips;
    private readonly Action<int> _stop;

    /// <summary>Lines read and not yet run (UI thread); null is the end of the input.</summary>
    private readonly Queue<string?> _lines = new();
    private bool _draining;
    private bool _ended;

    /// <summary>Lines read and not yet answered, counted under <see cref="_room"/> by the reader and the UI thread.</summary>
    private int _waiting;
    private readonly object _room = new();

    public BenchControl(AppServices services, MainViewModel vm, BenchOut output, BenchClips clips, Action<int> stop)
    {
        _services = services;
        _vm = vm;
        _out = output;
        _clips = clips;
        _stop = stop;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClipName();

    /// <summary>A word short and plain enough to be a mistyped verb: lower-case letters, at most 12.</summary>
    [GeneratedRegex("^[a-z]{1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainWord();

    /// <summary>
    /// One line of <paramref name="input"/>, or null at its end (reader thread). At most <see cref="MaxLine"/> + 1
    /// characters are kept — enough for the line to be refused as too long — and the rest of a longer line is read and
    /// dropped, so a line with no end in sight never grows the bench's memory.
    /// </summary>
    public static string? ReadLine(TextReader input)
    {
        var kept = new StringBuilder();
        var read = false;
        while (true)
        {
            var c = input.Read();
            if (c < 0) return read ? Line(kept) : null;
            read = true;
            if (c == '\n') return Line(kept);
            if (kept.Length <= MaxLine) kept.Append((char)c);
        }
    }

    private static string Line(StringBuilder kept)
        => kept.Length > 0 && kept[^1] == '\r' ? kept.ToString(0, kept.Length - 1) : kept.ToString();

    /// <summary>Waits until fewer than <see cref="MaxWaiting"/> lines are unanswered, then counts one more (reader thread).</summary>
    public void WaitForRoom()
    {
        lock (_room)
        {
            while (_waiting >= MaxWaiting) Monitor.Wait(_room);
            _waiting++;
        }
    }

    /// <summary>One line answered or dropped: its room is free (UI thread).</summary>
    private void Answered()
    {
        lock (_room)
        {
            _waiting--;
            Monitor.Pulse(_room);
        }
    }

    /// <summary>A line read, or null for the end of the input, queued behind the lines before it (UI thread).</summary>
    public void Enqueue(string? line)
    {
        if (_ended)
        {
            if (line is not null) Answered();                                                  // dropped: the bench is stopping
            return;
        }
        _lines.Enqueue(line);
        if (!_draining) _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        _draining = true;
        try
        {
            while (!_ended && _lines.Count > 0)
            {
                var line = _lines.Dequeue();
                if (line is null)
                {
                    _ended = true;
                    _stop(0);
                    return;
                }
                try
                {
                    await RunAsync(line);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A fault behind a line is that line's: said on stdout with its type, written whole to the desk's log.
                    Log.Error("A bench control line faulted.", ex);
                    _out.Line(new { control = Echo(FirstWord(line)), ok = false, error = $"the bench faulted on this line ({ex.GetType().Name}) — logged" });
                }
                finally
                {
                    Answered();
                }
            }
        }
        finally
        {
            _draining = false;
        }
    }

    private static string FirstWord(string line)
    {
        var words = line.Trim();
        var space = words.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? words : words[..space];
    }

    /// <summary>A word as an answer may repeat it: itself when it is short and plain, otherwise "?".</summary>
    private static string Echo(string word) => PlainWord().IsMatch(word) ? word : "?";

    /// <summary>One control line (UI thread); done when its answer is out.</summary>
    private async Task RunAsync(string line)
    {
        if (line.Length > MaxLine)
        {
            _out.Line(new { control = "", ok = false, error = $"a control line is at most {MaxLine} characters" });
            return;
        }
        var words = line.Trim();
        var space = words.IndexOf(' ', StringComparison.Ordinal);
        var verb = space < 0 ? words : words[..space];
        var rest = space < 0 ? "" : words[(space + 1)..].Trim();
        switch (verb)
        {
            case "clip":
                await ClipAsync(rest);
                break;
            case "propose":
                Propose(rest);
                break;
            case "exit" when rest.Length == 0:
                _out.Line(new { control = "exit", ok = true });
                _ended = true;
                _stop(0);
                break;
            case "exit":
                _out.Line(new { control = "exit", ok = false, error = "exit takes nothing after it" });
                break;
            default:
                _out.Line(new { control = Echo(verb), ok = false, error = Known });
                break;
        }
    }

    private async Task ClipAsync(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !ClipName().IsMatch(parts[0]) || parts[0].Contains("..", StringComparison.Ordinal))
        {
            _out.Line(new { control = "clip", ok = false, error = "clip takes <name> <seconds>: a file name of letters, digits, '.', '_' and '-', and the clip's length" });
            return;
        }
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 86_400)
        {
            _out.Line(new { control = "clip", ok = false, error = "a clip's length is seconds, more than 0 and at most 86400" });
            return;
        }
        if (!_clips.Synthetic)
        {
            _out.Line(new { control = "clip", ok = false, error = "this bench opens no clips: start it with --clips synthetic" });
            return;
        }
        if (_vm.IsSandboxActive)
        {
            // One air: with EDIT SAFE open the programme's picture is the preview's to edit, so a clip set here would land
            // in the preview and never on the air the line asked for.
            _out.Line(new { control = "clip", ok = false, error = "EDIT SAFE is open: the clip would land in the preview, not on air" });
            return;
        }
        var name = parts[0];
        string path;
        try
        {
            path = _clips.Write(name, seconds);
        }
        catch (IOException ex)
        {
            _out.Line(new { control = "clip", ok = false, error = $"the clip's file could not be written: {ex.Message}" });
            return;
        }
        if (_vm.EditTarget.ScreenId is not null) _vm.EditTarget = _vm.EditTargets[0];      // the programme's picture, never a screen's own
        var picture = _vm.ActivePattern;
        picture.Kind = PatternKind.Media;
        picture.Media.Source = MediaSource.Video;
        picture.Media.Loop = false;
        picture.Media.VideoPath = path;
        // Read back once the edit has published (the jobs ahead of this one have run): the inputs follow the air, and the
        // answer is the air's own reading of the clip — what STATE's video row reads.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _services.ReconcileInputs();
        var reading = _services.VideoOnAir();
        if (reading is null || reading.Role != VideoRole.Program || reading.Name != name)
        {
            _out.Line(new { control = "clip", ok = false, error = "the clip did not reach the air" });
            return;
        }
        _out.Line(new
        {
            control = "clip",
            ok = true,
            key = reading.Key,
            file = name,
            lengthMs = reading.HasLength ? (long)Math.Round(reading.LengthSeconds * 1000) : (long?)null,
        });
    }

    private void Propose(string rest)
    {
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        var kind = space < 0 ? rest : rest[..space];
        var title = space < 0 ? "" : rest[(space + 1)..].Trim();
        if (kind != "look")
        {
            _out.Line(new { control = "propose", ok = false, error = "propose takes look <title>: a look is the one kind the bench proposes" });
            return;
        }
        if (title.Length is 0 or > 80 || title.Any(char.IsControl))
        {
            _out.Line(new { control = "propose", ok = false, error = "a look's title is words, at most 80 characters" });
            return;
        }
        // As the assistant proposes a look: its kind, its title, and the look itself, named as the title.
        var proposal = new AssistantProposal { Kind = "look", Title = title, Looks = new[] { new LookPart(title, 0, null, null) } };
        var chip = new AssistantChip(_vm.Assistant, proposal);
        var (localId, words) = _vm.Assistant.ProposeToApprovalDesk(chip);
        if (localId is null)
        {
            _out.Line(new { control = "propose", ok = false, error = words });
            return;
        }
        _out.Line(new { control = "propose", ok = true, title, id = localId });
        // The approval desk's entry holds the chip while the desk can still move it (its own cap bounds them); the bench
        // only listens, so its words reach stdout for as long as there are words to say and nothing is kept past that.
        chip.PropertyChanged += (_, e) => Said(chip, title, localId, e);
    }

    /// <summary>The chip's words each time the approval desk moves the proposal: pending, approved, applied, rejected….</summary>
    private void Said(AssistantChip chip, string title, string localId, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantChip.AppliedText)) return;
        _out.Line(new { proposal = title, id = localId, said = chip.AppliedText });
    }
}
