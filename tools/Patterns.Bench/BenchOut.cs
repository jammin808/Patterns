using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Patterns.Bench;

/// <summary>
/// The bench's standard output: one JSON object per line, UTF-8, flushed at once, from any thread one line at a time.
/// The first line is the ready line (or the refusal or failure that ends the start); every later line answers a control
/// line or reports a proposal's words. Nothing else writes here — the desk logs to its folder — and no line carries a
/// secret: the token, a Stagewatch key and a window's address stay in the files they came from.
/// </summary>
internal sealed class BenchOut : IDisposable
{
    /// <summary>Plain JSON for a reader of lines, not a page: '+', quotes and words stay as written, control characters escaped.</summary>
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public BenchOut(Stream stdout)
    {
        _writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>One object as one line.</summary>
    public void Line(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        lock (_gate)
        {
            try
            {
                _writer.WriteLine(json);
            }
            catch (IOException)
            {
                // the reader has gone (its end of the pipe closed): the bench carries on until it is told to stop
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
