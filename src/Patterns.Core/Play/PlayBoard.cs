using Patterns.Core.Arcade;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Play;

public enum PlayBoardMode
{
    Off,
    Join,
    Results,
    Leaderboard,
    Message,
    Draughts,
    Path,
}

/// <summary>The wall's picture for the room: the door, the live results, the board, a message, the draughts board, the path — in 1920×1080 units, fitted to any size.</summary>
public static class PlayBoard
{
    private const float W = ArcadeStage.Width;
    private const float H = ArcadeStage.Height;
    private static readonly SKColor Ink = SKColors.White;
    private static readonly SKColor Faint = new(0xFF, 0xFF, 0xFF, 0x90);
    private static readonly SKColor Neon = new(0xE0, 0xFF, 0x5F);
    private static readonly SKColor Sky = new(0x5F, 0xD0, 0xFF);
    private static readonly SKColor[] Bars = { new(0x5F, 0xD0, 0xFF), new(0xFF, 0x9E, 0x58), new(0x7C, 0xF5, 0xC8), new(0xFF, 0x6E, 0xC7), new(0xB1, 0x8C, 0xFF), new(0xFF, 0xC2, 0x4D), new(0xC0, 0xCB, 0xDB), new(0xFF, 0x5C, 0x7A) };

    public static void Render(SKCanvas c, int width, int height, PaintCache p, PlayRoom room, PlayBoardMode mode, string joinUrl, string message, DateTime utcNow)
    {
        c.Clear(new SKColor(0x08, 0x09, 0x0C));
        if (width < 1 || height < 1) return;
        var scale = MathF.Min(width / W, height / H);
        c.Save();
        c.Translate((width - W * scale) / 2f, (height - H * scale) / 2f);
        c.Scale(scale);
        c.ClipRect(SKRect.Create(0, 0, W, H));
        c.Clear(new SKColor(0x0B, 0x0C, 0x10));
        switch (mode)
        {
            case PlayBoardMode.Join: DrawJoin(c, p, room, joinUrl, utcNow); break;
            case PlayBoardMode.Results: DrawResults(c, p, room, utcNow); break;
            case PlayBoardMode.Leaderboard: DrawLeaderboard(c, p, room); break;
            case PlayBoardMode.Message: DrawMessage(c, p, room, message); break;
            case PlayBoardMode.Draughts: DrawDraughts(c, p, room); break;
            case PlayBoardMode.Path: DrawPath(c, p, room); break;
            default: ArcadeText.Draw(c, p, room.Code, W / 2, H / 2, 80, Faint); break;
        }
        c.Restore();
    }

    private static void DrawJoin(SKCanvas c, PaintCache p, PlayRoom room, string joinUrl, DateTime utcNow)
    {
        ArcadeText.Draw(c, p, room.Show.Length > 0 ? room.Show.ToUpperInvariant() : "JOIN IN", W / 2, 130, 56, Faint);
        ArcadeText.Draw(c, p, "ROOM CODE", W / 2 - 420, 300, 34, Faint);
        ArcadeText.Draw(c, p, room.Code, W / 2 - 420, 520, 230, Neon);
        if (joinUrl.Length == 0)
        {
            ArcadeText.Draw(c, p, "the audience port is off", W / 2 - 420, 640, 34, new SKColor(0xFF, 0x9E, 0x58));
            ArcadeText.Draw(c, p, "Remote page → AUDIENCE, or AUDIENCE ON on the wire", W / 2 - 420, 700, 28, Faint, bold: false);
        }
        else
        {
            ArcadeText.Draw(c, p, joinUrl, W / 2 - 420, 640, 34, Ink, bold: false);
            ArcadeText.Draw(c, p, "on the room's Wi-Fi — scan, or type the code", W / 2 - 420, 700, 28, Faint, bold: false);
        }
        var qr = joinUrl.Length == 0 ? null : QrCode.Encode(joinUrl);
        if (qr is not null)
        {
            var box = 560f;
            var x0 = W / 2 + 180;
            var y0 = 220f;
            c.DrawRoundRect(SKRect.Create(x0 - 24, y0 - 24, box + 48, box + 48), 16, 16, p.FillAA(SKColors.White));
            var cell = box / qr.Size;
            var dark = p.Fill(SKColors.Black);
            for (var y = 0; y < qr.Size; y++)
                for (var x = 0; x < qr.Size; x++)
                    if (qr[x, y]) c.DrawRect(x0 + x * cell, y0 + y * cell, cell + 0.5f, cell + 0.5f, dark);
        }
        var joined = room.PlayerCount;
        var here = room.ActiveCount(utcNow);
        ArcadeText.Draw(c, p, $"{joined} joined · {here} here now", W / 2, 960, 40, Sky);
    }

