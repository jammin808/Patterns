using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Where the desk keeps the answer to "is this look still what is on the screens".
///
/// The Looks page has been able to say PROGRAM · EDITED since round 17, and it worked out the
/// answer in the view model, for the view model. Nothing else could see it: a Stream Deck key
/// showed a look solid green whether the operator had touched the picture since recalling it or
/// not, which is the one thing a key at front of house most needs to say. The reading moves down
/// here so the page, the wire, OSC and Companion all read the same one — a fact two surfaces can
/// disagree about is worse than a fact neither has.
///
/// Everything is cached on the snapshot version, because the readings are taken on the one-second
/// poll, after every action and on every STATE push, and a fingerprint is a whole serialisation of
/// the show. Nothing here publishes a snapshot or touches what a sink draws.
/// </summary>
public sealed class LookTally
{
    private readonly AppServices _s;

    // A look's payload is immutable while it is saved, so its fingerprint is keyed by the payload
    // itself and survives the list being reordered, renamed or reloaded.
    private readonly Dictionary<string, string> _byJson = new(StringComparer.Ordinal);

    private long _airVersion = -1;
    private bool _airSandboxed;
    private string _air = "";

    private long _previewVersion = -1;
    private string _preview = "";

    private long _offVersion = -1;
    private bool _offSandboxed;
    private string _offLookId = "";
    private List<string> _off = new();

    public LookTally(AppServices services) => _s = services;

    /// <summary>A saved look's fingerprint, cached by its payload.</summary>
    public string FingerprintOf(LookConfig look)
    {
        if (_byJson.TryGetValue(look.Json, out var fp)) return fp;
        if (_byJson.Count > 256) _byJson.Clear();
        fp = LookService.Fingerprint(look.Json);
        _byJson[look.Json] = fp;
        return fp;
    }

    /// <summary>The picture the audience is watching, as a fingerprint.</summary>
    public string AirFingerprint()
    {
        var version = _s.Bus.Current.Version;
        var sandboxed = _s.Sandbox.Active;
        if (version != _airVersion || sandboxed != _airSandboxed)
        {
            _air = LookService.Fingerprint(_s.AirState);
            _airVersion = version;
            _airSandboxed = sandboxed;
        }
        return _air;
    }

    /// <summary>The picture being built, as a fingerprint — kept by the SANDBOX's version, which is
    /// the one that moves when the operator edits the preview; the air's does not.</summary>
    public string PreviewFingerprint()
    {
        var version = _s.Bus.Sandbox?.Version ?? -1;
        if (version != _previewVersion)
        {
            _preview = LookService.Fingerprint(_s.State);
            _previewVersion = version;
        }
        return _preview;
    }

    /// <summary>
    /// The look the audience is watching: the one last put on air, or — when nothing was recorded,
    /// which is every fresh start and every show loaded from a file — whichever saved look this
    /// picture actually is.
    ///
    /// That second half is not a nicety. The Looks page has always had it, and when the reading
    /// moved down here without it the page lit a look while the wire lit none: one desk, two
    /// answers, which is the exact fault this class exists to end.
    /// </summary>
    public LookConfig? OnAir()
    {
        var looks = _s.State.LooksAndCues.Looks;
        var id = _s.AirLookId;
        if (id.Length > 0)
        {
            foreach (var look in looks)
            {
                if (look.Id == id) return look;
            }
            return null;
        }
        var air = AirFingerprint();
        foreach (var look in looks)
        {
            if (FingerprintOf(look) == air) return look;
        }
        return null;
    }

    /// <summary>
    /// True when the look on air has been changed since it was recalled. False when no look is on
    /// air at all — a picture nobody saved is not an edited look, it is simply not a look.
    /// </summary>
    public bool AirEdited()
    {
        var id = _s.AirLookId;
        if (id.Length == 0) return false;          // matched by picture, so by definition unedited
        var look = OnAir();
        return look is not null && FingerprintOf(look) != AirFingerprint();
    }

    /// <summary>
    /// The targets showing something other than what the look on air asked for — empty when no
    /// look is recorded, or when every screen is still doing as it was told.
    ///
    /// This is the reading a whole-show fingerprint could never give: on a rig with eight screens,
    /// "the look is up but screen 3 has gone its own way" is a different thing to know from "the
    /// look is up", and an operator who cannot tell them apart has to walk over and look.
    /// </summary>
    public IReadOnlyList<string> TargetsOffLook()
    {
        var look = OnAir();
        if (look is null) return Array.Empty<string>();

        // Keyed on the sandbox as well as the version, exactly like the air's own fingerprint:
        // AirState is the frozen programme while EDIT SAFE is open and the live state when it is
        // not, and opening or closing it swaps which one this reads without moving the bus version.
        var version = _s.Bus.Current.Version;
        var sandboxed = _s.Sandbox.Active;
        if (version == _offVersion && sandboxed == _offSandboxed && _offLookId == look.Id) return _off;

        var air = _s.AirState;
        var targets = _s.Bus.Current.Rig.Targets;
        _off = LookService.TargetsOffLook(air, look.Json, targets);
        _offVersion = version;
        _offSandboxed = sandboxed;
        _offLookId = look.Id;
        return _off;
    }

    /// <summary>True when this one target has gone its own way since the look was recalled.</summary>
    public bool IsOffLook(string targetId)
    {
        if (targetId.Length == 0) return false;
        var off = TargetsOffLook();
        for (var i = 0; i < off.Count; i++)
        {
            if (off[i] == targetId) return true;
        }
        return false;
    }
}
