using System.Runtime.CompilerServices;
using Patterns.Rendering;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

/// <summary>The attachment tests shrink real pictures: the render module's codec is registered once, as the desk registers it.</summary>
internal static class SuiteBoot
{
    [ModuleInitializer]
    internal static void Init() => RenderingModule.Register();
}
