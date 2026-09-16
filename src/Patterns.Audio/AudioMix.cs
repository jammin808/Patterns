using NAudio.CoreAudioApi;
using Patterns.Core.Services;

namespace Patterns.Audio;

/// <summary>The one format every mixer lane, tap and NDI block speaks: 48 kHz stereo float.</summary>
public static class AudioMix
{
    public const int Rate = 48000;

    public const int Channels = 2;
}

/// <summary>What an output device is called in the routing tables: its name when the operator chose it by name, else the computer output's key.</summary>
public static class AudioOutputs
{
    public const string DefaultDeviceKey = "(computer output)";

    /// <summary>The delay-table key of a resolved device: its name when it was chosen by name, else the computer-output key.</summary>
    public static string DelayKeyFor(MMDevice device, IReadOnlyList<string> chosenNames)
    {
        try
        {
            var name = device.FriendlyName;
            if (chosenNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
        catch
        {
            // A device that will not say its name is the default one.
        }
        return DefaultDeviceKey;
    }

    /// <summary>
    /// Stored names → devices. The <see cref="DefaultDeviceKey"/> entry adds the computer's
    /// default output (the venue-PA feed) and can combine with named HDMI screens; the same
    /// physical endpoint never plays twice. Empty selection (or nothing matching) = default.
    /// </summary>
    public static List<MMDevice> ResolveDevices(MMDeviceEnumerator enumerator, IReadOnlyList<string> names) => ResolveDevices(enumerator, names, out _);

    /// <summary>
    /// Round 71: the same resolution with the catalogue's word first. A name the catalogue knows is opened
    /// by its id — one call, no scan of every endpoint's property store on the desk's thread at a GO; a
    /// name it has not got (a device that arrived a moment ago) falls back to the scan, and the missing
    /// list reads as it always did.
    /// </summary>
    public static List<MMDevice> ResolveDevices(MMDeviceEnumerator enumerator, IReadOnlyList<string> names, IReadOnlyList<AudioEndpoint> known, out IReadOnlyList<string> missing)
    {
        var named = names.Where(n => n != DefaultDeviceKey).ToList();
        if (known.Count == 0 || named.Count == 0 || named.Any(n => !known.Any(k => string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase))))
        {
            return ResolveDevices(enumerator, names, out missing);
        }
        var result = new List<MMDevice>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gone = new List<string>();
        if (names.Contains(DefaultDeviceKey))
        {
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (taken.Add(device.ID)) result.Add(device);
            else device.Dispose();
        }
        foreach (var name in named)
        {
            var id = known.First(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)).Id;
            if (!taken.Add(id)) continue;
            try
            {
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active) result.Add(device);
                else
                {
                    device.Dispose();
                    gone.Add(name);
                }
            }
            catch (Exception ex)
            {
                // The catalogue's word is a moment old: the device left between the read and the press.
                Log.Warn($"'{name}' could not be opened by its id; the endpoints are read again.", ex);
                gone.Add(name);
            }
        }
        if (result.Count == 0) return ResolveDevices(enumerator, names, out missing);   // nothing opened: the scan and its fallback to the default, as ever
        missing = gone;
        return result;
    }

    /// <summary>As above, naming the chosen devices that were not there: the desk shows them, because a fallback must never be silent.</summary>
    public static List<MMDevice> ResolveDevices(MMDeviceEnumerator enumerator, IReadOnlyList<string> names, out IReadOnlyList<string> missing)
    {
        var result = new List<MMDevice>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDefault()
        {
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (taken.Add(device.ID)) result.Add(device);
            else device.Dispose();
        }

        if (names.Contains(DefaultDeviceKey)) AddDefault();
        if (names.Count > 0)
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (names.Any(n => string.Equals(n, device.FriendlyName, StringComparison.OrdinalIgnoreCase)) &&
                    taken.Add(device.ID))
                {
                    result.Add(device);
                }
                else
                {
                    device.Dispose();
                }
            }
        }
        if (result.Count == 0)
        {
            // Named an interface that is not plugged in. Falling back to the default endpoint is
            // right — a show must not go silent because a USB cable moved — but it must never be
            // silent about it: the programme is now coming out of the machine's own output, and
            // an operator who is not told will find out from the room.
            missing = names.Where(n => n != DefaultDeviceKey).ToList();
            AddDefault();
        }
        else
        {
            missing = names
                .Where(n => n != DefaultDeviceKey && !result.Any(d => string.Equals(d.FriendlyName, n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        return result;
    }
}
