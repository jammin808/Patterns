using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The launch of a host: <c>Patterns.exe --host &lt;role&gt;</c> is a small process of this same
/// build that never touches Avalonia, the settings or the watchdog — it speaks the
/// <see cref="HostProtocol"/> on its own stdin and stdout and does one native job for the desk.
/// The stream encoder is the first role; a decoder is the next.
/// </summary>
public static class HostEntry
{
    public const string Switch = "--host";

    public static bool IsHostLaunch(string[] args) => args.Length >= 2 && args[0] == Switch;

    public static int Run(string[] args)
    {
        var role = args[1];
        TimerResolution.Raise();   // a millisecond timer for the ring's wait and libVLC's own pacing
        try
        {
            return role switch
            {
                EncoderHost.Role => EncoderHost.Run(args.Skip(2).ToArray()),
                _ => Unknown(role),
            };
        }
        catch (Exception ex)
        {
            SayRaw(HostProtocol.Line(HostProtocol.Error, $"host {FaultWords.Describe(ex)}"));
            return 1;
        }
    }

    private static int Unknown(string role)
    {
        SayRaw(HostProtocol.Line(HostProtocol.Error, $"host no such host role '{role}'"));
        return 2;
    }

    /// <summary>A line to stdout in UTF-8 — the desk reads UTF-8; Console.Out in a console-less child would write the ANSI code page and turn every dash into a question mark.</summary>
    private static void SayRaw(string line)
    {
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        stdout.WriteLine(line);
    }
}

/// <summary>
/// The stream encoder in its own process. The desk sends START with an <see cref="EncoderPlan"/>:
/// for a rendered stream the host opens the frame ring the desk draws into and feeds libVLC's memory
/// input from it; for a desktop capture libVLC captures the display itself; the null plan reads the
/// ring and encodes nothing (the tests, and a rig without libVLC saying so). BEAT every second with
/// the frames taken and the encoder's state; ERROR with a code word — <c>libvlc</c>, <c>start</c>,
/// <c>encoder</c> — when it cannot go on; STOP and QUIT obeyed; the end of stdin is the desk gone,
/// and the host goes with it. A libVLC fault here ends this process and the desk starts another —
/// the show never notices.
/// </summary>
public sealed class EncoderHost : IDisposable
{
    public const string Role = "encoder";

    private readonly TextWriter _out;
    private readonly object _say = new();
    private readonly object _gate = new();
    private SharedFrameRing? _ring;
    private RingMediaInput? _input;
    private Thread? _counter;
    private volatile bool _counting;
    private long _counted;
    private string _state = "idle";
    private bool _reportedError;
    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private Media? _media;

    private EncoderHost(TextWriter output) => _out = output;