    private static void DrawResults(SKCanvas c, PaintCache p, PlayRoom room, DateTime utcNow)
    {
        var q = room.Current;
        if (q is null)
        {
            ArcadeText.Draw(c, p, "NO QUESTION YET", W / 2, H / 2, 64, Faint);
            return;
        }
        var lines = Wrap(p.FontBold, q.Text, 52, W - 160);
        var y = 110f;
        foreach (var line in lines.Take(3))
        {
            ArcadeText.Draw(c, p, line, W / 2, y, 52, Ink);
            y += 62;
        }
        var results = room.Results(q);
        var top = y + 30;
        switch (q.Kind)
        {
            case PlayQuestionKind.Choice:
            case PlayQuestionKind.Multi:
            case PlayQuestionKind.Quiz:
            {
                var n = q.Options.Count;
                var rowH = MathF.Min(110, (H - 160 - top) / Math.Max(1, n));
                var max = Math.Max(1, results.Counts.Count == 0 ? 1 : results.Counts.Max());
                for (var i = 0; i < n; i++)
                {
                    var ry = top + i * rowH;
                    var correct = q.Kind == PlayQuestionKind.Quiz && q.State == PlayQuestionState.Revealed && i == q.Correct;
                    var colour = correct ? Neon : Bars[i % Bars.Length];
                    var frac = results.Answers == 0 ? 0f : (float)results.Counts[i] / max;
                    c.DrawRoundRect(SKRect.Create(120, ry, (W - 240) * frac, rowH - 18), 10, 10, p.FillAA(colour.WithAlpha(0x70)));
                    c.DrawRoundRect(SKRect.Create(120, ry, W - 240, rowH - 18), 10, 10, p.StrokeAA(colour.WithAlpha(0x60), 2));
                    ArcadeText.Draw(c, p, $"{(char)('A' + i)}  {Fit(p.FontBold, q.Options[i], 36, W - 560)}", 148, ry + rowH / 2 + 4, 36, Ink, SKTextAlign.Left);
                    ArcadeText.Draw(c, p, $"{results.Counts[i]}  ·  {results.Percent(i)}%", W - 150, ry + rowH / 2 + 4, 34, Ink, SKTextAlign.Right);
                }
                break;
            }
            case PlayQuestionKind.Scale:
            {
                var counts = room.ScaleCounts(q);
                var n = counts.Length;
                var max = Math.Max(1, counts.Max());
                var barW = MathF.Min(140, (W - 240) / n);
                var x0 = W / 2 - barW * n / 2;
                var baseY = H - 200;
                for (var i = 0; i < n; i++)
                {
                    var h = (baseY - top - 40) * counts[i] / max;
                    c.DrawRoundRect(SKRect.Create(x0 + i * barW + 8, baseY - h, barW - 16, h), 8, 8, p.FillAA(Sky.WithAlpha(0x90)));
                    ArcadeText.Draw(c, p, (q.ScaleMin + i).ToString(), x0 + i * barW + barW / 2, baseY + 44, 32, Faint);
                    if (counts[i] > 0) ArcadeText.Draw(c, p, counts[i].ToString(), x0 + i * barW + barW / 2, baseY - h - 12, 28, Ink);
                }
                if (results.Answers > 0) ArcadeText.Draw(c, p, $"average {results.ScaleAverage:0.0}", W / 2, H - 90, 40, Neon);
                break;
            }
            case PlayQuestionKind.Words:
                DrawCloud(c, p, results.Words, top, H - 140);
                break;
        }
        var foot = $"{results.Answers} answer{(results.Answers == 1 ? "" : "s")}";
        if (q.Kind == PlayQuestionKind.Quiz && q.IsOpen) foot += $" · {Math.Ceiling(q.SecondsLeft(utcNow))} s";
        if (q.Kind == PlayQuestionKind.Quiz && q.State == PlayQuestionState.Revealed) foot += $" · {results.CorrectCount} right";
        if (q.IsOpen && q.Kind != PlayQuestionKind.Quiz) foot += " · answer on your phone";
        ArcadeText.Draw(c, p, foot, W / 2, H - 40, 30, Faint, bold: false);
    }

