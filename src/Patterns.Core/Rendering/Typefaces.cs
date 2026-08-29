using System.Reflection;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// Embedded Inter typefaces so text renders identically on any machine (stripped-down
/// media servers included). Falls back to the system default if resources fail to load.
/// </summary>
public static class Typefaces
{
    public static readonly SKTypeface Regular = LoadEmbedded("Inter-Regular.ttf") ?? SKTypeface.Default;
    public static readonly SKTypeface SemiBold = LoadEmbedded("Inter-SemiBold.ttf") ?? SKTypeface.Default;

    private static SKTypeface? LoadEmbedded(string fileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resource = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (resource is null) return null;
            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is null) return null;
            using var data = SKData.Create(stream);
            return SKTypeface.FromData(data);
        }
        catch (Exception ex)
        {
            Log.Warn($"Embedded font '{fileName}' failed to load; using system default.", ex);
            return null;
        }
    }
}
