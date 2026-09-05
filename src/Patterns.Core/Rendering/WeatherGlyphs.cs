using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// The sky in ten glyphs, drawn with paths so they scale to any screen and need no font or
/// image: a sun, a moon, clouds, drops, flakes, a bolt, fog lines. One box in, the glyph fills
/// it; the colours are fixed and only the alpha follows the chip's opacity.
/// </summary>
public static class WeatherGlyphs
{
    public static void Draw(SKCanvas c, PaintCache pc, WeatherSky sky, bool night, SKRect box, float opacity)
    {
        var a = (byte)Math.Clamp(opacity * 255, 0, 255);
        var sun = new SKColor(0xFF, 0xD5, 0x4A, a);
        var moon = new SKColor(0xE6, 0xE9, 0xF5, a);
        var cloud = new SKColor(0xEC, 0xEF, 0xF4, a);
        var darkCloud = new SKColor(0xB4, 0xBC, 0xC9, a);
        var rain = new SKColor(0x7C, 0xC4, 0xFF, a);
        var snow = new SKColor(0xFF, 0xFF, 0xFF, a);
        var bolt = new SKColor(0xFF, 0xD0, 0x30, a);
        var fog = new SKColor(0xC9, 0xCF, 0xD8, a);

        switch (sky)
        {
            case WeatherSky.Clear:
                if (night) Moon(c, pc, box, 0.5f, 0.5f, 0.3f, moon);
                else Sun(c, pc, box, 0.5f, 0.5f, 0.26f, sun);
                break;
            case WeatherSky.Fair:
                if (night) Moon(c, pc, box, 0.4f, 0.38f, 0.24f, moon);
                else Sun(c, pc, box, 0.4f, 0.38f, 0.22f, sun);
                Cloud(c, pc, box, 0.3f, 0.45f, 0.72f, cloud);
                break;
            case WeatherSky.PartlyCloudy:
                if (night) Moon(c, pc, box, 0.34f, 0.34f, 0.2f, moon);
                else Sun(c, pc, box, 0.34f, 0.34f, 0.19f, sun);
                Cloud(c, pc, box, 0.12f, 0.3f, 0.86f, cloud);
                break;
            case WeatherSky.Cloudy:
                Cloud(c, pc, box, 0.06f, 0.18f, 0.94f, cloud);
                break;
            case WeatherSky.Fog:
                Cloud(c, pc, box, 0.12f, 0.06f, 0.78f, fog);
                for (var i = 0; i < 3; i++)
                {
                    var y = box.Top + box.Height * (0.7f + i * 0.11f);
                    c.DrawLine(box.Left + box.Width * (0.16f + i * 0.06f), y, box.Left + box.Width * (0.84f - i * 0.04f), y, pc.StrokeAA(fog, box.Width * 0.055f));
                }
                break;
            case WeatherSky.Showers:
                Cloud(c, pc, box, 0.08f, 0.08f, 0.9f, cloud);
                Drops(c, pc, box, 3, 0.16f, rain);
                break;
            case WeatherSky.Rain:
                Cloud(c, pc, box, 0.08f, 0.06f, 0.9f, darkCloud);
                Drops(c, pc, box, 5, 0.2f, rain);
                break;
            case WeatherSky.Sleet:
                Cloud(c, pc, box, 0.08f, 0.06f, 0.9f, darkCloud);
                Drops(c, pc, box, 2, 0.18f, rain);
                Flakes(c, pc, box, 2, snow, offset: true);
                break;
            case WeatherSky.Snow:
                Cloud(c, pc, box, 0.08f, 0.06f, 0.9f, cloud);
                Flakes(c, pc, box, 3, snow, offset: false);
                break;
            case WeatherSky.Thunder:
                Cloud(c, pc, box, 0.08f, 0.04f, 0.9f, darkCloud);
                Bolt(c, pc, box, bolt);
                break;
            default:
                // Unknown: a cloud outline — something is coming, we do not know what.
                Cloud(c, pc, box, 0.06f, 0.18f, 0.94f, new SKColor(0xEC, 0xEF, 0xF4, (byte)(a / 2)));
                break;
        }
    }

