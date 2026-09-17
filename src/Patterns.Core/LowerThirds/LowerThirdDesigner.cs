using Patterns.Core.Services;

namespace Patterns.Core.LowerThirds;

/// <summary>
/// The lower-thirds designer's editing logic, with no desk in it: designs made, copied, loaded
/// and removed with names that never collide and a default (★) that follows; elements added at
/// the design's size, moved, given their motion and their keys; the people list kept, imported
/// and exported. The desk's view model keeps the selection, the preview's clock, the tallies and
/// the words on the status line, and asks this for every edit — so what a keystroke on the page
/// does to the show is here, tested without a window.
/// </summary>
public sealed class LowerThirdDesigner
{
    /// <summary>The hold the preview's scrubber gives a design that waits to be hidden (no hold of its own).</summary>
    public const double WaitingHoldMs = 1500;

    private readonly LowerThirdsConfig _lowers;

    public LowerThirdDesigner(LowerThirdsConfig lowers) => _lowers = lowers;

    // ---- designs -------------------------------------------------------------------------------

    /// <summary>A new design from a preset (or an empty box), named so it never collides, added; the first design of a show is its default.</summary>
    public LowerThirdDesign New(string preset) => New(preset, "");

    /// <summary>Round 74: a new design from a preset with the name asked for (made unique), added — LT NEW &lt;name&gt; on the wire and a deck's build key.</summary>
    public LowerThirdDesign New(string preset, string name)
    {
        var design = preset == "Blank" ? LowerThirdPresets.Blank() : LowerThirdPresets.Create(preset);
        design.Name = UniqueName(name.Trim().Length > 0 ? name.Trim() : design.Name);
        _lowers.Designs.Add(design);
        AdoptDefault(design);
        return design;
    }

    /// <summary>A copy of a design (fresh ids, a name that never collides), added.</summary>
    public LowerThirdDesign Duplicate(LowerThirdDesign design)
    {
        var copy = design.Clone();
        copy.Name = UniqueName(design.Name);
        _lowers.Designs.Add(copy);
        return copy;
    }

    /// <summary>A design read from a file into the show as a new design (fresh ids, a name that never collides), added.</summary>
    public LowerThirdDesign Adopt(LowerThirdDesign loaded, string fallbackName)
    {
        var design = loaded.Clone();
        design.Name = UniqueName(loaded.Name.Length > 0 ? loaded.Name : fallbackName);
        _lowers.Designs.Add(design);
        AdoptDefault(design);
        return design;
    }

