namespace Patterns.Core.RigDay;

/// <summary>One level of Blend Quest: a join (or the 2×2's middle, the boss), its members, whether it is clear and why not.</summary>
public sealed record QuestLevel(string Name, IReadOnlyList<string> Members, bool Cleared, bool Boss, string Words);
