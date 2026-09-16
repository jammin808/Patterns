using Patterns.Audio;
using Xunit;

namespace Patterns.Audio.Tests;

/// <summary>
/// Round 71: the machine's endpoints as a catalogue — read once, published as one value, changed only
/// when the machine changed; a burst of Windows' notifications is one read on a worker; off Windows it
/// is empty and silent.
/// </summary>
public class AudioEndpointCatalogueTests
{
    [Fact]
    public void TheFirstReadPublishesAndTheSameMachineReadAgainDoesNot()
    {
        var render = new List<AudioEndpoint> { new("{0.0.0}.a", "Main HDMI"), new("{0.0.0}.b", "Scarlett 2i2") };
        var capture = new List<AudioEndpoint> { new("{0.0.1}.c", "Microphone (Scarlett 2i2)") };
        using var catalogue = new AudioEndpointCatalogue(() => (render, capture));
        var changes = new List<AudioEndpointSnapshot>();
        catalogue.Changed += changes.Add;
        Assert.Equal(0, catalogue.Version);
        Assert.Empty(catalogue.RenderNames);
        Assert.Null(catalogue.Current.RenderIdOf("Main HDMI"));

        Assert.True(catalogue.ReadNow("start"));
        Assert.Equal(1, catalogue.Version);
        Assert.Equal(1, catalogue.Reads);
        Assert.Equal(new[] { "Main HDMI", "Scarlett 2i2" }, catalogue.RenderNames);
        Assert.Equal(new[] { "Microphone (Scarlett 2i2)" }, catalogue.CaptureNames);
        Assert.Equal("{0.0.0}.b", catalogue.Current.RenderIdOf("scarlett 2i2"));                       // by name, whatever the case
        Assert.Equal("{0.0.1}.c", catalogue.Current.CaptureIdOf("Microphone (Scarlett 2i2)"));
        Assert.Null(catalogue.Current.RenderIdOf(AudioOutputs.DefaultDeviceKey));                      // the computer output's key is not a device's name
        Assert.Null(catalogue.Current.RenderIdOf(""));
        Assert.Single(changes);
        Assert.Equal("start", changes[0].Reason);

        var names = catalogue.RenderNames;
        Assert.False(catalogue.ReadNow("device property changed"));                                    // the same machine: no change to publish
        Assert.Equal(1, catalogue.Version);
        Assert.Equal(2, catalogue.Reads);
        Assert.Same(names, catalogue.RenderNames);                                                     // the pages' identity holds
        Assert.Single(changes);
        Assert.Equal("device property changed", catalogue.Current.Reason);                             // the read is on record all the same

        render.Add(new("{0.0.0}.d", "USB Speakers"));
        Assert.True(catalogue.ReadNow("device added"));
        Assert.Equal(2, catalogue.Version);
        Assert.NotSame(names, catalogue.RenderNames);
        Assert.Equal(3, catalogue.RenderNames.Count);
        Assert.Equal(2, changes.Count);
        Assert.Equal("device added", changes[1].Reason);
        Assert.Contains("3 outputs · 1 input", catalogue.Words, StringComparison.Ordinal);
        Assert.Contains("(device added)", catalogue.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void ABurstOfNudgesIsOneReadOnAWorker()
    {
        var reads = 0;
        using var read = new ManualResetEventSlim(false);
        using var catalogue = new AudioEndpointCatalogue(() =>
        {
            Interlocked.Increment(ref reads);
            read.Set();
            return (new[] { new AudioEndpoint("a", "A") }, Array.Empty<AudioEndpoint>());
        }, debounceMs: 20);
        for (var i = 0; i < 5; i++) catalogue.Nudge("device added");                                  // a dock plugged in: several notifications at once
        Assert.True(read.Wait(TimeSpan.FromSeconds(5)), "the read never came");
        Assert.True(SpinWait.SpinUntil(() => catalogue.Version == 1, TimeSpan.FromSeconds(5)), "the read never published");
        Assert.False(SpinWait.SpinUntil(() => Volatile.Read(ref reads) >= 2, 150), "a burst read more than once");
        Assert.Equal(5, catalogue.Nudges);
        Assert.Equal(1, catalogue.Reads);
        Assert.Equal(new[] { "A" }, catalogue.RenderNames);
    }

    [Fact]
    public async Task TwoAsksAtOnceReadTwiceNeverTogether()
    {
        var inside = 0;
        var overlapped = false;
        using var catalogue = new AudioEndpointCatalogue(() =>
        {
            if (Interlocked.Increment(ref inside) > 1) overlapped = true;
            Thread.Sleep(30);
            Interlocked.Decrement(ref inside);
            return (Array.Empty<AudioEndpoint>(), Array.Empty<AudioEndpoint>());
        }, debounceMs: 5);
        var first = Task.Run(() => catalogue.ReadNow("one"));
        var second = Task.Run(() => catalogue.ReadNow("two"));
        await Task.WhenAll(first, second);
        Assert.True(SpinWait.SpinUntil(() => catalogue.Reads >= 2, TimeSpan.FromSeconds(5)), "the second ask was lost");   // the loser asked again after the winner
        Assert.False(overlapped);
    }

    [Fact]
    public void OffWindowsTheCatalogueIsEmptyAndSilent()
    {
        if (OperatingSystem.IsWindows()) return;
        using var catalogue = new AudioEndpointCatalogue();
        catalogue.Start();
        Assert.True(SpinWait.SpinUntil(() => catalogue.Reads >= 1, TimeSpan.FromSeconds(5)), "the start's read never came");
        Assert.Equal(0, catalogue.Version);
        Assert.Empty(catalogue.RenderNames);
        Assert.Empty(catalogue.CaptureNames);
        Assert.False(catalogue.Listening);
        Assert.Contains("0 outputs · 0 inputs", catalogue.Words, StringComparison.Ordinal);
    }
}
