using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>
/// Patterns' own test card — a picture, not an overlay on someone else's.
///
/// Every other card here tests one thing well, and that is right when you already know what you
/// are looking for. A rig day does not start like that. It starts with a wall that has just lit
/// up, and four questions asked in about ten seconds: which screen is this, am I getting the
/// pixels I sent, where did the edges go, and what has the processor done to black, white and
/// grey. Answering those with four cards means four trips to the desk, and on a rig with eight
/// screens it means thirty-two. One card answers all four at once, from the ladder — which is
/// what a broadcast test card was always for, and why every station had one.
///
/// Three things make the difference between a card and a picture of a card:
///
/// <list type="bullet">
/// <item>Every measurement is a FIXED number in this file. The step values, the clipping patches,
/// the gamma solids and the one-to-one fields cannot be configured, so the card an engineer reads
/// in a venue is the card the engineer read last month, and a reading can be quoted.</item>
/// <item>Every structural line is drawn through <see cref="PatternFrame.Hairline"/> and lands on
/// whole pixels, so the one-to-one fields are exactly 50 % duty at any device scale and the card
/// survives being a 96-pixel tile on a monitor wall as well as a 4 K output.</item>
/// <item>Nothing here needs a graphics card. Three of this desk's six sinks draw on the processor,
/// so a card that reads differently on the stream from in the room would be worse than no card.</item>
/// </list>
///
/// The honest limits are stated where they are read, not buried: the one-to-one fields and the
/// gamma match only mean anything at native resolution with no sharpening in the chain, and the
/// card says so on itself.
/// </summary>
public sealed class TestCardPattern : IPatternRenderer
{
    // ---- the fixed science ------------------------------------------------------------------
    //
    // Nothing below is configurable, on purpose: a card whose numbers can be argued with is a card
    // nobody can quote a reading from.

    /// <summary>The staircase: eleven steps, 0–100 % of code value in tens. Banding and crush read straight off it.</summary>
    private static readonly byte[] Steps = { 0, 26, 51, 77, 102, 128, 153, 179, 204, 230, 255 };

    /// <summary>
    /// Just off black, on black. A processor with its black level lifted or crushed loses these
    /// from the bottom up — if 2 is invisible but 6 is there, the crush is about 4 codes deep.
    /// </summary>
    private static readonly byte[] NearBlack = { 2, 4, 6, 8 };

    /// <summary>Just off white, on white — the same test at the top, where clipping lives.</summary>
    private static readonly byte[] NearWhite = { 253, 251, 249, 247 };

    /// <summary>
    /// The gamma match. A field of one-pixel black and white lines is half lit in LINEAR light, so
    /// the solid patch beside it that matches is 255·0.5^(1/γ): the label under whichever one
    /// disappears at a distance IS the display's gamma. Only true unscaled and unsharpened, which
    /// is why the card says so next to it.
    /// </summary>
    private static readonly (byte Value, string Label)[] GammaSolids = { (177, "1.9"), (186, "2.2"), (195, "2.6") };

    /// <summary>75 % primaries and secondaries, full-range RGB (191). A dead or swapped channel is unmissable.</summary>
    private const byte C75 = 191;

    private static readonly string[] StepLabels = { "0", "26", "51", "77", "102", "128", "153", "179", "204", "230", "255" };

    /// <summary>A code value's own label, from a table: the card draws on every frame of every sink, and a card must not allocate to say "251".</summary>
    private static readonly string[] ByteLabels = BuildByteLabels();

    private static string[] BuildByteLabels()
    {
        var all = new string[256];
        for (var i = 0; i < 256; i++) all[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return all;
    }

    private static string Label(byte v) => ByteLabels[v];

    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.TestCard;
        c.Clear(Ink.Ground);

        switch (o.Variant)
        {
            case TestCardVariant.Pixel:
                RenderPixel(c, in f, o);
                break;
            case TestCardVariant.Levels:
                RenderLevels(c, in f, o);
                break;
            default:
                RenderRig(c, in f, o);
                break;
        }
    }

    // ---- the cards --------------------------------------------------------------------------

