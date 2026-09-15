using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Rendering;

/// <summary>
/// Overlay layers composited over any pattern: clock/date, countdown, logo watermark,
/// message ticker (canvas space) and the info chip / identify badge (viewport space).
/// </summary>
public static class OverlayRenderer
{
    /// <summary>One overlay's draw from a frame — the frame is the current one, or the outgoing snapshot's while it leaves.</summary>
    private delegate void DrawOverlay(SKCanvas c, in PatternFrame f);

    public static void RenderCanvasOverlays(SKCanvas c, in PatternFrame f)
    {
        var overlays = f.Snapshot.State.Overlays;
        var overlaysAt = FrameStages.Now();

        // Every overlay arrives and leaves through the sink's tracker (round 63): switched on it
        // fades (or slides) in over the show's transition time, switched off it leaves the same
        // way, drawn from the snapshot that had it. The Patterns badge goes down first: every
        // other overlay sits over it.
        Appear(c, in f, AppearKey.Badge, overlays.Badge.ShowsOn(f.Config.Kind), 0,
            static (SKCanvas cv, in PatternFrame fr) => DrawBadge(cv, in fr, fr.Snapshot.State.Overlays.Badge));
        Appear(c, in f, AppearKey.Logo, overlays.Logo.Enabled, 0,
            static (SKCanvas cv, in PatternFrame fr) => DrawLogo(cv, in fr, fr.Snapshot.State.Overlays.Logo));
        Appear(c, in f, AppearKey.Clock, overlays.Clock.Enabled, 0,
            static (SKCanvas cv, in PatternFrame fr) => DrawClock(cv, in fr, fr.Snapshot.State.Overlays.Clock));
        Appear(c, in f, AppearKey.Weather, overlays.Weather.Enabled, 0,
            static (SKCanvas cv, in PatternFrame fr) => DrawWeather(cv, in fr, fr.Snapshot.State.Overlays.Weather));
        Appear(c, in f, AppearKey.Countdown, f.Snapshot.State.Countdown.Enabled, 0,
            static (SKCanvas cv, in PatternFrame fr) => DrawCountdown(cv, in fr, fr.Snapshot.State.Countdown));
        var msg = overlays.Message;
        var message = msg.Enabled && (!string.IsNullOrWhiteSpace(msg.Text) || (msg.UseFeed && f.Snapshot.FeedText.Length > 0));
        Appear(c, in f, AppearKey.Message, message, 0,
            static (SKCanvas cv, in PatternFrame fr) => DrawMessage(cv, in fr, fr.Snapshot.State.Overlays.Message));

        f.Sink.Stages.Note(FrameStage.Overlays, overlaysAt);

        // The lower third on air sits over every other overlay, on every sink that shows the canvas.
        var lowerThirdAt = FrameStages.Now();
        LowerThirds.LowerThirdRenderer.Render(c, in f);
        f.Sink.Stages.Note(FrameStage.LowerThird, lowerThirdAt);
    }

    private static void DrawLogo(SKCanvas c, in PatternFrame f, LogoOverlay o)
    {
        var logo = ImageCache.Get(f.Snapshot.State.Brand.LogoPath);
        if (logo is null) return;

        var targetH = (float)(f.H * o.HeightPct / 100);
        var targetW = targetH * logo.Width / Math.Max(1, logo.Height);
        var margin = OverlayPlace.MarginFor(f.Canvas);
        var rect = DrawUtil.Anchored(f.Canvas, targetW, targetH, o.Anchor, margin, o.OffsetXPct, o.OffsetYPct);

        var paint = f.Paints.FillAA(SKColors.White.WithAlpha((byte)(o.Opacity * 255)));
        c.DrawImage(logo, rect, DrawUtil.Smooth, paint);
        Hit(in f, HitKind.Logo, rect);
    }

    // The app's own colours — the badge names the maker, so it never takes the show's brand kit.
    // The mark itself lives in PatternsMark, because the test card draws it as part of the card
    // and two hand-drawn copies of a logo drift apart.
    private static readonly SKColor BadgeInk = PatternsMark.Card;
    private static readonly SKColor BadgeCyan = PatternsMark.Cyan;
    private static readonly SKColor BadgeMagenta = PatternsMark.Magenta;
    private static readonly SKColor BadgeMist = PatternsMark.Mist;
    private static readonly string[] BadgeLetters = PatternsMark.Letters;