    /// <summary>The name, or the name with a number after it, whichever is not a design's yet.</summary>
    public string UniqueName(string name)
    {
        var candidate = name;
        var n = 2;
        while (_lowers.Designs.Any(d => string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{name} {n++}";
        }
        return candidate;
    }

    /// <summary>The first design of a show is its default (★) until another is chosen; a default that was deleted moves to the next one added.</summary>
    public void AdoptDefault(LowerThirdDesign design)
    {
        if (_lowers.DefaultDesignId.Length == 0 || _lowers.Find(_lowers.DefaultDesignId) is null) _lowers.DefaultDesignId = design.Id;
    }

    /// <summary>
    /// Takes a design out of the show: hidden first when it is the one showing here, the default
    /// moved to the first that remains. Returns the neighbour to select in its place; null when none is left.
    /// </summary>
    public LowerThirdDesign? Remove(LowerThirdDesign design, DateTime nowUtc)
    {
        if (_lowers.ActiveId == design.Id) _lowers.Hide(nowUtc);
        var designs = _lowers.Designs;
        var index = designs.IndexOf(design);
        designs.Remove(design);
        if (_lowers.DefaultDesignId == design.Id) _lowers.DefaultDesignId = designs.FirstOrDefault()?.Id ?? "";
        return designs.Count == 0 ? null : designs[Math.Clamp(index, 0, designs.Count - 1)];
    }

    // ---- elements ------------------------------------------------------------------------------

    /// <summary>A new element of a kind, sized to the design and given a plain fade both ways, added.</summary>
    public static LowerThirdElement AddElement(LowerThirdDesign d, LowerThirdElementKind kind)
    {
        var bar = kind == LowerThirdElementKind.Bar;
        var full = kind is LowerThirdElementKind.Bar or LowerThirdElementKind.Particles or LowerThirdElementKind.Fractal or LowerThirdElementKind.Media;
        var e = new LowerThirdElement
        {
            Kind = kind,
            Name = kind.ToString(),
            X = 0,
            Y = 0,
            W = full ? d.Width : Math.Min(kind == LowerThirdElementKind.Text ? 600 : 200, d.Width),
            H = full ? d.Height : Math.Min(kind == LowerThirdElementKind.Text ? 80 : 200, d.Height),
            Fill = bar ? LowerThirdFill.Solid : LowerThirdFill.None,
        };
        if (kind == LowerThirdElementKind.Text) e.Text = "Text";
        LowerThirdMotions.Apply(e, d, LowerThirdMotion.Fade, LowerThirdMotion.Fade);
        d.Elements.Add(e);
        return e;
    }

    /// <summary>Takes an element out of its design; returns the neighbour to select in its place, null when none is left.</summary>
    public static LowerThirdElement? RemoveElement(LowerThirdDesign d, LowerThirdElement e)
    {
        var index = d.Elements.IndexOf(e);
        d.Elements.Remove(e);
        return d.Elements.Count == 0 ? null : d.Elements[Math.Clamp(index, 0, d.Elements.Count - 1)];
    }

    /// <summary>An element one place up (-1) or down (+1) in the drawing order; false when it cannot go that way.</summary>
    public static bool MoveElement(LowerThirdDesign d, LowerThirdElement e, int delta)
    {
        var index = d.Elements.IndexOf(e);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= d.Elements.Count) return false;
        d.Elements.Move(index, target);
        return true;
    }

    /// <summary>A motion chip's ready-made keys for the way in or out; returns where on the timeline the preview should stand to see it.</summary>
    public static double ApplyMotion(LowerThirdDesign d, LowerThirdElement e, LowerThirdMotion motion, bool isIn)
    {
        LowerThirdMotions.Apply(e, motion, isIn, LowerThirdMotions.DefaultDistance(motion, d));
        return isIn ? d.InMs * 0.5 : d.InMs + HoldOf(d) + d.OutMs * 0.5;
    }

    /// <summary>One more key on the way in or out: a copy of the last a quarter later, or the first at the end.</summary>
    public static void AddKey(LowerThirdElement e, bool isIn)
    {
        var keys = isIn ? e.In : e.Out;
        var last = keys.Count == 0 ? null : keys[^1];
        var key = last?.Clone() ?? new LowerThirdKeyframe { U = isIn ? 0 : 1 };
        if (last is not null) key.U = Math.Min(1, last.U + 0.25);
        keys.Add(key);
    }

    public static bool RemoveKey(LowerThirdElement e, LowerThirdKeyframe key, bool isIn) => (isIn ? e.In : e.Out).Remove(key);

    /// <summary>"TextColor:primary" — a brand word into one of the element's colour fields; false when the spec names no such field.</summary>
    public static bool SetColorWord(LowerThirdElement e, string spec)
    {
        var parts = spec.Split(':', 2);
        if (parts.Length != 2) return false;
        var word = parts[1];
        switch (parts[0])
        {
            case nameof(LowerThirdElement.TextColor): e.TextColor = word; return true;
            case nameof(LowerThirdElement.FillColor): e.FillColor = word; return true;
            case nameof(LowerThirdElement.FillColor2): e.FillColor2 = word; return true;
            case nameof(LowerThirdElement.BorderColor): e.BorderColor = word; return true;
            case nameof(LowerThirdElement.GlowColor): e.GlowColor = word; return true;
            case nameof(LowerThirdElement.ChaserColor): e.ChaserColor = word; return true;
            case nameof(LowerThirdElement.ShadowColor): e.ShadowColor = word; return true;
            default: return false;
        }
    }

