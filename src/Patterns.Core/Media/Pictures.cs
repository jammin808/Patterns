namespace Patterns.Core.Media;

/// <summary>A picture brought down for sending: its bytes and media type, the size it was and the size it is sent at.</summary>
public sealed record ShrunkPicture(byte[] Bytes, string MediaType, int Width, int Height, int SentWidth, int SentHeight)
{
    public bool Scaled => SentWidth != Width || SentHeight != Height;
}

/// <summary>
/// A picture decoded, fitted within a longest side and re-encoded under a byte ceiling — PNG when
/// asked and it fits, JPEG otherwise; null for bytes no codec knows. The lowest JPEG quality is
/// returned even over the ceiling, so the caller can say "too large even reduced".
/// </summary>
public delegate ShrunkPicture? PictureShrink(byte[] bytes, int maxSide, int maxBytes, bool preferPng);

/// <summary>
/// What the core asks of a drawing side for pictures and cannot do alone: the codec. The render
/// module registers its shrinker when it is built; the assistant's attachment rules use it and
/// say so when a build has none. The rules — what is read, how large, the words — stay here.
/// </summary>
public static class Pictures
{
    public static PictureShrink? Shrinker { get; set; }
}