    /// <summary>
    /// The Patterns badge: the app's own mark, drawn by hand so it is on every sink at any size —
    /// the icon (the dark tile, the light grid and the cyan cross of the app's own icon), the
    /// PATTERNS wordmark letter-spaced in white and the line under it, on a near-black card with a
    /// cyan edge and a magenta rule. The app's own colours, never the show's brand kit: it names
    /// the maker, not the client. Sized by the canvas height, so it reads the same on a 4K wall
    /// and an HD monitor; a drag on the PREVIEW pane moves it like any overlay.
    /// </summary>
    private static void DrawBadge(SKCanvas c, in PatternFrame f, BadgeOverlay o)
    {
        var pc = f.Paints;
        var h = (float)(f.H * o.HeightPct / 100);
        if (h < 8) return;
        var alpha = (float)o.Opacity;

        var line = o.ShowLine ? o.Line.Trim() : "";
        var word = pc.FontFor(null, bold: true);
        word.Size = h * (line.Length > 0 ? 0.40f : 0.46f);
        var spacing = word.Size * 0.14f;
        // The name shaped once at this size and drawn as one blob every frame after: the letter-by-letter draw was the steady frame's one allocation.
        var (wordBlob, wordW) = pc.SpacedWord(BadgeLetters, word, spacing);
        var small = pc.FontFor(null, bold: false);
        small.Size = h * 0.19f;
        var lineW = line.Length > 0 ? small.MeasureText(line) : 0;

        var pad = h * 0.16f;
        var icon = h - pad * 2;
        var gap = h * 0.2f;
        var boxW = pad + icon + gap + Math.Max(wordW, lineW) + pad * 1.5f;
        var margin = OverlayPlace.MarginFor(f.Canvas);
        var rect = DrawUtil.Anchored(f.Canvas, boxW, h, o.Anchor, margin, o.OffsetXPct, o.OffsetYPct);
        Hit(in f, HitKind.Badge, rect);

        var radius = h * 0.2f;
        c.DrawRoundRect(rect, radius, radius, pc.FillAA(BadgeInk.WithAlpha(Alpha(0.88f, alpha))));
        c.DrawRoundRect(rect, radius, radius, pc.StrokeAA(BadgeCyan.WithAlpha(Alpha(0.8f, alpha)), Math.Max(1f, h * 0.02f)));

        var iconRect = SKRect.Create(rect.Left + pad, rect.Top + pad, icon, icon);
        DrawBadgeIcon(c, pc, iconRect, alpha);

        var x = iconRect.Right + gap;
        var white = pc.Text(SKColors.White.WithAlpha(Alpha(1f, alpha)));
        if (line.Length > 0)
        {
            var wordBaseline = rect.Top + pad + word.Size * 0.92f;
            c.DrawText(wordBlob, x, wordBaseline, white);
            // The magenta rule between the name and the line, the length of the name.
            var ruleY = wordBaseline + h * 0.07f;
            var ruleH = Math.Max(1f, h * 0.03f);
            c.DrawRoundRect(SKRect.Create(x, ruleY, wordW, ruleH), ruleH / 2, ruleH / 2, pc.FillAA(BadgeMagenta.WithAlpha(Alpha(0.95f, alpha))));
            c.DrawText(pc.TextBlob(line, small), x, rect.Bottom - pad - small.Size * 0.22f, pc.Text(BadgeMist.WithAlpha(Alpha(0.92f, alpha))));
        }
        else
        {
            var m = word.Metrics;
            c.DrawText(wordBlob, x, rect.MidY - (m.Ascent + m.Descent) / 2, white);
        }
    }

    private static void DrawBadgeIcon(SKCanvas c, PaintCache pc, SKRect r, float alpha)
        => PatternsMark.Icon(c, pc, r, Alpha(1f, alpha));

    private static byte Alpha(float k, float opacity) => (byte)Math.Clamp(k * opacity * 255f, 0, 255);



