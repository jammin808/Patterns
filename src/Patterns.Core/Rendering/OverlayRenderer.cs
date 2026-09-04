using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// Overlay layers composited over any pattern: clock/date, countdown, logo watermark,
/// message ticker (canvas space) and the info chip / identify badge (viewport space).
/// </summary>
public static class OverlayRenderer
{
    public static void RenderCanvasOverlays(SKCanvas c, in PatternFrame f)
    {
        var overlays = f.Snapshot.State.Overlays;

        if (overlays.Logo.Enabled)
        {
            DrawLogo(c, in f, overlays.Logo);
        }

        if (overlays.Clock.Enabled)
        {
            DrawClock(c, in f, overlays.Clock);
        }

        var cd = f.Snapshot.State.Countdown;
        if (cd.Enabled)
        {
            DrawCountdown(c, in f, cd);
        }

        var msg = overlays.Message;
        if (msg.Enabled &&
            (!string.IsNullOrWhiteSpace(msg.Text) || (msg.UseFeed && f.Snapshot.FeedText.Length > 0)))
        {
            DrawMessage(c, in f, msg);
        }
    }

    private static void DrawLogo(SKCanvas c, in PatternFrame f, LogoOverlay o)
    {
        var logo = ImageCache.Get(f.Snapshot.State.Brand.LogoPath);
        if (logo is null) return;

        var targetH = (float)(f.H * o.HeightPct / 100);
        var targetW = targetH * logo.Width / Math.Max(1, logo.Height);
        var margin = Math.Max(10f, f.H * 0.03f);
        var rect = DrawUtil.Anchored(f.Canvas, targetW, targetH, o.Anchor, margin);

        var paint = f.Paints.FillAA(SKColors.White.WithAlpha((byte)(o.Opacity * 255)));
        c.DrawImage(logo, rect, DrawUtil.Smooth, paint);
    }

    private static void DrawClock(SKCanvas c, in PatternFrame f, ClockOverlay o)
    {
        var pc = f.Paints;
        var brandFamily = f.Snapshot.State.Brand.FontFamily;
        var now = f.Ctx.Now;

        var time = (o.TwentyFourHour, o.ShowSeconds) switch
        {
            (true, true) => now.ToString("HH:mm:ss"),
            (true, false) => now.ToString("HH:mm"),
            (false, true) => now.ToString("h:mm:ss tt"),
            (false, false) => now.ToString("h:mm tt"),
        };

        var size = (float)(f.H * o.SizePct / 100);
        var font = pc.FontFor(brandFamily, bold: true);
        font.Size = size;
        var timeW = DrawUtil.MeasureFixedDigits(time, font);

        var dateFontSize = size * 0.32f;
        string? date = o.ShowDate ? now.ToString("ddd d MMM yyyy") : null;
        float dateW = 0;
        if (date is not null)
        {
            var df = pc.FontFor(brandFamily, bold: false);
            df.Size = dateFontSize;
            dateW = df.MeasureText(date);
        }

        var padX = size * 0.4f;
        var padY = size * 0.28f;
        var boxW = Math.Max(timeW, dateW) + padX * 2;
        var boxH = size + padY * 2 + (date is not null ? dateFontSize * 1.5f : 0);
        var margin = Math.Max(10f, f.H * 0.03f);
        var rect = DrawUtil.Anchored(f.Canvas, boxW, boxH, o.Anchor, margin);

        var alpha = (byte)(o.Opacity * 255);
        if (o.Pill)
        {
            c.DrawRoundRect(rect, size * 0.22f, size * 0.22f,
                pc.FillAA(f.Palette.ChipBg.WithAlpha((byte)(f.Palette.ChipBg.Alpha * o.Opacity))));
        }

        var textColor = f.Color(o.TextColor, f.Palette.Text).WithAlpha(alpha);
        var timeCy = rect.Top + padY + size / 2;
        DrawUtil.FixedDigitsCentered(c, time, rect.MidX, timeCy, font, pc.Text(textColor));

        if (date is not null)
        {
            var df = pc.FontFor(brandFamily, bold: false);
            df.Size = dateFontSize;
            DrawUtil.TextCentered(c, date, rect.MidX, rect.Bottom - padY * 0.5f - dateFontSize * 0.55f,
                df, pc.Text(textColor.WithAlpha((byte)(alpha * 0.82))));
        }
    }