    /// <summary>
    /// A chosen file placed on an element: its path, and its name — an element still called
    /// "Image" tells the operator nothing; the file's name does. A name the operator has already
    /// typed is theirs and is left alone.
    /// </summary>
    public static void PlaceFile(LowerThirdElement e, string path)
    {
        e.Path = path;
        if (e.Name.Length == 0 || e.Name == e.Kind.ToString()) e.Name = Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>"The file is not there" — empty while the element's picture or clip opens, or for an element with no file.</summary>
    public static string FileTrouble(LowerThirdElement? e)
    {
        if (e is null || e.Kind is not (LowerThirdElementKind.Image or LowerThirdElementKind.Media)) return "";
        if (e.Path.Length == 0) return "";
        return ShowFiles.Exists(e.Path)
            ? ""
            : $"'{Path.GetFileName(e.Path)}' is not there — the design draws a blank where it should be. Choose it again to bring it into the show.";
    }

    /// <summary>The preview scrubber's range: the way in, a hold (its own, or <see cref="WaitingHoldMs"/> when it waits to be hidden), the way out.</summary>
    public static double PreviewLength(LowerThirdDesign? d) => d is null ? 1000 : d.InMs + HoldOf(d) + d.OutMs;

    private static double HoldOf(LowerThirdDesign d) => d.EffectiveHoldMs > 0 ? d.EffectiveHoldMs : WaitingHoldMs;

    // ---- the library: people and lines ----------------------------------------------------------

    /// <summary>A new library entry, named so it never collides, added.</summary>
    public LowerThirdEntry NewEntry(string name)
    {
        var entry = new LowerThirdEntry { Name = UniqueEntryName(name) };
        _lowers.Entries.Add(entry);
        return entry;
    }

    public string UniqueEntryName(string name)
    {
        var candidate = name;
        var n = 2;
        while (_lowers.Entries.Any(e => string.Equals(e.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{name} {n++}";
        }
        return candidate;
    }

    /// <summary>Takes an entry out of the library; false when it was not there. <paramref name="next"/> is the neighbour to select in its place.</summary>
    public bool RemoveEntry(LowerThirdEntry entry, out LowerThirdEntry? next)
    {
        next = null;
        var entries = _lowers.Entries;
        var index = entries.IndexOf(entry);
        if (index < 0) return false;
        entries.Remove(entry);
        next = entries.Count == 0 ? null : entries[Math.Clamp(index, 0, entries.Count - 1)];
        return true;
    }

    /// <summary>What an import did: the words for the status line, whether it read the file, and whether it replaced the list.</summary>
    public sealed record PeopleImport(string Words, bool Ok, bool Replaced);

    /// <summary>
    /// Reads a CSV or the first sheet of an .xlsx into the library — replacing it, or appended
    /// (a name already there is updated, never doubled).
    /// </summary>
    public PeopleImport ImportPeople(string path, bool append)
    {
        TableData table;
        try
        {
            table = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? XlsxTable.Read(File.ReadAllBytes(path))
                : CsvTable.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Error("People list read failed.", ex);
            return new PeopleImport($"Could not read {Path.GetFileName(path)}: {ex.Message}", Ok: false, Replaced: false);
        }
        var report = LowerThirdLibrary.Import(table);
        var entries = _lowers.Entries;
        var replaced = !append && report.Entries.Count > 0;
        if (replaced) entries.Clear();
        var (added, updated) = LowerThirdLibrary.Merge(entries, report.Entries);
        var words = $"{report.Summary}: {added} added, {updated} updated ({Path.GetFileName(path)})";
        if (report.Notes.Count > 0) words += " — " + report.Notes[0];
        return new PeopleImport(words, Ok: true, Replaced: replaced);
    }

    public string ExportPeopleCsv() => LowerThirdLibrary.Export(_lowers.Entries);

    // ---- the clock -----------------------------------------------------------------------------

    /// <summary>The design showing in a lower-thirds state and where it is on its way: arriving, holding, leaving, gone.</summary>
    public static (LowerThirdDesign? Design, LowerThirdPhase Phase) Phase(LowerThirdsConfig cfg, DateTime nowUtc)
    {
        var active = cfg.Active;
        if (active is null || LowerThirdClock.Instants(cfg) is not { } at) return (active, LowerThirdPhase.Gone);
        return (active, LowerThirdClock.Evaluate(active, at.ShownAt, at.HiddenAt, ShowClock.SecondsAt(nowUtc), cfg.RunHoldMs).Phase);
    }
}
