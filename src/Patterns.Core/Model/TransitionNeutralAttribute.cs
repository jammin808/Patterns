namespace Patterns.Core.Model;

/// <summary>
/// A property that is part of the picture but not of its identity: a change to it never starts
/// a crossfade. The layers' boxes wear it, so dragging a layer on the PREVIEW pane moves it
/// without fading, while a new picture in the layer still fades in.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TransitionNeutralAttribute : Attribute
{
}