    /// <summary>
    /// Draws one overlay through the sink's arrival tracker (round 63). Shown and settled, it is
    /// drawn as it always was; arriving, it is drawn into a layer at its presence (and slid in from
    /// its edge when the show says so); leaving, the snapshot that had it draws it the same way at
    /// a falling presence. A frame that is not the sink's own picture — a fade source, a tile, a
    /// layer's inner draw, a thumbnail — bypasses the tracker and draws what its snapshot says.
    /// </summary>
    private static void Appear(SKCanvas c, in PatternFrame f, AppearKey key, bool shown, int identity, DrawOverlay draw)
    {
        if (Appearances.Bypasses(f.Ctx))
        {
            if (shown) draw(c, in f);
            return;
        }
        var cfg = f.Snapshot.State.Overlays.Appear;
        var animate = cfg.Kind != AppearKind.Cut && Appearances.Animates(in f);
        var p = f.Sink.Appearances.Read(key, shown, identity, f.Ctx.Time, Appearances.Seconds(cfg, f.Snapshot), animate, f.Snapshot);
        if (p.DrawsOutgoing)
        {
            var was = Appearances.OutgoingFrame(in f, p.Outgoing!);
            WithPresence(c, in was, key, p.Out, cfg.Kind, draw);
        }
        if (!p.DrawsCurrent) return;
        if (p.In >= 1f) draw(c, in f);
        else WithPresence(c, in f, key, p.In, cfg.Kind, draw);
    }