    private static void DrawCountdown(SKCanvas c, in PatternFrame f, CountdownConfig cd)
    {
        var pc = f.Paints;
        var status = CountdownService.Evaluate(cd, f.Ctx.Now, f.Ctx.UtcNow);
        if (status.Phase == CountdownPhase.Idle) return;

        var over = status.Phase == CountdownPhase.Over;
        if (over && cd.EndBehavior == CountdownEndBehavior.Flash)
        {
            // 2 Hz flash: skip drawing on the off beat.
            if ((long)(f.Ctx.Time * 2) % 2 == 1) return;
        }

        var showMessage = over && cd.EndBehavior == CountdownEndBehavior.Message && !string.IsNullOrWhiteSpace(cd.EndMessage);
        var digits = showMessage ? cd.EndMessage : CountdownService.Format(status.Remaining);

        var brandFamily = f.Snapshot.State.Brand.FontFamily;
        var size = (float)(f.H * cd.SizePct / 100);
        var font = pc.FontFor(brandFamily, bold: true);
        font.Size = size;
        var mainW = showMessage ? font.MeasureText(digits) : DrawUtil.MeasureFixedDigits(digits, font);

        var labelSize = size * 0.24f;
        var label = cd.Label?.Trim() ?? "";
        float labelW = 0;
        if (label.Length > 0)
        {
            var lf = pc.FontFor(brandFamily, bold: false);
            lf.Size = labelSize;
            labelW = lf.MeasureText(label);
        }

        var padX = size * 0.45f;
        var padY = size * 0.3f;
        var barH = cd.ShowProgressBar ? size * 0.09f + size * 0.18f : 0;
        var boxW = Math.Max(mainW, labelW) + padX * 2;
        var boxH = size + padY * 2 + (label.Length > 0 ? labelSize * 1.7f : 0) + barH;
        var margin = Math.Max(10f, f.H * 0.03f);
        var rect = DrawUtil.Anchored(f.Canvas, boxW, boxH, cd.Anchor, margin);

        c.DrawRoundRect(rect, size * 0.16f, size * 0.16f, pc.FillAA(f.Palette.ChipBg));
        c.DrawRoundRect(rect, size * 0.16f, size * 0.16f, pc.StrokeAA(f.Palette.Accent.WithAlpha(0x70), Math.Max(1.5f, size * 0.02f)));

        var y = rect.Top + padY;
        if (label.Length > 0)
        {
            var lf = pc.FontFor(brandFamily, bold: false);
            lf.Size = labelSize;
            DrawUtil.TextCentered(c, label, rect.MidX, y + labelSize * 0.6f, lf, pc.Text(f.Palette.Accent));
            y += labelSize * 1.7f;
        }

        var urgent = !over && status.Remaining.TotalSeconds <= 60;
        var baseDigits = f.Color(cd.TextColor, f.Palette.Text);
        var digitsColor = over ? f.Palette.Accent : urgent ? new SKColor(0xFF, 0x64, 0x50) : baseDigits;
        if (showMessage)
        {
            DrawUtil.TextCentered(c, digits, rect.MidX, y + size / 2, font, pc.Text(digitsColor));
        }
        else
        {
            DrawUtil.FixedDigitsCentered(c, digits, rect.MidX, y + size / 2, font, pc.Text(digitsColor));
        }

        if (cd.ShowProgressBar)
        {
            var barW = boxW - padX * 2;
            var bh = size * 0.09f;
            var bx = rect.Left + padX;
            var by = rect.Bottom - padY * 0.4f - bh;
            c.DrawRoundRect(SKRect.Create(bx, by, barW, bh), bh / 2, bh / 2, pc.FillAA(new SKColor(255, 255, 255, 0x2E)));
            var w = (float)(barW * status.Progress01);
            if (w > 1)
            {
                c.DrawRoundRect(SKRect.Create(bx, by, w, bh), bh / 2, bh / 2, pc.FillAA(f.Palette.Accent));
            }
        }
    }