    /// <summary>
    /// Rig: the whole card. Nine proportional rows so it holds at 16:9, at a 32:9 joined canvas and
    /// in portrait — a fixed pixel layout would fall off the side of the first LED strip it met.
    /// </summary>
    private static void RenderRig(SKCanvas c, in PatternFrame f, TestCardOptions o)
    {
        int w = f.W, h = f.H;
        var hair = f.Hairline(1);
        var unit = Math.Min(w, h) / 9f;                       // one "row" — the card scales by the short side

        Frame(c, in f, hair);
        Rulers(c, in f, hair);

        // The measurement strip along the bottom: the staircase, then the clipping patches under it.
        var stripH = (int)MathF.Round(unit * 1.5f);
        var stripY = h - stripH - hair * 3;
        if (stripY > unit * 2)
        {
            Staircase(c, in f, hair * 3, stripY, w - hair * 6, (int)(stripH * 0.62f), labels: f.Resolves(unit * 0.22f, 6));
            var clipY = stripY + (int)(stripH * 0.62f);
            var clipH = stripH - (int)(stripH * 0.62f);
            ClipPatches(c, in f, hair * 3, clipY, w - hair * 6, clipH);
        }

        // The one-to-one fields: the four corners and the middle. Scaling anywhere in the chain
        // turns these from an even texture into a moiré you can see from the back of the hall.
        var pip = (int)MathF.Round(unit * 0.92f);
        if (f.Resolves(pip, 24))
        {
            var m = hair * 3 + (int)MathF.Round(unit * 0.42f);
            OneToOne(c, in f, m, m, pip, hair, 0);
            OneToOne(c, in f, w - m - pip, m, pip, hair, 1);
            OneToOne(c, in f, m, stripY - pip - hair * 2, pip, hair, 2);
            OneToOne(c, in f, w - m - pip, stripY - pip - hair * 2, pip, hair, 1);
        }

        // The middle: a circle that must be round, the mark inside it, the screen's own name under it.
        var midY = (stripY > unit * 2 ? stripY : h) / 2f;
        Centre(c, in f, o, w / 2f, midY, unit, hair);
        NotMeasuringNote(c, in f, unit, hair);

        // The gamma match sits beside the middle where the eye can defocus onto it.
        var gw = (int)MathF.Round(unit * 2.6f);
        var gh = (int)MathF.Round(unit * 0.78f);
        if (f.Resolves(gh, 14) && gw < w / 2 - unit)
        {
            GammaMatch(c, in f, w / 2 - gw / 2, (int)MathF.Round(midY + unit * 2.1f), gw, gh, hair);
        }

        Header(c, in f, o, unit, hair);
    }

    /// <summary>
    /// Pixel: the mapping half, as large as the frame allows. This is the one that goes up while
    /// somebody is on a ladder with a processor remote, so everything on it is twice the size and
    /// there is nothing on it to read at arm's length.
    /// </summary>
    private static void RenderPixel(SKCanvas c, in PatternFrame f, TestCardOptions o)
    {
        int w = f.W, h = f.H;
        var hair = f.Hairline(1);
        var unit = Math.Min(w, h) / 9f;

        Frame(c, in f, hair);
        Rulers(c, in f, hair);

        var pip = (int)MathF.Round(unit * 2.0f);
        if (f.Resolves(pip, 24))
        {
            var m = hair * 3 + (int)MathF.Round(unit * 0.5f);
            OneToOne(c, in f, m, m, pip, hair, 0);
            OneToOne(c, in f, w - m - pip, m, pip, hair, 1);
            OneToOne(c, in f, m, h - m - pip, pip, hair, 2);
            OneToOne(c, in f, w - m - pip, h - m - pip, pip, hair, 0);
        }

        Centre(c, in f, o, w / 2f, h / 2f, unit * 1.25f, hair);
        NotMeasuringNote(c, in f, unit, hair);
        Header(c, in f, o, unit, hair);
    }

    /// <summary>
    /// Levels: the measurement half, full frame. Nothing decorative competes with the patches,
    /// because this is the card somebody photographs and sends to the LED supplier.
    /// </summary>
    private static void RenderLevels(SKCanvas c, in PatternFrame f, TestCardOptions o)
    {
        int w = f.W, h = f.H;
        var hair = f.Hairline(1);
        var unit = Math.Min(w, h) / 9f;

        Frame(c, in f, hair);
        var pad = hair * 4;
        var top = (int)MathF.Round(unit * 1.15f);
        var body = h - top - pad;

        var stairH = (int)(body * 0.34f);
        Staircase(c, in f, pad, top, w - pad * 2, stairH, labels: f.Resolves(unit * 0.22f, 6));

        var clipH = (int)(body * 0.22f);
        ClipPatches(c, in f, pad, top + stairH, w - pad * 2, clipH);

        var colourH = (int)(body * 0.20f);
        Primaries(c, in f, pad, top + stairH + clipH + hair * 2, w - pad * 2, colourH);

        var gammaY = top + stairH + clipH + colourH + hair * 4;
        var gammaH = h - pad - gammaY;
        if (gammaH > unit * 0.4f)
        {
            GammaMatch(c, in f, pad, gammaY, w - pad * 2, gammaH, hair);
        }

        Header(c, in f, o, unit, hair);
    }

