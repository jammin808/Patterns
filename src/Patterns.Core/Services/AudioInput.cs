namespace Patterns.Core.Services;

/// <summary>
/// Which input a sound-reactive pattern listens to, decided from the names alone so the rule can
/// be read and tested without a sound card in the machine.
///
/// Two things make this more than a string compare. An operator who never wants to think about it
/// picks <see cref="DefaultDevice"/> and gets whatever Windows calls the input — the show file then
/// travels between a desk with a Scarlett and a laptop with a built-in microphone and works on
/// both. And Windows stamps a port number into a USB device's name — the capture card that was
/// "Microphone (2- USB Audio CODEC)" in rehearsal is "Microphone (3- USB Audio CODEC)" after
/// somebody moved it to the other socket — so the saved name is matched again with that number
/// taken out rather than reported missing an hour before doors.
/// </summary>
public static class AudioInput
{
    /// <summary>The choice that means "whatever this machine's own input is", saved in the show as this text.</summary>
    public const string DefaultDevice = "Default input";

    /// <summary>True when the show asked for the machine's own default input, or named nothing at all.</summary>
    public static bool WantsDefault(string? device)
        => string.IsNullOrWhiteSpace(device) || string.Equals(device.Trim(), DefaultDevice, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the named input sits in what the machine has now, or -1 for none — including when the
    /// show asked for the default, which is not a named endpoint. The name as saved wins; a name
    /// that differs only by the port number Windows stamps on it is the same box and matches too.
    /// </summary>
    public static int IndexOf(IReadOnlyList<string> devices, string? wanted)
    {
        if (devices.Count == 0 || WantsDefault(wanted)) return -1;
        var want = wanted!.Trim();
        for (var i = 0; i < devices.Count; i++)
        {
            if (string.Equals(devices[i]?.Trim(), want, StringComparison.OrdinalIgnoreCase)) return i;
        }
        var key = Key(want);
        if (key.Length == 0) return -1;
        for (var i = 0; i < devices.Count; i++)
        {
            if (Key(devices[i]) == key) return i;
        }
        return -1;
    }

    /// <summary>
    /// The name with the things that move taken out: lower case, one space between words, and the
    /// "2- " Windows writes inside the brackets for the socket the device is plugged into gone.
    /// </summary>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var text = name.Trim();
        var built = new System.Text.StringBuilder(text.Length);
        var space = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                if (space && built.Length > 0) built.Append(' ');
                built.Append('(');
                space = false;
                // A run of digits then "- " right after the bracket is the socket, not the device.
                var j = i + 1;
                while (j < text.Length && char.IsAsciiDigit(text[j])) j++;
                if (j > i + 1 && j + 1 < text.Length && text[j] == '-' && text[j + 1] == ' ') i = j + 1;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                space = built.Length > 0;
                continue;
            }
            if (space)
            {
                built.Append(' ');
                space = false;
            }
            built.Append(char.ToLowerInvariant(c));
        }
        return built.ToString();
    }
}