    private static void DrawMessage(SKCanvas c, in PatternFrame f, MessageOverlay o)
    {
        var pc = f.Paints;
        var size = (float)(f.H * o.SizePct / 100);
        var family = f.Snapshot.State.Brand.FontFamily;
        var font = pc.FontFor(family, bold: true);
        font.Size = size;
        // A live feed replaces the static text when configured and delivering.
        var text = o.UseFeed && f.Snapshot.FeedText.Length > 0 ? f.Snapshot.FeedText : o.Text;
        var textW = f.Sink.Ticker.MeasuredWidth(text, font, family);
        var messageColor = f.Color(o.TextColor, f.Palette.Text);
        var peak = (byte)Math.Clamp(o.BackgroundStrength * 255, 0, 255);

        if (o.Scroll && textW > 0)
        {
            var band = DrawUtil.Anchored(f.Canvas, f.W, size * 1.6f, o.Anchor, Math.Max(10f, f.H * 0.03f));
            switch (o.Background)
            {
                case MessageBackground.Chip:
                    c.DrawRect(band, pc.Fill(new SKColor(0, 0, 0, peak)));
                    break;
                case MessageBackground.Fade:
                    DrawFadeBand(c, in f, band, o.Anchor, size, peak);
                    break;
            }

            // The train of copies is periodic, so its phase is the travel distance modulo the
            // copy period — wrapping then moves the train by exactly one copy, which cannot be
            // seen. The distance comes from the snapshot's line so every sink agrees on it.
            var period = TickerMath.Period(textW, f.W);
            var line = f.Snapshot.Ticker ?? TickerLine.From(o.ScrollPxPerSec);
            var distance = line.DistanceAt(f.Ctx.Time);
            var m = font.Metrics;
            var baseline = band.MidY - (m.Ascent + m.Descent) / 2;
            foreach (var x in TickerMath.CopyPositions(distance, period, textW, f.W))
            {
                c.DrawText(text, x, baseline, SKTextAlign.Left, font, pc.Text(messageColor));
            }
            return;
        }

        switch (o.Background)
        {
            case MessageBackground.None:
                DrawUtil.Chip(c, text, f.Canvas, o.Anchor, size, pc, messageColor, SKColors.Transparent, fontOverride: font);
                break;
            case MessageBackground.Chip:
                DrawUtil.Chip(c, text, f.Canvas, o.Anchor, size, pc, messageColor, new SKColor(0, 0, 0, peak), fontOverride: font);
                break;
            case MessageBackground.Fade:
                DrawFadeBand(c, in f, DrawUtil.ChipBounds(textW, f.Canvas, o.Anchor, size), o.Anchor, size, peak);
                DrawUtil.Chip(c, text, f.Canvas, o.Anchor, size, pc, messageColor, SKColors.Transparent, fontOverride: font);
                break;
            default:
                DrawUtil.Chip(c, text, f.Canvas, o.Anchor, size, pc, messageColor, f.Palette.ChipBg, fontOverride: font);
                break;
        }
    }