    // ---- the parts --------------------------------------------------------------------------

    /// <summary>
    /// The outermost pixel, and the third one in. Both edges visible on all four sides means the
    /// signal arrives whole; one missing is overscan, and the ruler beside it says how much.
    /// </summary>
    private static void Frame(SKCanvas c, in PatternFrame f, int hair)
    {
        var pc = f.Paints;
        DrawUtil.BorderInside(c, new SKRectI(0, 0, f.W, f.H), hair, pc.Fill(Ink.Edge));
        var inset = hair * 3;
        if (f.W > inset * 4 && f.H > inset * 4)
        {
            DrawUtil.BorderInside(c, new SKRectI(inset, inset, f.W - inset, f.H - inset), hair, pc.Fill(Ink.EdgeDim));
        }
    }

    /// <summary>
    /// Ticks every ten pixels along the first hundred from each corner, the fiftieth long and the
    /// hundredth longer. An engineer counting the ticks that are missing knows the overscan in
    /// pixels rather than in "a bit" — which is the difference between a setting and an argument.
    /// </summary>
    private static void Rulers(SKCanvas c, in PatternFrame f, int hair)
    {
        if (!f.Resolves(10, 3)) return;                        // on a tile the ticks would fill in
        var pc = f.Paints;
        var tick = pc.Fill(Ink.Edge);
        var lead = pc.Fill(Ink.Accent);
        int w = f.W, h = f.H;
        var reach = Math.Min(100, Math.Min(w, h) / 4);

        for (var p = 10; p <= reach; p += 10)
        {
            var len = p % 100 == 0 ? 22 : p % 50 == 0 ? 15 : 8;
            var paint = p % 50 == 0 ? lead : tick;
            DrawUtil.LineV(c, p, 0, len, hair, paint);                      // top, from the left
            DrawUtil.LineV(c, w - p - hair, 0, len, hair, paint);            // top, from the right
            DrawUtil.LineV(c, p, h - len, h, hair, paint);
            DrawUtil.LineV(c, w - p - hair, h - len, h, hair, paint);
            DrawUtil.LineH(c, p, 0, len, hair, paint);                      // left, from the top
            DrawUtil.LineH(c, h - p - hair, 0, len, hair, paint);
            DrawUtil.LineH(c, p, w - len, w, hair, paint);
            DrawUtil.LineH(c, h - p - hair, w - len, w, hair, paint);
        }
    }

    /// <summary>
    /// A one-to-one field: vertical one-pixel lines, horizontal ones, and a one-pixel checker,
    /// each exactly half lit. Sent through untouched they read as three even textures of the same
    /// brightness; through any scaler they crawl, moiré or go grey at different rates, and which
    /// one breaks says whether the scaling is horizontal, vertical or both.
    ///
    /// The lines are one DEVICE pixel, not one canvas pixel, because a canvas pixel on a sink that
    /// is drawing at half size falls between pixel centres and vanishes. That keeps the field
    /// visible everywhere — and means that on any sink whose device scale is not 1 it is no longer
    /// measuring the chain, only decorating it. <see cref="Measures"/> is how the card knows, and
    /// the card marks the fields rather than letting them look identical either way: a measurement
    /// that cannot say when it is not measuring is the one thing this desk does not ship.
    /// </summary>
    private static void OneToOne(SKCanvas c, in PatternFrame f, int x, int y, int size, int hair, int kind)
    {
        if (x < 0 || y < 0 || x + size > f.W || y + size > f.H) return;
        var pc = f.Paints;
        c.DrawRect(SKRect.Create(x, y, size, size), pc.Fill(SKColors.Black));
        var white = pc.Fill(SKColors.White);
        var step = hair * 2;

        switch (kind)
        {
            case 0:                                            // vertical lines: horizontal scaling
                for (var i = x; i + hair <= x + size; i += step) c.DrawRect(SKRect.Create(i, y, hair, size), white);
                break;
            case 1:                                            // horizontal lines: vertical scaling
                for (var i = y; i + hair <= y + size; i += step) c.DrawRect(SKRect.Create(x, i, size, hair), white);
                break;
            default:
                // A one-pixel checker: both directions at once. Both squares of each cell's
                // diagonal are lit, so the field is half lit like the other two — light one only
                // and it is a quarter lit, which reads as a dimmer patch beside them and sends an
                // engineer looking for a fault in the processor that is really in the card.
                for (var j = y; j + hair <= y + size; j += step)
                {
                    for (var i = x; i + hair <= x + size; i += step)
                    {
                        c.DrawRect(SKRect.Create(i, j, hair, hair), white);
                        if (i + step <= x + size && j + step <= y + size)
                        {
                            c.DrawRect(SKRect.Create(i + hair, j + hair, hair, hair), white);
                        }
                    }
                }
                break;
        }
        DrawUtil.BorderInside(c, new SKRectI(x, y, x + size, y + size), hair, pc.Fill(Measures(f) ? Ink.Accent : Ink.Warn));
    }

