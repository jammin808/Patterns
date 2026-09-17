namespace Patterns.Core.Media;

/// <summary>
/// Round 76: what a page's video player is steered to decode with. YouTube hands a modern browser
/// VP9 or AV1 by preference; on a show machine whose card decodes neither in hardware (most
/// laptops' and many desktops' AV1, older cards' VP9) the browser decodes 1080p in software, the
/// picture the screencast captures arrives late and uneven, and the CPU the JPEG decode and the
/// outputs need is spent on the codec. H.264 every card decodes in hardware. The page is told, before
/// its scripts run, that the browser cannot play the other codecs — the trick the h264ify extension
/// plays — and the player picks H.264; the cost is YouTube's 1080p ceiling for H.264, which a 1080p
/// output never notices. Pure text; the browser runs it.
/// </summary>
public static class WebPlayback
{
    /// <summary>The codec strings the page is told the browser cannot play.</summary>
    public const string BlockedCodecs = "vp8|vp9|vp09|av01|av1";

    /// <summary>The words the page's status line carries while the preference is on.</summary>
    public const string PreferH264Words = "H.264 preferred";

    /// <summary>
    /// The script, run on every document created: MediaSource.isTypeSupported and canPlayType answer
    /// "no" for the blocked codecs and as before for everything else; a page without either is left alone.
    /// </summary>
    public static string PreferH264Script =>
        "(function(){try{var block=/(" + BlockedCodecs + ")/i;" +
        "var ms=window.MediaSource;if(ms&&ms.isTypeSupported){var o=ms.isTypeSupported.bind(ms);ms.isTypeSupported=function(t){return block.test(String(t))?false:o(t);};}" +
        "var p=window.HTMLMediaElement&&HTMLMediaElement.prototype;if(p&&p.canPlayType){var c=p.canPlayType;p.canPlayType=function(t){return block.test(String(t))?'':c.call(this,t);};}" +
        "window.__patternsPreferH264=true;}catch(x){}})()";
}
