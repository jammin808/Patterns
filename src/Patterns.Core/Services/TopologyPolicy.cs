namespace Patterns.Core.Services;

/// <summary>What an edit does to a source that is running.</summary>
public enum TopologyChange
{
    /// <summary>Applied to the running source in place: a volume, a mute, a route, a nickname, a crop.</summary>
    LiveSafe,
    /// <summary>The source has to be closed and opened again: the picture restarts. Staged while the source is on air.</summary>
    RequiresReopen,
    /// <summary>The desk has to be started again: refused while the stack is armed and the outputs are live.</summary>
    RequiresRestart,
}

/// <summary>The edits the table names.</summary>
public enum TopologyEdit
{
    CaptureFormat,
    CaptureLowLatency,
    ClipLoop,
    AudioRoutingMode,
    DirectOutputMode,
    ClipSound,
    InputNickname,
    InputCrop,
}

/// <summary>
/// One rule for every edit that changes how a source is opened rather than what it shows: a
/// capture device's mode or its low-latency profile, a clip's loop, the routing matrix's mode
/// (which decides a decoder's audio path when it opens), the direct-output kind (chosen when the
/// desk starts). The engine keeps the running source when such an edit lands while the source is
/// on air — the room keeps its picture — and stages the reopen for the moment the source leaves
/// the air or the outputs go off; the words say so wherever the edit is made. A live-safe edit
/// applies in place. Pure: a table and the words, so every picker, page and wire says the same.
/// </summary>
public static class TopologyPolicy
{
    public static TopologyChange Classify(TopologyEdit edit) => edit switch
    {
        TopologyEdit.CaptureFormat or TopologyEdit.CaptureLowLatency or TopologyEdit.ClipLoop or TopologyEdit.AudioRoutingMode => TopologyChange.RequiresReopen,
        TopologyEdit.DirectOutputMode => TopologyChange.RequiresRestart,
        _ => TopologyChange.LiveSafe,
    };

    /// <summary>"Low latency", "Format", "Loop", "Audio routing", "Direct output" — the edit's name for the words.</summary>
    public static string Name(TopologyEdit edit) => edit switch
    {
        TopologyEdit.CaptureFormat => "Format",
        TopologyEdit.CaptureLowLatency => "Low latency",
        TopologyEdit.ClipLoop => "Loop",
        TopologyEdit.AudioRoutingMode => "Audio routing",
        TopologyEdit.DirectOutputMode => "Direct output",
        TopologyEdit.ClipSound => "Sound",
        TopologyEdit.InputNickname => "Nickname",
        _ => "Crop",
    };

    /// <summary>"Low latency change pending — Cam Link 4K is on air; applies when it leaves the air or the outputs go off air."</summary>
    public static string PendingWords(TopologyEdit edit, string source)
        => $"{Name(edit)} change pending — {source} is on air; applies when it leaves the air or the outputs go off air.";

    /// <summary>Whether a source is on air: on the programme's picture while the outputs are live — the state a reopen is staged in.</summary>
    public static bool OnAir(bool outputsLive, bool onProgram) => outputsLive && onProgram;
}