    /// <summary>
    /// True when this sink draws one canvas pixel as one device pixel — the only condition under
    /// which the one-to-one fields and the gamma match mean anything. A monitor-wall tile, a
    /// preview pane, and a pattern canvas that is not the output's own size are all drawing
    /// something else, and the card says so rather than looking the same in both cases.
    /// </summary>
    private static bool Measures(in PatternFrame f) => Math.Abs(f.DeviceScale - 1f) < 0.001f || f.DeviceScale == 0;

    /// <summary>
    /// The line the card owes anybody reading it off a monitor wall or a preview pane: what you are
    /// looking at is not this screen's own pixels, so the one-to-one fields and the gamma match are
    /// decoration here. Said on the card, where the reading is taken — not in a manual.
    /// </summary>
    private static void NotMeasuringNote(SKCanvas c, in PatternFrame f, float unit, int hair)
    {
        if (Measures(in f) || !f.Resolves(unit * 0.2f, 9)) return;
        var pc = f.Paints;
        var font = pc.FontRegular;
        var was = font.Size;
        font.Size = Math.Clamp(unit * 0.2f, 7, 40);
        DrawUtil.TextCentered(c, "SCALED — 1:1 AND GAMMA READ ONLY AT NATIVE SIZE", f.W / 2f, f.H - hair * 3 - font.Size * 0.6f,
            font, pc.Text(Ink.Warn));
        font.Size = was;
    }

    /// <summary>The eleven steps, each a whole number of pixels wide so no step is a pixel thinner than its neighbour.</summary>
    private static void Staircase(SKCanvas c, in PatternFrame f, int x, int y, int w, int h, bool labels)
    {
        if (w <= 0 || h <= 0) return;
        var pc = f.Paints;
        var n = Steps.Length;
        for (var i = 0; i < n; i++)
        {
            var x0 = x + (int)Math.Round(w * (double)i / n);
            var x1 = x + (int)Math.Round(w * (double)(i + 1) / n);
            var v = Steps[i];
            c.DrawRect(SKRect.Create(x0, y, x1 - x0, h), pc.Fill(new SKColor(v, v, v)));
            if (!labels || x1 - x0 < 18) continue;
            var font = pc.FontRegular;
            var was = font.Size;
            font.Size = Math.Clamp(h * 0.28f, 8, 26);
            // The label goes in whichever ink the step cannot swallow.
            var ink = v > 140 ? SKColors.Black : SKColors.White;
            DrawUtil.TextCentered(c, StepLabels[i], (x0 + x1) / 2f, y + h - font.Size * 0.5f, font, pc.Text(ink));
            font.Size = was;
        }
    }