    private static void DrawCloud(SKCanvas c, PaintCache p, IReadOnlyList<KeyValuePair<string, int>> words, float top, float bottom)
    {
        if (words.Count == 0)
        {
            ArcadeText.Draw(c, p, "the cloud is empty — a word from your phone", W / 2, (top + bottom) / 2, 40, Faint, bold: false);
            return;
        }
        var max = Math.Max(1, words[0].Value);
        var rows = new List<List<(string Word, float Size, float Width)>>();
        var row = new List<(string, float, float)>();
        var rowWidth = 0f;
        foreach (var (word, count) in words)
        {
            var size = 30f + 70f * count / max;
            p.FontBold.Size = size;
            var width = p.FontBold.MeasureText(word) + 40;
            if (rowWidth + width > W - 200 && row.Count > 0)
            {
                rows.Add(row);
                row = new List<(string, float, float)>();
                rowWidth = 0;
            }
            row.Add((word, size, width));
            rowWidth += width;
        }
        if (row.Count > 0) rows.Add(row);
        var rowH = MathF.Min(120, (bottom - top) / Math.Max(1, rows.Count));
        var y = top + rowH / 2;
        var i = 0;
        foreach (var r in rows)
        {
            var total = r.Sum(w => w.Width);
            var x = W / 2 - total / 2;
            foreach (var (word, size, width) in r)
            {
                ArcadeText.Draw(c, p, word, x + width / 2, y + size / 3, size, Bars[i++ % Bars.Length]);
                x += width;
            }
            y += rowH;
            if (y > bottom) break;
        }
    }

    private static void DrawLeaderboard(SKCanvas c, PaintCache p, PlayRoom room)
    {
        ArcadeText.Draw(c, p, "LEADERBOARD", W / 2, 120, 64, Neon);
        var top = room.Leaderboard(10);
        if (top.Count == 0)
        {
            ArcadeText.Draw(c, p, "no points yet — a quiz scores the first right answers most", W / 2, H / 2, 36, Faint, bold: false);
            return;
        }
        var y = 220f;
        for (var i = 0; i < top.Count; i++)
        {
            var colour = i == 0 ? Neon : i < 3 ? Sky : Ink;
            ArcadeText.Draw(c, p, (i + 1).ToString(), 360, y, 44, Faint, SKTextAlign.Right);
            ArcadeText.Draw(c, p, Fit(p.FontBold, top[i].Nick + (top[i].Group.Length > 0 ? $"  ·  {top[i].Group}" : ""), 44, 900), 420, y, 44, colour, SKTextAlign.Left);
            ArcadeText.Draw(c, p, top[i].Score.ToString(), W - 360, y, 44, colour, SKTextAlign.Right);
            y += 78;
        }
    }

    private static void DrawMessage(SKCanvas c, PaintCache p, PlayRoom room, string message)
    {
        var text = message.Length > 0 ? message : room.Messages.LastOrDefault(m => m.To == "room")?.Text ?? "";
        if (text.Length == 0)
        {
            ArcadeText.Draw(c, p, room.Code, W / 2, H / 2, 120, Faint);
            return;
        }
        var lines = Wrap(p.FontBold, text, 72, W - 240);
        var y = H / 2 - (lines.Count - 1) * 45;
        foreach (var line in lines.Take(6))
        {
            ArcadeText.Draw(c, p, line, W / 2, y, 72, Ink);
            y += 90;
        }
        ArcadeText.Draw(c, p, room.Code, W / 2, H - 50, 30, Faint);
    }

