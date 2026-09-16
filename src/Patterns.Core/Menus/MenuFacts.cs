using System.Text.RegularExpressions;
using Patterns.Core.Model;

namespace Patterns.Core.Menus;

/// <summary>A look as a menu sees it.</summary>
public sealed record MenuLook(string Id, string Name, int Hotkey, bool OnAir, bool InPreview)
{
    /// <summary>The cues that recall it, by number — "used by 02.010, 05.030" on the entry.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = Array.Empty<string>();
}

/// <summary>A kind of picture: the enum's word (what the wire takes) and the desk's label.</summary>
public sealed record MenuKind(string Word, string Label);

/// <summary>A lower-third design as a menu sees it.</summary>
public sealed record MenuDesign(string Id, string Name, bool IsDefault, bool OnAir, bool InPreview);

/// <summary>A person of the lower-thirds library.</summary>
public sealed record MenuPerson(string Id, string Name, string Summary);

/// <summary>A picture of the media library a layer can take.</summary>
public sealed record MenuMedia(string Id, string Name, bool IsVideo);

/// <summary>
/// The desk's facts every menu reads: what is open, what is on air, what the show has to offer.
/// Pure data — the App gathers it from its services, the wire's MENU query from the same, a test
/// from nothing at all.
/// </summary>
public sealed record DeskFacts
{
    public bool SandboxOpen { get; init; }
    public bool PrepMode { get; init; }
    public bool AssistantReady { get; init; }
    /// <summary>A caller node: the stack and the pages, no wall and no preview of its own.</summary>
    public bool IsNode { get; init; }
    public string LookOnAirId { get; init; } = "";
    public string LookOnAirName { get; init; } = "";
    /// <summary>The look on air has been changed since it was recalled.</summary>
    public bool LookEdited { get; init; }
    public IReadOnlyList<MenuLook> Looks { get; init; } = Array.Empty<MenuLook>();
    public IReadOnlyList<string> Presets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MenuKind> Kinds { get; init; } = DefaultKinds;
    public IReadOnlyList<MenuDesign> Designs { get; init; } = Array.Empty<MenuDesign>();
    public IReadOnlyList<MenuPerson> People { get; init; } = Array.Empty<MenuPerson>();
    public IReadOnlyList<MenuMedia> Media { get; init; } = Array.Empty<MenuMedia>();
    /// <summary>The show's transition in words — "dissolve · 800 ms" — for the cue menu's "show default" line.</summary>
    public string TransitionDefault { get; init; } = "";

    /// <summary>The preview picture's media source by its enum word ("Web") when the picture is a media picture; "" otherwise (round 63).</summary>
    public string PreviewSource { get; init; } = "";

    /// <summary>Round 67.6: what the next TAKE alone arrives by, in the operator's words ("WIPE LEFT 800 ms", "STING Whoosh"); "" for the show's own.</summary>
    public string NextTake { get; init; } = "";

    /// <summary>The same as the words after TAKE NEXT that set it, for the menu to mark the choice that is on.</summary>
    public string NextTakeWire { get; init; } = "";

    /// <summary>The video stings of the library, for the NEXT TRANSITION drawer.</summary>
    public IReadOnlyList<MenuSting> Stings { get; init; } = Array.Empty<MenuSting>();

    public bool HasLookOnAir => LookOnAirId.Length > 0;

    public bool PreviewIsMedia => PreviewSource.Length > 0;

    /// <summary>Every kind of picture, its enum word and a readable label (the desk hands its own labels in when it has them).</summary>
    public static readonly IReadOnlyList<MenuKind> DefaultKinds = Enum.GetNames<PatternKind>()
        .Select(n => new MenuKind(n, Regex.Replace(n, "(?<=[a-z0-9])(?=[A-Z])", " ").Replace("Color", "Colour")))
        .ToList();
}

/// <summary>A tile of the wall — a screen, a joined canvas, or the programme — as its menu sees it.</summary>
public sealed record ScreenFacts
{
    /// <summary>A screen id, a canvas key, or "" for the programme.</summary>
    public string TargetId { get; init; } = "";
    /// <summary>The wire's screen number (overview order), "" for a canvas or the programme.</summary>
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsCanvas { get; init; }
    public bool OnAir { get; init; }
    public bool Own { get; init; }
    public bool Locked { get; init; }
    public bool Armed { get; init; }
    public bool Enabled { get; init; }
    /// <summary>Showing something other than what the look on air asked for.</summary>
    public bool OffLook { get; init; }
    /// <summary>Holding a picture the preview put there that the audience has not seen.</summary>
    public bool Staged { get; init; }
    public bool IsMirror { get; init; }
    public bool Monitored { get; init; } = true;
    public bool Collapsed { get; init; }
    public string RoleBadge { get; init; } = "";
    /// <summary>
    /// Round 67.7: the group the screen is in — its role's word (main, confidence, info, repeater); "" for the
    /// programme, or a canvas whose screens are in different groups (<see cref="GroupsMixed"/>).
    /// </summary>
    public string Group { get; init; } = "";
    /// <summary>A canvas whose screens are in different groups.</summary>
    public bool GroupsMixed { get; init; }
    /// <summary>The words of the target a repeater repeats ("1 · Main wall"); "" when none is chosen.</summary>
    public string MirrorSource { get; init; } = "";
    /// <summary>The kind of picture the target shows now (the enum's word), "" when unknown.</summary>
    public string ShowingKind { get; init; } = "";