    /// <summary>
    /// Black on black and white on white. Half the row is a black ground carrying four patches just
    /// above it; the other half a white ground carrying four just below. Every patch you cannot
    /// see is a code value the chain has thrown away, and the labels say which.
    /// </summary>
    private static void ClipPatches(SKCanvas c, in PatternFrame f, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        var pc = f.Paints;
        var half = w / 2;
        c.DrawRect(SKRect.Create(x, y, half, h), pc.Fill(SKColors.Black));
        c.DrawRect(SKRect.Create(x + half, y, w - half, h), pc.Fill(SKColors.White));

        var font = pc.FontRegular;
        var was = font.Size;
        font.Size = Math.Clamp(h * 0.36f, 7, 20);
        var show = f.Resolves(h * 0.36f, 6);

        Row(NearBlack, x, SKColors.White);
        Row(NearWhite, x + half, SKColors.Black);
        font.Size = was;

        void Row(byte[] values, int x0, SKColor ink)
        {
            var n = values.Length;
            var pad = Math.Max(1, h / 6);
            for (var i = 0; i < n; i++)
            {
                var a = x0 + (int)Math.Round(half * (double)i / n) + pad;
                var b = x0 + (int)Math.Round(half * (double)(i + 1) / n) - pad;
                if (b - a < 2) continue;
                var v = values[i];
                c.DrawRect(SKRect.Create(a, y + pad, b - a, h - pad * 2), pc.Fill(new SKColor(v, v, v)));
                if (show && b - a > 22)
                {
                    DrawUtil.TextCentered(c, Label(v), (a + b) / 2f, y + h - pad - font.Size * 0.2f, font, pc.Text(ink));
                }
            }
        }
    }

    /// <summary>75 % primaries and secondaries. Not a substitute for bars — a glance that says every channel is alive.</summary>
    private static void Primaries(SKCanvas c, in PatternFrame f, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        var pc = f.Paints;
        Span<SKColor> bars = stackalloc SKColor[8];
        bars[0] = SKColors.White;
        bars[1] = new SKColor(C75, C75, 0);
        bars[2] = new SKColor(0, C75, C75);
        bars[3] = new SKColor(0, C75, 0);
        bars[4] = new SKColor(C75, 0, C75);
        bars[5] = new SKColor(C75, 0, 0);
        bars[6] = new SKColor(0, 0, C75);
        bars[7] = SKColors.Black;
        for (var i = 0; i < 8; i++)
        {
            var x0 = x + (int)Math.Round(w * (double)i / 8);
            var x1 = x + (int)Math.Round(w * (double)(i + 1) / 8);
            c.DrawRect(SKRect.Create(x0, y, x1 - x0, h), pc.Fill(bars[i]));
        }
    }

    /// <summary>
    /// The gamma match: a one-pixel line field beside three solids. Step back until the lines blur,
    /// and the solid that vanishes into them is this display's gamma. Says its own condition, because
    /// a reading taken through a scaler or a sharpener is a reading of the scaler.
    /// </summary>
    private static void GammaMatch(SKCanvas c, in PatternFrame f, int x, int y, int w, int h, int hair)
    {
        if (w <= 0 || h <= 0) return;
        var pc = f.Paints;
        var cells = GammaSolids.Length + 1;
        var cw = w / cells;
        if (cw < 8) return;

        c.DrawRect(SKRect.Create(x, y, cw, h), pc.Fill(SKColors.Black));
        var white = pc.Fill(SKColors.White);
        for (var i = y; i + hair <= y + h; i += hair * 2) c.DrawRect(SKRect.Create(x, i, cw, hair), white);

        var font = pc.FontRegular;
        var was = font.Size;
        font.Size = Math.Clamp(h * 0.3f, 7, 22);
        var label = f.Resolves(h * 0.3f, 6);
        for (var i = 0; i < GammaSolids.Length; i++)
        {
            var (v, text) = GammaSolids[i];
            var x0 = x + cw * (i + 1);
            c.DrawRect(SKRect.Create(x0, y, cw, h), pc.Fill(new SKColor(v, v, v)));
            if (label && cw > 20)
            {
                DrawUtil.TextCentered(c, text, x0 + cw / 2f, y + h - font.Size * 0.25f, font, pc.Text(SKColors.Black));
            }
        }
        font.Size = was;
        DrawUtil.BorderInside(c, new SKRectI(x, y, x + cw * cells, y + h), hair, pc.Fill(Ink.EdgeDim));
    }