    /// <summary>
    /// The soft backdrop: a full-width band around the text row, darkest at the edge the message
    /// sits on and clear a text-height beyond the row — the classic lower third. A message in the
    /// middle row gets a band that is darkest on its own centre line and fades both ways.
    /// </summary>
    private static void DrawFadeBand(SKCanvas c, in PatternFrame f, SKRect row, Anchor9 anchor, float size, byte peak)
    {
        var feather = size * 1.2f;
        var top = (int)anchor / 3 == 0;
        var bottom = (int)anchor / 3 == 2;
        SKRect band;
        float darkY, clearY;
        if (top)
        {
            band = SKRect.Create(0, 0, f.W, row.Bottom + feather);
            darkY = 0;
            clearY = band.Bottom;
        }
        else if (bottom)
        {
            band = SKRect.Create(0, row.Top - feather, f.W, f.H - (row.Top - feather));
            darkY = f.H;
            clearY = band.Top;
        }
        else
        {
            // Middle row: two half-bands meeting on the text's centre line.
            var upper = SKRect.Create(0, row.Top - feather, f.W, row.MidY - (row.Top - feather));
            var lower = SKRect.Create(0, row.MidY, f.W, row.Bottom + feather - row.MidY);
            using var up = new SKPaint { Shader = f.Sink.Ticker.FadeShader(0, upper.Bottom, upper.Top, peak) };
            c.DrawRect(upper, up);
            using var down = new SKPaint { Shader = f.Sink.Ticker.FadeShader(0, lower.Top, lower.Bottom, peak) };
            c.DrawRect(lower, down);
            return;
        }
        using var paint = new SKPaint { Shader = f.Sink.Ticker.FadeShader(0, darkY, clearY, peak) };
        c.DrawRect(band, paint);
    }

    /// <summary>Viewport-space overlays: crisp per-sink info chip and the identify badge.</summary>
    public static void RenderViewportOverlays(
        SKCanvas c, ShowSnapshot snap, in RenderContext ctx, SinkState sink, Palette palette,
        bool blackout, PatternConfig? cfg = null)
    {
        // Badges only on real outputs — a "screen 0" badge on the preview would just confuse.
        if (ctx.Sink == SinkKind.Output && snap.IdentifyUntilUtc is { } until && until > ctx.UtcNow)
        {
            DrawIdentify(c, snap, in ctx, sink, palette, until);
        }

        // Soundcheck channel indicator — deliberately visible during blackout too.
        if (snap.ToneIndicator.Length > 0 && ctx.Sink != SinkKind.Thumbnail)
        {
            var toneSize = Math.Clamp(ctx.ViewportSize.Height * 0.035f, 12, 44);
            DrawUtil.Chip(c, $"♪ {snap.State.Tone.FrequencyHz:0} Hz — {snap.ToneIndicator}",
                ctx.ViewportSize, Anchor9.TopCenter, toneSize, sink.Paints, palette.Accent, palette.ChipBg);
        }

        if (blackout) return;

        DrawPip(c, snap, in ctx, sink, palette);

        var info = snap.State.Overlays.Info;
        if (info.Enabled && cfg is not null && ctx.Sink != SinkKind.Thumbnail)
        {
            var pc = sink.Paints;
            var fps = info.ShowFps ? $" · {ctx.MeasuredFps:0.0} fps" : "";
            var text = $"{ctx.SinkLabel} · {cfg.Kind}{fps}";
            var size = Math.Clamp(ctx.ViewportSize.Height * 0.02f, 10, 22);
            DrawUtil.Chip(c, text, ctx.ViewportSize, info.Anchor, size, pc, palette.Text, palette.ChipBg);
        }
    }

