using Avalonia;
using Patterns.Core.Geometry;

namespace Patterns.App.Services;

/// <summary>
/// The seam between the desk's UI geometry and the platform assembly (round 65.12): the probes
/// take the core's <see cref="RasterRect"/>, the desk holds Avalonia's <see cref="PixelRect"/>; a
/// display's bounds cross here and nowhere else.
/// </summary>
public static class PlatformSeams
{
    public static RasterRect ToRaster(this PixelRect r) => RasterRect.Create(r.X, r.Y, r.Width, r.Height);
}
