namespace Patterns.Core.Services;

/// <summary>
/// The colour language the deck and the desk share: one hue per kind of thing, one treatment per
/// state — green on air, armed or running; amber a preview, a hold, changed since, waiting; orange
/// late or a screen gone its own way; red a lower third on screen, the stream live, a failed cue,
/// black on its own; sky blue the overlays; steel blue the presenter's things; a hue per node
/// kind; dim for a bank key with nothing behind it. The Companion module carries the same table
/// (src/palette.js) and a test reads that file against this one, so a key on the Stream Deck and a
/// card on the desk never say one thing in two colours.
/// </summary>
public static class CompanionPalette
{
    /// <summary>The named colours, as red, green and blue.</summary>
    public static readonly IReadOnlyDictionary<string, (byte R, byte G, byte B)> Colours = new Dictionary<string, (byte, byte, byte)>(StringComparer.Ordinal)
    {
        ["white"] = (255, 255, 255),
        ["ink"] = (14, 15, 19),
        ["dark"] = (20, 22, 28),
        ["dim"] = (12, 13, 16),
        ["dimText"] = (80, 84, 94),
        ["green"] = (30, 158, 90),
        ["brightGreen"] = (46, 230, 138),
        ["amber"] = (255, 194, 77),
        ["orange"] = (255, 138, 0),
        ["red"] = (224, 52, 46),
        ["blackout"] = (200, 0, 0),
        ["outputsOn"] = (0, 100, 50),
        ["off"] = (90, 30, 30),
        ["steel"] = (0, 90, 130),
        ["blue"] = (0, 100, 160),
        ["music"] = (20, 120, 90),
        ["stingerBrown"] = (190, 120, 0),
        ["screenOn"] = (0, 120, 60),
        ["lock"] = (160, 110, 0),
        ["sky"] = (53, 170, 255),
        ["cyan"] = (53, 224, 208),
        ["desk"] = (0, 140, 200),
        ["caller"] = (130, 80, 220),
        ["arcade"] = (255, 70, 150),
        ["timer"] = (0, 170, 120),
        ["gone"] = (110, 40, 40),
    };

    /// <summary>What each kind of thing wears in each state — the colour's name per state.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> States = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
    {
        ["look"] = Row(("air", "green"), ("preview", "amber"), ("edited", "amber"), ("screensOff", "orange")),
        ["cue"] = Row(("armed", "green"), ("hold", "amber"), ("confirm", "amber"), ("failed", "red"), ("standby", "brightGreen"), ("late", "orange")),
        ["transport"] = Row(("blackout", "blackout"), ("outputsOn", "outputsOn"), ("off", "off"), ("frozen", "cyan"), ("review", "brightGreen"), ("editSafe", "steel"), ("black", "red")),
        ["screen"] = Row(("enabled", "screenOn"), ("locked", "lock"), ("armed", "green"), ("own", "steel"), ("black", "red"), ("offLook", "orange"), ("pattern", "green"), ("fault", "red")),
        ["stinger"] = Row(("playing", "stingerBrown"), ("hold", "amber")),
        ["vog"] = Row(("playing", "blue")),
        ["lowerThird"] = Row(("on", "red"), ("preview", "amber"), ("edited", "amber"), ("person", "red")),
        ["audio"] = Row(("playing", "blue")),
        ["music"] = Row(("playing", "music")),
        ["overlay"] = Row(("on", "sky")),
        ["countdown"] = Row(("running", "green"), ("over", "red")),
        ["presenter"] = Row(("on", "steel"), ("ended", "amber"), ("out", "red")),
        ["install"] = Row(("schedule", "green"), ("announcement", "amber"), ("advert", "steel")),
        // Round 65.10: signal truth and the rig on the keys — MATCH green, MISMATCH red; the known-good rig unchanged green, moved amber, commissioned green.
        ["signal"] = Row(("match", "green"), ("mismatch", "red")),
        ["rig"] = Row(("same", "green"), ("drift", "amber"), ("commissioned", "green")),
        // Round 66: the God's Eye's worst light on a key — red is wrong now, amber needs a look, green is all green.
        ["eye"] = Row(("red", "red"), ("amber", "amber"), ("green", "green")),
        ["stream"] = Row(("active", "red"), ("trouble", "amber")),
        ["device"] = Row(("open", "green"), ("fault", "red")),
        ["tone"] = Row(("on", "amber")),
        ["duck"] = Row(("on", "amber")),
        ["weather"] = Row(("on", "sky")),
        ["node"] = Row(("desk", "desk"), ("caller", "caller"), ("arcade", "arcade"), ("timer", "timer"), ("gone", "gone")),
        ["twin"] = Row(("inStep", "green"), ("silent", "red"), ("tookOver", "amber"), ("standby", "steel")),
        ["stage"] = Row(("running", "green"), ("amber", "amber"), ("red", "red"), ("pending", "amber"), ("paused", "sky")),
    };

    private static IReadOnlyDictionary<string, string> Row(params (string State, string Colour)[] pairs)
        => pairs.ToDictionary(p => p.State, p => p.Colour, StringComparer.Ordinal);

    /// <summary>"#1E9E5A" for a named colour; the dark ground for a name nobody has.</summary>
    public static string Hex(string name)
    {
        var (r, g, b) = Colours.TryGetValue(name, out var c) ? c : Colours["dark"];
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>The hex of a kind in a state — Hex("node", "caller"); the dark ground when the kind or the state is not in the table.</summary>
    public static string Hex(string kind, string state)
        => States.TryGetValue(kind, out var row) && row.TryGetValue(state, out var name) ? Hex(name) : Hex("dark");

    /// <summary>A node kind's hue on the desk's own pages — the same colour its key wears on the deck.</summary>
    public static string NodeHue(Model.NodeKind kind, bool fresh)
        => !fresh ? Hex("gone") : Hex("node", NodeKinds.Wire(kind));

    /// <summary>The stage timer's own colour word as a hue.</summary>
    public static string StageHue(string colour) => Hex("stage", colour == "green" ? "running" : colour);
}
