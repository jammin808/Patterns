using System.Text;

namespace Patterns.App.Services;

/// <summary>
/// Lines off a stream with a ceiling. <c>StreamReader.ReadLineAsync</c> buffers a line until it
/// ends, however long: a peer that never sends the newline is a peer that owns the desk's memory
/// and a connection slot for as long as it likes. Here a line past the ceiling is not a line —
/// the read throws, the caller answers or closes, and nothing past the ceiling is kept. The
/// ceiling can be raised once a peer has proved itself (the twin's key), so the first line of a
/// link is held short and a show's worth of JSON still travels after.
/// </summary>
public sealed class BoundedLineReader
{
    private readonly Stream _stream;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _ended;

    public BoundedLineReader(Stream stream, int maxLineBytes)
    {
        _stream = stream;
        MaxLineBytes = maxLineBytes;
        _buffer = new byte[Math.Min(Math.Max(maxLineBytes + 1, 256), 65536)];
    }

    /// <summary>The most bytes a line may run to before it is refused; raise it once the peer has proved itself.</summary>
    public int MaxLineBytes { get; set; }

    /// <summary>
    /// The next line without its ending (CRLF or LF), the last unfinished line at the end of the
    /// stream, or null once the stream has nothing more. Throws <see cref="InvalidDataException"/>
    /// the moment a line runs past <see cref="MaxLineBytes"/>.
    /// </summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var length = newline - _start;
                if (length > MaxLineBytes) throw new InvalidDataException($"a line past {MaxLineBytes} bytes");
                if (length > 0 && _buffer[newline - 1] == (byte)'\r') length--;
                var line = Encoding.UTF8.GetString(_buffer, _start, length);
                _start = newline + 1;
                return line;
            }
            var pending = _end - _start;
            if (pending > MaxLineBytes) throw new InvalidDataException($"a line past {MaxLineBytes} bytes");
            if (_ended)
            {
                if (pending == 0) return null;
                var last = Encoding.UTF8.GetString(_buffer, _start, pending).TrimEnd('\r');
                _start = _end = 0;
                return last;
            }
            // Room for more: slide what is pending to the front, grow towards the ceiling if the buffer is full.
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, pending);
                _end = pending;
                _start = 0;
            }
            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Min(Math.Max(_buffer.Length * 2, 256), MaxLineBytes + 1));
            }
            var n = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct);
            if (n <= 0) _ended = true;
            else _end += n;
        }
    }
}
