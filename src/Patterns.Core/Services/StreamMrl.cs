using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Services;

/// <summary>
/// Builds the libVLC input + stream-output chain for the streaming feature: capture one
/// screen (screen:// with a crop), encode h264 once at the configured resolution/frame
/// rate, and send the same encode to one or two destinations (duplicate). Pure string
/// building — unit tested; the app service just hands it to libVLC.
/// </summary>
public static class StreamMrl
{
    public sealed record Plan(string Mrl, string[] Options);

    /// <summary>Null when there is nothing to stream to.</summary>
    public static Plan? Build(StreamConfig cfg, SKRectI screenRect, IReadOnlyList<string> destinations)
    {
        var dests = destinations.Where(d => !string.IsNullOrWhiteSpace(d)).Take(2).ToList();
        if (dests.Count == 0) return null;

        var options = new List<string>
        {
            $":screen-fps={cfg.Fps}",
            $":screen-left={screenRect.Left}",
            $":screen-top={screenRect.Top}",
            $":screen-width={screenRect.Width}",
            $":screen-height={screenRect.Height}",
        };

        var audio = cfg.AudioDevice.Trim().Length > 0;
        if (audio)
        {
            // Optional audio from a DirectShow capture device (system-audio loopback needs
            // a virtual cable device — see the Stream tab hint).
            options.Add(":input-slave=dshow://");
            options.Add(":dshow-vdev=none");
            options.Add($":dshow-adev={cfg.AudioDevice.Trim()}");
        }

        var venc = $"venc=x264{{preset=veryfast,tune=zerolatency,keyint={cfg.Fps * 2}}}";
        var transcode = audio
            ? $"#transcode{{vcodec=h264,{venc},vb={cfg.VideoKbps},width={cfg.Width},height={cfg.Height},acodec=mp4a,ab={cfg.AudioKbps},channels=2,samplerate=48000}}"
            : $"#transcode{{vcodec=h264,{venc},vb={cfg.VideoKbps},width={cfg.Width},height={cfg.Height}}}";

        var outs = dests.Select(DstFor).ToList();
        var chain = outs.Count == 1
            ? $"{transcode}:{outs[0]}"
            : $"{transcode}:duplicate{{{string.Join(",", outs.Select(o => $"dst={o}"))}}}";

        options.Add(":sout=" + chain);
        options.Add(":sout-mux-caching=1500");
        return new Plan("screen://", options.ToArray());
    }

    /// <summary>One destination module: RTMP gets the FLV mux, SRT/UDP get MPEG-TS.</summary>
    public static string DstFor(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
        {
            return $"std{{access=srt,mux=ts,dst={trimmed["srt://".Length..]}}}";
        }
        if (trimmed.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            return $"std{{access=udp,mux=ts,dst={trimmed["udp://".Length..]}}}";
        }
        // rtmp:// and rtmps:// (and anything else) go through libavformat's FLV mux.
        return $"std{{access=rtmp,mux=ffmpeg{{mux=flv}},dst={trimmed}}}";
    }
}
