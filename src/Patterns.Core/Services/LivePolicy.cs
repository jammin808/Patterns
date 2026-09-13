using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a live desk refuses. Arm is the operator's policy: the stack is the show, GO is a press,
/// the automation waits. Outputs live is the machine's: the windows are up and the room is looking
/// at them. Both at once is a show running in front of an audience — and then work that is not the
/// show does not start: a calibration that puts test patterns on the screens or moves every
/// projector's picture, an update or a restart that takes the desk down. Content is never refused —
/// the arcade on a tile, the join wall, a look, a cue, a message — because the game and the audience
/// are show sources when the rundown puts them there; what is refused is the work an operator would
/// never mean to start mid-show, in words that say what lifts the refusal. Pure: a table, and a test.
/// </summary>
public static class LivePolicy
{
    /// <summary>The state the table applies in: the stack armed and the outputs live, at once.</summary>
    public static bool IsLive(bool armed, bool outputsLive) => armed && outputsLive;

    /// <summary>The verbs the table names, for the docs and the test.</summary>
    public static IReadOnlyList<ShowActionKind> Refused { get; } = new[]
    {
        ShowActionKind.CalibrateRun, ShowActionKind.CalibrateDemo, ShowActionKind.CalibrateApply, ShowActionKind.CalibrateUndo,
        ShowActionKind.UpdateApply, ShowActionKind.Restart,
    };

    /// <summary>Why the verb does not run while the stack is armed and the outputs are live, or null: it runs.</summary>
    public static string? Refusal(ShowActionKind kind, bool armed, bool outputsLive)
    {
        if (!IsLive(armed, outputsLive)) return null;
        var what = kind switch
        {
            ShowActionKind.CalibrateRun => "a calibration puts structured-light patterns on the room's screens",
            ShowActionKind.CalibrateDemo => "a calibration run takes the outputs for its patterns",
            ShowActionKind.CalibrateApply => "applying a calibration moves every projector's picture",
            ShowActionKind.CalibrateUndo => "undoing a calibration moves every projector's picture",
            ShowActionKind.UpdateApply => "an update takes the desk down and brings it back",
            ShowActionKind.Restart => "a restart takes the desk down and brings it back",
            _ => null,
        };
        return what is null ? null : $"Not while the stack is armed and the outputs are live: {what}. DISARM first (or OUTPUTS OFF), then again.";
    }
}
