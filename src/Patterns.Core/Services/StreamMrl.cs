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

    /// <summary>The rate the stream encodes at: the show's master rate when it follows one, else its own (10–60).</summary>
    public static int EffectiveFps(StreamConfig cfg, int masterFps)
        => cfg.FpsFollowsMaster && masterFps > 0 ? Math.Clamp(masterFps, 10, 60) : cfg.Fps;

    /// <summary>Null when there is nothing to stream to.</summary>
    public static Plan? Build(StreamConfig cfg, SKRectI screenRect, IReadOnlyList<string> destinations, int masterFps = 0)
    {
        var dests = destinations.Where(d => !string.IsNullOrWhiteSpace(d)).Take(2).ToList();
        if (dests.Count == 0) return null;

        var fps = EffectiveFps(cfg, masterFps);
        var options = new List<string>
        {
            $":screen-fps={fps}",
            $":screen-left={screenRect.Left}",
            $":screen-top={screenRect.Top}",
            $":screen-height={screenRect.Height}",
        };
        options.Insert(3, $":screen-width={screenRect.Width}");
        return Finish(cfg, fps, options, dests, "screen://");
    }

    /// <summary>
    /// The engine-fed plan: the stream's own screen (or any rig target) rendered by the engine at
    /// the stream's size and rate and handed to libVLC as raw BGRA frames through a memory
    /// input — the same encode and destinations as the desktop capture. Null when there is
    /// nothing to stream to.
    /// </summary>
    public static Plan? BuildRendered(StreamConfig cfg, IReadOnlyList<string> destinations, int masterFps = 0)
    {
        var dests = destinations.Where(d => !string.IsNullOrWhiteSpace(d)).Take(2).ToList();
        if (dests.Count == 0) return null;
        var fps = EffectiveFps(cfg, masterFps);
        return Finish(cfg, fps, RawVideoOptions(cfg.Width, cfg.Height, fps), dests, RenderedMrl);
    }

    /// <summary>What a rendered plan's MRL reads (the frames come through the memory input, not a location).</summary>
    public const string RenderedMrl = "imem://patterns";

    /// <summary>The raw-video demuxer's options for a BGRA frame feed of this size and rate.</summary>
    public static List<string> RawVideoOptions(int width, int height, int fps) => new()
    {
        ":demux=rawvideo",
        $":rawvid-width={width}",
        $":rawvid-height={height}",
        ":rawvid-chroma=RV32",
        $":rawvid-fps={fps}",
    };

    /// <summary>Bytes per BGRA frame at this size.</summary>
    public static int FrameBytes(int width, int height) => Math.Max(1, width) * Math.Max(1, height) * 4;

    private static Plan Finish(StreamConfig cfg, int fps, List<string> options, List<string> dests, string mrl)
    {
        var audio = cfg.AudioDevice.Trim().Length > 0;
        if (audio)
        {
            // Optional audio from a DirectShow capture device (system-audio loopback needs
            // a virtual cable device — see the Stream tab hint).
            options.Add(":input-slave=dshow://");
            options.Add(":dshow-vdev=none");
            options.Add($":dshow-adev={cfg.AudioDevice.Trim()}");
        }

        var venc = $"venc=x264{{preset=veryfast,tune=zerolatency,keyint={fps * 2}}}";
        var transcode = audio
            ? $"#transcode{{vcodec=h264,{venc},vb={cfg.VideoKbps},width={cfg.Width},height={cfg.Height},acodec=mp4a,ab={cfg.AudioKbps},channels=2,samplerate=48000}}"
            : $"#transcode{{vcodec=h264,{venc},vb={cfg.VideoKbps},width={cfg.Width},height={cfg.Height}}}";

        var outs = dests.Select(DstFor).ToList();
        var chain = outs.Count == 1
            ? $"{transcode}:{outs[0]}"
            : $"{transcode}:duplicate{{{string.Join(",", outs.Select(o => $"dst={o}"))}}}";

        options.Add(":sout=" + chain);
        options.Add(":sout-mux-caching=1500");
        return new Plan(mrl, options.ToArray());
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
