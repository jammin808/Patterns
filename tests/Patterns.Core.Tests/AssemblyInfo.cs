using System.Runtime.CompilerServices;
using Patterns.Rendering;
using Xunit;

// The engine keeps a little process-wide state on purpose — the sync marks' flash, the input
// bus — and a few tests switch it on to prove it. Run in parallel, a pixel test rendering on
// the flash's two-second grid (t = 0, 2, 4 …) could catch another test's flash as a white frame
// and fail for nothing (a CI run did). The suite takes seconds; it runs one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

/// <summary>The suite draws: the render module's hooks (the media memory source, the assistant's picture reader) are registered once, as the desk registers them.</summary>
internal static class SuiteBoot
{
    [ModuleInitializer]
    internal static void Init() => RenderingModule.Register();
}
