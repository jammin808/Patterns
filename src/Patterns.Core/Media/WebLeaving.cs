namespace Patterns.Core.Media;

/// <summary>
/// The script that fades a page's sound out as it leaves the programme (round 68): every media
/// element in the document has its volume ramped to nothing over the transition, then is muted —
/// a YouTube embed's player included, since the embed is the document. The browser's own mute
/// follows at the end of the fade, from the desk. Pure: a string, tested without a browser.
/// </summary>
public static class WebLeaving
{
    /// <summary>The longest fade a page gets: a transition is at most three seconds; a runaway value is not a minute of half-heard sound.</summary>
    public const double MaxFadeSeconds = 5;

    /// <summary>The fade for this many seconds: the elements found come back as the script's number (0 for none, -1 when the page would not be asked).</summary>
    public static string FadeScript(double seconds)
    {
        var ms = (int)Math.Round(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0.01, MaxFadeSeconds) * 1000);
        return "(function(){try{var d=" + ms.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";var t0=performance.now();" +
               "var els=Array.prototype.slice.call(document.querySelectorAll('video,audio'));if(!els.length)return 0;" +
               "var v0=els.map(function(e){return e.volume;});" +
               "function step(){var k=Math.min(1,(performance.now()-t0)/d);" +
               "for(var i=0;i<els.length;i++){try{els[i].volume=v0[i]*(1-k);}catch(x){}}" +
               "if(k<1){setTimeout(step,16);}else{for(var i=0;i<els.length;i++){try{els[i].muted=true;}catch(x){}}}}" +
               "setTimeout(step,0);return els.length;}catch(x){return -1;}})()";
    }
}