    private static void DrawDraughts(SKCanvas c, PaintCache p, PlayRoom room)
    {
        var d = room.Draughts;
        var cell = 110f;
        var x0 = W / 2 - 4 * cell;
        var y0 = H / 2 - 4 * cell + 20;
        for (var i = 0; i < 64; i++)
        {
            var r = i / 8;
            var col = i % 8;
            var dark = Draughts.IsDark(i);
            c.DrawRect(x0 + col * cell, y0 + r * cell, cell, cell, p.Fill(dark ? new SKColor(0x2A, 0x31, 0x3E) : new SKColor(0xC0, 0xCB, 0xDB)));
            var piece = d[i];
            if (piece == 0) continue;
            var centre = new SKPoint(x0 + col * cell + cell / 2, y0 + r * cell + cell / 2);
            var colour = piece > 0 ? SKColors.White : new SKColor(0xFF, 0x5C, 0x7A);
            c.DrawCircle(centre, cell * 0.38f, p.FillAA(colour));
            c.DrawCircle(centre, cell * 0.38f, p.StrokeAA(SKColors.Black.WithAlpha(0x80), 3));
            if (piece == 2 || piece == -2) ArcadeText.Draw(c, p, "K", centre.X, centre.Y + 14, 40, piece > 0 ? SKColors.Black : SKColors.White);
        }
        if (d.ContinueFrom is { } from)
        {
            c.DrawRect(x0 + from % 8 * cell, y0 + from / 8 * cell, cell, cell, p.StrokeAA(Neon, 5));
        }
        ArcadeText.Draw(c, p, "DRAUGHTS", W / 2, 70, 48, Neon);
        ArcadeText.Draw(c, p, d.Words, W / 2, H - 40, 32, Faint, bold: false);
        ArcadeText.Draw(c, p, $"BLACK\n", 200, H / 2, 36, new SKColor(0xFF, 0x5C, 0x7A));
        ArcadeText.Draw(c, p, d.NickOf(DraughtSide.Black).Length > 0 ? d.NickOf(DraughtSide.Black) : "a phone", 200, H / 2 + 44, 28, Faint, bold: false);
        ArcadeText.Draw(c, p, "WHITE", W - 200, H / 2, 36, SKColors.White);
        ArcadeText.Draw(c, p, d.NickOf(DraughtSide.White).Length > 0 ? d.NickOf(DraughtSide.White) : "a phone", W - 200, H / 2 + 44, 28, Faint, bold: false);
    }

    private static void DrawPath(SKCanvas c, PaintCache p, PlayRoom room)
    {
        var path = room.Path;
        ArcadeText.Draw(c, p, path.Title.ToUpperInvariant(), W / 2, 90, 44, Neon);
        var scene = path.Current;
        if (scene is null)
        {
            ArcadeText.Draw(c, p, "THE END", W / 2, H / 2, 96, Ink);
            return;
        }
        var lines = Wrap(p.FontRegular, scene.Text, 40, W - 240);
        var y = 190f;
        foreach (var line in lines.Take(7))
        {
            ArcadeText.Draw(c, p, line, W / 2, y, 40, Ink, bold: false);
            y += 54;
        }
        if (scene.Options.Count == 0)
        {
            ArcadeText.Draw(c, p, "THE END", W / 2, H - 120, 72, Neon);
            return;
        }
        var counts = path.VoteCounts();
        var max = Math.Max(1, counts.Length == 0 ? 1 : counts.Max());
        var top = MathF.Max(y + 40, H - 120 - scene.Options.Count * 110);
        for (var i = 0; i < scene.Options.Count; i++)
        {
            var ry = top + i * 110;
            var frac = path.VotingOpen && path.VoteCount > 0 ? (float)counts[i] / max : 0f;
            c.DrawRoundRect(SKRect.Create(160, ry, (W - 320) * frac, 88), 10, 10, p.FillAA(Bars[i % Bars.Length].WithAlpha(0x70)));
            c.DrawRoundRect(SKRect.Create(160, ry, W - 320, 88), 10, 10, p.StrokeAA(Bars[i % Bars.Length].WithAlpha(0x70), 2));
            ArcadeText.Draw(c, p, $"{i + 1}  {Fit(p.FontBold, scene.Options[i].Text, 36, W - 600)}", 190, ry + 56, 36, Ink, SKTextAlign.Left);
            if (path.VotingOpen) ArcadeText.Draw(c, p, counts[i].ToString(), W - 190, ry + 56, 36, Ink, SKTextAlign.Right);
        }
        ArcadeText.Draw(c, p, path.VotingOpen ? "VOTE ON YOUR PHONE" : path.LastChoice.Length > 0 ? $"the room chose: {path.LastChoice}" : "the host opens the vote", W / 2, H - 36, 30, Faint, bold: false);
    }

    /// <summary>Lines of at most <paramref name="maxWidth"/> at a size, broken at spaces (a word longer than a line stands alone).</summary>
    public static List<string> Wrap(SKFont font, string text, float size, float maxWidth)
    {
        font.Size = size;
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (font.MeasureText(candidate) <= maxWidth || line.Length == 0) line = candidate;
                else
                {
                    lines.Add(line);
                    line = word;
                }
            }
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    /// <summary>The text cut with an ellipsis to fit a width at a size.</summary>
    public static string Fit(SKFont font, string text, float size, float maxWidth)
    {
        font.Size = size;
        if (font.MeasureText(text) <= maxWidth) return text;
        var s = text;
        while (s.Length > 1 && font.MeasureText(s + "…") > maxWidth) s = s[..^1];
        return s + "…";
    }
}