    private static void Sun(SKCanvas c, PaintCache pc, SKRect box, float cx, float cy, float r, SKColor color)
    {
        var x = box.Left + box.Width * cx;
        var y = box.Top + box.Height * cy;
        var radius = box.Width * r;
        c.DrawCircle(x, y, radius, pc.FillAA(color));
        var stroke = pc.StrokeAA(color, box.Width * 0.06f);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            var dx = (float)Math.Cos(angle);
            var dy = (float)Math.Sin(angle);
            c.DrawLine(x + dx * radius * 1.35f, y + dy * radius * 1.35f, x + dx * radius * 1.75f, y + dy * radius * 1.75f, stroke);
        }
    }

    private static void Moon(SKCanvas c, PaintCache pc, SKRect box, float cx, float cy, float r, SKColor color)
    {
        var x = box.Left + box.Width * cx;
        var y = box.Top + box.Height * cy;
        var radius = box.Width * r;
        using var disc = new SKPath();
        disc.AddCircle(x, y, radius);
        using var bite = new SKPath();
        bite.AddCircle(x + radius * 0.55f, y - radius * 0.35f, radius * 0.9f);
        using var crescent = disc.Op(bite, SKPathOp.Difference);
        c.DrawPath(crescent ?? disc, pc.FillAA(color));
    }

    /// <summary>A cloud filling a sub-box: left/top as shares of the box, width as a share; the height follows.</summary>
    private static void Cloud(SKCanvas c, PaintCache pc, SKRect box, float left, float top, float width, SKColor color)
    {
        var w = box.Width * width;
        var h = w * 0.62f;
        var x = box.Left + box.Width * left;
        var y = box.Top + box.Height * top;
        using var path = new SKPath { FillType = SKPathFillType.Winding };
        path.AddRoundRect(new SKRect(x, y + h * 0.5f, x + w, y + h), h * 0.24f, h * 0.24f);
        path.AddCircle(x + w * 0.34f, y + h * 0.5f, w * 0.24f);
        path.AddCircle(x + w * 0.62f, y + h * 0.42f, w * 0.3f);
        c.DrawPath(path, pc.FillAA(color));
    }

    private static void Drops(SKCanvas c, PaintCache pc, SKRect box, int count, float length, SKColor color)
    {
        var stroke = pc.StrokeAA(color, box.Width * 0.05f);
        for (var i = 0; i < count; i++)
        {
            var x = box.Left + box.Width * (0.5f + (i - (count - 1) / 2f) * 0.14f);
            var y0 = box.Top + box.Height * 0.78f;
            var y1 = y0 + box.Height * length;
            c.DrawLine(x, y0, x - box.Width * 0.05f, y1, stroke);
        }
    }

    private static void Flakes(SKCanvas c, PaintCache pc, SKRect box, int count, SKColor color, bool offset)
    {
        var fill = pc.FillAA(color);
        for (var i = 0; i < count; i++)
        {
            var slot = offset ? i * 2 + 1 : i;
            var total = offset ? count * 2 : count;
            var x = box.Left + box.Width * (0.5f + (slot - (total - 1) / 2f) * 0.14f);
            var y = box.Top + box.Height * 0.88f;
            c.DrawCircle(x, y, box.Width * 0.045f, fill);
        }
    }

    private static void Bolt(SKCanvas c, PaintCache pc, SKRect box, SKColor color)
    {
        using var path = new SKPath();
        path.MoveTo(P(box, 0.54f, 0.58f));
        path.LineTo(P(box, 0.42f, 0.8f));
        path.LineTo(P(box, 0.51f, 0.8f));
        path.LineTo(P(box, 0.44f, 0.99f));
        path.LineTo(P(box, 0.62f, 0.74f));
        path.LineTo(P(box, 0.53f, 0.74f));
        path.LineTo(P(box, 0.6f, 0.58f));
        path.Close();
        c.DrawPath(path, pc.FillAA(color));
    }

    private static SKPoint P(SKRect box, float x, float y) => new(box.Left + box.Width * x, box.Top + box.Height * y);
}
