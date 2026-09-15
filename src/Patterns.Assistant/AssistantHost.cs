using Patterns.Core.Model;

namespace Patterns.Assistant;

/// <summary>
/// What the assistant asks of the process it runs in: the show, and the facts about it as the
/// process can gather them — the desk's outputs, stack and health; a node's nothing on air.
/// The kernel provides it for every role; the assistant reaches the show through the core's
/// vocabulary and never through the desk.
/// </summary>
public interface IAssistantHost
{
    ShowState State { get; }

    ShowFacts Facts();
}