    /// <summary>The draw into a layer at a presence, slid from its anchor's edge when the kind is a slide; the frame's canvas or viewport is the space.</summary>
    private static void WithPresence(SKCanvas c, in PatternFrame f, AppearKey key, float presence, AppearKind kind, DrawOverlay draw)
    {
        var save = c.Save();
        try
        {
            if (kind == AppearKind.Slide)
            {
                var space = key == AppearKey.Pip ? f.Ctx.ViewportSize : f.Canvas;
                var offset = Appearances.SlideOffset(Appearances.AnchorOf(f.Snapshot.State, key), space, presence);
                c.Translate(offset.X, offset.Y);
            }
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(presence * 255f, 0, 255)) };
            c.SaveLayer(paint);
            draw(c, in f);
        }
        finally
        {
            c.RestoreToCount(save);
        }
    }

    /// <summary>Records a box the desk can drag — on the top-level draw only, never from a fade source, a tile or a layer.</summary>
    private static void Hit(in PatternFrame f, HitKind kind, SKRect rect) => Hit(f.Ctx, f.Sink, kind, rect, false);

    private static void Hit(in RenderContext ctx, SinkState sink, HitKind kind, SKRect rect, bool viewport)
    {
        if (ctx.IsFadeSource || ctx.InMultiview || ctx.InLayer) return;
        sink.Hits.Add(new HitRect(kind, rect, viewport));
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
        var margin = OverlayPlace.MarginFor(f.Canvas);
        var rect = DrawUtil.Anchored(f.Canvas, boxW, boxH, o.Anchor, margin, o.OffsetXPct, o.OffsetYPct);
        Hit(in f, HitKind.Clock, rect);

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

    /// <summary>
    /// The weather chip: the place over a glyph and the big figure, the sky in words under them,
    /// the hours as small columns for the rest of today and tomorrow, and the source's credit.
    /// With no forecast yet the chip still draws — the operator sees where it sits — and says why.
    /// </summary>
    private static void DrawWeather(SKCanvas c, in PatternFrame f, WeatherOverlay o)
    {
        var pc = f.Paints;
        var state = f.Snapshot.State;
        var settings = state.Weather;
        var report = f.Snapshot.Weather;
        var card = report is null ? null : WeatherWords.Card(report, o.View, f.Ctx.Now, settings.Units);
        var family = state.Brand.FontFamily;
        var s = (float)(f.H * o.SizePct / 100);
        var alpha = (byte)(o.Opacity * 255);
        var textColor = f.Color(o.TextColor, f.Palette.Text).WithAlpha(alpha);

        var head = o.ShowPlace && settings.Place.Length > 0 ? settings.Place : "";
        if (card is not null && o.View != WeatherView.Now) head = head.Length > 0 ? $"{head} · {card.Title}" : card.Title;
        var figure = card?.Figure ?? "—";
        var detail = !o.ShowDetail ? ""
            : card is not null ? card.Detail
            : !settings.HasLocation ? "Set a place on the Overlays page"
            : report is null ? "Forecast on its way…"
            : "No forecast for this view yet";
        var marks = o.ShowDetail && card is not null ? card.Marks : Array.Empty<WeatherMark>();
        var credit = o.ShowCredit && report is not null ? WeatherSources.Credit(report.Source) : "";

        var headSize = s * 0.3f;
        var detailSize = s * 0.3f;
        var markLabelSize = s * 0.22f;
        var markFigureSize = s * 0.26f;
        var markGlyph = s * 0.55f;
        var creditSize = s * 0.18f;
        var glyph = s * 1.2f;
        var gap = s * 0.25f;
        var padX = s * 0.4f;
        var padY = s * 0.28f;
        var rowGap = s * 0.12f;

        var bold = pc.FontFor(family, bold: true);
        var regular = pc.FontFor(family, bold: false);
        bold.Size = s;
        var figureW = bold.MeasureText(figure);
        regular.Size = headSize;
        var headW = head.Length > 0 ? regular.MeasureText(head) : 0;
        regular.Size = detailSize;
        var detailW = detail.Length > 0 ? regular.MeasureText(detail) : 0;
        regular.Size = creditSize;
        var creditW = credit.Length > 0 ? regular.MeasureText(credit) : 0;
        var column = Math.Max(markGlyph, s * 0.9f) + s * 0.2f;
        var marksW = marks.Count > 0 ? marks.Count * column : 0;

        var boxW = Math.Max(Math.Max(glyph + gap + figureW, headW), Math.Max(Math.Max(detailW, marksW), creditW)) + padX * 2;
        var boxH = padY * 2 + (head.Length > 0 ? headSize * 1.3f : 0) + glyph
                   + (detail.Length > 0 ? rowGap + detailSize * 1.3f : 0)
                   + (marks.Count > 0 ? rowGap + markLabelSize * 1.2f + markGlyph + markFigureSize * 1.2f : 0)
                   + (credit.Length > 0 ? rowGap + creditSize * 1.2f : 0);
        var margin = OverlayPlace.MarginFor(f.Canvas);
        var rect = DrawUtil.Anchored(f.Canvas, boxW, boxH, o.Anchor, margin, o.OffsetXPct, o.OffsetYPct);
        Hit(in f, HitKind.Weather, rect);

        if (o.Pill)
        {
            c.DrawRoundRect(rect, s * 0.22f, s * 0.22f,
                pc.FillAA(f.Palette.ChipBg.WithAlpha((byte)(f.Palette.ChipBg.Alpha * o.Opacity))));
        }

        var x = rect.Left + padX;
        var y = rect.Top + padY;
        if (head.Length > 0)
        {
            regular.Size = headSize;
            DrawUtil.TextLeft(c, head, x, y + headSize, regular, pc.Text(textColor.WithAlpha((byte)(alpha * 0.82))));
            y += headSize * 1.3f;
        }

        var glyphBox = new SKRect(x, y, x + glyph, y + glyph);
        WeatherGlyphs.Draw(c, pc, card?.Sky ?? WeatherSky.Unknown, card?.Night ?? false, glyphBox, (float)o.Opacity);
        bold.Size = s;
        DrawUtil.TextCentered(c, figure, x + glyph + gap + figureW / 2, y + glyph / 2, bold, pc.Text(textColor));
        y += glyph;

        if (detail.Length > 0)
        {
            y += rowGap;
            regular.Size = detailSize;
            DrawUtil.TextLeft(c, detail, x, y + detailSize, regular, pc.Text(textColor.WithAlpha((byte)(alpha * 0.88))));
            y += detailSize * 1.3f;
        }

        if (marks.Count > 0)
        {
            y += rowGap;
            var mx = x;
            foreach (var mark in marks)
            {
                var cx = mx + column / 2;
                regular.Size = markLabelSize;
                DrawUtil.TextCentered(c, mark.Label, cx, y + markLabelSize * 0.6f, regular, pc.Text(textColor.WithAlpha((byte)(alpha * 0.75))));
                var gy = y + markLabelSize * 1.2f;
                WeatherGlyphs.Draw(c, pc, mark.Sky, mark.Night, new SKRect(cx - markGlyph / 2, gy, cx + markGlyph / 2, gy + markGlyph), (float)o.Opacity);
                regular.Size = markFigureSize;
                DrawUtil.TextCentered(c, mark.Figure, cx, gy + markGlyph + markFigureSize * 0.6f, regular, pc.Text(textColor));
                mx += column;
            }
            y += markLabelSize * 1.2f + markGlyph + markFigureSize * 1.2f;
        }

        if (credit.Length > 0)
        {
            y += rowGap;
            regular.Size = creditSize;
            DrawUtil.TextLeft(c, credit, x, y + creditSize, regular, pc.Text(textColor.WithAlpha((byte)(alpha * 0.6))));
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
        var margin = OverlayPlace.MarginFor(f.Canvas);
        var rect = DrawUtil.Anchored(f.Canvas, boxW, boxH, cd.Anchor, margin, cd.OffsetXPct, cd.OffsetYPct);
        Hit(in f, HitKind.Countdown, rect);

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
            // A ticker runs the full width: only the vertical nudge applies.
            var band = DrawUtil.Anchored(f.Canvas, f.W, size * 1.6f, o.Anchor, OverlayPlace.MarginFor(f.Canvas), 0, o.OffsetYPct);
            Hit(in f, HitKind.Message, band);
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

        SKRect chip;
        switch (o.Background)
        {
            case MessageBackground.None:
                chip = DrawUtil.Chip(c, text, f.Canvas, o.Anchor, size, pc, messageColor, SKColors.Transparent, fontOverride: font, offsetXPct: o.OffsetXPct, offsetYPct: o.OffsetYPct);
                break;
            case MessageBackground.Chip:
                chip = DrawUtil.Chip(c, text, f.Canvas, o.Anchor, size, pc, messageColor, new SKColor(0, 0, 0, peak), fontOverride: font, offsetXPct: o.OffsetXPct, offsetYPct: o.OffsetYPct);
                break;
            case MessageBackground.Fade:
                DrawFadeBand(c, in f, DrawUtil.ChipBounds(textW, f.Canvas, o.Anchor, size, -1, o.OffsetXPct, o.OffsetYPct), o.Anchor, size, peak);
                chip = DrawUtil.Chip(c, text, f.Canvas, o.Anchor, size, pc, messageColor, SKColors.Transparent, fontOverride: font, offsetXPct: o.OffsetXPct, offsetYPct: o.OffsetYPct);
                break;
            default:
                chip = DrawUtil.Chip(c, text, f.Canvas, o.Anchor, size, pc, messageColor, f.Palette.ChipBg, fontOverride: font, offsetXPct: o.OffsetXPct, offsetYPct: o.OffsetYPct);
                break;
        }
        Hit(in f, HitKind.Message, chip);
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

        // The PiP inset arrives and leaves like the canvas overlays (round 63), in viewport space.
        var pip = snap.State.Overlays.Pip;
        if (Appearances.Bypasses(ctx))
        {
            DrawPip(c, snap, in ctx, sink, palette);
        }
        else
        {
            var appear = snap.State.Overlays.Appear;
            var animate = appear.Kind != AppearKind.Cut && !snap.TransitionsOff && sink.TransitionFrom is null && snap.CutAtVersion != snap.Version;
            var identity = pip.Enabled ? PipKey(pip).GetHashCode() : 0;
            var p = sink.Appearances.Read(AppearKey.Pip, pip.Enabled, identity, ctx.Time, Appearances.Seconds(appear, snap), animate, snap);
            if (p.DrawsOutgoing) DrawPipAt(c, p.Outgoing!, in ctx, sink, palette, p.Out, appear.Kind);
            if (p.DrawsCurrent)
            {
                if (p.In >= 1f) DrawPip(c, snap, in ctx, sink, palette);
                else DrawPipAt(c, snap, in ctx, sink, palette, p.In, appear.Kind);
            }
        }

        var info = snap.State.Overlays.Info;
        if (info.Enabled && cfg is not null && ctx.Sink != SinkKind.Thumbnail)
        {
            var pc = sink.Paints;
            var text = InfoChipText(ctx.SinkLabel, ctx.ViewportSize, cfg.Kind, info.ShowFps, ctx.MeasuredFps, ctx.PresentFps, ctx.WantedFps, ctx.DisplayHz);
            var size = Math.Clamp(ctx.ViewportSize.Height * 0.02f, 10, 22);
            DrawUtil.Chip(c, text, ctx.ViewportSize, info.Anchor, size, pc, palette.Text, palette.ChipBg);
        }
    }

    /// <summary>
    /// The tech info chip's line (round 63): the sink, its pixels and their shape, the kind of
    /// picture, and — when asked — the frames it draws a second against the rate it presents at
    /// and its display's refresh. "50.0 fps of 50 · 50 Hz display (60 asked)" is a 60 fps show on
    /// a 50 Hz display saying exactly what the room gets, where it used to read 60.
    /// </summary>
    public static string InfoChipText(string sinkLabel, SKSizeI px, PatternKind kind, bool showFps, double measuredFps, int presentFps, int wantedFps, int displayHz)
    {
        var text = $"{sinkLabel} · {px.Width}×{px.Height} · {AspectWords.Of(px.Width, px.Height)} · {kind}";
        if (!showFps) return text;
        text += $" · {measuredFps:0.0} fps";
        if (presentFps > 0) text += $" of {presentFps}";
        if (displayHz > 0)
        {
            text += $" · {displayHz} Hz display";
            if (wantedFps > displayHz) text += $" ({wantedFps} asked)";
        }
        else if (wantedFps > 0 && presentFps != wantedFps)
        {
            text += $" ({wantedFps} asked)";
        }
        return text;
    }

    /// <summary>The inset's mount key: the feed, the device or this machine's arcade.</summary>
    public static string PipKey(PipOverlay pip) => pip.Source switch
    {
        PipSource.NdiFeed => Patterns.Core.Media.InputKeys.Ndi(pip.NdiSourceName),
        PipSource.Arcade => Patterns.Core.Media.InputKeys.Arcade(),
        _ => Patterns.Core.Media.InputKeys.Capture(pip.CaptureDevice),
    };

    /// <summary>The PiP inset at a presence: into a layer, slid from its anchor's edge when the kind is a slide; an outgoing snapshot draws as a fade source.</summary>
    private static void DrawPipAt(SKCanvas c, ShowSnapshot snap, in RenderContext ctx, SinkState sink, Palette palette, float presence, AppearKind kind)
    {
        var save = c.Save();
        try
        {
            if (kind == AppearKind.Slide)
            {
                var offset = Appearances.SlideOffset(snap.State.Overlays.Pip.Anchor, ctx.ViewportSize, presence);
                c.Translate(offset.X, offset.Y);
            }
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(presence * 255f, 0, 255)) };
            c.SaveLayer(paint);
            var was = ctx with { IsFadeSource = true };
            DrawPip(c, snap, in was, sink, palette);
        }
        finally
        {
            c.RestoreToCount(save);
        }
    }

    /// <summary>Picture-in-picture live inset — drawn per viewport so every screen carries it.</summary>
    private static void DrawPip(SKCanvas c, ShowSnapshot snap, in RenderContext ctx, SinkState sink, Palette palette)
    {
        var pip = snap.State.Overlays.Pip;
        if (!pip.Enabled || ctx.Sink == SinkKind.Thumbnail) return;

        var source = Media.InputBus.For(PipKey(pip));
        var pc = sink.Paints;
        int vw = ctx.ViewportSize.Width, vh = ctx.ViewportSize.Height;

        // The inset takes the shape of what survives the crop, so a cropped feed is never squashed.
        var crop = FrameCrop.From(pip);
        var w = (float)(vw * pip.WidthPct / 100.0);
        var aspect = source?.FrameSize is { } fs && fs.Height > 0 ? crop.AspectOf(fs) : 16f / 9f;
        var h = w / aspect;
        var margin = OverlayPlace.PipMarginFor(ctx.ViewportSize);

        // Anchor9 grid: 0..8 → left/centre/right × top/middle/bottom.
        var col = (int)pip.Anchor % 3;
        var row = (int)pip.Anchor / 3;
        var x = col switch { 0 => margin, 1 => (vw - w) / 2, _ => vw - w - margin };
        var y = row switch { 0 => margin, 1 => (vh - h) / 2, _ => vh - h - margin };
        x += (float)(vw * pip.OffsetXPct / 100);
        y += (float)(vh * pip.OffsetYPct / 100);
        var rect = SKRect.Create(x, y, w, h);
        Hit(in ctx, sink, HitKind.Pip, rect, viewport: true);

        var alpha = (byte)Math.Clamp(pip.Opacity * 255, 0, 255);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), IsAntialias = true };

        var drawnPip = source is null ? DrawnFrame.Nothing : source.Draw(c, rect, paint, in crop);
        sink.Stages.NoteLive(in drawnPip);
        if (!drawnPip.Drew)
        {
            // No frames yet — a quiet slate so the operator sees where the inset will be.
            c.DrawRoundRect(rect, 6, 6, pc.FillAA(new SKColor(0x10, 0x12, 0x18, alpha)));
            var f = pc.FontRegular;
            f.Size = Math.Clamp(h * 0.12f, 10, 26);
            var label = source?.StatusText ?? pip.Source switch
            {
                PipSource.NdiFeed => "PiP: choose an NDI source",
                PipSource.Arcade => "PiP: the arcade — first frame…",
                _ => "PiP: choose a capture device",
            };
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
