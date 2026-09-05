using Patterns.Core.Model;

namespace Patterns.Core.LowerThirds;

/// <summary>
/// Ten designs to start from. Colours are brand words where a brand should show through
/// (primary, secondary, accent, text, background) and hex where the design needs its own.
/// </summary>
public static class LowerThirdPresets
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "Clean", "Broadcast", "Glass", "Neon", "Corporate", "Tag", "Headshot", "Sparks", "Fractal", "Stamp",
    };

    /// <summary>A preset by name; an unknown name is the blank design.</summary>
    public static LowerThirdDesign Create(string name) => name switch
    {
        "Clean" => Clean(),
        "Broadcast" => Broadcast(),
        "Glass" => Glass(),
        "Neon" => Neon(),
        "Corporate" => Corporate(),
        "Tag" => Tag(),
        "Headshot" => Headshot(),
        "Sparks" => Sparks(),
        "Fractal" => Fractal(),
        "Stamp" => Stamp(),
        _ => Blank(),
    };

    /// <summary>An empty design box with nothing in it.</summary>
    public static LowerThirdDesign Blank() => new() { Name = "New lower third", Preset = "" };

    // ---- the builders -------------------------------------------------------------------------

    private static LowerThirdDesign Design(string preset, double w, double h, int inMs, int outMs, Anchor9 anchor = Anchor9.BottomLeft)
        => new() { Name = preset, Preset = preset, Width = w, Height = h, InMs = inMs, OutMs = outMs, Anchor = anchor };

    private static LowerThirdElement Bar(LowerThirdDesign d, string name, double x, double y, double w, double h,
        LowerThirdMotion motionIn, LowerThirdMotion motionOut, int delayMs = 0)
    {
        var e = new LowerThirdElement { Name = name, Kind = LowerThirdElementKind.Bar, X = x, Y = y, W = w, H = h, Fill = LowerThirdFill.Solid, DelayMs = delayMs };
        LowerThirdMotions.Apply(e, d, motionIn, motionOut);
        d.Elements.Add(e);
        return e;
    }

    private static LowerThirdElement Text(LowerThirdDesign d, string name, LowerThirdTextKind kind, double x, double y, double w, double h,
        double size, string color, LowerThirdMotion motionIn, LowerThirdMotion motionOut, int delayMs = 0, bool bold = true)
    {
        var e = new LowerThirdElement
        {
            Name = name, Kind = LowerThirdElementKind.Text, TextKind = kind, X = x, Y = y, W = w, H = h,
            FontSizePx = size, TextColor = color, Bold = bold, DelayMs = delayMs,
        };
        LowerThirdMotions.Apply(e, d, motionIn, motionOut);
        d.Elements.Add(e);
        return e;
    }

    private static LowerThirdElement Element(LowerThirdDesign d, string name, LowerThirdElementKind kind, double x, double y, double w, double h,
        LowerThirdMotion motionIn, LowerThirdMotion motionOut, int delayMs = 0)
    {
        var e = new LowerThirdElement { Name = name, Kind = kind, X = x, Y = y, W = w, H = h, DelayMs = delayMs };
        LowerThirdMotions.Apply(e, d, motionIn, motionOut);
        d.Elements.Add(e);
        return e;
    }

    private static LowerThirdDesign Clean()
    {
        var d = Design("Clean", 960, 200, 600, 450);
        var panel = Bar(d, "Panel", 0, 0, 960, 200, LowerThirdMotion.SlideLeft, LowerThirdMotion.SlideLeft);
        panel.FillColor = "#F4F6F9";
        panel.CornerPx = 8;
        panel.ShadowPx = 24;
        panel.ShadowDy = 10;
        var stripe = Bar(d, "Stripe", 0, 0, 16, 200, LowerThirdMotion.SlideLeft, LowerThirdMotion.SlideLeft);
        stripe.FillColor = "primary";
        Text(d, "Name", LowerThirdTextKind.Name, 44, 28, 880, 86, 64, "#0B0C10", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 200);
        Text(d, "Role", LowerThirdTextKind.Role, 44, 118, 880, 52, 34, "#4A5568", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 320, bold: false);
        return d;
    }

    private static LowerThirdDesign Broadcast()
    {
        var d = Design("Broadcast", 1000, 230, 700, 500);
        var top = Bar(d, "Name bar", 0, 0, 1000, 140, LowerThirdMotion.Wipe, LowerThirdMotion.Wipe);
        top.Fill = LowerThirdFill.Gradient;
        top.FillColor = "primary";
        top.FillColor2 = "secondary";
        top.Gradient = LowerThirdGradient.Diagonal;
        var bottom = Bar(d, "Role bar", 0, 140, 1000, 90, LowerThirdMotion.Wipe, LowerThirdMotion.Wipe, 150);
        bottom.FillColor = "#E60B0C10";
        Text(d, "Name", LowerThirdTextKind.Name, 36, 26, 930, 90, 68, "#FFFFFF", LowerThirdMotion.SlideUp, LowerThirdMotion.Fade, 250);
        Text(d, "Role", LowerThirdTextKind.Role, 36, 154, 930, 62, 34, "#DCE3EE", LowerThirdMotion.SlideUp, LowerThirdMotion.Fade, 350, bold: false);
        return d;
    }

    private static LowerThirdDesign Glass()
    {
        var d = Design("Glass", 900, 210, 650, 450);
        var glass = Bar(d, "Glass", 0, 0, 900, 210, LowerThirdMotion.Pop, LowerThirdMotion.Pop);
        glass.FillColor = "#24FFFFFF";
        glass.BorderPx = 2;
        glass.BorderColor = "#80FFFFFF";
        glass.CornerPx = 26;
        glass.GlowPx = 26;
        glass.GlowColor = "primary";
        glass.ShadowPx = 28;
        glass.ShadowColor = "#80000000";
        glass.ShadowDy = 12;
        Text(d, "Name", LowerThirdTextKind.Name, 40, 34, 820, 86, 62, "#FFFFFF", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 200);
        Text(d, "Role", LowerThirdTextKind.Role, 40, 124, 820, 54, 32, "#E6EDF6", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 300, bold: false);
        return d;
    }

    private static LowerThirdDesign Neon()
    {
        var d = Design("Neon", 960, 200, 700, 500);
        var frame = Bar(d, "Frame", 0, 0, 960, 200, LowerThirdMotion.SlideUp, LowerThirdMotion.SlideDown);
        frame.Fill = LowerThirdFill.None;
        frame.BorderPx = 3;
        frame.BorderColor = "accent";
        frame.CornerPx = 18;
        frame.GlowPx = 32;
        frame.GlowColor = "accent";
        frame.Chaser = true;
        frame.ChaserColor = "#FFFFFF";
        frame.ChaserLengthPct = 14;
        frame.ChaserSpeed = 0.5;
        var name = Text(d, "Name", LowerThirdTextKind.Name, 40, 28, 880, 90, 70, "#FFFFFF", LowerThirdMotion.SlideUp, LowerThirdMotion.SlideDown, 150);
        name.GlowPx = 22;
        name.GlowColor = "accent";
        var role = Text(d, "Role", LowerThirdTextKind.Role, 40, 122, 880, 54, 32, "accent", LowerThirdMotion.SlideUp, LowerThirdMotion.SlideDown, 250);
        role.Uppercase = true;
        return d;
    }

    private static LowerThirdDesign Corporate()
    {
        var d = Design("Corporate", 1100, 220, 700, 500);
        var panel = Bar(d, "Panel", 0, 0, 1100, 220, LowerThirdMotion.SlideRight, LowerThirdMotion.SlideLeft);
        panel.Fill = LowerThirdFill.Gradient;
        panel.FillColor = "#0B0C10";
        panel.FillColor2 = "#1B2130";
        panel.Gradient = LowerThirdGradient.LeftRight;
        panel.CornerPx = 10;
        panel.ShadowPx = 20;
        panel.ShadowDy = 10;
        var logo = Element(d, "Logo", LowerThirdElementKind.Logo, 28, 30, 160, 160, LowerThirdMotion.Pop, LowerThirdMotion.Fade, 250);
        logo.Fit = FitMode.Fit;
        var divider = Bar(d, "Divider", 214, 40, 4, 140, LowerThirdMotion.Fade, LowerThirdMotion.Fade, 200);
        divider.FillColor = "primary";
        Text(d, "Name", LowerThirdTextKind.Name, 246, 36, 830, 80, 60, "#FFFFFF", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 300);
        Text(d, "Role", LowerThirdTextKind.Role, 246, 120, 830, 46, 32, "#C8D1DE", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 380, bold: false);
        var company = Text(d, "Company", LowerThirdTextKind.Company, 246, 166, 830, 40, 26, "accent", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 450);
        company.Uppercase = true;
        return d;
    }

    private static LowerThirdDesign Tag()
    {
        var d = Design("Tag", 640, 92, 550, 400);
        var pill = Bar(d, "Pill", 0, 0, 640, 92, LowerThirdMotion.Drop, LowerThirdMotion.Rise);
        pill.FillColor = "primary";
        pill.CornerPx = 46;
        pill.ShadowPx = 16;
        pill.ShadowDy = 8;
        var name = Text(d, "Name", LowerThirdTextKind.Name, 34, 10, 572, 72, 44, "#FFFFFF", LowerThirdMotion.Drop, LowerThirdMotion.Rise, 80);
        name.Align = LowerThirdAlign.Center;
        return d;
    }

    private static LowerThirdDesign Headshot()
    {
        var d = Design("Headshot", 1000, 240, 700, 500);
        var photo = Element(d, "Photo", LowerThirdElementKind.Image, 0, 0, 240, 240, LowerThirdMotion.Pop, LowerThirdMotion.Pop);
        photo.CornerPx = 120;
        photo.Fit = FitMode.Fill;
        photo.BorderPx = 6;
        photo.BorderColor = "#FFFFFF";
        photo.ShadowPx = 18;
        var panel = Bar(d, "Panel", 220, 40, 780, 160, LowerThirdMotion.SlideLeft, LowerThirdMotion.SlideLeft, 100);
        panel.FillColor = "#EB101522";
        panel.CornerPx = 14;
        Text(d, "Name", LowerThirdTextKind.Name, 268, 62, 712, 76, 56, "#FFFFFF", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 300);
        Text(d, "Role", LowerThirdTextKind.Role, 268, 140, 712, 48, 30, "#B9C3D2", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 380, bold: false);
        return d;
    }

    private static LowerThirdDesign Sparks()
    {
        var d = Design("Sparks", 960, 230, 800, 600);
        var sparks = Element(d, "Sparks", LowerThirdElementKind.Particles, 0, 0, 960, 230, LowerThirdMotion.Fade, LowerThirdMotion.Fade);
        sparks.CornerPx = 12;
        var p = sparks.Particles;
        p.Preset = "Sparks";
        p.Count = 320;
        p.Emitter = ParticleEmitter.BottomEdge;
        p.Shape = ParticleShape.Circle;
        p.SizeMin = 1.5;
        p.SizeMax = 4;
        p.SpeedMin = 60;
        p.SpeedMax = 220;
        p.DirectionDeg = 270;
        p.SpreadDeg = 40;
        p.GravityY = 120;
        p.WindX = 0;
        p.Wobble = 0.3;
        p.Glow = true;
        p.ColorsCsv = "#FFB020,#FFFFFF,#FF6A00";
        var shade = Bar(d, "Shade", 0, 120, 960, 110, LowerThirdMotion.Fade, LowerThirdMotion.Fade);
        shade.Fill = LowerThirdFill.Gradient;
        shade.FillColor = "#00000000";
        shade.FillColor2 = "#CC000000";
        shade.Gradient = LowerThirdGradient.TopBottom;
        var name = Text(d, "Name", LowerThirdTextKind.Name, 30, 40, 900, 92, 72, "#FFFFFF", LowerThirdMotion.SlideUp, LowerThirdMotion.Fade, 200);
        name.GlowPx = 20;
        name.GlowColor = "accent";
        name.ShadowPx = 8;
        name.ShadowDy = 4;
        Text(d, "Role", LowerThirdTextKind.Role, 30, 140, 900, 56, 34, "#FFE7B0", LowerThirdMotion.SlideUp, LowerThirdMotion.Fade, 320, bold: false);
        return d;
    }

    private static LowerThirdDesign Fractal()
    {
        var d = Design("Fractal", 960, 220, 800, 600);
        var wave = Element(d, "Wave", LowerThirdElementKind.Fractal, 0, 0, 960, 220, LowerThirdMotion.Wipe, LowerThirdMotion.Wipe);
        wave.CornerPx = 12;
        var o = wave.Fractal;
        o.Kind = FractalKind.DomainWarp;
        o.Preset = "Lower third";
        o.Iterations = 40;
        o.Speed = 0.35;
        o.Quality = FractalQuality.Fast;
        o.Zoom = 1.4;
        o.ColorsCsv = "#0B0C2A,#1E3A8A,#3EC1F3,#FFFFFF";
        var shade = Bar(d, "Shade", 0, 0, 960, 220, LowerThirdMotion.Fade, LowerThirdMotion.Fade);
        shade.Fill = LowerThirdFill.Gradient;
        shade.FillColor = "#00000000";
        shade.FillColor2 = "#B0000000";
        shade.Gradient = LowerThirdGradient.TopBottom;
        shade.CornerPx = 12;
        var edge = Bar(d, "Edge", 0, 0, 10, 220, LowerThirdMotion.Wipe, LowerThirdMotion.Wipe);
        edge.FillColor = "#FFFFFF";
        var name = Text(d, "Name", LowerThirdTextKind.Name, 40, 40, 880, 84, 64, "#FFFFFF", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 250);
        name.ShadowPx = 10;
        name.ShadowDy = 4;
        Text(d, "Role", LowerThirdTextKind.Role, 40, 130, 880, 54, 32, "#DCE3EE", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 350, bold: false);
        return d;
    }

    private static LowerThirdDesign Stamp()
    {
        var d = Design("Stamp", 640, 170, 550, 400, Anchor9.TopRight);
        d.MarginX = 50;
        d.MarginY = 50;
        var card = Bar(d, "Card", 0, 0, 640, 170, LowerThirdMotion.Drop, LowerThirdMotion.Rise);
        card.FillColor = "#F7F9FC";
        card.CornerPx = 8;
        card.ShadowPx = 20;
        card.ShadowDy = 8;
        var stripe = Bar(d, "Stripe", 0, 0, 12, 170, LowerThirdMotion.Drop, LowerThirdMotion.Rise);
        stripe.FillColor = "accent";
        Text(d, "Date", LowerThirdTextKind.Date, 36, 24, 570, 62, 40, "#0B0C10", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 200);
        Text(d, "Time", LowerThirdTextKind.Time, 36, 88, 570, 56, 34, "#4A5568", LowerThirdMotion.Fade, LowerThirdMotion.Fade, 260, bold: false);
        return d;
    }
}