    public static int Run(string[] args)
    {
        using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true, NewLine = "\n" };
        using var stdin = new StreamReader(Console.OpenStandardInput());
        using var host = new EncoderHost(stdout);
        host.Say(HostProtocol.Hello, JsonUtil.Serialize(new HostHello(Environment.ProcessId, Role, VlcRuntime.Present)));
        var beat = new Thread(host.BeatLoop) { IsBackground = true, Name = "encoder-beat" };
        beat.Start();
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            var (word, rest) = HostProtocol.Parse(line);
            switch (word)
            {
                case HostProtocol.Start:
                    host.Start(rest);
                    break;
                case HostProtocol.Stop:
                    host.Stop();
                    host.Say(HostProtocol.Stopped);
                    break;
                case HostProtocol.Ping:
                    host.Beat();
                    break;
                case HostProtocol.Quit:
                    host.Stop();
                    return 0;
            }
        }
        host.Stop();   // stdin closed: the desk is gone, and so is the reason to encode
        return 0;
    }

    private void Say(string word, string rest = "")
    {
        lock (_say)
        {
            _out.WriteLine(HostProtocol.Line(word, rest));
        }
    }

    private void Start(string json)
    {
        EncoderPlan? plan;
        try
        {
            plan = JsonUtil.Deserialize<EncoderPlan>(json);
        }
        catch (Exception ex)
        {
            Say(HostProtocol.Error, $"{HostProtocol.ErrorStart} the plan did not read: {ex.Message}");
            return;
        }
        if (plan is null)
        {
            Say(HostProtocol.Error, $"{HostProtocol.ErrorStart} no plan");
            return;
        }
        Stop();
        try
        {
            lock (_gate)
            {
                if (plan.UsesRing) _ring = SharedFrameRing.Open(plan.Ring);
                if (plan.Kind == EncoderPlan.Null)
                {
                    StartCounting();
                }
                else
                {
                    var failure = StartEncoding(plan);
                    if (failure is not null)
                    {
                        Say(HostProtocol.Error, failure);
                        StopLocked();
                        return;
                    }
                }
            }
            Say(HostProtocol.Started);
            Beat();
        }
        catch (Exception ex)
        {
            Say(HostProtocol.Error, $"{HostProtocol.ErrorStart} {FaultWords.Describe(ex)}");
            Stop();
        }
    }

    /// <summary>The null plan: frames taken from the ring and counted, so the plumbing can be proven without an encoder.</summary>
    private void StartCounting()
    {
        var ring = _ring ?? throw new InvalidOperationException("the null plan needs a ring");
        _counting = true;
        _state = "counting";
        _counter = new Thread(() =>
        {
            var frame = new byte[ring.FrameBytes];
            long last = 0;
            while (_counting)
            {
                var seq = ring.WaitForFrame(last, 200);
                if (seq < 0)
                {
                    if (ring.IsClosed) break;
                    continue;
                }
                if (ring.TryRead(last, frame, out var got))
                {
                    last = got;
                    Interlocked.Increment(ref _counted);
                }
            }
        })
        { IsBackground = true, Name = "encoder-count" };
        _counter.Start();
    }

    /// <summary>libVLC in this process: the plan's media through the ring or off the desktop, played into the plan's stream output. Null when it is running; the ERROR words otherwise.</summary>
    private string? StartEncoding(EncoderPlan plan)
    {
        var vlc = VlcRuntime.Create(out var failure, "--no-video-title-show", "--quiet");
        if (vlc is null) return $"{HostProtocol.ErrorLibVlc} Streaming needs libVLC — use the full build (or install VLC). {failure}".TrimEnd();
        _vlc = vlc;
        Media media;
        if (plan.Kind == EncoderPlan.Rendered)
        {
            var ring = _ring ?? throw new InvalidOperationException("a rendered plan needs a ring");
            _input = new RingMediaInput(ring);
            media = new Media(vlc, _input, plan.Options);
        }
        else
        {
            media = new Media(vlc, plan.Mrl, FromType.FromLocation, plan.Options);
        }
        _media = media;
        var player = new MediaPlayer(media);
        _player = player;
        if (!player.Play()) return $"{HostProtocol.ErrorStart} Encoder failed to start — check the destination URLs.";
        _state = "encoding";
        _reportedError = false;
        return null;
    }

    private void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        // The counter leaves before the ring it reads goes; libVLC's own input thread leaves inside Stop() below.
        _counting = false;
        var counter = _counter;
        _counter = null;
        if (counter is not null && counter.IsAlive && !counter.Join(TimeSpan.FromSeconds(2))) Say(HostProtocol.Log, "the frame counter did not stop in time");
        _input?.Close();
        if (_player is not null || _media is not null || _vlc is not null) ReleaseVlc();
        _input = null;
        _ring?.Dispose();
        _ring = null;
        _state = "idle";
    }

    private void ReleaseVlc()
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _media?.Dispose();
            _vlc?.Dispose();
        }
        catch (Exception ex)
        {
            Say(HostProtocol.Log, $"stopping the encoder: {ex.Message}");
        }
        _player = null;
        _media = null;
        _vlc = null;
    }

    private void BeatLoop()
    {
        while (true)
        {
            Thread.Sleep(HostProtocol.BeatEvery);
            try
            {
                Beat();
            }
            catch
            {
                return;   // stdout is gone: the desk is gone
            }
        }
    }

    private void Beat()
    {
        long frames;
        string state;
        string? error = null;
        // A START in progress holds the gate through libVLC's bring-up, which can take seconds on a cold
        // plugin cache: the beat never waits for it — a host that is starting is alive, and says so.
        if (!Monitor.TryEnter(_gate, 0))
        {
            Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(Interlocked.Read(ref _counted), "starting")));
            return;
        }
        try
        {
            frames = _input?.Frames ?? Interlocked.Read(ref _counted);
            state = _state;
            if (_player is not null)
            {
                var vlcState = PlayerState();
                state = vlcState;
                // A live encode never ends by itself: Ended is the stream output gone (a destination that
                // closed the connection, a capture that stopped) as surely as Error — said once, as an error.
                if (!_reportedError && vlcState is nameof(VLCState.Error) or nameof(VLCState.Ended))
                {
                    _reportedError = true;
                    error = vlcState == nameof(VLCState.Error)
                        ? $"{HostProtocol.ErrorEncoder} the encoder reported an error"
                        : $"{HostProtocol.ErrorEncoder} the encoder ended on its own — a destination closed the connection, or the capture stopped";
                }
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
        if (error is not null) Say(HostProtocol.Error, error);
        Say(HostProtocol.Beat, JsonUtil.Serialize(new HostBeat(frames, state)));
    }

    private string PlayerState()
    {
        try
        {
            return _player?.State.ToString() ?? "gone";
        }
        catch
        {
            return "gone";
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// libVLC's memory input over the shared ring: the demuxer reads raw BGRA frames in the chunks it
/// asks for, a frame copied out of the ring once into a buffer allocated once, the newest frame
/// winning when the encoder is slow. Zero when the ring closes — the end of the stream.
/// </summary>
internal sealed class RingMediaInput : MediaInput
{
    private readonly SharedFrameRing _ring;
    private readonly byte[] _frame;
    private int _offset;
    private long _lastSeq;
    private long _frames;
    private volatile bool _closed;

    public RingMediaInput(SharedFrameRing ring)
    {
        _ring = ring;
        _frame = new byte[ring.FrameBytes];
        _offset = _frame.Length;   // nothing read yet
    }

    /// <summary>Frames handed to the encoder so far.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    public override bool Open(out ulong size)
    {
        size = ulong.MaxValue;   // a live feed has no length
        return true;
    }

    public override int Read(IntPtr buf, uint len)
    {
        if (len == 0) return 0;
        while (!_closed)
        {
            if (_offset >= _frame.Length)
            {
                var seq = _ring.WaitForFrame(_lastSeq, 500);
                if (seq < 0)
                {
                    if (_ring.IsClosed || _closed) return 0;
                    continue;
                }
                if (!_ring.TryRead(_lastSeq, _frame, out var got)) continue;
                _lastSeq = got;
                _offset = 0;
                Interlocked.Increment(ref _frames);
            }
            var n = (int)Math.Min(len, (uint)(_frame.Length - _offset));
            Marshal.Copy(_frame, _offset, buf, n);
            _offset += n;
            return n;
        }
        return 0;
    }

    public override bool Seek(ulong offset) => false;

    public override void Close() => _closed = true;
}

/// <summary>Where libVLC is on this machine and how it is brought up — the desk's decoders and the encoder host read the same table.</summary>
internal static class VlcRuntime
{
    /// <summary>The folders a libVLC may sit in, in the order they are tried; null is the library's own probing.</summary>
    public static IEnumerable<string?> CandidateDirs()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "libvlc", "win-x64");
        yield return Path.Combine(exeDir, "libvlc");
        yield return Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        yield return Path.Combine(AppContext.BaseDirectory, "libvlc");
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf, "VideoLAN", "VLC");
        }
        yield return null;
    }

    /// <summary>A cheap look, no library loaded: a libvlc folder with the library in it exists somewhere on the table.</summary>
    public static bool Present => CandidateDirs().Any(d => d is not null && Directory.Exists(d) &&
        (File.Exists(Path.Combine(d, "libvlc.dll")) || File.Exists(Path.Combine(d, "libvlc.so")) || File.Exists(Path.Combine(d, "libvlc.dylib"))));

    /// <summary>libVLC brought up from the first folder that works, with these options; null and the reason when none does.</summary>
    public static LibVLC? Create(out string failure, params string[] options)
    {
        var problems = new List<string>();
        foreach (var dir in CandidateDirs())
        {
            try
            {
                if (dir is not null && !Directory.Exists(dir)) continue;
                LibVLCSharp.Shared.Core.Initialize(dir);
                var vlc = new LibVLC(options);
                failure = "";
                return vlc;
            }
            catch (Exception ex)
            {
                problems.Add($"{dir ?? "default"}: {ex.Message}");
            }
        }
        failure = string.Join("; ", problems);
        return null;
    }
}
