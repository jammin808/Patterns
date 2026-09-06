using Patterns.Core.Rendering;

namespace Patterns.App.Rendering;

/// <summary>
/// The sinks of a control whose draw ops run on the compositor's render thread. The control opens
/// the guard when it joins the visual tree and closes it when it leaves (the UI thread); its draw
/// ops draw through <see cref="Draw"/> (the render thread). One gate for both: a draw op queued
/// before the control left and run after it draws nothing instead of touching freed Skia handles,
/// a close waits for the frame in progress, and a control that comes back — a page tab left and
/// re-entered — gets fresh sinks, never the ones it disposed. <see cref="RenderPipeline"/> keeps
/// the same rule for the wall's tiles and the windows; this is the same rule for a control that
/// draws by hand.
/// </summary>
public sealed class SinkGuard : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SinkState> _sinks = new(StringComparer.Ordinal);
    private bool _open;

    /// <summary>Whether draws go through: opened, and not yet closed.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate) return _open;
        }
    }

    /// <summary>How many sinks are alive right now (a closed guard has none).</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _sinks.Count;
        }
    }

    /// <summary>The control has joined the visual tree: draws may go through; the sinks are made as they are asked for.</summary>
    public void Open()
    {
        lock (_gate) _open = true;
    }

    /// <summary>The control has left the visual tree: every sink goes, and a draw op that runs later draws nothing.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _open = false;
            foreach (var sink in _sinks.Values) sink.Dispose();
            _sinks.Clear();
        }
    }

    /// <summary>
    /// The frame, under the gate: <paramref name="draw"/> gets the sink for any key it asks for
    /// (made on first use, kept until the close). False, and nothing drawn, when the guard is closed.
    /// </summary>
    public bool Draw(Action<Func<string, SinkState>> draw)
    {
        lock (_gate)
        {
            if (!_open) return false;
            draw(SinkFor);
            return true;
        }
    }

    private SinkState SinkFor(string key)
    {
        if (!_sinks.TryGetValue(key, out var sink))
        {
            sink = new SinkState();
            _sinks[key] = sink;
        }
        return sink;
    }

    public void Dispose() => Close();
}
