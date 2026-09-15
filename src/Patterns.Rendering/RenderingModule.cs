using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Services;

namespace Patterns.Rendering;

/// <summary>
/// The render module announcing itself to the core: what the core asks of a drawing side and
/// cannot do alone — the bytes the picture cache, the frame pools and the retiring frames hold
/// (the media memory view and the pressure ladder read them) and the picture codec the
/// assistant's attachments use. A composition root that draws calls this once; a role that never draws leaves the
/// hooks empty and the core's answers are honest for it (nothing held, no pictures read).
/// </summary>
public static class RenderingModule
{
    private static int _registered;

    /// <summary>Whether a drawing side registered in this process.</summary>
    public static bool Registered => Volatile.Read(ref _registered) != 0;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        MediaMemory.Source = static () => new MediaMemory.MediaBytes(
            ImageCache.Bytes, RetiredFrames.BytesOf(RetiredFrames.Kind.Picture),
            FramePools.Bytes, FramePools.RetiringBytes, RetiredFrames.BytesOf(RetiredFrames.Kind.Frame));
        Pictures.Shrinker = PictureShrinker.Shrink;
    }
}