    /// <summary>
    /// The middle: a circle that is only round when the pixel aspect is, a centre cross on the exact
    /// middle pixel, and the mark inside them. The mark is drawn INTO the card because a card is a
    /// thing a client photographs, and a sticker over the top of one always looks like a sticker.
    /// </summary>
    private static void Centre(SKCanvas c, in PatternFrame f, TestCardOptions o, float cx, float cy, float unit, int hair)
    {
        var pc = f.Paints;
        var r = unit * 1.35f;
        if (r * 2 >= Math.Min(f.W, f.H)) r = Math.Min(f.W, f.H) * 0.34f;

        c.DrawCircle(cx, cy, r, pc.StrokeAA(Ink.Edge, hair));
        c.DrawCircle(cx, cy, r * 0.62f, pc.StrokeAA(Ink.EdgeDim, hair));
        DrawUtil.Cross(c, (int)MathF.Round(cx), (int)MathF.Round(cy), (int)(r * 0.28f), hair, pc.Fill(Ink.Accent));

        if (!o.ShowMark || !f.Resolves(unit * 0.5f, 10)) return;

        // The mark, drawn into the card out of the same primitives the badge uses — the icon over
        // the wordmark, centred on the circle. Part of the picture: a client photographs a card,
        // and a sticker over the top of one always looks like a sticker.
        var icon = r * 0.46f;
        PatternsMark.Icon(c, pc, SKRect.Create(cx - icon / 2, cy - icon * 0.86f, icon, icon));

        var font = pc.FontBold;
        var was = font.Size;
        font.Size = Math.Clamp(r * 0.26f, 7, 200);
        PatternsMark.WordmarkCentered(c, cx, cy + r * 0.32f, font, pc.Text(SKColors.White));
        var ruleW = PatternsMark.MeasureWordmark(font, PatternsMark.SpacingFor(font.Size));
        var ruleH = Math.Max(1f, r * 0.022f);
        c.DrawRoundRect(SKRect.Create(cx - ruleW / 2, cy + r * 0.42f, ruleW, ruleH), ruleH / 2, ruleH / 2,
            pc.FillAA(PatternsMark.Magenta));
        font.Size = was;
    }

    /// <summary>
    /// The header: which screen this is, in pixels, with the operator's own words beside it. The
    /// single most asked question on a rig day, answered from the back of the hall.
    /// </summary>
    private static void Header(SKCanvas c, in PatternFrame f, TestCardOptions o, float unit, int hair)
    {
        if (!o.ShowIdentity || !f.Resolves(unit * 0.3f, 9)) return;
        var pc = f.Paints;
        var font = pc.FontBold;
        var was = font.Size;
        font.Size = Math.Clamp(unit * 0.42f, 9, 160);
        var name = ScreenName(f);
        DrawUtil.TextCentered(c, name.ToUpperInvariant(), f.W / 2f, hair * 4 + font.Size, font, pc.Text(Ink.Edge));

        var sub = pc.FontRegular;
        var subWas = sub.Size;
        sub.Size = Math.Clamp(unit * 0.24f, 7, 80);
        var size = $"{f.W} × {f.H}";
        var words = o.Label.Length > 0 ? $"{size}  ·  {o.Label}" : size;
        DrawUtil.TextCentered(c, words, f.W / 2f, hair * 4 + font.Size + sub.Size * 1.5f, sub, pc.Text(Ink.Accent));
        font.Size = was;
        sub.Size = subWas;
    }

    /// <summary>
    /// What to call this screen on the card. Four of the desk's sinks label themselves with an
    /// internal nickname — "thumb", "pgm-remote", "mv-remote" — which is fine on a status line and
    /// wrong in 60-point type on a wall, because the whole purpose of the line is to be the answer
    /// to "which screen am I looking at". Anything that is not a name an operator would recognise
    /// reads as the programme, which is what those sinks are showing.
    /// </summary>
    private static string ScreenName(in PatternFrame f)
    {
        var label = f.Ctx.SinkLabel;
        if (label.Length == 0) return "PROGRAM";
        foreach (var nickname in Nicknames)
        {
            if (string.Equals(label, nickname, StringComparison.OrdinalIgnoreCase)) return "PROGRAM";
        }
        return label;
    }

    private static readonly string[] Nicknames = { "thumb", "pgm-remote", "mv-remote", "design" };

    /// <summary>
    /// The card's own ink. Deliberately NOT the show's brand kit: this is an engineering card, and
    /// a client's colours on it would change what the greys look like next to — which is the one
    /// thing on the card that must not change between one venue and the next.
    /// </summary>
    private static class Ink
    {
        public static readonly SKColor Ground = new(0x0B, 0x0C, 0x10);
        public static readonly SKColor Edge = new(0xFF, 0xFF, 0xFF);
        public static readonly SKColor EdgeDim = new(0xFF, 0xFF, 0xFF, 0x5A);
        public static readonly SKColor Accent = PatternsMark.Cyan;

        /// <summary>What the card wears where it cannot honestly claim a measurement.</summary>
        public static readonly SKColor Warn = new(0xFF, 0x8A, 0x00);
    }
}
