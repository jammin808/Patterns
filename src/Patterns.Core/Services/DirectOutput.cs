using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>How the process presents its windows — decided once, when Patterns starts.</summary>
public enum DirectOutputMode
{
    /// <summary>The desktop compositor draws every window with everything else (the default).</summary>
    Composed,

    /// <summary>
    /// A low-latency flip-model swap chain per window: a borderless window that covers its
    /// display alone is flipped straight to that display by Windows, with no composition
    /// frame in between.
    /// </summary>
    LowLatencySwapChain,
}

/// <summary>Everything the decision reads. Unknown facts are their defaults; the rule is pure.</summary>
public sealed record DirectOutputFacts(
    bool IsWindows,
    int WindowsBuild,
    bool AnyOutputAsks,
    IReadOnlyList<GpuAdapterInfo> Adapters,
    string ActiveAdapterName,
    bool LastStartFailed);

/// <summary>What the process should do at this start, and why, in the operator's words.</summary>
public sealed record DirectOutputPlan(DirectOutputMode Mode, bool CardSuitable, string Reason);

/// <summary>
/// Direct output: the rule that decides whether the process asks Windows for the low-latency
/// swap chain, whether the card is good for it, and what each output's status line says.
/// The App layer feeds it the facts and applies the answer; nothing here touches a window.
/// </summary>
public static class DirectOutput
{
    /// <summary>Windows 10 — the flip path this rides on ("fullscreen optimisations") arrived with it.</summary>
    public const int MinWindowsBuild = 10240;

    /// <summary>The fuse file: written before a start that asks for the swap chain, removed once the desk is up.</summary>
    public const string FuseFileName = "patterns.direct.starting";

    /// <summary>
    /// The card the renderer runs on (the active one by name, else the best on the list) must be
    /// hardware: the software renderer composes and never flips. Any hardware card qualifies —
    /// the flip is the display controller's job, not the card's; a discrete card is the strong case.
    /// </summary>
    public static bool CardSuitable(IReadOnlyList<GpuAdapterInfo> adapters, string activeAdapterName, out string note)
    {
        GpuAdapterInfo? card = null;
        if (activeAdapterName.Length > 0)
        {
            foreach (var a in adapters)
            {
                if (string.Equals(a.Name, activeAdapterName, StringComparison.OrdinalIgnoreCase))
                {
                    card = a;
                    break;
                }
            }
        }
        if (card is null)
        {
            var best = GpuSelector.ChooseBest(adapters);
            if (best >= 0) card = adapters[best];
        }
        if (card is null)
        {
            note = "no graphics card enumerated";
            return false;
        }
        if (card.IsSoftware)
        {
            note = $"{card.Name} is the software renderer — it composes, it cannot flip";
            return false;
        }
        note = card.IsDiscreteVendor
            ? $"{card.Name}: a discrete card, the strong case"
            : $"{card.Name}: a hardware card";
        return true;
    }

    /// <summary>The decision for this start.</summary>
    public static DirectOutputPlan Decide(DirectOutputFacts f)
    {
        var suitable = CardSuitable(f.Adapters, f.ActiveAdapterName, out var cardNote);
        if (!f.IsWindows)
        {
            return new DirectOutputPlan(DirectOutputMode.Composed, suitable, "Direct output is Windows-only.");
        }
        if (!f.AnyOutputAsks)
        {
            return new DirectOutputPlan(DirectOutputMode.Composed, suitable, "No output asks for it.");
        }
        if (f.LastStartFailed)
        {
            return new DirectOutputPlan(DirectOutputMode.Composed, suitable,
                "Held off: the last start with direct output did not reach the desk. Untick and tick Direct output to try again.");
        }
        if (f.WindowsBuild > 0 && f.WindowsBuild < MinWindowsBuild)
        {
            return new DirectOutputPlan(DirectOutputMode.Composed, suitable, "Needs Windows 10 or later.");
        }
        if (!suitable)
        {
            return new DirectOutputPlan(DirectOutputMode.Composed, false, $"Not on this card — {cardNote}.");
        }
        return new DirectOutputPlan(DirectOutputMode.LowLatencySwapChain, true,
            $"Low-latency swap chain from this start ({cardNote}).");
    }

    /// <summary>
    /// One output's status line: what is in force now against what the outputs ask for. Honest
    /// about the one thing the app cannot read back — whether Windows took the flip — and what
    /// takes it away.
    /// </summary>
    public static string Status(bool asks, DirectOutputMode inForce, DirectOutputPlan wanted)
    {
        if (!asks)
        {
            return inForce == DirectOutputMode.LowLatencySwapChain
                ? "Composed by the desktop for this output (the low-latency swap chain is in force for the process; tick to prepare this window for the flip)."
                : "Composed by the desktop: Windows draws this output with everything else.";
        }
        if (inForce == DirectOutputMode.LowLatencySwapChain)
        {
            return "DIRECT — low-latency swap chain in force; this window covers its display exactly with the desktop's transitions off, and Windows flips it straight to the display while nothing sits on top of it. A window from another app over it puts it back through the compositor.";
        }
        if (wanted.Mode == DirectOutputMode.LowLatencySwapChain)
        {
            return "Restart Patterns to take effect: the swap chain is chosen at start.";
        }
        return wanted.Reason;
    }

    /// <summary>The summary line for the Machine page and the super-check.</summary>
    public static string Summary(int asking, DirectOutputMode inForce, DirectOutputPlan wanted)
    {
        if (asking == 0 && inForce == DirectOutputMode.Composed) return "Off — every output is composed by the desktop.";
        var who = asking == 1 ? "1 output asks" : $"{asking} outputs ask";
        if (inForce == DirectOutputMode.LowLatencySwapChain)
        {
            return asking == 0
                ? "Low-latency swap chain in force from this start; no output asks any more (the next start is composed)."
                : $"{who} · low-latency swap chain in force from this start.";
        }
        return wanted.Mode == DirectOutputMode.LowLatencySwapChain
            ? $"{who} · restart Patterns to take effect."
            : $"{who} · {wanted.Reason}";
    }
}
