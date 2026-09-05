using Patterns.Core.Media;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// A deck that is not a PDF yet — LibreOffice is converting it — or could not become one: no
/// pages, no frame, a status the placeholder card and the desk read. <see cref="DeckEngine"/>
/// puts the PDF source in its place when the conversion lands.
/// </summary>
public sealed class PendingDeckSource : IDeckSource
{
    private volatile string _status;

    public PendingDeckSource(string path, string status)
    {
        Path = path;
        _status = status;
    }

    public string Path { get; }

    public int PageCount => 0;

    public int Page => 0;

    public SKSize PageShape => new(16, 9);

    /// <summary>The conversion failed; the status says why and the deck stays a card until RELOAD.</summary>
    public bool Failed { get; private set; }

    public bool GoTo(int page) => false;

    public void Fail(string why)
    {
        Failed = true;
        _status = why;
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop) => false;

    public SKSizeI? FrameSize => null;

    public bool IsPlaying => false;

    public bool IsEnded => false;

    public double DurationSeconds => 0;

    public string StatusText => _status;
}