    /// <summary>Picture-in-picture live inset — drawn per viewport so every screen carries it.</summary>
    private static void DrawPip(SKCanvas c, ShowSnapshot snap, in RenderContext ctx, SinkState sink, Palette palette)
    {
        var pip = snap.State.Overlays.Pip;
        if (!pip.Enabled || ctx.Sink == SinkKind.Thumbnail) return;

        var source = Media.InputBus.For(pip.Source == PipSource.NdiFeed
            ? Media.InputKeys.Ndi(pip.NdiSourceName)
            : Media.InputKeys.Capture(pip.CaptureDevice));
        var pc = sink.Paints;
        int vw = ctx.ViewportSize.Width, vh = ctx.ViewportSize.Height;

        var w = (float)(vw * pip.WidthPct / 100.0);
        var aspect = source?.FrameSize is { } fs && fs.Height > 0 ? (float)fs.Width / fs.Height : 16f / 9f;
        var h = w / aspect;
        var margin = Math.Max(8f, vh * 0.02f);

        // Anchor9 grid: 0..8 → left/centre/right × top/middle/bottom.
        var col = (int)pip.Anchor % 3;
        var row = (int)pip.Anchor / 3;
        var x = col switch { 0 => margin, 1 => (vw - w) / 2, _ => vw - w - margin };
        var y = row switch { 0 => margin, 1 => (vh - h) / 2, _ => vh - h - margin };
        var rect = SKRect.Create(x, y, w, h);

        var alpha = (byte)Math.Clamp(pip.Opacity * 255, 0, 255);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), IsAntialias = true };

        if (source is null || !source.DrawFrame(c, rect, paint))
        {
            // No frames yet — a quiet slate so the operator sees where the inset will be.
            c.DrawRoundRect(rect, 6, 6, pc.FillAA(new SKColor(0x10, 0x12, 0x18, alpha)));
            var f = pc.FontRegular;
            f.Size = Math.Clamp(h * 0.12f, 10, 26);
            var label = source?.StatusText ?? (pip.Source == PipSource.NdiFeed ? "PiP: choose an NDI source" : "PiP: choose a capture device");
            DrawUtil.TextCentered(c, label, rect.MidX, rect.MidY, f, pc.Text(new SKColor(0x8A, 0x93, 0xA3, alpha)));
        }

        if (pip.ShowBorder)
        {
            c.DrawRoundRect(rect, 6, 6, pc.StrokeAA(palette.Accent.WithAlpha(alpha), Math.Max(1.5f, vh * 0.002f)));
        }
    }

    private static void DrawIdentify(
        SKCanvas c, ShowSnapshot snap, in RenderContext ctx, SinkState sink, Palette palette, DateTime until)
    {
        var pc = sink.Paints;
        int w = ctx.ViewportSize.Width, h = ctx.ViewportSize.Height;

        // Gentle pulse that fades out over the last second.
        var remaining = (until - ctx.UtcNow).TotalSeconds;
        var alpha = (byte)(Math.Clamp(remaining, 0, 1) * 255);

        DrawUtil.BorderInside(c, new SKRectI(0, 0, w, h), Math.Max(4, h / 90), pc.Fill(palette.Accent.WithAlpha(alpha)));

        var badge = Math.Min(w, h) * 0.36f;
        var rect = SKRect.Create((w - badge) / 2, (h - badge) / 2, badge, badge);
        c.DrawRoundRect(rect, badge * 0.12f, badge * 0.12f, pc.FillAA(new SKColor(0, 0, 0, (byte)(alpha * 0.78))));
        c.DrawRoundRect(rect, badge * 0.12f, badge * 0.12f, pc.StrokeAA(palette.Accent.WithAlpha(alpha), Math.Max(2, badge * 0.015f)));

        var font = pc.FontBold;
        font.Size = badge * 0.52f;
        DrawUtil.TextCentered(c, ctx.SinkIndex.ToString(), rect.MidX, rect.MidY - badge * 0.06f, font, pc.Text(SKColors.White.WithAlpha(alpha)));

        var sub = pc.FontRegular;
        sub.Size = Math.Clamp(badge * 0.085f, 10, 60);
        DrawUtil.TextCentered(c, $"{ctx.SinkLabel} · {w}×{h}", rect.MidX, rect.Bottom - badge * 0.14f, sub, pc.Text(SKColors.White.WithAlpha(alpha)));
    }
}