    /// <summary>Round 69: the output the screen's sound leaves by, as its label ("Info HDMI"); "" for none, or a canvas whose screens differ.</summary>
    public string SoundOut { get; init; } = "";
    /// <summary>The destination key behind <see cref="SoundOut"/> ("dev:Info HDMI"); "" for none.</summary>
    public string SoundOutKey { get; init; } = "";
    /// <summary>What the screen's sound is now, for the drawer's words ("the programme", "its own picture", "Main wall's picture (repeated)"); "" for a canvas.</summary>
    public string SoundSource { get; init; } = "";
    /// <summary>The outputs the sound may leave by — the matrix's destinations as (key, label): this machine's outputs, the show's NDI sends, the rows.</summary>
    public IReadOnlyList<(string Key, string Label)> SoundChoices { get; init; } = Array.Empty<(string, string)>();

    public bool IsProgram => TargetId.Length == 0;

    /// <summary>The wire's target for this tile: the screen number, else the id (a canvas key rides the action, not the wire).</summary>
    public string WireTarget => Number.Length > 0 ? Number : "";
}

/// <summary>A cue of the running order as its menu sees it.</summary>
public sealed record CueFacts
{
    public string Id { get; init; } = "";
    public string Number { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool IsStandby { get; init; }
    /// <summary>Why the cue cannot run ("" when it can).</summary>
    public string Problem { get; init; } = "";
    public string Summary { get; init; } = "";
    public string LookId { get; init; } = "";
    public string LookName { get; init; } = "";
    /// <summary>The look recall's transition words ("" = the show's default).</summary>
    public string Transition { get; init; } = "";
    public int? FollowSeconds { get; init; }
    public CueMark Mark { get; init; }
    public bool RequireConfirm { get; init; }
    public bool Ready { get; init; }
    /// <summary>The overlay steps the cue carries, by their ON kind.</summary>
    public IReadOnlySet<ShowActionKind> Overlays { get; init; } = new HashSet<ShowActionKind>();
    public bool CleanPicture { get; init; }
    public string LowerThirdDesignId { get; init; } = "";
    public string LowerThirdName { get; init; } = "";
    public double LowerThirdInAfter { get; init; }
    public double? LowerThirdOutAfter { get; init; }
    public bool IsClicker { get; init; }

    public bool HasLook => LookId.Length > 0;
    public bool HasLowerThird => LowerThirdDesignId.Length > 0;
    public bool IsBroken => Problem.Length > 0;
}

/// <summary>One of the two layers over the picture as its menu sees it.</summary>
public sealed record LayerFacts
{
    public int Index { get; init; } = 1;
    public bool Enabled { get; init; }
    public LayerSource Source { get; init; } = LayerSource.Image;
    /// <summary>What it shows, in words: the file's name, the feed, the screen.</summary>
    public string Words { get; init; } = "";
    public FitMode Fit { get; init; } = FitMode.Fill;
}

/// <summary>An overlay — the clock, the logo, the message, the PiP, the weather, the badge, the info chip, or the countdown — as its menu sees it.</summary>
public sealed record OverlayFacts
{
    /// <summary>clock · logo · message · pip · weather · badge · info · countdown.</summary>
    public string Kind { get; init; } = "clock";
    public string Label { get; init; } = "";
    public bool PreviewOn { get; init; }
    public bool AirOn { get; init; }
    /// <summary>The message's words, the countdown's label.</summary>
    public string Words { get; init; } = "";
    public Anchor9? Anchor { get; init; }
    public double CountdownMinutes { get; init; } = 5;
    public bool CountdownFollow { get; init; }

    public bool IsCountdown => Kind == "countdown";
}

/// <summary>A thing the RUN monitor can show: a screen (with its wire number) or a canvas.</summary>
public sealed record MonitorChoice(string TargetId, string Number, string Title)
{
    /// <summary>The wire's word for it: the number for a screen, the key for a canvas.</summary>
    public string Wire => Number.Length > 0 ? Number : TargetId;
}

/// <summary>The RUN surface's monitor as its menu reads it (round 62): what it shows now and what it could.</summary>
public sealed record MonitorFacts
{
    /// <summary>"" the main screen, "PGM" the programme, "OFF" hidden, else a target id.</summary>
    public string Current { get; init; } = "";
    /// <summary>The main screen's target id (the first Main-role screen, else the first screen); "" with no rig.</summary>
    public string MainTargetId { get; init; } = "";
    public string MainTitle { get; init; } = "";
    public IReadOnlyList<MonitorChoice> Choices { get; init; } = Array.Empty<MonitorChoice>();

    public bool IsOff => Current.Equals("OFF", StringComparison.OrdinalIgnoreCase);
    public bool IsProgram => Current.Equals("PGM", StringComparison.OrdinalIgnoreCase);
    public bool IsMain => Current.Length == 0;

    /// <summary>The target the monitor draws now: the main screen for "", null for the programme or hidden.</summary>
    public string? ShownTargetId => IsOff || IsProgram ? null : IsMain ? MainTargetId : Current;

    /// <summary>What it shows, in words.</summary>
    public string ShowingWords => IsOff ? "hidden" : IsProgram ? "the programme (PGM)"
        : IsMain ? (MainTitle.Length > 0 ? $"the main screen — {MainTitle}" : "the main screen")
        : Choices.FirstOrDefault(c => c.TargetId == Current)?.Title ?? Current;
}

/// <summary>A video sting of the library as the NEXT TRANSITION drawer lists it (round 67.6).</summary>
public sealed record MenuSting(string Id, string Name);
